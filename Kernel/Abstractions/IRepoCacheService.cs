namespace DevProjex.Kernel.Abstractions;

/// <summary>
/// Manages the persistent repository cache and temporary clone staging.
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
    /// Creates a temporary clone directory that is never exposed as a usable cache entry.
    /// </summary>
    string CreateRepositoryStagingDirectory(string repositoryUrl);

    /// <summary>
    /// Atomically publishes a completed staging clone into the persistent cache.
    /// </summary>
    string PublishRepositoryDirectory(string stagingPath, string repositoryUrl);

    /// <summary>
    /// Returns the most recently used indexed cache entry for an equivalent repository URL.
    /// </summary>
    RepositoryCacheIndexEntry? FindIndexedRepository(string repositoryUrl);

    /// <summary>
    /// Records repository metadata after a cache is published, opened or refreshed.
    /// </summary>
    void RecordIndexedRepository(
        string repositoryUrl,
        string localPath,
        string? branch = null,
        string? commitHash = null,
        RepositoryCacheEntryState state = RepositoryCacheEntryState.Ready);

    /// <summary>
    /// Removes only cache metadata for a path without deleting repository files.
    /// </summary>
    void RemoveIndexedRepository(string localPath);

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
    /// Cleans up abandoned staging directories. Completed repositories are retained
    /// regardless of age until an explicit cache-management policy removes them.
    /// </summary>
    void CleanupStaleCacheOnStartup();

    /// <summary>
    /// Checks if the given path is within the cache.
    /// </summary>
    bool IsInCache(string path);
}
