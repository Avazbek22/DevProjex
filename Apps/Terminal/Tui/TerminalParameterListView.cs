using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DevProjex.Terminal.Tui;

internal sealed class TerminalParameterListView : ListView
{
	private const long PointerEventDeduplicationWindowMilliseconds = 1_000;
	private int _lastPressedViewportColumn = -1;
	private int _lastPressedViewportRow = -1;
	private long _lastPressedAt;

	public TerminalParameterListView(
		bool showVerticalScrollBar = false,
		bool useUnicode = true)
	{
		if (showVerticalScrollBar)
			TerminalScrollBarStyle.Apply(this, useUnicode, vertical: true, horizontal: false);
	}

	public event EventHandler? SelectionToggleRequested;
	public event EventHandler? InteractionStarted;
	public event EventHandler? CommandLineRequested;

	protected override bool OnKeyDown(Key key)
	{
		if (!TerminalWorkspaceCommandKey.IsActivation(key))
			return base.OnKeyDown(key);
		CommandLineRequested?.Invoke(this, EventArgs.Empty);
		return true;
	}

	protected override bool OnMouseEvent(Mouse mouse)
	{
		if (mouse.Flags.HasFlag(MouseFlags.WheeledUp) ||
			mouse.Flags.HasFlag(MouseFlags.WheeledDown))
		{
			return base.OnMouseEvent(mouse);
		}

		var pressed = mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed);
		var released = mouse.Flags.HasFlag(MouseFlags.LeftButtonReleased);
		var clicked = mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked);
		if ((!pressed && !released && !clicked) || mouse.Position is not { } position)
			return base.OnMouseEvent(mouse);

		SetFocus();
		InteractionStarted?.Invoke(this, EventArgs.Empty);
		if (!TryResolveSelectionIndex(
				Viewport.Y,
				position.Y,
				Source?.Count ?? 0,
				out var row))
		{
			return true;
		}
		SelectedItem = row;
		EnsureSelectedItemVisible();
		var now = Environment.TickCount64;
		if (!pressed &&
			_lastPressedViewportRow == position.Y &&
			_lastPressedViewportColumn == position.X &&
			now - _lastPressedAt <= PointerEventDeduplicationWindowMilliseconds)
		{
			return true;
		}
		if (pressed)
		{
			// A settings refresh can move the viewport before the matching release event.
			_lastPressedViewportRow = position.Y;
			_lastPressedViewportColumn = position.X;
			_lastPressedAt = now;
		}
		if (position.X is >= 0 and <= 2)
			SelectionToggleRequested?.Invoke(this, EventArgs.Empty);
		return true;
	}

	internal static bool TryResolveSelectionIndex(
		int viewportTop,
		int pointerRow,
		int itemCount,
		out int selectionIndex)
	{
		selectionIndex = viewportTop + pointerRow;
		return viewportTop >= 0 &&
		       pointerRow >= 0 &&
		       selectionIndex >= 0 &&
		       selectionIndex < itemCount;
	}
}
