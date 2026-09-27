using System.Collections.ObjectModel;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DevProjex.Terminal.Tui;

internal sealed class TerminalParameterListView : ListView
{
	private readonly TerminalPointerEventDeduplicator _pointerEvents = new();
	private IReadOnlyList<TerminalParameterRow>? _rows;

	public TerminalParameterListView(
		bool showVerticalScrollBar = false,
		bool useUnicode = true)
	{
		// OnMouseEvent owns pointer input; the list's default bindings would activate on release.
		MouseBindings.Clear(Command.Activate);
		MouseBindings.Clear(Command.Accept);
		if (showVerticalScrollBar)
			TerminalScrollBarStyle.Apply(this, useUnicode, vertical: true, horizontal: false);
	}

	public event EventHandler? SelectionToggleRequested;
	public event EventHandler? InteractionStarted;
	public event EventHandler? CommandLineRequested;

	public void SetParameterSource(ObservableCollection<TerminalParameterRow> rows)
	{
		ArgumentNullException.ThrowIfNull(rows);
		_rows = rows;
		SetSource(rows);
	}

	public override void OnRowRender(ListViewRowEventArgs rowEventArgs)
	{
		base.OnRowRender(rowEventArgs);
		if (!IsRowEnabled(rowEventArgs.Row))
			rowEventArgs.RowAttribute = GetScheme().Disabled;
	}

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

		if (TerminalPointerEventDeduplicator.IsMotion(mouse.Flags))
			return true;

		var pressed = mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed);
		if (!IsPrimaryActivation(mouse.Flags) || mouse.Position is not { } position)
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
		if (!IsRowEnabled(row))
			return true;
		SelectedItem = row;
		EnsureSelectedItemVisible();
		if (!_pointerEvents.ShouldHandle(pressed))
		{
			return true;
		}
		if (TogglesOnPointer(row, Viewport.X + position.X))
			SelectionToggleRequested?.Invoke(this, EventArgs.Empty);
		return true;
	}

	// A checkbox changes only when its "[x]" marker is clicked; a click on the label moves the
	// cursor. Git mode rows are radio buttons and select from anywhere on the row.
	internal bool TogglesOnPointer(int row, int column) =>
		column is >= 0 and < TerminalParameterRow.MarkerColumns ||
		_rows is not null && row >= 0 && row < _rows.Count &&
		_rows[row].Kind == TerminalParameterRowKind.GitMode;

	internal bool IsRowEnabled(int row) =>
		_rows is null || row >= 0 && row < _rows.Count && _rows[row].IsEnabled;

	internal static bool IsPrimaryActivation(MouseFlags flags) =>
		!TerminalPointerEventDeduplicator.IsMotion(flags) &&
		(flags.HasFlag(MouseFlags.LeftButtonPressed) ||
		 flags.HasFlag(MouseFlags.LeftButtonClicked));

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
