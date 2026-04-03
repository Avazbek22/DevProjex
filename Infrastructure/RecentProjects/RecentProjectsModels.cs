namespace DevProjex.Infrastructure.RecentProjects;

public sealed class RecentProjectsDb
{
	public int SchemaVersion { get; set; }
	public List<RecentFolderEntry> RecentFolders { get; set; } = [];
	public List<RecentRepositoryEntry> RecentRepositories { get; set; } = [];
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
