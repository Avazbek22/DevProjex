using System.Text.RegularExpressions;

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
        var reloadIndex = tryOpenFolderBody.IndexOf("await ReloadProjectAsync(cancellationToken);", StringComparison.Ordinal);
        var deleteCacheIndex = tryOpenFolderBody.IndexOf("_repoCacheService.DeleteRepositoryDirectory(", StringComparison.Ordinal);

        Assert.True(reloadIndex >= 0, "Project reload call not found.");
        Assert.True(deleteCacheIndex > reloadIndex, "Cached repo cleanup must happen only after successful reload.");
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
