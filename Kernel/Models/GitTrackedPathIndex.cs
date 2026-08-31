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
/// filtering. Exact and Git-compatible paths are sorted once; compatibility aliases
/// are accepted only when the current working tree resolves them unambiguously.
/// </summary>
public sealed class GitTrackedPathIndex
{
	private readonly StringComparer _relativePathComparer;
	private readonly StringComparison _relativePathComparison;
	private readonly bool _normalizeUnicode;
	private readonly bool _ignoreAsciiCase;
	private readonly string _repositoryPathPrefix;
	private readonly string[] _trackedPaths;
	private readonly string[] _compatibleTrackedPaths;
	private readonly IReadOnlySet<string> _ambiguousCompatiblePaths;

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
		_repositoryPathPrefix = Path.EndsInDirectorySeparator(RepositoryRootPath)
			? RepositoryRootPath
			: RepositoryRootPath + Path.DirectorySeparatorChar;
		_trackedPaths = NormalizeSortAndDeduplicateExactPaths(trackedPaths);
		_compatibleTrackedPaths = BuildCompatiblePathIndex(
			_trackedPaths,
			out _ambiguousCompatiblePaths);
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
			var normalizedRepositoryRoot = PathUtility.Normalize(repositoryRootPath);
			return StringComparer.Ordinal.Equals(normalizedRepositoryRoot, RepositoryRootPath) ||
			       AreUniqueWindowsAliases(normalizedRepositoryRoot, RepositoryRootPath);
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

	public bool OwnsPath(string fullPath) =>
		TryGetNormalizedRelativePath(fullPath, out _);

	public bool TryGetPathIdentity(string fullPath, out string relativePath)
	{
		if (!TryGetNormalizedRelativePath(fullPath, out var exactRelativePath))
		{
			relativePath = string.Empty;
			return false;
		}

		var compatibleRelativePath = NormalizeForComparison(exactRelativePath);
		var containsExactPath = ContainsExactPath(exactRelativePath);
		var usesCompatibleIdentity = containsExactPath
			? !_ambiguousCompatiblePaths.Contains(compatibleRelativePath)
			: HasCompatibleExactPath(compatibleRelativePath) &&
			  IsUniqueExistingCompatiblePath(exactRelativePath, requireDirectory: false);
		relativePath = usesCompatibleIdentity
			? "compatible\0" + compatibleRelativePath
			: "exact\0" + exactRelativePath;
		return true;
	}

	// Repository ownership is exact unless Windows resolves both spellings to one
	// unambiguous physical directory. Git semantics apply only to the relative path.
	internal bool TryGetNormalizedRelativePath(string fullPath, out string relativePath)
	{
		relativePath = string.Empty;
		try
		{
			var normalizedFullPath = PathUtility.Normalize(fullPath);
			if (StringComparer.Ordinal.Equals(normalizedFullPath, RepositoryRootPath))
				return true;

			string candidate;
			if (normalizedFullPath.StartsWith(_repositoryPathPrefix, StringComparison.Ordinal) &&
			    normalizedFullPath.Length > _repositoryPathPrefix.Length)
			{
				candidate = normalizedFullPath[_repositoryPathPrefix.Length..];
			}
			else if (!TryGetWindowsAliasRelativePath(normalizedFullPath, out candidate))
			{
				return false;
			}
			if (candidate.Length == 0)
				return true;

			relativePath = NormalizeExactRelativePath(candidate);
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

	internal bool ContainsNormalizedRelativePath(string relativePath)
	{
		var exactRelativePath = NormalizeExactRelativePath(relativePath);
		if (ContainsExactPath(exactRelativePath))
			return true;

		var compatibleRelativePath = NormalizeForComparison(exactRelativePath);
		return HasCompatibleExactPath(compatibleRelativePath) &&
		       IsUniqueExistingCompatiblePath(exactRelativePath, requireDirectory: false);
	}

	internal bool HasDescendantNormalizedRelativePath(string relativePath)
	{
		var exactRelativePath = NormalizeExactRelativePath(relativePath);
		if (exactRelativePath.Length == 0)
			return _trackedPaths.Length > 0;

		if (HasExactDescendant(exactRelativePath))
			return true;

		var compatibleRelativePath = NormalizeForComparison(exactRelativePath);
		return HasCompatibleDescendant(compatibleRelativePath) &&
		       IsUniqueExistingCompatiblePath(exactRelativePath, requireDirectory: true);
	}

	internal bool ContainsOrHasDescendantNormalizedRelativePath(string relativePath)
	{
		var exactRelativePath = NormalizeExactRelativePath(relativePath);
		if (exactRelativePath.Length == 0)
			return _trackedPaths.Length > 0;

		if (ContainsExactPath(exactRelativePath) || HasExactDescendant(exactRelativePath))
			return true;

		var compatibleRelativePath = NormalizeForComparison(exactRelativePath);
		if (!HasCompatibleExactPath(compatibleRelativePath) &&
		    !HasCompatibleDescendant(compatibleRelativePath))
		{
			return false;
		}

		return IsUniqueExistingCompatiblePath(exactRelativePath, requireDirectory: null);
	}

	private bool ContainsExactPath(string relativePath) =>
		Array.BinarySearch(_trackedPaths, relativePath, _relativePathComparer) >= 0;

	private bool HasExactDescendant(string relativePath) =>
		HasDescendant(_trackedPaths, relativePath);

	private bool HasCompatibleExactPath(string relativePath) =>
		Array.BinarySearch(_compatibleTrackedPaths, relativePath, _relativePathComparer) >= 0;

	private bool HasCompatibleDescendant(string relativePath) =>
		HasDescendant(_compatibleTrackedPaths, relativePath);

	private bool HasDescendant(string[] paths, string relativePath)
	{
		var prefix = relativePath + "/";
		var index = FindLowerBound(paths, prefix);
		return index < paths.Length && paths[index].StartsWith(prefix, _relativePathComparison);
	}

	private int FindLowerBound(string[] paths, string value)
	{
		var low = 0;
		var high = paths.Length;
		while (low < high)
		{
			var middle = low + ((high - low) >> 1);
			if (_relativePathComparer.Compare(paths[middle], value) < 0)
				low = middle + 1;
			else
				high = middle;
		}

		return low;
	}

	private string[] NormalizeSortAndDeduplicateExactPaths(IEnumerable<string> trackedPaths)
	{
		var normalizedPaths = new List<string>();
		foreach (var trackedPath in trackedPaths)
		{
			if (string.IsNullOrEmpty(trackedPath))
				continue;

			if (Path.IsPathRooted(trackedPath))
				continue;

			var normalized = NormalizeExactRelativePath(trackedPath);
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

	private string[] BuildCompatiblePathIndex(
		IReadOnlyList<string> trackedPaths,
		out IReadOnlySet<string> ambiguousPaths)
	{
		if (!_ignoreAsciiCase && !_normalizeUnicode)
		{
			ambiguousPaths = new HashSet<string>(StringComparer.Ordinal);
			return trackedPaths as string[] ?? [.. trackedPaths];
		}

		var compatiblePaths = new string[trackedPaths.Count];
		for (var index = 0; index < trackedPaths.Count; index++)
			compatiblePaths[index] = NormalizeForComparison(trackedPaths[index]);
		Array.Sort(compatiblePaths, _relativePathComparer);

		var ambiguous = new HashSet<string>(StringComparer.Ordinal);
		var uniqueCount = compatiblePaths.Length == 0 ? 0 : 1;
		for (var index = 1; index < compatiblePaths.Length; index++)
		{
			if (_relativePathComparer.Equals(compatiblePaths[index], compatiblePaths[uniqueCount - 1]))
			{
				ambiguous.Add(compatiblePaths[index]);
				continue;
			}

			compatiblePaths[uniqueCount++] = compatiblePaths[index];
		}
		ambiguousPaths = ambiguous;
		return uniqueCount == compatiblePaths.Length
			? compatiblePaths
			: compatiblePaths[..uniqueCount];
	}

	private bool IsUniqueExistingCompatiblePath(string relativePath, bool? requireDirectory)
	{
		if (!_ignoreAsciiCase && !_normalizeUnicode)
			return false;

		try
		{
			var currentPath = RepositoryRootPath;
			var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
			if (segments.Length == 0)
				return false;

			for (var segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
			{
				var compatibleSegment = NormalizeForComparison(segments[segmentIndex]);
				string? match = null;
				foreach (var entry in Directory.EnumerateFileSystemEntries(currentPath))
				{
					var name = Path.GetFileName(entry);
					if (!StringComparer.Ordinal.Equals(NormalizeForComparison(name), compatibleSegment))
						continue;
					if (match is not null)
						return false;

					match = entry;
				}

				if (match is null)
					return false;
				currentPath = match;
				if (segmentIndex < segments.Length - 1 && !Directory.Exists(currentPath))
					return false;
			}

			return requireDirectory switch
			{
				true => Directory.Exists(currentPath),
				false => File.Exists(currentPath),
				null => File.Exists(currentPath) || Directory.Exists(currentPath)
			};
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			return false;
		}
	}

	private bool TryGetWindowsAliasRelativePath(string fullPath, out string relativePath)
	{
		relativePath = string.Empty;
		if (!OperatingSystem.IsWindows() || fullPath.Length < RepositoryRootPath.Length)
			return false;

		var repositoryRootEndsWithSeparator = Path.EndsInDirectorySeparator(RepositoryRootPath);
		if (fullPath.Length > RepositoryRootPath.Length &&
		    !repositoryRootEndsWithSeparator &&
		    !IsDirectorySeparator(fullPath[RepositoryRootPath.Length]))
		{
			return false;
		}

		var rootCandidate = fullPath[..RepositoryRootPath.Length];
		if (!rootCandidate.Equals(RepositoryRootPath, StringComparison.OrdinalIgnoreCase) ||
		    !AreUniqueWindowsAliases(rootCandidate, RepositoryRootPath))
		{
			return false;
		}

		if (fullPath.Length == RepositoryRootPath.Length)
			return true;

		var relativeOffset = RepositoryRootPath.Length + (repositoryRootEndsWithSeparator ? 0 : 1);
		if (fullPath.Length <= relativeOffset)
			return false;

		relativePath = fullPath[relativeOffset..];
		return true;
	}

	private static bool AreUniqueWindowsAliases(string leftPath, string rightPath)
	{
		if (!OperatingSystem.IsWindows() ||
		    !TryResolveUniqueWindowsPath(leftPath, out var leftRoot, out var leftSegments) ||
		    !TryResolveUniqueWindowsPath(rightPath, out var rightRoot, out var rightSegments) ||
		    !leftRoot.Equals(rightRoot, StringComparison.OrdinalIgnoreCase) ||
		    leftSegments.Length != rightSegments.Length)
		{
			return false;
		}

		for (var index = 0; index < leftSegments.Length; index++)
		{
			if (!StringComparer.Ordinal.Equals(leftSegments[index], rightSegments[index]))
				return false;
		}

		return true;
	}

	private static bool TryResolveUniqueWindowsPath(
		string path,
		out string rootPath,
		out string[] resolvedSegments)
	{
		rootPath = Path.GetPathRoot(path) ?? string.Empty;
		resolvedSegments = [];
		if (rootPath.Length == 0 || !Directory.Exists(rootPath))
			return false;

		try
		{
			var relativePath = path[rootPath.Length..];
			var requestedSegments = relativePath.Split(
				[Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
				StringSplitOptions.RemoveEmptyEntries);
			resolvedSegments = new string[requestedSegments.Length];
			var currentPath = rootPath;
			for (var index = 0; index < requestedSegments.Length; index++)
			{
				if (!TryFindUniqueWindowsCompatibleEntry(
					    Directory.EnumerateFileSystemEntries(currentPath),
					    requestedSegments[index],
					    out var match) ||
				    !Directory.Exists(match))
				{
					resolvedSegments = [];
					return false;
				}

				resolvedSegments[index] = Path.GetFileName(match);
				currentPath = match;
			}

			return true;
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			resolvedSegments = [];
			return false;
		}
	}

	internal static bool TryFindUniqueWindowsCompatibleEntry(
		IEnumerable<string> entries,
		string requestedName,
		out string match)
	{
		match = string.Empty;
		foreach (var entry in entries)
		{
			var name = Path.GetFileName(entry);
			if (name.Equals(requestedName, StringComparison.Ordinal))
			{
				match = entry;
				return true;
			}
			if (!name.Equals(requestedName, StringComparison.OrdinalIgnoreCase))
				continue;
			if (match.Length > 0)
			{
				match = string.Empty;
				return false;
			}

			match = entry;
		}

		return match.Length > 0;
	}

	private static bool IsDirectorySeparator(char value) =>
		value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;

	private static string NormalizeExactRelativePath(string path)
	{
		var normalized = path.Replace(Path.DirectorySeparatorChar, '/').TrimEnd('/');
		return normalized;
	}

	private string NormalizeForComparison(string value) =>
		GitPathTextNormalizer.NormalizeObservedPath(
			value,
			_normalizeUnicode,
			_ignoreAsciiCase);
}
