using System.Text;

namespace DevProjex.Kernel;

public static class SingleLineTextEscaping
{
	private const string HexDigits = "0123456789ABCDEF";

	public static string Escape(string value) => Escape(value, preserveTabs: false);

	/// <param name="preserveTabs">
	/// Keeps a tab as a tab. Quoted source lines use this: a tab is part of the code being quoted and
	/// cannot break a one-line layout, while an escaped one no longer copies back as code.
	/// </param>
	public static string Escape(string value, bool preserveTabs)
	{
		ArgumentNullException.ThrowIfNull(value);
		if (!ContainsUnsafeCharacter(value, preserveTabs))
			return value;

		var escaped = new StringBuilder(value.Length);
		AppendBounded(escaped, value.AsSpan(), int.MaxValue, preserveTabs);
		return escaped.ToString();
	}

	public static int GetEscapedLength(ReadOnlySpan<char> value, bool preserveTabs = false)
	{
		var length = 0;
		for (var index = 0; index < value.Length; index++)
		{
			var character = value[index];
			var isSurrogatePair = char.IsHighSurrogate(character) &&
			                      index + 1 < value.Length &&
			                      char.IsLowSurrogate(value[index + 1]);
			length = checked(length + GetEscapedLength(character, isSurrogatePair, preserveTabs));
			if (isSurrogatePair)
				index++;
		}

		return length;
	}

	public static bool AppendBounded(
		StringBuilder destination,
		ReadOnlySpan<char> value,
		int maximumAdditionalCharacters,
		bool preserveTabs = false)
	{
		ArgumentNullException.ThrowIfNull(destination);
		ArgumentOutOfRangeException.ThrowIfNegative(maximumAdditionalCharacters);
		var remaining = maximumAdditionalCharacters;
		for (var index = 0; index < value.Length; index++)
		{
			var character = value[index];
			var isSurrogatePair = char.IsHighSurrogate(character) &&
			                      index + 1 < value.Length &&
			                      char.IsLowSurrogate(value[index + 1]);
			var required = GetEscapedLength(character, isSurrogatePair, preserveTabs);
			if (required > remaining)
				return false;

			switch (character)
			{
				case '\r':
					destination.Append("\\r");
					break;
				case '\n':
					destination.Append("\\n");
					break;
				case '\t' when !preserveTabs:
					destination.Append("\\t");
					break;
				default:
					if (IsUnsafeCharacter(character, preserveTabs))
					{
						destination
							.Append("\\u")
							.Append(HexDigits[(character >> 12) & 0xF])
							.Append(HexDigits[(character >> 8) & 0xF])
							.Append(HexDigits[(character >> 4) & 0xF])
							.Append(HexDigits[character & 0xF]);
					}
					else
					{
						destination.Append(character);
						if (isSurrogatePair)
							destination.Append(value[++index]);
					}
					break;
			}

			remaining -= required;
		}

		return true;
	}

	private static bool ContainsUnsafeCharacter(string value, bool preserveTabs)
	{
		foreach (var character in value)
		{
			if (IsUnsafeCharacter(character, preserveTabs))
				return true;
		}

		return false;
	}

	private static int GetEscapedLength(char character, bool isSurrogatePair, bool preserveTabs) =>
		character is '\r' or '\n' || (character == '\t' && !preserveTabs)
			? 2
			: IsUnsafeCharacter(character, preserveTabs)
				? 6
				: isSurrogatePair ? 2 : 1;

	private static bool IsUnsafeCharacter(char character, bool preserveTabs) =>
		(char.IsControl(character) && !(preserveTabs && character == '\t')) ||
		character is '\u2028' or '\u2029';
}
