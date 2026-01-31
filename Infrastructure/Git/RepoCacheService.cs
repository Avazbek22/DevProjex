using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DevProjex.Kernel.Abstractions;

namespace DevProjex.Infrastructure.Git;

/// <summary>
/// Manages the repository cache directory in system temp folder with robust cleanup.
/// </summary>
public sealed class RepoCacheService : IRepoCacheService
{
	private const string AppFolderName = "DevProjex";
	private const string CacheFolderName = "RepoCache";
	private const string CleanupMarkerFile = ".pending-cleanup";

	public string CacheRootPath { get; }

	public RepoCacheService()
	{
		var tempPath = Path.GetTempPath();
		CacheRootPath = Path.Combine(tempPath, AppFolderName, CacheFolderName);
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
			{
				RemoveReadOnlyAttributes(path);
				Directory.Delete(path, recursive: true);
			}
		}
		catch
		{
			// Best effort cleanup - ignore errors
			// For reliable cleanup, use DeleteRepositoryDirectoryAsync
		}
	}

	public async Task<bool> DeleteRepositoryDirectoryAsync(string path, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(path))
			return true;

		if (!IsInCache(path))
			return true;

		if (!Directory.Exists(path))
			return true;

		// Attempt 1: Quick synchronous delete
		if (TryDeleteSync(path))
			return true;

		// Attempt 2: Remove read-only attributes and retry
		RemoveReadOnlyAttributes(path);
		if (TryDeleteSync(path))
			return true;

		// Attempt 3-5: Retry with exponential backoff
		for (int attempt = 0; attempt < 3; attempt++)
		{
			try
			{
				await Task.Delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt)), cancellationToken);
			}
			catch (OperationCanceledException)
			{
				// Cancellation requested - mark for deferred cleanup
				MarkForDeferredCleanup(path);
				return false;
			}

			if (TryDeleteSync(path))
				return true;
		}

		// Failed after retries - mark for deferred cleanup
		MarkForDeferredCleanup(path);
		return false;
	}

	public void ClearAllCache()
	{
		try
		{
			if (Directory.Exists(CacheRootPath))
			{
				RemoveReadOnlyAttributes(CacheRootPath);
				Directory.Delete(CacheRootPath, recursive: true);
			}
		}
		catch
		{
			// Best effort cleanup - ignore errors
		}
	}

	public async Task ClearAllCacheAsync(CancellationToken cancellationToken = default)
	{
		if (!Directory.Exists(CacheRootPath))
			return;

		try
		{
			// Get all cache directories
			var directories = Directory.GetDirectories(CacheRootPath)
				.Where(d => !Path.GetFileName(d).Equals(CleanupMarkerFile, StringComparison.Ordinal))
				.ToArray();

			// Delete each directory with retry logic
			foreach (var dir in directories)
			{
				await DeleteRepositoryDirectoryAsync(dir, cancellationToken);
			}

			// Try to delete the root cache directory if empty
			try
			{
				if (Directory.GetFileSystemEntries(CacheRootPath).Length == 0)
				{
					Directory.Delete(CacheRootPath, recursive: false);
				}
			}
			catch
			{
				// Ignore - directory might still have pending cleanup items
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
			// Best effort - some directories might remain
		}
	}

	public void CleanupStaleCacheOnStartup()
	{
		if (!Directory.Exists(CacheRootPath))
			return;

		try
		{
			var cleanupMarkerPath = Path.Combine(CacheRootPath, CleanupMarkerFile);

			// Check if there are pending cleanups from previous sessions
			if (File.Exists(cleanupMarkerPath))
			{
				// Clean up directories older than 24 hours
				var staleThreshold = DateTime.UtcNow.AddHours(-24);

				foreach (var dir in Directory.GetDirectories(CacheRootPath))
				{
					try
					{
						if (Directory.GetCreationTimeUtc(dir) < staleThreshold)
						{
							RemoveReadOnlyAttributes(dir);
							Directory.Delete(dir, recursive: true);
						}
					}
					catch
					{
						// Skip directories that can't be deleted
					}
				}

				// Remove cleanup marker if successful
				try
				{
					File.Delete(cleanupMarkerPath);
				}
				catch
				{
					// Ignore
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

	private bool TryDeleteSync(string path)
	{
		try
		{
			if (Directory.Exists(path))
			{
				Directory.Delete(path, recursive: true);
			}
			return true;
		}
		catch
		{
			return false;
		}
	}

	private void RemoveReadOnlyAttributes(string path)
	{
		try
		{
			var dirInfo = new DirectoryInfo(path);

			// Remove read-only from directory itself
			if ((dirInfo.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
			{
				dirInfo.Attributes &= ~FileAttributes.ReadOnly;
			}

			// Remove read-only from all files recursively
			foreach (var file in dirInfo.GetFiles("*", SearchOption.AllDirectories))
			{
				if ((file.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
				{
					file.Attributes &= ~FileAttributes.ReadOnly;
				}
			}

			// Remove read-only from all subdirectories
			foreach (var subDir in dirInfo.GetDirectories("*", SearchOption.AllDirectories))
			{
				if ((subDir.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
				{
					subDir.Attributes &= ~FileAttributes.ReadOnly;
				}
			}
		}
		catch
		{
			// Best effort - continue even if some attributes can't be changed
		}
	}

	private void MarkForDeferredCleanup(string path)
	{
		try
		{
			if (!Directory.Exists(CacheRootPath))
				Directory.CreateDirectory(CacheRootPath);

			var cleanupMarkerPath = Path.Combine(CacheRootPath, CleanupMarkerFile);
			var entry = $"{DateTime.UtcNow:O}|{path}{Environment.NewLine}";

			File.AppendAllText(cleanupMarkerPath, entry);
		}
		catch
		{
			// Best effort - ignore errors
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

	private static string SanitizeFileName(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
			return "repo";

		// Cross-platform invalid characters for file/folder names
		// Windows: < > : " / \ | ? *
		// Linux/macOS: /
		// We remove all Windows invalid chars for maximum compatibility
		var invalidChars = new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

		var sanitized = new StringBuilder(name.Length);
		foreach (var c in name)
		{
			if (!invalidChars.Contains(c) && !char.IsControl(c))
				sanitized.Append(c);
		}

		var result = sanitized.ToString().Trim();

		// If result is empty or too long, use fallback
		if (string.IsNullOrWhiteSpace(result))
			return "repo";

		// Limit length to avoid path too long issues (keep it reasonable)
		return result.Length > 100 ? result[..100] : result;
	}
}
