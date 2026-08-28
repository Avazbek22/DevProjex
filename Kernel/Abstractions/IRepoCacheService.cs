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
    /// Gets the current cache root followed by compatibility roots used for discovery.
    /// New repositories and metadata are always written to <see cref="CacheRootPath"/>.
    /// </summary>
    IReadOnlyList<string> CacheSearchRootPaths { get; }

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
    /// Lists ready repositories from the current and compatibility cache indexes.
    /// Missing repository directories are removed from their owning indexes.
    /// </summary>
    IReadOnlyList<RepositoryCacheCatalogEntry> ListIndexedRepositories();

    /// <summary>
    /// Lists every existing indexed cache entry for explicit cache management, including damaged
    /// entries that normal repository discovery must not offer for opening, and reports cache
    /// roots that could not be read safely.
    /// </summary>
    RepositoryCacheManagementListResult ListCacheEntriesForManagement();

    /// <summary>
    /// Pins a cache checkout for an indexed repository. Cache roots must be on a local file system;
    /// exclusive file-handle leases are not reliable on every network file system.
    /// </summary>
    Task<IRepositoryCacheSession?> TryAcquireRepositorySessionAsync(
        string repositoryUrl,
        string? branch = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pins the indexed repository containing an existing cache path.
    /// </summary>
    Task<IRepositoryCacheSession?> TryAcquireRepositorySessionByPathAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Serializes initial publication for equivalent repository URLs across processes.
    /// </summary>
    Task<IAsyncDisposable> AcquireRepositoryOperationAsync(
        string repositoryUrl,
        CancellationToken cancellationToken = default);

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
    /// Clears all cached repositories and reports entries removed, retained by leases,
    /// or left behind after a failed cleanup.
    /// </summary>
    CacheClearResult ClearAllCacheWithResult();

    /// <summary>
    /// Removes cached entries for an equivalent repository URL and reports the outcome.
    /// </summary>
    CacheClearResult RemoveCachedRepositoryWithResult(string repositoryUrl);

    /// <summary>
    /// Cleans up abandoned staging directories. Completed repositories are retained
    /// regardless of age until an explicit cache-management policy removes them.
    /// </summary>
    void CleanupStaleCacheOnStartup();

    /// <summary>
    /// Runs best-effort trash cleanup and size/age eviction without touching pinned repositories.
    /// </summary>
    void CollectGarbage();

    /// <summary>
    /// Requests best-effort garbage collection on the shared background scheduler.
    /// Repeated requests are coalesced without blocking the caller.
    /// </summary>
    void RequestGarbageCollection();

    /// <summary>
    /// Recomputes the approximate size after an explicit repository update.
    /// </summary>
    void RefreshIndexedRepositorySize(string localPath);

    /// <summary>
    /// Checks if the given path is within the cache.
    /// </summary>
    bool IsInCache(string path);

    /// <summary>
    /// Determines whether two cache paths belong to the same managed repository container.
    /// </summary>
    bool PathsBelongToSameRepository(string left, string right);
}
