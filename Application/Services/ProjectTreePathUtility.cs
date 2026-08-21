namespace DevProjex.Application.Services;

public static class ProjectTreePathUtility
{
	public static string GetRelativeDisplayPath(string projectRoot, string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		ArgumentException.ThrowIfNullOrWhiteSpace(path);

		var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(projectRoot));
		if (string.IsNullOrEmpty(rootName))
			rootName = Path.GetPathRoot(projectRoot)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		if (string.IsNullOrEmpty(rootName))
			throw new ArgumentException("The project root has no displayable name.", nameof(projectRoot));

		rootName = NormalizeSeparators(rootName);
		var relativePath = Path.GetRelativePath(projectRoot, path);
		if (relativePath == ".")
			return rootName;

		return $"{rootName}/{NormalizeSeparators(relativePath)}";
	}

	private static string NormalizeSeparators(string path) =>
		path
			.Replace(Path.DirectorySeparatorChar, '/')
			.Replace(Path.AltDirectorySeparatorChar, '/');
}
