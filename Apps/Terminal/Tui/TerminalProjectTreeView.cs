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
	private long _lastNamePressedAt;

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
		if (!_pointerEvents.ShouldHandle(isPressed, position.X, position.Y))
		{
			return true;
		}

		SetFocus();
		SelectedItem = rowIndex;
		EnsureSelectedItemVisible();

		if (isDoubleClicked ||
			isPressed && row.Node.IsDirectory && _lastNamePressedRow == rowIndex &&
			now - _lastNamePressedAt <= DoubleClickWindowMilliseconds)
		{
			if (row.Node.IsDirectory)
				ExpansionToggleRequested?.Invoke(this, EventArgs.Empty);
			_lastNamePressedRow = -1;
			return true;
		}
		_lastNamePressedRow = row.Node.IsDirectory ? rowIndex : -1;
		_lastNamePressedAt = now;
		SelectionToggleRequested?.Invoke(this, EventArgs.Empty);
		return true;
	}
}
