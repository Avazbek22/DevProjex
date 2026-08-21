namespace DevProjex.Application.Services;

public static class ProjectTreePathUtility
{
	public static string GetRelativeDisplayPath(string projectRoot, string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		ArgumentException.ThrowIfNullOrWhiteSpace(path);

		var relativePath = Path.GetRelativePath(projectRoot, path);
		if (relativePath == ".")
			return relativePath;

		return relativePath
			.Replace(Path.DirectorySeparatorChar, '/')
			.Replace(Path.AltDirectorySeparatorChar, '/');
	}
}
