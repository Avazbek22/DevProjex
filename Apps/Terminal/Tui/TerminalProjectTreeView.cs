using System.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace DevProjex.Terminal.Tui;

internal sealed class TerminalProjectTreeView : ListView
{
	private const long DoubleClickWindowMilliseconds = 500;
	private readonly Func<int, TerminalTreeRow?> _rowResolver;
	private int _lastNamePressedRow = -1;
	private long _lastNamePressedAt;
	private int _lastDisclosurePressedRow = -1;
	private long _lastDisclosurePressedAt;

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
		if (TerminalPointerInput.IsMotion(mouse.Flags))
			return true;
		if (!IsPrimaryActivation(mouse.Flags))
			return base.OnMouseEvent(mouse);
		if (!TerminalPointerInput.IsPress(mouse.Flags) || mouse.Position is not { } position)
			return true;

		var rowIndex = Viewport.Y + position.Y;
		var row = _rowResolver(rowIndex);
		if (row is null)
			return true;

		var now = Environment.TickCount64;
		SetFocus();
		SelectedItem = rowIndex;
		EnsureSelectedItemVisible();

		switch (ResolvePointerTarget(row, Viewport.X + position.X))
		{
			case TerminalTreePointerTarget.CheckBox:
				_lastNamePressedRow = -1;
				_lastDisclosurePressedRow = -1;
				SelectionToggleRequested?.Invoke(this, EventArgs.Empty);
				return true;
			case TerminalTreePointerTarget.Disclosure:
				ToggleExpansionFromDisclosure(rowIndex, now);
				return true;
		}

		// A click elsewhere on the row only moves the cursor; two presses on a folder name
		// within the double-click window expand or collapse it.
		_lastDisclosurePressedRow = -1;
		if (row.Node.IsDirectory && IsRepeatedPress(_lastNamePressedRow, _lastNamePressedAt, rowIndex, now))
		{
			_lastNamePressedRow = -1;
			ExpansionToggleRequested?.Invoke(this, EventArgs.Empty);
			return true;
		}
		_lastNamePressedRow = row.Node.IsDirectory ? rowIndex : -1;
		_lastNamePressedAt = now;
		return true;
	}

	internal static bool IsPrimaryActivation(MouseFlags flags) =>
		!TerminalPointerInput.IsMotion(flags) &&
		(flags.HasFlag(MouseFlags.LeftButtonPressed) ||
		 flags.HasFlag(MouseFlags.LeftButtonClicked) ||
		 flags.HasFlag(MouseFlags.LeftButtonDoubleClicked));

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

	// A double-click on the > / v marker is one request to open or close the folder; its
	// second press must not undo the first.
	private void ToggleExpansionFromDisclosure(int rowIndex, long now)
	{
		_lastNamePressedRow = -1;
		if (IsRepeatedPress(_lastDisclosurePressedRow, _lastDisclosurePressedAt, rowIndex, now))
		{
			_lastDisclosurePressedRow = -1;
			return;
		}
		_lastDisclosurePressedRow = rowIndex;
		_lastDisclosurePressedAt = now;
		ExpansionToggleRequested?.Invoke(this, EventArgs.Empty);
	}

	private static bool IsRepeatedPress(int lastRow, long lastAt, int rowIndex, long now) =>
		lastRow == rowIndex && now - lastAt <= DoubleClickWindowMilliseconds;
}

internal enum TerminalTreePointerTarget
{
	Row,
	Disclosure,
	CheckBox
}
