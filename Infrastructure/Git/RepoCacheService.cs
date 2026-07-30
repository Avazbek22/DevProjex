using System.Buffers;
using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Infrastructure.Git;

/// <summary>
/// Manages persistent repository clones and short-lived staging directories.
/// </summary>
public sealed class RepoCacheService : IRepoCacheService
{
	private const string AppFolderName = "DevProjex";
	private const string CacheFolderName = "RepoCache";
	private const string StagingFolderName = ".staging";
	private const string CacheIndexFileName = "cache-index.json";
	private const int CacheIndexSchemaVersion = 1;
	private static readonly TimeSpan IndexLockTimeout = TimeSpan.FromMilliseconds(500);
	private static readonly JsonSerializerOptions IndexSerializerOptions = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
	};

	// Pre-compiled search values for O(1) invalid character lookup (uses SIMD when available)
	private static readonly SearchValues<char> InvalidFileNameChars =
		SearchValues.Create("<>:\"/\\|?*");

	public string CacheRootPath { get; }
	public IReadOnlyList<string> CacheSearchRootPaths { get; }

	public RepoCacheService()
		: this(
			UserDataPathResolver.GetCacheRoot,
			UserDataPathResolver.GetLegacyLocalDataRoot)
	{
	}

	internal RepoCacheService(Func<string> cacheRootProvider)
		: this(cacheRootProvider, legacyDataRootProvider: null)
	{
	}

	internal RepoCacheService(
		Func<string> cacheRootProvider,
		Func<string>? legacyDataRootProvider)
	{
		ArgumentNullException.ThrowIfNull(cacheRootProvider);
		CacheRootPath = Path.Combine(
			cacheRootProvider(),
			AppFolderName,
			CacheFolderName);
		CacheSearchRootPaths = BuildCacheSearchRoots(
			CacheRootPath,
			legacyDataRootProvider);
	}

	/// <summary>
	/// Constructor for testing with custom cache path.
	/// </summary>
	public RepoCacheService(string customCachePath)
	{
		CacheRootPath = customCachePath ?? throw new ArgumentNullException(nameof(customCachePath));
		CacheSearchRootPaths = Array.AsReadOnly([CacheRootPath]);
	}

	public string CreateRepositoryDirectory(string repositoryUrl)
	{
		var path = CreateUniqueRepositoryPath(CacheRootPath, repositoryUrl);
		Directory.CreateDirectory(path);
		return path;
	}

	public string CreateRepositoryStagingDirectory(string repositoryUrl)
	{
		var stagingRoot = Path.Combine(CacheRootPath, StagingFolderName);
		var path = CreateUniqueRepositoryPath(stagingRoot, repositoryUrl);
		Directory.CreateDirectory(path);
		return path;
	}

	public string PublishRepositoryDirectory(string stagingPath, string repositoryUrl)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(stagingPath);
		var stagingRoot = Path.Combine(CacheRootPath, StagingFolderName);
		var normalizedStagingPath = PathUtility.Normalize(stagingPath);
		if (!PathUtility.IsPathInside(normalizedStagingPath, stagingRoot) ||
		    !Directory.Exists(normalizedStagingPath))
		{
			throw new InvalidOperationException("Repository staging path is invalid.");
		}

		Directory.CreateDirectory(CacheRootPath);
		var destination = CreateUniqueRepositoryPath(CacheRootPath, repositoryUrl);
		Directory.Move(normalizedStagingPath, destination);
		RecordIndexedRepository(repositoryUrl, destination);
		return destination;
	}

	public RepositoryCacheIndexEntry? FindIndexedRepository(string repositoryUrl)
	{
		var identity = RepositoryUrlUtility.GetComparisonKey(repositoryUrl);
		if (identity.Length == 0)
			return null;

		RepositoryCacheIndexEntry? matchingEntry = null;
		foreach (var searchRoot in CacheSearchRootPaths)
		{
			var fileSet = GetIndexFileSet(searchRoot);
			if (!PathComparer.Default.Equals(searchRoot, CacheRootPath) &&
			    !File.Exists(fileSet.PrimaryPath) &&
			    !File.Exists(fileSet.BackupPath))
			{
				continue;
			}

			if (!CrossProcessFileLock.TryAcquire(fileSet, IndexLockTimeout, out var heldLock))
				continue;

			using (heldLock)
			{
				var candidate = LoadIndex(fileSet).Entries
				.Where(entry => string.Equals(
					entry.Identity,
					identity,
					StringComparison.OrdinalIgnoreCase))
				.OrderByDescending(static entry => entry.LastUsedUtc)
				.FirstOrDefault();
				if (candidate is not null &&
				    (matchingEntry is null ||
				     candidate.LastUsedUtc > matchingEntry.LastUsedUtc))
				{
					matchingEntry = candidate;
				}
			}
		}

		if (matchingEntry is not null &&
		    !PathUtility.IsPathInside(matchingEntry.LocalPath, CacheRootPath))
		{
			RecordIndexedRepository(
				matchingEntry.RepositoryUrl,
				matchingEntry.LocalPath,
				matchingEntry.Branch,
				matchingEntry.CommitHash,
				matchingEntry.State);
		}

		return matchingEntry;
	}

	public void RecordIndexedRepository(
		string repositoryUrl,
		string localPath,
		string? branch = null,
		string? commitHash = null,
		RepositoryCacheEntryState state = RepositoryCacheEntryState.Ready)
	{
		var safeUrl = RepositoryUrlUtility.ToSafeDisplay(repositoryUrl);
		var identity = RepositoryUrlUtility.GetComparisonKey(safeUrl);
		if (identity.Length == 0 || string.IsNullOrWhiteSpace(localPath))
			return;

		string normalizedPath;
		try
		{
			normalizedPath = PathUtility.Normalize(localPath);
		}
		catch
		{
			return;
		}

		if (!IsInCache(normalizedPath))
			return;

		var fileSet = GetIndexFileSet();
		if (!CrossProcessFileLock.TryAcquire(fileSet, IndexLockTimeout, out var heldLock))
			return;

		using (heldLock)
		{
			if (JsonStorePersistence.ContainsFutureDocument(fileSet, CacheIndexSchemaVersion))
				return;

			var document = LoadIndex(fileSet);
			var entries = document.Entries
				.Where(entry =>
					!string.Equals(
						entry.Identity,
						identity,
						StringComparison.OrdinalIgnoreCase) &&
					!PathComparer.Default.Equals(entry.LocalPath, normalizedPath))
				.ToList();
			entries.Add(new RepositoryCacheIndexEntry(
				identity,
				safeUrl,
				normalizedPath,
				string.IsNullOrWhiteSpace(branch) ? null : branch.Trim(),
				string.IsNullOrWhiteSpace(commitHash) ? null : commitHash.Trim(),
				DateTimeOffset.UtcNow,
				state));
			JsonStorePersistence.TryWriteAtomic(
				fileSet,
				new RepositoryCacheIndexDocument(CacheIndexSchemaVersion, entries),
				IndexSerializerOptions);
		}
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
			if (!Directory.Exists(path))
				RemoveIndexedRepository(path);
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
		var stagingRoot = Path.Combine(CacheRootPath, StagingFolderName);
		if (!Directory.Exists(stagingRoot))
			return;

		try
		{
			var staleThreshold = DateTime.UtcNow.AddHours(-24);

			foreach (var dir in Directory.GetDirectories(stagingRoot))
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

	private static string CreateUniqueRepositoryPath(string root, string repositoryUrl)
	{
		var repoName = ExtractRepoName(repositoryUrl);
		while (true)
		{
			var suffix = $"{DateTime.UtcNow.Ticks:X}{Guid.NewGuid():N}"
				[..29]
				.ToUpperInvariant();
			var path = Path.Combine(root, $"{repoName}_{suffix}");
			if (!Directory.Exists(path) && !File.Exists(path))
				return path;
		}
	}

	public bool IsInCache(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return false;

		try
		{
			return CacheSearchRootPaths.Any(root =>
				PathUtility.IsPathInside(path, root));
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

	private JsonStoreFileSet GetIndexFileSet() =>
		GetIndexFileSet(CacheRootPath);

	private static JsonStoreFileSet GetIndexFileSet(string cacheRoot)
	{
		var primaryPath = Path.Combine(cacheRoot, CacheIndexFileName);
		return new JsonStoreFileSet(
			primaryPath,
			$"{primaryPath}.bak",
			$"{primaryPath}.lock");
	}

	private static IReadOnlyList<string> BuildCacheSearchRoots(
		string currentCacheRoot,
		Func<string>? legacyDataRootProvider)
	{
		var roots = new List<string> { currentCacheRoot };
		if (legacyDataRootProvider is not null)
		{
			try
			{
				var legacyCacheRoot = Path.Combine(
					legacyDataRootProvider(),
					AppFolderName,
					CacheFolderName);
				if (!roots.Any(root =>
					    PathComparer.Default.Equals(root, legacyCacheRoot)))
				{
					roots.Add(legacyCacheRoot);
				}
			}
			catch (Exception ex) when (ex is
				ArgumentException or
				IOException or
				NotSupportedException or
				UnauthorizedAccessException or
				InvalidOperationException or
				System.Security.SecurityException)
			{
				// Compatibility lookup must not prevent use of the configured cache root.
			}
		}

		return roots.AsReadOnly();
	}

	private RepositoryCacheIndexDocument LoadIndex(JsonStoreFileSet fileSet)
	{
		if (TryLoadIndex(fileSet.PrimaryPath, out var primary))
			return primary;
		if (TryLoadIndex(fileSet.BackupPath, out var backup))
			return backup;
		return RepositoryCacheIndexDocument.Empty;
	}

	private bool TryLoadIndex(
		string path,
		out RepositoryCacheIndexDocument document)
	{
		if (!JsonStorePersistence.TryReadNormalized(
			    path,
			    IndexSerializerOptions,
			    static () => RepositoryCacheIndexDocument.Empty,
			    NormalizeIndex,
			    out document,
			    out _))
		{
			document = RepositoryCacheIndexDocument.Empty;
			return false;
		}

		return document.SchemaVersion <= CacheIndexSchemaVersion;
	}

	private RepositoryCacheIndexDocument NormalizeIndex(
		RepositoryCacheIndexDocument document)
	{
		var entries = (document.Entries ?? [])
			.Where(entry =>
				entry is not null &&
				!string.IsNullOrWhiteSpace(entry.Identity) &&
				!string.IsNullOrWhiteSpace(entry.RepositoryUrl) &&
				!string.IsNullOrWhiteSpace(entry.LocalPath) &&
				IsInCache(entry.LocalPath))
			.GroupBy(
				static entry => entry.Identity,
				StringComparer.OrdinalIgnoreCase)
			.Select(static group => group
				.OrderByDescending(entry => entry.LastUsedUtc)
				.First())
			.OrderByDescending(static entry => entry.LastUsedUtc)
			.ToList();
		return new RepositoryCacheIndexDocument(
			CacheIndexSchemaVersion,
			entries);
	}

	public void RemoveIndexedRepository(string localPath)
	{
		string normalizedPath;
		try
		{
			normalizedPath = PathUtility.Normalize(localPath);
		}
		catch
		{
			return;
		}

		var fileSet = GetIndexFileSet();
		if (!CrossProcessFileLock.TryAcquire(fileSet, IndexLockTimeout, out var heldLock))
			return;

		using (heldLock)
		{
			if (JsonStorePersistence.ContainsFutureDocument(fileSet, CacheIndexSchemaVersion))
				return;

			var document = LoadIndex(fileSet);
			var entries = document.Entries
				.Where(entry => !PathComparer.Default.Equals(entry.LocalPath, normalizedPath))
				.ToList();
			if (entries.Count == document.Entries.Count)
				return;

			JsonStorePersistence.TryWriteAtomic(
				fileSet,
				new RepositoryCacheIndexDocument(CacheIndexSchemaVersion, entries),
				IndexSerializerOptions);
		}
	}

	private sealed record RepositoryCacheIndexDocument(
		int SchemaVersion,
		List<RepositoryCacheIndexEntry> Entries)
	{
		public static RepositoryCacheIndexDocument Empty =>
			new(CacheIndexSchemaVersion, []);
	}
}
