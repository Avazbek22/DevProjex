using System.Diagnostics;
using DevProjex.Application.Context;
using DevProjex.Application.Secrets;
using DevProjex.Application.Services;
using DevProjex.Infrastructure.Secrets;
using DevProjex.Infrastructure.SmartIgnore;

namespace DevProjex.Tests.Integration;

[Collection(SmartSecretsPerformanceCollection.Name)]
[Trait("Category", "LocalPerformance")]
public sealed class SmartSecretsPerformanceCharacterizationTests
{
	[Fact(Timeout = 60_000)]
	public async Task ManySmallFiles_CountPipeline_RemainsIncrementalAndBounded()
	{
		const int fileCount = 1_000;
		using var workspace = new TemporaryDirectory();
		workspace.CreateFile("project.csproj", "<Project />");
		var paths = new string[fileCount];
		long sourceBytes = 0;
		var expectedRedactions = 0;
		for (var index = 0; index < fileCount; index++)
		{
			var targetSize = index switch
			{
				< 500 => 1_024,
				< 800 => 4_096,
				< 950 => 16_384,
				_ => 65_536
			};
			var isConfiguration = index % 100 == 0;
			var prefix = isConfiguration
				? $"{{ \"Password\": \"p{index:D4}!\" }}\n"
				: $"internal sealed class Type{index:D4} {{ string apiKeyName = \"not-a-credential\"; }}\n";
			var content = prefix + new string('x', targetSize - prefix.Length);
			var relativePath = isConfiguration
				? $"config/appsettings.{index:D4}.json"
				: $"src/group-{index % 40:D2}/file-{index:D4}.cs";
			paths[index] = workspace.CreateFile(relativePath, content);
			sourceBytes += new FileInfo(paths[index]).Length;
			if (isConfiguration)
				expectedRedactions++;
		}

		var analyzer = new CountingBufferAnalyzer(new FileContentAnalyzer());
		using var session = new SecretRedactionSession(CreateSmartSecretsDetector());
		var preparer = new SecretRedactionOutputPreparer(analyzer);
		var context = new SecretRedactionContext(workspace.Path, session);
		// Rule parsing and the first regex compilation are fixed engine startup costs. Warm
		// them before measuring the per-selection pipeline so this gate detects file-scaling
		// regressions rather than runtime initialization changes.
		_ = await preparer.AnalyzeAsync(context, paths, TestContext.Current.CancellationToken);
		session.Disable();
		analyzer.ResetCounters();
		var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
		var stopwatch = Stopwatch.StartNew();
		var first = await preparer.AnalyzeAsync(context, paths, TestContext.Current.CancellationToken);
		stopwatch.Stop();
		var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
		var diagnostics = session.GetCacheDiagnostics();

		Assert.Equal(expectedRedactions, first.RedactedCount);
		Assert.Equal(fileCount, analyzer.ReadCount);
		Assert.Equal(sourceBytes, analyzer.BytesRead);
		Assert.Equal(0, diagnostics.ActiveFullContentBuffers);
		Assert.InRange(
			diagnostics.PeakFullContentBuffers,
			1,
			Math.Min(8, Math.Max(1, Environment.ProcessorCount)));
		Assert.InRange(diagnostics.RetainedBytes, 1, diagnostics.MaximumRetainedBytes);
		Assert.True(
			allocated < sourceBytes * 2,
			$"Count-only scan allocated {allocated:N0} bytes for {sourceBytes:N0} source bytes.");

		_ = await preparer.AnalyzeAsync(context, paths, TestContext.Current.CancellationToken);
		Assert.Equal(fileCount, analyzer.ReadCount);

		var changed = await File.ReadAllTextAsync(paths[0], TestContext.Current.CancellationToken);
		await File.WriteAllTextAsync(paths[0], changed[..^1] + "y", TestContext.Current.CancellationToken);
		File.SetLastWriteTimeUtc(paths[0], DateTime.UtcNow.AddSeconds(2));
		_ = await preparer.AnalyzeAsync(context, paths, TestContext.Current.CancellationToken);
		Assert.Equal(fileCount + 1, analyzer.ReadCount);

		session.Disable();
		diagnostics = session.GetCacheDiagnostics();
		Assert.Equal(0, diagnostics.EntryCount);
		Assert.Equal(0, diagnostics.RetainedBytes);
		TestContext.Current.TestOutputHelper?.WriteLine(
			$"Smart Secrets many-small-files baseline: files={fileCount:N0}, bytes={sourceBytes:N0}, " +
			$"elapsed={stopwatch.Elapsed.TotalMilliseconds:F2} ms, allocated={allocated:N0} B.");
	}

	[Fact]
	public async Task RealProjectDetectorProfile_ReportsCandidateAndRegexCosts()
	{
		var configuredRoots = Environment.GetEnvironmentVariable("DEVPROJEX_SECRET_PROFILE_ROOTS");
		if (string.IsNullOrWhiteSpace(configuredRoots))
			Assert.Skip("Set DEVPROJEX_SECRET_PROFILE_ROOTS to semicolon-separated project roots.");

		foreach (var root in configuredRoots.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var files = await LoadSelectedTextFilesAsync(root);
			var detector = new GitleaksSecretDetector();
			var initializationAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
			var initializationStopwatch = Stopwatch.StartNew();
			_ = detector.RuleCount;
			initializationStopwatch.Stop();
			var initializationAllocated = GC.GetTotalAllocatedBytes(precise: true) -
			                              initializationAllocatedBefore;
			var smartDetector = CreateSmartSecretsDetector();
			var orderedPaths = files.Select(static file => file.FullPath).ToArray();
			using var coldSession = new SecretRedactionSession(smartDetector);
			var coldPreparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());
			var coldContext = new SecretRedactionContext(root, coldSession);
			var coldAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
			var coldStopwatch = Stopwatch.StartNew();
			_ = await coldPreparer.AnalyzeAsync(
				coldContext,
				orderedPaths,
				TestContext.Current.CancellationToken);
			coldStopwatch.Stop();
			var coldAllocated = GC.GetTotalAllocatedBytes(precise: true) - coldAllocatedBefore;
			coldSession.Disable();
			var smartScope = smartDetector.CreateScope(root);
			var totalCharacters = files.Sum(static file => (long)file.Content.Length);
			var candidateRuleIds = files
				.SelectMany(file => detector.InspectCandidateRuleIds(
					file.RelativePath,
					file.Content.AsSpan(),
					TestContext.Current.CancellationToken))
				.ToArray();
			var uniqueCandidateRules = candidateRuleIds.ToHashSet(StringComparer.Ordinal);
			var busiestRules = candidateRuleIds
				.GroupBy(static id => id, StringComparer.Ordinal)
				.OrderByDescending(static group => group.Count())
				.Take(5)
				.Select(static group => $"{group.Key}:{group.Count()}");

			var candidateMeasurement = Measure(files, (file, token) =>
				detector.InspectCandidates(file.RelativePath, file.Content.AsSpan(), token).CandidateRuleCount);
			var detectionMeasurement = Measure(files, (file, token) =>
				detector.Detect(file.RelativePath, file.Content.AsSpan(), token).Count);
			var smartMeasurement = Measure(files, (file, token) =>
				smartScope.Detect(file.FullPath, file.RelativePath, file.Content.AsSpan(), token).Count);
			using var session = new SecretRedactionSession(smartDetector);
			var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());
			var context = new SecretRedactionContext(root, session);
			var pipelineStopwatch = Stopwatch.StartNew();
			var pipelineResult = await preparer.AnalyzeAsync(
				context,
				orderedPaths,
				TestContext.Current.CancellationToken);
			pipelineStopwatch.Stop();
			var warmStopwatch = Stopwatch.StartNew();
			_ = await preparer.AnalyzeAsync(
				context,
				orderedPaths,
				TestContext.Current.CancellationToken);
			warmStopwatch.Stop();

			TestContext.Current.TestOutputHelper?.WriteLine(
				$"{Path.GetFileName(root)}: files={files.Count:N0}, chars={totalCharacters:N0}, " +
				$"candidates/file={(double)candidateMeasurement.ResultCount / Math.Max(1, files.Count):F1}, " +
				$"uniqueCandidates={uniqueCandidateRules.Count:N0}, " +
				$"topCandidates={string.Join(',', busiestRules)}, " +
				$"engineInit={initializationStopwatch.Elapsed.TotalMilliseconds:F2} ms/" +
				$"{initializationAllocated:N0} B, " +
				$"cold={coldStopwatch.Elapsed.TotalMilliseconds:F2} ms/{coldAllocated:N0} B, " +
				$"candidate={candidateMeasurement.Elapsed.TotalMilliseconds:F2} ms, " +
				$"detect={detectionMeasurement.Elapsed.TotalMilliseconds:F2} ms, " +
				$"smart={smartMeasurement.Elapsed.TotalMilliseconds:F2} ms, " +
				$"throughput={ToMegabytes(totalCharacters) / smartMeasurement.Elapsed.TotalSeconds:F1} MB/s, " +
				$"allocated={smartMeasurement.AllocatedBytes:N0} B, " +
				$"pipeline={pipelineStopwatch.Elapsed.TotalMilliseconds:F2} ms, " +
				$"warm={warmStopwatch.Elapsed.TotalMilliseconds:F2} ms, " +
				$"redactions={pipelineResult.RedactedCount}.");
		}
	}

	private static Measurement Measure(
		IReadOnlyList<LoadedTextFile> files,
		Func<LoadedTextFile, CancellationToken, int> operation)
	{
		for (var warmup = 0; warmup < 2; warmup++)
		{
			foreach (var file in files)
				_ = operation(file, TestContext.Current.CancellationToken);
		}

		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
		var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
		var resultCount = 0;
		var stopwatch = Stopwatch.StartNew();
		foreach (var file in files)
			resultCount += operation(file, TestContext.Current.CancellationToken);
		stopwatch.Stop();
		return new Measurement(
			stopwatch.Elapsed,
			GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
			resultCount);
	}

	private static async Task<IReadOnlyList<LoadedTextFile>> LoadSelectedTextFilesAsync(string root)
	{
		var fullRoot = Path.GetFullPath(root);
		var plan = await BuildPlanAsync(fullRoot);
		var analyzer = new FileContentAnalyzer();
		var files = new List<LoadedTextFile>(plan.IncludedFiles.Count);
		foreach (var path in plan.IncludedFiles)
		{
			var content = await analyzer.TryReadAsTextAsync(
				path,
				SecretRedactionOutputPreparer.MaximumScannableFileBytes,
				TestContext.Current.CancellationToken);
			if (content is not null)
			{
				files.Add(new LoadedTextFile(
					path,
					Path.GetRelativePath(fullRoot, path).Replace('\\', '/'),
					content.Content));
			}
		}
		return files;
	}

	private static Task<ProjectContextPlan> BuildPlanAsync(string root)
	{
		var analysis = new ProjectAnalysisService(
			new ScanOptionsUseCase(new FileSystemScanner()),
			ProjectLoadWorkflowRuntime.CreateBuildTreeUseCase(),
			new FilterOptionSelectionService(),
			ProjectLoadWorkflowRuntime.CreateIgnoreOptionsService(),
			ProjectLoadWorkflowRuntime.CreateIgnoreRulesService(),
			new TreeExportService(),
			new FileContentAnalyzer());
		return new ProjectContextPlanner(analysis).BuildAsync(
			new ProjectContextRequest(root, ProjectSelectionSpec.Standard),
			TestContext.Current.CancellationToken);
	}

	private static double ToMegabytes(long characters) => characters * sizeof(char) / 1_000_000d;

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
				new RubyArtifactsIgnoreRule()
			]));

	private sealed record LoadedTextFile(string FullPath, string RelativePath, string Content);
	private readonly record struct Measurement(TimeSpan Elapsed, long AllocatedBytes, int ResultCount);

	private sealed class CountingBufferAnalyzer(IFileContentAnalyzer inner) : IFileContentAnalyzer
	{
		private int _readCount;
		private long _bytesRead;

		public int ReadCount => Volatile.Read(ref _readCount);
		public long BytesRead => Interlocked.Read(ref _bytesRead);

		public void ResetCounters()
		{
			Interlocked.Exchange(ref _readCount, 0);
			Interlocked.Exchange(ref _bytesRead, 0);
		}

		public FileContentClassification? ClassifyWithoutReading(string path) =>
			inner.ClassifyWithoutReading(path);

		public ValueTask<bool> IsTextFileAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.IsTextFileAsync(path, cancellationToken);

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.GetTextFileMetricsAsync(path, cancellationToken);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.TryReadAsTextAsync(path, cancellationToken);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			inner.TryReadAsTextAsync(path, maxSizeForFullRead, cancellationToken);

		public async ValueTask<ICompleteTextFileBuffer> OpenCompleteTextBufferAsync(
			string path,
			long maximumBytes,
			CancellationToken cancellationToken = default)
		{
			var result = await inner.OpenCompleteTextBufferAsync(path, maximumBytes, cancellationToken);
			if (result.Classification == FileContentClassification.Text)
			{
				Interlocked.Increment(ref _readCount);
				Interlocked.Add(ref _bytesRead, result.SizeBytes);
			}
			return result;
		}
	}
}

// GC.GetTotalAllocatedBytes is process-wide, so concurrent integration tests would contaminate
// the allocation characterization and turn unrelated work into a false performance regression.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SmartSecretsPerformanceCollection
{
	public const string Name = "SmartSecretsPerformance";
}
