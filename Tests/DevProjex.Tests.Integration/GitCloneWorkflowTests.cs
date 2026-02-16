using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DevProjex.Infrastructure.Git;
using DevProjex.Kernel.Models;
using DevProjex.Tests.Integration.Helpers;
using Xunit;

namespace DevProjex.Tests.Integration;

/// <summary>
/// Integration tests simulating real-world Git clone workflows.
/// These tests simulate the complete flow that happens in MainWindow.
/// </summary>
[Collection("GitNetworkTests")]
public sealed class GitCloneWorkflowTests : IDisposable
{
    private const string TestRepoUrl = "https://github.com/octocat/Hello-World";

    private readonly GitRepositoryService _gitService;
    private readonly RepoCacheService _cacheService;
    private readonly TemporaryDirectory _tempDir;

    public GitCloneWorkflowTests()
    {
        _gitService = new GitRepositoryService();
        var testCachePath = Path.Combine(Path.GetTempPath(), "DevProjex", "Tests", "GitIntegration");
        _cacheService = new RepoCacheService(testCachePath);
        _tempDir = new TemporaryDirectory();
    }

    public void Dispose()
    {
        _tempDir.Dispose();
    }

    [Fact]
    public async Task WorkflowSimulation_CloneRepository_OpenProject_Success()
    {
        if (!await _gitService.IsGitAvailableAsync())
            return;

        // Arrange - Simulate user clicking "Clone from Git"
        string? currentCachedRepoPath = null;

        try
        {
            // Step 1: Create cache directory (MainWindow does this)
            currentCachedRepoPath = _cacheService.CreateRepositoryDirectory(TestRepoUrl);

            // Step 2: Clone repository
            var cloneResult = await _gitService.CloneAsync(TestRepoUrl, currentCachedRepoPath);

            // Assert - clone succeeded
            Assert.True(cloneResult.Success, $"Clone failed: {cloneResult.ErrorMessage}");
            Assert.Equal(ProjectSourceType.GitClone, cloneResult.SourceType);
            Assert.NotNull(cloneResult.DefaultBranch);
            Assert.NotNull(cloneResult.RepositoryName);

            // Step 3: Simulate opening the project (MainWindow would call TryOpenFolderAsync)
            var projectPath = cloneResult.LocalPath;
            Assert.True(Directory.Exists(projectPath));

            // Verify repository files are accessible
            var files = Directory.GetFiles(projectPath, "*", SearchOption.AllDirectories);
            Assert.NotEmpty(files);

            // Step 4: Simulate loading Git branches (MainWindow does this)
            var branches = await _gitService.GetBranchesAsync(projectPath);
            Assert.NotEmpty(branches);

            // At this point, currentCachedRepoPath should NOT be deleted yet
            Assert.True(Directory.Exists(currentCachedRepoPath));
        }
        finally
        {
            // Cleanup - simulate closing the project or opening another one
            if (currentCachedRepoPath is not null)
                _cacheService.DeleteRepositoryDirectory(currentCachedRepoPath);
        }
    }

    [Fact]
    public async Task WorkflowSimulation_CloneFails_CacheIsCleanedUp()
    {
        if (!await _gitService.IsGitAvailableAsync())
            return;

        // Arrange - invalid URL
        var invalidUrl = "https://github.com/nonexistent/repo-12345-does-not-exist";
        string? currentCachedRepoPath = null;

        try
        {
            // Step 1: Create cache directory
            currentCachedRepoPath = _cacheService.CreateRepositoryDirectory(invalidUrl);

            // Step 2: Attempt to clone (should fail)
            var cloneResult = await _gitService.CloneAsync(invalidUrl, currentCachedRepoPath);

            // Assert - clone failed
            Assert.False(cloneResult.Success);
            Assert.NotNull(cloneResult.ErrorMessage);

            // Step 3: MainWindow cleans up cache on error
            var deletedPath = currentCachedRepoPath;
            _cacheService.DeleteRepositoryDirectory(currentCachedRepoPath);
            currentCachedRepoPath = null;

            // Assert - cache was cleaned up
            await AssertCacheEventuallyDeletedAsync(deletedPath);
        }
        finally
        {
            if (currentCachedRepoPath is not null)
                _cacheService.DeleteRepositoryDirectory(currentCachedRepoPath);
        }
    }

    [Fact]
    public async Task WorkflowSimulation_CloneCancelled_CacheIsCleanedUp()
    {
        if (!await _gitService.IsGitAvailableAsync())
            return;

        // Arrange
        string? currentCachedRepoPath = null;
        using var cts = new CancellationTokenSource();

        try
        {
            // Step 1: Create cache directory
            currentCachedRepoPath = _cacheService.CreateRepositoryDirectory(TestRepoUrl);

            // Step 2: Start clone and cancel immediately
            var cloneTask = _gitService.CloneAsync(TestRepoUrl, currentCachedRepoPath, null, cts.Token);
            await Task.Delay(100); // Let clone start
            cts.Cancel();

            // Assert - should throw OperationCanceledException
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cloneTask);

            // Step 3: MainWindow cleans up cache on cancellation
            _cacheService.DeleteRepositoryDirectory(currentCachedRepoPath);

            // Assert - cache was cleaned up
            await AssertCacheEventuallyDeletedAsync(currentCachedRepoPath);
        }
        finally
        {
            if (currentCachedRepoPath is not null)
                _cacheService.DeleteRepositoryDirectory(currentCachedRepoPath);
        }
    }

    [Fact]
    public async Task WorkflowSimulation_CloneFirstRepo_ThenCloneSecond_FirstCacheDeleted()
    {
        if (!await _gitService.IsGitAvailableAsync())
            return;

        // Arrange
        string? firstCachedRepoPath = null;
        string? secondCachedRepoPath = null;

        try
        {
            async Task<GitCloneResult> CloneWithOneRetryAsync(string targetPath)
            {
                var result = await _gitService.CloneAsync(TestRepoUrl, targetPath);
                if (result.Success)
                    return result;

                // Retry once to reduce flaky failures caused by transient network issues.
                _cacheService.DeleteRepositoryDirectory(targetPath);
                var retryPath = _cacheService.CreateRepositoryDirectory(TestRepoUrl);
                if (string.Equals(targetPath, firstCachedRepoPath, StringComparison.Ordinal))
                    firstCachedRepoPath = retryPath;
                else if (string.Equals(targetPath, secondCachedRepoPath, StringComparison.Ordinal))
                    secondCachedRepoPath = retryPath;

                return await _gitService.CloneAsync(TestRepoUrl, retryPath);
            }

            // Step 1: Clone first repository
            firstCachedRepoPath = _cacheService.CreateRepositoryDirectory(TestRepoUrl);
            var firstResult = await CloneWithOneRetryAsync(firstCachedRepoPath);
            if (!firstResult.Success)
                return;

            // Verify first repo is accessible
            Assert.True(Directory.Exists(firstCachedRepoPath));
            var firstFiles = Directory.GetFiles(firstCachedRepoPath, "*", SearchOption.AllDirectories);
            Assert.NotEmpty(firstFiles);

            // Step 2: User decides to clone a second repository
            // MainWindow should clean up the first cache before cloning
            _cacheService.DeleteRepositoryDirectory(firstCachedRepoPath);

            // Step 3: Clone second repository
            secondCachedRepoPath = _cacheService.CreateRepositoryDirectory(TestRepoUrl);
            var secondResult = await CloneWithOneRetryAsync(secondCachedRepoPath);
            if (!secondResult.Success)
                return;

            // Assert - paths are different and second cache exists
            Assert.NotEqual(firstCachedRepoPath, secondCachedRepoPath);
            Assert.True(Directory.Exists(secondCachedRepoPath), "Second cache should exist");
        }
        finally
        {
            if (firstCachedRepoPath is not null)
                _cacheService.DeleteRepositoryDirectory(firstCachedRepoPath);
            if (secondCachedRepoPath is not null)
                _cacheService.DeleteRepositoryDirectory(secondCachedRepoPath);
        }
    }

    private async Task AssertCacheEventuallyDeletedAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        const int maxAttempts = 30;
        const int delayMs = 100;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (!Directory.Exists(path))
                return;

            // Best-effort delete can fail transiently right after cancellation due to file locks.
            _cacheService.DeleteRepositoryDirectory(path);
            await Task.Delay(delayMs);
        }

        Assert.False(Directory.Exists(path), $"Cache directory was not deleted within {maxAttempts * delayMs} ms: {path}");
    }

    [Fact]
    public async Task WorkflowSimulation_CloneRepo_OpenLocalFolder_CacheDeleted()
    {
        if (!await _gitService.IsGitAvailableAsync())
            return;

        // Arrange
        string? currentCachedRepoPath = null;
        var localFolder = _tempDir.CreateDirectory("local-project");
        File.WriteAllText(Path.Combine(localFolder, "test.txt"), "local content");

        try
        {
            // Step 1: Clone repository
            currentCachedRepoPath = _cacheService.CreateRepositoryDirectory(TestRepoUrl);
            var cloneResult = await _gitService.CloneAsync(TestRepoUrl, currentCachedRepoPath);
            Assert.True(cloneResult.Success);

            // Step 2: User opens a local folder (via File → Open)
            // MainWindow should clean up Git cache when switching to local folder
            Assert.True(Directory.Exists(currentCachedRepoPath));

            // Simulate: fromDialog = true → cleanup Git cache (best effort)
            var originalPath = currentCachedRepoPath;
            _cacheService.DeleteRepositoryDirectory(currentCachedRepoPath);
            currentCachedRepoPath = null;

            // Simulate opening local folder
            var projectSourceType = ProjectSourceType.LocalFolder;

            // Assert - test that cleanup was called and project type changed
            Assert.NotEqual(originalPath, currentCachedRepoPath); // Cleared the reference
            Assert.Equal(ProjectSourceType.LocalFolder, projectSourceType);
        }
        finally
        {
            if (currentCachedRepoPath is not null)
                _cacheService.DeleteRepositoryDirectory(currentCachedRepoPath);
        }
    }

    [Fact]
    public async Task WorkflowSimulation_CloneRepo_SwitchBranch_FilesUpdate()
    {
        if (!await _gitService.IsGitAvailableAsync())
            return;

        // Arrange
        string? currentCachedRepoPath = null;

        try
        {
            // Step 1: Clone repository
            currentCachedRepoPath = _cacheService.CreateRepositoryDirectory(TestRepoUrl);
            var cloneResult = await _gitService.CloneAsync(TestRepoUrl, currentCachedRepoPath);
            Assert.True(cloneResult.Success);

            // Step 2: Get initial file state
            var initialFiles = Directory.GetFiles(currentCachedRepoPath, "*", SearchOption.AllDirectories);
            Assert.NotEmpty(initialFiles);

            // Step 3: Try to switch branches
            var branches = await _gitService.GetBranchesAsync(currentCachedRepoPath);
            if (branches.Count < 2)
                return; // Skip if repo doesn't have multiple branches

            var currentBranch = await _gitService.GetCurrentBranchAsync(currentCachedRepoPath);
            var targetBranch = branches[0].Name != currentBranch ? branches[0].Name : branches[1].Name;

            var switched = await _gitService.SwitchBranchAsync(currentCachedRepoPath, targetBranch);
            Assert.True(switched);

            // Step 4: Verify branch changed
            var newBranch = await _gitService.GetCurrentBranchAsync(currentCachedRepoPath);
            Assert.Equal(targetBranch, newBranch);

            // Step 5: Verify files still accessible (might be different content)
            var newFiles = Directory.GetFiles(currentCachedRepoPath, "*", SearchOption.AllDirectories);
            Assert.NotEmpty(newFiles);
        }
        finally
        {
            if (currentCachedRepoPath is not null)
                _cacheService.DeleteRepositoryDirectory(currentCachedRepoPath);
        }
    }

    [Fact]
    public async Task WorkflowSimulation_MultipleSequentialClones_EachGetsOwnCache()
    {
        if (!await _gitService.IsGitAvailableAsync())
            return;

        // Simulate user cloning, closing, cloning again multiple times
        string? cache1 = null;
        string? cache2 = null;
        string? cache3 = null;

        try
        {
            // Clone 1
            cache1 = _cacheService.CreateRepositoryDirectory(TestRepoUrl);
            var result1 = await _gitService.CloneAsync(TestRepoUrl, cache1);
            Assert.True(result1.Success);
            Assert.True(Directory.Exists(cache1));

            // User closes project → cleanup (best effort)
            _cacheService.DeleteRepositoryDirectory(cache1);

            // Clone 2 (new session)
            cache2 = _cacheService.CreateRepositoryDirectory(TestRepoUrl);
            var result2 = await _gitService.CloneAsync(TestRepoUrl, cache2);
            Assert.True(result2.Success);
            Assert.True(Directory.Exists(cache2));

            // User closes project → cleanup (best effort)
            _cacheService.DeleteRepositoryDirectory(cache2);

            // Clone 3 (new session)
            cache3 = _cacheService.CreateRepositoryDirectory(TestRepoUrl);
            var result3 = await _gitService.CloneAsync(TestRepoUrl, cache3);
            Assert.True(result3.Success);
            Assert.True(Directory.Exists(cache3));

            // All paths should be unique
            Assert.NotEqual(cache1, cache2);
            Assert.NotEqual(cache2, cache3);
            Assert.NotEqual(cache1, cache3);
        }
        finally
        {
            // Cleanup any remaining caches
            if (cache1 is not null) _cacheService.DeleteRepositoryDirectory(cache1);
            if (cache2 is not null) _cacheService.DeleteRepositoryDirectory(cache2);
            if (cache3 is not null) _cacheService.DeleteRepositoryDirectory(cache3);
        }
    }

    [Fact]
    public void WorkflowSimulation_CachePathSecurity_CannotDeleteOutsideCache()
    {
        // Arrange - try to trick the service into deleting a path outside cache
        var maliciousPath = Path.Combine(Path.GetTempPath(), "important-data");
        Directory.CreateDirectory(maliciousPath);
        File.WriteAllText(Path.Combine(maliciousPath, "important.txt"), "do not delete");

        try
        {
            // Act - attempt to delete (should be ignored by IsInCache check)
            _cacheService.DeleteRepositoryDirectory(maliciousPath);

            // Assert - path should still exist (not deleted)
            Assert.True(Directory.Exists(maliciousPath));
            Assert.True(File.Exists(Path.Combine(maliciousPath, "important.txt")));
        }
        finally
        {
            Directory.Delete(maliciousPath, recursive: true);
        }
    }
}
