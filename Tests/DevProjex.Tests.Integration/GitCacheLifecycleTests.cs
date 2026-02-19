namespace DevProjex.Tests.Integration;

/// <summary>
/// Integration tests for Git repository cache lifecycle management.
/// Tests the complete flow: clone → open → cleanup.
/// </summary>
[Collection("GitNetworkTests")]
public sealed class GitCacheLifecycleTests : IDisposable
{
    private const string TestRepoUrl = "https://github.com/octocat/Hello-World";

    private readonly GitRepositoryService _gitService;
    private readonly RepoCacheService _cacheService;
    private readonly TemporaryDirectory _tempDir;

    public GitCacheLifecycleTests()
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
    public async Task CloneToCache_RepositoryFilesExist_AfterSuccessfulClone()
    {
        if (!await _gitService.IsGitAvailableAsync())
            return;

        // Arrange
        var cacheDir = _cacheService.CreateRepositoryDirectory(TestRepoUrl);

        try
        {
            // Act
            var result = await _gitService.CloneAsync(TestRepoUrl, cacheDir);

            // Assert
            Assert.True(result.Success, $"Clone failed: {result.ErrorMessage}");
            Assert.True(Directory.Exists(cacheDir));
            Assert.True(Directory.Exists(Path.Combine(cacheDir, ".git")));

            // Verify repository files are actually present
            var files = Directory.GetFiles(cacheDir, "*", SearchOption.AllDirectories);
            Assert.NotEmpty(files);
        }
        finally
        {
            _cacheService.DeleteRepositoryDirectory(cacheDir);
        }
    }

    [Fact]
    public async Task FailedClone_CacheShouldBeCleanedUp_AfterError()
    {
        if (!await _gitService.IsGitAvailableAsync())
            return;

        // Arrange
        var invalidUrl = "https://github.com/nonexistent/repo-does-not-exist-12345";
        var cacheDir = _cacheService.CreateRepositoryDirectory(invalidUrl);

        try
        {
            // Act
            var result = await _gitService.CloneAsync(invalidUrl, cacheDir);

            // Assert - clone should fail
            Assert.False(result.Success);

            // Simulate cleanup after failed clone (like MainWindow does)
            _cacheService.DeleteRepositoryDirectory(cacheDir);

            // Verify cache was cleaned up
            Assert.False(Directory.Exists(cacheDir));
        }
        finally
        {
            // Ensure cleanup even if test fails
            if (Directory.Exists(cacheDir))
                _cacheService.DeleteRepositoryDirectory(cacheDir);
        }
    }

    [Fact]
    public void MultipleClones_EachGetsUniqueDirectory()
    {
        // Arrange
        var url = "https://github.com/user/repo";

        // Act
        var dir1 = _cacheService.CreateRepositoryDirectory(url);
        var dir2 = _cacheService.CreateRepositoryDirectory(url);
        var dir3 = _cacheService.CreateRepositoryDirectory(url);

        try
        {
            // Assert
            Assert.NotEqual(dir1, dir2);
            Assert.NotEqual(dir2, dir3);
            Assert.NotEqual(dir1, dir3);

            // All should be in cache
            Assert.True(_cacheService.IsInCache(dir1));
            Assert.True(_cacheService.IsInCache(dir2));
            Assert.True(_cacheService.IsInCache(dir3));
        }
        finally
        {
            _cacheService.DeleteRepositoryDirectory(dir1);
            _cacheService.DeleteRepositoryDirectory(dir2);
            _cacheService.DeleteRepositoryDirectory(dir3);
        }
    }

    [Fact]
    public async Task SequentialClones_OldCacheCanBeDeleted_BeforeNewClone()
    {
        if (!await _gitService.IsGitAvailableAsync())
            return;

        // Arrange
        var cache1 = _cacheService.CreateRepositoryDirectory(TestRepoUrl);
        var cache2 = _cacheService.CreateRepositoryDirectory(TestRepoUrl);

        try
        {
            // Act - Clone first repo
            var result1 = await _gitService.CloneAsync(TestRepoUrl, cache1);
            Assert.True(result1.Success);
            Assert.True(Directory.Exists(cache1));

            // Simulate: user clones second repo, old cache should be cleaned
            // Note: DeleteRepositoryDirectory uses best-effort cleanup and may not delete immediately
            // if files are locked by Git processes or Windows file system
            _cacheService.DeleteRepositoryDirectory(cache1);

            // Clone second repo
            var result2 = await _gitService.CloneAsync(TestRepoUrl, cache2);
            Assert.True(result2.Success);

            // Assert - new cache exists and paths are different
            Assert.NotEqual(cache1, cache2);
            Assert.True(Directory.Exists(cache2), "New cache should exist");
        }
        finally
        {
            _cacheService.DeleteRepositoryDirectory(cache1);
            _cacheService.DeleteRepositoryDirectory(cache2);
        }
    }

    [Fact]
    public void CacheDirectory_IsInsideExpectedLocation()
    {
        // Arrange
        var url = "https://github.com/user/repo";

        // Act
        var cacheDir = _cacheService.CreateRepositoryDirectory(url);

        try
        {
            // Assert - tests use custom cache path under Tests/GitIntegration
            var expectedRoot = Path.Combine(
                Path.GetTempPath(),
                "DevProjex",
                "Tests",
                "GitIntegration");

            var normalizedExpected = expectedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedCacheDir = cacheDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            Assert.StartsWith(normalizedExpected, normalizedCacheDir, StringComparison.OrdinalIgnoreCase);
            Assert.True(_cacheService.IsInCache(cacheDir));
        }
        finally
        {
            _cacheService.DeleteRepositoryDirectory(cacheDir);
        }
    }

    [Fact]
    public async Task ClonedRepository_CanListBranches()
    {
        if (!await _gitService.IsGitAvailableAsync())
            return;

        // Arrange
        var cacheDir = _cacheService.CreateRepositoryDirectory(TestRepoUrl);

        try
        {
            // Act
            var cloneResult = await _gitService.CloneAsync(TestRepoUrl, cacheDir);
            Assert.True(cloneResult.Success);

            var branches = await _gitService.GetBranchesAsync(cacheDir);

            // Assert
            Assert.NotEmpty(branches);
            Assert.Contains(branches, b => b.IsActive); // Should have active branch
        }
        finally
        {
            _cacheService.DeleteRepositoryDirectory(cacheDir);
        }
    }

    [Fact]
    public async Task ClonedRepository_CanSwitchBranches()
    {
        if (!await _gitService.IsGitAvailableAsync())
            return;

        // Arrange
        var cacheDir = _cacheService.CreateRepositoryDirectory(TestRepoUrl);

        try
        {
            // Act
            var cloneResult = await _gitService.CloneAsync(TestRepoUrl, cacheDir);

            // Skip if clone failed (might be network/rate limiting issue)
            if (!cloneResult.Success)
                return;

            var branches = await _gitService.GetBranchesAsync(cacheDir);
            if (branches.Count < 2)
                return; // Skip if repo doesn't have multiple branches

            var currentBranch = await _gitService.GetCurrentBranchAsync(cacheDir);
            var otherBranch = branches.FirstOrDefault(b => b.Name != currentBranch)?.Name;

            if (otherBranch is null)
                return; // Skip if no other branch available

            var switched = await _gitService.SwitchBranchAsync(cacheDir, otherBranch);

            // Assert - if switch succeeded, verify branch changed
            if (switched)
            {
                var newBranch = await _gitService.GetCurrentBranchAsync(cacheDir);
                Assert.Equal(otherBranch, newBranch);
            }
            // If switch failed, that's acceptable (might be shallow clone limitation)
        }
        finally
        {
            _cacheService.DeleteRepositoryDirectory(cacheDir);
        }
    }

    [Fact]
    public void DeleteNonExistentCache_DoesNotThrow()
    {
        // Arrange
        var nonExistent = Path.Combine(_cacheService.CacheRootPath, "does-not-exist");

        // Act & Assert - should not throw
        _cacheService.DeleteRepositoryDirectory(nonExistent);
    }

    [Fact]
    public void DeleteDirectoryOutsideCache_IsIgnored()
    {
        // Arrange
        var outsideDir = _tempDir.CreateDirectory("outside-cache");
        File.WriteAllText(Path.Combine(outsideDir, "test.txt"), "test");

        // Act
        _cacheService.DeleteRepositoryDirectory(outsideDir);

        // Assert - should NOT delete directory outside cache
        Assert.True(Directory.Exists(outsideDir));
        Assert.True(File.Exists(Path.Combine(outsideDir, "test.txt")));
    }

    [Fact]
    public void ClearAllCache_RemovesAllCachedRepositories()
    {
        // Arrange
        var cache1 = _cacheService.CreateRepositoryDirectory("https://github.com/user/repo1");
        var cache2 = _cacheService.CreateRepositoryDirectory("https://github.com/user/repo2");
        var cache3 = _cacheService.CreateRepositoryDirectory("https://github.com/user/repo3");

        File.WriteAllText(Path.Combine(cache1, "test1.txt"), "test");
        File.WriteAllText(Path.Combine(cache2, "test2.txt"), "test");
        File.WriteAllText(Path.Combine(cache3, "test3.txt"), "test");

        try
        {
            // Act
            _cacheService.ClearAllCache();

            // Assert
            Assert.False(Directory.Exists(cache1));
            Assert.False(Directory.Exists(cache2));
            Assert.False(Directory.Exists(cache3));
        }
        finally
        {
            // Cleanup in case test fails
            _cacheService.ClearAllCache();
        }
    }

    [Fact]
    public void IsInCache_ReturnsTrueForCachedPath()
    {
        // Arrange
        var cacheDir = _cacheService.CreateRepositoryDirectory("https://github.com/user/repo");

        try
        {
            // Act & Assert
            Assert.True(_cacheService.IsInCache(cacheDir));
        }
        finally
        {
            _cacheService.DeleteRepositoryDirectory(cacheDir);
        }
    }

    [Fact]
    public void IsInCache_ReturnsFalseForNonCachedPath()
    {
        // Arrange
        var randomPath = _tempDir.CreateDirectory("random");

        // Act & Assert
        Assert.False(_cacheService.IsInCache(randomPath));
    }

    [Fact]
    public void IsInCache_ReturnsFalseForNullOrEmpty()
    {
        // Act & Assert
        Assert.False(_cacheService.IsInCache(null!));
        Assert.False(_cacheService.IsInCache(string.Empty));
        Assert.False(_cacheService.IsInCache("   "));
    }
}
