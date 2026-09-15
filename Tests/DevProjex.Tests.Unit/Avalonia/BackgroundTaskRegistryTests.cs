using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class BackgroundTaskRegistryTests
{
	[Fact]
	public async Task RegisteredFaultIsReportedWithItsOperationName()
	{
		var reported = new TaskCompletionSource<(string Operation, Exception Error)>(
			TaskCreationOptions.RunContinuationsAsynchronously);
		using var registry = new BackgroundTaskRegistry(
			reportFailure: (operation, error) => reported.TrySetResult((operation, error)));
		var failure = new InvalidOperationException("fixture failure");

		registry.Register(Task.FromException(failure), "RecalculateMetrics");
		var actual = await reported.Task.WaitAsync(TestContext.Current.CancellationToken);

		Assert.Equal("RecalculateMetrics", actual.Operation);
		Assert.Same(failure, actual.Error);
		Assert.Equal(0, registry.TrackedTaskCount);
	}

	[Fact]
	public void DisposeCancelsTheSharedLifetimeToken()
	{
		var registry = new BackgroundTaskRegistry();
		var token = registry.LifetimeToken;

		registry.Dispose();

		Assert.True(token.IsCancellationRequested);
	}
}
