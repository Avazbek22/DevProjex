namespace DevProjex.Tests.Unit;

public sealed class CachedRepositoryRefreshCoordinatorTests
{
	[Fact]
	public async Task RefreshAsync_SwitchesToDefaultBranchBeforePulling()
	{
		var git = new RecordingGitRepositoryService();
		var phases = new List<CachedRepositoryRefreshPhase>();

		var result = await CachedRepositoryRefreshCoordinator.RefreshAsync(
			git,
			"repository",
			"feature",
			phase =>
			{
				phases.Add(phase);
				return Task.CompletedTask;
			},
			progress: null,
			TestContext.Current.CancellationToken);

		Assert.False(result.UpdateFailed);
		Assert.Equal("main", result.Branch);
		Assert.Equal(
			["default", "switch:main", "current", "pull:main", "current"],
			git.Operations);
		Assert.Equal(
			[CachedRepositoryRefreshPhase.SwitchingBranch, CachedRepositoryRefreshPhase.GettingUpdates],
			phases);
	}

	[Fact]
	public async Task RefreshAsync_WhenRemoteFails_ReturnsLocalBranchWithoutThrowing()
	{
		var git = new RecordingGitRepositoryService { FailPull = true };

		var result = await CachedRepositoryRefreshCoordinator.RefreshAsync(
			git,
			"repository",
			"feature",
			phaseChanged: null,
			progress: null,
			TestContext.Current.CancellationToken);

		Assert.True(result.UpdateFailed);
		Assert.Equal("main", result.Branch);
	}

	[Fact]
	public async Task RefreshAsync_WhenCurrentBranchProbeFails_KeepsSuccessfullySwitchedDefault()
	{
		var git = new RecordingGitRepositoryService { HideCurrentBranch = true };

		var result = await CachedRepositoryRefreshCoordinator.RefreshAsync(
			git,
			"repository",
			"feature",
			phaseChanged: null,
			progress: null,
			TestContext.Current.CancellationToken);

		Assert.False(result.UpdateFailed);
		Assert.Equal("main", result.Branch);
	}

	[Fact]
	public async Task RefreshAsync_DoesNotConvertCancellationIntoOfflineFallback()
	{
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		var git = new RecordingGitRepositoryService();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			CachedRepositoryRefreshCoordinator.RefreshAsync(
				git,
				"repository",
				"feature",
				phaseChanged: null,
				progress: null,
				cancellation.Token));
	}

	[Fact]
	public async Task RefreshAsync_DetectsCancellationSwallowedByBranchProbe()
	{
		using var cancellation = new CancellationTokenSource();
		var git = new RecordingGitRepositoryService
		{
			BeforeCurrentBranch = cancellation.Cancel
		};

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			CachedRepositoryRefreshCoordinator.RefreshAsync(
				git,
				"repository",
				"feature",
				phaseChanged: null,
				progress: null,
				cancellation.Token));

		Assert.DoesNotContain(git.Operations, operation => operation.StartsWith("pull:", StringComparison.Ordinal));
	}

	private sealed class RecordingGitRepositoryService : IGitRepositoryService
	{
		private string _branch = "feature";

		public List<string> Operations { get; } = [];

		public bool FailPull { get; init; }

		public bool HideCurrentBranch { get; init; }

		public Action? BeforeCurrentBranch { get; init; }

		public Task<string?> GetDefaultBranchAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Operations.Add("default");
			return Task.FromResult<string?>("main");
		}

		public Task<bool> SwitchBranchAsync(
			string repositoryPath,
			string branchName,
			IProgress<string>? progress = null,
			CancellationToken cancellationToken = default)
		{
			Operations.Add($"switch:{branchName}");
			_branch = branchName;
			return Task.FromResult(true);
		}

		public Task<bool> PullUpdatesAsync(
			string repositoryPath,
			IProgress<string>? progress = null,
			CancellationToken cancellationToken = default)
		{
			Operations.Add($"pull:{_branch}");
			if (FailPull)
				throw new IOException("Remote unavailable.");
			return Task.FromResult(true);
		}

		public Task<string?> GetCurrentBranchAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default)
		{
			Operations.Add("current");
			BeforeCurrentBranch?.Invoke();
			return Task.FromResult(HideCurrentBranch ? null : _branch);
		}

		public Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default) =>
			Task.FromResult(true);

		public Task<GitCloneResult> CloneAsync(
			string url,
			string targetDirectory,
			IProgress<string>? progress = null,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default) =>
			Task.FromResult<IReadOnlyList<GitBranch>>([]);

		public Task<string?> GetHeadCommitAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default) =>
			Task.FromResult<string?>(null);

		public Task<string?> GetRemoteUrlAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default) =>
			Task.FromResult<string?>(null);
	}
}
