namespace DevProjex.Application.Services;

public static class ExportOutputMetricsCalculator
{
	private const string ClipboardBlankLine = "\u00A0";
	private const string NoContentMarker = "[No Content, 0 bytes]";
	private const string WhitespaceMarkerPrefix = "[Whitespace, ";
	private const string WhitespaceMarkerSuffix = " bytes]";
	private static readonly System.Buffers.SearchValues<char> LineBreakCharacters =
		System.Buffers.SearchValues.Create("\r\n");

	public static ExportOutputMetrics FromText(string text)
	{
		if (string.IsNullOrEmpty(text))
			return ExportOutputMetrics.Empty;

		var stats = GetNormalizedTextStats(text.AsSpan());
		int chars = stats.NormalizedChars;
		int lines = stats.LineBreaks + 1;
		int tokens = EstimateTokens(chars);

		return new ExportOutputMetrics(lines, chars, tokens);
	}

	public static ExportOutputMetrics FromContentFiles(IEnumerable<ContentFileMetrics> files)
	{
		var uniquePaths = new HashSet<string>(PathComparer.Default);
		var ordered = new List<ContentFileMetrics>();
		foreach (var file in files)
		{
			if (string.IsNullOrWhiteSpace(file.Path))
				continue;

			if (!uniquePaths.Add(file.Path))
				continue;

			ordered.Add(file);
		}

		if (ordered.Count == 0)
			return ExportOutputMetrics.Empty;

		ordered.Sort(static (left, right) => PathComparer.Default.Compare(left.Path, right.Path));
		return FromOrderedContentFiles(ordered);
	}

	/// <summary>
	/// Calculates content metrics for an already de-duplicated, path-ordered sequence.
	/// This avoids extra HashSet/List allocations on hot status-bar recalculations.
	/// </summary>
	public static ExportOutputMetrics FromOrderedContentFiles(IReadOnlyList<ContentFileMetrics> ordered)
	{
		if (ordered.Count == 0)
			return ExportOutputMetrics.Empty;

		var accumulator = new OrderedContentMetricsAccumulator();
		foreach (var file in ordered)
			accumulator.AppendFile(file);

		return accumulator.ToMetrics();
	}

	/// <summary>
	/// Accumulates clipboard-style content metrics without forcing callers to build
	/// a temporary <see cref="List{T}"/> for large status-bar recalculations.
	/// </summary>
	public struct OrderedContentMetricsAccumulator
	{
		private const int NormalizedNewLineChars = 1;

		private int _chars;
		private int _lineBreaks;
		private int _trailingLineBreakChars;
		private int _trailingLineBreaks;
		private bool _anyWritten;

		public void AppendFile(ContentFileMetrics file)
		{
			if (string.IsNullOrWhiteSpace(file.Path))
				return;

			if (_anyWritten)
			{
				AppendLiteralLine(
					ClipboardBlankLine,
					NormalizedNewLineChars,
					ref _chars,
					ref _lineBreaks,
					ref _trailingLineBreakChars,
					ref _trailingLineBreaks);
				AppendLiteralLine(
					ClipboardBlankLine,
					NormalizedNewLineChars,
					ref _chars,
					ref _lineBreaks,
					ref _trailingLineBreakChars,
					ref _trailingLineBreaks);
			}

			_anyWritten = true;

			AppendLiteralLine(
				$"{file.Path}:",
				NormalizedNewLineChars,
				ref _chars,
				ref _lineBreaks,
				ref _trailingLineBreakChars,
				ref _trailingLineBreaks);
			AppendLiteralLine(
				ClipboardBlankLine,
				NormalizedNewLineChars,
				ref _chars,
				ref _lineBreaks,
				ref _trailingLineBreakChars,
				ref _trailingLineBreaks);

			if (file.IsEmpty)
			{
				AppendLiteralLine(
					NoContentMarker,
					NormalizedNewLineChars,
					ref _chars,
					ref _lineBreaks,
					ref _trailingLineBreakChars,
					ref _trailingLineBreaks);
				return;
			}

			if (file.IsWhitespaceOnly)
			{
				AppendLiteralLine(
					$"{WhitespaceMarkerPrefix}{file.SizeBytes}{WhitespaceMarkerSuffix}",
					NormalizedNewLineChars,
					ref _chars,
					ref _lineBreaks,
					ref _trailingLineBreakChars,
					ref _trailingLineBreaks);
				return;
			}

			if (file.IsEstimated)
			{
				// Estimated files intentionally keep an empty content line in the rendered export.
				AppendRenderedLine(
					renderedChars: 0,
					internalLineBreaks: 0,
					newLineChars: NormalizedNewLineChars,
					chars: ref _chars,
					lineBreaks: ref _lineBreaks,
					trailingLineBreakChars: ref _trailingLineBreakChars,
					trailingLineBreaks: ref _trailingLineBreaks);
				return;
			}

			var internalLineBreaks = Math.Max(0, file.LineCount - 1);
			var trimmedLineBreaks = Math.Max(0, internalLineBreaks - file.TrailingNewlineLineBreaks);
			var normalizedChars = Math.Max(0, file.CharCount - file.CrLfPairCount);
			var trimmedChars = Math.Max(0, normalizedChars - file.TrailingNewlineLineBreaks);

			AppendRenderedLine(
				renderedChars: trimmedChars,
				internalLineBreaks: trimmedLineBreaks,
				newLineChars: NormalizedNewLineChars,
				chars: ref _chars,
				lineBreaks: ref _lineBreaks,
				trailingLineBreakChars: ref _trailingLineBreakChars,
				trailingLineBreaks: ref _trailingLineBreaks);
		}

		public ExportOutputMetrics ToMetrics()
		{
			var chars = Math.Max(0, _chars - _trailingLineBreakChars);
			var lineBreaks = Math.Max(0, _lineBreaks - _trailingLineBreaks);

			if (chars == 0)
				return ExportOutputMetrics.Empty;

			var lines = lineBreaks + 1;
			var tokens = EstimateTokens(chars);
			return new ExportOutputMetrics(lines, chars, tokens);
		}
	}

	private static void AppendLiteralLine(
		string text,
		int newLineChars,
		ref int chars,
		ref int lineBreaks,
		ref int trailingLineBreakChars,
		ref int trailingLineBreaks)
	{
		AppendRenderedLine(
			renderedChars: text.Length,
			internalLineBreaks: 0,
			newLineChars: newLineChars,
			chars: ref chars,
			lineBreaks: ref lineBreaks,
			trailingLineBreakChars: ref trailingLineBreakChars,
			trailingLineBreaks: ref trailingLineBreaks);
	}

	private static void AppendRenderedLine(
		int renderedChars,
		int internalLineBreaks,
		int newLineChars,
		ref int chars,
		ref int lineBreaks,
		ref int trailingLineBreakChars,
		ref int trailingLineBreaks)
	{
		chars += renderedChars + newLineChars;
		lineBreaks += internalLineBreaks + 1;

		if (renderedChars == 0 && internalLineBreaks == 0)
		{
			trailingLineBreakChars += newLineChars;
			trailingLineBreaks++;
			return;
		}

		trailingLineBreakChars = newLineChars;
		trailingLineBreaks = 1;
	}

	private static int EstimateTokens(int chars) =>
		chars <= 0 ? 0 : (chars + 3) / 4;

	private static NormalizedTextStats GetNormalizedTextStats(ReadOnlySpan<char> text)
	{
		var normalizedChars = 0;
		var lineBreaks = 0;
		var index = 0;

		// Scan non-line-break segments in bulk to reduce per-char branching on hot paths.
		while (index < text.Length)
		{
			var remaining = text[index..];
			var breakOffset = remaining.IndexOfAny(LineBreakCharacters);
			if (breakOffset < 0)
			{
				normalizedChars += remaining.Length;
				break;
			}

			normalizedChars += breakOffset + 1;
			index += breakOffset;

			if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
			{
				// CRLF contributes a single normalized line-break character.
				index++;
			}

			lineBreaks++;
			index++;
		}

		return new NormalizedTextStats(normalizedChars, lineBreaks);
	}

	private readonly record struct NormalizedTextStats(int NormalizedChars, int LineBreaks);
}

public readonly record struct ContentFileMetrics(
	string Path,
	long SizeBytes,
	int LineCount,
	int CharCount,
	bool IsEmpty,
	bool IsWhitespaceOnly,
	bool IsEstimated = false,
	int CrLfPairCount = 0,
	int TrailingNewlineChars = 0,
	int TrailingNewlineLineBreaks = 0);

public readonly record struct ExportOutputMetrics(int Lines, int Chars, int Tokens)
{
	public static ExportOutputMetrics Empty { get; } = new(0, 0, 0);
}
