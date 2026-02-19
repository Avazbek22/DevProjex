namespace DevProjex.Tests.Integration;

/// <summary>
/// Advanced integration tests for Git operations covering edge cases and complex scenarios.
/// Tests repository state management, branch validation, and cache cleanup.
/// </summary>
public class GitAdvancedScenariosTests : IAsyncLifetime
{
    private readonly GitRepositoryService _gitService;
    private readonly RepoCacheService _cacheService;
    private readonly TemporaryDirectory _tempDir;

    private const string TestRepoUrl = "https://github.com/octocat/Hello-World";

    public GitAdvancedScenariosTests()
    {
        _gitService = new GitRepositoryService();
        var testCachePath = Path.Combine(Path.GetTempPath(), "DevProjex", "Tests", "GitAdvanced");
        _cacheService = new RepoCacheService(testCachePath);
        _tempDir = new TemporaryDirectory();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _tempDir.Dispose();
        try
        {
            _cacheService.ClearAllCache();
        }
        catch
        {
            // Best effort cleanup
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CloneRepository_ValidatesCurrentBranchAfterClone()
    {
        if (!await _gitService.IsGitAvailableAsync())
            return;

        // Arrange
        var cacheDir = _cacheService.CreateRepositoryDirectory(TestRepoUrl);

        try
        {
            // Act
            var cloneResult = await _gitService.CloneAsync(TestRepoUrl, cacheDir);
            var currentBranch = await _gitService.GetCurrentBranchAsync(cacheDir);

            // Assert
            Assert.True(cloneResult.Success);
            Assert.NotNull(currentBranch);
            Assert.NotEmpty(currentBranch);
            Assert.Equal("master", currentBranch, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _cacheService.DeleteRepositoryDirectory(cacheDir);
        }
    }

    [Fact]
    public async Task GetBranches_AfterClone_ContainsDefaultBranch()
    {
        if (!await _gitService.IsGitAvailableAsync())
            return;

        // Arrange
        var cacheDir = _cacheService.CreateRepositoryDirectory(TestRepoUrl);

        try
        {
            await _gitService.CloneAsync(TestRepoUrl, cacheDir);

            // Act
            var branches = await _gitService.GetBranchesAsync(cacheDir);

            // Assert
            Assert.NotEmpty(branches);
            Assert.Contains(branches, b => b.Name.Equals("master", StringComparison.OrdinalIgnoreCase));
            Assert.Single(branches.Where(b => b.IsActive));

            var activeBranch = branches.First(b => b.IsActive);
            Assert.Equal("master", activeBranch.Name, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _cacheService.DeleteRepositoryDirectory(cacheDir);
        }
    }

    [Fact]
    public async Task SwitchBranch_UpdatesCurrentBranch()
    {
        if (!await _gitService.IsGitAvailableAsync())
            return;

        // Arrange
        var cacheDir = _cacheService.CreateRepositoryDirectory(TestRepoUrl);

        try
        {
            await _gitService.CloneAsync(TestRepoUrl, cacheDir);
            var branches = await _gitService.GetBranchesAsync(cacheDir);

            // Find a non-active branch
            var targetBranch = branches.FirstOrDefault(b => !b.IsActive);
            if (targetBranch is null)
                return; // Skip if only one branch exists

            // Act
            var switchResult = await _gitService.SwitchBranchAsync(cacheDir, targetBranch.Name);
            var currentBranch = await _gitService.GetCurrentBranchAsync(cacheDir);

            // Assert
            Assert.True(switchResult);
            Assert.Equal(targetBranch.Name, currentBranch, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _cacheService.DeleteRepositoryDirectory(cacheDir);
        }
    }

    [Fact]
    public async Task GetBranches_AfterBranchSwitch_ReflectsNewActiveBranch()
    {
        if (!await _gitService.IsGitAvailableAsync())
            return;

        // Arrange
        var cacheDir = _cacheService.CreateRepositoryDirectory(TestRepoUrl);

        try
        {
            await _gitService.CloneAsync(TestRepoUrl, cacheDir);
            var initialBranches = await _gitService.GetBranchesAsync(cacheDir);

            var targetBranch = initialBranches.FirstOrDefault(b => !b.IsActive);
            if (targetBranch is null)
                return;

            // Act
            await _gitService.SwitchBranchAsync(cacheDir, targetBranch.Name);
            var updatedBranches = await _gitService.GetBranchesAsync(cacheDir);

            // Assert
            Assert.Single(updatedBranches.Where(b => b.IsActive));

            var activeBranch = updatedBranches.First(b => b.IsActive);
            Assert.Equal(targetBranch.Name, activeBranch.Name, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _cacheService.DeleteRepositoryDirectory(cacheDir);
        }
    }

    [Fact]
    public async Task CloneRepository_CreatesGitDirectory()
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
            Assert.True(result.Success);
            Assert.True(Directory.Exists(Path.Combine(cacheDir, ".git")));
        }
        finally
        {
            _cacheService.DeleteRepositoryDirectory(cacheDir);
        }
    }

    [Fact]
    public void CacheService_CreatesUniqueDirectoriesForSameUrl()
    {
        // Arrange & Act
        var dir1 = _cacheService.CreateRepositoryDirectory(TestRepoUrl);
        var dir2 = _cacheService.CreateRepositoryDirectory(TestRepoUrl);
        var dir3 = _cacheService.CreateRepositoryDirectory(TestRepoUrl);

        try
        {
            // Assert
            Assert.NotEqual(dir1, dir2);
            Assert.NotEqual(dir2, dir3);
            Assert.NotEqual(dir1, dir3);

            Assert.True(Directory.Exists(dir1));
            Assert.True(Directory.Exists(dir2));
            Assert.True(Directory.Exists(dir3));
        }
        finally
        {
            _cacheService.DeleteRepositoryDirectory(dir1);
            _cacheService.DeleteRepositoryDirectory(dir2);
            _cacheService.DeleteRepositoryDirectory(dir3);
        }
    }

    [Fact]
    public void CacheService_ExtractsRepositoryNameFromUrl()
    {
        // Act
        var dir = _cacheService.CreateRepositoryDirectory("https://github.com/user/my-awesome-repo.git");

        try
        {
            // Assert
            var dirName = Path.GetFileName(dir);
            Assert.Contains("my-awesome-repo", dirName);
        }
        finally
        {
            _cacheService.DeleteRepositoryDirectory(dir);
        }
    }

    [Fact]
    public void CacheService_CleanupStaleCache_RemovesOldDirectories()
    {
        // Arrange - create old directory
        var oldDir = _cacheService.CreateRepositoryDirectory(TestRepoUrl);
        var oldDirInfo = new DirectoryInfo(oldDir);

        // Set creation time to 25 hours ago (older than 24h threshold)
        oldDirInfo.CreationTimeUtc = DateTime.UtcNow.AddHours(-25);

        // Create recent directory
        var recentDir = _cacheService.CreateRepositoryDirectory(TestRepoUrl);

        try
        {
            // Act
            _cacheService.CleanupStaleCacheOnStartup();

            // Assert
            Assert.False(Directory.Exists(oldDir), "Old directory should be cleaned up");
            Assert.True(Directory.Exists(recentDir), "Recent directory should remain");
        }
        finally
        {
            _cacheService.DeleteRepositoryDirectory(oldDir);
            _cacheService.DeleteRepositoryDirectory(recentDir);
        }
    }

    [Fact]
    public async Task IsGitAvailable_ReturnsConsistentResult()
    {
        // Act
        var result1 = await _gitService.IsGitAvailableAsync();
        var result2 = await _gitService.IsGitAvailableAsync();

        // Assert
        Assert.Equal(result1, result2);
    }

    [Fact]
    public void CacheService_IsInCache_ValidatesCorrectly()
    {
        // Arrange
        var cacheDir = _cacheService.CreateRepositoryDirectory(TestRepoUrl);
        var nonCacheDir = Path.Combine(Path.GetTempPath(), "NotInCache");

        try
        {
            // Act & Assert
            Assert.True(_cacheService.IsInCache(cacheDir));
            Assert.False(_cacheService.IsInCache(nonCacheDir));
            Assert.False(_cacheService.IsInCache(null!));
            Assert.False(_cacheService.IsInCache(string.Empty));
        }
        finally
        {
            _cacheService.DeleteRepositoryDirectory(cacheDir);
        }
    }

    [Fact]
    public async Task Clone_WithInvalidPath_ReturnsFailure()
    {
        if (!await _gitService.IsGitAvailableAsync())
            return;

        // Arrange - invalid path with illegal characters
        var invalidPath = Path.Combine(_tempDir.Path, "invalid<>path");

        // Act
        var result = await _gitService.CloneAsync(TestRepoUrl, invalidPath);

        // Assert
        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task GetBranches_OnNonGitDirectory_ReturnsEmpty()
    {
        // Arrange - regular directory without .git
        var nonGitDir = _tempDir.CreateDirectory("not-a-git-repo");

        // Act
        var branches = await _gitService.GetBranchesAsync(nonGitDir);

        // Assert
        Assert.Empty(branches);
    }

    [Fact]
    public async Task GetCurrentBranch_OnNonGitDirectory_ReturnsNull()
    {
        // Arrange
        var nonGitDir = _tempDir.CreateDirectory("not-a-git-repo");

        // Act
        var branch = await _gitService.GetCurrentBranchAsync(nonGitDir);

        // Assert
        Assert.Null(branch);
    }
}
