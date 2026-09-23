namespace DevProjex.Tests.Terminal;

using DevProjex.Infrastructure.ProjectProfiles;

public sealed class TerminalSelectionProfilePersistenceCoordinatorTests
{
	private static readonly string[] FailureChoiceKeys =
	[
		"Terminal.Tui.ProfileSaveFailure.Title",
		"Terminal.Tui.ProfileSaveFailure.Message",
		"Terminal.Tui.ProfileSaveFailure.Stay",
		"Terminal.Tui.ProfileSaveFailure.ExitWithoutSaving",
		"Terminal.Tui.ProfileSaveFailure.ContinueWithoutSaving",
		"SelectionPersistence.Saving",
		"SelectionPersistence.Failed",
		"SelectionPersistence.Failed.Help"
	];

	[Fact]
	public async Task SchedulePublishesPendingSavingAndIdleStates()
	{
		var delay = new ControlledDelay();
		var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var coordinator = new TerminalSelectionProfilePersistenceCoordinator(
			async (_, _, cancellationToken) =>
			{
				writeStarted.TrySetResult();
				await releaseWrite.Task.WaitAsync(cancellationToken);
			},
			delay.WaitAsync);

		Assert.Equal(TerminalSelectionPersistencePhase.Idle, coordinator.State.Phase);
		coordinator.Schedule("project", CreateProfile(["src"]));
		Assert.Equal(TerminalSelectionPersistencePhase.Pending, coordinator.State.Phase);

		delay.Release();
		await writeStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
		Assert.Equal(TerminalSelectionPersistencePhase.Saving, coordinator.State.Phase);

		releaseWrite.TrySetResult();
		await WaitForStateAsync(coordinator, TerminalSelectionPersistencePhase.Idle);
	}

	[Fact]
	public async Task FailedStateRemainsUntilThePendingSelectionIsSaved()
	{
		var delay = new ControlledDelay();
		var attempts = 0;
		using var coordinator = new TerminalSelectionProfilePersistenceCoordinator(
			(_, _, _) =>
			{
				attempts++;
				if (attempts == 1)
					throw new IOException("profile is locked");
				return Task.CompletedTask;
			},
			delay.WaitAsync,
			maxBackgroundAttempts: 1);

		coordinator.Schedule("project", CreateProfile(["src"]));
		delay.Release();
		await WaitForStateAsync(coordinator, TerminalSelectionPersistencePhase.Failed);

		Assert.Equal("profile is locked", coordinator.State.FailureReason);
		Assert.True(await coordinator.FlushAsync(TestContext.Current.CancellationToken));
		Assert.Equal(TerminalSelectionPersistencePhase.Idle, coordinator.State.Phase);
		Assert.Equal(2, attempts);
	}

	[Fact]
	public async Task DeferredStateRemainsUntilThePendingSelectionIsSaved()
	{
		var delay = new ControlledDelay();
		var attempts = 0;
		using var coordinator = new TerminalSelectionProfilePersistenceCoordinator(
			(_, _, _) => Task.FromResult(++attempts == 1
				? ProjectProfilePersistenceResult.Deferred("storage is unavailable")
				: ProjectProfilePersistenceResult.Saved()),
			delay.WaitAsync,
			maxBackgroundAttempts: 1);

		coordinator.Schedule("project", CreateProfile(["src"]));
		delay.Release();
		await WaitForStateAsync(coordinator, TerminalSelectionPersistencePhase.Deferred);

		Assert.Equal("storage is unavailable", coordinator.State.FailureReason);
		Assert.True(await coordinator.FlushAsync(TestContext.Current.CancellationToken));
		Assert.Equal(TerminalSelectionPersistencePhase.Idle, coordinator.State.Phase);
		Assert.Equal(2, attempts);
	}

	[Fact]
	public async Task ProvenUnchangedSelectionClearsThePendingWrite()
	{
		var delay = new ControlledDelay();
		var attempts = 0;
		using var coordinator = new TerminalSelectionProfilePersistenceCoordinator(
			(_, _, _) =>
			{
				attempts++;
				return Task.FromResult(ProjectProfilePersistenceResult.Unchanged());
			},
			delay.WaitAsync,
			maxBackgroundAttempts: 1);

		coordinator.Schedule("project", CreateProfile(["src"]));
		delay.Release();
		await WaitForStateAsync(coordinator, TerminalSelectionPersistencePhase.Idle);

		Assert.Equal(1, attempts);
		Assert.True(await coordinator.FlushAsync(TestContext.Current.CancellationToken));
		Assert.Equal(1, attempts);
	}

	[Fact]
	public void PersistenceFailureChoicesExistInEveryLocalization()
	{
		var localizationDirectory = Path.Combine(
			PublishedApplicationLocator.FindRepositoryRoot(),
			"Assets",
			"Localization");
		var files = Directory.GetFiles(localizationDirectory, "*.json");

		Assert.Equal(20, files.Length);
		foreach (var file in files)
		{
			using var document = JsonDocument.Parse(File.ReadAllText(file));
			foreach (var key in FailureChoiceKeys)
			{
				Assert.True(document.RootElement.TryGetProperty(key, out var value),
					$"Missing {key} in {Path.GetFileName(file)}");
				Assert.False(string.IsNullOrWhiteSpace(value.GetString()),
					$"Empty {key} in {Path.GetFileName(file)}");
			}
		}
	}

	[Fact]
	public async Task Schedule_CoalescesSelectionChangesAndPreservesNullEmptyDistinction()
	{
		var firstDelay = new ControlledDelay();
		var secondDelay = new ControlledDelay();
		var delays = new Queue<ControlledDelay>([firstDelay, secondDelay]);
		var writes = new List<ProjectSelectionProfile>();
		var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var coordinator = new TerminalSelectionProfilePersistenceCoordinator(
			(_, profile, _) =>
			{
				writes.Add(profile);
				completed.TrySetResult();
				return Task.CompletedTask;
			},
			cancellationToken => delays.Dequeue().WaitAsync(cancellationToken));

		coordinator.Schedule("project", CreateProfile(selectedPaths: null));
		await firstDelay.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
		coordinator.Schedule("project", CreateProfile([]));
		await firstDelay.Canceled.Task.WaitAsync(TestContext.Current.CancellationToken);
		await secondDelay.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
		secondDelay.Release();
		await completed.Task.WaitAsync(TestContext.Current.CancellationToken);

		var written = Assert.Single(writes);
		Assert.Empty(Assert.IsAssignableFrom<IReadOnlyCollection<string>>(written.SelectedPaths));
	}

	[Fact]
	public async Task Flush_WritesPendingSelectionWithoutWaitingForIdleTimer()
	{
		var delay = new ControlledDelay();
		ProjectSelectionProfile? written = null;
		using var coordinator = new TerminalSelectionProfilePersistenceCoordinator(
			(_, profile, _) =>
			{
				written = profile;
				return Task.CompletedTask;
			},
			delay.WaitAsync);

		coordinator.Schedule("project", CreateProfile(["src"]));
		await delay.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
		Assert.True(await coordinator.FlushAsync(TestContext.Current.CancellationToken));

		Assert.True(delay.Canceled.Task.IsCompleted);
		Assert.Equal(["src"], written!.SelectedPaths);
	}

	[Fact]
	public async Task FailedBackgroundWriteRemainsPendingAndFlushRetriesIt()
	{
		var delay = new ControlledDelay();
		var firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var attempts = 0;
		ProjectSelectionProfile? written = null;
		using var coordinator = new TerminalSelectionProfilePersistenceCoordinator(
			(_, profile, _) =>
			{
				attempts++;
				if (attempts == 1)
				{
					firstAttempt.TrySetResult();
					throw new IOException("locked");
				}
				written = profile;
				return Task.CompletedTask;
			},
			delay.WaitAsync,
			maxBackgroundAttempts: 1);

		coordinator.Schedule("project", CreateProfile(["src"]));
		await delay.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
		delay.Release();
		await firstAttempt.Task.WaitAsync(TestContext.Current.CancellationToken);
		Assert.True(await coordinator.FlushAsync(TestContext.Current.CancellationToken));

		Assert.Equal(2, attempts);
		Assert.Equal(["src"], written!.SelectedPaths);
	}

	[Fact]
	public async Task FlushWaitsForAnActiveWriteInsteadOfStartingACompetingWrite()
	{
		var delay = new ControlledDelay();
		var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var attempts = 0;
		using var coordinator = new TerminalSelectionProfilePersistenceCoordinator(
			async (_, _, _) =>
			{
				attempts++;
				writeStarted.TrySetResult();
				await releaseWrite.Task;
			},
			delay.WaitAsync);

		coordinator.Schedule("project", CreateProfile(["src"]));
		await delay.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
		delay.Release();
		await writeStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
		var flush = coordinator.FlushAsync(TestContext.Current.CancellationToken);

		Assert.False(flush.IsCompleted);
		releaseWrite.TrySetResult();
		Assert.True(await flush);
		Assert.Equal(1, attempts);
	}

	[Fact]
	public async Task FlushReportsFailureAfterRetriesAndRetainsPendingSelection()
	{
		var delay = new ControlledDelay();
		var attempts = 0;
		using var coordinator = new TerminalSelectionProfilePersistenceCoordinator(
			(_, _, _) =>
			{
				attempts++;
				throw new IOException("locked");
			},
			delay.WaitAsync,
			retryDelayAsync: static (_, _) => Task.CompletedTask,
			maxBackgroundAttempts: 2);

		coordinator.Schedule("project", CreateProfile(["src"]));
		await delay.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

		Assert.False(await coordinator.FlushAsync(TestContext.Current.CancellationToken));
		Assert.Equal(2, attempts);

		coordinator.DiscardPending();
		Assert.True(await coordinator.FlushAsync(TestContext.Current.CancellationToken));
		Assert.Equal(2, attempts);
		Assert.Equal(TerminalSelectionPersistencePhase.Idle, coordinator.State.Phase);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task DisposeAllowsAnActiveSelectionWriteToFinish(bool backgroundWrite)
	{
		var delay = new ControlledDelay();
		var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var coordinator = new TerminalSelectionProfilePersistenceCoordinator(
			async (_, _, _) =>
			{
				writeStarted.TrySetResult();
				await releaseWrite.Task;
			},
			delay.WaitAsync);

		try
		{
			coordinator.Schedule("project", CreateProfile(["src"]));
			if (backgroundWrite)
			{
				delay.Release();
				await writeStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
			}
			var flush = coordinator.FlushAsync(TestContext.Current.CancellationToken);
			await writeStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

			coordinator.Dispose();
			Assert.False(flush.IsCompleted);
			releaseWrite.TrySetResult();
			Assert.True(await flush.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
		}
		finally
		{
			releaseWrite.TrySetResult();
		}
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(false, true)]
	public async Task QueuedFlushDoesNotRewriteCompletedOrDiscardedSelection(bool discardPending, bool dispose)
	{
		var delay = new ControlledDelay();
		var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var writeCount = 0;
		using var queuedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);
		using var coordinator = new TerminalSelectionProfilePersistenceCoordinator(
			async (_, _, _) =>
			{
				writeCount++;
				writeStarted.TrySetResult();
				await releaseWrite.Task;
			},
			delay.WaitAsync);

		try
		{
			coordinator.Schedule("project", CreateProfile(["src"]));
			var activeFlush = coordinator.FlushAsync(TestContext.Current.CancellationToken);
			await writeStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
			var queuedFlush = coordinator.FlushAsync(queuedCancellation.Token);
			Assert.False(queuedFlush.IsCompleted);

			if (dispose)
				coordinator.Dispose();
			else if (discardPending)
				coordinator.DiscardPending();

			releaseWrite.TrySetResult();
			Assert.True(await activeFlush.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
			Assert.True(await queuedFlush.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
			Assert.Equal(1, writeCount);
		}
		finally
		{
			releaseWrite.TrySetResult();
			await queuedCancellation.CancelAsync();
		}
	}

	[Fact]
	public async Task CanceledQueuedFlushDoesNotPreventAnotherFlushFromDrainingDuringDispose()
	{
		var delay = new ControlledDelay();
		var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var writeCount = 0;
		using var queuedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);
		using var drainingCancellation = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);
		using var coordinator = new TerminalSelectionProfilePersistenceCoordinator(
			async (_, _, _) =>
			{
				writeCount++;
				writeStarted.TrySetResult();
				await releaseWrite.Task;
			},
			delay.WaitAsync);

		try
		{
			coordinator.Schedule("project", CreateProfile(["src"]));
			var activeFlush = coordinator.FlushAsync(TestContext.Current.CancellationToken);
			await writeStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
			var canceledFlush = coordinator.FlushAsync(queuedCancellation.Token);
			await queuedCancellation.CancelAsync();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledFlush);
			var queuedFlush = coordinator.FlushAsync(drainingCancellation.Token);

			coordinator.Dispose();
			releaseWrite.TrySetResult();
			Assert.True(await activeFlush.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
			Assert.True(await queuedFlush.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
			Assert.Equal(1, writeCount);
		}
		finally
		{
			releaseWrite.TrySetResult();
			await drainingCancellation.CancelAsync();
		}
	}

	private static ProjectSelectionProfile CreateProfile(IReadOnlyCollection<string>? selectedPaths) =>
		new([], [".cs"], [], SelectedPaths: selectedPaths);

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
		TerminalSelectionProfilePersistenceCoordinator coordinator,
		TerminalSelectionPersistencePhase phase)
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
