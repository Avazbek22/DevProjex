namespace DevProjex.Application.Diff;

/// <summary>
/// Reads the committed and staged versions of selected files from Git's object store. An
/// implementation must never run a program the repository configures: no textconv, no
/// clean/smudge filter, no external diff, and no working-tree comparison.
/// </summary>
public interface IGitFileVersionReader
{
	Task<GitFileVersionSet> ReadAsync(GitFileVersionRequest request, CancellationToken cancellationToken = default);

	/// <summary>
	/// Explains a ref-to-ref scope Git could not read: whether either revision is absent from the
	/// repository that owns <paramref name="projectRoot"/>, and whether that repository is shallow.
	/// </summary>
	Task<GitRevisionAvailability> ProbeRangeAsync(
		string projectRoot,
		string diffRange,
		CancellationToken cancellationToken = default);
}

public enum GitRevisionAvailability
{
	/// <summary>Both revisions resolve, or the repository could not be asked.</summary>
	Unknown,
	Missing,
	MissingInShallowClone
}

/// <param name="Mode">
/// <see cref="GitFilteringMode.Changes"/> reads HEAD only; the new side is the working file.
/// <see cref="GitFilteringMode.Staged"/> reads HEAD and index stage 0.
/// <see cref="GitFilteringMode.Diff"/> reads both commits of <paramref name="DiffRange"/>.
/// </param>
/// <param name="Files">Absolute paths of selected files; nothing outside this list is looked up or reported.</param>
public sealed record GitFileVersionRequest(
	string ProjectRoot,
	GitFilteringMode Mode,
	string? DiffRange,
	IReadOnlyList<string> Files);

public enum GitFileVersionFailure
{
	None,
	Unavailable,
	UnknownRevision
}

public sealed record GitFileVersionSet(
	IReadOnlyDictionary<string, GitFileVersions> Files,
	GitFileVersionFailure Failure = GitFileVersionFailure.None,
	string? FailureReason = null)
{
	public bool IsAvailable => Failure == GitFileVersionFailure.None;

	public static GitFileVersionSet Unavailable(string reason) =>
		new(new Dictionary<string, GitFileVersions>(), GitFileVersionFailure.Unavailable, reason);

	public static GitFileVersionSet UnknownRevision(string reason) =>
		new(new Dictionary<string, GitFileVersions>(), GitFileVersionFailure.UnknownRevision, reason);
}

/// <param name="Old">The old side, or null when the old tree has no entry for the path.</param>
/// <param name="New">For staged and ref-to-ref scopes the new side, or null when it has no entry; unused for changes.</param>
/// <param name="RenamedFrom">The absolute rename source, reported only when that source is itself one of the requested files.</param>
/// <param name="Unsupported">The path cannot be looked up safely, or the index holds only conflict stages for it.</param>
public sealed record GitFileVersions(
	GitStoredFile? Old,
	GitStoredFile? New,
	string? RenamedFrom = null,
	bool Unsupported = false);

public enum GitStoredFileKind
{
	Regular,
	TooLarge,
	NotRegularFile
}

/// <param name="Content">The raw bytes for a regular file within the read limits; null otherwise.</param>
public sealed record GitStoredFile(GitStoredFileKind Kind, long Size, byte[]? Content);
