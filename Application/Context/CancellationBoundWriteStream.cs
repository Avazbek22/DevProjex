using System.Buffers;

namespace DevProjex.Application.Context;

internal sealed class CancellationBoundWriteStream(
	Stream destination,
	CancellationToken cancellationToken) : Stream
{
	private const int MaximumSynchronousWriteChunkBytes = 64 * 1024;

	public override bool CanRead => false;
	public override bool CanSeek => destination.CanSeek;
	public override bool CanWrite => destination.CanWrite;
	public override long Length => destination.Length;

	public override long Position
	{
		get => destination.Position;
		set
		{
			cancellationToken.ThrowIfCancellationRequested();
			destination.Position = value;
		}
	}

	public override void Flush()
	{
		cancellationToken.ThrowIfCancellationRequested();
		destination
			.FlushAsync(cancellationToken)
			.GetAwaiter()
			.GetResult();
	}

	public override Task FlushAsync(CancellationToken ignoredCancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return destination.FlushAsync(cancellationToken);
	}

	public override int Read(byte[] buffer, int offset, int count) =>
		throw new NotSupportedException();

	public override long Seek(long offset, SeekOrigin origin)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return destination.Seek(offset, origin);
	}

	public override void SetLength(long value)
	{
		cancellationToken.ThrowIfCancellationRequested();
		destination.SetLength(value);
	}

	public override void Write(byte[] buffer, int offset, int count)
	{
		cancellationToken.ThrowIfCancellationRequested();
		destination
			.WriteAsync(buffer, offset, count, cancellationToken)
			.GetAwaiter()
			.GetResult();
	}

	public override void Write(ReadOnlySpan<byte> buffer)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (buffer.IsEmpty)
			return;

		var rented = ArrayPool<byte>.Shared.Rent(
			Math.Min(buffer.Length, MaximumSynchronousWriteChunkBytes));
		try
		{
			while (!buffer.IsEmpty)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var chunkLength = Math.Min(buffer.Length, rented.Length);
				buffer[..chunkLength].CopyTo(rented);
				destination
					.WriteAsync(
						rented.AsMemory(0, chunkLength),
						cancellationToken)
					.GetAwaiter()
					.GetResult();
				buffer = buffer[chunkLength..];
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(rented, clearArray: true);
		}
	}

	public override Task WriteAsync(
		byte[] buffer,
		int offset,
		int count,
		CancellationToken ignoredCancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return destination.WriteAsync(buffer, offset, count, cancellationToken);
	}

	public override ValueTask WriteAsync(
		ReadOnlyMemory<byte> buffer,
		CancellationToken ignoredCancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return destination.WriteAsync(buffer, cancellationToken);
	}

	protected override void Dispose(bool disposing)
	{
		// The document service owns only this cancellation view, not the destination.
		base.Dispose(disposing);
	}
}
