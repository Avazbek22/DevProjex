namespace DevProjex.Terminal.Tui;

internal enum TerminalWelcomeActionKind
{
	OpenCurrent,
	RecentProjects,
	RecentRepositories,
	BrowseFolder,
	CloneRepository,
	OpenProfile,
	OpenDesktop,
	Help,
	Exit
}

internal sealed record TerminalWelcomeAction(
	TerminalWelcomeActionKind Kind,
	string Title,
	string Description);

internal sealed class TerminalWelcomeActionRow(TerminalWelcomeAction action)
{
	public TerminalWelcomeAction Action { get; } = action;
	public bool IsSelected { get; set; }

	public override string ToString() => $"{(IsSelected ? ">" : " ")} {Action.Title}";
}
