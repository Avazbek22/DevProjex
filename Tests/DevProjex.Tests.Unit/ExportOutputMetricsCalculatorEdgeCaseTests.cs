using DevProjex.Application.Preview;

namespace DevProjex.Tests.Unit;

public sealed class ExportOutputMetricsCalculatorEdgeCaseTests
{
	private const string ClipboardBlankLine = "\u00A0";

	[Theory]
	[InlineData("", 0, 0, 0)]
	[InlineData("a", 1, 1, 1)]
	[InlineData("abcd", 1, 4, 1)]
	[InlineData("abcde", 1, 5, 2)]
	[InlineData("abcdefgh", 1, 8, 2)]
	[InlineData("abcdefghi", 1, 9, 3)]
	[InlineData("abc\n", 2, 4, 1)]
	[InlineData("abc\r\n", 2, 4, 1)]
	public void FromText_TokenAndLineBoundariesStayStable(
		string text,
		int expectedLines,
		int expectedChars,
		int expectedTokens)
	{
		var metrics = ExportOutputMetricsCalculator.FromText(text);

		Assert.Equal(expectedLines, metrics.Lines);
		Assert.Equal(expectedChars, metrics.Chars);
		Assert.Equal(expectedTokens, metrics.Tokens);
	}

	[Fact]
	public void FromText_UnicodeEmojiAndCombiningMarksUseRenderedUtf16CharCount()
	{
		const string text = "Привет\nCafe\u0301\n🙂";

		var metrics = ExportOutputMetricsCalculator.FromText(text);

		Assert.Equal(3, metrics.Lines);
		Assert.Equal(GetExpectedNormalizedCharCount(text), metrics.Chars);
		Assert.Equal((metrics.Chars + 3) / 4, metrics.Tokens);
	}

	[Fact]
	public async Task FromDocumentAsync_PreservesUtf8RunesAndEveryLineEndingAcrossChunks()
	{
		const string text = "A🙂\r\nБ\rC\n終";
		using var document = new SingleByteChunkPreviewDocument(text);

		var actual = await ExportOutputMetricsCalculator.FromDocumentAsync(
			document,
			TestContext.Current.CancellationToken);

		Assert.Equal(ExportOutputMetricsCalculator.FromText(text), actual);
	}

	[Fact]
	public void FromContentFiles_IgnoresEmptyPathsAndPreservesWhitespaceOnlyFileNames()
	{
		var files = new[]
		{
			new ContentFileMetrics("", 0, 0, 0, IsEmpty: true, IsWhitespaceOnly: false),
			new ContentFileMetrics(" ", 0, 0, 0, IsEmpty: true, IsWhitespaceOnly: false),
			new ContentFileMetrics("b.txt", 4, 1, 4, IsEmpty: false, IsWhitespaceOnly: false),
			new ContentFileMetrics("a.txt", 0, 0, 0, IsEmpty: true, IsWhitespaceOnly: false),
			new ContentFileMetrics("a.txt", 9, 1, 9, IsEmpty: false, IsWhitespaceOnly: false),
			new ContentFileMetrics("\t", 0, 0, 0, IsEmpty: true, IsWhitespaceOnly: false)
		};
		var expectedText = string.Join(
			'\n',
			[
				"\t:",
				ClipboardBlankLine,
				"[No Content, 0 bytes]",
				ClipboardBlankLine,
				ClipboardBlankLine,
				" :",
				ClipboardBlankLine,
				"[No Content, 0 bytes]",
				ClipboardBlankLine,
				ClipboardBlankLine,
				"a.txt:",
				ClipboardBlankLine,
				"[No Content, 0 bytes]",
				ClipboardBlankLine,
				ClipboardBlankLine,
				"b.txt:",
				ClipboardBlankLine,
				"bbbb"
			]);

		var actual = ExportOutputMetricsCalculator.FromContentFiles(files);
		var expected = ExportOutputMetricsCalculator.FromText(expectedText);

		Assert.Equal(expected, actual);
	}

	[Fact]
	public void FromContentFiles_WhitespaceAndEstimatedBranchesMatchRenderedClipboardMarkers()
	{
		var files = new[]
		{
			new ContentFileMetrics(
				Path: "estimated.log",
				SizeBytes: 20_000_000,
				LineCount: 100_000,
				CharCount: 20_000_000,
				IsEmpty: false,
				IsWhitespaceOnly: false,
				IsEstimated: true),
			new ContentFileMetrics(
				Path: "spaces.txt",
				SizeBytes: 6,
				LineCount: 2,
				CharCount: 6,
				IsEmpty: false,
				IsWhitespaceOnly: true)
		};
		var expectedText = string.Join(
			'\n',
			[
				"estimated.log:",
				ClipboardBlankLine,
				string.Empty,
				ClipboardBlankLine,
				ClipboardBlankLine,
				"spaces.txt:",
				ClipboardBlankLine,
				"[Whitespace, 6 bytes]"
			]);

		var actual = ExportOutputMetricsCalculator.FromContentFiles(files);
		var expected = ExportOutputMetricsCalculator.FromText(expectedText);

		Assert.Equal(expected, actual);
	}

	[Fact]
	public void OrderedAccumulator_IgnoresEmptyPathButPreservesWhitespaceOnlyFileName()
	{
		var accumulator = new ExportOutputMetricsCalculator.OrderedContentMetricsAccumulator();

		accumulator.AppendFile(new ContentFileMetrics("", 0, 0, 0, IsEmpty: true, IsWhitespaceOnly: false));
		accumulator.AppendFile(new ContentFileMetrics("   ", 0, 0, 0, IsEmpty: true, IsWhitespaceOnly: false));

		var expected = ExportOutputMetricsCalculator.FromText(
			$"   :\n{ClipboardBlankLine}\n[No Content, 0 bytes]");
		Assert.Equal(expected, accumulator.ToMetrics());
	}

	[Fact]
	public void OrderedAccumulator_AggregatesWorkspaceMetricsBeyondInt32WithoutWrappingToZero()
	{
		var accumulator = new ExportOutputMetricsCalculator.OrderedContentMetricsAccumulator();
		accumulator.AppendFile(new ContentFileMetrics(
			Path: "a",
			SizeBytes: 1_500_000_000,
			LineCount: 1_200_000_000,
			CharCount: 1_500_000_000,
			IsEmpty: false,
			IsWhitespaceOnly: false));
		accumulator.AppendFile(new ContentFileMetrics(
			Path: "b",
			SizeBytes: 1_500_000_000,
			LineCount: 1_200_000_000,
			CharCount: 1_500_000_000,
			IsEmpty: false,
			IsWhitespaceOnly: false));

		var metrics = accumulator.ToMetrics();

		Assert.Equal(2_400_000_006L, metrics.Lines);
		Assert.Equal(3_000_000_015L, metrics.Chars);
		Assert.Equal(750_000_004L, metrics.Tokens);
		Assert.True(metrics.Lines > int.MaxValue);
		Assert.True(metrics.Chars > int.MaxValue);
		Assert.NotEqual(ExportOutputMetrics.Empty, metrics);
	}

	private static int GetExpectedNormalizedCharCount(string text)
	{
		var count = 0;
		for (var i = 0; i < text.Length; i++)
		{
			if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
				i++;

			count++;
		}

		return count;
	}

	private sealed class SingleByteChunkPreviewDocument(string text) : IPreviewTextDocument
	{
		public int LineCount => 1;
		public int MaxLineLength => text.Length;
		public long CharacterCount => text.Length;
		public IReadOnlyList<PreviewDocumentSection> Sections => [];

		public string GetFullText() => text;
		public string GetLineText(int lineNumber) => text;
		public string GetLineRangeText(int firstLine, int lastLine) => text;

		public async ValueTask WriteToAsync(
			Stream destination,
			CancellationToken cancellationToken = default)
		{
			var bytes = Encoding.UTF8.GetBytes(text);
			var singleByte = new byte[1];
			foreach (var value in bytes)
			{
				singleByte[0] = value;
				await destination
					.WriteAsync(singleByte, cancellationToken)
					.ConfigureAwait(false);
			}
		}

		public void Dispose()
		{
		}
	}
}
