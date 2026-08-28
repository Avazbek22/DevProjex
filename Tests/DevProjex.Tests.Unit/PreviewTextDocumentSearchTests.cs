using DevProjex.Application.Preview;

namespace DevProjex.Tests.Unit;

public sealed class PreviewTextDocumentSearchTests
{
	[Fact]
	public void FindAll_FileBackedMatchesInMemoryAcrossVisitChunkBoundaries()
	{
		using var temporary = new TemporaryDirectory();
		var lines = Enumerable.Range(1, 2_050)
			.Select(static lineNumber => lineNumber is 1_024 or 1_025 or 2_048 or 2_049
				? $"line {lineNumber}: 境界🙂 marker"
				: $"line {lineNumber}")
			.ToArray();
		var text = string.Concat(lines.Select(
			static (line, index) => index == 2_049
				? line
				: line + (index % 2 == 0 ? "\r\n" : "\n")));
		using var inMemory = new InMemoryPreviewTextDocument(text);
		using var fileBacked = CreateFileBackedDocument(temporary, text, inMemory);

		var expected = PreviewTextDocumentSearch.FindAll(
			inMemory,
			"境界🙂",
			TestContext.Current.CancellationToken);
		var actual = PreviewTextDocumentSearch.FindAll(
			fileBacked,
			"境界🙂",
			TestContext.Current.CancellationToken);

		Assert.Equal(expected, actual);
		Assert.Equal([1_023, 1_024, 2_047, 2_048], actual.Select(static match => match.Line));
	}

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

	[Theory]
	[InlineData("")]
	[InlineData("a")]
	[InlineData("界")]
	[InlineData(" a ")]
	public void CanSearchRequiresAtLeastTwoRunes(string query) =>
		Assert.False(PreviewTextDocumentSearch.CanSearch(query));

	[Fact]
	public void FindCapsMatchesAtTheSharedPreviewLimit()
	{
		using var document = new TrackingPreviewDocument(
			Enumerable.Repeat("marker marker", PreviewTextDocumentSearch.MaximumMatches).ToArray());

		var result = PreviewTextDocumentSearch.Find(
			document,
			"marker",
			TestContext.Current.CancellationToken);

		Assert.True(result.IsCapped);
		Assert.Equal(PreviewTextDocumentSearch.MaximumMatches, result.Matches.Count);
	}

	private static FileBackedPreviewTextDocument CreateFileBackedDocument(
		TemporaryDirectory temporary,
		string text,
		InMemoryPreviewTextDocument inMemory)
	{
		var bytes = Encoding.UTF8.GetBytes(text);
		var storagePath = Path.Combine(temporary.Path, $"{Guid.NewGuid():N}.preview.txt");
		File.WriteAllBytes(storagePath, bytes);
		var lineOffsets = new List<long> { 0 };
		for (var index = 0; index < bytes.Length; index++)
		{
			if (bytes[index] == (byte)'\n')
				lineOffsets.Add(index + 1L);
		}

		return new FileBackedPreviewTextDocument(
			storagePath,
			lineOffsets.ToArray(),
			bytes.Length,
			inMemory.MaxLineLength,
			inMemory.CharacterCount);
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
