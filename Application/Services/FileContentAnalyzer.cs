using System.Buffers;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using DevProjex.Application.Diagnostics;

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
public sealed class FileContentAnalyzer :
	IFileContentAnalyzer,
	IPrewarmFileContentAnalyzer,
	ICoherentFileContentAnalyzer
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
		encoderShouldEmitUTF8Identifier: true,
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
	private readonly Func<string, int, FileShare, bool, FileStream> _openSequentialRead;

	public FileContentAnalyzer()
		: this(OpenSequentialRead)
	{
	}

	internal FileContentAnalyzer(Func<string, int, FileShare, bool, FileStream> openSequentialRead)
	{
		ArgumentNullException.ThrowIfNull(openSequentialRead);
		_openSequentialRead = openSequentialRead;
	}

	public FileContentClassification? ClassifyWithoutReading(string path)
	{
		try
		{
			if (!UnixFileTypeInspector.IsRegularFile(path))
				return FileContentClassification.Unreadable;
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or SecurityException)
		{
			return null;
		}

		return HasKnownBinaryExtension(path)
			? FileContentClassification.Binary
			: null;
	}

	public ValueTask<FileContentReadResult> ReadClassifiedAsync(
		string path,
		long maxSizeForFullRead,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromResult(
			ReadFactWithIdentitySync(path, maxSizeForFullRead, cancellationToken).Fact.ToReadResult());

	public ValueTask<ContentReadFact> ReadFactAsync(
		string path,
		long maxSizeForFullRead,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromResult(ReadFactWithIdentitySync(path, maxSizeForFullRead, cancellationToken).Fact);

	ValueTask<IdentifiedContentReadFact> ICoherentFileContentAnalyzer.ReadFactWithIdentityAsync(
		string path,
		long maximumReadBytes,
		CancellationToken cancellationToken) =>
		ValueTask.FromResult(ReadFactWithIdentitySync(path, maximumReadBytes, cancellationToken));

	/// <inheritdoc />
	public ValueTask<bool> IsTextFileAsync(string path, CancellationToken cancellationToken = default)
	{
		var result = GetClassifiedMetricsWithIdentitySync(path, cancellationToken).Result;
		return ValueTask.FromResult(result.IsText);
	}

	/// <inheritdoc />
	public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		var result = GetClassifiedMetricsWithIdentitySync(path, cancellationToken).Result;
		return ValueTask.FromResult(result.IsText ? result.Metrics : null);
	}

	public ValueTask<FileContentMetricsResult> GetClassifiedMetricsAsync(
		string path,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromResult(GetClassifiedMetricsWithIdentitySync(path, cancellationToken).Result);

	ValueTask<IdentifiedFileContentMetricsResult>
		IPrewarmFileContentAnalyzer.GetClassifiedMetricsWithIdentityAsync(
			string path,
			CancellationToken cancellationToken) =>
		ValueTask.FromResult(GetClassifiedMetricsWithIdentitySync(path, cancellationToken));

	async ValueTask<BudgetedContentReadResult>
		IPrewarmFileContentAnalyzer.ReadFactWithBudgetAsync(
			string path,
			long maximumReadBytes,
			WeightedByteBudget byteBudget,
			SemaphoreSlim decodeScratchGate,
			CancellationToken cancellationToken)
	{
		WeightedByteBudget.Lease? lease = null;
		var enteredDecodeGate = false;
		try
		{
			if (HasKnownBinaryExtension(path))
			{
				UnixFileTypeInspector.EnsureRegularFile(path);
				return new BudgetedContentReadResult(
					new ContentReadFact(null, FileContentClassification.Binary, null, null),
					null,
					null);
			}

			using var stream = _openSequentialRead(path, StreamingBufferSize, SourceFileReadPolicy.Share, false);
			var reservation = EstimateMaximumRetainedFactBytes(stream.Length, maximumReadBytes);
			lease = await byteBudget.AcquireAsync(reservation, cancellationToken).ConfigureAwait(false);
			await decodeScratchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
			enteredDecodeGate = true;
			var fact = ReadFactFromOpenStream(stream, maximumReadBytes, cancellationToken);
			Debug.Assert(fact.ApproximateRetainedBytes <= reservation);
			return new BudgetedContentReadResult(
				fact,
				FileContentIdentity.TryCapture(stream),
				lease);
		}
		catch (OperationCanceledException)
		{
			lease?.Dispose();
			throw;
		}
		catch (UnauthorizedAccessException)
		{
			return Failure(FileContentClassification.AccessDenied, lease);
		}
		catch (SecurityException)
		{
			return Failure(FileContentClassification.AccessDenied, lease);
		}
		catch (FileNotFoundException)
		{
			return Failure(FileContentClassification.Missing, lease);
		}
		catch (DirectoryNotFoundException)
		{
			return Failure(FileContentClassification.Missing, lease);
		}
		catch (BinaryContentException)
		{
			return Failure(FileContentClassification.Binary, lease);
		}
		catch (DecoderFallbackException)
		{
			return Failure(FileContentClassification.UnsupportedEncoding, lease);
		}
		catch (IOException)
		{
			return Failure(FileContentClassification.Unreadable, lease);
		}
		finally
		{
			if (enteredDecodeGate)
				decodeScratchGate.Release();
		}
	}

	private static BudgetedContentReadResult Failure(
		FileContentClassification classification,
		WeightedByteBudget.Lease? lease) =>
		new(new ContentReadFact(null, classification, null, null), null, lease);

	private static long EstimateMaximumRetainedFactBytes(long sizeBytes, long maximumReadBytes) =>
		sizeBytes > maximumReadBytes
			? 128
			: checked(128 + sizeBytes * sizeof(char));

	public ValueTask<IFileContentSnapshot> OpenCompleteSnapshotAsync(
		string path,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromResult(OpenCompleteSnapshotSync(path, cancellationToken));

	public ValueTask<ICompleteTextFileBuffer> OpenCompleteTextBufferAsync(
		string path,
		long maximumBytes,
		CancellationToken cancellationToken = default) =>
		ValueTask.FromResult(OpenCompleteTextBufferWithIdentitySync(path, maximumBytes, cancellationToken).Buffer);

	ValueTask<IdentifiedCompleteTextFileBuffer>
		ICoherentFileContentAnalyzer.OpenCompleteTextBufferWithIdentityAsync(
			string path,
			long maximumBytes,
			CancellationToken cancellationToken) =>
		ValueTask.FromResult(OpenCompleteTextBufferWithIdentitySync(path, maximumBytes, cancellationToken));

	private IdentifiedCompleteTextFileBuffer OpenCompleteTextBufferWithIdentitySync(
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
			{
				UnixFileTypeInspector.EnsureRegularFile(path);
				return new IdentifiedCompleteTextFileBuffer(
					new ClassifiedCompleteTextFileBuffer(FileContentClassification.Binary),
					null);
			}

			using var stream = _openSequentialRead(
				path,
				1,
				SourceFileReadPolicy.Share,
				false);
			var sizeBytes = stream.Length;
			if (sizeBytes == 0)
			{
				return IdentifiedBuffer(
					stream,
					new ClassifiedCompleteTextFileBuffer(FileContentClassification.Text));
			}

			var bomEncoding = DetectBomEncoding(stream, cancellationToken);
			var encoding = bomEncoding ?? StrictUtf8;
			if (!CheckForNullBytes(stream, cancellationToken))
			{
				return IdentifiedBuffer(
					stream,
					new ClassifiedCompleteTextFileBuffer(FileContentClassification.Binary, sizeBytes));
			}
			// The scan limit is a text-buffer bound, not a project-copy size limit.
			// Classify known and null-bearing binaries first so large binary assets still
			// pass through folder and ZIP exports unchanged.
			if (sizeBytes > maximumBytes || sizeBytes > int.MaxValue)
			{
				return IdentifiedBuffer(
					stream,
					new ClassifiedCompleteTextFileBuffer(
						FileContentClassification.TooLarge,
						sizeBytes));
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
			ContentPipelineDiagnostics.RecordFullFileRead(sizeBytes);
			if (buffer.AsSpan(0, written).Contains('\0'))
			{
				return IdentifiedBuffer(
					stream,
					new ClassifiedCompleteTextFileBuffer(FileContentClassification.Binary, sizeBytes));
			}

			var result = new PooledCompleteTextFileBuffer(buffer, written, sizeBytes);
			buffer = null;
			written = 0;
			return IdentifiedBuffer(stream, result);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (UnauthorizedAccessException)
		{
			return UnidentifiedBuffer(FileContentClassification.AccessDenied);
		}
		catch (FileNotFoundException)
		{
			return UnidentifiedBuffer(FileContentClassification.Missing);
		}
		catch (DirectoryNotFoundException)
		{
			return UnidentifiedBuffer(FileContentClassification.Missing);
		}
		catch (DecoderFallbackException)
		{
			return UnidentifiedBuffer(FileContentClassification.UnsupportedEncoding);
		}
		catch (SecurityException)
		{
			return UnidentifiedBuffer(FileContentClassification.AccessDenied);
		}
		catch (IOException)
		{
			return UnidentifiedBuffer(FileContentClassification.Unreadable);
		}
		finally
		{
			if (buffer is not null)
			{
				ClearSensitiveCharacterBuffer(buffer);
				ArrayPool<char>.Shared.Return(buffer);
			}
		}
	}

	private static IdentifiedCompleteTextFileBuffer IdentifiedBuffer(
		FileStream stream,
		ICompleteTextFileBuffer buffer) =>
		new(buffer, FileContentIdentity.TryCapture(stream));

	private static IdentifiedCompleteTextFileBuffer UnidentifiedBuffer(
		FileContentClassification classification) =>
		new(new ClassifiedCompleteTextFileBuffer(classification), null);

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

	private IdentifiedFileContentMetricsResult GetClassifiedMetricsWithIdentitySync(
		string path,
		CancellationToken cancellationToken)
	{
		try
		{
			if (HasKnownBinaryExtension(path))
			{
				UnixFileTypeInspector.EnsureRegularFile(path);
				return new IdentifiedFileContentMetricsResult(
					new FileContentMetricsResult(FileContentClassification.Binary),
					null);
			}

			// Decoding owns pooled byte/char buffers; a second FileStream buffer only duplicates memory.
			using var stream = _openSequentialRead(path, 1, SourceFileReadPolicy.Share, false);
			var sizeBytes = stream.Length;

			if (sizeBytes == 0)
			{
				return Identified(
					stream,
					new FileContentMetricsResult(
						FileContentClassification.Text,
						new TextFileMetrics(
						SizeBytes: 0,
						LineCount: 0,
						CharCount: 0,
						IsEmpty: true,
						IsWhitespaceOnly: false,
						IsEstimated: false,
						CrLfPairCount: 0)));
			}

			var encoding = DetectBomEncoding(stream, cancellationToken);
			if (encoding is null && !CheckForNullBytes(stream, cancellationToken))
				return Identified(
					stream,
					new FileContentMetricsResult(FileContentClassification.Binary));

			if (sizeBytes > DefaultMaxSizeForFullRead)
			{
				return Identified(
					stream,
					new FileContentMetricsResult(
						FileContentClassification.TooLarge,
						new TextFileMetrics(
						SizeBytes: sizeBytes,
						LineCount: EstimateLineCount(sizeBytes),
						CharCount: (int)Math.Min(sizeBytes, int.MaxValue),
						IsEmpty: false,
						IsWhitespaceOnly: false,
						IsEstimated: true,
						CrLfPairCount: 0,
						TrailingNewlineChars: 0,
						TrailingNewlineLineBreaks: 0)));
			}

			var metrics = CountMetricsStreaming(
				stream,
				sizeBytes,
				encoding ?? StrictUtf8,
				cancellationToken,
				calculateFingerprint: false,
				out _);
			return Identified(
				stream,
				metrics is null
					? new FileContentMetricsResult(FileContentClassification.Binary)
					: new FileContentMetricsResult(FileContentClassification.Text, metrics));
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (UnauthorizedAccessException)
		{
			return new IdentifiedFileContentMetricsResult(
				new FileContentMetricsResult(FileContentClassification.AccessDenied),
				null);
		}
		catch (SecurityException)
		{
			return new IdentifiedFileContentMetricsResult(
				new FileContentMetricsResult(FileContentClassification.AccessDenied),
				null);
		}
		catch (FileNotFoundException)
		{
			return new IdentifiedFileContentMetricsResult(
				new FileContentMetricsResult(FileContentClassification.Missing),
				null);
		}
		catch (DirectoryNotFoundException)
		{
			return new IdentifiedFileContentMetricsResult(
				new FileContentMetricsResult(FileContentClassification.Missing),
				null);
		}
		catch (DecoderFallbackException)
		{
			return new IdentifiedFileContentMetricsResult(
				new FileContentMetricsResult(FileContentClassification.UnsupportedEncoding),
				null);
		}
		catch (IOException)
		{
			return new IdentifiedFileContentMetricsResult(
				new FileContentMetricsResult(FileContentClassification.Unreadable),
				null);
		}
	}

	private static IdentifiedFileContentMetricsResult Identified(
		FileStream stream,
		FileContentMetricsResult result) =>
		new(result, FileContentIdentity.TryCapture(stream));

	private IFileContentSnapshot OpenCompleteSnapshotSync(
		string path,
		CancellationToken cancellationToken)
	{
		FileStream? stream = null;
		try
		{
			if (HasKnownBinaryExtension(path))
			{
				UnixFileTypeInspector.EnsureRegularFile(path);
				return new ClassifiedFileContentSnapshot(
					new FileContentMetricsResult(FileContentClassification.Binary));
			}

			stream = _openSequentialRead(
				path,
				StreamingBufferSize,
				SourceFileReadPolicy.Share,
				true);
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
		var result = ReadFactWithIdentitySync(path, maxSizeForFullRead, cancellationToken).Fact.ToReadResult();
		return result.IsText ? result.Content : null;
	}

	private IdentifiedContentReadFact ReadFactWithIdentitySync(
		string path,
		long maxSizeForFullRead,
		CancellationToken cancellationToken)
	{
		try
		{
			// Fast path: known binary extensions - no file I/O needed
			if (HasKnownBinaryExtension(path))
			{
				UnixFileTypeInspector.EnsureRegularFile(path);
				return new IdentifiedContentReadFact(
					new ContentReadFact(null, FileContentClassification.Binary, null, null),
					null);
			}

			using var stream = _openSequentialRead(path, StreamingBufferSize, SourceFileReadPolicy.Share, false);
			var fact = ReadFactFromOpenStream(stream, maxSizeForFullRead, cancellationToken);
			return new IdentifiedContentReadFact(fact, FileContentIdentity.TryCapture(stream));
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (UnauthorizedAccessException)
		{
			return IdentifiedContentReadFact.Unidentified(FileContentClassification.AccessDenied);
		}
		catch (SecurityException)
		{
			return IdentifiedContentReadFact.Unidentified(FileContentClassification.AccessDenied);
		}
		catch (FileNotFoundException)
		{
			return IdentifiedContentReadFact.Unidentified(FileContentClassification.Missing);
		}
		catch (DirectoryNotFoundException)
		{
			return IdentifiedContentReadFact.Unidentified(FileContentClassification.Missing);
		}
		catch (BinaryContentException)
		{
			return IdentifiedContentReadFact.Unidentified(FileContentClassification.Binary);
		}
		catch (DecoderFallbackException)
		{
			return IdentifiedContentReadFact.Unidentified(FileContentClassification.UnsupportedEncoding);
		}
		catch (IOException)
		{
			return IdentifiedContentReadFact.Unidentified(FileContentClassification.Unreadable);
		}
	}

	private static ContentReadFact ReadFactFromOpenStream(
		FileStream stream,
		long maxSizeForFullRead,
		CancellationToken cancellationToken)
	{
		var sizeBytes = stream.Length;
		if (sizeBytes == 0)
		{
			ContentPipelineDiagnostics.RecordContentFingerprint();
			return new ContentReadFact(
				string.Empty,
				FileContentClassification.Text,
				new TextFileMetrics(0, 0, 0, true, false),
				ContentFingerprint.Compute(ReadOnlySpan<char>.Empty),
				TextFileEncoding.Utf8);
		}

		var encoding = DetectBomEncoding(stream, cancellationToken);
		if (encoding is null && !CheckForNullBytes(stream, cancellationToken))
			return new ContentReadFact(null, FileContentClassification.Binary, null, null);

		if (sizeBytes > maxSizeForFullRead)
		{
			return new ContentReadFact(
				null,
				FileContentClassification.TooLarge,
				new TextFileMetrics(
					sizeBytes,
					EstimateLineCount(sizeBytes),
					(int)Math.Min(sizeBytes, int.MaxValue),
					false,
					false,
					IsEstimated: true),
				null,
				ResolveTextEncoding(encoding));
		}

		return ReadFullContentStrict(
			stream,
			sizeBytes,
			encoding ?? StrictUtf8,
			ResolveTextEncoding(encoding),
			cancellationToken);
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

	internal static int EstimateLineCount(long sizeBytes) =>
		(int)Math.Clamp(sizeBytes / EstimatedCharsPerLine, 1L, int.MaxValue);

	/// <summary>
	/// Checks first 512 bytes for null bytes to detect binary content.
	/// Returns true if no null bytes found (text file), false otherwise (binary).
	/// </summary>
	private static bool CheckForNullBytes(FileStream stream, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
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
		UnixFileTypeInspector.EnsureRegularFile(path);
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
		ContentPipelineDiagnostics.RecordFullFileRead(sizeBytes);
		contentFingerprint = null;

		byte[]? byteBuffer = null;
		char[]? charBuffer = null;
		using var fingerprint = calculateFingerprint
			? IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
			: null;
		try
		{
			var counter = new TextMetricsCounter();
			var bomEncoding = DetectBomEncoding(stream, cancellationToken);
			var effectiveEncoding = bomEncoding is null
				? encoding
				: ResolveBomFallbackEncoding(bomEncoding);
			var decoder = effectiveEncoding.GetDecoder();
			byteBuffer = ArrayPool<byte>.Shared.Rent(StreamingBufferSize);
			charBuffer = ArrayPool<char>.Shared.Rent(
				Math.Max(1, effectiveEncoding.GetMaxCharCount(StreamingBufferSize)));
			stream.Position = GetPreambleLength(bomEncoding);

			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var bytesRead = stream.Read(byteBuffer, 0, StreamingBufferSize);
				if (bytesRead == 0)
					break;

				var bytesConsumed = 0;
				while (bytesConsumed < bytesRead)
				{
					decoder.Convert(
						byteBuffer.AsSpan(bytesConsumed, bytesRead - bytesConsumed),
						charBuffer,
						flush: false,
						out var bytesUsed,
						out var charsUsed,
						out _);
					if (bytesUsed == 0 && charsUsed == 0)
						throw new IOException("The streaming decoder made no progress.");
					bytesConsumed += bytesUsed;
					if (!AppendDecodedMetrics(
							ref counter,
							fingerprint,
							charBuffer.AsSpan(0, charsUsed)))
					{
						return null;
					}
				}
			}

			while (true)
			{
				decoder.Convert(
					ReadOnlySpan<byte>.Empty,
					charBuffer,
					flush: true,
					out _,
					out var charsUsed,
					out var completed);
				if (!AppendDecodedMetrics(
						ref counter,
						fingerprint,
						charBuffer.AsSpan(0, charsUsed)))
				{
					return null;
				}
				if (completed)
					break;
				if (charsUsed == 0)
					throw new IOException("The streaming decoder could not flush its pending state.");
			}

			contentFingerprint = fingerprint?.GetHashAndReset();
			if (calculateFingerprint)
				ContentPipelineDiagnostics.RecordContentFingerprint();
			return counter.Build(sizeBytes);
		}
		finally
		{
			if (byteBuffer is not null)
			{
				CryptographicOperations.ZeroMemory(byteBuffer.AsSpan());
				ArrayPool<byte>.Shared.Return(byteBuffer);
			}
			if (charBuffer is not null)
			{
				CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(charBuffer.AsSpan()));
				ArrayPool<char>.Shared.Return(charBuffer);
			}
		}
	}

	private static bool AppendDecodedMetrics(
		ref TextMetricsCounter counter,
		IncrementalHash? fingerprint,
		ReadOnlySpan<char> characters)
	{
		if (characters.IsEmpty)
			return true;
		// A null character after the initial binary probe still makes the file binary.
		if (!counter.Append(characters))
			return false;
		fingerprint?.AppendData(MemoryMarshal.AsBytes(characters));
		return true;
	}

	internal static void ClearSensitiveCharacterBuffer(char[] buffer)
	{
		ArgumentNullException.ThrowIfNull(buffer);
		CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(buffer.AsSpan()));
	}

	private static Encoding ResolveBomFallbackEncoding(Encoding bomEncoding)
	{
		if (ReferenceEquals(bomEncoding, StrictUtf8) ||
		    ReferenceEquals(bomEncoding, StrictUtf16Le) ||
		    ReferenceEquals(bomEncoding, StrictUtf16Be) ||
		    ReferenceEquals(bomEncoding, StrictUtf32Le) ||
		    ReferenceEquals(bomEncoding, StrictUtf32Be))
		{
			return bomEncoding;
		}
		throw new ArgumentOutOfRangeException(nameof(bomEncoding));
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
	/// Measures the exact text described by a validated compression plan without allocating that
	/// text. The encoder and metric counter both retain state across splice boundaries, so CRLF and
	/// surrogate pairs split by adjacent chunks behave exactly like the materialized output.
	/// </summary>
	public static TextFileMetrics ComputeTransformedMetrics(
		ReadOnlySpan<char> source,
		CodeCompressionPlan plan)
	{
		ArgumentNullException.ThrowIfNull(plan);
		if (source.Length != plan.SourceLength)
		{
			throw new ArgumentException(
				$"The plan was built for {plan.SourceLength} characters but the text has {source.Length}.",
				nameof(source));
		}

		var counter = new TextMetricsCounter();
		var encoder = Encoding.UTF8.GetEncoder();
		Span<byte> byteBuffer = stackalloc byte[512];
		long utf8Bytes = 0;
		var sourceCursor = 0;
		foreach (var edit in plan.Edits)
		{
			AppendTransformedMetricsChunk(
				ref counter,
				encoder,
				source[sourceCursor..edit.SourceStart],
				byteBuffer,
				flush: false,
				ref utf8Bytes);
			AppendTransformedMetricsChunk(
				ref counter,
				encoder,
				edit.Replacement.AsSpan(),
				byteBuffer,
				flush: false,
				ref utf8Bytes);
			sourceCursor = edit.SourceEnd;
		}
		AppendTransformedMetricsChunk(
			ref counter,
			encoder,
			source[sourceCursor..],
			byteBuffer,
			flush: false,
			ref utf8Bytes);
		AppendTransformedMetricsChunk(
			ref counter,
			encoder,
			ReadOnlySpan<char>.Empty,
			byteBuffer,
			flush: true,
			ref utf8Bytes);
		var metrics = counter.Build(utf8Bytes);
		Debug.Assert(metrics.CharCount == plan.TransformedLength);
		return metrics;
	}

	private static void AppendTransformedMetricsChunk(
		ref TextMetricsCounter counter,
		Encoder encoder,
		ReadOnlySpan<char> chunk,
		Span<byte> byteBuffer,
		bool flush,
		ref long utf8Bytes)
	{
		counter.Append(chunk);
		do
		{
			encoder.Convert(
				chunk,
				byteBuffer,
				flush,
				out var charactersUsed,
				out var bytesUsed,
				out var completed);
			utf8Bytes += bytesUsed;
			chunk = chunk[charactersUsed..];
			if (completed)
				return;
			if (charactersUsed == 0 && bytesUsed == 0)
				throw new InvalidOperationException("UTF-8 metric encoding made no progress.");
		} while (true);
	}

	/// <summary>
	/// The single per-character pass behind every text metric. Carrying its state across calls is
	/// what lets a CRLF pair split by a read-buffer boundary still count as one line break.
	/// </summary>
	internal struct TextMetricsCounter()
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

				if (c == '\r')
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

	private static ContentReadFact ReadFullContentStrict(
		FileStream stream,
		long sizeBytes,
		Encoding encoding,
		TextFileEncoding textEncoding,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		ContentPipelineDiagnostics.RecordFullFileRead(sizeBytes);
		stream.Position = 0;
		var maximumCharacters = encoding.GetMaxCharCount(checked((int)sizeBytes));
		var buffer = ArrayPool<char>.Shared.Rent(Math.Max(1, maximumCharacters));
		var charactersWritten = 0;
		var counter = new TextMetricsCounter();
		using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		using (var reader = new StreamReader(
		       stream,
			       encoding,
			       detectEncodingFromByteOrderMarks: true,
			       bufferSize: StreamingBufferSize,
			       leaveOpen: true))
		{
			try
			{
				while (true)
				{
					cancellationToken.ThrowIfCancellationRequested();
					var charactersRead = reader.Read(
						buffer,
						charactersWritten,
						buffer.Length - charactersWritten);
					if (charactersRead == 0)
						break;
					var chunk = buffer.AsSpan(charactersWritten, charactersRead);
					if (!counter.Append(chunk))
						throw new BinaryContentException();
					fingerprint.AppendData(MemoryMarshal.AsBytes(chunk));
					charactersWritten = checked(charactersWritten + charactersRead);
				}

				var content = new string(buffer, 0, charactersWritten);
				var rawMetrics = counter.Build(sizeBytes);
				var contentFingerprint = ContentFingerprint.FromBytes(fingerprint.GetHashAndReset());
				ContentPipelineDiagnostics.RecordContentFingerprint();
				return new ContentReadFact(
					content,
					FileContentClassification.Text,
					rawMetrics,
					contentFingerprint,
					textEncoding);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(buffer.AsSpan()));
				ArrayPool<char>.Shared.Return(buffer);
			}
		}
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
				ClearSensitiveCharacterBuffer(current);
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
			ContentPipelineDiagnostics.RecordFullFileRead(metrics.SizeBytes);
			ContentPipelineDiagnostics.RecordContentFingerprint();

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
				CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(buffer.AsSpan()));
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
