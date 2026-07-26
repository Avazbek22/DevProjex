namespace DevProjex.Kernel.Models;

/// <summary>
/// Immutable Git index projection used only to protect tracked working-tree paths
/// from .gitignore rules. Paths are sorted once so exact and descendant probes do
/// not require a second ancestor set for large repositories.
/// </summary>
public sealed class GitTrackedPathIndex(string repositoryRootPath, IEnumerable<string> trackedPaths)
{
	private static readonly StringComparer RelativePathComparer = PathComparer.Default;
	private static readonly StringComparison RelativePathComparison = PathComparer.Comparison;
	private readonly string[] _trackedPaths = NormalizeSortAndDeduplicate(trackedPaths);

	public string RepositoryRootPath { get; } = PathUtility.Normalize(repositoryRootPath);

	public int Count => _trackedPaths.Length;

	public bool Contains(string fullPath)
	{
		if (!TryGetRelativePath(fullPath, out var relativePath))
			return false;

		return Array.BinarySearch(_trackedPaths, relativePath, RelativePathComparer) >= 0;
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
		       _trackedPaths[index].StartsWith(prefix, RelativePathComparison);
	}

	private bool TryGetRelativePath(string fullPath, out string relativePath)
	{
		relativePath = string.Empty;
		try
		{
			if (!PathUtility.IsPathInside(fullPath, RepositoryRootPath))
				return false;

			var candidate = Path.GetRelativePath(RepositoryRootPath, fullPath);
			if (candidate == ".")
				return true;
			if (Path.IsPathRooted(candidate))
				return false;

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
			if (RelativePathComparer.Compare(_trackedPaths[middle], value) < 0)
				low = middle + 1;
			else
				high = middle;
		}

		return low;
	}

	private static string[] NormalizeSortAndDeduplicate(IEnumerable<string> trackedPaths)
	{
		ArgumentNullException.ThrowIfNull(trackedPaths);

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

		normalizedPaths.Sort(RelativePathComparer);
		var uniqueCount = 1;
		for (var index = 1; index < normalizedPaths.Count; index++)
		{
			if (RelativePathComparer.Equals(normalizedPaths[index], normalizedPaths[uniqueCount - 1]))
				continue;

			normalizedPaths[uniqueCount++] = normalizedPaths[index];
		}

		if (uniqueCount < normalizedPaths.Count)
			normalizedPaths.RemoveRange(uniqueCount, normalizedPaths.Count - uniqueCount);
		return [.. normalizedPaths];
	}

	private static string NormalizeRelativePath(string path) =>
		path.Replace(Path.DirectorySeparatorChar, '/').TrimEnd('/');
}
