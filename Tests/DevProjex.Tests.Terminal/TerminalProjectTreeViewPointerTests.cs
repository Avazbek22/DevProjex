using System.Collections.ObjectModel;
using System.Drawing;
using Terminal.Gui.Input;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalProjectTreeViewPointerTests
{
	[Theory]
	[InlineData(0)]
	[InlineData(3)]
	[InlineData(8)]
	public void ClickOnAFileRowTogglesItsSelectionExactlyOnce(int column)
	{
		using var tree = CreateTree(out var toggles);

		Send(tree, column, row: 1, MouseFlags.LeftButtonPressed);
		Send(tree, column, row: 1, MouseFlags.LeftButtonReleased);
		Send(tree, column, row: 1, MouseFlags.LeftButtonClicked);

		Assert.Equal(1, toggles.Count);
		Assert.Equal(1, tree.SelectedItem);
	}

	[Fact]
	public void ReleaseWithoutAHandledPressDoesNotToggleSelection()
	{
		using var tree = CreateTree(out var toggles);

		Send(tree, column: 3, row: 1, MouseFlags.LeftButtonReleased);
		Send(tree, column: 3, row: 1, MouseFlags.LeftButtonReleased | MouseFlags.Ctrl);

		Assert.Equal(0, toggles.Count);
	}

	[Fact]
	public void CtrlRightClickDoesNotToggleSelection()
	{
		using var tree = CreateTree(out var toggles);

		Send(tree, column: 3, row: 1, MouseFlags.RightButtonPressed | MouseFlags.Ctrl);
		Send(tree, column: 3, row: 1, MouseFlags.RightButtonReleased | MouseFlags.Ctrl);
		Send(tree, column: 3, row: 1, MouseFlags.RightButtonClicked | MouseFlags.Ctrl);

		Assert.Equal(0, toggles.Count);
	}

	[Fact]
	public void DoubleClickOnAFolderKeepsSelectionAndTogglesExpansionOnce()
	{
		using var tree = CreateTree(out var toggles);
		var expansions = 0;
		tree.ExpansionToggleRequested += (_, _) => expansions++;

		Send(tree, column: 6, row: 0, MouseFlags.LeftButtonPressed);
		Send(tree, column: 6, row: 0, MouseFlags.LeftButtonReleased);
		Send(tree, column: 6, row: 0, MouseFlags.LeftButtonClicked);
		Send(tree, column: 6, row: 0, MouseFlags.LeftButtonPressed);
		Send(tree, column: 6, row: 0, MouseFlags.LeftButtonReleased);
		Send(tree, column: 6, row: 0, MouseFlags.LeftButtonDoubleClicked);

		Assert.Equal(1, expansions);
		Assert.DoesNotContain("activate", toggles);
		Assert.Equal(0, toggles.Count % 2);
	}

	[Fact]
	public void SpaceStillActivatesTheSelectedRow()
	{
		using var tree = CreateTree(out var toggles);
		tree.SelectedItem = 1;

		tree.NewKeyDownEvent(Key.Space);

		Assert.Equal(1, toggles.Count);
	}

	[Fact]
	public void WheelScrollingKeepsItsLibraryBindings()
	{
		using var tree = CreateTree(out _);

		var commands = tree.MouseBindings.GetBindings()
			.Where(binding => binding.Key == MouseFlags.WheeledDown)
			.SelectMany(binding => binding.Value.Commands)
			.ToArray();

		Assert.Contains(Command.ScrollDown, commands);
	}

	// Wires the tree exactly as the workspace session does: pointer toggles arrive through
	// SelectionToggleRequested and keyboard activation (Space) through Activated.
	private static TerminalProjectTreeView CreateTree(out List<string> toggles)
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
		var recorded = new List<string>();
		tree.SelectionToggleRequested += (_, _) => recorded.Add("pointer");
		tree.Activated += (_, _) => recorded.Add("activate");
		toggles = recorded;
		return tree;
	}

	private static void Send(TerminalProjectTreeView tree, int column, int row, MouseFlags flags) =>
		tree.NewMouseEvent(new Mouse { Position = new Point(column, row), Flags = flags });
}
