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

	public static CallToolResult StructuredSuccess(object value, string? notice = null) =>
		StructuredSuccess(value, notice, static (_, trailer) => trailer);

	public static CallToolResult ProtectedJsonSuccess(object value, string? notice = null) =>
		ProtectedJsonSuccess(value, notice, static (_, trailer) => trailer);

	public static CallToolResult ProtectedJsonSuccess(
		object value,
		string? notice,
		Func<int, string?, string?> completeNotice)
	{
		ArgumentNullException.ThrowIfNull(value);
		ArgumentNullException.ThrowIfNull(completeNotice);
		var spotlighted = McpSpotlight.Wrap(JsonSerializer.Serialize(value, StructuredTextOptions));
		notice = completeNotice(spotlighted.Length, notice);
		List<ContentBlock> content = [new TextContentBlock { Text = spotlighted }];
		if (!string.IsNullOrWhiteSpace(notice))
			content.Add(new TextContentBlock { Text = notice });
		return new CallToolResult { Content = content };
	}

	/// <summary>
	/// Builds the result, letting the caller finish its trailing notice once the spotlighted
	/// structured block is known. A caller that reports how large its own reply is needs that length:
	/// the structured block is most of the reply, so measuring only the notice understates it.
	/// </summary>
	public static CallToolResult StructuredSuccess(
		object value,
		string? notice,
		Func<int, string?, string?> completeNotice)
	{
		ArgumentNullException.ThrowIfNull(value);
		ArgumentNullException.ThrowIfNull(completeNotice);
		var structured = JsonSerializer.SerializeToElement(value);
		var spotlighted = McpSpotlight.Wrap(JsonSerializer.Serialize(structured, StructuredTextOptions));
		notice = completeNotice(spotlighted.Length, notice);
		List<ContentBlock> content =
		[
			new TextContentBlock { Text = spotlighted }
		];
		if (!string.IsNullOrWhiteSpace(notice))
			content.Add(new TextContentBlock { Text = notice });
		return new CallToolResult
		{
			Content = content,
			StructuredContent = structured
		};
	}

	public static CallToolResult Error(McpToolException exception) =>
		new()
		{
			Content =
			[
				new TextContentBlock
				{
					Text = $"{exception.Code}: request failed.\n\n" +
					       McpSpotlight.Wrap(McpTextEscaping.EscapeSingleLine(exception.Message))
				}
			],
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
