namespace DevProjex.Infrastructure.FileSystem;

internal static class ProjectControlFileIdentityProbe
{
	public static ProjectControlFileIdentity Missing(string path) =>
		new(PathUtility.Normalize(path), Exists: false, 0, 0);

	public static ProjectControlFileIdentity Capture(string path)
	{
		var normalizedPath = PathUtility.Normalize(path);
		try
		{
			var file = new FileInfo(normalizedPath);
			file.Refresh();
			return file.Exists
				? new ProjectControlFileIdentity(
					normalizedPath,
					Exists: true,
					file.Length,
					file.LastWriteTimeUtc.Ticks)
				: Missing(normalizedPath);
		}
		catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
		{
			return Missing(normalizedPath);
		}
	}
}
