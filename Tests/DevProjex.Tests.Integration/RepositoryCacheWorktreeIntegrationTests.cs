namespace DevProjex.Tests.Integration;

public sealed class RepositoryCacheWorktreeIntegrationTests
{
	private static readonly TimeSpan BackgroundCleanupTimeout = TimeSpan.FromSeconds(15);

	[Fact]
	public async Task TwoWindows_SwitchingOneDetachedWorktreeDoesNotChangeTheOther()
	{
		await using var source = await GitTestRepository.CreateAsync(
			cancellationToken: TestContext.Current.CancellationToken);
		using var cache = new TemporaryDirectory();
		var firstStack = new RepoCacheService(cache.Path);
		var secondStack = new RepoCacheService(cache.Path);
		var git = new GitRepositoryService(allowFileTransportForTests: true);
		await PublishGitAsync(firstStack, git, source, TestContext.Current.CancellationToken);

		using var first = await firstStack.TryAcquireRepositorySessionAsync(
			source.RepositoryUrl,
			source.DefaultBranchName,
			TestContext.Current.CancellationToken);
		using var second = await secondStack.TryAcquireRepositorySessionAsync(
			source.RepositoryUrl,
			source.DefaultBranchName,
			TestContext.Current.CancellationToken);
		Assert.NotNull(first);
		Assert.NotNull(second);
		Assert.NotEqual(first.RepositoryPath, second.RepositoryPath, PathComparer.Default);
		Assert.True(await IsDetachedAsync(first.RepositoryPath, TestContext.Current.CancellationToken));
		Assert.True(await IsDetachedAsync(second.RepositoryPath, TestContext.Current.CancellationToken));
		var firstBefore = ReadWorkingTree(first.RepositoryPath);

		Assert.True(await git.SwitchBranchAsync(
			second.RepositoryPath,
			source.FeatureBranchName,
			cancellationToken: TestContext.Current.CancellationToken));

		Assert.Equal(firstBefore, ReadWorkingTree(first.RepositoryPath));
		Assert.False(File.Exists(Path.Combine(first.RepositoryPath, "feature", "feature.txt")));
		Assert.Equal(
			"Feature branch payload",
			File.ReadAllText(Path.Combine(second.RepositoryPath, "feature", "feature.txt")));
		Assert.Equal(
			source.DefaultBranchName,
			await git.GetCurrentBranchAsync(
				first.RepositoryPath,
				TestContext.Current.CancellationToken));
		Assert.Equal(
			source.FeatureBranchName,
			await git.GetCurrentBranchAsync(
				second.RepositoryPath,
				TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task TwoWindows_CanUseTheSameBranchAndReleasedCopyIsReused()
	{
		await using var source = await GitTestRepository.CreateAsync(
			cancellationToken: TestContext.Current.CancellationToken);
		using var cache = new TemporaryDirectory();
		var service = new RepoCacheService(cache.Path);
		var git = new GitRepositoryService(allowFileTransportForTests: true);
		var basePath = await PublishGitAsync(
			service,
			git,
			source,
			TestContext.Current.CancellationToken);

		var first = await service.TryAcquireRepositorySessionAsync(
			source.RepositoryUrl,
			source.DefaultBranchName,
			TestContext.Current.CancellationToken);
		var second = await service.TryAcquireRepositorySessionAsync(
			source.RepositoryUrl,
			source.DefaultBranchName,
			TestContext.Current.CancellationToken);
		Assert.NotNull(first);
		Assert.NotNull(second);
		Assert.Equal(basePath, first.RepositoryPath, PathComparer.Default);
		Assert.NotEqual(first.RepositoryPath, second.RepositoryPath, PathComparer.Default);
		Assert.Equal(ReadWorkingTree(first.RepositoryPath), ReadWorkingTree(second.RepositoryPath));

		first.Dispose();
		var reopened = await service.TryAcquireRepositorySessionAsync(
			source.RepositoryUrl,
			source.DefaultBranchName,
			TestContext.Current.CancellationToken);
		Assert.NotNull(reopened);
		Assert.Equal(basePath, reopened.RepositoryPath, PathComparer.Default);

		var extraCopyPath = second.RepositoryPath;
		second.Dispose();
		reopened.Dispose();
		using var afterCrashStyleRelease = await service.TryAcquireRepositorySessionAsync(
			source.RepositoryUrl,
			source.DefaultBranchName,
			TestContext.Current.CancellationToken);
		Assert.NotNull(afterCrashStyleRelease);
		Assert.Equal(basePath, afterCrashStyleRelease.RepositoryPath, PathComparer.Default);
		await WaitUntilAsync(() => !Directory.Exists(extraCopyPath));
	}

	[Fact]
	public async Task RequestedMissingBranch_FailsWithoutChangingIndexedMetadata()
	{
		await using var source = await GitTestRepository.CreateAsync(
			cancellationToken: TestContext.Current.CancellationToken);
		using var cache = new TemporaryDirectory();
		var service = new RepoCacheService(cache.Path);
		var git = new GitRepositoryService(allowFileTransportForTests: true);
		await PublishGitAsync(service, git, source, TestContext.Current.CancellationToken);
		var before = Assert.IsType<RepositoryCacheIndexEntry>(service.FindIndexedRepository(source.RepositoryUrl));

		var exception = await Assert.ThrowsAsync<RepositoryBranchUnavailableException>(
			() => service.TryAcquireRepositorySessionAsync(
				source.RepositoryUrl,
				"missing/branch",
				TestContext.Current.CancellationToken));

		Assert.Equal(RepositoryBranchUnavailableReason.NotFound, exception.Reason);
		var after = Assert.IsType<RepositoryCacheIndexEntry>(service.FindIndexedRepository(source.RepositoryUrl));
		Assert.Equal(before.Branch, after.Branch);
		Assert.Equal(before.LastUsedUtc, after.LastUsedUtc);
	}

	[Fact]
	public async Task IndexedBranchMissingLocally_IsFetchedAndRestoredFromOrigin()
	{
		await using var source = await GitTestRepository.CreateAsync(
			cancellationToken: TestContext.Current.CancellationToken);
		using var cache = new TemporaryDirectory();
		var service = new RepoCacheService(cache.Path);
		var git = new GitRepositoryService(allowFileTransportForTests: true);
		var basePath = await PublishGitAsync(service, git, source, TestContext.Current.CancellationToken);
		service.RecordIndexedRepository(source.RepositoryUrl, basePath, source.FeatureBranchName);
		await RunGitAsync(basePath, ["update-ref", "-d", $"refs/remotes/origin/{source.FeatureBranchName}"]);
		await RunGitAsync(basePath, ["update-ref", "-d", $"refs/heads/{source.FeatureBranchName}"]);

		using var session = await service.TryAcquireRepositorySessionAsync(
			source.RepositoryUrl,
			source.FeatureBranchName,
			TestContext.Current.CancellationToken);

		Assert.NotNull(session);
		Assert.Equal(source.FeatureBranchName, session.Branch);
		Assert.True(File.Exists(Path.Combine(session.RepositoryPath, "feature", "feature.txt")));
		Assert.Equal(
			source.FeatureBranchName,
			await git.GetCurrentBranchAsync(session.RepositoryPath, TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task IndexedBranchRemovedFromRepository_FailsInsteadOfOpeningHead()
	{
		await using var source = await GitTestRepository.CreateAsync(
			cancellationToken: TestContext.Current.CancellationToken);
		using var cache = new TemporaryDirectory();
		var service = new RepoCacheService(cache.Path);
		var git = new GitRepositoryService(allowFileTransportForTests: true);
		var basePath = await PublishGitAsync(service, git, source, TestContext.Current.CancellationToken);
		service.RecordIndexedRepository(source.RepositoryUrl, basePath, source.FeatureBranchName);
		await RunGitAsync(source.BareRepositoryPath, ["update-ref", "-d", $"refs/heads/{source.FeatureBranchName}"]);
		await RunGitAsync(basePath, ["update-ref", "-d", $"refs/remotes/origin/{source.FeatureBranchName}"]);
		await RunGitAsync(basePath, ["update-ref", "-d", $"refs/heads/{source.FeatureBranchName}"]);

		var exception = await Assert.ThrowsAsync<RepositoryBranchUnavailableException>(
			() => service.TryAcquireRepositorySessionAsync(
				source.RepositoryUrl,
				source.FeatureBranchName,
				TestContext.Current.CancellationToken));
		Assert.Equal(RepositoryBranchUnavailableReason.NotFound, exception.Reason);
	}

	[Fact]
	public async Task PullingOneSession_LeavesTheOtherSessionByteIdentical()
	{
		await using var source = await GitTestRepository.CreateAsync(
			cancellationToken: TestContext.Current.CancellationToken);
		using var cache = new TemporaryDirectory();
		var service = new RepoCacheService(cache.Path);
		var git = new GitRepositoryService(allowFileTransportForTests: true);
		await PublishGitAsync(service, git, source, TestContext.Current.CancellationToken);
		using var first = await service.TryAcquireRepositorySessionAsync(
			source.RepositoryUrl,
			source.DefaultBranchName,
			TestContext.Current.CancellationToken);
		using var second = await service.TryAcquireRepositorySessionAsync(
			source.RepositoryUrl,
			source.DefaultBranchName,
			TestContext.Current.CancellationToken);
		Assert.NotNull(first);
		Assert.NotNull(second);
		var secondBefore = ReadWorkingTree(second.RepositoryPath);

		await source.AddCommitToBranchAsync(
			source.DefaultBranchName,
			"src/new.txt",
			"new payload",
			"Update master",
			TestContext.Current.CancellationToken);
		Assert.True(await git.PullUpdatesAsync(
			first.RepositoryPath,
			cancellationToken: TestContext.Current.CancellationToken));

		Assert.Equal(
			"new payload",
			File.ReadAllText(Path.Combine(first.RepositoryPath, "src", "new.txt")));
		Assert.Equal(secondBefore, ReadWorkingTree(second.RepositoryPath));
		Assert.False(File.Exists(Path.Combine(second.RepositoryPath, "src", "new.txt")));
	}

	[Fact]
	public async Task ConcurrentInitialOpen_PerformsOneCloneAndReturnsTwoPinnedCopies()
	{
		await using var source = await GitTestRepository.CreateAsync(
			cancellationToken: TestContext.Current.CancellationToken);
		using var cache = new TemporaryDirectory();
		var cloneCount = 0;

		async Task<IRepositoryCacheSession> OpenAsync()
		{
			var service = new RepoCacheService(cache.Path);
			var git = new GitRepositoryService(allowFileTransportForTests: true);
			await using var operation = await service.AcquireRepositoryOperationAsync(
				source.RepositoryUrl,
				TestContext.Current.CancellationToken);
			var cached = await service.TryAcquireRepositorySessionAsync(
				source.RepositoryUrl,
				source.DefaultBranchName,
				TestContext.Current.CancellationToken);
			if (cached is not null)
				return cached;

			Interlocked.Increment(ref cloneCount);
			await PublishGitAsync(service, git, source, TestContext.Current.CancellationToken);
			return Assert.IsAssignableFrom<IRepositoryCacheSession>(
				await service.TryAcquireRepositorySessionAsync(
					source.RepositoryUrl,
					source.DefaultBranchName,
					TestContext.Current.CancellationToken));
		}

		var sessions = await Task.WhenAll(OpenAsync(), OpenAsync());
		try
		{
			Assert.Equal(1, cloneCount);
			Assert.NotEqual(sessions[0].RepositoryPath, sessions[1].RepositoryPath, PathComparer.Default);
			Assert.All(sessions, session => Assert.True(Directory.Exists(session.RepositoryPath)));
		}
		finally
		{
			foreach (var session in sessions)
				session.Dispose();
		}
	}

	[Fact]
	public async Task LegacyCheckout_IsMigratedInPlaceAndKeepsConcurrentSessionsDetached()
	{
		await using var source = await GitTestRepository.CreateAsync(
			cancellationToken: TestContext.Current.CancellationToken);
		using var cache = new TemporaryDirectory();
		var service = new RepoCacheService(cache.Path);
		var git = new GitRepositoryService(allowFileTransportForTests: true);
		var legacyPath = service.CreateRepositoryDirectory(source.RepositoryUrl);
		var result = await git.CloneAsync(
			source.RepositoryUrl,
			legacyPath,
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.True(result.Success);
		File.Delete(Path.Combine(cache.Path, RepositoryCacheLayout.MarkerFileName));
		service.RecordIndexedRepository(
			source.RepositoryUrl,
			legacyPath,
			source.DefaultBranchName);

		using var first = await service.TryAcquireRepositorySessionAsync(
			source.RepositoryUrl,
			source.DefaultBranchName,
			TestContext.Current.CancellationToken);
		using var second = await service.TryAcquireRepositorySessionAsync(
			source.RepositoryUrl,
			source.DefaultBranchName,
			TestContext.Current.CancellationToken);

		Assert.NotNull(first);
		Assert.NotNull(second);
		Assert.False(Directory.Exists(legacyPath));
		Assert.NotEqual(legacyPath, first.RepositoryPath, PathComparer.Default);
		Assert.NotEqual(first.RepositoryPath, second.RepositoryPath, PathComparer.Default);
		Assert.True(await IsDetachedAsync(first.RepositoryPath, TestContext.Current.CancellationToken));
		Assert.True(await IsDetachedAsync(second.RepositoryPath, TestContext.Current.CancellationToken));
		Assert.Equal("master branch payload", File.ReadAllText(
			Path.Combine(first.RepositoryPath, "src", "app.txt")));
		var firstBefore = ReadWorkingTree(first.RepositoryPath);

		Assert.True(await git.SwitchBranchAsync(
			second.RepositoryPath,
			source.FeatureBranchName,
			cancellationToken: TestContext.Current.CancellationToken));
		Assert.True(await IsDetachedAsync(second.RepositoryPath, TestContext.Current.CancellationToken));
		Assert.Equal(firstBefore, ReadWorkingTree(first.RepositoryPath));

		service.DeleteRepositoryDirectory(first.RepositoryPath);
		Assert.True(Directory.Exists(first.RepositoryPath));
	}

	[Fact]
	public async Task ZipUpdate_KeepsPinnedSnapshotImmutableUntilCollection()
	{
		const string repositoryUrl = "https://github.com/example/archive-only.git";
		using var cache = new TemporaryDirectory();
		var firstStack = new RepoCacheService(cache.Path);
		var secondStack = new RepoCacheService(cache.Path);
		var firstPath = PublishZip(firstStack, repositoryUrl, "old snapshot");
		var first = await firstStack.TryAcquireRepositorySessionAsync(
			repositoryUrl,
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.NotNull(first);

		var secondPath = PublishZip(secondStack, repositoryUrl, "new snapshot");
		using var second = await secondStack.TryAcquireRepositorySessionAsync(
			repositoryUrl,
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.NotNull(second);

		Assert.NotEqual(firstPath, secondPath, PathComparer.Default);
		Assert.Equal("old snapshot", File.ReadAllText(Path.Combine(first.RepositoryPath, "payload.txt")));
		Assert.Equal("new snapshot", File.ReadAllText(Path.Combine(second.RepositoryPath, "payload.txt")));
		secondStack.DeleteRepositoryDirectory(first.RepositoryPath);
		secondStack.ClearAllCache();
		Assert.True(Directory.Exists(first.RepositoryPath));
		Assert.True(Directory.Exists(second.RepositoryPath));
		secondStack.CollectGarbage();
		Assert.True(Directory.Exists(first.RepositoryPath));

		first.Dispose();
		secondStack.CollectGarbage();
		Assert.False(Directory.Exists(first.RepositoryPath));
	}

	[Fact]
	public async Task UnsupportedWorktreeFallback_UsesTheOriginalCheckoutForBothSessions()
	{
		await using var source = await GitTestRepository.CreateAsync(
			cancellationToken: TestContext.Current.CancellationToken);
		using var cache = new TemporaryDirectory();
		var publishingService = new RepoCacheService(cache.Path);
		var git = new GitRepositoryService(allowFileTransportForTests: true);
		var basePath = await PublishGitAsync(
			publishingService,
			git,
			source,
			TestContext.Current.CancellationToken);
		var firstStack = new RepoCacheService(
			cache.Path,
			RepositoryCachePolicy.Default,
			TimeProvider.System,
			new UnsupportedWorktreeManager());
		var secondStack = new RepoCacheService(
			cache.Path,
			RepositoryCachePolicy.Default,
			TimeProvider.System,
			new UnsupportedWorktreeManager());

		using var first = await firstStack.TryAcquireRepositorySessionAsync(
			source.RepositoryUrl,
			source.DefaultBranchName,
			TestContext.Current.CancellationToken);
		using var second = await secondStack.TryAcquireRepositorySessionAsync(
			source.RepositoryUrl,
			source.DefaultBranchName,
			TestContext.Current.CancellationToken);

		Assert.NotNull(first);
		Assert.NotNull(second);
		Assert.Equal(basePath, first.RepositoryPath, PathComparer.Default);
		Assert.Equal(basePath, second.RepositoryPath, PathComparer.Default);
		firstStack.DeleteRepositoryDirectory(basePath);
		Assert.True(Directory.Exists(basePath));
	}

	[Fact]
	public async Task UnsupportedWorktreeFallback_RejectsDifferentRequestedBranch()
	{
		await using var source = await GitTestRepository.CreateAsync(
			cancellationToken: TestContext.Current.CancellationToken);
		using var cache = new TemporaryDirectory();
		var publishingService = new RepoCacheService(cache.Path);
		var git = new GitRepositoryService(allowFileTransportForTests: true);
		var basePath = await PublishGitAsync(
			publishingService,
			git,
			source,
			TestContext.Current.CancellationToken);
		var service = new RepoCacheService(
			cache.Path,
			RepositoryCachePolicy.Default,
			TimeProvider.System,
			new UnsupportedWorktreeManager());

		var exception = await Assert.ThrowsAsync<RepositoryBranchUnavailableException>(
			() => service.TryAcquireRepositorySessionAsync(
				source.RepositoryUrl,
				source.FeatureBranchName,
				TestContext.Current.CancellationToken));

		Assert.Equal(RepositoryBranchUnavailableReason.WorktreeUnsupported, exception.Reason);
		Assert.Equal(source.DefaultBranchName, await git.GetCurrentBranchAsync(
			basePath,
			TestContext.Current.CancellationToken));
		Assert.Equal(source.DefaultBranchName, service.FindIndexedRepository(source.RepositoryUrl)?.Branch);
	}

	[Fact]
	public async Task TransientWorktreeProbeFailure_DoesNotRejectRequestedBranch()
	{
		await using var source = await GitTestRepository.CreateAsync(
			cancellationToken: TestContext.Current.CancellationToken);
		using var cache = new TemporaryDirectory();
		var publishingService = new RepoCacheService(cache.Path);
		var git = new GitRepositoryService(allowFileTransportForTests: true);
		await PublishGitAsync(
			publishingService,
			git,
			source,
			TestContext.Current.CancellationToken);
		var service = new RepoCacheService(
			cache.Path,
			RepositoryCachePolicy.Default,
			TimeProvider.System,
			new TransientProbeWorktreeManager());

		using var session = await service.TryAcquireRepositorySessionAsync(
			source.RepositoryUrl,
			source.FeatureBranchName,
			TestContext.Current.CancellationToken);

		Assert.NotNull(session);
		Assert.Equal(source.FeatureBranchName, session.Branch);
		Assert.True(File.Exists(Path.Combine(session.RepositoryPath, "feature", "feature.txt")));
	}

	[Fact]
	public async Task ReleasedRepository_IsEvictedAndNextOpenClonesAgain()
	{
		await using var source = await GitTestRepository.CreateAsync(
			cancellationToken: TestContext.Current.CancellationToken);
		using var cache = new TemporaryDirectory();
		using var cleanupCompleted = new ManualResetEventSlim();
		var cleanupCount = 0;
		RepoCacheService service = new(
			cache.Path,
			RepositoryCachePolicy.Default,
			TimeProvider.System,
			new GitWorktreeManager(),
			new RepoCacheTestHooks
			{
				AfterUnusedWorktreeCleanup = _ =>
				{
					if (Interlocked.Increment(ref cleanupCount) >= 2)
						cleanupCompleted.Set();
				}
			});
		var git = new GitRepositoryService(allowFileTransportForTests: true);
		var cloneCount = 0;

		async Task<IRepositoryCacheSession> OpenAsync()
		{
			await using var operation = await service.AcquireRepositoryOperationAsync(
				source.RepositoryUrl,
				TestContext.Current.CancellationToken);
			var cached = await service.TryAcquireRepositorySessionAsync(
				source.RepositoryUrl,
				source.DefaultBranchName,
				TestContext.Current.CancellationToken);
			if (cached is not null)
				return cached;
			cloneCount++;
			await PublishGitAsync(service, git, source, TestContext.Current.CancellationToken);
			return Assert.IsAssignableFrom<IRepositoryCacheSession>(
				await service.TryAcquireRepositorySessionAsync(
					source.RepositoryUrl,
					source.DefaultBranchName,
					TestContext.Current.CancellationToken));
		}

		var first = await OpenAsync();
		var firstPath = first.RepositoryPath;
		first.Dispose();
		using (var reused = await OpenAsync())
		{
			Assert.Equal(1, cloneCount);
			Assert.Equal(firstPath, reused.RepositoryPath, PathComparer.Default);
		}

		Assert.True(cleanupCompleted.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
		service = new RepoCacheService(
			cache.Path,
			new RepositoryCachePolicy(1, TimeSpan.FromDays(60)),
			TimeProvider.System,
			new GitWorktreeManager());
		service.CollectGarbage();
		Assert.Null(service.FindIndexedRepository(source.RepositoryUrl));
		using var afterEviction = await OpenAsync();
		Assert.Equal(2, cloneCount);
		Assert.NotEqual(firstPath, afterEviction.RepositoryPath, PathComparer.Default);
	}

	private static async Task<string> PublishGitAsync(
		RepoCacheService cache,
		GitRepositoryService git,
		GitTestRepository source,
		CancellationToken cancellationToken)
	{
		var staging = cache.CreateRepositoryStagingDirectory(source.RepositoryUrl);
		var result = await git.CloneAsync(
			source.RepositoryUrl,
			staging,
			cancellationToken: cancellationToken);
		Assert.True(result.Success, result.ErrorMessage);
		var published = cache.PublishRepositoryDirectory(staging, source.RepositoryUrl);
		cache.RecordIndexedRepository(
			source.RepositoryUrl,
			published,
			source.DefaultBranchName,
			await git.GetHeadCommitAsync(published, cancellationToken));
		return published;
	}

	private static string PublishZip(
		RepoCacheService cache,
		string repositoryUrl,
		string payload)
	{
		var staging = cache.CreateRepositoryStagingDirectory(repositoryUrl);
		File.WriteAllText(Path.Combine(staging, "payload.txt"), payload);
		return cache.PublishRepositoryDirectory(staging, repositoryUrl);
	}

	private static string[] ReadWorkingTree(string root) =>
		Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
			.Where(path => !Path.GetRelativePath(root, path)
				.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
				.Contains(".git", StringComparer.Ordinal))
			.Select(path => $"{Path.GetRelativePath(root, path)}:{Convert.ToBase64String(File.ReadAllBytes(path))}")
			.OrderBy(static value => value, StringComparer.Ordinal)
			.ToArray();

	private static async Task<bool> IsDetachedAsync(
		string repositoryPath,
		CancellationToken cancellationToken)
	{
		using var process = new Process
		{
			StartInfo = new ProcessStartInfo("git")
			{
				WorkingDirectory = repositoryPath,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			}
		};
		process.StartInfo.ArgumentList.Add("symbolic-ref");
		process.StartInfo.ArgumentList.Add("--quiet");
		process.StartInfo.ArgumentList.Add("HEAD");
		process.Start();
		await process.WaitForExitAsync(cancellationToken);
		return process.ExitCode != 0;
	}

	private static async Task RunGitAsync(string workingDirectory, IReadOnlyList<string> arguments)
	{
		var startInfo = new ProcessStartInfo(GitRuntime.GitExecutable)
		{
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		using var process = new Process
		{
			StartInfo = startInfo
		};
		process.Start();
		process.StandardInput.Close();
		var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		await process.WaitForExitAsync(TestContext.Current.CancellationToken);
		await Task.WhenAll(output, error);
		Assert.True(process.ExitCode == 0, await error);
	}

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		var stopwatch = Stopwatch.StartNew();
		while (!condition())
		{
			Assert.True(
				stopwatch.Elapsed < BackgroundCleanupTimeout,
				"The background worktree cleanup did not finish.");
			await Task.Delay(25, TestContext.Current.CancellationToken);
		}
	}

	private sealed class UnsupportedWorktreeManager : IGitWorktreeManager
	{
		public Task<WorktreeSupportState> GetSupportStateAsync(
			string basePath,
			CancellationToken cancellationToken) =>
			Task.FromResult(WorktreeSupportState.PermanentUnsupported);

		public Task<bool> PreparePrimaryAsync(
			string basePath,
			string? branch,
			CancellationToken cancellationToken) =>
			throw new InvalidOperationException("The fallback must not prepare a worktree.");

		public Task<bool> CreateDetachedAsync(
			string basePath,
			string worktreePath,
			string? branch,
			CancellationToken cancellationToken) =>
			throw new InvalidOperationException("The fallback must not create a worktree.");

		public Task RemoveAsync(
			string basePath,
			string worktreePath,
			CancellationToken cancellationToken) =>
			throw new InvalidOperationException("The fallback must not remove a worktree.");

		public Task PruneAsync(string basePath, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("The fallback must not prune worktrees.");
	}

	private sealed class TransientProbeWorktreeManager : IGitWorktreeManager
	{
		private readonly GitWorktreeManager _inner = new();

		public Task<WorktreeSupportState> GetSupportStateAsync(
			string basePath,
			CancellationToken cancellationToken) =>
			Task.FromResult(WorktreeSupportState.TransientFailure);

		public Task<bool> PreparePrimaryAsync(
			string basePath,
			string? branch,
			CancellationToken cancellationToken) =>
			_inner.PreparePrimaryAsync(basePath, branch, cancellationToken);

		public Task<bool> CreateDetachedAsync(
			string basePath,
			string worktreePath,
			string? branch,
			CancellationToken cancellationToken) =>
			_inner.CreateDetachedAsync(basePath, worktreePath, branch, cancellationToken);

		public Task RemoveAsync(
			string basePath,
			string worktreePath,
			CancellationToken cancellationToken) =>
			_inner.RemoveAsync(basePath, worktreePath, cancellationToken);

		public Task PruneAsync(string basePath, CancellationToken cancellationToken) =>
			_inner.PruneAsync(basePath, cancellationToken);
	}
}
