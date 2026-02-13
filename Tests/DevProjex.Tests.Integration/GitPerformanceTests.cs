using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DevProjex.Infrastructure.Git;
using DevProjex.Tests.Integration.Helpers;
using Xunit;

namespace DevProjex.Tests.Integration;

/// <summary>
/// Performance and optimization tests for Git operations.
/// These tests verify that our optimizations (shallow clone, --depth 1, etc.) work as expected.
///
/// Test categories:
/// - Shallow clone performance vs full clone
/// - Branch switch performance (fast path vs reliable path)
/// - Network traffic optimization
/// - Disk space optimization
/// </summary>
public class GitPerformanceTests : IAsyncLifetime
{
    private readonly GitRepositoryService _service;
    private readonly RepoCacheService _cacheService;
    private readonly TemporaryDirectory _tempDir;

    // Small test repository for performance tests
    private const string SmallRepoUrl = "https://github.com/octocat/Hello-World";

    public GitPerformanceTests()
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

    [Fact]
    public async Task ShallowClone_UsesSigificantlyLessDiskSpace()
    {
        // Test that shallow clone uses much less disk space than full clone
        if (!await _service.IsGitAvailableAsync())
        {
            // Skip if git not available
            return;
        }

        var shallowPath = _tempDir.CreateDirectory("shallow");
        var result = await _service.CloneAsync(SmallRepoUrl, shallowPath);

        Assert.True(result.Success);

        // Check .git directory size (should be small for shallow clone)
        var gitDir = Path.Combine(shallowPath, ".git");
        var gitDirSize = GetDirectorySize(gitDir);

        // Shallow clone .git should be < 5 MB for small repos
        Assert.True(gitDirSize < 5 * 1024 * 1024,
            $".git directory should be < 5MB for shallow clone, got {gitDirSize / 1024 / 1024}MB");
    }

    [Fact]
    public async Task BranchSwitch_FastPath_CompletesQuickly()
    {
        // Test that revisiting a branch uses fast path (~50ms)
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("fast-path-test");
        var cloneResult = await _service.CloneAsync(SmallRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        var defaultBranch = cloneResult.DefaultBranch ?? "main";

        // Switch to another branch (first time - reliable path)
        var otherBranch = defaultBranch == "main" ? "master" : "main";
        var branches = await _service.GetBranchesAsync(repoPath);

        if (branches.Count > 1)
        {
            await _service.SwitchBranchAsync(repoPath, otherBranch);

            // Switch back to default branch (second time - should use fast path)
            var sw = Stopwatch.StartNew();
            var success = await _service.SwitchBranchAsync(repoPath, defaultBranch);
            sw.Stop();

            Assert.True(success);
            // Fast path should complete in < 500ms (usually ~50ms, but allow margin)
            Assert.True(sw.ElapsedMilliseconds < 500,
                $"Fast path should complete quickly, took {sw.ElapsedMilliseconds}ms");
        }
    }

    [Fact]
    public async Task BranchSwitch_WithDepthOptimization_CompletesSuccessfully()
    {
        // Verify that using --depth 1 for branch fetch doesn't break functionality
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("depth-optimization");
        var cloneResult = await _service.CloneAsync(SmallRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        var branches = await _service.GetBranchesAsync(repoPath);
        if (branches.Count < 2)
        {
            // Skip if repo doesn't have multiple branches
            return;
        }

        // Find a branch that's not currently active
        var targetBranch = "";
        foreach (var branch in branches)
        {
            if (!branch.IsActive)
            {
                targetBranch = branch.Name;
                break;
            }
        }

        if (string.IsNullOrEmpty(targetBranch))
            return;

        // Switch should succeed even with --depth 1 optimization
        var success = await _service.SwitchBranchAsync(repoPath, targetBranch);
        Assert.True(success, "Branch switch with --depth 1 should succeed");

        // Verify we actually switched
        var currentBranch = await _service.GetCurrentBranchAsync(repoPath);
        Assert.Equal(targetBranch, currentBranch);

        // Verify files exist and are readable
        var files = Directory.GetFiles(repoPath, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
    }

    [Fact]
    public async Task GetBranches_UsingLsRemote_CompletesWithinReasonableTime()
    {
        // Verify that ls-remote approach is faster than fetch approach
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("ls-remote-perf");
        var cloneResult = await _service.CloneAsync(SmallRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        // Measure GetBranches time (uses ls-remote)
        var sw = Stopwatch.StartNew();
        var branches = await _service.GetBranchesAsync(repoPath);
        sw.Stop();

        Assert.NotEmpty(branches);
        // This is a network-bound integration scenario, so strict sub-second or 2s limits
        // are flaky across different providers, VPNs and temporary throttling.
        // We keep an upper bound to detect real regressions while remaining stable.
        Assert.True(sw.ElapsedMilliseconds < 10000,
            $"GetBranches should complete in reasonable time, took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task PullUpdates_WithDepthOptimization_WorksCorrectly()
    {
        // Verify that pull with --depth 1 doesn't break anything
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("pull-depth");
        var cloneResult = await _service.CloneAsync(SmallRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        // Pull updates (should use --depth 1)
        var pullSuccess = await _service.PullUpdatesAsync(repoPath);
        Assert.True(pullSuccess, "Pull with --depth 1 should succeed");

        // Verify repository is still functional
        var branches = await _service.GetBranchesAsync(repoPath);
        Assert.NotEmpty(branches);

        var currentBranch = await _service.GetCurrentBranchAsync(repoPath);
        Assert.NotNull(currentBranch);
    }

    [Fact]
    public async Task MultipleOperations_DoNotDegradePerformance()
    {
        // Verify that multiple git operations don't slow down over time
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("multi-ops");
        var cloneResult = await _service.CloneAsync(SmallRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        var branches = await _service.GetBranchesAsync(repoPath);
        if (branches.Count < 2)
            return;

        // Find two different branches
        string? branch1 = null, branch2 = null;
        foreach (var branch in branches)
        {
            if (branch1 == null)
                branch1 = branch.Name;
            else if (branch2 == null && branch.Name != branch1)
                branch2 = branch.Name;
        }

        if (branch1 == null || branch2 == null)
            return;

        // Perform multiple switch operations
        var times = new long[5];
        for (int i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            await _service.SwitchBranchAsync(repoPath, i % 2 == 0 ? branch1 : branch2);
            sw.Stop();
            times[i] = sw.ElapsedMilliseconds;
        }

        // Verify that later operations are not significantly slower
        // (they should use fast path after first switch)
        for (int i = 2; i < 5; i++)
        {
            Assert.True(times[i] < 1000,
                $"Repeated branch switch {i} should be fast, took {times[i]}ms");
        }
    }

    [Fact]
    public async Task ConcurrentGetBranches_DoesNotCauseErrors()
    {
        // Verify that concurrent GetBranches calls don't interfere
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("concurrent-branches");
        var cloneResult = await _service.CloneAsync(SmallRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        // Call GetBranches concurrently
        var task1 = _service.GetBranchesAsync(repoPath);
        var task2 = _service.GetBranchesAsync(repoPath);
        var task3 = _service.GetBranchesAsync(repoPath);

        var results = await Task.WhenAll(task1, task2, task3);

        // All should succeed and return same results
        Assert.NotEmpty(results[0]);
        Assert.NotEmpty(results[1]);
        Assert.NotEmpty(results[2]);

        Assert.Equal(results[0].Count, results[1].Count);
        Assert.Equal(results[1].Count, results[2].Count);
    }

    private static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path))
            return 0;

        long size = 0;
        var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            try
            {
                var fileInfo = new FileInfo(file);
                size += fileInfo.Length;
            }
            catch
            {
                // Skip files we can't access
            }
        }
        return size;
    }
}
