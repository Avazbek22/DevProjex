namespace DevProjex.Application.Diff;

/// <summary>
/// One version of a file as the line diff compares it: split on '\n', with a trailing '\r'
/// removed from every line and a leading byte-order mark removed from the first. A committed
/// blob is often LF while the working file is CRLF, so comparing raw lines would report every
/// line as changed.
/// </summary>
public sealed class DiffTextLines
{
	private const char ByteOrderMark = '﻿';

	private DiffTextLines(IReadOnlyList<string> lines, bool hasFinalNewline)
	{
		Lines = lines;
		HasFinalNewline = hasFinalNewline;
	}

	public static DiffTextLines Empty { get; } = new([], hasFinalNewline: true);

	public IReadOnlyList<string> Lines { get; }

	/// <summary>
	/// False when the last line is not terminated by '\n'. An empty text has no last line and
	/// counts as terminated.
	/// </summary>
	public bool HasFinalNewline { get; }

	public int Count => Lines.Count;

	public static DiffTextLines Split(string text, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(text);
		var start = text.Length > 0 && text[0] == ByteOrderMark ? 1 : 0;
		if (start == text.Length)
			return Empty;

		var lines = new List<string>();
		while (start < text.Length)
		{
			if ((lines.Count & 1023) == 0)
				cancellationToken.ThrowIfCancellationRequested();
			var end = text.IndexOf('\n', start);
			if (end < 0)
			{
				lines.Add(TrimCarriageReturn(text, start, text.Length));
				return new DiffTextLines(lines, hasFinalNewline: false);
			}
			lines.Add(TrimCarriageReturn(text, start, end));
			start = end + 1;
		}
		return new DiffTextLines(lines, hasFinalNewline: true);
	}

	/// <summary>
	/// Counts lines without materializing them, so an oversized version can be refused before
	/// it is split.
	/// </summary>
	public static int CountLines(string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		var start = text.Length > 0 && text[0] == ByteOrderMark ? 1 : 0;
		if (start == text.Length)
			return 0;
		var count = text.AsSpan(start).Count('\n');
		return text[^1] == '\n' ? count : count + 1;
	}

	/// <summary>Whether the line at <paramref name="index"/> is the last one and has no terminator.</summary>
	public bool IsUnterminated(int index) => !HasFinalNewline && index == Lines.Count - 1;

	private static string TrimCarriageReturn(string text, int start, int end)
	{
		if (end > start && text[end - 1] == '\r')
			end--;
		return text.Substring(start, end - start);
	}
}
