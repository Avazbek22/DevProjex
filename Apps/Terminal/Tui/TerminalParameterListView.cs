using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DevProjex.Terminal.Tui;

internal sealed class TerminalParameterListView : ListView
{
	private readonly TerminalPointerEventDeduplicator _pointerEvents = new();

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
		return TerminalInteractiveView.TryActivateCommandLine(
			key,
			() => CommandLineRequested?.Invoke(this, EventArgs.Empty)) || base.OnKeyDown(key);
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
		if (!_pointerEvents.ShouldHandle(pressed, position.X, position.Y))
		{
			return true;
		}
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
