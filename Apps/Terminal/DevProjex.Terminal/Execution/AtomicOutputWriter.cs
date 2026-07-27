namespace DevProjex.Terminal.Execution;

internal sealed class OutputDestinationConflictException(string path)
	: IOException("The output destination already exists.")
{
	public string Path { get; } = path;
}

internal static class AtomicOutputWriter
{
	public static async Task<string> WriteTextAsync(
		string path,
		string content,
		bool overwrite,
		CancellationToken cancellationToken,
		Action<string>? validateDestination = null)
	{
		var fullPath = Path.GetFullPath(path);
		cancellationToken.ThrowIfCancellationRequested();
		validateDestination?.Invoke(fullPath);
		var directory = Path.GetDirectoryName(fullPath);
		if (!string.IsNullOrWhiteSpace(directory))
			Directory.CreateDirectory(directory);
		validateDestination?.Invoke(fullPath);
		if (!overwrite && (File.Exists(fullPath) || Directory.Exists(fullPath)))
			throw new OutputDestinationConflictException(fullPath);

		var tempPath = Path.Combine(
			directory ?? Directory.GetCurrentDirectory(),
			$".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
		try
		{
			await File.WriteAllTextAsync(
				tempPath,
				content,
				new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
				cancellationToken).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			validateDestination?.Invoke(fullPath);
			File.Move(tempPath, fullPath, overwrite);
			return fullPath;
		}
		catch (IOException) when (!overwrite && (File.Exists(fullPath) || Directory.Exists(fullPath)))
		{
			throw new OutputDestinationConflictException(fullPath);
		}
		finally
		{
			try
			{
				if (File.Exists(tempPath))
					File.Delete(tempPath);
			}
			catch
			{
				// The next write uses a new sibling temp path.
			}
		}
	}
}
