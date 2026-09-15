namespace DevProjex.Kernel;

public static class FileSystemRootEntryPolicy
{
	public static bool IsPhysicalDirectory(string path) =>
		TryGetAttributes(path, out var attributes) &&
		(attributes & FileAttributes.ReparsePoint) == 0;

	public static bool IsReparsePoint(string path) =>
		TryGetAttributes(path, out var attributes) &&
		(attributes & FileAttributes.ReparsePoint) != 0;

	private static bool TryGetAttributes(string path, out FileAttributes attributes)
	{
		try
		{
			attributes = File.GetAttributes(path);
			return true;
		}
		catch (Exception exception) when (exception is
		       IOException or
		       UnauthorizedAccessException or
		       System.Security.SecurityException or
		       ArgumentException or
		       NotSupportedException)
		{
			// A root whose identity cannot be established must not be followed. Scan
			// consumers surface this through their existing root-access diagnostics.
			attributes = default;
			return false;
		}
	}
}
