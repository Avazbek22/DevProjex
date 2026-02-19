using System.Buffers;

namespace DevProjex.Infrastructure.Git;

/// <summary>
/// Manages the repository cache directory in system temp folder.
/// </summary>
public sealed class RepoCacheService : IRepoCacheService
{
	private const string AppFolderName = "DevProjex";
	private const string CacheFolderName = "RepoCache";

	// Pre-compiled search values for O(1) invalid character lookup (uses SIMD when available)
	private static readonly SearchValues<char> InvalidFileNameChars =
		SearchValues.Create("<>:\"/\\|?*");

	public string CacheRootPath { get; }

	public RepoCacheService()
	{
		var tempPath = Path.GetTempPath();
		CacheRootPath = Path.Combine(tempPath, AppFolderName, CacheFolderName);
	}

	/// <summary>
	/// Constructor for testing with custom cache path.
	/// </summary>
	public RepoCacheService(string customCachePath)
	{
		CacheRootPath = customCachePath ?? throw new ArgumentNullException(nameof(customCachePath));
	}

	public string CreateRepositoryDirectory(string repositoryUrl)
	{
		var repoName = ExtractRepoName(repositoryUrl);
		var timestamp = DateTime.UtcNow.Ticks.ToString("X");
		var folderName = $"{repoName}_{timestamp}";
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
			// Best effort - locked files will be cleaned up on next startup
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
			// Best effort - old files will be cleaned up on next startup
		}
	}

	public void CleanupStaleCacheOnStartup()
	{
		if (!Directory.Exists(CacheRootPath))
			return;

		try
		{
			var staleThreshold = DateTime.UtcNow.AddHours(-24);

			foreach (var dir in Directory.GetDirectories(CacheRootPath))
			{
				try
				{
					if (Directory.GetCreationTimeUtc(dir) < staleThreshold)
						Directory.Delete(dir, recursive: true);
				}
				catch
				{
					// Skip locked directories - will be cleaned on next startup
				}
			}
		}
		catch
		{
			// Best effort - ignore errors
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

	private static string ExtractRepoName(string url)
	{
		if (string.IsNullOrWhiteSpace(url))
			return "repo";

		try
		{
			// Remove trailing .git if present
			var cleanUrl = url.TrimEnd('/');
			if (cleanUrl.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
				cleanUrl = cleanUrl[..^4];

			// Extract last segment (repository name)
			var lastSlashIndex = cleanUrl.LastIndexOf('/');
			var repoName = lastSlashIndex >= 0
				? cleanUrl[(lastSlashIndex + 1)..]
				: cleanUrl;

			// Sanitize for file system compatibility
			return SanitizeFileName(repoName);
		}
		catch
		{
			return "repo";
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static string SanitizeFileName(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
			return "repo";

		// Use string.Create for zero-allocation string building when possible
		var span = name.AsSpan();

		// Fast path: check if sanitization is needed using SIMD-optimized search
		if (!span.ContainsAny(InvalidFileNameChars) && !ContainsControlChars(span))
		{
			var trimmed = name.Trim();
			if (string.IsNullOrWhiteSpace(trimmed))
				return "repo";
			return trimmed.Length > 100 ? trimmed[..100] : trimmed;
		}

		// Slow path: need to filter characters
		var sanitized = new StringBuilder(name.Length);
		foreach (var c in span)
		{
			if (!InvalidFileNameChars.Contains(c) && !char.IsControl(c))
				sanitized.Append(c);
		}

		var result = sanitized.ToString().Trim();

		// If result is empty or too long, use fallback
		if (string.IsNullOrWhiteSpace(result))
			return "repo";

		// Limit length to avoid path too long issues (keep it reasonable)
		return result.Length > 100 ? result[..100] : result;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool ContainsControlChars(ReadOnlySpan<char> span)
	{
		foreach (var c in span)
		{
			if (char.IsControl(c))
				return true;
		}
		return false;
	}
}
