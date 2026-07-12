using Avalonia.Controls.Documents;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowTreeQueryRefreshUiTests(UiWorkspaceFixture workspace)
{
    [AvaloniaFact]
    public async Task ApplySettings_WithActiveFilter_RebindsHighlightsAndExpansionToReplacementTree()
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

            var previousRoot = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);

            await UiTestDriver.ClickExtensionCheckBoxAsync(window, ".json");
            await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
            await UiTestDriver.ClickApplySettingsAsync(window);

            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var viewModel = UiTestDriver.GetViewModel(window);
                    var currentRoot = viewModel.TreeNodes.FirstOrDefault();
                    var match = FindNode(currentRoot, "PreviewService.cs");
                    return currentRoot is not null &&
                           !ReferenceEquals(previousRoot, currentRoot) &&
                           !viewModel.StatusBusy &&
                           viewModel.FilterMatchCount > 0 &&
                           HasVisibleMatchHighlight(match) &&
                           AreAncestorsExpanded(match);
                },
                "active filter highlights and expansion to be rebound after applying settings");

            var refreshedViewModel = UiTestDriver.GetViewModel(window);
            Assert.Equal("PreviewService", refreshedViewModel.NameFilter);
            Assert.Equal(1, refreshedViewModel.FilterMatchCount);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ApplySettings_WithActiveSearch_RebindsMatchesHighlightsAndExpansionToReplacementTree()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenSearchAsync(window);
            var searchBar = UiTestDriver.GetRequiredControl<SearchBarView>(window, "SearchBar");
            await UiTestDriver.EnterTextAsync(
                window,
                Assert.IsType<TextBox>(searchBar.SearchBoxControl),
                "PreviewService");
            await UiTestDriver.WaitForSearchAppliedAsync(window, "PreviewService");

            var previousRoot = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);

            await UiTestDriver.ClickExtensionCheckBoxAsync(window, ".json");
            await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
            await UiTestDriver.ClickApplySettingsAsync(window);

            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var viewModel = UiTestDriver.GetViewModel(window);
                    var currentRoot = viewModel.TreeNodes.FirstOrDefault();
                    var match = FindNode(currentRoot, "PreviewService.cs");
                    return currentRoot is not null &&
                           !ReferenceEquals(previousRoot, currentRoot) &&
                           !viewModel.StatusBusy &&
                           viewModel.SearchTotalMatches == 1 &&
                           HasVisibleMatchHighlight(match) &&
                           AreAncestorsExpanded(match);
                },
                "active search matches, highlights, and expansion to be rebound after applying settings");

            var refreshedViewModel = UiTestDriver.GetViewModel(window);
            Assert.Equal("PreviewService", refreshedViewModel.SearchQuery);
            Assert.Equal(1, refreshedViewModel.SearchTotalMatches);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    private static TreeNodeViewModel? FindNode(TreeNodeViewModel? root, string displayName) =>
        root?.Flatten().FirstOrDefault(node =>
            string.Equals(node.DisplayName, displayName, StringComparison.Ordinal));

    private static bool HasVisibleMatchHighlight(TreeNodeViewModel? node) =>
        node is { HasHighlightedDisplay: true, DisplayInlines: not null } &&
        node.DisplayInlines.OfType<Run>().Any(run => run.Background is not null);

    private static bool AreAncestorsExpanded(TreeNodeViewModel? node)
    {
        if (node is null)
            return false;

        for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (!ancestor.IsExpanded)
                return false;
        }

        return true;
    }
}
