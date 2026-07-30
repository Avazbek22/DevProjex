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
		Func<string, string>? validateDestination = null) =>
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
		Func<string, string>? validateDestination = null)
	{
		try
		{
			return await AtomicFileOutput.WriteAsync(
					path,
					overwrite,
					write,
					cancellationToken,
					validateDestination)
				.ConfigureAwait(false);
		}
		catch (AtomicFileOutputConflictException exception)
		{
			throw new OutputDestinationConflictException(exception.Path);
		}
	}
}
