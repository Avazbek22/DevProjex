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
	bool HasGitHistory,
	ProjectGitHistoryUnavailableReason? GitUnavailableReason = null);

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
	string GraphVariant)
{
	public int ResolvedInternalReferences { get; init; }

	public int InternalReferenceCandidates { get; init; }

	public double ResolvedInternalReferenceCoverage { get; init; }

	public int FilesWithResolvedEdges { get; init; }

	internal IReadOnlyDictionary<string, RankingSourceVersion> SourceVersions { get; init; } =
		new Dictionary<string, RankingSourceVersion>(StringComparer.Ordinal);
}

internal readonly record struct RankingSourceVersion(bool Exists, long Length, long LastWriteTimeUtcTicks)
{
	internal static RankingSourceVersion Capture(string path)
	{
		try
		{
			var file = new FileInfo(path);
			return file.Exists
				? new RankingSourceVersion(true, file.Length, file.LastWriteTimeUtc.Ticks)
				: default;
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			return default;
		}
	}

	internal bool IsCurrent(string path) => Equals(Capture(path));
}

public interface IImportanceRankingService
{
	Task<ImportanceRankingReport> RankAsync(
		string sourceRoot,
		IReadOnlyList<string> candidateFiles,
		CancellationToken cancellationToken = default);
}
