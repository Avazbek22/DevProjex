using System.Diagnostics;

namespace DevProjex.Tests.Unit;

public sealed class TrailingLineEndingTrimmingTests(ITestOutputHelper output)
{
	[Theory]
	[InlineData("", 0)]
	[InlineData("text", 4)]
	[InlineData("text\n", 4)]
	[InlineData("text\r\n\r", 4)]
	[InlineData("\r\n", 0)]
	[InlineData("文書\n", 2)]
	public void GetTrimmedLength_ReturnsPrefixWithoutTrailingLineEndings(
		string value,
		int expectedLength)
	{
		Assert.Equal(expectedLength, TrailingLineEndingTrimming.GetTrimmedLength(value));
	}

	[Theory]
	[InlineData("text\r\n", "text")]
	[InlineData("text \n", "text ")]
	[InlineData("\n\r", "")]
	[InlineData("文書", "文書")]
	public void Trim_MutatesBuilderWithoutChangingNonLineEndingCharacters(
		string value,
		string expected)
	{
		var builder = new StringBuilder(value);

		TrailingLineEndingTrimming.Trim(builder);

		Assert.Equal(expected, builder.ToString());
	}

	[Fact]
	[Trait("Category", "LocalPerformance")]
	public void LargeTextBenchmark()
	{
		if (!string.Equals(
			    Environment.GetEnvironmentVariable("DEVPROJEX_RUN_LARGE_PERF_TESTS"),
			    "1",
			    StringComparison.Ordinal))
		{
			Assert.Skip("Set DEVPROJEX_RUN_LARGE_PERF_TESTS=1 for the pre-release performance gate.");
		}

		const int contentLength = 20_000_000;
		var text = string.Concat(new string('x', contentLength), "\r\n");
		_ = text.TrimEnd('\r', '\n');
		_ = TrailingLineEndingTrimming.GetTrimmedLength(text);

		var baseline = Measure(() => text.TrimEnd('\r', '\n').Length);
		var optimized = Measure(() => TrailingLineEndingTrimming.GetTrimmedLength(text));

		Assert.Equal(contentLength, baseline.Result);
		Assert.Equal(contentLength, optimized.Result);
		output.WriteLine(
			$"Large trailing-line trim: baseline {baseline.Elapsed.TotalMilliseconds:F3} ms / " +
			$"{baseline.AllocatedBytes:N0} B; optimized {optimized.Elapsed.TotalMilliseconds:F3} ms / " +
			$"{optimized.AllocatedBytes:N0} B.");
	}

	private static BenchmarkResult Measure(Func<int> action)
	{
		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		var stopwatch = Stopwatch.StartNew();
		var result = action();
		stopwatch.Stop();
		return new BenchmarkResult(
			stopwatch.Elapsed,
			GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
			result);
	}

	private readonly record struct BenchmarkResult(
		TimeSpan Elapsed,
		long AllocatedBytes,
		int Result);
}
