namespace DevProjex.Kernel.Abstractions;

/// <summary>
/// Analyzes file content to determine if it's text or binary.
/// Single source of truth for text file detection across the application.
/// Uses null-byte detection as the universal, reliable method.
/// </summary>
public interface IFileContentAnalyzer
{
	/// <summary>
	/// Quickly checks if a file contains text content (not binary).
	/// Reads only the first 512 bytes to detect null bytes - sufficient for any binary format.
	/// This is the fastest check with minimal I/O.
	/// </summary>
	/// <param name="path">Absolute path to the file.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>True if file appears to be text, false if binary or on error.</returns>
	Task<bool> IsTextFileAsync(string path, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gets metrics for a text file using streaming (no full content in memory).
	/// Returns null if file is binary or cannot be read.
	/// Optimized for status bar metrics - counts lines/chars without storing content.
	/// </summary>
	/// <param name="path">Absolute path to the file.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>File metrics, or null if not a text file.</returns>
	Task<TextFileMetrics?> GetTextFileMetricsAsync(string path, CancellationToken cancellationToken = default);

	/// <summary>
	/// Tries to read file as text content with full content loaded.
	/// Returns null if file is binary or cannot be read.
	/// Use this for export operations where content is needed.
	/// </summary>
	/// <param name="path">Absolute path to the file.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Text content with metrics, or null if not a text file.</returns>
	Task<TextFileContent?> TryReadAsTextAsync(string path, CancellationToken cancellationToken = default);

	/// <summary>
	/// Tries to read file as text content with size limit for large files.
	/// For files exceeding maxSizeForFullRead, returns estimated metrics without full content.
	/// </summary>
	/// <param name="path">Absolute path to the file.</param>
	/// <param name="maxSizeForFullRead">Maximum file size in bytes for full content read.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>Text content with metrics (may be estimated for large files), or null if not a text file.</returns>
	Task<TextFileContent?> TryReadAsTextAsync(string path, long maxSizeForFullRead, CancellationToken cancellationToken = default);
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
public sealed record TextFileMetrics(
	long SizeBytes,
	int LineCount,
	int CharCount,
	bool IsEmpty,
	bool IsWhitespaceOnly,
	int TrailingNewlineChars = 0,
	int TrailingNewlineLineBreaks = 0);

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
