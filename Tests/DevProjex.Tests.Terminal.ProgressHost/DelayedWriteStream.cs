namespace DevProjex.Tests.Terminal.ProgressHost;

internal sealed class DelayedWriteStream(
	Stream inner,
	string cancelPath,
	CancellationTokenSource cancellation) : Stream
{
	public override bool CanRead => false;
	public override bool CanSeek => inner.CanSeek;
	public override bool CanWrite => true;
	public override long Length => inner.Length;
	public override long Position
	{
		get => inner.Position;
		set => inner.Position = value;
	}

	public override void Flush() => inner.Flush();
	public override Task FlushAsync(CancellationToken cancellationToken) =>
		inner.FlushAsync(cancellationToken);
	public override int Read(byte[] buffer, int offset, int count) =>
		throw new NotSupportedException();
	public override long Seek(long offset, SeekOrigin origin) =>
		inner.Seek(offset, origin);
	public override void SetLength(long value) => inner.SetLength(value);
	public override void Write(byte[] buffer, int offset, int count) =>
		inner.Write(buffer, offset, count);

	public override async ValueTask WriteAsync(
		ReadOnlyMemory<byte> buffer,
		CancellationToken cancellationToken = default)
	{
		CancelWhenRequested(cancellationToken);
		await Task.Delay(5, cancellationToken).ConfigureAwait(false);
		CancelWhenRequested(cancellationToken);
		await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
	}

	private void CancelWhenRequested(CancellationToken cancellationToken)
	{
		if (!File.Exists(cancelPath))
			return;
		cancellation.Cancel();
		cancellationToken.ThrowIfCancellationRequested();
	}

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
