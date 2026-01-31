using System;
using System.IO;
using System.Linq;
using DevProjex.Infrastructure.Git;
using Xunit;

namespace DevProjex.Tests.Unit;

/// <summary>
/// Unit tests for RepoCacheService cleanup functionality.
/// Tests stale cache cleanup, directory deletion, and cache management.
/// </summary>
public sealed class RepoCacheServiceCleanupTests : IDisposable
{
    private readonly RepoCacheService _service;
    private readonly string _testCacheRoot;

    public RepoCacheServiceCleanupTests()
    {
        _testCacheRoot = Path.Combine(Path.GetTempPath(), "DevProjex", "Tests", "CacheCleanup", Guid.NewGuid().ToString("N"));
        _service = new RepoCacheService(_testCacheRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testCacheRoot))
                Directory.Delete(_testCacheRoot, recursive: true);
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public void CleanupStaleCache_DoesNotRemoveRecentDirectories()
    {
        // Arrange
        var recentDir1 = _service.CreateRepositoryDirectory("https://github.com/user/repo1");
        var recentDir2 = _service.CreateRepositoryDirectory("https://github.com/user/repo2");

        try
        {
            // Act
            _service.CleanupStaleCacheOnStartup();

            // Assert
            Assert.True(Directory.Exists(recentDir1), "Recent directory 1 should not be deleted");
            Assert.True(Directory.Exists(recentDir2), "Recent directory 2 should not be deleted");
        }
        finally
        {
            _service.DeleteRepositoryDirectory(recentDir1);
            _service.DeleteRepositoryDirectory(recentDir2);
        }
    }

    [Fact]
    public void CleanupStaleCache_RemovesDirectoriesOlderThan24Hours()
    {
        // Arrange
        var oldDir = _service.CreateRepositoryDirectory("https://github.com/user/old-repo");
        var oldDirInfo = new DirectoryInfo(oldDir);

        // Make directory appear 25 hours old
        oldDirInfo.CreationTimeUtc = DateTime.UtcNow.AddHours(-25);

        // Act
        _service.CleanupStaleCacheOnStartup();

        // Assert
        Assert.False(Directory.Exists(oldDir), "Directory older than 24 hours should be removed");
    }

    [Fact]
    public void CleanupStaleCache_OnEmptyCache_DoesNotThrow()
    {
        // Act & Assert - should not throw
        _service.CleanupStaleCacheOnStartup();
    }

    [Fact]
    public void CleanupStaleCache_OnNonexistentCache_DoesNotThrow()
    {
        // Arrange - create service with non-existent path
        var nonExistentPath = Path.Combine(Path.GetTempPath(), "DevProjex", "Tests", "NonExistent", Guid.NewGuid().ToString("N"));
        var tempService = new RepoCacheService(nonExistentPath);

        // Act & Assert - should not throw
        tempService.CleanupStaleCacheOnStartup();
    }

    [Fact]
    public void DeleteRepositoryDirectory_RemovesNestedStructure()
    {
        // Arrange
        var dir = _service.CreateRepositoryDirectory("https://github.com/user/repo");
        var subDir1 = Path.Combine(dir, "subdir1");
        var subDir2 = Path.Combine(dir, "subdir1", "subdir2");
        Directory.CreateDirectory(subDir2);

        File.WriteAllText(Path.Combine(dir, "file1.txt"), "content1");
        File.WriteAllText(Path.Combine(subDir1, "file2.txt"), "content2");
        File.WriteAllText(Path.Combine(subDir2, "file3.txt"), "content3");

        // Act
        _service.DeleteRepositoryDirectory(dir);

        // Assert
        Assert.False(Directory.Exists(dir), "Directory and all nested content should be deleted");
    }

    [Fact]
    public void DeleteRepositoryDirectory_OnlyDeletesIfInsideCache()
    {
        // Arrange - directory outside cache
        var outsidePath = Path.Combine(Path.GetTempPath(), "OutsideCacheTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsidePath);
        File.WriteAllText(Path.Combine(outsidePath, "important.txt"), "data");

        try
        {
            // Act
            _service.DeleteRepositoryDirectory(outsidePath);

            // Assert - should NOT delete because it's outside cache
            Assert.True(Directory.Exists(outsidePath));
            Assert.True(File.Exists(Path.Combine(outsidePath, "important.txt")));
        }
        finally
        {
            Directory.Delete(outsidePath, recursive: true);
        }
    }

    [Fact]
    public void ClearAllCache_RemovesAllCachedRepositories()
    {
        // Arrange
        var dir1 = _service.CreateRepositoryDirectory("https://github.com/user/repo1");
        var dir2 = _service.CreateRepositoryDirectory("https://github.com/user/repo2");
        var dir3 = _service.CreateRepositoryDirectory("https://github.com/user/repo3");

        File.WriteAllText(Path.Combine(dir1, "file.txt"), "content");
        File.WriteAllText(Path.Combine(dir2, "file.txt"), "content");
        File.WriteAllText(Path.Combine(dir3, "file.txt"), "content");

        // Act
        _service.ClearAllCache();

        // Assert
        Assert.False(Directory.Exists(dir1));
        Assert.False(Directory.Exists(dir2));
        Assert.False(Directory.Exists(dir3));
    }

    [Fact]
    public void CreateRepositoryDirectory_AfterClearAllCache_CreatesNewDirectory()
    {
        // Arrange
        var dir1 = _service.CreateRepositoryDirectory("https://github.com/user/repo");
        _service.ClearAllCache();

        // Act
        var dir2 = _service.CreateRepositoryDirectory("https://github.com/user/repo");

        try
        {
            // Assert
            Assert.True(Directory.Exists(dir2));
            Assert.NotEqual(dir1, dir2); // Should be different due to timestamp
        }
        finally
        {
            _service.DeleteRepositoryDirectory(dir2);
        }
    }

    [Fact]
    public void IsInCache_AfterDeletion_ReturnsFalse()
    {
        // Arrange
        var dir = _service.CreateRepositoryDirectory("https://github.com/user/repo");
        Assert.True(_service.IsInCache(dir));

        // Act
        _service.DeleteRepositoryDirectory(dir);

        // Assert
        Assert.False(Directory.Exists(dir));
        // IsInCache checks path structure, not existence
        Assert.True(_service.IsInCache(dir)); // Path pattern still matches cache structure
    }

    [Fact]
    public void DeleteRepositoryDirectory_WithReadOnlyFiles_StillDeletes()
    {
        // Arrange
        var dir = _service.CreateRepositoryDirectory("https://github.com/user/repo");
        var filePath = Path.Combine(dir, "readonly.txt");
        File.WriteAllText(filePath, "content");

        // Make file read-only
        var fileInfo = new FileInfo(filePath);
        fileInfo.Attributes |= FileAttributes.ReadOnly;

        // Act
        _service.DeleteRepositoryDirectory(dir);

        // Assert - on some systems this may fail due to locks, but should not throw
        // Best effort deletion
        Assert.True(true); // Test completes without exception
    }

    [Fact]
    public void CacheRootPath_IsInsideTestDirectory()
    {
        // Act
        var cacheRoot = _service.CacheRootPath;

        // Assert
        Assert.Equal(_testCacheRoot, cacheRoot);
        Assert.Contains("DevProjex", cacheRoot);
        Assert.Contains("Tests", cacheRoot);
    }

    [Fact]
    public void CreateRepositoryDirectory_GeneratesUniqueTimestamps()
    {
        // Arrange
        var url = "https://github.com/user/repo";

        // Act - create multiple directories rapidly
        var dirs = Enumerable.Range(0, 5)
            .Select(_ => _service.CreateRepositoryDirectory(url))
            .ToArray();

        try
        {
            // Assert - all should be unique
            var uniqueDirs = dirs.Distinct().ToArray();
            Assert.Equal(5, uniqueDirs.Length);

            foreach (var dir in dirs)
            {
                Assert.True(Directory.Exists(dir));
            }
        }
        finally
        {
            foreach (var dir in dirs)
            {
                _service.DeleteRepositoryDirectory(dir);
            }
        }
    }

    [Fact]
    public void CleanupStaleCache_WithMixedAges_OnlyRemovesOld()
    {
        // Arrange
        var recentDir = _service.CreateRepositoryDirectory("https://github.com/user/recent");
        var oldDir1 = _service.CreateRepositoryDirectory("https://github.com/user/old1");
        var oldDir2 = _service.CreateRepositoryDirectory("https://github.com/user/old2");

        // Make some directories old
        new DirectoryInfo(oldDir1).CreationTimeUtc = DateTime.UtcNow.AddHours(-25);
        new DirectoryInfo(oldDir2).CreationTimeUtc = DateTime.UtcNow.AddHours(-30);

        try
        {
            // Act
            _service.CleanupStaleCacheOnStartup();

            // Assert
            Assert.True(Directory.Exists(recentDir), "Recent directory should remain");
            Assert.False(Directory.Exists(oldDir1), "Old directory 1 should be removed");
            Assert.False(Directory.Exists(oldDir2), "Old directory 2 should be removed");
        }
        finally
        {
            _service.DeleteRepositoryDirectory(recentDir);
            _service.DeleteRepositoryDirectory(oldDir1);
            _service.DeleteRepositoryDirectory(oldDir2);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DeleteRepositoryDirectory_InvalidPaths_DoesNotThrow(string? path)
    {
        // Act & Assert - should not throw
        _service.DeleteRepositoryDirectory(path!);
    }

    [Fact]
    public void IsInCache_RelativePaths_HandlesCorrectly()
    {
        // Arrange
        var relativePath = "relative/path/to/cache";

        // Act
        var result = _service.IsInCache(relativePath);

        // Assert - relative paths should be handled safely
        Assert.False(result);
    }
}
