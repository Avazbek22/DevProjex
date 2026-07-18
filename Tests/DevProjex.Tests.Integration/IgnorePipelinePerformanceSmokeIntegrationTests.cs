namespace DevProjex.Tests.Integration;

[Trait("Category", "LocalPerformance")]
public sealed class IgnorePipelinePerformanceSmokeIntegrationTests
{
	[Fact]
	public void IgnoreSectionSnapshot_TenThousandFiles_CompletesWithinSmokeBudget()
	{
		using var temp = CreateSyntheticWorkspace(fileCount: 10_000);
		var elapsed = MeasureIgnoreSnapshot(temp.Path);

		Assert.True(
			elapsed < TimeSpan.FromSeconds(5),
			$"10k ignore snapshot smoke exceeded budget: {elapsed}.");
	}

	[Fact]
	public void IgnoreSectionSnapshot_RepeatedLargeScans_DoNotRetainWorkspaceSizedManagedMemory()
	{
		using var temp = CreateSyntheticWorkspace(fileCount: 10_000);

		// Warm up JIT/static caches before measuring retained memory. The assertion is about
		// scan result lifetime, not one-time runtime initialization costs.
		RunIgnoreSnapshot(temp.Path);
		ForceFullCollection();
		var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);

		for (var attempt = 0; attempt < 3; attempt++)
			RunIgnoreSnapshot(temp.Path);

		ForceFullCollection();
		var retainedBytes = Math.Max(0, GC.GetTotalMemory(forceFullCollection: true) - baselineBytes);

		Assert.True(
			retainedBytes < 64L * 1024 * 1024,
			$"Repeated ignore scans retained too much managed memory: {retainedBytes:N0} bytes.");
	}

	[Fact]
	public void HierarchicalGitIgnore_OneHundredTwentyEightScopesAndEightThousandRules_CompletesWithinSmokeBudget()
	{
		using var temp = CreateHierarchicalGitIgnoreWorkspace(scopeCount: 128, rulesPerScope: 64);
		var observation = MeasureHierarchicalGitIgnore(temp.Path, expectedScopeCount: 128);

		Assert.True(
			observation.Elapsed < TimeSpan.FromSeconds(10),
			$"Hierarchical GitIgnore smoke exceeded budget: {observation.Elapsed}.");
		Assert.DoesNotContain("repo/scope-000/drop.cache", observation.Paths);
		Assert.DoesNotContain("repo/scope-127/drop.cache", observation.Paths);
		Assert.Contains("repo/scope-000/keep.cache", observation.Paths);
		Assert.Contains("repo/scope-127/visible.txt", observation.Paths);
	}

	[Theory]
	[InlineData(50_000, 90)]
	[InlineData(100_000, 180)]
	public void IgnoreSectionSnapshot_LargeWorkspaces_CompletesWithinOptInSmokeBudget(
		int fileCount,
		int maxSeconds)
	{
		if (!string.Equals(
			    Environment.GetEnvironmentVariable("DEVPROJEX_RUN_LARGE_PERF_TESTS"),
			    "1",
			    StringComparison.Ordinal))
		{
			return;
		}

		using var temp = CreateSyntheticWorkspace(fileCount);
		var elapsed = MeasureIgnoreSnapshot(temp.Path);

		Assert.True(
			elapsed < TimeSpan.FromSeconds(maxSeconds),
			$"{fileCount:N0} ignore snapshot smoke exceeded budget: {elapsed}.");
	}

	[Fact]
	public void HierarchicalGitIgnore_OneThousandScopesAndOneHundredThousandRules_CompletesWithinOptInSmokeBudget()
	{
		if (!string.Equals(
			    Environment.GetEnvironmentVariable("DEVPROJEX_RUN_LARGE_PERF_TESTS"),
			    "1",
			    StringComparison.Ordinal))
		{
			return;
		}

		using var temp = CreateHierarchicalGitIgnoreWorkspace(scopeCount: 1_000, rulesPerScope: 100);
		var observation = MeasureHierarchicalGitIgnore(temp.Path, expectedScopeCount: 1_000);

		Assert.True(
			observation.Elapsed < TimeSpan.FromSeconds(60),
			$"100k hierarchical GitIgnore rule smoke exceeded budget: {observation.Elapsed}.");
		Assert.DoesNotContain("repo/scope-999/drop.cache", observation.Paths);
		Assert.Contains("repo/scope-999/keep.cache", observation.Paths);
	}

	private static TimeSpan MeasureIgnoreSnapshot(string rootPath)
	{
		var stopwatch = Stopwatch.StartNew();
		RunIgnoreSnapshot(rootPath);
		stopwatch.Stop();

		return stopwatch.Elapsed;
	}

	private static void RunIgnoreSnapshot(string rootPath)
	{
		var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
		var rules = CreateIgnoreRules();
		var snapshot = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			rootPath,
			["src", "docs", "tests"],
			rules,
			rules,
			effectiveAllowedExtensions: null,
			includeDirectoryToggleProbeRoots: true);

		Assert.Contains(".cs", snapshot.Value.Extensions);
		Assert.True(snapshot.Value.EffectiveIgnoreOptionCounts.DotFolders >= 1);
	}

	private static HierarchicalGitIgnoreObservation MeasureHierarchicalGitIgnore(
		string rootPath,
		int expectedScopeCount)
	{
		var selectedRoots = new HashSet<string>(["repo"], PathComparer.Default);
		var extensions = new HashSet<string>([".cache", ".txt"], StringComparer.OrdinalIgnoreCase);
		var rules = CreateHierarchicalGitIgnoreRules();
		var stopwatch = Stopwatch.StartNew();
		var scan = new FileSystemScanner().ScanProjectWorkspace(
			new ProjectWorkspaceScanRequest(
				rootPath,
				selectedRoots,
				rules,
				rules,
				new ExtensionSetInclusionPolicy(extensions),
				CaptureTreeInventory: true,
				IncludeDirectoryToggleProbeRoots: false,
				IncludeControllerImpactProbeRoots: false),
			TestContext.Current.CancellationToken);
		var inventory = Assert.IsType<ProjectTreeInventorySnapshot>(scan.Value.TreeInventory);
		var tree = new TreeBuilder().Build(
			inventory,
			new TreeFilterOptions(extensions, selectedRoots, rules),
			TestContext.Current.CancellationToken);
		stopwatch.Stop();

		Assert.Equal(expectedScopeCount, inventory.DiscoveredGitIgnoreMatchers.Count);
		return new HierarchicalGitIgnoreObservation(
			stopwatch.Elapsed,
			FlattenRelativePaths(rootPath, tree.Root));
	}

	private static IgnoreRules CreateIgnoreRules()
		=> new(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: true,
			IgnoreDotFiles: true,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "node_modules" },
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
		{
			IgnoreEmptyFolders = true,
			IgnoreEmptyFiles = true,
			IgnoreExtensionlessFiles = true,
			UseSmartIgnore = true
		};

	private static IgnoreRules CreateHierarchicalGitIgnoreRules() =>
		new(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
		{
			EnableGitIgnoreTraversal = true,
			GitIgnoreCandidateMatchesActiveRules = true
		};

	private static void ForceFullCollection()
	{
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
	}

	private static TemporaryDirectory CreateSyntheticWorkspace(int fileCount)
	{
		var temp = new TemporaryDirectory();
		temp.CreateFile("src/App.cs", "class App {}");
		temp.CreateFile("docs/readme.md", "# docs");
		temp.CreateFile("tests/AppTests.cs", "class AppTests {}");
		temp.CreateFile(".idea/workspace.xml", "<project />");
		temp.CreateFile("node_modules/pkg/generated.js", "x");

		var roots = new[] { "src", "docs", "tests" };
		for (var index = 0; index < fileCount; index++)
		{
			var root = roots[index % roots.Length];
			var bucket = index / 100;
			var extension = index % 5 == 0 ? ".md" : ".cs";
			temp.CreateFile($"{root}/bucket-{bucket:D4}/file-{index:D6}{extension}", "content");
		}

		return temp;
	}

	private static TemporaryDirectory CreateHierarchicalGitIgnoreWorkspace(
		int scopeCount,
		int rulesPerScope)
	{
		var temp = new TemporaryDirectory();
		for (var scopeIndex = 0; scopeIndex < scopeCount; scopeIndex++)
		{
			var scope = $"repo/scope-{scopeIndex:D3}";
			var rules = new StringBuilder()
				.AppendLine("*.cache")
				.AppendLine("!keep.cache");
			for (var ruleIndex = 2; ruleIndex < rulesPerScope; ruleIndex++)
				rules.Append("unused-").Append(scopeIndex).Append('-').Append(ruleIndex).AppendLine(".artifact");

			temp.CreateFile($"{scope}/.gitignore", rules.ToString());
			temp.CreateFile($"{scope}/drop.cache", "ignored");
			temp.CreateFile($"{scope}/keep.cache", "visible");
			temp.CreateFile($"{scope}/visible.txt", "visible");
		}

		return temp;
	}

	private static List<string> FlattenRelativePaths(string rootPath, FileSystemNode root)
	{
		var paths = new List<string>();
		var pending = new Stack<FileSystemNode>();
		pending.Push(root);
		while (pending.Count > 0)
		{
			var node = pending.Pop();
			paths.Add(Path.GetRelativePath(rootPath, node.FullPath).Replace('\\', '/'));
			for (var index = node.Children.Count - 1; index >= 0; index--)
				pending.Push(node.Children[index]);
		}

		return paths;
	}

	private sealed record HierarchicalGitIgnoreObservation(
		TimeSpan Elapsed,
		IReadOnlyList<string> Paths);
}
