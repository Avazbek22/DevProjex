using System.Text;

namespace DevProjex.Application.Services;

internal static class MarkdownInlineLiteralEncoder
{
	public static string Encode(string value)
	{
		var sanitized = SingleLineTextEscaping.Escape(value);
		StringBuilder? output = null;
		for (var index = 0; index < sanitized.Length; index++)
		{
			var character = sanitized[index];
			if (!RequiresEscape(sanitized, index, character))
			{
				output?.Append(character);
				continue;
			}

			output ??= new StringBuilder(sanitized.Length + 8)
				.Append(sanitized, 0, index);
			output.Append('\\').Append(character);
		}

		return output?.ToString() ?? sanitized;
	}

	private static bool RequiresEscape(string value, int index, char character)
	{
		if (character is '\\' or '`' or '*' or '_' or '{' or '}' or '[' or ']' or '<' or '>' or
		    '&' or '|' or '~' or '^' or '$')
		{
			return true;
		}

		if (character is '-' or '+' or '#' or '!' && IsFirstNonWhitespaceCharacter(value, index))
			return true;

		return character is '.' or ')' && IsOrderedListDelimiter(value, index);
	}

	private static bool IsFirstNonWhitespaceCharacter(string value, int index)
	{
		for (var precedingIndex = 0; precedingIndex < index; precedingIndex++)
		{
			if (!char.IsWhiteSpace(value[precedingIndex]))
				return false;
		}

		return true;
	}

	private static bool IsOrderedListDelimiter(string value, int index)
	{
		if (index == 0 || index + 1 >= value.Length || !char.IsWhiteSpace(value[index + 1]))
			return false;

		var hasDigit = false;
		for (var digitIndex = index - 1; digitIndex >= 0; digitIndex--)
		{
			if (char.IsAsciiDigit(value[digitIndex]))
			{
				hasDigit = true;
				continue;
			}

			if (!char.IsWhiteSpace(value[digitIndex]))
				return false;
		}

		return hasDigit;
	}
}
