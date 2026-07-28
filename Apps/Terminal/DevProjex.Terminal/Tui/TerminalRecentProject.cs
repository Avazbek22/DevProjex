namespace DevProjex.Terminal.Tui;

internal sealed record TerminalRecentProject(
	string Path,
	DateTimeOffset OpenedUtc,
	bool IsAvailable,
	bool HasLocalProfile)
{
	public string Name
	{
		get
		{
			var trimmed = System.IO.Path.TrimEndingDirectorySeparator(Path);
			return System.IO.Path.GetFileName(trimmed) is { Length: > 0 } name
				? name
				: Path;
		}
	}
}

internal sealed class TerminalRecentProjectRow(TerminalRecentProject project)
{
	public TerminalRecentProject Project { get; } = project;
	public bool IsSelected { get; set; }

	public override string ToString()
	{
		var availability = Project.IsAvailable ? "[+]" : "[!]";
		var profile = Project.HasLocalProfile ? " *" : string.Empty;
		return $"{(IsSelected ? ">" : " ")} {availability} {Project.Name}{profile}";
	}
}

internal enum TerminalRecentProjectDecisionKind
{
	Back,
	Open,
	Remove
}

internal sealed record TerminalRecentProjectDecision(
	TerminalRecentProjectDecisionKind Kind,
	TerminalRecentProject? Project = null);
