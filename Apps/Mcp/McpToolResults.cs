namespace DevProjex.Mcp;

internal static class McpToolResults
{
	private const string MaxResultSizeKey = "anthropic/maxResultSizeChars";
	private static readonly JsonSerializerOptions StructuredTextOptions = new()
	{
		WriteIndented = true
	};

	public static CallToolResult TextSuccess(
		string text,
		bool advertiseLargeResult = false) =>
		new()
		{
			Content = [new TextContentBlock { Text = text }],
			Meta = advertiseLargeResult
				? new System.Text.Json.Nodes.JsonObject { [MaxResultSizeKey] = 200_000 }
				: null
		};

	public static CallToolResult StructuredSuccess(object value)
	{
		ArgumentNullException.ThrowIfNull(value);
		var structured = JsonSerializer.SerializeToElement(value);
		return new CallToolResult
		{
			Content =
			[
				new TextContentBlock
				{
					Text = JsonSerializer.Serialize(structured, StructuredTextOptions)
				}
			],
			StructuredContent = structured
		};
	}

	public static CallToolResult Error(McpToolException exception) =>
		new()
		{
			Content = [new TextContentBlock { Text = McpTextEscaping.EscapeSingleLine(exception.Message) }],
			IsError = true
		};

	public static CallToolResult Error(Exception exception) =>
		new()
		{
			Content =
			[
				new TextContentBlock
				{
					Text = $"DPX-MCP-OPERATION-FAILED: the operation could not be completed ({exception.GetType().Name}). " +
					       "Verify the project is readable and retry with narrower paths or patterns."
				}
			],
			IsError = true
		};
}
