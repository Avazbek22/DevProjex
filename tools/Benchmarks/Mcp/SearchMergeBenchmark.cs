using System.Diagnostics;
using System.Globalization;
using DevProjex.Mcp;

internal static class SearchMergeBenchmark
{
	public static void Run(string[] arguments)
	{
		var repetitions = 7;
		if (arguments.Length > 0)
		{
			if (arguments.Length != 2 || arguments[0] != "--repetitions")
				throw new ArgumentException("Only --repetitions is supported.");
			repetitions = int.Parse(arguments[1], CultureInfo.InvariantCulture);
		}
		if (repetitions < 3)
			throw new ArgumentOutOfRangeException(nameof(repetitions));

		Console.WriteLine("candidates,context_lines,groups,lines,matches,legacy_median_ms,current_median_ms,legacy_median_alloc_bytes,current_median_alloc_bytes");
		foreach (var count in new[] { 1_000, 3_000, 5_000 })
		{
			foreach (var contextLines in new[] { 0, 2, 20 })
			{
				var candidates = CreateCandidates(count, contextLines);
				if (candidates.Sum(candidate => candidate.StorageCharacters) > 2_000_000)
					throw new InvalidOperationException("Fixture exceeds the search retention character limit.");
				var expected = MergeLegacy(candidates);
				AssertEquivalent(expected, DevProjexMcpTools.BuildOrderedSearchGroups(candidates));
				var legacySamples = new Sample[repetitions];
				var currentSamples = new Sample[repetitions];
				for (var repetition = 0; repetition < repetitions; repetition++)
				{
					legacySamples[repetition] = Measure(() => MergeLegacy(candidates), expected);
					currentSamples[repetition] = Measure(
						() => DevProjexMcpTools.BuildOrderedSearchGroups(candidates), expected);
				}
				Console.WriteLine(string.Join(',',
					count,
					contextLines,
					expected.Count,
					expected.Sum(group => group.Lines.Count),
					expected.Sum(group => group.MatchLines.Count),
					Median(legacySamples.Select(sample => sample.Milliseconds)).ToString("0.000", CultureInfo.InvariantCulture),
					Median(currentSamples.Select(sample => sample.Milliseconds)).ToString("0.000", CultureInfo.InvariantCulture),
					Median(legacySamples.Select(sample => sample.AllocatedBytes)),
					Median(currentSamples.Select(sample => sample.AllocatedBytes))));
			}
		}
	}

	private static IReadOnlyList<McpSearchCandidate> CreateCandidates(int count, int contextLines) =>
		Enumerable.Range(1, count).Reverse().Select(matchLine =>
		{
			var start = Math.Max(1, matchLine - contextLines);
			var end = matchLine + contextLines;
			var lines = Enumerable.Range(start, end - start + 1)
				.Select(line => new McpSearchGroupLine(
					line, line == matchLine, $"{line}{(line == matchLine ? ':' : '-')}値"))
				.ToArray();
			return new McpSearchCandidate(
				new McpSearchRenderedGroup("f.cs", "f.cs", [matchLine], lines),
				matchLine,
				0,
				lines.Single(line => line.IsMatch).Text,
				lines.Sum(line => line.Text.Length + Environment.NewLine.Length) + 4 + Environment.NewLine.Length);
		}).ToArray();

	private static Sample Measure(
		Func<IReadOnlyList<McpSearchRenderedGroup>> merge,
		IReadOnlyList<McpSearchRenderedGroup> expected)
	{
		var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
		var timer = Stopwatch.StartNew();
		var groups = merge();
		timer.Stop();
		var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
		AssertEquivalent(expected, groups);
		return new Sample(timer.Elapsed.TotalMilliseconds, allocated);
	}

	private static void AssertEquivalent(
		IReadOnlyList<McpSearchRenderedGroup> expected,
		IReadOnlyList<McpSearchRenderedGroup> actual)
	{
		if (expected.Count != actual.Count)
			throw new InvalidOperationException("Group counts differ.");
		for (var index = 0; index < expected.Count; index++)
		{
			var left = expected[index];
			var right = actual[index];
			if (left.RelativePath != right.RelativePath || left.FullPath != right.FullPath ||
				!left.MatchLines.SequenceEqual(right.MatchLines) || !left.Lines.SequenceEqual(right.Lines))
			{
				throw new InvalidOperationException("Merged search evidence differs.");
			}
		}
	}

	// Retains the original boundary lookup for reproducible before/after measurements.
	private static IReadOnlyList<McpSearchRenderedGroup> MergeLegacy(IReadOnlyList<McpSearchCandidate> candidates)
	{
		var fileOrder = new List<string>();
		var byFile = new Dictionary<string, List<McpSearchCandidate>>(StringComparer.Ordinal);
		foreach (var candidate in candidates)
		{
			if (!byFile.TryGetValue(candidate.Group.RelativePath, out var fileCandidates))
			{
				fileCandidates = [];
				byFile[candidate.Group.RelativePath] = fileCandidates;
				fileOrder.Add(candidate.Group.RelativePath);
			}
			fileCandidates.Add(candidate);
		}

		var result = new List<McpSearchRenderedGroup>(candidates.Count);
		foreach (var path in fileOrder)
		{
			var ordered = byFile[path].OrderBy(item => item.MatchLine).ToArray();
			var currentMatches = new List<int>();
			var currentLines = new SortedDictionary<int, McpSearchGroupLine>();
			foreach (var candidate in ordered)
			{
				var firstLine = candidate.Group.Lines[0].LineNumber;
				var adjacent = currentLines.Count == 0 || firstLine <= currentLines.Keys.Last() + 1;
				if (!adjacent)
				{
					result.Add(new McpSearchRenderedGroup(path, ordered[0].Group.FullPath,
						currentMatches.ToArray(), currentLines.Values.ToArray()));
					currentMatches.Clear();
					currentLines.Clear();
				}

				currentMatches.Add(candidate.MatchLine);
				foreach (var line in candidate.Group.Lines)
				{
					if (currentLines.TryGetValue(line.LineNumber, out var existing))
					{
						var isMatch = existing.IsMatch || line.IsMatch;
						currentLines[line.LineNumber] = existing with
						{
							IsMatch = isMatch,
							Text = isMatch && !existing.IsMatch
								? MarkRenderedLineAsMatch(existing)
								: existing.Text
						};
					}
					else
						currentLines[line.LineNumber] = line;
				}
			}
			if (currentLines.Count > 0)
			{
				result.Add(new McpSearchRenderedGroup(path, ordered[0].Group.FullPath,
					currentMatches.ToArray(), currentLines.Values.ToArray()));
			}
		}
		return result;
	}

	private static string MarkRenderedLineAsMatch(McpSearchGroupLine line)
	{
		var marker = line.LineNumber.ToString(CultureInfo.InvariantCulture).Length;
		return marker < line.Text.Length && line.Text[marker] == '-'
			? string.Concat(line.Text.AsSpan(0, marker), ":", line.Text.AsSpan(marker + 1))
			: line.Text;
	}

	private static T Median<T>(IEnumerable<T> values) => values.Order().ElementAt(values.Count() / 2);
	private readonly record struct Sample(double Milliseconds, long AllocatedBytes);
}
