using System.Diagnostics;
using DevProjex.Application.Compression;

namespace DevProjex.Tests.Unit;

public sealed class ContentSelectionSnapshotPerformanceTests(ITestOutputHelper output)
{
	private const string EnabledVariable = "DEVPROJEX_RUN_CONTENT_SELECTION_BENCHMARK";

	[Fact]
	public void CanonicalLargeSelectionBenchmark()
	{
		if (!string.Equals(
			    Environment.GetEnvironmentVariable(EnabledVariable),
			    "1",
			    StringComparison.Ordinal))
		{
			Assert.Skip($"Set {EnabledVariable}=1 to run the content-selection benchmark.");
		}

		var paths = Enumerable.Range(0, 100_000)
			.Select(static index => $"root/src/folder-{index / 100:D4}/file-{index:D6}.cs")
			.ToArray();
		_ = ContentSelectionSnapshot.Create("root", paths);

		const int iterations = 5;
		var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
		var stopwatch = Stopwatch.StartNew();
		for (var iteration = 0; iteration < iterations; iteration++)
			_ = ContentSelectionSnapshot.Create("root", paths);
		stopwatch.Stop();
		var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

		output.WriteLine(
			"paths={0:N0}, iterations={1}, elapsed={2:F2} ms, allocated={3:N0} B",
			paths.Length,
			iterations,
			stopwatch.Elapsed.TotalMilliseconds,
			allocatedBytes);
	}
}
