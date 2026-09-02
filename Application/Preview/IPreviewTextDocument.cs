namespace DevProjex.Application.Preview;

/// <summary>
/// Visits one preview line. The line span must not be retained beyond the callback;
/// returning false stops the traversal.
/// </summary>
public delegate bool PreviewTextLineVisitor(int lineNumber, ReadOnlySpan<char> line);

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

    string GetFullText();

    string GetLineText(int lineNumber);

    string GetLineRangeText(int firstLine, int lastLine);

	/// <summary>
	/// Visits a one-based inclusive line range without requiring the whole document in memory.
	/// The supplied line span is valid only until the visitor returns.
	/// </summary>
	void VisitLines(
		int firstLine,
		int lastLine,
		PreviewTextLineVisitor visitor,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(visitor);
		if (!PreviewTextLineRange.TryNormalize(LineCount, firstLine, lastLine, out var first, out var last))
			return;

		for (var lineNumber = first; lineNumber <= last; lineNumber++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var line = GetLineText(lineNumber);
			if (!visitor(lineNumber, line))
				return;
		}
	}

	ValueTask WriteToAsync(
		Stream destination,
		CancellationToken cancellationToken = default) =>
		PreviewTextStreamWriter.WriteAsync(destination, GetFullText(), cancellationToken);
}

internal static class PreviewTextLineRange
{
	public static bool TryNormalize(
		int lineCount,
		int firstLine,
		int lastLine,
		out int normalizedFirstLine,
		out int normalizedLastLine)
	{
		if (lineCount <= 0 || lastLine < firstLine || lastLine < 1 || firstLine > lineCount)
		{
			normalizedFirstLine = 0;
			normalizedLastLine = 0;
			return false;
		}

		normalizedFirstLine = Math.Max(1, firstLine);
		normalizedLastLine = Math.Min(lineCount, lastLine);
		return normalizedLastLine >= normalizedFirstLine;
	}
}
