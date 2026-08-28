using System.Diagnostics;
using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class PreviewSelectionHashPerformanceTests(ITestOutputHelper output)
{
	private const string EnabledVariable = "DEVPROJEX_RUN_SELECTION_HASH_BENCHMARK";

	[Fact]
	public void LargeSelectionFingerprintBenchmark()
	{
		if (!string.Equals(
				Environment.GetEnvironmentVariable(EnabledVariable),
				"1",
				StringComparison.Ordinal))
		{
			Assert.Skip($"Set {EnabledVariable}=1 to run the selection fingerprint benchmark.");
		}

		var selectedPaths = Enumerable.Range(0, 100_000)
			.Select(static index => Path.Combine("root", "src", $"folder-{index / 100:D4}", $"file-{index:D6}.cs"))
			.ToHashSet(PathComparer.Default);

		_ = PreviewFileCollectionPolicy.BuildPathSetHash(selectedPaths);
		const int iterations = 20;
		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		var stopwatch = Stopwatch.StartNew();
		var fingerprint = 0;
		for (var iteration = 0; iteration < iterations; iteration++)
			fingerprint ^= PreviewFileCollectionPolicy.BuildPathSetHash(selectedPaths);
		stopwatch.Stop();
		GC.KeepAlive(fingerprint);

		var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
		output.WriteLine(
			"selection paths={0:N0}, iterations={1}, elapsed={2:F2} ms, allocated={3:N0} B",
			selectedPaths.Count,
			iterations,
			stopwatch.Elapsed.TotalMilliseconds,
			allocatedBytes);
		Assert.InRange(allocatedBytes, 0, 1024);
	}
}
