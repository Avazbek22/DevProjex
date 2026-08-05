using DevProjex.Application.Secrets;

namespace DevProjex.Application.Services;

public static class PreviewClipboardPayloadBuilder
{
    public static string BuildFullDocumentPayload(IPreviewTextDocument? document)
    {
        if (document is null)
            return string.Empty;

        return NormalizeLineEndingsForClipboard(document.GetLineRangeText(1, document.LineCount));
    }

    public static string BuildSectionPayload(
        IPreviewTextDocument? document,
        PreviewDocumentSection? section)
    {
        if (document is null || section is null)
            return string.Empty;

        // Clamp to the current document bounds so callers can safely reuse
        // stored section metadata while the preview keeps a line-based model.
        var firstLine = Math.Max(1, section.HeaderLine);
        var lastLine = Math.Min(document.LineCount, Math.Max(firstLine, section.EndLine));
        var payload = document.GetLineRangeText(firstLine, lastLine);
        return NormalizeLineEndingsForClipboard(IncludeLegendWhenRequired(
            document,
            firstLine,
            0,
            lastLine,
            int.MaxValue,
            payload));
    }

    public static string BuildSelectionPayload(
        IPreviewTextDocument? document,
        int firstLine,
        int firstColumn,
        int lastLine,
        int lastColumn,
        string selectedText)
    {
        if (document is null || string.IsNullOrEmpty(selectedText))
            return selectedText;

        return NormalizeLineEndingsForClipboard(IncludeLegendWhenRequired(
            document,
            firstLine,
            firstColumn,
            lastLine,
            lastColumn,
            selectedText));
    }

    private static string IncludeLegendWhenRequired(
        IPreviewTextDocument document,
        int firstLine,
        int firstColumn,
        int lastLine,
        int lastColumn,
        string payload)
    {
        var summary = document.RedactionSummary;
        if (summary is null || firstLine <= summary.LegendLineCount)
            return payload;

        var containsRedactedValue = document.Redactions.Any(span =>
            span.State == SecretPreviewSpanState.Redacted &&
            Intersects(span, firstLine, firstColumn, lastLine, lastColumn));
        if (!containsRedactedValue)
            return payload;

        var legend = document.GetLineRangeText(1, summary.LegendLineCount);
        return string.Concat(legend, "\n\n", payload);
    }

    private static bool Intersects(
        PreviewRedactionSpan span,
        int firstLine,
        int firstColumn,
        int lastLine,
        int lastColumn)
    {
        if (span.LineNumber < firstLine || span.LineNumber > lastLine)
            return false;

        var selectedStart = span.LineNumber == firstLine ? firstColumn : 0;
        var selectedEnd = span.LineNumber == lastLine ? lastColumn : int.MaxValue;
        return span.StartColumn < selectedEnd && span.StartColumn + span.Length > selectedStart;
    }

    private static string NormalizeLineEndingsForClipboard(string text)
    {
        if (string.IsNullOrEmpty(text) || Environment.NewLine == "\n")
            return text;

        return text.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
    }
}
