using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpSearchGroupMergeTests
{
	[Fact]
	public void EmptyCandidatesProduceNoGroups() =>
		Assert.Empty(DevProjexMcpTools.BuildOrderedSearchGroups([]));

	[Theory]
	[InlineData(0)]
	[InlineData(2)]
	[InlineData(20)]
	public void DenseOverlappingContextsKeepEachLineAndMatchExactlyOnce(int contextLines)
	{
		const int count = 256;
		var candidates = Enumerable.Range(1, count)
			.Select(line => Candidate("dense.cs", line, Math.Max(1, line - contextLines), line + contextLines))
			.Reverse()
			.ToArray();

		var group = Assert.Single(DevProjexMcpTools.BuildOrderedSearchGroups(candidates));

		Assert.Equal("dense.cs", group.RelativePath);
		Assert.Equal("dense.cs", group.FullPath);
		Assert.Equal(Enumerable.Range(1, count), group.MatchLines);
		Assert.Equal(Enumerable.Range(1, count + contextLines), group.Lines.Select(line => line.LineNumber));
		Assert.All(group.Lines, line =>
		{
			var isMatch = line.LineNumber <= count;
			Assert.Equal(isMatch, line.IsMatch);
			Assert.Equal(Text(line.LineNumber, isMatch), line.Text);
		});
		Assert.All(candidates, candidate => Assert.Equal(
			[new McpSearchProtectedLine(candidate.MatchLine, 1)], candidate.ProtectedLines));

		var rendered = DevProjexMcpTools.RenderSearchGroups([group], count, 100_000);
		Assert.Equal(count, rendered.ShownMatches);
		Assert.False(rendered.Truncated);
	}

	[Theory]
	[InlineData(5, 1)]
	[InlineData(6, 2)]
	public void AdjacentContextsMergeButOneMissingLineKeepsGroupsSeparate(int secondMatch, int groupCount)
	{
		var groups = DevProjexMcpTools.BuildOrderedSearchGroups(
		[
			Candidate("a.cs", secondMatch, secondMatch - 1, secondMatch + 1),
			Candidate("a.cs", 2, 1, 3)
		]);

		Assert.Equal(groupCount, groups.Count);
		Assert.Equal([2, secondMatch], groups.SelectMany(group => group.MatchLines));
		Assert.Equal([1, 2, 3, secondMatch - 1, secondMatch, secondMatch + 1],
			groups.SelectMany(group => group.Lines).Select(line => line.LineNumber));
	}

	[Fact]
	public void NarrowerOverlappingContextDoesNotMoveTheAccumulatedBoundaryBackwards()
	{
		var group = Assert.Single(DevProjexMcpTools.BuildOrderedSearchGroups(
		[
			Candidate("a.cs", 2, 1, 30),
			Candidate("a.cs", 10, 9, 11),
			Candidate("a.cs", 25, 24, 26)
		]));

		Assert.Equal([2, 10, 25], group.MatchLines);
		Assert.Equal(Enumerable.Range(1, 30), group.Lines.Select(line => line.LineNumber));
		Assert.Equal([2, 10, 25], group.Lines.Where(line => line.IsMatch).Select(line => line.LineNumber));
	}

	[Fact]
	public void FilePriorityOrderAndGroupBoundariesRemainIndependent()
	{
		var groups = DevProjexMcpTools.BuildOrderedSearchGroups(
		[
			Candidate("z.cs", 20, 19, 21),
			Candidate("a.cs", 2, 1, 3),
			Candidate("z.cs", 2, 1, 3),
			Candidate("a.cs", 8, 7, 9)
		]);

		Assert.Equal(["z.cs", "z.cs", "a.cs", "a.cs"], groups.Select(group => group.RelativePath));
		Assert.Equal([2, 20, 2, 8], groups.SelectMany(group => group.MatchLines));
		Assert.Equal([1, 19, 1, 7], groups.Select(group => group.Lines[0].LineNumber));
		Assert.Equal([3, 21, 3, 9], groups.Select(group => group.Lines[^1].LineNumber));
	}

	private static McpSearchCandidate Candidate(string path, int matchLine, int startLine, int endLine)
	{
		var lines = Enumerable.Range(startLine, endLine - startLine + 1)
			.Select(line => new McpSearchGroupLine(line, line == matchLine, Text(line, line == matchLine)))
			.ToArray();
		return new McpSearchCandidate(
			new McpSearchRenderedGroup(path, path, [matchLine], lines),
			matchLine,
			0,
			Text(matchLine, true),
			lines.Sum(line => line.Text.Length + Environment.NewLine.Length),
			ProtectedLines: [new McpSearchProtectedLine(matchLine, 1)]);
	}

	private static string Text(int line, bool isMatch) => $"{line}{(isMatch ? ':' : '-')}値😀";
}
