using DevProjex.Mcp;
using ModelContextProtocol.Protocol;

namespace DevProjex.Tests.Unit;

public sealed class McpGetFileRequestsTests
{
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

	private static McpGetFileRequestSet Parse(JsonElement element) => McpGetFileRequestSet.Parse(McpJsonArguments.Create(
		new CallToolRequestParams { Name = "get_file", Arguments = new Dictionary<string, JsonElement> { ["requests"] = element } }, "requests"));
}
