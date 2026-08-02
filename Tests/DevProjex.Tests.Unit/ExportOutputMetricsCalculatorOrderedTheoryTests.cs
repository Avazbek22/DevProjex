namespace DevProjex.Tests.Unit;

public sealed class ExportOutputMetricsCalculatorOrderedTheoryTests
{
	private const string ClipboardBlankLine = "\u00A0";
	private const string NoContentMarker = "[No Content, 0 bytes]";
	private const string WhitespaceMarkerPrefix = "[Whitespace, ";
	private const string WhitespaceMarkerSuffix = " bytes]";

	[Theory]
	[MemberData(nameof(OrderedCases))]
	public void FromOrderedContentFiles_MatchesRenderedClipboardMetrics(
		int caseId,
		IReadOnlyList<ContentFileMetrics> orderedFiles,
		string expectedRenderedText)
	{
		var actual = ExportOutputMetricsCalculator.FromOrderedContentFiles(orderedFiles);
		var expected = ExportOutputMetricsCalculator.FromText(expectedRenderedText);

		Assert.Equal(expected.Lines, actual.Lines);
		Assert.Equal(expected.Chars, actual.Chars);
		Assert.Equal(expected.Tokens, actual.Tokens);
		Assert.True(caseId >= 0);
	}

	[Theory]
	[MemberData(nameof(OrderedCases))]
	public void OrderedContentMetricsAccumulator_MatchesRenderedClipboardMetrics(
		int caseId,
		IReadOnlyList<ContentFileMetrics> orderedFiles,
		string expectedRenderedText)
	{
		var accumulator = new ExportOutputMetricsCalculator.OrderedContentMetricsAccumulator();
		foreach (var file in orderedFiles)
			accumulator.AppendFile(file);

		var actual = accumulator.ToMetrics();
		var expected = ExportOutputMetricsCalculator.FromText(expectedRenderedText);

		Assert.Equal(expected.Lines, actual.Lines);
		Assert.Equal(expected.Chars, actual.Chars);
		Assert.Equal(expected.Tokens, actual.Tokens);
		Assert.True(caseId >= 0);
	}

	public static IEnumerable<object[]> OrderedCases()
	{
		var variants = CreateVariants();
		var caseId = 0;

		for (var i = 0; i < variants.Count; i++)
		{
			yield return BuildCase(
				caseId++,
				[
					($"single-{i:D2}.txt", variants[i])
				]);
		}

		for (var i = 0; i < variants.Count; i++)
		{
			for (var j = i; j < variants.Count; j++)
			{
				yield return BuildCase(
					caseId++,
					[
						($"pair-{i:D2}-a.txt", variants[i]),
						($"pair-{i:D2}-{j:D2}-b.txt", variants[j])
					]);
			}
		}

		for (var i = 0; i < variants.Count; i++)
		{
			var next = variants[(i + 1) % variants.Count];
			var next2 = variants[(i + 2) % variants.Count];

			yield return BuildCase(
				caseId++,
				[
					($"triple-{i:D2}-a.txt", variants[i]),
					($"triple-{i:D2}-b.txt", next),
					($"triple-{i:D2}-c.txt", next2)
				]);
		}
	}

	private static object[] BuildCase(int caseId, IReadOnlyList<(string Path, ContentVariant Variant)> entries)
	{
		var ordered = entries
			.Select(tuple => tuple.Variant.ToMetrics(tuple.Path))
			.OrderBy(tuple => tuple.Path, PathComparer.Default)
			.ToList();

		var expectedRendered = RenderExpectedClipboardText(
			entries
				.OrderBy(tuple => tuple.Path, PathComparer.Default)
				.ToList());

		return [caseId, ordered, expectedRendered];
	}

	private static string RenderExpectedClipboardText(IReadOnlyList<(string Path, ContentVariant Variant)> orderedEntries)
	{
		if (orderedEntries.Count == 0)
			return string.Empty;

		var sb = new StringBuilder();
		var anyWritten = false;

		foreach (var (path, variant) in orderedEntries)
		{
			if (anyWritten)
			{
				sb.AppendLine(ClipboardBlankLine);
				sb.AppendLine(ClipboardBlankLine);
			}

			anyWritten = true;

			sb.AppendLine($"{path}:");
			sb.AppendLine(ClipboardBlankLine);

			if (variant.IsEmpty)
			{
				sb.AppendLine(NoContentMarker);
				continue;
			}

			if (variant.IsWhitespaceOnly)
			{
				sb.AppendLine($"{WhitespaceMarkerPrefix}{variant.SizeBytes}{WhitespaceMarkerSuffix}");
				continue;
			}

			if (variant.IsEstimated)
			{
				// Estimated entries contribute an empty content line in the rendered export.
				sb.AppendLine(string.Empty);
				continue;
			}

			sb.AppendLine(variant.RenderedText);
		}

		return sb.ToString().TrimEnd('\r', '\n');
	}

	private static List<ContentVariant> CreateVariants() =>
	[
		ContentVariant.FromRaw(string.Empty),
		ContentVariant.FromRaw("  \t  "),
		ContentVariant.FromRaw("alpha"),
		ContentVariant.FromRaw("line-1\nline-2"),
		ContentVariant.FromRaw("line-1\r\nline-2\r\n"),
		ContentVariant.FromRaw("line-1\nline-2\n"),
		ContentVariant.FromRaw("line-1\rline-2\r"),
		ContentVariant.FromRaw("line-1\r\nline-2\rline-3\n"),
		ContentVariant.FromRaw("Привет\nмир"),
		ContentVariant.Estimated()
	];

	private sealed record ContentVariant(
		long SizeBytes,
		int LineCount,
		int CharCount,
		bool IsEmpty,
		bool IsWhitespaceOnly,
		bool IsEstimated,
		int CrLfPairCount,
		int TrailingNewlineChars,
		int TrailingNewlineLineBreaks,
		string RenderedText)
	{
		public ContentFileMetrics ToMetrics(string path) =>
			new(
				Path: path,
				SizeBytes: SizeBytes,
				LineCount: LineCount,
				CharCount: CharCount,
				IsEmpty: IsEmpty,
				IsWhitespaceOnly: IsWhitespaceOnly,
				IsEstimated: IsEstimated,
				CrLfPairCount: CrLfPairCount,
				TrailingNewlineChars: TrailingNewlineChars,
				TrailingNewlineLineBreaks: TrailingNewlineLineBreaks);

		public static ContentVariant FromRaw(string rawText)
		{
			var isEmpty = rawText.Length == 0;
			var isWhitespaceOnly = rawText.Length > 0 && string.IsNullOrWhiteSpace(rawText);
			var lineCount = rawText.Length == 0 ? 0 : 1 + CountLineBreaks(rawText);
			var crLfPairs = CountCrLfPairs(rawText);
			var trailing = CountTrailingNewlineInfo(rawText);
			var rendered = isEmpty || isWhitespaceOnly
				? string.Empty
				: rawText.TrimEnd('\r', '\n');

			return new ContentVariant(
				SizeBytes: Encoding.UTF8.GetByteCount(rawText),
				LineCount: lineCount,
				CharCount: rawText.Length,
				IsEmpty: isEmpty,
				IsWhitespaceOnly: isWhitespaceOnly,
				IsEstimated: false,
				CrLfPairCount: crLfPairs,
				TrailingNewlineChars: trailing.Chars,
				TrailingNewlineLineBreaks: trailing.LineBreaks,
				RenderedText: rendered);
		}

		public static ContentVariant Estimated() =>
			new(
				SizeBytes: 32_000_000,
				LineCount: 180_000,
				CharCount: 32_000_000,
				IsEmpty: false,
				IsWhitespaceOnly: false,
				IsEstimated: true,
				CrLfPairCount: 0,
				TrailingNewlineChars: 0,
				TrailingNewlineLineBreaks: 0,
				RenderedText: string.Empty);

		private static int CountCrLfPairs(string text)
		{
			var count = 0;
			for (var i = 0; i < text.Length - 1; i++)
			{
				if (text[i] == '\r' && text[i + 1] == '\n')
					count++;
			}

			return count;
		}

		private static (int Chars, int LineBreaks) CountTrailingNewlineInfo(string text)
		{
			var start = text.Length;
			while (start > 0 && text[start - 1] is '\r' or '\n')
				start--;

			return (text.Length - start, CountLineBreaks(text[start..]));
		}

		private static int CountLineBreaks(string text)
		{
			var count = 0;
			for (var index = 0; index < text.Length; index++)
			{
				if (text[index] == '\r')
				{
					count++;
					if (index + 1 < text.Length && text[index + 1] == '\n')
						index++;
				}
				else if (text[index] == '\n')
				{
					count++;
				}
			}

			return count;
		}
	}
}
