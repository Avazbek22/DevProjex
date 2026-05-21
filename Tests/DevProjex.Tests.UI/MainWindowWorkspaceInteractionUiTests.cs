using DevProjex.Infrastructure.RecentProjects;
using DevProjex.Kernel.Abstractions;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowWorkspaceInteractionUiTests(UiWorkspaceFixture workspace)
{
    [AvaloniaFact]
    public async Task Startup_BootstrapsAllStateStoreFiles_InIsolatedAppData()
    {
        var appDataPath = Path.Combine(workspace.Project.AppDataPath, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appDataPath);

        var window = await UiTestDriver.CreateLoadedMainWindowAsync(
            workspace.Project,
            appDataPathOverride: appDataPath);

        try
        {
            var storeDirectory = Path.Combine(appDataPath, "DevProjex");

            Assert.True(File.Exists(Path.Combine(storeDirectory, "user-settings.json")));
            Assert.True(File.Exists(Path.Combine(storeDirectory, "user-settings.json.bak")));
            Assert.True(File.Exists(Path.Combine(storeDirectory, "recent-projects.json")));
            Assert.True(File.Exists(Path.Combine(storeDirectory, "recent-projects.json.bak")));
            Assert.True(File.Exists(Path.Combine(storeDirectory, "project-profiles.json")));
            Assert.True(File.Exists(Path.Combine(storeDirectory, "project-profiles.json.bak")));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);

            try
            {
                Directory.Delete(appDataPath, recursive: true);
            }
            catch
            {
                // Best effort test cleanup only.
            }
        }
    }

    [AvaloniaFact]
    public async Task OpenNewWindowMenuItem_LaunchesIndependentAppInstance()
    {
        var launcher = new RecordingAppInstanceLauncher(AppInstanceLaunchResult.Success);
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(
            workspace.Project,
            configureServices: services => services with
            {
                AppInstanceLauncher = launcher
            });

        try
        {
            var menuItem = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "OpenNewWindowMenuItem");

            await UiTestDriver.RaiseMenuItemClickAsync(menuItem);

            Assert.Equal(1, launcher.LaunchCallCount);
            Assert.True(window.IsVisible);
            Assert.True(UiTestDriver.GetViewModel(window).IsProjectLoaded);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task TreeNodeCheckbox_DoubleClick_DoesNotExpandBranch()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);
            var rootNode = Assert.Single(viewModel.TreeNodes);
            rootNode.IsExpanded = true;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);

            var srcNode = rootNode.Children.Single(node => string.Equals(node.DisplayName, "src", StringComparison.Ordinal));
            srcNode.IsExpanded = false;
            srcNode.IsChecked = false;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);

            var checkBox = await UiTestDriver.WaitForTreeNodeCheckBoxAsync(window, "src");
            await UiTestDriver.DoubleClickAsync(window, checkBox);

            Assert.False(srcNode.IsExpanded);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task RecentProjects_AreFlushedOnClose_WhenImmediateSaveFails()
    {
        var appDataPath = Path.Combine(workspace.Project.AppDataPath, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appDataPath);

        var options = new CommandLineOptions(workspace.Project.RootPath, AppLanguage.En, false);
        var baseServices = AvaloniaCompositionRoot.CreateDefault(options, () => appDataPath);
        var recentPathCallCount = 0;
        var services = baseServices with
        {
            RecentProjectsStore = new RecentProjectsStore(() =>
                Interlocked.Increment(ref recentPathCallCount) == 2
                    ? string.Concat("broken", '\0', "recent-root")
                    : appDataPath)
        };

        var window = new MainWindow(options, services)
        {
            Width = 1500,
            Height = 920
        };

        try
        {
            window.Show();

            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var viewModel = UiTestDriver.GetViewModel(window);
                    return viewModel.IsProjectLoaded &&
                           viewModel.TreeNodes.Count > 0 &&
                           !viewModel.StatusBusy;
                },
                "project to finish loading with a transient recent-projects save failure");

            await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);

            var filePath = Path.Combine(appDataPath, "DevProjex", "recent-projects.json");
            Assert.True(File.Exists(filePath));

            var store = new RecentProjectsStore(() => appDataPath);
            var db = store.Load();
            Assert.Single(db.RecentFolders);
            Assert.Empty(db.RecentRepositories);
        }
        finally
        {
            if (window.IsVisible)
                await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);

            try
            {
                Directory.Delete(appDataPath, recursive: true);
            }
            catch
            {
                // Best effort test cleanup only.
            }
        }
    }

    [AvaloniaFact]
    public async Task ProjectProfiles_AreFlushedOnClose_WhenApplySaveFails()
    {
        var appDataPath = Path.Combine(workspace.Project.AppDataPath, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appDataPath);

        var options = new CommandLineOptions(workspace.Project.RootPath, AppLanguage.En, false);
        var baseServices = AvaloniaCompositionRoot.CreateDefault(options, () => appDataPath);
        var flakyStore = new FlakyProjectProfileStore(Path.Combine(appDataPath, "DevProjex", "project-profiles.json"));
        var services = baseServices with
        {
            ProjectProfileStore = flakyStore
        };

        var window = new MainWindow(options, services)
        {
            Width = 1500,
            Height = 920
        };

        try
        {
            window.Show();

            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var viewModel = UiTestDriver.GetViewModel(window);
                    return viewModel.IsProjectLoaded &&
                           viewModel.TreeNodes.Count > 0 &&
                           !viewModel.StatusBusy;
                },
                "project to finish loading with a transient profile save failure");

            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var settingsContainer = UiTestDriver.GetRequiredControl<Border>(window, "SettingsContainer");
                    return UiTestDriver.GetBoundsInWindow(settingsContainer, window).Width >= 200;
                },
                "initial settings pane to become visually available before applying settings");

            var applyButton = UiTestDriver.GetRequiredApplySettingsButton(window);
            await UiTestDriver.RaiseButtonClickAsync(applyButton);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => flakyStore.SaveAttemptCount >= 1,
                "initial profile persistence attempt to run after applying settings");
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => !UiTestDriver.GetViewModel(window).StatusBusy,
                "apply settings operation to finish after the profile save attempt");
            Assert.Equal(1, flakyStore.SaveAttemptCount);

            await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);

            Assert.True(File.Exists(flakyStore.StoragePath));
            Assert.True(flakyStore.TryLoadProfile(workspace.Project.RootPath, out var profile));
            Assert.NotNull(profile);
        }
        finally
        {
            if (window.IsVisible)
                await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);

            try
            {
                Directory.Delete(appDataPath, recursive: true);
            }
            catch
            {
                // Best effort test cleanup only.
            }
        }
    }

    private sealed class FlakyProjectProfileStore(string storagePath) : IProjectProfileStore
    {
        private readonly Dictionary<string, ProjectSelectionProfile> _profiles = new(PathComparer.Default);
        private int _saveAttemptCount;

        public string StoragePath { get; } = storagePath;
        public int SaveAttemptCount => _saveAttemptCount;

        public bool EnsureStorageExists()
        {
            var directory = Path.GetDirectoryName(StoragePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            if (!File.Exists(StoragePath))
                File.WriteAllText(StoragePath, "{}");

            return true;
        }

        public bool TryLoadProfile(string localProjectPath, out ProjectSelectionProfile profile)
        {
            if (_profiles.TryGetValue(PathUtility.Normalize(localProjectPath), out var existing))
            {
                profile = CloneProfile(existing);
                return true;
            }

            profile = CreateEmptyProfile();
            return false;
        }

        public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile)
            => TrySaveProfile(localProjectPath, profile, DateTimeOffset.UtcNow);

        public bool TrySaveProfile(string localProjectPath, ProjectSelectionProfile profile, DateTimeOffset updatedUtc)
        {
            if (Interlocked.Increment(ref _saveAttemptCount) == 1)
                return false;

            var normalizedPath = PathUtility.Normalize(localProjectPath);
            _profiles[normalizedPath] = CloneProfile(profile);

            var directory = Path.GetDirectoryName(StoragePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(StoragePath, $$"""
                                        {
                                          "path": "{{normalizedPath}}",
                                          "saveAttemptCount": {{_saveAttemptCount}}
                                        }
                                        """);

            return true;
        }

        public void SaveProfile(string localProjectPath, ProjectSelectionProfile profile)
            => TrySaveProfile(localProjectPath, profile);

        public void ClearAllProfiles()
        {
            _profiles.Clear();
            if (File.Exists(StoragePath))
                File.Delete(StoragePath);
        }

        private static ProjectSelectionProfile CreateEmptyProfile()
            => new([], [], []);

        private static ProjectSelectionProfile CloneProfile(ProjectSelectionProfile profile)
        {
            return new ProjectSelectionProfile(
                SelectedRootFolders: profile.SelectedRootFolders.ToArray(),
                SelectedExtensions: profile.SelectedExtensions.ToArray(),
                SelectedIgnoreOptions: profile.SelectedIgnoreOptions.ToArray());
        }
    }

    private sealed class RecordingAppInstanceLauncher(AppInstanceLaunchResult launchResult) : IAppInstanceLauncher
    {
        private int _launchCallCount;

        public int LaunchCallCount => _launchCallCount;

        public AppInstanceLaunchResult LaunchNewInstance()
        {
            Interlocked.Increment(ref _launchCallCount);
            return launchResult;
        }
    }

    [AvaloniaFact]
    public async Task GitCloneRecentRepositoriesContainer_HidesWhileCloneIsInProgress()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);
            viewModel.RecentRepositories.Add(new RecentProjectEntryViewModel(
                "https://github.com/example/repo",
                "example / repo",
                "https://github.com/example/repo"));

            var cloneWindow = await UiTestDriver.OpenGitCloneWindowAsync(window);
            try
            {
                var recentContainer = cloneWindow.FindControl<Border>("RecentRepositoriesContainer");
                Assert.NotNull(recentContainer);
                Assert.True(recentContainer!.IsVisible);

                viewModel.GitCloneInProgress = true;
                await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);

                Assert.False(recentContainer.IsVisible);
            }
            finally
            {
                await UiTestDriver.CloseTopLevelWindowAsync(cloneWindow);
            }
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task FilterButton_RemainsEnabledButIgnoredInPreviewOnly()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.HidePreviewTreeAsync(window);

            var filterToggleButton = UiTestDriver.GetRequiredTopMenuControl<Button>(window, "FilterToggleButton");
            Assert.True(filterToggleButton.IsEnabled);

            await UiTestDriver.ClickAsync(window, filterToggleButton);
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);

            var viewModel = UiTestDriver.GetViewModel(window);
            Assert.False(viewModel.FilterVisible);
            Assert.False(UiTestDriver.GetRequiredControl<Border>(window, "FilterBarContainer").IsVisible);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task FilterButton_IgnoredInPreviewOnly_DoesNotClearSuspendedFilterState()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenFilterAsync(window);
            var filterBar = UiTestDriver.GetRequiredControl<FilterBarView>(window, "FilterBar");
            await UiTestDriver.EnterTextAsync(window, Assert.IsType<TextBox>(filterBar.FilterBoxControl), "app");
            await UiTestDriver.WaitForFilterAppliedAsync(window, "app");

            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.HidePreviewTreeAsync(window);

            var filterToggleButton = UiTestDriver.GetRequiredTopMenuControl<Button>(window, "FilterToggleButton");
            await UiTestDriver.ClickAsync(window, filterToggleButton);
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);

            var suspendedViewModel = UiTestDriver.GetViewModel(window);
            Assert.False(suspendedViewModel.FilterVisible);
            Assert.Equal("app", suspendedViewModel.NameFilter);

            await UiTestDriver.ClosePreviewAsync(window);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var viewModel = UiTestDriver.GetViewModel(window);
                    return viewModel.FilterVisible &&
                           viewModel.NameFilter == "app" &&
                           UiTestDriver.GetRequiredControl<Border>(window, "FilterBarContainer").IsVisible;
                },
                "suspended filter state to be restored after preview-only close");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task TreePreviewSplitter_DragResizesTreeAndPreviewPanes()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);

            var treeIsland = UiTestDriver.GetRequiredControl<Border>(window, "TreeIsland");
            var previewIsland = UiTestDriver.GetRequiredControl<Border>(window, "PreviewIsland");
            var splitter = UiTestDriver.GetRequiredControl<Border>(window, "TreePreviewSplitter");

            var treeWidthBefore = UiTestDriver.GetBoundsInWindow(treeIsland, window).Width;
            var previewWidthBefore = UiTestDriver.GetBoundsInWindow(previewIsland, window).Width;

            await UiTestDriver.DragAsync(window, splitter, deltaX: 120);

            var treeWidthAfter = UiTestDriver.GetBoundsInWindow(treeIsland, window).Width;
            var previewWidthAfter = UiTestDriver.GetBoundsInWindow(previewIsland, window).Width;

            Assert.True(treeWidthAfter > treeWidthBefore + 10);
            Assert.True(previewWidthAfter < previewWidthBefore - 10);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewSettingsSplitter_DragResizesSettingsPaneWithinConfiguredBounds()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);

            var splitter = UiTestDriver.GetRequiredControl<Border>(window, "PreviewSettingsSplitter");
            var settingsContainer = UiTestDriver.GetRequiredControl<Border>(window, "SettingsContainer");
            var settingsPanel = UiTestDriver.GetRequiredControl<SettingsPanelView>(window, "SettingsPanel");
            var widthBefore = UiTestDriver.GetBoundsInWindow(settingsContainer, window).Width;
            var requiredMinimum = settingsPanel.GetRequiredMinimumWidth();

            await UiTestDriver.DragAsync(window, splitter, deltaX: 220);
            var widthCollapsed = UiTestDriver.GetBoundsInWindow(settingsContainer, window).Width;

            await UiTestDriver.DragAsync(window, splitter, deltaX: -140);
            var widthExpanded = UiTestDriver.GetBoundsInWindow(settingsContainer, window).Width;

            var diagnostic =
                $"Before={widthBefore:F2}, Collapsed={widthCollapsed:F2}, Expanded={widthExpanded:F2}, " +
                $"RequiredMinimum={requiredMinimum:F2}";

            Assert.True(widthCollapsed >= requiredMinimum - 1, diagnostic);
            if (requiredMinimum < widthBefore - 1)
            {
                Assert.True(widthCollapsed < widthBefore - 1, diagnostic);
                Assert.True(widthExpanded > widthCollapsed + 5, diagnostic);
            }
            else
            {
                // Long localized labels can legitimately raise the content minimum above the
                // normal resize range. In that state the splitter must pin instead of clipping.
                Assert.InRange(widthCollapsed, requiredMinimum - 1, requiredMinimum + 1);
                Assert.InRange(widthExpanded, requiredMinimum - 1, requiredMinimum + 1);
            }
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task CtrlWheelOverTree_ChangesOnlyTreeZoomInsidePreviewWorkspace()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);

            var viewModel = UiTestDriver.GetViewModel(window);
            var treeIsland = UiTestDriver.GetRequiredControl<Border>(window, "TreeIsland");
            var point = UiTestDriver.GetControlCenter(treeIsland, window);
            var treeBefore = viewModel.TreeFontSize;
            var previewBefore = viewModel.PreviewFontSize;

            window.MouseMove(point, RawInputModifiers.None);
            window.MouseWheel(point, new Vector(0, 1), RawInputModifiers.Control);
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);

            Assert.True(viewModel.TreeFontSize > treeBefore);
            Assert.Equal(previewBefore, viewModel.PreviewFontSize);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task CtrlWheelOverPreview_ChangesOnlyPreviewZoomInsidePreviewWorkspace()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);

            var viewModel = UiTestDriver.GetViewModel(window);
            var previewIsland = UiTestDriver.GetRequiredControl<Border>(window, "PreviewIsland");
            var point = UiTestDriver.GetControlCenter(previewIsland, window);
            var treeBefore = viewModel.TreeFontSize;
            var previewBefore = viewModel.PreviewFontSize;

            window.MouseMove(point, RawInputModifiers.None);
            window.MouseWheel(point, new Vector(0, 1), RawInputModifiers.Control);
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);

            Assert.Equal(treeBefore, viewModel.TreeFontSize);
            Assert.True(viewModel.PreviewFontSize > previewBefore);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task CtrlZero_ResetsBothZoomTargetsWhenPreviewShowsTreeAndContent()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);

            var treeIsland = UiTestDriver.GetRequiredControl<Border>(window, "TreeIsland");
            var previewIsland = UiTestDriver.GetRequiredControl<Border>(window, "PreviewIsland");
            var treePoint = UiTestDriver.GetControlCenter(treeIsland, window);
            var previewPoint = UiTestDriver.GetControlCenter(previewIsland, window);

            window.MouseMove(treePoint, RawInputModifiers.None);
            window.MouseWheel(treePoint, new Vector(0, 1), RawInputModifiers.Control);
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);

            window.MouseMove(previewPoint, RawInputModifiers.None);
            window.MouseWheel(previewPoint, new Vector(0, 1), RawInputModifiers.Control);
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);

            await UiTestDriver.PressKeyAsync(window, Key.D0, RawInputModifiers.Control);

            var viewModel = UiTestDriver.GetViewModel(window);
            Assert.Equal(MainWindowViewModel.DefaultTreeFontSize, viewModel.TreeFontSize);
            Assert.Equal(MainWindowViewModel.DefaultPreviewFontSize, viewModel.PreviewFontSize);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task TreePreviewSplitter_RespectsMinimumWidthsAtExtremeDrag()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);

            var treeIsland = UiTestDriver.GetRequiredControl<Border>(window, "TreeIsland");
            var previewIsland = UiTestDriver.GetRequiredControl<Border>(window, "PreviewIsland");
            var splitter = UiTestDriver.GetRequiredControl<Border>(window, "TreePreviewSplitter");

            await UiTestDriver.DragAsync(window, splitter, deltaX: 2_000);
            var treeExpandedWidth = UiTestDriver.GetBoundsInWindow(treeIsland, window).Width;
            var previewCollapsedWidth = UiTestDriver.GetBoundsInWindow(previewIsland, window).Width;

            await UiTestDriver.DragAsync(window, splitter, deltaX: -2_000);
            var treeCollapsedWidth = UiTestDriver.GetBoundsInWindow(treeIsland, window).Width;
            var previewExpandedWidth = UiTestDriver.GetBoundsInWindow(previewIsland, window).Width;

            Assert.True(treeExpandedWidth >= 418);
            Assert.True(previewCollapsedWidth >= 320);
            Assert.True(treeCollapsedWidth >= 418);
            Assert.True(previewExpandedWidth >= 320);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewSettingsSplitter_RespectsHardBoundsAtExtremeDrag()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);

            var splitter = UiTestDriver.GetRequiredControl<Border>(window, "PreviewSettingsSplitter");
            var settingsContainer = UiTestDriver.GetRequiredControl<Border>(window, "SettingsContainer");
            var settingsPanel = UiTestDriver.GetRequiredControl<SettingsPanelView>(window, "SettingsPanel");
            var requiredMinimum = settingsPanel.GetRequiredMinimumWidth();

            await UiTestDriver.DragAsync(window, splitter, deltaX: 2_000);
            var collapsedWidth = UiTestDriver.GetBoundsInWindow(settingsContainer, window).Width;

            await UiTestDriver.DragAsync(window, splitter, deltaX: -2_000);
            var expandedWidth = UiTestDriver.GetBoundsInWindow(settingsContainer, window).Width;

            Assert.True(
                collapsedWidth >= requiredMinimum - 1,
                $"Collapsed={collapsedWidth:F2}, Expanded={expandedWidth:F2}, RequiredMinimum={requiredMinimum:F2}");
            Assert.True(
                expandedWidth >= collapsedWidth - 1,
                $"Collapsed={collapsedWidth:F2}, Expanded={expandedWidth:F2}, RequiredMinimum={requiredMinimum:F2}");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }
}
