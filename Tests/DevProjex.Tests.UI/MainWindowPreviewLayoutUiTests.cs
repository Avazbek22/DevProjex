namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowPreviewLayoutUiTests(UiWorkspaceFixture workspace)
{
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
