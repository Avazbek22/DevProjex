using DevProjex.Infrastructure.RecentProjects;

namespace DevProjex.Tests.Integration;

public sealed class RepositoryCacheRecentProjectsIndependenceTests : IDisposable
{
	private readonly string _root = Path.Combine(
		Path.GetTempPath(),
		"DevProjex",
		"Tests",
		"CacheRecentIndependence",
		Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_root))
				Directory.Delete(_root, recursive: true);
		}
		catch
		{
		}
	}

	[Fact]
	public void DeletingCache_DoesNotRemoveRepositoryFromRecentLinks()
	{
		const string repositoryUrl = "https://github.com/example/history.git";
		var recent = new RecentProjectsStore(() => Path.Combine(_root, "state"));
		var cache = new RepoCacheService(Path.Combine(_root, "cache"));
		var recentState = recent.AddRepository(recent.Load(), repositoryUrl);
		var repositoryPath = PublishZip(cache, repositoryUrl);

		cache.DeleteRepositoryDirectory(repositoryPath);

		Assert.Empty(cache.ListIndexedRepositories());
		Assert.Contains(
			recent.Load().RecentRepositories,
			entry => RepositoryUrlUtility.AreEquivalent(entry.Url, repositoryUrl));
		Assert.Contains(
			recentState.RecentRepositories,
			entry => RepositoryUrlUtility.AreEquivalent(entry.Url, repositoryUrl));
	}

	[Fact]
	public void RemovingRecentLink_DoesNotDeleteCachedRepository()
	{
		const string repositoryUrl = "https://github.com/example/cached.git";
		var recent = new RecentProjectsStore(() => Path.Combine(_root, "state"));
		var cache = new RepoCacheService(Path.Combine(_root, "cache"));
		var repositoryPath = PublishZip(cache, repositoryUrl);
		var recentState = recent.AddRepository(recent.Load(), repositoryUrl);

		recent.RemoveRepository(recentState, repositoryUrl);

		Assert.DoesNotContain(
			recent.Load().RecentRepositories,
			entry => RepositoryUrlUtility.AreEquivalent(entry.Url, repositoryUrl));
		Assert.True(Directory.Exists(repositoryPath));
		Assert.Single(cache.ListIndexedRepositories());
	}

	[Fact]
	public void CacheEviction_DoesNotRemoveRepositoryFromRecentLinks()
	{
		const string repositoryUrl = "https://github.com/example/evicted.git";
		var recent = new RecentProjectsStore(() => Path.Combine(_root, "state"));
		var clock = new MutableTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
		var cache = new RepoCacheService(
			Path.Combine(_root, "cache"),
			new RepositoryCachePolicy(1, TimeSpan.FromDays(60)),
			clock,
			new UnsupportedWorktreeManager());
		recent.AddRepository(recent.Load(), repositoryUrl);
		var repositoryPath = PublishZip(cache, repositoryUrl);

		cache.CollectGarbage();

		Assert.False(Directory.Exists(repositoryPath));
		Assert.Empty(cache.ListIndexedRepositories());
		Assert.Contains(
			recent.Load().RecentRepositories,
			entry => RepositoryUrlUtility.AreEquivalent(entry.Url, repositoryUrl));
	}

	private static string PublishZip(RepoCacheService cache, string repositoryUrl)
	{
		var staging = cache.CreateRepositoryStagingDirectory(repositoryUrl);
		File.WriteAllText(Path.Combine(staging, "payload.txt"), "cached payload");
		return cache.PublishRepositoryDirectory(staging, repositoryUrl);
	}

	private sealed class MutableTimeProvider(DateTimeOffset current) : TimeProvider
	{
		public override DateTimeOffset GetUtcNow() => current;
	}

	private sealed class UnsupportedWorktreeManager : IGitWorktreeManager
	{
		public Task<bool> IsSupportedAsync(string basePath, CancellationToken cancellationToken) => Task.FromResult(false);
		public Task<bool> PreparePrimaryAsync(string basePath, string? branch, CancellationToken cancellationToken) => Task.FromResult(true);
		public Task<bool> CreateDetachedAsync(string basePath, string worktreePath, string? branch, CancellationToken cancellationToken) => Task.FromResult(false);
		public Task RemoveAsync(string basePath, string worktreePath, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task PruneAsync(string basePath, CancellationToken cancellationToken) => Task.CompletedTask;
	}
}
