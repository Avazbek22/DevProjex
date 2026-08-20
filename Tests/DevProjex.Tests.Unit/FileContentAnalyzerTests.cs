using DevProjex.Application.Compression;

namespace DevProjex.Tests.Unit;

/// <summary>
/// Tests for FileContentAnalyzer - the single source of truth for text file detection.
/// </summary>
public sealed class FileContentAnalyzerTests
{
	private readonly IFileContentAnalyzer _analyzer = new FileContentAnalyzer();

	[Theory]
	[InlineData(ProbeOperation.CompleteTextBuffer)]
	[InlineData(ProbeOperation.StreamingMetrics)]
	[InlineData(ProbeOperation.CompleteSnapshot)]
	[InlineData(ProbeOperation.ReadFact)]
	public async Task NullByteProbe_IoFailureIsUnreadableRatherThanBinary(ProbeOperation operation)
	{
		using var temp = new TemporaryDirectory();
		var path = temp.CreateFile("probe.txt", "ordinary text");
		var analyzer = new FileContentAnalyzer(
			(filePath, _, _, _) => new ProbeFailureFileStream(filePath));

		var classification = await ClassifyAsync(analyzer, path, operation);

		Assert.Equal(FileContentClassification.Unreadable, classification);
		Assert.NotEqual(FileContentClassification.Binary, classification);
	}

	[Theory]
	[InlineData(ProbeOperation.CompleteTextBuffer)]
	[InlineData(ProbeOperation.StreamingMetrics)]
	[InlineData(ProbeOperation.CompleteSnapshot)]
	[InlineData(ProbeOperation.ReadFact)]
	public async Task UnexpectedReadFailurePropagates(ProbeOperation operation)
	{
		using var temp = new TemporaryDirectory();
		var path = temp.CreateFile("unexpected.txt", "ordinary text");
		var analyzer = new FileContentAnalyzer(
			static (_, _, _, _) => throw new InvalidOperationException("unexpected failure"));

		var exception = await Assert.ThrowsAsync<InvalidOperationException>(
			() => ClassifyAsync(analyzer, path, operation));

		Assert.Equal("unexpected failure", exception.Message);
	}

	[Fact]
	public async Task PrewarmRead_UnexpectedFailurePropagates()
	{
		var analyzer = new FileContentAnalyzer(
			static (_, _, _, _) => throw new InvalidOperationException("unexpected prewarm failure"));
		using var byteBudget = new WeightedByteBudget(1024);
		using var decodeGate = new SemaphoreSlim(1, 1);

		var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
			await ((IPrewarmFileContentAnalyzer)analyzer).ReadFactWithBudgetAsync(
				"ignored.txt",
				maximumReadBytes: 512,
				byteBudget,
				decodeGate,
				TestContext.Current.CancellationToken));

		Assert.Equal("unexpected prewarm failure", exception.Message);
	}

	private static async Task<FileContentClassification> ClassifyAsync(
		FileContentAnalyzer analyzer,
		string path,
		ProbeOperation operation)
	{
		switch (operation)
		{
			case ProbeOperation.CompleteTextBuffer:
				await using (var buffer = await analyzer.OpenCompleteTextBufferAsync(
				             path,
				             maximumBytes: 1024,
				             TestContext.Current.CancellationToken))
				{
					return buffer.Classification;
				}
			case ProbeOperation.StreamingMetrics:
				return (await analyzer.GetClassifiedMetricsAsync(
					path,
					TestContext.Current.CancellationToken)).Classification;
			case ProbeOperation.CompleteSnapshot:
				await using (var snapshot = await analyzer.OpenCompleteSnapshotAsync(
				             path,
				             TestContext.Current.CancellationToken))
				{
					return snapshot.Result.Classification;
				}
			case ProbeOperation.ReadFact:
				return (await analyzer.ReadFactAsync(
					path,
					maxSizeForFullRead: 1024,
					TestContext.Current.CancellationToken)).Classification;
			default:
				throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
		}
	}

	#region IsTextFileAsync Tests

	[Fact]
	public async Task IsTextFileAsync_TextFile_ReturnsTrue()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("text.txt", "Hello World");

		var result = await _analyzer.IsTextFileAsync(file, cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(result);
	}

	[Fact]
	public async Task IsTextFileAsync_EmptyFile_ReturnsTrue()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("empty.txt", string.Empty);

		var result = await _analyzer.IsTextFileAsync(file, cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(result);
	}

	[Fact]
	public async Task IsTextFileAsync_BinaryFile_ReturnsFalse()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateBinaryFile("binary.bin", [0x00, 0x01, 0x02]);

		var result = await _analyzer.IsTextFileAsync(file, cancellationToken: TestContext.Current.CancellationToken);

		Assert.False(result);
	}

	[Fact]
	public async Task IsTextFileAsync_BinaryFileWithNullInMiddle_ReturnsFalse()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateBinaryFile("mixed.bin", [0x48, 0x65, 0x00, 0x6C, 0x6C, 0x6F]); // "He\0llo"

		var result = await _analyzer.IsTextFileAsync(file, cancellationToken: TestContext.Current.CancellationToken);

		Assert.False(result);
	}

	[Fact]
	public async Task IsTextFileAsync_MissingFile_ReturnsFalse()
	{
		var result = await _analyzer.IsTextFileAsync("/nonexistent/file.txt", cancellationToken: TestContext.Current.CancellationToken);

		Assert.False(result);
	}

	[Fact]
	public async Task IsTextFileAsync_WhitespaceOnlyFile_ReturnsTrue()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("whitespace.txt", "   \n\t  ");

		var result = await _analyzer.IsTextFileAsync(file, cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(result);
	}

	[Fact]
	public async Task IsTextFileAsync_UnicodeTextFile_ReturnsTrue()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("unicode.txt", "Привет мир! 你好世界");

		var result = await _analyzer.IsTextFileAsync(file, cancellationToken: TestContext.Current.CancellationToken);

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
			() => _analyzer.IsTextFileAsync(file, cts.Token).AsTask());
	}

	#endregion

	#region TryReadAsTextAsync Tests

	[Fact]
	public async Task TryReadAsTextAsync_TextFile_ReturnsContent()
	{
		using var temp = new TemporaryDirectory();
		var content = "Hello World\nLine 2";
		var file = temp.CreateFile("text.txt", content);

		var result = await _analyzer.TryReadAsTextAsync(file, cancellationToken: TestContext.Current.CancellationToken);

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

		var result = await _analyzer.TryReadAsTextAsync(file, cancellationToken: TestContext.Current.CancellationToken);

		Assert.NotNull(result);
		Assert.Equal(3, result.LineCount);
	}

	[Fact]
	public async Task TryReadAsTextAsync_SingleLineFile_ReturnsLineCountOne()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("single.txt", "Single line");

		var result = await _analyzer.TryReadAsTextAsync(file, cancellationToken: TestContext.Current.CancellationToken);

		Assert.NotNull(result);
		Assert.Equal(1, result.LineCount);
	}

	[Fact]
	public async Task TryReadAsTextAsync_EmptyFile_ReturnsIsEmpty()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("empty.txt", string.Empty);

		var result = await _analyzer.TryReadAsTextAsync(file, cancellationToken: TestContext.Current.CancellationToken);

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

		var result = await _analyzer.TryReadAsTextAsync(file, cancellationToken: TestContext.Current.CancellationToken);

		Assert.NotNull(result);
		Assert.True(result.IsWhitespaceOnly);
		Assert.False(result.IsEmpty);
	}

	[Fact]
	public async Task TryReadAsTextAsync_BinaryFile_ReturnsNull()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateBinaryFile("binary.bin", [0x00, 0x01, 0x02]);

		var result = await _analyzer.TryReadAsTextAsync(file, cancellationToken: TestContext.Current.CancellationToken);
		var classified = await _analyzer.ReadClassifiedAsync(
			file,
			long.MaxValue,
			TestContext.Current.CancellationToken);

		Assert.Null(result);
		Assert.Equal(FileContentClassification.Binary, classified.Classification);
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

		var result = await _analyzer.TryReadAsTextAsync(file, cancellationToken: TestContext.Current.CancellationToken);
		var classified = await _analyzer.ReadClassifiedAsync(
			file,
			long.MaxValue,
			TestContext.Current.CancellationToken);

		Assert.Null(result);
		Assert.Equal(FileContentClassification.Binary, classified.Classification);
	}

	[Fact]
	public async Task TryReadAsTextAsync_MissingFile_ReturnsNull()
	{
		var result = await _analyzer.TryReadAsTextAsync("/nonexistent/file.txt", cancellationToken: TestContext.Current.CancellationToken);

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
		var result = await _analyzer.TryReadAsTextAsync(file, maxSizeForFullRead: 10, cancellationToken: TestContext.Current.CancellationToken);

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

		var result = await _analyzer.TryReadAsTextAsync(file, cancellationToken: TestContext.Current.CancellationToken);

		Assert.NotNull(result);
		Assert.Equal(5, result.CharCount);
	}

	[Fact]
	public async Task TryReadAsTextAsync_ReturnsCorrectSizeBytes()
	{
		using var temp = new TemporaryDirectory();
		var content = "Hello";
		var file = temp.CreateFile("text.txt", content);

		var result = await _analyzer.TryReadAsTextAsync(file, cancellationToken: TestContext.Current.CancellationToken);

		Assert.NotNull(result);
		Assert.Equal(5, result.SizeBytes);
	}

	[Fact]
	public async Task TryReadAsTextAsync_UnicodeFile_ReturnsCorrectMetrics()
	{
		using var temp = new TemporaryDirectory();
		var content = "Привет"; // 6 characters, 12 bytes in UTF-8
		var file = temp.CreateFile("unicode.txt", content);

		var result = await _analyzer.TryReadAsTextAsync(file, cancellationToken: TestContext.Current.CancellationToken);

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
			() => _analyzer.TryReadAsTextAsync(file, cts.Token).AsTask());
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

		var result = await _analyzer.IsTextFileAsync(fakePath, cancellationToken: TestContext.Current.CancellationToken);

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

		var result = await _analyzer.TryReadAsTextAsync(fakePath, cancellationToken: TestContext.Current.CancellationToken);

		Assert.Null(result);
	}

	[Theory]
	[InlineData(".PNG")] // uppercase
	[InlineData(".Jpg")] // mixed case
	[InlineData(".MP4")] // uppercase
	public async Task IsTextFileAsync_KnownBinaryExtension_CaseInsensitive(string extension)
	{
		var fakePath = $"/nonexistent/file{extension}";

		var result = await _analyzer.IsTextFileAsync(fakePath, cancellationToken: TestContext.Current.CancellationToken);

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

		var result = await _analyzer.TryReadAsTextAsync(file, cancellationToken: TestContext.Current.CancellationToken);

		Assert.Null(result);
	}

	[Fact]
	public async Task TryReadAsTextAsync_RealJpgFile_ReturnsNull()
	{
		using var temp = new TemporaryDirectory();
		// JPEG header bytes
		var jpgHeader = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 };
		var file = temp.CreateBinaryFile("image.jpg", jpgHeader);

		var result = await _analyzer.TryReadAsTextAsync(file, cancellationToken: TestContext.Current.CancellationToken);

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

		var result = await _analyzer.TryReadAsTextAsync(file, cancellationToken: TestContext.Current.CancellationToken);

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

		var result = await _analyzer.TryReadAsTextAsync(file, cancellationToken: TestContext.Current.CancellationToken);

		Assert.NotNull(result);
		Assert.Equal(content, result.Content);
	}

	[Fact]
	public async Task SupportedBomEncodingsAreClassifiedAndDecodedAsText()
	{
		using var temp = new TemporaryDirectory();
		const string source = "namespace EncodingFixture;\ninternal sealed class Пример { }";
		(string Name, Encoding Encoding)[] encodings =
		[
			("utf8-bom", new UTF8Encoding(true, true)),
			("utf16-le", new UnicodeEncoding(false, true, true)),
			("utf16-be", new UnicodeEncoding(true, true, true)),
			("utf32-le", new UTF32Encoding(false, true, true)),
			("utf32-be", new UTF32Encoding(true, true, true))
		];

		foreach (var fixture in encodings)
		{
			var path = Path.Combine(temp.Path, $"{fixture.Name}.cs");
			var payload = fixture.Encoding.GetPreamble()
				.Concat(fixture.Encoding.GetBytes(source))
				.ToArray();
			File.WriteAllBytes(path, payload);

			var classified = await _analyzer.ReadClassifiedAsync(
				path,
				long.MaxValue,
				TestContext.Current.CancellationToken);
			var fact = await _analyzer.ReadFactAsync(
				path,
				long.MaxValue,
				TestContext.Current.CancellationToken);
			var metrics = await _analyzer.GetTextFileMetricsAsync(
				path,
				TestContext.Current.CancellationToken);
			var classifiedMetrics = await _analyzer.GetClassifiedMetricsAsync(
				path,
				TestContext.Current.CancellationToken);

			Assert.Equal(FileContentClassification.Text, classified.Classification);
			Assert.Equal(FileContentClassification.Text, classifiedMetrics.Classification);
			Assert.Equal(source, classified.Content?.Content);
			Assert.Equal(source, fact.Content);
			Assert.Equal(
				FileContentAnalyzer.ComputeMetrics(source, payload.Length),
				fact.RawMetrics);
			Assert.Equal(ContentFingerprint.Compute(source), fact.Fingerprint);
			Assert.True(await _analyzer.IsTextFileAsync(
				path,
				TestContext.Current.CancellationToken));
			Assert.Equal(source.Length, metrics?.CharCount);
			Assert.Equal(source.Length, classifiedMetrics.Metrics?.CharCount);
		}
	}

	[Fact]
	public async Task SvgUsesContentClassificationInsteadOfBinaryExtensionShortcut()
	{
		using var temp = new TemporaryDirectory();
		const string svg = "<svg xmlns=\"http://www.w3.org/2000/svg\"><text>Пример</text></svg>";
		var path = temp.CreateFile("diagram.svg", svg);

		var result = await _analyzer.ReadClassifiedAsync(
			path,
			long.MaxValue,
			TestContext.Current.CancellationToken);

		Assert.Equal(FileContentClassification.Text, result.Classification);
		Assert.Equal(svg, result.Content?.Content);
	}

	[Fact]
	public async Task ClassifiedReadPreservesUnavailableContentReasons()
	{
		using var temp = new TemporaryDirectory();
		var binary = temp.CreateBinaryFile("payload.custom", [0x41, 0x00, 0x42]);
		var tooLarge = temp.CreateFile("large.cs", "class LargeFixture { }");
		var invalidUtf8 = temp.CreateBinaryFile("invalid.cs", [0xC3, 0x28]);
		var missing = Path.Combine(temp.Path, "missing.cs");

		var binaryResult = await _analyzer.ReadClassifiedAsync(
			binary,
			long.MaxValue,
			TestContext.Current.CancellationToken);
		var tooLargeResult = await _analyzer.ReadClassifiedAsync(
			tooLarge,
			1,
			TestContext.Current.CancellationToken);
		var invalidResult = await _analyzer.ReadClassifiedAsync(
			invalidUtf8,
			long.MaxValue,
			TestContext.Current.CancellationToken);
		var missingResult = await _analyzer.ReadClassifiedAsync(
			missing,
			long.MaxValue,
			TestContext.Current.CancellationToken);
		var binaryMetrics = await _analyzer.GetClassifiedMetricsAsync(
			binary,
			TestContext.Current.CancellationToken);
		var invalidMetrics = await _analyzer.GetClassifiedMetricsAsync(
			invalidUtf8,
			TestContext.Current.CancellationToken);
		var missingMetrics = await _analyzer.GetClassifiedMetricsAsync(
			missing,
			TestContext.Current.CancellationToken);

		Assert.Equal(FileContentClassification.Binary, binaryResult.Classification);
		Assert.Equal(FileContentClassification.TooLarge, tooLargeResult.Classification);
		Assert.True(tooLargeResult.Content?.IsEstimated);
		Assert.Equal(FileContentClassification.UnsupportedEncoding, invalidResult.Classification);
		Assert.Equal(FileContentClassification.Missing, missingResult.Classification);
		Assert.Equal(FileContentClassification.Binary, binaryMetrics.Classification);
		Assert.Equal(FileContentClassification.UnsupportedEncoding, invalidMetrics.Classification);
		Assert.Equal(FileContentClassification.Missing, missingMetrics.Classification);
		Assert.Null(binaryMetrics.Metrics);
		Assert.Null(invalidMetrics.Metrics);
		Assert.Null(missingMetrics.Metrics);
	}

	[Fact]
	public async Task TryReadAsTextAsync_TrailingNewline_CountsCorrectLines()
	{
		using var temp = new TemporaryDirectory();
		var content = "Line 1\nLine 2\n";
		var file = temp.CreateFile("trailing.txt", content);

		var result = await _analyzer.TryReadAsTextAsync(file, cancellationToken: TestContext.Current.CancellationToken);

		Assert.NotNull(result);
		Assert.Equal(3, result.LineCount); // "Line 1", "Line 2", and empty line after
	}

	[Fact]
	public async Task TryReadAsTextAsync_WindowsLineEndings_CountsCorrectLines()
	{
		using var temp = new TemporaryDirectory();
		var content = "Line 1\r\nLine 2\r\nLine 3";
		var file = temp.CreateFile("windows.txt", content);

		var result = await _analyzer.TryReadAsTextAsync(file, cancellationToken: TestContext.Current.CancellationToken);

		Assert.NotNull(result);
		Assert.Equal(3, result.LineCount);
	}

	[Fact]
	public async Task TryReadAsTextAsync_MixedLineEndings_CountsLogicalLineBreaks()
	{
		using var temp = new TemporaryDirectory();
		var content = "Line 1\nLine 2\r\nLine 3\rLine 4";
		var file = temp.CreateFile("mixed.txt", content);

		var result = await _analyzer.TryReadAsTextAsync(file, cancellationToken: TestContext.Current.CancellationToken);

		Assert.NotNull(result);
		Assert.Equal(4, result.LineCount);
	}

	[Fact]
	public async Task GetTextFileMetricsAsync_CrLfAcrossStreamingBufferBoundary_CountsSingleLineBreak()
	{
		using var temp = new TemporaryDirectory();
		var content = new string('a', 8191) + "\r\nb";
		var file = temp.CreateFile("buffer-boundary.txt", content);

		var result = await _analyzer.GetTextFileMetricsAsync(
			file,
			TestContext.Current.CancellationToken);

		Assert.NotNull(result);
		Assert.Equal(2, result.LineCount);
		Assert.Equal(content.Length, result.CharCount);
		Assert.Equal(1, result.CrLfPairCount);
		Assert.Equal(0, result.TrailingNewlineChars);
		Assert.Equal(0, result.TrailingNewlineLineBreaks);
	}

	[Theory]
	[InlineData("utf8", false, "empty")]
	[InlineData("utf8", true, "empty")]
	[InlineData("utf8", false, "mixed")]
	[InlineData("utf8", true, "mixed")]
	[InlineData("utf16-le", true, "mixed")]
	[InlineData("utf16-be", true, "mixed")]
	[InlineData("utf32-le", true, "mixed")]
	[InlineData("utf32-be", true, "mixed")]
	public async Task StreamingMetrics_MatchMaterializedMetrics_AcrossStrictEncodingMatrix(
		string encodingId,
		bool emitBom,
		string contentId)
	{
		using var temp = new TemporaryDirectory();
		var content = contentId switch
		{
			"empty" => string.Empty,
			"mixed" => "alpha\r\nПривет\n世界😀\r",
			_ => throw new ArgumentOutOfRangeException(nameof(contentId))
		};
		var encoding = CreateStrictEncoding(encodingId, emitBom);
		var payload = encoding.GetPreamble()
			.Concat(encoding.GetBytes(content))
			.ToArray();
		var path = temp.CreateBinaryFile($"{encodingId}-{contentId}.txt", payload);

		var streaming = await _analyzer.GetClassifiedMetricsAsync(
			path,
			TestContext.Current.CancellationToken);
		var materialized = await _analyzer.ReadFactAsync(
			path,
			long.MaxValue,
			TestContext.Current.CancellationToken);
		await using var snapshot = await _analyzer.OpenCompleteSnapshotAsync(
			path,
			TestContext.Current.CancellationToken);
		var copied = new StringBuilder();
		await snapshot.CopyTextToAsync(
			content.Length,
			(chunk, _) =>
			{
				copied.Append(chunk.Span);
				return ValueTask.CompletedTask;
			},
			TestContext.Current.CancellationToken);

		var expected = FileContentAnalyzer.ComputeMetrics(content, payload.Length);
		Assert.Equal(FileContentClassification.Text, streaming.Classification);
		Assert.Equal(FileContentClassification.Text, materialized.Classification);
		Assert.Equal(FileContentClassification.Text, snapshot.Result.Classification);
		Assert.Equal(content, materialized.Content);
		Assert.Equal(content, copied.ToString());
		Assert.Equal(expected, streaming.Metrics);
		Assert.Equal(expected, materialized.RawMetrics);
		Assert.Equal(expected, snapshot.Result.Metrics);
	}

	[Fact]
	public async Task StreamingMetrics_Utf8ScalarAcrossByteBufferBoundary_MatchesMaterializedMetrics()
	{
		using var temp = new TemporaryDirectory();
		var content = new string('a', 8190) + "😀\r\nnext";
		var path = temp.CreateBinaryFile("split-scalar.txt", new UTF8Encoding(false, true).GetBytes(content));

		var streaming = await _analyzer.GetClassifiedMetricsAsync(
			path,
			TestContext.Current.CancellationToken);
		var materialized = await _analyzer.ReadFactAsync(
			path,
			long.MaxValue,
			TestContext.Current.CancellationToken);

		Assert.Equal(FileContentClassification.Text, streaming.Classification);
		Assert.Equal(content, materialized.Content);
		Assert.Equal(materialized.RawMetrics, streaming.Metrics);
		Assert.Equal(ContentFingerprint.Compute(content), materialized.Fingerprint);
	}

	[Fact]
	public async Task StreamingMetrics_InvalidSequencesMatchMaterializedStrictFallbackClassification()
	{
		using var temp = new TemporaryDirectory();
		var invalidPayloads = new Dictionary<string, byte[]>(StringComparer.Ordinal)
		{
			["utf8"] = [0xC3, 0x28],
			["utf8-incomplete"] = [0xE2, 0x82]
		};

		foreach (var (caseId, payload) in invalidPayloads)
		{
			var path = temp.CreateBinaryFile($"invalid-{caseId}.txt", payload);
			var streaming = await _analyzer.GetClassifiedMetricsAsync(
				path,
				TestContext.Current.CancellationToken);
			var materialized = await _analyzer.ReadFactAsync(
				path,
				long.MaxValue,
				TestContext.Current.CancellationToken);

			Assert.True(
				streaming.Classification == FileContentClassification.UnsupportedEncoding,
				$"Streaming classification for {caseId} was {streaming.Classification}.");
			Assert.True(
				materialized.Classification == FileContentClassification.UnsupportedEncoding,
				$"Materialized classification for {caseId} was {materialized.Classification}.");
			Assert.Null(streaming.Metrics);
			Assert.Null(materialized.Content);
			Assert.Null(materialized.RawMetrics);
		}
	}

	[Fact]
	public async Task MalformedBomPayloadsAreUnsupportedAcrossReadSurfaces()
	{
		using var temp = new TemporaryDirectory();
		var malformedPayloads = new Dictionary<string, byte[]>(StringComparer.Ordinal)
		{
			["utf8-invalid"] = [0xEF, 0xBB, 0xBF, 0xC3, 0x28],
			["utf8-incomplete"] = [0xEF, 0xBB, 0xBF, 0xE2, 0x82],
			["utf16-le-low-surrogate"] = [0xFF, 0xFE, 0x00, 0xDC],
			["utf16-be-low-surrogate"] = [0xFE, 0xFF, 0xDC, 0x00],
			["utf32-le-out-of-range"] = [0xFF, 0xFE, 0x00, 0x00, 0x00, 0x00, 0x11, 0x00],
			["utf32-be-out-of-range"] = [0x00, 0x00, 0xFE, 0xFF, 0x00, 0x11, 0x00, 0x00]
		};

		foreach (var (caseId, payload) in malformedPayloads)
		{
			var path = temp.CreateBinaryFile($"malformed-{caseId}.txt", payload);
			var streaming = await _analyzer.GetClassifiedMetricsAsync(
				path,
				TestContext.Current.CancellationToken);
			var materialized = await _analyzer.ReadFactAsync(
				path,
				long.MaxValue,
				TestContext.Current.CancellationToken);
			await using var snapshot = await _analyzer.OpenCompleteSnapshotAsync(
				path,
				TestContext.Current.CancellationToken);
			await using var buffer = await _analyzer.OpenCompleteTextBufferAsync(
				path,
				long.MaxValue,
				TestContext.Current.CancellationToken);

			Assert.Equal(FileContentClassification.UnsupportedEncoding, streaming.Classification);
			Assert.Equal(FileContentClassification.UnsupportedEncoding, materialized.Classification);
			Assert.Equal(FileContentClassification.UnsupportedEncoding, snapshot.Result.Classification);
			Assert.Equal(FileContentClassification.UnsupportedEncoding, buffer.Classification);
			Assert.Null(streaming.Metrics);
			Assert.Null(materialized.Content);
			Assert.Null(materialized.RawMetrics);
			Assert.Null(snapshot.Result.Metrics);
			Assert.True(buffer.Content.IsEmpty, caseId);
		}
	}

	[Fact]
	public async Task StreamingMetrics_NullAfterInitialProbeMatchesMaterializedBinaryClassification()
	{
		using var temp = new TemporaryDirectory();
		var payload = Enumerable.Repeat((byte)'a', 8194).ToArray();
		payload[8192] = 0;
		var path = temp.CreateBinaryFile("late-null.txt", payload);

		var streaming = await _analyzer.GetClassifiedMetricsAsync(
			path,
			TestContext.Current.CancellationToken);
		var materialized = await _analyzer.ReadFactAsync(
			path,
			long.MaxValue,
			TestContext.Current.CancellationToken);

		Assert.Equal(FileContentClassification.Binary, streaming.Classification);
		Assert.Equal(FileContentClassification.Binary, materialized.Classification);
		Assert.Null(streaming.Metrics);
		Assert.Null(materialized.Content);
	}

	[Theory]
	[InlineData("single line")]
	[InlineData("line 1\nline 2\n")]
	[InlineData("line 1\rline 2\r")]
	[InlineData("line 1\r\nline 2\nline 3\r")]
	[InlineData(" \t\r\n\u2003")]
	[InlineData("Привет\n世界\r\n")]
	public async Task GetTextFileMetricsAsync_TextMatrix_MatchesFullContentMetrics(string content)
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("matrix.txt", content);

		var metrics = await _analyzer.GetTextFileMetricsAsync(
			file,
			TestContext.Current.CancellationToken);
		var fullContent = await _analyzer.TryReadAsTextAsync(
			file,
			TestContext.Current.CancellationToken);

		Assert.NotNull(metrics);
		Assert.NotNull(fullContent);
		Assert.Equal(fullContent.SizeBytes, metrics.SizeBytes);
		Assert.Equal(fullContent.LineCount, metrics.LineCount);
		Assert.Equal(fullContent.CharCount, metrics.CharCount);
		Assert.Equal(fullContent.IsEmpty, metrics.IsEmpty);
		Assert.Equal(fullContent.IsWhitespaceOnly, metrics.IsWhitespaceOnly);
		Assert.Equal(fullContent.IsEstimated, metrics.IsEstimated);
		Assert.Equal(fullContent.TrailingNewlineChars, metrics.TrailingNewlineChars);
		Assert.Equal(fullContent.TrailingNewlineLineBreaks, metrics.TrailingNewlineLineBreaks);
	}

	[Fact]
	public async Task GetTextFileMetricsAsync_FileOpenForConcurrentReads_RemainsReadable()
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile("shared.txt", "line 1\nline 2");
		await using var otherReader = new FileStream(
			file,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read);

		var metrics = await _analyzer.GetTextFileMetricsAsync(
			file,
			TestContext.Current.CancellationToken);

		Assert.NotNull(metrics);
		Assert.Equal(2, metrics.LineCount);
		Assert.Equal(13, metrics.CharCount);
	}

	[Fact]
	public async Task GetTextFileMetricsAsync_ConcurrentFiles_PreservesEveryResult()
	{
		using var temp = new TemporaryDirectory();
		var files = Enumerable.Range(0, 32)
			.Select(index => temp.CreateFile($"file-{index:D2}.txt", $"value-{index}\nnext"))
			.ToArray();

		var tasks = files.Select(path => Task.Run(
			async () => await _analyzer
				.GetTextFileMetricsAsync(path, TestContext.Current.CancellationToken),
			TestContext.Current.CancellationToken));
		var results = await Task.WhenAll(tasks);

		Assert.All(results, result =>
		{
			Assert.NotNull(result);
			Assert.Equal(2, result.LineCount);
			Assert.False(result.IsEstimated);
		});
	}

	[Fact]
	public async Task OpenCompleteTextBufferAsync_SupportedEncodings_ReturnExactOperationOwnedText()
	{
		using var temp = new TemporaryDirectory();
		const string content = "secret=Привет-世界\r\nnext=value";
		var encodings = new Encoding[]
		{
			new UTF8Encoding(false, true),
			new UTF8Encoding(true, true),
			new UnicodeEncoding(false, true, true),
			new UnicodeEncoding(true, true, true),
			new UTF32Encoding(false, true, true),
			new UTF32Encoding(true, true, true)
		};

		for (var index = 0; index < encodings.Length; index++)
		{
			var path = Path.Combine(temp.Path, $"encoded-{index}.txt");
			await File.WriteAllTextAsync(path, content, encodings[index], TestContext.Current.CancellationToken);
			await using var buffer = await _analyzer.OpenCompleteTextBufferAsync(
				path,
				1024 * 1024,
				TestContext.Current.CancellationToken);

			Assert.Equal(FileContentClassification.Text, buffer.Classification);
			Assert.Equal(new FileInfo(path).Length, buffer.SizeBytes);
			Assert.Equal(content, buffer.Content.ToString());
		}
	}

	[Fact]
	public async Task OpenCompleteTextBufferAsync_BinaryAndLimitRemainExplicit()
	{
		using var temp = new TemporaryDirectory();
		var binary = temp.CreateBinaryFile("opaque.unknown", [0x41, 0x42, 0x00, 0x43]);
		var oversized = temp.CreateFile("oversized.txt", new string('x', 1024));

		await using var binaryBuffer = await _analyzer.OpenCompleteTextBufferAsync(
			binary,
			2,
			TestContext.Current.CancellationToken);
		await using var oversizedBuffer = await _analyzer.OpenCompleteTextBufferAsync(
			oversized,
			128,
			TestContext.Current.CancellationToken);

		Assert.Equal(FileContentClassification.Binary, binaryBuffer.Classification);
		Assert.Equal(4, binaryBuffer.SizeBytes);
		Assert.True(binaryBuffer.Content.IsEmpty);
		Assert.Equal(FileContentClassification.TooLarge, oversizedBuffer.Classification);
		Assert.Equal(1024, oversizedBuffer.SizeBytes);
		Assert.True(oversizedBuffer.Content.IsEmpty);
	}

	[Fact]
	public async Task OpenCompleteTextBufferAsync_DisposeInvalidatesPooledContent()
	{
		using var temp = new TemporaryDirectory();
		var path = temp.CreateFile("content.txt", "sensitive operation text");
		var buffer = await _analyzer.OpenCompleteTextBufferAsync(
			path,
			1024,
			TestContext.Current.CancellationToken);
		Assert.Equal("sensitive operation text", buffer.Content.ToString());

		await buffer.DisposeAsync();

		Assert.Throws<ObjectDisposedException>(() => _ = buffer.Content);
	}

	private static Encoding CreateStrictEncoding(string encodingId, bool emitBom) =>
		encodingId switch
		{
			"utf8" => new UTF8Encoding(emitBom, throwOnInvalidBytes: true),
			"utf16-le" => new UnicodeEncoding(
				bigEndian: false,
				byteOrderMark: emitBom,
				throwOnInvalidBytes: true),
			"utf16-be" => new UnicodeEncoding(
				bigEndian: true,
				byteOrderMark: emitBom,
				throwOnInvalidBytes: true),
			"utf32-le" => new UTF32Encoding(
				bigEndian: false,
				byteOrderMark: emitBom,
				throwOnInvalidCharacters: true),
			"utf32-be" => new UTF32Encoding(
				bigEndian: true,
				byteOrderMark: emitBom,
				throwOnInvalidCharacters: true),
			_ => throw new ArgumentOutOfRangeException(nameof(encodingId))
		};

	public enum ProbeOperation
	{
		CompleteTextBuffer,
		StreamingMetrics,
		CompleteSnapshot,
		ReadFact
	}

	private sealed class ProbeFailureFileStream(string path) : FileStream(
		path,
		FileMode.Open,
		FileAccess.Read,
		FileShare.ReadWrite | FileShare.Delete,
		bufferSize: 1,
		FileOptions.SequentialScan)
	{
		private int _spanReads;

		public override int Read(Span<byte> buffer)
		{
			if (Interlocked.Increment(ref _spanReads) == 2)
				throw new IOException("Injected null-byte probe failure.");
			return base.Read(buffer);
		}
	}

	#endregion
}
