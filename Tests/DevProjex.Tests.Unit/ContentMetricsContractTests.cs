namespace DevProjex.Tests.Unit;

public sealed class ContentMetricsContractTests
{
	public static IEnumerable<object[]> PortableContentCases()
	{
		yield return ["plain", "alpha"];
		yield return ["lf", "alpha\nbeta\n"];
		yield return ["crlf", "alpha\r\nbeta\r\n"];
		yield return ["cr", "alpha\rbeta\r"];
		yield return ["mixed", "alpha\r\nbeta\rgamma\ndelta\r\n"];
		yield return ["empty", string.Empty];
		yield return ["whitespace", " \r\n\t\r "];
		yield return ["unicode", "Привет\rмир\n你好"];
	}

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
	public async Task ContentMetricsPipeline_RootHeaderMatchesRenderedContentWithoutMaterializingIt()
	{
		using var project = new TemporaryDirectory();
		var file = project.CreateFile(Path.Combine("src", "Program.cs"), "line1\r\nline2\r\n");
		var analyzer = new FileContentAnalyzer();
		var mapper = TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(project.Path);
		var inputs = await BuildMetricsInputsAsync([file], analyzer, mapper);
		var rendered = await new SelectedContentExportService(analyzer).BuildAsync(
			[file],
			TestContext.Current.CancellationToken,
			mapper,
			transformationContext: null,
			displayRootPath: project.Path);
		var accumulator = new ExportOutputMetricsCalculator.OrderedContentMetricsAccumulator();
		accumulator.AppendRootHeader(project.Path);
		foreach (var input in inputs)
			accumulator.AppendFile(input);

		Assert.Equal(ExportOutputMetricsCalculator.FromText(rendered), accumulator.ToMetrics());
	}

	[Fact]
	public async Task ContentMetricsPipeline_RootHeaderIsTheCompleteOutputWithoutTextFiles()
	{
		using var project = new TemporaryDirectory();
		var binary = project.CreateBinaryFile("image.bin", [0x00, 0x01, 0x02, 0x03, 0xFF]);
		var analyzer = new FileContentAnalyzer();
		var rendered = await new SelectedContentExportService(analyzer).BuildAsync(
			[binary],
			TestContext.Current.CancellationToken,
			TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(project.Path),
			transformationContext: null,
			displayRootPath: project.Path);
		var accumulator = new ExportOutputMetricsCalculator.OrderedContentMetricsAccumulator();
		accumulator.AppendRootHeader(project.Path);

		Assert.Equal(ContextRootPresentation.FormatLine(project.Path), rendered);
		Assert.Equal(ExportOutputMetricsCalculator.FromText(rendered), accumulator.ToMetrics());
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

	[Theory]
	[MemberData(nameof(PortableContentCases))]
	public async Task RelativeContentMetrics_MatchRenderedExport_AcrossLineEndingsAndUnicode(
		string caseId,
		string content)
	{
		using var temp = new TemporaryDirectory();
		var file = temp.CreateFile(Path.Combine("..cache", "Проект с пробелом", "notes.txt"), content);
		var analyzer = new FileContentAnalyzer();
		var exportService = new SelectedContentExportService(analyzer);
		var mapper = TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(temp.Path);

		var inputs = await BuildMetricsInputsAsync([file], analyzer, mapper);
		var exportText = await exportService.BuildAsync([file], CancellationToken.None, mapper);

		var expected = ExportOutputMetricsCalculator.FromText(exportText);
		var actual = ExportOutputMetricsCalculator.FromContentFiles(inputs);
		Assert.Equal(expected, actual);
		Assert.Contains("..cache/Проект с пробелом/notes.txt:", exportText, StringComparison.Ordinal);
		Assert.DoesNotContain($"{file}:", exportText, StringComparison.Ordinal);
		Assert.False(string.IsNullOrWhiteSpace(caseId));
	}

	[Fact]
	public async Task RelativeHeaders_ReduceOnlyPathCharacters_AndPreserveLineCount()
	{
		using var temp = new TemporaryDirectory();
		var alpha = temp.CreateFile(Path.Combine("src", "alpha.txt"), "alpha\nbeta");
		var beta = temp.CreateFile(Path.Combine("docs", "beta.txt"), "gamma");
		var analyzer = new FileContentAnalyzer();
		var relativeMapper = TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(temp.Path);

		var absoluteInputs = await BuildMetricsInputsAsync([alpha, beta], analyzer, mapFilePath: null);
		var relativeInputs = await BuildMetricsInputsAsync([alpha, beta], analyzer, relativeMapper);
		var absolute = ExportOutputMetricsCalculator.FromContentFiles(absoluteInputs);
		var relative = ExportOutputMetricsCalculator.FromContentFiles(relativeInputs);
		var expectedSavedChars =
			alpha.Length - "src/alpha.txt".Length +
			beta.Length - "docs/beta.txt".Length;

		Assert.Equal(absolute.Lines, relative.Lines);
		Assert.Equal(expectedSavedChars, absolute.Chars - relative.Chars);
		Assert.Equal((relative.Chars + 3) / 4, relative.Tokens);
		Assert.True(relative.Tokens <= absolute.Tokens);
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
