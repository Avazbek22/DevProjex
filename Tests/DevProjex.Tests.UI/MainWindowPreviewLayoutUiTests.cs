using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;
using DevProjex.Avalonia.Controls;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowPreviewLayoutUiTests(UiWorkspaceFixture workspace)
{
    [AvaloniaFact]
    public async Task ProjectTreeVerticalScrollBar_SpansFluentHorizontalScrollBarCorner()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var projectTree = UiTestDriver.GetRequiredControl<ProjectTreeView>(window, "ProjectTree");
            var treeScrollViewer = Assert.Single(
                projectTree.GetVisualDescendants().OfType<ScrollViewer>());
            var verticalScrollBar = Assert.Single(
                treeScrollViewer.GetVisualDescendants()
                    .OfType<ScrollBar>(),
                scrollBar => scrollBar.Orientation == Orientation.Vertical);
            var scrollBarsSeparator = Assert.Single(
                treeScrollViewer.GetVisualDescendants()
                    .OfType<Panel>(),
                panel => panel.Name == "PART_ScrollBarsSeparator");

            Assert.Equal(2, Grid.GetRowSpan(verticalScrollBar));
            Assert.False(scrollBarsSeparator.IsVisible);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task LoadedProject_SettingsIslandLeftEdgeAlignsWithFormatSwitcher()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var settingsContainer = UiTestDriver.GetRequiredControl<Border>(window, "SettingsContainer");
            var formatSwitcher = UiTestDriver.GetRequiredTopMenuControl<Border>(window, "FormatSegmentedControl");

            var settingsBounds = UiTestDriver.GetBoundsInWindow(settingsContainer, window);
            var formatBounds = UiTestDriver.GetBoundsInWindow(formatSwitcher, window);

            var delta = Math.Abs(settingsBounds.Left - formatBounds.Left);
            Assert.True(
                delta <= 2.5,
                $"SettingsLeft={settingsBounds.Left:F2}, SettingsWidth={settingsBounds.Width:F2}, " +
                $"FormatLeft={formatBounds.Left:F2}, FormatWidth={formatBounds.Width:F2}, Delta={delta:F2}");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewTreePane_DefaultsToMinimumWidth_PreservesManualResizeUntilNextOpen()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);

            var treePaneContainer = UiTestDriver.GetRequiredControl<Border>(window, "TreePaneContainer");
            var splitter = UiTestDriver.GetRequiredControl<Border>(window, "TreePreviewSplitter");
            var initialWidth = UiTestDriver.GetBoundsInWindow(treePaneContainer, window).Width;
            Assert.InRange(initialWidth, 417, 419);

            window.Width += 240;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);
            var widthAfterWindowExpansion = UiTestDriver.GetBoundsInWindow(treePaneContainer, window).Width;
            Assert.InRange(Math.Abs(widthAfterWindowExpansion - initialWidth), 0, 1.5);

            await UiTestDriver.DragAsync(window, splitter, deltaX: 100);
            var manuallyExpandedWidth = UiTestDriver.GetBoundsInWindow(treePaneContainer, window).Width;
            Assert.True(manuallyExpandedWidth > initialWidth + 20);

            await UiTestDriver.ClosePreviewAsync(window);
            await UiTestDriver.OpenPreviewAsync(window);
            var reopenedWidth = UiTestDriver.GetBoundsInWindow(treePaneContainer, window).Width;
            Assert.InRange(reopenedWidth, 417, 419);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewTreeTools_RightButtonsAlignWithTreeCloseButton()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);
            var treeCloseButton = UiTestDriver.GetRequiredControl<Button>(window, "PreviewTreeHideButton");
            var treeCloseBounds = UiTestDriver.GetBoundsInWindow(treeCloseButton, window);

            await UiTestDriver.OpenFilterAsync(window);
            var filterBar = UiTestDriver.GetRequiredControl<FilterBarView>(window, "FilterBar");
            var filterCloseButton = Assert.IsType<Button>(filterBar.FindControl<Button>("FilterCloseButton"));
            var filterCloseBounds = UiTestDriver.GetBoundsInWindow(filterCloseButton, window);
            Assert.InRange(Math.Abs(filterCloseBounds.Center.X - treeCloseBounds.Center.X), 0, 1.5);

            await UiTestDriver.RaiseButtonClickAsync(filterCloseButton);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => !UiTestDriver.GetViewModel(window).FilterVisible,
                "filter toolbar to close before opening search");

            await UiTestDriver.OpenSearchAsync(window);
            var searchBar = UiTestDriver.GetRequiredControl<SearchBarView>(window, "SearchBar");
            var previousButton = Assert.IsType<Button>(searchBar.FindControl<Button>("SearchPreviousButton"));
            var nextButton = Assert.IsType<Button>(searchBar.FindControl<Button>("SearchNextButton"));
            var searchCloseButton = Assert.IsType<Button>(searchBar.FindControl<Button>("SearchCloseButton"));
            var previousBounds = UiTestDriver.GetBoundsInWindow(previousButton, window);
            var nextBounds = UiTestDriver.GetBoundsInWindow(nextButton, window);
            var searchCloseBounds = UiTestDriver.GetBoundsInWindow(searchCloseButton, window);

            Assert.InRange(Math.Abs(searchCloseBounds.Center.X - treeCloseBounds.Center.X), 0, 1.5);
            Assert.InRange(nextBounds.Left - previousBounds.Right, 7, 9);
            Assert.InRange(searchCloseBounds.Left - nextBounds.Right, 7, 9);
            Assert.InRange(Math.Abs(previousBounds.Center.Y - nextBounds.Center.Y), 0, 1);
            Assert.InRange(Math.Abs(nextBounds.Center.Y - searchCloseBounds.Center.Y), 0, 1);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewOnlyClose_RestoresActiveFilterAfterToolbarCycle()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenFilterAsync(window);

            var filterBar = UiTestDriver.GetRequiredControl<FilterBarView>(window, "FilterBar");
            await UiTestDriver.EnterTextAsync(window, Assert.IsType<TextBox>(filterBar.FilterBoxControl), "preview");
            await UiTestDriver.WaitForFilterAppliedAsync(window, "preview");

            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.HidePreviewTreeAsync(window);
            await UiTestDriver.ClosePreviewAsync(window);

            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var viewModel = UiTestDriver.GetViewModel(window);
                    return viewModel.FilterVisible &&
                           viewModel.NameFilter == "preview" &&
                           UiTestDriver.GetRequiredControl<Border>(window, "FilterBarContainer").IsVisible;
                },
                "filter state to be restored after preview-only close");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewOnlyClose_RestoresActiveSearchAfterToolbarCycle()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenSearchAsync(window);

            var searchBar = UiTestDriver.GetRequiredControl<SearchBarView>(window, "SearchBar");
            await UiTestDriver.EnterTextAsync(window, Assert.IsType<TextBox>(searchBar.SearchBoxControl), "preview");
            await UiTestDriver.WaitForSearchAppliedAsync(window, "preview");

            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.HidePreviewTreeAsync(window);
            await UiTestDriver.ClosePreviewAsync(window);

            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var viewModel = UiTestDriver.GetViewModel(window);
                    return viewModel.SearchVisible &&
                           viewModel.SearchQuery == "preview" &&
                           UiTestDriver.GetRequiredControl<Border>(window, "SearchBarContainer").IsVisible;
                },
                "search state to be restored after preview-only close");
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }
}
