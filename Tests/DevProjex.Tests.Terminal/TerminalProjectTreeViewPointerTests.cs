using System.Collections.ObjectModel;
using System.Drawing;
using Terminal.Gui.Input;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalProjectTreeViewPointerTests
{
	private const int FolderRow = 0;
	private const int FileRow = 1;

	[Theory]
	[InlineData(2)]
	[InlineData(3)]
	[InlineData(4)]
	public void ClickOnTheCheckBoxTogglesSelectionExactlyOnce(int column)
	{
		using var tree = CreateTree(out var toggles, out _);

		Click(tree, column, FileRow);

		Assert.Equal(["pointer"], toggles);
		Assert.Equal(FileRow, tree.SelectedItem);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(5)]
	[InlineData(6)]
	[InlineData(9)]
	public void ClickBesideTheCheckBoxOnlyMovesTheCursor(int column)
	{
		using var tree = CreateTree(out var toggles, out var expansions);

		Click(tree, column, FileRow);

		Assert.Empty(toggles);
		Assert.Equal(0, expansions.Count);
		Assert.Equal(FileRow, tree.SelectedItem);
	}

	[Fact]
	public void ClickOnTheFolderMarkerTogglesExpansionWithoutSelection()
	{
		using var tree = CreateTree(out var toggles, out var expansions);

		Click(tree, column: 0, FolderRow);

		Assert.Empty(toggles);
		Assert.Equal(1, expansions.Count);
	}

	[Fact]
	public void DoubleClickOnAFolderNameTogglesExpansionOnceWithoutSelection()
	{
		using var tree = CreateTree(out var toggles, out var expansions);

		DoubleClick(tree, column: 7, FolderRow);

		Assert.Empty(toggles);
		Assert.Equal(1, expansions.Count);
	}

	[Fact]
	public void DoubleClickOnAFileNameChangesNothing()
	{
		using var tree = CreateTree(out var toggles, out var expansions);

		DoubleClick(tree, column: 7, FileRow);

		Assert.Empty(toggles);
		Assert.Equal(0, expansions.Count);
		Assert.Equal(FileRow, tree.SelectedItem);
	}

	[Fact]
	public void PointerDriftWhileTheButtonIsHeldDoesNotToggleTheCheckBoxAgain()
	{
		using var tree = CreateTree(out var toggles, out _);

		Send(tree, column: 2, FileRow, MouseFlags.LeftButtonPressed);
		Send(tree, column: 3, FileRow, MouseFlags.LeftButtonPressed | MouseFlags.PositionReport);
		Send(tree, column: 3, FileRow, MouseFlags.LeftButtonReleased);
		Send(tree, column: 3, FileRow, MouseFlags.LeftButtonClicked);

		Assert.Equal(["pointer"], toggles);
	}

	[Fact]
	public void PointerDriftOffAndBackOntoTheFolderMarkerTogglesExpansionOnce()
	{
		using var tree = CreateTree(out var toggles, out var expansions);

		Send(tree, column: 0, FolderRow, MouseFlags.LeftButtonPressed);
		Send(tree, column: 1, FolderRow, MouseFlags.LeftButtonPressed | MouseFlags.PositionReport);
		Send(tree, column: 0, FolderRow, MouseFlags.LeftButtonPressed | MouseFlags.PositionReport);
		Send(tree, column: 0, FolderRow, MouseFlags.LeftButtonReleased);
		Send(tree, column: 0, FolderRow, MouseFlags.LeftButtonClicked);

		Assert.Empty(toggles);
		Assert.Equal(1, expansions.Count);
	}

	[Fact]
	public void DoubleClickOnTheFolderMarkerTogglesExpansionOnce()
	{
		using var tree = CreateTree(out var toggles, out var expansions);

		DoubleClick(tree, column: 0, FolderRow);

		Assert.Empty(toggles);
		Assert.Equal(1, expansions.Count);
	}

	[Fact]
	public void DoubleClickOnAFolderNameThatDriftsBeforeTheSecondReleaseTogglesExpansionOnce()
	{
		using var tree = CreateTree(out var toggles, out var expansions);

		Click(tree, column: 7, FolderRow);
		Send(tree, column: 7, FolderRow, MouseFlags.LeftButtonPressed);
		Send(tree, column: 8, FolderRow, MouseFlags.LeftButtonPressed | MouseFlags.PositionReport);
		Send(tree, column: 8, FolderRow, MouseFlags.LeftButtonReleased);
		Send(tree, column: 8, FolderRow, MouseFlags.LeftButtonDoubleClicked);

		Assert.Empty(toggles);
		Assert.Equal(1, expansions.Count);
	}

	[Fact]
	public void MotionEventsAreNotPrimaryActivations()
	{
		Assert.False(TerminalProjectTreeView.IsPrimaryActivation(
			MouseFlags.LeftButtonPressed | MouseFlags.PositionReport));
		Assert.False(TerminalParameterListView.IsPrimaryActivation(
			MouseFlags.LeftButtonPressed | MouseFlags.PositionReport));
		Assert.True(TerminalProjectTreeView.IsPrimaryActivation(MouseFlags.LeftButtonPressed));
	}

	[Fact]
	public void OnlyARealPressIsAPointerAction()
	{
		Assert.True(TerminalPointerInput.IsPress(MouseFlags.LeftButtonPressed));
		Assert.False(TerminalPointerInput.IsPress(MouseFlags.LeftButtonPressed | MouseFlags.PositionReport));
		Assert.False(TerminalPointerInput.IsPress(MouseFlags.LeftButtonClicked));
		Assert.False(TerminalPointerInput.IsPress(MouseFlags.LeftButtonDoubleClicked));
	}

	[Theory]
	[InlineData(3, FileRow)]
	[InlineData(0, FolderRow)]
	[InlineData(7, FolderRow)]
	public void ReleaseOverTheTreeAfterAPressElsewhereDoesNothing(int column, int row)
	{
		using var tree = CreateTree(out var toggles, out var expansions);

		Send(tree, column, row, MouseFlags.LeftButtonReleased);
		Send(tree, column, row, MouseFlags.LeftButtonClicked);

		Assert.Empty(toggles);
		Assert.Equal(0, expansions.Count);
	}

	[Fact]
	public void SecondHalfOfADoubleClickThatStartedOnAClosedDialogDoesNotExpandAFolder()
	{
		using var tree = CreateTree(out var toggles, out var expansions);

		Send(tree, column: 7, FolderRow, MouseFlags.LeftButtonPressed);
		Send(tree, column: 7, FolderRow, MouseFlags.LeftButtonReleased);
		Send(tree, column: 7, FolderRow, MouseFlags.LeftButtonDoubleClicked);

		Assert.Empty(toggles);
		Assert.Equal(0, expansions.Count);
	}

	[Fact]
	public void ReleaseWithoutAHandledPressDoesNotToggleSelection()
	{
		using var tree = CreateTree(out var toggles, out _);

		Send(tree, column: 3, FileRow, MouseFlags.LeftButtonReleased);
		Send(tree, column: 3, FileRow, MouseFlags.LeftButtonReleased | MouseFlags.Ctrl);

		Assert.Empty(toggles);
	}

	[Fact]
	public void CtrlRightClickDoesNotToggleSelection()
	{
		using var tree = CreateTree(out var toggles, out _);

		Send(tree, column: 3, FileRow, MouseFlags.RightButtonPressed | MouseFlags.Ctrl);
		Send(tree, column: 3, FileRow, MouseFlags.RightButtonReleased | MouseFlags.Ctrl);
		Send(tree, column: 3, FileRow, MouseFlags.RightButtonClicked | MouseFlags.Ctrl);

		Assert.Empty(toggles);
	}

	[Fact]
	public void SpaceStillTogglesTheRowUnderTheCursor()
	{
		using var tree = CreateTree(out var toggles, out _);
		tree.SelectedItem = FileRow;

		tree.NewKeyDownEvent(Key.Space);

		Assert.Equal(["activate"], toggles);
	}

	[Fact]
	public void WheelScrollingKeepsItsLibraryBindings()
	{
		using var tree = CreateTree(out _, out _);

		var commands = tree.MouseBindings.GetBindings()
			.Where(binding => binding.Key == MouseFlags.WheeledDown)
			.SelectMany(binding => binding.Value.Commands)
			.ToArray();

		Assert.Contains(Command.ScrollDown, commands);
	}

	[Theory]
	[InlineData(0, false, 0, "Row")]
	[InlineData(0, true, 0, "Disclosure")]
	[InlineData(0, true, 2, "CheckBox")]
	[InlineData(0, true, 5, "Row")]
	[InlineData(2, true, 3, "Row")]
	[InlineData(2, true, 4, "Disclosure")]
	[InlineData(2, false, 6, "CheckBox")]
	[InlineData(2, false, 8, "CheckBox")]
	[InlineData(2, false, 9, "Row")]
	public void PointerTargetFollowsTheRenderedRowLayout(
		int depth,
		bool isDirectory,
		int column,
		string expectedTarget)
	{
		var node = new TreeNodeDescriptor("name", @"C:\project\name", isDirectory, false, "icon", []);
		var row = new TerminalTreeRow(node, depth, false, TerminalTreeCheckState.Unchecked);

		Assert.Equal(
			Enum.Parse<TerminalTreePointerTarget>(expectedTarget),
			TerminalProjectTreeView.ResolvePointerTarget(row, column));
		Assert.Equal("[ ]", row.ToString().Substring(depth * 2 + 2, 3));
	}

	// Wires the tree exactly as the workspace session does: pointer toggles arrive through
	// SelectionToggleRequested and keyboard activation (Space) through Activated.
	private static TerminalProjectTreeView CreateTree(out List<string> toggles, out List<int> expansions)
	{
		var folder = new TreeNodeDescriptor("src", @"C:\project\src", true, false, "folder", []);
		var file = new TreeNodeDescriptor("App.cs", @"C:\project\App.cs", false, false, "csharp", []);
		var rows = new ObservableCollection<TerminalTreeRow>
		{
			new(folder, 0, false, TerminalTreeCheckState.Unchecked),
			new(file, 0, false, TerminalTreeCheckState.Unchecked)
		};
		var tree = new TerminalProjectTreeView(
			index => index >= 0 && index < rows.Count ? rows[index] : null,
			showScrollBars: false)
		{
			Frame = new Rectangle(0, 0, 40, 10)
		};
		tree.SetSource(rows);
		tree.KeyBindings.ReplaceCommands(Key.Space, Command.Activate);
		var recordedToggles = new List<string>();
		var recordedExpansions = new List<int>();
		tree.SelectionToggleRequested += (_, _) => recordedToggles.Add("pointer");
		tree.Activated += (_, _) => recordedToggles.Add("activate");
		tree.ExpansionToggleRequested += (_, _) => recordedExpansions.Add(tree.SelectedItem ?? -1);
		toggles = recordedToggles;
		expansions = recordedExpansions;
		return tree;
	}

	private static void Click(TerminalProjectTreeView tree, int column, int row)
	{
		Send(tree, column, row, MouseFlags.LeftButtonPressed);
		Send(tree, column, row, MouseFlags.LeftButtonReleased);
		Send(tree, column, row, MouseFlags.LeftButtonClicked);
	}

	private static void DoubleClick(TerminalProjectTreeView tree, int column, int row)
	{
		Click(tree, column, row);
		Send(tree, column, row, MouseFlags.LeftButtonPressed);
		Send(tree, column, row, MouseFlags.LeftButtonReleased);
		Send(tree, column, row, MouseFlags.LeftButtonDoubleClicked);
	}

	private static void Send(TerminalProjectTreeView tree, int column, int row, MouseFlags flags) =>
		tree.NewMouseEvent(new Mouse { Position = new Point(column, row), Flags = flags });
}
