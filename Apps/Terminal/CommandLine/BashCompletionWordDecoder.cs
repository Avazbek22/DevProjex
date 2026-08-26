using System.Text;

namespace DevProjex.Terminal.CommandLine;

internal static class BashCompletionWordDecoder
{
	public static string Decode(string word)
	{
		ArgumentNullException.ThrowIfNull(word);
		return Parse(word, word.Length, splitWords: false).SingleOrDefault() ?? string.Empty;
	}

	public static IReadOnlyList<string> Tokenize(string commandLine, int cursorPosition)
	{
		ArgumentNullException.ThrowIfNull(commandLine);
		return Parse(
			commandLine,
			Math.Clamp(cursorPosition, 0, commandLine.Length),
			splitWords: true);
	}

	private static IReadOnlyList<string> Parse(
		string value,
		int length,
		bool splitWords)
	{
		var words = new List<string>();
		var decoded = new StringBuilder(length);
		var wordStarted = false;
		var quote = '\0';
		for (var index = 0; index < length; index++)
		{
			var character = value[index];
			if (quote == '\0' && splitWords && char.IsWhiteSpace(character))
			{
				if (wordStarted)
				{
					words.Add(decoded.ToString());
					decoded.Clear();
					wordStarted = false;
				}
				continue;
			}

			if (quote == '\'')
			{
				if (character == '\'')
					quote = '\0';
				else
					decoded.Append(character);
				continue;
			}

			if (quote == '"')
			{
				if (character == '"')
				{
					quote = '\0';
					continue;
				}

				if (character == '\\' &&
				    TryReadDoubleQuotedEscape(value, length, ref index, out var escaped))
				{
					if (escaped != '\n')
						decoded.Append(escaped);
					continue;
				}

				decoded.Append(character);
				continue;
			}

			switch (character)
			{
				case '\'':
				case '"':
					wordStarted = true;
					quote = character;
					break;
				case '\\' when index + 1 < length:
					wordStarted = true;
					var escaped = value[++index];
					if (escaped != '\n')
						decoded.Append(escaped);
					break;
				default:
					wordStarted = true;
					decoded.Append(character);
					break;
			}
		}

		if (wordStarted)
			words.Add(decoded.ToString());
		return words;
	}

	private static bool TryReadDoubleQuotedEscape(
		string value,
		int length,
		ref int index,
		out char escaped)
	{
		if (index + 1 >= length)
		{
			escaped = default;
			return false;
		}

		escaped = value[index + 1];
		if (escaped is not ('$' or '`' or '"' or '\\' or '\n'))
			return false;

		index++;
		return true;
	}
}
