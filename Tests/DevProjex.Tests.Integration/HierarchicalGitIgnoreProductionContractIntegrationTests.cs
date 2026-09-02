namespace DevProjex.Tests.Integration;

public sealed class HierarchicalGitIgnoreProductionContractIntegrationTests
{
	[Fact]
	public void CrossSectionPowerSet_DeepScopesKeepOptionsExtensionsAndBothTreePipelinesAligned()
	{
		using var temp = new TemporaryDirectory();
		SeedCrossSectionWorkspace(temp);
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var defaults = ComputeConvergedSnapshot(
			services,
			temp.Path,
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path) with
			{
				CaptureTreeInventory = true
			});

		foreach (var scenario in CrossSectionScenario.CreateMatrix())
		{
			var snapshot = ComputeConvergedSnapshot(
				services,
				temp.Path,
				CreateCrossSectionContext(temp.Path, defaults, scenario));
			AssertCrossSectionState(temp.Path, services, snapshot, scenario);
		}
	}

	[Fact]
	public void NestedScopeMutationJourney_AddRewriteDeleteRefreshesCachesAndEveryProjectionAtomically()
	{
		using var temp = new TemporaryDirectory();
		var deepScope = BuildDeepPath("workspace", "repo");
		var parentGitIgnore = Path.Combine(deepScope, ".gitignore");
		var childGitIgnore = Path.Combine(deepScope, "child", ".gitignore");
		temp.CreateFile("workspace/.gitignore", "controller.noise\n");
		temp.CreateFile("workspace/controller.noise", "keeps the controller available throughout the journey");
		temp.CreateFile(Path.Combine(deepScope, "artifact.tmp"), "tmp");
		temp.CreateFile(Path.Combine(deepScope, "artifact.log"), "log");
		temp.CreateFile(Path.Combine(deepScope, "child", "keep.log"), "log");
		temp.CreateFile("workspace/outside.tmp", "keeps extension visible");
		temp.CreateFile("workspace/outside.log", "keeps extension visible");
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();

		var snapshot = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path) with
			{
				CaptureTreeInventory = true
			},
			TestContext.Current.CancellationToken);
		AssertMutationStage(temp.Path, services, snapshot, expectedScopes: 1,
			visible: ["artifact.tmp", "artifact.log", "child/keep.log"],
			hidden: []);

		var parentPath = temp.CreateFile(parentGitIgnore, "*.tmp\n");
		snapshot = RefreshFromSnapshot(temp.Path, services, snapshot);
		AssertMutationStage(temp.Path, services, snapshot, expectedScopes: 2,
			visible: ["artifact.log", "child/keep.log"],
			hidden: ["artifact.tmp"]);

		var originalTimestamp = File.GetLastWriteTimeUtc(parentPath);
		File.WriteAllText(parentPath, "*.log\n");
		File.SetLastWriteTimeUtc(parentPath, originalTimestamp);
		snapshot = RefreshFromSnapshot(temp.Path, services, snapshot);
		AssertMutationStage(temp.Path, services, snapshot, expectedScopes: 2,
			visible: ["artifact.tmp"],
			hidden: ["artifact.log", "child/keep.log"]);

		temp.CreateFile(childGitIgnore, "!keep.log\n");
		snapshot = RefreshFromSnapshot(temp.Path, services, snapshot);
		AssertMutationStage(temp.Path, services, snapshot, expectedScopes: 3,
			visible: ["artifact.tmp", "child/keep.log"],
			hidden: ["artifact.log"]);

		File.Delete(Path.Combine(temp.Path, childGitIgnore));
		snapshot = RefreshFromSnapshot(temp.Path, services, snapshot);
		AssertMutationStage(temp.Path, services, snapshot, expectedScopes: 2,
			visible: ["artifact.tmp"],
			hidden: ["artifact.log", "child/keep.log"]);

		File.Delete(parentPath);
		snapshot = RefreshFromSnapshot(temp.Path, services, snapshot);
		AssertMutationStage(temp.Path, services, snapshot, expectedScopes: 1,
			visible: ["artifact.tmp", "artifact.log", "child/keep.log"],
			hidden: []);
	}

	[Fact]
	public async Task ParallelMultiRootScans_ProduceDeterministicScopedInventoryAndTreeResults()
	{
		using var temp = new TemporaryDirectory();
		const int rootCount = 12;
		for (var index = 0; index < rootCount; index++)
			SeedParallelRoot(temp, index);

		var selectedRoots = Enumerable.Range(0, rootCount)
			.Select(static index => $"project-{index:D2}")
			.ToHashSet(PathComparer.Default);
		var extensions = new HashSet<string>([".txt", ".shared", ".local"], StringComparer.OrdinalIgnoreCase);
		var rules = CreateTraversalRules();
		var scanner = new FileSystemScanner();
		var scans = await Task.WhenAll(
			Enumerable.Range(0, 8).Select(_ => Task.Run(
				() => ObserveParallelScan(scanner, temp.Path, selectedRoots, extensions, rules),
				TestContext.Current.CancellationToken)));

		var expected = scans[0];
		foreach (var actual in scans.Skip(1))
		{
			Assert.Equal(expected.ScopeCount, actual.ScopeCount);
			Assert.Equal(expected.Paths, actual.Paths);
			Assert.Equal(expected.Extensions, actual.Extensions);
		}

		Assert.Equal(rootCount * 2, expected.ScopeCount);
		for (var index = 0; index < rootCount; index++)
		{
			var root = $"project-{index:D2}";
			Assert.Contains($"{root}/main.txt", expected.Paths);
			Assert.Contains($"{root}/source/module/deep/keep.shared", expected.Paths);
			Assert.Contains($"{root}/source/sibling/visible.shared", expected.Paths);
			Assert.DoesNotContain($"{root}/source/module/drop.shared", expected.Paths);
			Assert.DoesNotContain($"{root}/source/module/deep/drop.local", expected.Paths);
		}
	}

	[Fact]
	public void DeepOnlyController_RootSelectionRoundTripKeepsOwnershipStateAndTreeConsistent()
	{
		using var temp = new TemporaryDirectory();
		var repoScope = BuildDeepPath("repo", "scope");
		temp.CreateFile(Path.Combine(repoScope, ".gitignore"), "*.noise\n");
		temp.CreateFile(Path.Combine(repoScope, "hidden.noise"), "ignored");
		temp.CreateFile(Path.Combine(repoScope, "visible.txt"), "visible");
		temp.CreateFile("clean/visible.txt", "visible");
		var services = ProjectLoadWorkflowRefreshHarness.CreateServices();
		var defaults = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateDefaultContext(temp.Path) with
			{
				CaptureTreeInventory = true
			},
			TestContext.Current.CancellationToken);

		AssertRootSelectionState(temp.Path, services, defaults, RootSet("repo", "clean"), expectGitOption: true);

		var repoOnly = RefreshWithRoots(temp.Path, services, defaults, RootSet("repo"));
		AssertRootSelectionState(temp.Path, services, repoOnly, RootSet("repo"), expectGitOption: true);

		var cleanOnly = RefreshWithRoots(temp.Path, services, repoOnly, RootSet("clean"));
		AssertRootSelectionState(temp.Path, services, cleanOnly, RootSet("clean"), expectGitOption: false);

		var restored = RefreshWithRoots(temp.Path, services, cleanOnly, RootSet("repo", "clean"));
		AssertRootSelectionState(temp.Path, services, restored, RootSet("repo", "clean"), expectGitOption: true);
		Assert.DoesNotContain(
			$"{repoScope.Replace('\\', '/')}/hidden.noise",
			BuildAndCompareTreePaths(temp.Path, services, restored));
	}

	private static void SeedCrossSectionWorkspace(TemporaryDirectory temp)
	{
		temp.CreateFile("alpha/App.csproj", "<Project />");
		temp.CreateFile("alpha/.gitignore", "*.tmp\nlogs/*\n!logs/keep.log\nsrc/git-owned.cs\n");
		temp.CreateFile("alpha/src/.gitignore", "!keep.tmp\n*.secret\n");
		temp.CreateFile("alpha/src/deep/.gitignore", "!visible.secret\n*.cache\n");
		temp.CreateFile("alpha/logs/drop.log", "ignored by Git");
		temp.CreateFile("alpha/logs/keep.log", "re-included by Git");
		temp.CreateFile("alpha/src/drop.tmp", "ignored by Git");
		temp.CreateFile("alpha/src/keep.tmp", "re-included by child scope");
		temp.CreateFile("alpha/src/drop.secret", "ignored by child scope");
		temp.CreateFile("alpha/src/deep/visible.secret", "re-included by deep scope");
		temp.CreateFile("alpha/src/deep/drop.cache", "ignored by deep scope");
		temp.CreateFile("alpha/src/git-owned.cs", "controller sentinel");
		temp.CreateFile("alpha/src/plain.cs", "visible");
		temp.CreateFile("alpha/src/empty.cs", string.Empty);
		temp.CreateFile("alpha/.settings/owned.cs", "dot-folder sentinel");

		temp.CreateFile("beta/package.json", "{}");
		temp.CreateFile("beta/src/app.ts", "export {};\n");
		temp.CreateFile("beta/node_modules/pkg/owned.ts", "smart-ignore sentinel");
		temp.CreateFile("beta/modules/.gitignore", "*.map\n!important.map\n");
		temp.CreateFile("beta/modules/app.map", "ignored by Git");
		temp.CreateFile("beta/modules/important.map", "re-included by Git");
		temp.CreateFile("beta/.cache/state.json", "{}");

		temp.CreateFile("gamma/readme.md", "ordinary root");
	}

	private static SelectionRefreshContext CreateCrossSectionContext(
		string rootPath,
		SelectionRefreshSnapshot defaults,
		CrossSectionScenario scenario)
	{
		var enabledIgnoreOptions = new HashSet<IgnoreOptionId>();
		if (scenario.GitIgnore)
			enabledIgnoreOptions.Add(IgnoreOptionId.UseGitIgnore);
		if (scenario.SmartIgnore)
			enabledIgnoreOptions.Add(IgnoreOptionId.SmartIgnore);
		if (scenario.DotFolders)
			enabledIgnoreOptions.Add(IgnoreOptionId.DotFolders);
		if (scenario.EmptyFiles)
			enabledIgnoreOptions.Add(IgnoreOptionId.EmptyFiles);

		var ignoreStates = Enum.GetValues<IgnoreOptionId>()
			.ToDictionary(id => id, enabledIgnoreOptions.Contains);
		var codeExtensions = new HashSet<string>([".cs", ".ts"], StringComparer.OrdinalIgnoreCase);
		var extensionStates = defaults.ExtensionOptions.ToDictionary(
			static option => option.Name,
			static _ => false,
			StringComparer.OrdinalIgnoreCase);
		foreach (var extension in new[] { ".cache", ".cs", ".gitignore", ".json", ".local", ".log", ".map", ".md", ".secret", ".tmp", ".ts" })
			extensionStates[extension] = codeExtensions.Contains(extension);

		return ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, defaults) with
		{
			AllRootFoldersChecked = true,
			RootSelectionInitialized = false,
			RootSelectionCache = new HashSet<string>(PathComparer.Default),
			RootOptionStateCache = defaults.RootOptions?.ToDictionary(
				static option => option.Name,
				static _ => true,
				PathComparer.Default),
			AllExtensionsChecked = scenario.ExtensionMode == ExtensionSelectionMode.All,
			ExtensionsSelectionInitialized = scenario.ExtensionMode == ExtensionSelectionMode.CodeOnly,
			ExtensionsSelectionCache = scenario.ExtensionMode == ExtensionSelectionMode.CodeOnly
				? codeExtensions
				: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			ExtensionOptionStateCache = scenario.ExtensionMode == ExtensionSelectionMode.CodeOnly
				? extensionStates
				: null,
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = enabledIgnoreOptions,
			IgnoreOptionStateCache = ignoreStates,
			IgnoreOptionStateCacheIsComplete = true,
			IgnoreAllPreference = null,
			CaptureTreeInventory = true
		};
	}

	private static void AssertCrossSectionState(
		string rootPath,
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		SelectionRefreshSnapshot snapshot,
		CrossSectionScenario scenario)
	{
		var because = scenario.ToString();
		Assert.Equal(["alpha", "beta", "gamma"],
			snapshot.RootOptions!.Where(static option => option.IsChecked).Select(static option => option.Name).Order());
		AssertIgnoreState(snapshot, IgnoreOptionId.UseGitIgnore, scenario.GitIgnore, because);
		AssertIgnoreState(snapshot, IgnoreOptionId.SmartIgnore, scenario.SmartIgnore, because);
		AssertIgnoreState(snapshot, IgnoreOptionId.DotFolders, scenario.DotFolders, because);
		AssertIgnoreState(snapshot, IgnoreOptionId.EmptyFiles, scenario.EmptyFiles, because);

		var checkedExtensions = snapshot.EffectiveExtensionOptions
			.Where(static option => option.IsChecked)
			.Select(static option => option.Name)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (scenario.ExtensionMode == ExtensionSelectionMode.CodeOnly)
			Assert.True(checkedExtensions.SetEquals([".cs", ".ts"]), $"{because}: unexpected extensions [{string.Join(", ", checkedExtensions)}]");
		else
			Assert.All(snapshot.EffectiveExtensionOptions, option => Assert.True(option.IsChecked, $"{because}: {option.Name} must stay checked"));

		var paths = BuildAndCompareTreePaths(rootPath, services, snapshot);
		AssertSentinel(paths, "alpha/src/plain.cs", expected: true, because);
		AssertSentinel(paths, "beta/src/app.ts", expected: true, because);
		AssertSentinel(paths, "alpha/src/git-owned.cs", expected: !scenario.GitIgnore, because);
		AssertSentinel(paths, "beta/node_modules/pkg/owned.ts", expected: !scenario.SmartIgnore, because);
		AssertSentinel(paths, "alpha/.settings/owned.cs", expected: !scenario.DotFolders, because);
		AssertSentinel(paths, "alpha/src/empty.cs", expected: !scenario.EmptyFiles, because);

		var allowsNonCode = scenario.ExtensionMode == ExtensionSelectionMode.All;
		AssertSentinel(paths, "alpha/logs/drop.log", allowsNonCode && !scenario.GitIgnore, because);
		AssertSentinel(paths, "alpha/logs/keep.log", allowsNonCode, because);
		AssertSentinel(paths, "alpha/src/drop.tmp", allowsNonCode && !scenario.GitIgnore, because);
		AssertSentinel(paths, "alpha/src/keep.tmp", allowsNonCode, because);
		AssertSentinel(paths, "alpha/src/drop.secret", allowsNonCode && !scenario.GitIgnore, because);
		AssertSentinel(paths, "alpha/src/deep/visible.secret", allowsNonCode, because);
		AssertSentinel(paths, "alpha/src/deep/drop.cache", allowsNonCode && !scenario.GitIgnore, because);
		AssertSentinel(paths, "beta/modules/app.map", allowsNonCode && !scenario.GitIgnore, because);
		AssertSentinel(paths, "beta/modules/important.map", allowsNonCode, because);
	}

	private static void AssertIgnoreState(
		SelectionRefreshSnapshot snapshot,
		IgnoreOptionId id,
		bool expected,
		string because)
	{
		var option = Assert.Single(snapshot.IgnoreOptions, option => option.Id == id);
		Assert.True(option.IsChecked == expected, $"{because}: {id} expected {expected}, actual {option.IsChecked}");
	}

	private static void AssertSentinel(
		IReadOnlyCollection<string> paths,
		string path,
		bool expected,
		string because)
	{
		Assert.True(
			paths.Contains(path, StringComparer.OrdinalIgnoreCase) == expected,
			$"{because}: '{path}' visibility expected {expected}");
	}

	private static SelectionRefreshSnapshot RefreshFromSnapshot(
		string rootPath,
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		SelectionRefreshSnapshot previous) =>
		services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, previous) with
			{
				CaptureTreeInventory = true
			},
			TestContext.Current.CancellationToken);

	private static SelectionRefreshSnapshot ComputeConvergedSnapshot(
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		string rootPath,
		SelectionRefreshContext context)
	{
		var previous = services.Engine.ComputeFullRefreshSnapshot(context, TestContext.Current.CancellationToken);
		for (var pass = 0; pass < 6; pass++)
		{
			var next = services.Engine.ComputeFullRefreshSnapshot(
				ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, previous) with
				{
					CaptureTreeInventory = context.CaptureTreeInventory
				},
				TestContext.Current.CancellationToken);
			try
			{
				ProjectLoadWorkflowRefreshHarness.AssertEquivalentSnapshots(previous, next);
				return next;
			}
			catch (Xunit.Sdk.XunitException)
			{
				previous = next;
			}
		}

		var final = services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, previous) with
			{
				CaptureTreeInventory = context.CaptureTreeInventory
			},
			TestContext.Current.CancellationToken);
		ProjectLoadWorkflowRefreshHarness.AssertEquivalentSnapshots(previous, final);
		return final;
	}

	private static void AssertMutationStage(
		string rootPath,
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		SelectionRefreshSnapshot snapshot,
		int expectedScopes,
		IReadOnlyList<string> visible,
		IReadOnlyList<string> hidden)
	{
		var gitIgnore = Assert.Single(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.UseGitIgnore);
		Assert.True(gitIgnore.IsChecked);
		Assert.Equal(expectedScopes, Assert.IsType<ProjectTreeInventorySnapshot>(snapshot.TreeInventory).DiscoveredGitIgnoreMatchers.Count);
		var paths = BuildAndCompareTreePaths(rootPath, services, snapshot);
		var deepScope = BuildDeepPath("workspace", "repo").Replace('\\', '/');
		foreach (var relativePath in visible)
			Assert.Contains($"{deepScope}/{relativePath}", paths);
		foreach (var relativePath in hidden)
			Assert.DoesNotContain($"{deepScope}/{relativePath}", paths);
		Assert.Contains("workspace/outside.tmp", paths);
		Assert.Contains("workspace/outside.log", paths);
	}

	private static void SeedParallelRoot(TemporaryDirectory temp, int index)
	{
		var root = $"project-{index:D2}";
		temp.CreateFile($"{root}/main.txt", "visible");
		temp.CreateFile($"{root}/source/module/.gitignore", "*.shared\n");
		temp.CreateFile($"{root}/source/module/drop.shared", "ignored");
		temp.CreateFile($"{root}/source/module/deep/.gitignore", "!keep.shared\n*.local\n");
		temp.CreateFile($"{root}/source/module/deep/keep.shared", "visible");
		temp.CreateFile($"{root}/source/module/deep/drop.local", "ignored");
		temp.CreateFile($"{root}/source/sibling/visible.shared", "outside scope");
	}

	private static ParallelScanObservation ObserveParallelScan(
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
		var inventory = Assert.IsType<ProjectTreeInventorySnapshot>(scan.Value.TreeInventory);
		var tree = new TreeBuilder().Build(
			inventory,
			new TreeFilterOptions(extensions, selectedRoots, rules),
			TestContext.Current.CancellationToken);
		return new ParallelScanObservation(
			FlattenRelativePaths(rootPath, tree.Root),
			inventory.DiscoveredGitIgnoreMatchers.Count,
			scan.Value.IgnoreSection.VisibleExtensions.Order(StringComparer.OrdinalIgnoreCase).ToArray());
	}

	private static SelectionRefreshSnapshot RefreshWithRoots(
		string rootPath,
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		SelectionRefreshSnapshot previous,
		IReadOnlySet<string> selectedRoots)
	{
		var rootStates = previous.RootOptions!.ToDictionary(
			static option => option.Name,
			option => selectedRoots.Contains(option.Name),
			PathComparer.Default);
		return services.Engine.ComputeFullRefreshSnapshot(
			ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, previous) with
			{
				AllRootFoldersChecked = previous.RootOptions!.Count == selectedRoots.Count,
				RootSelectionInitialized = true,
				RootSelectionCache = selectedRoots,
				RootOptionStateCache = rootStates,
				CaptureTreeInventory = true
			},
			TestContext.Current.CancellationToken);
	}

	private static void AssertRootSelectionState(
		string rootPath,
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		SelectionRefreshSnapshot snapshot,
		IReadOnlySet<string> expectedRoots,
		bool expectGitOption)
	{
		var actualRoots = snapshot.RootOptions!
			.Where(static option => option.IsChecked)
			.Select(static option => option.Name)
			.ToHashSet(PathComparer.Default);
		Assert.True(actualRoots.SetEquals(expectedRoots));
		var gitOptions = snapshot.IgnoreOptions.Where(option => option.Id == IgnoreOptionId.UseGitIgnore).ToArray();
		Assert.Equal(expectGitOption ? 1 : 0, gitOptions.Length);
		if (expectGitOption)
			Assert.True(gitOptions[0].IsChecked);
		var paths = BuildAndCompareTreePaths(rootPath, services, snapshot);
		Assert.Equal(expectedRoots.Count, paths.Count(path => !path.Contains('/')));
	}

	private static List<string> BuildAndCompareTreePaths(
		string rootPath,
		ProjectLoadWorkflowRefreshHarness.WorkflowServices services,
		SelectionRefreshSnapshot snapshot)
	{
		var selectedRoots = snapshot.RootOptions!
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
		var rules = services.IgnoreRulesService.Build(rootPath, ignoreOptions, selectedRoots);
		var options = new TreeFilterOptions(extensions, selectedRoots, rules);
		var builder = new TreeBuilder();
		var direct = builder.Build(rootPath, options, TestContext.Current.CancellationToken);
		var inventory = builder.Build(
			Assert.IsType<ProjectTreeInventorySnapshot>(snapshot.TreeInventory),
			options,
			TestContext.Current.CancellationToken);
		var directPaths = FlattenRelativePaths(rootPath, direct.Root);
		var inventoryPaths = FlattenRelativePaths(rootPath, inventory.Root);

		Assert.Equal(directPaths, inventoryPaths);
		return inventoryPaths;
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

	private static IgnoreRules CreateTraversalRules() =>
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

	private static string BuildDeepPath(string root, string leaf)
	{
		var segments = new List<string> { root };
		for (var depth = 0; depth < 12; depth++)
			segments.Add($"level-{depth:D2}");
		segments.Add(leaf);
		return Path.Combine([.. segments]);
	}

	private static HashSet<string> RootSet(params string[] roots) =>
		new(roots, PathComparer.Default);

	private enum ExtensionSelectionMode
	{
		All,
		CodeOnly
	}

	private sealed record CrossSectionScenario(
		bool GitIgnore,
		bool SmartIgnore,
		bool DotFolders,
		bool EmptyFiles,
		ExtensionSelectionMode ExtensionMode)
	{
		public static IEnumerable<CrossSectionScenario> CreateMatrix()
		{
			foreach (var gitIgnore in new[] { false, true })
			foreach (var smartIgnore in new[] { false, true })
			foreach (var dotFolders in new[] { false, true })
			foreach (var emptyFiles in new[] { false, true })
			foreach (var extensionMode in Enum.GetValues<ExtensionSelectionMode>())
				yield return new CrossSectionScenario(gitIgnore, smartIgnore, dotFolders, emptyFiles, extensionMode);
		}

		public override string ToString() =>
			$"Git={GitIgnore}; Smart={SmartIgnore}; DotFolders={DotFolders}; EmptyFiles={EmptyFiles}; Extensions={ExtensionMode}";
	}

	private sealed record ParallelScanObservation(
		IReadOnlyList<string> Paths,
		int ScopeCount,
		IReadOnlyList<string> Extensions);
}
