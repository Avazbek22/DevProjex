using System.Globalization;

namespace DevProjex.Mcp;

/// <summary>
/// Reads <c>detail_by_pattern</c>: an ordered list of glob-to-detail overrides layered on the
/// call-level <c>detail</c>. Entries apply in order and the last match wins, so a general entry can
/// be followed by a specific one.
///
/// Masks go through the same validation, normalisation and matcher as <c>include_patterns</c>; only
/// the error code differs, because a bad entry here is a bad argument shape rather than a bad
/// pattern parameter, and the caller needs the entry index to find it.
/// </summary>
internal static class McpDetailOverrides
{
	public const string ParameterName = "detail_by_pattern";
	public const int MaximumEntries = 16;
	public const int MaximumPatternsPerEntry = 32;

	public static IReadOnlyList<ContentDetailOverride>? Parse(McpJsonArguments arguments)
	{
		ArgumentNullException.ThrowIfNull(arguments);
		if (!arguments.TryGetElement(ParameterName, out var element))
			return null;
		if (element.ValueKind != JsonValueKind.Array)
			throw Invalid($"'{ParameterName}' must be an array of objects");
		if (element.GetArrayLength() > MaximumEntries)
			throw Invalid($"'{ParameterName}' accepts at most {MaximumEntries} entries");
		if (element.GetArrayLength() == 0)
			return null;

		var overrides = new List<ContentDetailOverride>(element.GetArrayLength());
		var index = 0;
		foreach (var entry in element.EnumerateArray())
		{
			if (entry.ValueKind != JsonValueKind.Object)
				throw InvalidEntry(index, "must be an object with 'patterns' and 'detail'");
			ValidatePropertyNames(entry, index);
			overrides.Add(new ContentDetailOverride(
				ReadPatterns(entry, index),
				ReadRequestedKinds(entry, index)));
			index++;
		}
		return overrides;
	}

	private static ContentDetailPatternSet ReadPatterns(JsonElement entry, int index)
	{
		if (!entry.TryGetProperty("patterns", out var patternsElement) ||
		    patternsElement.ValueKind != JsonValueKind.Array)
		{
			throw InvalidEntry(index, "'patterns' must be a non-empty array of strings");
		}
		if (patternsElement.GetArrayLength() == 0)
			throw InvalidEntry(index, "'patterns' must contain at least one pattern");
		if (patternsElement.GetArrayLength() > MaximumPatternsPerEntry)
			throw InvalidEntry(index, $"'patterns' accepts at most {MaximumPatternsPerEntry} patterns");

		var patterns = new List<string>(patternsElement.GetArrayLength());
		foreach (var pattern in patternsElement.EnumerateArray())
		{
			if (pattern.ValueKind != JsonValueKind.String)
				throw InvalidEntry(index, "every pattern must be a string");
			patterns.Add(pattern.GetString()!);
		}

		try
		{
			return ContentDetailPatternSet.Create(patterns);
		}
		catch (ProjectRelativeGlobException failure)
		{
			throw InvalidEntry(index, $"'patterns' is invalid: {failure.Reason}");
		}
	}

	private static CodeTransformKinds ReadRequestedKinds(JsonElement entry, int index)
	{
		if (!entry.TryGetProperty("detail", out var detailElement) ||
		    detailElement.ValueKind != JsonValueKind.String)
		{
			throw InvalidEntry(index, "'detail' must be one of full, compact, signatures");
		}

		// Deliberately case-sensitive and matched against the same tokens as the call-level detail,
		// so one mistake cannot mean two different things depending on where it was written.
		return detailElement.GetString() switch
		{
			"full" => McpDetailPolicy.RequestedKinds(McpDetailLevel.Full),
			"compact" => McpDetailPolicy.RequestedKinds(McpDetailLevel.Compact),
			"signatures" => McpDetailPolicy.RequestedKinds(McpDetailLevel.Signatures),
			var value => throw InvalidEntry(
				index,
				$"'detail' is '{McpTextEscaping.EscapeSingleLine(value ?? string.Empty)}'; " +
				"valid values are full, compact, signatures")
		};
	}

	private static void ValidatePropertyNames(JsonElement entry, int index)
	{
		foreach (var property in entry.EnumerateObject())
		{
			if (property.NameEquals("patterns") || property.NameEquals("detail"))
				continue;
			throw InvalidEntry(
				index,
				$"contains unknown property '{McpTextEscaping.EscapeSingleLine(property.Name)}'");
		}
	}

	private static McpToolException Invalid(string message) =>
		new(McpErrorCodes.InvalidArguments, $"{McpErrorCodes.InvalidArguments}: {message}.");

	private static McpToolException InvalidEntry(int index, string message) =>
		new(
			McpErrorCodes.InvalidArguments,
			$"{McpErrorCodes.InvalidArguments}: {ParameterName}[{index.ToString(CultureInfo.InvariantCulture)}] {message}.");
}
