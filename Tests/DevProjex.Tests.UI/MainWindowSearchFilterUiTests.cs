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
}
