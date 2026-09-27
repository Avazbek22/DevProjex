using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DevProjex.Terminal.Tui;

internal sealed class TerminalAggregateControl : Label
{
	private bool _isActive;
	private bool _isEnabled = true;

	public TerminalAggregateControl(bool isOnBorder)
	{
		IsOnBorder = isOnBorder;
		CanFocus = true;
		Height = 1;
		HotKeySpecifier = new Rune('\uffff');
		PreserveTrailingSpaces = true;
		// OnMouseEvent owns pointer input; the label's default binding would activate on release.
		MouseBindings.Clear(Command.Activate);
	}

	public event EventHandler? SelectionToggleRequested;
	public event EventHandler? InteractionStarted;
	public event EventHandler? CommandLineRequested;
	public bool IsOnBorder { get; }
	private string Leading => IsOnBorder ? " " : "  ";

	public void SetRow(TerminalParameterRow row)
	{
		ArgumentNullException.ThrowIfNull(row);
		var marker = row.IsSelected == true ? "[x]" : "[ ]";
		var trailing = IsOnBorder ? " " : string.Empty;
		var text = $"{Leading}{marker} {row.Label}{trailing}";
		_isEnabled = row.IsEnabled;
		Text = text;
		Width = text.GetColumns();
		SetNeedsDraw();
	}

	public void SetActive(bool value)
	{
		if (_isActive == value)
			return;
		_isActive = value;
		SetNeedsDraw();
	}

	protected override bool OnKeyDown(Key key)
	{
		return TerminalInteractiveView.TryActivateCommandLine(
			key,
			() => CommandLineRequested?.Invoke(this, EventArgs.Empty)) || base.OnKeyDown(key);
	}

	protected override bool OnDrawingContent(DrawContext? context)
	{
		SetAttributeForRole(!_isEnabled
			? VisualRole.Disabled
			: _isActive ? VisualRole.Focus : VisualRole.ReadOnly);
		AddStr(0, 0, Text);
		return true;
	}

	protected override bool OnMouseEvent(Mouse mouse)
	{
		if (TerminalPointerInput.IsMotion(mouse.Flags))
			return true;
		if (!mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed) &&
			!mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked))
		{
			return base.OnMouseEvent(mouse);
		}
		if (!TerminalPointerInput.IsPress(mouse.Flags))
			return true;

		SetFocus();
		InteractionStarted?.Invoke(this, EventArgs.Empty);
		if (IsMarkerColumn(mouse.Position?.X))
			SelectionToggleRequested?.Invoke(this, EventArgs.Empty);
		return true;
	}

	// Only the "[x]" marker toggles; a click on the label just focuses the control.
	internal bool IsMarkerColumn(int? column) =>
		column is { } value &&
		value >= Leading.Length &&
		value < Leading.Length + TerminalParameterRow.MarkerColumns;
}
