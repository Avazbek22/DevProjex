using System.Collections.ObjectModel;
using System.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Text;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalParameterPointerTests
{
	private const int CheckBoxRow = 0;
	private const int RadioRow = 1;

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	public void ClickOnTheCheckBoxMarkerTogglesTheRowOnce(int column)
	{
		using var list = CreateList(out var toggles);

		Click(list, column, CheckBoxRow);

		Assert.Equal(1, toggles.Count);
		Assert.Equal(CheckBoxRow, list.SelectedItem);
	}

	[Theory]
	[InlineData(3)]
	[InlineData(4)]
	[InlineData(8)]
	public void ClickOnACheckBoxLabelOnlyMovesTheCursor(int column)
	{
		using var list = CreateList(out var toggles);
		list.SelectedItem = RadioRow;

		Click(list, column, CheckBoxRow);

		Assert.Equal(0, toggles.Count);
		Assert.Equal(CheckBoxRow, list.SelectedItem);
	}

	[Theory]
	[InlineData(1)]
	[InlineData(6)]
	public void ClickAnywhereOnARadioRowSelectsIt(int column)
	{
		using var list = CreateList(out var toggles);

		Click(list, column, RadioRow);

		Assert.Equal(1, toggles.Count);
		Assert.Equal(RadioRow, list.SelectedItem);
	}

	[Fact]
	public void PointerDriftInsideTheMarkerTogglesOnce()
	{
		using var list = CreateList(out var toggles);

		Send(list, 0, CheckBoxRow, MouseFlags.LeftButtonPressed);
		Send(list, 1, CheckBoxRow, MouseFlags.LeftButtonPressed | MouseFlags.PositionReport);
		Send(list, 1, CheckBoxRow, MouseFlags.LeftButtonReleased);
		Send(list, 1, CheckBoxRow, MouseFlags.LeftButtonClicked);

		Assert.Equal(1, toggles.Count);
	}

	[Theory]
	[InlineData(1, CheckBoxRow)]
	[InlineData(6, RadioRow)]
	public void ReleaseOverARowAfterAPressElsewhereDoesNothing(int column, int row)
	{
		using var list = CreateList(out var toggles);

		Send(list, column, row, MouseFlags.LeftButtonReleased);
		Send(list, column, row, MouseFlags.LeftButtonClicked);

		Assert.Equal(0, toggles.Count);
	}

	[Fact]
	public void ReleaseAloneDoesNothing()
	{
		using var list = CreateList(out var toggles);
		var accepted = 0;
		list.Accepted += (_, _) => accepted++;

		Send(list, 1, CheckBoxRow, MouseFlags.LeftButtonReleased);
		Send(list, 1, CheckBoxRow, MouseFlags.LeftButtonDoubleClicked);

		Assert.Equal(0, toggles.Count);
		Assert.Equal(0, accepted);
	}

	[Theory]
	[InlineData(false, 2, true)]
	[InlineData(false, 4, true)]
	[InlineData(false, 1, false)]
	[InlineData(false, 6, false)]
	[InlineData(true, 1, true)]
	[InlineData(true, 3, true)]
	[InlineData(true, 0, false)]
	[InlineData(true, 5, false)]
	public void AggregateControlTogglesOnlyFromItsMarker(bool isOnBorder, int column, bool toggles)
	{
		using var control = new TerminalAggregateControl(isOnBorder);
		control.SetRow(new TerminalParameterRow(
			"extensions:all",
			TerminalParameterRowKind.ToggleAllExtensions,
			"All",
			IsSelected: false));
		control.Frame = new Rectangle(0, 0, control.Text.GetColumns(), 1);
		var count = 0;
		control.SelectionToggleRequested += (_, _) => count++;

		control.NewMouseEvent(new Mouse { Position = new Point(column, 0), Flags = MouseFlags.LeftButtonPressed });
		control.NewMouseEvent(new Mouse { Position = new Point(column, 0), Flags = MouseFlags.LeftButtonReleased });
		control.NewMouseEvent(new Mouse { Position = new Point(column, 0), Flags = MouseFlags.LeftButtonClicked });

		Assert.Equal(toggles ? 1 : 0, count);
		Assert.Equal("[ ]", control.Text.Substring(isOnBorder ? 1 : 2, 3));
	}

	[Fact]
	public void AggregateControlIgnoresPointerDriftWhileTheButtonIsHeld()
	{
		using var control = new TerminalAggregateControl(isOnBorder: false);
		control.SetRow(new TerminalParameterRow(
			"extensions:all",
			TerminalParameterRowKind.ToggleAllExtensions,
			"All",
			IsSelected: false));
		control.Frame = new Rectangle(0, 0, control.Text.GetColumns(), 1);
		var count = 0;
		control.SelectionToggleRequested += (_, _) => count++;

		control.NewMouseEvent(new Mouse { Position = new Point(2, 0), Flags = MouseFlags.LeftButtonPressed });
		control.NewMouseEvent(new Mouse { Position = new Point(3, 0), Flags = MouseFlags.LeftButtonPressed | MouseFlags.PositionReport });
		control.NewMouseEvent(new Mouse { Position = new Point(3, 0), Flags = MouseFlags.LeftButtonReleased });
		control.NewMouseEvent(new Mouse { Position = new Point(3, 0), Flags = MouseFlags.LeftButtonClicked });

		Assert.Equal(1, count);
	}

	[Fact]
	public void ReleaseOverTheAggregateMarkerAfterAPressElsewhereDoesNothing()
	{
		using var control = new TerminalAggregateControl(isOnBorder: true);
		control.SetRow(new TerminalParameterRow(
			"extensions:all",
			TerminalParameterRowKind.ToggleAllExtensions,
			"All",
			IsSelected: true));
		control.Frame = new Rectangle(0, 0, control.Text.GetColumns(), 1);
		var count = 0;
		control.SelectionToggleRequested += (_, _) => count++;

		control.NewMouseEvent(new Mouse { Position = new Point(2, 0), Flags = MouseFlags.LeftButtonReleased });
		control.NewMouseEvent(new Mouse { Position = new Point(2, 0), Flags = MouseFlags.LeftButtonClicked });

		Assert.Equal(0, count);
	}

	private static TerminalParameterListView CreateList(out List<int> toggles)
	{
		var rows = new ObservableCollection<TerminalParameterRow>
		{
			new("exclusion:bin", TerminalParameterRowKind.Exclusion, "bin and obj", IsSelected: false),
			new("git:none", TerminalParameterRowKind.GitMode, "Off", IsSelected: false, GitMode: GitFilteringMode.None)
		};
		var list = new TerminalParameterListView
		{
			Frame = new Rectangle(0, 0, 30, 5)
		};
		list.SetParameterSource(rows);
		var recorded = new List<int>();
		list.SelectionToggleRequested += (_, _) => recorded.Add(list.SelectedItem ?? -1);
		toggles = recorded;
		return list;
	}

	private static void Click(TerminalParameterListView list, int column, int row)
	{
		Send(list, column, row, MouseFlags.LeftButtonPressed);
		Send(list, column, row, MouseFlags.LeftButtonReleased);
		Send(list, column, row, MouseFlags.LeftButtonClicked);
	}

	private static void Send(TerminalParameterListView list, int column, int row, MouseFlags flags) =>
		list.NewMouseEvent(new Mouse { Position = new Point(column, row), Flags = flags });
}
