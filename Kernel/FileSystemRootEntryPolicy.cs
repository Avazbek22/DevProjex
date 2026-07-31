namespace DevProjex.Kernel;

public static class FileSystemRootEntryPolicy
{
	public static bool IsPhysicalDirectory(string path)
	{
		try
		{
			return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
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
			return false;
		}
	}
}
