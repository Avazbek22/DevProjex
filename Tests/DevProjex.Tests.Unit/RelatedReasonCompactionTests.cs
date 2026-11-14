using DevProjex.Application.Dependencies;

namespace DevProjex.Tests.Unit;

public sealed class RelatedReasonCompactionTests
{
	[Fact]
	public void EmptyListStaysEmpty()
	{
		Assert.Empty(RelatedReasonCompaction.Compact([]));
	}

	[Fact]
	public void SingleSiteKeepsItsReasonExactly()
	{
		Assert.Equal(
			["import ._client at line 6", "one module target"],
			RelatedReasonCompaction.Compact(["import ._client at line 6", "one module target"]));
	}

	[Fact]
	public void SitesSharingTextBecomeOneEntryWithAscendingLines()
	{
		// The engine sorts reasons ordinally, so line 141 arrives before line 56.
		var compacted = RelatedReasonCompaction.Compact(
		[
			"type reference Response at line 141",
			"type reference Response at line 186",
			"type reference Response at line 56"
		]);

		Assert.Equal(["type reference Response at lines 56, 141, 186"], compacted);
	}

	[Fact]
	public void ExactlyFiveSitesAreAllListed()
	{
		var compacted = RelatedReasonCompaction.Compact(Sites("type reference URL", 41, 126, 175, 211, 247));

		Assert.Equal(["type reference URL at lines 41, 126, 175, 211, 247"], compacted);
	}

	[Fact]
	public void MoreThanFiveSitesListTheFirstFiveAndCountTheRest()
	{
		var compacted = RelatedReasonCompaction.Compact(
			Sites("type reference Response", 417, 56, 141, 380, 186, 222, 339, 258, 298));

		Assert.Equal(["type reference Response at lines 56, 141, 186, 222, 258 (+4 more)"], compacted);
	}

	[Fact]
	public void DuplicateLinesAreListedAndCountedOnce()
	{
		var compacted = RelatedReasonCompaction.Compact(
		[
			"type reference User at line 3",
			"type reference User at line 3",
			"type reference User at line 9"
		]);

		Assert.Equal(["type reference User at lines 3, 9"], compacted);
	}

	[Fact]
	public void DuplicateSingleSiteCollapsesToTheOriginalReason()
	{
		Assert.Equal(
			["type reference User at line 3"],
			RelatedReasonCompaction.Compact(["type reference User at line 3", "type reference User at line 3"]));
	}

	[Fact]
	public void GroupTakesThePlaceOfItsFirstSiteAndOtherReasonsKeepTheirOrder()
	{
		var compacted = RelatedReasonCompaction.Compact(
		[
			"import ._models at line 8",
			"one module target",
			"one visible declaration identity",
			"type reference Response at line 141",
			"type argument of a call",
			"type reference Response at line 56",
			"declaration part of one resolved symbol with 2 files"
		]);

		Assert.Equal(
			[
				"import ._models at line 8",
				"one module target",
				"one visible declaration identity",
				"type reference Response at lines 56, 141",
				"type argument of a call",
				"declaration part of one resolved symbol with 2 files"
			],
			compacted);
	}

	[Fact]
	public void DifferentTextsWithTheSameLinesStaySeparate()
	{
		var compacted = RelatedReasonCompaction.Compact(
		[
			"type reference ILogEventSink at line 26",
			"type reference ILogEventSink at line 35",
			"type reference LogEvent at line 26",
			"type reference LogEvent at line 35",
			"import Serilog.Events at line 35"
		]);

		Assert.Equal(
			[
				"type reference ILogEventSink at lines 26, 35",
				"type reference LogEvent at lines 26, 35",
				"import Serilog.Events at line 35"
			],
			compacted);
	}

	[Fact]
	public void TextIsMatchedExactlyIncludingCase()
	{
		var compacted = RelatedReasonCompaction.Compact(
			["type reference user at line 1", "type reference User at line 2"]);

		Assert.Equal(["type reference user at line 1", "type reference User at line 2"], compacted);
	}

	[Fact]
	public void SiteMarkerInsideTheTextBelongsToTheText()
	{
		var compacted = RelatedReasonCompaction.Compact(
		[
			"reference note at line 7 of a header at line 12",
			"reference note at line 7 of a header at line 3",
			"reference flagged at line 4 by the parser",
			"reference flagged at line 4 by the parser"
		]);

		Assert.Equal(
			[
				"reference note at line 7 of a header at lines 3, 12",
				"reference flagged at line 4 by the parser",
				"reference flagged at line 4 by the parser"
			],
			compacted);
	}

	[Theory]
	[InlineData("type reference User at line ")]
	[InlineData("type reference User at line -4")]
	[InlineData("type reference User at line +4")]
	[InlineData("type reference User at line 04")]
	[InlineData("type reference User at line 4 ")]
	[InlineData("type reference User at line 4, column 2")]
	[InlineData("type reference User at line 99999999999")]
	[InlineData("at line 4")]
	public void NonCanonicalSiteSuffixesAreLeftUntouched(string reason)
	{
		var reasons = new[] { reason, "type reference User at line 4" };

		Assert.Equal(reasons, RelatedReasonCompaction.Compact(reasons));
	}

	[Fact]
	public void ProtectedSitesWithoutReferenceTextCompactByTheirLabel()
	{
		var compacted = RelatedReasonCompaction.Compact(
		[
			"one visible declaration identity",
			"type reference at line 12",
			"type reference at line 7",
			"type reference Logger at line 9"
		]);

		Assert.Equal(
			[
				"one visible declaration identity",
				"type reference at lines 7, 12",
				"type reference Logger at line 9"
			],
			compacted);
	}

	[Fact]
	public void UnchangedListIsReturnedAsIs()
	{
		IReadOnlyList<string> reasons = ["one module target", "import ._client at line 6"];

		Assert.Same(reasons, RelatedReasonCompaction.Compact(reasons));
	}

	private static string[] Sites(string text, params int[] lines) =>
		lines.Select(line => $"{text} at line {line}").ToArray();
}
