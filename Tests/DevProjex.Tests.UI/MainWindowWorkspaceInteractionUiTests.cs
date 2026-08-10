using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using DevProjex.Avalonia.Controls;
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
            var expectedStorePaths = new[]
            {
                Path.Combine(storeDirectory, "user-settings.json"),
                Path.Combine(storeDirectory, "user-settings.json.bak"),
                Path.Combine(storeDirectory, "recent-projects.json"),
                Path.Combine(storeDirectory, "recent-projects.json.bak"),
                Path.Combine(storeDirectory, "project-profiles.json"),
                Path.Combine(storeDirectory, "project-profiles.json.bak")
            };

            await UiTestDriver.WaitForConditionAsync(
                window,
                () => expectedStorePaths.All(File.Exists),
                "deferred startup state-store bootstrap to create primary and backup files");

            Assert.All(expectedStorePaths, path => Assert.True(File.Exists(path), path));
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
    public async Task LazyTreeNode_ChevronIsVisibleBeforeMaterialization_AndExpandsOnClick()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);
            var rootNode = Assert.Single(viewModel.TreeNodes);
            rootNode.IsExpanded = true;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);

            var srcNode = rootNode.Children.Single(
                node => string.Equals(
                    node.DisplayName,
                    "src",
                    StringComparison.Ordinal));
            Assert.True(srcNode.HasChildren);
            Assert.False(srcNode.AreChildrenRealized);

            var chevron = Assert.Single(
                window.GetVisualDescendants().OfType<ToggleButton>(),
                control =>
                    control.Name == "PART_ExpandCollapseChevron" &&
                    ReferenceEquals(control.DataContext, srcNode));

            Assert.True(chevron.IsVisible);
            Assert.True(chevron.Opacity > 0);

            var readmeNode = rootNode.Children.Single(
                node => string.Equals(
                    node.DisplayName,
                    "README.md",
                    StringComparison.Ordinal));
            var leafChevron = Assert.Single(
                window.GetVisualDescendants().OfType<ToggleButton>(),
                control =>
                    control.Name == "PART_ExpandCollapseChevron" &&
                    ReferenceEquals(control.DataContext, readmeNode));
            Assert.False(readmeNode.HasChildren);
            Assert.False(leafChevron.IsVisible);

            await UiTestDriver.ClickAsync(window, chevron);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => srcNode.IsExpanded &&
                      srcNode.AreChildrenRealized &&
                      srcNode.Children.Count > 0,
                "lazy tree node to materialize after chevron click");

            Assert.True(srcNode.IsExpanded);
            Assert.NotEmpty(srcNode.Children);

            srcNode.IsChecked = true;
            srcNode.IsExpanded = false;
            Assert.True(srcNode.TryReleaseChildrenToLazyState());
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);

            var releasedChevron = Assert.Single(
                window.GetVisualDescendants().OfType<ToggleButton>(),
                control =>
                    control.Name == "PART_ExpandCollapseChevron" &&
                    ReferenceEquals(control.DataContext, srcNode));
            Assert.False(srcNode.AreChildrenRealized);
            Assert.Empty(srcNode.ChildItemsSource);
            Assert.True(srcNode.HasChildren);
            Assert.True(
                releasedChevron.IsVisible,
                string.Join(
                    Environment.NewLine,
                    releasedChevron
                        .GetVisualAncestors()
                        .Select(visual =>
                            $"{visual.GetType().Name} '{(visual as Control)?.Name}': IsVisible={visual.IsVisible}")));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task TreeChevron_HasAccessibleHitTargetWithoutGrowingItsGlyph()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);
            var rootNode = Assert.Single(viewModel.TreeNodes);
            rootNode.IsExpanded = true;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);

            var folderNode = rootNode.Children.Single(
                node => string.Equals(
                    node.DisplayName,
                    "src",
                    StringComparison.Ordinal));
            folderNode.IsExpanded = false;
            folderNode.IsChecked = false;
            folderNode.IsSelected = false;

            var chevron = Assert.Single(
                window.GetVisualDescendants().OfType<ToggleButton>(),
                control =>
                    control.Name == "PART_ExpandCollapseChevron" &&
                    ReferenceEquals(control.DataContext, folderNode));
            var glyph = Assert.Single(
                chevron.GetVisualDescendants()
                    .OfType<global::Avalonia.Controls.Shapes.Path>(),
                path => path.Name == "ChevronPath");
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);

            var targetBounds = UiTestDriver.GetBoundsInWindow(chevron, window);
            var glyphBounds = UiTestDriver.GetBoundsInWindow(glyph, window);
            Assert.InRange(targetBounds.Width, 23.5, 24.5);
            Assert.InRange(targetBounds.Height, 23.5, 24.5);
            Assert.InRange(glyphBounds.Width, 5.5, 6.5);
            Assert.InRange(glyphBounds.Height, 11.5, 12.5);

            var paddedClickPoint = new Point(
                targetBounds.Left + 2,
                targetBounds.Center.Y);
            Assert.True(paddedClickPoint.X < glyphBounds.Left);

            window.MouseMove(paddedClickPoint, RawInputModifiers.None);
            window.MouseDown(
                paddedClickPoint,
                MouseButton.Left,
                RawInputModifiers.LeftMouseButton);
            window.MouseUp(
                paddedClickPoint,
                MouseButton.Left,
                RawInputModifiers.None);

            await UiTestDriver.WaitForConditionAsync(
                window,
                () => folderNode.IsExpanded,
                "tree folder to expand from the padded chevron hit target");

            Assert.False(folderNode.IsChecked);
            Assert.False(folderNode.IsSelected);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task TreeChevron_RotatesSingleGeometryAndReturnsToCollapsedState()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);
            var rootNode = Assert.Single(viewModel.TreeNodes);
            rootNode.IsExpanded = true;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);

            var folderNode = rootNode.Children.Single(
                node => string.Equals(
                    node.DisplayName,
                    "src",
                    StringComparison.Ordinal));
            var chevron = Assert.Single(
                window.GetVisualDescendants().OfType<ToggleButton>(),
                control =>
                    control.Name == "PART_ExpandCollapseChevron" &&
                    ReferenceEquals(control.DataContext, folderNode));
            var chevronPath = Assert.Single(
                chevron.GetVisualDescendants().OfType<global::Avalonia.Controls.Shapes.Path>(),
                path => path.Name == "ChevronPath");
            var transition = Assert.IsType<global::Avalonia.Animation.TransformOperationsTransition>(
                Assert.Single(chevronPath.Transitions!));
            var collapsedGeometry = chevronPath.Data;
            var collapsedTransform = chevronPath.RenderTransform?.Value ?? Matrix.Identity;

            Assert.Equal(TimeSpan.FromMilliseconds(120), transition.Duration);
            Assert.Equal(Visual.RenderTransformProperty, transition.Property);

            await UiTestDriver.ClickAsync(window, chevron);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => folderNode.IsExpanded,
                "tree folder to expand from its animated chevron");
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => IsQuarterTurn(
                    chevronPath.RenderTransform?.Value ?? Matrix.Identity),
                "tree chevron rotation to reach its expanded angle");

            var expandedTransform = chevronPath.RenderTransform?.Value ?? Matrix.Identity;
            Assert.NotEqual(collapsedTransform, expandedTransform);
            Assert.Same(collapsedGeometry, chevronPath.Data);

            await UiTestDriver.ClickAsync(window, chevron);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => !folderNode.IsExpanded,
                "tree folder to collapse from its animated chevron");
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => AreMatricesClose(
                    collapsedTransform,
                    chevronPath.RenderTransform?.Value ?? Matrix.Identity),
                "tree chevron rotation to return to its collapsed angle");

            var restoredTransform = chevronPath.RenderTransform?.Value ?? Matrix.Identity;
            AssertMatricesClose(collapsedTransform, restoredTransform);
            Assert.Same(collapsedGeometry, chevronPath.Data);

            await UiTestDriver.ClickAsync(window, chevron);
            await UiTestDriver.ClickAsync(window, chevron);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => !folderNode.IsExpanded,
                "rapidly toggled tree folder to keep the final collapsed state");
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => AreMatricesClose(
                    collapsedTransform,
                    chevronPath.RenderTransform?.Value ?? Matrix.Identity),
                "retargeted tree chevron rotation to return to its collapsed angle");

            var retargetedTransform = chevronPath.RenderTransform?.Value ?? Matrix.Identity;
            AssertMatricesClose(collapsedTransform, retargetedTransform);
            Assert.Same(collapsedGeometry, chevronPath.Data);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task TreeBranch_UserToggleAnimatesChildren_ProgrammaticToggleSnaps()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);
            var rootNode = Assert.Single(viewModel.TreeNodes);
            rootNode.IsExpanded = true;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);

            var folderNode = rootNode.Children.Single(
                node => string.Equals(
                    node.DisplayName,
                    "src",
                    StringComparison.Ordinal));
            var chevron = Assert.Single(
                window.GetVisualDescendants().OfType<ToggleButton>(),
                control =>
                    control.Name == "PART_ExpandCollapseChevron" &&
                    ReferenceEquals(control.DataContext, folderNode));
            var childrenHost = Assert.Single(
                window.GetVisualDescendants().OfType<AnimatedTreeChildrenHost>(),
                control => ReferenceEquals(control.DataContext, folderNode));
            var heightTransition = Assert.Single(
                childrenHost.Transitions!
                    .OfType<global::Avalonia.Animation.DoubleTransition>(),
                transition =>
                    transition.Property ==
                    AnimatedTreeChildrenHost.ExpansionProgressProperty);

            Assert.Equal(
                AnimatedTreeChildrenHost.ExpansionDuration,
                heightTransition.Duration);
            Assert.False(childrenHost.IsVisible);
            Assert.Equal(0d, childrenHost.ExpansionProgress);

            await UiTestDriver.ClickAsync(window, chevron);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => folderNode.IsExpanded &&
                      childrenHost.IsVisible &&
                      childrenHost.ExpansionProgress >= 0.999d,
                "tree children expansion animation to finish");

            await UiTestDriver.ClickAsync(window, chevron);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => !folderNode.IsExpanded && !childrenHost.IsVisible,
                "tree children collapse animation to finish");

            // Search, restore and bulk tree operations update IsExpanded directly. Those
            // paths must not fan out into many simultaneous layout animations.
            folderNode.IsExpanded = true;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
            Assert.True(childrenHost.IsVisible);
            Assert.Equal(1d, childrenHost.ExpansionProgress);

            folderNode.IsExpanded = false;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
            Assert.False(childrenHost.IsVisible);
            Assert.Equal(0d, childrenHost.ExpansionProgress);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task TreeExpansionAnimationSetting_ControlsChevronAndBranchMotionTogether()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);
            var tree = UiTestDriver.GetRequiredControl<ProjectTreeView>(window, "ProjectTree");
            var rootNode = Assert.Single(viewModel.TreeNodes);
            rootNode.IsExpanded = true;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);

            var folderNode = rootNode.Children.Single(
                node => string.Equals(
                    node.DisplayName,
                    "src",
                    StringComparison.Ordinal));
            var chevron = Assert.Single(
                window.GetVisualDescendants().OfType<ToggleButton>(),
                control =>
                    control.Name == "PART_ExpandCollapseChevron" &&
                    ReferenceEquals(control.DataContext, folderNode));
            var chevronPath = Assert.Single(
                chevron.GetVisualDescendants()
                    .OfType<global::Avalonia.Controls.Shapes.Path>(),
                path => path.Name == "ChevronPath");
            var childrenHost = Assert.Single(
                window.GetVisualDescendants().OfType<AnimatedTreeChildrenHost>(),
                control => ReferenceEquals(control.DataContext, folderNode));
            var menuItem = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(
                window,
                "TreeExpansionAnimationMenuItem");
            var menuCheckBox = Assert.IsType<CheckBox>(menuItem.Header);

            Assert.True(viewModel.IsTreeExpansionAnimationEnabled);
            Assert.True(tree.IsExpansionAnimationEnabled);
            Assert.True(menuCheckBox.IsChecked);
            Assert.Equal("Tree expansion animation", menuCheckBox.Content);
            Assert.Single(chevronPath.Transitions!);

            await UiTestDriver.RaiseMenuItemClickAsync(menuItem);
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);

            Assert.False(viewModel.IsTreeExpansionAnimationEnabled);
            Assert.False(tree.IsExpansionAnimationEnabled);
            Assert.False(menuCheckBox.IsChecked);
            Assert.True(chevronPath.Transitions is null or { Count: 0 });

            await UiTestDriver.ClickAsync(window, chevron);
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
            Assert.True(folderNode.IsExpanded);
            Assert.True(childrenHost.IsVisible);
            Assert.Equal(1d, childrenHost.ExpansionProgress);

            await UiTestDriver.ClickAsync(window, chevron);
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
            Assert.False(folderNode.IsExpanded);
            Assert.False(childrenHost.IsVisible);
            Assert.Equal(0d, childrenHost.ExpansionProgress);

            await UiTestDriver.RaiseMenuItemClickAsync(menuItem);
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);

            Assert.True(viewModel.IsTreeExpansionAnimationEnabled);
            Assert.True(tree.IsExpansionAnimationEnabled);
            Assert.True(menuCheckBox.IsChecked);
            Assert.Single(chevronPath.Transitions!);
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

        var options = new DesktopStartupOptions(
            new DesktopOpenRequest(workspace.Project.RootPath, Language: AppLanguage.En));
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
        UiTestDriver.TrackTopLevelWindow(window);

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

        var options = new DesktopStartupOptions(
            new DesktopOpenRequest(workspace.Project.RootPath, Language: AppLanguage.En));
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
        UiTestDriver.TrackTopLevelWindow(window);

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
            var settingsIsland = UiTestDriver.GetRequiredControl<Border>(window, "SettingsIsland");
            var settingsPanel = UiTestDriver.GetRequiredControl<SettingsPanelView>(window, "SettingsPanel");
            var widthBefore = UiTestDriver.GetBoundsInWindow(settingsIsland, window).Width;
            var requiredMinimum = settingsPanel.GetRequiredMinimumWidth();

            await UiTestDriver.DragAsync(window, splitter, deltaX: -220);
            var widthAfterExpansionDrag = UiTestDriver.GetBoundsInWindow(settingsIsland, window).Width;

            await UiTestDriver.DragAsync(window, splitter, deltaX: 220);
            var widthAfterCollapseDrag = UiTestDriver.GetBoundsInWindow(settingsIsland, window).Width;

            var diagnostic =
                $"Before={widthBefore:F2}, Expanded={widthAfterExpansionDrag:F2}, Collapsed={widthAfterCollapseDrag:F2}, " +
                $"RequiredMinimum={requiredMinimum:F2}";

            // The default settings island width is intentionally pinned to the visual minimum
            // so it aligns with the top tree-format switcher, while manual resize can still expand it.
            Assert.InRange(widthBefore, requiredMinimum - 1, requiredMinimum + 1);
            Assert.True(widthAfterExpansionDrag > widthBefore + 5, diagnostic);
            Assert.True(widthAfterCollapseDrag <= widthAfterExpansionDrag - 5, diagnostic);
            Assert.InRange(widthAfterCollapseDrag, requiredMinimum - 1, requiredMinimum + 1);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task SettingsPanel_RemovesFontPickerAndKeepsApplyButtonAtLeftEdge()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);

            var panelRoot = UiTestDriver.GetRequiredControl<Border>(window, "PanelRoot");
            var applyButton = UiTestDriver.GetRequiredControl<Button>(window, "ApplySettingsButton");
			var processingHeader = UiTestDriver.GetRequiredControl<TextBlock>(window, "ContentProcessingHeaderText");
            var ignoreHeader = UiTestDriver.GetRequiredControl<Grid>(window, "IgnoreHeaderGrid");

            var panelBounds = UiTestDriver.GetBoundsInWindow(panelRoot, window);
            var buttonBounds = UiTestDriver.GetBoundsInWindow(applyButton, window);
			var processingHeaderBounds = UiTestDriver.GetBoundsInWindow(processingHeader, window);
            var ignoreHeaderBounds = UiTestDriver.GetBoundsInWindow(ignoreHeader, window);

            Assert.DoesNotContain(
                window.GetVisualDescendants().OfType<ComboBox>(),
                control => string.Equals(control.Name, "FontComboBox", StringComparison.Ordinal));
            Assert.DoesNotContain(
                window.GetVisualDescendants().OfType<TextBlock>(),
                control => string.Equals(control.Name, "FontPickerLabel", StringComparison.Ordinal));

            Assert.Equal(HorizontalAlignment.Left, applyButton.HorizontalAlignment);
            Assert.Equal(HorizontalAlignment.Center, applyButton.HorizontalContentAlignment);
            Assert.Equal(0, applyButton.Margin.Left);
            Assert.Equal(0, applyButton.Margin.Right);
            Assert.True(applyButton.Margin.Top >= 0);
            Assert.True(applyButton.Margin.Bottom >= 0);

            Assert.InRange(buttonBounds.Left - ignoreHeaderBounds.Left, -1, 1);
			Assert.InRange(buttonBounds.Left - processingHeaderBounds.Left, -1, 1);
            Assert.True(
                buttonBounds.Width < ignoreHeaderBounds.Width - 20,
                $"Apply button should keep its natural width. Button={buttonBounds.Width:F2}, Header={ignoreHeaderBounds.Width:F2}.");

            var topGap = buttonBounds.Top - panelBounds.Top;
			var headerGap = processingHeaderBounds.Top - buttonBounds.Bottom;
            Assert.InRange(topGap, 0, 16);
			Assert.InRange(headerGap, 8, 24);
			Assert.True(processingHeaderBounds.Bottom < ignoreHeaderBounds.Top);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task SettingsLists_PointerOverDoesNotHighlightVirtualizedRows()
    {
        using var project = UiTestProject.CreateWithRootExtensionIgnoreStressWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            foreach (var listName in new[] { "IgnoreOptionsList", "ExtensionsList" })
            {
                var listBox = UiTestDriver.GetRequiredControl<ListBox>(window, listName);
                var firstItem = Assert.IsAssignableFrom<object>(listBox.Items.FirstOrDefault());
                listBox.ScrollIntoView(firstItem);
                await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
                var item = Assert.IsType<ListBoxItem>(
                    listBox.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault());

                window.MouseMove(UiTestDriver.GetControlCenter(item, window), RawInputModifiers.None);
                await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);

                var presenter = Assert.IsType<ContentPresenter>(
                    item.GetVisualDescendants()
                        .OfType<ContentPresenter>()
                        .FirstOrDefault(control =>
                            string.Equals(control.Name, "PART_ContentPresenter", StringComparison.Ordinal)));
                var background = Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background);

                Assert.True(item.IsPointerOver, $"Pointer did not enter settings list '{listName}'.");
                Assert.Equal(Colors.Transparent, background.Color);
            }
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ViewTreeFontMenu_UsesDynamicItemsWithPendingCheck()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);
            var defaultFont = FontFamily.Default;
            var customFont = new FontFamily("Consolas");

            viewModel.FontFamilies.Clear();
            viewModel.FontFamilies.Add(defaultFont);
            viewModel.FontFamilies.Add(customFont);
            viewModel.SelectedFontFamily = defaultFont;
            viewModel.PendingFontFamily = defaultFont;

            InvokeRefreshTreeFontMenu(window);

            var fontMenu = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "TreeFontMenuItem");
            Assert.Equal(viewModel.MenuViewTreeFont, fontMenu.Header);

            var initialItems = fontMenu.Items.OfType<MenuItem>().ToArray();
            Assert.Equal(2, initialItems.Length);
            Assert.StartsWith("✓ ", initialItems[0].Header?.ToString());
            Assert.Contains(viewModel.SettingsFontDefault, initialItems[0].Header?.ToString());
            Assert.StartsWith("   ", initialItems[1].Header?.ToString());

            await UiTestDriver.RaiseMenuItemClickAsync(initialItems[1]);

            Assert.Equal(customFont.Name, viewModel.PendingFontFamily?.Name);
            Assert.Equal(defaultFont.Name, viewModel.SelectedFontFamily?.Name);

            InvokeRefreshTreeFontMenu(window);
            var refreshedItems = fontMenu.Items.OfType<MenuItem>().ToArray();
            Assert.StartsWith("   ", refreshedItems[0].Header?.ToString());
            Assert.StartsWith("✓ ", refreshedItems[1].Header?.ToString());
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task LanguageMenu_ShowsCheckOnCurrentLanguage()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var englishItem = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "LanguageEnMenuItem");
            var russianItem = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "LanguageRuMenuItem");

            Assert.StartsWith("✓ ", englishItem.Header?.ToString());
            Assert.StartsWith("   ", russianItem.Header?.ToString());

            await UiTestDriver.RaiseMenuItemClickAsync(russianItem);

            Assert.StartsWith("   ", englishItem.Header?.ToString());
            Assert.StartsWith("✓ ", russianItem.Header?.ToString());
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
            var settingsIsland = UiTestDriver.GetRequiredControl<Border>(window, "SettingsIsland");
            var settingsPanel = UiTestDriver.GetRequiredControl<SettingsPanelView>(window, "SettingsPanel");
            var requiredMinimum = settingsPanel.GetRequiredMinimumWidth();

            await UiTestDriver.DragAsync(window, splitter, deltaX: 2_000);
            var collapsedWidth = UiTestDriver.GetBoundsInWindow(settingsIsland, window).Width;

            await UiTestDriver.DragAsync(window, splitter, deltaX: -2_000);
            var expandedWidth = UiTestDriver.GetBoundsInWindow(settingsIsland, window).Width;

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

    private static void InvokeRefreshTreeFontMenu(MainWindow window)
    {
        var method = typeof(MainWindow).GetMethod(
            "RefreshTreeFontMenu",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(method);
        method.Invoke(window, []);
    }

    private static void AssertMatricesClose(Matrix expected, Matrix actual)
    {
        Assert.True(
            AreMatricesClose(expected, actual),
            $"Expected transform {expected}, actual {actual}.");
    }

    private static bool AreMatricesClose(Matrix expected, Matrix actual)
    {
        const double tolerance = 0.001;
        return Math.Abs(expected.M11 - actual.M11) <= tolerance &&
               Math.Abs(expected.M12 - actual.M12) <= tolerance &&
               Math.Abs(expected.M21 - actual.M21) <= tolerance &&
               Math.Abs(expected.M22 - actual.M22) <= tolerance &&
               Math.Abs(expected.M31 - actual.M31) <= tolerance &&
               Math.Abs(expected.M32 - actual.M32) <= tolerance;
    }

    private static bool IsQuarterTurn(Matrix matrix)
    {
        const double tolerance = 0.001;
        return Math.Abs(matrix.M11) <= tolerance &&
               Math.Abs(matrix.M12 - 1.0) <= tolerance &&
               Math.Abs(matrix.M21 + 1.0) <= tolerance &&
               Math.Abs(matrix.M22) <= tolerance;
    }
}
