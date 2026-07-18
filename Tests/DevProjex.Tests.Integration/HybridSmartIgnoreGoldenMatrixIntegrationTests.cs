namespace DevProjex.Tests.Integration;

public sealed class HybridSmartIgnoreGoldenMatrixIntegrationTests
{
	private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".bin",
		".cs",
		".dll",
		".js",
		".json",
		".nupkg",
		".project",
		".pyc",
		".toml",
		".txt"
	};

	public enum WorkspaceView
	{
		DirectGitProject,
		DirectSmartProject,
		ParentGitOnly,
		ParentSmartOnly,
		ParentMixed,
		ParentPlainOnly
	}

	public static IEnumerable<object[]> HybridControllerCases()
	{
		foreach (WorkspaceView view in Enum.GetValues(typeof(WorkspaceView)))
		{
			for (var optionBits = 0; optionBits < 4; optionBits++)
				yield return [view, optionBits];
		}
	}

	[Theory]
	[MemberData(nameof(HybridControllerCases))]
	public void HybridController_GoldenMatrix_PreservesDiscoveryOwnershipRuntimeAndTree(
		WorkspaceView view,
		int optionBits)
	{
		using var temp = new TemporaryDirectory();
		SeedSyntheticWorkspace(temp);
		var contract = ResolveViewContract(temp.Path, view);
		var smartIgnore = CreateSyntheticSmartIgnoreService();
		var discovery = new ProjectScopeDiscoveryService(smartIgnore);
		var rulesService = new IgnoreRulesService(smartIgnore, discovery);
		var selectedOptions = ResolveSelectedOptions(optionBits);
		var gitSelected = selectedOptions.Contains(IgnoreOptionId.UseGitIgnore);
		var smartSelected = selectedOptions.Contains(IgnoreOptionId.SmartIgnore);

		var context = discovery.Discover(contract.OpenRootPath, contract.SelectedRootFolders);
		var availability = rulesService.GetIgnoreOptionsAvailability(
			contract.OpenRootPath,
			contract.SelectedRootFolders);
		var rules = rulesService.Build(
			contract.OpenRootPath,
			selectedOptions,
			contract.SelectedRootFolders);

		var expectedUseGitIgnore = contract.HasGitController && gitSelected;
		var expectedUseSmartIgnore = contract.SmartFollowsGitIgnore
			? gitSelected
			: contract.RuntimeSmartAvailable && smartSelected;

		Assert.Equal(contract.HasGitController, availability.IncludeGitIgnore);
		Assert.Equal(contract.HasSmartController, availability.IncludeSmartIgnore);
		Assert.Equal(contract.SmartFollowsGitIgnore, availability.SmartIgnoreFollowsGitIgnore);
		Assert.Equal(expectedUseGitIgnore, rules.UseGitIgnore);
		Assert.Equal(expectedUseSmartIgnore, rules.UseSmartIgnore);
		Assert.Equal(contract.SmartFollowsGitIgnore, rules.SmartIgnoreFollowsGitIgnore);
		Assert.Equal(expectedUseGitIgnore, rules.GitIgnoreCandidateMatchesActiveRules);
		Assert.Equal(expectedUseSmartIgnore, rules.SmartIgnoreCandidateMatchesActiveRules);

		AssertExactPaths(contract.ExpectedDiscoveryScopes, context.Scopes.Select(static scope => scope.RootPath));
		AssertExactPaths(contract.ExpectedSmartScopes, rules.SmartIgnoreCandidateScopeRoots);
		AssertExactPaths(
			expectedUseSmartIgnore ? contract.ExpectedSmartScopes : [],
			rules.SmartIgnoreScopeRoots);

		AssertRuleOwnership(temp.Path, contract, rules, expectedUseGitIgnore, expectedUseSmartIgnore);

		var tree = new TreeBuilder().Build(
			contract.OpenRootPath,
			new TreeFilterOptions(
				AllowedExtensions,
				new HashSet<string>(contract.SelectedRootFolders, StringComparer.OrdinalIgnoreCase),
				rules),
			TestContext.Current.CancellationToken);

		AssertGoldenTree(contract, tree, expectedUseGitIgnore, expectedUseSmartIgnore);
		AssertScannerControllerEvidence(contract, rules);
	}

	[Fact]
	public void ProductionRules_PolyglotScopes_DoNotLeakMergedStackNamesIntoSiblings()
	{
		using var temp = new TemporaryDirectory();
		SeedProductionPolyglotWorkspace(temp);
		var selectedRoots = new[] { "dotnet", "frontend", "python", "plain" };
		var service = CreateProductionRulesService();

		var availability = service.GetIgnoreOptionsAvailability(temp.Path, selectedRoots);
		var rules = service.Build(temp.Path, [IgnoreOptionId.SmartIgnore], selectedRoots);
		var tree = BuildTree(temp.Path, selectedRoots, rules);

		Assert.True(availability.IncludeSmartIgnore);
		Assert.True(rules.UseSmartIgnore);
		AssertExactPaths(
			[
				Path.Combine(temp.Path, "dotnet"),
				Path.Combine(temp.Path, "frontend"),
				Path.Combine(temp.Path, "python")
			],
			rules.SmartIgnoreScopeRoots);

		Assert.False(ContainsPath(tree, "dotnet/bin/own.dll"));
		Assert.True(ContainsPath(tree, "dotnet/node_modules/keep.txt"));
		Assert.True(ContainsPath(tree, "dotnet/__pycache__/keep.txt"));

		Assert.False(ContainsPath(tree, "frontend/node_modules/own.js"));
		Assert.True(ContainsPath(tree, "frontend/bin/keep.txt"));
		Assert.True(ContainsPath(tree, "frontend/__pycache__/keep.txt"));

		Assert.False(ContainsPath(tree, "python/__pycache__/own.pyc"));
		Assert.True(ContainsPath(tree, "python/bin/keep.txt"));
		Assert.True(ContainsPath(tree, "python/node_modules/keep.txt"));

		Assert.True(ContainsPath(tree, "plain/bin/keep.txt"));
		Assert.True(ContainsPath(tree, "plain/node_modules/keep.txt"));
		Assert.True(ContainsPath(tree, "plain/__pycache__/keep.txt"));
	}

	[Fact]
	public void ProductionRules_PortablePackageStoreCrossesScopeButLookalikesAndStackArtifactsDoNot()
	{
		using var temp = new TemporaryDirectory();
		SeedPortableStoreWorkspace(temp);
		var selectedRoots = new[] { "dotnet", "archive" };
		var service = new IgnoreRulesService(new SmartIgnoreService([new DotNetArtifactsIgnoreRule()]));

		var rules = service.Build(temp.Path, [IgnoreOptionId.SmartIgnore], selectedRoots);
		var tree = BuildTree(temp.Path, selectedRoots, rules);

		Assert.True(rules.UseSmartIgnore);
		AssertExactPaths([Path.Combine(temp.Path, "dotnet")], rules.SmartIgnoreScopeRoots);
		Assert.False(ContainsPath(tree, "dotnet/obj/project.assets.json"));
		Assert.False(ContainsPath(tree, "archive/packages"));
		Assert.True(ContainsPath(tree, "archive/source/packages/readme.txt"));
		Assert.True(ContainsPath(tree, "archive/obj/project.assets.json"));
	}

	[Fact]
	public void HybridController_ReusedServiceSelectionJourney_DoesNotLeakCachedScopesOrControllerModes()
	{
		using var temp = new TemporaryDirectory();
		SeedSyntheticWorkspace(temp);
		var smartIgnore = CreateSyntheticSmartIgnoreService();
		var discovery = new ProjectScopeDiscoveryService(smartIgnore);
		var rulesService = new IgnoreRulesService(smartIgnore, discovery);
		var steps = new[]
		{
			(WorkspaceView.ParentMixed, 2),
			(WorkspaceView.ParentGitOnly, 0),
			(WorkspaceView.DirectSmartProject, 2),
			(WorkspaceView.ParentPlainOnly, 2),
			(WorkspaceView.ParentMixed, 1),
			(WorkspaceView.ParentMixed, 3),
			(WorkspaceView.DirectGitProject, 1),
			(WorkspaceView.ParentMixed, 0)
		};

		foreach (var (view, optionBits) in steps)
		{
			var contract = ResolveViewContract(temp.Path, view);
			var selectedOptions = ResolveSelectedOptions(optionBits);
			var gitSelected = selectedOptions.Contains(IgnoreOptionId.UseGitIgnore);
			var smartSelected = selectedOptions.Contains(IgnoreOptionId.SmartIgnore);
			var expectedUseSmartIgnore = contract.SmartFollowsGitIgnore
				? gitSelected
				: contract.RuntimeSmartAvailable && smartSelected;
			var context = discovery.Discover(contract.OpenRootPath, contract.SelectedRootFolders);
			var rules = rulesService.Build(
				contract.OpenRootPath,
				selectedOptions,
				contract.SelectedRootFolders);

			AssertExactPaths(contract.ExpectedDiscoveryScopes, context.Scopes.Select(static scope => scope.RootPath));
			AssertExactPaths(contract.ExpectedSmartScopes, rules.SmartIgnoreCandidateScopeRoots);
			Assert.Equal(contract.HasGitController && gitSelected, rules.UseGitIgnore);
			Assert.Equal(expectedUseSmartIgnore, rules.UseSmartIgnore);
			Assert.Equal(contract.SmartFollowsGitIgnore, rules.SmartIgnoreFollowsGitIgnore);
			AssertRuleOwnership(
				temp.Path,
				contract,
				rules,
				contract.HasGitController && gitSelected,
				expectedUseSmartIgnore);
		}
	}

	[Fact]
	public void HybridController_ProjectMarkerMutation_RefreshesAvailabilityScopesAndTreeInBothDirections()
	{
		using var temp = new TemporaryDirectory();
		SeedSyntheticWorkspace(temp);
		var selectedRoots = new[] { "plain-data" };
		var smartIgnore = CreateSyntheticSmartIgnoreService();
		var rulesService = new IgnoreRulesService(smartIgnore);

		var initialAvailability = rulesService.GetIgnoreOptionsAvailability(temp.Path, selectedRoots);
		var initialRules = rulesService.Build(temp.Path, [IgnoreOptionId.SmartIgnore], selectedRoots);
		var initialTree = BuildTree(temp.Path, selectedRoots, initialRules);

		Assert.False(initialAvailability.IncludeSmartIgnore);
		Assert.Empty(initialRules.SmartIgnoreCandidateScopeRoots);
		Assert.True(ContainsPath(initialTree, "plain-data/beta-cache/keep.txt"));

		var markerPath = temp.CreateFile("plain-data/beta.project", "beta");
		rulesService.InvalidateCaches(temp.Path);
		var markedAvailability = rulesService.GetIgnoreOptionsAvailability(temp.Path, selectedRoots);
		var markedRules = rulesService.Build(temp.Path, [IgnoreOptionId.SmartIgnore], selectedRoots);
		var markedTree = BuildTree(temp.Path, selectedRoots, markedRules);

		Assert.True(markedAvailability.IncludeSmartIgnore);
		AssertExactPaths([Path.Combine(temp.Path, "plain-data")], markedRules.SmartIgnoreScopeRoots);
		Assert.False(ContainsPath(markedTree, "plain-data/beta-cache"));
		Assert.True(ContainsPath(markedTree, "plain-data/alpha-cache/keep.txt"));

		File.Delete(markerPath);
		rulesService.InvalidateCaches(temp.Path);
		var restoredAvailability = rulesService.GetIgnoreOptionsAvailability(temp.Path, selectedRoots);
		var restoredRules = rulesService.Build(temp.Path, [IgnoreOptionId.SmartIgnore], selectedRoots);
		var restoredTree = BuildTree(temp.Path, selectedRoots, restoredRules);

		Assert.False(restoredAvailability.IncludeSmartIgnore);
		Assert.Empty(restoredRules.SmartIgnoreCandidateScopeRoots);
		Assert.True(ContainsPath(restoredTree, "plain-data/beta-cache/keep.txt"));
	}

	[Fact]
	public void HybridController_GitIgnoreMutation_SwitchesVisibleControllerWithoutChangingSmartOwnership()
	{
		using var temp = new TemporaryDirectory();
		SeedSyntheticWorkspace(temp);
		temp.CreateFile("smart-project/git-only/drop.txt", "git artifact");
		var projectRoot = Path.Combine(temp.Path, "smart-project");
		var selectedRoots = new[] { "alpha-cache", "beta-cache", "git-only", "src" };
		var smartIgnore = CreateSyntheticSmartIgnoreService();
		var rulesService = new IgnoreRulesService(smartIgnore);

		var smartAvailability = rulesService.GetIgnoreOptionsAvailability(projectRoot, selectedRoots);
		var smartRules = rulesService.Build(projectRoot, [IgnoreOptionId.SmartIgnore], selectedRoots);
		var smartTree = BuildTree(projectRoot, selectedRoots, smartRules);

		Assert.False(smartAvailability.IncludeGitIgnore);
		Assert.True(smartAvailability.IncludeSmartIgnore);
		Assert.False(smartAvailability.SmartIgnoreFollowsGitIgnore);
		Assert.False(ContainsPath(smartTree, "beta-cache/artifact.bin"));
		Assert.True(ContainsPath(smartTree, "git-only/drop.txt"));

		var gitIgnorePath = temp.CreateFile("smart-project/.gitignore", "git-only/\n");
		rulesService.InvalidateCaches(projectRoot);
		var gitAvailability = rulesService.GetIgnoreOptionsAvailability(projectRoot, selectedRoots);
		var gitRules = rulesService.Build(projectRoot, [IgnoreOptionId.UseGitIgnore], selectedRoots);
		var gitTree = BuildTree(projectRoot, selectedRoots, gitRules);

		Assert.True(gitAvailability.IncludeGitIgnore);
		Assert.False(gitAvailability.IncludeSmartIgnore);
		Assert.True(gitAvailability.SmartIgnoreFollowsGitIgnore);
		Assert.True(gitRules.UseGitIgnore);
		Assert.True(gitRules.UseSmartIgnore);
		Assert.True(gitRules.SmartIgnoreFollowsGitIgnore);
		Assert.False(ContainsPath(gitTree, "beta-cache/artifact.bin"));
		Assert.False(ContainsPath(gitTree, "git-only/drop.txt"));

		File.Delete(gitIgnorePath);
		rulesService.InvalidateCaches(projectRoot);
		var restoredAvailability = rulesService.GetIgnoreOptionsAvailability(projectRoot, selectedRoots);
		var restoredRules = rulesService.Build(projectRoot, [IgnoreOptionId.SmartIgnore], selectedRoots);
		var restoredTree = BuildTree(projectRoot, selectedRoots, restoredRules);

		Assert.False(restoredAvailability.IncludeGitIgnore);
		Assert.True(restoredAvailability.IncludeSmartIgnore);
		Assert.False(restoredAvailability.SmartIgnoreFollowsGitIgnore);
		Assert.False(ContainsPath(restoredTree, "beta-cache/artifact.bin"));
		Assert.True(ContainsPath(restoredTree, "git-only/drop.txt"));
	}

	private static void AssertRuleOwnership(
		string workspaceRoot,
		ViewContract contract,
		IgnoreRules rules,
		bool expectedUseGitIgnore,
		bool expectedUseSmartIgnore)
	{
		if (contract.Includes("git-project"))
		{
			var gitOnly = Path.Combine(workspaceRoot, "git-project", "git-only");
			var alphaCache = Path.Combine(workspaceRoot, "git-project", "alpha-cache");
			var betaCache = Path.Combine(workspaceRoot, "git-project", "beta-cache");

			Assert.Equal(expectedUseGitIgnore, rules.IsGitIgnored(gitOnly, isDirectory: true, "git-only"));
			Assert.True(rules.EvaluateGitIgnoreCandidate(gitOnly, isDirectory: true, "git-only").IsIgnored);
			Assert.Equal(expectedUseSmartIgnore, rules.IsSmartIgnoredDirectory(alphaCache, "alpha-cache"));
			Assert.True(rules.IsSmartIgnoredDirectoryCandidate(alphaCache, "alpha-cache"));
			Assert.False(rules.IsSmartIgnoredDirectory(betaCache, "beta-cache"));
			Assert.False(rules.IsSmartIgnoredDirectoryCandidate(betaCache, "beta-cache"));
		}

		if (contract.Includes("smart-project"))
		{
			var betaCache = Path.Combine(workspaceRoot, "smart-project", "beta-cache");
			var alphaCache = Path.Combine(workspaceRoot, "smart-project", "alpha-cache");

			Assert.Equal(expectedUseSmartIgnore, rules.IsSmartIgnoredDirectory(betaCache, "beta-cache"));
			Assert.True(rules.IsSmartIgnoredDirectoryCandidate(betaCache, "beta-cache"));
			Assert.False(rules.IsSmartIgnoredDirectory(alphaCache, "alpha-cache"));
			Assert.False(rules.IsSmartIgnoredDirectoryCandidate(alphaCache, "alpha-cache"));
		}

		if (contract.Includes("plain-data"))
		{
			var alphaCache = Path.Combine(workspaceRoot, "plain-data", "alpha-cache");
			var betaCache = Path.Combine(workspaceRoot, "plain-data", "beta-cache");

			Assert.False(rules.IsSmartIgnoredDirectory(alphaCache, "alpha-cache"));
			Assert.False(rules.IsSmartIgnoredDirectoryCandidate(alphaCache, "alpha-cache"));
			Assert.False(rules.IsSmartIgnoredDirectory(betaCache, "beta-cache"));
			Assert.False(rules.IsSmartIgnoredDirectoryCandidate(betaCache, "beta-cache"));
		}
	}

	private static void AssertGoldenTree(
		ViewContract contract,
		TreeBuildResult tree,
		bool useGitIgnore,
		bool useSmartIgnore)
	{
		if (contract.Includes("git-project"))
		{
			Assert.True(ContainsPath(tree, contract.TreePath("git-project", "src/keep.txt")));
			Assert.Equal(!useGitIgnore, ContainsPath(tree, contract.TreePath("git-project", "git-only/drop.txt")));
			Assert.Equal(!useSmartIgnore, ContainsPath(tree, contract.TreePath("git-project", "alpha-cache/artifact.bin")));
			Assert.True(ContainsPath(tree, contract.TreePath("git-project", "beta-cache/must-stay.txt")));
		}

		if (contract.Includes("smart-project"))
		{
			Assert.True(ContainsPath(tree, contract.TreePath("smart-project", "src/keep.txt")));
			Assert.Equal(!useSmartIgnore, ContainsPath(tree, contract.TreePath("smart-project", "beta-cache/artifact.bin")));
			Assert.True(ContainsPath(tree, contract.TreePath("smart-project", "alpha-cache/must-stay.txt")));
		}

		if (contract.Includes("plain-data"))
		{
			Assert.True(ContainsPath(tree, contract.TreePath("plain-data", "alpha-cache/keep.txt")));
			Assert.True(ContainsPath(tree, contract.TreePath("plain-data", "beta-cache/keep.txt")));
		}
	}

	private static void AssertScannerControllerEvidence(ViewContract contract, IgnoreRules rules)
	{
		var scan = new ScanOptionsUseCase(new FileSystemScanner())
			.GetProjectWorkspaceSnapshotForRootFolders(
				contract.OpenRootPath,
				contract.SelectedRootFolders,
				rules,
				rules,
				new ExtensionSetInclusionPolicy(AllowedExtensions),
				includeDirectoryToggleProbeRoots: true,
				cancellationToken: TestContext.Current.CancellationToken,
				includeControllerImpactProbeRoots: true);

		Assert.NotNull(scan.Value.TreeInventory);
		Assert.Equal(
			contract.HasGitController,
			scan.Value.IgnoreSection.ControllerImpactCounts.GitIgnore > 0);
		Assert.Equal(
			contract.HasSmartImpact,
			scan.Value.IgnoreSection.ControllerImpactCounts.SmartIgnore > 0);
	}

	private static ViewContract ResolveViewContract(string rootPath, WorkspaceView view)
	{
		var gitProject = Path.Combine(rootPath, "git-project");
		var smartProject = Path.Combine(rootPath, "smart-project");
		var plainData = Path.Combine(rootPath, "plain-data");

		return view switch
		{
			WorkspaceView.DirectGitProject => new ViewContract(
				gitProject,
				["alpha-cache", "beta-cache", "git-only", "src"],
				["git-project"],
				[gitProject],
				[gitProject],
				HasGitController: true,
				HasSmartController: false,
				RuntimeSmartAvailable: false,
				SmartFollowsGitIgnore: true,
				HasSmartImpact: true,
				DirectProjectName: "git-project"),
			WorkspaceView.DirectSmartProject => new ViewContract(
				smartProject,
				["alpha-cache", "beta-cache", "src"],
				["smart-project"],
				[smartProject],
				[smartProject],
				HasGitController: false,
				HasSmartController: true,
				RuntimeSmartAvailable: true,
				SmartFollowsGitIgnore: false,
				HasSmartImpact: true,
				DirectProjectName: "smart-project"),
			WorkspaceView.ParentGitOnly => new ViewContract(
				rootPath,
				["git-project"],
				["git-project"],
				[gitProject],
				[gitProject],
				HasGitController: true,
				HasSmartController: false,
				RuntimeSmartAvailable: false,
				SmartFollowsGitIgnore: true,
				HasSmartImpact: true),
			WorkspaceView.ParentSmartOnly => new ViewContract(
				rootPath,
				["smart-project"],
				["smart-project"],
				[smartProject],
				[smartProject],
				HasGitController: false,
				HasSmartController: true,
				RuntimeSmartAvailable: true,
				SmartFollowsGitIgnore: false,
				HasSmartImpact: true),
			WorkspaceView.ParentMixed => new ViewContract(
				rootPath,
				["git-project", "smart-project", "plain-data"],
				["git-project", "smart-project", "plain-data"],
				[gitProject, smartProject, plainData],
				[gitProject, smartProject],
				HasGitController: true,
				HasSmartController: true,
				RuntimeSmartAvailable: true,
				SmartFollowsGitIgnore: false,
				HasSmartImpact: true),
			WorkspaceView.ParentPlainOnly => new ViewContract(
				rootPath,
				["plain-data"],
				["plain-data"],
				[plainData],
				[],
				HasGitController: false,
				HasSmartController: false,
				RuntimeSmartAvailable: true,
				SmartFollowsGitIgnore: false,
				HasSmartImpact: false),
			_ => throw new ArgumentOutOfRangeException(nameof(view), view, null)
		};
	}

	private static IReadOnlyCollection<IgnoreOptionId> ResolveSelectedOptions(int optionBits)
	{
		var selected = new List<IgnoreOptionId>(2);
		if ((optionBits & 1) != 0)
			selected.Add(IgnoreOptionId.UseGitIgnore);
		if ((optionBits & 2) != 0)
			selected.Add(IgnoreOptionId.SmartIgnore);
		return selected;
	}

	private static SmartIgnoreService CreateSyntheticSmartIgnoreService() => new([
		new MarkerScopedRule("alpha.project", "alpha-cache"),
		new MarkerScopedRule("beta.project", "beta-cache")
	]);

	private static IgnoreRulesService CreateProductionRulesService() => new(new SmartIgnoreService([
		new DotNetArtifactsIgnoreRule(),
		new FrontendArtifactsIgnoreRule(),
		new PythonArtifactsIgnoreRule()
	]));

	private static TreeBuildResult BuildTree(
		string rootPath,
		IReadOnlyCollection<string> selectedRoots,
		IgnoreRules rules) =>
		new TreeBuilder().Build(
			rootPath,
			new TreeFilterOptions(
				AllowedExtensions,
				new HashSet<string>(selectedRoots, StringComparer.OrdinalIgnoreCase),
				rules),
			TestContext.Current.CancellationToken);

	private static void SeedSyntheticWorkspace(TemporaryDirectory temp)
	{
		temp.CreateFile("git-project/.gitignore", "git-only/\n");
		temp.CreateFile("git-project/alpha.project", "alpha");
		temp.CreateFile("git-project/src/keep.txt", "source");
		temp.CreateFile("git-project/git-only/drop.txt", "ignored by git");
		temp.CreateFile("git-project/alpha-cache/artifact.bin", "owned alpha artifact");
		temp.CreateFile("git-project/beta-cache/must-stay.txt", "foreign beta name");

		temp.CreateFile("smart-project/beta.project", "beta");
		temp.CreateFile("smart-project/src/keep.txt", "source");
		temp.CreateFile("smart-project/beta-cache/artifact.bin", "owned beta artifact");
		temp.CreateFile("smart-project/alpha-cache/must-stay.txt", "foreign alpha name");

		temp.CreateFile("plain-data/alpha-cache/keep.txt", "ordinary folder");
		temp.CreateFile("plain-data/beta-cache/keep.txt", "ordinary folder");
	}

	private static void SeedProductionPolyglotWorkspace(TemporaryDirectory temp)
	{
		temp.CreateFile("dotnet/App.csproj", "<Project />");
		temp.CreateFile("dotnet/bin/own.dll", "artifact");
		temp.CreateFile("dotnet/node_modules/keep.txt", "foreign folder name");
		temp.CreateFile("dotnet/__pycache__/keep.txt", "foreign folder name");

		temp.CreateFile("frontend/package.json", "{}");
		temp.CreateFile("frontend/node_modules/own.js", "artifact");
		temp.CreateFile("frontend/bin/keep.txt", "foreign folder name");
		temp.CreateFile("frontend/__pycache__/keep.txt", "foreign folder name");

		temp.CreateFile("python/pyproject.toml", "[project]");
		temp.CreateFile("python/__pycache__/own.pyc", "artifact");
		temp.CreateFile("python/bin/keep.txt", "foreign folder name");
		temp.CreateFile("python/node_modules/keep.txt", "foreign folder name");

		temp.CreateFile("plain/bin/keep.txt", "ordinary folder");
		temp.CreateFile("plain/node_modules/keep.txt", "ordinary folder");
		temp.CreateFile("plain/__pycache__/keep.txt", "ordinary folder");
	}

	private static void SeedPortableStoreWorkspace(TemporaryDirectory temp)
	{
		temp.CreateFile("dotnet/App.csproj", "<Project />");
		temp.CreateFile("dotnet/obj/project.assets.json", "{}");

		temp.CreateFile("archive/packages/repositories.config", "<repositories />");
		temp.CreateFile("archive/packages/Alpha/Alpha.nupkg", "package");
		Directory.CreateDirectory(Path.Combine(temp.Path, "archive/packages/Alpha/lib"));
		temp.CreateFile("archive/packages/Beta/Beta.nupkg", "package");
		Directory.CreateDirectory(Path.Combine(temp.Path, "archive/packages/Beta/ref"));
		temp.CreateFile("archive/source/packages/readme.txt", "ordinary source packages");
		temp.CreateFile("archive/obj/project.assets.json", "scope-bound lookalike");
	}

	private static bool ContainsPath(TreeBuildResult tree, string relativePath)
	{
		var segments = relativePath.Split(
			['/', '\\'],
			StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		IReadOnlyList<FileSystemNode> children = tree.Root.Children;

		foreach (var segment in segments)
		{
			var node = children.FirstOrDefault(child =>
				string.Equals(child.Name, segment, StringComparison.OrdinalIgnoreCase));
			if (node is null)
				return false;

			children = node.Children;
		}

		return segments.Length > 0;
	}

	private static void AssertExactPaths(IEnumerable<string> expected, IEnumerable<string> actual)
	{
		var expectedPaths = expected.OrderBy(static path => path, PathComparer.Default).ToArray();
		var actualPaths = actual.OrderBy(static path => path, PathComparer.Default).ToArray();
		Assert.Equal(expectedPaths, actualPaths, PathComparer.Default);
	}

	private sealed record ViewContract(
		string OpenRootPath,
		string[] SelectedRootFolders,
		string[] IncludedProjects,
		string[] ExpectedDiscoveryScopes,
		string[] ExpectedSmartScopes,
		bool HasGitController,
		bool HasSmartController,
		bool RuntimeSmartAvailable,
		bool SmartFollowsGitIgnore,
		bool HasSmartImpact,
		string? DirectProjectName = null)
	{
		public bool Includes(string projectName) =>
			IncludedProjects.Contains(projectName, StringComparer.OrdinalIgnoreCase);

		public string TreePath(string projectName, string relativePath) =>
			string.Equals(DirectProjectName, projectName, StringComparison.OrdinalIgnoreCase)
				? relativePath
				: $"{projectName}/{relativePath}";
	}

	private sealed class MarkerScopedRule(
		string markerFile,
		string artifactFolder) :
		ISmartIgnoreRule,
		IProjectRootFactsSmartIgnoreRule,
		ISmartIgnoreRuleDescriptorProvider
	{
		private readonly IReadOnlySet<string> _markerFiles =
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { markerFile };
		private readonly IReadOnlySet<string> _artifactFolders =
			new HashSet<string>(StringComparer.OrdinalIgnoreCase) { artifactFolder };

		public SmartIgnoreRuleDescriptor Descriptor => new(
			_markerFiles,
			new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			_artifactFolders,
			new HashSet<string>(StringComparer.OrdinalIgnoreCase));

		public SmartIgnoreResult Evaluate(ProjectRootFacts rootFacts) =>
			rootFacts.Exists && rootFacts.HasMarkerFile(markerFile)
				? new SmartIgnoreResult(
					_artifactFolders,
					new HashSet<string>(StringComparer.OrdinalIgnoreCase))
				: SmartIgnoreResult.Empty;

		public SmartIgnoreResult Evaluate(string rootPath) =>
			File.Exists(Path.Combine(rootPath, markerFile))
				? new SmartIgnoreResult(
					_artifactFolders,
					new HashSet<string>(StringComparer.OrdinalIgnoreCase))
				: SmartIgnoreResult.Empty;
	}
}
