using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpRelatedEvidenceLineCursorTests
{
	[Fact(Timeout = 15_000)]
	public void SequentialEvidenceLineLookupsUseLinearBoundarySearches()
	{
		const int lineCount = 2_048;
		var content = string.Concat(Enumerable.Range(1, lineCount)
			.Select(static line => $"line-{line:D4}\r\n"));
		var cursor = new McpRelatedEvidenceLineCursor(content, Enumerable.Range(1, lineCount));
		var expectedStart = 0;
		for (var lineNumber = 1; lineNumber <= lineCount; lineNumber++)
		{
			Assert.True(cursor.TryReadLine(lineNumber, out var line, out var start, out var end));
			Assert.Equal($"line-{lineNumber:D4}", line);
			Assert.Equal(expectedStart, start);
			Assert.Equal(expectedStart + line.Length, end);
			expectedStart += line.Length + 2;
		}

		Assert.InRange(cursor.LineBoundarySearches, 0, lineCount * 2L);
	}

	[Fact]
	public void InvalidAndTrailingLineCoordinatesKeepExistingSemantics()
	{
		var cursor = new McpRelatedEvidenceLineCursor("first\r\nsecond\nthird\r", [4, 3, 2, 0, 1, 2]);

		Assert.False(cursor.TryReadLine(0, out var invalid, out var invalidStart, out var invalidEnd));
		Assert.Equal(string.Empty, invalid);
		Assert.Equal(0, invalidStart);
		Assert.Equal(0, invalidEnd);
		Assert.True(cursor.TryReadLine(1, out var first, out var firstStart, out var firstEnd));
		Assert.Equal("first", first);
		Assert.Equal((0, 5), (firstStart, firstEnd));
		Assert.True(cursor.TryReadLine(2, out var second, out var secondStart, out var secondEnd));
		Assert.Equal("second", second);
		Assert.Equal((7, 13), (secondStart, secondEnd));
		Assert.True(cursor.TryReadLine(3, out var third, out var thirdStart, out var thirdEnd));
		Assert.Equal("third", third);
		Assert.Equal((14, 19), (thirdStart, thirdEnd));
		Assert.True(cursor.TryReadLine(2, out second, out secondStart, out secondEnd));
		Assert.Equal("second", second);
		Assert.Equal((7, 13), (secondStart, secondEnd));
		Assert.False(cursor.TryReadLine(4, out invalid, out invalidStart, out invalidEnd));
		Assert.Equal(string.Empty, invalid);
		Assert.Equal(0, invalidStart);
		Assert.Equal(0, invalidEnd);
		var trailing = new McpRelatedEvidenceLineCursor("one\n", [3, 2]);
		Assert.True(trailing.TryReadLine(2, out var empty, out var emptyStart, out var emptyEnd));
		Assert.Equal(string.Empty, empty);
		Assert.Equal((4, 4), (emptyStart, emptyEnd));
		Assert.False(trailing.TryReadLine(3, out _, out _, out _));
	}
}
