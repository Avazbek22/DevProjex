using System.Security.Cryptography;

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

public enum ImportanceRankingStage
{
	IndexingFacts,
	ReadingHistory,
	ComputingPriorities
}

public readonly record struct ImportanceRankingProgress(
	ImportanceRankingStage Stage,
	int Completed,
	int Total);

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

	public bool GitHistoryIsShallow { get; init; }

	public bool GitHistoryIsComplete { get; init; } = true;

	internal IReadOnlyDictionary<string, RankingSourceVersion> SourceVersions { get; init; } =
		new Dictionary<string, RankingSourceVersion>(StringComparer.Ordinal);
}

internal readonly record struct RankingSourceVersion(
	bool Exists,
	long Length,
	long LastWriteTimeUtcTicks,
	string? ContentHash)
{
	internal static RankingSourceVersion Capture(string path)
	{
		try
		{
			var file = new FileInfo(path);
			if (!file.Exists)
				return default;
			using var stream = new FileStream(
				path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete);
			var hash = Convert.ToHexString(SHA256.HashData(stream));
			file.Refresh();
			return new RankingSourceVersion(true, file.Length, file.LastWriteTimeUtc.Ticks, hash);
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

	Task<ImportanceRankingReport> RankAsync(
		string sourceRoot,
		IReadOnlyList<string> candidateFiles,
		IProgress<ImportanceRankingProgress>? progress,
		CancellationToken cancellationToken = default) =>
		RankAsync(sourceRoot, candidateFiles, cancellationToken);
}
