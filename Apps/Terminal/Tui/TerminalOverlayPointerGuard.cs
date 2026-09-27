using System.Drawing;
using Terminal.Gui.Input;

namespace DevProjex.Terminal.Tui;

// An overlay that closes on the first click of a double- or triple-click leaves the rest of that
// gesture in flight, and Terminal.Gui delivers it to whatever the overlay covered: a Welcome row
// runs its action, a tree row takes the cursor. After an overlay closes, the guard swallows
// left-button events while they continue the same multi-click, as Terminal.Gui counts one: the
// same cell within the double-click window. Any other press starts new input.
internal sealed class TerminalOverlayPointerGuard
{
	private static readonly TimeSpan MultiClickWindow = TimeSpan.FromMilliseconds(500);

	private DateTime? _lastLeftButtonEventAt;
	private Point _lastLeftButtonEventPosition;
	private bool _swallowing;

	public void OverlayClosed() => _swallowing = true;

	public bool ShouldSwallow(MouseFlags flags, Point screenPosition, DateTime at)
	{
		if (!IsLeftButtonEvent(flags))
			return false;
		var continuesMultiClick =
			_lastLeftButtonEventAt is { } lastAt &&
			at - lastAt <= MultiClickWindow &&
			screenPosition == _lastLeftButtonEventPosition;
		_lastLeftButtonEventAt = at;
		_lastLeftButtonEventPosition = screenPosition;
		if (_swallowing && TerminalPointerInput.IsPress(flags) && !continuesMultiClick)
			_swallowing = false;
		return _swallowing;
	}

	private static bool IsLeftButtonEvent(MouseFlags flags) =>
		(flags & (MouseFlags.LeftButtonPressed |
			MouseFlags.LeftButtonReleased |
			MouseFlags.LeftButtonClicked |
			MouseFlags.LeftButtonDoubleClicked |
			MouseFlags.LeftButtonTripleClicked)) != 0;
}
