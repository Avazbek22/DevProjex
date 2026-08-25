namespace DevProjex.Mcp;

internal static class McpTextEscaping
{
	public static string EscapeSingleLine(string value)
	{
		if (!value.Any(IsUnsafeCharacter))
			return value;

		var escaped = new StringBuilder(value.Length);
		foreach (var character in value)
		{
			switch (character)
			{
				case '\r':
					escaped.Append("\\r");
					break;
				case '\n':
					escaped.Append("\\n");
					break;
				case '\t':
					escaped.Append("\\t");
					break;
				default:
					if (IsUnsafeCharacter(character))
					{
						escaped
							.Append("\\u")
							.Append(((int)character).ToString(
								"X4",
								System.Globalization.CultureInfo.InvariantCulture));
					}
					else
					{
						escaped.Append(character);
					}
					break;
			}
		}

		return escaped.ToString();
	}

	private static bool IsUnsafeCharacter(char character) =>
		char.IsControl(character) || character is '\u2028' or '\u2029';
}
