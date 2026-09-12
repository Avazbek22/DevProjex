using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpSearchCandidateCollectorTests
{
	[Fact]
	public void UsefulLateMatchDisplacesOneOfFiveThousandEarlyRepetitions()
	{
		var collector = new McpSearchCandidateCollector(5_000, 2_000_000);
		for (var line = 1; line <= 5_000; line++)
		{
			collector.Consider(Candidate(
				"generated/repetitions.txt",
				line,
				McpSearchCandidateCollector.Score(false, line - 1, McpDeclarationMatchQuality.None, false, line - 1, line - 1)));
		}
		collector.Consider(Candidate(
			"src/PublicApi.cs",
			1,
			McpSearchCandidateCollector.Score(false, 0, McpDeclarationMatchQuality.SimpleName, true, 0, 0)));

		var retained = collector.Snapshot();
		Assert.Equal(5_000, retained.Count);
		Assert.Contains(retained, item => item.Group.RelativePath == "src/PublicApi.cs");
		Assert.Equal(4_999, retained.Count(item => item.Group.RelativePath == "generated/repetitions.txt"));
		Assert.True(collector.MatchCapacityReached);
	}

	[Fact]
	public void PermutingCandidateArrivalProducesTheSameBoundedResult()
	{
		var source = Enumerable.Range(1, 40)
			.Select(index => Candidate(
				$"owner-{index % 5}/file-{index:D2}.txt",
				index,
				(index * 37) % 211))
			.ToArray();
		var forward = Collect(source, 11);
		var reverse = Collect(source.Reverse(), 11);
		var interleaved = Collect(source.Where((_, index) => index % 2 == 0)
			.Concat(source.Where((_, index) => index % 2 != 0)), 11);

		Assert.Equal(forward, reverse);
		Assert.Equal(forward, interleaved);
	}

	[Fact]
	public void ExplicitSnapshotCandidateKeepsPriorityWithoutAnOriginPenalty()
	{
		var explicitScore = McpSearchCandidateCollector.Score(
			true, 0, McpDeclarationMatchQuality.None, false, 0, 0);
		var ordinaryDeclarationScore = McpSearchCandidateCollector.Score(
			false, 0, McpDeclarationMatchQuality.FullyQualified, true, 0, 0);

		Assert.True(explicitScore > ordinaryDeclarationScore);
	}

	[Fact]
	public void UnsupportedLanguageGetsAFirstMatchBeforeRepeatedSupportedMatches()
	{
		var unsupportedFirst = McpSearchCandidateCollector.Score(
			false, 0, McpDeclarationMatchQuality.None, false, 0, 0);
		var supportedRepeated = McpSearchCandidateCollector.Score(
			false, 1, McpDeclarationMatchQuality.FullyQualified, true, 1, 0);

		Assert.True(unsupportedFirst > supportedRepeated);
	}

	[Fact]
	public void LateDeclarationCanEnterTheSixtyFourFileAnnotationSet()
	{
		var collector = new McpSearchCandidateCollector(5_000, 2_000_000);
		for (var index = 0; index < 100; index++)
		{
			collector.Consider(Candidate(
				$"generated/file-{index:D3}.txt",
				1,
				McpSearchCandidateCollector.Score(false, 0, McpDeclarationMatchQuality.None, false, 0, 0)));
		}
		collector.Consider(Candidate(
			"zz/PublicContract.cs",
			1,
			McpSearchCandidateCollector.Score(false, 0, McpDeclarationMatchQuality.FullyQualified, true, 0, 0)));

		var selected = collector.SelectFilesForAnnotation(64);
		Assert.Equal(64, selected.Count);
		Assert.Contains("zz/PublicContract.cs", selected);
	}

	[Fact]
	public void RetainedFragmentsStayInsideTheCharacterBound()
	{
		var collector = new McpSearchCandidateCollector(10, 100);
		collector.Consider(Candidate("a.txt", 1, 10, storageCharacters: 70));
		collector.Consider(Candidate("b.txt", 1, 20, storageCharacters: 70));

		Assert.Single(collector.Snapshot());
		Assert.Equal(70, collector.RetainedCharacters);
		Assert.True(collector.CharacterCapacityReached);
		Assert.Equal("b.txt", collector.Snapshot()[0].Group.RelativePath);
	}

	[Fact]
	public void SplittingMergedContextsKeepsEveryMatchingLineMarked()
	{
		var content = string.Join('\n', Enumerable.Range(1, 12)
			.Select(line => line % 3 == 1 ? $"const needle{line} = 1" : $"const filler{line} = 1"));
		var regex = new McpSearchRegex("needle", ignoreCase: false);
		var scan = McpSearchTextScanner.Scan(content, regex, 3, 100, CancellationToken.None);
		var collector = new McpSearchCandidateCollector(100, 100_000);

		DevProjexMcpTools.AddSearchCandidates(
			collector,
			"src/File.ts",
			"src/File.ts",
			content,
			scan.Matches,
			[],
			regex,
			3,
			explicitScope: false);
		var groups = DevProjexMcpTools.BuildOrderedSearchGroups(collector.Snapshot());

		Assert.Equal(4, collector.Count);
		Assert.Equal([1, 4, 7, 10], groups.SelectMany(group => group.Lines)
			.Where(line => line.IsMatch)
			.Select(line => line.LineNumber)
			.Distinct()
			.Order()
			.ToArray());
		Assert.All(
			groups.SelectMany(group => group.Lines).Where(line => line.IsMatch),
			line => Assert.StartsWith($"{line.LineNumber}:", line.Text, StringComparison.Ordinal));
	}

	[Fact]
	public void CompleteBoundaryStatesThatEveryEligibleSourceWasInspected()
	{
		var notice = DevProjexMcpTools.FormatSearchBoundaryNotice(Boundary(), false);

		Assert.Equal("[Search boundary] complete · sources inspected=10/10 · matches retained=3/3 · " +
		             "matches written=3 · declaration files named=2.", notice);
	}

	[Theory]
	[InlineData("inspection-bytes", 0)]
	[InlineData("retained-matches", 1)]
	[InlineData("annotation-files", 2)]
	[InlineData("response-characters", 3)]
	public void PartialBoundaryNamesEachPublishedSearchLimit(string expected, int limit)
	{
		var flags = new bool[7];
		flags[limit] = true;
		var boundary = Boundary() with
		{
			InspectionByteLimitReached = flags[0],
			RetainedMatchLimitReached = flags[1],
			AnnotationFileLimitReached = flags[2],
			ResponseCharacterLimitReached = flags[3]
		};

		var notice = DevProjexMcpTools.FormatSearchBoundaryNotice(boundary, true);

		Assert.Contains("[Search boundary] partial", notice, StringComparison.Ordinal);
		Assert.Contains($"limits={expected}", notice, StringComparison.Ordinal);
		Assert.Contains("continue with read_pack", notice, StringComparison.Ordinal);
	}

	[Fact]
	public void BoundaryKeepsEncounteredRetainedAndWrittenCountsDistinct()
	{
		var boundary = Boundary() with
		{
			EncounteredMatches = 8_000,
			RetainedMatches = 5_000,
			WrittenMatches = 151,
			RetainedMatchLimitReached = true,
			ResponseCharacterLimitReached = true
		};

		var notice = DevProjexMcpTools.FormatSearchBoundaryNotice(boundary, false);

		Assert.Contains("matches retained=5000/8000", notice, StringComparison.Ordinal);
		Assert.Contains("matches written=151", notice, StringComparison.Ordinal);
		Assert.Contains("continue the search", notice, StringComparison.Ordinal);
	}

	private static IReadOnlyList<string> Collect(IEnumerable<McpSearchCandidate> source, int capacity)
	{
		var collector = new McpSearchCandidateCollector(capacity, 2_000_000);
		foreach (var candidate in source)
			collector.Consider(candidate);
		return collector.Snapshot()
			.Select(item => $"{item.Group.RelativePath}:{item.MatchLine}:{item.Score}")
			.ToArray();
	}

	private static McpSearchCandidate Candidate(
		string path,
		int line,
		int score,
		int storageCharacters = 20)
	{
		var text = $"{line}:needle";
		return new McpSearchCandidate(
			new McpSearchRenderedGroup(
				path,
				path,
				[line],
				[new McpSearchGroupLine(line, true, text)]),
			line,
			score,
			text,
			storageCharacters);
	}

	private static McpSearchBoundary Boundary() => new(
		EligibleSources: 10,
		InspectedSources: 10,
		EncounteredMatches: 3,
		RetainedMatches: 3,
		WrittenMatches: 3,
		NamedDeclarationFiles: 2,
		InspectionByteLimitReached: false,
		RetainedMatchLimitReached: false,
		AnnotationFileLimitReached: false,
		ResponseCharacterLimitReached: false,
		RequestResultLimitReached: false,
		RetainedCharacterLimitReached: false,
		StoredCharacterLimitReached: false,
		UnscannableSources: 0);
}
