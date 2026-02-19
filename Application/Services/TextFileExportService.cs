namespace DevProjex.Application.Services;

public sealed class TextFileExportService
{
	private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

	public async Task WriteAsync(Stream stream, string content, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(stream);
		ArgumentNullException.ThrowIfNull(content);

		if (!stream.CanWrite)
			throw new InvalidOperationException("Target stream must be writable.");

		// Reset seekable streams to avoid stale bytes when overriding existing files.
		if (stream.CanSeek)
		{
			stream.SetLength(0);
			stream.Position = 0;
		}

		await using var writer = new StreamWriter(stream, Utf8WithoutBom, bufferSize: 4096, leaveOpen: true);
		await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
		await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
	}
}
