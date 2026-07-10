using System.IO.Enumeration;

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
	private readonly Dictionary<string, CacheEntry> _cache = new(PathComparer.Default);
	private readonly TimeSpan _cacheTtl;
	private readonly int _cacheLimit;
	private readonly Func<DateTime> _utcNowProvider;

	public ProjectRootFactsProvider(
		TimeSpan? cacheTtl = null,
		int cacheLimit = DefaultCacheLimit,
		Func<DateTime>? utcNowProvider = null)
	{
		_cacheTtl = cacheTtl ?? DefaultCacheTtl;
		_cacheLimit = Math.Max(0, cacheLimit);
		_utcNowProvider = utcNowProvider ?? (() => DateTime.UtcNow);
	}

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
					linkInfo.LinkTarget ?? string.Empty);
			}

			return new ProjectRootFileSignature(
				linkInfo.LastWriteTimeUtc.Ticks,
				linkInfo.Length,
				LinkTarget: string.Empty);
		}
		catch
		{
			return null;
		}
	}

	private readonly record struct ProjectRootEntry(
		string Name,
		string FullPath,
		bool IsDirectory,
		bool IsReparsePoint);

	private sealed record CacheEntry(DateTime CachedAtUtc, ProjectRootFacts Facts);
}
