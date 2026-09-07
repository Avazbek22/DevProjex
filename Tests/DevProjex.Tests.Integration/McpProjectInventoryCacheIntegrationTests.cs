using System.Collections;
using System.Diagnostics;
using System.Reflection;
using DevProjex.Application.Context;
using DevProjex.Mcp;

namespace DevProjex.Tests.Integration;

public sealed class McpProjectInventoryCacheIntegrationTests
{
	[Fact]
	public async Task BuildPlan_NarrowProjectionReusesInventoryAndTreeChangeRebuildsIt()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.CreateFile("project/src/Original.cs", "original\n");
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
		DisableWatcher(harness.Service);
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
		await using var harness = CreateHarness(project);
		var initial = await BuildAsync(harness.Service);
		RaiseWatcherError(harness.Service);
		var rebuilt = await BuildAsync(harness.Service);

		Assert.NotSame(initial, rebuilt);
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

	private static CacheHarness CreateHarness(
		string project,
		Func<string, CancellationToken, ValueTask>? inventoryBuilt = null)
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
			inventoryBuilt: inventoryBuilt);
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

	private static void DisableWatcher(McpProjectService service)
	{
		var monitor = GetMonitor(service);
		var watcher = (FileSystemWatcher)monitor.GetType()
			.GetField("watcher", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(monitor)!;
		watcher.EnableRaisingEvents = false;
	}

	private static void RaiseWatcherChange(McpProjectService service, string name = "Anchor.cs")
	{
		var monitor = GetMonitor(service);
		monitor.GetType()
			.GetMethod("OnChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(monitor, [monitor, new FileSystemEventArgs(WatcherChangeTypes.Changed, ".", name)]);
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
