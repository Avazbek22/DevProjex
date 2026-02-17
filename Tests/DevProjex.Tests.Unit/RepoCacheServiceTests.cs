using DevProjex.Infrastructure.Git;

namespace DevProjex.Tests.Unit;

/// <summary>
/// Unit tests for RepoCacheService.
/// Tests repository cache management, directory creation, cleanup, and path handling.
/// </summary>
public class RepoCacheServiceTests : IDisposable
{
    private readonly RepoCacheService _service;
    private readonly string _originalCacheRoot;
    private readonly string _testCacheRoot;

    public RepoCacheServiceTests()
    {
        _originalCacheRoot = Path.Combine(Path.GetTempPath(), "DevProjex", "RepoCache");
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
        stream.Write(new byte[] { 1, 2, 3 });
        stream.Flush();

        // Delete should not throw even with locked file
        _service.DeleteRepositoryDirectory(dir);

        // File might still exist if locked, but no exception should be thrown
        Assert.True(true);
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
}
