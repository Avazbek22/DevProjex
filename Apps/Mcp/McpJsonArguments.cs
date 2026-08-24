namespace DevProjex.Mcp;

internal sealed class McpJsonArguments(
	IDictionary<string, JsonElement>? values,
	IReadOnlySet<string> allowed)
{
	private readonly IDictionary<string, JsonElement> _values =
		values ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
	private readonly IReadOnlySet<string> _allowed = allowed;

	public static McpJsonArguments Create(
		CallToolRequestParams request,
		params string[] allowed)
	{
		ArgumentNullException.ThrowIfNull(request);
		var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
		var arguments = new McpJsonArguments(request.Arguments, allowedSet);
		arguments.ValidateNames();
		return arguments;
	}

	public string? OptionalString(string name)
	{
		if (!_values.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
			return null;
		if (value.ValueKind != JsonValueKind.String)
			throw Invalid(name, "a string");
		return value.GetString();
	}

	public string RequiredString(string name)
	{
		var value = OptionalString(name);
		if (string.IsNullOrWhiteSpace(value))
			throw new McpToolException(
				McpErrorCodes.InvalidArguments,
				$"{McpErrorCodes.InvalidArguments}: '{name}' is required and must be a non-empty string.");
		return value;
	}

	public IReadOnlyList<string>? OptionalStringArray(string name)
	{
		if (!_values.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
			return null;
		if (value.ValueKind != JsonValueKind.Array)
			throw Invalid(name, "an array of strings");

		var result = new List<string>();
		foreach (var item in value.EnumerateArray())
		{
			if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
				throw Invalid(name, "an array of non-empty strings");
			result.Add(item.GetString()!);
		}
		return result;
	}

	public bool OptionalBoolean(string name, bool defaultValue)
	{
		if (!_values.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
			return defaultValue;
		if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
			throw Invalid(name, "a boolean");
		return value.GetBoolean();
	}

	public int? OptionalInteger(string name, int minimum, int maximum)
	{
		if (!_values.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
			return null;

		int parsed;
		if (value.ValueKind == JsonValueKind.Number)
		{
			if (!value.TryGetInt32(out parsed))
				throw InvalidInteger(name, minimum, maximum);
		}
		else if (value.ValueKind == JsonValueKind.String &&
		         int.TryParse(
			         value.GetString(),
			         System.Globalization.NumberStyles.None,
			         System.Globalization.CultureInfo.InvariantCulture,
			         out parsed))
		{
		}
		else
		{
			throw InvalidInteger(name, minimum, maximum);
		}

		if (parsed < minimum || parsed > maximum)
			throw InvalidInteger(name, minimum, maximum);
		return parsed;
	}

	private void ValidateNames()
	{
		var unexpected = _values.Keys
			.Where(name => !_allowed.Contains(name))
			.OrderBy(static name => name, StringComparer.Ordinal)
			.ToArray();
		if (unexpected.Length == 0)
			return;

		throw new McpToolException(
			McpErrorCodes.InvalidArguments,
			$"{McpErrorCodes.InvalidArguments}: unknown argument(s): {string.Join(", ", unexpected)}. " +
			$"Valid arguments: {string.Join(", ", _allowed.OrderBy(static name => name, StringComparer.Ordinal))}.");
	}

	private static McpToolException Invalid(string name, string expected) =>
		new(
			McpErrorCodes.InvalidArguments,
			$"{McpErrorCodes.InvalidArguments}: '{name}' must be {expected}.");

	private static McpToolException InvalidInteger(string name, int minimum, int maximum) =>
		new(
			McpErrorCodes.InvalidRange,
			$"{McpErrorCodes.InvalidRange}: '{name}' must be an integer or numeric string from {minimum} to {maximum}.");
}
