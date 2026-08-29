namespace DevProjex.Application.Services;

internal sealed class TrailingLineEndingTextWriter(TextWriter inner) : TextWriter
{
	private readonly StringBuilder _trailing = new(2);
	private long _length;

	public override Encoding Encoding => inner.Encoding;
	public int Length => checked((int)_length);

	public override async Task WriteAsync(
		ReadOnlyMemory<char> buffer,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var contentLength = buffer.Length;
		while (contentLength > 0 && buffer.Span[contentLength - 1] is '\r' or '\n')
			contentLength--;

		if (contentLength > 0)
		{
			if (_trailing.Length > 0)
			{
				await inner.WriteAsync(
					_trailing.ToString().AsMemory(),
					cancellationToken).ConfigureAwait(false);
				_trailing.Clear();
			}
			await inner.WriteAsync(buffer[..contentLength], cancellationToken).ConfigureAwait(false);
		}

		if (contentLength < buffer.Length)
			_trailing.Append(buffer.Span[contentLength..]);
		_length = checked(_length + buffer.Length);
	}

	public async ValueTask CompleteAsync(CancellationToken cancellationToken)
	{
		_trailing.Clear();
		await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
	}
}
