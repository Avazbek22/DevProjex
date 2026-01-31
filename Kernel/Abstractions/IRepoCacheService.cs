using System.Threading;
using System.Threading.Tasks;

namespace DevProjex.Kernel.Abstractions;

/// <summary>
/// Manages the repository cache directory.
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
    /// Deletes a specific repository directory (best-effort, may fail silently).
    /// For reliable cleanup, use DeleteRepositoryDirectoryAsync.
    /// </summary>
    void DeleteRepositoryDirectory(string path);

    /// <summary>
    /// Deletes a specific repository directory with retry logic and proper error handling.
    /// Returns true if deletion succeeded, false if deferred for later cleanup.
    /// </summary>
    Task<bool> DeleteRepositoryDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all cached repositories (best-effort, may fail silently).
    /// </summary>
    void ClearAllCache();

    /// <summary>
    /// Clears all cached repositories with retry logic.
    /// </summary>
    Task ClearAllCacheAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleans up stale cache directories that failed to delete in previous sessions.
    /// Should be called on application startup.
    /// </summary>
    void CleanupStaleCacheOnStartup();

    /// <summary>
    /// Checks if the given path is within the cache.
    /// </summary>
    bool IsInCache(string path);
}
