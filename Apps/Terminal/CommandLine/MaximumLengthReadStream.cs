namespace DevProjex.Terminal.CommandLine;

internal sealed class MaximumLengthReadStream(
	Stream inner,
	long maximumBytes,
	Func<Exception> limitExceededExceptionFactory) : Stream
{
	private long _bytesRead;

	public override bool CanRead => inner.CanRead;
	public override bool CanSeek => false;
	public override bool CanWrite => false;
	public override long Length => throw new NotSupportedException();
	public override long Position
	{
		get => _bytesRead;
		set => throw new NotSupportedException();
	}

	public override int Read(byte[] buffer, int offset, int count)
	{
		var read = inner.Read(buffer, offset, ResolveReadCount(count));
		RegisterRead(read);
		return read;
	}

	public override int Read(Span<byte> buffer)
	{
		var read = inner.Read(buffer[..ResolveReadCount(buffer.Length)]);
		RegisterRead(read);
		return read;
	}

	public override async ValueTask<int> ReadAsync(
		Memory<byte> buffer,
		CancellationToken cancellationToken = default)
	{
		var read = await inner.ReadAsync(
			buffer[..ResolveReadCount(buffer.Length)],
			cancellationToken).ConfigureAwait(false);
		RegisterRead(read);
		return read;
	}

	private int ResolveReadCount(int requested)
	{
		var remainingWithSentinel = maximumBytes - _bytesRead + 1;
		return (int)Math.Min(requested, Math.Max(1, remainingWithSentinel));
	}

	private void RegisterRead(int count)
	{
		_bytesRead = checked(_bytesRead + count);
		if (_bytesRead > maximumBytes)
			throw limitExceededExceptionFactory();
	}

	public override void Flush() => throw new NotSupportedException();
	public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
	public override void SetLength(long value) => throw new NotSupportedException();
	public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

	protected override void Dispose(bool disposing)
	{
		if (disposing)
			inner.Dispose();
		base.Dispose(disposing);
	}

	public override async ValueTask DisposeAsync()
	{
		await inner.DisposeAsync().ConfigureAwait(false);
		GC.SuppressFinalize(this);
	}
}
