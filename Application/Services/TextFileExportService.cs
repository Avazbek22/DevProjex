namespace DevProjex.Application.Services;

public sealed class TextFileExportService
{
	public async Task WriteAsync(Stream stream, string content, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(stream);
		ArgumentNullException.ThrowIfNull(content);
		PrepareDestination(stream);

		await AppendAsync(stream, content, cancellationToken).ConfigureAwait(false);
		await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task AppendAsync(
		Stream stream,
		string content,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(content);
		await AppendAsync(stream, content.AsMemory(), cancellationToken).ConfigureAwait(false);
	}

	public async Task AppendAsync(
		Stream stream,
		ReadOnlyMemory<char> content,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(stream);
		if (!stream.CanWrite)
			throw new InvalidOperationException("Target stream must be writable.");

		await PreviewTextStreamWriter
			.WriteAsync(stream, content, cancellationToken)
			.ConfigureAwait(false);
	}

	public async Task WriteAsync(
		Stream stream,
		IPreviewTextDocument document,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(stream);
		ArgumentNullException.ThrowIfNull(document);
		PrepareDestination(stream);

		await document.WriteToAsync(stream, cancellationToken).ConfigureAwait(false);
		await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	private static void PrepareDestination(Stream stream)
	{
		if (!stream.CanWrite)
			throw new InvalidOperationException("Target stream must be writable.");

		// Reset seekable streams to avoid stale bytes when overriding existing files.
		if (stream.CanSeek)
		{
			stream.SetLength(0);
			stream.Position = 0;
		}
	}
}
