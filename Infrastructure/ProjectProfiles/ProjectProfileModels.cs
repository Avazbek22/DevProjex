namespace DevProjex.Infrastructure.ProjectProfiles;

internal sealed class ProjectProfileDb
{
	public int SchemaVersion { get; set; }
	public Dictionary<string, PersistedProjectProfile> Profiles { get; set; } = new(PathComparer.Default);
}

internal sealed class PersistedProjectProfile
{
	public List<string> SelectedRootFolders { get; set; } = [];
	public List<string> SelectedExtensions { get; set; } = [];
	public List<IgnoreOptionId> SelectedIgnoreOptions { get; set; } = [];
	public Dictionary<string, bool> RootFolderStates { get; set; } = new(PathComparer.Default);
	public Dictionary<string, bool> ExtensionStates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
	public Dictionary<IgnoreOptionId, bool> IgnoreOptionStates { get; set; } = [];
	public List<string> SelectedPaths { get; set; } = [];
	public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
