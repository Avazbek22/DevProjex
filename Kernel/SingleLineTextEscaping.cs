using System.Text;

namespace DevProjex.Kernel;

public static class SingleLineTextEscaping
{
	private const string HexDigits = "0123456789ABCDEF";

	public static string Escape(string value)
	{
		ArgumentNullException.ThrowIfNull(value);
		if (!ContainsUnsafeCharacter(value))
			return value;

		var escaped = new StringBuilder(value.Length);
		AppendBounded(escaped, value.AsSpan(), int.MaxValue);
		return escaped.ToString();
	}

	public static int GetEscapedLength(ReadOnlySpan<char> value)
	{
		var length = 0;
		for (var index = 0; index < value.Length; index++)
		{
			var character = value[index];
			var isSurrogatePair = char.IsHighSurrogate(character) &&
			                      index + 1 < value.Length &&
			                      char.IsLowSurrogate(value[index + 1]);
			length = checked(length + GetEscapedLength(character, isSurrogatePair));
			if (isSurrogatePair)
				index++;
		}

		return length;
	}

	public static bool AppendBounded(
		StringBuilder destination,
		ReadOnlySpan<char> value,
		int maximumAdditionalCharacters)
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
			var required = GetEscapedLength(character, isSurrogatePair);
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
				case '\t':
					destination.Append("\\t");
					break;
				default:
					if (IsUnsafeCharacter(character))
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

	private static bool ContainsUnsafeCharacter(string value)
	{
		foreach (var character in value)
		{
			if (IsUnsafeCharacter(character))
				return true;
		}

		return false;
	}

	private static int GetEscapedLength(char character, bool isSurrogatePair) =>
		character is '\r' or '\n' or '\t'
			? 2
			: IsUnsafeCharacter(character)
				? 6
				: isSurrogatePair ? 2 : 1;

	private static bool IsUnsafeCharacter(char character) =>
		char.IsControl(character) || character is '\u2028' or '\u2029';
}
