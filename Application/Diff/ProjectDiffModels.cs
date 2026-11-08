using DevProjex.Application.Secrets;

namespace DevProjex.Application.Diff;

public enum ProjectDiffFileStatus
{
	Added,
	Modified,
	Deleted,
	Renamed
}

/// <summary>Why a changed file is listed without hunks.</summary>
public enum ProjectDiffOmission
{
	None,

	/// <summary>A version is binary, not a regular file, not decodable text, or cannot be looked up safely.</summary>
	Binary,

	/// <summary>A version exceeds the read or line limit.</summary>
	TooLarge,

	/// <summary>Both versions fit, but the change exceeds the edit-distance limit of the line diff.</summary>
	ChangeTooLarge,

	/// <summary>No line differs once line endings and the byte-order mark are normalized.</summary>
	LineEndingsOnly,

	/// <summary>The bytes are identical on both sides; Git reported the file as changed only because its mode changed (for example an executable bit flipped by <c>chmod</c>).</summary>
	ContentUnchanged
}

/// <param name="RelativePath">Project-relative, '/'-separated.</param>
/// <param name="OldRelativePath">The rename source, only for <see cref="ProjectDiffFileStatus.Renamed"/>.</param>
public sealed record ProjectDiffFile(
	string FullPath,
	string RelativePath,
	string? OldRelativePath,
	ProjectDiffFileStatus Status,
	ProjectDiffOmission Omission,
	int Added,
	int Removed,
	IReadOnlyList<UnifiedDiffHunk> Hunks)
{
	public int ChangedLines => Added + Removed;
}

/// <param name="Files">Largest change first, then project-relative path in ordinal order.</param>
/// <param name="DeletedOutsideSelection">
/// Selected files the scope deletes that are no longer in the working tree. Only the selection
/// rules a path alone decides are applied to them, and only this count is reported.
/// </param>
/// <param name="NotInWorkingTree">
/// Selected files a staged or ref-to-ref scope adds, changes or renames to that the working tree
/// does not have, counted by the same path rules; their versions are not read.
/// </param>
public sealed record ProjectDiffResult(
	GitFilteringMode Mode,
	string? DiffRange,
	IReadOnlyList<ProjectDiffFile> Files,
	int DeletedOutsideSelection,
	SecretRedactionSnapshot? Protection,
	int NotInWorkingTree = 0)
{
	public int Added => Files.Sum(static file => file.Added);
	public int Removed => Files.Sum(static file => file.Removed);
	public int Count(ProjectDiffOmission omission) => Files.Count(file => file.Omission == omission);
	public int ShownCount => Count(ProjectDiffOmission.None);
	public bool HasChanges => Files.Count > 0 || DeletedOutsideSelection > 0 || NotInWorkingTree > 0;
}

/// <param name="Plan">The effective selection of a call with a momentary Git scope; only its files are read.</param>
/// <param name="DiffRange">The range to read for a ref-to-ref scope; a remote source passes its resolved commit range.</param>
/// <param name="Redaction">The protection every file read applies; null only when the caller turned protection off.</param>
/// <param name="ReadWorkingFile">The guarded read the caller uses for file content, for the new side of the changes scope.</param>
public sealed record ProjectDiffRequest(
	ProjectContextPlan Plan,
	GitFilteringMode Mode,
	string? DiffRange,
	int ContextLines,
	SecretRedactionContext? Redaction,
	Func<string, CancellationToken, ValueTask<FileContentReadResult>> ReadWorkingFile);

public sealed class ProjectDiffException(GitFileVersionFailure failure, string message) : Exception(message)
{
	public GitFileVersionFailure Failure { get; } = failure;
}
