using DevProjex.Infrastructure.Git;
using DevProjex.Kernel.Abstractions;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalRepositoryCloneCoordinatorTests
{
	private const string RepositoryUrl = "https://example.test/owner/repository.git";

	[Fact]
	public async Task RepeatedClone_ReusesPublishedCacheWithoutCloningAgain()
	{
		using var data = new TemporaryDirectory();
		var git = new CountingCloneService();
		var cache = new RepoCacheService(Path.Combine(data.Path, "RepoCache"));
		var coordinator = new TerminalRepositoryCloneCoordinator(git, cache);

		string firstPath;
		using (var first = await coordinator.AcquireAsync(
		       RepositoryUrl,
		       progress: null,
		       phaseChanged: null,
		       TestContext.Current.CancellationToken))
		{
			firstPath = first.Result.LocalPath;
		}

		using var second = await coordinator.AcquireAsync(
			RepositoryUrl,
			progress: null,
			phaseChanged: null,
			TestContext.Current.CancellationToken);

		Assert.Equal(1, git.CloneCount);
		Assert.Equal(firstPath, second.Result.LocalPath, PathComparer.Default);
		Assert.Single(cache.ListIndexedRepositories());
	}

	[Fact]
	public async Task ConcurrentClone_UsesOnePublishedCacheContainer()
	{
		using var data = new TemporaryDirectory();
		var git = new CountingCloneService(TimeSpan.FromMilliseconds(100));
		var cache = new RepoCacheService(Path.Combine(data.Path, "RepoCache"));
		var coordinator = new TerminalRepositoryCloneCoordinator(git, cache);

		var firstTask = coordinator.AcquireAsync(
			RepositoryUrl,
			progress: null,
			phaseChanged: null,
			TestContext.Current.CancellationToken);
		var secondTask = coordinator.AcquireAsync(
			RepositoryUrl,
			progress: null,
			phaseChanged: null,
			TestContext.Current.CancellationToken);
		var leases = await Task.WhenAll(firstTask, secondTask);
		using var first = leases[0];
		using var second = leases[1];

		Assert.Equal(1, git.CloneCount);
		Assert.Equal(first.Result.LocalPath, second.Result.LocalPath, PathComparer.Default);
		Assert.Single(cache.ListIndexedRepositories());
	}

	private sealed class CountingCloneService(TimeSpan? delay = null) : IGitRepositoryService
	{
		private int _cloneCount;

		public int CloneCount => Volatile.Read(ref _cloneCount);

		public Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default) =>
			Task.FromResult(true);

		public async Task<GitCloneResult> CloneAsync(
			string url,
			string targetDirectory,
			IProgress<string>? progress = null,
			CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _cloneCount);
			if (delay is not null)
				await Task.Delay(delay.Value, cancellationToken);
			await File.WriteAllTextAsync(
				Path.Combine(targetDirectory, "README.md"),
				"cached clone",
				cancellationToken);
			return new GitCloneResult(
				true,
				targetDirectory,
				ProjectSourceType.GitClone,
				"main",
				"repository",
				url,
				null);
		}

		public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<string?> GetDefaultBranchAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<bool> SwitchBranchAsync(
			string repositoryPath,
			string branchName,
			IProgress<string>? progress = null,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<bool> PullUpdatesAsync(
			string repositoryPath,
			IProgress<string>? progress = null,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<string?> GetHeadCommitAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<string?> GetCurrentBranchAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<string?> GetRemoteUrlAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}
}
