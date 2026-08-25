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
    [InlineData("phase 10% final 75%", 75)]
    public void GitProgressStatusParser_ParsesTrailingPercent(string status, double expected)
    {
        Assert.True(GitProgressStatusParser.TryParseTrailingPercent(status, out var percent));
        Assert.Equal(expected, percent);
    }

    [Theory]
    [InlineData("42%", 42)]
    [InlineData("Receiving objects: 42% (42/100), 1.00 MiB", 42)]
    [InlineData("remote: Compressing objects: 100% (10/10), done.", 100)]
    [InlineData("Resolving deltas: 12.5% (1/8)", 12.5)]
    public void GitProgressStatusParser_ParsesStandaloneAndEmbeddedPercent(
        string status,
        double expected)
    {
        Assert.True(GitProgressStatusParser.TryParsePercent(status, out var percent));
        Assert.Equal(expected, percent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Cloning into repository...")]
    [InlineData("Receiving objects: 101%")]
    [InlineData("Receiving objects: -1%")]
    public void GitProgressStatusParser_RejectsMissingOrInvalidPercent(string status)
    {
        Assert.False(GitProgressStatusParser.TryParsePercent(status, out _));
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

    [Fact]
    public void ProjectTreeInventoryReuseScope_RejectsGitIgnoreInventoryForTrackedOnlyMode()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "reuse-scope-git-mode");
        var gitIgnoreOptions = CreateInventoryScopeOptions(
            roots: ["src"],
            useGitIgnore: true,
            useSmartIgnore: false,
            ignoreHiddenFolders: false,
            ignoreDotFolders: false);
        var trackedOnlyOptions = CreateInventoryScopeOptions(
            roots: ["src"],
            useGitIgnore: false,
            useSmartIgnore: false,
            ignoreHiddenFolders: false,
            ignoreDotFolders: false,
            useTrackedGitFilesOnly: true);
        var scope = ProjectTreeInventoryReuseScope.Create(
            rootPath,
            gitIgnoreOptions,
            supportsHiddenDotFolderVariants: true);

        Assert.False(scope.CanProject(rootPath, trackedOnlyOptions));
    }

    [Fact]
    public void ProjectTreeInventoryReuseScope_RejectsTrackedOnlyInventoryForGitIgnoreMode()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "reuse-scope-tracked-mode");
        var trackedOnlyOptions = CreateInventoryScopeOptions(
            roots: ["src"],
            useGitIgnore: false,
            useSmartIgnore: false,
            ignoreHiddenFolders: false,
            ignoreDotFolders: false,
            useTrackedGitFilesOnly: true);
        var gitIgnoreOptions = CreateInventoryScopeOptions(
            roots: ["src"],
            useGitIgnore: true,
            useSmartIgnore: false,
            ignoreHiddenFolders: false,
            ignoreDotFolders: false);
        var scope = ProjectTreeInventoryReuseScope.Create(
            rootPath,
            trackedOnlyOptions,
            supportsHiddenDotFolderVariants: true);

        Assert.True(scope.CanProject(rootPath, trackedOnlyOptions));
        Assert.False(scope.CanProject(rootPath, gitIgnoreOptions));
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

        viewModel.StatusPresentationReady = false;
        coordinator.SyncWithStatusBar();

        Assert.Equal(TaskbarProgressRecordingState.Clear, taskbar.LastState);

        viewModel.StatusPresentationReady = true;
        coordinator.SyncWithStatusBar();

        Assert.Equal(TaskbarProgressRecordingState.Progress, taskbar.LastState);
        Assert.Equal(64, taskbar.LastPercent);

        coordinator.BeginGitClone();
        coordinator.UpdateGitClone("Receiving objects: 77%");

        Assert.Equal(TaskbarProgressRecordingState.Progress, taskbar.LastState);
        Assert.Equal(77, taskbar.LastPercent);

        viewModel.StatusBusy = false;
        coordinator.SyncWithStatusBar();

        Assert.Equal(TaskbarProgressRecordingState.Progress, taskbar.LastState);
        Assert.Equal(77, taskbar.LastPercent);

        coordinator.MarkGitCloneError();
        Assert.Equal(TaskbarProgressRecordingState.Error, taskbar.LastState);

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
			ProjectLoadHostCall.ClearProjectLoadCancellation,
			ProjectLoadHostCall.ScheduleInitialProjectLoadCleanup,
            ProjectLoadHostCall.RecordRecentFolder,
			ProjectLoadHostCall.ReleaseCurrentRepositorySession
        ], host.Calls);
    }

    [Fact]
    public async Task ProjectLoadPipeline_ReleasesCachedSessionOnlyAfterSuccessfulReload()
    {
        var viewModel = CreateViewModel();
        var reloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new RecordingProjectLoadHost(viewModel)
        {
            CurrentCachedRepoPathValue = @"C:\Cache\Repo",
			ReloadHandler = async token =>
            {
                reloadStarted.SetResult();
                await releaseReload.Task.WaitAsync(token);
            }
        };
        var status = new StatusOperationCoordinator(
            viewModel,
            isBackgroundMetricsActive: () => false,
            metricsOperationTextProvider: () => viewModel.StatusOperationCalculatingData);
        using var pipeline = new ProjectLoadPipeline(host, status);

        var loadTask = pipeline.OpenFolderAsync(
            @"C:\Project",
            fromDialog: true,
            recordRecentFolder: false);
        await reloadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(loadTask.IsCompleted);
        Assert.Equal(@"C:\Cache\Repo", host.CurrentCachedRepoPath);
        Assert.DoesNotContain(ProjectLoadHostCall.ReleaseCurrentRepositorySession, host.Calls);

        releaseReload.SetResult();
        await loadTask;

        Assert.Null(host.CurrentCachedRepoPath);
        Assert.Equal(1, host.Calls.Count(call => call == ProjectLoadHostCall.ReleaseCurrentRepositorySession));
    }

    [Fact]
    public async Task ProjectLoadPipeline_AwaitsRecentProjectsPersistenceBeforeFinalizingLoad()
    {
        var viewModel = CreateViewModel();
        var persistenceStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePersistence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new RecordingProjectLoadHost(viewModel)
        {
            RecordRecentFolderHandler = async cancellationToken =>
            {
                persistenceStarted.SetResult();
                await releasePersistence.Task.WaitAsync(cancellationToken);
            }
        };
        var status = new StatusOperationCoordinator(
            viewModel,
            isBackgroundMetricsActive: () => false,
            metricsOperationTextProvider: () => viewModel.StatusOperationCalculatingData);
        using var pipeline = new ProjectLoadPipeline(host, status);

        var loadTask = pipeline.OpenFolderAsync(
            @"C:\Project",
            fromDialog: false,
            recordRecentFolder: true);
        await persistenceStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(loadTask.IsCompleted);
        Assert.True(viewModel.StatusBusy);
		Assert.Contains(ProjectLoadHostCall.ClearProjectLoadCancellation, host.Calls);
		Assert.Contains(ProjectLoadHostCall.ScheduleInitialProjectLoadCleanup, host.Calls);

        releasePersistence.SetResult();
        await loadTask;

        Assert.False(viewModel.StatusBusy);
        Assert.Contains(ProjectLoadHostCall.ClearProjectLoadCancellation, host.Calls);
        Assert.Contains(ProjectLoadHostCall.ScheduleInitialProjectLoadCleanup, host.Calls);
    }

    [Fact]
    public async Task ProjectLoadPipeline_OpenFolderAsync_CancellationAppliesFallbackWithoutSuccessSideEffects()
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
        ProjectLoadPipeline? pipeline = null;
        host.ReloadHandler = token =>
        {
            pipeline!.CancelActiveLoad();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        pipeline = new ProjectLoadPipeline(host, status);

        await pipeline.OpenFolderAsync(@"C:\Canceled", fromDialog: true, recordRecentFolder: true);
        pipeline.Dispose();

        Assert.Contains(ProjectLoadHostCall.ApplyCancellationFallback, host.Calls);
        Assert.Contains(ProjectLoadHostCall.ShowLoadCanceledToast, host.Calls);
        Assert.DoesNotContain(ProjectLoadHostCall.RecordRecentFolder, host.Calls);
        Assert.DoesNotContain(ProjectLoadHostCall.ReleaseCurrentRepositorySession, host.Calls);
        Assert.Equal(@"C:\Cache\Repo", host.CurrentCachedRepoPath);
        Assert.DoesNotContain(ProjectLoadHostCall.ScheduleInitialProjectLoadCleanup, host.Calls);
        Assert.False(viewModel.StatusBusy);
    }

	[Fact]
	public void StatusOperationCoordinator_BackgroundMetricsDoesNotReplaceExplicitSecretAnalysis()
	{
		var viewModel = CreateViewModel();
		var coordinator = new StatusOperationCoordinator(
			viewModel,
			isBackgroundMetricsActive: () => true,
			metricsOperationTextProvider: () => "Calculating data");

		var secretAnalysis = coordinator.Begin(
			"Searching for secrets",
			operationType: StatusOperationType.SecretAnalysis);
		var metrics = coordinator.Begin(
			"Calculating data",
			operationType: StatusOperationType.MetricsCalculation);

		Assert.True(coordinator.IsActive(secretAnalysis));
		Assert.False(coordinator.IsActive(metrics));
		Assert.Equal("Searching for secrets", viewModel.StatusOperationText);

		coordinator.Complete(metrics);
		Assert.True(coordinator.IsActive(secretAnalysis));
		Assert.Equal("Searching for secrets", viewModel.StatusOperationText);
	}

	[Fact]
	public async Task ProjectLoadPipeline_FailureBeforePublicationRestoresStableProject()
	{
		var viewModel = CreateViewModel();
		viewModel.IsProjectLoaded = true;
		var host = new RecordingProjectLoadHost(viewModel)
		{
			StableIdentity = @"C:\Stable",
			CurrentIdentity = @"C:\Stable",
			ReloadHandler = _ => throw new InvalidOperationException("tree build failed")
		};
		var status = new StatusOperationCoordinator(
			viewModel,
			() => false,
			() => viewModel.StatusOperationCalculatingData);
		using var pipeline = new ProjectLoadPipeline(host, status);

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			pipeline.OpenFolderAsync(@"C:\Broken", fromDialog: false, recordRecentFolder: false));

		Assert.Equal(@"C:\Stable", host.CurrentIdentity);
		Assert.Contains(ProjectLoadHostCall.ApplyCancellationFallback, host.Calls);
		Assert.DoesNotContain(ProjectLoadHostCall.ClearProjectLoadCancellation, host.Calls);
	}

	[Fact]
	public async Task ProjectLoadPipeline_FailureAfterPublicationKeepsPublishedProject()
	{
		var viewModel = CreateViewModel();
		viewModel.IsProjectLoaded = true;
		var host = new RecordingProjectLoadHost(viewModel)
		{
			StableIdentity = @"C:\Stable",
			CurrentIdentity = @"C:\Stable",
			RecordRecentFolderHandler = _ => throw new IOException("recent store failed")
		};
		var status = new StatusOperationCoordinator(
			viewModel,
			() => false,
			() => viewModel.StatusOperationCalculatingData);
		using var pipeline = new ProjectLoadPipeline(host, status);

		await Assert.ThrowsAsync<IOException>(() =>
			pipeline.OpenFolderAsync(@"C:\Published", fromDialog: false, recordRecentFolder: true));

		Assert.Equal(@"C:\Published", host.StableIdentity);
		Assert.Equal(@"C:\Published", host.CurrentIdentity);
		Assert.DoesNotContain(ProjectLoadHostCall.ApplyCancellationFallback, host.Calls);
		Assert.Contains(ProjectLoadHostCall.ClearProjectLoadCancellation, host.Calls);
	}

	[Fact]
	public async Task ProjectLoadPipeline_SupersededLoadCapturesLastStableProject()
	{
		var viewModel = CreateViewModel();
		viewModel.IsProjectLoaded = true;
		var firstReloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var reloadCount = 0;
		var host = new RecordingProjectLoadHost(viewModel)
		{
			StableIdentity = @"C:\ProjectA",
			CurrentIdentity = @"C:\ProjectA",
			ReloadHandler = async token =>
			{
				if (Interlocked.Increment(ref reloadCount) != 1)
					return;

				firstReloadStarted.SetResult();
				await Task.Delay(Timeout.InfiniteTimeSpan, token);
			}
		};
		var status = new StatusOperationCoordinator(
			viewModel,
			() => false,
			() => viewModel.StatusOperationCalculatingData);
		using var pipeline = new ProjectLoadPipeline(host, status);

		var loadB = pipeline.OpenFolderAsync(@"C:\ProjectB", fromDialog: false, recordRecentFolder: false);
		await firstReloadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
		var loadC = pipeline.OpenFolderAsync(@"C:\ProjectC", fromDialog: false, recordRecentFolder: false);
		await Task.WhenAll(loadB, loadC);

		Assert.Equal([@"C:\ProjectA", @"C:\ProjectA"], host.CapturedStableIdentities);
		Assert.Equal(@"C:\ProjectC", host.StableIdentity);
		Assert.Equal(@"C:\ProjectC", host.CurrentIdentity);
	}

	[Fact]
	public void ProjectRuntimeStateSnapshot_ClearedStateCoversEveryRuntimeField()
	{
		var expectedProperties = new[]
		{
			nameof(ProjectRuntimeStateSnapshot.HideSecretsApplied),
			nameof(ProjectRuntimeStateSnapshot.HidePrivateDataApplied),
			nameof(ProjectRuntimeStateSnapshot.CompressCodeApplied),
			nameof(ProjectRuntimeStateSnapshot.StripCommentsApplied),
			nameof(ProjectRuntimeStateSnapshot.StripBlankLinesApplied),
			nameof(ProjectRuntimeStateSnapshot.SecretRedactedCount),
			nameof(ProjectRuntimeStateSnapshot.SecretDetectedCount),
			nameof(ProjectRuntimeStateSnapshot.PrivateDataRedactedCount),
			nameof(ProjectRuntimeStateSnapshot.PrivateDataDetectedCount),
			nameof(ProjectRuntimeStateSnapshot.SecretScanState),
			nameof(ProjectRuntimeStateSnapshot.CompressionSnapshot)
		};
		var actualProperties = typeof(ProjectRuntimeStateSnapshot)
			.GetProperties(BindingFlags.Instance | BindingFlags.Public)
			.Select(static property => property.Name)
			.Order()
			.ToArray();

		Assert.Equal(expectedProperties.Order(), actualProperties);
		var cleared = ProjectRuntimeStateSnapshot.Cleared;
		Assert.False(cleared.HideSecretsApplied);
		Assert.False(cleared.HidePrivateDataApplied);
		Assert.False(cleared.CompressCodeApplied);
		Assert.False(cleared.StripCommentsApplied);
		Assert.False(cleared.StripBlankLinesApplied);
		Assert.Null(cleared.SecretRedactedCount);
		Assert.Null(cleared.SecretDetectedCount);
		Assert.Null(cleared.PrivateDataRedactedCount);
		Assert.Null(cleared.PrivateDataDetectedCount);
		Assert.Equal(DevProjex.Application.Secrets.SecretScanState.Disabled, cleared.SecretScanState);
		Assert.Null(cleared.CompressionSnapshot);
	}

	[Fact]
	public void ProjectLoadCancellationSnapshot_InternalStateChecklistIsComplete()
	{
		var expectedProperties = new[]
		{
			"SelectionCheckpoint",
			"RuntimeState",
			"TreeSelection",
			"TreeExpansion",
			"SearchQuery",
			"NameFilter",
			"PreviewSearchVisible",
			"PreviewSearchQuery"
		};
		var internalProperties = typeof(ProjectLoadCancellationSnapshot)
			.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic)
			.Where(static property => property.Name != "EqualityContract")
			.Select(static property => property.Name)
			.Order()
			.ToArray();

		Assert.Equal(expectedProperties.Order(), internalProperties);
	}

	[Fact]
	public void MetricsPipeline_StatusSnapshotIsRejectedAfterProjectSwitchAndExplicitReset()
	{
		var viewModel = CreateViewModel();
		viewModel.SelectedPreviewContentMode = PreviewContentMode.TreeAndContent;
		var currentPath = @"C:\ProjectA";
		var localization = new LocalizationService(CreateCatalog(), AppLanguage.En);
		var status = new StatusOperationCoordinator(
			viewModel,
			isBackgroundMetricsActive: () => false,
			metricsOperationTextProvider: () => viewModel.StatusOperationCalculatingData);
		using var pipeline = new MetricsPipeline(
			viewModel,
			localization,
			new StubFileContentAnalyzer(),
			new TreeExportService(),
			status,
			currentTreeProvider: () => null,
			currentPathProvider: () => currentPath,
			selectedPathsProvider: () => new HashSet<string>(PathComparer.Default),
			treeFormatProvider: () => TreeTextFormat.Ascii,
			exportPathPresentationProvider: () => null,
			boundsWidthProvider: () => 900);
		using var document = new InMemoryPreviewTextDocument("tree\ncontent");

		pipeline.UpdateStatusBarMetrics(1, 2, 3, 4, 5, 6);
		Assert.True(pipeline.TryGetCachedPreviewSelectionMetrics(
			PreviewContentMode.TreeAndContent,
			document,
			new PreviewSelectionRange(1, 0, 2, 7),
			out _));

		currentPath = @"C:\ProjectB";
		Assert.False(pipeline.TryGetCachedPreviewSelectionMetrics(
			PreviewContentMode.TreeAndContent,
			document,
			new PreviewSelectionRange(1, 0, 2, 7),
			out _));

		pipeline.ResetStatusMetricsSnapshot();
		Assert.False(pipeline.HasStatusMetricsSnapshot);
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

        await pipeline.RefreshNowAsync().Completion;

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

        await pipeline.RefreshNowAsync().Completion;

        Assert.Equal(1, host.ApplyDocumentCount);
        Assert.Equal(1, host.BuildDocumentCount);
        Assert.Equal(1, host.PreviewDocumentCleanupRequestCount);
        viewModel.PreviewDocument?.Dispose();
    }

    [Fact]
    public async Task PreviewWorkspacePipeline_ModeSwitchBuildPreparesReplacementBeforePublicationGate()
    {
        var viewModel = CreateViewModel();
        viewModel.IsProjectLoaded = true;
        viewModel.PreviewWorkspaceMode = PreviewWorkspaceMode.TreeAndPreview;
        using var currentDocument = new InMemoryPreviewTextDocument("current");
        viewModel.PreviewDocument = currentDocument;

        var buildStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBuild = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var buildCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var publicationReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new RecordingPreviewWorkspaceHost(viewModel)
        {
            IsPreviewModeSwitchInProgress = true,
            AllowCacheHit = false,
            BuildDocumentHandler = cancellationToken =>
            {
                buildStarted.TrySetResult();
                releaseBuild.Task.Wait(cancellationToken);
                var result = new PreviewBuildResult(
                    new InMemoryPreviewTextDocument("replacement"));
                buildCompleted.TrySetResult();
                return result;
            }
        };
        using var pipeline = new PreviewWorkspacePipeline(
            host,
            TimeSpan.FromMilliseconds(1));

        var refreshOperation = pipeline.RefreshNowAsync(
            allowDuringModeSwitch: true,
            publicationReady: publicationReady.Task);
        await buildStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.True(host.IsPreviewModeSwitchInProgress);
        Assert.Same(currentDocument, viewModel.PreviewDocument);
        Assert.Equal(0, host.ClearDocumentCount);
        Assert.False(refreshOperation.Completion.IsCompleted);

        releaseBuild.TrySetResult();
        await buildCompleted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Same(currentDocument, viewModel.PreviewDocument);
        Assert.Equal(0, host.ApplyDocumentCount);
        Assert.False(refreshOperation.Completion.IsCompleted);

        publicationReady.TrySetResult();
        await refreshOperation.Completion;

        Assert.NotNull(viewModel.PreviewDocument);
        Assert.NotSame(currentDocument, viewModel.PreviewDocument);
        Assert.Equal("replacement", viewModel.PreviewDocument.GetLineText(1));
        viewModel.PreviewDocument.Dispose();
        viewModel.PreviewDocument = null;
    }

    [Fact]
    public async Task PreviewWorkspacePipeline_DeferredPresentationBuildsBeforeGateWithoutChangingUi()
    {
        var viewModel = CreateViewModel();
        viewModel.IsProjectLoaded = true;
        viewModel.PreviewWorkspaceMode = PreviewWorkspaceMode.TreeAndPreview;
        var buildStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBuild = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var publicationReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new RecordingPreviewWorkspaceHost(viewModel)
        {
            AllowCacheHit = false,
            BuildDocumentHandler = cancellationToken =>
            {
                buildStarted.TrySetResult();
                releaseBuild.Task.Wait(cancellationToken);
                return new PreviewBuildResult(
                    new InMemoryPreviewTextDocument("prepared"));
            }
        };
        using var pipeline = new PreviewWorkspacePipeline(
            host,
            TimeSpan.FromMilliseconds(1));

        var refreshOperation = pipeline.RefreshNowAsync(
            publicationReady: publicationReady.Task,
            deferPresentationUntilPublication: true);
        await buildStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.False(viewModel.IsPreviewLoading);
        Assert.Null(viewModel.PreviewDocument);
        Assert.Equal(0, host.ApplyDocumentCount);

        releaseBuild.TrySetResult();
        publicationReady.TrySetResult();
        await refreshOperation.Completion;

        Assert.False(viewModel.IsPreviewLoading);
        Assert.NotNull(viewModel.PreviewDocument);
        Assert.Equal("prepared", viewModel.PreviewDocument.GetLineText(1));
        viewModel.PreviewDocument.Dispose();
        viewModel.PreviewDocument = null;
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

        await pipeline.RefreshNowAsync().Completion;
        pipeline.Dispose();

        Assert.Equal(1, host.BuildDocumentCount);
        Assert.Equal(0, host.ApplyDocumentCount);
        Assert.False(viewModel.IsPreviewLoading);
    }

    [Fact]
    public async Task PreviewWorkspacePipeline_FirstContentReadyCompletesAfterWarmupWithoutWaitingForFullBuild()
    {
        var viewModel = CreateViewModel();
        viewModel.IsProjectLoaded = true;
        viewModel.PreviewWorkspaceMode = PreviewWorkspaceMode.TreeAndPreview;

        var buildStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBuild = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new RecordingPreviewWorkspaceHost(viewModel)
        {
            WarmupSnapshot = new PreviewWarmupSnapshot("warmup", 1),
            BuildDocumentHandler = cancellationToken =>
            {
                buildStarted.TrySetResult();
                releaseBuild.Task.Wait(cancellationToken);
                return new PreviewBuildResult(
                    new InMemoryPreviewTextDocument("complete"));
            }
        };
        using var pipeline = new PreviewWorkspacePipeline(
            host,
            TimeSpan.FromMilliseconds(1));

        var refreshOperation = pipeline.RefreshNowAsync();
        await refreshOperation.FirstContentReady.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        await buildStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, host.ApplyTextCount);
        Assert.Equal("warmup", viewModel.PreviewText);
        Assert.False(refreshOperation.Completion.IsCompleted);

        releaseBuild.TrySetResult();
        await refreshOperation.Completion;

        Assert.Equal(1, host.ApplyDocumentCount);
        Assert.Equal("complete", viewModel.PreviewDocument?.GetLineText(1));
        viewModel.PreviewDocument?.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewWorkspacePipeline_CanceledBuildCannotPublishLateResult(
        bool cancelActiveBuild)
    {
        var viewModel = CreateViewModel();
        viewModel.IsProjectLoaded = true;
        viewModel.PreviewWorkspaceMode = PreviewWorkspaceMode.TreeAndPreview;

        var buildStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBuild = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new RecordingPreviewWorkspaceHost(viewModel)
        {
            BuildDocumentHandler = _ =>
            {
                buildStarted.TrySetResult();
                releaseBuild.Task.Wait(TestContext.Current.CancellationToken);
                return new PreviewBuildResult(
                    new InMemoryPreviewTextDocument("late"));
            }
        };
        using var pipeline = new PreviewWorkspacePipeline(
            host,
            TimeSpan.FromMilliseconds(1));

        var refreshOperation = pipeline.RefreshNowAsync();
        await buildStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        if (cancelActiveBuild)
            pipeline.CancelActiveBuild();
        else
            pipeline.CancelRefresh();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await refreshOperation.FirstContentReady)
            .WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
        Assert.False(refreshOperation.Completion.IsCompleted);

        releaseBuild.TrySetResult();
        await refreshOperation.Completion;

        Assert.Equal(0, host.ApplyDocumentCount);
        Assert.Null(viewModel.PreviewDocument);
        Assert.False(viewModel.IsPreviewLoading);
        Assert.True(pipeline.IsIdle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewWorkspacePipeline_CancelOrDisposeCompletesStatusBeforeNonCooperativeBuildReturns(
        bool disposePipeline)
    {
        var viewModel = CreateViewModel();
        viewModel.IsProjectLoaded = true;
        viewModel.PreviewWorkspaceMode = PreviewWorkspaceMode.TreeAndPreview;

        var buildStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBuild = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new RecordingPreviewWorkspaceHost(viewModel)
        {
            BuildDocumentHandler = _ =>
            {
                buildStarted.TrySetResult();
                releaseBuild.Task.Wait(TestContext.Current.CancellationToken);
                return new PreviewBuildResult(
                    new InMemoryPreviewTextDocument("late"));
            }
        };
        var pipeline = new PreviewWorkspacePipeline(
            host,
            TimeSpan.FromMilliseconds(1));

        try
        {
            var refreshOperation = pipeline.RefreshNowAsync();
            await buildStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);

            if (disposePipeline)
                pipeline.Dispose();
            else
                pipeline.CancelRefresh();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    async () => await refreshOperation.FirstContentReady)
                .WaitAsync(
                    TimeSpan.FromSeconds(5),
                    TestContext.Current.CancellationToken);

            Assert.Equal(1, host.CompletedPreviewBuildOperationCount);
            Assert.False(refreshOperation.Completion.IsCompleted);

            releaseBuild.TrySetResult();
            await refreshOperation.Completion;

            Assert.Equal(1, host.CompletedPreviewBuildOperationCount);
            Assert.Equal(0, host.ApplyDocumentCount);
        }
        finally
        {
            releaseBuild.TrySetResult();
            pipeline.Dispose();
        }
    }

    [Fact]
    public async Task PreviewWorkspacePipeline_ScheduledRefreshTransfersPendingFirstContentSignal()
    {
        var viewModel = CreateViewModel();
        viewModel.IsProjectLoaded = true;
        viewModel.PreviewWorkspaceMode = PreviewWorkspaceMode.TreeAndPreview;

        var firstBuildStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstBuild = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBuildCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var buildOrdinal = 0;
        var host = new RecordingPreviewWorkspaceHost(viewModel)
        {
            BuildDocumentHandler = _ =>
            {
                var ordinal = Interlocked.Increment(ref buildOrdinal);
                if (ordinal == 1)
                {
                    firstBuildStarted.TrySetResult();
                    releaseFirstBuild.Task.Wait(
                        TestContext.Current.CancellationToken);
                    return new PreviewBuildResult(
                        new InMemoryPreviewTextDocument("stale"));
                }

                secondBuildCompleted.TrySetResult();
                return new PreviewBuildResult(
                    new InMemoryPreviewTextDocument("latest"));
            }
        };
        using var pipeline = new PreviewWorkspacePipeline(
            host,
            TimeSpan.FromMilliseconds(1));

        var initialRefresh = pipeline.RefreshNowAsync();
        await firstBuildStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        pipeline.ScheduleRefresh(immediate: true);
        await secondBuildCompleted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        await initialRefresh.FirstContentReady.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal("latest", viewModel.PreviewDocument?.GetLineText(1));
        Assert.Equal(1, host.ApplyDocumentCount);
        Assert.False(initialRefresh.Completion.IsCompleted);

        releaseFirstBuild.TrySetResult();
        await initialRefresh.Completion;

        Assert.Equal(1, host.ApplyDocumentCount);
        Assert.Equal("latest", viewModel.PreviewDocument?.GetLineText(1));
        viewModel.PreviewDocument?.Dispose();
    }

    [Fact]
    public async Task PreviewWorkspacePipeline_OlderBuildCannotConsumeNewerDebouncedRefresh()
    {
        var viewModel = CreateViewModel();
        viewModel.IsProjectLoaded = true;
        viewModel.PreviewWorkspaceMode = PreviewWorkspaceMode.TreeAndPreview;

        var firstBuildStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstBuild = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var buildOrdinal = 0;
        var host = new RecordingPreviewWorkspaceHost(viewModel)
        {
            BuildDocumentHandler = cancellationToken =>
            {
                var ordinal = Interlocked.Increment(ref buildOrdinal);
                if (ordinal == 1)
                {
                    firstBuildStarted.TrySetResult();
                    releaseFirstBuild.Task.Wait(cancellationToken);
                    return new PreviewBuildResult(
                        new InMemoryPreviewTextDocument("stale"));
                }

                return new PreviewBuildResult(
                    new InMemoryPreviewTextDocument("latest"));
            }
        };
        using var pipeline = new PreviewWorkspacePipeline(
            host,
            TimeSpan.FromMinutes(1));

        var initialRefresh = pipeline.RefreshNowAsync();
        await firstBuildStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        pipeline.ScheduleRefresh();
        releaseFirstBuild.TrySetResult();
        await initialRefresh.Completion;

        Assert.True(pipeline.IsRefreshRequested);
        Assert.False(pipeline.IsIdle);
        Assert.Null(viewModel.PreviewDocument);

        await pipeline.RefreshAsync();
        await initialRefresh.FirstContentReady.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, host.BuildDocumentCount);
        Assert.Equal(1, host.ApplyDocumentCount);
        Assert.Equal("latest", viewModel.PreviewDocument?.GetLineText(1));
        Assert.False(pipeline.IsRefreshRequested);
        Assert.True(pipeline.IsIdle);
        viewModel.PreviewDocument?.Dispose();
        viewModel.PreviewDocument = null;
    }

    [Fact]
    public async Task PreviewWorkspacePipeline_HandledFailureCompletesRefreshRequest()
    {
        var viewModel = CreateViewModel();
        viewModel.IsProjectLoaded = true;
        viewModel.PreviewWorkspaceMode = PreviewWorkspaceMode.TreeAndPreview;
        var host = new RecordingPreviewWorkspaceHost(viewModel)
        {
            BuildDocumentHandler = _ =>
                throw new IOException("diagnostic failure")
        };
        using var pipeline = new PreviewWorkspacePipeline(
            host,
            TimeSpan.FromMilliseconds(1));

        await pipeline.RefreshNowAsync().Completion;

        Assert.False(pipeline.IsRefreshRequested);
        Assert.True(pipeline.IsIdle);
        Assert.False(viewModel.IsPreviewLoading);
        Assert.Equal("diagnostic failure", viewModel.PreviewText);
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
    public async Task RefreshTreePipeline_SelectionChangesDuringBuild_RejectsObsoleteTree()
    {
        var viewModel = CreateViewModel();
        var buildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new RecordingRefreshTreeHost(viewModel)
        {
            SelectionRevision = 41,
            BuildTreeHandler = token =>
            {
                buildStarted.TrySetResult();
                releaseBuild.Task.Wait(token);
                return new BuildTreeSnapshotResult(
                    RecordingRefreshTreeHost.CreateResult("obsolete"),
                    RecordingRefreshTreeHost.CreateInventorySnapshot());
            }
        };
        using var pipeline = new RefreshTreePipeline(host);

        var refreshTask = pipeline.RefreshTreeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await buildStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        host.SelectionRevision++;
        releaseBuild.SetResult();

        var outcome = await refreshTask;

        Assert.Equal(TreeRefreshOutcome.StaleInput, outcome);
        Assert.Equal(1, host.BuildTreeCount);
        Assert.Equal(0, host.ApplyCount);
        Assert.Empty(viewModel.TreeNodes);
    }

    [Fact]
    public async Task RefreshTreePipeline_InteractiveFilterUsesSnapshotWithoutFilesystemRescan()
    {
        var viewModel = CreateViewModel();
        var projectRoot = new TreeNodeDescriptor(
            "ProjectA",
            @"C:\ProjectA",
            IsDirectory: true,
            IsAccessDenied: false,
            IconKey: "folder",
            Children:
            [
                new TreeNodeDescriptor(
                    "src",
                    @"C:\ProjectA\src",
                    IsDirectory: true,
                    IsAccessDenied: false,
                    IconKey: "folder",
                    Children:
                    [
                        new TreeNodeDescriptor(
                            "keep.cs",
                            @"C:\ProjectA\src\keep.cs",
                            IsDirectory: false,
                            IsAccessDenied: false,
                            IconKey: "csharp",
                            Children: []),
                        new TreeNodeDescriptor(
                            "drop.cs",
                            @"C:\ProjectA\src\drop.cs",
                            IsDirectory: false,
                            IsAccessDenied: false,
                            IconKey: "csharp",
                            Children: [])
                    ]),
                new TreeNodeDescriptor(
                    "README.md",
                    @"C:\ProjectA\README.md",
                    IsDirectory: false,
                    IsAccessDenied: false,
                    IconKey: "markdown",
                    Children: [])
            ]);
        var host = new RecordingRefreshTreeHost(viewModel)
        {
            NameFilter = "keep",
            InteractiveFilterBaseTree = new BuildTreeResult(
                projectRoot,
                RootAccessDenied: false,
                HadAccessDenied: false)
        };
        using var pipeline = new RefreshTreePipeline(host);

        var outcome = await pipeline.RefreshTreeAsync(
            interactiveFilter: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TreeRefreshOutcome.Applied, outcome);
        Assert.Equal(0, host.BuildTreeCount);
        Assert.Equal(1, host.ApplyCount);
        Assert.Equal(1, host.BeforeInteractiveFilterRefreshCount);
        Assert.Equal(0, host.BeforeFullTreeRefreshCount);
        Assert.True(host.LastUsedInMemoryFilter);
        var filteredRoot = Assert.IsType<TreeNodeDescriptor>(host.LastAppliedResult?.Tree.Root);
        var source = Assert.Single(filteredRoot.Children);
        Assert.Equal("src", source.DisplayName);
        Assert.Equal("keep.cs", Assert.Single(source.Children).DisplayName);
        Assert.Equal(2, projectRoot.Children.Count);
        Assert.Equal(2, projectRoot.Children[0].Children.Count);
        Assert.True(host.LastAppliedInput!.PreserveCheckedPaths);
        Assert.False(host.LastAppliedInput.PreserveExpandedPaths);
    }

    [Fact]
    public async Task RefreshTreePipeline_ContinuousRefreshTransfersSelectionAndExpansion()
    {
        var host = new RecordingRefreshTreeHost(CreateViewModel());
        using var pipeline = new RefreshTreePipeline(host);

        var outcome = await pipeline.RefreshTreeAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TreeRefreshOutcome.Applied, outcome);
        Assert.True(host.LastAppliedInput!.PreserveCheckedPaths);
        Assert.True(host.LastAppliedInput.PreserveExpandedPaths);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task RefreshTreePipeline_FullRefresh_UsesRequestedStatusMetricsVisibilityPolicy(
        bool preserveStatusMetrics,
        bool expectedVisible)
    {
        var viewModel = CreateViewModel();
        viewModel.StatusMetricsVisible = true;
        var host = new RecordingRefreshTreeHost(viewModel);
        using var pipeline = new RefreshTreePipeline(host);

        var outcome = await pipeline.RefreshTreeAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            preserveStatusMetrics: preserveStatusMetrics);

        Assert.Equal(TreeRefreshOutcome.Applied, outcome);
        Assert.Equal(preserveStatusMetrics, host.LastPreserveStatusMetrics);
        Assert.Equal(expectedVisible, viewModel.StatusMetricsVisible);
    }

	[Fact]
	public async Task RefreshTreePipeline_IncompleteInventoryKeepsPublishedTreeAndReportsOnce()
	{
		var viewModel = CreateViewModel();
		var published = new TreeNodeViewModel(
			RecordingRefreshTreeHost.CreateResult("published").Root,
			parent: null,
			icon: null);
		viewModel.TreeNodes.Add(published);
		var host = new RecordingRefreshTreeHost(viewModel)
		{
			BuildTreeHandler = _ => new BuildTreeSnapshotResult(
				RecordingRefreshTreeHost.CreateResult("partial", hadScanFailure: true),
				RecordingRefreshTreeHost.CreateInventorySnapshot(hadScanFailure: true))
		};
		using var pipeline = new RefreshTreePipeline(host);

		var outcome = await pipeline.RefreshTreeAsync(
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(TreeRefreshOutcome.Skipped, outcome);
		Assert.Equal(0, host.ApplyCount);
		Assert.Equal(1, host.IncompleteScanReportCount);
		Assert.Same(published, Assert.Single(viewModel.TreeNodes));
	}

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectLoadSnapshotPipeline_ReloadAsync_BuildsTreeFromSelectionSnapshot(
        bool preserveTreeState)
    {
        var selectionSnapshot = CreateSelectionRefreshSnapshot();
        var host = new RecordingProjectLoadSnapshotHost(selectionSnapshot);
        var pipeline = new ProjectLoadSnapshotPipeline(host);

        await pipeline.ReloadAsync(
            @"C:\Project",
            preserveTreeState,
			persistentMarks: null,
            TestContext.Current.CancellationToken);

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
        Assert.Equal(preserveTreeState, host.CapturedTreeInput.PreserveCheckedPaths);
        Assert.Equal(preserveTreeState, host.CapturedTreeInput.PreserveExpandedPaths);
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

        await pipeline.ReloadAsync(
            @"C:\Project",
            preserveTreeState: false,
			persistentMarks: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [ProjectLoadSnapshotHostCall.BuildSelectionSnapshot],
            host.Calls);
        Assert.Equal(0, host.BuildTreeCount);
        Assert.Equal(0, host.ApplyCount);
    }

	[Fact]
	public async Task ProjectLoadSnapshotPipeline_IncompleteTreePublishesPartialTreeWithoutReusableInventory()
	{
		var host = new RecordingProjectLoadSnapshotHost(CreateSelectionRefreshSnapshot())
		{
			BuildTreeResult = new BuildTreeSnapshotResult(
				RecordingRefreshTreeHost.CreateResult("partial", hadScanFailure: true),
				RecordingRefreshTreeHost.CreateInventorySnapshot(hadScanFailure: true))
		};
		var pipeline = new ProjectLoadSnapshotPipeline(host);

		await pipeline.ReloadAsync(
			@"C:\Project",
			preserveTreeState: false,
			persistentMarks: null,
			TestContext.Current.CancellationToken);

		Assert.Equal(1, host.ApplyCount);
		Assert.Equal(1, host.IncompleteScanReportCount);
		Assert.Null(host.AppliedSnapshot?.TreeInventory);
		Assert.True(host.AppliedSnapshot?.TreeResult.HadScanFailure);
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

    private static ProjectLoadCancellationSnapshot CreateProjectLoadSnapshot(bool hadLoadedProjectBefore)
    {
        var viewModel = CreateViewModel();
        using var selection = CreateSelectionCoordinator(viewModel);
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
            AllExtensionsChecked: true,
            AllIgnoreChecked: true,
            HasCompleteMetricsBaseline: false,
            Extensions: [],
            IgnoreOptions: [])
		{
			SelectionCheckpoint = selection.CaptureProjectCheckpoint()
		};
    }

    private static TreeFilterOptions CreateInventoryScopeOptions(
        IReadOnlyCollection<string> roots,
        bool useGitIgnore,
        bool useSmartIgnore,
        bool ignoreHiddenFolders,
        bool ignoreDotFolders,
        bool useTrackedGitFilesOnly = false)
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
                UseTrackedGitFilesOnly = useTrackedGitFilesOnly,
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
            (_, _) => new IgnoreOptionsAvailability(
                IncludeGitIgnore: true,
                IncludeSmartIgnore: true,
                IncludeTrackedGitFilesOnly: true),
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
                ["Settings.Ignore.HideSecrets"] = "Hide secrets",
                ["Settings.Ignore.UseGitIgnore"] = "Use .gitignore",
                ["Settings.Ignore.TrackedGitFilesOnly"] = "Tracked Git files only",
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

		public List<string?> CapturedStableIdentities { get; } = [];

		public string? CurrentIdentity { get; set; }

		public string? StableIdentity { get; set; }

		public Func<CancellationToken, Task>? ReloadHandler { get; set; }

		public bool ReloadPublishes { get; set; } = true;

        public Func<CancellationToken, Task>? RecordRecentFolderHandler { get; set; }


		public void CaptureProjectLoadCancellationSnapshot()
		{
			CapturedStableIdentities.Add(StableIdentity);
			Calls.Add(ProjectLoadHostCall.CaptureCancellationSnapshot);
		}

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

		public void ClearPreviousProjectState(bool forceCompactingGc, bool preserveProjectSessions)
		{
			Assert.True(preserveProjectSessions);
            Calls.Add(forceCompactingGc
                ? ProjectLoadHostCall.ClearPreviousProjectStateWithCompactingGc
				: ProjectLoadHostCall.ClearPreviousProjectState);
		}

        public void SetProjectLoadIdentity(string path, bool fromDialog)
        {
			CurrentIdentity = path;
            Calls.Add(ProjectLoadHostCall.SetProjectLoadIdentity);
            viewModel.IsProjectLoaded = true;
            viewModel.SettingsVisible = true;
            viewModel.SearchVisible = false;
            if (fromDialog)
                viewModel.ProjectSourceType = ProjectSourceType.LocalFolder;
        }

        public void UpdateTitle() =>
            Calls.Add(ProjectLoadHostCall.UpdateTitle);

		public async Task<bool> ReloadProjectAsync(CancellationToken cancellationToken, bool applyStoredProfile)
        {
            _ = applyStoredProfile;
            Calls.Add(ProjectLoadHostCall.ReloadProject);
            if (ReloadHandler is not null)
                await ReloadHandler(cancellationToken);
			return ReloadPublishes;
        }

        public async Task RecordRecentFolderAsync(string path, CancellationToken cancellationToken)
        {
            _ = path;
            Calls.Add(ProjectLoadHostCall.RecordRecentFolder);
            if (RecordRecentFolderHandler is not null)
                await RecordRecentFolderHandler(cancellationToken);
        }

        public void ReleaseCurrentRepositorySession()
        {
            CurrentCachedRepoPathValue = null;
            Calls.Add(ProjectLoadHostCall.ReleaseCurrentRepositorySession);
        }

		public void ClearProjectLoadCancellation()
		{
			StableIdentity = CurrentIdentity;
			Calls.Add(ProjectLoadHostCall.ClearProjectLoadCancellation);
		}

        public bool TryApplyActiveProjectLoadCancellationFallback()
        {
			CurrentIdentity = StableIdentity;
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
        ReleaseCurrentRepositorySession,
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

		public int IncompleteScanReportCount { get; private set; }

		public BuildTreeSnapshotResult? BuildTreeResult { get; init; }

		public ProjectLoadSnapshot? AppliedSnapshot { get; private set; }

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
            SelectionRefreshSnapshot snapshot,
            bool preserveTreeState)
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
                NameFilter: null,
                PreserveCheckedPaths: preserveTreeState,
                PreserveExpandedPaths: preserveTreeState);
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
			return BuildTreeResult ?? new BuildTreeSnapshotResult(
                RecordingRefreshTreeHost.CreateResult("root"),
                CreateInventorySnapshot());
        }

        public bool TryHandleTreeRootAccessDenied(TreeRefreshInput input, BuildTreeResult result)
        {
            Assert.Same(CapturedTreeInput, input);
            _ = result;
            return false;
        }

		public void ReportIncompleteTreeScan() =>
			IncompleteScanReportCount++;

        public TreeNodeViewModel BuildTreeViewModel(TreeRefreshInput input, BuildTreeResult result)
        {
            Assert.Same(CapturedTreeInput, input);
            Calls.Add(ProjectLoadSnapshotHostCall.BuildTreeViewModel);
            return new TreeNodeViewModel(result.Root, parent: null, icon: null)
            {
                DisplayName = input.DisplayName
            };
        }

		public bool ApplyProjectLoadSnapshot(
            ProjectLoadSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Same(selectionSnapshot, snapshot.SelectionSnapshot);
            Assert.Same(CapturedTreeInput, snapshot.TreeInput);
			if (!snapshot.TreeResult.HadScanFailure)
				Assert.NotNull(snapshot.TreeInventory);
            Calls.Add(ProjectLoadSnapshotHostCall.ApplySnapshot);
			AppliedSnapshot = snapshot;
            ApplyCount++;
			return true;
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
        private readonly object _statusOperationSync = new();
        private readonly HashSet<long> _completedPreviewBuildOperations = [];
        private long _previewBuildOperationSequence;

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

        public PreviewWarmupSnapshot? WarmupSnapshot { get; set; }

        public bool AllowCacheHit { get; set; } = true;

        public int ApplyDocumentCount { get; private set; }

        public int ApplyTextCount { get; private set; }

        public int BuildDocumentCount { get; private set; }

        public int PreviewDocumentCleanupRequestCount { get; private set; }

        public int ClearDocumentCount { get; private set; }

        public int CompletedPreviewBuildOperationCount
        {
            get
            {
                lock (_statusOperationSync)
                    return _completedPreviewBuildOperations.Count;
            }
        }

        public bool EnsurePreviewTreeReady() => true;

		public string ResolvePreviewErrorMessage(Exception exception) => exception.Message;

        public void ApplyPreviewNoDataText() =>
            viewModel.PreviewText = "No data";

        public long BeginPreviewBuildOperation(CancellationTokenSource previewCts)
        {
            _ = previewCts;
            return Interlocked.Increment(
                ref _previewBuildOperationSequence);
        }

        public void CompletePreviewBuildOperation(long operationId)
        {
            lock (_statusOperationSync)
            {
                Assert.True(
                    _completedPreviewBuildOperations.Add(operationId),
                    $"Preview build operation {operationId} completed more than once.");
            }
        }

        public PreviewRefreshInput CapturePreviewRefreshInput() => Input;

        public bool IsCurrentPreviewCacheHit(PreviewCacheKeyData key) =>
            AllowCacheHit &&
            key == Input.CacheKey &&
            viewModel.PreviewDocument is not null;

        public IPreviewTextDocument? CurrentPreviewDocument => viewModel.PreviewDocument;

        public void ApplyPreviewDocument(IPreviewTextDocument document)
        {
            ApplyDocumentCount++;
            viewModel.PreviewDocument = document;
        }

        public void ApplyPreviewText(string text)
        {
            ApplyTextCount++;
            viewModel.PreviewText = text;
        }

        public void ApplyPreviewText(string text, int lineCount)
        {
            ApplyTextCount++;
            viewModel.PreviewText = text;
            viewModel.PreviewLineCount = lineCount;
        }

        public void ClearPreviewDocument()
        {
            ClearDocumentCount++;
            viewModel.PreviewDocument = null;
        }

        public Task<PreviewWarmupSnapshot?> TryBuildPreviewWarmupSnapshotAsync(
            PreviewRefreshInput input,
            CancellationToken cancellationToken)
        {
            _ = input;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(WarmupSnapshot);
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

        public void SchedulePreviewMemoryCleanup()
        {
        }

        public void SchedulePreviewRebuildMemoryCleanup()
        {
            PreviewDocumentCleanupRequestCount++;
        }
    }

    private sealed class RecordingRefreshTreeHost(MainWindowViewModel viewModel) : IRefreshTreePipelineHost
    {
        public MainWindowViewModel ViewModel => viewModel;

        public string CurrentPath { get; set; } = @"C:\ProjectA";

        public long SelectionRevision { get; set; }

        public string? NameFilter { get; set; }

        public BuildTreeResult? InteractiveFilterBaseTree { get; set; }

        public Func<CancellationToken, BuildTreeSnapshotResult>? BuildTreeHandler { get; set; }

        public Func<TreeRefreshInput, BuildTreeResult, TreeNodeViewModel>? BuildViewModelHandler { get; set; }

        public int BuildTreeCount { get; private set; }

        public int ApplyCount { get; private set; }

        public int BeforeFullTreeRefreshCount { get; private set; }

        public bool LastPreserveStatusMetrics { get; private set; }

        public int BeforeInteractiveFilterRefreshCount { get; private set; }

		public int IncompleteScanReportCount { get; private set; }

        public bool LastUsedInMemoryFilter { get; private set; }

        public BuildTreeSnapshotResult? LastAppliedResult { get; private set; }

        public TreeRefreshInput? LastAppliedInput { get; private set; }

        public TreeRefreshInput? CaptureTreeRefreshInput(bool preserveCheckedPaths)
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
                NameFilter: NameFilter,
                SelectionRevision: SelectionRevision,
                InteractiveFilterBaseTree: InteractiveFilterBaseTree);
        }

        public void BeforeFullTreeRefresh(bool preserveStatusMetrics)
        {
            BeforeFullTreeRefreshCount++;
            LastPreserveStatusMetrics = preserveStatusMetrics;
            if (!preserveStatusMetrics)
                viewModel.StatusMetricsVisible = false;
        }

        public void BeforeInteractiveFilterRefresh() =>
            BeforeInteractiveFilterRefreshCount++;

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

		public void ReportIncompleteTreeScan() =>
			IncompleteScanReportCount++;

        public TreeNodeViewModel BuildTreeViewModel(TreeRefreshInput input, BuildTreeResult result)
        {
            return BuildViewModelHandler?.Invoke(input, result) ??
                   new TreeNodeViewModel(result.Root, parent: null, icon: null)
                   {
                       DisplayName = input.DisplayName
            };
        }

        public bool IsTreeRefreshInputCurrent(TreeRefreshInput input) =>
            PathComparer.Default.Equals(CurrentPath, input.CurrentPath) &&
            input.SelectionRevision == SelectionRevision;

        public void ApplyTreeRefreshResult(
            TreeRefreshInput input,
            BuildTreeSnapshotResult result,
            TreeNodeViewModel root,
            bool interactiveFilter,
            bool usedInMemoryFilter,
            MemoryCleanupReason? postLoadCleanupReason,
            CancellationToken cancellationToken)
        {
            _ = interactiveFilter;
            cancellationToken.ThrowIfCancellationRequested();

            // Mirrors the production host guard: a project switch can happen after the
            // background build but before UI application, and that stale tree must not win.
            if (!PathComparer.Default.Equals(CurrentPath, input.CurrentPath))
                return;

            ApplyCount++;
            LastAppliedInput = input;
            LastAppliedResult = result;
            LastUsedInMemoryFilter = usedInMemoryFilter;
            viewModel.TreeNodes.Add(root);
        }

        public static BuildTreeResult CreateResult(string name, bool hadScanFailure = false)
        {
            var root = new TreeNodeDescriptor(
                name,
                Path.Combine(@"C:\ProjectA", name),
                IsDirectory: true,
                IsAccessDenied: false,
                IconKey: "folder",
                Children: []);

            return new BuildTreeResult(
				root,
				RootAccessDenied: false,
				HadAccessDenied: false,
				HadScanFailure: hadScanFailure);
        }

        public static ProjectTreeInventorySnapshot CreateInventorySnapshot(bool hadScanFailure = false)
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
				hadAccessDenied: false,
				hadScanFailure: hadScanFailure);
        }
    }

    private sealed class StubFileContentAnalyzer : IFileContentAnalyzer
    {
        public ValueTask<bool> IsTextFileAsync(string path, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(true);

        public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
            string path,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<TextFileMetrics?>(null);

        public ValueTask<TextFileContent?> TryReadAsTextAsync(
            string path,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<TextFileContent?>(null);

        public ValueTask<TextFileContent?> TryReadAsTextAsync(
            string path,
            long maxSizeForFullRead,
            CancellationToken cancellationToken = default)
        {
            _ = maxSizeForFullRead;
            return TryReadAsTextAsync(path, cancellationToken);
        }
    }
}
