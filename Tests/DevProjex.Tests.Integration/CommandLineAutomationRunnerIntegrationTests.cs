using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Integration;

public sealed class CommandLineAutomationRunnerIntegrationTests
{
	[Fact]
	public async Task RunUtilityOrHeadlessAsync_NoUiWritesReportAndPrintsReportPathToStdout()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		temp.CreateFile(Path.Combine("src", "appsettings.json"), "{}\n");
		temp.CreateFile(Path.Combine("tests", "AppTests.cs"), "class AppTests {}\n");
		var reportPath = Path.Combine(temp.Path, "reports", "cli-report.json");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.ReportPath, reportPath,
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);
		var context = CreateContext(output, error);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			context,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal($"{Path.GetFullPath(reportPath)}{Environment.NewLine}", output.ToString());
		Assert.Equal(string.Empty, error.ToString());
		Assert.True(File.Exists(reportPath));

		var json = await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken);
		using var document = JsonDocument.Parse(json);
		var root = document.RootElement;
		Assert.Equal(ProjectAnalysisReport.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
		Assert.Equal(temp.Path, root.GetProperty("rootPath").GetString());
		Assert.Equal("src", root.GetProperty("selection").GetProperty("selectedRootFolders")[0].GetString());
		Assert.Equal(".cs", root.GetProperty("selection").GetProperty("selectedExtensions")[0].GetString());
		Assert.Empty(root.GetProperty("selection").GetProperty("selectedIgnoreOptions").EnumerateArray());
		Assert.True(root.GetProperty("timing").GetProperty("totalMilliseconds").GetDouble() >= 0);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_NoUiMissingRootReturnsRuntimeErrorAndDoesNotCreateReport()
	{
		using var temp = new TemporaryDirectory();
		var missingRoot = Path.Combine(temp.Path, "missing");
		var reportPath = Path.Combine(temp.Path, "reports", "missing.json");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Path, missingRoot,
			CommandLineOptionTokens.ReportPath, reportPath
		]);
		var context = CreateContext(output, error);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			context,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.RuntimeError, exitCode);
		Assert.Equal(string.Empty, output.ToString());
		Assert.Contains("Project path was not found", error.ToString(), StringComparison.Ordinal);
		Assert.False(File.Exists(reportPath));
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ExportTreeToStdoutWithoutNoUiUsesSelectionFilters()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		temp.CreateFile(Path.Combine("docs", "Guide.md"), "# Guide\n");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(string.Empty, error.ToString());
		Assert.Contains("src", output.ToString(), StringComparison.Ordinal);
		Assert.Contains("App.cs", output.ToString(), StringComparison.Ordinal);
		Assert.DoesNotContain("Guide.md", output.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ExportTreeContentToFileWritesUtf8PayloadAndPrintsPath()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		temp.CreateFile(Path.Combine("src", "appsettings.json"), "{}\n");
		var exportPath = Path.Combine(temp.Path, "exports", "context.txt");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "tree-content",
			CommandLineOptionTokens.Output, exportPath,
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal($"{Path.GetFullPath(exportPath)}{Environment.NewLine}", output.ToString());
		Assert.Equal(string.Empty, error.ToString());
		Assert.True(File.Exists(exportPath));

		var bytes = await File.ReadAllBytesAsync(exportPath, TestContext.Current.CancellationToken);
		Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
		var payload = Encoding.UTF8.GetString(bytes);
		Assert.Contains("App.cs", payload, StringComparison.Ordinal);
		Assert.Contains("class App", payload, StringComparison.Ordinal);
		Assert.DoesNotContain("appsettings.json", payload, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ConvenienceAliasesExportTreeContentToFile()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		temp.CreateFile(Path.Combine("src", "appsettings.json"), "{}\n");
		temp.CreateFile(Path.Combine("docs", "Guide.md"), "# Guide\n");
		var exportPath = Path.Combine(temp.Path, "exports", "alias-context.txt");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			temp.Path,
			CommandLineOptionTokens.Export, "tree-content",
			CommandLineOptionTokens.ShortOutput, exportPath,
			CommandLineOptionTokens.Roots, "src",
			CommandLineOptionTokens.Extensions, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal($"{Path.GetFullPath(exportPath)}{Environment.NewLine}", output.ToString());
		Assert.Equal(string.Empty, error.ToString());
		var payload = await File.ReadAllTextAsync(exportPath, TestContext.Current.CancellationToken);
		Assert.Contains("App.cs", payload, StringComparison.Ordinal);
		Assert.Contains("class App", payload, StringComparison.Ordinal);
		Assert.DoesNotContain("appsettings.json", payload, StringComparison.Ordinal);
		Assert.DoesNotContain("Guide.md", payload, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ExportContentOnlyWritesNoTreeBranches()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		temp.CreateFile(Path.Combine("docs", "Guide.md"), "# Guide\n");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "content",
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(string.Empty, error.ToString());
		Assert.Contains("App.cs:", output.ToString(), StringComparison.Ordinal);
		Assert.Contains("class App", output.ToString(), StringComparison.Ordinal);
		Assert.DoesNotContain("├──", output.ToString(), StringComparison.Ordinal);
		Assert.DoesNotContain("Guide.md", output.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ExportJsonTreeToStdoutWritesParseableTreeOnly()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.ExportFormat, "json",
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(string.Empty, error.ToString());
		using var document = JsonDocument.Parse(output.ToString());
		var root = document.RootElement;
		Assert.Equal(Path.GetFullPath(temp.Path), root.GetProperty("rootPath").GetString());
		Assert.Equal("App.cs", root.GetProperty("root").GetProperty("dirs")[0].GetProperty("files")[0].GetString());
		Assert.DoesNotContain("class App", output.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_FormatAliasWritesJsonTreeToStdout()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			temp.Path,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Format, "json",
			CommandLineOptionTokens.Roots, "src",
			CommandLineOptionTokens.Extensions, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(string.Empty, error.ToString());
		using var document = JsonDocument.Parse(output.ToString());
		Assert.Equal("App.cs", document.RootElement.GetProperty("root").GetProperty("dirs")[0].GetProperty("files")[0].GetString());
		Assert.DoesNotContain("class App", output.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_StrictExportWritesPayloadThenReturnsRuntimeErrorForWarnings()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var exportPath = Path.Combine(temp.Path, "exports", "strict-context.txt");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Strict,
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Output, exportPath,
			CommandLineOptionTokens.IncludeRoot, "missing-root",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.RuntimeError, exitCode);
		Assert.Equal($"{Path.GetFullPath(exportPath)}{Environment.NewLine}", output.ToString());
		Assert.True(File.Exists(exportPath));
		Assert.Contains("Strict mode failed", error.ToString(), StringComparison.Ordinal);
		Assert.Contains("Selected root folder was not found", error.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ReportAndExportToDifferentFilesWritesBothAndPrintsBothPaths()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var reportPath = Path.Combine(temp.Path, "out", "report.json");
		var exportPath = Path.Combine(temp.Path, "out", "context.txt");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.ReportPath, reportPath,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Output, exportPath,
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(string.Empty, error.ToString());
		Assert.Equal(
			$"{Path.GetFullPath(reportPath)}{Environment.NewLine}{Path.GetFullPath(exportPath)}{Environment.NewLine}",
			output.ToString());
		Assert.True(File.Exists(reportPath));
		Assert.True(File.Exists(exportPath));
		using var reportDocument = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken));
		Assert.Equal(ProjectAnalysisReport.CurrentSchemaVersion, reportDocument.RootElement.GetProperty("schemaVersion").GetInt32());
		Assert.Contains("App.cs", await File.ReadAllTextAsync(exportPath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ReportAndExportSamePathReturnsUsageErrorWithoutWritingFile()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var sharedPath = Path.Combine(temp.Path, "out", "same.txt");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.ReportPath, sharedPath,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Output, sharedPath
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Equal(string.Empty, output.ToString());
		Assert.Contains("--report-path and --output must point to different files", error.ToString(), StringComparison.Ordinal);
		Assert.False(File.Exists(sharedPath));
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ContentExportWithNoTextFilesWritesEmptyStdout()
	{
		using var temp = new TemporaryDirectory();
		var binaryPath = Path.Combine(temp.Path, "src", "image.bin");
		Directory.CreateDirectory(Path.GetDirectoryName(binaryPath)!);
		await File.WriteAllBytesAsync(binaryPath, [0, 1, 2, 3, 0], TestContext.Current.CancellationToken);
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "content",
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "bin",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(string.Empty, output.ToString());
		Assert.Equal(string.Empty, error.ToString());
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ContentExportWithJsonFormatReturnsUsageError()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "content",
			CommandLineOptionTokens.ExportFormat, "json"
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Equal(string.Empty, output.ToString());
		Assert.Contains("--export-format applies only to tree", error.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ExportOptionsBeforeExportParseAndRunCorrectly()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var exportPath = Path.Combine(temp.Path, "context.txt");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Output, exportPath,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal($"{Path.GetFullPath(exportPath)}{Environment.NewLine}", output.ToString());
		Assert.Equal(string.Empty, error.ToString());
		Assert.Contains("App.cs", await File.ReadAllTextAsync(exportPath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ExportOutputExistingDirectoryReturnsRuntimeError()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var outputDirectory = temp.CreateDirectory("existing-output-directory");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Output, outputDirectory,
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.RuntimeError, exitCode);
		Assert.Equal(string.Empty, output.ToString());
		Assert.StartsWith("DevProjex: ", error.ToString(), StringComparison.Ordinal);
		Assert.True(Directory.Exists(outputDirectory));
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ExportTreeContentJsonWritesJsonTreeAndPlainTextContent()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "tree-content",
			CommandLineOptionTokens.ExportFormat, "json",
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(string.Empty, error.ToString());
		var payload = output.ToString();
		var separatorIndex = payload.IndexOf("\u00A0", StringComparison.Ordinal);
		Assert.True(separatorIndex > 0, "Expected tree-content JSON export to separate JSON tree from plain text content.");
		using var document = JsonDocument.Parse(payload[..separatorIndex].TrimEnd('\r', '\n'));
		Assert.Equal("App.cs", document.RootElement.GetProperty("root").GetProperty("dirs")[0].GetProperty("files")[0].GetString());
		Assert.Contains("class App", payload[separatorIndex..], StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ExportOverwritesExistingFileAndRemovesTailBytes()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var exportPath = Path.Combine(temp.Path, "context.txt");
		await File.WriteAllTextAsync(exportPath, new string('x', 2048), TestContext.Current.CancellationToken);
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Output, exportPath,
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal($"{Path.GetFullPath(exportPath)}{Environment.NewLine}", output.ToString());
		Assert.Equal(string.Empty, error.ToString());
		var payload = await File.ReadAllTextAsync(exportPath, TestContext.Current.CancellationToken);
		Assert.Contains("App.cs", payload, StringComparison.Ordinal);
		Assert.DoesNotContain(new string('x', 64), payload, StringComparison.Ordinal);
	}

	private static CommandLineAutomationContext CreateContext(TextWriter output, TextWriter error) =>
		new(
			Output: output,
			Error: error,
			ServicesFactory: AvaloniaCompositionRoot.CreateDefault,
			HelpContentProvider: new CommandLineHelpContentProvider(),
			VersionProvider: () => "test-version");
}
