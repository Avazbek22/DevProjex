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
        return NormalizeLineEndingsForClipboard(payload);
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

		return NormalizeLineEndingsForClipboard(selectedText);
    }

    private static string NormalizeLineEndingsForClipboard(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        return Environment.NewLine == "\n"
            ? normalized
            : normalized.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
    }
}
