namespace DevProjex.Application.Diff;

public enum LineDiffOutcome
{
	/// <summary>Both versions have the same lines, including the final-newline state.</summary>
	Unchanged,

	/// <summary>The versions differ, but no line does once line endings and the byte-order mark are normalized.</summary>
	LineEndingsOnly,

	Changed,

	/// <summary>A version exceeds the line limit, or the edit script exceeds the edit-distance limit.</summary>
	TooLarge
}

public enum UnifiedDiffLineKind
{
	Context,
	Removed,
	Added
}

/// <param name="IsUnterminated">The line is the last of its version and has no '\n'; unified output follows it with "\ No newline at end of file".</param>
public readonly record struct UnifiedDiffLine(UnifiedDiffLineKind Kind, string Text, bool IsUnterminated = false);

/// <summary>
/// One unified-diff hunk. Starts are 1-based; a side with no lines starts at the line before the
/// hunk, which is 0 at the top of the file, exactly as git and GNU diff print it.
/// </summary>
public sealed record UnifiedDiffHunk(
	int OldStart,
	int OldLines,
	int NewStart,
	int NewLines,
	IReadOnlyList<UnifiedDiffLine> Lines);

public sealed record LineDiffResult(
	LineDiffOutcome Outcome,
	int Added,
	int Removed,
	IReadOnlyList<UnifiedDiffHunk> Hunks)
{
	public static LineDiffResult Unchanged { get; } = new(LineDiffOutcome.Unchanged, 0, 0, []);
	public static LineDiffResult LineEndingsOnly { get; } = new(LineDiffOutcome.LineEndingsOnly, 0, 0, []);
	public static LineDiffResult TooLarge { get; } = new(LineDiffOutcome.TooLarge, 0, 0, []);
}
