namespace DevProjex.Tests.Terminal;

public sealed class TerminalFrameTitleTests
{
	[Theory]
	[InlineData("Content processing:", "Content processing")]
	[InlineData("Traitement du contenu :", "Traitement du contenu")]
	[InlineData("Exclusions", "Exclusions")]
	public void NormalizeRemovesOnlyTrailingTitlePunctuation(string value, string expected)
	{
		Assert.Equal(expected, TerminalFrameTitle.Normalize(value));
	}

	[Fact]
	public void FitTruncatesTheTitleWithinTheReservedColumns()
	{
		Assert.Equal(
			"Long set…",
			TerminalFrameTitle.Fit("Long settings title:", 9, useUnicode: true));
	}
}
