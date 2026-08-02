using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using DevProjex.Avalonia.Controls;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class ProjectTreeVirtualizationUiTests
{
    [AvaloniaFact]
    public async Task ExpandedNestedFolders_RenderAtStrictlyIncreasingHorizontalLevels()
    {
        using var project = UiTestProject.CreateDefault();
        var window =
            await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            var tree = UiTestDriver.GetRequiredControl<ProjectTreeView>(
                window,
                "ProjectTree");
            var viewModel = UiTestDriver.GetViewModel(window);
            var root = Assert.Single(viewModel.TreeNodes);
            root.IsExpanded = true;
            var src = Assert.Single(
                root.Children,
                static child => child.DisplayName == "src");
            src.IsExpanded = true;
            var appCore = Assert.Single(
                src.Children,
                static child => child.DisplayName == "AppCore");
            appCore.IsExpanded = true;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);

            var rootX = GetContentHorizontalPosition(tree, root);
            var srcX = GetContentHorizontalPosition(tree, src);
            var appCoreX = GetContentHorizontalPosition(tree, appCore);
            Assert.True(
                srcX > rootX + 8,
                $"Expected src at a deeper visual level: root={rootX}, src={srcX}.");
            Assert.True(
                appCoreX > srcX + 8,
                $"Expected AppCore at a deeper visual level: src={srcX}, AppCore={appCoreX}.");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task FolderChevron_RemainsVisibleAcrossLazyExpansionCycles()
    {
        using var project = UiTestProject.CreateDefault();
        var window =
            await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            var tree = UiTestDriver.GetRequiredControl<ProjectTreeView>(
                window,
                "ProjectTree");
            var viewModel = UiTestDriver.GetViewModel(window);
            var root = Assert.Single(viewModel.TreeNodes);
            root.IsExpanded = true;
            var folder = Assert.Single(
                root.Children,
                static child => child.DisplayName == "src");
            folder.IsExpanded = false;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
            AssertChevronVisible(tree, folder);

            folder.IsExpanded = true;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
            AssertChevronVisible(tree, folder);

            folder.IsExpanded = false;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 2);
            AssertChevronVisible(tree, folder);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task SearchResult_PreservesEveryRenderedHierarchyLevel()
    {
        using var project = UiTestProject.CreateDefault();
        var window =
            await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.OpenSearchAsync(window);
            var searchBar = UiTestDriver.GetRequiredControl<SearchBarView>(
                window,
                "SearchBar");
            await UiTestDriver.EnterTextAsync(
                window,
                Assert.IsType<TextBox>(searchBar.SearchBoxControl),
                "PreviewService");
            await UiTestDriver.WaitForSearchAppliedAsync(
                window,
                "PreviewService");

            AssertNestedPreviewServicePath(window);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task FilterResult_PreservesEveryRenderedHierarchyLevel()
    {
        using var project = UiTestProject.CreateDefault();
        var window =
            await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.OpenFilterAsync(window);
            var filterBar = UiTestDriver.GetRequiredControl<FilterBarView>(
                window,
                "FilterBar");
            await UiTestDriver.EnterTextAsync(
                window,
                Assert.IsType<TextBox>(filterBar.FilterBoxControl),
                "PreviewService");
            await UiTestDriver.WaitForFilterAppliedAsync(
                window,
                "PreviewService");

            AssertNestedPreviewServicePath(window);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    private static void AssertNestedPreviewServicePath(MainWindow window)
    {
        var tree = UiTestDriver.GetRequiredControl<ProjectTreeView>(
            window,
            "ProjectTree");
        var root = Assert.Single(
            UiTestDriver.GetViewModel(window).TreeNodes);
        var path = new[]
        {
            root,
            FindChild(root, "src"),
            FindDescendant(root, "AppCore"),
            FindDescendant(root, "Services"),
            FindDescendant(root, "PreviewService.cs")
        };
        var positions = path
            .Select(node => GetContentHorizontalPosition(tree, node))
            .ToArray();

        for (var index = 1; index < positions.Length; index++)
        {
            Assert.True(
                positions[index] > positions[index - 1] + 8,
                $"Expected a deeper visual level at index {index}: " +
                $"{string.Join(", ", positions)}.");
        }
    }

    private static TreeNodeViewModel FindChild(
        TreeNodeViewModel parent,
        string displayName) =>
        Assert.Single(
            parent.Children,
            child => child.DisplayName == displayName);

    private static TreeNodeViewModel FindDescendant(
        TreeNodeViewModel root,
        string displayName)
    {
        TreeNodeViewModel? match = null;
        TreeNodeViewModel.ForEachRealizedDescendant(
            [root],
            node =>
            {
                if (node.DisplayName == displayName)
                    match = node;
            });
        return Assert.IsType<TreeNodeViewModel>(match);
    }

    private static void AssertChevronVisible(
        ProjectTreeView tree,
        TreeNodeViewModel node)
    {
        var container = Assert.IsType<ProjectTreeViewItem>(
            tree.TreeContainerFromItem(node));
        var chevron = container
            .GetVisualDescendants()
            .OfType<ToggleButton>()
            .Single(toggle =>
                toggle.Name == "PART_ExpandCollapseChevron" &&
                ReferenceEquals(
                    toggle.FindAncestorOfType<ProjectTreeViewItem>(),
                    container));
        Assert.True(chevron.IsVisible);
    }

    private static double GetContentHorizontalPosition(
        ProjectTreeView tree,
        TreeNodeViewModel node)
    {
        var container = Assert.IsType<ProjectTreeViewItem>(
            tree.TreeContainerFromItem(node));
        var content = container
            .GetVisualDescendants()
            .OfType<StackPanel>()
            .Single(panel =>
                panel.Name == "TreeItemContent" &&
                ReferenceEquals(
                    panel.FindAncestorOfType<ProjectTreeViewItem>(),
                    container));
        var origin = content.TranslatePoint(default, tree);
        Assert.True(origin.HasValue);
        return origin.Value.X;
    }
}
