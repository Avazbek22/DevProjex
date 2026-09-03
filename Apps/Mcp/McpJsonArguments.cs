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

	public string RequiredString(string name, bool allowWhitespace = false)
	{
		var value = OptionalString(name);
		if (string.IsNullOrEmpty(value) || (!allowWhitespace && string.IsNullOrWhiteSpace(value)))
			throw new McpToolException(
				McpErrorCodes.InvalidArguments,
				$"{McpErrorCodes.InvalidArguments}: '{name}' is required and must be a " +
				(allowWhitespace ? "non-empty string." : "non-whitespace string."));
		return value;
	}

	public IReadOnlyList<string>? OptionalStringArray(
		string name,
		bool allowWhitespace = false,
		int? maximumItems = null,
		int? maximumItemScalarValues = null,
		string? tooManyItemsHint = null,
		string? overLengthHint = null)
	{
		if (!_values.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
			return null;
		if (value.ValueKind != JsonValueKind.Array)
			throw Invalid(name, "an array of strings");
		if (maximumItems is not null && value.GetArrayLength() > maximumItems.Value)
		{
			throw Invalid(
				name,
				$"an array with at most {maximumItems.Value} items; " +
				$"{tooManyItemsHint ?? "narrow the selection and retry"}");
		}

		var result = new List<string>(value.GetArrayLength());
		foreach (var item in value.EnumerateArray())
		{
			var itemValue = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
			if (item.ValueKind != JsonValueKind.String ||
			    string.IsNullOrEmpty(itemValue) ||
			    (!allowWhitespace && string.IsNullOrWhiteSpace(itemValue)))
				throw Invalid(name, "an array of non-empty strings");
			if (maximumItemScalarValues is not null &&
			    McpUnicodeLength.ExceedsScalarValueCount(itemValue, maximumItemScalarValues.Value))
			{
				throw Invalid(
					name,
					$"an array whose items contain at most {maximumItemScalarValues.Value} characters; " +
					$"{overLengthHint ?? "shorten the paths and retry"}");
			}
			result.Add(itemValue);
		}
		return result;
	}

	public bool OptionalBoolean(string name, bool defaultValue)
	{
		if (!_values.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
			return defaultValue;
		if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
			return value.GetBoolean();
		if (value.ValueKind == JsonValueKind.String)
		{
			return value.GetString() switch
			{
				"true" => true,
				"false" => false,
				_ => throw Invalid(name, "a boolean or the string 'true' or 'false'")
			};
		}
		throw Invalid(name, "a boolean or the string 'true' or 'false'");
	}

	public int? OptionalInteger(string name, int minimum, int maximum)
	{
		if (!_values.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
			return null;

		int parsed;
		if (value.ValueKind == JsonValueKind.Number)
		{
			if (!TryGetIntegralInt64(value, out var integer) || integer is < int.MinValue or > int.MaxValue)
				throw InvalidInteger(name, minimum, maximum);
			parsed = (int)integer;
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

	public long? OptionalInt64(string name, long minimum, long maximum)
	{
		if (!_values.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
			return null;

		long parsed;
		if (value.ValueKind == JsonValueKind.Number)
		{
			if (!TryGetIntegralInt64(value, out parsed))
				throw InvalidInt64(name, minimum, maximum);
		}
		else if (value.ValueKind == JsonValueKind.String &&
		         long.TryParse(
			         value.GetString(),
			         System.Globalization.NumberStyles.None,
			         System.Globalization.CultureInfo.InvariantCulture,
			         out parsed))
		{
		}
		else
		{
			throw InvalidInt64(name, minimum, maximum);
		}

		if (parsed < minimum || parsed > maximum)
			throw InvalidInt64(name, minimum, maximum);
		return parsed;
	}

	private static bool TryGetIntegralInt64(JsonElement value, out long parsed)
	{
		if (value.TryGetInt64(out parsed))
			return true;
		if (!value.TryGetDecimal(out var decimalValue) || decimal.Truncate(decimalValue) != decimalValue ||
		    decimalValue is < long.MinValue or > long.MaxValue)
		{
			parsed = default;
			return false;
		}

		parsed = (long)decimalValue;
		return true;
	}

	private void ValidateNames()
	{
		var unexpected = _values.Keys
			.Where(name => !_allowed.Contains(name))
			.OrderBy(static name => name, StringComparer.Ordinal)
			.ToArray();
		if (unexpected.Length == 0)
			return;

		var guidance = _allowed.Count == 0
			? "This tool takes no arguments."
			: $"Valid arguments: {string.Join(", ", _allowed.OrderBy(static name => name, StringComparer.Ordinal))}.";
		throw new McpToolException(
			McpErrorCodes.InvalidArguments,
			$"{McpErrorCodes.InvalidArguments}: unknown argument(s): {string.Join(", ", unexpected)}. {guidance}");
	}

	private static McpToolException Invalid(string name, string expected) =>
		new(
			McpErrorCodes.InvalidArguments,
			$"{McpErrorCodes.InvalidArguments}: '{name}' must be {expected}.");

	private static McpToolException InvalidInteger(string name, int minimum, int maximum) =>
		new(
			McpErrorCodes.InvalidRange,
			$"{McpErrorCodes.InvalidRange}: '{name}' must be an integer or numeric string from {minimum} to {maximum}.");

	private static McpToolException InvalidInt64(string name, long minimum, long maximum) =>
		new(
			McpErrorCodes.InvalidRange,
			$"{McpErrorCodes.InvalidRange}: '{name}' must be an integer or numeric string from {minimum} to {maximum}.");
}
