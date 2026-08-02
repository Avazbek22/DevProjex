using System.Text;

namespace DevProjex.Kernel.Models;

internal static class GitPathTextNormalizer
{
	public static string NormalizeObservedPath(
		string value,
		bool normalizeUnicode,
		bool ignoreAsciiCase)
	{
		var normalized = normalizeUnicode && ContainsNonAscii(value) &&
		                 !value.IsNormalized(NormalizationForm.FormC)
			? value.Normalize(NormalizationForm.FormC)
			: value;
		return ignoreAsciiCase ? FoldUpperAscii(normalized) : normalized;
	}

	public static string NormalizePattern(string value, bool ignoreAsciiCase)
	{
		if (!ignoreAsciiCase)
			return value;

		StringBuilder? builder = null;
		for (var index = 0; index < value.Length; index++)
		{
			var current = value[index];
			if (current == '\\' && index + 1 < value.Length)
			{
				if (builder is not null)
					builder.Append(current).Append(value[++index]);
				else
					index++;
				continue;
			}

			if (current == '[')
			{
				var classEnd = FindCharacterClassEnd(value, index + 1);
				if (classEnd < 0)
				{
					builder?.Append(value.AsSpan(index));
					break;
				}

				builder?.Append(value.AsSpan(index, classEnd - index + 1));
				index = classEnd;
				continue;
			}

			if (current is >= 'A' and <= 'Z')
			{
				if (builder is null)
				{
					builder = new StringBuilder(value.Length);
					builder.Append(value.AsSpan(0, index));
				}

				builder.Append((char)(current + ('a' - 'A')));
				continue;
			}

			builder?.Append(current);
		}

		return builder?.ToString() ?? value;
	}

	public static int FindCharacterClassEnd(ReadOnlySpan<char> pattern, int start)
	{
		var index = start;
		if (index < pattern.Length && pattern[index] is '!' or '^')
			index++;

		var isFirstItem = true;
		var hasPreviousCharacter = false;
		while (index < pattern.Length)
		{
			var current = pattern[index];
			if (!isFirstItem && current == ']')
				return index;

			if (current == '\\')
			{
				if (++index >= pattern.Length)
					return -1;

				hasPreviousCharacter = true;
				isFirstItem = false;
				index++;
				continue;
			}

			if (current == '-' &&
			    hasPreviousCharacter &&
			    index + 1 < pattern.Length &&
			    pattern[index + 1] != ']')
			{
				index++;
				if (pattern[index] == '\\' && ++index >= pattern.Length)
					return -1;

				hasPreviousCharacter = false;
				isFirstItem = false;
				index++;
				continue;
			}

			if (current == '[' && index + 1 < pattern.Length && pattern[index + 1] == ':')
			{
				var relativeEnd = pattern[(index + 2)..].IndexOf(']');
				if (relativeEnd < 0)
					return -1;

				var posixClassEnd = index + 2 + relativeEnd;
				if (posixClassEnd > index + 1 && pattern[posixClassEnd - 1] == ':')
				{
					hasPreviousCharacter = false;
					isFirstItem = false;
					index = posixClassEnd + 1;
					continue;
				}
			}

			hasPreviousCharacter = true;
			isFirstItem = false;
			index++;
		}

		return -1;
	}

	public static bool RequiresObservedPathNormalization(
		ReadOnlySpan<char> value,
		bool normalizeUnicode,
		bool ignoreAsciiCase) =>
		normalizeUnicode && ContainsNonAscii(value) ||
		ignoreAsciiCase && ContainsUpperAscii(value);

	private static bool ContainsNonAscii(ReadOnlySpan<char> value)
	{
		foreach (var character in value)
		{
			if (character > 0x7f)
				return true;
		}

		return false;
	}

	private static bool ContainsUpperAscii(ReadOnlySpan<char> value)
	{
		foreach (var character in value)
		{
			if (character is >= 'A' and <= 'Z')
				return true;
		}

		return false;
	}

	private static string FoldUpperAscii(string value)
	{
		var firstUpperIndex = -1;
		for (var index = 0; index < value.Length; index++)
		{
			if (value[index] is not (>= 'A' and <= 'Z'))
				continue;

			firstUpperIndex = index;
			break;
		}

		if (firstUpperIndex < 0)
			return value;

		return string.Create(
			value.Length,
			(Value: value, FirstUpperIndex: firstUpperIndex),
			static (destination, state) =>
			{
				state.Value.AsSpan(0, state.FirstUpperIndex).CopyTo(destination);
				for (var index = state.FirstUpperIndex; index < state.Value.Length; index++)
				{
					var character = state.Value[index];
					destination[index] = character is >= 'A' and <= 'Z'
						? (char)(character + ('a' - 'A'))
						: character;
				}
			});
	}
}
