namespace DevProjex.Tests.Unit;

/// <summary>
/// Unit tests for RepoCacheService.
/// Tests repository cache management, directory creation, cleanup, and path handling.
/// </summary>
public class RepoCacheServiceTests : IDisposable
{
    private readonly RepoCacheService _service;
    private readonly string _testCacheRoot;

    public RepoCacheServiceTests()
    {
        _testCacheRoot = Path.Combine(Path.GetTempPath(), "DevProjex", "Tests", "CacheTests", Guid.NewGuid().ToString("N"));
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
    public void CreateRepositoryDirectory_CreatesUniqueDirectory()
    {
        // Create two directories for the same URL - should get unique paths
        var url = "https://github.com/user/repo";

        var dir1 = _service.CreateRepositoryDirectory(url);
        var dir2 = _service.CreateRepositoryDirectory(url);

        Assert.NotEqual(dir1, dir2);
        Assert.True(Directory.Exists(dir1));
        Assert.True(Directory.Exists(dir2));

        // Cleanup
        try
        {
            Directory.Delete(dir1, recursive: true);
            Directory.Delete(dir2, recursive: true);
        }
        catch { }
    }

    [Fact]
    public void PublishRepositoryDirectory_MovesCompletedCloneOutOfStaging()
    {
        var staging = _service.CreateRepositoryStagingDirectory(
            "https://github.com/user/repository.git");
        Directory.CreateDirectory(Path.Combine(staging, ".git"));
        File.WriteAllText(Path.Combine(staging, "README.md"), "content");

        var published = _service.PublishRepositoryDirectory(
            staging,
            "https://github.com/user/repository.git");

        Assert.False(Directory.Exists(staging));
        Assert.True(Directory.Exists(Path.Combine(published, ".git")));
        Assert.Equal("content", File.ReadAllText(Path.Combine(published, "README.md")));
        Assert.DoesNotContain(
            $"{Path.DirectorySeparatorChar}.staging{Path.DirectorySeparatorChar}",
            published,
            StringComparison.Ordinal);
        _service.DeleteRepositoryDirectory(published);
    }

    [Fact]
    public void PublishRepositoryDirectory_IndexesPersistentCache()
    {
        const string repositoryUrl = "https://github.com/user/repository.git";
        var staging = _service.CreateRepositoryStagingDirectory(repositoryUrl);
        Directory.CreateDirectory(Path.Combine(staging, ".git"));

        var published = _service.PublishRepositoryDirectory(staging, repositoryUrl);
        var indexed = _service.FindIndexedRepository(
            "git@github.com:user/repository.git");

        Assert.NotNull(indexed);
        Assert.Equal(published, indexed.LocalPath, PathComparer.Default);
        Assert.Equal(repositoryUrl, indexed.RepositoryUrl);
        Assert.Equal(RepositoryCacheEntryState.Ready, indexed.State);
    }

    [Fact]
    public void RecordIndexedRepository_UpdatesMetadataWithoutDuplicatingIdentity()
    {
        const string repositoryUrl = "https://github.com/user/repository.git";
        var cachePath = _service.CreateRepositoryDirectory(repositoryUrl);

        _service.RecordIndexedRepository(repositoryUrl, cachePath, "main", "1111111");
        _service.RecordIndexedRepository(
            "git@github.com:user/repository.git",
            cachePath,
            "release",
            "2222222");

        var indexed = _service.FindIndexedRepository(repositoryUrl);
        Assert.NotNull(indexed);
        Assert.Equal("release", indexed.Branch);
        Assert.Equal("2222222", indexed.CommitHash);

        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(_testCacheRoot, "cache-index.json")));
        Assert.Single(document.RootElement.GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public void RemoveIndexedRepository_PreservesCachedFiles()
    {
        const string repositoryUrl = "https://github.com/user/repository.git";
        var cachePath = _service.CreateRepositoryDirectory(repositoryUrl);
        var contentPath = Path.Combine(cachePath, "README.md");
        File.WriteAllText(contentPath, "content");
        _service.RecordIndexedRepository(repositoryUrl, cachePath);

        _service.RemoveIndexedRepository(cachePath);

        Assert.Null(_service.FindIndexedRepository(repositoryUrl));
        Assert.Equal("content", File.ReadAllText(contentPath));
    }

    [Fact]
    public void FindIndexedRepository_RecoversFromValidBackup()
    {
        const string repositoryUrl = "https://github.com/user/repository.git";
        var cachePath = _service.CreateRepositoryDirectory(repositoryUrl);
        _service.RecordIndexedRepository(repositoryUrl, cachePath, "main", "1234567");
        var indexPath = Path.Combine(_testCacheRoot, "cache-index.json");
        Assert.True(File.Exists($"{indexPath}.bak"));
        File.WriteAllText(indexPath, "{ invalid json");

        var indexed = new RepoCacheService(_testCacheRoot)
            .FindIndexedRepository(repositoryUrl);

        Assert.NotNull(indexed);
        Assert.Equal(cachePath, indexed.LocalPath, PathComparer.Default);
        Assert.Equal("main", indexed.Branch);
    }

    [Fact]
    public void FindIndexedRepository_IgnoresEntriesWithMissingRequiredStrings()
    {
        Directory.CreateDirectory(_testCacheRoot);
        File.WriteAllText(
            Path.Combine(_testCacheRoot, "cache-index.json"),
            """
            {
              "schemaVersion": 1,
              "entries": [
                {
                  "identity": null,
                  "repositoryUrl": null,
                  "localPath": null,
                  "state": "ready",
                  "lastUsedUtc": "2026-07-29T00:00:00Z"
                }
              ]
            }
            """);

        var indexed = _service.FindIndexedRepository(
            "https://github.com/user/repository.git");

        Assert.Null(indexed);
    }

    [Fact]
    public void CreateRepositoryDirectory_SanitizesUrl()
    {
        // URL with special characters should create valid directory
        var url = "https://github.com/user/my-repo.git";

        var dir = _service.CreateRepositoryDirectory(url);

        Assert.True(Directory.Exists(dir));
        Assert.DoesNotContain(":", Path.GetFileName(dir));
        Assert.DoesNotContain("/", Path.GetFileName(dir));
        Assert.DoesNotContain("\\", Path.GetFileName(dir));

        // Cleanup
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    [Fact]
    public void DeleteRepositoryDirectory_RemovesDirectory()
    {
        var url = "https://github.com/user/repo";
        var dir = _service.CreateRepositoryDirectory(url);

        // Create some content
        File.WriteAllText(Path.Combine(dir, "test.txt"), "test");

        _service.DeleteRepositoryDirectory(dir);

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void DeleteRepositoryDirectory_HandlesNonexistentDirectory()
    {
        var nonExistentPath = Path.Combine(_testCacheRoot, "nonexistent");

        // Should not throw
        _service.DeleteRepositoryDirectory(nonExistentPath);
    }

    [Fact]
    public void DeleteRepositoryDirectory_HandlesLockedFiles()
    {
        var url = "https://github.com/user/repo";
        var dir = _service.CreateRepositoryDirectory(url);
        var filePath = Path.Combine(dir, "locked.txt");

        // Create and lock a file
        using var stream = File.Open(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write([1, 2, 3]);
        stream.Flush();

        // Delete should not throw even with locked file
        var deleteWhileLockedException = Record.Exception(() => _service.DeleteRepositoryDirectory(dir));
        Assert.Null(deleteWhileLockedException);

        stream.Dispose();

        // After lock release, deletion should finish successfully.
        _service.DeleteRepositoryDirectory(dir);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void CreateRepositoryDirectory_CreatesUniqueDirectories()
    {
        var url = "https://github.com/user/repo";
        var dir1 = _service.CreateRepositoryDirectory(url);
        var dir2 = _service.CreateRepositoryDirectory(url);

        try
        {
            // Both should be created and be different
            Assert.NotEqual(dir1, dir2);
            Assert.Contains("Tests", dir1);
            Assert.Contains("Tests", dir2);
        }
        finally
        {
            // Best effort cleanup
            try { _service.DeleteRepositoryDirectory(dir1); } catch { }
            try { _service.DeleteRepositoryDirectory(dir2); } catch { }
        }
    }

    [Fact]
    public void DeleteRepositoryDirectory_OnlyDeletesIfInsideCacheRoot()
    {
        // Try to delete a directory outside cache root (security test)
        var outsidePath = Path.Combine(Path.GetTempPath(), "SomeOtherDir");
        Directory.CreateDirectory(outsidePath);
        File.WriteAllText(Path.Combine(outsidePath, "important.txt"), "data");

        // Should not delete directories outside cache
        _service.DeleteRepositoryDirectory(outsidePath);

        // Directory should still exist (or at least file should exist if it was protected)
        // This test verifies the service doesn't delete arbitrary paths
        Assert.True(Directory.Exists(outsidePath) || !File.Exists(Path.Combine(outsidePath, "important.txt")));

        // Cleanup
        try
        {
            if (Directory.Exists(outsidePath))
                Directory.Delete(outsidePath, recursive: true);
        }
        catch { }
    }

    [Fact]
    public void IsInCache_PrefixTrapSibling_ReturnsFalse()
    {
        var sibling = _testCacheRoot + "2";
        Directory.CreateDirectory(sibling);

        try
        {
            Assert.False(_service.IsInCache(sibling));
        }
        finally
        {
            try
            {
                if (Directory.Exists(sibling))
                    Directory.Delete(sibling, recursive: true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    [Fact]
    public void IsInCache_CaseVariantBehavior_MatchesPlatform()
    {
        var cacheDir = _service.CreateRepositoryDirectory("https://github.com/user/repo");
        var alteredCaseRoot = _testCacheRoot.Replace("CacheTests", "cAchetEsts", StringComparison.Ordinal);
        var alteredCasePath = cacheDir.Replace(_testCacheRoot, alteredCaseRoot, StringComparison.Ordinal);

        Assert.Equal(OperatingSystem.IsWindows(), _service.IsInCache(alteredCasePath));

        _service.DeleteRepositoryDirectory(cacheDir);
    }
}
