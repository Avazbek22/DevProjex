namespace DevProjex.Application.Preview;

public sealed class InMemoryPreviewTextDocument : IPreviewTextDocument
{
    private readonly string _text;
    private readonly List<int> _lineStarts = [0];
    private readonly int _lineCount;
    private readonly int _maxLineLength;

    public InMemoryPreviewTextDocument(
		string? text,
		IReadOnlyList<PreviewDocumentSection>? sections = null,
		IReadOnlyList<PreviewRedactionSpan>? redactions = null,
		PreviewRedactionSummary? redactionSummary = null)
    {
        _text = text ?? string.Empty;
        Sections = sections is { Count: > 0 } ? sections.ToArray() : Array.Empty<PreviewDocumentSection>();
		Redactions = redactions is { Count: > 0 } ? redactions.ToArray() : Array.Empty<PreviewRedactionSpan>();
		RedactionSummary = redactionSummary;
        (_lineCount, _maxLineLength) = BuildLineMetadata(_text, _lineStarts);
    }

    public int LineCount => _lineCount;

    public int MaxLineLength => _maxLineLength;

    public long CharacterCount => _text.Length;

    public IReadOnlyList<PreviewDocumentSection> Sections { get; }

	public IReadOnlyList<PreviewRedactionSpan> Redactions { get; }

	public PreviewRedactionSummary? RedactionSummary { get; }

    public string GetFullText() => _text;

    public string GetLineText(int lineNumber)
    {
        if (_text.Length == 0)
            return string.Empty;

        var normalizedLine = Math.Clamp(lineNumber, 1, _lineCount) - 1;
        return GetLineSlice(_text, _lineStarts, normalizedLine).ToString();
    }

    public string GetLineRangeText(int firstLine, int lastLine)
    {
        if (_text.Length == 0)
            return string.Empty;

        var normalizedFirstLine = Math.Max(1, firstLine);
        var normalizedLastLine = Math.Min(_lineCount, Math.Max(normalizedFirstLine, lastLine));
        if (normalizedLastLine < normalizedFirstLine)
            return string.Empty;

        var linesCount = normalizedLastLine - normalizedFirstLine + 1;
        var estimatedLineLength = Math.Max(12, Math.Min(_maxLineLength, 256));
        var builder = new StringBuilder(linesCount * (estimatedLineLength + 1));

        for (var lineIndex = normalizedFirstLine - 1; lineIndex <= normalizedLastLine - 1; lineIndex++)
        {
            var line = GetLineSlice(_text, _lineStarts, lineIndex);
            if (!line.IsEmpty)
                builder.Append(line);

            if (lineIndex < normalizedLastLine - 1)
                builder.Append('\n');
        }

        return builder.ToString();
    }

    public void Dispose()
    {
    }

    private static (int LineCount, int MaxLineLength) BuildLineMetadata(string text, List<int> lineStarts)
    {
        if (text.Length == 0)
            return (1, 0);

        var currentLineLength = 0;
        var maxLineLength = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\n')
            {
                if (currentLineLength > maxLineLength)
                    maxLineLength = currentLineLength;

                currentLineLength = 0;
                lineStarts.Add(i + 1);
                continue;
            }

            if (ch != '\r')
                currentLineLength++;
        }

        if (currentLineLength > maxLineLength)
            maxLineLength = currentLineLength;

        return (Math.Max(1, lineStarts.Count), maxLineLength);
    }

    private static ReadOnlySpan<char> GetLineSlice(string text, IReadOnlyList<int> lineStarts, int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= lineStarts.Count)
            return ReadOnlySpan<char>.Empty;

        var start = lineStarts[lineIndex];
        var nextStart = lineIndex + 1 < lineStarts.Count
            ? lineStarts[lineIndex + 1]
            : text.Length;

        var endExclusive = lineIndex + 1 < lineStarts.Count
            ? Math.Max(start, nextStart - 1)
            : nextStart;

        if (endExclusive > start && text[endExclusive - 1] == '\r')
            endExclusive--;

        return endExclusive > start
            ? text.AsSpan(start, endExclusive - start)
            : ReadOnlySpan<char>.Empty;
    }
}
