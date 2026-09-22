using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.Secrets;
using System.Text.Json.Nodes;

namespace DevProjex.Mcp;

internal static class McpToolResults
{
	private const string MaxResultSizeKey = "anthropic/maxResultSizeChars";
	private const string RedactedMetadataMarker = "[redacted]";
	private static readonly Lazy<IReadOnlyList<ISecretDetector>> MetadataDetectors = new(
		static () => [new GitleaksSecretDetector(), new PrivateDataDetector()],
		LazyThreadSafetyMode.ExecutionAndPublication);
	private static readonly JsonSerializerOptions StructuredTextOptions = new()
	{
		WriteIndented = true
	};

	public static CallToolResult TextSuccess(
		string text,
		bool advertiseLargeResult = false)
	{
		McpSpotlight.EnsureBalanced(text);
		return new CallToolResult
		{
			Content = [new TextContentBlock { Text = text }],
			Meta = advertiseLargeResult
				? new System.Text.Json.Nodes.JsonObject { [MaxResultSizeKey] = 200_000 }
				: null
		};
	}

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
		var protectedValue = ProtectStructuredMetadata(value);
		var spotlighted = McpSpotlight.Wrap(JsonSerializer.Serialize(protectedValue, StructuredTextOptions));
		notice = completeNotice(spotlighted.Length, notice);
		List<ContentBlock> content = [new TextContentBlock { Text = spotlighted }];
		if (!string.IsNullOrWhiteSpace(notice))
		{
			McpSpotlight.EnsureBalanced(notice);
			content.Add(new TextContentBlock { Text = notice });
		}
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
		{
			McpSpotlight.EnsureBalanced(notice);
			content.Add(new TextContentBlock { Text = notice });
		}
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
						   McpSpotlight.Wrap(McpTextEscaping.EscapeSingleLine(ProtectMetadataString(exception.Message)))
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

	public static void EnsureBalanced(CallToolResult result)
	{
		ArgumentNullException.ThrowIfNull(result);
		foreach (var block in result.Content.OfType<TextContentBlock>())
			McpSpotlight.EnsureBalanced(block.Text);
	}

	internal static string ProtectMetadataString(string value)
	{
		if (string.IsNullOrEmpty(value))
			return value;
		try
		{
			var ranges = MetadataDetectors.Value
				.SelectMany(detector => detector.Detect("mcp-metadata.txt", value))
				.Where(finding => finding.Start >= 0 && finding.Length > 0 && finding.Start + finding.Length <= value.Length)
				.Select(finding => (Start: finding.Start, End: finding.Start + finding.Length))
				.OrderBy(static range => range.Start)
				.ThenByDescending(static range => range.End)
				.ToArray();
			if (ranges.Length == 0)
				return value;

			var merged = new List<(int Start, int End)>(ranges.Length);
			foreach (var range in ranges)
			{
				if (merged.Count == 0 || range.Start > merged[^1].End)
				{
					merged.Add(range);
					continue;
				}
				merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, range.End));
			}

			var protectedValue = new StringBuilder(value.Length);
			var cursor = 0;
			foreach (var range in merged)
			{
				protectedValue.Append(value, cursor, range.Start - cursor);
				protectedValue.Append(RedactedMetadataMarker);
				cursor = range.End;
			}
			protectedValue.Append(value, cursor, value.Length - cursor);
			return protectedValue.ToString();
		}
		catch (SecretDetectionException)
		{
			return RedactedMetadataMarker;
		}
	}

	private static JsonElement ProtectStructuredMetadata(object value)
	{
		var node = JsonSerializer.SerializeToNode(value) ?? new JsonObject();
		ProtectNode(node);
		return JsonSerializer.SerializeToElement(node);
	}

	private static void ProtectNode(JsonNode node)
	{
		switch (node)
		{
			case JsonObject jsonObject:
				foreach (var property in jsonObject.ToArray())
				{
					if (property.Value is JsonValue value && value.TryGetValue<string>(out var text))
						jsonObject[property.Key] = ProtectMetadataString(text);
					else if (property.Value is not null)
						ProtectNode(property.Value);
				}
				break;
			case JsonArray jsonArray:
				for (var index = 0; index < jsonArray.Count; index++)
				{
					if (jsonArray[index] is JsonValue value && value.TryGetValue<string>(out var text))
						jsonArray[index] = ProtectMetadataString(text);
					else if (jsonArray[index] is { } child)
						ProtectNode(child);
				}
				break;
		}
	}
}
