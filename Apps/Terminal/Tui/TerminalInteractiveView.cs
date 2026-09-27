using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

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

	// A Dialog treats every Accept raised inside it as a press of its default button: it closes
	// before the originating list or button raises Accepted. Views whose Enter, double-click or
	// press has its own meaning in a dialog therefore act on Accepting and keep the command there.
	public static void OnAccept(View view, Action accept)
	{
		ArgumentNullException.ThrowIfNull(view);
		ArgumentNullException.ThrowIfNull(accept);
		view.Accepting += (_, args) =>
		{
			args.Handled = true;
			accept();
		};
	}
}

// Pointer actions in the workspace happen on the press a view receives itself. Terminal.Gui
// synthesizes Clicked and DoubleClicked for the view under the release, even when the press
// started elsewhere (the preview, or a dialog button that has just closed), so those events
// are never treated as a new action.
internal static class TerminalPointerInput
{
	// A real button press, not movement: movement with a button held arrives with
	// PositionReport and may still carry the pressed flag.
	public static bool IsPress(MouseFlags flags) =>
		flags.HasFlag(MouseFlags.LeftButtonPressed) && !IsMotion(flags);

	public static bool IsMotion(MouseFlags flags) => flags.HasFlag(MouseFlags.PositionReport);
}
