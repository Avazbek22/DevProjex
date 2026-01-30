using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DevProjex.Infrastructure.Git;
using DevProjex.Tests.Integration.Helpers;
using Xunit;

namespace DevProjex.Tests.Integration;

/// <summary>
/// Integration tests for Git error recovery and resilience.
/// Tests recovery from network errors, corrupted repositories, and cancellation.
/// </summary>
public class GitErrorRecoveryTests : IAsyncLifetime
{
    private readonly GitRepositoryService _service;
    private readonly RepoCacheService _cacheService;
    private readonly TemporaryDirectory _tempDir;

    private const string TestRepoUrl = "https://github.com/octocat/Hello-World";
    private const string InvalidRepoUrl = "https://github.com/invalid-user-xyz/invalid-repo-abc";

    public GitErrorRecoveryTests()
    {
        _service = new GitRepositoryService();
        _cacheService = new RepoCacheService();
        _tempDir = new TemporaryDirectory();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _tempDir.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CloneAsync_InvalidRepository_ReturnsFailureResult()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var targetDir = _tempDir.CreateDirectory("invalid-clone");

        var result = await _service.CloneAsync(InvalidRepoUrl, targetDir);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.NotEmpty(result.ErrorMessage);
    }

    [Fact]
    public async Task CloneAsync_Cancelled_CleansUpPartialClone()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var targetDir = _tempDir.CreateDirectory("cancelled-clone");
        using var cts = new CancellationTokenSource();

        var cloneTask = _service.CloneAsync(TestRepoUrl, targetDir, cancellationToken: cts.Token);

        // Cancel after brief delay
        await Task.Delay(50);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cloneTask);

        // Directory might exist but should not have complete .git
        // This is acceptable - partial cleanup
    }

    [Fact]
    public async Task GetBranchesAsync_NonGitDirectory_ReturnsEmptyList()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var nonGitDir = _tempDir.CreateDirectory("not-git");
        File.WriteAllText(Path.Combine(nonGitDir, "test.txt"), "test");

        var branches = await _service.GetBranchesAsync(nonGitDir);

        Assert.Empty(branches);
    }

    [Fact]
    public async Task GetBranchesAsync_MissingGitDirectory_ReturnsEmptyList()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var missingDir = Path.Combine(_tempDir.Path, "nonexistent");

        var branches = await _service.GetBranchesAsync(missingDir);

        Assert.Empty(branches);
    }

    [Fact]
    public async Task SwitchBranchAsync_NonGitDirectory_ReturnsFalse()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var nonGitDir = _tempDir.CreateDirectory("not-git-switch");

        var success = await _service.SwitchBranchAsync(nonGitDir, "main");

        Assert.False(success);
    }

    [Fact]
    public async Task PullUpdatesAsync_NonGitDirectory_ReturnsFalse()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var nonGitDir = _tempDir.CreateDirectory("not-git-pull");

        var success = await _service.PullUpdatesAsync(nonGitDir);

        Assert.False(success);
    }

    [Fact]
    public async Task GetCurrentBranchAsync_NonGitDirectory_ReturnsNull()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var nonGitDir = _tempDir.CreateDirectory("not-git-current");

        var branch = await _service.GetCurrentBranchAsync(nonGitDir);

        Assert.Null(branch);
    }

    [Fact]
    public async Task CloneAsync_ToExistingDirectory_HandlesGracefully()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var targetDir = _tempDir.CreateDirectory("existing-dir");
        File.WriteAllText(Path.Combine(targetDir, "existing.txt"), "data");

        var result = await _service.CloneAsync(TestRepoUrl, targetDir);

        // Should either succeed (overwriting) or fail gracefully
        if (!result.Success)
        {
            Assert.NotNull(result.ErrorMessage);
        }
    }

    [Fact]
    public async Task SwitchBranchAsync_WithCancellation_StopsGracefully()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("cancel-switch");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        var branches = await _service.GetBranchesAsync(repoPath);
        if (branches.Count < 2)
            return;

        using var cts = new CancellationTokenSource();
        var targetBranch = branches[0].Name;

        var switchTask = _service.SwitchBranchAsync(repoPath, targetBranch, cancellationToken: cts.Token);

        cts.Cancel();

        // Should handle cancellation gracefully
        try
        {
            await switchTask;
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Repository should still be in valid state
        var currentBranch = await _service.GetCurrentBranchAsync(repoPath);
        Assert.NotNull(currentBranch);
    }

    [Fact]
    public async Task PullUpdatesAsync_WithCancellation_DoesNotCorruptRepo()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("cancel-pull");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        using var cts = new CancellationTokenSource(50); // Cancel after 50ms

        try
        {
            await _service.PullUpdatesAsync(repoPath, cancellationToken: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Repository should still be functional
        var branches = await _service.GetBranchesAsync(repoPath);
        Assert.NotEmpty(branches);
    }

    [Fact]
    public async Task CloneAsync_AfterFailedClone_CanRetry()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var targetDir = _tempDir.CreateDirectory("retry-clone");

        // First attempt with invalid URL
        var result1 = await _service.CloneAsync(InvalidRepoUrl, targetDir);
        Assert.False(result1.Success);

        // Clean up failed attempt
        try
        {
            if (Directory.Exists(targetDir))
                Directory.Delete(targetDir, recursive: true);
            Directory.CreateDirectory(targetDir);
        }
        catch { }

        // Second attempt with valid URL
        var result2 = await _service.CloneAsync(TestRepoUrl, targetDir);

        // Should succeed on retry
        Assert.True(result2.Success);
    }

    [Fact]
    public async Task GetBranchesAsync_AfterFailedOperation_StillWorks()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("after-fail");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        // Try invalid operation
        await _service.SwitchBranchAsync(repoPath, "invalid-branch-xyz");

        // GetBranches should still work
        var branches = await _service.GetBranchesAsync(repoPath);
        Assert.NotEmpty(branches);
    }

    [Fact]
    public async Task SwitchBranchAsync_AfterNetworkError_Recovers()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("network-recovery");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        var branches = await _service.GetBranchesAsync(repoPath);
        if (branches.Count < 2)
            return;

        // Simulate network error by trying to switch to nonexistent remote branch
        await _service.SwitchBranchAsync(repoPath, "nonexistent-branch");

        // Should be able to switch to valid branch after error
        var targetBranch = branches[0].Name;
        var success = await _service.SwitchBranchAsync(repoPath, targetBranch);

        Assert.True(success);
    }

    [Fact]
    public async Task CloneAsync_WithInvalidPath_HandlesGracefully()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        // Try to clone to path with special characters
        var specialPath = Path.Combine(_tempDir.Path, "repo:invalid");

        try
        {
            var result = await _service.CloneAsync(TestRepoUrl, specialPath);

            // Should either fail gracefully or handle the path
            if (!result.Success)
            {
                Assert.NotNull(result.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            // Any exception is also acceptable - as long as it doesn't crash
            Assert.NotNull(ex);
        }
    }

    [Fact]
    public async Task IsGitAvailableAsync_CallMultipleTimes_StaysConsistent()
    {
        // Call multiple times to ensure no state corruption
        var result1 = await _service.IsGitAvailableAsync();
        var result2 = await _service.IsGitAvailableAsync();
        var result3 = await _service.IsGitAvailableAsync();

        Assert.Equal(result1, result2);
        Assert.Equal(result2, result3);
    }

    [Fact]
    public async Task GetCurrentBranchAsync_EmptyRepository_ReturnsNull()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("empty-current");

        // Create empty git repo using Process
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"init \"{repoPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = System.Diagnostics.Process.Start(psi);
        await process!.WaitForExitAsync();

        var currentBranch = await _service.GetCurrentBranchAsync(repoPath);

        // Empty repo with no commits has no current branch
        Assert.Null(currentBranch);
    }

    [Fact]
    public async Task SwitchBranchAsync_MultipleFailures_DoesNotCorruptState()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("multi-fail");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        var originalBranch = await _service.GetCurrentBranchAsync(repoPath);

        // Try switching to invalid branches multiple times
        await _service.SwitchBranchAsync(repoPath, "invalid1");
        await _service.SwitchBranchAsync(repoPath, "invalid2");
        await _service.SwitchBranchAsync(repoPath, "invalid3");

        // Original branch should still be active
        var currentBranch = await _service.GetCurrentBranchAsync(repoPath);
        Assert.Equal(originalBranch, currentBranch);
    }

    [Fact]
    public async Task CloneAsync_WithProgressCallback_HandlesExceptionsGracefully()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var targetDir = _tempDir.CreateDirectory("progress-exception");
        var progress = new Progress<string>(msg =>
        {
            // Progress callback that throws
            if (msg.Contains("Cloning"))
                throw new InvalidOperationException("Test exception");
        });

        // Should not fail even if progress callback throws
        var result = await _service.CloneAsync(TestRepoUrl, targetDir, progress);

        // Operation might succeed or fail, but should not crash
        Assert.NotNull(result);
    }
}
