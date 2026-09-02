namespace DevProjex.Kernel.Models;

/// <summary>
/// Describes a repository that can be opened from the persistent local cache.
/// </summary>
public sealed record RepositoryCacheCatalogEntry(
	string RepositoryUrl,
	string RepositoryName,
	string? Branch,
	DateTimeOffset LastOpenedUtc,
	long ApproximateSizeBytes,
	RepositoryCacheContentKind ContentKind,
	string LocalPath,
	string? CommitHash = null,
	RepositoryCacheEntryState State = RepositoryCacheEntryState.Ready);
