using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpBatchTextLineViewTests
{
	[Fact(Timeout = 15_000)]
	public void DisjointRangesReuseOneFullTextScan()
	{
		var text = string.Concat(Enumerable.Range(1, 2_048)
			.Select(static line => $"line-{line:D4}\r\n"));
		var starts = Enumerable.Range(0, 16).Select(static index => 1_000 + index * 40).ToArray();
		var view = new McpBatchTextLineView(text, starts);
		foreach (var start in starts)
		{
			var page = view.Slice(start, start + 1, 1_000, 50_000, CancellationToken.None);
			Assert.Equal($"line-{start:D4}\nline-{start + 1:D4}", page.Text);
		}

		Assert.Equal(1, view.FullTextScans);
	}

	[Theory]
	[InlineData("first\r\nsecond\rthird\n", 1, 4, 1, 50)]
	[InlineData("first\r\nsecond\rthird\n", 2, 3, 2, 50)]
	[InlineData("one\r", 2, 2, 1, 50)]
	[InlineData("one\n", 2, 2, 1, 50)]
	[InlineData("A😀B\nnext", 1, 2, 1, 2)]
	[InlineData("A😀B\nnext", 1, 2, 2, 2)]
	public void IndexedPagesMatchExistingSlice(
		string text,
		int start,
		int end,
		int column,
		int maximumCharacters)
	{
		var view = new McpBatchTextLineView(text, [start]);
		var expected = McpTextRanges.Slice(
			text, start, end, 1_000, maximumCharacters, CancellationToken.None, column);
		var actual = view.Slice(start, end, 1_000, maximumCharacters, CancellationToken.None, column);
		Assert.Equal(expected, actual);
	}

	[Fact]
	public void InvalidLineCoordinatesMatchExistingSlice()
	{
		var view = new McpBatchTextLineView("one\rtwo", [0, 3, 4]);
		foreach (var start in new[] { 0, 3, 4 })
		{
			var expected = Assert.Throws<McpToolException>(() =>
				McpTextRanges.Slice("one\rtwo", start, null, 1_000, 50_000, CancellationToken.None));
			var actual = Assert.Throws<McpToolException>(() =>
				view.Slice(start, null, 1_000, 50_000, CancellationToken.None));
			Assert.Equal(expected.Code, actual.Code);
		}
	}

	[Fact(Timeout = 15_000)]
	public void IndexedSlicesMatchPublicSlicesAcrossLineEndingsAndPageBounds()
	{
		string[] contents =
		[
			"α😀beta\r\nsecond\rthird\n",
			"one\n\nthree",
			"\r\n\r",
			"long-😀-line\rnext\r\nlast"
		];
		foreach (var text in contents)
		{
			var total = McpTextRanges.Slice(text, null, null, 1_000, 50_000, CancellationToken.None).TotalLines;
			var view = new McpBatchTextLineView(text, Enumerable.Range(1, total + 1));
			for (var start = 1; start <= total + 1; start++)
			{
				foreach (var end in new int?[] { null, start, start + 2 })
					foreach (var maximumLines in new[] { 1, 3 })
						foreach (var maximumCharacters in new[] { 1, 4, 50 })
							foreach (var column in new[] { 1, 2, 4 })
							{
								McpTextPage expected;
								try
								{
									expected = McpTextRanges.Slice(
										text, start, end, maximumLines, maximumCharacters, CancellationToken.None, column);
								}
								catch (McpToolException exception)
								{
									var actual = Assert.Throws<McpToolException>(() => view.Slice(
										start, end, maximumLines, maximumCharacters, CancellationToken.None, column));
									Assert.Equal(exception.Code, actual.Code);
									continue;
								}
								Assert.Equal(expected, view.Slice(
									start, end, maximumLines, maximumCharacters, CancellationToken.None, column));
							}
			}
		}
	}
}
