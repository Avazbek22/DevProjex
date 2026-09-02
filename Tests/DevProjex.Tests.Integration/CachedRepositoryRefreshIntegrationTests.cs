namespace DevProjex.Tests.Integration;

[Collection(GitNetworkTestCollection.Name)]
public sealed class CachedRepositoryRefreshIntegrationTests : IDisposable
{
	private readonly string _cacheRoot = Path.Combine(
		Path.GetTempPath(),
		"DevProjex",
		"Tests",
		"CachedRepositoryRefresh",
		Guid.NewGuid().ToString("N"));

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_cacheRoot))
				Directory.Delete(_cacheRoot, recursive: true);
		}
		catch
		{
		}
	}

	[Fact]
	public async Task CachedNetworkOpen_SwitchesToDefaultThenFetchesLatestCommit()
	{
		var git = new GitRepositoryService();
		if (!await git.IsGitAvailableAsync(TestContext.Current.CancellationToken))
			return;
		await using var remote = await GitTestRepository.CreateAsync(
			"cached-refresh",
			cancellationToken: TestContext.Current.CancellationToken);
		var cache = new RepoCacheService(_cacheRoot);
		await PublishCloneAsync(cache, git, remote, TestContext.Current.CancellationToken);

		using (var featureSession = await cache.TryAcquireRepositorySessionAsync(
			       remote.RepositoryUrl,
			       cancellationToken: TestContext.Current.CancellationToken))
		{
			Assert.NotNull(featureSession);
			Assert.True(await git.SwitchBranchAsync(
				featureSession.RepositoryPath,
				remote.FeatureBranchName,
				cancellationToken: TestContext.Current.CancellationToken));
			cache.RecordIndexedRepository(
				remote.RepositoryUrl,
				featureSession.RepositoryPath,
				remote.FeatureBranchName);
		}

		await remote.AddCommitToBranchAsync(
			remote.DefaultBranchName,
			"fresh.txt",
			"fresh remote content",
			"advance default branch",
			TestContext.Current.CancellationToken);
		var remoteHead = await remote.GetBranchHeadAsync(
			remote.DefaultBranchName,
			TestContext.Current.CancellationToken);
		using var session = await cache.TryAcquireRepositorySessionAsync(
			remote.RepositoryUrl,
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.NotNull(session);

		var result = await CachedRepositoryRefreshCoordinator.RefreshAsync(
			git,
			session.RepositoryPath,
			session.Branch,
			phaseChanged: null,
			progress: null,
			TestContext.Current.CancellationToken);

		Assert.False(result.UpdateFailed);
		Assert.Equal(remote.DefaultBranchName, result.Branch);
		Assert.Equal(
			remoteHead,
			await git.GetHeadCommitAsync(
				session.RepositoryPath,
				TestContext.Current.CancellationToken));
		Assert.Equal(
			"fresh remote content",
			await File.ReadAllTextAsync(
				Path.Combine(session.RepositoryPath, "fresh.txt"),
				TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task CachedNetworkOpen_WhenRemoteIsUnavailable_KeepsUsableLocalCopy()
	{
		var git = new GitRepositoryService();
		if (!await git.IsGitAvailableAsync(TestContext.Current.CancellationToken))
			return;
		await using var remote = await GitTestRepository.CreateAsync(
			"cached-offline",
			cancellationToken: TestContext.Current.CancellationToken);
		var cache = new RepoCacheService(_cacheRoot);
		await PublishCloneAsync(cache, git, remote, TestContext.Current.CancellationToken);
		using var session = await cache.TryAcquireRepositorySessionAsync(
			remote.RepositoryUrl,
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.NotNull(session);
		var unavailableRemotePath = remote.BareRepositoryPath + ".offline";
		Directory.Move(remote.BareRepositoryPath, unavailableRemotePath);

		var result = await CachedRepositoryRefreshCoordinator.RefreshAsync(
			git,
			session.RepositoryPath,
			session.Branch,
			phaseChanged: null,
			progress: null,
			TestContext.Current.CancellationToken);

		Assert.True(result.UpdateFailed);
		Assert.True(File.Exists(Path.Combine(session.RepositoryPath, "README.md")));
		Assert.NotNull(await git.GetHeadCommitAsync(
			session.RepositoryPath,
			TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task LocalCacheOpen_RestoresLastBranchWithoutAvailableRemote()
	{
		var git = new GitRepositoryService();
		if (!await git.IsGitAvailableAsync(TestContext.Current.CancellationToken))
			return;
		await using var remote = await GitTestRepository.CreateAsync(
			"local-cache-offline",
			cancellationToken: TestContext.Current.CancellationToken);
		var cache = new RepoCacheService(_cacheRoot);
		var basePath = await PublishCloneAsync(cache, git, remote, TestContext.Current.CancellationToken);

		using (var featureSession = await cache.TryAcquireRepositorySessionAsync(
		       remote.RepositoryUrl,
		       cancellationToken: TestContext.Current.CancellationToken))
		{
			Assert.NotNull(featureSession);
			Assert.True(await git.SwitchBranchAsync(
				featureSession.RepositoryPath,
				remote.FeatureBranchName,
				cancellationToken: TestContext.Current.CancellationToken));
			cache.RecordIndexedRepository(
				remote.RepositoryUrl,
				featureSession.RepositoryPath,
				remote.FeatureBranchName);
		}

		Directory.Move(remote.BareRepositoryPath, remote.BareRepositoryPath + ".offline");
		using var reopened = await cache.TryAcquireRepositorySessionByPathAsync(
			basePath,
			TestContext.Current.CancellationToken);

		Assert.NotNull(reopened);
		Assert.Equal(remote.FeatureBranchName, reopened.Branch);
		Assert.Equal(
			"Feature branch payload",
			await File.ReadAllTextAsync(
				Path.Combine(reopened.RepositoryPath, "feature", "feature.txt"),
				TestContext.Current.CancellationToken));
	}

	private static async Task<string> PublishCloneAsync(
		RepoCacheService cache,
		GitRepositoryService git,
		GitTestRepository remote,
		CancellationToken cancellationToken)
	{
		var staging = cache.CreateRepositoryStagingDirectory(remote.RepositoryUrl);
		var clone = await git.CloneAsync(
			remote.RepositoryUrl,
			staging,
			cancellationToken: cancellationToken);
		Assert.True(clone.Success, clone.ErrorMessage);
		var published = cache.PublishRepositoryDirectory(staging, remote.RepositoryUrl);
		cache.RecordIndexedRepository(
			remote.RepositoryUrl,
			published,
			clone.DefaultBranch);
		return published;
	}
}
