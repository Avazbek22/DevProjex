using System.Globalization;
using System.Runtime.InteropServices;
using DevProjex.Application.Compression;
using DevProjex.Application.Diagnostics;
using DevProjex.Infrastructure.Compression;

namespace DevProjex.Tests.Integration;

[Collection(CompressionOptimizationBenchmarkCollection.Name)]
[Trait("Category", "LocalPerformance")]
public sealed class PerformanceAuditRound2Tests
{
	private const string EnabledVariable = "DEVPROJEX_RUN_PERFORMANCE_AUDIT_ROUND2";
	private const int DefaultMeasuredRuns = 3;
	private const long DefaultUnsupportedCorpusBytes = 128L * 1024 * 1024;
	private const int DefaultUnsupportedShardBytes = 16 * 1024;
	private const int DefaultCommentHeavyFiles = 60;
	private const int DefaultCommentsPerFile = 6_000;
	private const int DefaultMixedCopiesPerLanguage = 4;
	private const int DefaultSessionCreationIterations = 32;
	private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	[Fact(Timeout = 1_800_000)]
	public async Task NewTransformationLayer_RecordsRoundTwoPerformanceProfile()
	{
		if (!string.Equals(Environment.GetEnvironmentVariable(EnabledVariable), "1", StringComparison.Ordinal))
			Assert.Skip($"Set {EnabledVariable}=1 to run the profiling audit.");
		if (!string.Equals(ReadBuildConfiguration(), "Release", StringComparison.OrdinalIgnoreCase))
			Assert.Skip("The profiling audit must run from a Release build.");

		var cancellationToken = TestContext.Current.CancellationToken;
		var settings = AuditSettings.ReadFromEnvironment();
		using var workspace = new TemporaryDirectory();
		var corpora = CreateCorpora(workspace, settings);
		var runs = new List<AuditRun>(settings.MeasuredRuns);
		for (var index = 0; index < settings.MeasuredRuns; index++)
		{
			runs.Add(await RunAuditAsync(
				index + 1,
				workspace.Path,
				corpora,
				settings,
				cancellationToken));
		}

		var report = new PerformanceAuditReport(
			SchemaVersion: 1,
			Stage: settings.Stage,
			CreatedAtUtc: DateTimeOffset.UtcNow,
			Machine: new AuditMachine(
				Environment.MachineName,
				Environment.OSVersion.ToString(),
				RuntimeInformation.FrameworkDescription,
				RuntimeInformation.ProcessArchitecture.ToString(),
				Environment.ProcessorCount,
				ReadBuildConfiguration()),
			Manifest: new ScenarioManifest(
				UnsupportedTotalBytes: settings.UnsupportedTotalBytes,
				UnsupportedShardBytes: settings.UnsupportedShardBytes,
				UnsupportedShardCount: corpora.UnsupportedShards.Count,
				CommentHeavyFiles: corpora.CommentHeavyFiles.Count,
				CommentsPerFile: settings.CommentsPerFile,
				MixedLanguageFiles: corpora.MixedLanguageFiles.Count,
				MixedLanguageCount: MixedLanguageTemplates.Count,
				SessionCreationIterations: settings.SessionCreationIterations,
				Notes:
				[
					"All corpora are generated before measured phases.",
					"The unsupported corpus uses small shards because per-file stream buffers are the measured allocation risk.",
					"Comment-heavy XML runs Comments, an immediate warm repeat, Both, then Comments again to expose retained-byte LRU pressure.",
					"Embedded cold means a unique materialization directory inside the current process.",
					"Streaming metrics is the non-materializing product control for unsupported content.",
					"ProcessLifetimePeakWorkingSetBytes is an OS process-lifetime high-water mark, not a phase-local peak."
				]),
			Runs: runs,
			Medians: BuildMedians(runs));

		var outputPath = ResolveOutputPath(settings.Stage);
		Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
		await File.WriteAllTextAsync(
			outputPath,
			JsonSerializer.Serialize(report, JsonOptions),
			Utf8WithoutBom,
			cancellationToken);
		TestContext.Current.TestOutputHelper?.WriteLine($"Performance audit round 2: {outputPath}");
	}

	private static async Task<AuditRun> RunAuditAsync(
		int index,
		string workspaceRoot,
		AuditCorpora corpora,
		AuditSettings settings,
		CancellationToken cancellationToken)
	{
		var giantUnsupported = await MeasureGiantUnsupportedAsync(
			corpora,
			cancellationToken);
		var commentHeavy = await MeasureCommentHeavyAsync(
			corpora.CommentHeavyRoot,
			corpora.CommentHeavyFiles,
			cancellationToken);
		var modeSwitch = await MeasureModeSwitchAsync(
			corpora.MixedLanguageRoot,
			corpora.MixedLanguageFiles,
			cancellationToken);
		var startup = await MeasureSessionStartupAsync(
			settings.SessionCreationIterations,
			cancellationToken);
		var embeddedDelivery = await MeasureEmbeddedDeliveryAsync(
			workspaceRoot,
			index,
			corpora.MixedLanguageRoot,
			corpora.MixedLanguageFiles,
			cancellationToken);
		return new AuditRun(index, giantUnsupported, commentHeavy, modeSwitch, startup, embeddedDelivery);
	}

	private static async Task<GiantUnsupportedRun> MeasureGiantUnsupportedAsync(
		AuditCorpora corpora,
		CancellationToken cancellationToken)
	{
		var analyzer = new FileContentAnalyzer();
		var singleStreaming = await MeasurePhaseAsync(
			async token =>
			{
				var metrics = await analyzer.GetClassifiedMetricsAsync(corpora.OversizedUnsupportedFile, token);
				return metrics.Metrics?.CharCount ?? 0;
			},
			compression: null,
			cancellationToken);
		var shardedStreaming = await MeasurePhaseAsync(
			async token =>
			{
				var metrics = await ProjectContentMetricsCalculator.CalculateAsync(
					analyzer,
					corpora.UnsupportedShards,
					token);
				return metrics.Chars;
			},
			compression: null,
			cancellationToken);

		using var compression = CodeCompressionFactory.CreateSession();
		var context = new CodeCompressionContext(
			corpora.UnsupportedRoot,
			compression,
			CodeTransformKinds.Bodies);
		var shardedPrewarm = await MeasurePhaseAsync(
			async token =>
			{
				var result = await new CodeCompressionPrewarmer(analyzer)
					.WarmAsync(context, corpora.UnsupportedShards, token);
				return result.WarmedFiles;
			},
			compression,
			cancellationToken);

		return new GiantUnsupportedRun(
			SingleOversizedStreaming: singleStreaming.Measurement,
			ShardedStreaming: shardedStreaming.Measurement,
			ShardedCompressionPrewarm: shardedPrewarm.Measurement,
			CompressionDiagnostics: compression.Diagnostics);
	}

	private static async Task<CommentHeavyRun> MeasureCommentHeavyAsync(
		string root,
		IReadOnlyList<string> paths,
		CancellationToken cancellationToken)
	{
		var analyzer = new FileContentAnalyzer();
		using var compression = CodeCompressionFactory.CreateSession();
		var commentsContext = new CodeCompressionContext(root, compression, CodeTransformKinds.Comments);
		var cold = await MeasureWarmupAsync(analyzer, commentsContext, paths, compression, cancellationToken);
		var afterCold = compression.Diagnostics;
		var warm = await MeasureWarmupAsync(analyzer, commentsContext, paths, compression, cancellationToken);
		var afterWarm = compression.Diagnostics;
		var bothContext = new CodeCompressionContext(
			root,
			compression,
			CodeTransformKinds.Bodies | CodeTransformKinds.Comments);
		var both = await MeasureWarmupAsync(analyzer, bothContext, paths, compression, cancellationToken);
		var afterBoth = compression.Diagnostics;
		var commentsAfterBoth = await MeasureWarmupAsync(
			analyzer,
			commentsContext,
			paths,
			compression,
			cancellationToken);
		return new CommentHeavyRun(
			Cold: cold.Measurement,
			Warm: warm.Measurement,
			Both: both.Measurement,
			CommentsAfterBoth: commentsAfterBoth.Measurement,
			AfterCold: afterCold,
			AfterWarm: afterWarm,
			AfterBoth: afterBoth,
			AfterCommentsRevisit: compression.Diagnostics);
	}

	private static async Task<ModeSwitchRun> MeasureModeSwitchAsync(
		string root,
		IReadOnlyList<string> paths,
		CancellationToken cancellationToken)
	{
		var analyzer = new FileContentAnalyzer();
		using var compression = CodeCompressionFactory.CreateSession();
		var comments = await MeasureModeAsync(
			analyzer, compression, root, paths, CodeTransformKinds.Comments, cancellationToken);
		var bodies = await MeasureModeAsync(
			analyzer, compression, root, paths, CodeTransformKinds.Bodies, cancellationToken);
		var both = await MeasureModeAsync(
			analyzer,
			compression,
			root,
			paths,
			CodeTransformKinds.Bodies | CodeTransformKinds.Comments,
			cancellationToken);
		var commentsAgain = await MeasureModeAsync(
			analyzer, compression, root, paths, CodeTransformKinds.Comments, cancellationToken);
		return new ModeSwitchRun(comments, bodies, both, commentsAgain);
	}

	private static async Task<ModePhase> MeasureModeAsync(
		IFileContentAnalyzer analyzer,
		CodeCompressionSession compression,
		string root,
		IReadOnlyList<string> paths,
		CodeTransformKinds kinds,
		CancellationToken cancellationToken)
	{
		var context = new CodeCompressionContext(root, compression, kinds);
		var measured = await MeasureWarmupAsync(analyzer, context, paths, compression, cancellationToken);
		return new ModePhase(measured.Measurement, compression.Diagnostics);
	}

	private static Task<MeasuredResult<long>> MeasureWarmupAsync(
		IFileContentAnalyzer analyzer,
		CodeCompressionContext context,
		IReadOnlyList<string> paths,
		CodeCompressionSession compression,
		CancellationToken cancellationToken) =>
		MeasurePhaseAsync(
			async token =>
			{
				var result = await new CodeCompressionPrewarmer(analyzer).WarmAsync(context, paths, token);
				return result.WarmedFiles;
			},
			compression,
			cancellationToken);

	private static async Task<SessionStartupRun> MeasureSessionStartupAsync(
		int iterations,
		CancellationToken cancellationToken)
	{
		var measured = await MeasurePhaseAsync(
			token =>
			{
				for (var index = 0; index < iterations; index++)
				{
					token.ThrowIfCancellationRequested();
					using var session = CodeCompressionFactory.CreateSession();
					var runtime = session.Diagnostics.Runtime;
					if (runtime.CompiledQuerySets != 0 || runtime.MaterializedWorkers != 0)
						throw new InvalidOperationException("Creating an unused session loaded a grammar.");
				}
				return Task.FromResult((long)iterations);
			},
			compression: null,
			cancellationToken);
		return new SessionStartupRun(iterations, measured.Measurement);
	}

	private static async Task<EmbeddedDeliveryRun> MeasureEmbeddedDeliveryAsync(
		string workspaceRoot,
		int runIndex,
		string corpusRoot,
		IReadOnlyList<string> paths,
		CancellationToken cancellationToken)
	{
		var grammarRoot = Path.Combine(workspaceRoot, "embedded", $"run-{runIndex:D2}");
		var locator = new EmbeddedGrammarLibraryLocator(
			typeof(CodeCompressionFactory).Assembly,
			CodeCompressionFactory.EmbeddedResourcePrefix,
			grammarRoot);
		using var compression = new CodeCompressionSession(new TreeSitterCodeCompressor(locator));
		var context = new CodeCompressionContext(
			corpusRoot,
			compression,
			CodeTransformKinds.Bodies | CodeTransformKinds.Comments);
		var analyzer = new FileContentAnalyzer();
		var cold = await MeasureWarmupAsync(analyzer, context, paths, compression, cancellationToken);
		var afterCold = compression.Diagnostics;
		var warm = await MeasureWarmupAsync(analyzer, context, paths, compression, cancellationToken);
		var files = Directory.Exists(grammarRoot)
			? Directory.EnumerateFiles(grammarRoot, "*", SearchOption.AllDirectories).ToArray()
			: [];
		return new EmbeddedDeliveryRun(
			Cold: cold.Measurement,
			Warm: warm.Measurement,
			MaterializedFiles: files.Length,
			MaterializedBytes: files.Sum(static path => new FileInfo(path).Length),
			AdvertisedLibraries: locator.EnumerateLibraries().Count,
			AfterCold: afterCold,
			AfterWarm: compression.Diagnostics);
	}

	private static async Task<MeasuredResult<long>> MeasurePhaseAsync(
		Func<CancellationToken, Task<long>> operation,
		CodeCompressionSession? compression,
		CancellationToken cancellationToken)
	{
		using var contentMeasurement = ContentPipelineDiagnostics.BeginMeasurement();
		var compressionBefore = compression?.Diagnostics;
		var process = Process.GetCurrentProcess();
		process.Refresh();
		var cpuBefore = process.TotalProcessorTime;
		var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
		var gen0Before = GC.CollectionCount(0);
		var gen1Before = GC.CollectionCount(1);
		var gen2Before = GC.CollectionCount(2);
		var stopwatch = Stopwatch.StartNew();
		var value = await operation(cancellationToken);
		stopwatch.Stop();
		process.Refresh();
		var content = contentMeasurement.Capture();
		return new MeasuredResult<long>(
			value,
			new PhaseMeasurement(
				ElapsedMilliseconds: stopwatch.Elapsed.TotalMilliseconds,
				CpuMilliseconds: (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
				AllocatedBytes: GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
				ProcessLifetimePeakWorkingSetBytes: process.PeakWorkingSet64,
				EndWorkingSetBytes: process.WorkingSet64,
				EndPrivateBytes: process.PrivateMemorySize64,
				Gen0Collections: GC.CollectionCount(0) - gen0Before,
				Gen1Collections: GC.CollectionCount(1) - gen1Before,
				Gen2Collections: GC.CollectionCount(2) - gen2Before,
				OperationValue: value,
				FullFileReads: content.FullFileReads,
				FullFileReadBytes: content.FullFileReadBytes,
				ContentFingerprintComputations: content.ContentFingerprintComputations,
				PlanApplications: content.PlanApplications,
				Compression: Difference(compressionBefore, compression?.Diagnostics)));
	}

	private static CompressionWork Difference(
		CodeCompressionDiagnosticsSnapshot? before,
		CodeCompressionDiagnosticsSnapshot? after)
	{
		if (after is null)
			return new CompressionWork();
		return new CompressionWork(
			HashComputations: after.HashComputations - (before?.HashComputations ?? 0),
			CacheHits: after.CacheHits - (before?.CacheHits ?? 0),
			CacheMisses: after.CacheMisses - (before?.CacheMisses ?? 0),
			AnalysisExecutions: after.AnalysisExecutions - (before?.AnalysisExecutions ?? 0),
			PrewarmRequests: after.PrewarmRequests - (before?.PrewarmRequests ?? 0),
			PrewarmCacheHits: after.PrewarmCacheHits - (before?.PrewarmCacheHits ?? 0),
			PrewarmAnalyses: after.PrewarmAnalyses - (before?.PrewarmAnalyses ?? 0),
			PrewarmReuses: after.PrewarmReuses - (before?.PrewarmReuses ?? 0),
			UnsupportedFastPaths: after.UnsupportedFastPaths - (before?.UnsupportedFastPaths ?? 0));
	}

	private static AuditCorpora CreateCorpora(TemporaryDirectory workspace, AuditSettings settings)
	{
		var unsupportedRoot = workspace.CreateDirectory("unsupported");
		var unsupportedShards = new List<string>();
		var remaining = settings.UnsupportedTotalBytes;
		var shardIndex = 0;
		while (remaining > 0)
		{
			var length = (int)Math.Min(settings.UnsupportedShardBytes, remaining);
			var path = Path.Combine(unsupportedRoot, $"shard-{shardIndex:D4}.txt");
			WriteRepeatedAsciiFile(path, length);
			unsupportedShards.Add(path);
			remaining -= length;
			shardIndex++;
		}
		var oversizedPath = Path.Combine(unsupportedRoot, "single-oversized.txt");
		WriteRepeatedAsciiFile(oversizedPath, settings.UnsupportedTotalBytes);

		var commentHeavyRoot = workspace.CreateDirectory("comment-heavy");
		var commentHeavySource = BuildCommentHeavyXml(settings.CommentsPerFile);
		var commentHeavyFiles = new List<string>(settings.CommentHeavyFiles);
		for (var index = 0; index < settings.CommentHeavyFiles; index++)
		{
			var path = Path.Combine(commentHeavyRoot, $"catalog-{index:D3}.xml");
			File.WriteAllText(path, commentHeavySource, Utf8WithoutBom);
			commentHeavyFiles.Add(path);
		}

		var mixedRoot = workspace.CreateDirectory("mixed-20-formats");
		var mixedFiles = new List<string>(MixedLanguageTemplates.Count * settings.MixedCopiesPerLanguage);
		foreach (var (extension, source) in MixedLanguageTemplates)
		{
			var languageRoot = Path.Combine(mixedRoot, extension);
			Directory.CreateDirectory(languageRoot);
			for (var copy = 0; copy < settings.MixedCopiesPerLanguage; copy++)
			{
				var path = Path.Combine(languageRoot, $"sample-{copy:D3}.{extension}");
				File.WriteAllText(path, source, Utf8WithoutBom);
				mixedFiles.Add(path);
			}
		}
		unsupportedShards.Sort(PathComparer.Default);
		commentHeavyFiles.Sort(PathComparer.Default);
		mixedFiles.Sort(PathComparer.Default);
		return new AuditCorpora(
			unsupportedRoot,
			oversizedPath,
			unsupportedShards,
			commentHeavyRoot,
			commentHeavyFiles,
			mixedRoot,
			mixedFiles);
	}

	private static void WriteRepeatedAsciiFile(string path, long length)
	{
		const string line = "unsupported payload for streaming metrics characterization 0123456789\n";
		var pattern = Encoding.ASCII.GetBytes(line);
		var block = new byte[64 * 1024];
		for (var offset = 0; offset < block.Length; offset += pattern.Length)
			pattern.AsSpan(0, Math.Min(pattern.Length, block.Length - offset)).CopyTo(block.AsSpan(offset));

		using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, block.Length);
		var remaining = length;
		while (remaining > 0)
		{
			var count = (int)Math.Min(block.Length, remaining);
			stream.Write(block, 0, count);
			remaining -= count;
		}
	}

	private static string BuildCommentHeavyXml(int commentCount)
	{
		var builder = new StringBuilder("<?xml version=\"1.0\"?>\n<catalog>\n");
		for (var index = 0; index < commentCount; index++)
		{
			builder.Append("  <!-- documentation entry ")
				.Append(index.ToString(CultureInfo.InvariantCulture))
				.Append(" -->\n  <item id=\"")
				.Append(index.ToString(CultureInfo.InvariantCulture))
				.Append("\">value</item>\n");
		}
		return builder.Append("</catalog>\n").ToString();
	}

	private static readonly IReadOnlyDictionary<string, string> MixedLanguageTemplates =
		new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["bash"] = "#!/usr/bin/env bash\n# comment\nname=\"value#literal\"\nprintf '%s\\n' \"$name\"\n",
			["c"] = "/* comment */\nint add(int a, int b) { int value = a + b; return value; }\n",
			["cpp"] = "// comment\nclass Sample { public: int add(int a, int b) { return a + b; } };\n",
			["cs"] = "// comment\nsealed class Sample { int Add(int a, int b) { return a + b; } }\n",
			["css"] = "/* comment */\n.card { content: \"/* literal */\"; color: red; }\n",
			["go"] = "package sample\n// comment\nfunc add(a int, b int) int { return a + b }\n",
			["html"] = "<!doctype html>\n<!-- comment -->\n<html><body><p>value</p></body></html>\n",
			["java"] = "// comment\nfinal class Sample { int add(int a, int b) { return a + b; } }\n",
			["js"] = "// comment\nexport function add(a, b) { return a + b; }\n",
			["kt"] = "// comment\nclass Sample { fun add(a: Int, b: Int): Int { return a + b } }\n",
			["php"] = "<?php\n// comment\nfinal class Sample { public function add(int $a, int $b): int { return $a + $b; } }\n",
			["py"] = "# comment\ndef add(a, b):\n    return a + b\n",
			["rb"] = "# comment\ndef add(a, b)\n  a + b\nend\n",
			["rs"] = "// comment\nfn add(a: i32, b: i32) -> i32 { a + b }\n",
			["scala"] = "// comment\nobject Sample { def add(a: Int, b: Int): Int = { a + b } }\n",
			["toml"] = "# comment\n[service]\nname = \"api#literal\"\n",
			["ts"] = "// comment\nexport function add(a: number, b: number): number { return a + b; }\n",
			["tsx"] = "// comment\nexport function Sample() { return <section>value</section>; }\n",
			["xml"] = "<?xml version=\"1.0\"?>\n<!-- comment -->\n<root><value>text</value></root>\n",
			["yaml"] = "---\n# comment\nservice:\n  name: \"api#literal\"\n...\n"
		};

	private static IReadOnlyDictionary<string, BenchmarkMedian> BuildMedians(IReadOnlyList<AuditRun> runs)
	{
		var measurements = new Dictionary<string, List<PhaseMeasurement>>(StringComparer.Ordinal);
		foreach (var run in runs)
		{
			Add(measurements, "giant.singleOversizedStreaming", run.GiantUnsupported.SingleOversizedStreaming);
			Add(measurements, "giant.shardedStreaming", run.GiantUnsupported.ShardedStreaming);
			Add(measurements, "giant.shardedCompressionPrewarm", run.GiantUnsupported.ShardedCompressionPrewarm);
			Add(measurements, "comments.cold", run.CommentHeavy.Cold);
			Add(measurements, "comments.warm", run.CommentHeavy.Warm);
			Add(measurements, "comments.both", run.CommentHeavy.Both);
			Add(measurements, "comments.commentsAfterBoth", run.CommentHeavy.CommentsAfterBoth);
			Add(measurements, "modes.comments", run.ModeSwitch.Comments.Measurement);
			Add(measurements, "modes.bodies", run.ModeSwitch.Bodies.Measurement);
			Add(measurements, "modes.both", run.ModeSwitch.Both.Measurement);
			Add(measurements, "modes.commentsAgain", run.ModeSwitch.CommentsAgain.Measurement);
			Add(measurements, "startup.createSessions", run.SessionStartup.CreateSessions);
			Add(measurements, "embedded.cold", run.EmbeddedDelivery.Cold);
			Add(measurements, "embedded.warm", run.EmbeddedDelivery.Warm);
		}
		return measurements.ToDictionary(
			static pair => pair.Key,
			static pair => BenchmarkMedian.Create(pair.Value),
			StringComparer.Ordinal);
	}

	private static void Add(
		Dictionary<string, List<PhaseMeasurement>> target,
		string key,
		PhaseMeasurement value)
	{
		if (!target.TryGetValue(key, out var values))
		{
			values = [];
			target.Add(key, values);
		}
		values.Add(value);
	}

	private static string ResolveOutputPath(string stage)
	{
		var configured = Environment.GetEnvironmentVariable("DEVPROJEX_PERFORMANCE_AUDIT_ROUND2_OUTPUT");
		if (!string.IsNullOrWhiteSpace(configured))
			return Path.GetFullPath(configured);
		return Path.Combine(FindRepositoryRoot(), "artifacts", "performance-audit-round2", $"{stage}.json");
	}

	private static string FindRepositoryRoot()
	{
		var current = new DirectoryInfo(AppContext.BaseDirectory);
		while (current is not null)
		{
			if (File.Exists(Path.Combine(current.FullName, "DevProjex.sln")))
				return current.FullName;
			current = current.Parent;
		}
		throw new DirectoryNotFoundException("DevProjex.sln was not found above the test output directory.");
	}

	private static string ReadBuildConfiguration() =>
		typeof(PerformanceAuditRound2Tests).Assembly
			.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown";

	private sealed record AuditSettings(
		string Stage,
		int MeasuredRuns,
		long UnsupportedTotalBytes,
		int UnsupportedShardBytes,
		int CommentHeavyFiles,
		int CommentsPerFile,
		int MixedCopiesPerLanguage,
		int SessionCreationIterations)
	{
		public static AuditSettings ReadFromEnvironment() => new(
			Stage: NormalizeStage(Environment.GetEnvironmentVariable("DEVPROJEX_PERFORMANCE_AUDIT_ROUND2_STAGE")),
			MeasuredRuns: ReadPositiveInt("DEVPROJEX_PERFORMANCE_AUDIT_RUNS", DefaultMeasuredRuns),
			UnsupportedTotalBytes: ReadPositiveLong(
				"DEVPROJEX_PERFORMANCE_AUDIT_UNSUPPORTED_BYTES",
				DefaultUnsupportedCorpusBytes),
			UnsupportedShardBytes: ReadPositiveInt(
				"DEVPROJEX_PERFORMANCE_AUDIT_UNSUPPORTED_SHARD_BYTES",
				DefaultUnsupportedShardBytes),
			CommentHeavyFiles: ReadPositiveInt(
				"DEVPROJEX_PERFORMANCE_AUDIT_COMMENT_FILES",
				DefaultCommentHeavyFiles),
			CommentsPerFile: ReadPositiveInt(
				"DEVPROJEX_PERFORMANCE_AUDIT_COMMENTS_PER_FILE",
				DefaultCommentsPerFile),
			MixedCopiesPerLanguage: ReadPositiveInt(
				"DEVPROJEX_PERFORMANCE_AUDIT_MIXED_COPIES",
				DefaultMixedCopiesPerLanguage),
			SessionCreationIterations: ReadPositiveInt(
				"DEVPROJEX_PERFORMANCE_AUDIT_SESSION_ITERATIONS",
				DefaultSessionCreationIterations));
	}

	private static int ReadPositiveInt(string name, int fallback)
	{
		var value = Environment.GetEnvironmentVariable(name);
		return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
			? parsed
			: fallback;
	}

	private static long ReadPositiveLong(string name, long fallback)
	{
		var value = Environment.GetEnvironmentVariable(name);
		return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
			? parsed
			: fallback;
	}

	private static string NormalizeStage(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
			return "local";
		var normalized = string.Concat(value.Select(character =>
			char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-'));
		return normalized.Length == 0 ? "local" : normalized;
	}

	private sealed record AuditCorpora(
		string UnsupportedRoot,
		string OversizedUnsupportedFile,
		IReadOnlyList<string> UnsupportedShards,
		string CommentHeavyRoot,
		IReadOnlyList<string> CommentHeavyFiles,
		string MixedLanguageRoot,
		IReadOnlyList<string> MixedLanguageFiles);

	private sealed record PerformanceAuditReport(
		int SchemaVersion,
		string Stage,
		DateTimeOffset CreatedAtUtc,
		AuditMachine Machine,
		ScenarioManifest Manifest,
		IReadOnlyList<AuditRun> Runs,
		IReadOnlyDictionary<string, BenchmarkMedian> Medians);

	private sealed record AuditMachine(
		string Name,
		string OperatingSystem,
		string Framework,
		string Architecture,
		int ProcessorCount,
		string Configuration);

	private sealed record ScenarioManifest(
		long UnsupportedTotalBytes,
		int UnsupportedShardBytes,
		int UnsupportedShardCount,
		int CommentHeavyFiles,
		int CommentsPerFile,
		int MixedLanguageFiles,
		int MixedLanguageCount,
		int SessionCreationIterations,
		IReadOnlyList<string> Notes);

	private sealed record AuditRun(
		int Index,
		GiantUnsupportedRun GiantUnsupported,
		CommentHeavyRun CommentHeavy,
		ModeSwitchRun ModeSwitch,
		SessionStartupRun SessionStartup,
		EmbeddedDeliveryRun EmbeddedDelivery);

	private sealed record GiantUnsupportedRun(
		PhaseMeasurement SingleOversizedStreaming,
		PhaseMeasurement ShardedStreaming,
		PhaseMeasurement ShardedCompressionPrewarm,
		CodeCompressionDiagnosticsSnapshot CompressionDiagnostics);

	private sealed record CommentHeavyRun(
		PhaseMeasurement Cold,
		PhaseMeasurement Warm,
		PhaseMeasurement Both,
		PhaseMeasurement CommentsAfterBoth,
		CodeCompressionDiagnosticsSnapshot AfterCold,
		CodeCompressionDiagnosticsSnapshot AfterWarm,
		CodeCompressionDiagnosticsSnapshot AfterBoth,
		CodeCompressionDiagnosticsSnapshot AfterCommentsRevisit);

	private sealed record ModeSwitchRun(
		ModePhase Comments,
		ModePhase Bodies,
		ModePhase Both,
		ModePhase CommentsAgain);

	private sealed record ModePhase(
		PhaseMeasurement Measurement,
		CodeCompressionDiagnosticsSnapshot Diagnostics);

	private sealed record SessionStartupRun(int Iterations, PhaseMeasurement CreateSessions);

	private sealed record EmbeddedDeliveryRun(
		PhaseMeasurement Cold,
		PhaseMeasurement Warm,
		int MaterializedFiles,
		long MaterializedBytes,
		int AdvertisedLibraries,
		CodeCompressionDiagnosticsSnapshot AfterCold,
		CodeCompressionDiagnosticsSnapshot AfterWarm);

	private sealed record MeasuredResult<T>(T Value, PhaseMeasurement Measurement);

	private sealed record PhaseMeasurement(
		double ElapsedMilliseconds,
		double CpuMilliseconds,
		long AllocatedBytes,
		long ProcessLifetimePeakWorkingSetBytes,
		long EndWorkingSetBytes,
		long EndPrivateBytes,
		int Gen0Collections,
		int Gen1Collections,
		int Gen2Collections,
		long OperationValue,
		long FullFileReads,
		long FullFileReadBytes,
		long ContentFingerprintComputations,
		long PlanApplications,
		CompressionWork Compression);

	private sealed record CompressionWork(
		long HashComputations = 0,
		long CacheHits = 0,
		long CacheMisses = 0,
		long AnalysisExecutions = 0,
		long PrewarmRequests = 0,
		long PrewarmCacheHits = 0,
		long PrewarmAnalyses = 0,
		long PrewarmReuses = 0,
		long UnsupportedFastPaths = 0);

	private sealed record BenchmarkMedian(
		double ElapsedMilliseconds,
		double CpuMilliseconds,
		long AllocatedBytes,
		long FullFileReads,
		long FullFileReadBytes,
		long ContentFingerprintComputations,
		long PlanApplications,
		long CompressionCacheHits,
		long CompressionCacheMisses,
		long CompressionAnalyses,
		long PrewarmRequests,
		long PrewarmCacheHits,
		long PrewarmAnalyses,
		long PrewarmReuses,
		long UnsupportedFastPaths,
		long ProcessLifetimePeakWorkingSetBytes,
		long EndWorkingSetBytes,
		long EndPrivateBytes,
		int Gen0Collections,
		int Gen1Collections,
		int Gen2Collections)
	{
		public static BenchmarkMedian Create(IReadOnlyList<PhaseMeasurement> values) => new(
			Median(values.Select(static value => value.ElapsedMilliseconds)),
			Median(values.Select(static value => value.CpuMilliseconds)),
			Median(values.Select(static value => value.AllocatedBytes)),
			Median(values.Select(static value => value.FullFileReads)),
			Median(values.Select(static value => value.FullFileReadBytes)),
			Median(values.Select(static value => value.ContentFingerprintComputations)),
			Median(values.Select(static value => value.PlanApplications)),
			Median(values.Select(static value => value.Compression.CacheHits)),
			Median(values.Select(static value => value.Compression.CacheMisses)),
			Median(values.Select(static value => value.Compression.AnalysisExecutions)),
			Median(values.Select(static value => value.Compression.PrewarmRequests)),
			Median(values.Select(static value => value.Compression.PrewarmCacheHits)),
			Median(values.Select(static value => value.Compression.PrewarmAnalyses)),
			Median(values.Select(static value => value.Compression.PrewarmReuses)),
			Median(values.Select(static value => value.Compression.UnsupportedFastPaths)),
			Median(values.Select(static value => value.ProcessLifetimePeakWorkingSetBytes)),
			Median(values.Select(static value => value.EndWorkingSetBytes)),
			Median(values.Select(static value => value.EndPrivateBytes)),
			(int)Median(values.Select(static value => (long)value.Gen0Collections)),
			(int)Median(values.Select(static value => (long)value.Gen1Collections)),
			(int)Median(values.Select(static value => (long)value.Gen2Collections)));

		private static double Median(IEnumerable<double> values)
		{
			var ordered = values.Order().ToArray();
			return ordered[ordered.Length / 2];
		}

		private static long Median(IEnumerable<long> values)
		{
			var ordered = values.Order().ToArray();
			return ordered[ordered.Length / 2];
		}
	}
}
