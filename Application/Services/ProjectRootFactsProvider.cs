using System.IO.Enumeration;
using System.Security.Cryptography;
using DevProjex.Application.Diagnostics;

namespace DevProjex.Application.Services;

public sealed class ProjectRootFactsProvider
{
	private const int DefaultCacheLimit = 256;
	private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromSeconds(5);
	private static readonly EnumerationOptions TopLevelEnumerationOptions = new()
	{
		RecurseSubdirectories = false,
		ReturnSpecialDirectories = false,
		AttributesToSkip = 0,
		IgnoreInaccessible = false
	};

	private readonly object _cacheSync = new();
	private readonly Dictionary<string, LinkedListNode<CacheEntry>> _cache = new(PathComparer.Default);
	private readonly LinkedList<CacheEntry> _cacheLru = new();
	private readonly TimeSpan _cacheTtl;
	private readonly int _cacheLimit;
	private readonly Func<DateTime> _utcNowProvider;
	private readonly Func<string, ProjectRootFacts> _factsBuilder;
	private readonly Dictionary<string, long> _latestBuildSequences = new(PathComparer.Default);
	private long _cacheGeneration;
	private long _nextBuildSequence;

	public ProjectRootFactsProvider(
		TimeSpan? cacheTtl = null,
		int cacheLimit = DefaultCacheLimit,
		Func<DateTime>? utcNowProvider = null)
		: this(cacheTtl, cacheLimit, utcNowProvider, Build)
	{
	}

	internal ProjectRootFactsProvider(
		TimeSpan? cacheTtl,
		int cacheLimit,
		Func<DateTime>? utcNowProvider,
		Func<string, ProjectRootFacts> factsBuilder)
	{
		_cacheTtl = cacheTtl ?? DefaultCacheTtl;
		_cacheLimit = Math.Max(0, cacheLimit);
		_utcNowProvider = utcNowProvider ?? (() => DateTime.UtcNow);
		_factsBuilder = factsBuilder ?? throw new ArgumentNullException(nameof(factsBuilder));
	}

	public ProjectRootFacts Get(string rootPath, bool forceRefresh = false)
	{
		IgnorePipelineDiagnostics.RecordRootFactsRequest();
		if (string.IsNullOrWhiteSpace(rootPath))
			return ProjectRootFacts.Missing(rootPath);

		if (!TryNormalizePath(rootPath, out var normalizedRootPath))
			return ProjectRootFacts.Missing(rootPath);

		var now = _utcNowProvider();
		if (!forceRefresh && _cacheLimit > 0)
		{
			lock (_cacheSync)
			{
				if (_cache.TryGetValue(normalizedRootPath, out var cachedNode))
				{
					var age = now - cachedNode.Value.CachedAtUtc;
					if (age >= TimeSpan.Zero && age <= _cacheTtl)
					{
						_cacheLru.Remove(cachedNode);
						_cacheLru.AddFirst(cachedNode);
						IgnorePipelineDiagnostics.RecordRootFactsCacheHit();
						return cachedNode.Value.Facts;
					}
				}
			}
		}

		IgnorePipelineDiagnostics.RecordRootFactsBuild();
		if (_cacheLimit == 0)
			return _factsBuilder(normalizedRootPath);

		long cacheGeneration;
		long buildSequence;
		lock (_cacheSync)
		{
			cacheGeneration = _cacheGeneration;
			buildSequence = unchecked(++_nextBuildSequence);
			_latestBuildSequences[normalizedRootPath] = buildSequence;
		}

		ProjectRootFacts facts;
		try
		{
			facts = _factsBuilder(normalizedRootPath);
		}
		catch
		{
			AbandonBuildReservation(normalizedRootPath, cacheGeneration, buildSequence);
			throw;
		}

		lock (_cacheSync)
		{
			// A filesystem invalidation or a newer refresh wins even if this older build
			// completes later. Returning the facts to its caller is safe; caching them is not.
			if (_cacheGeneration != cacheGeneration ||
			    !_latestBuildSequences.TryGetValue(normalizedRootPath, out var latestSequence) ||
			    latestSequence != buildSequence)
			{
				return facts;
			}

			_latestBuildSequences.Remove(normalizedRootPath);
			RemoveCacheEntry(normalizedRootPath);
			var entry = new CacheEntry(normalizedRootPath, _utcNowProvider(), facts);
			_cache[normalizedRootPath] = _cacheLru.AddFirst(entry);
			TrimCache();
		}

		return facts;
	}

	public void Invalidate(string rootPath, bool includeDescendants = false)
	{
		if (!TryNormalizePath(rootPath, out var normalizedRootPath))
			return;

		lock (_cacheSync)
		{
			_cacheGeneration = unchecked(_cacheGeneration + 1);
			_latestBuildSequences.Clear();
			if (!includeDescendants)
			{
				RemoveCacheEntry(normalizedRootPath);
				return;
			}

			foreach (var cachedPath in _cache.Keys.ToArray())
			{
				if (IsSameOrDescendantPath(cachedPath, normalizedRootPath))
					RemoveCacheEntry(cachedPath);
			}
		}
	}

	private void AbandonBuildReservation(string path, long cacheGeneration, long buildSequence)
	{
		lock (_cacheSync)
		{
			if (_cacheGeneration == cacheGeneration &&
			    _latestBuildSequences.TryGetValue(path, out var latestSequence) &&
			    latestSequence == buildSequence)
			{
				_latestBuildSequences.Remove(path);
			}
		}
	}

	private void TrimCache()
	{
		while (_cache.Count > _cacheLimit && _cacheLru.Last is { } leastRecentlyUsed)
		{
			RemoveCacheEntry(leastRecentlyUsed.Value.Path);
			IgnorePipelineDiagnostics.RecordRootFactsEviction();
		}
	}

	private void RemoveCacheEntry(string path)
	{
		if (!_cache.Remove(path, out var node))
			return;

		_cacheLru.Remove(node);
	}

	private static ProjectRootFacts Build(string rootPath)
	{
		if (!Directory.Exists(rootPath))
			return ProjectRootFacts.Missing(rootPath);

		var files = new List<ProjectRootFileFact>();
		var directories = new List<ProjectRootDirectoryFact>();

		try
		{
			foreach (var entry in EnumerateTopLevelEntries(rootPath))
			{
				if (entry.IsDirectory)
				{
					directories.Add(new ProjectRootDirectoryFact(
						entry.Name,
						entry.FullPath,
						entry.IsReparsePoint));
					continue;
				}

				files.Add(new ProjectRootFileFact(
					entry.Name,
					Path.GetExtension(entry.Name),
					entry.IsReparsePoint));
			}
		}
		catch (UnauthorizedAccessException)
		{
			return ProjectRootFacts.Inaccessible(rootPath);
		}
		catch (IOException)
		{
			return ProjectRootFacts.Inaccessible(rootPath);
		}

		var gitIgnoreSignature = TryGetTopLevelGitIgnoreSignature(rootPath, files);
		return new ProjectRootFacts(
			rootPath,
			exists: true,
			isAccessible: true,
			files,
			directories,
			gitIgnoreSignature);
	}

	private static IEnumerable<ProjectRootEntry> EnumerateTopLevelEntries(string rootPath)
	{
		var enumerable = new FileSystemEnumerable<ProjectRootEntry>(
			rootPath,
			static (ref FileSystemEntry entry) =>
			{
				var name = entry.FileName.ToString();
				return new ProjectRootEntry(
					name,
					entry.ToSpecifiedFullPath(),
					entry.IsDirectory,
					(entry.Attributes & FileAttributes.ReparsePoint) != 0);
			},
			TopLevelEnumerationOptions);
		enumerable.ShouldIncludePredicate = static (ref FileSystemEntry entry) => true;
		return enumerable;
	}

	private static ProjectRootFileSignature? TryGetTopLevelGitIgnoreSignature(
		string rootPath,
		IReadOnlyList<ProjectRootFileFact> files)
	{
		var hasGitIgnore = false;
		foreach (var file in files)
		{
			if (!file.IsReparsePoint && PathComparer.Default.Equals(file.Name, ".gitignore"))
			{
				hasGitIgnore = true;
				break;
			}
		}

		if (!hasGitIgnore)
			return null;

		return TryGetFileSignature(Path.Combine(rootPath, ".gitignore"));
	}

	public static ProjectRootFileSignature? TryGetFileSignature(string filePath)
	{
		try
		{
			var linkInfo = new FileInfo(filePath);
			if (!linkInfo.Exists)
				return null;

			if (linkInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
			    !string.IsNullOrEmpty(linkInfo.LinkTarget))
				return null;
			if (linkInfo.Length > GitIgnoreFileReader.MaximumFileSizeBytes)
				return null;

			return new ProjectRootFileSignature(
				linkInfo.LastWriteTimeUtc.Ticks,
				linkInfo.Length,
				LinkTarget: string.Empty,
				ComputeContentFingerprint(linkInfo.FullName));
		}
		catch (Exception exception) when (exception is
		       IOException or
		       UnauthorizedAccessException or
		       System.Security.SecurityException or
		       NotSupportedException or
		       ArgumentException)
		{
			return null;
		}
	}

	public static bool HasMatchingFileMetadata(
		string filePath,
		ProjectRootFileSignature expectedSignature)
	{
		try
		{
			var linkInfo = new FileInfo(filePath);
			if (!linkInfo.Exists)
				return false;

			if (linkInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
			    !string.IsNullOrEmpty(linkInfo.LinkTarget))
				return false;

			return expectedSignature.LinkTarget.Length == 0 &&
			       linkInfo.LastWriteTimeUtc.Ticks == expectedSignature.LastWriteTicksUtc &&
			       linkInfo.Length == expectedSignature.LengthBytes;
		}
		catch (Exception exception) when (exception is
		       IOException or
		       UnauthorizedAccessException or
		       System.Security.SecurityException or
		       NotSupportedException or
		       ArgumentException)
		{
			return false;
		}
	}

	private static string ComputeContentFingerprint(string filePath)
	{
		using var stream = new FileStream(
			filePath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.ReadWrite | FileShare.Delete,
			bufferSize: 4096,
			FileOptions.SequentialScan);
		return Convert.ToHexString(SHA256.HashData(stream));
	}

	private static bool TryNormalizePath(string path, out string normalizedPath)
	{
		try
		{
			normalizedPath = PathUtility.Normalize(path);
			return true;
		}
		catch
		{
			normalizedPath = string.Empty;
			return false;
		}
	}

	private static bool IsSameOrDescendantPath(string candidatePath, string rootPath)
		=> PathUtility.IsPathInside(candidatePath, rootPath);

	private readonly record struct ProjectRootEntry(
		string Name,
		string FullPath,
		bool IsDirectory,
		bool IsReparsePoint);

	private sealed record CacheEntry(string Path, DateTime CachedAtUtc, ProjectRootFacts Facts);
}
