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
