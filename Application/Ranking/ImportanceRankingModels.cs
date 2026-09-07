namespace DevProjex.Application.Ranking;

public enum ProjectContextRank
{
	Importance
}

public enum ImportanceFileRole
{
	Source,
	TestSource,
	Manifest,
	EntryPoint
}

public sealed record ImportanceRankingEntry(
	string FullPath,
	string Path,
	int Priority,
	double Score,
	int Dependents,
	int Dependencies,
	int? Commits,
	int? MostRecentCommitPosition,
	ImportanceFileRole Role,
	bool HasGraphFacts,
	bool HasGitHistory);

public sealed record ImportanceRankingReport(
	string Algorithm,
	IReadOnlyList<ImportanceRankingEntry> Entries,
	IReadOnlyList<ImportanceRankingEntry> TopEntries,
	int CandidateCount,
	int GraphSupportedSources,
	int GraphExtractionFailures,
	double GraphCoverage,
	int GitWindow,
	int GitCommitCount,
	ProjectGitHistoryUnavailableReason GitUnavailableReason,
	bool RedistributedMissingSignals,
	string GraphVariant);

public interface IImportanceRankingService
{
	Task<ImportanceRankingReport> RankAsync(
		string sourceRoot,
		IReadOnlyList<string> candidateFiles,
		CancellationToken cancellationToken = default);
}
