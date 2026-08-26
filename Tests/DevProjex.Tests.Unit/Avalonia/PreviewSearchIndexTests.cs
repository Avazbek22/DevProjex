using DevProjex.Avalonia.Services;
using DevProjex.Application.Preview;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class PreviewSearchIndexTests
{
	[Fact]
	public void Find_SearchesVisibleLinesOrdinalIgnoreCase_WithoutMaterializingDocument()
	{
		using var document = new TrackingDocument(
		[
			"ignored tree Needle",
			"ignored header Needle",
			"first Needle",
			"no match",
			"NEEDLE and needle"
		],
		[
			new PreviewDocumentSection(
				"file.txt",
				StartLine: 2,
				EndLine: 5,
				HeaderLine: 2,
				ContentStartLine: 3)
		]);

		var result = PreviewSearchIndex.Find(
			document,
			"needle",
			TestContext.Current.CancellationToken);

		Assert.Equal(
		[
			new PreviewSearchMatch(3, 6, 6),
			new PreviewSearchMatch(5, 0, 6),
			new PreviewSearchMatch(5, 11, 6)
		],
			result.Matches);
		Assert.False(result.IsCapped);
		Assert.Equal(3, document.LineReadCount);
		Assert.False(document.FullTextRequested);
	}

	[Fact]
	public void Find_WithoutFileContentSections_DoesNotReadDocument()
	{
		using var document = new TrackingDocument(["tree-only match"]);

		var result = PreviewSearchIndex.Find(
			document,
			"match",
			TestContext.Current.CancellationToken);

		Assert.Empty(result.Matches);
		Assert.False(result.IsCapped);
		Assert.Equal(0, document.LineReadCount);
	}

	[Fact]
	public void Find_StopsAtCapAndReportsOverflow()
	{
		using var document = new RepeatedLineDocument(10_001, "match");

		var result = PreviewSearchIndex.Find(
			document,
			"match",
			TestContext.Current.CancellationToken);

		Assert.Equal(PreviewSearchIndex.MaximumMatches, result.Matches.Length);
		Assert.True(result.IsCapped);
		Assert.Equal(10_001, document.LineReadCount);
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("x")]
	[InlineData("🙂")]
	[InlineData("two\nlines")]
	public void Find_RejectsShortOrMultilineQueryWithoutReadingDocument(string query)
	{
		using var document = new TrackingDocument(["two lines"]);

		var result = PreviewSearchIndex.Find(
			document,
			query,
			TestContext.Current.CancellationToken);

		Assert.Empty(result.Matches);
		Assert.False(result.IsCapped);
		Assert.Equal(0, document.LineReadCount);
	}

	[Fact]
	public void Find_OneCharacterQueryDoesNotScanLargeDocument()
	{
		using var document = new RepeatedLineDocument(1_000_000, "x");

		var result = PreviewSearchIndex.Find(
			document,
			"x",
			TestContext.Current.CancellationToken);

		Assert.Empty(result.Matches);
		Assert.False(result.IsCapped);
		Assert.Equal(0, document.LineReadCount);
	}

	private sealed class TrackingDocument(
		IReadOnlyList<string> lines,
		IReadOnlyList<PreviewDocumentSection>? sections = null) : IPreviewTextDocument
	{
		public int LineReadCount { get; private set; }
		public bool FullTextRequested { get; private set; }
		public int LineCount => lines.Count;
		public int MaxLineLength => lines.Max(static line => line.Length);
		public long CharacterCount => lines.Sum(static line => (long)line.Length);
		public IReadOnlyList<PreviewDocumentSection> Sections { get; } = sections ?? [];
		public IReadOnlyList<PreviewRedactionSpan> Redactions => [];

		public string GetFullText()
		{
			FullTextRequested = true;
			return string.Join('\n', lines);
		}

		public string GetLineText(int lineNumber)
		{
			LineReadCount++;
			return lines[lineNumber - 1];
		}

		public string GetLineRangeText(int firstLine, int lastLine) =>
			string.Join('\n', lines.Skip(firstLine - 1).Take(lastLine - firstLine + 1));

		public void Dispose()
		{
		}
	}

	private sealed class RepeatedLineDocument(int lineCount, string line) : IPreviewTextDocument
	{
		public int LineReadCount { get; private set; }
		public int LineCount { get; } = lineCount;
		public int MaxLineLength => line.Length;
		public long CharacterCount => (long)LineCount * line.Length;
		public IReadOnlyList<PreviewDocumentSection> Sections { get; } =
		[
			new PreviewDocumentSection(
				"repeated.txt",
				StartLine: 1,
				EndLine: lineCount,
				HeaderLine: 0,
				ContentStartLine: 1)
		];
		public IReadOnlyList<PreviewRedactionSpan> Redactions => [];
		public string GetFullText() => throw new InvalidOperationException("Full text must not be requested.");

		public string GetLineText(int lineNumber)
		{
			LineReadCount++;
			return line;
		}

		public string GetLineRangeText(int firstLine, int lastLine) =>
			throw new InvalidOperationException("Line ranges must not be requested.");

		public void Dispose()
		{
		}
	}
}
