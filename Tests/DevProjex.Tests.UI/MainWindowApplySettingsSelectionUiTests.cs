using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DevProjex.Application.Services;
using DevProjex.Application.UseCases;
using DevProjex.Infrastructure.FileSystem;
using DevProjex.Infrastructure.ResourceStore;
using DevProjex.Kernel.Abstractions;

namespace DevProjex.Tests.UI;

[Collection("AvaloniaUI")]
public sealed class MainWindowApplySettingsSelectionUiTests
{
    [AvaloniaFact]
    public async Task StructuralApply_PreservesManualSubsetWithoutRetainingOldTree()
    {
        using var project = UiTestProject.CreateWithDynamicIgnoreEntries();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
        try
        {
            await UiTestDriver.WaitForInitialMetricsBaselineAsync(window);
            var selectedPath = Path.Combine(project.RootPath, "src");
            var oldRoot = SelectOnlyPathAndCaptureRoot(window, selectedPath);

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.EmptyFiles);
            await UiTestDriver.ClickApplySettingsAsync(window);

            var selectedPaths = UiTestDriver.GetCheckedTreePaths(window);
            Assert.Single(selectedPaths);
            Assert.Contains(selectedPath, selectedPaths);
            await AssertEventuallyCollectedAsync(oldRoot);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task StructuralApply_HiddenExtensionReportsExactLossAndKeepsSurvivors()
    {
        using var project = UiTestProject.CreateWithDynamicIgnoreEntries();
        var firstDisappearingPath = Path.Combine(project.RootPath, "src", "selected.first.tree-state");
        var secondDisappearingPath = Path.Combine(project.RootPath, "docs", "selected.second.tree-state");
        File.WriteAllText(firstDisappearingPath, "first\n");
        File.WriteAllText(secondDisappearingPath, "second\n");
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
        var observedToastMessages = new ConcurrentQueue<string>();
        var toastItems = UiTestDriver.GetToastService(window).Items;
        System.Collections.Specialized.NotifyCollectionChangedEventHandler toastChanged = (_, args) =>
        {
            if (args.NewItems is null)
                return;

            foreach (var toast in args.NewItems.OfType<ToastMessageViewModel>())
                observedToastMessages.Enqueue(toast.Message);
        };
        toastItems.CollectionChanged += toastChanged;
        try
        {
            await UiTestDriver.WaitForInitialMetricsBaselineAsync(window);
            var survivingPath = Path.Combine(project.RootPath, "README.md");
            SelectOnlyPaths(
                window,
                survivingPath,
                firstDisappearingPath,
                secondDisappearingPath);

            await UiTestDriver.ClickExtensionCheckBoxAsync(window, ".tree-state");
            await UiTestDriver.ClickApplySettingsAsync(window);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => observedToastMessages.Contains(
                    "Checked items hidden by the current settings: 2"),
                "exact structural selection-loss toast");

            var selectedPaths = UiTestDriver.GetCheckedTreePaths(window);
            Assert.Single(selectedPaths);
            Assert.Contains(survivingPath, selectedPaths);
            Assert.DoesNotContain(firstDisappearingPath, selectedPaths);
            Assert.DoesNotContain(secondDisappearingPath, selectedPaths);
            Assert.Contains(
                "Checked items hidden by the current settings: 2",
                observedToastMessages);

            await UiTestDriver.ClickExtensionCheckBoxAsync(window, ".tree-state");
            await UiTestDriver.ClickApplySettingsAsync(window);

            Assert.False(FindNodeByPath(window, firstDisappearingPath)!.IsChecked);
            Assert.False(FindNodeByPath(window, secondDisappearingPath)!.IsChecked);
            Assert.Equal([survivingPath], UiTestDriver.GetCheckedTreePaths(window));
        }
        finally
        {
            toastItems.CollectionChanged -= toastChanged;
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task StructuralApply_EmptySelectionKeepsSelectAllSemantics()
    {
        using var project = UiTestProject.CreateWithDynamicIgnoreEntries();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
        try
        {
            await UiTestDriver.WaitForInitialMetricsBaselineAsync(window);
            Assert.Empty(UiTestDriver.GetCheckedTreePaths(window));

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.EmptyFiles);
            await UiTestDriver.ClickApplySettingsAsync(window);

            Assert.Empty(UiTestDriver.GetCheckedTreePaths(window));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task StructuralApply_CheckedRootRemainsChecked()
    {
        using var project = UiTestProject.CreateWithDynamicIgnoreEntries();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
        try
        {
            await UiTestDriver.WaitForInitialMetricsBaselineAsync(window);
            var root = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
            root.IsChecked = true;

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.EmptyFiles);
            await UiTestDriver.ClickApplySettingsAsync(window);

            var refreshedRoot = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
            Assert.True(refreshedRoot.IsChecked);
            Assert.Equal([project.RootPath], UiTestDriver.GetCheckedTreePaths(window));
            Assert.All(refreshedRoot.Flatten(), static node => Assert.True(node.IsChecked));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task StructuralApply_PreservesExpansionAndPreviewWithoutRealizingClosedBranches()
    {
        using var project = UiTestProject.CreateWithDynamicIgnoreEntries();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
        try
        {
            await UiTestDriver.WaitForInitialMetricsBaselineAsync(window);
            var root = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
            root.IsChecked = false;
            FindRequiredDirectChild(root, "README.md").IsChecked = true;
            FindRequiredDirectChild(root, "docs").IsChecked = true;
            var source = FindRequiredDirectChild(root, "src");
            var configs = FindRequiredDirectChild(root, "configs");
            source.IsExpanded = true;
            var appCore = FindRequiredDirectChild(source, "AppCore");
            appCore.IsExpanded = true;
            Assert.False(configs.AreChildrenRealized);

            await UiTestDriver.OpenPreviewAsync(window);
            var previewBefore = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.EmptyFolders);
            await UiTestDriver.ClickApplySettingsAsync(window);
            await UiTestDriver.WaitForPreviewReadyAsync(window);

            var refreshedRoot = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
            var refreshedSource = FindRequiredDirectChild(refreshedRoot, "src");
            var refreshedConfigs = FindRequiredDirectChild(refreshedRoot, "configs");
            Assert.True(refreshedSource.IsExpanded);
            Assert.True(FindRequiredDirectChild(refreshedSource, "AppCore").IsExpanded);
            Assert.False(refreshedConfigs.IsExpanded);
            Assert.False(refreshedConfigs.AreChildrenRealized);
            Assert.Equal(previewBefore, UiTestDriver.ComputeCurrentPreviewCopyPayload(window));
            Assert.DoesNotContain(
                UiTestDriver.GetToastService(window).Items,
                toast => toast.Message.StartsWith(
                    "Checked items hidden by the current settings:",
                    StringComparison.Ordinal));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task StructuralApply_WhileFilterActive_DefersExactLossToastUntilFilterCloses()
    {
        using var project = UiTestProject.CreateWithDynamicIgnoreEntries();
        var disappearingPath = Path.Combine(project.RootPath, "src", "selected.tree-state");
        File.WriteAllText(disappearingPath, "selected\n");
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
        try
        {
            await UiTestDriver.WaitForInitialMetricsBaselineAsync(window);
            var survivingPath = Path.Combine(project.RootPath, "README.md");
            SelectOnlyPaths(window, survivingPath, disappearingPath);

            await UiTestDriver.OpenFilterAsync(window);
            var filterBar = UiTestDriver.GetRequiredControl<FilterBarView>(window, "FilterBar");
            await UiTestDriver.EnterTextAsync(
                window,
                Assert.IsType<TextBox>(filterBar.FilterBoxControl),
                "selected.tree-state");
            await UiTestDriver.WaitForFilterAppliedAsync(window, "selected.tree-state");
            Assert.True(FindNodeByPath(window, disappearingPath)!.IsChecked);
            var filterSelectionSnapshot = GetInteractiveFilterSelectionSnapshot(window);
            Assert.NotNull(filterSelectionSnapshot);

            await UiTestDriver.ClickExtensionCheckBoxAsync(window, ".tree-state");
            await UiTestDriver.ClickApplySettingsAsync(window);
            Assert.Same(filterSelectionSnapshot, GetInteractiveFilterSelectionSnapshot(window));

            Assert.DoesNotContain(
                UiTestDriver.GetToastService(window).Items,
                toast => toast.Message.StartsWith(
                    "Checked items hidden by the current settings:",
                    StringComparison.Ordinal));

            await UiTestDriver.PressKeyAsync(window, Key.Escape);
            Assert.False(UiTestDriver.GetViewModel(window).FilterVisible);
            await GetSearchFilterController(window).CloseFilterAsync();
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => UiTestDriver.GetToastService(window).Items.Any(
                    toast => string.Equals(
                        toast.Message,
                        "Checked items hidden by the current settings: 1",
                        StringComparison.Ordinal)),
                "structural selection loss toast after closing the filter");

            Assert.Equal([survivingPath], UiTestDriver.GetCheckedTreePaths(window));
            Assert.Null(GetInteractiveFilterSelectionSnapshot(window));
            Assert.Contains(
                UiTestDriver.GetToastService(window).Items,
                toast => string.Equals(
                    toast.Message,
                    "Checked items hidden by the current settings: 1",
                    StringComparison.Ordinal));

            await UiTestDriver.ClickExtensionCheckBoxAsync(window, ".tree-state");
            await UiTestDriver.ClickApplySettingsAsync(window);

            Assert.False(FindNodeByPath(window, disappearingPath)!.IsChecked);
            Assert.Equal([survivingPath], UiTestDriver.GetCheckedTreePaths(window));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task StructuralApply_RealTreeCheckboxSelectionSurvivesGraphReplacement()
    {
        using var project = UiTestProject.CreateWithDynamicIgnoreEntries();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
        try
        {
            await UiTestDriver.WaitForInitialMetricsBaselineAsync(window);
            var oldRoot = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
            oldRoot.IsExpanded = true;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
            var sourcePath = Path.Combine(project.RootPath, "src");
            var sourceCheckBox = await UiTestDriver.WaitForTreeNodeCheckBoxAsync(window, "src");
            await UiTestDriver.ClickAsync(window, sourceCheckBox);
            Assert.True(FindNodeByPath(window, sourcePath)!.IsChecked);

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.EmptyFolders);
            await UiTestDriver.ClickApplySettingsAsync(window);

            Assert.NotSame(oldRoot, Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes));
            Assert.True(FindNodeByPath(window, sourcePath)!.IsChecked);
            Assert.Equal([sourcePath], UiTestDriver.GetCheckedTreePaths(window));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task RefreshProject_PreservesSelectionAndExpansion()
    {
        using var project = UiTestProject.CreateDefault();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
        try
        {
            var sourcePath = Path.Combine(project.RootPath, "src");
            var oldRoot = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
            var source = FindRequiredDirectChild(oldRoot, "src");
            source.IsChecked = true;
            source.IsExpanded = true;
            FindRequiredDirectChild(source, "AppCore").IsExpanded = true;

            await UiTestDriver.RefreshProjectAsync(window);

            var refreshedRoot = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
            Assert.NotSame(oldRoot, refreshedRoot);
            var refreshedSource = FindRequiredDirectChild(refreshedRoot, "src");
            Assert.True(refreshedSource.IsChecked);
            Assert.True(refreshedSource.IsExpanded);
            Assert.True(FindRequiredDirectChild(refreshedSource, "AppCore").IsExpanded);
            Assert.Equal([sourcePath], UiTestDriver.GetCheckedTreePaths(window));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task RefreshProject_SelectionChangedDuringBuildUsesLatestUiState()
    {
        using var project = UiTestProject.CreateWithDynamicIgnoreEntries();
        var blockingTreeBuilder = new BlockingTreeBuilder();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(
            project,
            configureServices: services => services with
            {
                BuildTreeUseCase = new BuildTreeUseCase(
                    blockingTreeBuilder,
                    new TreeNodePresentationService(
                        services.Localization,
                        new IconMapper()))
            });
        try
        {
            var root = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
            root.IsChecked = false;
            var docs = FindRequiredDirectChild(root, "docs");
            var source = FindRequiredDirectChild(root, "src");
            docs.IsChecked = true;

            blockingTreeBuilder.Arm();
            var refreshTask = UiTestDriver.RefreshProjectAsync(window);
            await blockingTreeBuilder.BuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            docs.IsChecked = false;
            source.IsChecked = true;
            source.IsExpanded = true;
            blockingTreeBuilder.Release();
            await refreshTask.WaitAsync(TimeSpan.FromSeconds(40));

            var refreshedRoot = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
            var refreshedSource = FindRequiredDirectChild(refreshedRoot, "src");
            Assert.True(refreshedSource.IsChecked);
            Assert.True(refreshedSource.IsExpanded);
            Assert.Equal([refreshedSource.FullPath], UiTestDriver.GetCheckedTreePaths(window));
        }
        finally
        {
            blockingTreeBuilder.Release();
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task GitPull_PreservesCheckedFolderExpansionAndSelectsNewFiles()
    {
        using var project = UiTestProject.CreateDefault();
        var git = new MutatingGitRepositoryService(project.RootPath)
        {
            PullMutation = () => File.WriteAllText(
                Path.Combine(project.RootPath, "src", "pulled.cs"),
                "internal sealed class Pulled {}\n")
        };
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(
            project,
            configureServices: services => services with { GitRepositoryService = git },
            projectSourceType: ProjectSourceType.GitClone,
            managedClonePath: project.RootPath,
            repositoryUrl: "https://example.test/repository.git");
        try
        {
            var root = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
            root.IsChecked = false;
            var source = FindRequiredDirectChild(root, "src");
            var sourcePath = source.FullPath;
            source.IsChecked = true;
            source.IsExpanded = true;

            await InvokePrivateTaskAsync(window, "GetGitUpdatesAsync");
            await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);

            var refreshedRoot = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
            var refreshedSource = FindRequiredDirectChild(refreshedRoot, "src");
            Assert.True(refreshedSource.IsChecked);
            Assert.True(refreshedSource.IsExpanded);
            var pulledFile = Assert.Single(
                refreshedSource.Children,
                node => string.Equals(node.DisplayName, "pulled.cs", StringComparison.Ordinal));
            Assert.True(pulledFile.IsChecked);
            Assert.Equal([sourcePath], UiTestDriver.GetCheckedTreePaths(window));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task GitBranchSwitch_PreservesSurvivorsAndReportsDisappearingSelection()
    {
        using var project = UiTestProject.CreateDefault();
        var disappearingPath = Path.Combine(project.RootPath, "docs", "app-preview-notes.md");
        var git = new MutatingGitRepositoryService(project.RootPath)
        {
            SwitchMutation = () => File.Delete(disappearingPath)
        };
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(
            project,
            configureServices: services => services with { GitRepositoryService = git },
            projectSourceType: ProjectSourceType.GitClone,
            managedClonePath: project.RootPath,
            repositoryUrl: "https://example.test/repository.git");
        try
        {
            var sourcePath = Path.Combine(project.RootPath, "src");
            SelectOnlyPaths(window, sourcePath, disappearingPath);
            var source = FindNodeByPath(window, sourcePath)!;
            source.IsExpanded = true;

            InvokePrivateAsyncVoid(window, "OnGitBranchSwitch", window, "feature");
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => UiTestDriver.GetToastService(window).Items.Any(
                    toast => string.Equals(
                        toast.Message,
                        "Checked items hidden by the current settings: 1",
                        StringComparison.Ordinal)),
                "git branch switch selection-loss toast",
                timeout: TimeSpan.FromSeconds(30));

            Assert.Equal([sourcePath], UiTestDriver.GetCheckedTreePaths(window));
            Assert.True(FindNodeByPath(window, sourcePath)!.IsExpanded);
            Assert.Contains(
                UiTestDriver.GetToastService(window).Items,
                toast => string.Equals(
                    toast.Message,
                    "Checked items hidden by the current settings: 1",
                    StringComparison.Ordinal));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ProjectSwitch_DoesNotTransferSelectionOrExpansion()
    {
        using var firstProject = UiTestProject.CreateDefault();
        using var secondProject = UiTestProject.CreateDefault();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(firstProject);
        try
        {
            var firstRoot = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
            firstRoot.IsChecked = true;
            FindRequiredDirectChild(firstRoot, "src").IsExpanded = true;

            await UiTestDriver.OpenFolderAsync(window, secondProject.RootPath);

            var secondRoot = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
            Assert.True(PathComparer.Default.Equals(secondProject.RootPath, secondRoot.FullPath));
            Assert.False(secondRoot.IsChecked);
            Assert.Empty(UiTestDriver.GetCheckedTreePaths(window));
            Assert.False(FindRequiredDirectChild(secondRoot, "src").IsExpanded);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

	[AvaloniaFact]
	public async Task ProjectSwitch_LoadsContentTransformationStateFromTheTargetProfile()
	{
		using var firstProject = UiTestProject.CreateWithSecretRedactionWorkspace();
		using var secondProject = UiTestProject.CreateDefault();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(firstProject);
		try
		{
			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HideSecrets);
			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HidePrivateData);
			await UiTestDriver.ClickApplySettingsAsync(window);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => UiTestDriver.GetAppliedContentRedactionState(window) == (true, true),
				"both redaction transformations to become applied in the first project");
			var profileStore = new DevProjex.Infrastructure.ProjectProfiles.ProjectProfileStore(
				() => UiTestDriver.GetWindowAppDataPath(window));
			Assert.Equal(
				ProjectProfileLookupStatus.Missing,
				profileStore.LookupProfile(secondProject.RootPath, TimeSpan.FromSeconds(1)).Status);

			await UiTestDriver.OpenFolderAsync(window, secondProject.RootPath);
			Assert.Equal(
				ProjectProfileLookupStatus.Missing,
				profileStore.LookupProfile(secondProject.RootPath, TimeSpan.FromSeconds(1)).Status);

			await UiTestDriver.WaitForIgnoreOptionStateAsync(
				window,
				IgnoreOptionId.HideSecrets,
				visible: true,
				isChecked: false);
			await UiTestDriver.WaitForIgnoreOptionStateAsync(
				window,
				IgnoreOptionId.HidePrivateData,
				visible: true,
				isChecked: false);
			Assert.Equal((false, false), UiTestDriver.GetAppliedContentRedactionState(window));
			Assert.False(UiTestDriver.GetViewModel(window).HasPendingFilterSettingsChanges);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

    [AvaloniaFact]
    public async Task StructuralApply_SelectionChangedDuringBuildUsesLatestUiState()
    {
        using var project = UiTestProject.CreateWithDynamicIgnoreEntries();
        var blockingTreeBuilder = new BlockingTreeBuilder();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(
            project,
            configureServices: services => services with
            {
                BuildTreeUseCase = new BuildTreeUseCase(
                    blockingTreeBuilder,
                    new TreeNodePresentationService(
                        services.Localization,
                        new IconMapper()))
            });
        try
        {
            var root = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
            root.IsChecked = false;
            FindRequiredDirectChild(root, "docs").IsChecked = true;
            var source = FindRequiredDirectChild(root, "src");
            Assert.False(source.IsExpanded);
            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.EmptyFolders);
            await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);

            blockingTreeBuilder.Arm();
            var previousApplyTask = window.LatestApplySettingsTask;
            await UiTestDriver.RaiseButtonClickAsync(UiTestDriver.GetRequiredApplySettingsButton(window));
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => !ReferenceEquals(previousApplyTask, window.LatestApplySettingsTask),
                "Apply to start its background tree build");
            await blockingTreeBuilder.BuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            root.IsChecked = true;
            source.IsExpanded = true;
            blockingTreeBuilder.Release();
            await window.LatestApplySettingsTask.WaitAsync(TimeSpan.FromSeconds(30));
            await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);

            var refreshedRoot = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
            Assert.True(refreshedRoot.IsChecked);
            Assert.True(FindRequiredDirectChild(refreshedRoot, "src").IsExpanded);
            Assert.Equal([project.RootPath], UiTestDriver.GetCheckedTreePaths(window));
        }
        finally
        {
            blockingTreeBuilder.Release();
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task StructuralApply_RepeatedRefreshCapturesStateFromLatestPublishedTree()
    {
        using var project = UiTestProject.CreateWithDynamicIgnoreEntries();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
        try
        {
            var root = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
            root.IsChecked = false;
            var docs = FindRequiredDirectChild(root, "docs");
            docs.IsChecked = true;
            docs.IsExpanded = true;

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.EmptyFolders);
            await UiTestDriver.ClickApplySettingsAsync(window);

            var firstRefreshRoot = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
            firstRefreshRoot.IsChecked = false;
            FindRequiredDirectChild(firstRefreshRoot, "docs").IsExpanded = false;
            var source = FindRequiredDirectChild(firstRefreshRoot, "src");
            source.IsChecked = true;
            source.IsExpanded = true;

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.EmptyFolders);
            await UiTestDriver.ClickApplySettingsAsync(window);

            var secondRefreshRoot = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
            var restoredSource = FindRequiredDirectChild(secondRefreshRoot, "src");
            Assert.Equal([restoredSource.FullPath], UiTestDriver.GetCheckedTreePaths(window));
            Assert.True(restoredSource.IsExpanded);
            Assert.False(FindRequiredDirectChild(secondRefreshRoot, "docs").IsExpanded);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task StructuralApply_CanceledDuringBuildLeavesPublishedTreeStateUntouched()
    {
        using var project = UiTestProject.CreateWithDynamicIgnoreEntries();
        var blockingTreeBuilder = new BlockingTreeBuilder();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(
            project,
            configureServices: services => services with
            {
                BuildTreeUseCase = new BuildTreeUseCase(
                    blockingTreeBuilder,
                    new TreeNodePresentationService(
                        services.Localization,
                        new IconMapper()))
            });
        try
        {
            var root = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
            root.IsChecked = false;
            var source = FindRequiredDirectChild(root, "src");
            source.IsChecked = true;
            source.IsExpanded = true;
            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.EmptyFolders);
            await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);

            blockingTreeBuilder.Arm();
            var previousApplyTask = window.LatestApplySettingsTask;
            await UiTestDriver.RaiseButtonClickAsync(UiTestDriver.GetRequiredApplySettingsButton(window));
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => !ReferenceEquals(previousApplyTask, window.LatestApplySettingsTask),
                "Apply to start its background tree build");
            await blockingTreeBuilder.BuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            GetRequiredApplySettingsCancellationSource(window).Cancel();
            blockingTreeBuilder.Release();
            await window.LatestApplySettingsTask.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Same(root, Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes));
            Assert.True(source.IsChecked);
            Assert.True(source.IsExpanded);
        }
        finally
        {
            blockingTreeBuilder.Release();
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    private static CancellationTokenSource GetRequiredApplySettingsCancellationSource(MainWindow window)
    {
        var field = typeof(MainWindow).GetField(
            "_applySettingsCts",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return Assert.IsType<CancellationTokenSource>(field?.GetValue(window));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference SelectOnlyPathAndCaptureRoot(MainWindow window, string selectedPath)
    {
        var root = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
        root.IsChecked = false;
        var selectedNode = Assert.Single(
            root.Flatten(),
            node => PathComparer.Default.Equals(node.FullPath, selectedPath));
        selectedNode.IsChecked = true;
        return new WeakReference(root);
    }

    private static void SelectOnlyPaths(MainWindow window, params string[] selectedPaths)
    {
        var root = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
        root.IsChecked = false;
        var nodesByPath = root
            .Flatten()
            .ToDictionary(static node => node.FullPath, PathComparer.Default);
        foreach (var path in selectedPaths)
            nodesByPath[path].IsChecked = true;
    }

    private static TreeNodeViewModel? FindNodeByPath(MainWindow window, string path) =>
        Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes)
            .Flatten()
            .FirstOrDefault(node => PathComparer.Default.Equals(node.FullPath, path));

    private static TreeNodeViewModel FindRequiredDirectChild(
        TreeNodeViewModel parent,
        string displayName) =>
        Assert.Single(
            parent.Children,
            node => string.Equals(node.DisplayName, displayName, StringComparison.Ordinal));

    private static DevProjex.Avalonia.Coordinators.SearchFilterInteractionController
        GetSearchFilterController(MainWindow window)
    {
        var field = typeof(MainWindow).GetField(
            "_searchFilterController",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return Assert.IsType<DevProjex.Avalonia.Coordinators.SearchFilterInteractionController>(
            field?.GetValue(window));
    }

    private static ProjectTreeSelectionSnapshot? GetInteractiveFilterSelectionSnapshot(
        MainWindow window)
    {
        var field = typeof(MainWindow).GetField(
            "_interactiveFilterSelectionSnapshot",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var snapshot = field?.GetValue(window);
        return snapshot is null
            ? null
            : Assert.IsType<ProjectTreeSelectionSnapshot>(snapshot);
    }

    private static async Task InvokePrivateTaskAsync(MainWindow window, string methodName)
    {
        var method = typeof(MainWindow).GetMethod(
            methodName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        Task? task = null;
        await window.Dispatcher.InvokeAsync(
            () =>
            {
                task = Assert.IsAssignableFrom<Task>(method!.Invoke(window, []));
            },
            DispatcherPriority.Normal);
        await Assert.IsAssignableFrom<Task>(task);
    }

    private static void InvokePrivateAsyncVoid(
        MainWindow window,
        string methodName,
        params object?[] arguments)
    {
        var method = typeof(MainWindow).GetMethod(
            methodName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(window, arguments);
    }

    private static async Task AssertEventuallyCollectedAsync(WeakReference reference)
    {
        for (var attempt = 0; attempt < 12 && reference.IsAlive; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
        }

        Assert.False(reference.IsAlive);
    }

    private sealed class BlockingTreeBuilder :
        ITreeBuilder,
        IProjectTreeInventoryBuilder,
        IProjectTreeCompositeInventoryBuilder
    {
        private readonly TreeBuilder _inner = new();
        private readonly ManualResetEventSlim _release = new(initialState: true);
        private int _armed;

        public TaskCompletionSource BuildStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm()
        {
            _release.Reset();
            Volatile.Write(ref _armed, 1);
        }

        public void Release() => _release.Set();

        public TreeBuildResult Build(
            string rootPath,
            TreeFilterOptions options,
            CancellationToken cancellationToken = default) =>
            _inner.Build(rootPath, options, cancellationToken);

        public ProjectTreeInventorySnapshot ReadInventory(
            string rootPath,
            TreeFilterOptions options,
            CancellationToken cancellationToken = default) =>
            _inner.ReadInventory(rootPath, options, cancellationToken);

        public TreeBuildResult Build(
            ProjectTreeInventorySnapshot inventory,
            TreeFilterOptions options,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                BuildStarted.TrySetResult();
                _release.Wait(cancellationToken);
            }

            return _inner.Build(inventory, options, cancellationToken);
        }

        public ProjectTreeInventorySnapshot ReadCompositeInventory(
            string rootPath,
            IReadOnlySet<string> allowedRootFolders,
            IgnoreRules discoveryRules,
            IgnoreRules projectionRules,
            CancellationToken cancellationToken = default) =>
            _inner.ReadCompositeInventory(
                rootPath,
                allowedRootFolders,
                discoveryRules,
                projectionRules,
                cancellationToken);
    }

    private sealed class MutatingGitRepositoryService(string repositoryPath) : IGitRepositoryService
    {
        private string _branch = "main";
        private string _head = "before";

        public Action? PullMutation { get; init; }

        public Action? SwitchMutation { get; init; }

        public Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<GitCloneResult> CloneAsync(
            string url,
            string targetDirectory,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(
            string requestedRepositoryPath,
            CancellationToken cancellationToken = default)
        {
            Assert.True(PathComparer.Default.Equals(repositoryPath, requestedRepositoryPath));
            IReadOnlyList<GitBranch> branches =
            [
                new("main", IsActive: string.Equals(_branch, "main", StringComparison.Ordinal), IsRemote: false),
                new("feature", IsActive: string.Equals(_branch, "feature", StringComparison.Ordinal), IsRemote: false)
            ];
            return Task.FromResult(branches);
        }

        public Task<string?> GetDefaultBranchAsync(
            string requestedRepositoryPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("main");

        public Task<bool> SwitchBranchAsync(
            string requestedRepositoryPath,
            string branchName,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Assert.True(PathComparer.Default.Equals(repositoryPath, requestedRepositoryPath));
            SwitchMutation?.Invoke();
            _branch = branchName;
            _head = "switched";
            return Task.FromResult(true);
        }

        public Task<bool> PullUpdatesAsync(
            string requestedRepositoryPath,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Assert.True(PathComparer.Default.Equals(repositoryPath, requestedRepositoryPath));
            PullMutation?.Invoke();
            _head = "after";
            return Task.FromResult(true);
        }

        public Task<string?> GetHeadCommitAsync(
            string requestedRepositoryPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(_head);

        public Task<string?> GetCurrentBranchAsync(
            string requestedRepositoryPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(_branch);

        public Task<string?> GetRemoteUrlAsync(
            string requestedRepositoryPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("https://example.test/repository.git");
    }
}
