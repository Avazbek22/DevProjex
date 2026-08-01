using System.Buffers;

namespace DevProjex.Terminal.Execution;

internal sealed class Utf8TextWriterStream(
	TextWriter writer,
	CancellationToken lifetimeCancellationToken) : Stream
{
	private static readonly UTF8Encoding StrictUtf8 = new(
		encoderShouldEmitUTF8Identifier: false,
		throwOnInvalidBytes: true);
	private readonly Decoder _decoder = StrictUtf8.GetDecoder();
	private bool _completed;

	public override bool CanRead => false;
	public override bool CanSeek => false;
	public override bool CanWrite => !_completed;
	public override long Length => throw new NotSupportedException();

	public override long Position
	{
		get => throw new NotSupportedException();
		set => throw new NotSupportedException();
	}

	public async Task CompleteAsync(CancellationToken cancellationToken)
	{
		if (_completed)
			return;

		await DecodeAsync(ReadOnlyMemory<byte>.Empty, flush: true, cancellationToken)
			.ConfigureAwait(false);
		await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
		_completed = true;
	}

	public override void Flush() => writer.Flush();

	public override Task FlushAsync(CancellationToken cancellationToken) =>
		writer.FlushAsync(cancellationToken);

	public override int Read(byte[] buffer, int offset, int count) =>
		throw new NotSupportedException();

	public override long Seek(long offset, SeekOrigin origin) =>
		throw new NotSupportedException();

	public override void SetLength(long value) =>
		throw new NotSupportedException();

	public override void Write(byte[] buffer, int offset, int count)
	{
		ArgumentNullException.ThrowIfNull(buffer);
		ArgumentOutOfRangeException.ThrowIfNegative(offset);
		ArgumentOutOfRangeException.ThrowIfNegative(count);
		if (offset > buffer.Length - count)
			throw new ArgumentException("Offset and count exceed the buffer length.");
		ThrowIfCompleted();
		Decode(buffer.AsSpan(offset, count), flush: false);
	}

	public override void Write(ReadOnlySpan<byte> buffer)
	{
		ThrowIfCompleted();
		Decode(buffer, flush: false);
	}

	public override Task WriteAsync(
		byte[] buffer,
		int offset,
		int count,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(buffer);
		ArgumentOutOfRangeException.ThrowIfNegative(offset);
		ArgumentOutOfRangeException.ThrowIfNegative(count);
		if (offset > buffer.Length - count)
			throw new ArgumentException("Offset and count exceed the buffer length.");
		return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
	}

	public override ValueTask WriteAsync(
		ReadOnlyMemory<byte> buffer,
		CancellationToken cancellationToken = default)
	{
		ThrowIfCompleted();
		return new ValueTask(DecodeAsync(buffer, flush: false, cancellationToken));
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing && !_completed)
		{
			lifetimeCancellationToken.ThrowIfCancellationRequested();
			Decode(ReadOnlySpan<byte>.Empty, flush: true);
			writer.Flush();
			_completed = true;
		}
		base.Dispose(disposing);
	}

	public override async ValueTask DisposeAsync()
	{
		await CompleteAsync(lifetimeCancellationToken).ConfigureAwait(false);
		GC.SuppressFinalize(this);
	}

	private void Decode(ReadOnlySpan<byte> bytes, bool flush)
	{
		var characterBuffer = ArrayPool<char>.Shared.Rent(
			Math.Max(1, StrictUtf8.GetMaxCharCount(Math.Min(bytes.Length, 16 * 1024))));
		try
		{
			while (!bytes.IsEmpty || flush)
			{
				_decoder.Convert(
					bytes,
					characterBuffer,
					flush,
					out var bytesUsed,
					out var charactersUsed,
					out var completed);
				if (charactersUsed > 0)
					writer.Write(characterBuffer, 0, charactersUsed);
				bytes = bytes[bytesUsed..];
				if (completed)
					break;
			}
		}
		finally
		{
			ArrayPool<char>.Shared.Return(characterBuffer);
		}
	}

	private async Task DecodeAsync(
		ReadOnlyMemory<byte> bytes,
		bool flush,
		CancellationToken cancellationToken)
	{
		var characterBuffer = ArrayPool<char>.Shared.Rent(
			Math.Max(1, StrictUtf8.GetMaxCharCount(Math.Min(bytes.Length, 16 * 1024))));
		try
		{
			while (!bytes.IsEmpty || flush)
			{
				cancellationToken.ThrowIfCancellationRequested();
				_decoder.Convert(
					bytes.Span,
					characterBuffer,
					flush,
					out var bytesUsed,
					out var charactersUsed,
					out var completed);
				if (charactersUsed > 0)
				{
					await writer.WriteAsync(
							characterBuffer.AsMemory(0, charactersUsed),
							cancellationToken)
						.ConfigureAwait(false);
				}
				bytes = bytes[bytesUsed..];
				if (completed)
					break;
			}
		}
		finally
		{
			ArrayPool<char>.Shared.Return(characterBuffer);
		}
	}

	private void ThrowIfCompleted()
	{
		ObjectDisposedException.ThrowIf(_completed, this);
	}
}
