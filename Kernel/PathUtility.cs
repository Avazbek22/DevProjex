namespace DevProjex.Kernel;

public static class PathUtility
{
	// Real filesystem paths must follow one conservative comparison policy.
	// We intentionally avoid assuming case-insensitive semantics outside Windows.
	public static StringComparison DefaultComparison => OperatingSystem.IsWindows()
		? StringComparison.OrdinalIgnoreCase
		: StringComparison.Ordinal;

	public static string Normalize(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return path;

		var fullPath = Path.GetFullPath(path);
		return TrimTrailingSeparators(fullPath);
	}

	public static string NormalizeForCacheKey(string path)
	{
		var normalized = Normalize(path);
		return OperatingSystem.IsWindows()
			? normalized.ToUpperInvariant()
			: normalized;
	}

	public static bool IsPathInside(string path, string rootPath)
	{
		if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(rootPath))
			return false;

		var normalizedPath = Normalize(path);
		var normalizedRootPath = Normalize(rootPath);

		if (string.Equals(normalizedPath, normalizedRootPath, DefaultComparison))
			return true;

		if (!normalizedPath.StartsWith(normalizedRootPath, DefaultComparison))
			return false;

		if (normalizedPath.Length <= normalizedRootPath.Length)
			return false;
		if (IsDirectorySeparator(normalizedRootPath[^1]))
			return true;

		var next = normalizedPath[normalizedRootPath.Length];
		return IsDirectorySeparator(next);
	}

	private static bool IsDirectorySeparator(char value) =>
		value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;

	private static string TrimTrailingSeparators(string path)
	{
		var root = Path.GetPathRoot(path);
		if (string.IsNullOrEmpty(root))
			return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '\\');

		if (PathComparer.Default.Equals(path, root))
			return path;

		// Normalize legacy persisted paths that may contain the other platform's separator.
		return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '\\');
	}
}
