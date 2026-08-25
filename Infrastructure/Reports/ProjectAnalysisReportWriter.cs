using DevProjex.Application.Services;

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
		try
		{
			await AtomicFileOutput.WriteAsync(
				fullPath,
				overwrite: true,
				(stream, token) => JsonSerializer.SerializeAsync(stream, report, JsonOptions, token),
				cancellationToken).ConfigureAwait(false);
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
}
