namespace DevProjex.Application.Services;

public static class PreviewSelectionMetricsCalculator
{
    public static ExportOutputMetrics Calculate(
        IPreviewTextDocument document,
        PreviewSelectionRange selectionRange,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var normalizedRange = selectionRange.Normalize();
        if (normalizedRange.IsCollapsed)
            return ExportOutputMetrics.Empty;
        cancellationToken.ThrowIfCancellationRequested();

        long totalChars = 0;
        long lineBreaks = 0;

        if (normalizedRange.EndLine <= document.LineCount)
        {
            document.VisitLines(
                normalizedRange.StartLine,
                normalizedRange.EndLine,
                AccumulateLine,
                cancellationToken);
        }
        else if (document is InMemoryPreviewTextDocument or FileBackedPreviewTextDocument)
        {
            if (normalizedRange.StartLine <= document.LineCount)
            {
                document.VisitLines(
                    normalizedRange.StartLine,
                    document.LineCount,
                    AccumulateLine,
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var tail = CalculateClampedTail(
                normalizedRange,
                document.LineCount,
                document.GetLineText(document.LineCount).Length);
            totalChars += tail.Chars;
            lineBreaks += tail.LineBreaks;
        }
        else
        {
            // Custom documents may define their own out-of-range line behavior.
            for (var lineNumber = normalizedRange.StartLine; ; lineNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AccumulateLine(lineNumber, document.GetLineText(lineNumber));
                if (lineNumber == normalizedRange.EndLine)
                    break;
            }
        }

        if (totalChars <= 0)
            return ExportOutputMetrics.Empty;

        return new ExportOutputMetrics(
            Lines: lineBreaks + 1,
            Chars: totalChars,
            Tokens: EstimateTokens(totalChars));

        bool AccumulateLine(int lineNumber, ReadOnlySpan<char> lineText)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var segmentStart = lineNumber == normalizedRange.StartLine
                ? Math.Clamp(normalizedRange.StartColumn, 0, lineText.Length)
                : 0;
            var segmentEnd = lineNumber == normalizedRange.EndLine
                ? Math.Clamp(normalizedRange.EndColumn, segmentStart, lineText.Length)
                : lineText.Length;

            if (segmentEnd > segmentStart)
                totalChars += segmentEnd - segmentStart;

            if (lineNumber < normalizedRange.EndLine)
            {
                totalChars++;
                lineBreaks++;
            }
            return true;
        }
    }

    private static (long Chars, long LineBreaks) CalculateClampedTail(
        PreviewSelectionRange range,
        int lineCount,
        int lastLineLength)
    {
        var firstTailLine = Math.Max((long)range.StartLine, (long)lineCount + 1);
        var tailLineCount = (long)range.EndLine - firstTailLine + 1;
        var firstColumn = firstTailLine == range.StartLine
            ? Math.Clamp(range.StartColumn, 0, lastLineLength)
            : 0;
        var lastColumn = Math.Clamp(
            range.EndColumn,
            tailLineCount == 1 ? firstColumn : 0,
            lastLineLength);
        var lineBreaks = tailLineCount - 1;
        var chars = tailLineCount * lastLineLength - firstColumn -
                    (lastLineLength - lastColumn) + lineBreaks;
        return (chars, lineBreaks);
    }

    private static long EstimateTokens(long chars) =>
        chars <= 0 ? 0 : (chars / 4) + (chars % 4 == 0 ? 0 : 1);
}
