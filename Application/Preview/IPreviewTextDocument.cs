namespace DevProjex.Application.Preview;

/// <summary>
/// Provides line-based access to preview text without requiring a single giant in-memory string.
/// </summary>
public interface IPreviewTextDocument : IDisposable
{
    int LineCount { get; }

    int MaxLineLength { get; }

    long CharacterCount { get; }

    IReadOnlyList<PreviewDocumentSection> Sections { get; }

	IReadOnlyList<PreviewRedactionSpan> Redactions => Array.Empty<PreviewRedactionSpan>();

	PreviewRedactionSummary? RedactionSummary => null;

    string GetFullText();

    string GetLineText(int lineNumber);

    string GetLineRangeText(int firstLine, int lastLine);
}
