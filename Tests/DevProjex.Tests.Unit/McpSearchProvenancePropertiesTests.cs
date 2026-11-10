using DevProjex.Application.Secrets;
using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpSearchProvenancePropertiesTests
{
	[Theory]
	[InlineData("\n")]
	[InlineData("\r\n")]
	[InlineData("\r")]
	public void GeneratedLiteralSearchMatchesAnIndependentProtectedSpanOracle(string ending)
	{
		const int seed = 76031;
		var random = new Random(seed);
		var regex = new McpSearchRegex("needle", false);
		for (var sample = 0; sample < 120; sample++)
		{
			var lines = Enumerable.Range(0, random.Next(1, 45)).Select(_ =>
				string.Join(" Ω🙂 ", Enumerable.Range(0, random.Next(1, 5)).Select(_ => random.Next(2) == 0 ? "needle" : "ordinary"))).ToArray();
			var content = string.Join(ending, lines) + (random.Next(2) == 0 ? ending : "");
			var ranges = new List<TransformedTextRange>();
			for (var offset = 0; offset < content.Length; offset++)
			{
				if (random.Next(17) == 0)
				{
					var length = Math.Min(random.Next(1, 10), content.Length - offset);
					ranges.Add(new(offset, length));
					offset += length - 1;
				}
			}
			var expected = new List<int>();
			var lineOffset = 0;
			for (var index = 0; index < lines.Length; index++)
			{
				var position = 0;
				while ((position = lines[index].IndexOf("needle", position, StringComparison.Ordinal)) >= 0)
				{
					var start = lineOffset + position;
					if (!ranges.Any(range => start < range.End && start + 6 > range.Start))
					{
						expected.Add(index + 1);
						break;
					}
					position += 6;
				}
				lineOffset += lines[index].Length + ending.Length;
			}
			foreach (var limit in new[] { 0, 3, 100 })
			{
				var context = random.Next(4);
				var actual = McpSearchTextScanner.Scan(content, regex, context, limit, ranges, CancellationToken.None);
				Assert.True(actual.TotalMatches == expected.Count, $"seed={seed}; sample={sample}; limit={limit}");
				Assert.Equal(expected.Take(limit), actual.Matches.SelectMany(match => match.MatchLineNumbers));
				var expectedContext = expected.Take(limit).SelectMany(line => Enumerable.Range(Math.Max(1, line - context),
					Math.Min(lines.Length, line + context) - Math.Max(1, line - context) + 1)).Distinct().Order();
				Assert.Equal(expectedContext, actual.Matches.SelectMany(match => match.Lines).Select(line => line.LineNumber).Distinct().Order());
				Assert.All(actual.Matches.SelectMany(match => match.Lines), line =>
					Assert.Equal(lines[line.LineNumber - 1], content.Substring(line.Offset, line.Length)));
			}
		}
	}

	[Theory]
	[InlineData(4093)]
	[InlineData(65533)]
	public void LongLinesCountUnretainedMatchesWithoutSplittingUnicodeOrProtectedSpans(int padding)
	{
		var content = new string('x', padding) + "🙂needle\r\nneedle\r\nneedle";
		var protectedStart = content.LastIndexOf("needle", StringComparison.Ordinal);
		var result = McpSearchTextScanner.Scan(content, new McpSearchRegex("needle", false), 0, 1,
			[new(protectedStart, 6)], TestContext.Current.CancellationToken);
		Assert.Equal(2, result.TotalMatches);
		var match = Assert.Single(result.Matches);
		Assert.Equal(1, match.MatchLineNumber);
		Assert.Equal(new string('x', padding) + "🙂needle", content.Substring(match.Lines[0].Offset, match.Lines[0].Length));
	}

	[Theory]
	[InlineData("\n")]
	[InlineData("\r\n")]
	[InlineData("\r")]
	public void MultilineMatchingMapsProtectedSpansBackToOriginalOffsets(string ending)
	{
		var content = "🙂 before" + ending + "needle" + ending + "tail" + ending + "needle" + ending + "tail" + ending;
		var start = content.IndexOf("needle", StringComparison.Ordinal);
		var matches = new List<McpSearchMatchContext>();
		var pattern = ending == "\r" ? "needle\\rtail" : "needle\\ntail";
		var actual = McpSearchTextScanner.ScanEachMultiline(content, new McpSearchRegex(pattern, false, multiline: true), 1,
			[new(start + "needle".Length + ending.Length, 4)], matches.Add, CancellationToken.None,
			preserveCarriageReturns: ending == "\r");
		Assert.Equal(1, actual.TotalMatches);
		var match = Assert.Single(matches);
		Assert.Equal(4, match.MatchLineNumber);
		Assert.Equal(5, match.SpanEndLineNumber);
		Assert.Equal(new[] { 3, 4, 5 }, match.Lines.Select(line => line.LineNumber));
	}

	[Fact]
	public void SourceTextResemblingAPlaceholderIsNotAutomaticallyProtected()
	{
		const string content = "DEVPROJEX_REDACTED[needle]\nneedle\n";
		var actual = McpSearchTextScanner.Scan(content, new McpSearchRegex("needle", false), 0, 10,
			[new(content.LastIndexOf("needle", StringComparison.Ordinal), 6)], CancellationToken.None);
		Assert.Equal(1, actual.TotalMatches);
		Assert.Equal(1, Assert.Single(actual.Matches).MatchLineNumber);
	}

	[Fact]
	public void CancellationFromTheConsumerStopsScanningWithoutCompletingTheFile()
	{
		using var cancellation = new CancellationTokenSource();
		var seen = 0;
		Assert.Throws<OperationCanceledException>(() => McpSearchTextScanner.ScanEach(
			string.Concat(Enumerable.Repeat("needle" + new string('x', 4100) + "\n", 20)),
			new McpSearchRegex("needle", false), 0, [], _ =>
			{
				seen++;
				cancellation.Cancel();
			}, cancellation.Token));
		Assert.Equal(1, seen);
	}
}
