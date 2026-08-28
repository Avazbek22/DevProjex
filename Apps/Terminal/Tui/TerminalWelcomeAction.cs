namespace DevProjex.Terminal.Tui;

internal enum TerminalWelcomeActionKind
{
	OpenCurrent,
	RecentProject,
	RecentWorkspaces,
	BrowseFolder,
	OpenPortableProfile,
	CloneRepository,
	OpenDesktop,
	Help,
	Exit
}

internal sealed record TerminalWelcomeAction(
	TerminalWelcomeActionKind Kind,
	string Title,
	string Description,
	string? Value = null,
	int? Number = null);

internal sealed class TerminalWelcomeActionRow(TerminalWelcomeAction action)
{
	public TerminalWelcomeAction Action { get; } = action;
	public bool IsSelected { get; set; }

	public override string ToString()
	{
		var number = Action.Number is { } value ? $"[{value}] " : string.Empty;
		return $"{(IsSelected ? ">" : " ")} {number}{Action.Title}";
	}
}
