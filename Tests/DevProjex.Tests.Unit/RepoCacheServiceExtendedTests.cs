namespace DevProjex.Tests.Unit;

/// <summary>
/// Extended unit tests for RepoCacheService covering edge cases and security.
/// </summary>
public sealed class RepoCacheServiceExtendedTests : IDisposable
{
    private readonly RepoCacheService _service;
    private readonly string _testCacheRoot;

    public RepoCacheServiceExtendedTests()
    {
        // Use temp directory for testing to avoid polluting real cache
        _testCacheRoot = Path.Combine(Path.GetTempPath(), "DevProjex", "Tests", "ExtendedCacheTests", Guid.NewGuid().ToString("N"));
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

    [Theory]
    [InlineData("https://github.com/user/repo")]
    [InlineData("https://github.com/user/repo.git")]
    [InlineData("https://github.com/ORG/PROJECT")]
    [InlineData("https://gitlab.com/user/my-project")]
    public void CreateRepositoryDirectory_CreatesValidPath(string url)
    {
        // Act
        var path = _service.CreateRepositoryDirectory(url);

        try
        {
            // Assert
            Assert.NotNull(path);
            Assert.NotEmpty(path);
            Assert.True(Directory.Exists(path));
            Assert.Contains("Tests", path);
        }
        finally
        {
            _service.DeleteRepositoryDirectory(path);
        }
    }

    [Fact]
    public void CreateRepositoryDirectory_PathContainsRepoName()
    {
        // Arrange
        var url = "https://github.com/user/my-repo";

        // Act
        var path = _service.CreateRepositoryDirectory(url);

        try
        {
            // Assert - path should contain repo name and timestamp
            var dirName = Path.GetFileName(path);
            Assert.Contains("_", dirName); // Format: repoName_timestamp
            Assert.Contains("my-repo", dirName); // Repo name present
            Assert.Matches(@"^.+_[A-F0-9]+$", dirName); // Name + hex timestamp
        }
        finally
        {
            _service.DeleteRepositoryDirectory(path);
        }
    }

    [Fact]
    public void CreateRepositoryDirectory_SameUrl_DifferentPaths_DueToTimestamp()
    {
        // Arrange
        var url = "https://github.com/user/repo";

        // Act
        var path1 = _service.CreateRepositoryDirectory(url);
        Thread.Sleep(10); // Ensure different timestamp
        var path2 = _service.CreateRepositoryDirectory(url);

        try
        {
            // Assert
            Assert.NotEqual(path1, path2);

            var dir1 = Path.GetFileName(path1);
            var dir2 = Path.GetFileName(path2);

            // Repo name part should be same, timestamp different
            var repoName1 = dir1.Split('_')[0];
            var repoName2 = dir2.Split('_')[0];
            Assert.Equal(repoName1, repoName2);
            Assert.Equal("repo", repoName1); // Extracted from URL

            var timestamp1 = dir1.Split('_')[1];
            var timestamp2 = dir2.Split('_')[1];
            Assert.NotEqual(timestamp1, timestamp2);
        }
        finally
        {
            _service.DeleteRepositoryDirectory(path1);
            _service.DeleteRepositoryDirectory(path2);
        }
    }

    [Fact]
    public void DeleteRepositoryDirectory_WithNestedFiles_DeletesAll()
    {
        // Arrange
        var path = _service.CreateRepositoryDirectory("https://github.com/user/repo");
        var subDir = Path.Combine(path, "subdir");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(path, "file1.txt"), "content");
        File.WriteAllText(Path.Combine(subDir, "file2.txt"), "content");

        // Act
        _service.DeleteRepositoryDirectory(path);

        // Assert
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void DeleteRepositoryDirectory_NullPath_DoesNotThrow()
    {
        // Act & Assert - should not throw
        _service.DeleteRepositoryDirectory(null!);
    }

    [Fact]
    public void DeleteRepositoryDirectory_EmptyPath_DoesNotThrow()
    {
        // Act & Assert - should not throw
        _service.DeleteRepositoryDirectory(string.Empty);
        _service.DeleteRepositoryDirectory("   ");
    }

    [Fact]
    public void DeleteRepositoryDirectory_NonExistentPath_DoesNotThrow()
    {
        // Arrange
        var nonExistent = Path.Combine(_service.CacheRootPath, "does-not-exist-12345");

        // Act & Assert - should not throw
        _service.DeleteRepositoryDirectory(nonExistent);
    }

    [Fact]
    public void DeleteRepositoryDirectory_PathOutsideCache_IgnoresDelete()
    {
        // Arrange
        var outsidePath = Path.Combine(Path.GetTempPath(), "outside-cache-test");
        Directory.CreateDirectory(outsidePath);
        File.WriteAllText(Path.Combine(outsidePath, "test.txt"), "test");

        try
        {
            // Act
            _service.DeleteRepositoryDirectory(outsidePath);

            // Assert - should NOT delete
            Assert.True(Directory.Exists(outsidePath));
            Assert.True(File.Exists(Path.Combine(outsidePath, "test.txt")));
        }
        finally
        {
            Directory.Delete(outsidePath, recursive: true);
        }
    }

    [Fact]
    public void IsInCache_CorrectlyIdentifiesCachePath()
    {
        // Arrange
        var cachePath = _service.CreateRepositoryDirectory("https://github.com/user/repo");

        try
        {
            // Act & Assert
            Assert.True(_service.IsInCache(cachePath));
        }
        finally
        {
            _service.DeleteRepositoryDirectory(cachePath);
        }
    }

    [Fact]
    public void IsInCache_ReturnsFalseForPathOutsideCache()
    {
        // Arrange
        var outsidePath = Path.GetTempPath();

        // Act & Assert
        Assert.False(_service.IsInCache(outsidePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsInCache_HandlesInvalidInput(string? path)
    {
        // Act & Assert
        Assert.False(_service.IsInCache(path!));
    }

    [Fact]
    public void IsInCache_CaseInsensitiveOnWindows()
    {
        // Arrange
        var cachePath = _service.CreateRepositoryDirectory("https://github.com/user/repo");

        try
        {
            // Act
            var upperPath = cachePath.ToUpperInvariant();
            var lowerPath = cachePath.ToLowerInvariant();

            // Assert - should work regardless of case (Windows behavior)
            Assert.True(_service.IsInCache(upperPath));
            Assert.True(_service.IsInCache(lowerPath));
        }
        finally
        {
            _service.DeleteRepositoryDirectory(cachePath);
        }
    }

    [Fact]
    public void IsInCache_HandlesMalformedPaths()
    {
        // Arrange
        var malformedPaths = new[]
        {
            "C:\\invalid\\\\double\\\\slash",
            "C:/mixed/slashes\\here",
            "relative/path/not/absolute"
        };

        // Act & Assert - should not throw
        foreach (var path in malformedPaths)
        {
            var result = _service.IsInCache(path);
            Assert.False(result); // Malformed paths should not be considered in cache
        }
    }

    [Fact]
    public void CacheRootPath_IsInTempDirectory()
    {
        // Act
        var cacheRoot = _service.CacheRootPath;

        // Assert
        var tempPath = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedCacheRoot = cacheRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        Assert.StartsWith(tempPath, normalizedCacheRoot, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DevProjex", cacheRoot);
        Assert.Contains("Tests", cacheRoot);
    }

    [Fact]
    public void ClearAllCache_RemovesRootDirectory()
    {
        // Arrange
        var cache1 = _service.CreateRepositoryDirectory("https://github.com/user/repo1");
        var cache2 = _service.CreateRepositoryDirectory("https://github.com/user/repo2");
        File.WriteAllText(Path.Combine(cache1, "file.txt"), "test");
        File.WriteAllText(Path.Combine(cache2, "file.txt"), "test");

        try
        {
            // Act
            _service.ClearAllCache();

            // Assert
            Assert.False(Directory.Exists(cache1));
            Assert.False(Directory.Exists(cache2));
            // Root might be recreated or deleted, both are acceptable
        }
        finally
        {
            // Ensure cleanup
            try { _service.ClearAllCache(); } catch { }
        }
    }

    [Fact]
    public void ClearAllCache_WhenCacheDoesNotExist_DoesNotThrow()
    {
        // Arrange - ensure cache doesn't exist
        _service.ClearAllCache();

        // Act & Assert - should not throw even if called again
        _service.ClearAllCache();
    }

    [Fact]
    public void CreateMultipleDirectories_AllAreUnique()
    {
        // Arrange
        var url = "https://github.com/user/repo";
        var paths = new string[10];

        try
        {
            // Act - create 10 directories rapidly
            for (int i = 0; i < 10; i++)
            {
                paths[i] = _service.CreateRepositoryDirectory(url);
                Thread.Sleep(1); // Minimal delay to ensure unique timestamps
            }

            // Assert - all paths should be unique
            var uniquePaths = paths.Distinct().ToArray();
            Assert.Equal(10, uniquePaths.Length);
            Assert.All(paths, p => Assert.True(_service.IsInCache(p)));
        }
        finally
        {
            foreach (var path in paths)
            {
                if (!string.IsNullOrEmpty(path))
                    _service.DeleteRepositoryDirectory(path);
            }
        }
    }

    [Theory]
    [InlineData("https://github.com/user/repo", "https://github.com/user/repo", "repo")]
    [InlineData("https://github.com/user/repo.git", "https://github.com/user/repo.git", "repo")]
    [InlineData("https://github.com/user/my-project", "https://github.com/user/my-project.git", "my-project")]
    public void SameUrl_ProducesSameRepoNamePrefix(string url1, string url2, string expectedRepoName)
    {
        // Act
        var path1 = _service.CreateRepositoryDirectory(url1);
        var path2 = _service.CreateRepositoryDirectory(url2);

        try
        {
            // Assert
            var repoName1 = Path.GetFileName(path1).Split('_')[0];
            var repoName2 = Path.GetFileName(path2).Split('_')[0];

            // Both should extract same repository name
            Assert.Equal(expectedRepoName, repoName1);
            Assert.Equal(expectedRepoName, repoName2);
            Assert.Equal(repoName1, repoName2);
        }
        finally
        {
            _service.DeleteRepositoryDirectory(path1);
            _service.DeleteRepositoryDirectory(path2);
        }
    }

    [Fact]
    public void DeleteRepositoryDirectory_WithLockedFile_DoesNotThrow()
    {
        // Arrange
        var path = _service.CreateRepositoryDirectory("https://github.com/user/repo");
        var filePath = Path.Combine(path, "locked.txt");
        File.WriteAllText(filePath, "content");

        // Lock the file
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None);

        // Act - deletion will fail silently due to locked file
        _service.DeleteRepositoryDirectory(path);

        // Cleanup
        stream.Close();
        stream.Dispose();

        // Final cleanup after unlock
        _service.DeleteRepositoryDirectory(path);
    }
}
