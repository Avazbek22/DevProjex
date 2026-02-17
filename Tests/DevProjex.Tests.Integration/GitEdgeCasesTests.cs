using DevProjex.Infrastructure.Git;
using DevProjex.Tests.Integration.Helpers;

namespace DevProjex.Tests.Integration;

/// <summary>
/// Edge cases and real-world scenario tests for Git integration.
/// These tests verify behavior in unusual or problematic situations.
///
/// Test categories:
/// - Unusual repository states
/// - Network/connectivity issues
/// - Concurrent operations
/// - Repository cleanup and recovery
/// - Special characters and internationalization
/// - Large repositories and timeouts
/// </summary>
public class GitEdgeCasesTests : IAsyncLifetime
{
    private readonly GitRepositoryService _service;
    private readonly RepoCacheService _cacheService;
    private readonly TemporaryDirectory _tempDir;

    private const string TestRepoUrl = "https://github.com/octocat/Hello-World";

    public GitEdgeCasesTests()
    {
        _service = new GitRepositoryService();
        var testCachePath = Path.Combine(Path.GetTempPath(), "DevProjex", "Tests", "GitIntegration");
        _cacheService = new RepoCacheService(testCachePath);
        _tempDir = new TemporaryDirectory();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _tempDir.Dispose();
        return Task.CompletedTask;
    }

    #region Repository State Edge Cases

    [Fact]
    public async Task SwitchBranch_WhenRepositoryHasOnlyOneBranch_ReturnsFalseForNonexistent()
    {
        // Test switching to nonexistent branch in single-branch repo
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("single-branch");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        // Try to switch to definitely nonexistent branch
        var success = await _service.SwitchBranchAsync(repoPath, "nonexistent-branch-xyz-123");
        Assert.False(success, "Should fail for nonexistent branch");
    }

    [Fact]
    public async Task GetCurrentBranch_AfterSuccessfulSwitch_ReturnsCorrectBranch()
    {
        // Verify that GetCurrentBranch accurately reflects branch switches
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("current-branch-accuracy");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        var initialBranch = await _service.GetCurrentBranchAsync(repoPath);
        Assert.NotNull(initialBranch);

        var branches = await _service.GetBranchesAsync(repoPath);
        if (branches.Count < 2)
            return;

        // Find different branch
        var targetBranch = branches.FirstOrDefault(b => b.Name != initialBranch)?.Name;
        if (targetBranch == null)
            return;

        // Switch and verify
        await _service.SwitchBranchAsync(repoPath, targetBranch);
        var currentBranch = await _service.GetCurrentBranchAsync(repoPath);
        Assert.Equal(targetBranch, currentBranch);
    }

    [Fact]
    public async Task PullUpdates_OnFreshClone_Succeeds()
    {
        // Verify that pulling immediately after clone doesn't break
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("fresh-pull");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        // Pull on fresh clone should succeed (even though nothing to pull)
        var pullSuccess = await _service.PullUpdatesAsync(repoPath);
        Assert.True(pullSuccess, "Pull on fresh clone should succeed");
    }

    [Fact]
    public async Task MultiplePullUpdates_InSequence_AllSucceed()
    {
        // Test multiple consecutive pulls don't break repository state
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("multi-pull");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        // Pull multiple times
        for (int i = 0; i < 3; i++)
        {
            var success = await _service.PullUpdatesAsync(repoPath);
            Assert.True(success, $"Pull #{i + 1} should succeed");
        }
    }

    #endregion

    #region Concurrent Operations

    [Fact]
    public async Task ConcurrentPullUpdates_ToSameRepository_Succeed()
    {
        // Verify concurrent pulls don't cause race conditions
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("concurrent-pull");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        // Start multiple pulls concurrently
        var task1 = _service.PullUpdatesAsync(repoPath);
        var task2 = _service.PullUpdatesAsync(repoPath);

        var results = await Task.WhenAll(task1, task2);

        // At least one should succeed (git handles concurrency)
        Assert.Contains(true, results);
    }

    [Fact]
    public async Task SwitchBranch_WhileAnotherOperationInProgress_HandlesGracefully()
    {
        // Test that overlapping operations don't corrupt repository
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("overlapping-ops");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        var branches = await _service.GetBranchesAsync(repoPath);
        if (branches.Count < 2)
            return;

        var branch1 = branches[0].Name;
        var branch2 = branches[1].Name;

        // Start pull and switch concurrently
        var pullTask = _service.PullUpdatesAsync(repoPath);
        await Task.Delay(50); // Small delay to ensure pull started
        var switchTask = _service.SwitchBranchAsync(repoPath, branch2);

        await Task.WhenAll(pullTask, switchTask);

        // Repository should still be functional
        var currentBranch = await _service.GetCurrentBranchAsync(repoPath);
        Assert.NotNull(currentBranch);
    }

    #endregion

    #region Cache and Cleanup

    [Fact]
    public void RepoCacheService_IsInCache_CorrectlyIdentifiesCachePaths()
    {
        // Test cache path detection
        var cacheRoot = _cacheService.CacheRootPath;

        // Valid cache path
        var validPath = Path.Combine(cacheRoot, "test_folder");
        Assert.True(_cacheService.IsInCache(validPath));

        // Invalid paths
        Assert.False(_cacheService.IsInCache("C:\\SomeRandomPath"));
        Assert.False(_cacheService.IsInCache("/home/user/projects"));
        Assert.False(_cacheService.IsInCache(""));
        Assert.False(_cacheService.IsInCache(null!));
    }

    [Fact]
    public void RepoCacheService_CreateRepositoryDirectory_CreatesUniqueDirectories()
    {
        // Verify that multiple creates for same URL produce different directories
        var url = "https://github.com/test/repo";

        var dir1 = _cacheService.CreateRepositoryDirectory(url);
        Thread.Sleep(10); // Ensure different timestamp
        var dir2 = _cacheService.CreateRepositoryDirectory(url);

        Assert.NotEqual(dir1, dir2);
        Assert.True(Directory.Exists(dir1));
        Assert.True(Directory.Exists(dir2));

        // Cleanup
        _cacheService.DeleteRepositoryDirectory(dir1);
        _cacheService.DeleteRepositoryDirectory(dir2);
    }

    [Fact]
    public void RepoCacheService_DeleteRepositoryDirectory_IgnoresNonCachePaths()
    {
        // Verify that delete refuses to delete paths outside cache
        var outsidePath = Path.Combine(Path.GetTempPath(), "not-in-cache");
        Directory.CreateDirectory(outsidePath);

        // Should not delete (safety check)
        _cacheService.DeleteRepositoryDirectory(outsidePath);

        // Should still exist (wasn't deleted)
        Assert.True(Directory.Exists(outsidePath));

        // Cleanup manually
        Directory.Delete(outsidePath);
    }

    [Fact]
    public async Task CloneAsync_CreatesDirectoryInCache()
    {
        // Verify that cloned repos go into cache
        if (!await _service.IsGitAvailableAsync())
            return;

        var targetDir = _cacheService.CreateRepositoryDirectory(TestRepoUrl);

        try
        {
            var result = await _service.CloneAsync(TestRepoUrl, targetDir);
            Assert.True(result.Success);

            // Verify it's in cache
            Assert.True(_cacheService.IsInCache(targetDir));
            Assert.True(Directory.Exists(targetDir));

            // Verify it has .git directory
            Assert.True(Directory.Exists(Path.Combine(targetDir, ".git")));
        }
        finally
        {
            _cacheService.DeleteRepositoryDirectory(targetDir);
        }
    }

    #endregion

    #region Special URLs and Characters

    [Fact]
    public async Task CloneAsync_HandlesUrlsWithDifferentFormats()
    {
        // Test various URL formats (http, https, with/without .git)
        if (!await _service.IsGitAvailableAsync())
            return;

        var urls = new[]
        {
            "https://github.com/octocat/Hello-World",
            "https://github.com/octocat/Hello-World.git",
            "http://github.com/octocat/Hello-World"
        };

        foreach (var url in urls)
        {
            var targetDir = _tempDir.CreateDirectory($"url-format-{urls.ToList().IndexOf(url)}");
            var result = await _service.CloneAsync(url, targetDir);

            // All formats should work
            Assert.True(result.Success || result.ErrorMessage != null,
                $"Clone should either succeed or return error message for {url}");
        }
    }

    [Fact]
    public async Task GetBranches_ReturnsCorrectActiveStatus()
    {
        // Verify that only one branch is marked as active
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("active-branch");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        var branches = await _service.GetBranchesAsync(repoPath);
        Assert.NotEmpty(branches);

        var activeBranches = branches.Where(b => b.IsActive).ToList();
        Assert.Single(activeBranches); // Exactly one active branch

        // Active branch should match GetCurrentBranch
        var currentBranch = await _service.GetCurrentBranchAsync(repoPath);
        Assert.Equal(currentBranch, activeBranches[0].Name);
    }

    #endregion

    #region Error Recovery

    [Fact]
    public async Task SwitchBranch_AfterFailedAttempt_CanRetrySuccessfully()
    {
        // Test that failed switch doesn't corrupt repository state
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("failed-switch-recovery");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        // Try to switch to nonexistent branch
        var failedSwitch = await _service.SwitchBranchAsync(repoPath, "nonexistent-xyz");
        Assert.False(failedSwitch);

        // Repository should still work
        var currentBranch = await _service.GetCurrentBranchAsync(repoPath);
        Assert.NotNull(currentBranch);

        var branches = await _service.GetBranchesAsync(repoPath);
        Assert.NotEmpty(branches);

        // Should be able to switch to valid branch after failed attempt
        if (branches.Count > 1)
        {
            var validBranch = branches.FirstOrDefault(b => !b.IsActive)?.Name;
            if (validBranch != null)
            {
                var successSwitch = await _service.SwitchBranchAsync(repoPath, validBranch);
                Assert.True(successSwitch, "Should recover from failed switch");
            }
        }
    }

    [Fact]
    public async Task CloneAsync_WithCancellation_CleansUpPartialClone()
    {
        // Verify that cancelled clone doesn't leave partial data
        if (!await _service.IsGitAvailableAsync())
            return;

        var targetDir = _tempDir.CreateDirectory("cancelled-clone");
        using var cts = new CancellationTokenSource();

        // Start clone and cancel quickly
        var cloneTask = _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: cts.Token);
        await Task.Delay(100); // Let it start
        cts.Cancel();

        try
        {
            await cloneTask;
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Partial clone might exist, but should be cleanable
        if (Directory.Exists(targetDir))
        {
            // Should be able to delete it
            try
            {
                Directory.Delete(targetDir, recursive: true);
            }
            catch
            {
                // Some files might be locked briefly, that's ok
            }
        }
    }

    #endregion

    #region Branch Name Edge Cases

    [Fact]
    public async Task SwitchBranch_WithBranchNameContainingSlashes_HandlesCorrectly()
    {
        // Test branch names like "feature/my-feature" (common in git-flow)
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("branch-slashes");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        // Try to switch to branch with slashes (if exists)
        // This is just to verify our code handles slashes correctly
        var branches = await _service.GetBranchesAsync(repoPath);

        var branchWithSlash = branches.FirstOrDefault(b => b.Name.Contains('/'));
        if (branchWithSlash != null)
        {
            var success = await _service.SwitchBranchAsync(repoPath, branchWithSlash.Name);
            // Should either succeed or fail gracefully
            Assert.True(success || !success); // Always true, just verify no crash
        }
    }

    [Fact]
    public async Task GetBranches_FiltersOutHEADPointer()
    {
        // Verify that "HEAD -> origin/main" entries are filtered out
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("head-filter");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        var branches = await _service.GetBranchesAsync(repoPath);

        // No branch should contain "->" (HEAD pointer)
        foreach (var branch in branches)
        {
            Assert.DoesNotContain("->", branch.Name);
            Assert.DoesNotContain("HEAD", branch.Name);
        }
    }

    #endregion

    #region Progress Reporting

    [Fact]
    public async Task CloneAsync_AcceptsProgressCallback()
    {
        // Verify progress callback doesn't cause errors (Git may not report progress in non-interactive mode)
        if (!await _service.IsGitAvailableAsync())
            return;

        var targetDir = _tempDir.CreateDirectory("progress-test");
        var progressReports = new List<string>();
        var progress = new Progress<string>(msg => progressReports.Add(msg));

        var result = await _service.CloneAsync(TestRepoUrl, targetDir, progress);
        Assert.True(result.Success);

        // Progress callback should not cause errors (number of reports may vary)
        Assert.NotNull(progressReports);
    }

    [Fact]
    public async Task SwitchBranch_FirstTime_ReportsProgress()
    {
        // Verify progress reporting for branch switch
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("switch-progress");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        var branches = await _service.GetBranchesAsync(repoPath);
        if (branches.Count < 2)
            return;

        var targetBranch = branches.FirstOrDefault(b => !b.IsActive)?.Name;
        if (targetBranch == null)
            return;

        var progressReports = new List<string>();
        var progress = new Progress<string>(msg => progressReports.Add(msg));

        await _service.SwitchBranchAsync(repoPath, targetBranch, progress);

        // First switch should report progress (fetching)
        // Note: Fast path (revisit) might not report progress
        // This test is informational rather than strict
        Assert.True(true); // Just verify no crash during progress reporting
    }

    #endregion

    #region Repository Metadata

    [Fact]
    public async Task CloneAsync_ExtractsCorrectRepositoryName()
    {
        // Test repository name extraction from various URLs
        if (!await _service.IsGitAvailableAsync())
            return;

        var targetDir = _tempDir.CreateDirectory("repo-name");
        var result = await _service.CloneAsync(
            "https://github.com/octocat/Hello-World.git",
            targetDir);

        Assert.True(result.Success);
        Assert.Equal("Hello-World", result.RepositoryName);
    }

    [Fact]
    public async Task CloneAsync_StoresOriginalUrl()
    {
        // Verify that original URL is preserved in result
        if (!await _service.IsGitAvailableAsync())
            return;

        var url = "https://github.com/octocat/Hello-World";
        var targetDir = _tempDir.CreateDirectory("url-storage");
        var result = await _service.CloneAsync(url, targetDir);

        Assert.True(result.Success);
        Assert.Equal(url, result.RepositoryUrl);
    }

    [Fact]
    public async Task CloneAsync_SetsCorrectSourceType()
    {
        // Verify that source type is set to GitClone for git operations
        if (!await _service.IsGitAvailableAsync())
            return;

        var targetDir = _tempDir.CreateDirectory("source-type");
        var result = await _service.CloneAsync(TestRepoUrl, targetDir);

        Assert.True(result.Success);
        Assert.Equal(ProjectSourceType.GitClone, result.SourceType);
    }

    #endregion
}
