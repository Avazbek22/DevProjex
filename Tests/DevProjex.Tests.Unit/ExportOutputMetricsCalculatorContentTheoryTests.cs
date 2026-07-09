namespace DevProjex.Tests.Unit;

public sealed class ExportOutputMetricsCalculatorContentTheoryTests
{
	private const string ClipboardBlankLine = "\u00A0";
	private const string NoContentMarker = "[No Content, 0 bytes]";
	private const string WhitespaceMarkerPrefix = "[Whitespace, ";
	private const string WhitespaceMarkerSuffix = " bytes]";

	public static IEnumerable<object[]> ContentCases()
	{
		var variants = GetVariants();
		var caseId = 0;

		// 9 single-file cases
		for (var i = 0; i < variants.Count; i++)
		{
			var entries = new List<(string Path, ContentVariant Variant)>
			{
				($"single-{i}.txt", variants[i])
			};
			yield return BuildCase(caseId++, entries);
		}

		// 81 two-file cases (9 x 9)
		for (var i = 0; i < variants.Count; i++)
		{
			for (var j = 0; j < variants.Count; j++)
			{
				var entries = new List<(string Path, ContentVariant Variant)>
				{
					($"zeta-{i}.txt", variants[i]),
					($"alpha-{j}.txt", variants[j])
				};
				yield return BuildCase(caseId++, entries);
			}
		}

		// 36 three-file cases with duplicate paths to verify first-entry dedup behavior.
		for (var i = 0; i < variants.Count; i++)
		{
			for (var offset = 1; offset <= 4; offset++)
			{
				var mid = variants[(i + offset) % variants.Count];
				var duplicate = variants[(i + offset + 1) % variants.Count];

				var entries = new List<(string Path, ContentVariant Variant)>
				{
					($"dup-{i}.txt", variants[i]),
					($"mid-{offset}-{i}.txt", mid),
					($"dup-{i}.txt", duplicate)
				};
				yield return BuildCase(caseId++, entries);
			}
		}
	}

	[Theory]
	[MemberData(nameof(ContentCases))]
	public void FromContentFiles_MatchesRenderedExportMetrics(
		int caseId,
		IReadOnlyList<ContentFileMetrics> files,
		string expectedExportText)
	{
		var actual = ExportOutputMetricsCalculator.FromContentFiles(files);
		var expected = GetExpectedNormalizedMetrics(expectedExportText);

		Assert.Equal(expected.Lines, actual.Lines);
		Assert.Equal(expected.Chars, actual.Chars);
		Assert.Equal(expected.Tokens, actual.Tokens);
		Assert.True(caseId >= 0);
	}

	[Fact]
	public void FromContentFiles_EstimatedSingleFile_MatchesRenderedClipboardText()
	{
		const string path = "estimated.txt";
		IReadOnlyList<ContentFileMetrics> files =
		[
			new ContentFileMetrics(
				Path: path,
				SizeBytes: 25_000_000,
				LineCount: 150_000,
				CharCount: 25_000_000,
				IsEmpty: false,
				IsWhitespaceOnly: false,
				IsEstimated: true)
		];

		var actual = ExportOutputMetricsCalculator.FromContentFiles(files);
		var expectedText = $"{path}:{Environment.NewLine}{ClipboardBlankLine}";
		var expected = GetExpectedNormalizedMetrics(expectedText);

		Assert.Equal(expected.Lines, actual.Lines);
		Assert.Equal(expected.Chars, actual.Chars);
		Assert.Equal(expected.Tokens, actual.Tokens);
	}

	private static ExportOutputMetrics GetExpectedNormalizedMetrics(string text)
	{
		if (string.IsNullOrEmpty(text))
			return ExportOutputMetrics.Empty;

		var chars = GetExpectedNormalizedCharCount(text);
		var lineBreaks = 0;
		for (var i = 0; i < text.Length; i++)
		{
			var c = text[i];
			if (c == '\r')
			{
				if (i + 1 < text.Length && text[i + 1] == '\n')
					i++;

				lineBreaks++;
				continue;
			}

			if (c == '\n')
				lineBreaks++;
		}

		var lines = lineBreaks + 1;
		var tokens = (int)Math.Ceiling(chars / 4.0);
		return new ExportOutputMetrics(lines, chars, tokens);
	}

	private static int GetExpectedNormalizedCharCount(string text)
	{
		var normalizedChars = 0;
		for (var i = 0; i < text.Length; i++)
		{
			var c = text[i];
			if (c == '\r')
			{
				if (i + 1 < text.Length && text[i + 1] == '\n')
					i++;
			}

			normalizedChars++;
		}

		return normalizedChars;
	}

	private static object[] BuildCase(int caseId, IReadOnlyList<(string Path, ContentVariant Variant)> entries)
	{
		var files = entries
			.Select(entry => new ContentFileMetrics(
				Path: entry.Path,
				SizeBytes: entry.Variant.SizeBytes,
				LineCount: entry.Variant.LineCount,
				CharCount: entry.Variant.CharCount,
				IsEmpty: entry.Variant.IsEmpty,
				IsWhitespaceOnly: entry.Variant.IsWhitespaceOnly,
				IsEstimated: entry.Variant.IsEstimated,
				CrLfPairCount: entry.Variant.CrLfPairCount,
				TrailingNewlineChars: entry.Variant.TrailingNewlineChars,
				TrailingNewlineLineBreaks: entry.Variant.TrailingNewlineLineBreaks))
			.ToList();

		var expectedText = RenderExpectedText(entries);
		return [caseId, files, expectedText];
	}

	private static string RenderExpectedText(IReadOnlyList<(string Path, ContentVariant Variant)> entries)
	{
		var firstByPath = new Dictionary<string, ContentVariant>(PathComparer.Default);
		foreach (var entry in entries)
		{
			if (!firstByPath.ContainsKey(entry.Path))
				firstByPath[entry.Path] = entry.Variant;
		}

		var ordered = firstByPath
			.OrderBy(pair => pair.Key, PathComparer.Default)
			.ToList();

		if (ordered.Count == 0)
			return string.Empty;

		var sb = new StringBuilder();
		var anyWritten = false;
		foreach (var (path, variant) in ordered)
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
			}
			else if (variant.IsWhitespaceOnly)
			{
				sb.AppendLine($"{WhitespaceMarkerPrefix}{variant.SizeBytes}{WhitespaceMarkerSuffix}");
			}
			else
			{
				sb.AppendLine(variant.RenderedText);
			}
		}

		return sb.ToString().TrimEnd('\r', '\n');
	}

	private static List<ContentVariant> GetVariants() =>
	[
		ContentVariant.FromRaw("empty", string.Empty),
		ContentVariant.FromRaw("spaces", "   "),
		ContentVariant.FromRaw("single", "abc"),
		ContentVariant.FromRaw("lf", "a\nb"),
		ContentVariant.FromRaw("lf_trailing", "a\nb\n"),
		ContentVariant.FromRaw("crlf_trailing", "a\r\nb\r\n"),
		ContentVariant.FromRaw("cr", "a\rb"),
		ContentVariant.FromRaw("cr_trailing", "a\rb\r"),
		ContentVariant.FromRaw("newline_only", "\n"),
		ContentVariant.FromRaw("mixed", "x\r\ny\rz\n"),
		ContentVariant.FromEstimated("estimated_large")
	];

	private sealed record ContentVariant(
		string Name,
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
		public static ContentVariant FromRaw(string name, string rawContent)
		{
			var charCount = rawContent.Length;
			var lineCount = rawContent.Length == 0 ? 0 : 1 + CountLineBreaks(rawContent);
			var crLfPairCount = CountCrLfPairs(rawContent);
			var isEmpty = rawContent.Length == 0;
			var isWhitespaceOnly = rawContent.Length > 0 && string.IsNullOrWhiteSpace(rawContent);
			var trailing = CountTrailingNewlineInfo(rawContent);
			var rendered = isEmpty || isWhitespaceOnly
				? string.Empty
				: rawContent.TrimEnd('\r', '\n');

			return new ContentVariant(
				name,
				SizeBytes: charCount,
				LineCount: lineCount,
				CharCount: charCount,
				IsEmpty: isEmpty,
				IsWhitespaceOnly: isWhitespaceOnly,
				IsEstimated: false,
				CrLfPairCount: crLfPairCount,
				TrailingNewlineChars: trailing.Chars,
				TrailingNewlineLineBreaks: trailing.LineBreaks,
				RenderedText: rendered);
		}

		public static ContentVariant FromEstimated(string name)
		{
			return new ContentVariant(
				Name: name,
				SizeBytes: 25_000_000,
				LineCount: 150_000,
				CharCount: 25_000_000,
				IsEmpty: false,
				IsWhitespaceOnly: false,
				IsEstimated: true,
				CrLfPairCount: 0,
				TrailingNewlineChars: 0,
				TrailingNewlineLineBreaks: 0,
				RenderedText: string.Empty);
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
	}
}
