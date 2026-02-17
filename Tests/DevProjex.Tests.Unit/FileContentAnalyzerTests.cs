using System.Text;
using DevProjex.Application.Services;
using DevProjex.Kernel.Abstractions;
using DevProjex.Tests.Unit.Helpers;

namespace DevProjex.Tests.Unit;

/// <summary>
/// Tests for FileContentAnalyzer - the single source of truth for text file detection.
/// </summary>
public sealed class FileContentAnalyzerTests
{
	private readonly IFileContentAnalyzer _analyzer = new FileContentAnalyzer();

	#region IsTextFileAsync Tests

	[Fact]
	public async Task IsTextFileAsync_TextFile_ReturnsTrue()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("text.txt", "Hello World");

		var result = await _analyzer.IsTextFileAsync(file);

		Assert.True(result);
	}

	[Fact]
	public async Task IsTextFileAsync_EmptyFile_ReturnsTrue()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("empty.txt", string.Empty);

		var result = await _analyzer.IsTextFileAsync(file);

		Assert.True(result);
	}

	[Fact]
	public async Task IsTextFileAsync_BinaryFile_ReturnsFalse()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateBinaryFile("binary.bin", new byte[] { 0x00, 0x01, 0x02 });

		var result = await _analyzer.IsTextFileAsync(file);

		Assert.False(result);
	}

	[Fact]
	public async Task IsTextFileAsync_BinaryFileWithNullInMiddle_ReturnsFalse()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateBinaryFile("mixed.bin", new byte[] { 0x48, 0x65, 0x00, 0x6C, 0x6C, 0x6F }); // "He\0llo"

		var result = await _analyzer.IsTextFileAsync(file);

		Assert.False(result);
	}

	[Fact]
	public async Task IsTextFileAsync_MissingFile_ReturnsFalse()
	{
		var result = await _analyzer.IsTextFileAsync("/nonexistent/file.txt");

		Assert.False(result);
	}

	[Fact]
	public async Task IsTextFileAsync_WhitespaceOnlyFile_ReturnsTrue()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("whitespace.txt", "   \n\t  ");

		var result = await _analyzer.IsTextFileAsync(file);

		Assert.True(result);
	}

	[Fact]
	public async Task IsTextFileAsync_UnicodeTextFile_ReturnsTrue()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("unicode.txt", "Привет мир! 你好世界");

		var result = await _analyzer.IsTextFileAsync(file);

		Assert.True(result);
	}

	[Fact]
	public async Task IsTextFileAsync_CancellationRequested_ThrowsOperationCanceledException()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("text.txt", "Hello");
		var cts = new CancellationTokenSource();
		cts.Cancel();

		// TaskCanceledException inherits from OperationCanceledException
		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => _analyzer.IsTextFileAsync(file, cts.Token));
	}

	#endregion

	#region TryReadAsTextAsync Tests

	[Fact]
	public async Task TryReadAsTextAsync_TextFile_ReturnsContent()
	{
		using var temp = new TemporaryDirectory();
		var content = "Hello World\nLine 2";
		var file = temp.CreateFile("text.txt", content);

		var result = await _analyzer.TryReadAsTextAsync(file);

		Assert.NotNull(result);
		Assert.Equal(content, result.Content);
		Assert.False(result.IsEmpty);
		Assert.False(result.IsWhitespaceOnly);
		Assert.False(result.IsEstimated);
	}

	[Fact]
	public async Task TryReadAsTextAsync_TextFile_CalculatesCorrectLineCount()
	{
		using var temp = new TemporaryDirectory();
		var content = "Line 1\nLine 2\nLine 3";
		var file = temp.CreateFile("text.txt", content);

		var result = await _analyzer.TryReadAsTextAsync(file);

		Assert.NotNull(result);
		Assert.Equal(3, result.LineCount);
	}

	[Fact]
	public async Task TryReadAsTextAsync_SingleLineFile_ReturnsLineCountOne()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("single.txt", "Single line");

		var result = await _analyzer.TryReadAsTextAsync(file);

		Assert.NotNull(result);
		Assert.Equal(1, result.LineCount);
	}

	[Fact]
	public async Task TryReadAsTextAsync_EmptyFile_ReturnsIsEmpty()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("empty.txt", string.Empty);

		var result = await _analyzer.TryReadAsTextAsync(file);

		Assert.NotNull(result);
		Assert.True(result.IsEmpty);
		Assert.Equal(0, result.SizeBytes);
		Assert.Equal(0, result.LineCount);
		Assert.Equal(0, result.CharCount);
		Assert.Equal(string.Empty, result.Content);
	}

	[Fact]
	public async Task TryReadAsTextAsync_WhitespaceOnlyFile_ReturnsIsWhitespaceOnly()
	{
		using var temp = new TemporaryDirectory();
		var content = "   \n\t  ";
		var file = temp.CreateFile("whitespace.txt", content);

		var result = await _analyzer.TryReadAsTextAsync(file);

		Assert.NotNull(result);
		Assert.True(result.IsWhitespaceOnly);
		Assert.False(result.IsEmpty);
	}

	[Fact]
	public async Task TryReadAsTextAsync_BinaryFile_ReturnsNull()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateBinaryFile("binary.bin", new byte[] { 0x00, 0x01, 0x02 });

		var result = await _analyzer.TryReadAsTextAsync(file);

		Assert.Null(result);
	}

	[Fact]
	public async Task TryReadAsTextAsync_FileWithNullBytesAfterFirst8KB_ReturnsNull()
	{
		using var temp = new TemporaryDirectory();
		// Create file with valid text in first 8KB, then null byte
		var builder = new StringBuilder();
		for (int i = 0; i < 9000; i++)
			builder.Append('A');

		var textPart = builder.ToString();
		var bytes = Encoding.UTF8.GetBytes(textPart);
		var withNull = new byte[bytes.Length + 1];
		Array.Copy(bytes, withNull, bytes.Length);
		withNull[^1] = 0; // Null byte at the end

		var file = temp.CreateBinaryFile("hidden_binary.txt", withNull);

		var result = await _analyzer.TryReadAsTextAsync(file);

		Assert.Null(result);
	}

	[Fact]
	public async Task TryReadAsTextAsync_MissingFile_ReturnsNull()
	{
		var result = await _analyzer.TryReadAsTextAsync("/nonexistent/file.txt");

		Assert.Null(result);
	}

	[Fact]
	public async Task TryReadAsTextAsync_LargeFile_ReturnsEstimatedMetrics()
	{
		using var temp = new TemporaryDirectory();
		// Create file larger than 1MB (using small maxSize for test)
		var content = new string('A', 100);
		var file = temp.CreateFile("large.txt", content);

		// Use very small maxSizeForFullRead to trigger estimation
		var result = await _analyzer.TryReadAsTextAsync(file, maxSizeForFullRead: 10);

		Assert.NotNull(result);
		Assert.True(result.IsEstimated);
		Assert.Equal(string.Empty, result.Content); // Content not read for estimated
	}

	[Fact]
	public async Task TryReadAsTextAsync_ReturnsCorrectCharCount()
	{
		using var temp = new TemporaryDirectory();
		var content = "Hello";
		var file = temp.CreateFile("text.txt", content);

		var result = await _analyzer.TryReadAsTextAsync(file);

		Assert.NotNull(result);
		Assert.Equal(5, result.CharCount);
	}

	[Fact]
	public async Task TryReadAsTextAsync_ReturnsCorrectSizeBytes()
	{
		using var temp = new TemporaryDirectory();
		var content = "Hello";
		var file = temp.CreateFile("text.txt", content);

		var result = await _analyzer.TryReadAsTextAsync(file);

		Assert.NotNull(result);
		Assert.Equal(5, result.SizeBytes);
	}

	[Fact]
	public async Task TryReadAsTextAsync_UnicodeFile_ReturnsCorrectMetrics()
	{
		using var temp = new TemporaryDirectory();
		var content = "Привет"; // 6 characters, 12 bytes in UTF-8
		var file = temp.CreateFile("unicode.txt", content);

		var result = await _analyzer.TryReadAsTextAsync(file);

		Assert.NotNull(result);
		Assert.Equal(6, result.CharCount);
		Assert.Equal(content, result.Content);
	}

	[Fact]
	public async Task TryReadAsTextAsync_CancellationRequested_ThrowsOperationCanceledException()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("text.txt", "Hello");
		var cts = new CancellationTokenSource();
		cts.Cancel();

		// TaskCanceledException inherits from OperationCanceledException
		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => _analyzer.TryReadAsTextAsync(file, cts.Token));
	}

	#endregion

	#region Known Binary Extensions (Fast Path)

	[Theory]
	[InlineData(".png")]
	[InlineData(".jpg")]
	[InlineData(".jpeg")]
	[InlineData(".gif")]
	[InlineData(".mp4")]
	[InlineData(".mp3")]
	[InlineData(".exe")]
	[InlineData(".dll")]
	[InlineData(".zip")]
	[InlineData(".pdf")]
	[InlineData(".docx")]
	public async Task IsTextFileAsync_KnownBinaryExtension_ReturnsFalseWithoutReadingFile(string extension)
	{
		// File doesn't need to exist - extension check happens first
		var fakePath = $"/nonexistent/file{extension}";

		var result = await _analyzer.IsTextFileAsync(fakePath);

		Assert.False(result);
	}

	[Theory]
	[InlineData(".png")]
	[InlineData(".jpg")]
	[InlineData(".mp4")]
	[InlineData(".exe")]
	[InlineData(".zip")]
	[InlineData(".pdf")]
	public async Task TryReadAsTextAsync_KnownBinaryExtension_ReturnsNullWithoutReadingFile(string extension)
	{
		// File doesn't need to exist - extension check happens first
		var fakePath = $"/nonexistent/file{extension}";

		var result = await _analyzer.TryReadAsTextAsync(fakePath);

		Assert.Null(result);
	}

	[Theory]
	[InlineData(".PNG")] // uppercase
	[InlineData(".Jpg")] // mixed case
	[InlineData(".MP4")] // uppercase
	public async Task IsTextFileAsync_KnownBinaryExtension_CaseInsensitive(string extension)
	{
		var fakePath = $"/nonexistent/file{extension}";

		var result = await _analyzer.IsTextFileAsync(fakePath);

		Assert.False(result);
	}

	[Fact]
	public async Task TryReadAsTextAsync_RealPngFile_ReturnsNull()
	{
		using var temp = new TemporaryDirectory();
		// PNG signature + IHDR chunk length (contains null bytes like real PNG files)
		// Real PNG files always have null bytes in IHDR chunk length field
		var pngBytes = new byte[]
		{
			0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG signature
			0x00, 0x00, 0x00, 0x0D, // IHDR chunk length (13 bytes) - has null bytes
			0x49, 0x48, 0x44, 0x52  // IHDR chunk type
		};
		var file = temp.CreateBinaryFile("image.png", pngBytes);

		var result = await _analyzer.TryReadAsTextAsync(file);

		Assert.Null(result);
	}

	[Fact]
	public async Task TryReadAsTextAsync_RealJpgFile_ReturnsNull()
	{
		using var temp = new TemporaryDirectory();
		// JPEG header bytes
		var jpgHeader = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 };
		var file = temp.CreateBinaryFile("image.jpg", jpgHeader);

		var result = await _analyzer.TryReadAsTextAsync(file);

		Assert.Null(result);
	}

	#endregion

	#region Edge Cases

	[Theory]
	[InlineData("\n")]
	[InlineData("\r\n")]
	[InlineData("\n\n\n")]
	public async Task TryReadAsTextAsync_NewlinesOnly_ReturnsWhitespaceOnly(string content)
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("newlines.txt", content);

		var result = await _analyzer.TryReadAsTextAsync(file);

		Assert.NotNull(result);
		Assert.True(result.IsWhitespaceOnly);
	}

	[Fact]
	public async Task TryReadAsTextAsync_FileWithBOM_ReadsCorrectly()
	{
		using var temp = new TemporaryDirectory();
		var content = "Hello";
		var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
		var bytes = encoding.GetBytes(content);
		var bom = encoding.GetPreamble();
		var withBom = new byte[bom.Length + bytes.Length];
		Array.Copy(bom, withBom, bom.Length);
		Array.Copy(bytes, 0, withBom, bom.Length, bytes.Length);

		var file = temp.CreateBinaryFile("bom.txt", withBom);

		var result = await _analyzer.TryReadAsTextAsync(file);

		Assert.NotNull(result);
		Assert.Equal(content, result.Content);
	}

	[Fact]
	public async Task TryReadAsTextAsync_TrailingNewline_CountsCorrectLines()
	{
		using var temp = new TemporaryDirectory();
		var content = "Line 1\nLine 2\n";
		var file = temp.CreateFile("trailing.txt", content);

		var result = await _analyzer.TryReadAsTextAsync(file);

		Assert.NotNull(result);
		Assert.Equal(3, result.LineCount); // "Line 1", "Line 2", and empty line after
	}

	[Fact]
	public async Task TryReadAsTextAsync_WindowsLineEndings_CountsCorrectLines()
	{
		using var temp = new TemporaryDirectory();
		var content = "Line 1\r\nLine 2\r\nLine 3";
		var file = temp.CreateFile("windows.txt", content);

		var result = await _analyzer.TryReadAsTextAsync(file);

		Assert.NotNull(result);
		Assert.Equal(3, result.LineCount);
	}

	[Fact]
	public async Task TryReadAsTextAsync_MixedLineEndings_CountsNewlinesOnly()
	{
		using var temp = new TemporaryDirectory();
		var content = "Line 1\nLine 2\r\nLine 3\rLine 4";
		var file = temp.CreateFile("mixed.txt", content);

		var result = await _analyzer.TryReadAsTextAsync(file);

		Assert.NotNull(result);
		// Counts \n only: after "Line 1", after "Line 2\r", and none more
		// So: "Line 1\n" (1), "Line 2\r\n" (1), "Line 3\rLine 4" (0) = 3 lines total (1 + newline count)
		Assert.Equal(3, result.LineCount);
	}

	#endregion
}
