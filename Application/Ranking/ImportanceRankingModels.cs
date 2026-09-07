using System.Buffers;
using System.Security.Cryptography;
using DevProjex.Application.Diagnostics;
using DevProjex.Application.Dependencies;

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

public enum ImportanceMissingSignalPolicy
{
	Redistribute,
	NeutralFill,
	ConfidenceLimited
}

public enum ImportanceRankingSignal
{
	None,
	Graph,
	Git,
	Role
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
	ProjectGitHistoryUnavailableReason? GitUnavailableReason = null)
{
	public double Confidence { get; init; } = 1;

	public ImportanceRankingSignal MainContribution { get; init; }

	public bool ConfidenceLimited { get; init; }

	public bool IsCoordinator { get; init; }

	public int? Hop { get; init; }

	public int? BaseImportancePriority { get; init; }

	public FocusRankingVia? Via { get; init; }

	public bool IsFocusSeed { get; init; }
}

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

	public bool HasMissingSignals { get; init; }

	public ImportanceMissingSignalPolicy MissingSignalPolicy { get; init; } =
		ImportanceMissingSignalPolicy.ConfidenceLimited;

	public FocusRankingSummary? Focus { get; init; }

	internal IReadOnlyDictionary<string, RankingSourceVersion> SourceVersions { get; init; } =
		new Dictionary<string, RankingSourceVersion>(StringComparer.Ordinal);

	internal DependencyIndexMetrics? DependencyMetrics { get; init; }
}

internal readonly record struct RankingSourceVersion(
	bool Exists,
	long Length,
	long LastWriteTimeUtcTicks,
	string? ContentHash)
{
	internal bool HasMatchingContentHash(ReadOnlySpan<byte> contentHash)
	{
		if (ContentHash is null)
			return false;
		var expectedHash = Convert.FromHexString(ContentHash);
		return CryptographicOperations.FixedTimeEquals(contentHash, expectedHash);
	}

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

	internal static async ValueTask<RankingSourceVersion> CaptureAsync(
		string path,
		CancellationToken cancellationToken,
		Action<long>? chunkRead = null)
	{
		byte[]? buffer = null;
		try
		{
			var file = new FileInfo(path);
			if (!file.Exists)
				return default;
			await using var stream = new FileStream(
				path,
				new FileStreamOptions
				{
					Mode = FileMode.Open,
					Access = FileAccess.Read,
					Share = FileShare.ReadWrite | FileShare.Delete,
					BufferSize = 64 * 1024,
					Options = FileOptions.Asynchronous | FileOptions.SequentialScan
				});
			using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
			ContentPipelineDiagnostics.RecordSourceVersionHashPass();
			buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var read = await stream
					.ReadAsync(buffer.AsMemory(), cancellationToken)
					.ConfigureAwait(false);
				if (read == 0)
					break;
				hash.AppendData(buffer.AsSpan(0, read));
				ContentPipelineDiagnostics.RecordSourceVersionHashBytes(read);
				chunkRead?.Invoke(read);
			}
			file.Refresh();
			return new RankingSourceVersion(
				true,
				file.Length,
				file.LastWriteTimeUtc.Ticks,
				Convert.ToHexString(hash.GetHashAndReset()));
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			return default;
		}
		finally
		{
			if (buffer is not null)
			{
				CryptographicOperations.ZeroMemory(buffer);
				ArrayPool<byte>.Shared.Return(buffer);
			}
		}
	}

	internal bool IsCurrent(string path) => Equals(Capture(path));

	internal async ValueTask<bool> IsCurrentAsync(string path, CancellationToken cancellationToken) =>
		Equals(await CaptureAsync(path, cancellationToken).ConfigureAwait(false));

	internal bool HasMatchingMetadata(string path)
	{
		try
		{
			var file = new FileInfo(path);
			return Exists == file.Exists &&
			       (!Exists ||
			        (Length == file.Length && LastWriteTimeUtcTicks == file.LastWriteTimeUtc.Ticks));
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			return false;
		}
	}
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

	Task<ImportanceRankingReport> RankAsync(
		string sourceRoot,
		IReadOnlyList<string> candidateFiles,
		FocusRankingRequest focus,
		IProgress<ImportanceRankingProgress>? progress = null,
		CancellationToken cancellationToken = default) =>
		throw new NotSupportedException("This ranking service does not support focus ordering.");
}
