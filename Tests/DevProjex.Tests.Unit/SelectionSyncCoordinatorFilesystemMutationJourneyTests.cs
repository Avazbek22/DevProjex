using DevProjex.Application.Presentation;
using DevProjex.Application.Models;
using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Unit;

[Collection("AvaloniaUI")]
public sealed class SelectionSyncCoordinatorFilesystemMutationJourneyTests
{
	[AvaloniaFact]
	public async Task Refresh_WhenEnumerationFails_PreservesPublishedSelectionAndStateCaches()
	{
		using var workspace = new TemporaryDirectory();
		workspace.CreateFile("src/App.cs", "class App {}\n");
		workspace.CreateFile("docs/readme.md", "# Readme\n");
		var failEnumeration = false;
		var notifications = 0;
		var scanner = new FileSystemScanner((point, _) =>
		{
			if (failEnumeration && point == FileSystemScanEnumerationPoint.RootDirectories)
				throw new IOException("Simulated refresh failure.");
		});
		var viewModel = CreateViewModel();
		var ignoreRulesService = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService();
		using var coordinator = CreateCoordinator(
			viewModel,
			ignoreRulesService,
			() => workspace.Path,
			scanner,
			() => notifications++);

		await coordinator.RefreshProjectSelectionAsync(workspace.Path, TestContext.Current.CancellationToken);
		coordinator.HookOptionListeners(viewModel.Extensions);
		coordinator.HookIgnoreListeners(viewModel.IgnoreOptions);
		SetChecked(viewModel.Extensions, ".md", false);
		await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);
		var extensionsBefore = viewModel.Extensions.Select(static item => (item.Name, item.IsChecked)).ToArray();
		var ignoreBefore = viewModel.IgnoreOptions.Select(static item => (item.Id, item.IsChecked)).ToArray();
		var rootsBefore = coordinator.GetProjectScanRoots().ToArray();
		var extensionStatesBefore = coordinator.SnapshotExtensionOptionStatesForPersistence();

		failEnumeration = true;
		coordinator.InvalidateFileSystemCaches();
		await coordinator.RefreshProjectSelectionAsync(workspace.Path, TestContext.Current.CancellationToken);

		Assert.Equal(extensionsBefore, viewModel.Extensions.Select(static item => (item.Name, item.IsChecked)));
		Assert.Equal(ignoreBefore, viewModel.IgnoreOptions.Select(static item => (item.Id, item.IsChecked)));
		Assert.Equal(rootsBefore, coordinator.GetProjectScanRoots(), PathComparer.Default);
		Assert.Equal(extensionStatesBefore, coordinator.SnapshotExtensionOptionStatesForPersistence());
		Assert.Equal(1, notifications);

		var store = new RecordingProfileStore();
		using var secretSession = new SecretRedactionSession(new EmptySecretDetector());
		var persistence = new ProjectProfilePersistenceCoordinator(
			viewModel,
			coordinator,
			store,
			secretSession);
		await persistence.PersistIfNeededAsync(workspace.Path, TestContext.Current.CancellationToken);

		Assert.Equal(1, store.SaveAttempts);
		Assert.NotNull(store.LastProfile);
		Assert.Contains(".cs", store.LastProfile.SelectedExtensions, StringComparer.OrdinalIgnoreCase);
		Assert.True(store.LastProfile.ExtensionStates?[".cs"]);
		Assert.False(store.LastProfile.ExtensionStates?[".md"]);
	}

	[AvaloniaFact]
	public async Task InitialProfileScan_WhenEnumerationFails_RemainsIncompleteAndIsNotPersisted()
	{
		using var workspace = new TemporaryDirectory();
		workspace.CreateFile("app.cs", "class App {}\n");
		var scanner = new FileSystemScanner((point, _) =>
		{
			if (point == FileSystemScanEnumerationPoint.RootFiles)
				throw new IOException("Simulated initial scan failure.");
		});
		var viewModel = CreateViewModel();
		var ignoreRulesService = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService();
		using var coordinator = CreateCoordinator(
			viewModel,
			ignoreRulesService,
			() => workspace.Path,
			scanner);
		coordinator.ApplyProjectProfileSelections(
			workspace.Path,
			new ProjectSelectionProfile([], [".cs"], []));

		await coordinator.RefreshProjectSelectionAsync(workspace.Path, TestContext.Current.CancellationToken);

		Assert.False(coordinator.IsSelectionStateCompleteForPersistence);
		var store = new RecordingProfileStore();
		using var secretSession = new SecretRedactionSession(new EmptySecretDetector());
		var persistence = new ProjectProfilePersistenceCoordinator(
			viewModel,
			coordinator,
			store,
			secretSession);
		await persistence.PersistIfNeededAsync(workspace.Path, TestContext.Current.CancellationToken);
		Assert.Equal(0, store.SaveAttempts);
	}

    [AvaloniaFact]
    public async Task RepeatedF5MutationJourney_LongLivedIslandMatchesColdSnapshotAndFinalTreeAtEveryStep()
    {
        using var workspace = new TemporaryDirectory();
        SeedMutableWorkspace(workspace);
        var currentPath = workspace.Path;
        var viewModel = CreateViewModel();
        var ignoreRulesService = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService();
		using var coordinator = CreateCoordinator(viewModel, ignoreRulesService, () => currentPath);

        await coordinator.RefreshProjectSelectionAsync(
            currentPath,
            TestContext.Current.CancellationToken);
        coordinator.HookOptionListeners(viewModel.Extensions);
        coordinator.HookIgnoreListeners(viewModel.IgnoreOptions);

        SetChecked(viewModel.Extensions, ".md", false);
        SetChecked(viewModel.IgnoreOptions, IgnoreOptionId.EmptyFiles, false);
        await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);
        await AssertIslandMatchesColdSnapshotAsync(
            "manual baseline",
            workspace.Path,
            viewModel,
            coordinator);

        var gitIgnorePath = Path.Combine(workspace.Path, ".gitignore");
        var originalWriteTime = File.GetLastWriteTimeUtc(gitIgnorePath);
        File.WriteAllText(gitIgnorePath, "git-cache-b/\n");
        File.SetLastWriteTimeUtc(gitIgnorePath, originalWriteTime);
        await RefreshLikeF5Async(workspace.Path, coordinator, ignoreRulesService);

        Assert.Contains("git-cache-a", coordinator.GetProjectScanRoots(), PathComparer.Default);
        Assert.DoesNotContain("git-cache-b", coordinator.GetProjectScanRoots(), PathComparer.Default);
        await AssertIslandMatchesColdSnapshotAsync(
            "same-metadata .gitignore rewrite",
            workspace.Path,
            viewModel,
            coordinator);

        var nestedGitIgnorePath = Path.Combine(workspace.Path, "src", ".gitignore");
        var nestedOriginalWriteTime = File.GetLastWriteTimeUtc(nestedGitIgnorePath);
        File.WriteAllText(nestedGitIgnorePath, "generated-b/\n");
        File.SetLastWriteTimeUtc(nestedGitIgnorePath, nestedOriginalWriteTime);
        await RefreshLikeF5Async(workspace.Path, coordinator, ignoreRulesService);

        AssertOption(viewModel.Extensions, ".alpha", expectedChecked: true);
        Assert.DoesNotContain(viewModel.Extensions, option => option.Name == ".bravo");
        await AssertIslandMatchesColdSnapshotAsync(
            "same-metadata nested .gitignore rewrite",
            workspace.Path,
            viewModel,
            coordinator);

        File.Delete(Path.Combine(workspace.Path, "requirements.txt"));
        Directory.Delete(Path.Combine(workspace.Path, "__pycache__"), recursive: true);
        await RefreshLikeF5Async(workspace.Path, coordinator, ignoreRulesService);

        Assert.DoesNotContain(viewModel.IgnoreOptions, option => option.Id == IgnoreOptionId.SmartIgnore);
        await AssertIslandMatchesColdSnapshotAsync(
            "smart controller evidence removed",
            workspace.Path,
            viewModel,
            coordinator);

        workspace.CreateFile("package.json", "{}\n");
        workspace.CreateFile("node_modules/pkg/index.js", "module.exports = {};\n");
        await RefreshLikeF5Async(workspace.Path, coordinator, ignoreRulesService);

        AssertOption(viewModel.IgnoreOptions, IgnoreOptionId.SmartIgnore, expectedChecked: true);
        Assert.DoesNotContain("node_modules", coordinator.GetProjectScanRoots(), PathComparer.Default);
        await AssertIslandMatchesColdSnapshotAsync(
            "smart controller evidence restored for another stack",
            workspace.Path,
            viewModel,
            coordinator);

        Directory.Delete(Path.Combine(workspace.Path, "docs"), recursive: true);
        await RefreshLikeF5Async(workspace.Path, coordinator, ignoreRulesService);
        Assert.DoesNotContain("docs", coordinator.GetProjectScanRoots(), PathComparer.Default);
        Assert.DoesNotContain(viewModel.Extensions, option => option.Name == ".md");

        workspace.CreateFile("docs/readme.md", "# Restored\n");
        await RefreshLikeF5Async(workspace.Path, coordinator, ignoreRulesService);

        Assert.Contains("docs", coordinator.GetProjectScanRoots(), PathComparer.Default);
        AssertOption(viewModel.Extensions, ".md", expectedChecked: false);
        await AssertIslandMatchesColdSnapshotAsync(
            "deleted root and unchecked extension restored",
            workspace.Path,
            viewModel,
            coordinator);

        workspace.CreateFile("generated/report.json", "{}\n");
        await RefreshLikeF5Async(workspace.Path, coordinator, ignoreRulesService);

        Assert.Contains("generated", coordinator.GetProjectScanRoots(), PathComparer.Default);
        AssertOption(viewModel.Extensions, ".json", expectedChecked: true);
        await AssertIslandMatchesColdSnapshotAsync(
            "new root and extension",
            workspace.Path,
            viewModel,
            coordinator);

        File.Delete(gitIgnorePath);
        File.Delete(nestedGitIgnorePath);
        await RefreshLikeF5Async(workspace.Path, coordinator, ignoreRulesService);

        Assert.DoesNotContain(viewModel.IgnoreOptions, option => option.Id == IgnoreOptionId.UseGitIgnore);
        Assert.Contains("git-cache-b", coordinator.GetProjectScanRoots(), PathComparer.Default);
        await AssertIslandMatchesColdSnapshotAsync(
            "git controller removed",
            workspace.Path,
            viewModel,
            coordinator);

        workspace.CreateFile(".gitignore", "git-cache-a/\n");
        await RefreshLikeF5Async(workspace.Path, coordinator, ignoreRulesService);

        AssertOption(viewModel.IgnoreOptions, IgnoreOptionId.UseGitIgnore, expectedChecked: true);
        Assert.DoesNotContain("git-cache-a", coordinator.GetProjectScanRoots(), PathComparer.Default);
        Assert.Contains("git-cache-b", coordinator.GetProjectScanRoots(), PathComparer.Default);
        await AssertIslandMatchesColdSnapshotAsync(
            "git controller restored",
            workspace.Path,
            viewModel,
            coordinator);
    }

    [AvaloniaFact]
    public async Task DeepNestedGitIgnore_ProfileOffToggleRoundTripUpdatesAllIslandSections()
    {
        using var workspace = new TemporaryDirectory();
        var deepScope = Path.Combine(
            ["workspace", .. Enumerable.Range(0, 12).Select(static index => $"level-{index:D2}")]);
        workspace.CreateFile(Path.Combine(deepScope, ".gitignore"), "*.deep\n");
        workspace.CreateFile(Path.Combine(deepScope, "artifact.deep"), "ignored candidate\n");
        workspace.CreateFile(Path.Combine(deepScope, "keep.cs"), "class Keep {}\n");
        var currentPath = workspace.Path;
        var viewModel = CreateViewModel();
        var ignoreRulesService = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService();
		using var coordinator = CreateCoordinator(viewModel, ignoreRulesService, () => currentPath);
        var profile = new ProjectSelectionProfile(
            SelectedRootFolders: [],
            SelectedExtensions: [".cs", ".deep"],
            SelectedIgnoreOptions: [],
            ExtensionStates: new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                [".cs"] = true,
                [".deep"] = true
            },
            IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
            {
                [IgnoreOptionId.UseGitIgnore] = false
            });

        coordinator.ApplyProjectProfileSelections(currentPath, profile);
        await coordinator.RefreshProjectSelectionAsync(
            currentPath,
            TestContext.Current.CancellationToken);
        coordinator.HookOptionListeners(viewModel.Extensions);
        coordinator.HookIgnoreListeners(viewModel.IgnoreOptions);

        AssertOption(viewModel.IgnoreOptions, IgnoreOptionId.UseGitIgnore, expectedChecked: false);
        AssertOption(viewModel.Extensions, ".deep", expectedChecked: true);
        await AssertIslandMatchesColdSnapshotAsync(
            "deep git controller off",
            workspace.Path,
            viewModel,
            coordinator);

        SetChecked(viewModel.IgnoreOptions, IgnoreOptionId.UseGitIgnore, true);
        await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);

        AssertOption(viewModel.IgnoreOptions, IgnoreOptionId.UseGitIgnore, expectedChecked: true);
        Assert.DoesNotContain(viewModel.Extensions, option => option.Name == ".deep");
        await AssertIslandMatchesColdSnapshotAsync(
            "deep git controller on",
            workspace.Path,
            viewModel,
            coordinator);

        SetChecked(viewModel.IgnoreOptions, IgnoreOptionId.UseGitIgnore, false);
        await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);

        AssertOption(viewModel.IgnoreOptions, IgnoreOptionId.UseGitIgnore, expectedChecked: false);
        AssertOption(viewModel.Extensions, ".deep", expectedChecked: true);
        await AssertIslandMatchesColdSnapshotAsync(
            "deep git controller round-trip",
            workspace.Path,
            viewModel,
            coordinator);
    }

    [AvaloniaFact]
    public async Task ControllersHiddenAndReappearing_RetainExplicitUncheckedProfileState()
    {
        using var workspace = new TemporaryDirectory();
        workspace.CreateFile("src/App.cs", "class App {}\n");
        var currentPath = workspace.Path;
        var viewModel = CreateViewModel();
        var ignoreRulesService = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService();
		using var coordinator = CreateCoordinator(viewModel, ignoreRulesService, () => currentPath);
        var profile = new ProjectSelectionProfile(
            SelectedRootFolders: [],
            SelectedExtensions: [],
            SelectedIgnoreOptions: [],
            IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
            {
                [IgnoreOptionId.UseGitIgnore] = false,
                [IgnoreOptionId.SmartIgnore] = false
            });

        coordinator.ApplyProjectProfileSelections(currentPath, profile);
        await coordinator.RefreshProjectSelectionAsync(
            currentPath,
            TestContext.Current.CancellationToken);
        coordinator.HookOptionListeners(viewModel.Extensions);
        coordinator.HookIgnoreListeners(viewModel.IgnoreOptions);

        Assert.DoesNotContain(viewModel.IgnoreOptions, option =>
            option.Id is IgnoreOptionId.UseGitIgnore or IgnoreOptionId.SmartIgnore);

        workspace.CreateFile(".gitignore", "logs/\n");
        workspace.CreateFile("logs/runtime.log", "runtime\n");
        workspace.CreateFile("requirements.txt", "pytest\n");
        workspace.CreateFile("__pycache__/app.pyc", "binary\n");
        await RefreshLikeF5Async(workspace.Path, coordinator, ignoreRulesService);

        AssertOption(viewModel.IgnoreOptions, IgnoreOptionId.UseGitIgnore, expectedChecked: false);
        AssertOption(viewModel.IgnoreOptions, IgnoreOptionId.SmartIgnore, expectedChecked: false);
        Assert.Contains("logs", coordinator.GetProjectScanRoots(), PathComparer.Default);
        Assert.Contains("__pycache__", coordinator.GetProjectScanRoots(), PathComparer.Default);
        await AssertIslandMatchesColdSnapshotAsync(
            "unchecked controllers appear",
            workspace.Path,
            viewModel,
            coordinator);

        File.Delete(Path.Combine(workspace.Path, ".gitignore"));
        File.Delete(Path.Combine(workspace.Path, "requirements.txt"));
        Directory.Delete(Path.Combine(workspace.Path, "__pycache__"), recursive: true);
        await RefreshLikeF5Async(workspace.Path, coordinator, ignoreRulesService);

        Assert.DoesNotContain(viewModel.IgnoreOptions, option =>
            option.Id is IgnoreOptionId.UseGitIgnore or IgnoreOptionId.SmartIgnore);
        await AssertIslandMatchesColdSnapshotAsync(
            "unchecked controllers disappear",
            workspace.Path,
            viewModel,
            coordinator);

        workspace.CreateFile(".gitignore", "logs/\n");
        workspace.CreateFile("requirements.txt", "pytest\n");
        workspace.CreateFile("__pycache__/app.pyc", "binary\n");
        await RefreshLikeF5Async(workspace.Path, coordinator, ignoreRulesService);

        AssertOption(viewModel.IgnoreOptions, IgnoreOptionId.UseGitIgnore, expectedChecked: false);
        AssertOption(viewModel.IgnoreOptions, IgnoreOptionId.SmartIgnore, expectedChecked: false);
        await AssertIslandMatchesColdSnapshotAsync(
            "unchecked controllers reappear",
            workspace.Path,
            viewModel,
            coordinator);
    }

    private static async Task RefreshLikeF5Async(
        string rootPath,
        SelectionSyncCoordinator coordinator,
        IgnoreRulesService ignoreRulesService)
    {
        var canReuseCaches = ignoreRulesService.RevalidateCaches(
            rootPath,
            TestContext.Current.CancellationToken);
        if (!canReuseCaches)
            coordinator.InvalidateFileSystemCaches();

        await coordinator.RefreshProjectSelectionAsync(
            rootPath,
            TestContext.Current.CancellationToken);
    }

    private static async Task AssertIslandMatchesColdSnapshotAsync(
        string step,
        string rootPath,
        MainWindowViewModel viewModel,
        SelectionSyncCoordinator coordinator)
    {
        var scanRoots = coordinator.GetProjectScanRoots().ToHashSet(PathComparer.Default);
        var extensionStates = coordinator.SnapshotExtensionOptionStatesForPersistence() ??
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var ignoreStates = coordinator.SnapshotIgnoreOptionStatesForPersistence() ??
            new Dictionary<IgnoreOptionId, bool>();
        var coldRulesService = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService();
        var coldEngine = CreateEngine(coldRulesService);
        var context = SelectionRefreshContext.ForDesktop(
            path: rootPath,
            preparedSelectionMode: PreparedSelectionMode.None,
            allExtensionsChecked: viewModel.AllExtensionsChecked && !extensionStates.Values.Contains(false),
            extensionsSelectionInitialized: true,
            extensionsSelectionCache: extensionStates
                .Where(static pair => pair.Value)
                .Select(static pair => pair.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            ignoreSelectionInitialized: true,
            ignoreSelectionCache: ignoreStates
                .Where(static pair => pair.Value)
                .Select(static pair => pair.Key)
                .ToHashSet(),
            ignoreOptionStateCache: ignoreStates,
            ignoreAllPreference: null,
            currentSnapshotState: new IgnoreSectionSnapshotState(
                HasIgnoreOptionCounts: false,
                IgnoreOptionCounts: IgnoreOptionCounts.Empty,
                ControllerImpactCounts: IgnoreControllerImpactCounts.Empty,
                HasExtensionlessEntries: false,
                ExtensionlessEntriesCount: 0),
            extensionOptionStateCache: extensionStates,
            ignoreOptionStateCacheIsComplete: true,
            captureTreeInventory: true,
            currentScanRootOptions: scanRoots
                .Select(static root => new SelectionOption(root, true))
                .ToArray(),
            extensionSelectionIsExplicit: false);
        var coldSnapshot = coldEngine.ComputeFullRefreshSnapshot(
            context,
            TestContext.Current.CancellationToken);

        var coldScanRoots = (coldSnapshot.RootOptions ?? [])
            .Where(static option => option.IsChecked)
            .Select(static option => option.Name)
            .ToHashSet(PathComparer.Default);
        Assert.True(
            scanRoots.SetEquals(coldScanRoots),
            $"{step}: scan roots differ from a cold refresh. " +
            $"Expected=[{string.Join(", ", coldScanRoots.Order(PathComparer.Default))}]; " +
            $"Actual=[{string.Join(", ", scanRoots.Order(PathComparer.Default))}].");
        AssertOptionsEqual(
            coldSnapshot.EffectiveExtensionOptions,
            viewModel.Extensions.Select(static option => new SelectionOption(option.Name, option.IsChecked)),
            $"{step}: extension options");
        Assert.True(
            coldSnapshot.IgnoreOptions.SequenceEqual(
				 viewModel.IgnoreOptions.Select(static option =>
					 new ResolvedIgnoreOptionState(
						 option.Id,
						 option.Label,
						 DefaultChecked: !ProjectPresentationCatalog.ContentTransformationOptionIds.Contains(option.Id),
						 option.IsChecked))),
            $"{step}: ignore options differ from a cold refresh.");

        Assert.Equal(
            viewModel.Extensions.Count > 0 && viewModel.Extensions.All(static option => option.IsChecked),
            viewModel.AllExtensionsChecked);
		Assert.Equal(
			AreAllPathIgnoreOptionsChecked(viewModel.IgnoreOptions),
			viewModel.AllIgnoreChecked);

        Assert.All(
            scanRoots,
            root => Assert.True(
                Directory.Exists(Path.Combine(rootPath, root)),
                $"{step}: scan root '{root}' is not a direct existing directory."));
        Assert.Equal(
            viewModel.Extensions.Count,
            viewModel.Extensions.Select(static option => option.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        Assert.Equal(
            viewModel.IgnoreOptions.Count,
            viewModel.IgnoreOptions.Select(static option => option.Id).Distinct().Count());

        var selectedRoots = scanRoots;
        var selectedExtensions = viewModel.Extensions
            .Where(static option => option.IsChecked)
            .Select(static option => option.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedIgnoreOptions = viewModel.IgnoreOptions
            .Where(static option => option.IsChecked)
            .Select(static option => option.Id)
            .ToHashSet();
        var treeRules = coldRulesService.Build(rootPath, selectedIgnoreOptions, selectedRoots);
        var tree = new TreeBuilder().Build(
            rootPath,
            new TreeFilterOptions(selectedExtensions, selectedRoots, treeRules),
            TestContext.Current.CancellationToken);
        var treeRoots = tree.Root.Children
            .Where(static child => child.IsDirectory)
            .Select(static child => child.Name)
            .ToHashSet(PathComparer.Default);

        Assert.True(
            treeRoots.IsSubsetOf(selectedRoots),
            $"{step}: final tree contains a root outside the structural project scope. " +
            $"Scope=[{string.Join(", ", selectedRoots.Order(PathComparer.Default))}]; " +
            $"Tree=[{string.Join(", ", treeRoots.Order(PathComparer.Default))}].");

        await Task.CompletedTask;
	}

	private static bool AreAllPathIgnoreOptionsChecked(IEnumerable<IgnoreOptionViewModel> options)
	{
		var pathOptions = options
			.Where(static option =>
				!ProjectPresentationCatalog.ContentTransformationOptionIds.Contains(option.Id) &&
				!GitFilteringModeResolver.IsGitFilteringOption(option.Id))
			.ToArray();
		if (pathOptions.Length == 0)
			return false;

		return pathOptions.All(static option => option.IsChecked);
	}

    private static void AssertOptionsEqual(
        IEnumerable<SelectionOption> expected,
        IEnumerable<SelectionOption> actual,
        string message)
    {
        var expectedArray = expected.ToArray();
        var actualArray = actual.ToArray();
        Assert.True(
            expectedArray.SequenceEqual(actualArray),
            $"{message}.{Environment.NewLine}" +
            $"Expected=[{string.Join(", ", expectedArray.Select(static option => $"{option.Name}:{option.IsChecked}"))}]" +
            $"{Environment.NewLine}" +
            $"Actual=[{string.Join(", ", actualArray.Select(static option => $"{option.Name}:{option.IsChecked}"))}]");
    }

    private static SelectionRefreshEngine CreateEngine(IgnoreRulesService ignoreRulesService)
    {
        return new SelectionRefreshEngine(
            new ScanOptionsUseCase(new FileSystemScanner()),
            new FilterOptionSelectionService(),
            ProjectLoadWorkflowRuntime.CreateIgnoreOptionsService(),
            (path, selectedIgnoreOptions, selectedRoots) =>
                ignoreRulesService.Build(path, selectedIgnoreOptions, selectedRoots),
            (path, selectedRoots) => ignoreRulesService.GetIgnoreOptionsAvailability(path, selectedRoots) with
            {
                ShowAdvancedCounts = true
            });
    }

    private static SelectionSyncCoordinator CreateCoordinator(
        MainWindowViewModel viewModel,
        IgnoreRulesService ignoreRulesService,
        Func<string?> currentPathProvider,
		FileSystemScanner? scanner = null,
		Action? scanIncomplete = null)
    {
        return new SelectionSyncCoordinator(
            viewModel,
            new ScanOptionsUseCase(scanner ?? new FileSystemScanner()),
            new FilterOptionSelectionService(),
            ProjectLoadWorkflowRuntime.CreateIgnoreOptionsService(),
            (path, selectedIgnoreOptions, selectedRoots) =>
                ignoreRulesService.Build(path, selectedIgnoreOptions, selectedRoots),
            (path, selectedRoots) => ignoreRulesService.GetIgnoreOptionsAvailability(path, selectedRoots) with
            {
                ShowAdvancedCounts = true
            },
            _ => false,
            currentPathProvider,
			scanIncomplete: scanIncomplete);
    }

    private static MainWindowViewModel CreateViewModel()
    {
        var localization = ProjectLoadWorkflowRuntime.CreateLocalizationService();
        return new MainWindowViewModel(localization, new HelpContentProvider());
    }

    private static void SeedMutableWorkspace(TemporaryDirectory workspace)
    {
        workspace.CreateFile(".gitignore", "git-cache-a/\n");
        workspace.CreateFile("requirements.txt", "pytest\n");
        workspace.CreateFile("src/App.cs", "class App {}\n");
        workspace.CreateFile("src/.gitignore", "generated-a/\n");
        workspace.CreateFile("src/generated-a/drop.alpha", "ignored alpha\n");
        workspace.CreateFile("src/generated-b/keep.bravo", "visible bravo\n");
        workspace.CreateFile("docs/readme.md", "# Readme\n");
        workspace.CreateFile("tests/test_app.py", "def test_app(): pass\n");
        workspace.CreateFile("__pycache__/app.pyc", "binary\n");
        workspace.CreateFile("git-cache-a/drop.log", "ignored A\n");
        workspace.CreateFile("git-cache-b/keep.log", "visible B\n");
        workspace.CreateFile("empty.txt", string.Empty);
    }

    private static void SetChecked(
        IEnumerable<SelectionOptionViewModel> options,
        string name,
        bool isChecked)
    {
        var option = Assert.Single(options, candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        option.IsChecked = isChecked;
    }

    private static void SetChecked(
        IEnumerable<IgnoreOptionViewModel> options,
        IgnoreOptionId id,
        bool isChecked)
    {
        var option = Assert.Single(options, candidate => candidate.Id == id);
        option.IsChecked = isChecked;
    }

    private static void AssertOption(
        IEnumerable<SelectionOptionViewModel> options,
        string name,
        bool expectedChecked)
    {
        var option = Assert.Single(options, candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(expectedChecked, option.IsChecked);
    }

    private static void AssertOption(
        IEnumerable<IgnoreOptionViewModel> options,
        IgnoreOptionId id,
        bool expectedChecked)
    {
        var option = Assert.Single(options, candidate => candidate.Id == id);
        Assert.Equal(expectedChecked, option.IsChecked);
    }

	private sealed class RecordingProfileStore : IProjectProfileStore
	{
		public int SaveAttempts { get; private set; }
		public ProjectSelectionProfile? LastProfile { get; private set; }

		public bool EnsureStorageExists() => true;
		public bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile)
		{
			profile = new ProjectSelectionProfile([], [], []);
			return false;
		}

		public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile)
		{
			SaveAttempts++;
			LastProfile = profile;
			return true;
		}

		public bool TrySaveProfile(
			string localProjectPath,
			ProjectSelectionProfile profile,
			DateTimeOffset updatedUtc) =>
			TrySaveProfile(localProjectPath, profile);

		public void SaveProfile(string localProjectPath, ProjectSelectionProfile profile) =>
			_ = TrySaveProfile(localProjectPath, profile);

		public ProjectProfileClearStatus ClearAllProfiles() => ProjectProfileClearStatus.Cleared;
	}

	private sealed class EmptySecretDetector : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) => [];
	}
}
