namespace DevProjex.Terminal.Execution;

internal sealed class OutputDestinationConflictException(string path)
	: IOException("The output destination already exists.")
{
	public string Path { get; } = path;
}

internal static class AtomicOutputWriter
{
	public static Task<string> WriteTextAsync(
		string path,
		string content,
		bool overwrite,
		CancellationToken cancellationToken,
		Action<string>? validateDestination = null) =>
		WriteAsync(
			path,
			overwrite,
			async (destination, token) =>
			{
				await using var writer = new StreamWriter(
					destination,
					new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
					bufferSize: 16 * 1024,
					leaveOpen: true);
				await writer.WriteAsync(content.AsMemory(), token).ConfigureAwait(false);
				await writer.FlushAsync(token).ConfigureAwait(false);
			},
			cancellationToken,
			validateDestination);

	public static async Task<string> WriteAsync(
		string path,
		bool overwrite,
		Func<Stream, CancellationToken, Task> write,
		CancellationToken cancellationToken,
		Action<string>? validateDestination = null)
	{
		ArgumentNullException.ThrowIfNull(write);
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
			await using (var destination = new FileStream(
				             tempPath,
				             FileMode.CreateNew,
				             FileAccess.Write,
				             FileShare.None,
				             bufferSize: 64 * 1024,
				             FileOptions.Asynchronous | FileOptions.SequentialScan))
			{
				await write(destination, cancellationToken).ConfigureAwait(false);
				await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
			}
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
			catch (Exception exception) when (
				exception is IOException or UnauthorizedAccessException)
			{
				// Preserve the primary write/cancellation failure. A later invocation
				// uses a unique sibling staging path and never treats this file as output.
			}
		}
	}
}
