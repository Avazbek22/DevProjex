using System.Globalization;

namespace DevProjex.Application.Diff;

/// <summary>
/// Writes hunks in the unified format git and GNU diff print: a count of one is omitted from the
/// range, and a line without a final newline is followed by the standard marker line.
/// </summary>
public static class UnifiedDiffFormatter
{
	public const string NoNewlineMarker = "\\ No newline at end of file";

	public static string FormatHeader(UnifiedDiffHunk hunk)
	{
		ArgumentNullException.ThrowIfNull(hunk);
		return $"@@ -{FormatRange(hunk.OldStart, hunk.OldLines)} +{FormatRange(hunk.NewStart, hunk.NewLines)} @@";
	}

	public static string FormatRange(int start, int count) =>
		count == 1
			? start.ToString(CultureInfo.InvariantCulture)
			: string.Create(CultureInfo.InvariantCulture, $"{start},{count}");

	public static char Prefix(UnifiedDiffLineKind kind) => kind switch
	{
		UnifiedDiffLineKind.Context => ' ',
		UnifiedDiffLineKind.Removed => '-',
		UnifiedDiffLineKind.Added => '+',
		_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
	};

	/// <summary>Writes the header and every line, each ended by '\n'.</summary>
	/// <param name="formatLine">Applied to the text of every line after its prefix; null writes the text as is.</param>
	public static void WriteHunk(TextWriter writer, UnifiedDiffHunk hunk, Func<string, string>? formatLine = null)
	{
		ArgumentNullException.ThrowIfNull(writer);
		ArgumentNullException.ThrowIfNull(hunk);
		writer.Write(FormatHeader(hunk));
		writer.Write('\n');
		foreach (var line in hunk.Lines)
		{
			writer.Write(Prefix(line.Kind));
			writer.Write(formatLine is null ? line.Text : formatLine(line.Text));
			writer.Write('\n');
			if (line.IsUnterminated)
			{
				writer.Write(NoNewlineMarker);
				writer.Write('\n');
			}
		}
	}

	public static string FormatHunk(UnifiedDiffHunk hunk)
	{
		using var writer = new StringWriter(CultureInfo.InvariantCulture);
		WriteHunk(writer, hunk);
		return writer.ToString();
	}

	/// <summary>The characters <see cref="WriteHunk"/> writes, without writing them.</summary>
	public static long MeasureHunk(UnifiedDiffHunk hunk)
	{
		ArgumentNullException.ThrowIfNull(hunk);
		long characters = FormatHeader(hunk).Length + 1;
		foreach (var line in hunk.Lines)
		{
			characters += line.Text.Length + 2;
			if (line.IsUnterminated)
				characters += NoNewlineMarker.Length + 1;
		}
		return characters;
	}
}
