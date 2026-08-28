using System.Diagnostics;
using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class SecretRedactionCountOnlyPerformanceTests
{
	[Fact]
	public void CachedFindingCountBenchmark()
	{
		if (!string.Equals(
			    Environment.GetEnvironmentVariable("DEVPROJEX_RUN_LARGE_PERF_TESTS"),
			    "1",
			    StringComparison.Ordinal))
		{
			Assert.Skip("Set DEVPROJEX_RUN_LARGE_PERF_TESTS=1 for the pre-release performance gate.");
		}

		const int fileCount = 3_000;
		const int findingsPerFile = 20;
		using var workspace = new TemporaryDirectory();
		var paths = Enumerable.Range(0, fileCount)
			.Select(index => Path.Combine(workspace.Path, $"file-{index:D4}.txt"))
			.ToArray();
		var candidates = Enumerable.Range(0, findingsPerFile)
			.Select(static index => new SecretFindingCandidateMetadata(
				index * 10,
				8,
				$"rule-{index:D2}",
				$"fingerprint-{index:D2}",
				index,
				SecretFindingSource.Detector,
				PersistentMarkHash: null,
				SessionMarkId: null,
				PersistentMarkId: null,
				RedactionFindingCategory.Secrets))
			.ToArray();
		var segments = Enumerable.Range(0, findingsPerFile)
			.Select(static index => new SecretFindingSegmentMetadata(index * 10, 8, [index]))
			.ToArray();
		var entry = new SecretScanCacheEntry(
			paths[0],
			new SecretFileMetadata(200, 0),
			"content",
			"rules",
			string.Empty,
			0,
			IsBinary: false,
			candidates,
			segments,
			ApproximateRetainedBytes: 1);
		using var session = new SecretRedactionSession(new EmptyDetector());
		var scope = session.BeginOutput(workspace.Path, paths);
		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		var stopwatch = Stopwatch.StartNew();
		foreach (var path in paths)
			scope.ProcessEntry(path, entry);
		var snapshot = scope.Complete();
		stopwatch.Stop();
		var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

		Assert.Equal(fileCount * findingsPerFile, snapshot.DetectedCount);
		Assert.Equal(fileCount * findingsPerFile, snapshot.RedactedCount);
		Assert.True(
			allocatedBytes < 8_000_000,
			$"Count-only aggregation allocated {allocatedBytes:N0} bytes.");
		TestContext.Current.TestOutputHelper?.WriteLine(
			$"Counted {snapshot.RedactedCount:N0} cached findings in " +
			$"{stopwatch.Elapsed.TotalMilliseconds:F3} ms / {allocatedBytes:N0} B.");
	}

	private sealed class EmptyDetector : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) => [];
	}
}
