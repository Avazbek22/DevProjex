using DevProjex.Application.Models;
using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Unit;

[Collection("AvaloniaUI")]
public sealed class SelectionSyncCoordinatorFilesystemMutationJourneyTests
{
    [AvaloniaFact]
    public async Task RepeatedF5MutationJourney_LongLivedIslandMatchesColdSnapshotAndFinalTreeAtEveryStep()
    {
        using var workspace = new TemporaryDirectory();
        SeedMutableWorkspace(workspace);
        var currentPath = workspace.Path;
        var viewModel = CreateViewModel();
        var ignoreRulesService = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService();
        using var coordinator = CreateCoordinator(viewModel, ignoreRulesService, () => currentPath);

        await coordinator.RefreshRootAndDependentsAsync(
            currentPath,
            TestContext.Current.CancellationToken);
        coordinator.HookOptionListeners(viewModel.RootFolders);
        coordinator.HookOptionListeners(viewModel.Extensions);
        coordinator.HookIgnoreListeners(viewModel.IgnoreOptions);

        SetChecked(viewModel.RootFolders, "docs", false);
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

        AssertOption(viewModel.RootFolders, "git-cache-a", expectedChecked: true);
        Assert.DoesNotContain(viewModel.RootFolders, option => option.Name == "git-cache-b");
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
        Assert.DoesNotContain(viewModel.RootFolders, option => option.Name == "node_modules");
        await AssertIslandMatchesColdSnapshotAsync(
            "smart controller evidence restored for another stack",
            workspace.Path,
            viewModel,
            coordinator);

        Directory.Delete(Path.Combine(workspace.Path, "docs"), recursive: true);
        await RefreshLikeF5Async(workspace.Path, coordinator, ignoreRulesService);
        Assert.DoesNotContain(viewModel.RootFolders, option => option.Name == "docs");
        Assert.DoesNotContain(viewModel.Extensions, option => option.Name == ".md");

        workspace.CreateFile("docs/readme.md", "# Restored\n");
        await RefreshLikeF5Async(workspace.Path, coordinator, ignoreRulesService);

        AssertOption(viewModel.RootFolders, "docs", expectedChecked: false);
        Assert.DoesNotContain(viewModel.Extensions, option => option.Name == ".md");
        SetChecked(viewModel.RootFolders, "docs", true);
        await coordinator.WaitForPendingRefreshesAsync(TestContext.Current.CancellationToken);
        AssertOption(viewModel.Extensions, ".md", expectedChecked: false);
        await AssertIslandMatchesColdSnapshotAsync(
            "deleted unchecked root and extension restored",
            workspace.Path,
            viewModel,
            coordinator);

        workspace.CreateFile("generated/report.json", "{}\n");
        await RefreshLikeF5Async(workspace.Path, coordinator, ignoreRulesService);

        AssertOption(viewModel.RootFolders, "generated", expectedChecked: true);
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
        AssertOption(viewModel.RootFolders, "git-cache-b", expectedChecked: true);
        await AssertIslandMatchesColdSnapshotAsync(
            "git controller removed",
            workspace.Path,
            viewModel,
            coordinator);

        workspace.CreateFile(".gitignore", "git-cache-a/\n");
        await RefreshLikeF5Async(workspace.Path, coordinator, ignoreRulesService);

        AssertOption(viewModel.IgnoreOptions, IgnoreOptionId.UseGitIgnore, expectedChecked: true);
        Assert.DoesNotContain(viewModel.RootFolders, option => option.Name == "git-cache-a");
        AssertOption(viewModel.RootFolders, "git-cache-b", expectedChecked: true);
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
            SelectedRootFolders: ["workspace"],
            SelectedExtensions: [".cs", ".deep"],
            SelectedIgnoreOptions: [],
            RootFolderStates: new Dictionary<string, bool>(PathComparer.Default)
            {
                ["workspace"] = true
            },
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
        await coordinator.RefreshRootAndDependentsAsync(
            currentPath,
            TestContext.Current.CancellationToken);
        coordinator.HookOptionListeners(viewModel.RootFolders);
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
        await coordinator.RefreshRootAndDependentsAsync(
            currentPath,
            TestContext.Current.CancellationToken);
        coordinator.HookOptionListeners(viewModel.RootFolders);
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
        AssertOption(viewModel.RootFolders, "logs", expectedChecked: true);
        AssertOption(viewModel.RootFolders, "__pycache__", expectedChecked: true);
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

    [AvaloniaFact]
    public async Task CorruptedPersistedRootStates_CannotEscapeProjectOrPublishPhantomOptions()
    {
        using var parent = new TemporaryDirectory();
        var projectPath = parent.CreateFolder("project");
        var outsidePath = parent.CreateFolder("outside");
        WriteFile(projectPath, "src/App.cs", "class App {}\n");
        WriteFile(outsidePath, "leak/secret.outside", "must not be scanned\n");

        var viewModel = CreateViewModel();
        var ignoreRulesService = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService();
        using var coordinator = CreateCoordinator(viewModel, ignoreRulesService, () => projectPath);
        var maliciousRootStates = new Dictionary<string, bool>(PathComparer.Default)
        {
            ["src"] = true,
            ["missing"] = true,
            ["."] = true,
            [".."] = true,
            [$"..{Path.DirectorySeparatorChar}outside"] = true,
            [$"src{Path.DirectorySeparatorChar}nested"] = true,
            [outsidePath] = true
        };
        var profile = new ProjectSelectionProfile(
            SelectedRootFolders: maliciousRootStates
                .Where(static pair => pair.Value)
                .Select(static pair => pair.Key)
                .ToArray(),
            SelectedExtensions: [".cs", ".outside"],
            SelectedIgnoreOptions: [],
            RootFolderStates: maliciousRootStates,
            ExtensionStates: new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                [".cs"] = true,
                [".outside"] = true
            },
            IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>());

        coordinator.ApplyProjectProfileSelections(projectPath, profile);
        await coordinator.RefreshRootAndDependentsAsync(
            projectPath,
            TestContext.Current.CancellationToken);

        var root = Assert.Single(viewModel.RootFolders);
        Assert.Equal("src", root.Name);
        Assert.True(root.IsChecked);
        Assert.Contains(viewModel.Extensions, option => option.Name == ".cs" && option.IsChecked);
        Assert.DoesNotContain(viewModel.Extensions, option => option.Name == ".outside");
        Assert.All(
            viewModel.RootFolders,
            option => Assert.StartsWith(
                Path.GetFullPath(projectPath) + Path.DirectorySeparatorChar,
                Path.GetFullPath(Path.Combine(projectPath, option.Name)),
                PathComparer.Comparison));

        await AssertIslandMatchesColdSnapshotAsync(
            "corrupted root profile",
            projectPath,
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

        await coordinator.RefreshRootAndDependentsAsync(
            rootPath,
            TestContext.Current.CancellationToken);
    }

    private static async Task AssertIslandMatchesColdSnapshotAsync(
        string step,
        string rootPath,
        MainWindowViewModel viewModel,
        SelectionSyncCoordinator coordinator)
    {
        var rootStates = coordinator.SnapshotRootOptionStatesForPersistence() ??
            new Dictionary<string, bool>(PathComparer.Default);
        var extensionStates = coordinator.SnapshotExtensionOptionStatesForPersistence() ??
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var ignoreStates = coordinator.SnapshotIgnoreOptionStatesForPersistence() ??
            new Dictionary<IgnoreOptionId, bool>();
        var coldRulesService = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService();
        var coldEngine = CreateEngine(coldRulesService);
        var context = new SelectionRefreshContext(
            Path: rootPath,
            PreparedSelectionMode: PreparedSelectionMode.None,
            AllRootFoldersChecked: viewModel.AllRootFoldersChecked && !rootStates.Values.Contains(false),
            AllExtensionsChecked: viewModel.AllExtensionsChecked && !extensionStates.Values.Contains(false),
            RootSelectionInitialized: true,
            RootSelectionCache: rootStates
                .Where(static pair => pair.Value)
                .Select(static pair => pair.Key)
                .ToHashSet(PathComparer.Default),
            ExtensionsSelectionInitialized: true,
            ExtensionsSelectionCache: extensionStates
                .Where(static pair => pair.Value)
                .Select(static pair => pair.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            IgnoreSelectionInitialized: true,
            IgnoreSelectionCache: ignoreStates
                .Where(static pair => pair.Value)
                .Select(static pair => pair.Key)
                .ToHashSet(),
            IgnoreOptionStateCache: ignoreStates,
            IgnoreAllPreference: null,
            CurrentSnapshotState: new IgnoreSectionSnapshotState(
                HasIgnoreOptionCounts: false,
                IgnoreOptionCounts: IgnoreOptionCounts.Empty,
                ControllerImpactCounts: IgnoreControllerImpactCounts.Empty,
                HasExtensionlessEntries: false,
                ExtensionlessEntriesCount: 0),
            RootOptionStateCache: rootStates,
            ExtensionOptionStateCache: extensionStates,
            IgnoreOptionStateCacheIsComplete: true,
            CaptureTreeInventory: true,
            CurrentRootOptions: viewModel.RootFolders
                .Select(static option => new SelectionOption(option.Name, option.IsChecked))
                .ToArray());
        var coldSnapshot = coldEngine.ComputeFullRefreshSnapshot(
            context,
            TestContext.Current.CancellationToken);

        AssertOptionsEqual(
            coldSnapshot.RootOptions ?? [],
            viewModel.RootFolders.Select(static option => new SelectionOption(option.Name, option.IsChecked)),
            $"{step}: root options");
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
						 DefaultChecked: option.Id != IgnoreOptionId.HideSecrets,
						 option.IsChecked))),
            $"{step}: ignore options differ from a cold refresh.");

        Assert.Equal(
            viewModel.RootFolders.Count == 0 || viewModel.RootFolders.All(static option => option.IsChecked),
            viewModel.AllRootFoldersChecked);
        Assert.Equal(
            viewModel.Extensions.Count > 0 && viewModel.Extensions.All(static option => option.IsChecked),
            viewModel.AllExtensionsChecked);
        Assert.Equal(
            viewModel.IgnoreOptions.Count > 0 && viewModel.IgnoreOptions.All(static option => option.IsChecked),
            viewModel.AllIgnoreChecked);

        Assert.All(
            viewModel.RootFolders,
            option => Assert.True(
                Directory.Exists(Path.Combine(rootPath, option.Name)),
                $"{step}: root option '{option.Name}' is not a direct existing directory."));
        Assert.Equal(
            viewModel.RootFolders.Count,
            viewModel.RootFolders.Select(static option => option.Name).Distinct(PathComparer.Default).Count());
        Assert.Equal(
            viewModel.Extensions.Count,
            viewModel.Extensions.Select(static option => option.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        Assert.Equal(
            viewModel.IgnoreOptions.Count,
            viewModel.IgnoreOptions.Select(static option => option.Id).Distinct().Count());

        var selectedRoots = viewModel.RootFolders
            .Where(static option => option.IsChecked)
            .Select(static option => option.Name)
            .ToHashSet(PathComparer.Default);
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
            selectedRoots.SetEquals(treeRoots),
            $"{step}: checked roots [{string.Join(", ", selectedRoots.Order(PathComparer.Default))}] " +
            $"do not match final tree roots [{string.Join(", ", treeRoots.Order(PathComparer.Default))}].");

        await Task.CompletedTask;
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
        Func<string?> currentPathProvider)
    {
        return new SelectionSyncCoordinator(
            viewModel,
            new ScanOptionsUseCase(new FileSystemScanner()),
            new FilterOptionSelectionService(),
            ProjectLoadWorkflowRuntime.CreateIgnoreOptionsService(),
            (path, selectedIgnoreOptions, selectedRoots) =>
                ignoreRulesService.Build(path, selectedIgnoreOptions, selectedRoots),
            (path, selectedRoots) => ignoreRulesService.GetIgnoreOptionsAvailability(path, selectedRoots) with
            {
                ShowAdvancedCounts = true
            },
            _ => false,
            currentPathProvider);
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

    private static void WriteFile(string rootPath, string relativePath, string content)
    {
        var fullPath = Path.Combine(rootPath, relativePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(fullPath, content);
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
}
