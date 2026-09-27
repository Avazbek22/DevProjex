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

// Terminal.Gui reports one click twice: the press and, after the release, a synthesized
// Clicked event. The Clicked event completes the press that started it however long the
// button was held and even when the pointer moved a cell in between. A Clicked event with no
// press before it is handled on its own.
internal sealed class TerminalPointerEventDeduplicator
{
	private bool _pressPending;

	public bool ShouldHandle(bool pressed)
	{
		if (pressed)
		{
			_pressPending = true;
			return true;
		}
		var completesPress = _pressPending;
		_pressPending = false;
		return !completesPress;
	}

	// Pointer movement, including movement with a button held, arrives with PositionReport and
	// may still carry the pressed flag; it continues a gesture and never starts a new one.
	public static bool IsMotion(MouseFlags flags) => flags.HasFlag(MouseFlags.PositionReport);
}
