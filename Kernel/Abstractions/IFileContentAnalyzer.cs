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

public sealed record FileContentReadResult(
	FileContentClassification Classification,
	TextFileContent? Content = null)
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
