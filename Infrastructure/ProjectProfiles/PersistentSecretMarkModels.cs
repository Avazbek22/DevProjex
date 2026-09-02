namespace DevProjex.Infrastructure.ProjectProfiles;

internal sealed class PersistentSecretMarkDb
{
	public int SchemaVersion { get; set; }
	public Dictionary<string, PersistedProjectSecretMarks> Projects { get; set; } = new(PathComparer.Default);

	[JsonIgnore]
	public HashSet<string> InvalidProjects { get; } = new(PathComparer.Default);
}

internal sealed class PersistedProjectSecretMarks
{
	public long AppliedRevision { get; set; }
	public List<PersistedSecretMarkState> States { get; set; } = [];
}

internal sealed class PersistedSecretMarkState
{
	public string Hash { get; set; } = string.Empty;
	public int Length { get; set; }
	public ManualRedactionClass Class { get; set; }
	public string? Key { get; set; }
	public string? RelativePath { get; set; }
	public int? SourceOffset { get; set; }
	public bool Removed { get; set; }
	public long IssuedUtcTicks { get; set; }
	public Guid OperationId { get; set; }
	public long AppliedRevision { get; set; }
}
