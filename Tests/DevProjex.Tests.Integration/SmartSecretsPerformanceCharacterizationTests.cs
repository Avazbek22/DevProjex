using System.Security.Cryptography;
using DevProjex.Application.Context;
using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.Secrets;

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

		// Exercise both clean and detected-file paths before the process-wide allocation sample so
		// one-time runtime initialization cannot be mistaken for per-file pipeline overhead.
		using (var warmUpSession = new SecretRedactionSession(CreateSmartSecretsDetector()))
		{
			await warmUpSession.BeginWarmUp();
			var warmUpPreparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());
			var warmUpContext = new SecretRedactionContext(workspace.Path, warmUpSession);
			var warmUpPaths = paths[..Math.Min(paths.Length, SecretRedactionOutputPreparer.MaximumParallelScans)];
			_ = await warmUpPreparer.DiscoverAsync(
				warmUpContext,
				warmUpPaths,
				TestContext.Current.CancellationToken);
		}

		var analyzer = new CountingBufferAnalyzer(new FileContentAnalyzer());
		using var session = new SecretRedactionSession(CreateSmartSecretsDetector());
		var preparer = new SecretRedactionOutputPreparer(analyzer);
		var context = new SecretRedactionContext(workspace.Path, session);
		await session.BeginWarmUp();
		analyzer.ResetCounters();
		var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
		var firstStopwatch = Stopwatch.StartNew();
		var first = await preparer.DiscoverAsync(context, paths, TestContext.Current.CancellationToken);
		firstStopwatch.Stop();
		var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
		var diagnostics = session.GetCacheDiagnostics();

		Assert.Equal(expectedRedactions, first.RedactedCount);
		Assert.Equal(fileCount, analyzer.ReadCount);
		Assert.Equal(sourceBytes, analyzer.BytesRead);
		Assert.Equal(0, diagnostics.ActiveFullContentBuffers);
		Assert.InRange(
			diagnostics.PeakFullContentBuffers,
			1,
			Math.Min(
				SecretRedactionOutputPreparer.MaximumParallelScans,
				Math.Max(1, Environment.ProcessorCount)));
		Assert.InRange(diagnostics.RetainedBytes, 1, diagnostics.MaximumRetainedBytes);
		const int maximumPerFileOverheadBytes = 4 * 1024;
		var allocationBudget = sourceBytes * 2 + fileCount * maximumPerFileOverheadBytes;
		Assert.True(
			allocated < allocationBudget,
			$"Count-only scan allocated {allocated:N0} bytes; the cross-platform budget was " +
			$"{allocationBudget:N0} bytes for {sourceBytes:N0} source bytes across {fileCount:N0} files.");

		session.Disable();
		analyzer.ResetCounters();
		var steadyStopwatch = Stopwatch.StartNew();
		_ = await preparer.DiscoverAsync(context, paths, TestContext.Current.CancellationToken);
		steadyStopwatch.Stop();
		Assert.Equal(fileCount, analyzer.ReadCount);
		var detectionsAfterCacheRepopulation = session.GetCacheDiagnostics().DetectionRuns;

		_ = await preparer.DiscoverAsync(context, paths, TestContext.Current.CancellationToken);
		// Equal size and timestamp are not proof that privacy-sensitive content is unchanged. A warm
		// refresh re-reads each file for its fingerprint, but cached findings avoid every detector run.
		Assert.Equal(fileCount * 2, analyzer.ReadCount);
		Assert.Equal(
			detectionsAfterCacheRepopulation,
			session.GetCacheDiagnostics().DetectionRuns);

		var readsBeforeSelectionOnlyRefresh = analyzer.ReadCount;
		var detectionsBeforeSelectionOnlyRefresh = session.GetCacheDiagnostics().DetectionRuns;
		var selectionOnlyStopwatch = Stopwatch.StartNew();
		var selectionOnly = await preparer.DiscoverAsync(
			context,
			paths,
			SecretDiscoveryCacheMode.ReuseValidatedContent,
			TestContext.Current.CancellationToken);
		selectionOnlyStopwatch.Stop();
		Assert.Equal(expectedRedactions, selectionOnly.RedactedCount);
		Assert.Equal(readsBeforeSelectionOnlyRefresh, analyzer.ReadCount);
		Assert.Equal(
			detectionsBeforeSelectionOnlyRefresh,
			session.GetCacheDiagnostics().DetectionRuns);

		var changed = await File.ReadAllTextAsync(paths[0], TestContext.Current.CancellationToken);
		await File.WriteAllTextAsync(paths[0], changed[..^1] + "y", TestContext.Current.CancellationToken);
		File.SetLastWriteTimeUtc(paths[0], DateTime.UtcNow.AddSeconds(2));
		_ = await preparer.DiscoverAsync(context, paths, TestContext.Current.CancellationToken);
		Assert.Equal(fileCount * 3, analyzer.ReadCount);
		Assert.Equal(
			detectionsAfterCacheRepopulation + 1,
			session.GetCacheDiagnostics().DetectionRuns);

		session.Disable();
		diagnostics = session.GetCacheDiagnostics();
		Assert.Equal(0, diagnostics.EntryCount);
		Assert.Equal(0, diagnostics.RetainedBytes);
		TestContext.Current.TestOutputHelper?.WriteLine(
			$"Smart Secrets many-small-files baseline: files={fileCount:N0}, bytes={sourceBytes:N0}, " +
			$"first={firstStopwatch.Elapsed.TotalMilliseconds:F2} ms, " +
			$"strict-warm={steadyStopwatch.Elapsed.TotalMilliseconds:F2} ms, " +
			$"selection-only={selectionOnlyStopwatch.Elapsed.TotalMilliseconds:F2} ms, " +
			$"allocated={allocated:N0} B.");
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
			var orderedPaths = files.Select(static file => file.FullPath).ToArray();
			var totalCharacters = files.Sum(static file => (long)file.Content.Length);
			var manifestIdentity = ComputeManifestIdentity(files);
			var smartDetector = CreateSmartSecretsDetector();
			using var session = new SecretRedactionSession(smartDetector);
			var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());
			var context = new SecretRedactionContext(root, session);

			ForceCollection();
			var retainedBeforeWarmUp = GC.GetTotalMemory(forceFullCollection: false);
			var warmUpAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
			var warmUpStopwatch = Stopwatch.StartNew();
			await session.BeginWarmUp();
			warmUpStopwatch.Stop();
			var warmUpAllocated = GC.GetTotalAllocatedBytes(precise: true) - warmUpAllocatedBefore;
			ForceCollection();
			var retainedAfterWarmUp = GC.GetTotalMemory(forceFullCollection: false);

			var firstAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
			var firstStopwatch = Stopwatch.StartNew();
			var firstResult = await preparer.AnalyzeAsync(
				context,
				orderedPaths,
				TestContext.Current.CancellationToken);
			firstStopwatch.Stop();
			var firstAllocated = GC.GetTotalAllocatedBytes(precise: true) - firstAllocatedBefore;

			session.Disable();
			var steadyAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
			var steadyStopwatch = Stopwatch.StartNew();
			var steadyResult = await preparer.AnalyzeAsync(
				context,
				orderedPaths,
				TestContext.Current.CancellationToken);
			steadyStopwatch.Stop();
			var steadyAllocated = GC.GetTotalAllocatedBytes(precise: true) - steadyAllocatedBefore;

			var cachedStopwatch = Stopwatch.StartNew();
			var cachedResult = await preparer.AnalyzeAsync(
				context,
				orderedPaths,
				TestContext.Current.CancellationToken);
			cachedStopwatch.Stop();
			var selectionOnlyStopwatch = Stopwatch.StartNew();
			var selectionOnlyResult = await preparer.DiscoverAsync(
				context,
				orderedPaths,
				SecretDiscoveryCacheMode.ReuseValidatedContent,
				TestContext.Current.CancellationToken);
			selectionOnlyStopwatch.Stop();
			Assert.Equal(firstResult.RedactedCount, steadyResult.RedactedCount);
			Assert.Equal(firstResult.RedactedCount, cachedResult.RedactedCount);
			Assert.Equal(firstResult.RedactedCount, selectionOnlyResult.RedactedCount);

			var smartScope = smartDetector.CreateScope(root);
			var actualRuleIds = files
				.SelectMany(file => smartScope
					.Detect(file.FullPath, file.RelativePath, file.Content.AsSpan(), TestContext.Current.CancellationToken)
					.Select(static finding => finding.RuleId))
				.ToHashSet(StringComparer.Ordinal);
			var candidateRuleIds = files
				.SelectMany(file => detector.InspectCandidateRuleIds(
					file.RelativePath,
					file.Content.AsSpan(),
					TestContext.Current.CancellationToken))
				.ToArray();
			var uniqueCandidateRules = candidateRuleIds.ToHashSet(StringComparer.Ordinal);
			var runnableRules = files
				.SelectMany(file => detector.InspectRunnableRuleIds(
						file.RelativePath,
						file.Content.AsSpan(),
						TestContext.Current.CancellationToken)
					.Select(ruleId => new { file.RelativePath, RuleId = ruleId }))
				.ToArray();
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
			TestContext.Current.TestOutputHelper?.WriteLine(
				$"{Path.GetFileName(root)}: manifest={manifestIdentity}, files={files.Count:N0}, " +
				$"chars={totalCharacters:N0}, " +
				$"candidates/file={(double)candidateMeasurement.ResultCount / Math.Max(1, files.Count):F1}, " +
				$"uniqueCandidates={uniqueCandidateRules.Count:N0}, " +
				$"candidateIds={string.Join(',', uniqueCandidateRules.Order(StringComparer.Ordinal))}, " +
				$"topCandidates={string.Join(',', busiestRules)}, " +
				$"runnable={string.Join(',', runnableRules.GroupBy(static item => item.RuleId, StringComparer.Ordinal).Select(static group => $"{group.Key}:{group.Count()}"))}, " +
				$"runnableFiles={string.Join(',', runnableRules.Select(static item => $"{item.RelativePath}:{item.RuleId}"))}, " +
				$"actualRules={string.Join(',', actualRuleIds.Order(StringComparer.Ordinal))}, " +
				$"engineInit={initializationStopwatch.Elapsed.TotalMilliseconds:F2} ms/" +
				$"{initializationAllocated:N0} B, " +
				$"warmup={warmUpStopwatch.Elapsed.TotalMilliseconds:F2} ms/{warmUpAllocated:N0} B, " +
				$"retainedWarmup={retainedAfterWarmUp - retainedBeforeWarmUp:N0} B, " +
				$"first={firstStopwatch.Elapsed.TotalMilliseconds:F2} ms/{firstAllocated:N0} B, " +
				$"steady={steadyStopwatch.Elapsed.TotalMilliseconds:F2} ms/{steadyAllocated:N0} B, " +
				$"cached={cachedStopwatch.Elapsed.TotalMilliseconds:F2} ms, " +
				$"selectionOnly={selectionOnlyStopwatch.Elapsed.TotalMilliseconds:F2} ms, " +
				$"candidate={candidateMeasurement.Elapsed.TotalMilliseconds:F2} ms, " +
				$"detect={detectionMeasurement.Elapsed.TotalMilliseconds:F2} ms, " +
				$"smart={smartMeasurement.Elapsed.TotalMilliseconds:F2} ms, " +
				$"throughput={ToMegabytes(totalCharacters) / smartMeasurement.Elapsed.TotalSeconds:F1} MB/s, " +
				$"allocated={smartMeasurement.AllocatedBytes:N0} B, " +
				$"redactions={firstResult.RedactedCount}.");
		}
	}

	private static string ComputeManifestIdentity(IReadOnlyList<LoadedTextFile> files)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		foreach (var file in files.OrderBy(static file => file.RelativePath, StringComparer.Ordinal))
		{
			hash.AppendData(Encoding.UTF8.GetBytes(file.RelativePath));
			hash.AppendData([0]);
			hash.AppendData(Encoding.UTF8.GetBytes(file.Content));
			hash.AppendData([0]);
		}
		return Convert.ToHexString(hash.GetHashAndReset())[..12];
	}

	private static void ForceCollection()
	{
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
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
