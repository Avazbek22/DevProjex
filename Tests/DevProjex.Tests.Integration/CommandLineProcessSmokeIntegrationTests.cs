using DevProjex.Avalonia;
using DevProjex.Infrastructure.TerminalCommands;

namespace DevProjex.Tests.Integration;

public sealed class CommandLineProcessSmokeIntegrationTests
{
	[Theory]
	[InlineData(CommandLineOptionTokens.Help)]
	[InlineData(CommandLineOptionTokens.ShortHelp)]
	[InlineData(CommandLineOptionTokens.WindowsHelp)]
	public async Task Process_HelpAliasesPrintHelpAndExitZero(string helpToken)
	{
		var result = await RunAppAsync(helpToken);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Contains("Usage:", result.Stdout, StringComparison.Ordinal);
		Assert.Contains(helpToken, result.Stdout, StringComparison.Ordinal);
		Assert.Equal(string.Empty, result.Stderr);
	}

	[Theory]
	[InlineData(CommandLineOptionTokens.Help)]
	[InlineData(CommandLineOptionTokens.ShortHelp)]
	[InlineData(CommandLineOptionTokens.WindowsHelp)]
	public async Task WindowsPortableLauncher_HelpAliasesPrintHelpToCurrentConsole(string helpToken)
	{
		if (!OperatingSystem.IsWindows())
			return;

		using var temp = new TemporaryDirectory();
		var appAssemblyPath = typeof(App).Assembly.Location;
		var launcherPath = Path.Combine(temp.Path, CommandLineExecutableAliases.WindowsPortableCommandFileName);
		var appExecutablePath = Path.ChangeExtension(appAssemblyPath, ".exe");
		await File.WriteAllTextAsync(
			launcherPath,
			TerminalCommandSetupService.BuildWindowsLauncherContent(appExecutablePath),
			TestContext.Current.CancellationToken);

		var result = await RunWindowsCommandAsync(launcherPath, helpToken);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Contains("Usage:", result.Stdout, StringComparison.Ordinal);
		Assert.Contains(helpToken, result.Stdout, StringComparison.Ordinal);
		Assert.Equal(string.Empty, result.Stderr);
	}

	[Fact]
	public async Task WindowsExecutable_HelpPrintsHelpToRedirectedStdout()
	{
		if (!OperatingSystem.IsWindows())
			return;

		var appExecutablePath = Path.ChangeExtension(typeof(App).Assembly.Location, ".exe");
		Assert.True(File.Exists(appExecutablePath), $"Expected Windows apphost executable at {appExecutablePath}.");

		var result = await RunExecutableAsync(appExecutablePath, CommandLineOptionTokens.Help);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Contains("Usage:", result.Stdout, StringComparison.Ordinal);
		Assert.Contains(CommandLineOptionTokens.Help, result.Stdout, StringComparison.Ordinal);
		Assert.Equal(string.Empty, result.Stderr);
	}

	[Fact]
	public async Task Process_HelpWinsOverInvalidArgumentsAndStillExitsZero()
	{
		var result = await RunAppAsync(CommandLineOptionTokens.Help, "--unknown", CommandLineOptionTokens.NoUi);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Contains("Usage:", result.Stdout, StringComparison.Ordinal);
		Assert.Equal(string.Empty, result.Stderr);
	}

	[Fact]
	public async Task Process_HelpDocumentsAllSupportedCommandNames()
	{
		var result = await RunAppAsync(CommandLineOptionTokens.Help);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		foreach (var commandName in CommandLineExecutableAliases.DocumentedCommandNames)
			Assert.Contains(commandName, result.Stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_VersionPrintsVersionAndExitsZero()
	{
		var result = await RunAppAsync(CommandLineOptionTokens.Version);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.False(string.IsNullOrWhiteSpace(result.Stdout));
		Assert.Equal(string.Empty, result.Stderr);
		var version = Assert.Single(result.Stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
		Assert.DoesNotContain("+", version, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_VersionWinsOverInvalidArgumentsAndNoUi()
	{
		var result = await RunAppAsync(CommandLineOptionTokens.Version, "--unknown", CommandLineOptionTokens.NoUi);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.False(string.IsNullOrWhiteSpace(result.Stdout));
		Assert.Equal(string.Empty, result.Stderr);
	}

	[Fact]
	public async Task Process_NoUiWritesReportAndPrintsReportPath()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var reportPath = Path.Combine(temp.Path, "reports", "process-report.json");

		var result = await RunAppAsync(
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.ReportPath, reportPath,
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal($"{Path.GetFullPath(reportPath)}{Environment.NewLine}", result.Stdout);
		Assert.Equal(string.Empty, result.Stderr);
		Assert.True(File.Exists(reportPath));
	}

	[Fact]
	public async Task Process_SilentAliasRunsHeadlessReport()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var reportPath = Path.Combine(temp.Path, "reports", "silent-report.json");

		var result = await RunAppAsync(
			CommandLineOptionTokens.Silent,
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.ReportPath, reportPath,
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal($"{Path.GetFullPath(reportPath)}{Environment.NewLine}", result.Stdout);
		Assert.Equal(string.Empty, result.Stderr);
		Assert.True(File.Exists(reportPath));
	}

	[Fact]
	public async Task Process_ExportTreeToStdoutWithoutNoUi()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		temp.CreateFile(Path.Combine("docs", "Guide.md"), "# Guide\n");

		var result = await RunAppAsync(
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		Assert.Contains("App.cs", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("Guide.md", result.Stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_ExportTreeFromCurrentDirectory_NormalizesRootAndAppliesDefaultIgnores()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "[Bb]in/\n[Oo]bj/\n");
		temp.CreateFile("App.csproj", "<Project />\n");
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		temp.CreateFile(Path.Combine("bin", "Debug", "DevProjex.dll"), "binary\n");
		temp.CreateFile(Path.Combine("obj", "Release", "Generated.g.cs"), "generated\n");
		temp.CreateFile(Path.Combine("Infrastructure_artifacts_temp", "temp-build", "obj", "Release", "net10.0", "Generated.g.cs"), "generated\n");

		var result = await RunAppWithWorkingDirectoryAsync(
			temp.Path,
			".",
			CommandLineOptionTokens.Export, "tree");

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);

		// macOS can expose the same temp directory as either /var/... or /private/var/... across process boundaries.
		var expectedRoot = GetComparablePath(temp.Path);
		var printedRootLine = result.Stdout
			.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.FirstOrDefault() ?? string.Empty;
		var printedRoot = GetComparablePath(printedRootLine.TrimEnd(':'));

		Assert.Equal(expectedRoot, printedRoot);
		Assert.False(result.Stdout.StartsWith($".:{Environment.NewLine}", StringComparison.Ordinal));
		Assert.Contains("App.cs", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("├── .", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("bin", result.Stdout, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("obj", result.Stdout, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("Generated.g.cs", result.Stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_ExportTreeContentToRelativeOutputFromWorkingDirectory()
	{
		using var temp = new TemporaryDirectory();
		var projectPath = temp.CreateDirectory("project with spaces");
		temp.CreateFile(Path.Combine("project with spaces", "src", "App.cs"), "class App {}\n");
		var relativeOutputPath = Path.Combine("exports", "context.txt");
		var expectedOutputPath = Path.GetFullPath(Path.Combine(temp.Path, relativeOutputPath));

		var result = await RunAppWithWorkingDirectoryAsync(
			temp.Path,
			projectPath,
			CommandLineOptionTokens.Export, "tree-content",
			CommandLineOptionTokens.Output, relativeOutputPath,
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		var printedOutputPath = AssertSingleOutputLine(result.Stdout);
		AssertRelativeOutputPathResolvedFromWorkingDirectory(
			printedOutputPath,
			expectedOutputPath,
			projectPath,
			relativeOutputPath);
		var payload = await File.ReadAllTextAsync(printedOutputPath, TestContext.Current.CancellationToken);
		Assert.Contains("App.cs", payload, StringComparison.Ordinal);
		Assert.Contains("class App", payload, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_ConvenienceAliasesExportTreeContentToRelativeOutput()
	{
		using var temp = new TemporaryDirectory();
		var projectPath = temp.CreateDirectory("project with spaces");
		temp.CreateFile(Path.Combine("project with spaces", "src", "App.cs"), "class App {}\n");
		temp.CreateFile(Path.Combine("project with spaces", "src", "appsettings.json"), "{}\n");
		temp.CreateFile(Path.Combine("project with spaces", "docs", "Guide.md"), "# Guide\n");
		var relativeOutputPath = Path.Combine("exports", "alias-context.txt");
		var expectedOutputPath = Path.GetFullPath(Path.Combine(temp.Path, relativeOutputPath));

		var result = await RunAppWithWorkingDirectoryAsync(
			temp.Path,
			projectPath,
			CommandLineOptionTokens.Export, "tree-content",
			CommandLineOptionTokens.ShortOutput, relativeOutputPath,
			CommandLineOptionTokens.Roots, "src",
			CommandLineOptionTokens.Extensions, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		var printedOutputPath = AssertSingleOutputLine(result.Stdout);
		AssertRelativeOutputPathResolvedFromWorkingDirectory(
			printedOutputPath,
			expectedOutputPath,
			projectPath,
			relativeOutputPath);
		var payload = await File.ReadAllTextAsync(printedOutputPath, TestContext.Current.CancellationToken);
		Assert.Contains("App.cs", payload, StringComparison.Ordinal);
		Assert.Contains("class App", payload, StringComparison.Ordinal);
		Assert.DoesNotContain("appsettings.json", payload, StringComparison.Ordinal);
		Assert.DoesNotContain("Guide.md", payload, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_ExportJsonTreeToStdoutWritesJsonOnly()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");

		var result = await RunAppAsync(
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.ExportFormat, "json",
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		using var document = JsonDocument.Parse(result.Stdout);
		Assert.Equal(Path.GetFullPath(temp.Path), document.RootElement.GetProperty("rootPath").GetString());
		Assert.DoesNotContain("class App", result.Stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_FormatAliasWritesJsonTreeToStdout()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");

		var result = await RunAppAsync(
			temp.Path,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Format, "json",
			CommandLineOptionTokens.Roots, "src",
			CommandLineOptionTokens.Extensions, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		using var document = JsonDocument.Parse(result.Stdout);
		Assert.Equal("App.cs", document.RootElement.GetProperty("root").GetProperty("dirs")[0].GetProperty("files")[0].GetString());
		Assert.DoesNotContain("class App", result.Stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_ReportDashWritesJsonToStdout()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");

		var result = await RunAppAsync(
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Report, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);

		using var document = JsonDocument.Parse(result.Stdout);
		Assert.Equal(ProjectAnalysisReport.CurrentSchemaVersion, document.RootElement.GetProperty("schemaVersion").GetInt32());
		Assert.Equal(temp.Path, document.RootElement.GetProperty("rootPath").GetString());
	}

	[Fact]
	public async Task Process_SilentReportDashWithJsonFormatWritesOnlyJsonToStdout()
	{
		using var temp = new TemporaryDirectory();
		var projectPath = temp.CreateDirectory("project with spaces");
		temp.CreateFile(Path.Combine("project with spaces", "src app", "Program.cs"), "class Program {}\n");
		temp.CreateFile(Path.Combine("project with spaces", ".cache", "Cached.cs"), "class Cached {}\n");
		var dashPath = Path.Combine(Environment.CurrentDirectory, CommandLineOptionTokens.StandardOutputReportPath);
		var dashFileExistedBefore = File.Exists(dashPath);

		var result = await RunAppAsync(
			CommandLineOptionTokens.Silent,
			CommandLineOptionTokens.Path, projectPath,
			CommandLineOptionTokens.Report, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.ReportFormat, "json",
			CommandLineOptionTokens.IncludeRoot, "src app",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreDotFolders);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		Assert.Equal(dashFileExistedBefore, File.Exists(dashPath));
		Assert.StartsWith("{", result.Stdout.TrimStart(), StringComparison.Ordinal);
		Assert.EndsWith("}", result.Stdout.TrimEnd(), StringComparison.Ordinal);

		using var document = JsonDocument.Parse(result.Stdout);
		var root = document.RootElement;
		var selection = root.GetProperty("selection");
		var inventory = root.GetProperty("inventory");

		Assert.Equal(ProjectAnalysisReport.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
		Assert.Equal(projectPath, root.GetProperty("rootPath").GetString());
		Assert.Equal(["src app"], ReadStringArray(selection.GetProperty("selectedRootFolders")));
		Assert.Equal([".cs"], ReadStringArray(selection.GetProperty("selectedExtensions")));
		Assert.Equal(["dotFolders"], ReadStringArray(selection.GetProperty("selectedIgnoreOptions")));
		Assert.Equal(1, inventory.GetProperty("tree").GetProperty("fileCount").GetInt32());
	}

	[Fact]
	public async Task Process_StrictReturnsRuntimeErrorWhenReportContainsWarnings()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var reportPath = Path.Combine(temp.Path, "reports", "strict-warning.json");

		var result = await RunAppAsync(
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Strict,
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.ReportPath, reportPath,
			CommandLineOptionTokens.IncludeRoot, "missing-root",
			CommandLineOptionTokens.IncludeExtension, "missingext",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.RuntimeError, result.ExitCode);
		Assert.Equal($"{Path.GetFullPath(reportPath)}{Environment.NewLine}", result.Stdout);
		Assert.Contains("Strict mode failed", result.Stderr, StringComparison.Ordinal);
		Assert.True(File.Exists(reportPath));
	}

	[Fact]
	public async Task Process_NoUiSupportsPositionalPathAndRelativeReportFromWorkingDirectory()
	{
		using var temp = new TemporaryDirectory();
		var projectPath = temp.CreateDirectory("project with spaces");
		temp.CreateFile(Path.Combine("project with spaces", "src", "App.cs"), "class App {}\n");
		var relativeReportPath = Path.Combine("reports", "relative-process-report.json");
		var expectedReportPath = Path.GetFullPath(Path.Combine(temp.Path, relativeReportPath));

		var result = await RunAppWithWorkingDirectoryAsync(
			temp.Path,
			CommandLineOptionTokens.NoUi,
			projectPath,
			CommandLineOptionTokens.Report, relativeReportPath,
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		var reportedReportPath = AssertSingleOutputLine(result.Stdout);
		AssertRelativeReportPathResolvedFromWorkingDirectory(
			reportedReportPath,
			expectedReportPath,
			projectPath,
			relativeReportPath);
		Assert.True(File.Exists(expectedReportPath));
	}

	[Fact]
	public async Task Process_NoUiSupportsInlineValueSyntax()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var reportPath = Path.Combine(temp.Path, "reports", "inline-report.json");

		var result = await RunAppAsync(
			CommandLineOptionTokens.NoUi,
			$"{CommandLineOptionTokens.Path}={temp.Path}",
			$"{CommandLineOptionTokens.ReportPath}={reportPath}",
			$"{CommandLineOptionTokens.IncludeRoot}=src",
			$"{CommandLineOptionTokens.IncludeExtension}=cs",
			$"{CommandLineOptionTokens.Ignore}={CommandLineOptionTokens.IgnoreNone}");

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal($"{Path.GetFullPath(reportPath)}{Environment.NewLine}", result.Stdout);
		Assert.Equal(string.Empty, result.Stderr);
		Assert.True(File.Exists(reportPath));
	}

	[Fact]
	public async Task Process_SilentFullAutomationCommandWritesStableJsonReport()
	{
		using var temp = new TemporaryDirectory();
		var projectPath = temp.CreateDirectory("project with spaces");
		temp.CreateFile(Path.Combine("project with spaces", "src app", "Program.cs"), "class Program {}\n");
		temp.CreateFile(Path.Combine("project with spaces", "src app", "appsettings.json"), "{}\n");
		temp.CreateFile(Path.Combine("project with spaces", ".cache", "Cached.cs"), "class Cached {}\n");
		var reportPath = Path.Combine(temp.Path, "reports with spaces", "full-command-report.json");

		var result = await RunAppAsync(
			CommandLineOptionTokens.Silent,
			CommandLineOptionTokens.Path, projectPath,
			CommandLineOptionTokens.ReportPath, reportPath,
			CommandLineOptionTokens.ReportFormat, "json",
			CommandLineOptionTokens.IncludeRoot, "src app",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.IncludeExtension, ".CS",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreDotFolders);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal($"{Path.GetFullPath(reportPath)}{Environment.NewLine}", result.Stdout);
		Assert.Equal(string.Empty, result.Stderr);
		Assert.True(File.Exists(reportPath));

		using var document = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken));
		var root = document.RootElement;
		var selection = root.GetProperty("selection");
		var inventory = root.GetProperty("inventory");
		var diagnostics = root.GetProperty("diagnostics");

		Assert.Equal(ProjectAnalysisReport.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
		Assert.Equal(projectPath, root.GetProperty("rootPath").GetString());
		Assert.Equal(["src app"], ReadStringArray(selection.GetProperty("selectedRootFolders")));
		Assert.Equal([".cs"], ReadStringArray(selection.GetProperty("selectedExtensions")));
		Assert.Equal(["dotFolders"], ReadStringArray(selection.GetProperty("selectedIgnoreOptions")));
		Assert.Contains("src app", ReadStringArray(inventory.GetProperty("availableRootFolders")));
		Assert.Contains(".cs", ReadStringArray(inventory.GetProperty("availableExtensions")));
		Assert.Equal(1, inventory.GetProperty("tree").GetProperty("fileCount").GetInt32());
		Assert.False(diagnostics.GetProperty("rootAccessDenied").GetBoolean());
		Assert.False(diagnostics.GetProperty("hadAccessDenied").GetBoolean());
		Assert.Empty(diagnostics.GetProperty("warnings").EnumerateArray());
	}

	[Fact]
	public async Task Process_NoUiInvalidCombinationWritesStderrAndUsageExitCode()
	{
		var result = await RunAppAsync(CommandLineOptionTokens.NoUi, CommandLineOptionTokens.Report);

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Equal(string.Empty, result.Stdout);
		Assert.Contains("Headless analysis requires --path", result.Stderr, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_ReportStdoutAndExportConflictReturnsUsageError()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");

		var result = await RunAppAsync(
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Report, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.Export, "tree");

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Equal(string.Empty, result.Stdout);
		Assert.Contains("Cannot combine --report - with --export", result.Stderr, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_InvalidReportFormatReturnsUsageErrorBeforeCreatingReport()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var reportPath = Path.Combine(temp.Path, "reports", "invalid-format.json");

		var result = await RunAppAsync(
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.ReportPath, reportPath,
			CommandLineOptionTokens.ReportFormat, "xml");

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Equal(string.Empty, result.Stdout);
		Assert.Contains("Unsupported report format 'xml'.", result.Stderr, StringComparison.Ordinal);
		Assert.False(File.Exists(reportPath));
	}

	[Fact]
	public async Task Process_InvalidExportModeReturnsUsageErrorBeforeCreatingOutput()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var outputPath = Path.Combine(temp.Path, "context.txt");

		var result = await RunAppAsync(
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "zip",
			CommandLineOptionTokens.Output, outputPath);

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Equal(string.Empty, result.Stdout);
		Assert.Contains("Unsupported export mode 'zip'.", result.Stderr, StringComparison.Ordinal);
		Assert.False(File.Exists(outputPath));
	}

	[Fact]
	public async Task Process_InvalidExportFormatReturnsUsageErrorBeforeCreatingOutput()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var outputPath = Path.Combine(temp.Path, "context.txt");

		var result = await RunAppAsync(
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Output, outputPath,
			CommandLineOptionTokens.ExportFormat, "xml");

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Equal(string.Empty, result.Stdout);
		Assert.Contains("Unsupported export format 'xml'.", result.Stderr, StringComparison.Ordinal);
		Assert.False(File.Exists(outputPath));
	}

	[Fact]
	public async Task Process_FormatAliasAsciiWithoutExportReturnsUsageErrorInsteadOfOpeningUi()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");

		var result = await RunAppAsync(
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Format, "ascii");

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Equal(string.Empty, result.Stdout);
		Assert.Contains("--output and --export-format require --export", result.Stderr, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_ReportPathPointingToExistingDirectoryReturnsRuntimeError()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var reportDirectoryPath = temp.CreateDirectory("existing-report-directory");

		var result = await RunAppAsync(
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.ReportPath, reportDirectoryPath,
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.RuntimeError, result.ExitCode);
		Assert.Equal(string.Empty, result.Stdout);
		Assert.StartsWith("DevProjex: ", result.Stderr, StringComparison.Ordinal);
		Assert.True(Directory.Exists(reportDirectoryPath));
	}

	[Fact]
	public async Task Process_ParseErrorWritesStderrAndDoesNotStartUi()
	{
		var result = await RunAppAsync("--unknown");

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Equal(string.Empty, result.Stdout);
		Assert.Contains("Unknown option '--unknown'.", result.Stderr, StringComparison.Ordinal);
	}

	private static Task<CommandLineProcessResult> RunAppAsync(params string[] args) =>
		RunAppCoreAsync(workingDirectory: null, args);

	private static Task<CommandLineProcessResult> RunAppWithWorkingDirectoryAsync(string workingDirectory, params string[] args) =>
		RunAppCoreAsync(workingDirectory, args);

	private static async Task<CommandLineProcessResult> RunAppCoreAsync(string? workingDirectory, params string[] args)
	{
		var appPath = typeof(App).Assembly.Location;
		var startInfo = new ProcessStartInfo
		{
			FileName = "dotnet",
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};

		if (!string.IsNullOrWhiteSpace(workingDirectory))
			startInfo.WorkingDirectory = workingDirectory;

		startInfo.ArgumentList.Add(appPath);
		foreach (var arg in args)
			startInfo.ArgumentList.Add(arg);

		return await RunProcessAsync(startInfo);
	}

	private static async Task<CommandLineProcessResult> RunWindowsCommandAsync(string commandPath, params string[] args)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = "cmd.exe",
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};

		startInfo.ArgumentList.Add("/d");
		startInfo.ArgumentList.Add("/c");
		startInfo.ArgumentList.Add(commandPath);
		foreach (var arg in args)
			startInfo.ArgumentList.Add(arg);

		return await RunProcessAsync(startInfo);
	}

	private static async Task<CommandLineProcessResult> RunExecutableAsync(string executablePath, params string[] args)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = executablePath,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};

		foreach (var arg in args)
			startInfo.ArgumentList.Add(arg);

		return await RunProcessAsync(startInfo);
	}

	private static async Task<CommandLineProcessResult> RunProcessAsync(ProcessStartInfo startInfo)
	{
		using var process = Process.Start(startInfo)
			?? throw new InvalidOperationException("Failed to start DevProjex command-line smoke process.");
		var stdoutTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
		var stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
		var waitForExitTask = process.WaitForExitAsync(TestContext.Current.CancellationToken);
		var completedTask = await Task.WhenAny(
			waitForExitTask,
			Task.Delay(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));

		if (completedTask != waitForExitTask)
		{
			TryKill(process);
			throw new TimeoutException("DevProjex command-line smoke process did not exit within 20 seconds.");
		}

		return new CommandLineProcessResult(
			process.ExitCode,
			await stdoutTask,
			await stderrTask);
	}

	private static void TryKill(Process process)
	{
		try
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
		}
		catch
		{
			// The test is already failing on timeout; process cleanup is best effort.
		}
	}

	private static string[] ReadStringArray(JsonElement element) =>
		element.EnumerateArray()
			.Select(static item => item.GetString() ?? string.Empty)
			.ToArray();

	private static string AssertSingleOutputLine(string stdout)
	{
		var outputLines = stdout
			.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		return Assert.Single(outputLines);
	}

	private static void AssertRelativeReportPathResolvedFromWorkingDirectory(
		string reportedReportPath,
		string expectedReportPath,
		string projectPath,
		string relativeReportPath)
	{
		Assert.True(
			Path.IsPathFullyQualified(reportedReportPath),
			$"Expected the report path printed to stdout to be absolute, but got '{reportedReportPath}'.");
		Assert.EndsWith(relativeReportPath, reportedReportPath, StringComparison.Ordinal);
		Assert.False(
			IsPathUnderDirectory(reportedReportPath, projectPath),
			$"Relative report paths must resolve from the process working directory, not from the project path '{projectPath}'.");
		Assert.True(
			File.Exists(expectedReportPath),
			$"Expected the report file to be reachable through the requested working-directory path '{expectedReportPath}'.");
		Assert.True(
			File.Exists(reportedReportPath),
			$"Expected the report file to exist at the path printed by the app: '{reportedReportPath}'.");
	}

	private static void AssertRelativeOutputPathResolvedFromWorkingDirectory(
		string printedOutputPath,
		string expectedOutputPath,
		string projectPath,
		string relativeOutputPath)
	{
		Assert.True(
			Path.IsPathFullyQualified(printedOutputPath),
			$"Expected the export path printed to stdout to be absolute, but got '{printedOutputPath}'.");
		Assert.EndsWith(relativeOutputPath, printedOutputPath, StringComparison.Ordinal);
		Assert.False(
			IsPathUnderDirectory(printedOutputPath, projectPath),
			$"Relative export paths must resolve from the process working directory, not from the project path '{projectPath}'.");
		Assert.True(
			File.Exists(expectedOutputPath),
			$"Expected the export file to be reachable through the requested working-directory path '{expectedOutputPath}'.");
		Assert.True(
			File.Exists(printedOutputPath),
			$"Expected the export file to exist at the path printed by the app: '{printedOutputPath}'.");
	}

	private static bool IsPathUnderDirectory(string path, string directory)
	{
		var comparison = OperatingSystem.IsWindows()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
		var fullPath = AddTrailingDirectorySeparator(GetComparablePath(path));
		var fullDirectory = AddTrailingDirectorySeparator(GetComparablePath(directory));
		return fullPath.StartsWith(fullDirectory, comparison);
	}

	private static string GetComparablePath(string path)
	{
		var fullPath = Path.GetFullPath(path);
		return OperatingSystem.IsMacOS()
			? NormalizeMacOsPrivateVarAlias(fullPath)
			: fullPath;
	}

	private static string NormalizeMacOsPrivateVarAlias(string path)
	{
		const string privateVarPrefix = "/private/var/";
		const string varPrefix = "/var/";

		// macOS temp paths can surface as either /var/... or /private/var/... depending on the process boundary.
		if (path.StartsWith(privateVarPrefix, StringComparison.Ordinal))
			return varPrefix + path[privateVarPrefix.Length..];

		return path;
	}

	private static string AddTrailingDirectorySeparator(string path)
	{
		var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		return trimmed + Path.DirectorySeparatorChar;
	}

	private sealed record CommandLineProcessResult(int ExitCode, string Stdout, string Stderr);
}
