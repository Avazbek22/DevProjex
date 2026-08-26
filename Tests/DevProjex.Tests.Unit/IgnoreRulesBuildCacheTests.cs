namespace DevProjex.Tests.Unit;

public sealed class IgnoreRulesBuildCacheTests
{
	private static readonly string WorkspacePath = Path.Combine(
		Path.GetTempPath(),
		"DevProjex",
		"Tests",
		"IgnoreRulesCacheWorkspace");

	[Fact]
	public void GetOrBuild_ReorderedEquivalentSelectionsReuseTheSameRules()
	{
		var buildCount = 0;
		var cache = new IgnoreRulesBuildCache((_, _, _) =>
		{
			buildCount++;
			return CreateRules();
		});

		var first = cache.GetOrBuild(
			WorkspacePath,
			[IgnoreOptionId.SmartIgnore, IgnoreOptionId.UseGitIgnore],
			["tests", "src"]);
		var second = cache.GetOrBuild(
			WorkspacePath,
			[IgnoreOptionId.UseGitIgnore, IgnoreOptionId.SmartIgnore],
			["src", "tests", "src"]);

		Assert.Same(first, second);
		Assert.Equal(1, buildCount);
	}

	[Fact]
	public async Task GetOrBuild_ConcurrentEquivalentRequestsBuildExactlyOnce()
	{
		var buildCount = 0;
		var cancellationToken = TestContext.Current.CancellationToken;
		using var releaseBuild = new ManualResetEventSlim();
		var cache = new IgnoreRulesBuildCache((_, _, _) =>
		{
			Interlocked.Increment(ref buildCount);
			releaseBuild.Wait(cancellationToken);
			return CreateRules();
		});

		var requests = Enumerable.Range(0, 16)
			.Select(_ => RunOnDedicatedThread(
				() => cache.GetOrBuild(
					WorkspacePath,
					[IgnoreOptionId.UseGitIgnore],
					["src"]),
				cancellationToken))
			.ToArray();

		try
		{
			Assert.True(SpinWait.SpinUntil(
				() => Volatile.Read(ref buildCount) == 1,
				TimeSpan.FromSeconds(2)));
		}
		finally
		{
			releaseBuild.Set();
		}

		var results = await Task.WhenAll(requests);

		Assert.Equal(1, buildCount);
		Assert.All(results, rules => Assert.Same(results[0], rules));
	}

	#pragma warning disable xUnit1051 // This test verifies cancellation with its own controlled token.
	[Fact]
	public async Task GetOrBuild_CancelledWaiterDoesNotWaitForActiveBuild()
	{
		var testCancellationToken = TestContext.Current.CancellationToken;
		using var buildStarted = new ManualResetEventSlim();
		using var releaseBuild = new ManualResetEventSlim();
		using var waiterCancellation = new CancellationTokenSource();
		var buildCount = 0;
		var cache = new IgnoreRulesBuildCache((_, _, _, _) =>
		{
			Interlocked.Increment(ref buildCount);
			buildStarted.Set();
			releaseBuild.Wait(testCancellationToken);
			return CreateRules();
		});

		var activeBuild = RunOnDedicatedThread(
			() => cache.GetOrBuild(WorkspacePath, [], selectedRootFolders: null),
			testCancellationToken);
		Assert.True(buildStarted.Wait(TimeSpan.FromSeconds(2), testCancellationToken));

		var waitingBuild = RunOnDedicatedThread(
			() => Assert.ThrowsAny<OperationCanceledException>(() =>
				cache.GetOrBuildWithCancellation(
					WorkspacePath,
					[IgnoreOptionId.SmartIgnore],
					selectedRootFolders: null,
					waiterCancellation.Token)),
			testCancellationToken);
		waiterCancellation.Cancel();

		try
		{
			await waitingBuild.WaitAsync(TimeSpan.FromSeconds(2), testCancellationToken);
		}
		finally
		{
			releaseBuild.Set();
		}

		await activeBuild;
		Assert.Equal(1, buildCount);
	}
	#pragma warning restore xUnit1051

	[Fact]
	public void GetOrBuild_ChangedSelectionAndInvalidationBothRebuild()
	{
		var buildCount = 0;
		var cache = new IgnoreRulesBuildCache((_, _, _) =>
		{
			buildCount++;
			return CreateRules();
		});

		var initial = cache.GetOrBuild(WorkspacePath, [], ["src"]);
		var changed = cache.GetOrBuild(WorkspacePath, [IgnoreOptionId.SmartIgnore], ["src"]);
		cache.Invalidate();
		var invalidated = cache.GetOrBuild(WorkspacePath, [IgnoreOptionId.SmartIgnore], ["src"]);

		Assert.NotSame(initial, changed);
		Assert.NotSame(changed, invalidated);
		Assert.Equal(3, buildCount);
	}

	[Fact]
	public void GetOrBuild_FailedBuildIsNotCachedAndNextRequestRetries()
	{
		var buildCount = 0;
		var cache = new IgnoreRulesBuildCache((_, _, _) =>
		{
			if (++buildCount == 1)
				throw new IOException("transient build failure");

			return CreateRules();
		});

		Assert.Throws<IOException>(() =>
			cache.GetOrBuild(WorkspacePath, [IgnoreOptionId.UseGitIgnore], ["src"]));

		var recovered = cache.GetOrBuild(WorkspacePath, [IgnoreOptionId.UseGitIgnore], ["src"]);
		var cached = cache.GetOrBuild(WorkspacePath, [IgnoreOptionId.UseGitIgnore], ["src"]);

		Assert.Same(recovered, cached);
		Assert.Equal(2, buildCount);
	}

	[Fact]
	public async Task Invalidate_DuringBuildForcesTheNextRequestToRebuild()
	{
		var buildCount = 0;
		var cancellationToken = TestContext.Current.CancellationToken;
		using var buildStarted = new ManualResetEventSlim();
		using var releaseBuild = new ManualResetEventSlim();
		var cache = new IgnoreRulesBuildCache((_, _, _) =>
		{
			if (Interlocked.Increment(ref buildCount) == 1)
			{
				buildStarted.Set();
				releaseBuild.Wait(cancellationToken);
			}

			return CreateRules();
		});

		var firstBuild = RunOnDedicatedThread(
			() => cache.GetOrBuild(WorkspacePath, [IgnoreOptionId.SmartIgnore], ["src"]),
			cancellationToken);
		Assert.True(buildStarted.Wait(
			TimeSpan.FromSeconds(2),
			cancellationToken));

		var invalidationStarted = new TaskCompletionSource(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var invalidation = RunOnDedicatedThread(
			() =>
			{
				invalidationStarted.SetResult();
				cache.Invalidate();
			},
			cancellationToken);

		try
		{
			await invalidationStarted.Task.WaitAsync(
				TimeSpan.FromSeconds(2),
				cancellationToken);
		}
		finally
		{
			releaseBuild.Set();
		}

		var first = await firstBuild;
		await invalidation;
		var second = cache.GetOrBuild(WorkspacePath, [IgnoreOptionId.SmartIgnore], ["src"]);

		Assert.NotSame(first, second);
		Assert.Equal(2, buildCount);
	}

	private static Task<T> RunOnDedicatedThread<T>(
		Func<T> action,
		CancellationToken cancellationToken) =>
		Task.Factory.StartNew(
			action,
			cancellationToken,
			TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
			TaskScheduler.Default);

	private static Task RunOnDedicatedThread(
		Action action,
		CancellationToken cancellationToken) =>
		Task.Factory.StartNew(
			action,
			cancellationToken,
			TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
			TaskScheduler.Default);

	private static IgnoreRules CreateRules() =>
		new(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}
