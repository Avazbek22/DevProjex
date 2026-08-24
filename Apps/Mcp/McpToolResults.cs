namespace DevProjex.Mcp;

internal static class McpToolResults
{
	private const string MaxResultSizeKey = "anthropic/maxResultSizeChars";

	public static CallToolResult Success(
		string text,
		object? structured = null,
		bool advertiseLargeResult = false) =>
		new()
		{
			Content = [new TextContentBlock { Text = text }],
			StructuredContent = structured is null ? null : JsonSerializer.SerializeToElement(structured),
			Meta = advertiseLargeResult
				? new System.Text.Json.Nodes.JsonObject { [MaxResultSizeKey] = 200_000 }
				: null
		};

	public static CallToolResult Error(McpToolException exception) =>
		new()
		{
			Content = [new TextContentBlock { Text = exception.Message }],
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
