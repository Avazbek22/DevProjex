using static DevProjex.Tests.Shared.ProjectLoadWorkflow.ProjectLoadWorkflowRefreshHarness;

namespace DevProjex.Tests.Integration;

public sealed class SmartArtifactIgnoreNegativeMatrixIntegrationTests
{
	[Theory]
	[MemberData(nameof(SourceLookalikeCases))]
	public void TreeBuilder_SmartIgnorePreservesSourceLookalikeWithMisleadingEvidence(
		string sourcePath,
		string content)
	{
		using var project = new TemporaryDirectory();
		project.CreateFile("src/App.cs", "class App {}\n");
		project.CreateFile(sourcePath, content);
		var rules = CreateRules(project.Path, [IgnoreOptionId.SmartIgnore]);

		var tree = BuildTree(project.Path, rules);

		AssertPathVisible(tree.Root, "src/App.cs");
		AssertPathVisible(tree.Root, sourcePath);
	}

	[Fact]
	public void SelectionRefresh_WeakCandidatesDoNotExposeSmartControllerOrPolluteOtherSections()
	{
		using var project = new TemporaryDirectory();
		SeedNegativeWorkspace(project, includeRealArtifact: false);
		var services = CreateServices();

		var snapshot = services.Engine.ComputeFullRefreshSnapshot(
			CreateDefaultContext(project.Path),
			TestContext.Current.CancellationToken);

		Assert.DoesNotContain(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.SmartIgnore);
		Assert.Equal(0, snapshot.ControllerImpactCounts.SmartIgnore);
		Assert.Contains(snapshot.EffectiveExtensionOptions, option => option.Name == ".cs");
		Assert.Contains(snapshot.EffectiveExtensionOptions, option => option.Name == ".php");
		Assert.Contains(snapshot.EffectiveExtensionOptions, option => option.Name == ".json");
		Assert.Contains(snapshot.RootOptions!, option => option.Name == "build" && option.IsChecked);
		Assert.Contains(snapshot.RootOptions!, option => option.Name == "packages" && option.IsChecked);
	}

	[Fact]
	public void SelectionRefresh_RealArtifactDoesNotStealWeakCandidateRootsOrExtensionsAcrossToggleCycle()
	{
		using var project = new TemporaryDirectory();
		SeedNegativeWorkspace(project, includeRealArtifact: true);
		var services = CreateServices();

		var enabled = services.Engine.ComputeFullRefreshSnapshot(
			CreateDefaultContext(project.Path),
			TestContext.Current.CancellationToken);
		var disabled = services.Engine.ComputeFullRefreshSnapshot(
			CreateSmartStateContext(project.Path, enabled, isChecked: false),
			TestContext.Current.CancellationToken);
		var reEnabled = services.Engine.ComputeFullRefreshSnapshot(
			CreateSmartStateContext(project.Path, disabled, isChecked: true),
			TestContext.Current.CancellationToken);

		AssertSmartState(enabled, isChecked: true, artifactExtensionVisible: false);
		AssertSmartState(disabled, isChecked: false, artifactExtensionVisible: true);
		AssertSmartState(reEnabled, isChecked: true, artifactExtensionVisible: false);
		AssertSourceContract(enabled);
		AssertSourceContract(disabled);
		AssertSourceContract(reEnabled);
		AssertEquivalentVisibleSnapshots(enabled, reEnabled);
	}

	[Fact]
	public void SelectionRefresh_SelectingOnlyWeakCandidateRootRetainsReversibleControllerWithoutLeakingTreeContent()
	{
		using var project = new TemporaryDirectory();
		SeedNegativeWorkspace(project, includeRealArtifact: true);
		var services = CreateServices();
		var baseline = services.Engine.ComputeFullRefreshSnapshot(
			CreateDefaultContext(project.Path),
			TestContext.Current.CancellationToken);
		var rootStates = baseline.RootOptions!.ToDictionary(
			option => option.Name,
			option => option.Name == "build",
			PathComparer.Default);
		var buildOnlyContext = CreateContextFromSnapshot(project.Path, baseline) with
		{
			AllRootFoldersChecked = false,
			RootSelectionInitialized = true,
			RootSelectionCache = new HashSet<string>(PathComparer.Default) { "build" },
			RootOptionStateCache = rootStates,
			AllExtensionsChecked = true,
			ExtensionsSelectionInitialized = false,
			ExtensionsSelectionCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
			ExtensionOptionStateCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
		};

		var buildOnly = services.Engine.ComputeFullRefreshSnapshot(
			buildOnlyContext,
			TestContext.Current.CancellationToken);

		var retainedSmart = Assert.Single(
			buildOnly.IgnoreOptions,
			option => option.Id == IgnoreOptionId.SmartIgnore);
		Assert.True(retainedSmart.IsChecked);
		Assert.True(buildOnly.ControllerImpactCounts.SmartIgnore > 0);
		Assert.Contains(buildOnly.EffectiveExtensionOptions, option => option.Name == ".md");
		Assert.Contains(buildOnly.EffectiveExtensionOptions, option => option.Name == ".txt");
		Assert.DoesNotContain(buildOnly.EffectiveExtensionOptions, option => option.Name == ".json");
		AssertTreeContainsOnlySelectedRoot(project.Path, buildOnly, "build");
	}

	[Fact]
	public void IgnoreRules_ActiveAndCandidateBranchesRejectWeakEvidenceWithoutCrossDirectoryLeakage()
	{
		using var project = new TemporaryDirectory();
		SeedNegativeWorkspace(project, includeRealArtifact: true);
		project.CreateFile("App.csproj.user.backup", "shared fixture\n");
		var rules = CreateRules(project.Path, [IgnoreOptionId.SmartIgnore]);

		Assert.False(rules.IsSmartIgnoredDirectory(
			Path.Combine(project.Path, "build"),
			"build"));
		Assert.False(rules.IsSmartIgnoredDirectoryCandidate(
			Path.Combine(project.Path, "build"),
			"build"));
		Assert.False(rules.IsSmartIgnoredDirectory(
			Path.Combine(project.Path, "packages"),
			"packages"));
		Assert.False(rules.IsSmartIgnoredDirectoryCandidate(
			Path.Combine(project.Path, "packages"),
			"packages"));
		Assert.True(rules.IsSmartIgnoredDirectory(
			Path.Combine(project.Path, "obj"),
			"obj"));
		Assert.True(rules.IsSmartIgnoredDirectoryCandidate(
			Path.Combine(project.Path, "obj"),
			"obj"));
		Assert.False(rules.IsSmartIgnoredFile(
			Path.Combine(project.Path, "App.csproj.user.backup"),
			"App.csproj.user.backup",
			shouldApplySmartIgnore: true));
		Assert.True(rules.IsSmartIgnoredFile(
			Path.Combine(project.Path, "App.csproj.user"),
			"App.csproj.user",
			shouldApplySmartIgnore: true));
	}

	[Theory]
	[MemberData(nameof(IndependentIgnoreControllerCases))]
	public void TreeBuilder_IndependentIgnoreControllersNeverPromoteWeakSmartCandidates(
		IndependentIgnoreControllerCase testCase)
	{
		using var project = new TemporaryDirectory();
		SeedNegativeWorkspace(project, includeRealArtifact: false);
		project.CreateFile(".gitignore", "logs/\n");
		project.CreateFile("logs/runtime.log", "git-owned log\n");
		var rules = CreateRules(project.Path, testCase.EnabledOptions);

		var tree = BuildTree(project.Path, rules);

		AssertPathVisible(tree.Root, "src/App.cs");
		AssertPathVisible(tree.Root, "obj-backup/project.assets.json");
		AssertPathVisible(tree.Root, "build/docs/CMakeCache.txt");
		AssertPathVisible(tree.Root, "vendor/src/autoload.php");
		AssertPathVisible(tree.Root, "packages/Alpha/Alpha.nupkg");
		AssertPathVisible(tree.Root, "m2-backup/repository/service/package.json");
		Assert.Equal(
			!testCase.EnabledOptions.Contains(IgnoreOptionId.UseGitIgnore),
			ContainsPath(tree.Root, "logs/runtime.log"));
	}

	public static TheoryData<string, string> SourceLookalikeCases() => new()
	{
		{ "obj-backup/project.assets.json", "{}\n" },
		{ "build/docs/CMakeCache.txt", "source documentation\n" },
		{ "vendor/src/autoload.php", "<?php // source\n" },
		{ "Library/ArtifactDB/Book.cs", "class Book {}\n" },
		{ "packages/Alpha/Alpha.nupkg", "single incomplete package\n" },
		{ "m2-backup/repository/service/package.json", "{}\n" },
		{ "cmake-build/CMakeCache.txt", "source fixture\n" }
	};

	public static TheoryData<IndependentIgnoreControllerCase> IndependentIgnoreControllerCases() => new()
	{
		new IndependentIgnoreControllerCase("all off", []),
		new IndependentIgnoreControllerCase("smart only", [IgnoreOptionId.SmartIgnore]),
		new IndependentIgnoreControllerCase("git controller with implicit smart", [IgnoreOptionId.UseGitIgnore]),
		new IndependentIgnoreControllerCase("dot filters", [IgnoreOptionId.DotFolders, IgnoreOptionId.DotFiles]),
		new IndependentIgnoreControllerCase("hidden filters", [IgnoreOptionId.HiddenFolders, IgnoreOptionId.HiddenFiles]),
		new IndependentIgnoreControllerCase("empty filters", [IgnoreOptionId.EmptyFolders, IgnoreOptionId.EmptyFiles]),
		new IndependentIgnoreControllerCase("extensionless filter", [IgnoreOptionId.ExtensionlessFiles]),
		new IndependentIgnoreControllerCase(
			"all controllers",
			[
				IgnoreOptionId.UseGitIgnore,
				IgnoreOptionId.SmartIgnore,
				IgnoreOptionId.HiddenFolders,
				IgnoreOptionId.HiddenFiles,
				IgnoreOptionId.DotFolders,
				IgnoreOptionId.DotFiles,
				IgnoreOptionId.EmptyFolders,
				IgnoreOptionId.EmptyFiles,
				IgnoreOptionId.ExtensionlessFiles
			])
	};

	private static void SeedNegativeWorkspace(TemporaryDirectory project, bool includeRealArtifact)
	{
		project.CreateFile("App.csproj", "<Project />\n");
		project.CreateFile("src/App.cs", "class App {}\n");
		project.CreateFile("obj-backup/project.assets.json", "{}\n");
		project.CreateFile("build/docs/CMakeCache.txt", "source documentation\n");
		project.CreateFile("build/README.md", "source build folder\n");
		project.CreateFile("vendor/src/autoload.php", "<?php // source\n");
		project.CreateFile("packages/Alpha/Alpha.nupkg", "single incomplete package\n");
		project.CreateDirectory("packages/Alpha/lib");
		project.CreateFile("m2-backup/repository/service/package.json", "{}\n");
		if (includeRealArtifact)
		{
			project.CreateFile("obj/project.assets.json", "{}\n");
			project.CreateFile("App.csproj.user", "local state\n");
		}
	}

	private static IgnoreRules CreateRules(string rootPath, IReadOnlyCollection<IgnoreOptionId> selectedOptions) =>
		ProjectLoadWorkflowRuntime.CreateIgnoreRulesService().Build(
			rootPath,
			selectedOptions,
			selectedRootFolders: []);

	private static TreeBuildResult BuildTree(string rootPath, IgnoreRules rules)
	{
		var extensions = Directory
			.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
			.Select(static path => Path.GetExtension(path) ?? string.Empty)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var roots = Directory
			.EnumerateDirectories(rootPath, "*", SearchOption.TopDirectoryOnly)
			.Select(Path.GetFileName)
			.Where(static name => !string.IsNullOrWhiteSpace(name))
			.Select(static name => name!)
			.ToHashSet(PathComparer.Default);

		return new TreeBuilder().Build(
			rootPath,
			new TreeFilterOptions(
				AllowedExtensions: extensions,
				AllowedRootFolders: roots,
				IgnoreRules: rules),
			TestContext.Current.CancellationToken);
	}

	private static SelectionRefreshContext CreateSmartStateContext(
		string rootPath,
		SelectionRefreshSnapshot snapshot,
		bool isChecked)
	{
		var selected = CollectCheckedIgnoreOptionIds(snapshot);
		var states = new Dictionary<IgnoreOptionId, bool>(snapshot.IgnoreOptionStateCache)
		{
			[IgnoreOptionId.SmartIgnore] = isChecked
		};
		if (isChecked)
			selected.Add(IgnoreOptionId.SmartIgnore);
		else
			selected.Remove(IgnoreOptionId.SmartIgnore);

		return CreateContextFromSnapshot(rootPath, snapshot) with
		{
			IgnoreSelectionInitialized = true,
			IgnoreSelectionCache = selected,
			IgnoreOptionStateCache = states,
			IgnoreOptionStateCacheIsComplete = true,
			IgnoreAllPreference = null
		};
	}

	private static void AssertSmartState(
		SelectionRefreshSnapshot snapshot,
		bool isChecked,
		bool artifactExtensionVisible)
	{
		var smart = Assert.Single(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.SmartIgnore);
		Assert.Equal(isChecked, smart.IsChecked);
		Assert.True(snapshot.ControllerImpactCounts.SmartIgnore > 0);
		Assert.Equal(
			artifactExtensionVisible,
			snapshot.EffectiveExtensionOptions.Any(option => option.Name == ".user"));
		Assert.Contains(snapshot.EffectiveExtensionOptions, option => option.Name == ".nupkg");
	}

	private static void AssertSourceContract(SelectionRefreshSnapshot snapshot)
	{
		Assert.Contains(snapshot.EffectiveExtensionOptions, option => option.Name == ".cs");
		Assert.Contains(snapshot.EffectiveExtensionOptions, option => option.Name == ".php");
		Assert.Contains(snapshot.EffectiveExtensionOptions, option => option.Name == ".json");
		Assert.Contains(snapshot.RootOptions!, option => option.Name == "build" && option.IsChecked);
		Assert.Contains(snapshot.RootOptions!, option => option.Name == "vendor" && option.IsChecked);
		Assert.Contains(snapshot.RootOptions!, option => option.Name == "packages" && option.IsChecked);
	}

	private static void AssertTreeContainsOnlySelectedRoot(
		string rootPath,
		SelectionRefreshSnapshot snapshot,
		string selectedRoot)
	{
		var rules = CreateRules(rootPath, CollectCheckedIgnoreOptionIds(snapshot));
		var tree = new TreeBuilder().Build(
			rootPath,
			new TreeFilterOptions(
				AllowedExtensions: CollectCheckedExtensionNames(snapshot),
				AllowedRootFolders: CollectCheckedRootNames(snapshot),
				IgnoreRules: rules),
			TestContext.Current.CancellationToken);

		var visibleDirectories = tree.Root.Children.Where(static child => child.IsDirectory).ToArray();
		var visibleRootFiles = tree.Root.Children.Where(static child => !child.IsDirectory).ToArray();
		Assert.Single(visibleDirectories);
		Assert.Equal(selectedRoot, visibleDirectories[0].Name);
		Assert.Contains(visibleRootFiles, child => child.Name == "App.csproj");
		Assert.DoesNotContain(visibleRootFiles, child => child.Name == "App.csproj.user");
		AssertPathVisible(tree.Root, "build/README.md");
		AssertPathVisible(tree.Root, "build/docs/CMakeCache.txt");
	}

	private static void AssertPathVisible(FileSystemNode root, string relativePath) =>
		Assert.True(ContainsPath(root, relativePath), $"Expected '{relativePath}' to remain visible.");

	private static bool ContainsPath(FileSystemNode root, string relativePath)
	{
		var current = root;
		foreach (var segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
		{
			var next = current.Children.FirstOrDefault(child =>
				string.Equals(child.Name, segment, StringComparison.Ordinal));
			if (next is null)
				return false;

			current = next;
		}

		return true;
	}

	public sealed record IndependentIgnoreControllerCase(
		string Name,
		IgnoreOptionId[] EnabledOptions)
	{
		public override string ToString() => Name;
	}
}
