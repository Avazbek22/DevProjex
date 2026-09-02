using System.Buffers;

namespace DevProjex.Application.Preview;

public sealed class FileBackedPreviewTextDocument(
    string storagePath,
    long[] lineOffsets,
    long fileLength,
    int maxLineLength,
    long characterCount,
    IReadOnlyList<PreviewDocumentSection>? sections = null,
	IReadOnlyList<PreviewRedactionSpan>? redactions = null)
    : IPreviewTextDocument
{
	private const int MaximumLinesPerVisitChunk = 1024;
	private const int MaximumBytesPerVisitChunk = 1024 * 1024;
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

	private readonly SemaphoreSlim _streamGate = new(1, 1);
    private FileStream? _stream = new(
        storagePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 4096,
        options: FileOptions.RandomAccess | FileOptions.DeleteOnClose);
    private bool _disposed;

    public int LineCount => Math.Max(1, lineOffsets.Length);

    public int MaxLineLength { get; } = maxLineLength;

    public long CharacterCount { get; } = characterCount;

    public IReadOnlyList<PreviewDocumentSection> Sections { get; } =
        sections is { Count: > 0 } ? sections.ToArray() : Array.Empty<PreviewDocumentSection>();

	public IReadOnlyList<PreviewRedactionSpan> Redactions { get; } =
		redactions is { Count: > 0 } ? redactions.ToArray() : Array.Empty<PreviewRedactionSpan>();

    public string GetFullText()
    {
        ThrowIfDisposed();
        return ReadTextRange(0, fileLength, trimTrailingLineEnding: false);
    }

	public async ValueTask WriteToAsync(
		Stream destination,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(destination);
		if (!destination.CanWrite)
			throw new InvalidOperationException("Target stream must be writable.");

		await _streamGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			ThrowIfDisposed();
			var stream = _stream!;
			stream.Seek(0, SeekOrigin.Begin);
			var buffer = ArrayPool<byte>.Shared.Rent(PreviewTextStreamWriter.BufferSizeBytes);
			try
			{
				while (true)
				{
					cancellationToken.ThrowIfCancellationRequested();
					var bytesRead = await stream
						.ReadAsync(buffer, cancellationToken)
						.ConfigureAwait(false);
					if (bytesRead == 0)
						break;

					await destination
						.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
						.ConfigureAwait(false);
				}
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
			}
		}
		finally
		{
			_streamGate.Release();
		}
	}

    public string GetLineText(int lineNumber)
    {
        ThrowIfDisposed();

        if (lineOffsets.Length == 0)
            return string.Empty;

        var normalizedLine = Math.Clamp(lineNumber, 1, LineCount);
        var startOffset = lineOffsets[normalizedLine - 1];
        var endOffset = normalizedLine < LineCount
            ? lineOffsets[normalizedLine]
            : fileLength;

        return ReadTextRange(startOffset, endOffset, trimTrailingLineEnding: true);
    }

    public string GetLineRangeText(int firstLine, int lastLine)
    {
        ThrowIfDisposed();

        if (lineOffsets.Length == 0)
            return string.Empty;

        var normalizedFirstLine = Math.Max(1, firstLine);
        var normalizedLastLine = Math.Min(LineCount, Math.Max(normalizedFirstLine, lastLine));
        if (normalizedLastLine < normalizedFirstLine)
            return string.Empty;

        var startOffset = lineOffsets[normalizedFirstLine - 1];
        var endOffset = normalizedLastLine < LineCount
            ? lineOffsets[normalizedLastLine]
            : fileLength;

        return ReadTextRange(startOffset, endOffset, trimTrailingLineEnding: true);
    }

	public void VisitLines(
		int firstLine,
		int lastLine,
		PreviewTextLineVisitor visitor,
		CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		ArgumentNullException.ThrowIfNull(visitor);
		if (!PreviewTextLineRange.TryNormalize(LineCount, firstLine, lastLine, out var first, out var last))
			return;
		if (lineOffsets.Length == 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			_ = visitor(1, ReadOnlySpan<char>.Empty);
			return;
		}

		var lineIndex = first - 1;
		var lastLineIndexExclusive = last;
		while (lineIndex < lastLineIndexExclusive)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var chunkEndExclusive = ResolveVisitChunkEnd(lineIndex, lastLineIndexExclusive);
			var startOffset = lineOffsets[lineIndex];
			var endOffset = chunkEndExclusive < LineCount
				? lineOffsets[chunkEndExclusive]
				: fileLength;
			var byteCount = checked((int)Math.Max(0, endOffset - startOffset));
			if (byteCount == 0)
			{
				for (; lineIndex < chunkEndExclusive; lineIndex++)
				{
					cancellationToken.ThrowIfCancellationRequested();
					if (!visitor(lineIndex + 1, ReadOnlySpan<char>.Empty))
						return;
				}
				continue;
			}

			var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
			try
			{
				var bytesRead = ReadBytes(startOffset, buffer, byteCount);
				var characters = ArrayPool<char>.Shared.Rent(Math.Max(1, bytesRead));
				try
				{
					var characterCount = Utf8WithoutBom.GetChars(
						buffer.AsSpan(0, bytesRead),
						characters);
					if (!VisitDecodedLines(
						characters.AsSpan(0, characterCount),
						lineIndex,
						chunkEndExclusive,
						visitor,
						cancellationToken))
					{
						return;
					}
				}
				finally
				{
					ArrayPool<char>.Shared.Return(characters, clearArray: true);
				}
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
			}

			lineIndex = chunkEndExclusive;
		}
	}

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

		_streamGate.Wait();
		try
        {
            _stream?.Dispose();
            _stream = null;
        }
		finally
		{
			_streamGate.Release();
		}

        try
        {
            if (File.Exists(storagePath))
                File.Delete(storagePath);
        }
        catch
        {
            // Best-effort cleanup only. Temporary preview storage must not crash shutdown.
        }
    }

    private int ReadBytes(long startOffset, byte[] buffer, int byteCount)
    {
		_streamGate.Wait();
		try
        {
            ThrowIfDisposed();

            var stream = _stream!;
            stream.Seek(startOffset, SeekOrigin.Begin);

            var totalBytesRead = 0;
            while (totalBytesRead < byteCount)
            {
                var bytesRead = stream.Read(buffer, totalBytesRead, byteCount - totalBytesRead);
                if (bytesRead == 0)
                    break;

                totalBytesRead += bytesRead;
            }

            return totalBytesRead;
        }
		finally
		{
			_streamGate.Release();
		}
    }

	private int ResolveVisitChunkEnd(int startLineIndex, int lastLineIndexExclusive)
	{
		var startOffset = lineOffsets[startLineIndex];
		var maximumEnd = Math.Min(
			lastLineIndexExclusive,
			startLineIndex + MaximumLinesPerVisitChunk);
		var endExclusive = startLineIndex + 1;
		while (endExclusive < maximumEnd)
		{
			var candidateEndExclusive = endExclusive + 1;
			var candidateEndOffset = candidateEndExclusive < LineCount
				? lineOffsets[candidateEndExclusive]
				: fileLength;
			if (candidateEndOffset - startOffset > MaximumBytesPerVisitChunk)
				break;
			endExclusive = candidateEndExclusive;
		}

		return endExclusive;
	}

	private static bool VisitDecodedLines(
		ReadOnlySpan<char> decoded,
		int firstLineIndex,
		int lastLineIndexExclusive,
		PreviewTextLineVisitor visitor,
		CancellationToken cancellationToken)
	{
		var position = 0;
		for (var lineIndex = firstLineIndex; lineIndex < lastLineIndexExclusive; lineIndex++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var remaining = decoded[position..];
			var separatorOffset = remaining.IndexOf('\n');
			var rawLength = separatorOffset >= 0 ? separatorOffset : remaining.Length;
			var visibleLength = rawLength;
			if (visibleLength > 0 && remaining[visibleLength - 1] == '\r')
				visibleLength--;
			if (!visitor(lineIndex + 1, remaining[..visibleLength]))
				return false;

			position = separatorOffset >= 0
				? checked(position + separatorOffset + 1)
				: decoded.Length;
		}

		return true;
	}

    private string ReadTextRange(
        long startOffset,
        long endOffset,
        bool trimTrailingLineEnding)
    {
        var byteCount = checked((int)Math.Max(0, endOffset - startOffset));
        if (byteCount == 0)
            return string.Empty;

        var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var bytesRead = ReadBytes(startOffset, buffer, byteCount);
            if (trimTrailingLineEnding &&
                bytesRead > 0 &&
                buffer[bytesRead - 1] == (byte)'\n')
                bytesRead--;

            if (trimTrailingLineEnding &&
                bytesRead > 0 &&
                buffer[bytesRead - 1] == (byte)'\r')
                bytesRead--;

            return bytesRead == 0
                ? string.Empty
                : Utf8WithoutBom.GetString(buffer, 0, bytesRead);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FileBackedPreviewTextDocument));
    }
}
