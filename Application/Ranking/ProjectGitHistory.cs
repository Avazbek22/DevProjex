namespace DevProjex.Application.Ranking;

public enum ProjectGitHistoryUnavailableReason
{
	None,
	GitUnavailable,
	NotRepository,
	NestedRepository,
	OldGitPromisorRepository,
	ProcessFailed,
	OutputLimitExceeded,
	InvalidOutput
}

public sealed record ProjectGitFileActivity(int CommitCount, int MostRecentCommitPosition);

public sealed record ProjectGitHistorySnapshot(
	int WindowSize,
	int CommitCount,
	IReadOnlyDictionary<string, ProjectGitFileActivity> Files,
	IReadOnlyDictionary<string, ProjectGitHistoryUnavailableReason> UnavailableFiles,
	ProjectGitHistoryUnavailableReason UnavailableReason = ProjectGitHistoryUnavailableReason.None,
	string? Detail = null)
{
	public bool IsAvailable => UnavailableReason == ProjectGitHistoryUnavailableReason.None;

	public bool IsShallow { get; init; }

	public bool IsComplete { get; init; } = true;
}

public interface IProjectGitHistoryReader
{
	Task<ProjectGitHistorySnapshot> ReadAsync(
		string sourceRoot,
		IReadOnlyList<string> candidateFiles,
		CancellationToken cancellationToken = default);
}
