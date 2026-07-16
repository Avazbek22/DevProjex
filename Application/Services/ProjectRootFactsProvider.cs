using System.IO.Enumeration;
using System.Security.Cryptography;

namespace DevProjex.Application.Services;

public sealed class ProjectRootFactsProvider(
	TimeSpan? cacheTtl = null,
	int cacheLimit = ProjectRootFactsProvider.DefaultCacheLimit,
	Func<DateTime>? utcNowProvider = null)
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
	private readonly Dictionary<string, CacheEntry> _cache = new(PathComparer.Default);
	private readonly TimeSpan _cacheTtl = cacheTtl ?? DefaultCacheTtl;
	private readonly int _cacheLimit = Math.Max(0, cacheLimit);
	private readonly Func<DateTime> _utcNowProvider = utcNowProvider ?? (() => DateTime.UtcNow);

	public ProjectRootFacts Get(string rootPath, bool forceRefresh = false)
	{
		if (string.IsNullOrWhiteSpace(rootPath))
			return ProjectRootFacts.Missing(rootPath);

		string normalizedRootPath;
		try
		{
			normalizedRootPath = Path.GetFullPath(rootPath);
		}
		catch
		{
			return ProjectRootFacts.Missing(rootPath);
		}

		var now = _utcNowProvider();
		if (!forceRefresh && _cacheLimit > 0)
		{
			lock (_cacheSync)
			{
				if (_cache.TryGetValue(normalizedRootPath, out var cached) &&
				    now - cached.CachedAtUtc <= _cacheTtl)
				{
					return cached.Facts;
				}
			}
		}

		var facts = Build(normalizedRootPath);
		if (_cacheLimit > 0)
		{
			lock (_cacheSync)
			{
				_cache[normalizedRootPath] = new CacheEntry(now, facts);
				if (_cache.Count > _cacheLimit)
					_cache.Clear();
			}
		}

		return facts;
	}

	public void Invalidate(string rootPath, bool includeDescendants = false)
	{
		if (!TryNormalizePath(rootPath, out var normalizedRootPath))
			return;

		lock (_cacheSync)
		{
			if (!includeDescendants)
			{
				_cache.Remove(normalizedRootPath);
				return;
			}

			foreach (var cachedPath in _cache.Keys.ToArray())
			{
				if (IsSameOrDescendantPath(cachedPath, normalizedRootPath))
					_cache.Remove(cachedPath);
			}
		}
	}

	internal void RefreshCacheLifetime(IReadOnlyCollection<string> rootPaths)
	{
		if (rootPaths.Count == 0 || _cacheLimit == 0)
			return;

		var now = _utcNowProvider();
		lock (_cacheSync)
		{
			foreach (var rootPath in rootPaths)
			{
				if (_cache.TryGetValue(rootPath, out var cached))
					_cache[rootPath] = cached with { CachedAtUtc = now };
			}
		}
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

				files.Add(new ProjectRootFileFact(entry.Name, Path.GetExtension(entry.Name)));
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
			if (PathComparer.Default.Equals(file.Name, ".gitignore"))
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

			if (linkInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
			{
				var resolvedTarget = linkInfo.ResolveLinkTarget(returnFinalTarget: true);
				if (resolvedTarget is not FileInfo targetInfo || !targetInfo.Exists)
					return null;

				targetInfo.Refresh();
				return new ProjectRootFileSignature(
					targetInfo.LastWriteTimeUtc.Ticks,
					targetInfo.Length,
					linkInfo.LinkTarget ?? string.Empty,
					ComputeContentFingerprint(targetInfo.FullName));
			}

			return new ProjectRootFileSignature(
				linkInfo.LastWriteTimeUtc.Ticks,
				linkInfo.Length,
				LinkTarget: string.Empty,
				ComputeContentFingerprint(linkInfo.FullName));
		}
		catch
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

			if (linkInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
			{
				var resolvedTarget = linkInfo.ResolveLinkTarget(returnFinalTarget: true);
				if (resolvedTarget is not FileInfo targetInfo || !targetInfo.Exists)
					return false;

				targetInfo.Refresh();
				return targetInfo.LastWriteTimeUtc.Ticks == expectedSignature.LastWriteTicksUtc &&
				       targetInfo.Length == expectedSignature.LengthBytes &&
				       string.Equals(
					       linkInfo.LinkTarget ?? string.Empty,
					       expectedSignature.LinkTarget,
					       StringComparison.Ordinal);
			}

			return linkInfo.LastWriteTimeUtc.Ticks == expectedSignature.LastWriteTicksUtc &&
			       linkInfo.Length == expectedSignature.LengthBytes &&
			       expectedSignature.LinkTarget.Length == 0;
		}
		catch
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
			normalizedPath = Path.GetFullPath(path);
			return true;
		}
		catch
		{
			normalizedPath = string.Empty;
			return false;
		}
	}

	private static bool IsSameOrDescendantPath(string candidatePath, string rootPath)
	{
		if (PathComparer.Default.Equals(candidatePath, rootPath))
			return true;
		if (!candidatePath.StartsWith(rootPath, PathComparison))
			return false;

		return candidatePath.Length > rootPath.Length &&
		       IsDirectorySeparator(candidatePath[rootPath.Length]);
	}

	private static bool IsDirectorySeparator(char value) =>
		value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;

	private static StringComparison PathComparison => OperatingSystem.IsWindows()
		? StringComparison.OrdinalIgnoreCase
		: StringComparison.Ordinal;

	private readonly record struct ProjectRootEntry(
		string Name,
		string FullPath,
		bool IsDirectory,
		bool IsReparsePoint);

	private sealed record CacheEntry(DateTime CachedAtUtc, ProjectRootFacts Facts);
}
