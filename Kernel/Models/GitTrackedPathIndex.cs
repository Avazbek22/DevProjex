using System.Text;

namespace DevProjex.Kernel.Models;

public readonly record struct GitPathComparisonSemantics(
	bool IgnoreCase,
	bool NormalizeUnicode)
{
	public static GitPathComparisonSemantics PlatformDefault { get; } = new(
		IgnoreCase: OperatingSystem.IsWindows(),
		NormalizeUnicode: OperatingSystem.IsMacOS());
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
	{
		ArgumentNullException.ThrowIfNull(trackedPaths);

		RepositoryRootPath = PathUtility.Normalize(repositoryRootPath);
		_relativePathComparer = comparisonSemantics.IgnoreCase
			? StringComparer.OrdinalIgnoreCase
			: StringComparer.Ordinal;
		_relativePathComparison = comparisonSemantics.IgnoreCase
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
		_normalizeUnicode = comparisonSemantics.NormalizeUnicode;
		_comparisonRootPath = NormalizeUnicodeForComparison(RepositoryRootPath);
		_repositoryPathPrefix = Path.EndsInDirectorySeparator(_comparisonRootPath)
			? _comparisonRootPath
			: _comparisonRootPath + Path.DirectorySeparatorChar;
		_trackedPaths = NormalizeSortAndDeduplicate(trackedPaths);
	}

	public string RepositoryRootPath { get; }

	public int Count => _trackedPaths.Length;

	internal bool MatchesRepositoryRoot(string repositoryRootPath)
	{
		try
		{
			var normalizedRootPath = NormalizeUnicodeForComparison(
				PathUtility.Normalize(repositoryRootPath));
			return _relativePathComparer.Equals(normalizedRootPath, _comparisonRootPath);
		}
		catch
		{
			return false;
		}
	}

	internal bool IsPathInsideRepository(string fullPath) =>
		TryGetRelativePath(fullPath, out _);

	public bool Contains(string fullPath)
	{
		if (!TryGetRelativePath(fullPath, out var relativePath))
			return false;

		return Array.BinarySearch(_trackedPaths, relativePath, _relativePathComparer) >= 0;
	}

	public bool HasDescendant(string directoryPath)
	{
		if (!TryGetRelativePath(directoryPath, out var relativePath))
			return false;

		if (relativePath.Length == 0)
			return _trackedPaths.Length > 0;

		var prefix = relativePath + "/";
		var index = FindLowerBound(prefix);
		return index < _trackedPaths.Length &&
		       _trackedPaths[index].StartsWith(prefix, _relativePathComparison);
	}

	public bool ContainsOrHasDescendant(string directoryPath)
	{
		if (!TryGetRelativePath(directoryPath, out var relativePath))
			return false;

		if (relativePath.Length == 0)
			return _trackedPaths.Length > 0;

		var index = Array.BinarySearch(_trackedPaths, relativePath, _relativePathComparer);
		if (index >= 0)
			return true;

		var prefix = relativePath + "/";
		index = FindLowerBound(prefix);
		return index < _trackedPaths.Length &&
		       _trackedPaths[index].StartsWith(prefix, _relativePathComparison);
	}

	private bool TryGetRelativePath(string fullPath, out string relativePath)
	{
		relativePath = string.Empty;
		try
		{
			var normalizedFullPath = NormalizeUnicodeForComparison(PathUtility.Normalize(fullPath));
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
		return NormalizeUnicodeForComparison(normalized);
	}

	private string NormalizeUnicodeForComparison(string value)
	{
		if (!_normalizeUnicode || !ContainsNonAscii(value))
			return value;

		return value.IsNormalized(NormalizationForm.FormC)
			? value
			: value.Normalize(NormalizationForm.FormC);
	}

	private static bool ContainsNonAscii(string value)
	{
		foreach (var character in value)
		{
			if (character > 0x7f)
				return true;
		}

		return false;
	}
}
