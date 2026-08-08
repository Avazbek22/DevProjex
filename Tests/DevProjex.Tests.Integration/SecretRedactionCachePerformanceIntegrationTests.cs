using System.Diagnostics;
using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Integration;

[Trait("Category", "LocalPerformance")]
public sealed class SecretRedactionCachePerformanceIntegrationTests
{
	private const int FileCount = 500;
	private const int CharactersPerFile = 400_000;
	private const string Secret = "baseline-secret-value-0123456789";

	[Fact(Timeout = 120_000)]
	public async Task CountOnlyScan_LargeSyntheticSelection_IsBoundedAndIncrementalAcrossLifecycle()
	{
		if (!string.Equals(
		    Environment.GetEnvironmentVariable("DEVPROJEX_RUN_LARGE_PERF_TESTS"),
		    "1",
		    StringComparison.Ordinal))
		{
			Assert.Skip("Set DEVPROJEX_RUN_LARGE_PERF_TESTS=1 for the pre-release performance gate.");
		}

		using var workspace = new TemporaryDirectory();
		var payload = $"token={Secret}\n" + new string('x', CharactersPerFile - Secret.Length - 7);
		var paths = new string[FileCount];
		for (var index = 0; index < paths.Length; index++)
			paths[index] = workspace.CreateFile($"src/file-{index:D4}.txt", payload);

		var detector = new CountingDetector();
		var analyzer = new CountingFileContentAnalyzer(new FileContentAnalyzer());
		var session = new SecretRedactionSession(detector);
		var preparer = new SecretRedactionOutputPreparer(analyzer);
		var context = new SecretRedactionContext(workspace.Path, session);
		ForceFullCollection();
		var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
		var retainedBefore = GC.GetTotalMemory(forceFullCollection: true);

		var stopwatch = Stopwatch.StartNew();
		var first = await preparer.AnalyzeAsync(
			context,
			paths,
			TestContext.Current.CancellationToken);
		stopwatch.Stop();
		var firstAllocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
		var firstDiagnostics = session.GetCacheDiagnostics();
		Assert.Equal(FileCount, first.RedactedCount);
		Assert.Equal(FileCount, analyzer.FullContentReadCount);
		Assert.Equal(FileCount * (long)CharactersPerFile, analyzer.FullContentBytesRead);
		Assert.Equal(FileCount, detector.CallCount);
		Assert.Equal(FileCount, firstDiagnostics.EntryCount);
		Assert.InRange(firstDiagnostics.RetainedBytes, 1, firstDiagnostics.MaximumRetainedBytes);
		Assert.Equal(0, firstDiagnostics.ActiveFullContentBuffers);
		Assert.InRange(
			firstDiagnostics.PeakFullContentBuffers,
			1,
			Math.Min(8, Math.Max(1, Environment.ProcessorCount)));
		var sourceCharacterCount = FileCount * (long)CharactersPerFile;
		Assert.True(
			firstAllocated < sourceCharacterCount,
			$"Count-only scan allocated {firstAllocated:N0} bytes for {FileCount * (long)CharactersPerFile:N0} source characters.");

		var readsAfterFirst = analyzer.FullContentReadCount;
		var bytesAfterFirst = analyzer.FullContentBytesRead;
		var detectionsAfterFirst = detector.CallCount;
		var warmStopwatch = Stopwatch.StartNew();
		var second = await preparer.AnalyzeAsync(
			context,
			paths,
			TestContext.Current.CancellationToken);
		warmStopwatch.Stop();
		Assert.Equal(first.RedactedCount, second.RedactedCount);
		Assert.Equal(readsAfterFirst + FileCount, analyzer.FullContentReadCount);
		Assert.Equal(
			bytesAfterFirst + FileCount * (long)CharactersPerFile,
			analyzer.FullContentBytesRead);
		Assert.Equal(detectionsAfterFirst, detector.CallCount);
		Assert.True(
			warmStopwatch.Elapsed <= stopwatch.Elapsed * 2 + TimeSpan.FromSeconds(1),
			$"Fingerprint-only refresh took {warmStopwatch.Elapsed.TotalSeconds:F3}s after a " +
			$"{stopwatch.Elapsed.TotalSeconds:F3}s cold scan.");

		var readsAfterSecond = analyzer.FullContentReadCount;
		var bytesAfterSecond = analyzer.FullContentBytesRead;

		File.WriteAllText(paths[0], payload[..^1] + "y");
		File.SetLastWriteTimeUtc(paths[0], DateTime.UtcNow.AddSeconds(2));
		var third = await preparer.AnalyzeAsync(
			context,
			paths,
			TestContext.Current.CancellationToken);
		Assert.Equal(first.RedactedCount, third.RedactedCount);
		Assert.Equal(readsAfterSecond + FileCount, analyzer.FullContentReadCount);
		Assert.Equal(
			bytesAfterSecond + FileCount * (long)CharactersPerFile,
			analyzer.FullContentBytesRead);
		Assert.Equal(detectionsAfterFirst + 1, detector.CallCount);

		session.Disable();
		var disabledDiagnostics = session.GetCacheDiagnostics();
		Assert.Equal(0, disabledDiagnostics.EntryCount);
		Assert.Equal(0, disabledDiagnostics.RetainedBytes);
		Assert.Null(session.GetRedactionCount(workspace.Path, paths));

		for (var cycle = 0; cycle < 10; cycle++)
		{
			var cycleResult = await preparer.AnalyzeAsync(
				context,
				paths,
				TestContext.Current.CancellationToken);
			Assert.Equal(FileCount, cycleResult.RedactedCount);
			session.Disable();
			var cycleDiagnostics = session.GetCacheDiagnostics();
			Assert.Equal(0, cycleDiagnostics.EntryCount);
			Assert.Equal(0, cycleDiagnostics.RetainedBytes);
			Assert.Equal(0, cycleDiagnostics.ActiveFullContentBuffers);
		}

		ForceFullCollection();
		var retainedGrowth = Math.Max(0, GC.GetTotalMemory(forceFullCollection: true) - retainedBefore);
		TestContext.Current.TestOutputHelper?.WriteLine(
			$"Hide Secrets hardened scan: files={FileCount}, sourceChars={FileCount * (long)CharactersPerFile:N0}, " +
			$"cold={stopwatch.Elapsed.TotalSeconds:F3}s, warm={warmStopwatch.Elapsed.TotalSeconds:F3}s, " +
			$"allocated={firstAllocated:N0}, " +
			$"cacheBytes={firstDiagnostics.RetainedBytes:N0}, retainedAfterCycles={retainedGrowth:N0}.");
		Assert.True(
			retainedGrowth < 64L * 1024 * 1024,
			$"Ten enable/disable cycles retained {retainedGrowth:N0} managed bytes.");
	}

	private static void ForceFullCollection()
	{
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
	}

	private sealed class CountingDetector : ISecretDetector
	{
		private int _callCount;

		public int CallCount => Volatile.Read(ref _callCount);

		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _callCount);
			cancellationToken.ThrowIfCancellationRequested();
			var start = content.IndexOf(Secret, StringComparison.Ordinal);
			return start < 0
				? []
				: [new DetectedSecret("baseline", start, Secret.Length, Secret, 0)];
		}

		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			ReadOnlySpan<char> content,
			CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _callCount);
			cancellationToken.ThrowIfCancellationRequested();
			var start = content.IndexOf(Secret, StringComparison.Ordinal);
			return start < 0
				? []
				: [new DetectedSecret("baseline", start, Secret.Length, Secret, 0)];
		}
	}

	private sealed class CountingFileContentAnalyzer(IFileContentAnalyzer inner) : IFileContentAnalyzer
	{
		private int _fullContentReadCount;
		private long _fullContentBytesRead;

		public int FullContentReadCount => Volatile.Read(ref _fullContentReadCount);
		public long FullContentBytesRead => Interlocked.Read(ref _fullContentBytesRead);

		public FileContentClassification? ClassifyWithoutReading(string path) =>
			inner.ClassifyWithoutReading(path);

		public async ValueTask<FileContentReadResult> ReadClassifiedAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default)
		{
			var result = await inner.ReadClassifiedAsync(path, maxSizeForFullRead, cancellationToken);
			if (result.Content is { IsEstimated: false } content)
			{
				Interlocked.Increment(ref _fullContentReadCount);
				Interlocked.Add(ref _fullContentBytesRead, content.SizeBytes);
			}
			return result;
		}

		public ValueTask<bool> IsTextFileAsync(string path, CancellationToken cancellationToken = default) =>
			inner.IsTextFileAsync(path, cancellationToken);

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.GetTextFileMetricsAsync(path, cancellationToken);

		public ValueTask<FileContentMetricsResult> GetClassifiedMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.GetClassifiedMetricsAsync(path, cancellationToken);

		public async ValueTask<IFileContentSnapshot> OpenCompleteSnapshotAsync(
			string path,
			CancellationToken cancellationToken = default)
		{
			var snapshot = await inner.OpenCompleteSnapshotAsync(path, cancellationToken);
			if (snapshot.Result.Classification == FileContentClassification.Text &&
			    snapshot.Result.Metrics is { } metrics)
			{
				Interlocked.Increment(ref _fullContentReadCount);
				Interlocked.Add(ref _fullContentBytesRead, metrics.SizeBytes);
			}
			return snapshot;
		}

		public async ValueTask<ICompleteTextFileBuffer> OpenCompleteTextBufferAsync(
			string path,
			long maximumBytes,
			CancellationToken cancellationToken = default)
		{
			var buffer = await inner.OpenCompleteTextBufferAsync(path, maximumBytes, cancellationToken);
			if (buffer.Classification == FileContentClassification.Text)
			{
				Interlocked.Increment(ref _fullContentReadCount);
				Interlocked.Add(ref _fullContentBytesRead, buffer.SizeBytes);
			}
			return buffer;
		}

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.TryReadAsTextAsync(path, cancellationToken);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			inner.TryReadAsTextAsync(path, maxSizeForFullRead, cancellationToken);
	}
}
