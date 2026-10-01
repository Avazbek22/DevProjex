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
	public int? Width { get; set; }

	public override string ToString()
	{
		var number = Action.Number is { } value ? $"[{value}] " : string.Empty;
		var prefix = $"{(IsSelected ? ">" : " ")} {number}";
		// Recent projects often share a long parent path, so a shortened path keeps its end.
		var title = Width is { } width &&
					Action is { Kind: TerminalWelcomeActionKind.RecentProject, Value: { } path }
			? TerminalWorkspaceSession.FitPathToWidth(path, width - prefix.Length)
			: Action.Title;
		return prefix + title;
	}
}
