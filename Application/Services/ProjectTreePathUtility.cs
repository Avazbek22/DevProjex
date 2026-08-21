namespace DevProjex.Application.Services;

public static class ProjectTreePathUtility
{
	public static string GetRelativeDisplayPath(string projectRoot, string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		ArgumentException.ThrowIfNullOrWhiteSpace(path);

		var relativePath = Path.GetRelativePath(projectRoot, path);
		if (IsOutsideRoot(relativePath))
			throw new ArgumentException("The path is outside the project root.", nameof(path));

		var rootName = ResolveRootName(projectRoot);
		if (relativePath == ".")
			return rootName;

		var separator = rootName[^1] == '/' ? string.Empty : "/";
		return $"{rootName}{separator}{NormalizeSeparators(relativePath)}";
	}

	private static string ResolveRootName(string projectRoot)
	{
		var trimmedRoot = projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var rootName = Path.GetFileName(trimmedRoot);
		if (!string.IsNullOrEmpty(rootName))
			return NormalizeSeparators(rootName);

		var volumeRoot = Path.GetPathRoot(projectRoot);
		if (string.IsNullOrEmpty(volumeRoot))
			throw new ArgumentException("The project root has no displayable name.", nameof(projectRoot));

		var trimmed = volumeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		return NormalizeSeparators(trimmed.Length == 0 ? volumeRoot : trimmed);
	}

	private static bool IsOutsideRoot(string relativePath) =>
		Path.IsPathRooted(relativePath) ||
		relativePath == ".." ||
		relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
		relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);

	private static string NormalizeSeparators(string path) =>
		path
			.Replace(Path.DirectorySeparatorChar, '/')
			.Replace(Path.AltDirectorySeparatorChar, '/');
}
