using DevProjex.Avalonia.Coordinators;
using DevProjex.Avalonia.ViewModels;

namespace DevProjex.Tests.Integration;

public sealed class ProjectLoadLifecycleIntegrationTests
{
	[Fact]
	public async Task ProjectLoadCancellation_RestoresTheCompleteStableCheckpoint()
	{
		var projectA = ProjectState.Stable("A");
		var host = CreateHost(projectA);
		var reloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		host.ReloadHandler = async token =>
		{
			reloadStarted.TrySetResult();
			await Task.Delay(Timeout.InfiniteTimeSpan, token);
			return false;
		};
		using var pipeline = CreatePipeline(host);

		var loading = pipeline.OpenFolderAsync("B", fromDialog: false, recordRecentFolder: false);
		await reloadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
		pipeline.CancelActiveLoad();
		await loading;

		Assert.Equal(projectA, host.CurrentState);
		Assert.Equal(projectA, host.StableState);
	}

	[Fact]
	public async Task ProjectLoadFailureBeforePublication_RestoresTheCompleteStableCheckpoint()
	{
		var projectA = ProjectState.Stable("A");
		var host = CreateHost(projectA);
		host.ReloadHandler = _ => throw new IOException("tree build failed");
		using var pipeline = CreatePipeline(host);

		await Assert.ThrowsAsync<IOException>(() =>
			pipeline.OpenFolderAsync("B", fromDialog: false, recordRecentFolder: false));

		Assert.Equal(projectA, host.CurrentState);
		Assert.Equal(projectA, host.StableState);
	}

	[Fact]
	public async Task ProjectLoadFailureAfterPublication_KeepsThePublishedProject()
	{
		var host = CreateHost(ProjectState.Stable("A"));
		host.RecordRecentFolderHandler = _ => throw new IOException("recent store failed");
		using var pipeline = CreatePipeline(host);

		await Assert.ThrowsAsync<IOException>(() =>
			pipeline.OpenFolderAsync("B", fromDialog: false, recordRecentFolder: true));

		Assert.Equal(ProjectState.Stable("B"), host.CurrentState);
		Assert.Equal(ProjectState.Stable("B"), host.StableState);
	}

	[Fact]
	public async Task SupersededProjectLoads_CaptureTheLastStableProjectInsteadOfPartialState()
	{
		var projectA = ProjectState.Stable("A");
		var host = CreateHost(projectA);
		var firstReloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var reloadCount = 0;
		host.ReloadHandler = async token =>
		{
			if (Interlocked.Increment(ref reloadCount) == 1)
			{
				firstReloadStarted.TrySetResult();
				await Task.Delay(Timeout.InfiniteTimeSpan, token);
				return false;
			}

			host.CurrentState = ProjectState.Stable(host.CurrentState.Identity);
			return true;
		};
		using var pipeline = CreatePipeline(host);

		var loadingB = pipeline.OpenFolderAsync("B", fromDialog: false, recordRecentFolder: false);
		await firstReloadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
		var loadingC = pipeline.OpenFolderAsync("C", fromDialog: false, recordRecentFolder: false);
		await Task.WhenAll(loadingB, loadingC);

		Assert.Equal([projectA, projectA], host.CapturedCheckpoints);
		Assert.Equal(ProjectState.Stable("C"), host.CurrentState);
		Assert.Equal(ProjectState.Stable("C"), host.StableState);
	}

	[Fact]
	public async Task SupersededProjectLoads_CancelingReplacementRestoresTheLastStableProject()
	{
		var projectA = ProjectState.Stable("A");
		var host = CreateHost(projectA);
		var firstReloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var secondReloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var reloadCount = 0;
		host.ReloadHandler = async token =>
		{
			if (Interlocked.Increment(ref reloadCount) == 1)
				firstReloadStarted.TrySetResult();
			else
				secondReloadStarted.TrySetResult();

			await Task.Delay(Timeout.InfiniteTimeSpan, token);
			return false;
		};
		using var pipeline = CreatePipeline(host);

		var loadingB = pipeline.OpenFolderAsync("B", fromDialog: false, recordRecentFolder: false);
		await firstReloadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
		var loadingC = pipeline.OpenFolderAsync("C", fromDialog: false, recordRecentFolder: false);
		await secondReloadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
		pipeline.CancelActiveLoad();
		await Task.WhenAll(loadingB, loadingC);

		Assert.Equal([projectA, projectA], host.CapturedCheckpoints);
		Assert.Equal(projectA, host.CurrentState);
		Assert.Equal(projectA, host.StableState);
	}

	private static LifecycleHost CreateHost(ProjectState stableState)
	{
		var localization = ProjectLoadWorkflowRuntime.CreateLocalizationService();
		var viewModel = new MainWindowViewModel(localization, new HelpContentProvider())
		{
			IsProjectLoaded = true
		};
		return new LifecycleHost(viewModel, stableState);
	}

	private static ProjectLoadPipeline CreatePipeline(LifecycleHost host)
	{
		var status = new StatusOperationCoordinator(
			host.ViewModel,
			isBackgroundMetricsActive: () => false,
			metricsOperationTextProvider: () => host.ViewModel.StatusOperationCalculatingData);
		return new ProjectLoadPipeline(host, status);
	}

	private sealed record ProjectState(
		string Identity,
		bool HideSecretsApplied,
		bool HidePrivateDataApplied,
		string TreeToken,
		string SearchQuery)
	{
		public static ProjectState Stable(string identity) =>
			new(identity, true, true, $"tree-{identity}", $"search-{identity}");

		public static ProjectState Loading(string identity) =>
			new(identity, false, false, string.Empty, string.Empty);
	}

	private sealed class LifecycleHost(
		MainWindowViewModel viewModel,
		ProjectState stableState) : IProjectLoadPipelineHost
	{
		private ProjectState? _checkpoint;

		public MainWindowViewModel ViewModel => viewModel;

		public string? CurrentCachedRepoPath => null;

		public ProjectState CurrentState { get; set; } = stableState;

		public ProjectState StableState { get; private set; } = stableState;

		public List<ProjectState> CapturedCheckpoints { get; } = [];

		public Func<CancellationToken, Task<bool>>? ReloadHandler { get; set; }

		public Func<CancellationToken, Task>? RecordRecentFolderHandler { get; set; }

		public void CaptureProjectLoadCancellationSnapshot()
		{
			_checkpoint = StableState;
			CapturedCheckpoints.Add(StableState);
		}

		public Task PrepareSearchAndFilterForProjectLoadAsync() => Task.CompletedTask;

		public void CancelBackgroundMemoryCleanup()
		{
		}

		public void CancelPreviewRefresh()
		{
		}

		public Task YieldProjectLoadStartupFrameAsync(CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.CompletedTask;
		}

		public void ClearPreviousProjectState(bool forceCompactingGc, bool preserveProjectSessions)
		{
			Assert.True(forceCompactingGc);
			Assert.True(preserveProjectSessions);
			CurrentState = ProjectState.Loading(CurrentState.Identity);
		}

		public void SetProjectLoadIdentity(string path, bool fromDialog)
		{
			_ = fromDialog;
			CurrentState = ProjectState.Loading(path);
		}

		public void UpdateTitle()
		{
		}

		public async Task<bool> ReloadProjectAsync(
			CancellationToken cancellationToken,
			bool applyStoredProfile)
		{
			Assert.True(applyStoredProfile);
			if (ReloadHandler is not null)
				return await ReloadHandler(cancellationToken);

			CurrentState = ProjectState.Stable(CurrentState.Identity);
			return true;
		}

		public async Task RecordRecentFolderAsync(string path, CancellationToken cancellationToken)
		{
			_ = path;
			if (RecordRecentFolderHandler is not null)
				await RecordRecentFolderHandler(cancellationToken);
		}

		public void ReleaseCurrentRepositorySession()
		{
		}

		public void ClearProjectLoadCancellation()
		{
			StableState = CurrentState;
			_checkpoint = null;
		}

		public bool TryApplyActiveProjectLoadCancellationFallback()
		{
			if (_checkpoint is null)
				return false;

			CurrentState = _checkpoint;
			_checkpoint = null;
			return true;
		}

		public void ScheduleProjectLoadMemoryCleanup(bool hadLoadedProjectBefore)
		{
			_ = hadLoadedProjectBefore;
		}

		public void ShowLoadCanceledToast()
		{
		}
	}
}
