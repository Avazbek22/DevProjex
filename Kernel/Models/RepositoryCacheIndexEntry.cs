namespace DevProjex.Kernel.Models;

public enum RepositoryCacheEntryState
{
	Ready,
	Damaged
}

public sealed record RepositoryCacheIndexEntry(
	string Identity,
	string RepositoryUrl,
	string LocalPath,
	string? Branch,
	string? CommitHash,
	DateTimeOffset LastUsedUtc,
	RepositoryCacheEntryState State);
