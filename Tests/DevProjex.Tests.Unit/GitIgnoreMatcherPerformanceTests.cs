using System.Diagnostics;

namespace DevProjex.Tests.Unit;

public sealed class GitIgnoreMatcherPerformanceTests(ITestOutputHelper output)
{
	[Fact]
	[Trait("Category", "LocalPerformance")]
	public void LastMatchingRuleBenchmark()
	{
		if (!string.Equals(
			    Environment.GetEnvironmentVariable("DEVPROJEX_RUN_LARGE_PERF_TESTS"),
			    "1",
			    StringComparison.Ordinal))
		{
			Assert.Skip("Set DEVPROJEX_RUN_LARGE_PERF_TESTS=1 for the pre-release performance gate.");
		}

		var rules = Enumerable.Range(0, GitIgnoreMatcher.MaximumEffectiveRuleCount - 1)
			.Select(static index => $"never-{index}.tmp")
			.Append("target.txt")
			.ToArray();
		var matcher = GitIgnoreMatcher.Build("/repo", rules);
		for (var iteration = 0; iteration < 100; iteration++)
			Assert.True(matcher.EvaluateRelative("src/target.txt", false, "target.txt").IsIgnored);

		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		var stopwatch = Stopwatch.StartNew();
		for (var iteration = 0; iteration < 5_000; iteration++)
			Assert.True(matcher.EvaluateRelative("src/target.txt", false, "target.txt").IsIgnored);
		stopwatch.Stop();

		output.WriteLine(
			$"Last matching GitIgnore rule: {stopwatch.Elapsed.TotalMilliseconds:F3} ms / " +
			$"{GC.GetAllocatedBytesForCurrentThread() - allocatedBefore:N0} B for 5,000 evaluations.");
	}
}
