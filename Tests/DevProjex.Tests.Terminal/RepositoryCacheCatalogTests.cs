using DevProjex.Infrastructure.Git;
using DevProjex.Kernel.Abstractions;

namespace DevProjex.Tests.Terminal;

public sealed class RepositoryCacheCatalogTests
{
	[Fact]
	public async Task ReadyCacheIsResolvedByOriginWithoutContactingTheNetwork()
	{
		using var temporary = new TemporaryDirectory();
		var cacheRoot = temporary.CreateDirectory("RepoCache");
		var cachePath = Path.Combine(cacheRoot, "DevProjex_8DEEC71CEE019B1");
		Directory.CreateDirectory(Path.Combine(cachePath, ".git"));
		var git = new FakeGitRepositoryService();
		git.Repositories[cachePath] = new FakeRepository(
			"git@github.com:Avazbek22/DevProjex.git",
			"main",
			"0123456789abcdef");
		var catalog = new RepositoryCacheCatalog(
			git,
			new RepoCacheService(cacheRoot));

		var result = await catalog.FindAsync(
			"https://github.com/Avazbek22/DevProjex",
			TestContext.Current.CancellationToken);

		Assert.Equal(RepositoryCacheState.Ready, result.State);
		Assert.Equal("DevProjex", result.RepositoryName);
		Assert.Equal(cachePath, result.LocalPath, PathComparer.Default);
		Assert.Equal("main", result.Branch);
		Assert.Equal("0123456789abcdef", result.CommitHash);
		Assert.Equal(0, git.NetworkOperationCount);
	}

	[Fact]
	public async Task EncodedPhysicalCacheNameIsResolvedByOriginUrl()
	{
		using var temporary = new TemporaryDirectory();
		var cacheRoot = temporary.CreateDirectory("RepoCache");
		var cachePath = Path.Combine(cacheRoot, "Repository%20Name_8DEEC71CEE019B1");
		Directory.CreateDirectory(Path.Combine(cachePath, ".git"));
		var git = new FakeGitRepositoryService();
		git.Repositories[cachePath] = new FakeRepository(
			"https://github.com/example/Repository%20Name.git",
			"main",
			"0123456789abcdef");
		var catalog = new RepositoryCacheCatalog(
			git,
			new RepoCacheService(cacheRoot));

		var result = await catalog.FindAsync(
			"https://github.com/example/Repository%20Name.git",
			TestContext.Current.CancellationToken);

		Assert.Equal(RepositoryCacheState.Ready, result.State);
		Assert.Equal("Repository Name", result.RepositoryName);
		Assert.Equal(cachePath, result.LocalPath, PathComparer.Default);
		Assert.Equal(0, git.NetworkOperationCount);
	}

	[Fact]
	public async Task MatchingCacheWithoutGitMetadataIsReportedAsDamaged()
	{
		using var temporary = new TemporaryDirectory();
		var cacheRoot = temporary.CreateDirectory("RepoCache");
		var cachePath = Path.Combine(cacheRoot, "DevProjex_8DEEC71CEE019B1");
		Directory.CreateDirectory(cachePath);
		var git = new FakeGitRepositoryService();
		git.Repositories[cachePath] = new FakeRepository(
			"https://github.com/Avazbek22/DevProjex",
			"main",
			"0123456789abcdef");
		var catalog = new RepositoryCacheCatalog(
			git,
			new RepoCacheService(cacheRoot));

		var result = await catalog.FindAsync(
			"https://github.com/Avazbek22/DevProjex",
			TestContext.Current.CancellationToken);

		Assert.Equal(RepositoryCacheState.Damaged, result.State);
		Assert.Equal(cachePath, result.LocalPath, PathComparer.Default);
		Assert.Equal(0, git.NetworkOperationCount);
	}

	[Fact]
	public async Task UnrelatedSameNameCacheDoesNotBecomeReady()
	{
		using var temporary = new TemporaryDirectory();
		var cacheRoot = temporary.CreateDirectory("RepoCache");
		var cachePath = Path.Combine(cacheRoot, "DevProjex_8DEEC71CEE019B1");
		Directory.CreateDirectory(Path.Combine(cachePath, ".git"));
		var git = new FakeGitRepositoryService();
		git.Repositories[cachePath] = new FakeRepository(
			"https://example.com/another/DevProjex",
			"main",
			"0123456789abcdef");
		var catalog = new RepositoryCacheCatalog(
			git,
			new RepoCacheService(cacheRoot));

		var result = await catalog.FindAsync(
			"https://github.com/Avazbek22/DevProjex",
			TestContext.Current.CancellationToken);

		Assert.Equal(RepositoryCacheState.Missing, result.State);
		Assert.Null(result.LocalPath);
		Assert.Equal(0, git.NetworkOperationCount);
	}

	[Fact]
	public async Task IndexedCacheAvoidsScanningUnrelatedRepositoryDirectories()
	{
		using var temporary = new TemporaryDirectory();
		var cacheRoot = temporary.CreateDirectory("RepoCache");
		var cachePath = Path.Combine(cacheRoot, "opaque-cache-identity");
		Directory.CreateDirectory(Path.Combine(cachePath, ".git"));
		var git = new FakeGitRepositoryService();
		git.Repositories[cachePath] = new FakeRepository(
			"https://github.com/Avazbek22/DevProjex",
			"main",
			"0123456789abcdef");
		for (var index = 0; index < 40; index++)
		{
			var unrelated = Path.Combine(cacheRoot, $"DevProjex_{index:D2}");
			Directory.CreateDirectory(Path.Combine(unrelated, ".git"));
			git.Repositories[unrelated] = new FakeRepository(
				$"https://example.com/owner/repository-{index:D2}",
				"main",
				index.ToString("x8"));
		}
		var cache = new RepoCacheService(cacheRoot);
		cache.RecordIndexedRepository(
			"https://github.com/Avazbek22/DevProjex",
			cachePath,
			"main",
			"0123456789abcdef");
		var catalog = new RepositoryCacheCatalog(git, cache);

		var result = await catalog.FindAsync(
			"git@github.com:Avazbek22/DevProjex.git",
			TestContext.Current.CancellationToken);

		Assert.Equal(RepositoryCacheState.Ready, result.State);
		Assert.Equal(cachePath, result.LocalPath, PathComparer.Default);
		Assert.Equal(1, git.RemoteQueryCount);
		Assert.Equal(0, git.NetworkOperationCount);
	}

	[Fact]
	public async Task IndexedCacheWithDifferentOriginIsRemovedAndNotOpened()
	{
		using var temporary = new TemporaryDirectory();
		var cacheRoot = temporary.CreateDirectory("RepoCache");
		var cachePath = Path.Combine(cacheRoot, "opaque-cache-identity");
		Directory.CreateDirectory(Path.Combine(cachePath, ".git"));
		var git = new FakeGitRepositoryService();
		git.Repositories[cachePath] = new FakeRepository(
			"https://example.com/another/repository.git",
			"main",
			"0123456789abcdef");
		var cache = new RepoCacheService(cacheRoot);
		const string requestedUrl = "https://github.com/owner/repository.git";
		cache.RecordIndexedRepository(requestedUrl, cachePath);
		var catalog = new RepositoryCacheCatalog(git, cache);

		var result = await catalog.FindAsync(
			requestedUrl,
			TestContext.Current.CancellationToken);

		Assert.Equal(RepositoryCacheState.Missing, result.State);
		Assert.Null(result.LocalPath);
		Assert.Null(cache.FindIndexedRepository(requestedUrl));
		Assert.Equal(0, git.NetworkOperationCount);
	}

	private sealed record FakeRepository(
		string RemoteUrl,
		string? Branch,
		string? Commit);

	private sealed class FakeGitRepositoryService : IGitRepositoryService
	{
		public Dictionary<string, FakeRepository> Repositories { get; } =
			new(PathComparer.Default);

		public int NetworkOperationCount { get; private set; }
		public int RemoteQueryCount { get; private set; }

		public Task<string?> GetRemoteUrlAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default)
		{
			RemoteQueryCount++;
			return Task.FromResult(
				Repositories.TryGetValue(repositoryPath, out var repository)
					? repository.RemoteUrl
					: null);
		}

		public Task<string?> GetCurrentBranchAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default) =>
			Task.FromResult(
				Repositories.TryGetValue(repositoryPath, out var repository)
					? repository.Branch
					: null);

		public Task<string?> GetHeadCommitAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default) =>
			Task.FromResult(
				Repositories.TryGetValue(repositoryPath, out var repository)
					? repository.Commit
					: null);

		public Task<bool> IsGitAvailableAsync(
			CancellationToken cancellationToken = default) =>
			Task.FromResult(true);

		public Task<GitCloneResult> CloneAsync(
			string url,
			string targetDirectory,
			IProgress<string>? progress = null,
			CancellationToken cancellationToken = default)
		{
			NetworkOperationCount++;
			throw new NotSupportedException();
		}

		public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default)
		{
			NetworkOperationCount++;
			throw new NotSupportedException();
		}

		public Task<string?> GetDefaultBranchAsync(
			string repositoryPath,
			CancellationToken cancellationToken = default)
		{
			NetworkOperationCount++;
			throw new NotSupportedException();
		}

		public Task<bool> SwitchBranchAsync(
			string repositoryPath,
			string branchName,
			IProgress<string>? progress = null,
			CancellationToken cancellationToken = default)
		{
			NetworkOperationCount++;
			throw new NotSupportedException();
		}

		public Task<bool> PullUpdatesAsync(
			string repositoryPath,
			IProgress<string>? progress = null,
			CancellationToken cancellationToken = default)
		{
			NetworkOperationCount++;
			throw new NotSupportedException();
		}
	}
}
