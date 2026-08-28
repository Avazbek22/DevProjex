namespace DevProjex.Terminal.Tui;

internal enum TerminalWorkspacePane
{
	Tree,
	Preview,
	Controls
}

internal enum TerminalControlSection
{
	Content,
	Exclusions,
	Extensions
}

internal readonly record struct WorkspaceFocusSnapshot(
	TerminalWorkspacePane Pane,
	TerminalControlSection ControlSection,
	TerminalControlSection? AggregateSection);

internal sealed class WorkspaceFocusModel
{
	private WorkspaceFocusSnapshot? _beforeBusy;

	public TerminalWorkspacePane Pane { get; set; } = TerminalWorkspacePane.Tree;
	public TerminalControlSection ControlSection { get; set; } = TerminalControlSection.Content;
	public TerminalControlSection? AggregateSection { get; set; }
	public TerminalWorkspacePane CommandReturnPane { get; set; } = TerminalWorkspacePane.Tree;

	public WorkspaceFocusSnapshot Capture() => new(Pane, ControlSection, AggregateSection);

	public void Restore(WorkspaceFocusSnapshot snapshot)
	{
		Pane = snapshot.Pane;
		ControlSection = snapshot.ControlSection;
		AggregateSection = snapshot.AggregateSection;
	}

	public void SaveBeforeBusy() => _beforeBusy ??= Capture();

	public WorkspaceFocusSnapshot RestoreAfterBusy()
	{
		var snapshot = _beforeBusy ?? Capture();
		Restore(snapshot);
		_beforeBusy = null;
		return snapshot;
	}
}
