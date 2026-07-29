namespace DevProjex.Terminal.Tui;

internal enum TerminalWorkspaceActionKind
{
	Analyze,
	Search,
	PreviewView,
	PreviewFormat,
	OpenControls,
	GitFiltering,
	Exclusions,
	RootFolders,
	FileTypes,
	ExportContext,
	ExportFolder,
	ExportZip,
	SaveProfile,
	OpenDesktop,
	SourceDetails,
	GetUpdates,
	SwitchBranch,
	RecentWorkspaces,
	ReturnToWelcome,
	Help
}

internal sealed record TerminalWorkspaceAction(
	TerminalWorkspaceActionKind Kind,
	string Category,
	string Title,
	string Description,
	string Shortcut,
	string? Value = null);

internal sealed class TerminalWorkspaceActionRow(TerminalWorkspaceAction action)
{
	public TerminalWorkspaceAction Action { get; } = action;

	public override string ToString()
	{
		var shortcut = string.IsNullOrWhiteSpace(Action.Shortcut)
			? "    "
			: $"[{Action.Shortcut}] ";
		var value = string.IsNullOrWhiteSpace(Action.Value)
			? string.Empty
			: $": {Action.Value}";
		return $"{shortcut}{Action.Title}{value}";
	}
}

internal sealed record TerminalPaletteItem(
	string Category,
	string Title,
	string Description,
	string Shortcut,
	string? Value,
	Action Execute);

internal sealed class TerminalPaletteRow(TerminalPaletteItem item)
{
	public TerminalPaletteItem Item { get; } = item;

	public override string ToString()
	{
		var shortcut = string.IsNullOrWhiteSpace(Item.Shortcut)
			? string.Empty
			: $"  [{Item.Shortcut}]";
		var value = string.IsNullOrWhiteSpace(Item.Value)
			? string.Empty
			: $": {Item.Value}";
		return Item.Title + value + shortcut;
	}
}
