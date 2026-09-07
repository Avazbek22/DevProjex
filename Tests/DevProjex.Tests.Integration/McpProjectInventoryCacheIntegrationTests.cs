using System.Collections;
using System.Diagnostics;
using System.Reflection;
using DevProjex.Application.Context;
using DevProjex.Mcp;

namespace DevProjex.Tests.Integration;

public sealed class McpProjectInventoryCacheIntegrationTests
{
	[Fact(Timeout = 30_000)]
	public async Task BuildPlan_ChangeAfterTraversalCannotPublishAStalePlanUnderANewRevision()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.CreateDirectory("project/nested");
		workspace.CreateFile("project/nested/Hidden.cs", "hidden\n");
		var ignore = workspace.CreateFile("project/nested/.gitignore", string.Empty);
		var buildReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var buildCount = 0;
		await using var harness = CreateHarness(
			project,
			async (_, cancellationToken) =>
			{
				if (Interlocked.Increment(ref buildCount) != 1)
					return;
				buildReached.TrySetResult();
				await resume.Task.WaitAsync(cancellationToken);
			});

		var pending = BuildAsync(harness.Service);
		await buildReached.Task.WaitAsync(TestContext.Current.CancellationToken);
		File.WriteAllText(ignore, "Hidden.cs\n");
		File.SetLastWriteTimeUtc(ignore, DateTime.UtcNow.AddSeconds(2));
		resume.TrySetResult();
		var plan = await pending;

		Assert.True(buildCount >= 2);
		Assert.False(HasFile(plan, "nested/Hidden.cs"));
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

	private static void RaiseWatcherChange(McpProjectService service)
	{
		var monitor = GetMonitor(service);
		monitor.GetType()
			.GetMethod("OnChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(monitor, [monitor, new FileSystemEventArgs(WatcherChangeTypes.Changed, ".", "Anchor.cs")]);
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
