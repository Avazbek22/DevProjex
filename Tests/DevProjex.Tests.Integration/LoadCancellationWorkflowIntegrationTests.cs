namespace DevProjex.Tests.Integration;

public sealed class LoadCancellationWorkflowIntegrationTests
{
    [Fact]
    public void MainWindow_LoadCancellation_UsesSnapshotFallbackInsteadOfHardReset()
    {
        var content = ReadMainWindowCode();

        Assert.Contains("CaptureProjectLoadCancellationSnapshot()", content);
        Assert.Contains("TryApplyActiveProjectLoadCancellationFallback()", content);

        var hardResetInsideLoadCancelCatch = new Regex(
            @"catch \(OperationCanceledException\) when \(cancellationToken\.IsCancellationRequested\)\s*\{[^}]*ResetToInitialProjectStateAfterCancellation\(\)",
            RegexOptions.Singleline);

        Assert.DoesNotMatch(hardResetInsideLoadCancelCatch, content);
    }

    [Fact]
    public void MainWindow_StatusCancelButton_UsesLoadFallbackPolicy()
    {
        var content = ReadMainWindowCode();

        var loadCancelBranchUsesFallback = new Regex(
            @"if \(activeOperationType == StatusOperationType\.LoadProject\)\s*\{[^}]*TryApplyActiveProjectLoadCancellationFallback\(\)",
            RegexOptions.Singleline);

        Assert.Matches(loadCancelBranchUsesFallback, content);
    }

    [Fact]
    public void MainWindow_TryOpenFolder_DeletesCachedRepoOnlyAfterSuccessfulReload()
    {
        var content = ReadMainWindowCode();
        var tryOpenFolderStart = content.IndexOf("private async Task TryOpenFolderAsync(", StringComparison.Ordinal);
        var tryElevateStart = content.IndexOf("private bool TryElevateAndRestart(", StringComparison.Ordinal);

        Assert.True(tryOpenFolderStart >= 0, "TryOpenFolderAsync method not found.");
        Assert.True(tryElevateStart > tryOpenFolderStart, "TryOpenFolderAsync boundary not found.");

        var tryOpenFolderBody = content.Substring(tryOpenFolderStart, tryElevateStart - tryOpenFolderStart);
        var reloadMatch = Regex.Match(
            tryOpenFolderBody,
            @"await\s+ReloadProjectAsync\(\s*cancellationToken\s*,\s*applyStoredProfile:\s*true\s*\)\s*;",
            RegexOptions.Singleline);
        var reloadIndex = reloadMatch.Success ? reloadMatch.Index : -1;
        var deleteCacheIndex = tryOpenFolderBody.IndexOf("_repoCacheService.DeleteRepositoryDirectory(", StringComparison.Ordinal);

        Assert.True(reloadIndex >= 0, "Project reload call not found.");
        Assert.True(deleteCacheIndex > reloadIndex, "Cached repo cleanup must happen only after successful reload.");
    }

    [Fact]
    public void MainWindow_TryOpenFolder_ReloadProject_UsesStoredProfileRestore()
    {
        var content = ReadMainWindowCode();
        var tryOpenFolderStart = content.IndexOf("private async Task TryOpenFolderAsync(", StringComparison.Ordinal);
        var tryElevateStart = content.IndexOf("private bool TryElevateAndRestart(", StringComparison.Ordinal);

        Assert.True(tryOpenFolderStart >= 0, "TryOpenFolderAsync method not found.");
        Assert.True(tryElevateStart > tryOpenFolderStart, "TryOpenFolderAsync boundary not found.");

        var tryOpenFolderBody = content.Substring(tryOpenFolderStart, tryElevateStart - tryOpenFolderStart);
        Assert.Matches(
            new Regex(
                @"await\s+ReloadProjectAsync\(\s*cancellationToken\s*,\s*applyStoredProfile:\s*true\s*\)\s*;",
                RegexOptions.Singleline),
            tryOpenFolderBody);
    }

    [Fact]
    public void MainWindow_RefreshTree_SwapsOldTreeOnlyAfterNewRootIsBuilt()
    {
        var content = ReadMainWindowCode();
        var refreshTreeStart = content.IndexOf("private async Task RefreshTreeAsync(", StringComparison.Ordinal);
        var refreshTreeEnd = content.IndexOf("private TreeNodeViewModel BuildTreeViewModel(", StringComparison.Ordinal);

        Assert.True(refreshTreeStart >= 0, "RefreshTreeAsync method not found.");
        Assert.True(refreshTreeEnd > refreshTreeStart, "BuildTreeViewModel boundary not found.");

        var refreshTreeBody = content.Substring(refreshTreeStart, refreshTreeEnd - refreshTreeStart);
        var buildRootIndex = refreshTreeBody.IndexOf("var root = await Task.Run", StringComparison.Ordinal);
        var clearOldTreeIndex = refreshTreeBody.IndexOf("_viewModel.TreeNodes.Clear();", StringComparison.Ordinal);

        Assert.True(buildRootIndex >= 0, "New root build pipeline not found.");
        Assert.True(clearOldTreeIndex > buildRootIndex, "Old tree must be cleared only after new root is built.");
    }

    [Fact]
    public void MainWindow_RestorePreviousProjectStateAfterCancellation_SynchronizesSearchAndFilterVisualState()
    {
        var content = ReadMainWindowCode();
        var restoreStart = content.IndexOf("private void RestorePreviousProjectStateAfterCancellation(", StringComparison.Ordinal);
        var restoreEnd = content.IndexOf("private static CancellationTokenSource ReplaceCancellationSource(", StringComparison.Ordinal);

        Assert.True(restoreStart >= 0, "RestorePreviousProjectStateAfterCancellation method not found.");
        Assert.True(restoreEnd > restoreStart, "RestorePreviousProjectStateAfterCancellation boundary not found.");

        var restoreBody = content.Substring(restoreStart, restoreEnd - restoreStart);
        Assert.Contains("SyncSearchAndFilterVisualStateFromFlags();", restoreBody);
    }

    [Fact]
    public void MainWindow_OnApplySettings_WaitsForPendingSelectionRefresh_BeforePersistingProfile()
    {
        var content = ReadMainWindowCode();
        var applyStart = content.IndexOf("private async void OnApplySettings(", StringComparison.Ordinal);
        var persistStart = content.IndexOf("private void PersistLocalProjectProfileIfNeeded()", StringComparison.Ordinal);

        Assert.True(applyStart >= 0, "OnApplySettings method not found.");
        Assert.True(persistStart > applyStart, "OnApplySettings boundary not found.");

        var applyBody = content.Substring(applyStart, persistStart - applyStart);
        var refreshTreeIndex = applyBody.IndexOf("await RefreshTreeAsync();", StringComparison.Ordinal);
        var waitIndex = applyBody.IndexOf("await _selectionCoordinator.WaitForPendingRefreshesAsync();", StringComparison.Ordinal);
        var persistIndex = applyBody.IndexOf("PersistLocalProjectProfileIfNeeded();", StringComparison.Ordinal);

        Assert.True(refreshTreeIndex >= 0, "RefreshTreeAsync call not found.");
        Assert.True(waitIndex > refreshTreeIndex, "Pending refresh wait must happen after RefreshTreeAsync.");
        Assert.True(persistIndex > waitIndex, "Profile persist must happen after pending refresh wait.");
    }

    [Fact]
    public void MainWindow_ReloadProject_AppliesStoredProfile_BeforeSelectionRefresh()
    {
        var content = ReadMainWindowCode();
        var reloadStart = content.IndexOf("private async Task ReloadProjectAsync(", StringComparison.Ordinal);
        var clearPreviousStart = content.IndexOf("private void ClearPreviousProjectState(", StringComparison.Ordinal);

        Assert.True(reloadStart >= 0, "ReloadProjectAsync method not found.");
        Assert.True(clearPreviousStart > reloadStart, "ReloadProjectAsync boundary not found.");

        var reloadBody = content.Substring(reloadStart, clearPreviousStart - reloadStart);
        var applyProfileIndex = reloadBody.IndexOf("_selectionCoordinator.ApplyProjectProfileSelections(_currentPath, profile);", StringComparison.Ordinal);
        var resetProfileIndex = reloadBody.IndexOf("_selectionCoordinator.ResetProjectProfileSelections(_currentPath);", StringComparison.Ordinal);
        var refreshSelectionIndex = reloadBody.IndexOf("await _selectionCoordinator.RefreshRootAndDependentsAsync(_currentPath, cancellationToken);", StringComparison.Ordinal);
        var refreshTreeIndex = reloadBody.IndexOf("await RefreshTreeAsync(cancellationToken: cancellationToken);", StringComparison.Ordinal);

        Assert.True(applyProfileIndex >= 0, "ApplyProjectProfileSelections call not found.");
        Assert.True(resetProfileIndex >= 0, "ResetProjectProfileSelections call not found.");
        Assert.True(refreshSelectionIndex > applyProfileIndex, "Selection refresh must happen after profile apply.");
        Assert.True(refreshSelectionIndex > resetProfileIndex, "Selection refresh must happen after profile reset.");
        Assert.True(refreshTreeIndex > refreshSelectionIndex, "Tree refresh must happen after selection refresh.");
    }

    [Fact]
    public void MainWindow_TryOpenFolder_SetsCurrentPath_BeforeProjectReload()
    {
        var content = ReadMainWindowCode();
        var tryOpenFolderStart = content.IndexOf("private async Task TryOpenFolderAsync(", StringComparison.Ordinal);
        var tryElevateStart = content.IndexOf("private bool TryElevateAndRestart(", StringComparison.Ordinal);

        Assert.True(tryOpenFolderStart >= 0, "TryOpenFolderAsync method not found.");
        Assert.True(tryElevateStart > tryOpenFolderStart, "TryOpenFolderAsync boundary not found.");

        var tryOpenFolderBody = content.Substring(tryOpenFolderStart, tryElevateStart - tryOpenFolderStart);
        var setPathIndex = tryOpenFolderBody.IndexOf("_currentPath = path;", StringComparison.Ordinal);
        var reloadIndex = tryOpenFolderBody.IndexOf("await ReloadProjectAsync(cancellationToken, applyStoredProfile: true);", StringComparison.Ordinal);

        Assert.True(setPathIndex >= 0, "_currentPath assignment not found.");
        Assert.True(reloadIndex > setPathIndex, "_currentPath must be assigned before reload.");
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
