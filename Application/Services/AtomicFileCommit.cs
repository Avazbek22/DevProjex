namespace DevProjex.Application.Services;

internal static class AtomicFileCommit
{
	public static void Commit(
		string temporaryPath,
		string destinationPath,
		bool overwrite)
	{
		if (!overwrite || !File.Exists(destinationPath))
		{
			File.Move(temporaryPath, destinationPath, overwrite);
			return;
		}

		if (DestinationIsSymbolicLink(destinationPath))
		{
			File.Move(temporaryPath, destinationPath, overwrite: true);
			return;
		}

		try
		{
			File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null);
		}
		catch (FileNotFoundException) when (!File.Exists(destinationPath))
		{
			File.Move(temporaryPath, destinationPath, overwrite: true);
		}
		catch (NotSupportedException)
		{
			File.Move(temporaryPath, destinationPath, overwrite: true);
		}
	}

	public static bool DestinationIsSymbolicLink(string path)
	{
		try
		{
			return new FileInfo(path).LinkTarget is not null ||
			       new DirectoryInfo(path).LinkTarget is not null;
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}
}
