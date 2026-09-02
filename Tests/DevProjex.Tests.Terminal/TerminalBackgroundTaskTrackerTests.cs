namespace DevProjex.Tests.Terminal;

public sealed class TerminalBackgroundTaskTrackerTests
{
	[Fact]
	public async Task CompleteAsyncWaitsForEveryTrackedTaskAfterCallersReplaceTheirReferences()
	{
		var tracker = new TerminalBackgroundTaskTracker();
		var superseded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var current = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_ = tracker.Track(superseded.Task);
		_ = tracker.Track(current.Task);

		var completion = tracker.CompleteAsync();
		current.SetResult();
		await Task.Yield();

		Assert.False(completion.IsCompleted);
		superseded.SetResult();
		await completion.WaitAsync(
			TimeSpan.FromSeconds(1),
			TestContext.Current.CancellationToken);
	}
}
