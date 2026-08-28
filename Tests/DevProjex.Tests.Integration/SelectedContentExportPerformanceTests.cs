using System.Diagnostics;

namespace DevProjex.Tests.Integration;

public sealed class SelectedContentExportPerformanceTests(ITestOutputHelper output)
{
	private const string EnabledVariable = "DEVPROJEX_RUN_SELECTED_CONTENT_BENCHMARK";

	[Fact]
	public async Task MaterializedLargeContentBenchmark()
	{
		if (!string.Equals(
			    Environment.GetEnvironmentVariable(EnabledVariable),
			    "1",
			    StringComparison.Ordinal))
		{
			Assert.Skip($"Set {EnabledVariable}=1 to run the selected-content benchmark.");
		}

		using var project = new TemporaryDirectory();
		var path = project.CreateFile("large.txt", new string('x', 5_000_000));
		var service = new SelectedContentExportService(new FileContentAnalyzer());
		var warmup = await service.BuildAsync(
			[path],
			TestContext.Current.CancellationToken,
			Path.GetFileName);
		Assert.EndsWith(new string('x', 128), warmup, StringComparison.Ordinal);

		var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
		var stopwatch = Stopwatch.StartNew();
		var result = await service.BuildAsync(
			[path],
			TestContext.Current.CancellationToken,
			Path.GetFileName);
		stopwatch.Stop();
		var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

		Assert.Equal(warmup, result);
		output.WriteLine(
			"characters={0:N0}, elapsed={1:F2} ms, allocated={2:N0} B",
			result.Length,
			stopwatch.Elapsed.TotalMilliseconds,
			allocatedBytes);
	}
}
