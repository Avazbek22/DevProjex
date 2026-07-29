using System.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace DevProjex.Terminal.Tui;

internal sealed class TerminalProjectTreeView : ListView
{
	private const long PointerEventDeduplicationWindowMilliseconds = 1_000;
	private const long DoubleClickWindowMilliseconds = 500;
	private readonly Func<int, TerminalTreeRow?> _rowResolver;
	private int _lastPressedViewportColumn = -1;
	private int _lastPressedViewportRow = -1;
	private long _lastPressedAt;
	private int _lastNamePressedRow = -1;
	private long _lastNamePressedAt;

	public TerminalProjectTreeView(Func<int, TerminalTreeRow?> rowResolver)
	{
		_rowResolver = rowResolver;
	}

	public event EventHandler? SelectionToggleRequested;
	public event EventHandler? ExpansionToggleRequested;

	public int VerticalOffset => Viewport.Y;

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
		if (!isPressed &&
		    _lastPressedViewportRow == position.Y &&
		    _lastPressedViewportColumn == position.X &&
		    now - _lastPressedAt <= PointerEventDeduplicationWindowMilliseconds)
		{
			return true;
		}
		if (isPressed)
		{
			// A selection refresh can move the viewport before the matching release event.
			_lastPressedViewportRow = position.Y;
			_lastPressedViewportColumn = position.X;
			_lastPressedAt = now;
		}

		SetFocus();
		SelectedItem = rowIndex;
		EnsureSelectedItemVisible();

		var disclosureColumn = row.Depth * 2;
		var checkboxStart = disclosureColumn + 2;
		var nameStart = disclosureColumn + 6;
		if (isDoubleClicked)
		{
			if (position.X >= nameStart && row.Node.IsDirectory)
				ExpansionToggleRequested?.Invoke(this, EventArgs.Empty);
			return true;
		}

		if (position.X == disclosureColumn && row.Node.IsDirectory)
		{
			_lastNamePressedRow = -1;
			ExpansionToggleRequested?.Invoke(this, EventArgs.Empty);
		}
		else if (position.X >= checkboxStart &&
		         position.X < checkboxStart + 3)
		{
			_lastNamePressedRow = -1;
			SelectionToggleRequested?.Invoke(this, EventArgs.Empty);
		}
		else if (position.X >= nameStart && row.Node.IsDirectory)
		{
			if (_lastNamePressedRow == rowIndex &&
			    now - _lastNamePressedAt <= DoubleClickWindowMilliseconds)
			{
				_lastNamePressedRow = -1;
				ExpansionToggleRequested?.Invoke(this, EventArgs.Empty);
			}
			else
			{
				_lastNamePressedRow = rowIndex;
				_lastNamePressedAt = now;
			}
		}
		else
		{
			_lastNamePressedRow = -1;
		}
		return true;
	}
}
