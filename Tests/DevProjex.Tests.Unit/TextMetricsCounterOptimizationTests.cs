using System.Diagnostics;

namespace DevProjex.Tests.Unit;

public sealed class TextMetricsCounterOptimizationTests(ITestOutputHelper output)
{
	private const long SyntheticSizeBytes = 123_456;
	private const string UnicodeWhitespace =
		"\t\n\v\f\r \u0085\u00A0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006" +
		"\u2007\u2008\u2009\u200A\u2028\u2029\u202F\u205F\u3000";

	[Fact]
	public void Append_MatchesScalarOracleAcrossStatefulAndUnicodeChunks()
	{
		var contractCases = new[]
		{
			string.Empty,
			"\r\nalpha\rbravo\n",
			"````alpha```beta`",
			UnicodeWhitespace,
			"世界😀e\u0301\r\n終",
			new string('a', 257) + "\r\n" + new string('b', 257) + "````\n",
			new string('x', 96) + "\0ignored"
		};

		foreach (var text in contractCases)
		{
			for (var split = 0; split <= text.Length; split++)
				AssertEquivalent(text, split, text.Length - split);
		}

		var random = new Random(0x5EED);
		var randomText = CreateRandomText(random, 8 * 1024);
		for (var iteration = 0; iteration < 32; iteration++)
			AssertEquivalent(randomText, CreateRandomChunkLengths(randomText.Length, random));
	}

	[Fact]
	[Trait("Category", "LocalPerformance")]
	public void TextMetricsCounterBenchmark()
	{
		if (!string.Equals(
		    Environment.GetEnvironmentVariable("DEVPROJEX_RUN_LARGE_PERF_TESTS"),
		    "1",
		    StringComparison.Ordinal))
		{
			Assert.Skip("Set DEVPROJEX_RUN_LARGE_PERF_TESTS=1 for the pre-release performance gate.");
		}

		foreach (var (name, text) in CreateBenchmarkCases(8 * 1024 * 1024))
		{
			Assert.Equal(CountScalar(text), CountOptimized(text));
			Func<TextFileMetrics> scalar = () => CountScalar(text);
			Func<TextFileMetrics> optimized = () => CountOptimized(text);
			var scalarRuns = new List<BenchmarkResult>(9);
			var optimizedRuns = new List<BenchmarkResult>(9);
			for (var iteration = 0; iteration < 9; iteration++)
			{
				if ((iteration & 1) == 0)
				{
					scalarRuns.Add(Measure(scalar));
					optimizedRuns.Add(Measure(optimized));
				}
				else
				{
					optimizedRuns.Add(Measure(optimized));
					scalarRuns.Add(Measure(scalar));
				}
			}

			var scalarMedian = Median(scalarRuns);
			var optimizedMedian = Median(optimizedRuns);
			Assert.Equal(scalarMedian.Metrics, optimizedMedian.Metrics);
			Assert.True(optimizedMedian.AllocatedBytes <= scalarMedian.AllocatedBytes);
			output.WriteLine(
				$"{name} ({text.Length:N0} chars): scalar {scalarMedian.Elapsed.TotalMilliseconds:F3} ms / " +
				$"{scalarMedian.AllocatedBytes:N0} B; SIMD {optimizedMedian.Elapsed.TotalMilliseconds:F3} ms / " +
				$"{optimizedMedian.AllocatedBytes:N0} B; " +
				$"speedup {scalarMedian.Elapsed.TotalMilliseconds / optimizedMedian.Elapsed.TotalMilliseconds:F2}x.");
		}
	}

	private static void AssertEquivalent(string text, params int[] chunkLengths)
	{
		var expected = new ScalarMetricsCounter();
		var actual = new FileContentAnalyzer.TextMetricsCounter();
		var offset = 0;
		foreach (var chunkLength in chunkLengths)
		{
			Assert.InRange(chunkLength, 0, text.Length - offset);
			var chunk = text.AsSpan(offset, chunkLength);
			var expectedAccepted = expected.Append(chunk);
			var actualAccepted = actual.Append(chunk);
			offset += chunkLength;

			Assert.Equal(expectedAccepted, actualAccepted);
			Assert.Equal(expected.Build(SyntheticSizeBytes), actual.Build(SyntheticSizeBytes));
			if (!expectedAccepted)
				return;
		}

		Assert.Equal(text.Length, offset);
	}

	private static string CreateRandomText(Random random, int targetLength)
	{
		var fragments = new[]
		{
			"ordinaryIdentifier", " = 12345; ", "\r", "\n", "\r\n", "`", "```", "\t", "\u2003",
			"世界", "😀", "e\u0301"
		};
		var builder = new StringBuilder(targetLength + 32);
		while (builder.Length < targetLength)
			builder.Append(fragments[random.Next(fragments.Length)]);
		return builder.ToString();
	}

	private static int[] CreateRandomChunkLengths(int textLength, Random random)
	{
		var lengths = new List<int> { 0 };
		var offset = 0;
		while (offset < textLength)
		{
			var length = Math.Min(random.Next(1, 97), textLength - offset);
			lengths.Add(length);
			offset += length;
			if (random.Next(4) == 0)
				lengths.Add(0);
		}
		return lengths.ToArray();
	}

	private static string BuildBenchmarkText(int targetLength)
	{
		const string line =
			"public static string FormatValue(int value) => $\"value={value:D8}\"; // benchmark payload\r\n";
		var builder = new StringBuilder(targetLength + line.Length);
		while (builder.Length < targetLength)
			builder.Append(line);
		return builder.ToString();
	}

	private static IEnumerable<(string Name, string Text)> CreateBenchmarkCases(int targetLength)
	{
		yield return ("code", BuildBenchmarkText(targetLength));
		yield return ("newline-dense", RepeatToLength("a\n", targetLength));
		yield return ("backtick-dense", RepeatToLength("`a` ", targetLength));
		yield return ("cjk", RepeatToLength("世界の設定値を検証します。", targetLength));
	}

	private static string RepeatToLength(string fragment, int targetLength)
	{
		var builder = new StringBuilder(targetLength + fragment.Length);
		while (builder.Length < targetLength)
			builder.Append(fragment);
		return builder.ToString();
	}

	private static TextFileMetrics CountOptimized(string text)
	{
		var counter = new FileContentAnalyzer.TextMetricsCounter();
		if (!counter.Append(text))
			throw new InvalidOperationException("The benchmark corpus unexpectedly contains a null character.");
		return counter.Build(text.Length);
	}

	private static TextFileMetrics CountScalar(string text)
	{
		var counter = new ScalarMetricsCounter();
		if (!counter.Append(text))
			throw new InvalidOperationException("The benchmark corpus unexpectedly contains a null character.");
		return counter.Build(text.Length);
	}

	private static BenchmarkResult Measure(Func<TextFileMetrics> action)
	{
		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		var started = Stopwatch.GetTimestamp();
		var metrics = action();
		var elapsed = Stopwatch.GetElapsedTime(started);
		return new BenchmarkResult(
			elapsed,
			GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
			metrics);
	}

	private static BenchmarkResult Median(List<BenchmarkResult> runs)
	{
		runs.Sort(static (left, right) => left.Elapsed.CompareTo(right.Elapsed));
		return runs[runs.Count / 2];
	}

	// This intentionally remains scalar so tests compare the optimized implementation with an independent oracle.
	private struct ScalarMetricsCounter()
	{
		private int _lineCount = 1;
		private int _charCount;
		private bool _hasNonWhitespace;
		private int _crLfPairCount;
		private int _trailingNewlineChars;
		private int _trailingNewlineLineBreaks;
		private bool _previousWasCarriageReturn;
		private int _currentBacktickRun;
		private int _longestBacktickRun;

		public bool Append(ReadOnlySpan<char> span)
		{
			foreach (var character in span)
			{
				if (character == '\0')
					return false;

				_charCount++;
				if (character == '\r')
				{
					_lineCount++;
					_trailingNewlineChars++;
					_trailingNewlineLineBreaks++;
					_previousWasCarriageReturn = true;
				}
				else if (character == '\n')
				{
					_trailingNewlineChars++;
					if (_previousWasCarriageReturn)
						_crLfPairCount++;
					else
					{
						_lineCount++;
						_trailingNewlineLineBreaks++;
					}
					_previousWasCarriageReturn = false;
				}
				else
				{
					_previousWasCarriageReturn = false;
					_trailingNewlineChars = 0;
					_trailingNewlineLineBreaks = 0;
				}

				if (!_hasNonWhitespace && !char.IsWhiteSpace(character))
					_hasNonWhitespace = true;
				if (character == '`')
					_longestBacktickRun = Math.Max(_longestBacktickRun, ++_currentBacktickRun);
				else
					_currentBacktickRun = 0;
			}

			return true;
		}

		public TextFileMetrics Build(long sizeBytes) =>
			new(
				SizeBytes: sizeBytes,
				LineCount: _charCount == 0 ? 0 : _lineCount,
				CharCount: _charCount,
				IsEmpty: _charCount == 0,
				IsWhitespaceOnly: _charCount > 0 && !_hasNonWhitespace,
				IsEstimated: false,
				CrLfPairCount: _crLfPairCount,
				TrailingNewlineChars: _trailingNewlineChars,
				TrailingNewlineLineBreaks: _trailingNewlineLineBreaks,
				LongestBacktickRun: _longestBacktickRun);
	}

	private readonly record struct BenchmarkResult(
		TimeSpan Elapsed,
		long AllocatedBytes,
		TextFileMetrics Metrics);
}
