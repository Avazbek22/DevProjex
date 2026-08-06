namespace DevProjex.Tests.Integration;

public sealed class HierarchicalGitIgnoreTraversalIntegrationTests
{
	[Fact]
	public void DeepNestedScopes_ApplyParentChildPrecedenceAndSiblingIsolationAcrossInventoryAndTree()
	{
		using var temp = new TemporaryDirectory();
		var repo = SeedDeepWorkspace(temp);
		var rules = CreateRules(enableGitIgnore: true);
		var selectedRoots = new HashSet<string>(["workspace", "sibling"], PathComparer.Default);
		var allowedExtensions = new HashSet<string>(
			[".txt", ".cache", ".local", ".noise"],
			StringComparer.OrdinalIgnoreCase);
		var options = new TreeFilterOptions(allowedExtensions, selectedRoots, rules);

		var scanner = new FileSystemScanner();
		var workspace = scanner.ScanProjectWorkspace(
			new ProjectWorkspaceScanRequest(
				temp.Path,
				selectedRoots,
				rules,
				rules,
				new ExtensionSetInclusionPolicy(allowedExtensions),
				CaptureTreeInventory: true,
				IncludeDirectoryToggleProbeRoots: false,
				IncludeControllerImpactProbeRoots: false),
			TestContext.Current.CancellationToken);
		var inventory = Assert.IsType<ProjectTreeInventorySnapshot>(workspace.Value.TreeInventory);
		var builder = new TreeBuilder();
		var directTree = builder.Build(temp.Path, options, TestContext.Current.CancellationToken);
		var inventoryTree = builder.Build(inventory, options, TestContext.Current.CancellationToken);
		var directPaths = FlattenRelativePaths(temp.Path, directTree.Root);
		var inventoryPaths = FlattenRelativePaths(temp.Path, inventoryTree.Root);

		Assert.Equal(directPaths, inventoryPaths);
		Assert.Equal(3, inventory.DiscoveredGitIgnoreMatchers.Count);
		Assert.Contains(Relative(repo, "readme.txt"), inventoryPaths);
		Assert.Contains(Relative(repo, "module/keep.cache"), inventoryPaths);
		Assert.Contains(Relative(repo, "module/keep.local"), inventoryPaths);
		Assert.Contains(Relative(repo, "other/drop.local"), inventoryPaths);
		Assert.Contains("sibling/visible.cache", inventoryPaths);
		Assert.DoesNotContain(Relative(repo, "drop.cache"), inventoryPaths);
		Assert.DoesNotContain(Relative(repo, "other/keep.cache"), inventoryPaths);
		Assert.DoesNotContain(Relative(repo, "module/drop.local"), inventoryPaths);
		Assert.DoesNotContain(Relative(repo, "module/child/hidden.txt"), inventoryPaths);
		Assert.DoesNotContain(Relative(repo, "generated/output.noise"), inventoryPaths);
		Assert.DoesNotContain(".noise", workspace.Value.IgnoreSection.VisibleExtensions);
	}

	[Fact]
	public void DisabledController_DiscoversEveryScopeAndMeasuresImpactWithoutFilteringOtherSections()
	{
		using var temp = new TemporaryDirectory();
		var repo = SeedDeepWorkspace(temp);
		var rules = CreateRules(enableGitIgnore: false);
		var selectedRoots = new HashSet<string>(["workspace", "sibling"], PathComparer.Default);
		var allowedExtensions = new HashSet<string>(
			[".txt", ".cache", ".local", ".noise"],
			StringComparer.OrdinalIgnoreCase);
		var scanner = new FileSystemScanner();

		var workspace = scanner.ScanProjectWorkspace(
			new ProjectWorkspaceScanRequest(
				temp.Path,
				selectedRoots,
				rules,
				rules,
				new ExtensionSetInclusionPolicy(allowedExtensions),
				CaptureTreeInventory: true,
				IncludeDirectoryToggleProbeRoots: false,
				IncludeControllerImpactProbeRoots: false),
			TestContext.Current.CancellationToken);
		var inventory = Assert.IsType<ProjectTreeInventorySnapshot>(workspace.Value.TreeInventory);
		var tree = new TreeBuilder().Build(
			inventory,
			new TreeFilterOptions(allowedExtensions, selectedRoots, rules),
			TestContext.Current.CancellationToken);
		var paths = FlattenRelativePaths(temp.Path, tree.Root);

		Assert.Equal(3, inventory.DiscoveredGitIgnoreMatchers.Count);
		Assert.True(workspace.Value.IgnoreSection.ControllerImpactCounts.GitIgnore >= 5);
		Assert.Contains(Relative(repo, "drop.cache"), paths);
		Assert.Contains(Relative(repo, "module/drop.local"), paths);
		Assert.Contains(Relative(repo, "module/child/hidden.txt"), paths);
		Assert.Contains(Relative(repo, "generated/output.noise"), paths);
		Assert.Contains(".noise", workspace.Value.IgnoreSection.VisibleExtensions);
	}

	[Fact]
	public void FullRefresh_DeepOnlyGitIgnorePromotesCheckedControllerAndConvergesWithoutScopeProbeSupport()
	{
		using var temp = new TemporaryDirectory();
		var repo = SeedDeepWorkspace(temp);
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var context = ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path) with
		{
			CaptureTreeInventory = true
		};

		var snapshot = services.Engine.ComputeFullRefreshSnapshot(
			context,
			TestContext.Current.CancellationToken);
		var gitIgnore = Assert.Single(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.UseGitIgnore);
		Assert.True(gitIgnore.IsChecked);
		Assert.True(snapshot.ControllerImpactCounts.GitIgnore >= 5);
		Assert.NotNull(snapshot.TreeInventory);
		Assert.DoesNotContain(
			snapshot.EffectiveExtensionOptions,
			static option => option.Name.Equals(".noise", StringComparison.OrdinalIgnoreCase));

		var selectedRoots = snapshot.RootOptions!
			.Where(static option => option.IsChecked)
			.Select(static option => option.Name)
			.ToHashSet(PathComparer.Default);
		var selectedExtensions = snapshot.EffectiveExtensionOptions
			.Where(static option => option.IsChecked)
			.Select(static option => option.Name)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var selectedIgnoreOptions = snapshot.IgnoreOptions
			.Where(static option => option.IsChecked)
			.Select(static option => option.Id)
			.ToArray();
		var rules = services.IgnoreRulesService.Build(temp.Path, selectedIgnoreOptions, selectedRoots);
		var tree = new TreeBuilder().Build(
			snapshot.TreeInventory!,
			new TreeFilterOptions(selectedExtensions, selectedRoots, rules),
			TestContext.Current.CancellationToken);
		var paths = FlattenRelativePaths(temp.Path, tree.Root);

		Assert.DoesNotContain(Relative(repo, "drop.cache"), paths);
		Assert.DoesNotContain(Relative(repo, "generated/output.noise"), paths);

		var converged = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(temp.Path, snapshot) with
			{
				CaptureTreeInventory = true
			},
			TestContext.Current.CancellationToken);
		ProjectLoadWorkflowRefreshHarness.AssertEquivalentSnapshots(snapshot, converged);
	}

	[Fact]
	public void SameLengthSameTimestampRewrite_InvalidatesNestedMatcherForWorkspaceAndDirectTree()
	{
		using var temp = new TemporaryDirectory();
		var repo = BuildDeepRepoPath();
		var gitIgnorePath = temp.CreateFile(Path.Combine(repo, ".gitignore"), "*.tmp\n");
		temp.CreateFile(Path.Combine(repo, "data.tmp"), "tmp");
		temp.CreateFile(Path.Combine(repo, "data.log"), "log");
		var originalTimestamp = File.GetLastWriteTimeUtc(gitIgnorePath);
		var rules = CreateRules(enableGitIgnore: true);
		var selectedRoots = new HashSet<string>(["workspace"], PathComparer.Default);
		var extensions = new HashSet<string>([".tmp", ".log"], StringComparer.OrdinalIgnoreCase);
		var scanner = new FileSystemScanner();

		var before = BuildWorkspaceTree(scanner, temp.Path, selectedRoots, extensions, rules);
		Assert.DoesNotContain(Relative(repo, "data.tmp"), FlattenRelativePaths(temp.Path, before.Root));

		File.WriteAllText(gitIgnorePath, "*.log\n");
		File.SetLastWriteTimeUtc(gitIgnorePath, originalTimestamp);

		var after = BuildWorkspaceTree(scanner, temp.Path, selectedRoots, extensions, rules);
		var direct = new TreeBuilder().Build(
			temp.Path,
			new TreeFilterOptions(extensions, selectedRoots, rules),
			TestContext.Current.CancellationToken);
		var afterPaths = FlattenRelativePaths(temp.Path, after.Root);

		Assert.Equal(FlattenRelativePaths(temp.Path, direct.Root), afterPaths);
		Assert.Contains(Relative(repo, "data.tmp"), afterPaths);
		Assert.DoesNotContain(Relative(repo, "data.log"), afterPaths);
	}

	[Fact]
	public void FullRefresh_DeepControllerToggleCycleKeepsRootsStableAndUpdatesExtensionsAndTreeTogether()
	{
		using var temp = new TemporaryDirectory();
		var repo = SeedDeepWorkspace(temp);
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var defaults = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path) with
			{
				CaptureTreeInventory = true
			},
			TestContext.Current.CancellationToken);
		var allOffStates = defaults.IgnoreOptionStateCache.ToDictionary(static pair => pair.Key, static _ => false);

		var allOff = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(temp.Path, defaults) with
			{
				IgnoreSelectionInitialized = true,
				IgnoreSelectionCache = new HashSet<IgnoreOptionId>(),
				IgnoreOptionStateCache = allOffStates,
				IgnoreOptionStateCacheIsComplete = true,
				IgnoreAllPreference = false,
				CaptureTreeInventory = true
			},
			TestContext.Current.CancellationToken);
		var disabledGitIgnore = Assert.Single(
			allOff.IgnoreOptions,
			option => option.Id == IgnoreOptionId.UseGitIgnore);
		Assert.False(disabledGitIgnore.IsChecked);
		Assert.Contains(
			allOff.EffectiveExtensionOptions,
			static option => option.Name.Equals(".noise", StringComparison.OrdinalIgnoreCase));
		Assert.Contains(Relative(repo, "generated/output.noise"), BuildSnapshotTreePaths(temp.Path, services, allOff));

		var gitOnStates = allOff.IgnoreOptionStateCache.ToDictionary(static pair => pair.Key, static pair => pair.Value);
		gitOnStates[IgnoreOptionId.UseGitIgnore] = true;
		var gitOn = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(temp.Path, allOff) with
			{
				IgnoreSelectionInitialized = true,
				IgnoreSelectionCache = new HashSet<IgnoreOptionId> { IgnoreOptionId.UseGitIgnore },
				IgnoreOptionStateCache = gitOnStates,
				IgnoreOptionStateCacheIsComplete = true,
				IgnoreAllPreference = null,
				CaptureTreeInventory = true
			},
			TestContext.Current.CancellationToken);

		Assert.Equal(
			allOff.RootOptions!.Select(static option => option.Name),
			gitOn.RootOptions!.Select(static option => option.Name));
		Assert.DoesNotContain(
			gitOn.EffectiveExtensionOptions,
			static option => option.Name.Equals(".noise", StringComparison.OrdinalIgnoreCase));
		Assert.DoesNotContain(Relative(repo, "generated/output.noise"), BuildSnapshotTreePaths(temp.Path, services, gitOn));
	}

	[Fact]
	public void ParentExcludedDirectory_DoesNotLoadChildRulesButReachableSiblingScopeStillApplies()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile("workspace/.gitignore", "blocked/\n");
		temp.CreateFile("workspace/blocked/.gitignore", "!visible.txt\n");
		temp.CreateFile("workspace/blocked/visible.txt", "cannot be re-included");
		temp.CreateFile("workspace/open/.gitignore", "*.tmp\n");
		temp.CreateFile("workspace/open/drop.tmp", "ignored");
		temp.CreateFile("workspace/open/keep.txt", "visible");
		var rules = CreateRules(enableGitIgnore: true);
		var selectedRoots = new HashSet<string>(["workspace"], PathComparer.Default);
		var extensions = new HashSet<string>([".tmp", ".txt"], StringComparer.OrdinalIgnoreCase);
		var scanner = new FileSystemScanner();

		var scan = scanner.ScanProjectWorkspace(
			new ProjectWorkspaceScanRequest(
				temp.Path,
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
		var paths = FlattenRelativePaths(temp.Path, tree.Root);

		Assert.Equal(2, inventory.DiscoveredGitIgnoreMatchers.Count);
		Assert.DoesNotContain("workspace/blocked", paths);
		Assert.DoesNotContain("workspace/blocked/visible.txt", paths);
		Assert.DoesNotContain("workspace/open/drop.tmp", paths);
		Assert.Contains("workspace/open/keep.txt", paths);
		Assert.DoesNotContain(".tmp", scan.Value.IgnoreSection.VisibleExtensions);
		Assert.Contains(".txt", scan.Value.IgnoreSection.VisibleExtensions);
	}

	private static string SeedDeepWorkspace(TemporaryDirectory temp)
	{
		var repo = BuildDeepRepoPath();
		temp.CreateFile(Path.Combine(repo, ".gitignore"), "*.cache\n*.noise\ngenerated/\n");
		temp.CreateFile(Path.Combine(repo, "module", ".gitignore"), "!keep.cache\n*.local\n!keep.local\n");
		temp.CreateFile(Path.Combine(repo, "module", "child", ".gitignore"), "*.txt\n");
		temp.CreateFile(Path.Combine(repo, "readme.txt"), "visible");
		temp.CreateFile(Path.Combine(repo, "drop.cache"), "ignored");
		temp.CreateFile(Path.Combine(repo, "generated", "output.noise"), "ignored");
		temp.CreateFile(Path.Combine(repo, "module", "keep.cache"), "visible");
		temp.CreateFile(Path.Combine(repo, "module", "drop.local"), "ignored");
		temp.CreateFile(Path.Combine(repo, "module", "keep.local"), "visible");
		temp.CreateFile(Path.Combine(repo, "module", "child", "hidden.txt"), "ignored");
		temp.CreateFile(Path.Combine(repo, "other", "keep.cache"), "ignored");
		temp.CreateFile(Path.Combine(repo, "other", "drop.local"), "visible");
		temp.CreateFile("sibling/visible.cache", "outside scope");
		return repo.Replace('\\', '/');
	}

	private static string BuildDeepRepoPath()
	{
		var segments = new List<string> { "workspace" };
		for (var depth = 0; depth < 12; depth++)
			segments.Add($"level-{depth:D2}");
		segments.Add("repo");
		return Path.Combine([.. segments]);
	}

	private static IgnoreRules CreateRules(bool enableGitIgnore) =>
		new(
			IgnoreHiddenFolders: false,
			IgnoreHiddenFiles: false,
			IgnoreDotFolders: false,
			IgnoreDotFiles: false,
			SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
		{
			EnableGitIgnoreTraversal = enableGitIgnore,
			GitIgnoreCandidateMatchesActiveRules = enableGitIgnore
		};

	private static TreeBuildResult BuildWorkspaceTree(
		FileSystemScanner scanner,
		string rootPath,
		IReadOnlySet<string> selectedRoots,
		IReadOnlySet<string> extensions,
		IgnoreRules rules)
	{
		var scan = scanner.ScanProjectWorkspace(
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
		return new TreeBuilder().Build(
			Assert.IsType<ProjectTreeInventorySnapshot>(scan.Value.TreeInventory),
			new TreeFilterOptions(extensions, selectedRoots, rules),
			TestContext.Current.CancellationToken);
	}

	private static List<string> BuildSnapshotTreePaths(
		string rootPath,
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		SelectionRefreshSnapshot snapshot)
	{
		var roots = snapshot.RootOptions!
			.Where(static option => option.IsChecked)
			.Select(static option => option.Name)
			.ToHashSet(PathComparer.Default);
		var extensions = snapshot.EffectiveExtensionOptions
			.Where(static option => option.IsChecked)
			.Select(static option => option.Name)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var ignoreOptions = snapshot.IgnoreOptions
			.Where(static option => option.IsChecked)
			.Select(static option => option.Id)
			.ToArray();
		var rules = services.IgnoreRulesService.Build(rootPath, ignoreOptions, roots);
		var tree = new TreeBuilder().Build(
			Assert.IsType<ProjectTreeInventorySnapshot>(snapshot.TreeInventory),
			new TreeFilterOptions(extensions, roots, rules),
			TestContext.Current.CancellationToken);
		return FlattenRelativePaths(rootPath, tree.Root);
	}

	private static List<string> FlattenRelativePaths(string rootPath, FileSystemNode root)
	{
		var paths = new List<string>();
		var pending = new Stack<FileSystemNode>();
		for (var index = root.Children.Count - 1; index >= 0; index--)
			pending.Push(root.Children[index]);

		while (pending.Count > 0)
		{
			var node = pending.Pop();
			paths.Add(Path.GetRelativePath(rootPath, node.FullPath).Replace('\\', '/'));
			for (var index = node.Children.Count - 1; index >= 0; index--)
				pending.Push(node.Children[index]);
		}

		paths.Sort(StringComparer.OrdinalIgnoreCase);
		return paths;
	}

	private static string Relative(string repo, string path) =>
		$"{repo}/{path}".Replace('\\', '/');
}
