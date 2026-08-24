using System.Diagnostics;

namespace DevProjex.Tests.Unit;

public sealed class RepositoryCacheIsolationTests : IDisposable
{
	private const string RepositoryUrl = "https://github.com/example/isolation.git";
	private static readonly TimeSpan BackgroundOperationTimeout = TimeSpan.FromSeconds(30);
	private readonly string _cacheRoot = Path.Combine(
		Path.GetTempPath(),
		"DevProjex",
		"Tests",
		"RepositoryCacheIsolation",
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
	public void ExclusiveLease_IsReleasedByDispose()
	{
		var leasePath = Path.Combine(_cacheRoot, "lease.lock");

		Assert.True(RepositoryFileLease.TryAcquireExclusive(leasePath, out var first));
		Assert.False(RepositoryFileLease.TryAcquireExclusive(leasePath, out _));

		first!.Dispose();
		Assert.True(RepositoryFileLease.TryAcquireExclusive(leasePath, out var second));
		second!.Dispose();
	}

	[Fact]
	public void DefaultPolicy_UsesTenGiBAndNinetyDays()
	{
		Assert.Equal(10L * 1024 * 1024 * 1024, RepositoryCachePolicy.Default.MaximumSizeBytes);
		Assert.Equal(TimeSpan.FromDays(90), RepositoryCachePolicy.Default.MaximumUnusedAge);
	}

	[Fact]
	public void IndexSize_IsCalculatedAtPublishAndRefreshedOnlyAfterExplicitUpdate()
	{
		var service = CreateService(new FakeWorktreeManager(supported: true));
		var path = Publish(service, RepositoryUrl, RepositoryCacheContentKind.Zip, new string('a', 128));
		var initial = Assert.IsType<RepositoryCacheIndexEntry>(service.FindIndexedRepository(RepositoryUrl));
		Assert.True(initial.ApproximateSizeBytes >= 128);

		File.WriteAllText(Path.Combine(path, "updated.bin"), new string('b', 1024));
		Assert.Equal(
			initial.ApproximateSizeBytes,
			service.FindIndexedRepository(RepositoryUrl)!.ApproximateSizeBytes);

		service.RefreshIndexedRepositorySize(path);
		Assert.True(service.FindIndexedRepository(RepositoryUrl)!.ApproximateSizeBytes >= 1152);
	}

	[Fact]
	public async Task GitSessions_CreateDetachedCopiesAndReuseReleasedCopy()
	{
		var worktrees = new FakeWorktreeManager(supported: true);
		var service = CreateService(worktrees);
		var basePath = Publish(service, RepositoryUrl, RepositoryCacheContentKind.Git, "main");

		var first = await service.TryAcquireRepositorySessionAsync(
			RepositoryUrl,
			"main",
			TestContext.Current.CancellationToken);
		var second = await service.TryAcquireRepositorySessionAsync(
			RepositoryUrl,
			"main",
			TestContext.Current.CancellationToken);

		Assert.NotNull(first);
		Assert.NotNull(second);
		Assert.Equal(basePath, first.RepositoryPath, PathComparer.Default);
		Assert.NotEqual(first.RepositoryPath, second.RepositoryPath, PathComparer.Default);
		Assert.True(Directory.Exists(second.RepositoryPath));
		Assert.Equal(1, worktrees.CreatedCount);

		first.Dispose();
		var reused = await service.TryAcquireRepositorySessionAsync(
			RepositoryUrl,
			"main",
			TestContext.Current.CancellationToken);
		Assert.NotNull(reused);
		Assert.Equal(basePath, reused.RepositoryPath, PathComparer.Default);
		Assert.Equal(1, worktrees.CreatedCount);

		second.Dispose();
		reused.Dispose();
		using var afterCleanup = await service.TryAcquireRepositorySessionAsync(
			RepositoryUrl,
			"main",
			TestContext.Current.CancellationToken);
		Assert.NotNull(afterCleanup);
		Assert.Equal(basePath, afterCleanup.RepositoryPath, PathComparer.Default);
		await WaitUntilAsync(() => worktrees.RemovedCount == 1);
		Assert.Equal(1, worktrees.RemovedCount);
		Assert.Equal(1, worktrees.PrunedCount);
	}

	[Fact]
	public async Task GitSessionReturnsBeforeUnusedWorktreeCleanupCompletes()
	{
		var worktrees = new FakeWorktreeManager(supported: true, blockRemoval: true);
		var service = CreateService(worktrees);
		_ = Publish(service, RepositoryUrl, RepositoryCacheContentKind.Git, "main");
		var first = await service.TryAcquireRepositorySessionAsync(
			RepositoryUrl,
			"main",
			TestContext.Current.CancellationToken);
		var second = await service.TryAcquireRepositorySessionAsync(
			RepositoryUrl,
			"main",
			TestContext.Current.CancellationToken);
		Assert.NotNull(first);
		Assert.NotNull(second);
		first.Dispose();
		second.Dispose();

		using var reopened = await service.TryAcquireRepositorySessionAsync(
			RepositoryUrl,
			"main",
			TestContext.Current.CancellationToken).WaitAsync(
			BackgroundOperationTimeout,
			TestContext.Current.CancellationToken);
		Assert.NotNull(reopened);
		await worktrees.RemovalStarted.Task.WaitAsync(
			BackgroundOperationTimeout,
			TestContext.Current.CancellationToken);
		Assert.Equal(0, worktrees.RemovedCount);

		worktrees.ReleaseRemoval();
		await WaitUntilAsync(() => worktrees.RemovedCount == 1);
	}

	[Fact]
	public async Task GitSessionReturnsBeforeRepositorySizeRefreshAndBackgroundRefreshUpdatesIndex()
	{
		var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var allowRefresh = new ManualResetEventSlim();
		var refreshCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var hooks = new RepoCacheTestHooks
		{
			BeforeRepositorySizeRefresh = _ =>
			{
				refreshStarted.TrySetResult();
				Assert.True(allowRefresh.Wait(
					BackgroundOperationTimeout,
					TestContext.Current.CancellationToken));
			},
			AfterRepositorySizeRefresh = _ => refreshCompleted.TrySetResult()
		};
		var worktrees = new FakeWorktreeManager(supported: true);
		var service = CreateService(worktrees, hooks: hooks);
		var basePath = Publish(service, RepositoryUrl, RepositoryCacheContentKind.Git, "main");
		var initialSize = service.FindIndexedRepository(RepositoryUrl)!.ApproximateSizeBytes;
		using var first = await service.TryAcquireRepositorySessionAsync(
			RepositoryUrl,
			"main",
			TestContext.Current.CancellationToken);

		using var second = await service.TryAcquireRepositorySessionAsync(
			RepositoryUrl,
			"main",
			TestContext.Current.CancellationToken).WaitAsync(
			TimeSpan.FromSeconds(2),
			TestContext.Current.CancellationToken);

		Assert.NotNull(first);
		Assert.NotNull(second);
		await refreshStarted.Task.WaitAsync(
			BackgroundOperationTimeout,
			TestContext.Current.CancellationToken);
		Assert.Equal(initialSize, service.FindIndexedRepository(RepositoryUrl)!.ApproximateSizeBytes);

		allowRefresh.Set();
		await refreshCompleted.Task.WaitAsync(
			BackgroundOperationTimeout,
			TestContext.Current.CancellationToken);
		Assert.True(service.FindIndexedRepository(RepositoryUrl)!.ApproximateSizeBytes > initialSize);
		Assert.True(Directory.Exists(basePath));
	}

	[Fact]
	public async Task RepositorySizeRefresh_RequestDuringCompletedEnumeration_CoalescesOneFreshPass()
	{
		using var firstSizeCalculated = new ManualResetEventSlim();
		using var allowFirstRefreshCommit = new ManualResetEventSlim();
		using var secondRefreshStarted = new ManualResetEventSlim();
		using var allowSecondRefresh = new ManualResetEventSlim();
		var refreshCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var refreshCount = 0;
		var hooks = new RepoCacheTestHooks
		{
			BeforeRepositorySizeRefresh = _ =>
			{
				var pass = Interlocked.Increment(ref refreshCount);
				if (pass != 2)
					return;
				secondRefreshStarted.Set();
				Assert.True(allowSecondRefresh.Wait(BackgroundOperationTimeout));
			},
			AfterRepositorySizeCalculated = _ =>
			{
				if (Volatile.Read(ref refreshCount) != 1)
					return;
				firstSizeCalculated.Set();
				Assert.True(allowFirstRefreshCommit.Wait(BackgroundOperationTimeout));
			},
			AfterRepositorySizeRefresh = _ => refreshCompleted.TrySetResult()
		};
		var service = CreateService(new FakeWorktreeManager(supported: true), hooks: hooks);
		_ = Publish(service, RepositoryUrl, RepositoryCacheContentKind.Git, "main");
		var sessions = new List<IRepositoryCacheSession>();
		try
		{
			sessions.Add(Assert.IsAssignableFrom<IRepositoryCacheSession>(
				await service.TryAcquireRepositorySessionAsync(
					RepositoryUrl,
					"main",
					TestContext.Current.CancellationToken)));
			sessions.Add(Assert.IsAssignableFrom<IRepositoryCacheSession>(
				await service.TryAcquireRepositorySessionAsync(
					RepositoryUrl,
					"main",
					TestContext.Current.CancellationToken)));
			Assert.True(firstSizeCalculated.Wait(
				BackgroundOperationTimeout,
				TestContext.Current.CancellationToken));

			sessions.Add(Assert.IsAssignableFrom<IRepositoryCacheSession>(
				await service.TryAcquireRepositorySessionAsync(
					RepositoryUrl,
					"main",
					TestContext.Current.CancellationToken)));
			allowFirstRefreshCommit.Set();
			Assert.True(secondRefreshStarted.Wait(
				BackgroundOperationTimeout,
				TestContext.Current.CancellationToken));
			var staleSize = service.FindIndexedRepository(RepositoryUrl)!.ApproximateSizeBytes;

			allowSecondRefresh.Set();
			await refreshCompleted.Task.WaitAsync(
				BackgroundOperationTimeout,
				TestContext.Current.CancellationToken);

			Assert.Equal(2, Volatile.Read(ref refreshCount));
			Assert.True(service.FindIndexedRepository(RepositoryUrl)!.ApproximateSizeBytes > staleSize);
		}
		finally
		{
			allowFirstRefreshCommit.Set();
			allowSecondRefresh.Set();
			foreach (var session in sessions)
				session.Dispose();
		}
	}

	[Fact]
	public async Task WorktreeCleanup_MultipleOpensWhileFirstCleanupIsBlocked_CoalescesOnePendingRepeat()
	{
		using var cleanupStarted = new ManualResetEventSlim();
		using var allowCleanup = new ManualResetEventSlim();
		var cleanupCount = 0;
		var hooks = new RepoCacheTestHooks
		{
			BeforeUnusedWorktreeCleanup = _ =>
			{
				Interlocked.Increment(ref cleanupCount);
				cleanupStarted.Set();
				Assert.True(allowCleanup.Wait(
					TimeSpan.FromSeconds(3),
					TestContext.Current.CancellationToken));
			}
		};
		var service = CreateService(new FakeWorktreeManager(supported: true), hooks: hooks);
		_ = Publish(service, RepositoryUrl, RepositoryCacheContentKind.Git, "main");
		var sessions = new List<IRepositoryCacheSession>();
		try
		{
			sessions.Add(Assert.IsAssignableFrom<IRepositoryCacheSession>(
				await service.TryAcquireRepositorySessionAsync(
					RepositoryUrl,
					"main",
					TestContext.Current.CancellationToken)));
			Assert.True(cleanupStarted.Wait(
				BackgroundOperationTimeout,
				TestContext.Current.CancellationToken));

			for (var index = 0; index < 3; index++)
			{
				sessions.Add(Assert.IsAssignableFrom<IRepositoryCacheSession>(
					await service.TryAcquireRepositorySessionAsync(
						RepositoryUrl,
						"main",
						TestContext.Current.CancellationToken)));
			}

			Assert.Equal(1, Volatile.Read(ref cleanupCount));
			allowCleanup.Set();
			await WaitUntilAsync(() => Volatile.Read(ref cleanupCount) == 2);
			Assert.Equal(2, Volatile.Read(ref cleanupCount));
		}
		finally
		{
			allowCleanup.Set();
			foreach (var session in sessions)
				session.Dispose();
		}
	}

	[Fact]
	public async Task GarbageCollection_MultipleOpensWhileFirstPassIsBlocked_CoalescesOnePendingRepeat()
	{
		using var collectionStarted = new ManualResetEventSlim();
		using var allowCollection = new ManualResetEventSlim();
		var secondPassCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var collectionCount = 0;
		var hooks = new RepoCacheTestHooks
		{
			BeforeScheduledGarbageCollection = () =>
			{
				var pass = Interlocked.Increment(ref collectionCount);
				if (pass != 1)
					return;
				collectionStarted.Set();
				Assert.True(allowCollection.Wait(
					TimeSpan.FromSeconds(3),
					TestContext.Current.CancellationToken));
			},
			AfterScheduledGarbageCollection = () =>
			{
				if (Volatile.Read(ref collectionCount) == 2)
					secondPassCompleted.TrySetResult();
			}
		};
		var service = CreateService(new FakeWorktreeManager(supported: true), hooks: hooks);
		_ = Publish(service, RepositoryUrl, RepositoryCacheContentKind.Zip, "snapshot");
		var sessions = new List<IRepositoryCacheSession>();
		try
		{
			sessions.Add(Assert.IsAssignableFrom<IRepositoryCacheSession>(
				await service.TryAcquireRepositorySessionAsync(
					RepositoryUrl,
					cancellationToken: TestContext.Current.CancellationToken)));
			Assert.True(collectionStarted.Wait(
				TimeSpan.FromSeconds(15),
				TestContext.Current.CancellationToken));

			for (var index = 0; index < 5; index++)
			{
				sessions.Add(Assert.IsAssignableFrom<IRepositoryCacheSession>(
					await service.TryAcquireRepositorySessionAsync(
						RepositoryUrl,
						cancellationToken: TestContext.Current.CancellationToken)));
			}

			Assert.Equal(1, Volatile.Read(ref collectionCount));
			allowCollection.Set();
			await secondPassCompleted.Task.WaitAsync(
				TimeSpan.FromSeconds(3),
				TestContext.Current.CancellationToken);
			await Task.Delay(100, TestContext.Current.CancellationToken);
			Assert.Equal(2, Volatile.Read(ref collectionCount));
		}
		finally
		{
			allowCollection.Set();
			foreach (var session in sessions)
				session.Dispose();
		}
	}

	[Fact]
	public async Task GitFallbackWithoutWorktreeSupportSharesLegacyCheckoutAndPinsDeletion()
	{
		var service = CreateService(new FakeWorktreeManager(supported: false));
		var basePath = Publish(service, RepositoryUrl, RepositoryCacheContentKind.Git, "main");
		using var first = await service.TryAcquireRepositorySessionAsync(
			RepositoryUrl,
			cancellationToken: TestContext.Current.CancellationToken);
		using var second = await service.TryAcquireRepositorySessionAsync(
			RepositoryUrl,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.NotNull(first);
		Assert.NotNull(second);
		Assert.Equal(basePath, first.RepositoryPath, PathComparer.Default);
		Assert.Equal(basePath, second.RepositoryPath, PathComparer.Default);
		service.DeleteRepositoryDirectory(basePath);
		Assert.True(Directory.Exists(basePath));
	}

	[Fact]
	public async Task PinnedRepository_IsNeverDeletedUntilLeaseIsReleased()
	{
		var cleanupCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var hooks = new RepoCacheTestHooks
		{
			AfterUnusedWorktreeCleanup = _ => cleanupCompleted.TrySetResult()
		};
		var service = CreateService(new FakeWorktreeManager(supported: true), hooks: hooks);
		var basePath = Publish(service, RepositoryUrl, RepositoryCacheContentKind.Git, "main");
		var session = await service.TryAcquireRepositorySessionAsync(
			RepositoryUrl,
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.NotNull(session);
		await cleanupCompleted.Task.WaitAsync(
			BackgroundOperationTimeout,
			TestContext.Current.CancellationToken);

		service.DeleteRepositoryDirectory(basePath);
		Assert.True(Directory.Exists(basePath));
		Assert.NotNull(service.FindIndexedRepository(RepositoryUrl));
		Assert.Single(service.ListIndexedRepositories());

		session.Dispose();
		service.DeleteRepositoryDirectory(basePath);
		Assert.False(Directory.Exists(basePath));
		Assert.Null(service.FindIndexedRepository(RepositoryUrl));
		Assert.Empty(service.ListIndexedRepositories());
	}

	[Fact]
	public async Task ClearAllCache_PreservesPinnedRepositoryAndRemovesUnpinnedEntries()
	{
		var service = CreateService(new FakeWorktreeManager(supported: true));
		var pinnedUrl = "https://github.com/example/pinned.zip";
		var removableUrl = "https://github.com/example/removable.zip";
		var pinnedPath = Publish(service, pinnedUrl, RepositoryCacheContentKind.Zip, "pinned");
		var removablePath = Publish(service, removableUrl, RepositoryCacheContentKind.Zip, "remove");
		var pinned = await service.TryAcquireRepositorySessionAsync(
			pinnedUrl,
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.NotNull(pinned);

		service.ClearAllCache();

		var retained = Assert.Single(service.ListIndexedRepositories());
		Assert.Equal(pinnedUrl, retained.RepositoryUrl);
		Assert.True(Directory.Exists(pinnedPath));
		Assert.False(Directory.Exists(removablePath));
		using (JsonDocument.Parse(File.ReadAllBytes(Path.Combine(_cacheRoot, "cache-index.json"))))
		{
		}

		pinned.Dispose();
		service.ClearAllCache();
		Assert.Empty(service.ListIndexedRepositories());
	}

	[Fact]
	public async Task OpenDeleteRace_KeepsPinnedIndexEntryVisible()
	{
		using var leaseAcquired = new ManualResetEventSlim();
		using var allowRecheck = new ManualResetEventSlim();
		var hooks = new RepoCacheTestHooks
		{
			AfterSessionLeaseAcquired = _ =>
			{
				leaseAcquired.Set();
				Assert.True(allowRecheck.Wait(
					TimeSpan.FromSeconds(5),
					TestContext.Current.CancellationToken));
			}
		};
		var service = CreateService(new FakeWorktreeManager(supported: true), hooks: hooks);
		var otherProcess = CreateService(new FakeWorktreeManager(supported: true));
		var basePath = Publish(service, RepositoryUrl, RepositoryCacheContentKind.Git, "main");

		var openTask = Task.Run(
			() => service.TryAcquireRepositorySessionAsync(
				RepositoryUrl,
				cancellationToken: TestContext.Current.CancellationToken),
			TestContext.Current.CancellationToken);
		Assert.True(leaseAcquired.Wait(
			TimeSpan.FromSeconds(5),
			TestContext.Current.CancellationToken));
		var deleteTask = Task.Run(
			() => otherProcess.DeleteRepositoryDirectory(basePath),
			TestContext.Current.CancellationToken);
		allowRecheck.Set();

		using var session = await openTask;
		await deleteTask;
		Assert.NotNull(session);
		Assert.True(Directory.Exists(session.RepositoryPath));
		Assert.NotNull(service.FindIndexedRepository(RepositoryUrl));
	}

	[Fact]
	public async Task CatalogListingAndGarbageCollection_RaceKeepsIndexConsistent()
	{
		var policy = new RepositoryCachePolicy(1, TimeSpan.FromDays(60));
		var service = CreateService(new FakeWorktreeManager(supported: true), policy);
		var otherProcess = CreateService(new FakeWorktreeManager(supported: true), policy);
		var pinnedUrl = "https://github.com/example/catalog-pinned.zip";
		var removableUrl = "https://github.com/example/catalog-removable.zip";
		var pinnedPath = Publish(service, pinnedUrl, RepositoryCacheContentKind.Zip, "pinned");
		using var pinned = await service.TryAcquireRepositorySessionAsync(
			pinnedUrl,
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.NotNull(pinned);
		Publish(service, removableUrl, RepositoryCacheContentKind.Zip, "removable");

		var operations = new List<Task>(16);
		for (var iteration = 0; iteration < 8; iteration++)
		{
			operations.Add(Task.Run(
				() => service.ListIndexedRepositories(),
				TestContext.Current.CancellationToken));
			operations.Add(Task.Run(
				otherProcess.CollectGarbage,
				TestContext.Current.CancellationToken));
		}
		await Task.WhenAll(operations);

		var retained = Assert.Single(service.ListIndexedRepositories());
		Assert.Equal(pinnedUrl, retained.RepositoryUrl);
		Assert.True(Directory.Exists(pinnedPath));
		using var index = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(_cacheRoot, "cache-index.json")));
		Assert.Equal(2, index.RootElement.GetProperty("schemaVersion").GetInt32());
	}

	[Fact]
	public async Task ZipPublish_CreatesImmutableVersionsAndCollectsReleasedOldVersion()
	{
		var service = CreateService(new FakeWorktreeManager(supported: true));
		var firstPath = Publish(service, RepositoryUrl, RepositoryCacheContentKind.Zip, "old");
		using var first = await service.TryAcquireRepositorySessionAsync(
			RepositoryUrl,
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.NotNull(first);

		var secondPath = Publish(service, RepositoryUrl, RepositoryCacheContentKind.Zip, "new");
		using var second = await service.TryAcquireRepositorySessionAsync(
			RepositoryUrl,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.NotNull(second);
		Assert.NotEqual(firstPath, secondPath, PathComparer.Default);
		Assert.Equal("old", File.ReadAllText(Path.Combine(first.RepositoryPath, "payload.txt")));
		Assert.Equal("new", File.ReadAllText(Path.Combine(second.RepositoryPath, "payload.txt")));
		service.CollectGarbage();
		Assert.True(Directory.Exists(first.RepositoryPath));

		first.Dispose();
		service.CollectGarbage();
		Assert.False(Directory.Exists(first.RepositoryPath));
	}

	[Fact]
	public void Trash_RetriesLockedFilesAndClearsReadOnlyFiles()
	{
		var service = CreateService(new FakeWorktreeManager(supported: true));
		var repositoryPath = service.CreateRepositoryDirectory(RepositoryUrl);
		var lockedPath = Path.Combine(repositoryPath, "locked.txt");
		var readOnlyPath = Path.Combine(repositoryPath, "readonly.txt");
		File.WriteAllText(lockedPath, "locked");
		File.WriteAllText(readOnlyPath, "readonly");
		File.SetAttributes(readOnlyPath, FileAttributes.ReadOnly);

		using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
		{
			service.DeleteRepositoryDirectory(repositoryPath);
			if (OperatingSystem.IsWindows())
			{
				Assert.True(Directory.Exists(repositoryPath) || Directory.Exists(
					RepositoryCacheLayout.GetTrashRoot(_cacheRoot)));
			}
			else
			{
				Assert.False(Directory.Exists(repositoryPath));
			}
		}

		service.CollectGarbage();
		Assert.False(Directory.Exists(repositoryPath));
		var trashRoot = RepositoryCacheLayout.GetTrashRoot(_cacheRoot);
		Assert.False(Directory.Exists(trashRoot));
	}

	[Fact]
	public async Task Eviction_UsesAgeAndLruButNeverRemovesPinnedRepository()
	{
		var clock = new MutableTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
		var service = CreateService(
			new FakeWorktreeManager(supported: true),
			new RepositoryCachePolicy(220, TimeSpan.FromDays(60)),
			clock);
		var oldestUrl = "https://github.com/example/oldest.git";
		var oldest = Publish(service, oldestUrl, RepositoryCacheContentKind.Zip, new string('a', 128));
		using var pinned = await service.TryAcquireRepositorySessionAsync(
			oldestUrl,
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.NotNull(pinned);

		clock.Advance(TimeSpan.FromDays(1));
		var newerUrl = "https://github.com/example/newer.git";
		var newer = Publish(service, newerUrl, RepositoryCacheContentKind.Zip, new string('b', 128));
		service.CollectGarbage();

		Assert.True(Directory.Exists(oldest));
		Assert.False(Directory.Exists(newer));

		pinned.Dispose();
		clock.Advance(TimeSpan.FromDays(61));
		service.CollectGarbage();
		Assert.False(Directory.Exists(oldest));
	}

	[Fact]
	public async Task Eviction_UsesTheMostRecentOpenTimeForLruOrdering()
	{
		var clock = new MutableTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
		var service = CreateService(
			new FakeWorktreeManager(supported: true),
			new RepositoryCachePolicy(300, TimeSpan.FromDays(60)),
			clock);
		var reopenedUrl = "https://github.com/example/reopened.git";
		var reopenedPath = Publish(
			service,
			reopenedUrl,
			RepositoryCacheContentKind.Zip,
			new string('a', 128));

		clock.Advance(TimeSpan.FromDays(1));
		var staleUrl = "https://github.com/example/stale.git";
		var stalePath = Publish(
			service,
			staleUrl,
			RepositoryCacheContentKind.Zip,
			new string('b', 128));

		clock.Advance(TimeSpan.FromDays(1));
		using (var reopened = await service.TryAcquireRepositorySessionAsync(
			       reopenedUrl,
			       cancellationToken: TestContext.Current.CancellationToken))
		{
			Assert.NotNull(reopened);
		}

		clock.Advance(TimeSpan.FromDays(1));
		var newestUrl = "https://github.com/example/newest.git";
		var newestPath = Publish(
			service,
			newestUrl,
			RepositoryCacheContentKind.Zip,
			new string('c', 128));
		service.CollectGarbage();

		Assert.True(Directory.Exists(reopenedPath));
		Assert.False(Directory.Exists(stalePath));
		Assert.True(Directory.Exists(newestPath));
	}

	private RepoCacheService CreateService(
		IGitWorktreeManager worktreeManager,
		RepositoryCachePolicy? policy = null,
		TimeProvider? timeProvider = null,
		RepoCacheTestHooks? hooks = null) =>
		new(
			_cacheRoot,
			policy ?? RepositoryCachePolicy.Default,
			timeProvider ?? TimeProvider.System,
			worktreeManager,
			hooks);

	private static string Publish(
		RepoCacheService service,
		string repositoryUrl,
		RepositoryCacheContentKind kind,
		string payload)
	{
		var staging = service.CreateRepositoryStagingDirectory(repositoryUrl);
		if (kind == RepositoryCacheContentKind.Git)
			Directory.CreateDirectory(Path.Combine(staging, ".git"));
		File.WriteAllText(Path.Combine(staging, "payload.txt"), payload);
		var published = service.PublishRepositoryDirectory(staging, repositoryUrl);
		service.RecordIndexedRepository(repositoryUrl, published, payload);
		return published;
	}

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		var stopwatch = Stopwatch.StartNew();
		while (!condition() && stopwatch.Elapsed < BackgroundOperationTimeout)
			await Task.Delay(20, TestContext.Current.CancellationToken);
		Assert.True(condition(), "The asynchronous repository cleanup did not complete in time.");
	}

	private sealed class FakeWorktreeManager : IGitWorktreeManager
	{
		private readonly bool _supported;
		private readonly bool _blockRemoval;
		private readonly TaskCompletionSource _releaseRemoval = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public FakeWorktreeManager(bool supported, bool blockRemoval = false)
		{
			_supported = supported;
			_blockRemoval = blockRemoval;
			if (!blockRemoval)
				_releaseRemoval.TrySetResult();
		}

		public int CreatedCount { get; private set; }
		public int RemovedCount { get; private set; }
		public int PrunedCount { get; private set; }
		public TaskCompletionSource RemovalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public void ReleaseRemoval() => _releaseRemoval.TrySetResult();
		public Task<bool> IsSupportedAsync(string basePath, CancellationToken cancellationToken) =>
			Task.FromResult(_supported);
		public Task<bool> PreparePrimaryAsync(
			string basePath,
			string? branch,
			CancellationToken cancellationToken) => Task.FromResult(true);
		public Task<bool> CreateDetachedAsync(
			string basePath,
			string worktreePath,
			string? branch,
			CancellationToken cancellationToken)
		{
			Directory.CreateDirectory(worktreePath);
			File.WriteAllText(Path.Combine(worktreePath, ".git"), "gitdir: fake");
			File.Copy(
				Path.Combine(basePath, "payload.txt"),
				Path.Combine(worktreePath, "payload.txt"));
			CreatedCount++;
			return Task.FromResult(true);
		}
		public async Task RemoveAsync(
			string basePath,
			string worktreePath,
			CancellationToken cancellationToken)
		{
			RemovalStarted.TrySetResult();
			if (!_blockRemoval)
				_releaseRemoval.TrySetResult();
			await _releaseRemoval.Task.WaitAsync(cancellationToken);
			if (Directory.Exists(worktreePath))
				Directory.Delete(worktreePath, recursive: true);
			RemovedCount++;
		}
		public Task PruneAsync(string basePath, CancellationToken cancellationToken)
		{
			PrunedCount++;
			return Task.CompletedTask;
		}
	}

	private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
	{
		private DateTimeOffset _utcNow = utcNow;
		public override DateTimeOffset GetUtcNow() => _utcNow;
		public void Advance(TimeSpan elapsed) => _utcNow += elapsed;
	}
}
