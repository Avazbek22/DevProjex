using System.Collections.ObjectModel;
using System.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DevProjex.Tests.Terminal;

// A Terminal.Gui Dialog turns any Accept raised inside it into a press of its default button.
// These tests route real input through a Dialog the way the TUI builds its overlays.
public sealed class TerminalDialogAcceptTests
{
	[Fact]
	public void EnterOnAListRunsTheListActionInsteadOfTheDefaultButton()
	{
		using var dialog = CreateDialog(out var list, out _, out var actions);

		list.NewKeyDownEvent(Key.Enter);

		Assert.Equal(["list"], actions);
		Assert.Null(dialog.Result);
	}

	[Fact]
	public void DoubleClickOnAListRowRunsTheListActionForThatRow()
	{
		using var dialog = CreateDialog(out var list, out _, out var actions);

		Send(list, row: 1, MouseFlags.LeftButtonPressed);
		Send(list, row: 1, MouseFlags.LeftButtonReleased);
		Send(list, row: 1, MouseFlags.LeftButtonClicked);
		Send(list, row: 1, MouseFlags.LeftButtonPressed);
		Send(list, row: 1, MouseFlags.LeftButtonReleased);
		Send(list, row: 1, MouseFlags.LeftButtonDoubleClicked);

		Assert.Equal(["list"], actions);
		Assert.Equal(1, list.SelectedItem);
		Assert.Null(dialog.Result);
	}

	[Theory]
	[InlineData(0, "back")]
	[InlineData(1, "open")]
	public void ClickOnADialogButtonRunsItsOwnAction(int buttonIndex, string expectedAction)
	{
		using var dialog = CreateDialog(out _, out var buttons, out var actions);

		Click(buttons[buttonIndex]);

		Assert.Equal([expectedAction], actions);
		Assert.Null(dialog.Result);
	}

	[Theory]
	[InlineData(0, "back")]
	[InlineData(1, "open")]
	public void EnterOnAFocusedDialogButtonRunsItsOwnAction(int buttonIndex, string expectedAction)
	{
		using var dialog = CreateDialog(out _, out var buttons, out var actions);

		buttons[buttonIndex].NewKeyDownEvent(Key.Enter);

		Assert.Equal([expectedAction], actions);
		Assert.Null(dialog.Result);
	}

	[Fact]
	public void ButtonWithoutItsOwnActionStillClosesTheDialogWithItsResult()
	{
		using var dialog = CreateDialog(out _, out _, out var actions);
		var cancel = new Button { Text = "Cancel" };
		dialog.AddButton(cancel);
		dialog.AddButton(new Button { Text = "Last" });

		Click(cancel);

		Assert.Empty(actions);
		Assert.Equal(2, dialog.Result);
	}

	private static Dialog CreateDialog(
		out ListView list,
		out Button[] buttons,
		out List<string> actions)
	{
		var recorded = new List<string>();
		var dialog = new Dialog();
		list = new ListView { Frame = new Rectangle(0, 0, 30, 4) };
		list.SetSource(new ObservableCollection<string>(["first", "second", "third"]));
		list.SelectedItem = 0;
		TerminalInteractiveView.OnAccept(list, () => recorded.Add("list"));
		dialog.Add(list);
		var back = new Button { Text = "Back" };
		TerminalInteractiveView.OnAccept(back, () => recorded.Add("back"));
		var open = new Button { Text = "Open" };
		TerminalInteractiveView.OnAccept(open, () => recorded.Add("open"));
		dialog.AddButton(back);
		dialog.AddButton(open);
		buttons = [back, open];
		actions = recorded;
		return dialog;
	}

	private static void Click(Button button)
	{
		SendTo(button, new Point(1, 0), MouseFlags.LeftButtonPressed);
		SendTo(button, new Point(1, 0), MouseFlags.LeftButtonReleased);
		SendTo(button, new Point(1, 0), MouseFlags.LeftButtonClicked);
	}

	private static void Send(ListView list, int row, MouseFlags flags) =>
		SendTo(list, new Point(2, row), flags);

	private static void SendTo(View view, Point position, MouseFlags flags) =>
		view.NewMouseEvent(new Mouse { Position = position, Flags = flags });
}
