namespace DevProjex.Application.Preview;

internal static class PreviewTextStreamWriter
{
	internal const int BufferSizeBytes = 80 * 1024;
	private const int MaximumCharactersPerChunk = BufferSizeBytes / 4;
	private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

	public static async ValueTask WriteAsync(
		Stream destination,
		string content,
		CancellationToken cancellationToken)
	{
		var encoder = Utf8WithoutBom.GetEncoder();
		var buffer = new byte[BufferSizeBytes];
		var offset = 0;
		while (offset < content.Length)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var characterCount = Math.Min(MaximumCharactersPerChunk, content.Length - offset);
			var flush = offset + characterCount == content.Length;
			encoder.Convert(
				content.AsSpan(offset, characterCount),
				buffer,
				flush,
				out var charactersUsed,
				out var bytesUsed,
				out _);
			if (charactersUsed == 0)
				throw new EncoderFallbackException("UTF-8 encoder did not consume the input chunk.");

			offset += charactersUsed;
			if (bytesUsed > 0)
			{
				await destination
					.WriteAsync(buffer.AsMemory(0, bytesUsed), cancellationToken)
					.ConfigureAwait(false);
			}
		}
	}
}
