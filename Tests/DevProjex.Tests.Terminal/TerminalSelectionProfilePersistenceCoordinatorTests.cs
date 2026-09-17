namespace DevProjex.Tests.Terminal;

public sealed class TerminalSelectionProfilePersistenceCoordinatorTests
{
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
		await coordinator.FlushAsync(TestContext.Current.CancellationToken);

		Assert.True(delay.Canceled.Task.IsCompleted);
		Assert.Equal(["src"], written!.SelectedPaths);
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
}
