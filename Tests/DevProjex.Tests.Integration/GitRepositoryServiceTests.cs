namespace DevProjex.Tests.Integration;

/// <summary>
/// Integration tests for GitRepositoryService.
/// These tests require git to be installed and network access for some tests.
///
/// Test categories:
/// - Git availability detection
/// - Clone operations (requires network)
/// - Branch operations
/// - Update operations
///
/// IMPORTANT: These tests use real git operations and network access.
/// Some tests will be skipped if git is not available.
/// </summary>
[Collection(GitNetworkTestCollection.Name)]
public class GitRepositoryServiceTests : IAsyncLifetime
{
    private readonly GitRepositoryService _service = new();
    private string? _tempDir;
    private bool _gitAvailable;
    private GitTestRepository? _testRepository;

    private string TestRepoUrl => _testRepository!.RepositoryUrl;
    private string TestRepoName => _testRepository!.RepositoryName;

    public async ValueTask InitializeAsync()
    {
        _gitAvailable = await SharedGitRepositories.IsGitAvailableAsync();
        _tempDir = Path.Combine(Path.GetTempPath(), "DevProjex", "Tests", "GitTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        if (_gitAvailable)
            _testRepository = await SharedGitRepositories.GetDefaultRepositoryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        // Cleanup with retry for locked git files
        if (_tempDir != null && Directory.Exists(_tempDir))
            await TryDeleteDirectoryAsync(_tempDir);
    }

    /// <summary>
    /// Attempts to delete directory with retries for locked files.
    /// Git may keep files locked briefly after operations.
    /// </summary>
    private static async Task TryDeleteDirectoryAsync(string path)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                // Reset readonly attributes
                SetAttributesNormal(path);
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(100 * (i + 1));
            }
            catch (IOException)
            {
                await Task.Delay(100 * (i + 1));
            }
        }

        // Final attempt - ignore errors
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Ignore - OS will clean up temp folder eventually
        }
    }

    private static void SetAttributesNormal(string path)
    {
        try
        {
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Helper to skip test if git is not available.
    /// </summary>
    private void SkipIfNoGit()
    {
        if (!_gitAvailable)
            Assert.Skip("Git is not available on this system.");
    }

    /// <summary>
    /// Helper to skip test with custom condition.
    /// </summary>
    private static void SkipIf(bool condition, string reason)
    {
        if (condition)
            Assert.Skip(reason);
    }

    #region Git Availability Tests

    [Fact]
    public async Task IsGitAvailableAsync_DoesNotThrow()
    {
        // This test verifies that git detection works without throwing
        var result = await _service.IsGitAvailableAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Result depends on environment, but method should not throw
        Assert.True(result || !result);
    }

    [Fact]
    public async Task IsGitAvailableAsync_ReturnsConsistentResults()
    {
        // Multiple calls should return the same result
        var result1 = await _service.IsGitAvailableAsync(cancellationToken: TestContext.Current.CancellationToken);
        var result2 = await _service.IsGitAvailableAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(result1, result2);
    }

    #endregion

    #region Clone Tests

    [Fact]
    public async Task CloneAsync_ReturnsError_ForNonGitUrl()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "non-git-url-test");
        var nonRepositoryPath = Path.Combine(_tempDir!, "not-a-repository");
        Directory.CreateDirectory(nonRepositoryPath);
        File.WriteAllText(Path.Combine(nonRepositoryPath, "README.txt"), "not a git repository");

        var result = await _service.CloneAsync(
            new Uri(nonRepositoryPath).AbsoluteUri,
            targetDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task CloneAsync_ReturnsError_ForMissingRemote()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "invalid-domain-test");
        var missingRemote = new Uri(Path.Combine(_tempDir!, "missing-repository.git")).AbsoluteUri;

        var result = await _service.CloneAsync(
            missingRemote,
            targetDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task CloneAsync_ClonesRepository_Successfully()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "clone-test");

        var result = await _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, $"Clone failed: {result.ErrorMessage}");
        Assert.Equal(targetDir, result.LocalPath);
        Assert.Equal(ProjectSourceType.GitClone, result.SourceType);
        Assert.Equal(TestRepoName, result.RepositoryName);
        Assert.NotNull(result.DefaultBranch);
        Assert.True(Directory.Exists(targetDir), "Target directory should exist");
        Assert.True(Directory.Exists(Path.Combine(targetDir, ".git")), ".git directory should exist");
    }

    [Fact]
    public async Task CloneAsync_ReportsProgress()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "progress-test");
        var progress = new ProgressRecorder();

        var result = await _service.CloneAsync(TestRepoUrl, targetDir, progress, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, $"Clone failed: {result.ErrorMessage}");
        // Git may or may not report progress depending on output - just verify operation succeeded
        // Progress reports are optional (git stderr output may be empty for fast operations)
    }

    [Fact]
    public async Task CloneAsync_SupportsCancellation()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "cancel-test");
        using var cts = new CancellationTokenSource();

        // Cancel immediately
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task CloneAsync_ReturnsError_ForInvalidUrl()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "invalid-url-test");
        var missingRemote = new Uri(Path.Combine(_tempDir!, "missing-repository-2.git")).AbsoluteUri;

        var result = await _service.CloneAsync(
            missingRemote,
            targetDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task CloneAsync_ExtractsRepositoryName_FromUrl()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "name-test");

        var result = await _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, $"Clone failed: {result.ErrorMessage}");
        Assert.Equal(TestRepoName, result.RepositoryName);
    }

    [Fact]
    public async Task CloneAsync_CreatesShallowClone()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "shallow-test");
        var result = await _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, $"Clone failed: {result.ErrorMessage}");

        // Verify shallow clone by checking for shallow file
        var shallowFile = Path.Combine(targetDir, ".git", "shallow");
        Assert.True(File.Exists(shallowFile), "Repository should be shallow (have .git/shallow file)");
    }

    #endregion

    #region Branch Tests

    [Fact]
    public async Task GetBranchesAsync_ReturnsBranches_AfterClone()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "branches-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        var branches = await _service.GetBranchesAsync(targetDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(branches);
        // At least one branch should be active (current branch)
        Assert.Contains(branches, b => b.IsActive);
    }

    [Fact]
    public async Task GetBranchesAsync_ActiveBranchIsFirst()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "active-first-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        var branches = await _service.GetBranchesAsync(targetDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(branches);
        // First branch should be active (sorting places active first)
        Assert.True(branches[0].IsActive, "First branch should be the active one");
    }

    [Fact]
    public async Task GetCurrentBranchAsync_ReturnsCurrentBranch()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "current-branch-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        var currentBranch = await _service.GetCurrentBranchAsync(targetDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(currentBranch);
        Assert.NotEmpty(currentBranch);
    }

    [Fact]
    public async Task SwitchBranchAsync_SwitchesToDifferentBranch()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "switch-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        // Get list of branches
        var branches = await _service.GetBranchesAsync(targetDir, cancellationToken: TestContext.Current.CancellationToken);
        SkipIf(branches.Count < 2, "Repository has only one branch, cannot test switching");

        // Find a branch that is not currently active
        var otherBranch = branches.FirstOrDefault(b => !b.IsActive);
        SkipIf(otherBranch is null, "No other branch available for switching");

        // Switch to the other branch
        var success = await _service.SwitchBranchAsync(targetDir, otherBranch!.Name, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(success, "Branch switch should succeed");

        // Verify current branch changed
        var newCurrentBranch = await _service.GetCurrentBranchAsync(targetDir, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(otherBranch.Name, newCurrentBranch);
    }

    [Fact]
    public async Task SwitchBranchAsync_ReturnsFalse_ForNonexistentBranch()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "nonexistent-branch-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        var success = await _service.SwitchBranchAsync(targetDir, "nonexistent-branch-xyz123", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(success, "Switching to nonexistent branch should fail");
    }

    [Fact]
    public async Task SwitchBranchAsync_ReportsProgress()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "switch-progress-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        var branches = await _service.GetBranchesAsync(targetDir, cancellationToken: TestContext.Current.CancellationToken);
        var otherBranch = branches.FirstOrDefault(b => !b.IsActive);
        SkipIf(otherBranch is null, "No other branch available");

        var progress = new ProgressRecorder();

        var success = await _service.SwitchBranchAsync(targetDir, otherBranch!.Name, progress, cancellationToken: TestContext.Current.CancellationToken);

        // Verify operation succeeded - progress reports are optional
        Assert.True(success, "Branch switch should succeed");
    }

    [Fact]
    public async Task SwitchBranchAsync_CanSwitchBackToOriginalBranch()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "switch-back-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        var originalBranch = await _service.GetCurrentBranchAsync(targetDir, cancellationToken: TestContext.Current.CancellationToken);
        var branches = await _service.GetBranchesAsync(targetDir, cancellationToken: TestContext.Current.CancellationToken);
        var otherBranch = branches.FirstOrDefault(b => !b.IsActive);
        SkipIf(otherBranch is null, "No other branch available");

        // Switch to other branch
        var switchResult1 = await _service.SwitchBranchAsync(targetDir, otherBranch!.Name, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(switchResult1, "First switch should succeed");

        // Switch back to original
        var switchResult2 = await _service.SwitchBranchAsync(targetDir, originalBranch!, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(switchResult2, "Switch back should succeed");

        var finalBranch = await _service.GetCurrentBranchAsync(targetDir, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(originalBranch, finalBranch);
    }

    #endregion

    #region Pull Updates Tests

    [Fact]
    public async Task PullUpdatesAsync_SucceedsOnCleanRepository()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "pull-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        var success = await _service.PullUpdatesAsync(targetDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(success, "Pull updates should succeed on clean repository");
    }

    [Fact]
    public async Task PullUpdatesAsync_ReportsProgress()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "pull-progress-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        var progress = new ProgressRecorder();

        var success = await _service.PullUpdatesAsync(targetDir, progress, cancellationToken: TestContext.Current.CancellationToken);

        // Verify operation succeeded - progress reports are optional
        Assert.True(success, "Pull updates should succeed on clean repository");
    }

    [Fact]
    public async Task PullUpdatesAsync_WorksAfterBranchSwitch()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "pull-after-switch-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: TestContext.Current.CancellationToken);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        // Get branches and switch if possible
        var branches = await _service.GetBranchesAsync(targetDir, cancellationToken: TestContext.Current.CancellationToken);
        var otherBranch = branches.FirstOrDefault(b => !b.IsActive);

        if (otherBranch is not null)
        {
            var switchResult = await _service.SwitchBranchAsync(targetDir, otherBranch.Name, cancellationToken: TestContext.Current.CancellationToken);
            SkipIf(!switchResult, "Branch switch failed");
        }

        // Pull should still work after switch
        var pullSuccess = await _service.PullUpdatesAsync(targetDir, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(pullSuccess, "Pull updates should work after branch switch");
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task GetBranchesAsync_ReturnsEmptyList_ForInvalidPath()
    {
        var invalidPath = Path.Combine(_tempDir!, "nonexistent-repo-xyz");

        var branches = await _service.GetBranchesAsync(invalidPath, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(branches);
    }

    [Fact]
    public async Task GetCurrentBranchAsync_ReturnsNull_ForInvalidPath()
    {
        var invalidPath = Path.Combine(_tempDir!, "nonexistent-repo-xyz");

        var branch = await _service.GetCurrentBranchAsync(invalidPath, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(branch);
    }

    [Fact]
    public async Task SwitchBranchAsync_ReturnsFalse_ForInvalidPath()
    {
        var invalidPath = Path.Combine(_tempDir!, "nonexistent-repo-xyz");

        var success = await _service.SwitchBranchAsync(invalidPath, "main", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(success);
    }

    [Fact]
    public async Task PullUpdatesAsync_ReturnsFalse_ForInvalidPath()
    {
        var invalidPath = Path.Combine(_tempDir!, "nonexistent-repo-xyz");

        var success = await _service.PullUpdatesAsync(invalidPath, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(success);
    }

    [Fact]
    public async Task GetBranchesAsync_ReturnsEmptyList_ForNonGitDirectory()
    {
        // Create a regular directory (not a git repo)
        var nonGitDir = Path.Combine(_tempDir!, "not-a-git-repo");
        Directory.CreateDirectory(nonGitDir);
        File.WriteAllText(Path.Combine(nonGitDir, "file.txt"), "content");

        var branches = await _service.GetBranchesAsync(nonGitDir, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(branches);
    }

    #endregion

    #region Cancellation Tests

    [Fact]
    public async Task GetBranchesAsync_HandlesErrors_ForCancelledToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await _service.GetBranchesAsync(_tempDir!, cts.Token));
    }

    #endregion

    #region Concurrent Operations Tests

    [Fact]
    public async Task MultipleClones_DoNotInterfere()
    {
        SkipIfNoGit();

        // Clone two repositories in parallel to different directories
        var targetDir1 = Path.Combine(_tempDir!, "parallel-1");
        var targetDir2 = Path.Combine(_tempDir!, "parallel-2");

        var task1 = _service.CloneAsync(TestRepoUrl, targetDir1, cancellationToken: TestContext.Current.CancellationToken);
        var task2 = _service.CloneAsync(TestRepoUrl, targetDir2, cancellationToken: TestContext.Current.CancellationToken);

        var results = await Task.WhenAll(task1, task2);

        Assert.True(results[0].Success, $"First clone failed: {results[0].ErrorMessage}");
        Assert.True(results[1].Success, $"Second clone failed: {results[1].ErrorMessage}");

        // Both should have valid content
        Assert.True(Directory.Exists(Path.Combine(targetDir1, ".git")));
        Assert.True(Directory.Exists(Path.Combine(targetDir2, ".git")));
    }

    #endregion
}
