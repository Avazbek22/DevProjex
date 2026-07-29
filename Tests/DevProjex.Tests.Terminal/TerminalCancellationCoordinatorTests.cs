namespace DevProjex.Tests.Terminal;

public sealed class TerminalCancellationCoordinatorTests
{
	[Fact]
	public void FirstInterruptStartsCleanupAndSecondAllowsNativeTermination()
	{
		var policy = new InterruptPolicy();

		Assert.True(policy.TryBeginCancellation());
		Assert.False(policy.TryBeginCancellation());
		Assert.False(policy.TryBeginCancellation());
	}
}
