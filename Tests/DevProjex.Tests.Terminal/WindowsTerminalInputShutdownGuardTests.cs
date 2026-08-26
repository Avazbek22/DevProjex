namespace DevProjex.Tests.Terminal;

public sealed class WindowsTerminalInputShutdownGuardTests
{
	[Fact]
	public void ArmKeepsCancelingPendingReadsUntilApplicationDisposalCompletes()
	{
		var cancellationAttempts = 0;
		using var guard = new WindowsTerminalInputShutdownGuard(
			() => Interlocked.Increment(ref cancellationAttempts),
			TimeSpan.FromMilliseconds(1));

		guard.Arm();
		guard.Arm();

		Assert.True(SpinWait.SpinUntil(
			() => Volatile.Read(ref cancellationAttempts) >= 3,
			TimeSpan.FromSeconds(2)));
		guard.Dispose();
		var attemptsAfterDispose = Volatile.Read(ref cancellationAttempts);
		Thread.Sleep(20);

		Assert.Equal(attemptsAfterDispose, Volatile.Read(ref cancellationAttempts));
	}

	[Fact]
	public void ArmAfterDisposalDoesNotStartAWorker()
	{
		var cancellationAttempts = 0;
		var guard = new WindowsTerminalInputShutdownGuard(
			() => Interlocked.Increment(ref cancellationAttempts),
			TimeSpan.FromMilliseconds(1));

		guard.Dispose();
		guard.Arm();
		guard.Dispose();

		Assert.Equal(0, Volatile.Read(ref cancellationAttempts));
	}
}
