using System.Buffers;
using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;

namespace DevProjex.Application.Services;

/// <summary>
/// Analyzes file content to determine if it's text or binary.
///
/// Known binary extensions are rejected without I/O. Other files use a null-byte
/// probe and, when metrics or content are requested, validation continues through
/// the complete decoded stream so a binary marker after the probe is not missed.
/// Operations complete synchronously; ValueTask avoids one completed Task allocation
/// per file while the caller owns bounded parallel scheduling.
/// </summary>
public sealed class FileContentAnalyzer : IFileContentAnalyzer
{
	// 512 bytes is sufficient - all binary formats have null bytes in first 512 bytes
	private const int BinaryCheckBufferSize = 512;

	// Files larger than this get estimated metrics (no full read)
	private const int DefaultMaxSizeForFullRead = 10 * 1024 * 1024; // 10MB

	// For line estimation when file is too large to read
	private const int EstimatedCharsPerLine = 60;

	// Buffer size for streaming read (balance between memory and I/O efficiency)
	private const int StreamingBufferSize = 8192;

	// Known binary extensions - skip file read entirely (fast path)
	private static readonly FrozenSet<string> KnownBinaryExtensions = new[]
	{
		// Images
		".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tiff", ".tif",
		// Video
		".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm",
		// Audio
		".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma", ".m4a",
		// Archives
		".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz",
		// Executables/Libraries
		".exe", ".dll", ".so", ".dylib", ".pdb", ".ilk",
		// Documents (binary)
		".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
		// Fonts
		".ttf", ".otf", ".woff", ".woff2", ".eot",
		// Other binary
		".bin", ".dat", ".db", ".sqlite", ".mdb"
	}.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
	private static readonly Encoding StrictUtf8 = new UTF8Encoding(
		encoderShouldEmitUTF8Identifier: false,
		throwOnInvalidBytes: true);
	private static readonly Encoding StrictUtf16Le = new UnicodeEncoding(
		bigEndian: false,
		byteOrderMark: true,
		throwOnInvalidBytes: true);
	private static readonly Encoding StrictUtf16Be = new UnicodeEncoding(
		bigEndian: true,
		byteOrderMark: true,
		throwOnInvalidBytes: true);
	private static readonly Encoding StrictUtf32Le = new UTF32Encoding(
		bigEndian: false,
		byteOrderMark: true,
		throwOnInvalidCharacters: true);
	private static readonly Encoding StrictUtf32Be = new UTF32Encoding(
		bigEndian: true,
		byteOrderMark: true,
		throwOnInvalidCharacters: true);

	public FileContentClassification? ClassifyWithoutReading(string path) =>
		HasKnownBinaryExtension(path)
			? FileContentClassification.Binary
			: null;

	public ValueTask<FileContentReadResult> ReadClassifiedAsync(
		string path,
		long maxSizeForFullRead,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromResult(ReadClassifiedSync(path, maxSizeForFullRead, cancellationToken));

	/// <inheritdoc />
	public ValueTask<bool> IsTextFileAsync(string path, CancellationToken cancellationToken = default)
	{
		var result = GetClassifiedMetricsSync(path, cancellationToken);
		return ValueTask.FromResult(result.IsText);
	}

	/// <inheritdoc />
	public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		var result = GetClassifiedMetricsSync(path, cancellationToken);
		return ValueTask.FromResult(result.IsText ? result.Metrics : null);
	}

	public ValueTask<FileContentMetricsResult> GetClassifiedMetricsAsync(
		string path,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromResult(GetClassifiedMetricsSync(path, cancellationToken));

	public ValueTask<IFileContentSnapshot> OpenCompleteSnapshotAsync(
		string path,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromResult(OpenCompleteSnapshotSync(path, cancellationToken));

	public ValueTask<ICompleteTextFileBuffer> OpenCompleteTextBufferAsync(
		string path,
		long maximumBytes,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromResult(OpenCompleteTextBufferSync(path, maximumBytes, cancellationToken));

	private static ICompleteTextFileBuffer OpenCompleteTextBufferSync(
		string path,
		long maximumBytes,
		CancellationToken cancellationToken)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
		char[]? buffer = null;
		var written = 0;
		try
		{
			if (HasKnownBinaryExtension(path))
				return new ClassifiedCompleteTextFileBuffer(FileContentClassification.Binary);

			using var stream = OpenSequentialRead(
				path,
				bufferSize: 1,
				FileShare.Read | FileShare.Delete);
			var sizeBytes = stream.Length;
			if (sizeBytes == 0)
				return new ClassifiedCompleteTextFileBuffer(FileContentClassification.Text);

			var bomEncoding = DetectBomEncoding(stream, cancellationToken);
			var encoding = bomEncoding ?? StrictUtf8;
			if (!CheckForNullBytes(stream, cancellationToken))
				return new ClassifiedCompleteTextFileBuffer(FileContentClassification.Binary, sizeBytes);
			// The scan limit is a text-buffer bound, not a project-copy size limit.
			// Classify known and null-bearing binaries first so large binary assets still
			// pass through folder and ZIP exports unchanged.
			if (sizeBytes > maximumBytes || sizeBytes > int.MaxValue)
			{
				return new ClassifiedCompleteTextFileBuffer(
					FileContentClassification.TooLarge,
					sizeBytes);
			}

			var capacity = Math.Max(1, encoding.GetMaxCharCount(checked((int)sizeBytes)));
			buffer = ArrayPool<char>.Shared.Rent(capacity);
			stream.Position = GetPreambleLength(bomEncoding);
			var byteBuffer = ArrayPool<byte>.Shared.Rent(StreamingBufferSize);
			try
			{
				var decoder = encoding.GetDecoder();
				while (true)
				{
					cancellationToken.ThrowIfCancellationRequested();
					var bytesRead = stream.Read(byteBuffer, 0, StreamingBufferSize);
					if (bytesRead == 0)
						break;

					var consumed = 0;
					while (consumed < bytesRead)
					{
						decoder.Convert(
							byteBuffer.AsSpan(consumed, bytesRead - consumed),
							buffer.AsSpan(written),
							flush: false,
							out var bytesUsed,
							out var charsUsed,
							out _);
						if (bytesUsed == 0 && charsUsed == 0)
							throw new IOException("The complete text buffer capacity was exhausted.");
						consumed += bytesUsed;
						written = checked(written + charsUsed);
					}
				}

				decoder.Convert(
					ReadOnlySpan<byte>.Empty,
					buffer.AsSpan(written),
					flush: true,
					out _,
					out var finalChars,
					out var completed);
				written = checked(written + finalChars);
				if (!completed)
					throw new IOException("The complete text buffer capacity was exhausted.");
			}
			finally
			{
				CryptographicOperations.ZeroMemory(byteBuffer.AsSpan());
				ArrayPool<byte>.Shared.Return(byteBuffer);
			}

			if (stream.Length != sizeBytes)
				throw new IOException("The file changed while its text buffer was being read.");
			if (buffer.AsSpan(0, written).Contains('\0'))
				return new ClassifiedCompleteTextFileBuffer(FileContentClassification.Binary, sizeBytes);

			var result = new PooledCompleteTextFileBuffer(buffer, written, sizeBytes);
			buffer = null;
			written = 0;
			return result;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (UnauthorizedAccessException)
		{
			return new ClassifiedCompleteTextFileBuffer(FileContentClassification.AccessDenied);
		}
		catch (FileNotFoundException)
		{
			return new ClassifiedCompleteTextFileBuffer(FileContentClassification.Missing);
		}
		catch (DirectoryNotFoundException)
		{
			return new ClassifiedCompleteTextFileBuffer(FileContentClassification.Missing);
		}
		catch (DecoderFallbackException)
		{
			return new ClassifiedCompleteTextFileBuffer(FileContentClassification.UnsupportedEncoding);
		}
		catch (SecurityException)
		{
			return new ClassifiedCompleteTextFileBuffer(FileContentClassification.AccessDenied);
		}
		catch (IOException)
		{
			return new ClassifiedCompleteTextFileBuffer(FileContentClassification.Unreadable);
		}
		finally
		{
			if (buffer is not null)
			{
				CryptographicOperations.ZeroMemory(
					MemoryMarshal.AsBytes(buffer.AsSpan(0, written)));
				ArrayPool<char>.Shared.Return(buffer);
			}
		}
	}

	private static int GetPreambleLength(Encoding? bomEncoding)
	{
		if (bomEncoding is null)
			return 0;
		if (ReferenceEquals(bomEncoding, StrictUtf8))
			return 3;
		if (ReferenceEquals(bomEncoding, StrictUtf16Le) ||
		    ReferenceEquals(bomEncoding, StrictUtf16Be))
		{
			return 2;
		}
		return 4;
	}

	private static FileContentMetricsResult GetClassifiedMetricsSync(
		string path,
		CancellationToken cancellationToken)
	{
		try
		{
			if (HasKnownBinaryExtension(path))
				return new FileContentMetricsResult(FileContentClassification.Binary);

			using var stream = OpenSequentialRead(path, StreamingBufferSize, FileShare.Read);
			var sizeBytes = stream.Length;

			if (sizeBytes == 0)
			{
				return new FileContentMetricsResult(
					FileContentClassification.Text,
					new TextFileMetrics(
						SizeBytes: 0,
						LineCount: 0,
						CharCount: 0,
						IsEmpty: true,
						IsWhitespaceOnly: false,
						IsEstimated: false,
						CrLfPairCount: 0));
			}

			var encoding = DetectBomEncoding(stream, cancellationToken);
			if (encoding is null && !CheckForNullBytes(stream, cancellationToken))
				return new FileContentMetricsResult(FileContentClassification.Binary);

			if (sizeBytes > DefaultMaxSizeForFullRead)
			{
				return new FileContentMetricsResult(
					FileContentClassification.TooLarge,
					new TextFileMetrics(
						SizeBytes: sizeBytes,
						LineCount: Math.Max(1, (int)(sizeBytes / EstimatedCharsPerLine)),
						CharCount: (int)Math.Min(sizeBytes, int.MaxValue),
						IsEmpty: false,
						IsWhitespaceOnly: false,
						IsEstimated: true,
						CrLfPairCount: 0,
						TrailingNewlineChars: 0,
						TrailingNewlineLineBreaks: 0));
			}

			var metrics = CountMetricsStreaming(
				stream,
				sizeBytes,
				encoding ?? StrictUtf8,
				cancellationToken,
				calculateFingerprint: false,
				out _);
			return metrics is null
				? new FileContentMetricsResult(FileContentClassification.Binary)
				: new FileContentMetricsResult(FileContentClassification.Text, metrics);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (UnauthorizedAccessException)
		{
			return new FileContentMetricsResult(FileContentClassification.AccessDenied);
		}
		catch (FileNotFoundException)
		{
			return new FileContentMetricsResult(FileContentClassification.Missing);
		}
		catch (DirectoryNotFoundException)
		{
			return new FileContentMetricsResult(FileContentClassification.Missing);
		}
		catch (DecoderFallbackException)
		{
			return new FileContentMetricsResult(FileContentClassification.UnsupportedEncoding);
		}
		catch (IOException)
		{
			return new FileContentMetricsResult(FileContentClassification.Unreadable);
		}
		catch
		{
			return new FileContentMetricsResult(FileContentClassification.Unreadable);
		}
	}

	private static IFileContentSnapshot OpenCompleteSnapshotSync(
		string path,
		CancellationToken cancellationToken)
	{
		FileStream? stream = null;
		try
		{
			if (HasKnownBinaryExtension(path))
			{
				return new ClassifiedFileContentSnapshot(
					new FileContentMetricsResult(FileContentClassification.Binary));
			}

			stream = OpenSequentialRead(
				path,
				StreamingBufferSize,
				FileShare.Read | FileShare.Delete,
				asynchronous: true);
			var sizeBytes = stream.Length;
			if (sizeBytes == 0)
			{
				var emptySnapshot = new StreamFileContentSnapshot(
					stream,
					StrictUtf8,
					new TextFileMetrics(
						SizeBytes: 0,
						LineCount: 0,
						CharCount: 0,
						IsEmpty: true,
						IsWhitespaceOnly: false,
						IsEstimated: false),
					SHA256.HashData(ReadOnlySpan<byte>.Empty));
				stream = null;
				return emptySnapshot;
			}

			var encoding = DetectBomEncoding(stream, cancellationToken);
			if (encoding is null && !CheckForNullBytes(stream, cancellationToken))
			{
				return new ClassifiedFileContentSnapshot(
					new FileContentMetricsResult(FileContentClassification.Binary));
			}

			var metrics = CountMetricsStreaming(
				stream,
				sizeBytes,
				encoding ?? StrictUtf8,
				cancellationToken,
				calculateFingerprint: true,
				out var contentFingerprint);
			if (metrics is null)
			{
				return new ClassifiedFileContentSnapshot(
					new FileContentMetricsResult(FileContentClassification.Binary));
			}
			if (stream.Length != sizeBytes)
				throw new IOException("The file changed while its snapshot was being measured.");

			var snapshot = new StreamFileContentSnapshot(
				stream,
				encoding ?? StrictUtf8,
				metrics,
				contentFingerprint!);
			stream = null;
			return snapshot;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (UnauthorizedAccessException)
		{
			return new ClassifiedFileContentSnapshot(
				new FileContentMetricsResult(FileContentClassification.AccessDenied));
		}
		catch (FileNotFoundException)
		{
			return new ClassifiedFileContentSnapshot(
				new FileContentMetricsResult(FileContentClassification.Missing));
		}
		catch (DirectoryNotFoundException)
		{
			return new ClassifiedFileContentSnapshot(
				new FileContentMetricsResult(FileContentClassification.Missing));
		}
		catch (DecoderFallbackException)
		{
			return new ClassifiedFileContentSnapshot(
				new FileContentMetricsResult(FileContentClassification.UnsupportedEncoding));
		}
		catch (IOException)
		{
			return new ClassifiedFileContentSnapshot(
				new FileContentMetricsResult(FileContentClassification.Unreadable));
		}
		catch (SecurityException)
		{
			return new ClassifiedFileContentSnapshot(
				new FileContentMetricsResult(FileContentClassification.AccessDenied));
		}
		finally
		{
			stream?.Dispose();
		}
	}

	/// <inheritdoc />
	public ValueTask<TextFileContent?> TryReadAsTextAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		return TryReadAsTextAsync(path, DefaultMaxSizeForFullRead, cancellationToken);
	}

	/// <inheritdoc />
	public ValueTask<TextFileContent?> TryReadAsTextAsync(
		string path,
		long maxSizeForFullRead,
		CancellationToken cancellationToken = default)
	{
		return ValueTask.FromResult(TryReadAsTextSync(path, maxSizeForFullRead, cancellationToken));
	}

	private TextFileContent? TryReadAsTextSync(string path, long maxSizeForFullRead, CancellationToken cancellationToken)
	{
		var result = ReadClassifiedSync(path, maxSizeForFullRead, cancellationToken);
		return result.IsText ? result.Content : null;
	}

	private static FileContentReadResult ReadClassifiedSync(
		string path,
		long maxSizeForFullRead,
		CancellationToken cancellationToken)
	{
		try
		{
			// Fast path: known binary extensions - no file I/O needed
			if (HasKnownBinaryExtension(path))
				return new FileContentReadResult(FileContentClassification.Binary);

			using var stream = OpenSequentialRead(path, StreamingBufferSize, FileShare.Read);
			var sizeBytes = stream.Length;

			// Empty file
			if (sizeBytes == 0)
			{
				return new FileContentReadResult(
					FileContentClassification.Text,
					new TextFileContent(
						Content: string.Empty,
						SizeBytes: 0,
						LineCount: 0,
						CharCount: 0,
						IsEmpty: true,
						IsWhitespaceOnly: false,
						IsEstimated: false),
					TextFileEncoding.Utf8);
			}

			var encoding = DetectBomEncoding(stream, cancellationToken);
			if (encoding is null && !CheckForNullBytes(stream, cancellationToken))
				return new FileContentReadResult(FileContentClassification.Binary);

			// For large files, return estimated metrics without full content
			if (sizeBytes > maxSizeForFullRead)
			{
				return new FileContentReadResult(
					FileContentClassification.TooLarge,
					new TextFileContent(
						Content: string.Empty,
						SizeBytes: sizeBytes,
						LineCount: Math.Max(1, (int)(sizeBytes / EstimatedCharsPerLine)),
						CharCount: (int)Math.Min(sizeBytes, int.MaxValue),
						IsEmpty: false,
						IsWhitespaceOnly: false,
						IsEstimated: true,
						TrailingNewlineChars: 0,
						TrailingNewlineLineBreaks: 0),
					ResolveTextEncoding(encoding));
			}

			return new FileContentReadResult(
				FileContentClassification.Text,
				ReadFullContentStrict(
					stream,
					sizeBytes,
					encoding ?? StrictUtf8,
					cancellationToken),
				ResolveTextEncoding(encoding));
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (UnauthorizedAccessException)
		{
			return new FileContentReadResult(FileContentClassification.AccessDenied);
		}
		catch (FileNotFoundException)
		{
			return new FileContentReadResult(FileContentClassification.Missing);
		}
		catch (DirectoryNotFoundException)
		{
			return new FileContentReadResult(FileContentClassification.Missing);
		}
		catch (BinaryContentException)
		{
			return new FileContentReadResult(FileContentClassification.Binary);
		}
		catch (DecoderFallbackException)
		{
			return new FileContentReadResult(FileContentClassification.UnsupportedEncoding);
		}
		catch (IOException)
		{
			return new FileContentReadResult(FileContentClassification.Unreadable);
		}
		catch
		{
			return new FileContentReadResult(FileContentClassification.Unreadable);
		}
	}

	private static TextFileEncoding ResolveTextEncoding(Encoding? bomEncoding)
	{
		if (bomEncoding is null)
			return TextFileEncoding.Utf8;
		if (ReferenceEquals(bomEncoding, StrictUtf8))
			return TextFileEncoding.Utf8Bom;
		if (ReferenceEquals(bomEncoding, StrictUtf16Le))
			return TextFileEncoding.Utf16LittleEndian;
		if (ReferenceEquals(bomEncoding, StrictUtf16Be))
			return TextFileEncoding.Utf16BigEndian;
		if (ReferenceEquals(bomEncoding, StrictUtf32Le))
			return TextFileEncoding.Utf32LittleEndian;
		if (ReferenceEquals(bomEncoding, StrictUtf32Be))
			return TextFileEncoding.Utf32BigEndian;
		throw new ArgumentOutOfRangeException(nameof(bomEncoding));
	}

	/// <summary>
	/// Checks first 512 bytes for null bytes to detect binary content.
	/// Returns true if no null bytes found (text file), false otherwise (binary).
	/// </summary>
	private static bool CheckForNullBytes(FileStream stream, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		try
		{
			stream.Position = 0;
			int toRead = (int)Math.Min(BinaryCheckBufferSize, stream.Length);
			Span<byte> buffer = stackalloc byte[toRead];
			int bytesRead = stream.Read(buffer);

			if (TryResolveBomEncoding(buffer[..bytesRead], out _))
				return true;

			// Span.Contains uses the runtime's vectorized search without changing the
			// established null-byte binary detection contract.
			return !buffer[..bytesRead].Contains((byte)0);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
			return false;
		}
	}

	private static Encoding? DetectBomEncoding(
		FileStream stream,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		stream.Position = 0;
		Span<byte> bom = stackalloc byte[(int)Math.Min(4, stream.Length)];
		var bytesRead = stream.Read(bom);
		stream.Position = 0;
		return TryResolveBomEncoding(bom[..bytesRead], out var encoding)
			? encoding
			: null;
	}

	private static bool TryResolveBomEncoding(ReadOnlySpan<byte> value, out Encoding encoding)
	{
		if (value.Length >= 4 &&
		    value[0] == 0x00 && value[1] == 0x00 &&
		    value[2] == 0xFE && value[3] == 0xFF)
		{
			encoding = StrictUtf32Be;
			return true;
		}
		if (value.Length >= 4 &&
		    value[0] == 0xFF && value[1] == 0xFE &&
		    value[2] == 0x00 && value[3] == 0x00)
		{
			encoding = StrictUtf32Le;
			return true;
		}
		if (value.Length >= 3 &&
		    value[0] == 0xEF && value[1] == 0xBB && value[2] == 0xBF)
		{
			encoding = StrictUtf8;
			return true;
		}
		if (value.Length >= 2 && value[0] == 0xFE && value[1] == 0xFF)
		{
			encoding = StrictUtf16Be;
			return true;
		}
		if (value.Length >= 2 && value[0] == 0xFF && value[1] == 0xFE)
		{
			encoding = StrictUtf16Le;
			return true;
		}

		encoding = null!;
		return false;
	}

	private static FileStream OpenSequentialRead(
		string path,
		int bufferSize,
		FileShare fileShare,
		bool asynchronous = false)
	{
		// Callers keep this handle for length, probing, and decoding. Besides saving an
		// extra open/stat cycle, one handle gives each operation a more coherent file view.
		return new FileStream(
			path,
			FileMode.Open,
			FileAccess.Read,
			fileShare,
			bufferSize,
			FileOptions.SequentialScan |
			(asynchronous ? FileOptions.Asynchronous : FileOptions.None));
	}

	private static bool HasKnownBinaryExtension(string path)
	{
		var extension = Path.GetExtension(path.AsSpan());
		if (extension.IsEmpty)
			return false;

		if (KnownBinaryExtensions.TryGetAlternateLookup<ReadOnlySpan<char>>(out var lookup))
			return lookup.Contains(extension);

		// The ordinal-ignore-case frozen set supports span lookup on current runtimes.
		// Keep a compatibility fallback so an implementation detail cannot change behavior.
		return KnownBinaryExtensions.Contains(extension.ToString());
	}

	/// <summary>
	/// Counts lines and characters by streaming through file.
	/// Does NOT load full content into memory - uses ArrayPool for zero-allocation streaming.
	/// </summary>
	private static TextFileMetrics? CountMetricsStreaming(
		FileStream stream,
		long sizeBytes,
		Encoding encoding,
		CancellationToken cancellationToken,
		bool calculateFingerprint,
		out byte[]? contentFingerprint)
	{
		cancellationToken.ThrowIfCancellationRequested();
		contentFingerprint = null;

		// Rent buffer from pool to avoid allocation per file
		char[] buffer = ArrayPool<char>.Shared.Rent(StreamingBufferSize);
		using var fingerprint = calculateFingerprint
			? IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
			: null;
		try
		{
			var counter = new TextMetricsCounter();

			stream.Position = 0;
			using var reader = new StreamReader(
				stream,
				encoding,
				detectEncodingFromByteOrderMarks: true,
				bufferSize: StreamingBufferSize,
				leaveOpen: true);

			int charsRead;

			while ((charsRead = reader.Read(buffer, 0, StreamingBufferSize)) > 0)
			{
				cancellationToken.ThrowIfCancellationRequested();

				// Use Span for faster iteration without bounds checking
				var span = buffer.AsSpan(0, charsRead);
				fingerprint?.AppendData(MemoryMarshal.AsBytes(span));
				// A null byte after the first 512 bytes still means binary.
				if (!counter.Append(span))
					return null;
			}

			contentFingerprint = fingerprint?.GetHashAndReset();
			return counter.Build(sizeBytes);
		}
		finally
		{
			// Always return buffer to pool
			ArrayPool<char>.Shared.Return(buffer);
		}
	}


	/// <summary>
	/// Metrics for text that is already in memory - the compressed form of a file, which never
	/// exists on disk. It runs the same counter as the streaming path so a transformed file and an
	/// untransformed one are measured identically.
	/// </summary>
	public static TextFileMetrics ComputeMetrics(ReadOnlySpan<char> text, long sizeBytes)
	{
		var counter = new TextMetricsCounter();
		counter.Append(text);
		return counter.Build(sizeBytes);
	}

	/// <summary>
	/// The single per-character pass behind every text metric. Carrying its state across calls is
	/// what lets a CRLF pair split by a read-buffer boundary still count as one line break.
	/// </summary>
	private struct TextMetricsCounter()
	{
		private int _lineCount = 1; // A file with no newlines is one line.
		private int _charCount;
		private bool _hasNonWhitespace;
		private int _crLfPairCount;
		private int _trailingNewlineChars;
		private int _trailingNewlineLineBreaks;
		private bool _previousWasCarriageReturn;
		private int _currentBacktickRun;
		private int _longestBacktickRun;

		/// <summary>Returns false when a null byte proves the content is binary.</summary>
		public bool Append(ReadOnlySpan<char> span)
		{
			for (int i = 0; i < span.Length; i++)
			{
				char c = span[i];

				if (c == '\0')
					return false;

				_charCount++;

				if (c == '\n')
				{
					_lineCount++;
					_trailingNewlineChars++;
					_trailingNewlineLineBreaks++;
					_previousWasCarriageReturn = true;
				}
				else if (c == '\n')
				{
					_trailingNewlineChars++;
					if (_previousWasCarriageReturn)
					{
						// CRLF is one logical line break even when the pair crosses a read-buffer boundary.
						_crLfPairCount++;
					}
					else
					{
						_lineCount++;
						_trailingNewlineLineBreaks++;
					}

					_previousWasCarriageReturn = false;
				}
				else
				{
					_previousWasCarriageReturn = false;
					_trailingNewlineChars = 0;
					_trailingNewlineLineBreaks = 0;
				}

				if (!_hasNonWhitespace && !char.IsWhiteSpace(c))
					_hasNonWhitespace = true;

				if (c == '`')
					_longestBacktickRun = Math.Max(_longestBacktickRun, ++_currentBacktickRun);
				else
					_currentBacktickRun = 0;
			}

			return true;
		}

		public TextFileMetrics Build(long sizeBytes) =>
			new(
				SizeBytes: sizeBytes,
				LineCount: _charCount == 0 ? 0 : _lineCount,
				CharCount: _charCount,
				IsEmpty: _charCount == 0,
				IsWhitespaceOnly: _charCount > 0 && !_hasNonWhitespace,
				IsEstimated: false,
				CrLfPairCount: _crLfPairCount,
				TrailingNewlineChars: _trailingNewlineChars,
				TrailingNewlineLineBreaks: _trailingNewlineLineBreaks,
				LongestBacktickRun: _longestBacktickRun);
	}

	private static TextFileContent ReadFullContentStrict(
		FileStream stream,
		long sizeBytes,
		Encoding encoding,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		stream.Position = 0;
		var contentBuilder = new StringBuilder(
			(int)Math.Min(sizeBytes, 1024 * 1024));
		var buffer = ArrayPool<char>.Shared.Rent(StreamingBufferSize);
		using (var reader = new StreamReader(
			       stream,
			       encoding,
			       detectEncodingFromByteOrderMarks: true,
			       bufferSize: StreamingBufferSize,
			       leaveOpen: true))
		{
			try
			{
				int charactersRead;
				while ((charactersRead = reader.Read(buffer, 0, buffer.Length)) > 0)
				{
					cancellationToken.ThrowIfCancellationRequested();
					if (buffer.AsSpan(0, charactersRead).Contains('\0'))
						throw new BinaryContentException();
					contentBuilder.Append(buffer, 0, charactersRead);
				}
			}
			finally
			{
				ArrayPool<char>.Shared.Return(buffer);
			}
		}

		var content = contentBuilder.ToString();
		var lineCount = content.Length == 0 ? 0 : 1 + CountNormalizedLineBreaks(content);
		var trailingInfo = GetTrailingNewlineInfo(content);
		return new TextFileContent(
			Content: content,
			SizeBytes: sizeBytes,
			LineCount: lineCount,
			CharCount: content.Length,
			IsEmpty: content.Length == 0,
			IsWhitespaceOnly: content.Length > 0 && string.IsNullOrWhiteSpace(content),
			IsEstimated: false,
			TrailingNewlineChars: trailingInfo.Chars,
			TrailingNewlineLineBreaks: trailingInfo.LineBreaks);
	}

	/// <summary>
	/// Counts logical line breaks while treating CRLF as a single break.
	/// </summary>
	private static int CountNormalizedLineBreaks(ReadOnlySpan<char> content)
	{
		int count = 0;
		for (var index = 0; index < content.Length; index++)
		{
			var c = content[index];
			if (c == '\r')
			{
				count++;
				if (index + 1 < content.Length && content[index + 1] == '\n')
					index++;
			}
			else if (c == '\n')
			{
				count++;
			}
		}

		return count;
	}

	private static (int Chars, int LineBreaks) GetTrailingNewlineInfo(string content)
	{
		var start = content.Length;
		while (start > 0 && content[start - 1] is '\r' or '\n')
			start--;

		var trailing = content.AsSpan(start);
		return (trailing.Length, CountNormalizedLineBreaks(trailing));
	}

	private sealed class ClassifiedFileContentSnapshot(
		FileContentMetricsResult result) : IFileContentSnapshot
	{
		public FileContentMetricsResult Result { get; } = result;

		public ValueTask CopyTextToAsync(
			int maximumCharacters,
			Func<ReadOnlyMemory<char>, CancellationToken, ValueTask> writeChunk,
			CancellationToken cancellationToken = default)
		{
			ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
			ArgumentNullException.ThrowIfNull(writeChunk);
			return ValueTask.FromException(
				new IOException("The snapshot does not contain readable text."));
		}

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private sealed class ClassifiedCompleteTextFileBuffer(
		FileContentClassification classification,
		long sizeBytes = 0) : ICompleteTextFileBuffer
	{
		public FileContentClassification Classification { get; } = classification;
		public long SizeBytes { get; } = sizeBytes;
		public ReadOnlyMemory<char> Content => ReadOnlyMemory<char>.Empty;
		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private sealed class PooledCompleteTextFileBuffer(
		char[] buffer,
		int length,
		long sizeBytes) : ICompleteTextFileBuffer
	{
		private char[]? _buffer = buffer;

		public FileContentClassification Classification => FileContentClassification.Text;
		public long SizeBytes { get; } = sizeBytes;
		public ReadOnlyMemory<char> Content => (_buffer ??
			throw new ObjectDisposedException(nameof(PooledCompleteTextFileBuffer))).AsMemory(0, length);

		public ValueTask DisposeAsync()
		{
			var current = Interlocked.Exchange(ref _buffer, null);
			if (current is not null)
			{
				CryptographicOperations.ZeroMemory(
					MemoryMarshal.AsBytes(current.AsSpan(0, length)));
				ArrayPool<char>.Shared.Return(current);
			}
			return ValueTask.CompletedTask;
		}
	}

	private sealed class StreamFileContentSnapshot(
		FileStream stream,
		Encoding encoding,
		TextFileMetrics metrics,
		byte[] contentFingerprint) : IFileContentSnapshot
	{
		private FileStream? _stream = stream;

		public FileContentMetricsResult Result { get; } =
			new(FileContentClassification.Text, metrics);

		public async ValueTask CopyTextToAsync(
			int maximumCharacters,
			Func<ReadOnlyMemory<char>, CancellationToken, ValueTask> writeChunk,
			CancellationToken cancellationToken = default)
		{
			ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
			ArgumentNullException.ThrowIfNull(writeChunk);
			ArgumentOutOfRangeException.ThrowIfGreaterThan(
				maximumCharacters,
				metrics.CharCount);
			var source = _stream ??
			             throw new ObjectDisposedException(
				             nameof(StreamFileContentSnapshot));
			if (source.Length != metrics.SizeBytes)
				throw CreateChangedFileException();

			var buffer = ArrayPool<char>.Shared.Rent(StreamingBufferSize);
			using var fingerprint = IncrementalHash.CreateHash(
				HashAlgorithmName.SHA256);
			try
			{
				source.Position = 0;
				using var reader = new StreamReader(
					source,
					encoding,
					detectEncodingFromByteOrderMarks: true,
					bufferSize: StreamingBufferSize,
					leaveOpen: true);
				var totalCharacters = 0;
				var writtenCharacters = 0;
				var hasPendingHighSurrogate = false;
				var pendingHighSurrogate = '\0';
				while (true)
				{
					cancellationToken.ThrowIfCancellationRequested();
					var prefixLength = hasPendingHighSurrogate ? 1 : 0;
					if (hasPendingHighSurrogate)
						buffer[0] = pendingHighSurrogate;
					var count = await reader
						.ReadAsync(
							buffer.AsMemory(
								prefixLength,
								StreamingBufferSize - prefixLength),
							cancellationToken)
						.ConfigureAwait(false);
					if (count == 0)
					{
						if (prefixLength != 0)
						throw new DecoderFallbackException(
							"The text ends with an unmatched high surrogate.");
						break;
					}

					count += prefixLength;
					hasPendingHighSurrogate = false;
					if (char.IsHighSurrogate(buffer[count - 1]))
					{
						pendingHighSurrogate = buffer[count - 1];
						hasPendingHighSurrogate = true;
						count--;
					}
					if (count == 0)
						continue;

					var chunk = buffer.AsMemory(0, count);
					if (chunk.Span.Contains('\0'))
						throw CreateChangedFileException();
					fingerprint.AppendData(MemoryMarshal.AsBytes(chunk.Span));
					totalCharacters = checked(totalCharacters + count);

					var charactersToWrite = Math.Min(
						count,
						maximumCharacters - writtenCharacters);
					if (charactersToWrite <= 0)
						continue;
					if (charactersToWrite < count &&
					    char.IsHighSurrogate(buffer[charactersToWrite - 1]) &&
					    char.IsLowSurrogate(buffer[charactersToWrite]))
					{
						throw new IOException(
							"The requested snapshot prefix splits a Unicode scalar.");
					}

					await writeChunk(
							chunk[..charactersToWrite],
							cancellationToken)
						.ConfigureAwait(false);
					writtenCharacters += charactersToWrite;
				}

				var currentFingerprint = fingerprint.GetHashAndReset();
				if (totalCharacters != metrics.CharCount ||
				    writtenCharacters != maximumCharacters ||
				    source.Length != metrics.SizeBytes ||
				    !CryptographicOperations.FixedTimeEquals(
					    currentFingerprint,
					    contentFingerprint))
				{
					throw CreateChangedFileException();
				}
			}
			finally
			{
				ArrayPool<char>.Shared.Return(buffer);
			}
		}

		public async ValueTask DisposeAsync()
		{
			var source = Interlocked.Exchange(ref _stream, null);
			if (source is not null)
				await source.DisposeAsync().ConfigureAwait(false);
		}

		private static IOException CreateChangedFileException() =>
			new("The file changed while its snapshot was being streamed.");
	}

	private sealed class BinaryContentException : Exception
	{
	}
}
