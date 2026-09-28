using System.Drawing;
using Terminal.Gui.Input;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalOverlayPointerGuardTests
{
	private static readonly DateTime Start = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Local);
	private static readonly Point Button = new(45, 27);

	[Fact]
	public void SecondClickOfADoubleClickThatClosedAnOverlayIsSwallowed()
	{
		var guard = new TerminalOverlayPointerGuard();
		Assert.Equal([false, false, false], Click(guard, Button, atMilliseconds: 0));

		guard.OverlayClosed();

		Assert.Equal(
			[true, true, true],
			Click(guard, Button, atMilliseconds: 140, MouseFlags.LeftButtonDoubleClicked));
	}

	[Fact]
	public void EveryFurtherClickOfTheSameMultiClickIsSwallowed()
	{
		var guard = new TerminalOverlayPointerGuard();
		Click(guard, Button, atMilliseconds: 0);
		guard.OverlayClosed();

		Click(guard, Button, atMilliseconds: 150, MouseFlags.LeftButtonDoubleClicked);

		Assert.Equal(
			[true, true, true],
			Click(guard, Button, atMilliseconds: 300, MouseFlags.LeftButtonTripleClicked));
	}

	[Fact]
	public void PressAfterTheDoubleClickWindowStartsANewGesture()
	{
		var guard = new TerminalOverlayPointerGuard();
		Click(guard, Button, atMilliseconds: 0);
		guard.OverlayClosed();
		Click(guard, Button, atMilliseconds: 100, MouseFlags.LeftButtonDoubleClicked);

		Assert.Equal([false, false, false], Click(guard, Button, atMilliseconds: 1_200));
		Assert.Equal(
			[false, false, false],
			Click(guard, Button, atMilliseconds: 1_300, MouseFlags.LeftButtonDoubleClicked));
	}

	[Fact]
	public void PressOnAnotherCellIsANewClickEvenRightAfterTheOverlayCloses()
	{
		var guard = new TerminalOverlayPointerGuard();
		Click(guard, Button, atMilliseconds: 0);
		guard.OverlayClosed();

		Assert.Equal([false, false, false], Click(guard, new Point(12, 5), atMilliseconds: 100));
	}

	[Fact]
	public void OverlayClosedWithoutARecentClickLeavesTheNextClickAlone()
	{
		var guard = new TerminalOverlayPointerGuard();
		Click(guard, Button, atMilliseconds: 0);

		guard.OverlayClosed();

		Assert.Equal([false, false, false], Click(guard, Button, atMilliseconds: 2_000));
	}

	[Theory]
	[InlineData(MouseFlags.WheeledDown)]
	[InlineData(MouseFlags.PositionReport)]
	[InlineData(MouseFlags.RightButtonClicked)]
	public void EventsWithoutTheLeftButtonAreNeverSwallowed(MouseFlags flags)
	{
		var guard = new TerminalOverlayPointerGuard();
		Click(guard, Button, atMilliseconds: 0);
		guard.OverlayClosed();

		Assert.False(guard.ShouldSwallow(flags, Button, Start.AddMilliseconds(50)));
		Assert.Equal(
			[true, true, true],
			Click(guard, Button, atMilliseconds: 100, MouseFlags.LeftButtonDoubleClicked));
	}

	private static bool[] Click(
		TerminalOverlayPointerGuard guard,
		Point position,
		int atMilliseconds,
		MouseFlags click = MouseFlags.LeftButtonClicked)
	{
		var pressedAt = Start.AddMilliseconds(atMilliseconds);
		var releasedAt = pressedAt.AddMilliseconds(40);
		return
		[
			guard.ShouldSwallow(MouseFlags.LeftButtonPressed, position, pressedAt),
			guard.ShouldSwallow(MouseFlags.LeftButtonReleased, position, releasedAt),
			guard.ShouldSwallow(click, position, releasedAt)
		];
	}
}
