namespace DevProjex.Tests.Unit.Avalonia;

public sealed class TreeSelectionProfilePersistenceCoordinatorTests
{
	[Fact]
	public async Task Schedule_CoalescesToTheLatestSelectionAfterTheDelay()
	{
		var delays = new Queue<ControlledDelay>();
		var firstDelay = new ControlledDelay();
		var secondDelay = new ControlledDelay();
		delays.Enqueue(firstDelay);
		delays.Enqueue(secondDelay);
		var writes = new List<(string ProjectPath, IReadOnlyCollection<string>? SelectedPaths)>();
		var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var coordinator = new TreeSelectionProfilePersistenceCoordinator(
			(projectPath, selectedPaths, _) =>
			{
				writes.Add((projectPath, selectedPaths));
				written.TrySetResult();
				return Task.CompletedTask;
			},
			cancellationToken => delays.Dequeue().WaitAsync(cancellationToken));

		coordinator.Schedule(@"C:\Project", ["src"]);
		await firstDelay.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
		coordinator.Schedule(@"C:\Project", ["tests"]);
		await firstDelay.Canceled.Task.WaitAsync(TestContext.Current.CancellationToken);
		await secondDelay.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

		secondDelay.Release();
		await written.Task.WaitAsync(TestContext.Current.CancellationToken);

		var write = Assert.Single(writes);
		Assert.Equal(Path.GetFullPath(@"C:\Project"), write.ProjectPath);
		Assert.Equal(["tests"], write.SelectedPaths);
	}

	[Fact]
	public async Task Flush_WritesPendingSelectionWithoutWaitingForTheTimer()
	{
		var delay = new ControlledDelay();
		var writes = new List<IReadOnlyCollection<string>?>();
		using var coordinator = new TreeSelectionProfilePersistenceCoordinator(
			(_, selectedPaths, _) =>
			{
				writes.Add(selectedPaths);
				return Task.CompletedTask;
			},
			delay.WaitAsync);

		coordinator.Schedule(@"C:\Project", []);
		await delay.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
		await coordinator.FlushAsync(TestContext.Current.CancellationToken);

		Assert.True(delay.Canceled.Task.IsCompleted);
		Assert.Empty(Assert.IsAssignableFrom<IReadOnlyCollection<string>>(Assert.Single(writes)));
	}

	[Fact]
	public async Task Flush_WaitsForSelectionWriteThatStartedAfterTheDelay()
	{
		var delay = new ControlledDelay();
		var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var coordinator = new TreeSelectionProfilePersistenceCoordinator(
			async (_, _, cancellationToken) =>
			{
				writeStarted.TrySetResult();
				await releaseWrite.Task.WaitAsync(cancellationToken);
			},
			delay.WaitAsync);

		coordinator.Schedule(@"C:\Project", ["src"]);
		await delay.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
		delay.Release();
		await writeStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

		var flush = coordinator.FlushAsync(TestContext.Current.CancellationToken);
		Assert.False(flush.IsCompleted);
		releaseWrite.TrySetResult();
		await flush;
	}

	[Fact]
	public async Task ConcurrentFlush_RetriesSelectionWhenTheFirstWriteIsCanceled()
	{
		var delay = new ControlledDelay();
		var firstWriteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var writes = new List<IReadOnlyCollection<string>?>();
		using var firstCancellation = new CancellationTokenSource();
		using var coordinator = new TreeSelectionProfilePersistenceCoordinator(
			async (_, selectedPaths, cancellationToken) =>
			{
				writes.Add(selectedPaths);
				if (writes.Count == 1)
				{
					firstWriteStarted.TrySetResult();
					await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
				}
			},
			delay.WaitAsync);

		coordinator.Schedule(@"C:\Project", ["src"]);
		await delay.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
		var firstFlush = coordinator.FlushAsync(firstCancellation.Token);
		await firstWriteStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

		var secondFlush = coordinator.FlushAsync(TestContext.Current.CancellationToken);
		Assert.False(secondFlush.IsCompleted);
		firstCancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstFlush);
		await secondFlush;

		Assert.Equal(2, writes.Count);
		Assert.All(writes, selectedPaths => Assert.Equal(["src"], selectedPaths));
	}

	[Fact]
	public async Task CancelPending_DoesNotWriteTheSelection()
	{
		var delay = new ControlledDelay();
		var writeCount = 0;
		using var coordinator = new TreeSelectionProfilePersistenceCoordinator(
			(_, _, _) =>
			{
				writeCount++;
				return Task.CompletedTask;
			},
			delay.WaitAsync);

		coordinator.Schedule(@"C:\Project", selectedPaths: null);
		await delay.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
		coordinator.CancelPending();
		await delay.Canceled.Task.WaitAsync(TestContext.Current.CancellationToken);

		Assert.Equal(0, writeCount);
	}

	private sealed class ControlledDelay
	{
		private readonly TaskCompletionSource _release =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public TaskCompletionSource Canceled { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Started { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public async Task WaitAsync(CancellationToken cancellationToken)
		{
			Started.TrySetResult();
			try
			{
				await _release.Task.WaitAsync(cancellationToken);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				Canceled.TrySetResult();
				throw;
			}
		}

		public void Release() => _release.TrySetResult();
	}
}
