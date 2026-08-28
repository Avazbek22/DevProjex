using System.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace DevProjex.Terminal.Tui;

internal sealed class TerminalProjectTreeView : ListView
{
	private const long DoubleClickWindowMilliseconds = 500;
	private readonly Func<int, TerminalTreeRow?> _rowResolver;
	private readonly TerminalPointerEventDeduplicator _pointerEvents = new();
	private int _lastNamePressedRow = -1;
	private int _lastNamePressedColumn = -1;
	private long _lastNamePressedAt;
	private int _lastManualDoubleClickRow = -1;
	private int _lastManualDoubleClickColumn = -1;
	private long _lastManualDoubleClickAt;

	public TerminalProjectTreeView(
		Func<int, TerminalTreeRow?> rowResolver,
		bool useUnicode = true,
		bool showScrollBars = true)
	{
		_rowResolver = rowResolver;
		if (showScrollBars)
			TerminalScrollBarStyle.Apply(this, useUnicode, vertical: true, horizontal: true);
	}

	public event EventHandler? SelectionToggleRequested;
	public event EventHandler? ExpansionToggleRequested;
	public event EventHandler? CommandLineRequested;

	public int VerticalOffset => Viewport.Y;

	public void UpdateContentMetrics(int rowWidth, int rowCount) =>
		SetContentSize(new Size(Math.Max(1, rowWidth), Math.Max(1, rowCount)));

	public void RestoreVerticalOffset(int offset, int rowCount)
	{
		var maximumOffset = Math.Max(0, rowCount - Math.Max(1, Viewport.Height));
		Viewport = new Rectangle(
			Viewport.X,
			Math.Clamp(offset, 0, maximumOffset),
			Viewport.Width,
			Viewport.Height);
		SetNeedsDraw();
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

		var isPressed = mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed);
		var isReleased = mouse.Flags.HasFlag(MouseFlags.LeftButtonReleased);
		var isClicked = mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked);
		var isDoubleClicked = mouse.Flags.HasFlag(MouseFlags.LeftButtonDoubleClicked);
		if (!isPressed && !isReleased && !isClicked && !isDoubleClicked)
			return base.OnMouseEvent(mouse);
		if (mouse.Position is not { } position)
			return true;

		var rowIndex = Viewport.Y + position.Y;
		var row = _rowResolver(rowIndex);
		if (row is null)
			return true;

		var now = Environment.TickCount64;
		if (isDoubleClicked &&
		    _lastManualDoubleClickRow == rowIndex &&
		    _lastManualDoubleClickColumn == position.X &&
		    now - _lastManualDoubleClickAt <= DoubleClickWindowMilliseconds)
		{
			_lastManualDoubleClickRow = -1;
			_lastManualDoubleClickColumn = -1;
			return true;
		}
		if (!isDoubleClicked && !_pointerEvents.ShouldHandle(isPressed, position.X, position.Y))
		{
			return true;
		}

		SetFocus();
		SelectedItem = rowIndex;
		EnsureSelectedItemVisible();

		if (isDoubleClicked ||
			isPressed && row.Node.IsDirectory &&
			_lastNamePressedRow == rowIndex &&
			_lastNamePressedColumn == position.X &&
			now - _lastNamePressedAt <= DoubleClickWindowMilliseconds)
		{
			if (row.Node.IsDirectory)
			{
				// The first click already toggled the whole row. Balance the second
				// click so a double-click changes expansion without changing selection.
				SelectionToggleRequested?.Invoke(this, EventArgs.Empty);
				ExpansionToggleRequested?.Invoke(this, EventArgs.Empty);
				if (!isDoubleClicked)
				{
					_lastManualDoubleClickRow = rowIndex;
					_lastManualDoubleClickColumn = position.X;
					_lastManualDoubleClickAt = now;
				}
			}
			_lastNamePressedRow = -1;
			_lastNamePressedColumn = -1;
			return true;
		}
		_lastNamePressedRow = row.Node.IsDirectory ? rowIndex : -1;
		_lastNamePressedColumn = row.Node.IsDirectory ? position.X : -1;
		_lastNamePressedAt = now;
		SelectionToggleRequested?.Invoke(this, EventArgs.Empty);
		return true;
	}
}
