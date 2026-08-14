using DevProjex.Kernel.Contracts;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using DevProjex.Avalonia.Controls;
using DevProjex.Avalonia.Coordinators;
using System.Reflection;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowSearchFilterUiTests(UiWorkspaceFixture workspace)
{
    [AvaloniaFact]
    public async Task SearchHotkey_OpensSearchAndFindsMatches()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenSearchAsync(window);

            var searchBar = UiTestDriver.GetRequiredControl<SearchBarView>(window, "SearchBar");
            var searchBox = Assert.IsType<TextBox>(searchBar.SearchBoxControl);
            await UiTestDriver.EnterTextAsync(window, searchBox, "app");
            await UiTestDriver.WaitForSearchAppliedAsync(window, "app");

            var viewModel = UiTestDriver.GetViewModel(window);
            Assert.True(viewModel.SearchVisible);
            Assert.True(viewModel.SearchTotalMatches > 0);

            await UiTestDriver.PressKeyAsync(window, Key.Escape);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => !UiTestDriver.GetViewModel(window).SearchVisible,
                "search bar to close");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task SearchNextButton_DoesNotRevealAdditionalBranchesAfterSearchSettles()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenSearchAsync(window);
            var searchBar = UiTestDriver.GetRequiredControl<SearchBarView>(
                window,
                "SearchBar");
            await UiTestDriver.EnterTextAsync(
                window,
                Assert.IsType<TextBox>(searchBar.SearchBoxControl),
                "app");
            await UiTestDriver.WaitForSearchAppliedAsync(window, "app");

            var viewModel = UiTestDriver.GetViewModel(window);
            Assert.True(viewModel.SearchTotalMatches > 1);
            var root = Assert.Single(viewModel.TreeNodes);
            var realizedBeforeNavigation = CollectRealizedPaths(root);
            var expandedBeforeNavigation = CollectExpandedPaths(root);
            var nextButton = Assert.IsType<Button>(
                searchBar.FindControl<Button>("SearchNextButton"));

            for (var index = 0;
                 index < Math.Min(5, viewModel.SearchTotalMatches - 1);
                 index++)
            {
                nextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
            }

            Assert.Equal(
                realizedBeforeNavigation,
                CollectRealizedPaths(root));
            Assert.Equal(
                expandedBeforeNavigation,
                CollectExpandedPaths(root));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task FilterToggleButton_OpensFilterAndFiltersTree()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenFilterAsync(window);

            var filterBar = UiTestDriver.GetRequiredControl<FilterBarView>(window, "FilterBar");
            var filterBox = Assert.IsType<TextBox>(filterBar.FilterBoxControl);
            await UiTestDriver.EnterTextAsync(window, filterBox, "app");
            await UiTestDriver.WaitForFilterAppliedAsync(window, "app");

            var viewModel = UiTestDriver.GetViewModel(window);
            Assert.True(viewModel.FilterVisible);
            Assert.True(viewModel.FilterMatchCount > 0);
            Assert.NotEmpty(viewModel.TreeNodes);

            await UiTestDriver.PressKeyAsync(window, Key.Escape);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => !UiTestDriver.GetViewModel(window).FilterVisible,
                "filter bar to close");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task InteractiveFilter_PreservesHiddenSelectionAndRestoresEntryExpansion()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);
            var root = Assert.Single(viewModel.TreeNodes);
            root.IsChecked = false;
            var readme = Assert.Single(
                root.Children,
                node => string.Equals(node.DisplayName, "README.md", StringComparison.Ordinal));
            var docs = Assert.Single(
                root.Children,
                node => string.Equals(node.DisplayName, "docs", StringComparison.Ordinal));
            var source = Assert.Single(
                root.Children,
                node => string.Equals(node.DisplayName, "src", StringComparison.Ordinal));
            var previewService = Assert.Single(
                root.Flatten(),
                node => string.Equals(node.DisplayName, "PreviewService.cs", StringComparison.Ordinal));
            readme.IsChecked = true;
            previewService.IsChecked = true;
            docs.IsExpanded = true;
            source.IsExpanded = false;
            var expectedReadmePath = readme.FullPath;

            await UiTestDriver.OpenFilterAsync(window);
            var filterBar = UiTestDriver.GetRequiredControl<FilterBarView>(window, "FilterBar");
            await UiTestDriver.EnterTextAsync(
                window,
                Assert.IsType<TextBox>(filterBar.FilterBoxControl),
                "PreviewService");
            await UiTestDriver.WaitForFilterAppliedAsync(window, "PreviewService");

            var filteredRoot = Assert.Single(viewModel.TreeNodes);
            var filteredPreviewService = Assert.Single(
                filteredRoot.Flatten(),
                node => string.Equals(node.DisplayName, "PreviewService.cs", StringComparison.Ordinal));
            Assert.True(filteredPreviewService.IsChecked);
            filteredPreviewService.IsChecked = false;

            await UiTestDriver.PressKeyAsync(window, Key.Escape);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => !viewModel.FilterVisible &&
                      !viewModel.IsFilterInProgress &&
                      string.IsNullOrEmpty(viewModel.NameFilter),
                "filter close to restore selection and expansion");

            var restoredRoot = Assert.Single(viewModel.TreeNodes);
            Assert.Equal([expectedReadmePath], UiTestDriver.GetCheckedTreePaths(window));
            Assert.True(Assert.Single(
                restoredRoot.Children,
                node => string.Equals(node.DisplayName, "docs", StringComparison.Ordinal)).IsExpanded);
            Assert.False(Assert.Single(
                restoredRoot.Children,
                node => string.Equals(node.DisplayName, "src", StringComparison.Ordinal)).IsExpanded);
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
    public async Task SearchAndFilter_AreMutuallyExclusiveWhenSwitchingTools()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenSearchAsync(window);
            Assert.True(UiTestDriver.GetViewModel(window).SearchVisible);

            await UiTestDriver.OpenFilterAsync(window);

            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var viewModel = UiTestDriver.GetViewModel(window);
                    return viewModel.FilterVisible &&
                           !viewModel.SearchVisible &&
                           UiTestDriver.GetRequiredControl<Border>(window, "FilterBarContainer").IsVisible &&
                           !UiTestDriver.GetRequiredControl<Border>(window, "SearchBarContainer").IsVisible;
                },
                "filter to replace search");

            await UiTestDriver.OpenSearchAsync(window);

            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var viewModel = UiTestDriver.GetViewModel(window);
                    return viewModel.SearchVisible &&
                           !viewModel.FilterVisible &&
                           UiTestDriver.GetRequiredControl<Border>(window, "SearchBarContainer").IsVisible &&
                           !UiTestDriver.GetRequiredControl<Border>(window, "FilterBarContainer").IsVisible;
                },
                "search to replace filter");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task SearchAndFilter_OpenFocusesInputTextBox()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenSearchAsync(window);

            var searchBar = UiTestDriver.GetRequiredControl<SearchBarView>(window, "SearchBar");
            var searchBox = Assert.IsType<TextBox>(searchBar.SearchBoxControl);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => searchBox.IsFocused,
                "search textbox to receive focus after opening");

            await UiTestDriver.OpenFilterAsync(window);

            var filterBar = UiTestDriver.GetRequiredControl<FilterBarView>(window, "FilterBar");
            var filterBox = Assert.IsType<TextBox>(filterBar.FilterBoxControl);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => filterBox.IsFocused,
                "filter textbox to receive focus after opening");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task SearchHotkey_IsIgnoredInPreviewOnly()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.HidePreviewTreeAsync(window);

            await UiTestDriver.PressKeyAsync(window, Key.F, RawInputModifiers.Control);
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 10);

            var viewModel = UiTestDriver.GetViewModel(window);
            Assert.False(viewModel.SearchVisible);
            Assert.False(UiTestDriver.GetRequiredControl<Border>(window, "SearchBarContainer").IsVisible);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task SearchClose_AfterNoMatchesRestoresRootAndClearsSearchState()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenSearchAsync(window);
            var searchBar = UiTestDriver.GetRequiredControl<SearchBarView>(window, "SearchBar");
            var searchBox = Assert.IsType<TextBox>(searchBar.SearchBoxControl);
            const string query = "__definitely_missing__";
            await UiTestDriver.EnterTextAsync(window, searchBox, query);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var viewModel = UiTestDriver.GetViewModel(window);
                    return viewModel.SearchQuery == query &&
                           !viewModel.IsSearchInProgress &&
                           viewModel.SearchTotalMatches == 0;
                },
                "missing search query to settle");

            var root = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
            Assert.False(root.IsExpanded);

            await UiTestDriver.PressKeyAsync(window, Key.Escape);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var viewModel = UiTestDriver.GetViewModel(window);
                    return !viewModel.SearchVisible &&
                           string.IsNullOrEmpty(viewModel.SearchQuery) &&
                           root.IsExpanded;
                },
                "search close to normalize the tree");

            Assert.Equal(0, UiTestDriver.GetViewModel(window).SearchTotalMatches);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task SearchClose_QueryClearedBeforeDebounce_RestoresAppliedSearchAndLeavesNoStaleHighlights()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenSearchAsync(window);
            var searchBar = UiTestDriver.GetRequiredControl<SearchBarView>(window, "SearchBar");
            var searchBox = Assert.IsType<TextBox>(searchBar.SearchBoxControl);
            await UiTestDriver.EnterTextAsync(window, searchBox, "PreviewService");
            await UiTestDriver.WaitForSearchAppliedAsync(window, "PreviewService");

            searchBox.Text = string.Empty;
            await GetSearchFilterController(window).CloseSearchAsync();
            await Task.Delay(UiTimingProfile.Scale(TimeSpan.FromMilliseconds(600)));
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);

            var viewModel = UiTestDriver.GetViewModel(window);
            var root = Assert.Single(viewModel.TreeNodes);
            Assert.False(viewModel.SearchVisible);
            Assert.False(viewModel.IsSearchInProgress);
            Assert.Equal(0, viewModel.SearchTotalMatches);
            Assert.True(root.IsExpanded);
            TreeNodeViewModel.ForEachRealizedDescendant(
                viewModel.TreeNodes,
                node =>
                {
                    Assert.False(node.HasHighlightedDisplay);
                    Assert.False(node.IsCurrentSearchMatch);
                });
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task SearchClose_ConcurrentCallsShareOneCleanupAndRemoveEveryBatchedHighlight()
    {
        const int childCount = 600;
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);
            var root = CreateFlatSearchTree(childCount, "close-batch-match");
            viewModel.TreeNodes.Clear();
            viewModel.TreeNodes.Add(root);
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
            await UiTestDriver.OpenSearchAsync(window);

            var searchBar = UiTestDriver.GetRequiredControl<SearchBarView>(window, "SearchBar");
            var searchBox = Assert.IsType<TextBox>(searchBar.SearchBoxControl);
            searchBox.Text = "close-batch-match";
            var controller = GetSearchFilterController(window);
            controller.CancelPending();
            controller.UpdateSearchMatches();
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            Assert.All(root.Children, node => Assert.True(node.HasHighlightedDisplay));

            var firstClose = controller.CloseSearchAsync();
            var secondClose = controller.CloseSearchAsync();

            Assert.Same(firstClose, secondClose);
            await Task.WhenAll(firstClose, secondClose);
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);

            Assert.Equal(0, viewModel.SearchTotalMatches);
            Assert.All(root.Children, node =>
            {
                Assert.False(node.HasHighlightedDisplay);
                Assert.False(node.IsCurrentSearchMatch);
            });
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task FilterClose_QueryClearedBeforeDebounce_RestoresSavedExpansionSnapshot()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);
            var baselineRoot = Assert.Single(viewModel.TreeNodes);
            var baselineCount = CountDescriptorNodes(baselineRoot.Descriptor);
            await UiTestDriver.OpenFilterAsync(window);
            var filterBar = UiTestDriver.GetRequiredControl<FilterBarView>(window, "FilterBar");
            var filterBox = Assert.IsType<TextBox>(filterBar.FilterBoxControl);
            await UiTestDriver.EnterTextAsync(window, filterBox, "PreviewService");
            await UiTestDriver.WaitForFilterAppliedAsync(window, "PreviewService");
            Assert.True(CountDescriptorNodes(Assert.Single(viewModel.TreeNodes).Descriptor) < baselineCount);

            filterBox.Text = string.Empty;
            await GetSearchFilterController(window).CloseFilterAsync();
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);

            Assert.False(viewModel.FilterVisible);
            Assert.False(viewModel.IsFilterInProgress);
            Assert.Equal(baselineCount, CountDescriptorNodes(Assert.Single(viewModel.TreeNodes).Descriptor));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task FilterClose_ConcurrentCallsShareOneRestoreOperation()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenFilterAsync(window);
            var filterBar = UiTestDriver.GetRequiredControl<FilterBarView>(window, "FilterBar");
            await UiTestDriver.EnterTextAsync(
                window,
                Assert.IsType<TextBox>(filterBar.FilterBoxControl),
                "PreviewService");
            await UiTestDriver.WaitForFilterAppliedAsync(window, "PreviewService");

            var controller = GetSearchFilterController(window);
            var firstClose = controller.CloseFilterAsync();
            var secondClose = controller.CloseFilterAsync();

            Assert.Same(firstClose, secondClose);
            await Task.WhenAll(firstClose, secondClose);

            var viewModel = UiTestDriver.GetViewModel(window);
            Assert.False(viewModel.FilterVisible);
            Assert.False(viewModel.IsFilterInProgress);
            Assert.Equal(string.Empty, viewModel.NameFilter);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ClearProjectState_CancelsPendingSearchBeforeReplacementTreeCanReceiveItsResult()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenSearchAsync(window);
            var searchBar = UiTestDriver.GetRequiredControl<SearchBarView>(window, "SearchBar");
            var searchBox = Assert.IsType<TextBox>(searchBar.SearchBoxControl);
            const string pendingQuery = "pending-switch-target";
            searchBox.Text = pendingQuery;
            Assert.Equal(pendingQuery, UiTestDriver.GetViewModel(window).SearchQuery);

            var controller = GetSearchFilterController(window);
            controller.ClearProjectState();

            var childDescriptor = new TreeNodeDescriptor(
                "pending-switch-target.cs",
                @"C:\Replacement\pending-switch-target.cs",
                IsDirectory: false,
                IsAccessDenied: false,
                IconKey: "text",
                Children: []);
            var rootDescriptor = new TreeNodeDescriptor(
                "Replacement",
                @"C:\Replacement",
                IsDirectory: true,
                IsAccessDenied: false,
                IconKey: "folder",
                Children: [childDescriptor]);
            var replacementRoot = new TreeNodeViewModel(rootDescriptor, null, null);
            var replacementChild = new TreeNodeViewModel(childDescriptor, replacementRoot, null);
            replacementRoot.Children.Add(replacementChild);

            var viewModel = UiTestDriver.GetViewModel(window);
            viewModel.TreeNodes.Clear();
            viewModel.TreeNodes.Add(replacementRoot);

            await Task.Delay(UiTimingProfile.Scale(TimeSpan.FromMilliseconds(650)));
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);

            Assert.Equal(0, viewModel.SearchTotalMatches);
            Assert.False(viewModel.IsSearchInProgress);
            Assert.False(replacementRoot.IsExpanded);
            Assert.False(replacementChild.HasHighlightedDisplay);
            Assert.False(replacementChild.IsCurrentSearchMatch);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ClearProjectState_InvalidatesQueuedEmptySearchWorkBeforeTreeReplacement()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenSearchAsync(window);
            var searchBar = UiTestDriver.GetRequiredControl<SearchBarView>(window, "SearchBar");
            var searchBox = Assert.IsType<TextBox>(searchBar.SearchBoxControl);
            await UiTestDriver.EnterTextAsync(window, searchBox, "PreviewService");
            await UiTestDriver.WaitForSearchAppliedAsync(window, "PreviewService");

            searchBox.Text = string.Empty;
            Thread.Sleep(
                UiTimingProfile.Scale(TimeSpan.FromMilliseconds(550)) +
                TimeSpan.FromMilliseconds(25));

            var controller = GetSearchFilterController(window);
            controller.ClearProjectState();
            var replacementRoot = CreateFlatSearchTree(1, "replacement-target");
            var replacementChild = Assert.Single(replacementRoot.Children);
            var viewModel = UiTestDriver.GetViewModel(window);
            viewModel.TreeNodes.Clear();
            viewModel.TreeNodes.Add(replacementRoot);

            await Task.Delay(UiTimingProfile.Scale(TimeSpan.FromMilliseconds(650)));
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);

            Assert.Equal(0, viewModel.SearchTotalMatches);
            Assert.False(replacementRoot.IsExpanded);
            Assert.False(replacementChild.HasHighlightedDisplay);
            Assert.False(replacementChild.IsCurrentSearchMatch);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task SearchNavigation_NarrowPreview_RevealsClippedResultWithoutSnappingBackForVisibleSibling()
    {
        using var project =
            UiTestProject.CreateWithDeepHorizontalSearchWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            window.Width = Math.Max(window.MinWidth, 900);
            window.Height = window.MinHeight;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.OpenSearchAsync(window);
            var tree =
                UiTestDriver.GetRequiredControl<ProjectTreeView>(
                    window,
                    "ProjectTree");
            var scrollViewer = Assert.IsType<ScrollViewer>(
                tree.FindDescendantOfType<ScrollViewer>());
            scrollViewer.Offset =
                new Vector(0, scrollViewer.Offset.Y);
            var searchBar =
                UiTestDriver.GetRequiredControl<SearchBarView>(
                    window,
                    "SearchBar");
            var searchBox = Assert.IsType<TextBox>(
                searchBar.SearchBoxControl);
            const string query = "horizontal-search-target";
            var automaticHorizontalOffsets = new List<double>();
            void RecordAutomaticHorizontalOffset(
                object? _,
                ScrollChangedEventArgs __) =>
                automaticHorizontalOffsets.Add(scrollViewer.Offset.X);

            scrollViewer.ScrollChanged += RecordAutomaticHorizontalOffset;
            try
            {
                await UiTestDriver.EnterTextAsync(window, searchBox, query);
                await UiTestDriver.WaitForSearchAppliedAsync(window, query);
                await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);
            }
            finally
            {
                scrollViewer.ScrollChanged -= RecordAutomaticHorizontalOffset;
            }

            Assert.InRange(Math.Abs(scrollViewer.Offset.X), 0, 0.5);
            Assert.All(
                automaticHorizontalOffsets,
                offset => Assert.InRange(Math.Abs(offset), 0, 0.5));

            await UiTestDriver.PressKeyAsync(window, Key.Enter);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var bounds = GetSelectedTreeItemHorizontalBounds(
                        tree,
                        scrollViewer);
                    return bounds is { } current &&
                           current.Left >= -1 &&
                           (current.ContentWidth >
                            current.ViewportWidth
                               ? current.Left <= 1
                               : current.Right <=
                                 current.ViewportWidth + 1);
                },
                "deep search result to become horizontally visible");

            var shiftedOffsetX = scrollViewer.Offset.X;
            Assert.True(shiftedOffsetX > 0);

            var navigationOffsets = new List<double>();
            void RecordNavigationOffset(
                object? _,
                ScrollChangedEventArgs __) =>
                navigationOffsets.Add(scrollViewer.Offset.X);

            scrollViewer.ScrollChanged += RecordNavigationOffset;
            try
            {
                var nextButton = Assert.IsType<Button>(
                    searchBar.FindControl<Button>("SearchNextButton"));
                nextButton.RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent));
                await UiTestDriver.WaitForConditionAsync(
                    window,
                    () => tree.SelectedItem is TreeNodeViewModel
                    {
                        DisplayName:
                        "horizontal-search-target-c-short.cs"
                    },
                    "next visible sibling search result to be selected");
                await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);
            }
            finally
            {
                scrollViewer.ScrollChanged -= RecordNavigationOffset;
            }

            Assert.InRange(
                Math.Abs(scrollViewer.Offset.X - shiftedOffsetX),
                0,
                0.5);
            Assert.All(
                navigationOffsets,
                offset => Assert.InRange(
                    Math.Abs(offset - shiftedOffsetX),
                    0,
                    0.5));
            AssertTreeItemIsHorizontallyVisible(tree, scrollViewer);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task SearchNavigation_WideTreePane_DoesNotResetHorizontalPositionBetweenVisibleResults()
    {
        using var project =
            UiTestProject.CreateWithDeepHorizontalSearchWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            window.Width = Math.Max(window.MinWidth, 900);
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
            await UiTestDriver.OpenSearchAsync(window);
            var tree =
                UiTestDriver.GetRequiredControl<ProjectTreeView>(
                    window,
                    "ProjectTree");
            var scrollViewer = Assert.IsType<ScrollViewer>(
                tree.FindDescendantOfType<ScrollViewer>());
            scrollViewer.Offset = new Vector(0, scrollViewer.Offset.Y);
            var searchBar =
                UiTestDriver.GetRequiredControl<SearchBarView>(
                    window,
                    "SearchBar");
            const string query = "horizontal-search-target";
            await UiTestDriver.EnterTextAsync(
                window,
                Assert.IsType<TextBox>(searchBar.SearchBoxControl),
                query);
            await UiTestDriver.WaitForSearchAppliedAsync(window, query);

            await UiTestDriver.PressKeyAsync(window, Key.Enter);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => scrollViewer.Offset.X > 0,
                "wide tree search result to require horizontal scrolling");
            var shiftedOffsetX = scrollViewer.Offset.X;

            var navigationOffsets = new List<double>();
            void RecordNavigationOffset(
                object? _,
                ScrollChangedEventArgs __) =>
                navigationOffsets.Add(scrollViewer.Offset.X);

            scrollViewer.ScrollChanged += RecordNavigationOffset;
            try
            {
                var nextButton = Assert.IsType<Button>(
                    searchBar.FindControl<Button>("SearchNextButton"));
                nextButton.RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent));
                await UiTestDriver.WaitForConditionAsync(
                    window,
                    () => tree.SelectedItem is TreeNodeViewModel
                    {
                        DisplayName:
                        "horizontal-search-target-c-short.cs"
                    },
                    "wide tree sibling search result to be selected");
                await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);
            }
            finally
            {
                scrollViewer.ScrollChanged -= RecordNavigationOffset;
            }

            Assert.InRange(
                Math.Abs(scrollViewer.Offset.X - shiftedOffsetX),
                0,
                0.5);
            Assert.All(
                navigationOffsets,
                offset => Assert.InRange(
                    Math.Abs(offset - shiftedOffsetX),
                    0,
                    0.5));
            AssertTreeItemIsHorizontallyVisible(tree, scrollViewer);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task SearchNavigation_NarrowPreview_KeepsCurrentResultInsideVerticalComfortZone()
    {
        using var project =
            UiTestProject.CreateWithLargeFlatTree(fileCount: 120);
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            window.Width = Math.Max(window.MinWidth, 900);
            window.Height = window.MinHeight;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.OpenSearchAsync(window);
            var tree =
                UiTestDriver.GetRequiredControl<ProjectTreeView>(
                    window,
                    "ProjectTree");
            var scrollViewer = Assert.IsType<ScrollViewer>(
                tree.FindDescendantOfType<ScrollViewer>());
            var searchBar =
                UiTestDriver.GetRequiredControl<SearchBarView>(
                    window,
                    "SearchBar");
            const string query = "file-0060";
            await UiTestDriver.EnterTextAsync(
                window,
                Assert.IsType<TextBox>(searchBar.SearchBoxControl),
                query);
            await UiTestDriver.WaitForSearchAppliedAsync(window, query);

            var nextButton = Assert.IsType<Button>(
                searchBar.FindControl<Button>("SearchNextButton"));
            nextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var bounds = GetSelectedTreeItemVerticalBounds(
                        tree,
                        scrollViewer);
                    return bounds is { } current &&
                           IsInsideVerticalComfortZone(current);
                },
                "selected search result to enter the vertical comfort zone");
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);

            var positionedBounds = Assert.NotNull(
                GetSelectedTreeItemVerticalBounds(tree, scrollViewer));
            Assert.True(IsInsideVerticalComfortZone(positionedBounds));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task SearchNavigation_AdjacentResultsInsideComfortZone_DoNotMoveVerticalOffset()
    {
        using var project =
            UiTestProject.CreateWithLargeFlatTree(fileCount: 120);
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            window.Width = Math.Max(window.MinWidth, 900);
            window.Height = window.MinHeight;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.OpenSearchAsync(window);
            var tree =
                UiTestDriver.GetRequiredControl<ProjectTreeView>(
                    window,
                    "ProjectTree");
            var scrollViewer = Assert.IsType<ScrollViewer>(
                tree.FindDescendantOfType<ScrollViewer>());
            var searchBar =
                UiTestDriver.GetRequiredControl<SearchBarView>(
                    window,
                    "SearchBar");
            const string query = "file-004";
            await UiTestDriver.EnterTextAsync(
                window,
                Assert.IsType<TextBox>(searchBar.SearchBoxControl),
                query);
            await UiTestDriver.WaitForSearchAppliedAsync(window, query);
            var nextButton = Assert.IsType<Button>(
                searchBar.FindControl<Button>("SearchNextButton"));

            nextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var bounds = GetSelectedTreeItemVerticalBounds(
                        tree,
                        scrollViewer);
                    return tree.SelectedItem is TreeNodeViewModel
                           {
                               DisplayName: "file-0041.txt"
                           } &&
                           bounds is { } current &&
                           IsInsideVerticalComfortZone(current);
                },
                "first navigated result to enter the vertical comfort zone");
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);

            var stableOffsetY = scrollViewer.Offset.Y;
            var navigationOffsets = new List<double>();
            void RecordNavigationOffset(
                object? _,
                ScrollChangedEventArgs __) =>
                navigationOffsets.Add(scrollViewer.Offset.Y);

            scrollViewer.ScrollChanged += RecordNavigationOffset;
            try
            {
                for (var index = 42; index <= 44; index++)
                {
                    nextButton.RaiseEvent(
                        new RoutedEventArgs(Button.ClickEvent));
                    var expectedName = $"file-{index:0000}.txt";
                    await UiTestDriver.WaitForConditionAsync(
                        window,
                        () => tree.SelectedItem is TreeNodeViewModel node &&
                              string.Equals(
                                  node.DisplayName,
                                  expectedName,
                                  StringComparison.Ordinal),
                        $"search result '{expectedName}' to be selected");
                    await UiTestDriver.WaitForSettledFramesAsync(
                        frameCount: 2);
                }
            }
            finally
            {
                scrollViewer.ScrollChanged -= RecordNavigationOffset;
            }

            Assert.InRange(
                Math.Abs(scrollViewer.Offset.Y - stableOffsetY),
                0,
                0.5);
            Assert.All(
                navigationOffsets,
                offset => Assert.InRange(
                    Math.Abs(offset - stableOffsetY),
                    0,
                    0.5));
            Assert.True(IsInsideVerticalComfortZone(Assert.NotNull(
                GetSelectedTreeItemVerticalBounds(tree, scrollViewer))));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task SearchNavigation_VisibleHierarchicalSibling_DoesNotSnapToViewportTop()
    {
        using var project =
            UiTestProject.CreateWithHierarchicalAppSearchWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            window.Width = Math.Max(window.MinWidth, 900);
            window.Height = window.MinHeight;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.OpenSearchAsync(window);
            var tree =
                UiTestDriver.GetRequiredControl<ProjectTreeView>(
                    window,
                    "ProjectTree");
            var scrollViewer = Assert.IsType<ScrollViewer>(
                tree.FindDescendantOfType<ScrollViewer>());
            var searchBar =
                UiTestDriver.GetRequiredControl<SearchBarView>(
                    window,
                    "SearchBar");
            const string query = "App";
            await UiTestDriver.EnterTextAsync(
                window,
                Assert.IsType<TextBox>(searchBar.SearchBoxControl),
                query);
            await UiTestDriver.WaitForSearchAppliedAsync(window, query);

            var root = Assert.Single(
                UiTestDriver.GetViewModel(window).TreeNodes);
            var appsNode = root.Children.Single(
                node => string.Equals(
                    node.DisplayName,
                    "Apps",
                    StringComparison.Ordinal));
            tree.ScrollIntoView(appsNode);
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);

            var appsContainer = Assert.IsAssignableFrom<TreeViewItem>(
                tree.TreeContainerFromItem(appsNode));
            var appsTop = Assert.NotNull(
                appsContainer.TranslatePoint(default, scrollViewer)).Y;
            var centeredOffset = Math.Clamp(
                scrollViewer.Offset.Y +
                appsTop -
                (scrollViewer.Viewport.Height * 0.45),
                0,
                Math.Max(
                    0,
                    scrollViewer.Extent.Height -
                    scrollViewer.Viewport.Height));
            scrollViewer.Offset = new Vector(
                scrollViewer.Offset.X,
                centeredOffset);
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);

            var initialOffsetY = scrollViewer.Offset.Y;
            var nextButton = Assert.IsType<Button>(
                searchBar.FindControl<Button>("SearchNextButton"));
            var navigationOffsets = new List<double>();
            void RecordNavigationOffset(
                object? _,
                ScrollChangedEventArgs __) =>
                navigationOffsets.Add(scrollViewer.Offset.Y);

            scrollViewer.ScrollChanged += RecordNavigationOffset;
            try
            {
                nextButton.RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent));
                await UiTestDriver.WaitForConditionAsync(
                    window,
                    () => tree.SelectedItem is TreeNodeViewModel
                    {
                        DisplayName: "Application.csproj"
                    },
                    "Application.csproj search result to be selected");
                await UiTestDriver.WaitForSettledFramesAsync(
                    frameCount: 4);

                nextButton.RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent));
                await UiTestDriver.WaitForConditionAsync(
                    window,
                    () => ReferenceEquals(tree.SelectedItem, appsNode),
                    "Apps search result to be selected");
                await UiTestDriver.WaitForSettledFramesAsync(
                    frameCount: 8);
            }
            finally
            {
                scrollViewer.ScrollChanged -= RecordNavigationOffset;
            }

            var selectedBounds = Assert.NotNull(
                GetSelectedTreeItemVerticalBounds(
                    tree,
                    scrollViewer));
            Assert.InRange(
                selectedBounds.Top,
                selectedBounds.ViewportHeight * 0.2,
                selectedBounds.ViewportHeight * 0.7);
            Assert.InRange(
                Math.Abs(scrollViewer.Offset.Y - initialOffsetY),
                0,
                0.5);
            Assert.All(
                navigationOffsets,
                offset => Assert.InRange(
                    Math.Abs(offset - initialOffsetY),
                    0,
                    0.5));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task RepeatedSearchCycles_DoNotMaterializeAdditionalViewModelBranches()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var root = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
            var expectedRetainedPaths = new HashSet<string>(
                CollectRealizedPaths(root),
                PathComparer.Default);
            AddMatchingDescriptorPaths(
                root.Descriptor,
                "PreviewService",
                expectedRetainedPaths);
            await ApplyAndCloseSearchAsync(window, "PreviewService");

            var realizedAfterFirstCycle = CollectRealizedPaths(root);
            Assert.True(
                expectedRetainedPaths.SetEquals(realizedAfterFirstCycle),
                $"Expected: {string.Join(", ", expectedRetainedPaths.Order(PathComparer.Default))}{Environment.NewLine}" +
                $"Actual: {string.Join(", ", realizedAfterFirstCycle)}");

            for (var cycle = 0; cycle < 4; cycle++)
                await ApplyAndCloseSearchAsync(window, "PreviewService");

            var realizedAfterRepeatedCycles = CollectRealizedPaths(root);
            Assert.True(
                realizedAfterFirstCycle.SequenceEqual(realizedAfterRepeatedCycles),
                $"Initial: {string.Join(", ", realizedAfterFirstCycle)}{Environment.NewLine}" +
                $"Repeated: {string.Join(", ", realizedAfterRepeatedCycles)}");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task SearchClose_WithPreviewOpen_PreservesPreviewAndReleasesSearchOnlyBranches()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);
            var viewModel = UiTestDriver.GetViewModel(window);
            var initialRoot = Assert.Single(viewModel.TreeNodes);
            var previewDocument = viewModel.PreviewDocument;
            Assert.NotNull(previewDocument);

            await ApplyAndCloseSearchAsync(window, "PreviewService");
            var realizedAfterFirstClose = CollectRealizedPaths(initialRoot);

            Assert.True(viewModel.IsPreviewMode);
            Assert.False(viewModel.IsPreviewLoading);
            Assert.Same(previewDocument, viewModel.PreviewDocument);
            Assert.Same(initialRoot, Assert.Single(viewModel.TreeNodes));

            for (var cycle = 0; cycle < 3; cycle++)
                await ApplyAndCloseSearchAsync(window, "PreviewService");

            Assert.True(viewModel.IsPreviewMode);
            Assert.False(viewModel.IsPreviewLoading);
            Assert.Same(previewDocument, viewModel.PreviewDocument);
            Assert.Same(initialRoot, Assert.Single(viewModel.TreeNodes));
            Assert.Equal(realizedAfterFirstClose, CollectRealizedPaths(initialRoot));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task FilterClose_AwaitsFullTreeRestoreAndDetachesFilteredGraph()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);
            var initialRoot = Assert.Single(viewModel.TreeNodes);
            var baselineCount = CountDescriptorNodes(initialRoot.Descriptor);
            var baselineRealizedCount = CountRealizedNodes(initialRoot);
            await UiTestDriver.OpenFilterAsync(window);
            var filterBar = UiTestDriver.GetRequiredControl<FilterBarView>(window, "FilterBar");
            await UiTestDriver.EnterTextAsync(
                window,
                Assert.IsType<TextBox>(filterBar.FilterBoxControl),
                "PreviewService");
            await UiTestDriver.WaitForFilterAppliedAsync(window, "PreviewService");

            var filteredRoot = Assert.Single(viewModel.TreeNodes);
            Assert.True(CountDescriptorNodes(filteredRoot.Descriptor) < baselineCount);

            await UiTestDriver.PressKeyAsync(window, Key.Escape);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var currentRoot = viewModel.TreeNodes.FirstOrDefault();
                    return !viewModel.FilterVisible &&
                           !viewModel.IsFilterInProgress &&
                           string.IsNullOrEmpty(viewModel.NameFilter) &&
                           currentRoot is not null &&
                           CountDescriptorNodes(currentRoot.Descriptor) == baselineCount;
                },
                "filter close to restore the complete tree");

            Assert.False(filteredRoot.HasChildren);
            Assert.Empty(filteredRoot.Children);
            Assert.Equal(
                baselineRealizedCount,
                CountRealizedNodes(Assert.Single(viewModel.TreeNodes)));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    private static async Task ApplyAndCloseSearchAsync(MainWindow window, string query)
    {
        await UiTestDriver.OpenSearchAsync(window);
        var searchBar = UiTestDriver.GetRequiredControl<SearchBarView>(window, "SearchBar");
        await UiTestDriver.EnterTextAsync(
            window,
            Assert.IsType<TextBox>(searchBar.SearchBoxControl),
            query);
        await UiTestDriver.WaitForSearchAppliedAsync(window, query);
        await UiTestDriver.PressKeyAsync(window, Key.Escape);
        await GetSearchFilterController(window).CloseSearchAsync();
        await UiTestDriver.WaitForConditionAsync(
            window,
            () =>
            {
                var viewModel = UiTestDriver.GetViewModel(window);
                return !viewModel.SearchVisible &&
                       string.IsNullOrEmpty(viewModel.SearchQuery);
            },
            "search cycle to close");
    }

    private static int CountRealizedNodes(TreeNodeViewModel root)
    {
        var count = 0;
        TreeNodeViewModel.ForEachRealizedDescendant(
            new List<TreeNodeViewModel> { root },
            _ => count++);
        return count;
    }

    private static string[] CollectRealizedPaths(TreeNodeViewModel root)
    {
        var paths = new List<string>();
        TreeNodeViewModel.ForEachRealizedDescendant(
            new List<TreeNodeViewModel> { root },
            node => paths.Add(node.FullPath));
        paths.Sort(PathComparer.Default);
        return [.. paths];
    }

    private static string[] CollectExpandedPaths(TreeNodeViewModel root)
    {
        var paths = new List<string>();
        TreeNodeViewModel.ForEachRealizedDescendant(
            new List<TreeNodeViewModel> { root },
            node =>
            {
                if (node.IsExpanded)
                    paths.Add(node.FullPath);
            });
        paths.Sort(PathComparer.Default);
        return [.. paths];
    }

    private static int CountDescriptorNodes(TreeNodeDescriptor root)
    {
        var count = 0;
        var stack = new Stack<TreeNodeDescriptor>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            count++;
            for (var index = current.Children.Count - 1; index >= 0; index--)
                stack.Push(current.Children[index]);
        }

        return count;
    }

    private static bool AddMatchingDescriptorPaths(
        TreeNodeDescriptor node,
        string query,
        HashSet<string> paths)
    {
        var containsMatch = node.DisplayName.Contains(
            query,
            StringComparison.OrdinalIgnoreCase);
        foreach (var child in node.Children)
            containsMatch |= AddMatchingDescriptorPaths(child, query, paths);

        if (containsMatch)
            paths.Add(node.FullPath);

        return containsMatch;
    }

    private static SearchFilterInteractionController GetSearchFilterController(MainWindow window)
    {
        var field = typeof(MainWindow).GetField(
            "_searchFilterController",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return Assert.IsType<SearchFilterInteractionController>(field?.GetValue(window));
    }

    private static TreeNodeViewModel CreateFlatSearchTree(
        int childCount,
        string namePrefix)
    {
        var childDescriptors = Enumerable
            .Range(0, childCount)
            .Select(index => new TreeNodeDescriptor(
                $"{namePrefix}-{index:D4}.txt",
                $"/synthetic/{namePrefix}-{index:D4}.txt",
                IsDirectory: false,
                IsAccessDenied: false,
                IconKey: "text",
                Children: []))
            .ToArray();
        var rootDescriptor = new TreeNodeDescriptor(
            "Synthetic",
            "/synthetic",
            IsDirectory: true,
            IsAccessDenied: false,
            IconKey: "folder",
            Children: childDescriptors);
        var root = new TreeNodeViewModel(rootDescriptor, null, null);
        foreach (var childDescriptor in childDescriptors)
            root.Children.Add(new TreeNodeViewModel(childDescriptor, root, null));

        return root;
    }

    private static (
        double Left,
        double Right,
        double ContentWidth,
        double ViewportWidth)?
        GetSelectedTreeItemHorizontalBounds(
            ProjectTreeView tree,
            ScrollViewer scrollViewer)
    {
        var selectedNode = tree.SelectedItem as TreeNodeViewModel;
        if (selectedNode is null)
            return null;

        var container = tree.FindDescendantOfType<TreeViewItem>(
            includeSelf: false,
            visual => visual is TreeViewItem item &&
                      ReferenceEquals(
                          item.DataContext,
                          selectedNode));
        var content = container?.FindDescendantOfType<Control>(
            includeSelf: false,
            visual => visual is Control
            {
                Name: "TreeItemContent"
            });
        var topLeft = content?.TranslatePoint(
            default,
            scrollViewer);
        if (content is null || topLeft is null)
            return null;

        return (
            topLeft.Value.X,
            topLeft.Value.X + content.Bounds.Width,
            content.Bounds.Width,
            scrollViewer.Viewport.Width);
    }

    private static void AssertTreeItemIsHorizontallyVisible(
        ProjectTreeView tree,
        ScrollViewer scrollViewer)
    {
        var bounds = Assert.NotNull(
            GetSelectedTreeItemHorizontalBounds(tree, scrollViewer));
        Assert.True(
            bounds.Left >= -1,
            $"Selected item begins outside the viewport: {bounds.Left:F2}.");
        Assert.True(
            bounds.ContentWidth > bounds.ViewportWidth ||
            bounds.Right <= bounds.ViewportWidth + 1,
            $"Selected item ends outside the viewport: {bounds.Right:F2} > {bounds.ViewportWidth:F2}.");
    }

    private static (
        double Top,
        double Bottom,
        double ViewportHeight)?
        GetSelectedTreeItemVerticalBounds(
            ProjectTreeView tree,
            ScrollViewer scrollViewer)
    {
        var selectedNode = tree.SelectedItem as TreeNodeViewModel;
        if (selectedNode is null)
            return null;

        var container = tree.FindDescendantOfType<TreeViewItem>(
            includeSelf: false,
            visual => visual is TreeViewItem item &&
                      ReferenceEquals(
                          item.DataContext,
                          selectedNode));
        var topLeft = container?.TranslatePoint(
            default,
            scrollViewer);
        if (container is null || topLeft is null)
            return null;

        return (
            topLeft.Value.Y,
            topLeft.Value.Y + container.Bounds.Height,
            scrollViewer.Viewport.Height);
    }

    private static bool IsInsideVerticalComfortZone(
        (
            double Top,
            double Bottom,
            double ViewportHeight) bounds)
    {
        const double tolerance = 2;
        const double comfortInsetRatio = 0.2;
        var comfortTop = bounds.ViewportHeight * comfortInsetRatio;
        var comfortBottom =
            bounds.ViewportHeight * (1 - comfortInsetRatio);
        return bounds.Top >= comfortTop - tolerance &&
               bounds.Bottom <= comfortBottom + tolerance;
    }
}
