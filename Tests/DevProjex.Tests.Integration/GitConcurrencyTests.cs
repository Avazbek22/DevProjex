using DevProjex.Infrastructure.Git;
using DevProjex.Tests.Integration.Helpers;

namespace DevProjex.Tests.Integration;

/// <summary>
/// Integration tests for concurrent Git operations.
/// Tests thread safety, parallel execution, and resource locking.
/// </summary>
public class GitConcurrencyTests : IAsyncLifetime
{
    private readonly GitRepositoryService _service;
    private readonly RepoCacheService _cacheService;
    private readonly TemporaryDirectory _tempDir;

    private const string TestRepoUrl = "https://github.com/octocat/Hello-World";

    public GitConcurrencyTests()
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
    public async Task ParallelClones_ToDifferentDirectories_AllSucceed()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var dir1 = _tempDir.CreateDirectory("parallel-clone-1");
        var dir2 = _tempDir.CreateDirectory("parallel-clone-2");
        var dir3 = _tempDir.CreateDirectory("parallel-clone-3");

        var task1 = _service.CloneAsync(TestRepoUrl, dir1);
        var task2 = _service.CloneAsync(TestRepoUrl, dir2);
        var task3 = _service.CloneAsync(TestRepoUrl, dir3);

        var results = await Task.WhenAll(task1, task2, task3);

        Assert.All(results, r => Assert.True(r.Success, r.ErrorMessage ?? "Unknown error"));
    }

    [Fact]
    public async Task ParallelGetBranches_SameRepository_AllSucceed()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("parallel-branches");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        // Start 5 parallel GetBranches operations
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => _service.GetBranchesAsync(repoPath))
            .ToList();

        var results = await Task.WhenAll(tasks);

        // All should succeed and return same data
        Assert.All(results, r => Assert.NotEmpty(r));
        Assert.Equal(results[0].Count, results[1].Count);
        Assert.Equal(results[1].Count, results[2].Count);
    }

    [Fact]
    public async Task ParallelPull_SameRepository_DoesNotCorruptState()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("parallel-pull");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        // Start 3 parallel pull operations
        var task1 = _service.PullUpdatesAsync(repoPath);
        var task2 = _service.PullUpdatesAsync(repoPath);
        var task3 = _service.PullUpdatesAsync(repoPath);

        var results = await Task.WhenAll(task1, task2, task3);

        // At least one should succeed (others might fail due to lock)
        Assert.Contains(results, r => r);

        // Repository should still be valid
        var currentBranch = await _service.GetCurrentBranchAsync(repoPath);
        Assert.NotNull(currentBranch);
    }

    [Fact]
    public async Task ConcurrentBranchSwitch_SameRepository_EventuallySucceeds()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("concurrent-switch");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        var branches = await _service.GetBranchesAsync(repoPath);
        if (branches.Count < 2)
            return;

        var branch1 = branches[0].Name;
        var branch2 = branches[1].Name;

        // Try switching to different branches concurrently
        var task1 = _service.SwitchBranchAsync(repoPath, branch1);
        var task2 = _service.SwitchBranchAsync(repoPath, branch2);

        var results = await Task.WhenAll(task1, task2);

        // At least one should succeed
        Assert.Contains(results, r => r);

        // Repository should be in valid state
        var currentBranch = await _service.GetCurrentBranchAsync(repoPath);
        Assert.NotNull(currentBranch);
        Assert.True(currentBranch == branch1 || currentBranch == branch2);
    }

    [Fact]
    public async Task ParallelIsGitAvailable_ReturnsConsistentResults()
    {
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => _service.IsGitAvailableAsync())
            .ToList();

        var results = await Task.WhenAll(tasks);

        // All should return same result
        var firstResult = results[0];
        Assert.All(results, r => Assert.Equal(firstResult, r));
    }

    [Fact]
    public async Task MixedOperations_SameRepository_MaintainConsistency()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("mixed-ops");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        // Mix different operations in parallel
        var getBranchesTask = _service.GetBranchesAsync(repoPath);
        var getCurrentBranchTask = _service.GetCurrentBranchAsync(repoPath);
        var pullTask = _service.PullUpdatesAsync(repoPath);

        await Task.WhenAll(
            getBranchesTask.ContinueWith(_ => { }),
            getCurrentBranchTask.ContinueWith(_ => { }),
            pullTask.ContinueWith(_ => { })
        );

        // Repository should still be accessible
        var finalBranches = await _service.GetBranchesAsync(repoPath);
        Assert.NotEmpty(finalBranches);
    }

    [Fact]
    public async Task ParallelCacheOperations_DifferentUrls_AllSucceed()
    {
        var urls = new[]
        {
            "https://github.com/user1/repo1",
            "https://github.com/user2/repo2",
            "https://github.com/user3/repo3",
            "https://github.com/user4/repo4"
        };

        var tasks = urls.Select(url => Task.Run(() => _cacheService.CreateRepositoryDirectory(url))).ToList();

        var results = await Task.WhenAll(tasks);

        // All should create unique directories
        Assert.Equal(urls.Length, results.Distinct().Count());
        Assert.All(results, dir => Assert.True(Directory.Exists(dir)));

        // Cleanup
        foreach (var dir in results)
        {
            try { _cacheService.DeleteRepositoryDirectory(dir); } catch { }
        }
    }

    [Fact]
    public async Task RapidSequentialBranchSwitches_DoesNotCauseErrors()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("rapid-switch");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        var branches = await _service.GetBranchesAsync(repoPath);
        if (branches.Count < 2)
            return;

        var branch1 = branches[0].Name;
        var branch2 = branches[1].Name;

        // Rapidly switch back and forth 10 times
        for (int i = 0; i < 10; i++)
        {
            var targetBranch = i % 2 == 0 ? branch1 : branch2;
            var success = await _service.SwitchBranchAsync(repoPath, targetBranch);
            Assert.True(success, $"Switch {i} to {targetBranch} failed");
        }

        // Final state should be consistent
        var currentBranch = await _service.GetCurrentBranchAsync(repoPath);
        Assert.NotNull(currentBranch);
    }

    [Fact]
    public async Task ConcurrentCacheDeletion_SameDirectory_HandlesGracefully()
    {
        var url = "https://github.com/user/repo";
        var dir = _cacheService.CreateRepositoryDirectory(url);
        File.WriteAllText(Path.Combine(dir, "test.txt"), "test");

        // Try deleting same directory from multiple threads
        var task1 = Task.Run(() => _cacheService.DeleteRepositoryDirectory(dir));
        var task2 = Task.Run(() => _cacheService.DeleteRepositoryDirectory(dir));
        var task3 = Task.Run(() => _cacheService.DeleteRepositoryDirectory(dir));

        // Should not throw
        await Task.WhenAll(task1, task2, task3);

        Assert.True(true); // No exception thrown
    }

    [Fact]
    public async Task ParallelGetCurrentBranch_ReturnsConsistentResults()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("parallel-current");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => _service.GetCurrentBranchAsync(repoPath))
            .ToList();

        var results = await Task.WhenAll(tasks);

        // All should return same branch name
        var firstBranch = results[0];
        Assert.All(results, r => Assert.Equal(firstBranch, r));
    }

    [Fact]
    public async Task StressTest_ManyOperations_MaintainsStability()
    {
        if (!await _service.IsGitAvailableAsync())
            return;

        var repoPath = _tempDir.CreateDirectory("stress-test");
        var cloneResult = await _service.CloneAsync(TestRepoUrl, repoPath);
        Assert.True(cloneResult.Success);

        var tasks = new List<Task>();

        // Create 20 concurrent operations
        for (int i = 0; i < 20; i++)
        {
            if (i % 3 == 0)
                tasks.Add(_service.GetBranchesAsync(repoPath).ContinueWith(_ => { }));
            else if (i % 3 == 1)
                tasks.Add(_service.GetCurrentBranchAsync(repoPath).ContinueWith(_ => { }));
            else
                tasks.Add(_service.PullUpdatesAsync(repoPath).ContinueWith(_ => { }));
        }

        // Wait for all to complete
        await Task.WhenAll(tasks);

        // Repository should still be functional
        var finalBranches = await _service.GetBranchesAsync(repoPath);
        Assert.NotEmpty(finalBranches);

        var finalCurrent = await _service.GetCurrentBranchAsync(repoPath);
        Assert.NotNull(finalCurrent);
    }
}
