using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpSearchLineEndingTests
{
	[Theory]
	[InlineData("a\nb\n")]
	[InlineData("a\r\nb\r\n")]
	[InlineData("a\rb\r")]
	[InlineData("a\nb")]
	public void ContextOmitsOnlyTheSyntheticEndLine(string text)
	{
		var result = McpSearchTextScanner.Scan(text, new McpSearchRegex("b", false), 2, 10, CancellationToken.None);
		Assert.Single(result.Matches);
		Assert.Equal(new[] { 1, 2 }, result.Matches[0].Lines.Select(line => line.LineNumber));
	}

	[Fact]
	public void EmptyTextHasNoMatches()
	{
		Assert.Equal(0, McpSearchTextScanner.Scan("", new McpSearchRegex("^$", false), 2, 10, CancellationToken.None).TotalMatches);
	}

	[Theory]
	[InlineData("\n")]
	[InlineData("\r\n")]
	[InlineData("\r")]
	public void EndOfFileStillMatchesAndMultilineContextStopsAtTheLastRealLine(string ending)
	{
		var text = "a" + ending + "b" + ending;
		var eof = McpSearchTextScanner.Scan(text, new McpSearchRegex("^$", false), 2, 10, CancellationToken.None);
		Assert.Equal(3, Assert.Single(eof.Matches).MatchLineNumber);
		var matches = new List<McpSearchMatchContext>();
		McpSearchTextScanner.ScanEachMultiline(text, new McpSearchRegex("a[\\s\\S]*b", false, multiline: true), 2, [], matches.Add, CancellationToken.None);
		Assert.Equal(new[] { 1, 2 }, Assert.Single(matches).Lines.Select(line => line.LineNumber));
	}

	[Fact]
	public void OutlineHistoryEvictsTheLeastRecentlyUsedPath()
	{
		var cache = new McpOutlineReadCache<int>(2);
		int Touch(string path) => cache.AddOrUpdate(path, (_, _) => 1, (_, value, _) => value + 1, 0);
		Assert.Equal(1, Touch("a"));
		Touch("b");
		Touch("a");
		Touch("c");
		Assert.Equal(2, cache.Count);
		Assert.Equal(1, Touch("b"));
	}

	[Fact]
	public void OutlineHistoryMatchesABoundedRecencyModelAcrossUpdatesAndEvictions()
	{
		const int seed = 92351;
		var random = new Random(seed);
		var cache = new McpOutlineReadCache<int>(8);
		var model = new List<(string Path, int Reads)>();
		for (var index = 0; index < 1000; index++)
		{
			var path = "folder/名前" + random.Next(24) + ".ts";
			var previous = model.FindIndex(item => item.Path == path);
			var expected = previous < 0 ? 1 : model[previous].Reads + 1;
			if (previous >= 0)
				model.RemoveAt(previous);
			model.Insert(0, (path, expected));
			if (model.Count > 8)
				model.RemoveAt(8);
			Assert.True(expected == cache.AddOrUpdate(path, (_, _) => 1, (_, value, _) => value + 1, 0), $"seed={seed}; operation={index}");
			Assert.Equal(model.Count, cache.Count);
		}
	}

	[Fact]
	public async Task ConcurrentOutlineReadsCannotLoseUpdatesOfTheSameVersion()
	{
		var cache = new McpOutlineReadCache<int>(8);
		var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var workers = Enumerable.Range(0, 24).Select(_ => Task.Run(async () =>
		{
			await start.Task.ConfigureAwait(false);
			for (var index = 0; index < 50; index++)
				cache.AddOrUpdate("same.cs", (_, increment) => increment, (_, value, increment) => value + increment, 1);
		}, TestContext.Current.CancellationToken)).ToArray();
		start.SetResult();
		await Task.WhenAll(workers);
		Assert.Equal(1201, cache.AddOrUpdate("same.cs", (_, _) => 1, (_, value, _) => value + 1, 0));
		Assert.Equal(1, cache.Count);
	}

	[Fact]
	public void MarkdownOutlineIsIndependentOfLineEndings()
	{
		var text = string.Join("\n", Enumerable.Range(0, 400).Select(index => "# Section " + index + new string('x', 70))) + "\n";
		var lf = McpLargeFileOutlineBuilder.Build("a.md", text, () => [], CancellationToken.None);
		Assert.NotNull(lf);
		foreach (var ending in new[] { "\r\n", "\r" })
			Assert.Equal(lf, McpLargeFileOutlineBuilder.Build("a.md", text.Replace("\n", ending), () => [], CancellationToken.None));
	}
}
