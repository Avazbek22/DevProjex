namespace DevProjex.Kernel.Models;

public readonly record struct GitPathComparisonSemantics(
	bool IgnoreCase,
	bool NormalizeUnicode,
	bool IsAuthoritative = true)
{
	public static GitPathComparisonSemantics PlatformDefault { get; } = new(
		IgnoreCase: OperatingSystem.IsWindows(),
		NormalizeUnicode: OperatingSystem.IsMacOS(),
		IsAuthoritative: true);
}

/// <summary>
/// Immutable Git index projection shared by .gitignore overrides and tracked-only
/// filtering. Paths are sorted once so exact and descendant probes do not require
/// a second ancestor set for large repositories.
/// </summary>
public sealed class GitTrackedPathIndex
{
	private readonly StringComparer _relativePathComparer;
	private readonly StringComparison _relativePathComparison;
	private readonly bool _normalizeUnicode;
	private readonly bool _ignoreAsciiCase;
	private readonly string _comparisonRootPath;
	private readonly string _repositoryPathPrefix;
	private readonly string[] _trackedPaths;

	public GitTrackedPathIndex(string repositoryRootPath, IEnumerable<string> trackedPaths)
		: this(repositoryRootPath, trackedPaths, GitPathComparisonSemantics.PlatformDefault)
	{
	}

	public GitTrackedPathIndex(
		string repositoryRootPath,
		IEnumerable<string> trackedPaths,
		GitPathComparisonSemantics comparisonSemantics)
		: this(repositoryRootPath, trackedPaths, comparisonSemantics, isAvailable: true)
	{
	}

	private GitTrackedPathIndex(
		string repositoryRootPath,
		IEnumerable<string> trackedPaths,
		GitPathComparisonSemantics comparisonSemantics,
		bool isAvailable)
	{
		ArgumentNullException.ThrowIfNull(trackedPaths);

		RepositoryRootPath = PathUtility.Normalize(repositoryRootPath);
		IsAvailable = isAvailable;
		_relativePathComparer = StringComparer.Ordinal;
		_relativePathComparison = StringComparison.Ordinal;
		_normalizeUnicode = comparisonSemantics.NormalizeUnicode;
		_ignoreAsciiCase = comparisonSemantics.IgnoreCase;
		_comparisonRootPath = NormalizeForComparison(RepositoryRootPath);
		_repositoryPathPrefix = Path.EndsInDirectorySeparator(_comparisonRootPath)
			? _comparisonRootPath
			: _comparisonRootPath + Path.DirectorySeparatorChar;
		_trackedPaths = NormalizeSortAndDeduplicate(trackedPaths);
	}

	public string RepositoryRootPath { get; }

	public int Count => _trackedPaths.Length;
	public bool IsAvailable { get; }

	public static GitTrackedPathIndex Unavailable(string repositoryRootPath) =>
		new(
			repositoryRootPath,
			[],
			GitPathComparisonSemantics.PlatformDefault,
			isAvailable: false);

	internal bool MatchesRepositoryRoot(string repositoryRootPath)
	{
		try
		{
			var normalizedRootPath = NormalizeForComparison(
				PathUtility.Normalize(repositoryRootPath));
			return _relativePathComparer.Equals(normalizedRootPath, _comparisonRootPath);
		}
		catch
		{
			return false;
		}
	}

	public bool Contains(string fullPath)
	{
		if (!TryGetNormalizedRelativePath(fullPath, out var relativePath))
			return false;

		return ContainsNormalizedRelativePath(relativePath);
	}

	public bool HasDescendant(string directoryPath)
	{
		if (!TryGetNormalizedRelativePath(directoryPath, out var relativePath))
			return false;

		return HasDescendantNormalizedRelativePath(relativePath);
	}

	public bool ContainsOrHasDescendant(string directoryPath)
	{
		if (!TryGetNormalizedRelativePath(directoryPath, out var relativePath))
			return false;

		return ContainsOrHasDescendantNormalizedRelativePath(relativePath);
	}

	// Scan contexts use this once to establish repository ownership. The returned key
	// is the only form accepted by the internal probes below; callers must not pass raw paths.
	internal bool TryGetNormalizedRelativePath(string fullPath, out string relativePath)
	{
		relativePath = string.Empty;
		try
		{
			var normalizedFullPath = NormalizeForComparison(PathUtility.Normalize(fullPath));
			if (_relativePathComparer.Equals(normalizedFullPath, _comparisonRootPath))
				return true;
			if (!normalizedFullPath.StartsWith(_repositoryPathPrefix, _relativePathComparison) ||
			    normalizedFullPath.Length <= _repositoryPathPrefix.Length)
			{
				return false;
			}

			var candidate = normalizedFullPath[_repositoryPathPrefix.Length..];
			relativePath = NormalizeRelativePath(candidate);
			return relativePath.Length > 0 &&
			       relativePath != ".." &&
			       !relativePath.StartsWith("../", StringComparison.Ordinal);
		}
		catch
		{
			relativePath = string.Empty;
			return false;
		}
	}

	internal bool ContainsNormalizedRelativePath(string relativePath) =>
		Array.BinarySearch(_trackedPaths, relativePath, _relativePathComparer) >= 0;

	internal bool HasDescendantNormalizedRelativePath(string relativePath)
	{
		if (relativePath.Length == 0)
			return _trackedPaths.Length > 0;

		var prefix = relativePath + "/";
		var index = FindLowerBound(prefix);
		return index < _trackedPaths.Length &&
		       _trackedPaths[index].StartsWith(prefix, _relativePathComparison);
	}

	internal bool ContainsOrHasDescendantNormalizedRelativePath(string relativePath)
	{
		if (relativePath.Length == 0)
			return _trackedPaths.Length > 0;

		return ContainsNormalizedRelativePath(relativePath) ||
		       HasDescendantNormalizedRelativePath(relativePath);
	}

	private int FindLowerBound(string value)
	{
		var low = 0;
		var high = _trackedPaths.Length;
		while (low < high)
		{
			var middle = low + ((high - low) >> 1);
			if (_relativePathComparer.Compare(_trackedPaths[middle], value) < 0)
				low = middle + 1;
			else
				high = middle;
		}

		return low;
	}

	private string[] NormalizeSortAndDeduplicate(IEnumerable<string> trackedPaths)
	{
		var normalizedPaths = new List<string>();
		foreach (var trackedPath in trackedPaths)
		{
			if (string.IsNullOrEmpty(trackedPath))
				continue;

			if (Path.IsPathRooted(trackedPath))
				continue;

			var normalized = NormalizeRelativePath(trackedPath);
			if (normalized.Length == 0 ||
			    normalized == "." ||
			    normalized == ".." ||
			    normalized.StartsWith("../", StringComparison.Ordinal))
			{
				continue;
			}

			normalizedPaths.Add(normalized);
		}

		if (normalizedPaths.Count == 0)
			return [];

		normalizedPaths.Sort(_relativePathComparer);
		var uniqueCount = 1;
		for (var index = 1; index < normalizedPaths.Count; index++)
		{
			if (_relativePathComparer.Equals(normalizedPaths[index], normalizedPaths[uniqueCount - 1]))
				continue;

			normalizedPaths[uniqueCount++] = normalizedPaths[index];
		}

		if (uniqueCount < normalizedPaths.Count)
			normalizedPaths.RemoveRange(uniqueCount, normalizedPaths.Count - uniqueCount);
		return [.. normalizedPaths];
	}

	private string NormalizeRelativePath(string path)
	{
		var normalized = path.Replace(Path.DirectorySeparatorChar, '/').TrimEnd('/');
		return NormalizeForComparison(normalized);
	}

	private string NormalizeForComparison(string value) =>
		GitPathTextNormalizer.NormalizeObservedPath(
			value,
			_normalizeUnicode,
			_ignoreAsciiCase);
}
