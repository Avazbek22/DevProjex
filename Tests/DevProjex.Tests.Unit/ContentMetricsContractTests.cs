namespace DevProjex.Tests.Unit;

public sealed class ContentMetricsContractTests
{
	[Fact]
	public async Task ContentMetricsPipeline_EqualsRenderedExportMetrics_WithCrLfAndMappedPaths()
	{
		using var temp = new TemporaryDirectory();
		var alpha = temp.CreateFile("alpha.txt", "line1\r\nline2\r\nline3\r\n");
		var beta = temp.CreateFile("beta.txt", "a\nb\nc");
		var gamma = temp.CreateFile("gamma.txt", "   ");

		var analyzer = new FileContentAnalyzer();
		var exportService = new SelectedContentExportService(analyzer);
		Func<string, string> mapper = path =>
		{
			var name = Path.GetFileName(path);
			return $"https://github.com/org/repo/{name}";
		};

		var inputs = await BuildMetricsInputsAsync([beta, alpha, gamma, alpha], analyzer, mapper);
		var exportText = await exportService.BuildAsync([beta, alpha, gamma, alpha], CancellationToken.None, mapper);

		var expected = ExportOutputMetricsCalculator.FromText(exportText);
		var actual = ExportOutputMetricsCalculator.FromContentFiles(inputs);

		Assert.Equal(expected.Lines, actual.Lines);
		Assert.Equal(expected.Chars, actual.Chars);
		Assert.Equal(expected.Tokens, actual.Tokens);
	}

	[Fact]
	public async Task ContentMetricsPipeline_EqualsRenderedExportMetrics_ForEstimatedLargeFile()
	{
		using var temp = new TemporaryDirectory();
		var largeFile = Path.Combine(temp.Path, "large.txt");
		await WriteLargeTextFileAsync(largeFile, 11 * 1024 * 1024);

		var analyzer = new FileContentAnalyzer();
		var exportService = new SelectedContentExportService(analyzer);

		var inputs = await BuildMetricsInputsAsync([largeFile], analyzer, mapFilePath: null);
		var exportText = await exportService.BuildAsync([largeFile], CancellationToken.None, displayPathMapper: null);

		var expected = ExportOutputMetricsCalculator.FromText(exportText);
		var actual = ExportOutputMetricsCalculator.FromContentFiles(inputs);

		Assert.Equal(expected.Lines, actual.Lines);
		Assert.Equal(expected.Chars, actual.Chars);
		Assert.Equal(expected.Tokens, actual.Tokens);
	}

	[Fact]
	public async Task ContentMetricsPipeline_MixedTextAndBinaryFiles_SkipsBinaryWithoutZeroingContentMetrics()
	{
		using var temp = new TemporaryDirectory();
		var alpha = temp.CreateFile("alpha.txt", "line1\nline2\nline3\n");
		var beta = temp.CreateFile("beta.md", "# Title\n\nbody\n");
		var binary = temp.CreateBinaryFile("image.bin", [0x00, 0x01, 0x02, 0x03, 0xFF]);

		var analyzer = new FileContentAnalyzer();
		var exportService = new SelectedContentExportService(analyzer);
		var inputs = await BuildMetricsInputsAsync(
			[binary, beta, alpha],
			analyzer,
			mapFilePath: null);
		var exportText = await exportService.BuildAsync(
			[binary, beta, alpha],
			CancellationToken.None,
			displayPathMapper: null);

		var expected = ExportOutputMetricsCalculator.FromText(exportText);
		var actual = ExportOutputMetricsCalculator.FromContentFiles(inputs);

		Assert.NotEqual(ExportOutputMetrics.Empty, actual);
		Assert.Equal(expected.Lines, actual.Lines);
		Assert.Equal(expected.Chars, actual.Chars);
		Assert.Equal(expected.Tokens, actual.Tokens);
	}

	private static async Task<IReadOnlyList<ContentFileMetrics>> BuildMetricsInputsAsync(
		IEnumerable<string> filePaths,
		IFileContentAnalyzer analyzer,
		Func<string, string>? mapFilePath)
	{
		var unique = new HashSet<string>(PathComparer.Default);
		foreach (var path in filePaths)
		{
			if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
				unique.Add(path);
		}

		if (unique.Count == 0)
			return [];

		var ordered = new List<string>(unique);
		ordered.Sort(PathComparer.Default);

		var results = new List<ContentFileMetrics>(ordered.Count);
		foreach (var path in ordered)
		{
			var metrics = await analyzer.GetTextFileMetricsAsync(path);
			if (metrics is null)
				continue;

			var displayPath = MapDisplayPath(path, mapFilePath);
			results.Add(new ContentFileMetrics(
				Path: displayPath,
				SizeBytes: metrics.SizeBytes,
				LineCount: metrics.LineCount,
				CharCount: metrics.CharCount,
				IsEmpty: metrics.IsEmpty,
				IsWhitespaceOnly: metrics.IsWhitespaceOnly,
				IsEstimated: metrics.IsEstimated,
				CrLfPairCount: metrics.CrLfPairCount,
				TrailingNewlineChars: metrics.TrailingNewlineChars,
				TrailingNewlineLineBreaks: metrics.TrailingNewlineLineBreaks));
		}

		return results;
	}

	private static string MapDisplayPath(string path, Func<string, string>? mapFilePath)
	{
		if (mapFilePath is null)
			return path;

		try
		{
			var mapped = mapFilePath(path);
			return string.IsNullOrWhiteSpace(mapped) ? path : mapped;
		}
		catch
		{
			return path;
		}
	}

	private static async Task WriteLargeTextFileAsync(string path, int targetBytes)
	{
		const int chunkSize = 8192;
		var chunk = new string('A', chunkSize);
		var written = 0;

		await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
		await using var writer = new StreamWriter(stream, Encoding.UTF8);
		while (written < targetBytes)
		{
			var toWrite = Math.Min(chunkSize, targetBytes - written);
			await writer.WriteAsync(chunk.AsMemory(0, toWrite));
			written += toWrite;
		}
	}
}
