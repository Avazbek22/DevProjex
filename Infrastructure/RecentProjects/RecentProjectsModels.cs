namespace DevProjex.Infrastructure.RecentProjects;

public sealed class RecentProjectsDb
{
	public int SchemaVersion { get; set; }
	public List<RecentFolderEntry> RecentFolders { get; set; } = [];
	public List<RecentFolderRemovalEntry> RecentFolderRemovals { get; set; } = [];
	public List<RecentRepositoryEntry> RecentRepositories { get; set; } = [];
	public List<RecentRepositoryRemovalEntry> RecentRepositoryRemovals { get; set; } = [];
}

public sealed record RecentFolderEntry
{
	public string Path { get; set; } = string.Empty;
	public DateTimeOffset OpenedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record RecentRepositoryEntry
{
	public string Url { get; set; } = string.Empty;
	public DateTimeOffset OpenedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record RecentFolderRemovalEntry
{
	public string Path { get; set; } = string.Empty;
	public DateTimeOffset RemovedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record RecentRepositoryRemovalEntry
{
	public string Url { get; set; } = string.Empty;
	public DateTimeOffset RemovedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public enum RecentProjectsLoadStatus
{
	Success = 0,
	TemporarilyUnavailable = 1,
	InvalidStorage = 2
}

public sealed record RecentProjectsLoadResult(
	RecentProjectsDb Database,
	RecentProjectsLoadStatus Status);
