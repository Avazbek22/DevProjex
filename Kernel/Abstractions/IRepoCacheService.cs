namespace DevProjex.Kernel.Abstractions;

/// <summary>
/// Manages the repository cache directory in temp folder.
/// </summary>
public interface IRepoCacheService
{
    /// <summary>
    /// Gets the root path of the repository cache.
    /// </summary>
    string CacheRootPath { get; }

    /// <summary>
    /// Creates a unique directory for a new cloned repository.
    /// </summary>
    string CreateRepositoryDirectory(string repositoryUrl);

    /// <summary>
    /// Deletes a specific repository directory (best-effort).
    /// Locked files will be cleaned up on next startup.
    /// </summary>
    void DeleteRepositoryDirectory(string path);

    /// <summary>
    /// Clears all cached repositories (best-effort).
    /// Locked files will be cleaned up on next startup.
    /// </summary>
    void ClearAllCache();

    /// <summary>
    /// Cleans up cache directories older than 24 hours.
    /// Should be called on application startup.
    /// </summary>
    void CleanupStaleCacheOnStartup();

    /// <summary>
    /// Checks if the given path is within the cache.
    /// </summary>
    bool IsInCache(string path);
}
