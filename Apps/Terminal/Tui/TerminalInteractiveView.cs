using Terminal.Gui.Input;

namespace DevProjex.Terminal.Tui;

internal static class TerminalInteractiveView
{
	public static bool TryActivateCommandLine(Key key, Action activate)
	{
		ArgumentNullException.ThrowIfNull(activate);
		if (!TerminalWorkspaceCommandKey.IsActivation(key))
			return false;
		activate();
		return true;
	}
}

internal sealed class TerminalPointerEventDeduplicator(long windowMilliseconds = 1_000)
{
	private int _column = -1;
	private int _row = -1;
	private long _pressedAt;

	public bool ShouldHandle(bool pressed, int column, int row)
	{
		var now = Environment.TickCount64;
		if (!pressed && _column == column && _row == row && now - _pressedAt <= windowMilliseconds)
			return false;
		if (pressed)
		{
			_column = column;
			_row = row;
			_pressedAt = now;
		}
		return true;
	}
}
