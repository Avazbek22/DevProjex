namespace DevProjex.Tests.Terminal;

public sealed class TerminalTextPositionTests
{
	[Theory]
	[InlineData("", 0, 0)]
	[InlineData("plain", 3, 3)]
	[InlineData("a😀b", 0, 0)]
	[InlineData("a😀b", 1, 1)]
	[InlineData("a😀b", 2, 3)]
	[InlineData("a😀b", 3, 4)]
	[InlineData("a😀b", 99, 4)]
	public void RuneToUtf16IndexHandlesWideUnicodeScalars(
		string text,
		int runeIndex,
		int expected)
	{
		Assert.Equal(expected, TerminalTextPosition.RuneToUtf16Index(text, runeIndex));
	}

	[Theory]
	[InlineData("", 0, 0)]
	[InlineData("plain", 3, 3)]
	[InlineData("a😀b", 0, 0)]
	[InlineData("a😀b", 1, 1)]
	[InlineData("a😀b", 3, 2)]
	[InlineData("a😀b", 4, 3)]
	[InlineData("a😀b", 99, 3)]
	public void Utf16ToRuneIndexHandlesWideUnicodeScalars(
		string text,
		int utf16Index,
		int expected)
	{
		Assert.Equal(expected, TerminalTextPosition.Utf16ToRuneIndex(text, utf16Index));
	}
}
