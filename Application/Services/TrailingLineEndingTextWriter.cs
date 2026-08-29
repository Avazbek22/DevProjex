namespace DevProjex.Application.Services;

internal sealed class TrailingLineEndingTextWriter(TextWriter inner) : TextWriter
{
	private const int FlushBufferLength = 4 * 1024;
	private const int MaximumRetainedBitBufferBytes = 64 * 1024;
	private byte[] _trailingBits = [];
	private char[]? _flushBuffer;
	private int _trailingLength;
	private long _length;

	public override Encoding Encoding => inner.Encoding;
	public int Length => checked((int)_length);
	internal int BufferedLineEndingCount => _trailingLength;
	internal int BufferedStorageCapacityBytes => _trailingBits.Length;

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
			await FlushTrailingAsync(cancellationToken).ConfigureAwait(false);
			await inner.WriteAsync(buffer[..contentLength], cancellationToken).ConfigureAwait(false);
		}

		if (contentLength < buffer.Length)
			AppendTrailing(buffer.Span[contentLength..]);
		_length = checked(_length + buffer.Length);
	}

	public async ValueTask CompleteAsync(CancellationToken cancellationToken)
	{
		ClearTrailing();
		await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	private void AppendTrailing(ReadOnlySpan<char> lineEndings)
	{
		var requiredLength = checked(_trailingLength + lineEndings.Length);
		EnsureTrailingCapacity(requiredLength);
		for (var index = 0; index < lineEndings.Length; index++)
		{
			var bitIndex = _trailingLength + index;
			var mask = (byte)(1 << (bitIndex & 7));
			ref var storage = ref _trailingBits[bitIndex >> 3];
			if (lineEndings[index] == '\n')
				storage |= mask;
			else
				storage &= (byte)~mask;
		}
		_trailingLength = requiredLength;
	}

	private async Task FlushTrailingAsync(CancellationToken cancellationToken)
	{
		if (_trailingLength == 0)
			return;

		var buffer = _flushBuffer ??= new char[FlushBufferLength];
		var offset = 0;
		while (offset < _trailingLength)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var count = Math.Min(buffer.Length, _trailingLength - offset);
			for (var index = 0; index < count; index++)
			{
				var bitIndex = offset + index;
				buffer[index] = (_trailingBits[bitIndex >> 3] & (1 << (bitIndex & 7))) != 0
					? '\n'
					: '\r';
			}
			await inner.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
			offset += count;
		}

		ClearTrailing();
	}

	private void EnsureTrailingCapacity(int characterCount)
	{
		var requiredBytes = checked((int)((characterCount + 7L) / 8));
		if (requiredBytes <= _trailingBits.Length)
			return;

		var newLength = Math.Max(16, _trailingBits.Length);
		while (newLength < requiredBytes)
			newLength = newLength > int.MaxValue / 2
				? requiredBytes
				: Math.Max(requiredBytes, newLength * 2);
		Array.Resize(ref _trailingBits, newLength);
	}

	private void ClearTrailing()
	{
		_trailingLength = 0;
		if (_trailingBits.Length > MaximumRetainedBitBufferBytes)
			_trailingBits = [];
	}
}
