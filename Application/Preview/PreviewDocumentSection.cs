namespace DevProjex.Application.Preview;

/// <summary>
/// Describes a single file section inside the preview document.
/// The section starts at the header line with the display path and spans
/// all content lines that belong to the same file entry.
/// </summary>
public sealed record PreviewDocumentSection(
    string DisplayPath,
    int StartLine,
    int EndLine,
    int HeaderLine,
    int ContentStartLine,
    PreviewContentCoordinateMap? CoordinateMap = null);

/// <summary>
/// Provides efficient lookups over ordered preview sections without forcing
/// UI code to scan the full file list during scrolling.
/// </summary>
public static class PreviewDocumentSectionLookup
{
    public static PreviewDocumentSection? FindContainingSection(
        IReadOnlyList<PreviewDocumentSection> sections,
        int lineNumber)
    {
        var index = FindContainingSectionIndex(sections, lineNumber);
        return index >= 0 ? sections[index] : null;
    }

    public static PreviewDocumentSection? FindContainingOrNextSection(
        IReadOnlyList<PreviewDocumentSection> sections,
        int lineNumber)
    {
        if (sections.Count == 0)
            return null;

        var containingIndex = FindContainingSectionIndex(sections, lineNumber);
        if (containingIndex >= 0)
            return sections[containingIndex];

        var low = 0;
        var high = sections.Count - 1;
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            if (sections[mid].StartLine < lineNumber)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low < sections.Count ? sections[low] : null;
    }

    public static int FindFirstIntersectingSectionIndex(
        IReadOnlyList<PreviewDocumentSection> sections,
        int firstVisibleLine)
    {
        if (sections.Count == 0)
            return -1;

        var low = 0;
        var high = sections.Count - 1;
        var result = -1;

        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            if (sections[mid].EndLine >= firstVisibleLine)
            {
                result = mid;
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }

        return result;
    }

    private static int FindContainingSectionIndex(
        IReadOnlyList<PreviewDocumentSection> sections,
        int lineNumber)
    {
        var low = 0;
        var high = sections.Count - 1;

        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var section = sections[mid];
            if (lineNumber < section.StartLine)
            {
                high = mid - 1;
                continue;
            }

            if (lineNumber > section.EndLine)
            {
                low = mid + 1;
                continue;
            }

            return mid;
        }

        return -1;
    }
}
