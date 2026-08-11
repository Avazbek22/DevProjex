using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using DevProjex.Application.Compression;
using DevProjex.Application.Context;
using DevProjex.Application.Diagnostics;
using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.Compression;
using DevProjex.Infrastructure.Secrets;

namespace DevProjex.Tests.Integration;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CompressionOptimizationBenchmarkCollection
{
	public const string Name = "Compression optimization benchmark";
}

[Collection(CompressionOptimizationBenchmarkCollection.Name)]
[Trait("Category", "LocalPerformance")]
public sealed class CompressionOptimizationBenchmarkTests
{
	private const int MeasuredRuns = 3;
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	[Fact(Timeout = 1_800_000)]
	public async Task RealProject_RecordsContentTransformationMatrix()
	{
		if (!string.Equals(
				Environment.GetEnvironmentVariable("DEVPROJEX_RUN_COMPRESSION_OPT_BENCHMARK"),
				"1",
				StringComparison.Ordinal))
		{
			Assert.Skip("Set DEVPROJEX_RUN_COMPRESSION_OPT_BENCHMARK=1 to run the optimization benchmark.");
		}

		var root = Environment.GetEnvironmentVariable("DEVPROJEX_COMPRESSION_PROFILE_ROOT");
		if (string.IsNullOrWhiteSpace(root))
			Assert.Skip("Set DEVPROJEX_COMPRESSION_PROFILE_ROOT to a real project folder.");

		root = Path.GetFullPath(root);
		var stage = NormalizeStage(
			Environment.GetEnvironmentVariable("DEVPROJEX_COMPRESSION_BENCHMARK_STAGE"));
		var baselinePlan = await BuildPlanAsync(root);
		var goldenPlan = await BuildGoldenPlanAsync();
		var commentsOnlyPlan = await BuildCommentsOnlyPlanAsync();
		var structuredDataStressPlan = await BuildStructuredDataStressPlanAsync();
		Assert.NotEmpty(baselinePlan.IncludedFiles);
		Assert.NotEmpty(commentsOnlyPlan.IncludedFiles);
		Assert.NotEmpty(structuredDataStressPlan.IncludedFiles);
		var iterations = new List<BenchmarkIteration>(MeasuredRuns);
		for (var index = 0; index < MeasuredRuns; index++)
		{
			iterations.Add(await RunIterationAsync(
				index + 1,
				baselinePlan,
				commentsOnlyPlan,
				structuredDataStressPlan,
				goldenPlan,
				TestContext.Current.CancellationToken));
		}

		Assert.Single(iterations.Select(static run => run.Golden.TextSha256).Distinct(StringComparer.Ordinal));
		Assert.Single(iterations.Select(static run => run.Golden.JsonSha256).Distinct(StringComparer.Ordinal));
		var report = new CompressionOptimizationBenchmarkReport(
			SchemaVersion: 1,
			Stage: stage,
			CreatedAtUtc: DateTimeOffset.UtcNow,
			Machine: new BenchmarkMachine(
				Environment.MachineName,
				Environment.OSVersion.ToString(),
				RuntimeInformation.FrameworkDescription,
				RuntimeInformation.ProcessArchitecture.ToString(),
				Environment.ProcessorCount),
			ProjectRoot: root,
			FileCount: baselinePlan.IncludedFiles.Count,
			CommentsOnlyCorpusFileCount: commentsOnlyPlan.IncludedFiles.Count,
			StructuredDataStressFileCount: structuredDataStressPlan.IncludedFiles.Count,
			Runs: iterations,
			Medians: BuildMedians(iterations));

		var outputPath = ResolveOutputPath(stage);
		Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
		await File.WriteAllTextAsync(
			outputPath,
			JsonSerializer.Serialize(report, JsonOptions),
			new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
			TestContext.Current.CancellationToken);
		TestContext.Current.TestOutputHelper?.WriteLine($"Compression benchmark: {outputPath}");
	}

	private static async Task<BenchmarkIteration> RunIterationAsync(
		int index,
		ProjectContextPlan plan,
		ProjectContextPlan commentsOnlyPlan,
		ProjectContextPlan structuredDataStressPlan,
		ProjectContextPlan goldenPlan,
		CancellationToken cancellationToken)
	{
		var scenarios = new Dictionary<string, ScenarioRun>(StringComparer.Ordinal)
		{
			["none"] = await RunScenarioAsync(
				plan, compress: false, stripComments: false, hideSecrets: false, cancellationToken),
			["compression"] = await RunScenarioAsync(
				plan, compress: true, stripComments: false, hideSecrets: false, cancellationToken),
			["comments"] = await RunScenarioAsync(
				plan, compress: false, stripComments: true, hideSecrets: false, cancellationToken),
			["secrets"] = await RunScenarioAsync(
				plan, compress: false, stripComments: false, hideSecrets: true, cancellationToken),
			["compressionAndComments"] = await RunScenarioAsync(
				plan, compress: true, stripComments: true, hideSecrets: false, cancellationToken),
			["commentsAndSecrets"] = await RunScenarioAsync(
				plan, compress: false, stripComments: true, hideSecrets: true, cancellationToken),
			["compressionAndSecrets"] = await RunScenarioAsync(
				plan, compress: true, stripComments: false, hideSecrets: true, cancellationToken),
			["all"] = await RunScenarioAsync(
				plan, compress: true, stripComments: true, hideSecrets: true, cancellationToken),
			["commentsOnlyCorpusCompression"] = await RunScenarioAsync(
				commentsOnlyPlan, compress: true, stripComments: false, hideSecrets: false, cancellationToken),
			["commentsOnlyCorpusComments"] = await RunScenarioAsync(
				commentsOnlyPlan, compress: false, stripComments: true, hideSecrets: false, cancellationToken),
			["commentsOnlyCorpusBoth"] = await RunScenarioAsync(
				commentsOnlyPlan, compress: true, stripComments: true, hideSecrets: false, cancellationToken),
			["structuredDataCompression"] = await RunScenarioAsync(
				structuredDataStressPlan, compress: true, stripComments: false, hideSecrets: false, cancellationToken),
			["structuredDataComments"] = await RunScenarioAsync(
				structuredDataStressPlan, compress: false, stripComments: true, hideSecrets: false, cancellationToken),
			["structuredDataBoth"] = await RunScenarioAsync(
				structuredDataStressPlan, compress: true, stripComments: true, hideSecrets: false, cancellationToken)
		};
		var toggle = await RunToggleScenarioAsync(plan, cancellationToken);
		var golden = await BuildGoldenAsync(goldenPlan, cancellationToken);
		return new BenchmarkIteration(index, scenarios, toggle, golden);
	}

	private static async Task<ScenarioRun> RunScenarioAsync(
		ProjectContextPlan plan,
		bool compress,
		bool stripComments,
		bool hideSecrets,
		CancellationToken cancellationToken)
	{
		var transformKinds = CodeTransformIdentity.Resolve(compress, stripComments);
		var coldPreview = await MeasurePhaseAsync(
			async token =>
			{
				using var coldCompression = CodeCompressionFactory.CreateSession();
				using var coldSecrets = new SecretRedactionSession(CreateSmartSecretsDetector());
				return await BuildContentPreviewAsync(
					plan,
					transformKinds == CodeTransformKinds.None ? null : coldCompression,
					transformKinds,
					hideSecrets ? coldSecrets : null,
					token);
			},
			compression: null,
			secrets: null,
			cancellationToken);

		var analyzer = new FileContentAnalyzer();
		using var compression = CodeCompressionFactory.CreateSession();
		using var secrets = new SecretRedactionSession(CreateSmartSecretsDetector());
		var compressionContext = transformKinds != CodeTransformKinds.None
			? new CodeCompressionContext(plan.SourceRoot, compression, transformKinds)
			: null;
		var redactionContext = hideSecrets
			? new SecretRedactionContext(plan.SourceRoot, secrets)
			: null;
		var transformation = ContentTransformationContext.For(compressionContext, redactionContext);

		ContentReadFactSnapshot? prewarmReadFacts = null;
		var prewarm = await MeasurePhaseAsync(
			async token =>
			{
				if (compressionContext is not null)
				{
					var warmup = await new CodeCompressionPrewarmer(analyzer)
						.WarmAsync(compressionContext, plan.IncludedFiles, token);
					prewarmReadFacts = warmup.ReadFacts;
				}
				return 0L;
			},
			compression,
			secrets,
			cancellationToken);

		var metrics = await MeasurePhaseAsync(
			token => MeasureMetricsAsync(
				plan,
				analyzer,
				compressionContext,
				plan.IncludedFiles,
				prewarmReadFacts,
				token),
			compression,
			secrets,
			cancellationToken);

		var discovery = await MeasurePhaseAsync(
			async token =>
			{
				if (redactionContext is null)
					return 0L;
				var snapshot = await new SecretRedactionOutputPreparer(analyzer)
					.AnalyzeAsync(transformation!, plan.IncludedFiles, token);
				return snapshot.RedactedCount;
			},
			compression,
			secrets,
			cancellationToken);

		var warmPreview = await MeasurePhaseAsync(
				token => BuildContentPreviewAsync(
					plan,
					compressionContext is null ? null : compression,
					transformKinds,
					hideSecrets ? secrets : null,
				token),
			compression,
			secrets,
			cancellationToken);
		var repeatedPreview = await MeasurePhaseAsync(
			token => BuildContentPreviewAsync(
				plan,
				compressionContext is null ? null : compression,
				transformKinds,
				hideSecrets ? secrets : null,
				token),
			compression,
			secrets,
			cancellationToken);

		var selectionPaths = plan.IncludedFiles
			.Where(static (_, itemIndex) => itemIndex % 2 == 0)
			.ToArray();
		var selectionMetrics = await MeasurePhaseAsync(
			token => MeasureMetricsAsync(
				plan,
				analyzer,
				compressionContext,
				selectionPaths,
				readFacts: null,
				cancellationToken: token),
			compression,
			secrets,
			cancellationToken);
		var treeAndContent = await MeasurePhaseAsync(
			token => BuildTreeAndContentPreviewAsync(plan, transformation, token),
			compression,
			secrets,
			cancellationToken);

		return new ScenarioRun(
			ColdPreview: coldPreview.Measurement,
			Prewarm: prewarm.Measurement,
			MetricsInitialization: metrics.Measurement,
			SecretDiscovery: discovery.Measurement,
			PostLoadTotal: PhaseMeasurement.Combine(
				prewarm.Measurement,
				metrics.Measurement,
				discovery.Measurement),
			WarmPreview: warmPreview.Measurement,
			RepeatedPreview: repeatedPreview.Measurement,
			SelectionMetrics: selectionMetrics.Measurement,
			TreeToBoth: treeAndContent.Measurement,
			CompressionDiagnostics: compression.Diagnostics,
			SecretDiagnostics: secrets.GetCacheDiagnostics());
	}

	private static async Task<ToggleRun> RunToggleScenarioAsync(
		ProjectContextPlan plan,
		CancellationToken cancellationToken)
	{
		var analyzer = new FileContentAnalyzer();
		using var compression = CodeCompressionFactory.CreateSession();
		using var secrets = new SecretRedactionSession(CreateSmartSecretsDetector());
		var preparer = new SecretRedactionOutputPreparer(analyzer);
		var rawContext = ContentTransformationContext.For(
			compression: null,
			new SecretRedactionContext(plan.SourceRoot, secrets))!;
		var compressedContext = ContentTransformationContext.For(
			new CodeCompressionContext(plan.SourceRoot, compression),
			new SecretRedactionContext(plan.SourceRoot, secrets))!;

		var initialSecrets = await MeasurePhaseAsync(
			async token => (await preparer.AnalyzeAsync(rawContext, plan.IncludedFiles, token)).RedactedCount,
			compression,
			secrets,
			cancellationToken);
		var prewarm = await MeasurePhaseAsync(
			async token =>
			{
				await new CodeCompressionPrewarmer(analyzer).WarmAsync(
					compressedContext.Compression!,
					plan.IncludedFiles,
					token);
				return 0L;
			},
			compression,
			secrets,
			cancellationToken);
		var compressionOn = await MeasurePhaseAsync(
			async token => (await preparer.AnalyzeAsync(compressedContext, plan.IncludedFiles, token)).RedactedCount,
			compression,
			secrets,
			cancellationToken);
		var compressionOff = await MeasurePhaseAsync(
			async token => (await preparer.AnalyzeAsync(rawContext, plan.IncludedFiles, token)).RedactedCount,
			compression,
			secrets,
			cancellationToken);

		return new ToggleRun(
			InitialSecrets: initialSecrets.Measurement,
			CompressionPrewarm: prewarm.Measurement,
			CompressionOn: compressionOn.Measurement,
			CompressionOff: compressionOff.Measurement,
			CompressionDiagnostics: compression.Diagnostics,
			SecretDiagnostics: secrets.GetCacheDiagnostics());
	}

	private static async Task<long> BuildContentPreviewAsync(
		ProjectContextPlan plan,
		CodeCompressionSession? compression,
		CodeTransformKinds transformKinds,
		SecretRedactionSession? secrets,
		CancellationToken cancellationToken)
	{
		using var document = await new PreviewDocumentBuilder(new FileContentAnalyzer())
			.BuildContentDocumentAsync(
				plan.IncludedFiles,
				cancellationToken,
				path => Path.GetRelativePath(plan.SourceRoot, path).Replace('\\', '/'),
				transformationContext: ContentTransformationContext.For(
					compression is null
						? null
						: new CodeCompressionContext(plan.SourceRoot, compression, transformKinds),
					secrets is null ? null : new SecretRedactionContext(plan.SourceRoot, secrets)));
		return document?.CharacterCount ?? 0;
	}

	private static async Task<long> BuildTreeAndContentPreviewAsync(
		ProjectContextPlan plan,
		ContentTransformationContext? transformation,
		CancellationToken cancellationToken)
	{
		using var document = await new PreviewDocumentBuilder(new FileContentAnalyzer())
			.BuildTreeAndContentDocumentAsync(
				plan.SourceIdentity?.DisplayName ?? Path.GetFileName(plan.SourceRoot),
				plan.IncludedFiles,
				cancellationToken,
				TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(plan.SourceRoot),
				transformationContext: transformation);
		return document.CharacterCount;
	}

	private static async Task<long> MeasureMetricsAsync(
		ProjectContextPlan plan,
		IFileContentAnalyzer analyzer,
		CodeCompressionContext? compression,
		IReadOnlyList<string> paths,
		ContentReadFactSnapshot? readFacts,
		CancellationToken cancellationToken)
	{
		using var scope = compression?.BeginMeasurement();
		long characters = 0;
		foreach (var path in paths)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ContentReadFact? retainedFact = null;
			if (readFacts is not null && readFacts.TryGet(path, out var retained))
				retainedFact = retained;
			if (scope is null || !compression!.IsSupported(path))
			{
				var metrics = retainedFact?.RawMetrics ??
					(await analyzer.GetClassifiedMetricsAsync(path, cancellationToken)).Metrics;
				if (metrics is not null)
					characters += metrics.CharCount;
				continue;
			}

			var fact = retainedFact ?? await analyzer.ReadFactAsync(
				path,
				10L * 1024 * 1024,
				cancellationToken);
			if (!fact.IsMaterializedText || fact.Fingerprint is not { } fingerprint)
				continue;
			var compressionPlan = scope.ResolvePlan(
				path,
				Path.GetRelativePath(plan.SourceRoot, path),
				fact.Content!,
				fingerprint,
				cancellationToken);
			characters += compressionPlan.HasEdits
				? FileContentAnalyzer
					.ComputeTransformedMetrics(fact.Content, compressionPlan)
					.CharCount
				: fact.RawMetrics!.CharCount;
		}
		return characters;
	}

	private static async Task<GoldenHashes> BuildGoldenAsync(
		ProjectContextPlan baseline,
		CancellationToken cancellationToken)
	{
		using var compression = CodeCompressionFactory.CreateSession();
		using var secrets = new SecretRedactionSession(CreateSmartSecretsDetector());
		var transformedPlan = baseline with
		{
			Selection = baseline.Selection with { CompressCode = true, HideSecrets = true }
		};
		var service = new ProjectContextDocumentService(
			new TreeExportService(),
			new FileContentAnalyzer(),
			secretRedactionSession: secrets,
			codeCompressionSession: compression);
		var limits = new ProjectContextDocumentLimits(
			MaximumTreeNodes: 100_000,
			MaximumFiles: transformedPlan.IncludedFiles.Count,
			MaximumCharacters: 32 * 1024 * 1024,
			MaximumFileBytes: 10 * 1024 * 1024);
		var text = await service.BuildAsync(
			transformedPlan,
			ProjectContextView.TreeContent,
			ProjectContextDocumentFormat.Text,
			limits,
			cancellationToken);
		var json = await service.BuildAsync(
			transformedPlan,
			ProjectContextView.TreeContent,
			ProjectContextDocumentFormat.Json,
			limits,
			cancellationToken);
		return new GoldenHashes(ComputeSha256(text), ComputeSha256(json));
	}

	private static async Task<MeasuredResult<T>> MeasurePhaseAsync<T>(
		Func<CancellationToken, Task<T>> operation,
		CodeCompressionSession? compression,
		SecretRedactionSession? secrets,
		CancellationToken cancellationToken)
	{
		using var contentMeasurement = ContentPipelineDiagnostics.BeginMeasurement();
		var compressionBefore = compression?.Diagnostics;
		var secretsBefore = secrets?.GetCacheDiagnostics();
		var process = Process.GetCurrentProcess();
		process.Refresh();
		var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
		var gen0Before = GC.CollectionCount(0);
		var gen1Before = GC.CollectionCount(1);
		var gen2Before = GC.CollectionCount(2);
		var stopwatch = Stopwatch.StartNew();
		var result = await operation(cancellationToken);
		stopwatch.Stop();
		process.Refresh();
		var content = contentMeasurement.Capture();
		var compressionAfter = compression?.Diagnostics;
		var secretsAfter = secrets?.GetCacheDiagnostics();
		return new MeasuredResult<T>(
			result,
			new PhaseMeasurement(
				stopwatch.Elapsed.TotalMilliseconds,
				GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
				process.PeakWorkingSet64,
				process.PrivateMemorySize64,
				GC.CollectionCount(0) - gen0Before,
				GC.CollectionCount(1) - gen1Before,
				GC.CollectionCount(2) - gen2Before,
				content.FullFileReads,
				content.FullFileReadBytes,
				content.ContentFingerprintComputations,
				content.PlanApplications,
				Difference(compressionBefore, compressionAfter),
				Difference(secretsBefore, secretsAfter)));
	}

	private static CompressionWork Difference(
		CodeCompressionDiagnosticsSnapshot? before,
		CodeCompressionDiagnosticsSnapshot? after)
	{
		if (after is null)
			return new CompressionWork();
		before ??= new CodeCompressionDiagnosticsSnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, default);
		return new CompressionWork(
			after.HashComputations - before.HashComputations,
			after.CacheHits - before.CacheHits,
			after.CacheMisses - before.CacheMisses,
			after.AnalysisExecutions - before.AnalysisExecutions,
			after.PrewarmRequests - before.PrewarmRequests,
			after.PrewarmCacheHits - before.PrewarmCacheHits,
			after.PrewarmAnalyses - before.PrewarmAnalyses,
			after.PrewarmReuses - before.PrewarmReuses,
			after.UnsupportedFastPaths - before.UnsupportedFastPaths);
	}

	private static SecretWork Difference(
		SecretScanCacheDiagnostics? before,
		SecretScanCacheDiagnostics? after)
	{
		if (after is null)
			return new SecretWork();
		before ??= new SecretScanCacheDiagnostics(0, 0, 0, 0, 0, 0, 0, 0, 0);
		return new SecretWork(
			after.CacheHits - before.CacheHits,
			after.CacheMisses - before.CacheMisses,
			after.DetectionRuns - before.DetectionRuns,
			after.PeakFullContentBuffers);
	}

	private static IReadOnlyDictionary<string, BenchmarkMedian> BuildMedians(
		IReadOnlyList<BenchmarkIteration> iterations)
	{
		var measurements = new Dictionary<string, List<PhaseMeasurement>>(StringComparer.Ordinal);
		foreach (var iteration in iterations)
		{
			foreach (var (scenarioName, scenario) in iteration.Scenarios)
			{
				AddMeasurements(measurements, $"scenario.{scenarioName}", scenario.Measurements());
			}
			AddMeasurements(measurements, "toggle", iteration.Toggle.Measurements());
		}
		return measurements.ToDictionary(
			static pair => pair.Key,
			static pair => BenchmarkMedian.Create(pair.Value),
			StringComparer.Ordinal);
	}

	private static void AddMeasurements(
		Dictionary<string, List<PhaseMeasurement>> target,
		string prefix,
		IReadOnlyDictionary<string, PhaseMeasurement> values)
	{
		foreach (var (name, measurement) in values)
		{
			var key = $"{prefix}.{name}";
			if (!target.TryGetValue(key, out var list))
			{
				list = [];
				target.Add(key, list);
			}
			list.Add(measurement);
		}
	}

	private static async Task<ProjectContextPlan> BuildPlanAsync(string root)
	{
		var analysis = new ProjectAnalysisService(
			new ScanOptionsUseCase(new FileSystemScanner()),
			ProjectLoadWorkflowRuntime.CreateBuildTreeUseCase(),
			new FilterOptionSelectionService(),
			ProjectLoadWorkflowRuntime.CreateIgnoreOptionsService(),
			ProjectLoadWorkflowRuntime.CreateIgnoreRulesService(),
			new TreeExportService(),
			new FileContentAnalyzer());
		return await new ProjectContextPlanner(analysis).BuildAsync(
			new ProjectContextRequest(root, ProjectSelectionSpec.Standard),
			TestContext.Current.CancellationToken);
	}

	private static async Task<ProjectContextPlan> BuildGoldenPlanAsync()
	{
		var root = Path.Combine(
			Path.GetTempPath(),
			"DevProjex.CompressionOptimization.GoldenCorpus");
		if (Directory.Exists(root))
			Directory.Delete(root, recursive: true);
		Directory.CreateDirectory(root);

		var files = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["src/Service.cs"] =
				"namespace Golden;\npublic sealed class Service\n{\n    public string Token => \"golden-secret-value-123\";\n    public int Run(int value)\n    {\n        return value + 1;\n    }\n}\n",
			["src/component.tsx"] =
				"export const Card = (value: string) => (\n  <section data-token=\"golden-secret-value-123\">\n    {value}\n  </section>\n);\n",
			["src/worker.py"] =
				"def execute(value):\n    \"\"\"Return the normalized value.\"\"\"\n    token = \"golden-secret-value-123\"\n    return value.strip() + token\n"
		};
		var timestamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
		foreach (var (relativePath, content) in files)
		{
			var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			await File.WriteAllTextAsync(
				path,
				content,
				new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
				TestContext.Current.CancellationToken);
			File.SetLastWriteTimeUtc(path, timestamp);
		}

		return await BuildPlanAsync(root);
	}

	private static async Task<ProjectContextPlan> BuildCommentsOnlyPlanAsync()
	{
		var root = Path.Combine(
			Path.GetTempPath(),
			"DevProjex.CompressionOptimization.CommentsOnlyCorpus");
		if (Directory.Exists(root))
			Directory.Delete(root, recursive: true);
		Directory.CreateDirectory(root);

		var templates = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["web/page-{0}.html"] =
				"<!doctype html>\n<!-- page metadata -->\n<html><head><style>/* embedded CSS stays raw */ .card { color: red; }</style></head>\n<body><!-- navigation --><main class=\"card\">Dashboard</main>\n<script>// embedded JavaScript stays raw\nwindow.app = { ready: true };</script></body></html>\n",
			["web/site-{0}.css"] =
				"/* design tokens */\n:root { --asset: \"/* literal */\"; --gap: 1rem; }\n/* layout */\n.dashboard { display: grid; gap: var(--gap); background: url(\"/img/*hero*/.png\"); }\n.card { padding: 1rem; /* compact spacing */ border: 1px solid #ddd; }\n",
			["config/app-{0}.toml"] =
				"# service configuration\n[service]\nname = \"api#worker\" # deployment name\nendpoint = \"https://example.test/v1#health\"\n# retry policy\nretries = 4\n",
			["scripts/deploy-{0}.sh"] =
				"#!/usr/bin/env bash\n# Fail on the first deployment error.\nset -euo pipefail\nlabel=\"release#stable\" # visible release channel\ncat <<'EOF'\n# heredoc content is data\nEOF\nprintf '%s\\n' \"$label\"\n",
			["markup/view-{0}.axaml"] =
				"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!-- design-time note -->\n<UserControl xmlns=\"https://github.com/avaloniaui\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" x:Class=\"Bench.View\">\n  <TextBlock x:Name=\"Title\" Text=\"{Binding Title}\" /><!-- binding note -->\n</UserControl>\n",
			["projects/library-{0}.csproj"] =
				"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <!-- build contract -->\n  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n  <ItemGroup><!-- pinned package --><PackageReference Include=\"Example\" Version=\"1.0.0\" /></ItemGroup>\n</Project>\n",
			["config/deployment-{0}.yaml"] =
				"---\n# deployment metadata\nservice: &service\n  name: api # public name\n  image: \"registry/app#stable\"\n  script: |\n    echo \"# block scalar data\"\n    printf '%s\\n' done\nreplica: *service\n...\n"
		};
		var timestamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
		for (var copy = 0; copy < 8; copy++)
		{
			foreach (var (relativePathTemplate, content) in templates)
			{
				var relativePath = string.Format(
					CultureInfo.InvariantCulture,
					relativePathTemplate,
					copy);
				var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				await File.WriteAllTextAsync(
					path,
					content,
					new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
					TestContext.Current.CancellationToken);
				File.SetLastWriteTimeUtc(path, timestamp);
			}
		}

		return await BuildPlanAsync(root);
	}

	private static async Task<ProjectContextPlan> BuildStructuredDataStressPlanAsync()
	{
		var root = Path.Combine(
			Path.GetTempPath(),
			"DevProjex.CompressionOptimization.StructuredDataStressCorpus");
		if (Directory.Exists(root))
			Directory.Delete(root, recursive: true);
		Directory.CreateDirectory(root);

		var largeXml = new StringBuilder("<?xml version=\"1.0\"?>\n<catalog>\n");
		for (var index = 0; index < 6_000; index++)
		{
			largeXml.Append("  <!-- generated item ")
				.Append(index)
				.Append(" -->\n  <item id=\"")
				.Append(index)
				.Append("\"><![CDATA[value <!-- retained --> # ")
				.Append(index)
				.Append("]]></item>\n");
		}
		largeXml.Append("</catalog>\n");

		var deepYaml = new StringBuilder("---\n");
		for (var depth = 0; depth < 256; depth++)
		{
			deepYaml.Append(' ', depth * 2)
				.Append("level_")
				.Append(depth)
				.Append(": # nested mapping\n");
		}
		deepYaml.Append(' ', 512)
			.Append("payload: |\n")
			.Append(' ', 514)
			.Append("# block scalar content is data\n")
			.Append(' ', 514)
			.Append("done\n...\n");

		var files = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["large/catalog.xml"] = largeXml.ToString(),
			["deep/config.yaml"] = deepYaml.ToString(),
			["limits/over-cap.xml"] =
				"<!-- oversized comment -->\n<root>" + new string('x', 2 * 1024 * 1024) + "</root>\n"
		};
		var timestamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
		foreach (var (relativePath, content) in files)
		{
			var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			await File.WriteAllTextAsync(
				path,
				content,
				new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
				TestContext.Current.CancellationToken);
			File.SetLastWriteTimeUtc(path, timestamp);
		}

		return await BuildPlanAsync(root);
	}

	private static SmartSecretsDetector CreateSmartSecretsDetector() =>
		new(
			new GitleaksSecretDetector(),
			new SmartIgnoreService(
			[
				new CommonSmartIgnoreRule(),
				new FrontendArtifactsIgnoreRule(),
				new DotNetArtifactsIgnoreRule(),
				new PythonArtifactsIgnoreRule(),
				new JvmArtifactsIgnoreRule(),
				new RustArtifactsIgnoreRule(),
				new GoArtifactsIgnoreRule(),
				new PhpArtifactsIgnoreRule(),
				new RubyArtifactsIgnoreRule(),
				new SwiftArtifactsIgnoreRule(),
				new DartArtifactsIgnoreRule()
			]));

	private static string ResolveOutputPath(string stage)
	{
		var explicitPath = Environment.GetEnvironmentVariable("DEVPROJEX_COMPRESSION_BENCHMARK_OUTPUT");
		if (!string.IsNullOrWhiteSpace(explicitPath))
			return Path.GetFullPath(explicitPath);
		return Path.Combine(FindRepositoryRoot(), "artifacts", "compression-optimization", $"{stage}.json");
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

	private static string NormalizeStage(string? stage)
	{
		if (string.IsNullOrWhiteSpace(stage))
			return "local";
		var normalized = string.Concat(stage.Select(character =>
			char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-'));
		return normalized.Length == 0 ? "local" : normalized;
	}

	private static string ComputeSha256(string value) =>
		Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

	private sealed record CompressionOptimizationBenchmarkReport(
		int SchemaVersion,
		string Stage,
		DateTimeOffset CreatedAtUtc,
		BenchmarkMachine Machine,
		string ProjectRoot,
		int FileCount,
		int CommentsOnlyCorpusFileCount,
		int StructuredDataStressFileCount,
		IReadOnlyList<BenchmarkIteration> Runs,
		IReadOnlyDictionary<string, BenchmarkMedian> Medians);

	private sealed record BenchmarkMachine(
		string Name,
		string OperatingSystem,
		string Framework,
		string Architecture,
		int ProcessorCount);

	private sealed record BenchmarkIteration(
		int Index,
		IReadOnlyDictionary<string, ScenarioRun> Scenarios,
		ToggleRun Toggle,
		GoldenHashes Golden);

	private sealed record ScenarioRun(
		PhaseMeasurement ColdPreview,
		PhaseMeasurement Prewarm,
		PhaseMeasurement MetricsInitialization,
		PhaseMeasurement SecretDiscovery,
		PhaseMeasurement PostLoadTotal,
		PhaseMeasurement WarmPreview,
		PhaseMeasurement RepeatedPreview,
		PhaseMeasurement SelectionMetrics,
		PhaseMeasurement TreeToBoth,
		CodeCompressionDiagnosticsSnapshot CompressionDiagnostics,
		SecretScanCacheDiagnostics SecretDiagnostics)
	{
		public IReadOnlyDictionary<string, PhaseMeasurement> Measurements() =>
			new Dictionary<string, PhaseMeasurement>(StringComparer.Ordinal)
			{
				["coldPreview"] = ColdPreview,
				["prewarm"] = Prewarm,
				["metricsInitialization"] = MetricsInitialization,
				["secretDiscovery"] = SecretDiscovery,
				["postLoadTotal"] = PostLoadTotal,
				["warmPreview"] = WarmPreview,
				["repeatedPreview"] = RepeatedPreview,
				["selectionMetrics"] = SelectionMetrics,
				["treeToBoth"] = TreeToBoth
			};
	}

	private sealed record ToggleRun(
		PhaseMeasurement InitialSecrets,
		PhaseMeasurement CompressionPrewarm,
		PhaseMeasurement CompressionOn,
		PhaseMeasurement CompressionOff,
		CodeCompressionDiagnosticsSnapshot CompressionDiagnostics,
		SecretScanCacheDiagnostics SecretDiagnostics)
	{
		public IReadOnlyDictionary<string, PhaseMeasurement> Measurements() =>
			new Dictionary<string, PhaseMeasurement>(StringComparer.Ordinal)
			{
				["initialSecrets"] = InitialSecrets,
				["compressionPrewarm"] = CompressionPrewarm,
				["compressionOn"] = CompressionOn,
				["compressionOff"] = CompressionOff
			};
	}

	private sealed record GoldenHashes(string TextSha256, string JsonSha256);

	private sealed record MeasuredResult<T>(T Result, PhaseMeasurement Measurement);

	private sealed record PhaseMeasurement(
		double ElapsedMilliseconds,
		long AllocatedBytes,
		long PeakWorkingSetBytes,
		long PrivateBytes,
		int Gen0Collections,
		int Gen1Collections,
		int Gen2Collections,
		long FullFileReads,
		long FullFileReadBytes,
		long ContentFingerprintComputations,
		long PlanApplications,
		CompressionWork Compression,
		SecretWork Secrets)
	{
		public static PhaseMeasurement Combine(params PhaseMeasurement[] values) => new(
			values.Sum(static value => value.ElapsedMilliseconds),
			values.Sum(static value => value.AllocatedBytes),
			values.Max(static value => value.PeakWorkingSetBytes),
			values.Max(static value => value.PrivateBytes),
			values.Sum(static value => value.Gen0Collections),
			values.Sum(static value => value.Gen1Collections),
			values.Sum(static value => value.Gen2Collections),
			values.Sum(static value => value.FullFileReads),
			values.Sum(static value => value.FullFileReadBytes),
			values.Sum(static value => value.ContentFingerprintComputations),
			values.Sum(static value => value.PlanApplications),
			CompressionWork.Combine(values.Select(static value => value.Compression)),
			SecretWork.Combine(values.Select(static value => value.Secrets)));
	}

	private sealed record CompressionWork(
		long HashComputations = 0,
		long CacheHits = 0,
		long CacheMisses = 0,
		long AnalysisExecutions = 0,
		long PrewarmRequests = 0,
		long PrewarmCacheHits = 0,
		long PrewarmAnalyses = 0,
		long PrewarmReuses = 0,
		long UnsupportedFastPaths = 0)
	{
		public static CompressionWork Combine(IEnumerable<CompressionWork> values) => new(
			values.Sum(static value => value.HashComputations),
			values.Sum(static value => value.CacheHits),
			values.Sum(static value => value.CacheMisses),
			values.Sum(static value => value.AnalysisExecutions),
			values.Sum(static value => value.PrewarmRequests),
			values.Sum(static value => value.PrewarmCacheHits),
			values.Sum(static value => value.PrewarmAnalyses),
			values.Sum(static value => value.PrewarmReuses),
			values.Sum(static value => value.UnsupportedFastPaths));
	}

	private sealed record SecretWork(
		long CacheHits = 0,
		long CacheMisses = 0,
		long DetectionRuns = 0,
		int PeakFullContentBuffers = 0)
	{
		public static SecretWork Combine(IEnumerable<SecretWork> values) => new(
			values.Sum(static value => value.CacheHits),
			values.Sum(static value => value.CacheMisses),
			values.Sum(static value => value.DetectionRuns),
			values.Max(static value => value.PeakFullContentBuffers));
	}

	private sealed record BenchmarkMedian(
		double ElapsedMilliseconds,
		long AllocatedBytes,
		long FullFileReads,
		long FullFileReadBytes,
		long ContentFingerprintComputations,
		long PlanApplications,
		long CompressionHashes,
		long CompressionAnalyses,
		long SecretDetectionRuns,
		long PeakWorkingSetBytes,
		long PrivateBytes,
		int Gen0Collections,
		int Gen1Collections,
		int Gen2Collections)
	{
		public static BenchmarkMedian Create(IReadOnlyList<PhaseMeasurement> values) => new(
			Median(values.Select(static value => value.ElapsedMilliseconds)),
			Median(values.Select(static value => value.AllocatedBytes)),
			Median(values.Select(static value => value.FullFileReads)),
			Median(values.Select(static value => value.FullFileReadBytes)),
			Median(values.Select(static value => value.ContentFingerprintComputations)),
			Median(values.Select(static value => value.PlanApplications)),
			Median(values.Select(static value => value.Compression.HashComputations)),
			Median(values.Select(static value => value.Compression.AnalysisExecutions)),
			Median(values.Select(static value => value.Secrets.DetectionRuns)),
			Median(values.Select(static value => value.PeakWorkingSetBytes)),
			Median(values.Select(static value => value.PrivateBytes)),
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
