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
		// OnMouseEvent owns every pointer gesture. The list's default bindings would turn the
		// release that ends a click into a second activation and undo the toggle.
		MouseBindings.Clear(Command.Activate);
		MouseBindings.Clear(Command.Accept);
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
		var isDoubleClicked = mouse.Flags.HasFlag(MouseFlags.LeftButtonDoubleClicked);
		if (!IsPrimaryActivation(mouse.Flags))
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

		switch (ResolvePointerTarget(row, Viewport.X + position.X))
		{
			case TerminalTreePointerTarget.CheckBox:
				ForgetNamePress();
				if (!isDoubleClicked)
					SelectionToggleRequested?.Invoke(this, EventArgs.Empty);
				return true;
			case TerminalTreePointerTarget.Disclosure:
				ForgetNamePress();
				if (!isDoubleClicked)
					ExpansionToggleRequested?.Invoke(this, EventArgs.Empty);
				return true;
		}

		// A click elsewhere on the row only moves the cursor; a double-click on a folder
		// expands or collapses it.
		if (!row.Node.IsDirectory)
		{
			ForgetNamePress();
			return true;
		}
		if (isDoubleClicked ||
			isPressed &&
			_lastNamePressedRow == rowIndex &&
			_lastNamePressedColumn == position.X &&
			now - _lastNamePressedAt <= DoubleClickWindowMilliseconds)
		{
			ExpansionToggleRequested?.Invoke(this, EventArgs.Empty);
			if (!isDoubleClicked)
			{
				_lastManualDoubleClickRow = rowIndex;
				_lastManualDoubleClickColumn = position.X;
				_lastManualDoubleClickAt = now;
			}
			ForgetNamePress();
			return true;
		}
		_lastNamePressedRow = rowIndex;
		_lastNamePressedColumn = position.X;
		_lastNamePressedAt = now;
		return true;
	}

	internal static bool IsPrimaryActivation(MouseFlags flags) =>
		flags.HasFlag(MouseFlags.LeftButtonPressed) ||
		flags.HasFlag(MouseFlags.LeftButtonClicked) ||
		flags.HasFlag(MouseFlags.LeftButtonDoubleClicked);

	// Mirrors the row layout built by TerminalTreeRow: "<indent><disclosure> [x] <name>".
	internal static TerminalTreePointerTarget ResolvePointerTarget(TerminalTreeRow row, int column)
	{
		var disclosureColumn = row.Depth * 2;
		var checkBoxStart = disclosureColumn + 2;
		if (column == disclosureColumn && row.Node.IsDirectory)
			return TerminalTreePointerTarget.Disclosure;
		return column >= checkBoxStart && column < checkBoxStart + 3
			? TerminalTreePointerTarget.CheckBox
			: TerminalTreePointerTarget.Row;
	}

	private void ForgetNamePress()
	{
		_lastNamePressedRow = -1;
		_lastNamePressedColumn = -1;
	}
}

internal enum TerminalTreePointerTarget
{
	Row,
	Disclosure,
	CheckBox
}
