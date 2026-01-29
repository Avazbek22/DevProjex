using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using DevProjex.Kernel.Abstractions;

namespace DevProjex.Infrastructure.Git;

/// <summary>
/// Manages the repository cache directory in AppData.
/// </summary>
public sealed class RepoCacheService : IRepoCacheService
{
    private const string AppFolderName = "DevProjex";
    private const string CacheFolderName = "RepoCache";

    public string CacheRootPath { get; }

    public RepoCacheService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        CacheRootPath = Path.Combine(appData, AppFolderName, CacheFolderName);
    }

    public string CreateRepositoryDirectory(string repositoryUrl)
    {
        var hash = ComputeUrlHash(repositoryUrl);
        var timestamp = DateTime.UtcNow.Ticks.ToString("X");
        var folderName = $"{hash}_{timestamp}";
        var path = Path.Combine(CacheRootPath, folderName);

        Directory.CreateDirectory(path);
        return path;
    }

    public void DeleteRepositoryDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (!IsInCache(path))
            return;

        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort cleanup - ignore errors
        }
    }

    public void ClearAllCache()
    {
        try
        {
            if (Directory.Exists(CacheRootPath))
                Directory.Delete(CacheRootPath, recursive: true);
        }
        catch
        {
            // Best effort cleanup - ignore errors
        }
    }

    public bool IsInCache(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var fullPath = Path.GetFullPath(path);
            var cachePath = Path.GetFullPath(CacheRootPath);

            return fullPath.StartsWith(cachePath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string ComputeUrlHash(string url)
    {
        var bytes = Encoding.UTF8.GetBytes(url.ToLowerInvariant().Trim());
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes)[..16];
    }
}
