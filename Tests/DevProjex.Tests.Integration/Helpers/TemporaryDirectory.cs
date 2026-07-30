namespace DevProjex.Tests.Integration.Helpers;

internal sealed class TemporaryDirectory : IDisposable
{
	public TemporaryDirectory(string? baseDirectory = null)
	{
		var root = string.IsNullOrWhiteSpace(baseDirectory)
			? System.IO.Path.GetTempPath()
			: System.IO.Path.GetFullPath(baseDirectory);
		Path = System.IO.Path.Combine(
			root,
			"DevProjex",
			"Tests",
			"Temp",
			Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Path);
	}

	public string Path { get; }

	public string CreateFile(string relativePath, string content)
	{
		var fullPath = System.IO.Path.Combine(Path, relativePath);
		var dir = System.IO.Path.GetDirectoryName(fullPath);
		if (!string.IsNullOrWhiteSpace(dir))
			Directory.CreateDirectory(dir);
		File.WriteAllText(fullPath, content);
		return fullPath;
	}

	public string CreateDirectory(string relativePath)
	{
		var fullPath = System.IO.Path.Combine(Path, relativePath);
		Directory.CreateDirectory(fullPath);
		return fullPath;
	}

	public void Dispose()
	{
		if (Directory.Exists(Path))
		{
			try
			{
				Directory.Delete(Path, recursive: true);
			}
			catch (UnauthorizedAccessException)
			{
				// Ignore - files may be locked by Git or other processes
				// OS will clean up temp files eventually
			}
			catch (IOException)
			{
				// Ignore - files may be in use
			}
		}
	}
}
