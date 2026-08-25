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

	public static string NormalizeSeparators(string path)
	{
		ArgumentNullException.ThrowIfNull(path);
		return OperatingSystem.IsWindows()
			? path.Replace('\\', '/')
			: path;
	}

	public static string GetPortableRelativePath(string rootPath, string path) =>
		NormalizeSeparators(Path.GetRelativePath(rootPath, path));

	public static bool IsRelativePathOutsideRoot(string relativePath)
	{
		ArgumentNullException.ThrowIfNull(relativePath);
		if (Path.IsPathRooted(relativePath) || relativePath.Equals("..", StringComparison.Ordinal))
			return true;

		if (relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
			return true;

		return Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar &&
		       relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
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
			return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

		if (PathComparer.Default.Equals(path, root))
			return path;

		return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
	}
}
