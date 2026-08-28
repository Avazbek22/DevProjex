using System.Diagnostics;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalWorkspaceCommandCompletionPerformanceTests
{
	private const string EnabledVariable = "DEVPROJEX_RUN_LARGE_PERF_TESTS";

	[Fact]
	public void GhostCompletionAvoidsFullCandidateMaterialization()
	{
		if (!string.Equals(
				Environment.GetEnvironmentVariable(EnabledVariable),
				"1",
				StringComparison.Ordinal))
		{
			Assert.Skip($"Set {EnabledVariable}=1 to run the completion allocation benchmark.");
		}

		var parser = new TerminalWorkspaceCommandParser();
		var context = new TerminalWorkspaceCommandParseContext(
			Enumerable.Range(0, 1_024)
				.Select(static index => $".extension-{index:D4}")
				.ToArray());
		_ = parser.GetCompletion("type ", 5, context);
		_ = parser.GetGhostCompletion("type ", 5, context);

		const int iterations = 100;
		var full = MeasureFullCompletion(parser, context, iterations);
		var ghost = MeasureGhostCompletion(parser, context, iterations);

		TestContext.Current.TestOutputHelper?.WriteLine(
			$"full={full.Elapsed.TotalMilliseconds:F3} ms / {full.AllocatedBytes:N0} B; " +
			$"ghost={ghost.Elapsed.TotalMilliseconds:F3} ms / {ghost.AllocatedBytes:N0} B");
		Assert.True(
			ghost.AllocatedBytes * 10 < full.AllocatedBytes,
			$"Ghost completion allocated {ghost.AllocatedBytes:N0} B versus " +
			$"{full.AllocatedBytes:N0} B for full candidates.");
	}

	private static CompletionMeasurement MeasureFullCompletion(
		TerminalWorkspaceCommandParser parser,
		TerminalWorkspaceCommandParseContext context,
		int iterations)
	{
		TerminalWorkspaceCommandCompletion? last = null;
		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		var stopwatch = Stopwatch.StartNew();
		for (var iteration = 0; iteration < iterations; iteration++)
			last = parser.GetCompletion("type ", 5, context);
		stopwatch.Stop();
		var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
		GC.KeepAlive(last);
		return new CompletionMeasurement(stopwatch.Elapsed, allocatedBytes);
	}

	private static CompletionMeasurement MeasureGhostCompletion(
		TerminalWorkspaceCommandParser parser,
		TerminalWorkspaceCommandParseContext context,
		int iterations)
	{
		var last = TerminalWorkspaceCommandGhostCompletion.Empty;
		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		var stopwatch = Stopwatch.StartNew();
		for (var iteration = 0; iteration < iterations; iteration++)
			last = parser.GetGhostCompletion("type ", 5, context);
		stopwatch.Stop();
		var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
		GC.KeepAlive(last);
		return new CompletionMeasurement(stopwatch.Elapsed, allocatedBytes);
	}

	private readonly record struct CompletionMeasurement(
		TimeSpan Elapsed,
		long AllocatedBytes);
}
