using System.Collections;
using System.Diagnostics;
using System.Reflection;
using DevProjex.Application.Context;
using DevProjex.Mcp;

namespace DevProjex.Tests.Integration;

public sealed class McpProjectInventoryCacheIntegrationTests
{
	[Fact(Timeout = 30_000)]
	public async Task CancelingOneInventoryWaiterDoesNotCancelAnotherWaiterForTheSameBuild()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.CreateFile("project/Anchor.cs", "anchor\n");
		var buildEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseBuild = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var buildCount = 0;
		await using var harness = CreateHarness(project, async (_, token) =>
		{
			Interlocked.Increment(ref buildCount);
			buildEntered.TrySetResult(true);
			await releaseBuild.Task.WaitAsync(token);
		});
		var request = new ProjectContextRequest(project, ProjectSelectionSpec.Standard);

		using var canceledCaller = new CancellationTokenSource();
		var first = BuildCachedBasePlanAsync(harness.Service, request, canceledCaller.Token);
		try
		{
			await buildEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
			var second = BuildCachedBasePlanAsync(harness.Service, request, TestContext.Current.CancellationToken);
			Assert.False(second.IsCompleted);
			Assert.Equal(1, Volatile.Read(ref buildCount));

			canceledCaller.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
			releaseBuild.TrySetResult(true);
			var result = await second.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

			Assert.True(HasFile(result, "Anchor.cs"));
			Assert.Equal(1, Volatile.Read(ref buildCount));
		}
		finally
		{
			releaseBuild.TrySetResult(true);
		}
	}

	[Fact(Timeout = 30_000)]
	public async Task DisposingInventoryServiceCancelsAnActiveSharedBuild()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.CreateFile("project/Anchor.cs", "anchor\n");
		var buildEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseBuild = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		await using var harness = CreateHarness(project, async (_, token) =>
		{
			buildEntered.TrySetResult(true);
			await releaseBuild.Task.WaitAsync(token);
		});
		var request = new ProjectContextRequest(project, ProjectSelectionSpec.Standard);
		var pending = BuildCachedBasePlanAsync(harness.Service, request, CancellationToken.None);
		try
		{
			await buildEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
			harness.Service.Dispose();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(
				() => pending.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
		}
		finally
		{
			releaseBuild.TrySetResult(true);
		}
	}

	[Fact(Timeout = 30_000)]
	public async Task RepeatedCanceledUniqueInventoriesStopTheirScansAndAllowFreshJoins()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.CreateFile("project/Anchor.cs", "anchor\n");
		using var buildEntered = new SemaphoreSlim(0);
		using var buildExited = new SemaphoreSlim(0);
		var activeBuilds = 0;
		var buildCount = 0;
		await using var harness = CreateHarness(project, async (_, token) =>
		{
			if (Interlocked.Increment(ref buildCount) > 12)
				return;
			Interlocked.Increment(ref activeBuilds);
			buildEntered.Release();
			try
			{
				await Task.Delay(Timeout.Infinite, token);
			}
			finally
			{
				Interlocked.Decrement(ref activeBuilds);
				buildExited.Release();
			}
		});
		ProjectContextRequest? lastRequest = null;
		for (var index = 0; index < 12; index++)
		{
			var selection = ProjectSelectionSpec.Standard with
			{
				Extensions = [".cs", $".unused{index}"]
			};
			lastRequest = new ProjectContextRequest(project, selection);
			using var cancellation = new CancellationTokenSource();
			var pending = BuildCachedBasePlanAsync(harness.Service, lastRequest, cancellation.Token);
			Assert.True(await buildEntered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
			cancellation.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
			Assert.True(await buildExited.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
			Assert.Equal(0, Volatile.Read(ref activeBuilds));
			Assert.InRange(ReadCacheCount(harness.Service, "inventoryCache"), 0, 8);
		}

		var retry = await BuildCachedBasePlanAsync(
			harness.Service,
			lastRequest!,
			TestContext.Current.CancellationToken);
		Assert.True(HasFile(retry, "Anchor.cs"));
		Assert.Equal(13, Volatile.Read(ref buildCount));
	}

	[Fact(Timeout = 30_000)]
	public async Task EvictingAnActiveInventoryDoesNotCancelItsWaiter()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.CreateFile("project/Anchor.cs", "anchor\n");
		var buildEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseBuild = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var buildCount = 0;
		await using var harness = CreateHarness(project, async (_, token) =>
		{
			if (Interlocked.Increment(ref buildCount) != 1)
				return;
			buildEntered.TrySetResult(true);
			await releaseBuild.Task.WaitAsync(token);
		});
		var request = new ProjectContextRequest(project, ProjectSelectionSpec.Standard);
		var pending = BuildCachedBasePlanAsync(harness.Service, request, TestContext.Current.CancellationToken);
		try
		{
			await buildEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
			for (var index = 0; index < 9; index++)
			{
				var differentRequest = new ProjectContextRequest(
					project,
					ProjectSelectionSpec.Standard with { Extensions = [".cs", $".unused{index}"] });
				_ = await BuildCachedBasePlanAsync(
					harness.Service,
					differentRequest,
					TestContext.Current.CancellationToken);
			}
			Assert.False(pending.IsCompleted);
			releaseBuild.TrySetResult(true);
			var result = await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
			Assert.True(HasFile(result, "Anchor.cs"));
		}
		finally
		{
			releaseBuild.TrySetResult(true);
		}
	}

	[Fact]
	public async Task MaximumFileSizeRefreshReadsOnlyTheNarrowedCandidates()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		for (var index = 0; index < 100; index++)
			workspace.CreateFile($"project/src/File{index:D3}.txt", new string('x', index + 1));
		var sizeReads = new List<string>();
		await using var harness = CreateHarness(project, effectiveFileSizeRead: sizeReads.Add);

		var plan = await harness.Service.BuildPlanAsync(
			project: null,
			branch: null,
			paths: ["src/File099.txt"],
			includePatterns: null,
			excludePatterns: null,
			profile: null,
			trackedOnly: false,
			gitScope: null,
			maximumFileBytes: 1_024,
			TestContext.Current.CancellationToken,
			includeOutputMetrics: false);

		Assert.Single(plan.IncludedFiles);
		Assert.Single(sizeReads);
		Assert.EndsWith(Path.Combine("src", "File099.txt"), sizeReads[0], StringComparison.Ordinal);
	}

	[Fact]
	public async Task BuildPlan_NarrowProjectionReusesInventoryAndTreeChangeRebuildsIt()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.CreateFile("project/src/Original.cs", "original\n");
		var buildCount = 0;
		McpProjectService? service = null;
		await using var harness = CreateHarness(
			project,
			(_, _) =>
			{
				Interlocked.Increment(ref buildCount);
				DisableWatcher(service!);
				return ValueTask.CompletedTask;
			});
		service = harness.Service;

		var initial = await BuildAsync(harness.Service);
		var buildsAfterInitial = buildCount;
		var narrow = await BuildAsync(harness.Service, includePatterns: ["src/**"]);

		Assert.True(HasFile(initial, "src/Original.cs"));
		Assert.True(HasFile(narrow, "src/Original.cs"));
		Assert.Equal(buildsAfterInitial, buildCount);

		workspace.CreateFile("project/src/AddedAfterCache.cs", "added\n");
		RaiseWatcherChange(harness.Service, "src/AddedAfterCache.cs");
		var changed = await BuildAsync(harness.Service, includePatterns: ["src/**"]);

		Assert.True(HasFile(changed, "src/AddedAfterCache.cs"));
		Assert.Equal(buildsAfterInitial + 1, buildCount);
	}

	[Fact]
	public async Task LiveContextReusesAnUnchangedRevisionAndRebuildsAfterProfileChange()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.CreateFile("project/src/Inside.cs", "inside\n");
		workspace.CreateFile("project/tests/Outside.cs", "outside\n");
		var profileStore = new ProjectProfileStore(() => Path.Combine(workspace.Path, "app-data"));
		profileStore.SaveProfile(project, new ProjectSelectionProfile([], [], [], SelectedPaths: ["src"]));
		var buildCount = 0;
		McpProjectService? service = null;
		await using var harness = CreateHarness(
			project,
			(_, _) =>
			{
				Interlocked.Increment(ref buildCount);
				DisableWatcher(service!);
				return ValueTask.CompletedTask;
			},
			live: true);
		service = harness.Service;

		var initial = await BuildAsync(harness.Service);
		var buildsAfterInitial = buildCount;
		var unchanged = await BuildAsync(harness.Service);

		Assert.True(HasFile(initial, "src/Inside.cs"));
		Assert.False(HasFile(initial, "tests/Outside.cs"));
		Assert.Same(initial, unchanged);
		Assert.Equal(buildsAfterInitial, buildCount);

		profileStore.SaveProfile(project, new ProjectSelectionProfile([], [], [], SelectedPaths: ["tests"]));
		var changed = await BuildAsync(harness.Service);

		Assert.False(HasFile(changed, "src/Inside.cs"));
		Assert.True(HasFile(changed, "tests/Outside.cs"));
		Assert.NotSame(initial, changed);
		Assert.True(buildCount > buildsAfterInitial);
	}

	[Fact(Timeout = 30_000)]
	public async Task BuildPlan_NestedIgnoreChangedAfterItWasReadCannotBePublishedWithDelayedWatcherDelivery()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.CreateDirectory("project/nested");
		workspace.CreateFile("project/nested/Hidden.cs", "hidden\n");
		var ignore = workspace.CreateFile("project/nested/.gitignore", string.Empty);
		var buildCount = 0;
		McpProjectService? service = null;
		await using var harness = CreateHarness(
			project,
			(_, _) =>
			{
				if (Interlocked.Increment(ref buildCount) != 1)
					return ValueTask.CompletedTask;
				DisableWatcher(service!);
				File.WriteAllText(ignore, "Hidden.cs\n");
				File.SetLastWriteTimeUtc(ignore, DateTime.UtcNow.AddSeconds(2));
				return ValueTask.CompletedTask;
			});
		service = harness.Service;

		var plan = await BuildAsync(harness.Service);

		Assert.True(buildCount >= 2);
		Assert.False(HasFile(plan, "nested/Hidden.cs"));
	}

	[Fact(Timeout = 30_000)]
	public async Task BuildPlan_NestedGitmodulesChangedAfterItWasReadCannotPublishAnOpaqueChild()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.CreateDirectory("project/owner/.git");
		var modules = workspace.CreateFile("project/owner/.gitmodules", string.Empty);
		workspace.CreateDirectory("project/owner/child/.git");
		workspace.CreateFile("project/owner/child/Visible.cs", "visible\n");
		var buildCount = 0;
		McpProjectService? service = null;
		await using var harness = CreateHarness(
			project,
			(_, _) =>
			{
				if (Interlocked.Increment(ref buildCount) != 1)
					return ValueTask.CompletedTask;
				DisableWatcher(service!);
				File.WriteAllText(modules, "[submodule \"child\"]\n path = child\n");
				File.SetLastWriteTimeUtc(modules, DateTime.UtcNow.AddSeconds(2));
				return ValueTask.CompletedTask;
			});
		service = harness.Service;

		var plan = await BuildAsync(harness.Service);

		Assert.True(buildCount >= 2);
		Assert.True(HasFile(plan, "owner/child/Visible.cs"));
	}

	[Fact]
	public async Task BuildPlan_NestedIgnoreStampInvalidatesEvenWhenWatcherDeliveryIsDelayed()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.CreateDirectory("project/nested");
		workspace.CreateFile("project/nested/Hidden.cs", "hidden\n");
		var ignore = workspace.CreateFile("project/nested/.gitignore", string.Empty);
		await using var harness = CreateHarness(project);

		var initial = await BuildAsync(harness.Service);
		Assert.True(HasFile(initial, "nested/Hidden.cs"));
		DisableWatcher(harness.Service);
		File.WriteAllText(ignore, "Hidden.cs\n");
		File.SetLastWriteTimeUtc(ignore, DateTime.UtcNow.AddSeconds(2));

		var changed = await BuildAsync(harness.Service);

		Assert.False(HasFile(changed, "nested/Hidden.cs"));
		Assert.NotSame(initial, changed);
	}

	[Fact]
	public async Task BuildPlan_MaximumFileBytesRefreshesSizesBeforeReusingAnInventory()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var path = workspace.CreateFile("project/Payload.txt", "small\n");
		await using var harness = CreateHarness(project);

		var initial = await BuildAsync(harness.Service, maximumFileBytes: 64);
		Assert.True(HasFile(initial, "Payload.txt"));
		DisableWatcher(harness.Service);
		File.WriteAllText(path, new string('x', 128));

		var changed = await BuildAsync(harness.Service, maximumFileBytes: 64);

		Assert.False(HasFile(changed, "Payload.txt"));
	}

	[Fact]
	public async Task BuildPlan_GitIndexStampInvalidatesWithoutAWatcherEvent()
	{
		if (!IsGitAvailable())
			Assert.Skip("Git is unavailable.");

		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.CreateFile("project/Tracked.cs", "tracked\n");
		workspace.CreateFile("project/Staged.cs", "staged\n");
		RunGit(project, "init", "--quiet");
		RunGit(project, "config", "user.email", "tests@example.invalid");
		RunGit(project, "config", "user.name", "DevProjex Tests");
		RunGit(project, "add", "Tracked.cs");
		RunGit(project, "commit", "--quiet", "-m", "fixture");
		await using var harness = CreateHarness(project);

		var initial = await BuildAsync(harness.Service, trackedOnly: true);
		Assert.True(HasFile(initial, "Tracked.cs"));
		Assert.False(HasFile(initial, "Staged.cs"));
		DisableWatcher(harness.Service);
		RunGit(project, "add", "Staged.cs");

		var changed = await BuildAsync(harness.Service, trackedOnly: true);

		Assert.True(HasFile(changed, "Staged.cs"));
		Assert.NotSame(initial, changed);
	}

	[Fact]
	public async Task BuildPlan_NestedWorktreeIndexOutsideTheWatchedRootInvalidatesThePlan()
	{
		if (!IsGitAvailable())
			Assert.Skip("Git is unavailable.");

		using var workspace = new TemporaryDirectory();
		var repository = workspace.CreateDirectory("repository");
		var project = workspace.CreateDirectory("project");
		workspace.CreateFile("repository/Anchor.cs", "anchor\n");
		RunGit(repository, "init", "--quiet");
		RunGit(repository, "config", "user.email", "tests@example.invalid");
		RunGit(repository, "config", "user.name", "DevProjex Tests");
		RunGit(repository, "add", "Anchor.cs");
		RunGit(repository, "commit", "--quiet", "-m", "fixture");
		var worktree = Path.Combine(project, "nested");
		RunGit(repository, "worktree", "add", "--quiet", "--detach", worktree, "HEAD");
		File.WriteAllText(Path.Combine(worktree, "Staged.cs"), "staged\n");
		await using var harness = CreateHarness(project);

		var initial = await BuildAsync(harness.Service, trackedOnly: true);
		Assert.False(HasFile(initial, "nested/Staged.cs"));
		DisableWatcher(harness.Service);
		RunGit(worktree, "add", "Staged.cs");

		var changed = await BuildAsync(harness.Service, trackedOnly: true);

		Assert.True(HasFile(changed, "nested/Staged.cs"));
		Assert.NotSame(initial, changed);
	}

	[Fact]
	public async Task BuildPlan_WatcherErrorDisablesReuseAndForcesANewScan()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.CreateFile("project/Anchor.cs", "anchor\n");
		var buildCount = 0;
		await using var harness = CreateHarness(
			project,
			(_, _) =>
			{
				Interlocked.Increment(ref buildCount);
				return ValueTask.CompletedTask;
			});
		var initial = await BuildAsync(harness.Service);
		RaiseWatcherError(harness.Service);
		var rebuilt = await BuildAsync(harness.Service);
		var stable = await BuildAsync(harness.Service);

		Assert.NotSame(initial, rebuilt);
		Assert.Same(rebuilt, stable);
		Assert.Equal(2, buildCount);
	}

	[Fact]
	public async Task NinthRootEvictsTheLeastRecentMonitorAndThenReusesItsInventory()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("root-09");
		workspace.CreateFile("root-09/Anchor.cs", "anchor\n");
		var buildCount = 0;
		await using var harness = CreateHarness(
			project,
			(_, _) =>
			{
				Interlocked.Increment(ref buildCount);
				return ValueTask.CompletedTask;
			});
		for (var index = 1; index <= 8; index++)
			GetOrCreateMonitor(harness.Service, workspace.CreateDirectory($"root-{index:D2}"));

		var initial = await BuildAsync(harness.Service);
		for (var iteration = 0; iteration < 100; iteration++)
			Assert.Same(initial, await BuildAsync(harness.Service));

		Assert.Equal(1, buildCount);
		Assert.Equal(8, ReadCacheCount(harness.Service, "rootMonitors"));
	}

	[Fact]
	public async Task ChangesInsideAProvenIgnoredArtifactTreeDoNotInvalidateItsInventory()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.CreateFile("project/Anchor.cs", "anchor\n");
		workspace.CreateFile("project/node_modules/.package-lock.json", "{}\n");
		workspace.CreateFile("project/node_modules/package/index.js", "module.exports = 1;\n");
		var buildCount = 0;
		await using var harness = CreateHarness(
			project,
			(_, _) =>
			{
				Interlocked.Increment(ref buildCount);
				return ValueTask.CompletedTask;
			});

		var initial = await BuildAsync(harness.Service);
		var buildsAfterInitial = buildCount;
		Assert.Contains(ProjectExclusion.SmartIgnore, initial.Selection.Exclusions ?? []);
		Assert.True(IsIgnoredMonitorChange(harness.Service, "node_modules/package/index.js"));
		for (var iteration = 0; iteration < 50_000; iteration++)
			RaiseWatcherChange(harness.Service, "node_modules/package/index.js");
		var unchanged = await BuildAsync(harness.Service);

		Assert.Same(initial, unchanged);
		Assert.Equal(buildsAfterInitial, buildCount);

		RaiseWatcherChange(harness.Service, "Anchor.cs");
		var changed = await BuildAsync(harness.Service);
		Assert.NotSame(initial, changed);
		Assert.Equal(buildsAfterInitial + 1, buildCount);
	}

	[Fact]
	public async Task RemovingAnArtifactSignatureInvalidatesThePreviouslyIgnoredInventory()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.CreateFile("project/Anchor.cs", "anchor\n");
		var signature = workspace.CreateFile("project/node_modules/.package-lock.json", "{}\n");
		workspace.CreateFile("project/node_modules/package/index.js", "module.exports = 1;\n");
		await using var harness = CreateHarness(project);

		var initial = await BuildAsync(harness.Service);
		Assert.False(HasFile(initial, "node_modules/package/index.js"));
		Assert.True(IsIgnoredMonitorChange(harness.Service, "node_modules/package/index.js"));

		File.Delete(signature);
		var removedSignature = new FileSystemEventArgs(
			WatcherChangeTypes.Deleted,
			project,
			"node_modules/.package-lock.json");
		RaiseWatcherEvent(harness.Service, removedSignature);
		var updated = await BuildAsync(harness.Service);

		Assert.True(HasFile(updated, "node_modules/package/index.js"));
		Assert.NotSame(initial, updated);
	}

	[Theory]
	[InlineData(WatcherChangeTypes.Changed)]
	[InlineData(WatcherChangeTypes.Created)]
	[InlineData(WatcherChangeTypes.Deleted)]
	public async Task EveryEventInsideAProvenIgnoredArtifactTreeKeepsTheInventory(
		WatcherChangeTypes changeType)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.CreateFile("project/Anchor.cs", "anchor\n");
		workspace.CreateFile("project/node_modules/.package-lock.json", "{}\n");
		workspace.CreateFile("project/node_modules/package/index.js", "module.exports = 1;\n");
		await using var harness = CreateHarness(project);

		_ = await BuildAsync(harness.Service);

		Assert.True(IsIgnoredMonitorChange(
			harness.Service,
			new FileSystemEventArgs(changeType, ".", "node_modules/package/index.js")));
	}

	[Fact]
	public async Task RenameIsIgnoredOnlyWhenBothPathsAreInsideTheProvenIgnoredTree()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.CreateFile("project/Anchor.cs", "anchor\n");
		workspace.CreateFile("project/node_modules/.package-lock.json", "{}\n");
		workspace.CreateFile("project/node_modules/package/index.js", "module.exports = 1;\n");
		await using var harness = CreateHarness(project);

		_ = await BuildAsync(harness.Service);

		Assert.True(IsIgnoredMonitorChange(
			harness.Service,
			new RenamedEventArgs(
				WatcherChangeTypes.Renamed,
				".",
				"node_modules/package/new.js",
				"node_modules/package/index.js")));
		Assert.False(IsIgnoredMonitorChange(
			harness.Service,
			new RenamedEventArgs(
				WatcherChangeTypes.Renamed,
				".",
				"node_modules/package/Anchor.cs",
				"Anchor.cs")));
		Assert.False(IsIgnoredMonitorChange(
			harness.Service,
			new RenamedEventArgs(
				WatcherChangeTypes.Renamed,
				".",
				"Anchor.cs",
				"node_modules/package/index.js")));
	}

	[Fact(Timeout = 60_000)]
	public async Task BuildPlan_RepeatedInvalidationKeepsInventoryAndProjectionStorageBounded()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.CreateFile("project/Anchor.cs", "anchor\n");
		await using var harness = CreateHarness(project);

		for (var iteration = 0; iteration < 1_000; iteration++)
		{
			_ = await BuildAsync(harness.Service, includePatterns: ["**/*.cs"]);
			RaiseWatcherChange(harness.Service);
		}

		Assert.InRange(ReadCacheCount(harness.Service, "inventoryCache"), 0, 8);
		Assert.InRange(ReadCacheCount(harness.Service, "projectionCache"), 0, 16);
		Assert.Null(typeof(McpProjectService).GetField("inventoryCacheOrder", BindingFlags.Instance | BindingFlags.NonPublic));
		Assert.Null(typeof(McpProjectService).GetField("projectionCacheOrder", BindingFlags.Instance | BindingFlags.NonPublic));
	}

	[Fact]
	public async Task BuildPlanWithoutRequestedPathsDoesNotBuildThePathMembershipIndex()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		for (var index = 0; index < 2_000; index++)
			workspace.CreateFile($"project/Source{index:D4}.cs", "source\n");
		await using var harness = CreateHarness(project);

		_ = await BuildAsync(harness.Service);

		Assert.Equal(0, harness.Service.PlanMembershipBuildCount);
	}

	[Fact]
	public async Task LocalProfilesAreReadOnceForEveryRootInOneListOperation()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var appData = Path.Combine(workspace.Path, "app-data");
		var canonicalProject = McpRootRegistry.ResolvePhysicalExistingPath(project, requireDirectory: true);
		new ProjectProfileStore(() => appData).SaveProfile(
			canonicalProject,
			new ProjectSelectionProfile([], [".cs"], []));
		await using var harness = CreateHarness(project);

		var catalog = await harness.Service.ReadLocalProfileCatalogAsync(
			Enumerable.Repeat(canonicalProject, 100).ToArray(),
			TestContext.Current.CancellationToken);

		Assert.Equal("available", catalog.Status);
		Assert.Contains(canonicalProject, catalog.ProjectRoots, PathComparer.Default);
		Assert.Equal(1, harness.Service.ProfileCatalogReadCount);
	}

	private static Task<ProjectContextPlan> BuildAsync(
		McpProjectService service,
		bool trackedOnly = false,
		long? maximumFileBytes = null,
		IReadOnlyList<string>? includePatterns = null) =>
		service.BuildPlanAsync(
			project: null,
			branch: null,
			paths: null,
			includePatterns,
			excludePatterns: null,
			profile: null,
			trackedOnly,
			gitScope: null,
			maximumFileBytes,
			TestContext.Current.CancellationToken,
			includeOutputMetrics: false);

	private static Task<ProjectContextPlan> BuildCachedBasePlanAsync(
		McpProjectService service,
		ProjectContextRequest request,
		CancellationToken cancellationToken)
	{
		var method = typeof(McpProjectService).GetMethod(
			"BuildBasePlanAsync",
			BindingFlags.Instance | BindingFlags.NonPublic)!;
		return (Task<ProjectContextPlan>)method.Invoke(
			service,
			[request, false, true, null, cancellationToken])!;
	}

	private static CacheHarness CreateHarness(
		string project,
		Func<string, CancellationToken, ValueTask>? inventoryBuilt = null,
		Action<string>? effectiveFileSizeRead = null,
		bool live = false)
	{
		var registry = new McpRootRegistry([project]);
		var sources = new McpProjectSourceResolver(
			registry,
			allowRemote: false,
			static () => throw new InvalidOperationException("Remote services are not used by this fixture."));
		var jail = new McpProjectRootJail(registry, sources);
		var services = McpServices.Create(
			jail,
			() => Path.Combine(Path.GetDirectoryName(project)!, "app-data"));
		var service = new McpProjectService(
			sources,
			jail,
			services,
			hidePrivateData: false,
			serverGitMode: GitFilteringMode.RespectGitIgnore,
			inventoryBuilt: inventoryBuilt,
			effectiveFileSizeRead: effectiveFileSizeRead,
			liveContext: live
				? new McpLiveContextState(registry, () => services.ProfileStore)
				: null);
		return new CacheHarness(service, services, sources);
	}

	private static object GetMonitor(McpProjectService service)
	{
		var monitors = typeof(McpProjectService)
			.GetField("rootMonitors", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(service)!;
		var values = (IEnumerable)monitors.GetType().GetProperty("Values")!.GetValue(monitors)!;
		return values.Cast<object>().Single();
	}

	private static object GetOrCreateMonitor(McpProjectService service, string root) =>
		typeof(McpProjectService)
			.GetMethod("GetOrCreateRootMonitor", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(service, [root])!;

	private static void DisableWatcher(McpProjectService service)
	{
		var monitor = GetMonitor(service);
		var watcher = (FileSystemWatcher)monitor.GetType()
			.GetField("watcher", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(monitor)!;
		watcher.EnableRaisingEvents = false;
	}

	private static void RaiseWatcherChange(McpProjectService service, string name = "Anchor.cs") =>
		RaiseWatcherEvent(service, new FileSystemEventArgs(WatcherChangeTypes.Changed, ".", name));

	private static void RaiseWatcherEvent(McpProjectService service, FileSystemEventArgs eventArgs)
	{
		var monitor = GetMonitor(service);
		monitor.GetType()
			.GetMethod("OnChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(monitor, [monitor, eventArgs]);
	}

	private static bool IsIgnoredMonitorChange(McpProjectService service, string name)
		=> IsIgnoredMonitorChange(
			service,
			new FileSystemEventArgs(WatcherChangeTypes.Changed, ".", name));

	private static bool IsIgnoredMonitorChange(McpProjectService service, FileSystemEventArgs eventArgs)
	{
		var monitor = GetMonitor(service);
		var ignoreChange = (Func<FileSystemEventArgs, bool>)monitor.GetType()
			.GetField("ignoreChange", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(monitor)!;
		return ignoreChange(eventArgs);
	}

	private static void RaiseWatcherError(McpProjectService service)
	{
		var monitor = GetMonitor(service);
		monitor.GetType()
			.GetMethod("OnError", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(monitor, [monitor, new ErrorEventArgs(new InternalBufferOverflowException("fixture"))]);
	}

	private static int ReadCacheCount(McpProjectService service, string fieldName)
	{
		var cache = typeof(McpProjectService)
			.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(service)!;
		return (int)cache.GetType().GetProperty("Count")!.GetValue(cache)!;
	}

	private static bool HasFile(ProjectContextPlan plan, string relativePath) =>
		plan.IncludedFiles.Any(path =>
			PathUtility.GetPortableRelativePath(plan.SourceRoot, path).Equals(relativePath, StringComparison.Ordinal));

	private static bool IsGitAvailable()
	{
		try
		{
			using var process = Process.Start(new ProcessStartInfo("git", "--version")
			{
				UseShellExecute = false,
				CreateNoWindow = true
			});
			return process is not null && process.WaitForExit(10_000) && process.ExitCode == 0;
		}
		catch
		{
			return false;
		}
	}

	private static void RunGit(string workingDirectory, params string[] arguments)
	{
		var startInfo = new ProcessStartInfo("git")
		{
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
		Assert.True(process.WaitForExit(20_000));
		Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
	}

	private sealed class CacheHarness(
		McpProjectService service,
		McpServices services,
		McpProjectSourceResolver sources) : IAsyncDisposable
	{
		public McpProjectService Service { get; } = service;

		public ValueTask DisposeAsync()
		{
			Service.Dispose();
			services.Dispose();
			sources.Dispose();
			return ValueTask.CompletedTask;
		}
	}
}
