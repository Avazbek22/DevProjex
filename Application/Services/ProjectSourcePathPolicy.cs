namespace DevProjex.Application.Services;

internal static class ProjectSourcePathPolicy
{
	public static FileContentClassification? ClassifyUnavailable(string projectRoot, string path)
	{
		try
		{
			var normalizedRoot = PathUtility.Normalize(projectRoot);
			var normalizedPath = PathUtility.Normalize(path);
			var relativePath = Path.GetRelativePath(normalizedRoot, normalizedPath);
			if (PathUtility.IsRelativePathOutsideRoot(relativePath))
				return FileContentClassification.Unreadable;

			var currentPath = normalizedRoot;
			if (IsReparsePoint(currentPath))
				return FileContentClassification.Unreadable;

			foreach (var segment in relativePath.Split(
			         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
			         StringSplitOptions.RemoveEmptyEntries))
			{
				currentPath = Path.Combine(currentPath, segment);
				if (IsReparsePoint(currentPath))
					return FileContentClassification.Unreadable;
			}

			return null;
		}
		catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
		{
			return FileContentClassification.Missing;
		}
		catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
		{
			return FileContentClassification.AccessDenied;
		}
		catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
		{
			return FileContentClassification.Unreadable;
		}
	}

	private static bool IsReparsePoint(string path) =>
		(File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
