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
    /// Deletes a specific repository directory.
    /// </summary>
    void DeleteRepositoryDirectory(string path);

    /// <summary>
    /// Clears all cached repositories.
    /// </summary>
    void ClearAllCache();

    /// <summary>
    /// Checks if the given path is within the cache.
    /// </summary>
    bool IsInCache(string path);
}
