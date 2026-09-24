namespace DevProjex.Tests.Unit.Avalonia;

using DevProjex.Infrastructure.ProjectProfiles;

public sealed class TreeSelectionProfilePersistenceCoordinatorTests
{
	[Fact]
	public async Task Schedule_PublishesPendingSavingAndIdleStates()
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

		Assert.Equal(SelectionPersistencePhase.Idle, coordinator.State.Phase);
		coordinator.Schedule(@"C:\Project", ["src"]);
		Assert.Equal(SelectionPersistencePhase.Pending, coordinator.State.Phase);

		delay.Release();
		await writeStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
		Assert.Equal(SelectionPersistencePhase.Saving, coordinator.State.Phase);

		releaseWrite.TrySetResult();
		await WaitForStateAsync(coordinator, SelectionPersistencePhase.Idle);
		Assert.Null(coordinator.State.FailureReason);
	}

	[Fact]
	public async Task FailedWrite_RemainsFailedUntilThePendingSelectionIsSaved()
	{
		var delay = new ControlledDelay();
		var attempts = 0;
		using var coordinator = new TreeSelectionProfilePersistenceCoordinator(
			(_, _, _) =>
			{
				attempts++;
				if (attempts == 1)
					throw new IOException("profile is locked");
				return Task.CompletedTask;
			},
			delay.WaitAsync);

		coordinator.Schedule(@"C:\Project", ["src"]);
		delay.Release();
		await WaitForStateAsync(coordinator, SelectionPersistencePhase.Failed);

		Assert.Equal("profile is locked", coordinator.State.FailureReason);
		Assert.True(await coordinator.FlushAsync(TestContext.Current.CancellationToken));
		Assert.Equal(SelectionPersistencePhase.Idle, coordinator.State.Phase);
		Assert.Equal(2, attempts);
	}

	[Fact]
	public async Task DeferredWriteRemainsPendingUntilStorageBecomesAvailable()
	{
		var delay = new ControlledDelay();
		var attempts = 0;
		using var coordinator = new TreeSelectionProfilePersistenceCoordinator(
			(_, _, _) => Task.FromResult(++attempts == 1
				? ProjectProfilePersistenceResult.Deferred("profile is temporarily unavailable")
				: ProjectProfilePersistenceResult.Saved()),
			delay.WaitAsync);

		coordinator.Schedule(@"C:\Project", ["src"]);
		delay.Release();
		await WaitForStateAsync(coordinator, SelectionPersistencePhase.Deferred);

		Assert.Equal("profile is temporarily unavailable", coordinator.State.FailureReason);
		Assert.True(await coordinator.FlushAsync(TestContext.Current.CancellationToken));
		Assert.Equal(SelectionPersistencePhase.Idle, coordinator.State.Phase);
		Assert.Equal(2, attempts);
	}

	[Fact]
	public async Task ProvenUnchangedSelectionClearsThePendingWrite()
	{
		var delay = new ControlledDelay();
		var attempts = 0;
		using var coordinator = new TreeSelectionProfilePersistenceCoordinator(
			(_, _, _) =>
			{
				attempts++;
				return Task.FromResult(ProjectProfilePersistenceResult.Unchanged());
			},
			delay.WaitAsync);

		coordinator.Schedule(@"C:\Project", ["src"]);
		delay.Release();
		await WaitForStateAsync(coordinator, SelectionPersistencePhase.Idle);

		Assert.Equal(1, attempts);
		Assert.True(await coordinator.FlushAsync(TestContext.Current.CancellationToken));
		Assert.Equal(1, attempts);
	}

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
		Assert.Equal(SelectionPersistencePhase.Idle, coordinator.State.Phase);
	}

	[Fact]
	public async Task CancelPendingFinishesAnActiveWriteBeforeTheCallerClearsProfiles()
	{
		var delay = new ControlledDelay();
		var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var operations = new List<string>();
		using var coordinator = new TreeSelectionProfilePersistenceCoordinator(
			async (_, _, _) =>
			{
				writeStarted.TrySetResult();
				await releaseWrite.Task;
				operations.Add("write");
			},
			delay.WaitAsync);

		coordinator.Schedule(@"C:\Project", ["src"]);
		delay.Release();
		await writeStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
		var cancellation = coordinator.CancelPendingAndDrainAsync(TestContext.Current.CancellationToken);
		Assert.False(cancellation.IsCompleted);
		releaseWrite.TrySetResult();
		await cancellation;
		operations.Add("clear");

		Assert.Equal(["write", "clear"], operations);
	}

	[Fact]
	public async Task Dispose_AllowsAnActiveFlushToFinishWithoutReleasingADisposedGate()
	{
		var delay = new ControlledDelay();
		var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var coordinator = new TreeSelectionProfilePersistenceCoordinator(
			async (_, _, _) =>
			{
				writeStarted.TrySetResult();
				await releaseWrite.Task;
			},
			delay.WaitAsync);

		coordinator.Schedule(@"C:\Project", ["src"]);
		var flush = coordinator.FlushAsync(TestContext.Current.CancellationToken);
		await writeStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

		coordinator.Dispose();
		Assert.False(flush.IsCompleted);
		releaseWrite.TrySetResult();

		Assert.True(await flush.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
		Assert.Equal(SelectionPersistencePhase.Idle, coordinator.State.Phase);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Dispose_AllowsQueuedPersistenceCallsToDrain(bool cancelPending)
	{
		var delay = new ControlledDelay();
		var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var writeCount = 0;
		using var coordinator = new TreeSelectionProfilePersistenceCoordinator(
			async (_, _, _) =>
			{
				writeCount++;
				writeStarted.TrySetResult();
				await releaseWrite.Task;
			},
			delay.WaitAsync);
		using var queuedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);
		queuedCancellation.CancelAfter(TimeSpan.FromSeconds(5));

		coordinator.Schedule(@"C:\Project", ["src"]);
		var activeFlush = coordinator.FlushAsync(TestContext.Current.CancellationToken);
		await writeStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
		Task queuedCall = cancelPending
			? coordinator.CancelPendingAndDrainAsync(queuedCancellation.Token)
			: coordinator.FlushAsync(queuedCancellation.Token);
		Assert.False(queuedCall.IsCompleted);

		coordinator.Dispose();
		releaseWrite.TrySetResult();
		Assert.True(await activeFlush.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
		await queuedCall.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

		Assert.Equal(1, writeCount);
		Assert.Equal(SelectionPersistencePhase.Idle, coordinator.State.Phase);
	}

	[Fact]
	public async Task DisposedCoordinator_IgnoresNewFlushAndDrainRequests()
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

		coordinator.Dispose();
		coordinator.Schedule(@"C:\Project", ["src"]);

		Assert.True(await coordinator.FlushAsync(TestContext.Current.CancellationToken));
		await coordinator.CancelPendingAndDrainAsync(TestContext.Current.CancellationToken);
		Assert.Equal(0, writeCount);
		Assert.Equal(SelectionPersistencePhase.Idle, coordinator.State.Phase);
	}

	[Fact]
	public async Task FlushIncludesSelectionScheduledWhileItsFirstWriteIsInProgress()
	{
		var firstWriteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseFirstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var writes = new List<IReadOnlyCollection<string>?>();
		using var coordinator = new TreeSelectionProfilePersistenceCoordinator(
			async (_, selectedPaths, cancellationToken) =>
			{
				writes.Add(selectedPaths);
				if (writes.Count == 1)
				{
					firstWriteStarted.TrySetResult();
					await releaseFirstWrite.Task.WaitAsync(cancellationToken);
				}
			},
			static cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));

		try
		{
			coordinator.Schedule("project", ["src"]);
			var flush = coordinator.FlushAsync(TestContext.Current.CancellationToken);
			await firstWriteStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

			coordinator.Schedule("project", ["tests"]);
			releaseFirstWrite.TrySetResult();

			Assert.True(await flush.WaitAsync(TestContext.Current.CancellationToken));
			Assert.Collection(writes,
				selection => Assert.Equal(["src"], selection),
				selection => Assert.Equal(["tests"], selection));
			Assert.Equal(SelectionPersistencePhase.Idle, coordinator.State.Phase);
		}
		finally
		{
			releaseFirstWrite.TrySetResult();
		}
	}

	[Fact]
	public async Task QueuedFlushPreservesANewerSelectionScheduledDuringTheActiveWrite()
	{
		var delay = new ControlledDelay();
		var firstWriteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseFirstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var writes = new List<IReadOnlyCollection<string>?>();
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		cancellation.CancelAfter(TimeSpan.FromSeconds(10));
		using var coordinator = new TreeSelectionProfilePersistenceCoordinator(
			async (_, selectedPaths, cancellationToken) =>
			{
				writes.Add(selectedPaths);
				if (writes.Count == 1)
				{
					firstWriteStarted.TrySetResult();
					await releaseFirstWrite.Task.WaitAsync(cancellationToken);
				}
			},
			delay.WaitAsync);

		try
		{
			coordinator.Schedule("project", ["src"]);
			var activeFlush = coordinator.FlushAsync(cancellation.Token);
			await firstWriteStarted.Task.WaitAsync(cancellation.Token);
			var queuedFlush = coordinator.FlushAsync(cancellation.Token);
			Assert.False(queuedFlush.IsCompleted);

			coordinator.Schedule("project", ["tests"]);
			Assert.Equal(SelectionPersistencePhase.Pending, coordinator.State.Phase);
			releaseFirstWrite.TrySetResult();

			Assert.All(await Task.WhenAll(activeFlush, queuedFlush).WaitAsync(cancellation.Token), saved => Assert.True(saved));
			Assert.True(await coordinator.FlushAsync(cancellation.Token));
			Assert.Collection(writes,
				selection => Assert.Equal(["src"], selection),
				selection => Assert.Equal(["tests"], selection));
			Assert.Equal(SelectionPersistencePhase.Idle, coordinator.State.Phase);
		}
		finally
		{
			releaseFirstWrite.TrySetResult();
			await cancellation.CancelAsync();
		}
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

	private static async Task WaitForStateAsync(
		TreeSelectionProfilePersistenceCoordinator coordinator,
		SelectionPersistencePhase phase)
	{
		if (coordinator.State.Phase == phase)
			return;

		var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		void OnStateChanged(object? sender, EventArgs args)
		{
			if (coordinator.State.Phase == phase)
				reached.TrySetResult();
		}

		coordinator.StateChanged += OnStateChanged;
		try
		{
			if (coordinator.State.Phase == phase)
				return;
			await reached.Task.WaitAsync(TestContext.Current.CancellationToken);
		}
		finally
		{
			coordinator.StateChanged -= OnStateChanged;
		}
	}
}
