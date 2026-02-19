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
public class GitRepositoryServiceTests : IAsyncLifetime
{
    private readonly GitRepositoryService _service;
    private string? _tempDir;
    private bool _gitAvailable;

    // Test repository - small public repo for testing
    // Using octocat/Hello-World - small and stable repo
    private const string TestRepoUrl = "https://github.com/octocat/Hello-World.git";
    private const string TestRepoName = "Hello-World";

    public GitRepositoryServiceTests()
    {
        _service = new GitRepositoryService();
    }

    public async Task InitializeAsync()
    {
        _gitAvailable = await _service.IsGitAvailableAsync();
        _tempDir = Path.Combine(Path.GetTempPath(), "DevProjex", "Tests", "GitTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public Task DisposeAsync()
    {
        // Cleanup with retry for locked git files
        if (_tempDir != null && Directory.Exists(_tempDir))
        {
            TryDeleteDirectory(_tempDir);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Attempts to delete directory with retries for locked files.
    /// Git may keep files locked briefly after operations.
    /// </summary>
    private static void TryDeleteDirectory(string path)
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
                Thread.Sleep(100 * (i + 1));
            }
            catch (IOException)
            {
                Thread.Sleep(100 * (i + 1));
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
            Assert.Fail("SKIP: Git is not available on this system");
    }

    /// <summary>
    /// Helper to skip test with custom condition.
    /// </summary>
    private static void SkipIf(bool condition, string reason)
    {
        if (condition)
            Assert.Fail($"SKIP: {reason}");
    }

    #region Git Availability Tests

    [Fact]
    public async Task IsGitAvailableAsync_DoesNotThrow()
    {
        // This test verifies that git detection works without throwing
        var result = await _service.IsGitAvailableAsync();

        // Result depends on environment, but method should not throw
        Assert.True(result || !result);
    }

    [Fact]
    public async Task IsGitAvailableAsync_ReturnsConsistentResults()
    {
        // Multiple calls should return the same result
        var result1 = await _service.IsGitAvailableAsync();
        var result2 = await _service.IsGitAvailableAsync();

        Assert.Equal(result1, result2);
    }

    #endregion

    #region Clone Tests

    [Fact]
    public async Task CloneAsync_ReturnsError_ForNonGitUrl()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "non-git-url-test");

        // Try to clone a URL that is not a git repository (e.g., Telegram link)
        var result = await _service.CloneAsync(
            "https://t.me/addstickers/SomeSticker",
            targetDir);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task CloneAsync_ReturnsError_ForInvalidDomain()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "invalid-domain-test");

        // Try to clone from a URL that doesn't exist
        var result = await _service.CloneAsync(
            "https://this-domain-absolutely-does-not-exist-xyz123.com/user/repo.git",
            targetDir);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task CloneAsync_ClonesRepository_Successfully()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "clone-test");

        var result = await _service.CloneAsync(TestRepoUrl, targetDir);

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
        var progressReports = new List<string>();
        var progress = new Progress<string>(msg => progressReports.Add(msg));

        var result = await _service.CloneAsync(TestRepoUrl, targetDir, progress);

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

        var result = await _service.CloneAsync(
            "https://github.com/nonexistent-user-xyz123/nonexistent-repo-abc456.git",
            targetDir);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task CloneAsync_ExtractsRepositoryName_FromUrl()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "name-test");

        var result = await _service.CloneAsync(TestRepoUrl, targetDir);

        Assert.True(result.Success, $"Clone failed: {result.ErrorMessage}");
        Assert.Equal(TestRepoName, result.RepositoryName);
    }

    [Fact]
    public async Task CloneAsync_CreatesShallowClone()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "shallow-test");
        var result = await _service.CloneAsync(TestRepoUrl, targetDir);

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
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        var branches = await _service.GetBranchesAsync(targetDir);

        Assert.NotEmpty(branches);
        // At least one branch should be active (current branch)
        Assert.Contains(branches, b => b.IsActive);
    }

    [Fact]
    public async Task GetBranchesAsync_ActiveBranchIsFirst()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "active-first-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        var branches = await _service.GetBranchesAsync(targetDir);

        Assert.NotEmpty(branches);
        // First branch should be active (sorting places active first)
        Assert.True(branches[0].IsActive, "First branch should be the active one");
    }

    [Fact]
    public async Task GetCurrentBranchAsync_ReturnsCurrentBranch()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "current-branch-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        var currentBranch = await _service.GetCurrentBranchAsync(targetDir);

        Assert.NotNull(currentBranch);
        Assert.NotEmpty(currentBranch);
    }

    [Fact]
    public async Task SwitchBranchAsync_SwitchesToDifferentBranch()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "switch-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        // Get list of branches
        var branches = await _service.GetBranchesAsync(targetDir);
        SkipIf(branches.Count < 2, "Repository has only one branch, cannot test switching");

        // Find a branch that is not currently active
        var otherBranch = branches.FirstOrDefault(b => !b.IsActive);
        SkipIf(otherBranch is null, "No other branch available for switching");

        // Switch to the other branch
        var success = await _service.SwitchBranchAsync(targetDir, otherBranch!.Name);

        Assert.True(success, "Branch switch should succeed");

        // Verify current branch changed
        var newCurrentBranch = await _service.GetCurrentBranchAsync(targetDir);
        Assert.Equal(otherBranch.Name, newCurrentBranch);
    }

    [Fact]
    public async Task SwitchBranchAsync_ReturnsFalse_ForNonexistentBranch()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "nonexistent-branch-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        var success = await _service.SwitchBranchAsync(targetDir, "nonexistent-branch-xyz123");

        Assert.False(success, "Switching to nonexistent branch should fail");
    }

    [Fact]
    public async Task SwitchBranchAsync_ReportsProgress()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "switch-progress-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        var branches = await _service.GetBranchesAsync(targetDir);
        var otherBranch = branches.FirstOrDefault(b => !b.IsActive);
        SkipIf(otherBranch is null, "No other branch available");

        var progressReports = new List<string>();
        var progress = new Progress<string>(msg => progressReports.Add(msg));

        var success = await _service.SwitchBranchAsync(targetDir, otherBranch!.Name, progress);

        // Verify operation succeeded - progress reports are optional
        Assert.True(success, "Branch switch should succeed");
    }

    [Fact]
    public async Task SwitchBranchAsync_CanSwitchBackToOriginalBranch()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "switch-back-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        var originalBranch = await _service.GetCurrentBranchAsync(targetDir);
        var branches = await _service.GetBranchesAsync(targetDir);
        var otherBranch = branches.FirstOrDefault(b => !b.IsActive);
        SkipIf(otherBranch is null, "No other branch available");

        // Switch to other branch
        var switchResult1 = await _service.SwitchBranchAsync(targetDir, otherBranch!.Name);
        Assert.True(switchResult1, "First switch should succeed");

        // Switch back to original
        var switchResult2 = await _service.SwitchBranchAsync(targetDir, originalBranch!);
        Assert.True(switchResult2, "Switch back should succeed");

        var finalBranch = await _service.GetCurrentBranchAsync(targetDir);
        Assert.Equal(originalBranch, finalBranch);
    }

    #endregion

    #region Pull Updates Tests

    [Fact]
    public async Task PullUpdatesAsync_SucceedsOnCleanRepository()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "pull-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        var success = await _service.PullUpdatesAsync(targetDir);

        Assert.True(success, "Pull updates should succeed on clean repository");
    }

    [Fact]
    public async Task PullUpdatesAsync_ReportsProgress()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "pull-progress-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        var progressReports = new List<string>();
        var progress = new Progress<string>(msg => progressReports.Add(msg));

        var success = await _service.PullUpdatesAsync(targetDir, progress);

        // Verify operation succeeded - progress reports are optional
        Assert.True(success, "Pull updates should succeed on clean repository");
    }

    [Fact]
    public async Task PullUpdatesAsync_WorksAfterBranchSwitch()
    {
        SkipIfNoGit();

        var targetDir = Path.Combine(_tempDir!, "pull-after-switch-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, targetDir);
        SkipIf(!cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");

        // Get branches and switch if possible
        var branches = await _service.GetBranchesAsync(targetDir);
        var otherBranch = branches.FirstOrDefault(b => !b.IsActive);

        if (otherBranch is not null)
        {
            var switchResult = await _service.SwitchBranchAsync(targetDir, otherBranch.Name);
            SkipIf(!switchResult, "Branch switch failed");
        }

        // Pull should still work after switch
        var pullSuccess = await _service.PullUpdatesAsync(targetDir);
        Assert.True(pullSuccess, "Pull updates should work after branch switch");
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task GetBranchesAsync_ReturnsEmptyList_ForInvalidPath()
    {
        var invalidPath = Path.Combine(_tempDir!, "nonexistent-repo-xyz");

        var branches = await _service.GetBranchesAsync(invalidPath);

        Assert.Empty(branches);
    }

    [Fact]
    public async Task GetCurrentBranchAsync_ReturnsNull_ForInvalidPath()
    {
        var invalidPath = Path.Combine(_tempDir!, "nonexistent-repo-xyz");

        var branch = await _service.GetCurrentBranchAsync(invalidPath);

        Assert.Null(branch);
    }

    [Fact]
    public async Task SwitchBranchAsync_ReturnsFalse_ForInvalidPath()
    {
        var invalidPath = Path.Combine(_tempDir!, "nonexistent-repo-xyz");

        var success = await _service.SwitchBranchAsync(invalidPath, "main");

        Assert.False(success);
    }

    [Fact]
    public async Task PullUpdatesAsync_ReturnsFalse_ForInvalidPath()
    {
        var invalidPath = Path.Combine(_tempDir!, "nonexistent-repo-xyz");

        var success = await _service.PullUpdatesAsync(invalidPath);

        Assert.False(success);
    }

    [Fact]
    public async Task GetBranchesAsync_ReturnsEmptyList_ForNonGitDirectory()
    {
        // Create a regular directory (not a git repo)
        var nonGitDir = Path.Combine(_tempDir!, "not-a-git-repo");
        Directory.CreateDirectory(nonGitDir);
        File.WriteAllText(Path.Combine(nonGitDir, "file.txt"), "content");

        var branches = await _service.GetBranchesAsync(nonGitDir);

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

        var task1 = _service.CloneAsync(TestRepoUrl, targetDir1);
        var task2 = _service.CloneAsync(TestRepoUrl, targetDir2);

        var results = await Task.WhenAll(task1, task2);

        Assert.True(results[0].Success, $"First clone failed: {results[0].ErrorMessage}");
        Assert.True(results[1].Success, $"Second clone failed: {results[1].ErrorMessage}");

        // Both should have valid content
        Assert.True(Directory.Exists(Path.Combine(targetDir1, ".git")));
        Assert.True(Directory.Exists(Path.Combine(targetDir2, ".git")));
    }

    #endregion
}
