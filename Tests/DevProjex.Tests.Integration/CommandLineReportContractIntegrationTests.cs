using DevProjex.Avalonia.Services;
using DevProjex.Infrastructure.Reports;

namespace DevProjex.Tests.Integration;

public sealed class CommandLineReportContractIntegrationTests
{
	[Fact]
	public async Task NoUi_PositionalPathAndReportOptionValue_HandleSpacesAndKeepWorkspaceReadOnly()
	{
		using var temp = new TemporaryDirectory();
		var rootPath = CreateCrossPlatformWorkspace(temp);
		var sourceFile = Path.Combine(rootPath, "src app", "Program.cs");
		var originalSource = await File.ReadAllTextAsync(sourceFile, TestContext.Current.CancellationToken);
		var reportPath = Path.Combine(temp.Path, "reports with spaces", "positional-report.json");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.NoUi,
			rootPath,
			CommandLineOptionTokens.Report, reportPath,
			CommandLineOptionTokens.IncludeRoot, "src app",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal($"{Path.GetFullPath(reportPath)}{Environment.NewLine}", output.ToString());
		Assert.Equal(string.Empty, error.ToString());
		Assert.True(File.Exists(reportPath));
		Assert.Equal(originalSource, await File.ReadAllTextAsync(sourceFile, TestContext.Current.CancellationToken));

		using var document = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken));
		var root = document.RootElement;
		Assert.Equal(rootPath, root.GetProperty("rootPath").GetString());
		Assert.Equal(["src app"], ReadStringArray(root.GetProperty("selection").GetProperty("selectedRootFolders")));
		Assert.Equal([".cs"], ReadStringArray(root.GetProperty("selection").GetProperty("selectedExtensions")));
		Assert.Empty(root.GetProperty("selection").GetProperty("selectedIgnoreOptions").EnumerateArray());
		Assert.Contains("docs", ReadStringArray(root.GetProperty("inventory").GetProperty("availableRootFolders")));
		Assert.Contains("src app", ReadStringArray(root.GetProperty("inventory").GetProperty("availableRootFolders")));
		Assert.Contains(".cs", ReadStringArray(root.GetProperty("inventory").GetProperty("availableExtensions")));
		Assert.Contains(".md", ReadStringArray(root.GetProperty("inventory").GetProperty("availableExtensions")));
		Assert.Equal(1, root.GetProperty("inventory").GetProperty("tree").GetProperty("fileCount").GetInt32());
		Assert.False(root.GetProperty("diagnostics").GetProperty("hadAccessDenied").GetBoolean());
	}

	[Fact]
	public async Task NoUi_ReportFormatJsonWithoutExplicitReportPath_WritesToDefaultReportFolder()
	{
		using var temp = new TemporaryDirectory();
		var rootPath = CreateCrossPlatformWorkspace(temp);
		var documentsPath = Path.Combine(temp.Path, "Documents");
		var expectedReportPath = Path.Combine(
			documentsPath,
			"DevProjex",
			"reports",
			"devprojex-report-2026-06-19_10-15-16-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.json");
		var reportPathResolver = new ReportPathResolver(
			specialFolderPathProvider: folder => folder == Environment.SpecialFolder.MyDocuments ? documentsPath : string.Empty,
			utcNowProvider: () => new DateTimeOffset(2026, 6, 19, 10, 15, 16, TimeSpan.Zero),
			reportIdProvider: () => Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

		await RunNoUiReportAsync(
			expectedReportPath,
			services => services with { ReportPathResolver = reportPathResolver },
			CommandLineOptionTokens.Silent,
			CommandLineOptionTokens.Path, rootPath,
			CommandLineOptionTokens.ReportFormat, "json",
			CommandLineOptionTokens.IncludeRoot, "src app",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		using var document = JsonDocument.Parse(await File.ReadAllTextAsync(expectedReportPath, TestContext.Current.CancellationToken));
		var root = document.RootElement;
		Assert.Equal(ProjectAnalysisReport.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
		Assert.Equal(["src app"], ReadStringArray(root.GetProperty("selection").GetProperty("selectedRootFolders")));
		Assert.Equal([".cs"], ReadStringArray(root.GetProperty("selection").GetProperty("selectedExtensions")));
	}

	[Fact]
	public async Task NoUi_ReportDashWritesJsonToStdoutWithoutCreatingDashFile()
	{
		using var temp = new TemporaryDirectory();
		var rootPath = CreateCrossPlatformWorkspace(temp);
		var dashPath = Path.Combine(Environment.CurrentDirectory, CommandLineOptionTokens.StandardOutputReportPath);
		var dashFileExistedBefore = File.Exists(dashPath);
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Path, rootPath,
			CommandLineOptionTokens.Report, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.IncludeRoot, "src app",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(string.Empty, error.ToString());
		Assert.Equal(dashFileExistedBefore, File.Exists(dashPath));

		using var document = JsonDocument.Parse(output.ToString());
		var root = document.RootElement;
		Assert.Equal(ProjectAnalysisReport.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
		Assert.Equal(rootPath, root.GetProperty("rootPath").GetString());
		Assert.Equal(["src app"], ReadStringArray(root.GetProperty("selection").GetProperty("selectedRootFolders")));
		Assert.Equal([".cs"], ReadStringArray(root.GetProperty("selection").GetProperty("selectedExtensions")));
	}

	[Fact]
	public async Task NoUi_WithoutExplicitOutputWritesJsonToStdoutAndDoesNotCreateDefaultReport()
	{
		using var temp = new TemporaryDirectory();
		var rootPath = CreateCrossPlatformWorkspace(temp);
		var documentsPath = Path.Combine(temp.Path, "Documents");
		var defaultReportsDirectory = Path.Combine(documentsPath, "DevProjex", "reports");
		var reportPathResolver = new ReportPathResolver(
			specialFolderPathProvider: folder => folder == Environment.SpecialFolder.MyDocuments ? documentsPath : string.Empty,
			utcNowProvider: () => new DateTimeOffset(2026, 6, 19, 10, 15, 16, TimeSpan.Zero));
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.Silent,
			rootPath,
			CommandLineOptionTokens.IncludeRoot, "src app",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error, services => services with { ReportPathResolver = reportPathResolver }),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(string.Empty, error.ToString());
		Assert.False(Directory.Exists(defaultReportsDirectory));

		using var document = JsonDocument.Parse(output.ToString());
		var root = document.RootElement;
		Assert.Equal(ProjectAnalysisReport.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
		Assert.Equal(rootPath, root.GetProperty("rootPath").GetString());
		Assert.Equal(["src app"], ReadStringArray(root.GetProperty("selection").GetProperty("selectedRootFolders")));
		Assert.Equal([".cs"], ReadStringArray(root.GetProperty("selection").GetProperty("selectedExtensions")));
	}

	[Fact]
	public async Task NoUi_StrictWithoutExplicitOutputWritesJsonBeforeReturningRuntimeError()
	{
		using var temp = new TemporaryDirectory();
		var rootPath = CreateCrossPlatformWorkspace(temp);
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Strict,
			rootPath,
			CommandLineOptionTokens.IncludeRoot, "missing-root",
			CommandLineOptionTokens.IncludeExtension, "missingext",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.RuntimeError, exitCode);
		Assert.Contains("Strict mode failed", error.ToString(), StringComparison.Ordinal);

		using var document = JsonDocument.Parse(output.ToString());
		var diagnostics = document.RootElement.GetProperty("diagnostics");
		Assert.Contains(
			diagnostics.GetProperty("warnings").EnumerateArray().Select(static warning => warning.GetString()),
			warning => warning?.Contains("missing-root", StringComparison.Ordinal) == true);
	}

	[Fact]
	public async Task NoUi_StrictReturnsRuntimeErrorAfterWritingReportWhenDiagnosticsExist()
	{
		using var temp = new TemporaryDirectory();
		var rootPath = CreateCrossPlatformWorkspace(temp);
		var reportPath = Path.Combine(temp.Path, "reports", "strict-diagnostics.json");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Strict,
			CommandLineOptionTokens.Path, rootPath,
			CommandLineOptionTokens.ReportPath, reportPath,
			CommandLineOptionTokens.IncludeRoot, "missing-root",
			CommandLineOptionTokens.IncludeExtension, "missingext",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.RuntimeError, exitCode);
		Assert.Equal($"{Path.GetFullPath(reportPath)}{Environment.NewLine}", output.ToString());
		Assert.True(File.Exists(reportPath));
		Assert.Contains("Strict mode failed", error.ToString(), StringComparison.Ordinal);
		Assert.Contains("Selected root folder was not found", error.ToString(), StringComparison.Ordinal);
		Assert.Contains("Selected extension was not found", error.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task NoUi_StrictKeepsSuccessWhenReportDiagnosticsAreClean()
	{
		using var temp = new TemporaryDirectory();
		var rootPath = CreateCrossPlatformWorkspace(temp);
		var reportPath = Path.Combine(temp.Path, "reports", "strict-clean.json");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Strict,
			CommandLineOptionTokens.Path, rootPath,
			CommandLineOptionTokens.ReportPath, reportPath,
			CommandLineOptionTokens.IncludeRoot, "src app",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal($"{Path.GetFullPath(reportPath)}{Environment.NewLine}", output.ToString());
		Assert.Equal(string.Empty, error.ToString());
		Assert.True(File.Exists(reportPath));
	}

	[Fact]
	public async Task NoUi_DotFolderIgnoreOptionChangesEffectiveReportTree()
	{
		using var temp = new TemporaryDirectory();
		var rootPath = CreateCrossPlatformWorkspace(temp);
		var noIgnoreReportPath = Path.Combine(temp.Path, "reports", "ignore-none.json");
		var dotFoldersReportPath = Path.Combine(temp.Path, "reports", "ignore-dot-folders.json");

		await RunNoUiReportAsync(
			noIgnoreReportPath,
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Path, rootPath,
			CommandLineOptionTokens.ReportPath, noIgnoreReportPath,
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);
		await RunNoUiReportAsync(
			dotFoldersReportPath,
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Path, rootPath,
			CommandLineOptionTokens.ReportPath, dotFoldersReportPath,
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreDotFolders);

		using var noIgnoreDocument = JsonDocument.Parse(await File.ReadAllTextAsync(noIgnoreReportPath, TestContext.Current.CancellationToken));
		using var dotFoldersDocument = JsonDocument.Parse(await File.ReadAllTextAsync(dotFoldersReportPath, TestContext.Current.CancellationToken));
		var noIgnoreRoot = noIgnoreDocument.RootElement;
		var dotFoldersRoot = dotFoldersDocument.RootElement;

		Assert.Empty(noIgnoreRoot.GetProperty("selection").GetProperty("selectedIgnoreOptions").EnumerateArray());
		Assert.Equal(["dotFolders"], ReadStringArray(dotFoldersRoot.GetProperty("selection").GetProperty("selectedIgnoreOptions")));
		Assert.True(
			ReadTreeFileCount(noIgnoreRoot) > ReadTreeFileCount(dotFoldersRoot),
			"Turning on dot-folder ignore must remove the .cache/cached.cs file from the effective report tree.");
	}

	[Fact]
	public async Task NoUi_SeparatedAndInlineSelectionArguments_ProduceEquivalentStableReportPayloads()
	{
		using var temp = new TemporaryDirectory();
		var rootPath = CreateCrossPlatformWorkspace(temp);
		var separatedReportPath = Path.Combine(temp.Path, "reports", "separated.json");
		var inlineReportPath = Path.Combine(temp.Path, "reports", "inline.json");

		await RunNoUiReportAsync(
			separatedReportPath,
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Path, rootPath,
			CommandLineOptionTokens.ReportPath, separatedReportPath,
			CommandLineOptionTokens.IncludeRoot, "src app",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.IncludeExtension, ".CS",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);
		await RunNoUiReportAsync(
			inlineReportPath,
			CommandLineOptionTokens.NoUi,
			$"{CommandLineOptionTokens.Path}={rootPath}",
			$"{CommandLineOptionTokens.ReportPath}={inlineReportPath}",
			$"{CommandLineOptionTokens.IncludeRoot}=src app",
			$"{CommandLineOptionTokens.IncludeExtension}=cs",
			$"{CommandLineOptionTokens.IncludeExtension}=.CS",
			$"{CommandLineOptionTokens.Ignore}={CommandLineOptionTokens.IgnoreNone}");

		using var separatedDocument = JsonDocument.Parse(await File.ReadAllTextAsync(separatedReportPath, TestContext.Current.CancellationToken));
		using var inlineDocument = JsonDocument.Parse(await File.ReadAllTextAsync(inlineReportPath, TestContext.Current.CancellationToken));
		var separated = separatedDocument.RootElement;
		var inline = inlineDocument.RootElement;

		Assert.Equal(separated.GetProperty("rootPath").GetString(), inline.GetProperty("rootPath").GetString());
		Assert.Equal(separated.GetProperty("selection").GetRawText(), inline.GetProperty("selection").GetRawText());
		Assert.Equal(separated.GetProperty("inventory").GetRawText(), inline.GetProperty("inventory").GetRawText());
		Assert.Equal(separated.GetProperty("metrics").GetRawText(), inline.GetProperty("metrics").GetRawText());
		Assert.Equal(separated.GetProperty("diagnostics").GetRawText(), inline.GetProperty("diagnostics").GetRawText());
	}

	[Fact]
	public async Task NoUi_UnknownSelectionOverrides_CreateReportWithWarningsInsteadOfFailing()
	{
		using var temp = new TemporaryDirectory();
		var rootPath = CreateCrossPlatformWorkspace(temp);
		var reportPath = Path.Combine(temp.Path, "reports", "unknown-selection.json");

		await RunNoUiReportAsync(
			reportPath,
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Path, rootPath,
			CommandLineOptionTokens.ReportPath, reportPath,
			CommandLineOptionTokens.IncludeRoot, "missing-root",
			CommandLineOptionTokens.IncludeExtension, "missingext",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		using var document = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken));
		var root = document.RootElement;
		var warnings = ReadStringArray(root.GetProperty("diagnostics").GetProperty("warnings"));

		Assert.Equal(["missing-root"], ReadStringArray(root.GetProperty("selection").GetProperty("selectedRootFolders")));
		Assert.Equal([".missingext"], ReadStringArray(root.GetProperty("selection").GetProperty("selectedExtensions")));
		Assert.Contains("Selected root folder was not found in the current project: missing-root", warnings);
		Assert.Contains("Selected extension was not found in the current project: .missingext", warnings);
		Assert.Equal(0, root.GetProperty("inventory").GetProperty("tree").GetProperty("fileCount").GetInt32());
	}

	[Fact]
	public async Task NoUi_ReportWriterReplacesExistingReportAndLeavesNoTemporaryFiles()
	{
		using var temp = new TemporaryDirectory();
		var rootPath = CreateCrossPlatformWorkspace(temp);
		var reportPath = Path.Combine(temp.Path, "reports", "replace-existing.json");
		Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
		await File.WriteAllTextAsync(reportPath, "stale report", TestContext.Current.CancellationToken);

		await RunNoUiReportAsync(
			reportPath,
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Path, rootPath,
			CommandLineOptionTokens.ReportPath, reportPath,
			CommandLineOptionTokens.IncludeRoot, "src app",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		using var document = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken));
		Assert.Equal(ProjectAnalysisReport.CurrentSchemaVersion, document.RootElement.GetProperty("schemaVersion").GetInt32());
		Assert.DoesNotContain(
			Directory.EnumerateFiles(Path.GetDirectoryName(reportPath)!, "*.tmp"),
			path => Path.GetFileName(path).Contains("replace-existing", StringComparison.Ordinal));
	}

	private static async Task RunNoUiReportAsync(string expectedReportPath, params string[] args)
		=> await RunNoUiReportAsync(expectedReportPath, configureServices: null, args);

	private static async Task RunNoUiReportAsync(
		string expectedReportPath,
		Func<AvaloniaAppServices, AvaloniaAppServices>? configureServices,
		params string[] args)
	{
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse(args);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(output, error, configureServices),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal($"{Path.GetFullPath(expectedReportPath)}{Environment.NewLine}", output.ToString());
		Assert.Equal(string.Empty, error.ToString());
		Assert.True(File.Exists(expectedReportPath));
	}

	private static CommandLineAutomationContext CreateContext(
		TextWriter output,
		TextWriter error,
		Func<AvaloniaAppServices, AvaloniaAppServices>? configureServices = null) =>
		new(
			Output: output,
			Error: error,
			ServicesFactory: options =>
			{
				var services = AvaloniaCompositionRoot.CreateDefault(options);
				return configureServices?.Invoke(services) ?? services;
			},
			HelpContentProvider: new CommandLineHelpContentProvider(),
			VersionProvider: () => "test-version");

	private static string CreateCrossPlatformWorkspace(TemporaryDirectory temp)
	{
		var rootFolder = "project with spaces";
		temp.CreateFile(Path.Combine(rootFolder, "src app", "Program.cs"), "class Program {}\n");
		temp.CreateFile(Path.Combine(rootFolder, "src app", "appsettings.json"), "{}\n");
		temp.CreateFile(Path.Combine(rootFolder, "docs", "README.md"), "# Docs\n");
		temp.CreateFile(Path.Combine(rootFolder, "tests", "ProgramTests.cs"), "class ProgramTests {}\n");
		temp.CreateFile(Path.Combine(rootFolder, ".cache", "cached.cs"), "class Cached {}\n");
		return Path.Combine(temp.Path, rootFolder);
	}

	private static string[] ReadStringArray(JsonElement element) =>
		element.EnumerateArray()
			.Select(static item => item.GetString() ?? string.Empty)
			.ToArray();

	private static int ReadTreeFileCount(JsonElement root) =>
		root.GetProperty("inventory").GetProperty("tree").GetProperty("fileCount").GetInt32();
}
