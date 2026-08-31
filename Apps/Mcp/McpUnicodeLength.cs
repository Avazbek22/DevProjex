namespace DevProjex.Mcp;

internal static class McpUnicodeLength
{
	public static bool ExceedsScalarValueCount(string value, int maximum)
	{
		ArgumentNullException.ThrowIfNull(value);
		ArgumentOutOfRangeException.ThrowIfNegative(maximum);
		if (value.Length <= maximum)
			return false;

		var count = 0;
		foreach (var _ in value.EnumerateRunes())
		{
			if (++count > maximum)
				return true;
		}

		return false;
	}
}
