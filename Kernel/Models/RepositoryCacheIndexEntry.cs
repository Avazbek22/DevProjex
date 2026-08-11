namespace DevProjex.Kernel.Models;

public enum RepositoryCacheEntryState
{
	Ready,
	Damaged
}

public enum RepositoryCacheContentKind
{
	Unknown,
	Git,
	Zip
}

public sealed record RepositoryCacheIndexEntry(
	string Identity,
	string RepositoryUrl,
	string LocalPath,
	string? Branch,
	string? CommitHash,
	DateTimeOffset LastUsedUtc,
	RepositoryCacheEntryState State,
	long ApproximateSizeBytes = 0,
	RepositoryCacheContentKind ContentKind = RepositoryCacheContentKind.Unknown)
{
	/// <summary>
	/// Gets the last time a repository session was opened. The persisted property keeps its legacy
	/// name so schema-v1 cache indexes remain readable without rewriting or downloading repositories.
	/// </summary>
	[System.Text.Json.Serialization.JsonIgnore]
	public DateTimeOffset LastOpenedUtc => LastUsedUtc;
}
