using DevProjex.Mcp;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Unit;

public sealed class McpGetFileRequestsTests
{
	[Theory]
	[InlineData(0, 1)]
	[InlineData(-1, 1)]
	[InlineData(3, 2)]
	public void InvalidCoordinatesRejectTheEntireBatchBeforeDelivery(int start, int end)
	{
		var argument = JsonSerializer.SerializeToElement(new[]
		{
			new { path = "Source.txt", ranges = new[] { new { start_line = start, end_line = end } } }
		});
		var error = Assert.Throws<McpToolException>(() => Parse(argument));
		Assert.Equal("DPX-MCP-INVALID-ARGUMENTS", error.Code);
		Assert.Contains("requests[0].ranges[0]", error.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("[{\"path\":\"src/File.cs\"}]")]
	[InlineData(" \n[{\"path\":\"src/File.cs\",\"ranges\":[{\"start_line\":1,\"end_line\":2}]}] \t")]
	[InlineData("[{\"path\":\"src/File.cs\",\"symbol\":\"Run\"}]")]
	public void EncodedArraysKeepEveryParsedRequestAndRange(string encoded)
	{
		using var document = JsonDocument.Parse(encoded);
		var array = Parse(document.RootElement);
		var text = Parse(JsonSerializer.SerializeToElement(encoded));
		Assert.True(text.IsBatch);
		Assert.Equal(JsonSerializer.Serialize(array), JsonSerializer.Serialize(text));
	}

	[Theory]
	[InlineData("{}")]
	[InlineData("[broken")]
	[InlineData("[]")]
	[InlineData("[] trailing")]
	[InlineData("")]
	public void InvalidEncodedArraysKeepTheExistingArrayError(string encoded)
	{
		var error = Assert.Throws<McpToolException>(() => Parse(JsonSerializer.SerializeToElement(encoded)));
		Assert.Equal("DPX-MCP-INVALID-ARGUMENTS: 'requests' must be an array with 1 to 8 entries.", error.Message);
	}

	[Fact]
	public void EncodedArraysKeepTheFileCountAndRangeValidation()
	{
		var tooMany = JsonSerializer.Serialize(Enumerable.Range(0, 9).Select(index => new { path = $"File{index}.cs" }));
		var countError = Assert.Throws<McpToolException>(() => Parse(JsonSerializer.SerializeToElement(tooMany)));
		Assert.Equal("DPX-MCP-INVALID-ARGUMENTS: 'requests' must be an array with 1 to 8 entries.", countError.Message);
		const string invalidRange = "[{\"path\":\"File.cs\",\"ranges\":[{\"start_line\":4,\"end_line\":2}]}]";
		using var document = JsonDocument.Parse(invalidRange);
		var arrayError = Assert.Throws<McpToolException>(() => Parse(document.RootElement));
		var stringError = Assert.Throws<McpToolException>(() => Parse(JsonSerializer.SerializeToElement(invalidRange)));
		Assert.Equal(arrayError.Message, stringError.Message);
	}

	[Fact]
	public void PathArrayParsesAsTheWholeFileRequestsBatch()
	{
		var paths = new[] { "src/a.ts", "src/b.ts", "README.md" };
		var expected = Parse(JsonSerializer.SerializeToElement(paths.Select(static path => new { path })));
		var fromPathArray = ParseNamed("path", JsonSerializer.SerializeToElement(paths));
		var fromPathsArray = ParseNamed("paths", JsonSerializer.SerializeToElement(paths));
		Assert.True(fromPathArray.IsBatch);
		Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(fromPathArray));
		Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(fromPathsArray));
	}

	[Fact]
	public void PathsStringParsesAsOneWholeFileRequest()
	{
		var expected = Parse(JsonSerializer.SerializeToElement(new[] { new { path = "src/a.ts" } }));
		var parsed = ParseNamed("paths", JsonSerializer.SerializeToElement("src/a.ts"));
		Assert.True(parsed.IsBatch);
		Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(parsed));
	}

	[Theory]
	[InlineData("path")]
	[InlineData("paths")]
	public void PathListsKeepTheFileCountLimit(string name)
	{
		var tooMany = JsonSerializer.SerializeToElement(Enumerable.Range(0, 9).Select(static index => $"File{index}.cs"));
		var error = Assert.Throws<McpToolException>(() => ParseNamed(name, tooMany));
		Assert.Equal($"DPX-MCP-INVALID-ARGUMENTS: '{name}' must be an array with 1 to 8 entries.", error.Message);
		var empty = Assert.Throws<McpToolException>(() => ParseNamed(name, JsonSerializer.SerializeToElement(Array.Empty<string>())));
		Assert.Equal(error.Message, empty.Message);
	}

	[Fact]
	public void PathListsRejectBlankEntriesAndLineFields()
	{
		var blank = Assert.Throws<McpToolException>(() => ParseNamed("paths", JsonSerializer.SerializeToElement(new[] { "a.cs", " " })));
		Assert.Equal("DPX-MCP-INVALID-ARGUMENTS: paths[1] must be a non-whitespace string.", blank.Message);
		var lines = Assert.Throws<McpToolException>(() => McpGetFileRequestSet.Parse(Create(new Dictionary<string, JsonElement>
		{
			["path"] = JsonSerializer.SerializeToElement(new[] { "a.cs", "b.cs" }),
			["start_line"] = JsonSerializer.SerializeToElement(2)
		})));
		Assert.Equal(
			"DPX-MCP-INVALID-ARGUMENTS: start_line, end_line, and start_column are valid only with a single 'path'.",
			lines.Message);
	}

	[Theory]
	[InlineData("path", "requests")]
	[InlineData("paths", "requests")]
	[InlineData("path", "paths")]
	public void PathFormsCannotBeCombined(string first, string second)
	{
		var arguments = new Dictionary<string, JsonElement>
		{
			[first] = first == "requests"
				? JsonSerializer.SerializeToElement(new[] { new { path = "a.cs" } })
				: JsonSerializer.SerializeToElement(new[] { "a.cs" }),
			[second] = second == "requests"
				? JsonSerializer.SerializeToElement(new[] { new { path = "a.cs" } })
				: JsonSerializer.SerializeToElement("a.cs")
		};
		var error = Assert.Throws<McpToolException>(() => McpGetFileRequestSet.Parse(Create(arguments)));
		Assert.Equal("DPX-MCP-INVALID-ARGUMENTS: 'path', 'paths' and 'requests' cannot be combined.", error.Message);
	}

	private static McpGetFileRequestSet ParseNamed(string name, JsonElement element) =>
		McpGetFileRequestSet.Parse(Create(new Dictionary<string, JsonElement> { [name] = element }));

	private static McpJsonArguments Create(Dictionary<string, JsonElement> arguments) => McpJsonArguments.Create(
		new CallToolRequestParams { Name = "get_file", Arguments = arguments },
		"path", "paths", "requests", "start_line", "end_line", "start_column");

	private static McpGetFileRequestSet Parse(JsonElement element) => McpGetFileRequestSet.Parse(McpJsonArguments.Create(
		new CallToolRequestParams { Name = "get_file", Arguments = new Dictionary<string, JsonElement> { ["requests"] = element } }, "requests"));
}
