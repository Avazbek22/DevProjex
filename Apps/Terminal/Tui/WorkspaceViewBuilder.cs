using Terminal.Gui.Views;

namespace DevProjex.Terminal.Tui;

internal sealed record WorkspaceViewGraph(
	TerminalProjectTreeView Tree,
	TerminalVirtualizedPreviewView Preview,
	FrameView TreeFrame,
	FrameView PreviewFrame,
	FrameView ControlsFrame,
	Label Status,
	Label Footer,
	TerminalWorkspaceCommandLineView CommandLine);

internal sealed class WorkspaceViewBuilder
{
	private TerminalProjectTreeView? _tree;
	private TerminalVirtualizedPreviewView? _preview;
	private FrameView? _treeFrame;
	private FrameView? _previewFrame;
	private FrameView? _controlsFrame;
	private Label? _status;
	private Label? _footer;
	private TerminalWorkspaceCommandLineView? _commandLine;

	public WorkspaceViewBuilder WithTree(TerminalProjectTreeView tree, FrameView frame)
	{
		_tree = tree;
		_treeFrame = frame;
		return this;
	}

	public WorkspaceViewBuilder WithPreview(TerminalVirtualizedPreviewView preview, FrameView frame)
	{
		_preview = preview;
		_previewFrame = frame;
		return this;
	}

	public WorkspaceViewBuilder WithControls(FrameView frame)
	{
		_controlsFrame = frame;
		return this;
	}

	public WorkspaceViewBuilder WithChrome(Label status, Label footer, TerminalWorkspaceCommandLineView commandLine)
	{
		_status = status;
		_footer = footer;
		_commandLine = commandLine;
		return this;
	}

	public WorkspaceViewGraph Build() => new(
		_tree ?? throw Missing(nameof(_tree)),
		_preview ?? throw Missing(nameof(_preview)),
		_treeFrame ?? throw Missing(nameof(_treeFrame)),
		_previewFrame ?? throw Missing(nameof(_previewFrame)),
		_controlsFrame ?? throw Missing(nameof(_controlsFrame)),
		_status ?? throw Missing(nameof(_status)),
		_footer ?? throw Missing(nameof(_footer)),
		_commandLine ?? throw Missing(nameof(_commandLine)));

	private static InvalidOperationException Missing(string member) =>
		new($"Workspace view '{member}' has not been configured.");
}
