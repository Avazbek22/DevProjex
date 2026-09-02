using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpBoundedLineTextWriterTests
{
	[Fact]
	public void CharacterLimitStopsAnOversizedLineWithoutMaterializingItsTail()
	{
		using var writer = new McpBoundedLineTextWriter(
			maximumLines: 2_000,
			maximumCharacters: 4_096);

		Assert.Throws<McpLineLimitReachedException>(() => writer.Write(new string('x', 100_000)));

		Assert.True(writer.IsTruncated);
		Assert.Equal(4_096, writer.Text.Length);
		Assert.Equal(new string('x', 4_096), writer.Text);
	}

	[Fact]
	public void CharacterLimitNeverSplitsASurrogatePair()
	{
		using var writer = new McpBoundedLineTextWriter(
			maximumLines: 10,
			maximumCharacters: 8);

		Assert.Throws<McpLineLimitReachedException>(() => writer.Write("1234567😀tail"));

		Assert.True(writer.IsTruncated);
		Assert.Equal("1234567", writer.Text);
		Assert.False(char.IsHighSurrogate(writer.Text[^1]));
	}

	[Fact]
	public void CharacterLimitNeverPublishesAHighSurrogateSplitAcrossWrites()
	{
		using var writer = new McpBoundedLineTextWriter(
			maximumLines: 10,
			maximumCharacters: 8);
		writer.Write("1234567\uD83D");

		Assert.Throws<McpLineLimitReachedException>(() => writer.Write('\uDE00'));

		Assert.Equal("1234567", writer.Text);
	}

	[Fact]
	public void ResponseSegmentLimitPreservesCompleteUnicodeScalarsAndMarker()
	{
		const string marker = "[truncated]";

		var result = DevProjexMcpTools.LimitResponseSegment(
			"1234567😀tail",
			maximumCharacters: 20,
			marker,
			forceMarker: true);

		Assert.Equal("1234567\n[truncated]", result);
		Assert.True(result.Length <= 20);
		Assert.DoesNotContain('\uFFFD', result);
	}
}
