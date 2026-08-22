using System.Globalization;

namespace DevProjex.Terminal.Rendering;

internal static class TerminalTextEscaping
{
	public static string EscapeSingleLine(string value)
	{
		if (!value.Any(IsUnsafeTerminalCharacter))
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
					if (IsUnsafeTerminalCharacter(character))
					{
						escaped
							.Append("\\u")
							.Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
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

	private static bool IsUnsafeTerminalCharacter(char character) =>
		char.IsControl(character) || character is '\u2028' or '\u2029';
}
