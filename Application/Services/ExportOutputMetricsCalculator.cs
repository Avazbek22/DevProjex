namespace DevProjex.Application.Services;

public static class ExportOutputMetricsCalculator
{
	private const string ClipboardBlankLine = "\u00A0";
	private const string NoContentMarker = "[No Content, 0 bytes]";
	private const string WhitespaceMarkerPrefix = "[Whitespace, ";
	private const string WhitespaceMarkerSuffix = " bytes]";

	public static ExportOutputMetrics FromText(string text)
	{
		if (string.IsNullOrEmpty(text))
			return ExportOutputMetrics.Empty;

		int chars = text.Length;
		int lineBreaks = CountLineBreaks(text.AsSpan());
		int lines = lineBreaks + (EndsWithLineBreak(text) ? 0 : 1);
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

		var newLineChars = Environment.NewLine.Length;
		int chars = 0;
		int lineBreaks = 0;
		bool anyWritten = false;

		foreach (var file in ordered)
		{
			if (anyWritten)
			{
				AppendLine(ClipboardBlankLine, newLineChars, ref chars, ref lineBreaks);
				AppendLine(ClipboardBlankLine, newLineChars, ref chars, ref lineBreaks);
			}

			anyWritten = true;

			AppendLine($"{file.Path}:", newLineChars, ref chars, ref lineBreaks);
			AppendLine(ClipboardBlankLine, newLineChars, ref chars, ref lineBreaks);

			if (file.IsEmpty)
			{
				AppendLine(NoContentMarker, newLineChars, ref chars, ref lineBreaks);
				continue;
			}

			if (file.IsWhitespaceOnly)
			{
				AppendLine($"{WhitespaceMarkerPrefix}{file.SizeBytes}{WhitespaceMarkerSuffix}", newLineChars, ref chars, ref lineBreaks);
				continue;
			}

			int internalLineBreaks = Math.Max(0, file.LineCount - 1);
			int trimmedLineBreaks = Math.Max(0, internalLineBreaks - file.TrailingNewlineLineBreaks);
			int trimmedChars = Math.Max(0, file.CharCount - file.TrailingNewlineChars);

			chars += trimmedChars + newLineChars;
			lineBreaks += trimmedLineBreaks + 1;
		}

		// SelectedContentExportService trims trailing CR/LF from the final result.
		chars = Math.Max(0, chars - newLineChars);
		lineBreaks = Math.Max(0, lineBreaks - 1);

		if (chars == 0)
			return ExportOutputMetrics.Empty;

		int lines = lineBreaks + 1;
		int tokens = EstimateTokens(chars);
		return new ExportOutputMetrics(lines, chars, tokens);
	}

	private static void AppendLine(string text, int newLineChars, ref int chars, ref int lineBreaks)
	{
		chars += text.Length + newLineChars;
		lineBreaks++;
	}

	private static int EstimateTokens(int chars) =>
		(int)Math.Ceiling(chars / 4.0);

	private static int CountLineBreaks(ReadOnlySpan<char> text)
	{
		int count = 0;
		foreach (var c in text)
		{
			if (c == '\n')
				count++;
		}

		return count;
	}

	private static bool EndsWithLineBreak(string text) =>
		text.Length > 0 && (text[^1] == '\n' || text[^1] == '\r');
}

public readonly record struct ContentFileMetrics(
	string Path,
	long SizeBytes,
	int LineCount,
	int CharCount,
	bool IsEmpty,
	bool IsWhitespaceOnly,
	int TrailingNewlineChars,
	int TrailingNewlineLineBreaks);

public readonly record struct ExportOutputMetrics(int Lines, int Chars, int Tokens)
{
	public static ExportOutputMetrics Empty { get; } = new(0, 0, 0);
}
