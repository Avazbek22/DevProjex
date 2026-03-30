using DevProjex.Application.Preview;

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
        return NormalizeLineEndingsForClipboard(document.GetLineRangeText(firstLine, lastLine));
    }

    private static string NormalizeLineEndingsForClipboard(string text)
    {
        if (string.IsNullOrEmpty(text) || Environment.NewLine == "\n")
            return text;

        return text.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
    }
}
