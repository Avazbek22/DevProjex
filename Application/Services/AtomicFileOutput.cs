using System.Runtime.ExceptionServices;

namespace DevProjex.Application.Services;

public sealed class AtomicFileOutputConflictException(string path)
	: IOException("The output destination already exists.")
{
	public string Path { get; } = path;
}

public sealed class AtomicFileOutputCleanupException(
	string outputPath,
	string temporaryPath,
	Exception? operationException,
	Exception cleanupException)
	: IOException(
		"The temporary output could not be removed after the atomic write.",
		operationException is null
			? cleanupException
			: new AggregateException(operationException, cleanupException))
{
	public string OutputPath { get; } = outputPath;
	public string TemporaryPath { get; } = temporaryPath;
	public Exception? OperationException { get; } = operationException;
	public Exception CleanupException { get; } = cleanupException;
}

public static class ExactFileOutputDestinationPolicy
{
	public static string Resolve(
		string sourceRoot,
		string destination,
		bool overwrite)
	{
		var fullPath = Path.GetFullPath(destination);
		var resolvedPath = ProjectCopyExportService.ResolveDestinationOutsideProject(
			sourceRoot,
			fullPath);
		if (Directory.Exists(fullPath) ||
		    Directory.Exists(resolvedPath) ||
		    (!overwrite &&
		     (AtomicFileCommit.DestinationEntryExists(fullPath) ||
		      AtomicFileCommit.DestinationEntryExists(resolvedPath))))
		{
			throw new AtomicFileOutputConflictException(fullPath);
		}

		return resolvedPath;
	}
}

public static class AtomicFileOutput
{
	private const int CleanupAttemptCount = 4;
	private const int CleanupInitialDelayMilliseconds = 50;

	public static async Task<string> WriteAsync(
		string path,
		bool overwrite,
		Func<Stream, CancellationToken, Task> write,
		CancellationToken cancellationToken,
		Func<string, string>? validateDestination = null)
	{
		ArgumentNullException.ThrowIfNull(write);
		var requestedPath = Path.GetFullPath(path);
		var fullPath = requestedPath;
		cancellationToken.ThrowIfCancellationRequested();
		fullPath = validateDestination?.Invoke(fullPath) ?? fullPath;
		var directory = Path.GetDirectoryName(fullPath);
		if (string.IsNullOrWhiteSpace(directory) ||
		    !Directory.Exists(directory))
		{
			throw new DirectoryNotFoundException(
				"The output destination parent directory does not exist.");
		}
		try
		{
			RevalidateResolvedPath(fullPath, validateDestination);
			if (!overwrite && AtomicFileCommit.DestinationEntryExists(fullPath))
				throw new AtomicFileOutputConflictException(fullPath);
		}
		catch (AtomicFileOutputConflictException exception)
		{
			throw new AtomicFileOutputConflictException(
				ProjectCopyExportService.ResolveReportedDestinationPath(
					requestedPath,
					exception.Path));
		}

		var tempPath = Path.Combine(
			directory,
			$".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
		Exception? operationException = null;
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
			RevalidateResolvedPath(fullPath, validateDestination);
			cancellationToken.ThrowIfCancellationRequested();
			AtomicFileCommit.Commit(tempPath, fullPath, overwrite);
		}
		catch (Exception exception) when (
			(exception is IOException or UnauthorizedAccessException) &&
			CommitFailureIsDestinationConflict(fullPath, overwrite))
		{
			operationException = new AtomicFileOutputConflictException(
				ProjectCopyExportService.ResolveReportedDestinationPath(
					requestedPath,
					fullPath));
		}
		catch (Exception exception)
		{
			operationException = exception;
		}

		var cleanupException = await TryDeleteTemporaryFileAsync(tempPath)
			.ConfigureAwait(false);
		if (cleanupException is not null)
		{
			throw new AtomicFileOutputCleanupException(
				fullPath,
				tempPath,
				operationException,
				cleanupException);
		}

		if (operationException is not null)
			ExceptionDispatchInfo.Capture(operationException).Throw();

		return PathComparer.Default.Equals(requestedPath, fullPath)
			? requestedPath
			: ProjectCopyExportService.ResolveReportedDestinationPath(
				requestedPath,
				fullPath);
	}

	private static bool CommitFailureIsDestinationConflict(
		string destinationPath,
		bool overwrite)
	{
		if (!overwrite)
			return AtomicFileCommit.DestinationEntryExists(destinationPath);

		return Directory.Exists(destinationPath);
	}

	private static void RevalidateResolvedPath(
		string resolvedPath,
		Func<string, string>? validateDestination)
	{
		if (validateDestination is null)
			return;

		var currentResolvedPath = validateDestination(resolvedPath);
		if (!PathComparer.Default.Equals(currentResolvedPath, resolvedPath))
		{
			throw new IOException(
				"The resolved output destination changed during the operation.");
		}
	}

	private static async Task<Exception?> TryDeleteTemporaryFileAsync(string path)
	{
		for (var attempt = 1; attempt <= CleanupAttemptCount; attempt++)
		{
			try
			{
				File.Delete(path);
				return null;
			}
			catch (Exception exception) when (
				exception is IOException or UnauthorizedAccessException)
			{
				if (attempt == CleanupAttemptCount)
					return exception;

				// Windows scanners can briefly retain a closed staging file handle.
				await Task.Delay(CleanupInitialDelayMilliseconds * attempt)
					.ConfigureAwait(false);
			}
		}

		return null;
	}
}
