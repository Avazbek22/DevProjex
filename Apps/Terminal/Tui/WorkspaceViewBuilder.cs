using System.Collections.ObjectModel;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DevProjex.Terminal.Tui;

#pragma warning disable CS0618

internal sealed record WelcomeViewGraph(
	ObservableCollection<TerminalWelcomeActionRow> Rows,
	ListView List,
	TextView Detail,
	FrameView ActionsFrame,
	FrameView DetailFrame,
	Label ActionsHeading,
	Label DetailHeading,
	Label Heading,
	Label Version,
	Label Tagline,
	Label CurrentTitle,
	Label CurrentPath,
	Label CurrentStatus,
	Label QuickStart,
	Label Footer,
	TerminalWorkspaceCommandLineView CommandLine,
	Label TooSmall);

internal sealed record LoadingViewGraph(Label TooSmall);

internal sealed record WorkspaceControlViewGraph(
	FrameView Frame,
	Label PanelHeading,
	Label CollapsedSummary,
	FrameView ContentFrame,
	FrameView ExclusionFrame,
	FrameView ExtensionFrame,
	View FilterHost,
	TerminalParameterListView ContentList,
	TerminalAggregateControl ContentAll,
	TerminalAggregateControl ExclusionAll,
	TerminalParameterListView ExclusionList,
	TerminalAggregateControl ExtensionAll,
	TerminalParameterListView ExtensionList)
{
	public ResettableObservableCollection<TerminalParameterRow> ContentRows { get; set; } = [];
	public ObservableCollection<TerminalParameterRow> ContentAllRows { get; set; } = [];
	public ObservableCollection<TerminalParameterRow> ExclusionAllRows { get; set; } = [];
	public ResettableObservableCollection<TerminalParameterRow> ExclusionRows { get; set; } = [];
	public ObservableCollection<TerminalParameterRow> ExtensionAllRows { get; set; } = [];
	public ResettableObservableCollection<TerminalParameterRow> ExtensionRows { get; set; } = [];
}

internal sealed record WorkspaceViewGraph(
	TerminalProjectTreeView Tree,
	TerminalVirtualizedPreviewView Preview,
	FrameView TreeFrame,
	FrameView PreviewFrame,
	Label TreeHeading,
	Label TreeEmptyHint,
	Label PreviewHeading,
	Label PreviewRange,
	Label WorkspaceHeading,
	Label WorkspacePath,
	TerminalCornerProgressView CornerProgress,
	WorkspaceControlViewGraph Controls,
	Label Status,
	Label Footer,
	TerminalWorkspaceCommandLineView CommandLine,
	Label TooSmall);

internal sealed class WorkspaceViewBuilder
{
	private TerminalProjectTreeView? _tree;
	private TerminalVirtualizedPreviewView? _preview;
	private FrameView? _treeFrame;
	private FrameView? _previewFrame;
	private Label? _treeHeading;
	private Label? _treeEmptyHint;
	private Label? _previewHeading;
	private Label? _previewRange;
	private Label? _workspaceHeading;
	private Label? _workspacePath;
	private TerminalCornerProgressView? _cornerProgress;
	private WorkspaceControlViewGraph? _controls;
	private Label? _status;
	private Label? _footer;
	private TerminalWorkspaceCommandLineView? _commandLine;
	private Label? _tooSmall;

	public WorkspaceViewBuilder WithHeader(
		Label heading,
		Label path,
		TerminalCornerProgressView cornerProgress)
	{
		_workspaceHeading = heading;
		_workspacePath = path;
		_cornerProgress = cornerProgress;
		return this;
	}

	public WorkspaceViewBuilder WithTree(
		TerminalProjectTreeView tree,
		FrameView frame,
		Label heading,
		Label emptyHint)
	{
		_tree = tree;
		_treeFrame = frame;
		_treeHeading = heading;
		_treeEmptyHint = emptyHint;
		return this;
	}

	public WorkspaceViewBuilder WithPreview(
		TerminalVirtualizedPreviewView preview,
		FrameView frame,
		Label heading,
		Label range)
	{
		_preview = preview;
		_previewFrame = frame;
		_previewHeading = heading;
		_previewRange = range;
		return this;
	}

	public WorkspaceViewBuilder WithControls(WorkspaceControlViewGraph controls)
	{
		_controls = controls;
		return this;
	}

	public WorkspaceViewBuilder WithChrome(
		Label status,
		Label footer,
		TerminalWorkspaceCommandLineView commandLine,
		Label tooSmall)
	{
		_status = status;
		_footer = footer;
		_commandLine = commandLine;
		_tooSmall = tooSmall;
		return this;
	}

	public WorkspaceViewGraph Build() => new(
		_tree ?? throw Missing(nameof(_tree)),
		_preview ?? throw Missing(nameof(_preview)),
		_treeFrame ?? throw Missing(nameof(_treeFrame)),
		_previewFrame ?? throw Missing(nameof(_previewFrame)),
		_treeHeading ?? throw Missing(nameof(_treeHeading)),
		_treeEmptyHint ?? throw Missing(nameof(_treeEmptyHint)),
		_previewHeading ?? throw Missing(nameof(_previewHeading)),
		_previewRange ?? throw Missing(nameof(_previewRange)),
		_workspaceHeading ?? throw Missing(nameof(_workspaceHeading)),
		_workspacePath ?? throw Missing(nameof(_workspacePath)),
		_cornerProgress ?? throw Missing(nameof(_cornerProgress)),
		_controls ?? throw Missing(nameof(_controls)),
		_status ?? throw Missing(nameof(_status)),
		_footer ?? throw Missing(nameof(_footer)),
		_commandLine ?? throw Missing(nameof(_commandLine)),
		_tooSmall ?? throw Missing(nameof(_tooSmall)));

	private static InvalidOperationException Missing(string member) =>
		new($"Workspace view '{member}' has not been configured.");
}

#pragma warning restore CS0618
