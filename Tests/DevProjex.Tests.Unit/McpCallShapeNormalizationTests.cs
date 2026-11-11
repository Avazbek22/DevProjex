using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpCallShapeNormalizationTests
{
	[Theory]
	[InlineData("path")]
	[InlineData("paths")]
	public void OneListedPathUsesTheScalarCoordinatesAndSymbol(string name)
	{
		var ranged = Parse(new() { [name] = new[] { "Source.cs" }, ["start_line"] = 2, ["end_line"] = 4, ["start_column"] = 3 });
		Assert.False(ranged.IsBatch);
		Assert.Equal((2, 4), (ranged.Requests[0].Ranges[0].StartLine, ranged.Requests[0].Ranges[0].EndLine));
		var symbol = Parse(new() { [name] = new[] { "Source.cs" }, ["symbol"] = "Demo.Run" });
		Assert.False(symbol.IsBatch);
		Assert.Equal("Source.cs", symbol.Requests[0].Path);
	}

	[Theory]
	[InlineData("path", true, false)]
	[InlineData("paths", false, true)]
	[InlineData("paths", true, true)]
	public void ListedFilesApplyTheSameRangeWithIndependentDefaults(string name, bool start, bool end)
	{
		var fields = new Dictionary<string, object?> { [name] = new[] { "a.cs", "b.cs" } };
		if (start) fields["start_line"] = 2;
		if (end) fields["end_line"] = 4;
		var parsed = Parse(fields);
		Assert.True(parsed.IsBatch);
		Assert.All(parsed.Requests, item => Assert.Equal((start ? 2 : 1, end ? 4 : int.MaxValue),
			(item.Ranges[0].StartLine, item.Ranges[0].EndLine)));
		Assert.Equal("[Read as] start_line and end_line were applied to each of the 2 files.", parsed.ReadAsNotice);
		Assert.All(parsed.Requests, item => Assert.Equal(end, item.Ranges[0].EndLineWasSpecified));
	}

	[Fact]
	public void AllPathFormsMergeAfterRequestsWithoutReplacingNarrowedReads()
	{
		var parsed = Parse(new()
		{
			["requests"] = new[] { new { path = "a.cs", ranges = new[] { new { start_line = 2, end_line = 3 } } } },
			["path"] = new[] { "./a.cs", "b.cs" }, ["paths"] = new[] { "b.cs", "c.cs" }
		});
		Assert.Equal(["a.cs", "b.cs", "c.cs"], parsed.Requests.Select(static item => item.Path));
		Assert.False(parsed.Requests[0].Ranges[0].IsWholeFile);
		Assert.All(parsed.Requests.Skip(1), item => Assert.True(item.Ranges[0].IsWholeFile));
		Assert.Equal("[Read as] path and requests were combined; 2 file(s) from path are read whole.", parsed.ReadAsNotice);
	}

	[Theory]
	[InlineData("path")]
	[InlineData("paths")]
	[InlineData("requests")]
	public void SixteenFilesAreAcceptedAndSeventeenAreRejected(string form)
	{
		object Values(int count) => form == "requests"
			? Enumerable.Range(0, count).Select(index => new { path = $"file{index}.cs" }).ToArray()
			: Enumerable.Range(0, count).Select(index => $"file{index}.cs").ToArray();
		Assert.Equal(16, Parse(new() { [form] = Values(16) }).Requests.Count);
		Assert.Equal(McpErrorCodes.InvalidArguments, Assert.Throws<McpToolException>(() => Parse(new() { [form] = Values(17) })).Code);
	}

	[Fact]
	public void CombinedPathsResolvePhysicalIdentityBeforeApplyingTheUnionLimit()
	{
		var fields = new Dictionary<string, object?>
		{
			["requests"] = Enumerable.Range(0, 16).Select(index => new { path = $"file{index}.cs", symbol = "Demo.Run" }).ToArray(),
			["paths"] = new[] { "/project/file0.cs" }
		};
		var arguments = new McpJsonArguments(fields.ToDictionary(static pair => pair.Key, static pair => JsonSerializer.SerializeToElement(pair.Value)),
			fields.Keys.ToHashSet(StringComparer.Ordinal));
		var pending = McpGetFileRequestSet.Parse(arguments, deferCombinedPathValidation: true);
		Assert.Equal(17, pending.Requests.Count);
		var resolved = pending.ResolveCombinedPaths(path => path.StartsWith("/project/", StringComparison.Ordinal) ? path : "/project/" + path);
		Assert.Equal(16, resolved.Requests.Count);
		Assert.Null(resolved.ReadAsNotice);
		Assert.All(resolved.Requests, item => Assert.Equal("Demo.Run", item.Symbol));
		Assert.Throws<McpToolException>(() => pending.ResolveCombinedPaths(static path => path));
	}

	[Theory]
	[InlineData("start_column", 2)]
	[InlineData("symbol", "Run")]
	public void ScalarOnlySelectorsStillRejectSeveralFiles(string field, object value)
	{
		Assert.False(Parse(new() { ["paths"] = new[] { "a.cs" }, [field] = value }).IsBatch);
		Assert.Throws<McpToolException>(() => Parse(new() { ["paths"] = new[] { "a.cs", "b.cs" }, [field] = value }));
	}

	[Theory]
	[InlineData("start_line", 2)]
	[InlineData("end_line", 3)]
	[InlineData("start_column", 2)]
	[InlineData("symbol", "Run")]
	public void TopLevelSelectorsCannotAccompanyRequests(string field, object value)
	{
		Assert.Equal(2, Parse(new() { ["requests"] = new[] { new { path = "a.cs" } }, ["path"] = "b.cs" }).Requests.Count);
		Assert.Throws<McpToolException>(() => Parse(new() { ["requests"] = new[] { new { path = "a.cs" } }, [field] = value }));
	}

	private static McpGetFileRequestSet Parse(Dictionary<string, object?> values) => McpGetFileRequestSet.Parse(
		new McpJsonArguments(values.ToDictionary(static pair => pair.Key, static pair => JsonSerializer.SerializeToElement(pair.Value)),
			values.Keys.ToHashSet(StringComparer.Ordinal)));
}
