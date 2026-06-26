namespace DevProjex.Infrastructure.Reports;

public sealed class ProjectAnalysisReportWriter
{
	private static readonly SemaphoreSlim FileWriteLock = new(1, 1);

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
	};

	public async Task WriteAsync(
		ProjectAnalysisReport report,
		string path,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new ArgumentException("Report path is required.", nameof(path));

		var fullPath = Path.GetFullPath(path);
		var directory = Path.GetDirectoryName(fullPath);
		if (!string.IsNullOrWhiteSpace(directory))
			Directory.CreateDirectory(directory);

		await FileWriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);

		var tempPath = string.Empty;
		try
		{
			tempPath = BuildTemporaryPath(fullPath);
			await using (var stream = new FileStream(
				             tempPath,
				             FileMode.Create,
				             FileAccess.Write,
				             FileShare.None,
				             bufferSize: 16 * 1024,
				             FileOptions.Asynchronous | FileOptions.SequentialScan))
			{
				await JsonSerializer.SerializeAsync(stream, report, JsonOptions, cancellationToken)
					.ConfigureAwait(false);
				await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
			}

			File.Move(tempPath, fullPath, overwrite: true);
		}
		catch
		{
			if (!string.IsNullOrEmpty(tempPath))
				TryDeleteTempFile(tempPath);
			throw;
		}
		finally
		{
			FileWriteLock.Release();
		}
	}

	public async Task WriteAsync(
		ProjectAnalysisReport report,
		TextWriter writer,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(writer);

		var json = JsonSerializer.Serialize(report, JsonOptions);
		await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
	}

	private static string BuildTemporaryPath(string fullPath)
	{
		var directory = Path.GetDirectoryName(fullPath);
		var fileName = Path.GetFileName(fullPath);
		var tempFileName = $".{fileName}.{Guid.NewGuid():N}.tmp";
		return string.IsNullOrWhiteSpace(directory)
			? tempFileName
			: Path.Combine(directory, tempFileName);
	}

	private static void TryDeleteTempFile(string tempPath)
	{
		try
		{
			if (File.Exists(tempPath))
				File.Delete(tempPath);
		}
		catch
		{
			// Best effort cleanup: the next write uses a unique temp file and remains safe.
		}
	}
}
