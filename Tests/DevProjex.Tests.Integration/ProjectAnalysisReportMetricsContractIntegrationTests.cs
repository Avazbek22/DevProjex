using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Integration;

public sealed class ProjectAnalysisReportMetricsContractIntegrationTests
{
	[Fact]
	public async Task NoUiReportMetrics_MatchRenderedExports_ForMixedTextBinaryWhitespaceAndCrLfFiles()
	{
		using var temp = new TemporaryDirectory();
		var projectPath = SeedMetricsWorkspace(temp);

		using var report = await RunReportToStdoutAsync(
			CommandLineOptionTokens.Silent,
			CommandLineOptionTokens.Path, projectPath,
			CommandLineOptionTokens.Roots, "src",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);
		var expected = await CalculateRenderedMetricsAsync(
			projectPath,
			selectedRootFolders: ["src"],
			selectedExtensions: null,
			selectedIgnoreOptions: []);

		AssertReportMetrics(report.RootElement, expected);
		Assert.Equal(4, report.RootElement.GetProperty("inventory").GetProperty("tree").GetProperty("fileCount").GetInt32());
	}

	[Fact]
	public async Task NoUiReportMetrics_ChangeWhenSelectionNarrowsToMarkdownDocs()
	{
		using var temp = new TemporaryDirectory();
		var projectPath = SeedMetricsWorkspace(temp);

		using var fullReport = await RunReportToStdoutAsync(
			CommandLineOptionTokens.Silent,
			CommandLineOptionTokens.Path, projectPath,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);
		using var docsReport = await RunReportToStdoutAsync(
			CommandLineOptionTokens.Silent,
			CommandLineOptionTokens.Path, projectPath,
			CommandLineOptionTokens.Roots, "docs",
			CommandLineOptionTokens.Extensions, "md",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);
		var expectedDocs = await CalculateRenderedMetricsAsync(
			projectPath,
			selectedRootFolders: ["docs"],
			selectedExtensions: [".md"],
			selectedIgnoreOptions: []);

		AssertReportMetrics(docsReport.RootElement, expectedDocs);

		var fullTreeChars = fullReport.RootElement.GetProperty("metrics").GetProperty("tree").GetProperty("chars").GetInt32();
		var docsTreeChars = docsReport.RootElement.GetProperty("metrics").GetProperty("tree").GetProperty("chars").GetInt32();
		var fullContentChars = fullReport.RootElement.GetProperty("metrics").GetProperty("content").GetProperty("chars").GetInt32();
		var docsContentChars = docsReport.RootElement.GetProperty("metrics").GetProperty("content").GetProperty("chars").GetInt32();

		Assert.True(docsTreeChars < fullTreeChars);
		Assert.True(docsContentChars < fullContentChars);
		Assert.Equal(["docs"], ReadStringArray(docsReport.RootElement.GetProperty("selection").GetProperty("selectedRootFolders")));
		Assert.Equal([".md"], ReadStringArray(docsReport.RootElement.GetProperty("selection").GetProperty("selectedExtensions")));
	}

	[Fact]
	public async Task NoUiReportMetrics_AreStableBetweenStdoutReportAndFileReport()
	{
		using var temp = new TemporaryDirectory();
		var projectPath = SeedMetricsWorkspace(temp);
		var reportPath = Path.Combine(temp.Path, "reports", "metrics.json");

		using var stdoutReport = await RunReportToStdoutAsync(
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Path, projectPath,
			CommandLineOptionTokens.Report, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.Roots, "src",
			CommandLineOptionTokens.Extensions, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);
		await RunReportToFileAsync(
			reportPath,
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Path, projectPath,
			CommandLineOptionTokens.ReportPath, reportPath,
			CommandLineOptionTokens.Roots, "src",
			CommandLineOptionTokens.Extensions, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		using var fileReport = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken));

		Assert.Equal(
			stdoutReport.RootElement.GetProperty("metrics").GetRawText(),
			fileReport.RootElement.GetProperty("metrics").GetRawText());
		Assert.Equal(
			stdoutReport.RootElement.GetProperty("inventory").GetProperty("tree").GetRawText(),
			fileReport.RootElement.GetProperty("inventory").GetProperty("tree").GetRawText());
	}

	private static string SeedMetricsWorkspace(TemporaryDirectory temp)
	{
		var projectPath = temp.CreateDirectory("metrics project with spaces");
		WriteFile(projectPath, Path.Combine("src", "App.cs"), "namespace Metrics;\r\npublic sealed class App {}\r\n");
		WriteFile(projectPath, Path.Combine("src", "empty.txt"), string.Empty);
		WriteFile(projectPath, Path.Combine("src", "spaces.txt"), "  \r\n\t  ");
		WriteBytes(projectPath, Path.Combine("src", "image.bin"), [0x00, 0x01, 0x02, 0x03]);
		WriteFile(projectPath, Path.Combine("docs", "Guide.md"), "# Guide\n\nПривет\n");
		WriteFile(projectPath, Path.Combine("docs", "notes.txt"), "note one\nnote two\n");
		return projectPath;
	}

	private static async Task<JsonDocument> RunReportToStdoutAsync(params string[] args)
	{
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(args);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(string.Empty, error.ToString());
		Assert.StartsWith("{", output.ToString().TrimStart(), StringComparison.Ordinal);
		return JsonDocument.Parse(output.ToString());
	}

	private static async Task RunReportToFileAsync(string reportPath, params string[] args)
	{
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(args);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal($"{Path.GetFullPath(reportPath)}{Environment.NewLine}", output.ToString());
		Assert.Equal(string.Empty, error.ToString());
		Assert.True(File.Exists(reportPath));
	}

	private static async Task<ExpectedReportMetrics> CalculateRenderedMetricsAsync(
		string projectPath,
		IReadOnlyCollection<string>? selectedRootFolders,
		IReadOnlyCollection<string>? selectedExtensions,
		IReadOnlyCollection<IgnoreOptionId>? selectedIgnoreOptions)
	{
		var services = AvaloniaCompositionRoot.CreateDefault(CommandLineOptions.Empty);
		var loaded = services.ProjectAnalysisService.Load(new ProjectAnalysisRequest(
			RootPath: projectPath,
			SelectedRootFolders: selectedRootFolders,
			SelectedExtensions: selectedExtensions,
			SelectedIgnoreOptions: selectedIgnoreOptions));
		var treeText = new TreeExportService().BuildFullTree(projectPath, loaded.Tree.Root, TreeTextFormat.Ascii);
		var contentText = await new SelectedContentExportService(new FileContentAnalyzer())
			.BuildAsync(loaded.Tree.OrderedFilePaths ?? [], TestContext.Current.CancellationToken);

		return new ExpectedReportMetrics(
			Tree: ExportOutputMetricsCalculator.FromText(treeText),
			Content: ExportOutputMetricsCalculator.FromText(contentText));
	}

	private static void AssertReportMetrics(JsonElement report, ExpectedReportMetrics expected)
	{
		var metrics = report.GetProperty("metrics");
		AssertMetric(metrics.GetProperty("tree"), expected.Tree);
		AssertMetric(metrics.GetProperty("content"), expected.Content);
	}

	private static void AssertMetric(JsonElement actual, ExportOutputMetrics expected)
	{
		Assert.Equal(expected.Lines, actual.GetProperty("lines").GetInt32());
		Assert.Equal(expected.Chars, actual.GetProperty("chars").GetInt32());
		Assert.Equal(expected.Tokens, actual.GetProperty("tokens").GetInt32());
	}

	private static CommandLineAutomationContext CreateContext(TextWriter output, TextWriter error) =>
		new(
			Output: output,
			Error: error,
			ServicesFactory: AvaloniaCompositionRoot.CreateDefault,
			HelpContentProvider: new CommandLineHelpContentProvider(),
			VersionProvider: () => "test-version");

	private static string[] ReadStringArray(JsonElement element) =>
		element.EnumerateArray()
			.Select(static item => item.GetString() ?? string.Empty)
			.ToArray();

	private static void WriteFile(string rootPath, string relativePath, string content)
	{
		var path = Path.Combine(rootPath, relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
	}

	private static void WriteBytes(string rootPath, string relativePath, byte[] content)
	{
		var path = Path.Combine(rootPath, relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllBytes(path, content);
	}

	private sealed record ExpectedReportMetrics(ExportOutputMetrics Tree, ExportOutputMetrics Content);
}
