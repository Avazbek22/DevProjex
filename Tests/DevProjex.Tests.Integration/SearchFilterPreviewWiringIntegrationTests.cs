namespace DevProjex.Tests.Integration;

public sealed class SearchFilterPreviewWiringIntegrationTests
{
    [Fact]
    public void MainWindow_OnToggleSearch_HasProjectAndPreviewGuards()
    {
        var body = SliceMainWindow("private async void OnToggleSearch(", "private void OnSearchClose(");

        Assert.Contains("if (!_viewModel.IsProjectLoaded) return;", body, StringComparison.Ordinal);
        Assert.Contains("if (_viewModel.IsPreviewMode) return;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_OnToggleSearch_WhenSearchVisible_ClosesSearchAndReturns()
    {
        var body = SliceMainWindow("private async void OnToggleSearch(", "private void OnSearchClose(");

        Assert.Contains("if (_viewModel.SearchVisible)", body, StringComparison.Ordinal);
        Assert.Contains("await CloseSearchAsync();", body, StringComparison.Ordinal);
        Assert.Contains("return;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_OnToggleSearch_ClosesFilterBeforeShowingSearch()
    {
        var body = SliceMainWindow("private async void OnToggleSearch(", "private void OnSearchClose(");

        var closeFilter = body.IndexOf("await CloseFilterAsync(focusTree: false);", StringComparison.Ordinal);
        var showSearch = body.IndexOf("ShowSearch();", StringComparison.Ordinal);

        Assert.True(closeFilter >= 0, "CloseFilterAsync call not found.");
        Assert.True(showSearch > closeFilter, "Search must be shown only after filter close path.");
    }

    [Fact]
    public void MainWindow_OnToggleFilter_HasProjectAndPreviewGuards()
    {
        var body = SliceMainWindow("private async void OnToggleFilter(", "private void OnFilterClose(");

        Assert.Contains("if (!_viewModel.IsProjectLoaded) return;", body, StringComparison.Ordinal);
        Assert.Contains("if (_viewModel.IsPreviewMode) return;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_OnToggleFilter_WhenFilterVisible_ClosesFilterAndReturns()
    {
        var body = SliceMainWindow("private async void OnToggleFilter(", "private void OnFilterClose(");

        Assert.Contains("if (_viewModel.FilterVisible)", body, StringComparison.Ordinal);
        Assert.Contains("await CloseFilterAsync();", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_OnToggleFilter_ClosesSearchWithAnimationWaitBeforeShowFilter()
    {
        var body = SliceMainWindow("private async void OnToggleFilter(", "private void OnFilterClose(");

        var closeSearch = body.IndexOf("await CloseSearchAsync(focusTree: false, waitForAnimation: true);", StringComparison.Ordinal);
        var showFilter = body.IndexOf("ShowFilter();", StringComparison.Ordinal);

        Assert.True(closeSearch >= 0, "CloseSearchAsync with waitForAnimation should be used.");
        Assert.True(showFilter > closeSearch, "Filter must be shown only after search close path.");
    }

    [Fact]
    public void MainWindow_ShowSearch_ForwardsSelectAllFlagToFocusRoutine()
    {
        var body = SliceMainWindow("private void ShowSearch(", "private async Task FocusSearchBoxAfterOpenAnimationAsync(");
        Assert.Contains("_ = FocusSearchBoxAfterOpenAnimationAsync(selectAllOnFocus);", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_ShowFilter_ForwardsSelectAllFlagToFocusRoutine()
    {
        var body = SliceMainWindow("private void ShowFilter(", "private async Task CloseFilterAsync(");
        Assert.Contains("_ = FocusFilterBoxAfterOpenAnimationAsync(selectAllOnFocus);", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_RestoreSearchAndFilterAfterPreview_RestoresOnlyOneTool()
    {
        var body = SliceMainWindow("private void RestoreSearchAndFilterAfterPreview()", "private void ClearPreviewMemory()");

        Assert.Contains("if (_restoreSearchAfterPreview && !_viewModel.SearchVisible)", body, StringComparison.Ordinal);
        Assert.Contains("else if (_restoreFilterAfterPreview && !_viewModel.FilterVisible)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_RestoreSearchAndFilterAfterPreview_UsesNoSelectAllFocusMode()
    {
        var body = SliceMainWindow("private void RestoreSearchAndFilterAfterPreview()", "private void ClearPreviewMemory()");

        Assert.Contains("ShowSearch(focusInput: true, selectAllOnFocus: false);", body, StringComparison.Ordinal);
        Assert.Contains("ShowFilter(focusInput: true, selectAllOnFocus: false);", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_ForceCloseSearchAndFilterForPreview_HidesBothFlags()
    {
        var body = SliceMainWindow("private void ForceCloseSearchAndFilterForPreview()", "private void FocusPreviewSurface()");

        Assert.Contains("_viewModel.SearchVisible = false;", body, StringComparison.Ordinal);
        Assert.Contains("_viewModel.FilterVisible = false;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_ForceCloseSearchAndFilterForPreview_CancelsPendingCoordinators()
    {
        var body = SliceMainWindow("private void ForceCloseSearchAndFilterForPreview()", "private void FocusPreviewSurface()");

        Assert.Contains("_searchCoordinator.CancelPending();", body, StringComparison.Ordinal);
        Assert.Contains("_filterCoordinator.CancelPending();", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_ForceCloseSearchAndFilterForPreview_DoesNotClearQueries()
    {
        var body = SliceMainWindow("private void ForceCloseSearchAndFilterForPreview()", "private void FocusPreviewSurface()");

        Assert.DoesNotContain("_viewModel.SearchQuery = string.Empty;", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_viewModel.NameFilter = string.Empty;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_OnKeyDown_CtrlF_UsesDeferredSearchToggle()
    {
        var body = SliceMainWindow("private void OnKeyDown(", "private void OnTreePointerEntered(");

        Assert.Contains("if (mods == KeyModifiers.Control && e.Key == Key.F)", body, StringComparison.Ordinal);
        Assert.Contains("ScheduleSearchOrFilterHotkeyToggle(", body, StringComparison.Ordinal);
        Assert.Contains("isSearchToggle: true", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_OnKeyDown_CtrlShiftN_UsesDeferredFilterToggle()
    {
        var body = SliceMainWindow("private void OnKeyDown(", "private void OnTreePointerEntered(");

        Assert.Contains("if (mods == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.N)", body, StringComparison.Ordinal);
        Assert.Contains("isSearchToggle: false", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_OnKeyDown_Escape_ClosesSearchBeforeFilter()
    {
        var body = SliceMainWindow("private void OnKeyDown(", "private void OnTreePointerEntered(");

        var closeSearch = body.IndexOf("if (e.Key == Key.Escape && _viewModel.SearchVisible)", StringComparison.Ordinal);
        var closeFilter = body.IndexOf("if (e.Key == Key.Escape && _viewModel.FilterVisible)", StringComparison.Ordinal);

        Assert.True(closeSearch >= 0, "Escape search-close branch not found.");
        Assert.True(closeFilter > closeSearch, "Search close branch should be evaluated before filter close branch.");
    }

    [Fact]
    public void MainWindow_FocusInputTextBox_SelectAllBranch_KeepsExistingBehavior()
    {
        var body = SliceMainWindow("private void FocusInputTextBox(", "private static void PlaceCaretAtTextEnd(");

        Assert.Contains("if (selectAllOnFocus)", body, StringComparison.Ordinal);
        Assert.Contains("textBox.SelectAll();", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_FocusInputTextBox_NoSelectAllBranch_PlacesCaretToEndTwice()
    {
        var body = SliceMainWindow("private void FocusInputTextBox(", "private static void PlaceCaretAtTextEnd(");

        Assert.Contains("PlaceCaretAtTextEnd(textBox);", body, StringComparison.Ordinal);
        Assert.Contains("DispatcherPriority.Input", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_PlaceCaretAtTextEnd_SetsSelectionAndCaretIndex()
    {
        var body = SliceMainWindow("private static void PlaceCaretAtTextEnd(", "private async Task CloseSearchAsync(");

        Assert.Contains("textBox.SelectionStart = end;", body, StringComparison.Ordinal);
        Assert.Contains("textBox.SelectionEnd = end;", body, StringComparison.Ordinal);
        Assert.Contains("textBox.CaretIndex = end;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_CloseSearchAsync_ImmediatelyClearsQueryAndMatches()
    {
        var body = SliceMainWindow("private async Task CloseSearchAsync(", "private bool IsSearchBarEffectivelyVisible()");

        Assert.Contains("_viewModel.SearchQuery = string.Empty;", body, StringComparison.Ordinal);
        Assert.Contains("_searchCoordinator.CancelPending();", body, StringComparison.Ordinal);
        Assert.Contains("_searchCoordinator.UpdateSearchMatches();", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_CloseSearchAsync_SchedulesBackgroundMemoryCleanup()
    {
        var body = SliceMainWindow("private async Task CloseSearchAsync(", "private bool IsSearchBarEffectivelyVisible()");

        Assert.Contains("ScheduleBackgroundMemoryCleanup();", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_CloseFilterAsync_WaitsAnimationBeforeConditionalFilterClear()
    {
        var body = SliceMainWindow("private async Task CloseFilterAsync(", "private void ForceCloseSearchAndFilterForPreview()");

        Assert.Contains("await WaitForPanelAnimationAsync(FilterBarAnimationDuration);", body, StringComparison.Ordinal);
        Assert.Contains("if (_viewModel.FilterVisible)", body, StringComparison.Ordinal);
        Assert.Contains("return;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_CloseFilterAsync_CancelsPendingInBothCodePaths()
    {
        var body = SliceMainWindow("private async Task CloseFilterAsync(", "private void ForceCloseSearchAndFilterForPreview()");

        Assert.Contains("_filterCoordinator.CancelPending();", body, StringComparison.Ordinal);
        Assert.Contains("if (!string.IsNullOrEmpty(_viewModel.NameFilter))", body, StringComparison.Ordinal);
        Assert.Contains("else", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_CloseFilterAsync_ResetsFilterAndRebuildsTreeWhenQueryExists()
    {
        var body = SliceMainWindow("private async Task CloseFilterAsync(", "private void ForceCloseSearchAndFilterForPreview()");

        Assert.Contains("_viewModel.NameFilter = string.Empty;", body, StringComparison.Ordinal);
        Assert.Contains("_ = ApplyFilterRealtimeAsync(CancellationToken.None);", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_HotkeyToggleScheduler_UsesPendingFlagsForDebounce()
    {
        var body = SliceMainWindow("private void ScheduleSearchOrFilterHotkeyToggle(", "private void ShowSearch(");

        Assert.Contains("Interlocked.CompareExchange(ref pendingFlag, 1, 0) != 0", body, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _pendingSearchHotkeyToggle, 0);", body, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _pendingFilterHotkeyToggle, 0);", body, StringComparison.Ordinal);
    }

    private static string SliceMainWindow(string startMarker, string endMarker)
    {
        var content = ReadMainWindowCode();
        var start = content.IndexOf(startMarker, StringComparison.Ordinal);
        var end = content.IndexOf(endMarker, start + 1, StringComparison.Ordinal);

        Assert.True(start >= 0, $"Start marker not found: {startMarker}");
        Assert.True(end > start, $"End marker not found after start: {endMarker}");

        return content.Substring(start, end - start);
    }

    private static string ReadMainWindowCode()
    {
        var repoRoot = FindRepositoryRoot();
        var file = Path.Combine(repoRoot, "Apps", "Avalonia", "DevProjex.Avalonia", "MainWindow.axaml.cs");
        return File.ReadAllText(file);
    }

    private static string FindRepositoryRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) ||
                File.Exists(Path.Combine(dir, "DevProjex.sln")))
                return dir;

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
