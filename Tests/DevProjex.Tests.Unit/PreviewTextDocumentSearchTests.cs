using DevProjex.Application.Preview;

namespace DevProjex.Tests.Unit;

public sealed class PreviewTextDocumentSearchTests
{
	[Fact]
	public void FindAll_SearchesEveryLineWithoutMaterializingTheWholeDocument()
	{
		using var document = new TrackingPreviewDocument(
		[
			"first marker",
			"middle",
			"second MARKER",
			"final marker"
		]);

		var matches = PreviewTextDocumentSearch.FindAll(
			document,
			"marker",
			TestContext.Current.CancellationToken);

		Assert.Equal(
		[
			new PreviewTextSearchMatch(0, 6),
			new PreviewTextSearchMatch(2, 7),
			new PreviewTextSearchMatch(3, 6)
		],
			matches);
		Assert.Equal(4, document.LineReadCount);
		Assert.False(document.FullTextRequested);
	}

	[Fact]
	public void FindAll_ObservesCancellationBetweenFileBackedLines()
	{
		using var cancellation = new CancellationTokenSource();
		using var document = new TrackingPreviewDocument(
			Enumerable.Range(1, 100).Select(index => $"line {index}").ToArray(),
			onLineRead: count =>
			{
				if (count == 3)
					cancellation.Cancel();
			});

		Assert.Throws<OperationCanceledException>(() =>
			PreviewTextDocumentSearch.FindAll(document, "line", cancellation.Token));
		Assert.InRange(document.LineReadCount, 3, 4);
	}

	private sealed class TrackingPreviewDocument(
		IReadOnlyList<string> lines,
		Action<int>? onLineRead = null) : IPreviewTextDocument
	{
		public int LineReadCount { get; private set; }
		public bool FullTextRequested { get; private set; }
		public int LineCount => lines.Count;
		public int MaxLineLength => lines.Max(static line => line.Length);
		public long CharacterCount => lines.Sum(static line => (long)line.Length);
		public IReadOnlyList<PreviewDocumentSection> Sections => [];

		public string GetFullText()
		{
			FullTextRequested = true;
			return string.Join('\n', lines);
		}

		public string GetLineText(int lineNumber)
		{
			LineReadCount++;
			onLineRead?.Invoke(LineReadCount);
			return lines[lineNumber - 1];
		}

		public string GetLineRangeText(int firstLine, int lastLine) =>
			string.Join('\n', lines.Skip(firstLine - 1).Take(lastLine - firstLine + 1));

		public void Dispose()
		{
		}
	}
}
