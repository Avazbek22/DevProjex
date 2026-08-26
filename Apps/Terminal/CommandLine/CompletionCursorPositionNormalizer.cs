using System.Text;

namespace DevProjex.Terminal.CommandLine;

internal static class CompletionCursorPositionNormalizer
{
	public const string Utf8ByteUnit = "utf8-byte";
	public const string UnicodeScalarUnit = "unicode-scalar";

	public static bool TryNormalize(
		string commandLine,
		int position,
		string? unit,
		out int normalizedPosition)
	{
		ArgumentNullException.ThrowIfNull(commandLine);
		switch (unit)
		{
			case null:
			case "utf16":
				normalizedPosition = Math.Clamp(position, 0, commandLine.Length);
				return true;
			case Utf8ByteUnit:
				normalizedPosition = ConvertUtf8ByteOffset(commandLine, position);
				return true;
			case UnicodeScalarUnit:
				normalizedPosition = ConvertUnicodeScalarOffset(commandLine, position);
				return true;
			default:
				normalizedPosition = default;
				return false;
		}
	}

	private static int ConvertUtf8ByteOffset(string value, int offset)
	{
		var remainingBytes = Math.Max(0, offset);
		var utf16Offset = 0;
		foreach (var rune in value.EnumerateRunes())
		{
			if (remainingBytes < rune.Utf8SequenceLength)
				break;
			remainingBytes -= rune.Utf8SequenceLength;
			utf16Offset += rune.Utf16SequenceLength;
		}
		return utf16Offset;
	}

	private static int ConvertUnicodeScalarOffset(string value, int offset)
	{
		var remainingScalars = Math.Max(0, offset);
		var utf16Offset = 0;
		foreach (var rune in value.EnumerateRunes())
		{
			if (remainingScalars == 0)
				break;
			remainingScalars--;
			utf16Offset += rune.Utf16SequenceLength;
		}
		return utf16Offset;
	}
}
