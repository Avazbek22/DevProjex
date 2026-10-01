using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpGetFileRangeStatusTests
{
	[Theory]
	[InlineData(1, 100, "Ok")]
	[InlineData(50, 1_500, "Partial")]
	[InlineData(1_450, 1_600, "NotReturned")]
	public void ClassifyDeliveryUsesEachOriginalRange(
		int startLine,
		int endLine,
		string expected)
	{
		var range = new McpGetFileRange(1, 1, startLine, endLine);
		var page = Page(startLine: 1, endLine: 992, totalLines: 1_600);

		var status = range.ClassifyDelivery(page);

		Assert.Equal(expected, status.ToString());
	}

	[Fact]
	public void ClassifyDeliveryTreatsAClampedRangeAsComplete()
	{
		var range = new McpGetFileRange(1, 1, 1, int.MaxValue);
		var page = Page(startLine: 1, endLine: 5, totalLines: 5);

		var status = range.ClassifyDelivery(page);

		Assert.Equal(McpGetFileRangeDeliveryStatus.Ok, status);
	}

	[Fact]
	public void ClassifyDeliveryRejectsCoverageBeforeTheRequestedRange()
	{
		var range = new McpGetFileRange(1, 1, 20, 30);
		var page = Page(startLine: 1, endLine: 10, totalLines: 100);

		var status = range.ClassifyDelivery(page);

		Assert.Equal(McpGetFileRangeDeliveryStatus.NotReturned, status);
	}

	[Fact]
	public void ClassifyDeliveryTreatsAPartialLongLineAsPartial()
	{
		var range = new McpGetFileRange(1, 1, 1, 1);
		var page = Page(startLine: 1, endLine: 1, totalLines: 2, characterLimitReached: true);

		var status = range.ClassifyDelivery(page);

		Assert.Equal(McpGetFileRangeDeliveryStatus.Partial, status);
	}

	[Theory]
	[InlineData(1, 100, 50, 50, 100)]
	[InlineData(50, 100, 50, 50, 100)]
	[InlineData(50, 100, 80, 80, 100)]
	public void ContinueAtLineReturnsAValidRemainingRange(
		int startLine,
		int endLine,
		int nextLine,
		int expectedStartLine,
		int expectedEndLine)
	{
		var range = new McpGetFileRange(2, 3, startLine, endLine);

		var continuation = Assert.IsType<McpGetFileRange>(range.ContinueAtLine(nextLine));

		Assert.Equal(2, continuation.RequestIndex);
		Assert.Equal(3, continuation.RangeIndex);
		Assert.Equal(expectedStartLine, continuation.StartLine);
		Assert.Equal(expectedEndLine, continuation.EndLine);
	}

	[Fact]
	public void ContinueAtLineOmitsAnAlreadyDeliveredRange()
	{
		var range = new McpGetFileRange(1, 1, 10, 20);

		Assert.Null(range.ContinueAtLine(21));
	}

	[Fact]
	public void ContinueAtLineRejectsAnInvalidCoordinate()
	{
		var range = new McpGetFileRange(1, 1, 10, 20);

		Assert.Throws<ArgumentOutOfRangeException>(() => range.ContinueAtLine(0));
	}

	private static McpTextPage Page(
		int startLine,
		int endLine,
		int totalLines,
		bool characterLimitReached = false) =>
		new(
			Text: string.Empty,
			StartLine: startLine,
			EndLine: endLine,
			TotalLines: totalLines,
			IsTruncated: endLine < totalLines || characterLimitReached,
			CharacterLimitReached: characterLimitReached);
}
