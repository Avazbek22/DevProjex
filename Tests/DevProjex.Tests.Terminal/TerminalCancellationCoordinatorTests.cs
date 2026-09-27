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

	[Fact]
	public async Task CloseRequestCancelsAndHoldsUntilTheCommandHasShutDown()
	{
		var coordinator = new TerminalCancellationCoordinator(registerSystemHandlers: false);
		var cancellationToken = TestContext.Current.CancellationToken;
		var closeRequest = Task.Run(
			() => coordinator.HandleCloseRequest(static () => { }, TimeSpan.FromSeconds(30)),
			cancellationToken);

		Assert.True(coordinator.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)));
		await Task.Delay(200, cancellationToken);
		Assert.False(closeRequest.IsCompleted);

		coordinator.Dispose();
		await closeRequest.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
	}

	[Fact]
	public void CloseRequestReleasesTheHandlerAfterTheGracePeriod()
	{
		using var coordinator = new TerminalCancellationCoordinator(registerSystemHandlers: false);
		var elapsed = System.Diagnostics.Stopwatch.StartNew();

		coordinator.HandleCloseRequest(static () => { }, TimeSpan.FromMilliseconds(150));

		Assert.True(coordinator.Token.IsCancellationRequested);
		Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(100));
	}

	[Fact]
	public async Task DisposeDoesNotSuppressOrDisposeAnAcceptedInFlightInterrupt()
	{
		var coordinator = new TerminalCancellationCoordinator(registerSystemHandlers: false);
		var cancellationToken = TestContext.Current.CancellationToken;
		using var cancellationEntered = new ManualResetEventSlim();
		using var releaseCancellation = new ManualResetEventSlim();
		using var registration = coordinator.Token.Register(() =>
		{
			cancellationEntered.Set();
			Assert.True(
				releaseCancellation.Wait(TimeSpan.FromSeconds(10), cancellationToken),
				"The cancellation callback was not released.");
		});
		var nativeActionSuppressed = false;
		var interrupt = Task.Run(() => coordinator.TryHandleInterrupt(
			() => nativeActionSuppressed = true));

		Assert.True(
			cancellationEntered.Wait(TimeSpan.FromSeconds(10), cancellationToken),
			"The interrupt did not enter synchronous cancellation.");
		coordinator.Dispose();
		var suppressedAfterDispose = false;
		Assert.False(coordinator.TryHandleInterrupt(() => suppressedAfterDispose = true));
		releaseCancellation.Set();

		Assert.True(await interrupt);
		Assert.True(nativeActionSuppressed);
		Assert.False(suppressedAfterDispose);
	}
}
