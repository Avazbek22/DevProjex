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

        var totalChars = 0;
        var lineBreaks = 0;

        for (var lineNumber = normalizedRange.StartLine; lineNumber <= normalizedRange.EndLine; lineNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var lineText = document.GetLineText(lineNumber);
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
        }

        if (totalChars <= 0)
            return ExportOutputMetrics.Empty;

        return new ExportOutputMetrics(
            Lines: lineBreaks + 1,
            Chars: totalChars,
            Tokens: EstimateTokens(totalChars));
    }

    private static int EstimateTokens(int chars) =>
        chars <= 0 ? 0 : (chars + 3) / 4;
}
