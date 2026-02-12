using System.Text;
using DevProjex.Kernel.Abstractions;

namespace DevProjex.Application.Services;

/// <summary>
/// Analyzes file content to determine if it's text or binary.
///
/// Detection method: Null-byte detection in first 512 bytes.
/// This is the universal, reliable method that works for ANY binary format:
/// - Images (PNG, JPG, GIF, etc.) - contain null bytes in header/data
/// - Video (MP4, AVI, MKV, etc.) - contain null bytes
/// - Audio (MP3, WAV, FLAC, etc.) - contain null bytes
/// - Archives (ZIP, RAR, 7z, etc.) - contain null bytes
/// - Executables (EXE, DLL, etc.) - contain null bytes
///
/// No extension-based filtering - that would be unreliable and incomplete.
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

	/// <inheritdoc />
	public async Task<bool> IsTextFileAsync(string path, CancellationToken cancellationToken = default)
	{
		try
		{
			var fileInfo = new FileInfo(path);
			if (!fileInfo.Exists)
				return false;

			// Empty files are considered text
			if (fileInfo.Length == 0)
				return true;

			// Check for null bytes in first 512 bytes
			return await Task.Run(() => CheckForNullBytes(path, cancellationToken), cancellationToken)
				.ConfigureAwait(false);
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

	/// <inheritdoc />
	public async Task<TextFileMetrics?> GetTextFileMetricsAsync(string path, CancellationToken cancellationToken = default)
	{
		try
		{
			var fileInfo = new FileInfo(path);
			if (!fileInfo.Exists)
				return null;

			var sizeBytes = fileInfo.Length;

			// Empty file
			if (sizeBytes == 0)
				return new TextFileMetrics(
					SizeBytes: 0,
					LineCount: 0,
					CharCount: 0,
					IsEmpty: true,
					IsWhitespaceOnly: false);

			// Check if binary first (fast - only 512 bytes)
			var isText = await Task.Run(() => CheckForNullBytes(path, cancellationToken), cancellationToken)
				.ConfigureAwait(false);

			if (!isText)
				return null;

			// For very large files, estimate metrics without reading
			if (sizeBytes > DefaultMaxSizeForFullRead)
			{
				return new TextFileMetrics(
					SizeBytes: sizeBytes,
					LineCount: Math.Max(1, (int)(sizeBytes / EstimatedCharsPerLine)),
					CharCount: (int)Math.Min(sizeBytes, int.MaxValue),
					IsEmpty: false,
					IsWhitespaceOnly: false);
			}

			// Stream through file counting metrics without loading content into memory
			return await Task.Run(() => CountMetricsStreaming(path, sizeBytes, cancellationToken), cancellationToken)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
			return null;
		}
	}

	/// <inheritdoc />
	public Task<TextFileContent?> TryReadAsTextAsync(string path, CancellationToken cancellationToken = default)
	{
		return TryReadAsTextAsync(path, DefaultMaxSizeForFullRead, cancellationToken);
	}

	/// <inheritdoc />
	public async Task<TextFileContent?> TryReadAsTextAsync(string path, long maxSizeForFullRead, CancellationToken cancellationToken = default)
	{
		try
		{
			var fileInfo = new FileInfo(path);
			if (!fileInfo.Exists)
				return null;

			var sizeBytes = fileInfo.Length;

			// Empty file
			if (sizeBytes == 0)
				return new TextFileContent(
					Content: string.Empty,
					SizeBytes: 0,
					LineCount: 0,
					CharCount: 0,
					IsEmpty: true,
					IsWhitespaceOnly: false,
					IsEstimated: false);

			// Check if binary (fast - only 512 bytes)
			var isText = await Task.Run(() => CheckForNullBytes(path, cancellationToken), cancellationToken)
				.ConfigureAwait(false);

			if (!isText)
				return null;

			// For large files, return estimated metrics without full content
			if (sizeBytes > maxSizeForFullRead)
			{
				return new TextFileContent(
					Content: string.Empty,
					SizeBytes: sizeBytes,
					LineCount: Math.Max(1, (int)(sizeBytes / EstimatedCharsPerLine)),
					CharCount: (int)Math.Min(sizeBytes, int.MaxValue),
					IsEmpty: false,
					IsWhitespaceOnly: false,
					IsEstimated: true);
			}

			// Read full content for export
			return await Task.Run(() => ReadFullContent(path, sizeBytes, cancellationToken), cancellationToken)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// Checks first 512 bytes for null bytes to detect binary content.
	/// Returns true if no null bytes found (text file), false otherwise (binary).
	/// </summary>
	private static bool CheckForNullBytes(string path, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		try
		{
			using var fs = new FileStream(
				path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite,
				BinaryCheckBufferSize,
				FileOptions.SequentialScan);

			int toRead = (int)Math.Min(BinaryCheckBufferSize, fs.Length);
			Span<byte> buffer = stackalloc byte[toRead];
			int bytesRead = fs.Read(buffer);

			// Check for null bytes - any null byte means binary
			for (int i = 0; i < bytesRead; i++)
			{
				if (buffer[i] == 0)
					return false;
			}

			return true;
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

	/// <summary>
	/// Counts lines and characters by streaming through file.
	/// Does NOT load full content into memory - only uses a small buffer.
	/// </summary>
	private static TextFileMetrics? CountMetricsStreaming(string path, long sizeBytes, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		try
		{
			int lineCount = 1; // Start with 1 (file with no newlines = 1 line)
			int charCount = 0;
			bool hasNonWhitespace = false;

			using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: StreamingBufferSize);

			var buffer = new char[StreamingBufferSize];
			int charsRead;

			while ((charsRead = reader.Read(buffer, 0, buffer.Length)) > 0)
			{
				cancellationToken.ThrowIfCancellationRequested();

				for (int i = 0; i < charsRead; i++)
				{
					char c = buffer[i];

					// Null byte in content = binary file (edge case after first 512 bytes)
					if (c == '\0')
						return null;

					charCount++;

					if (c == '\n')
						lineCount++;

					if (!hasNonWhitespace && !char.IsWhiteSpace(c))
						hasNonWhitespace = true;
				}
			}

			// Adjust line count: if file is empty, 0 lines
			if (charCount == 0)
				lineCount = 0;

			return new TextFileMetrics(
				SizeBytes: sizeBytes,
				LineCount: lineCount,
				CharCount: charCount,
				IsEmpty: charCount == 0,
				IsWhitespaceOnly: charCount > 0 && !hasNonWhitespace);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// Reads full file content for export operations.
	/// Content is loaded into memory - use only when content is needed.
	/// </summary>
	private static TextFileContent? ReadFullContent(string path, long sizeBytes, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		try
		{
			string content;
			using (var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
			{
				content = reader.ReadToEnd();
			}

			// Check for null bytes (edge case: null after first 512 bytes)
			if (content.Contains('\0'))
				return null;

			bool isWhitespaceOnly = string.IsNullOrWhiteSpace(content);
			int lineCount = content.Length == 0 ? 0 : 1 + CountNewlines(content);

			return new TextFileContent(
				Content: content,
				SizeBytes: sizeBytes,
				LineCount: lineCount,
				CharCount: content.Length,
				IsEmpty: content.Length == 0,
				IsWhitespaceOnly: isWhitespaceOnly,
				IsEstimated: false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// Counts newline characters efficiently.
	/// </summary>
	private static int CountNewlines(string content)
	{
		int count = 0;
		foreach (char c in content.AsSpan())
		{
			if (c == '\n')
				count++;
		}
		return count;
	}
}
