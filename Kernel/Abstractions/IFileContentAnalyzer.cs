using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DevProjex.Kernel.Abstractions;

/// <summary>
/// Classifies file content and reads supported text through one shared contract.
/// Binary, encoding, size, access, and transient filesystem failures remain distinct.
/// </summary>
public interface IFileContentAnalyzer
{
	/// <summary>
	/// Returns a definitive classification available from path metadata alone.
	/// A null result means the file must be inspected before it can be classified.
	/// </summary>
	FileContentClassification? ClassifyWithoutReading(string path) => null;

	/// <summary>
	/// Reads a file and preserves the reason why text content is unavailable.
	/// Implementations should prefer this method for user-facing preview and export diagnostics.
	/// </summary>
	async ValueTask<FileContentReadResult> ReadClassifiedAsync(
		string path,
		long maxSizeForFullRead,
		CancellationToken cancellationToken = default)
	{
		var content = await TryReadAsTextAsync(path, maxSizeForFullRead, cancellationToken)
			.ConfigureAwait(false);
		if (content is null)
			return new FileContentReadResult(FileContentClassification.Binary);
		return new FileContentReadResult(
			content.IsEstimated
				? FileContentClassification.TooLarge
				: FileContentClassification.Text,
			content);
	}

	/// <summary>
	/// Determines whether a file contains valid supported text without materializing its content.
	/// </summary>
	/// <param name="path">Absolute path to the file.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>True if file appears to be text, false if binary or on error.</returns>
	ValueTask<bool> IsTextFileAsync(string path, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets metrics for a text file using streaming (no full content in memory).
	/// Returns null if file is binary or cannot be read.
	/// Optimized for status bar metrics - counts lines/chars without storing content.
	/// </summary>
	/// <param name="path">Absolute path to the file.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>File metrics, or null if not a text file.</returns>
	ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(string path, CancellationToken cancellationToken = default);

	/// <summary>
	/// Streams file metrics while preserving why metrics are unavailable.
	/// </summary>
	async ValueTask<FileContentMetricsResult> GetClassifiedMetricsAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		var metrics = await GetTextFileMetricsAsync(path, cancellationToken).ConfigureAwait(false);
		return metrics is null
			? new FileContentMetricsResult(FileContentClassification.Unreadable)
			: new FileContentMetricsResult(
				metrics.IsEstimated
					? FileContentClassification.TooLarge
					: FileContentClassification.Text,
				metrics);
	}

	/// <summary>
	/// Opens one coherent file snapshot for exact document metadata and content.
	/// The snapshot remains valid until disposed and must not reopen the source path
	/// between classification, measurement, and content streaming.
	/// </summary>
	async ValueTask<IFileContentSnapshot> OpenCompleteSnapshotAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		var result = await ReadClassifiedAsync(
				path,
				long.MaxValue,
				cancellationToken)
			.ConfigureAwait(false);
		return new MaterializedFileContentSnapshot(result);
	}

	/// <summary>
	/// Reads a bounded text file into an operation-owned buffer in one pass. Callers that need
	/// random access to the complete text, such as span-based scanners, should use this contract
	/// instead of measuring a snapshot and streaming the same file a second time.
	/// </summary>
	async ValueTask<ICompleteTextFileBuffer> OpenCompleteTextBufferAsync(
		string path,
		long maximumBytes,
		CancellationToken cancellationToken = default)
	{
		var result = await ReadClassifiedAsync(path, maximumBytes, cancellationToken)
			.ConfigureAwait(false);
		return new MaterializedCompleteTextFileBuffer(result);
	}

	/// <summary>
	/// Tries to read file as text content with full content loaded.
	/// Returns null if file is binary or cannot be read.
	/// Use this for export operations where content is needed.
	/// </summary>
	/// <param name="path">Absolute path to the file.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Text content with metrics, or null if not a text file.</returns>
	ValueTask<TextFileContent?> TryReadAsTextAsync(string path, CancellationToken cancellationToken = default);

	/// <summary>
	/// Tries to read file as text content with size limit for large files.
	/// For files exceeding maxSizeForFullRead, returns estimated metrics without full content.
	/// </summary>
	/// <param name="path">Absolute path to the file.</param>
	/// <param name="maxSizeForFullRead">Maximum file size in bytes for full content read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Text content with metrics (may be estimated for large files), or null if not a text file.</returns>
	ValueTask<TextFileContent?> TryReadAsTextAsync(
		string path,
		long maxSizeForFullRead,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Reads one coherent text fact for consumers that need content, exact raw metrics and a stable
	/// content identity. Implementations should calculate all three while decoding the same stream.
	/// </summary>
	async ValueTask<ContentReadFact> ReadFactAsync(
		string path,
		long maxSizeForFullRead,
		CancellationToken cancellationToken = default)
	{
		var result = await ReadClassifiedAsync(path, maxSizeForFullRead, cancellationToken)
			.ConfigureAwait(false);
		return ContentReadFact.FromReadResult(result);
	}
}

public interface ICompleteTextFileBuffer : IAsyncDisposable
{
	FileContentClassification Classification { get; }

	long SizeBytes { get; }

	ReadOnlyMemory<char> Content { get; }
}

internal sealed class MaterializedCompleteTextFileBuffer : ICompleteTextFileBuffer
{
	private readonly string? _content;

	public MaterializedCompleteTextFileBuffer(FileContentReadResult result)
	{
		ArgumentNullException.ThrowIfNull(result);
		Classification = result.Classification;
		SizeBytes = result.Content?.SizeBytes ?? 0;
		_content = result.Classification == FileContentClassification.Text
			? result.Content?.Content
			: null;
	}

	public FileContentClassification Classification { get; }

	public long SizeBytes { get; }

	public ReadOnlyMemory<char> Content => _content?.AsMemory() ?? ReadOnlyMemory<char>.Empty;

	public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public interface IFileContentSnapshot : IAsyncDisposable
{
	FileContentMetricsResult Result { get; }

	ValueTask CopyTextToAsync(
		int maximumCharacters,
		Func<ReadOnlyMemory<char>, CancellationToken, ValueTask> writeChunk,
		CancellationToken cancellationToken = default);
}

internal sealed class MaterializedFileContentSnapshot : IFileContentSnapshot
{
	private const int ChunkSize = 8192;
	private readonly string? _content;

	public MaterializedFileContentSnapshot(FileContentReadResult result)
	{
		ArgumentNullException.ThrowIfNull(result);
		_content = result.Classification == FileContentClassification.Text
			? result.Content?.Content
			: null;
		Result = new FileContentMetricsResult(
			result.Classification,
			result.Content is null
				? null
				: CreateMetrics(result.Content));
	}

	public FileContentMetricsResult Result { get; }

	public async ValueTask CopyTextToAsync(
		int maximumCharacters,
		Func<ReadOnlyMemory<char>, CancellationToken, ValueTask> writeChunk,
		CancellationToken cancellationToken = default)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
		ArgumentNullException.ThrowIfNull(writeChunk);
		if (Result.Classification != FileContentClassification.Text ||
		    _content is null)
		{
			throw new IOException("The snapshot does not contain readable text.");
		}
		if (maximumCharacters > _content.Length)
			throw new IOException("The snapshot contains fewer characters than expected.");

		for (var offset = 0; offset < maximumCharacters; offset += ChunkSize)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var length = Math.Min(ChunkSize, maximumCharacters - offset);
			await writeChunk(
					_content.AsMemory(offset, length),
					cancellationToken)
				.ConfigureAwait(false);
		}
	}

	public ValueTask DisposeAsync() => ValueTask.CompletedTask;

	private static TextFileMetrics CreateMetrics(TextFileContent content) =>
		new(
			content.SizeBytes,
			content.LineCount,
			content.CharCount,
			content.IsEmpty,
			content.IsWhitespaceOnly,
			content.IsEstimated,
			TrailingNewlineChars: content.TrailingNewlineChars,
			TrailingNewlineLineBreaks: content.TrailingNewlineLineBreaks,
			LongestBacktickRun: FindLongestBacktickRun(content.Content));

	private static int FindLongestBacktickRun(string value)
	{
		var current = 0;
		var longest = 0;
		foreach (var character in value)
		{
			if (character == '`')
				longest = Math.Max(longest, ++current);
			else
				current = 0;
		}
		return longest;
	}
}

public enum FileContentClassification
{
	Text,
	Binary,
	TooLarge,
	Unreadable,
	AccessDenied,
	Missing,
	UnsupportedEncoding
}

public enum TextFileEncoding
{
	Utf8 = 0,
	Utf8Bom = 1,
	Utf16LittleEndian = 2,
	Utf16BigEndian = 3,
	Utf32LittleEndian = 4,
	Utf32BigEndian = 5
}

public sealed record FileContentReadResult(
	FileContentClassification Classification,
	TextFileContent? Content = null,
	TextFileEncoding? Encoding = null)
{
	public bool IsText =>
		Classification is FileContentClassification.Text or FileContentClassification.TooLarge;
}

public sealed record FileContentMetricsResult(
	FileContentClassification Classification,
	TextFileMetrics? Metrics = null)
{
	public bool IsText =>
		Classification is FileContentClassification.Text or FileContentClassification.TooLarge;
}

/// <summary>
/// Lightweight metrics for a text file - no content stored.
/// Used for status bar display where content is not needed.
/// </summary>
/// <param name="SizeBytes">File size in bytes.</param>
/// <param name="LineCount">Number of lines in the file.</param>
/// <param name="CharCount">Number of characters in the file.</param>
/// <param name="IsEmpty">True if file has zero bytes.</param>
/// <param name="IsWhitespaceOnly">True if file contains only whitespace characters.</param>
/// <param name="IsEstimated">True if metrics are estimated (content not fully read).</param>
/// <param name="CrLfPairCount">Number of CRLF pairs detected in content.</param>
public sealed record TextFileMetrics(
	long SizeBytes,
	int LineCount,
	int CharCount,
	bool IsEmpty,
	bool IsWhitespaceOnly,
	bool IsEstimated = false,
	int CrLfPairCount = 0,
	int TrailingNewlineChars = 0,
	int TrailingNewlineLineBreaks = 0,
	int LongestBacktickRun = 0);

/// <summary>
/// Full text file content with metrics - content stored for export.
/// </summary>
/// <param name="Content">The text content of the file. Empty string for estimated metrics.</param>
/// <param name="SizeBytes">File size in bytes.</param>
/// <param name="LineCount">Number of lines in the file.</param>
/// <param name="CharCount">Number of characters in the file.</param>
/// <param name="IsEmpty">True if file has zero bytes.</param>
/// <param name="IsWhitespaceOnly">True if file contains only whitespace characters.</param>
/// <param name="IsEstimated">True if metrics are estimated (content not fully read).</param>
public sealed record TextFileContent(
	string Content,
	long SizeBytes,
	int LineCount,
	int CharCount,
	bool IsEmpty,
	bool IsWhitespaceOnly,
	bool IsEstimated = false,
	int TrailingNewlineChars = 0,
	int TrailingNewlineLineBreaks = 0);

/// <summary>A canonical SHA-256 over the UTF-16 text consumed by transformation caches.</summary>
public readonly record struct ContentFingerprint(
	ulong Part0,
	ulong Part1,
	ulong Part2,
	ulong Part3)
{
	public const int ByteLength = SHA256.HashSizeInBytes;

	public static ContentFingerprint Compute(ReadOnlySpan<char> content)
	{
		Span<byte> hash = stackalloc byte[ByteLength];
		SHA256.HashData(MemoryMarshal.AsBytes(content), hash);
		return FromBytes(hash);
	}

	public static ContentFingerprint FromBytes(ReadOnlySpan<byte> hash)
	{
		if (hash.Length != ByteLength)
			throw new ArgumentException($"A SHA-256 fingerprint must contain {ByteLength} bytes.", nameof(hash));
		return new ContentFingerprint(
			BinaryPrimitives.ReadUInt64LittleEndian(hash),
			BinaryPrimitives.ReadUInt64LittleEndian(hash[8..]),
			BinaryPrimitives.ReadUInt64LittleEndian(hash[16..]),
			BinaryPrimitives.ReadUInt64LittleEndian(hash[24..]));
	}

	public void WriteBytes(Span<byte> destination)
	{
		if (destination.Length < ByteLength)
			throw new ArgumentException($"The destination must contain at least {ByteLength} bytes.", nameof(destination));
		BinaryPrimitives.WriteUInt64LittleEndian(destination, Part0);
		BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], Part1);
		BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], Part2);
		BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], Part3);
	}

	public string ToHexString()
	{
		Span<byte> hash = stackalloc byte[ByteLength];
		WriteBytes(hash);
		return Convert.ToHexString(hash);
	}
}

/// <summary>
/// Immutable result of one full decode. The content, metrics and fingerprint always describe the
/// same stream version, which lets downstream phases avoid independent reads and hashes.
/// </summary>
public sealed record ContentReadFact(
	string? Content,
	FileContentClassification Classification,
	TextFileMetrics? RawMetrics,
	ContentFingerprint? Fingerprint,
	TextFileEncoding? Encoding = null)
{
	public bool IsMaterializedText =>
		Classification == FileContentClassification.Text && Content is not null && RawMetrics is not null;

	public long ApproximateRetainedBytes =>
		Content is null ? 128 : 128L + Content.Length * sizeof(char);

	public FileContentReadResult ToReadResult()
	{
		TextFileContent? text = null;
		if (RawMetrics is { } metrics)
		{
			text = new TextFileContent(
				Content ?? string.Empty,
				metrics.SizeBytes,
				metrics.LineCount,
				metrics.CharCount,
				metrics.IsEmpty,
				metrics.IsWhitespaceOnly,
				metrics.IsEstimated,
				metrics.TrailingNewlineChars,
				metrics.TrailingNewlineLineBreaks);
		}
		return new FileContentReadResult(Classification, text, Encoding);
	}

	public static ContentReadFact FromReadResult(FileContentReadResult result)
	{
		var text = result.Content;
		var metrics = text is null
			? null
			: new TextFileMetrics(
				text.SizeBytes,
				text.LineCount,
				text.CharCount,
				text.IsEmpty,
				text.IsWhitespaceOnly,
				text.IsEstimated,
				TrailingNewlineChars: text.TrailingNewlineChars,
				TrailingNewlineLineBreaks: text.TrailingNewlineLineBreaks);
		ContentFingerprint? fingerprint = result.Classification == FileContentClassification.Text && text is not null
			? ContentFingerprint.Compute(text.Content.AsSpan())
			: null;
		return new ContentReadFact(text?.Content, result.Classification, metrics, fingerprint, result.Encoding);
	}
}
