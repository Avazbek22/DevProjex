using Avalonia.Controls;
using DevProjex.Application.Models;
using DevProjex.Application.Preview;
using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit;

public sealed class MainWindowCoordinatorRefactorTests
{
    [Theory]
    [InlineData("Receiving objects: 42%", 42)]
    [InlineData("99%", 99)]
    [InlineData("Resolving deltas: 12.5%", 12.5)]
    public void GitProgressStatusParser_ParsesTrailingPercent(string status, double expected)
    {
        Assert.True(GitProgressStatusParser.TryParseTrailingPercent(status, out var percent));
        Assert.Equal(expected, percent);
    }

    [Fact]
    public void ProjectTreeInventoryReuseScope_AllowsHiddenDotProjection_WhenSnapshotIsBroad()
    {
        var scope = ProjectTreeInventoryReuseScope.Create(
            @"C:\Project",
            CreateInventoryScopeOptions(
                roots: ["src", ".idea"],
                useGitIgnore: true,
                useSmartIgnore: true,
                ignoreHiddenFolders: true,
                ignoreDotFolders: true),
            supportsHiddenDotFolderVariants: true);

        var nextOptions = CreateInventoryScopeOptions(
            roots: ["src"],
            useGitIgnore: true,
            useSmartIgnore: true,
            ignoreHiddenFolders: false,
            ignoreDotFolders: false);

        Assert.True(scope.CanProject(@"C:\Project", nextOptions));
    }

    [Fact]
    public void ProjectTreeInventoryReuseScope_RejectsHiddenDotProjection_WhenSnapshotIsNarrow()
    {
        var scope = ProjectTreeInventoryReuseScope.Create(
            @"C:\Project",
            CreateInventoryScopeOptions(
                roots: ["src"],
                useGitIgnore: true,
                useSmartIgnore: true,
                ignoreHiddenFolders: true,
                ignoreDotFolders: true),
            supportsHiddenDotFolderVariants: false);

        var nextOptions = CreateInventoryScopeOptions(
            roots: ["src"],
            useGitIgnore: true,
            useSmartIgnore: true,
            ignoreHiddenFolders: false,
            ignoreDotFolders: false);

        Assert.False(scope.CanProject(@"C:\Project", nextOptions));
    }

    [Fact]
    public void ProjectTreeInventoryReuseScope_RejectsUnsafeRootAndControllerExpansion()
    {
        var scope = ProjectTreeInventoryReuseScope.Create(
            @"C:\Project",
            CreateInventoryScopeOptions(
                roots: ["src"],
                useGitIgnore: true,
                useSmartIgnore: true,
                ignoreHiddenFolders: false,
                ignoreDotFolders: false),
            supportsHiddenDotFolderVariants: true);

        Assert.False(scope.CanProject(
            @"C:\Project",
            CreateInventoryScopeOptions(
                roots: ["src", "docs"],
                useGitIgnore: true,
                useSmartIgnore: true,
                ignoreHiddenFolders: false,
                ignoreDotFolders: false)));
        Assert.False(scope.CanProject(
            @"C:\Project",
            CreateInventoryScopeOptions(
                roots: ["src"],
                useGitIgnore: false,
                useSmartIgnore: true,
                ignoreHiddenFolders: false,
                ignoreDotFolders: false)));
        Assert.False(scope.CanProject(
            @"C:\Project",
            CreateInventoryScopeOptions(
                roots: ["src"],
                useGitIgnore: true,
                useSmartIgnore: false,
                ignoreHiddenFolders: false,
                ignoreDotFolders: false)));
    }

    [Fact]
    public void ProjectTreeInventoryReuseScope_TracksDeepGitIgnoreTraversalWithoutPrebuiltMatcher()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "reuse-scope-deep-git");
        var disabledOptions = CreateInventoryScopeOptions(
            roots: ["src"],
            useGitIgnore: false,
            useSmartIgnore: false,
            ignoreHiddenFolders: false,
            ignoreDotFolders: false);
        var enabledOptions = disabledOptions with
        {
            IgnoreRules = disabledOptions.IgnoreRules with { EnableGitIgnoreTraversal = true }
        };
        var scope = ProjectTreeInventoryReuseScope.Create(
            rootPath,
            enabledOptions,
            supportsHiddenDotFolderVariants: true);

        Assert.False(scope.CanProject(rootPath, disabledOptions));
    }

    [Theory]
    [InlineData(1_000_000, 17_300, true)]
    [InlineData(50_000, 0, true)]
    [InlineData(49_999, 1, false)]
    [InlineData(100_000, 40_000, false)]
    [InlineData(60_000, 40_000, false)]
    [InlineData(50_000, 25_001, false)]
    public void ProjectTreeInventoryRetentionPolicy_ReleasesOnlyMateriallyOversizedSnapshots(
        int inventoryEntries,
        int visibleEntries,
        bool expectedRelease)
    {
        Assert.Equal(
            expectedRelease,
            ProjectTreeInventoryRetentionPolicy.ShouldReleaseReusedInventory(
                inventoryEntries,
                visibleEntries));
    }

    [Theory]
    [InlineData(49_999, false)]
    [InlineData(50_000, true)]
    public void ProjectTreeInventoryRetentionPolicy_MeasuresVisibleTreeOnlyPastRetentionFloor(
        int inventoryEntries,
        bool expectedMeasurement)
    {
        Assert.Equal(
            expectedMeasurement,
            ProjectTreeInventoryRetentionPolicy.RequiresVisibleTreeMeasurement(inventoryEntries));
    }

    [Fact]
    public void ProjectTreeInventoryRetentionPolicy_CountsCompleteVisibleHierarchy()
    {
        var leaf = new TreeNodeDescriptor("app.cs", "/project/src/app.cs", false, false, "file", []);
        var directory = new TreeNodeDescriptor("src", "/project/src", true, false, "folder", [leaf]);
        var root = new TreeNodeDescriptor("project", "/project", true, false, "folder", [directory]);

        Assert.Equal(3, ProjectTreeInventoryRetentionPolicy.CountTreeEntries(root));
    }

    [Fact]
    public void TaskbarProgressCoordinator_SyncsStatusAndGitCloneProgress()
    {
        var viewModel = CreateViewModel();
        var taskbar = new RecordingTaskbarProgressService();
        var coordinator = new TaskbarProgressCoordinator(viewModel, taskbar);

        viewModel.StatusBusy = true;
        viewModel.StatusProgressIsIndeterminate = true;
        coordinator.SyncWithStatusBar();

        Assert.Equal(TaskbarProgressRecordingState.Indeterminate, taskbar.LastState);

        viewModel.StatusProgressIsIndeterminate = false;
        viewModel.StatusProgressValue = 64;
        coordinator.SyncWithStatusBar();

        Assert.Equal(TaskbarProgressRecordingState.Progress, taskbar.LastState);
        Assert.Equal(64, taskbar.LastPercent);

        coordinator.BeginGitClone();
        coordinator.UpdateGitClone("Receiving objects: 77%");

        Assert.Equal(TaskbarProgressRecordingState.Progress, taskbar.LastState);
        Assert.Equal(77, taskbar.LastPercent);

        coordinator.MarkGitCloneError();
        Assert.Equal(TaskbarProgressRecordingState.Error, taskbar.LastState);

        viewModel.StatusBusy = false;
        coordinator.CompleteGitClone();
        Assert.Equal(TaskbarProgressRecordingState.Clear, taskbar.LastState);
    }

    [Fact]
    public void StatusOperationCoordinator_TracksActiveOperationAndIgnoresStaleCompletion()
    {
        var viewModel = CreateViewModel();
        var coordinator = new StatusOperationCoordinator(
            viewModel,
            isBackgroundMetricsActive: () => false,
            metricsOperationTextProvider: () => viewModel.StatusOperationCalculatingData);

        var first = coordinator.Begin("Loading", operationType: StatusOperationType.LoadProject);
        var second = coordinator.Begin("Preview", operationType: StatusOperationType.PreviewBuild);

        coordinator.Complete(first);

        Assert.True(viewModel.StatusBusy);
        Assert.Equal("Preview", viewModel.StatusOperationText);
        Assert.True(coordinator.IsActive(second));

        coordinator.UpdateProgress(42, "Preview 42%", second);

        Assert.False(viewModel.StatusProgressIsIndeterminate);
        Assert.Equal(42, viewModel.StatusProgressValue);
        Assert.Equal("Preview 42%", viewModel.StatusOperationText);

        coordinator.Complete(second);

        Assert.False(viewModel.StatusBusy);
        Assert.Equal(string.Empty, viewModel.StatusOperationText);
    }

    [Fact]
    public void StatusOperationCoordinator_LeavesMetricsVisibleWhenMetricsOperationIsActive()
    {
        var viewModel = CreateViewModel();
        var metricsActive = true;
        var coordinator = new StatusOperationCoordinator(
            viewModel,
            isBackgroundMetricsActive: () => metricsActive,
            metricsOperationTextProvider: () => "Calculating data");

        var operationId = coordinator.Begin(
            "Calculating data",
            operationType: StatusOperationType.MetricsCalculation);

        coordinator.Complete(operationId);

        Assert.True(viewModel.StatusBusy);
        Assert.Equal("Calculating data", viewModel.StatusOperationText);

        metricsActive = false;
        coordinator.Complete(operationId);

        Assert.False(viewModel.StatusBusy);
    }

    [Fact]
    public async Task ProjectLoadPipeline_OpenFolderAsync_RunsStableSuccessSequence()
    {
        var viewModel = CreateViewModel();
        var host = new RecordingProjectLoadHost(viewModel)
        {
            CurrentCachedRepoPathValue = @"C:\Cache\Repo"
        };
        var status = new StatusOperationCoordinator(
            viewModel,
            isBackgroundMetricsActive: () => false,
            metricsOperationTextProvider: () => viewModel.StatusOperationCalculatingData);
        using var pipeline = new ProjectLoadPipeline(host, status);

        await pipeline.OpenFolderAsync(@"C:\Project", fromDialog: true, recordRecentFolder: true);

        Assert.Equal(ProjectSourceType.LocalFolder, viewModel.ProjectSourceType);
        Assert.True(viewModel.IsProjectLoaded);
        Assert.Equal(
        [
            ProjectLoadHostCall.CaptureCancellationSnapshot,
            ProjectLoadHostCall.PrepareSearchAndFilter,
            ProjectLoadHostCall.CancelBackgroundMemoryCleanup,
            ProjectLoadHostCall.CancelPreviewRefresh,
            ProjectLoadHostCall.SetProjectLoadIdentity,
            ProjectLoadHostCall.UpdateTitle,
            ProjectLoadHostCall.YieldProjectLoadStartupFrame,
            ProjectLoadHostCall.ReloadProject,
            ProjectLoadHostCall.RecordRecentFolder,
            ProjectLoadHostCall.DeleteRepositoryDirectory,
            ProjectLoadHostCall.ClearCurrentCachedRepoPath,
            ProjectLoadHostCall.ClearProjectLoadCancellation,
            ProjectLoadHostCall.ScheduleInitialProjectLoadCleanup
        ], host.Calls);
    }

    [Fact]
    public async Task ProjectLoadPipeline_OpenFolderAsync_CancellationAppliesFallbackWithoutSuccessSideEffects()
    {
        var viewModel = CreateViewModel();
        var host = new RecordingProjectLoadHost(viewModel);
        var status = new StatusOperationCoordinator(
            viewModel,
            isBackgroundMetricsActive: () => false,
            metricsOperationTextProvider: () => viewModel.StatusOperationCalculatingData);
        ProjectLoadPipeline? pipeline = null;
        host.ReloadHandler = token =>
        {
            pipeline!.CancelActiveLoad();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        pipeline = new ProjectLoadPipeline(host, status);

        await pipeline.OpenFolderAsync(@"C:\Canceled", fromDialog: false, recordRecentFolder: true);
        pipeline.Dispose();

        Assert.Contains(ProjectLoadHostCall.ApplyCancellationFallback, host.Calls);
        Assert.Contains(ProjectLoadHostCall.ShowLoadCanceledToast, host.Calls);
        Assert.DoesNotContain(ProjectLoadHostCall.RecordRecentFolder, host.Calls);
        Assert.DoesNotContain(ProjectLoadHostCall.ScheduleInitialProjectLoadCleanup, host.Calls);
        Assert.False(viewModel.StatusBusy);
    }

    [Fact]
    public void MetricsPipeline_StatusSnapshotFeedsPreviewSelectionCache()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedPreviewContentMode = PreviewContentMode.TreeAndContent;
        using var pipeline = CreateMetricsPipeline(viewModel, boundsWidth: 900);
        using var document = new InMemoryPreviewTextDocument("tree\ncontent");

        pipeline.UpdateStatusBarMetrics(
            treeLines: 1,
            treeChars: 2,
            treeTokens: 3,
            contentLines: 4,
            contentChars: 5,
            contentTokens: 6);

        Assert.Equal("[lines 1]", viewModel.StatusTreeStatsText);
        Assert.True(pipeline.HasStatusMetricsSnapshot);
        Assert.True(pipeline.TryGetCachedPreviewSelectionMetrics(
            viewModel.SelectedPreviewContentMode,
            document,
            new PreviewSelectionRange(1, 0, 2, 7),
            out var metrics));
        Assert.Equal(new ExportOutputMetrics(5, 7, 9), metrics);
    }

    [Fact]
    public void MetricsPipeline_StatusRendering_UsesCombinedRelativeMetricsOnlyWhileCombinedPreviewIsVisible()
    {
        var viewModel = CreateViewModel();
        viewModel.PreviewWorkspaceMode = PreviewWorkspaceMode.TreeAndPreview;
        viewModel.SelectedPreviewContentMode = PreviewContentMode.TreeAndContent;
        using var pipeline = CreateMetricsPipeline(viewModel, boundsWidth: 1400);
        using var document = new InMemoryPreviewTextDocument("tree\ncontent");
        var relativeContentMetrics = new ExportOutputMetrics(4, 300, 75);

        pipeline.UpdateStatusBarMetrics(
            treeLines: 1,
            treeChars: 20,
            treeTokens: 5,
            contentLines: 4,
            contentChars: 500,
            contentTokens: 125,
            relativeContentMetrics);

        Assert.Contains("chars 300", viewModel.StatusContentStatsText, StringComparison.Ordinal);
        Assert.True(pipeline.TryGetCachedPreviewSelectionMetrics(
            PreviewContentMode.TreeAndContent,
            document,
            new PreviewSelectionRange(1, 0, 2, 7),
            out var combinedMetrics));
        Assert.Equal(new ExportOutputMetrics(5, 320, 80), combinedMetrics);

        viewModel.SelectedPreviewContentMode = PreviewContentMode.Content;
        pipeline.RenderStatusBarMetrics();
        Assert.Contains("chars 500", viewModel.StatusContentStatsText, StringComparison.Ordinal);

        viewModel.SelectedPreviewContentMode = PreviewContentMode.TreeAndContent;
        viewModel.PreviewWorkspaceMode = PreviewWorkspaceMode.Off;
        pipeline.RenderStatusBarMetrics();
        Assert.Contains("chars 500", viewModel.StatusContentStatsText, StringComparison.Ordinal);
    }

    [Fact]
    public void MetricsPipeline_StatusSnapshotPreservesWorkspaceMetricsBeyondInt32()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedPreviewContentMode = PreviewContentMode.TreeAndContent;
        using var pipeline = CreateMetricsPipeline(viewModel, boundsWidth: 1400);
        using var document = new InMemoryPreviewTextDocument("tree\ncontent");

        pipeline.UpdateStatusBarMetrics(
            treeLines: 100_000_000,
            treeChars: 1_000_000_000,
            treeTokens: 250_000_000,
            contentLines: 2_400_000_006,
            contentChars: 3_000_000_015,
            contentTokens: 750_000_004);

        Assert.True(pipeline.TryGetCachedPreviewSelectionMetrics(
            PreviewContentMode.TreeAndContent,
            document,
            new PreviewSelectionRange(1, 0, 2, 7),
            out var metrics));
        Assert.Equal(
            new ExportOutputMetrics(2_500_000_006, 4_000_000_015, 1_000_000_004),
            metrics);
        Assert.DoesNotContain("chars 0", viewModel.StatusContentStatsText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewWorkspacePipeline_CacheHitReappliesDocumentWithoutRebuilding()
    {
        var viewModel = CreateViewModel();
        viewModel.IsProjectLoaded = true;
        viewModel.PreviewWorkspaceMode = PreviewWorkspaceMode.TreeAndPreview;
        using var document = new InMemoryPreviewTextDocument("cached");
        viewModel.PreviewDocument = document;

        var host = new RecordingPreviewWorkspaceHost(viewModel);
        using var pipeline = new PreviewWorkspacePipeline(host, TimeSpan.FromMilliseconds(1));
        pipeline.CachePreview(host.Input.CacheKey);

        await pipeline.RefreshNowAsync();

        Assert.Equal(1, host.ApplyDocumentCount);
        Assert.Equal(0, host.BuildDocumentCount);
        Assert.Equal(0, host.PreviewDocumentCleanupRequestCount);
        Assert.True(pipeline.IsIdle);
        Assert.False(viewModel.IsPreviewLoading);
    }

    [Fact]
    public async Task PreviewWorkspacePipeline_NewBuildSchedulesCleanupForAppliedDocument()
    {
        var viewModel = CreateViewModel();
        viewModel.IsProjectLoaded = true;
        viewModel.PreviewWorkspaceMode = PreviewWorkspaceMode.TreeAndPreview;

        var host = new RecordingPreviewWorkspaceHost(viewModel);
        using var pipeline = new PreviewWorkspacePipeline(host, TimeSpan.FromMilliseconds(1));

        await pipeline.RefreshNowAsync();

        Assert.Equal(1, host.ApplyDocumentCount);
        Assert.Equal(1, host.BuildDocumentCount);
        Assert.Equal(1, host.PreviewDocumentCleanupRequestCount);
        viewModel.PreviewDocument?.Dispose();
    }

    [Fact]
    public async Task PreviewWorkspacePipeline_StaleBuildDoesNotApplyResult()
    {
        var viewModel = CreateViewModel();
        viewModel.IsProjectLoaded = true;
        viewModel.PreviewWorkspaceMode = PreviewWorkspaceMode.TreeAndPreview;

        PreviewWorkspacePipeline? pipeline = null;
        var host = new RecordingPreviewWorkspaceHost(viewModel)
        {
            BuildDocumentHandler = _ =>
            {
                pipeline!.CancelActiveBuildAndInvalidate();
                return new PreviewBuildResult(new InMemoryPreviewTextDocument("stale"));
            }
        };
        pipeline = new PreviewWorkspacePipeline(host, TimeSpan.FromMilliseconds(1));

        await pipeline.RefreshNowAsync();
        pipeline.Dispose();

        Assert.Equal(1, host.BuildDocumentCount);
        Assert.Equal(0, host.ApplyDocumentCount);
        Assert.False(viewModel.IsPreviewLoading);
    }

    [Fact]
    public async Task RefreshTreePipeline_CancelsActiveRefreshBeforeApplyingTree()
    {
        var viewModel = CreateViewModel();
        var releaseBuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new RecordingRefreshTreeHost(viewModel)
        {
            BuildTreeHandler = token =>
            {
                releaseBuild.Task.Wait(TestContext.Current.CancellationToken);
                token.ThrowIfCancellationRequested();
                return new BuildTreeSnapshotResult(
                    RecordingRefreshTreeHost.CreateResult("root"),
                    RecordingRefreshTreeHost.CreateInventorySnapshot());
            }
        };
        using var pipeline = new RefreshTreePipeline(host);

        var refreshTask = pipeline.RefreshTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await Task.Delay(30, TestContext.Current.CancellationToken);
        pipeline.CancelActiveRefresh();
        releaseBuild.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refreshTask);
        Assert.Equal(0, host.ApplyCount);
    }

    [Fact]
    public async Task RefreshTreePipeline_LeavesStaleProjectTreeUntouched()
    {
        var viewModel = CreateViewModel();
        RecordingRefreshTreeHost? host = null;
        host = new RecordingRefreshTreeHost(viewModel)
        {
            BuildViewModelHandler = (input, result) =>
            {
                host!.CurrentPath = @"C:\ProjectB";
                return new TreeNodeViewModel(result.Root, parent: null, icon: null)
                {
                    DisplayName = input.DisplayName
                };
            }
        };
        using var pipeline = new RefreshTreePipeline(host);

        await pipeline.RefreshTreeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, host.BuildTreeCount);
        Assert.Equal(0, host.ApplyCount);
        Assert.Empty(viewModel.TreeNodes);
    }

    [Fact]
    public async Task ProjectLoadSnapshotPipeline_ReloadAsync_BuildsTreeFromSelectionSnapshot()
    {
        var selectionSnapshot = CreateSelectionRefreshSnapshot();
        var host = new RecordingProjectLoadSnapshotHost(selectionSnapshot);
        var pipeline = new ProjectLoadSnapshotPipeline(host);

        await pipeline.ReloadAsync(@"C:\Project", TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                ProjectLoadSnapshotHostCall.BuildSelectionSnapshot,
                ProjectLoadSnapshotHostCall.CreateTreeInput,
                ProjectLoadSnapshotHostCall.BeforeTreeRefresh,
                ProjectLoadSnapshotHostCall.BuildTree,
                ProjectLoadSnapshotHostCall.BuildTreeViewModel,
                ProjectLoadSnapshotHostCall.ApplySnapshot
            ],
            host.Calls);
        Assert.NotNull(host.CapturedTreeInput);
        Assert.Contains("src", host.CapturedTreeInput!.Options.AllowedRootFolders);
        Assert.DoesNotContain("docs", host.CapturedTreeInput.Options.AllowedRootFolders);
        Assert.Contains(".cs", host.CapturedTreeInput.Options.AllowedExtensions);
        Assert.DoesNotContain(".md", host.CapturedTreeInput.Options.AllowedExtensions);
        Assert.True(host.CapturedTreeInput.Options.IgnoreRules.IgnoreDotFolders);
    }

    [Fact]
    public async Task ProjectLoadSnapshotPipeline_ReloadAsync_SelectionAccessDeniedStopsBeforeTreeBuild()
    {
        var host = new RecordingProjectLoadSnapshotHost(
            CreateSelectionRefreshSnapshot(rootAccessDenied: true))
        {
            HandleSelectionAccessDenied = true
        };
        var pipeline = new ProjectLoadSnapshotPipeline(host);

        await pipeline.ReloadAsync(@"C:\Project", TestContext.Current.CancellationToken);

        Assert.Equal(
            [ProjectLoadSnapshotHostCall.BuildSelectionSnapshot],
            host.Calls);
        Assert.Equal(0, host.BuildTreeCount);
        Assert.Equal(0, host.ApplyCount);
    }

    [Fact]
    public void ProjectLoadCancellationCoordinator_AppliesExpectedFallback()
    {
        var coordinator = new ProjectLoadCancellationCoordinator();
        var resetCalled = false;
        ProjectLoadCancellationSnapshot? restored = null;

        Assert.False(coordinator.TryApply(() => resetCalled = true, snapshot => restored = snapshot));

        var noPreviousProject = CreateProjectLoadSnapshot(hadLoadedProjectBefore: false);
        coordinator.Capture(noPreviousProject);

        Assert.True(coordinator.TryApply(() => resetCalled = true, snapshot => restored = snapshot));
        Assert.True(resetCalled);
        Assert.Null(restored);

        resetCalled = false;
        var previousProject = CreateProjectLoadSnapshot(hadLoadedProjectBefore: true);
        coordinator.Capture(previousProject);

        Assert.True(coordinator.TryApply(() => resetCalled = true, snapshot => restored = snapshot));
        Assert.False(resetCalled);
        Assert.Same(previousProject, restored);
    }

    [Fact]
    public void ProjectProfilePersistenceCoordinator_PersistsOnlyLocalFoldersAndFlushesPendingSave()
    {
        var viewModel = CreateViewModel();
        var store = new FlakyProjectProfileStore();
        var selectionCoordinator = CreateSelectionCoordinator(viewModel);
        var coordinator = new ProjectProfilePersistenceCoordinator(viewModel, selectionCoordinator, store);

        viewModel.ProjectSourceType = ProjectSourceType.GitClone;
        coordinator.PersistIfNeeded(@"C:\Repo");

        Assert.Equal(0, store.SaveAttempts);

        viewModel.ProjectSourceType = ProjectSourceType.LocalFolder;
        viewModel.RootFolders.Add(new SelectionOptionViewModel("src", true));
        viewModel.RootFolders.Add(new SelectionOptionViewModel("docs", false));
        viewModel.Extensions.Add(new SelectionOptionViewModel(".cs", true));
        viewModel.Extensions.Add(new SelectionOptionViewModel(".csv", false));

        store.FailNextSave = true;
        coordinator.PersistIfNeeded(@"C:\Project");

        Assert.Equal(1, store.SaveAttempts);
        Assert.False(store.HasProfile(@"C:\Project"));

        coordinator.FlushPending();

        Assert.Equal(2, store.SaveAttempts);
        Assert.True(store.TryLoadProfile(@"C:\Project", out var persisted));
        Assert.Equal(["src"], persisted.SelectedRootFolders);
        Assert.Equal([".cs"], persisted.SelectedExtensions);
        Assert.False(persisted.RootFolderStates!["docs"]);
        Assert.False(persisted.ExtensionStates![".csv"]);
    }

    private static ProjectLoadCancellationSnapshot CreateProjectLoadSnapshot(bool hadLoadedProjectBefore)
    {
        return new ProjectLoadCancellationSnapshot(
            HadLoadedProjectBefore: hadLoadedProjectBefore,
            Path: @"C:\Project",
            ProjectDisplayName: "Project",
            RepositoryUrl: null,
            Tree: null,
            ProjectSourceType: ProjectSourceType.LocalFolder,
            CurrentBranch: string.Empty,
            GitBranches: [],
            SettingsVisible: true,
            SearchVisible: false,
            FilterVisible: false,
            PreviewWorkspaceMode: PreviewWorkspaceMode.Off,
            StatusMetricsVisible: false,
            StatusTreeStatsText: string.Empty,
            StatusContentStatsText: string.Empty,
            AllRootFoldersChecked: true,
            AllExtensionsChecked: true,
            AllIgnoreChecked: true,
            HasCompleteMetricsBaseline: false,
            RootFolders: [],
            Extensions: [],
            IgnoreOptions: []);
    }

    private static TreeFilterOptions CreateInventoryScopeOptions(
        IReadOnlyCollection<string> roots,
        bool useGitIgnore,
        bool useSmartIgnore,
        bool ignoreHiddenFolders,
        bool ignoreDotFolders)
    {
        return new TreeFilterOptions(
            AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AllowedRootFolders: new HashSet<string>(roots, PathComparer.Default),
            IgnoreRules: new IgnoreRules(
                IgnoreHiddenFolders: ignoreHiddenFolders,
                IgnoreHiddenFiles: false,
                IgnoreDotFolders: ignoreDotFolders,
                IgnoreDotFiles: false,
                SmartIgnoredFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                SmartIgnoredFiles: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
            {
                UseGitIgnore = useGitIgnore,
                UseSmartIgnore = useSmartIgnore
            });
    }

    private static SelectionRefreshSnapshot CreateSelectionRefreshSnapshot(bool rootAccessDenied = false)
    {
        return new SelectionRefreshSnapshot(
            RootOptions:
            [
                new SelectionOption("src", true),
                new SelectionOption("docs", false)
            ],
            ExtensionOptions:
            [
                new SelectionOption(".cs", true),
                new SelectionOption(".md", false)
            ],
            IgnoreOptions:
            [
                new ResolvedIgnoreOptionState(IgnoreOptionId.DotFolders, "dot folders", true, true),
                new ResolvedIgnoreOptionState(IgnoreOptionId.EmptyFiles, "empty files", true, false)
            ],
            ExtensionlessEntriesCount: 0,
            HasIgnoreOptionCounts: true,
            IgnoreOptionCounts: new IgnoreOptionCounts(DotFolders: 1),
            ControllerImpactCounts: IgnoreControllerImpactCounts.Empty,
            IgnoreOptionStateCache: new Dictionary<IgnoreOptionId, bool>
            {
                [IgnoreOptionId.DotFolders] = true,
                [IgnoreOptionId.EmptyFiles] = false
            },
            RootAccessDenied: rootAccessDenied,
            HadAccessDenied: rootAccessDenied);
    }

    private static SelectionSyncCoordinator CreateSelectionCoordinator(MainWindowViewModel viewModel)
    {
        var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
        var scanner = new StubFileSystemScanner();
        var scanOptions = new ScanOptionsUseCase(scanner);
        var filterService = new FilterOptionSelectionService();
        var ignoreService = new IgnoreOptionsService(localization);

        return new SelectionSyncCoordinator(
            viewModel,
            scanOptions,
            filterService,
            ignoreService,
            (_, _, _) => new IgnoreRules(
                IgnoreHiddenFolders: false,
                IgnoreHiddenFiles: false,
                IgnoreDotFolders: false,
                IgnoreDotFiles: false,
                SmartIgnoredFolders: new HashSet<string>(),
                SmartIgnoredFiles: new HashSet<string>()),
            (_, _) => new IgnoreOptionsAvailability(false, false),
            _ => false,
            () => @"C:\Project");
    }

    private static MainWindowViewModel CreateViewModel()
    {
        var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
        return new MainWindowViewModel(localization, new HelpContentProvider());
    }

    private static MetricsPipeline CreateMetricsPipeline(MainWindowViewModel viewModel, double boundsWidth)
    {
        var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
        var status = new StatusOperationCoordinator(
            viewModel,
            isBackgroundMetricsActive: () => false,
            metricsOperationTextProvider: () => viewModel.StatusOperationCalculatingData);

        return new MetricsPipeline(
            viewModel,
            localization,
            new StubFileContentAnalyzer(),
            new TreeExportService(),
            status,
            currentTreeProvider: () => null,
            currentPathProvider: () => null,
            selectedPathsProvider: () => new HashSet<string>(PathComparer.Default),
            treeFormatProvider: () => TreeTextFormat.Ascii,
            exportPathPresentationProvider: () => null,
            boundsWidthProvider: () => boundsWidth);
    }

    private static StubLocalizationCatalog CreateCatalog()
    {
        var data = new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
        {
            [AppLanguage.En] = new Dictionary<string, string>
            {
                ["Settings.Ignore.SmartIgnore"] = "Smart ignore",
                ["Settings.Ignore.UseGitIgnore"] = "Use .gitignore",
                ["Settings.Ignore.HiddenFolders"] = "Hidden folders",
                ["Settings.Ignore.HiddenFiles"] = "Hidden files",
                ["Settings.Ignore.DotFolders"] = "dot folders",
                ["Settings.Ignore.DotFiles"] = "dot files",
                ["Settings.Ignore.ExtensionlessFiles"] = "Files without extension",
                ["Status.Operation.LoadingProject"] = "Loading project",
                ["Status.Metric.Lines"] = "{0} lines",
                ["Status.Metric.Chars"] = "{0} chars",
                ["Status.Metric.Tokens"] = "{0} tokens",
                ["Status.Operation.CalculatingData"] = "Calculating data"
            }
        };

        return new StubLocalizationCatalog(data);
    }

    private sealed class FlakyProjectProfileStore : IProjectProfileStore
    {
        private readonly Dictionary<string, ProjectSelectionProfile> _profiles = new(PathComparer.Default);

        public bool FailNextSave { get; set; }

        public int SaveAttempts { get; private set; }

        public bool EnsureStorageExists() => true;

        public bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile)
        {
            if (_profiles.TryGetValue(localProjectPath, out profile!))
                return true;

            profile = new ProjectSelectionProfile([], [], []);
            return false;
        }

        public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile)
            => TrySaveProfile(localProjectPath, profile, DateTimeOffset.UtcNow);

        public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile, DateTimeOffset updatedUtc)
        {
            _ = updatedUtc;
            SaveAttempts++;
            if (FailNextSave)
            {
                FailNextSave = false;
                return false;
            }

            _profiles[localProjectPath] = ProjectSelectionProfileBuilder.Clone(profile);
            return true;
        }

        public void SaveProfile(string localProjectPath, ProjectSelectionProfile profile)
        {
            TrySaveProfile(localProjectPath, profile);
        }

        public void ClearAllProfiles()
        {
            _profiles.Clear();
        }

        public bool HasProfile(string path) => _profiles.ContainsKey(path);
    }

    private sealed class RecordingTaskbarProgressService : ITaskbarProgressService
    {
        public bool IsSupported => true;

        public TaskbarProgressRecordingState LastState { get; private set; } = TaskbarProgressRecordingState.None;

        public double LastPercent { get; private set; }

        public void Attach(Window window)
        {
            _ = window;
            LastState = TaskbarProgressRecordingState.Attached;
        }

        public void SetIndeterminate()
        {
            LastState = TaskbarProgressRecordingState.Indeterminate;
        }

        public void SetProgress(double percent)
        {
            LastState = TaskbarProgressRecordingState.Progress;
            LastPercent = percent;
        }

        public void SetPaused()
        {
            LastState = TaskbarProgressRecordingState.Paused;
        }

        public void SetError()
        {
            LastState = TaskbarProgressRecordingState.Error;
        }

        public void Clear()
        {
            LastState = TaskbarProgressRecordingState.Clear;
        }

        public void Dispose()
        {
            LastState = TaskbarProgressRecordingState.Disposed;
        }
    }

    private enum TaskbarProgressRecordingState
    {
        None,
        Attached,
        Indeterminate,
        Progress,
        Paused,
        Error,
        Clear,
        Disposed
    }

    private sealed class RecordingProjectLoadHost(MainWindowViewModel viewModel) : IProjectLoadPipelineHost
    {
        public MainWindowViewModel ViewModel => viewModel;

        public string? CurrentCachedRepoPathValue { get; set; }

        public string? CurrentCachedRepoPath => CurrentCachedRepoPathValue;

        public List<ProjectLoadHostCall> Calls { get; } = [];

        public Func<CancellationToken, Task>? ReloadHandler { get; set; }

        public void CaptureProjectLoadCancellationSnapshot() =>
            Calls.Add(ProjectLoadHostCall.CaptureCancellationSnapshot);

        public Task PrepareSearchAndFilterForProjectLoadAsync()
        {
            Calls.Add(ProjectLoadHostCall.PrepareSearchAndFilter);
            return Task.CompletedTask;
        }

        public void CancelBackgroundMemoryCleanup() =>
            Calls.Add(ProjectLoadHostCall.CancelBackgroundMemoryCleanup);

        public void CancelPreviewRefresh() =>
            Calls.Add(ProjectLoadHostCall.CancelPreviewRefresh);

        public Task YieldProjectLoadStartupFrameAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(ProjectLoadHostCall.YieldProjectLoadStartupFrame);
            return Task.CompletedTask;
        }

        public void ClearPreviousProjectState(bool forceCompactingGc) =>
            Calls.Add(forceCompactingGc
                ? ProjectLoadHostCall.ClearPreviousProjectStateWithCompactingGc
                : ProjectLoadHostCall.ClearPreviousProjectState);

        public void SetProjectLoadIdentity(string path, bool fromDialog)
        {
            _ = path;
            Calls.Add(ProjectLoadHostCall.SetProjectLoadIdentity);
            viewModel.IsProjectLoaded = true;
            viewModel.SettingsVisible = true;
            viewModel.SearchVisible = false;
            if (fromDialog)
                viewModel.ProjectSourceType = ProjectSourceType.LocalFolder;
        }

        public void UpdateTitle() =>
            Calls.Add(ProjectLoadHostCall.UpdateTitle);

        public async Task ReloadProjectAsync(CancellationToken cancellationToken, bool applyStoredProfile)
        {
            _ = applyStoredProfile;
            Calls.Add(ProjectLoadHostCall.ReloadProject);
            if (ReloadHandler is not null)
                await ReloadHandler(cancellationToken);
        }

        public void RecordRecentFolder(string path)
        {
            _ = path;
            Calls.Add(ProjectLoadHostCall.RecordRecentFolder);
        }

        public void DeleteRepositoryDirectory(string path)
        {
            _ = path;
            Calls.Add(ProjectLoadHostCall.DeleteRepositoryDirectory);
        }

        public void ClearCurrentCachedRepoPath()
        {
            CurrentCachedRepoPathValue = null;
            Calls.Add(ProjectLoadHostCall.ClearCurrentCachedRepoPath);
        }

        public void ClearProjectLoadCancellation() =>
            Calls.Add(ProjectLoadHostCall.ClearProjectLoadCancellation);

        public bool TryApplyActiveProjectLoadCancellationFallback()
        {
            Calls.Add(ProjectLoadHostCall.ApplyCancellationFallback);
            return true;
        }

        public void ScheduleProjectLoadMemoryCleanup(bool hadLoadedProjectBefore) =>
            Calls.Add(hadLoadedProjectBefore
                ? ProjectLoadHostCall.ScheduleProjectSwitchCleanup
                : ProjectLoadHostCall.ScheduleInitialProjectLoadCleanup);

        public void ShowLoadCanceledToast() =>
            Calls.Add(ProjectLoadHostCall.ShowLoadCanceledToast);
    }

    private enum ProjectLoadHostCall
    {
        CaptureCancellationSnapshot,
        PrepareSearchAndFilter,
        CancelBackgroundMemoryCleanup,
        CancelPreviewRefresh,
        YieldProjectLoadStartupFrame,
        ClearPreviousProjectState,
        ClearPreviousProjectStateWithCompactingGc,
        SetProjectLoadIdentity,
        UpdateTitle,
        ReloadProject,
        RecordRecentFolder,
        DeleteRepositoryDirectory,
        ClearCurrentCachedRepoPath,
        ClearProjectLoadCancellation,
        ApplyCancellationFallback,
        ScheduleInitialProjectLoadCleanup,
        ScheduleProjectSwitchCleanup,
        ShowLoadCanceledToast
    }

    private sealed class RecordingProjectLoadSnapshotHost(SelectionRefreshSnapshot selectionSnapshot)
        : IProjectLoadSnapshotPipelineHost
    {
        public List<ProjectLoadSnapshotHostCall> Calls { get; } = [];

        public TreeRefreshInput? CapturedTreeInput { get; private set; }

        public int BuildTreeCount { get; private set; }

        public int ApplyCount { get; private set; }

        public bool HandleSelectionAccessDenied { get; init; }

        public Task<SelectionRefreshSnapshot?> BuildSelectionSnapshotAsync(
            string currentPath,
            CancellationToken cancellationToken)
        {
            Assert.Equal(@"C:\Project", currentPath);
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(ProjectLoadSnapshotHostCall.BuildSelectionSnapshot);
            return Task.FromResult<SelectionRefreshSnapshot?>(selectionSnapshot);
        }

        public bool TryHandleSelectionRootAccessDenied(
            string currentPath,
            SelectionRefreshSnapshot snapshot)
        {
            Assert.Equal(@"C:\Project", currentPath);
            Assert.Same(selectionSnapshot, snapshot);
            return HandleSelectionAccessDenied;
        }

        public TreeRefreshInput CreateTreeRefreshInput(
            string currentPath,
            SelectionRefreshSnapshot snapshot)
        {
            Assert.Same(selectionSnapshot, snapshot);
            Calls.Add(ProjectLoadSnapshotHostCall.CreateTreeInput);

            var allowedRoots = snapshot.RootOptions!
                .Where(static option => option.IsChecked)
                .Select(static option => option.Name)
                .ToHashSet(PathComparer.Default);
            var allowedExtensions = snapshot.ExtensionOptions
                .Where(static option => option.IsChecked)
                .Select(static option => option.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selectedIgnoreOptions = snapshot.IgnoreOptions
                .Where(static option => option.IsChecked)
                .Select(static option => option.Id)
                .ToHashSet();
            var rules = new IgnoreRules(
                IgnoreHiddenFolders: false,
                IgnoreHiddenFiles: false,
                IgnoreDotFolders: selectedIgnoreOptions.Contains(IgnoreOptionId.DotFolders),
                IgnoreDotFiles: false,
                SmartIgnoredFolders: new HashSet<string>(),
                SmartIgnoredFiles: new HashSet<string>());

            CapturedTreeInput = new TreeRefreshInput(
                currentPath,
                "Project",
                new TreeFilterOptions(
                    allowedExtensions,
                    allowedRoots,
                    rules),
                NameFilter: null);
            return CapturedTreeInput;
        }

        public void BeforeProjectLoadTreeRefresh() =>
            Calls.Add(ProjectLoadSnapshotHostCall.BeforeTreeRefresh);

        public BuildTreeSnapshotResult BuildTree(TreeRefreshInput input, CancellationToken cancellationToken)
        {
            Assert.Same(CapturedTreeInput, input);
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(ProjectLoadSnapshotHostCall.BuildTree);
            BuildTreeCount++;
            return new BuildTreeSnapshotResult(
                RecordingRefreshTreeHost.CreateResult("root"),
                CreateInventorySnapshot());
        }

        public bool TryHandleTreeRootAccessDenied(TreeRefreshInput input, BuildTreeResult result)
        {
            Assert.Same(CapturedTreeInput, input);
            _ = result;
            return false;
        }

        public TreeNodeViewModel BuildTreeViewModel(TreeRefreshInput input, BuildTreeResult result)
        {
            Assert.Same(CapturedTreeInput, input);
            Calls.Add(ProjectLoadSnapshotHostCall.BuildTreeViewModel);
            return new TreeNodeViewModel(result.Root, parent: null, icon: null)
            {
                DisplayName = input.DisplayName
            };
        }

        public void ApplyProjectLoadSnapshot(
            ProjectLoadSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Same(selectionSnapshot, snapshot.SelectionSnapshot);
            Assert.Same(CapturedTreeInput, snapshot.TreeInput);
            Assert.NotNull(snapshot.TreeInventory);
            Calls.Add(ProjectLoadSnapshotHostCall.ApplySnapshot);
            ApplyCount++;
        }

        private static ProjectTreeInventorySnapshot CreateInventorySnapshot()
        {
            return new ProjectTreeInventorySnapshot(
                [
                    new ProjectTreeInventoryEntry(
                        "Project",
                        @"C:\Project",
                        relativePath: string.Empty,
                        parentIndex: -1,
                        isDirectory: true,
                        isHidden: false,
                        length: 0)
                ],
                rootAccessDenied: false,
                hadAccessDenied: false);
        }
    }

    private enum ProjectLoadSnapshotHostCall
    {
        BuildSelectionSnapshot,
        CreateTreeInput,
        BeforeTreeRefresh,
        BuildTree,
        BuildTreeViewModel,
        ApplySnapshot
    }

    private sealed class RecordingPreviewWorkspaceHost(MainWindowViewModel viewModel) : IPreviewWorkspacePipelineHost
    {
        private static readonly TreeNodeDescriptor EmptyRoot = new(
            "root",
            @"C:\Project",
            IsDirectory: true,
            IsAccessDenied: false,
            IconKey: "folder",
            Children: []);

        public MainWindowViewModel ViewModel => viewModel;

        public bool IsPreviewModeSwitchInProgress { get; set; }

        public PreviewRefreshInput Input { get; } = new(
            SelectedMode: PreviewContentMode.Content,
            SelectedPaths: new HashSet<string>(PathComparer.Default),
            HasSelection: false,
            TreeFormat: TreeTextFormat.Ascii,
            NoCheckedFilesText: "No checked files",
            NoTextContentText: "No text content",
            NoDataText: "No data",
            CurrentPath: @"C:\Project",
            CurrentTreeRoot: EmptyRoot,
            CurrentTreeOrderedFilePaths: null,
            PathPresentation: null,
            CacheKey: new PreviewCacheKeyData(
                ProjectPath: @"C:\Project",
                TreeIdentity: 1,
                Mode: PreviewContentMode.Content,
                TreeFormat: TreeTextFormat.Ascii,
                SelectedCount: 0,
                SelectedHash: 0));

        public Func<CancellationToken, PreviewBuildResult>? BuildDocumentHandler { get; set; }

        public int ApplyDocumentCount { get; private set; }

        public int BuildDocumentCount { get; private set; }

        public int PreviewDocumentCleanupRequestCount { get; private set; }

        public bool EnsurePreviewTreeReady() => true;

        public void ApplyPreviewNoDataText() =>
            viewModel.PreviewText = "No data";

        public long BeginPreviewBuildOperation(CancellationTokenSource previewCts)
        {
            _ = previewCts;
            return 1;
        }

        public void CompletePreviewBuildOperation(long operationId) =>
            Assert.Equal(1, operationId);

        public PreviewRefreshInput CapturePreviewRefreshInput() => Input;

        public bool IsCurrentPreviewCacheHit(PreviewCacheKeyData key) =>
            key == Input.CacheKey && viewModel.PreviewDocument is not null;

        public IPreviewTextDocument? CurrentPreviewDocument => viewModel.PreviewDocument;

        public void ApplyPreviewDocument(IPreviewTextDocument document)
        {
            ApplyDocumentCount++;
            viewModel.PreviewDocument = document;
        }

        public void ApplyPreviewText(string text) =>
            viewModel.PreviewText = text;

        public void ApplyPreviewText(string text, int lineCount)
        {
            viewModel.PreviewText = text;
            viewModel.PreviewLineCount = lineCount;
        }

        public void ClearPreviewDocument() =>
            viewModel.PreviewDocument = null;

        public Task<PreviewWarmupSnapshot?> TryBuildPreviewWarmupSnapshotAsync(
            PreviewRefreshInput input,
            CancellationToken cancellationToken)
        {
            _ = input;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<PreviewWarmupSnapshot?>(null);
        }

        public PreviewBuildResult BuildPreviewDocument(
            PreviewRefreshInput input,
            CancellationToken cancellationToken)
        {
            _ = input;
            cancellationToken.ThrowIfCancellationRequested();
            BuildDocumentCount++;
            return BuildDocumentHandler?.Invoke(cancellationToken) ??
                   new PreviewBuildResult(new InMemoryPreviewTextDocument("built"));
        }

        public void CachePreview(PreviewCacheKeyData key) =>
            Assert.Equal(Input.CacheKey, key);

        public void InvalidatePreviewCache()
        {
        }

        public void SchedulePreviewMemoryCleanup(bool force)
        {
            _ = force;
        }

        public void SchedulePreviewMemoryCleanupForDocument(IPreviewTextDocument document)
        {
            _ = document;
            PreviewDocumentCleanupRequestCount++;
        }
    }

    private sealed class RecordingRefreshTreeHost(MainWindowViewModel viewModel) : IRefreshTreePipelineHost
    {
        public MainWindowViewModel ViewModel => viewModel;

        public string CurrentPath { get; set; } = @"C:\ProjectA";

        public Func<CancellationToken, BuildTreeSnapshotResult>? BuildTreeHandler { get; set; }

        public Func<TreeRefreshInput, BuildTreeResult, TreeNodeViewModel>? BuildViewModelHandler { get; set; }

        public int BuildTreeCount { get; private set; }

        public int ApplyCount { get; private set; }

        public TreeRefreshInput? CaptureTreeRefreshInput()
        {
            return new TreeRefreshInput(
                CurrentPath,
                "ProjectA",
                new TreeFilterOptions(
                    AllowedExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    AllowedRootFolders: new HashSet<string>(PathComparer.Default),
                    IgnoreRules: new IgnoreRules(
                        IgnoreHiddenFolders: false,
                        IgnoreHiddenFiles: false,
                        IgnoreDotFolders: false,
                        IgnoreDotFiles: false,
                        SmartIgnoredFolders: new HashSet<string>(),
                        SmartIgnoredFiles: new HashSet<string>())),
                NameFilter: null);
        }

        public void BeforeFullTreeRefresh() =>
            viewModel.StatusMetricsVisible = false;

        public bool TryBuildInteractiveFilteredTreeResult(
            string? nameFilter,
            CancellationToken cancellationToken,
            out BuildTreeResult result)
        {
            _ = nameFilter;
            cancellationToken.ThrowIfCancellationRequested();
            result = null!;
            return false;
        }

        public BuildTreeSnapshotResult BuildTree(TreeRefreshInput input, CancellationToken cancellationToken)
        {
            _ = input;
            BuildTreeCount++;
            return BuildTreeHandler?.Invoke(cancellationToken) ??
                   new BuildTreeSnapshotResult(CreateResult("root"), CreateInventorySnapshot());
        }

        public bool TryHandleRootAccessDenied(TreeRefreshInput input, BuildTreeResult result)
        {
            _ = input;
            return result.RootAccessDenied;
        }

        public TreeNodeViewModel BuildTreeViewModel(TreeRefreshInput input, BuildTreeResult result)
        {
            return BuildViewModelHandler?.Invoke(input, result) ??
                   new TreeNodeViewModel(result.Root, parent: null, icon: null)
                   {
                       DisplayName = input.DisplayName
                   };
        }

        public void ApplyTreeRefreshResult(
            TreeRefreshInput input,
            BuildTreeSnapshotResult result,
            TreeNodeViewModel root,
            bool interactiveFilter,
            bool usedInMemoryFilter,
            CancellationToken cancellationToken)
        {
            _ = result;
            _ = interactiveFilter;
            _ = usedInMemoryFilter;
            cancellationToken.ThrowIfCancellationRequested();

            // Mirrors the production host guard: a project switch can happen after the
            // background build but before UI application, and that stale tree must not win.
            if (!PathComparer.Default.Equals(CurrentPath, input.CurrentPath))
                return;

            ApplyCount++;
            viewModel.TreeNodes.Add(root);
        }

        public static BuildTreeResult CreateResult(string name)
        {
            var root = new TreeNodeDescriptor(
                name,
                Path.Combine(@"C:\ProjectA", name),
                IsDirectory: true,
                IsAccessDenied: false,
                IconKey: "folder",
                Children: []);

            return new BuildTreeResult(root, RootAccessDenied: false, HadAccessDenied: false);
        }

        public static ProjectTreeInventorySnapshot CreateInventorySnapshot()
        {
            return new ProjectTreeInventorySnapshot(
                [
                    new ProjectTreeInventoryEntry(
                        "ProjectA",
                        @"C:\ProjectA",
                        relativePath: string.Empty,
                        parentIndex: -1,
                        isDirectory: true,
                        isHidden: false,
                        length: 0)
                ],
                rootAccessDenied: false,
                hadAccessDenied: false);
        }
    }

    private sealed class StubFileContentAnalyzer : IFileContentAnalyzer
    {
        public Task<bool> IsTextFileAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<TextFileMetrics?> GetTextFileMetricsAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult<TextFileMetrics?>(null);

        public Task<TextFileContent?> TryReadAsTextAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult<TextFileContent?>(null);

        public Task<TextFileContent?> TryReadAsTextAsync(
            string path,
            long maxSizeForFullRead,
            CancellationToken cancellationToken = default)
        {
            _ = maxSizeForFullRead;
            return TryReadAsTextAsync(path, cancellationToken);
        }
    }
}
