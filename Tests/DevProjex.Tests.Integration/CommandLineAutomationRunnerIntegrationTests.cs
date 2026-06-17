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

	private static CommandLineAutomationContext CreateContext(TextWriter output, TextWriter error) =>
		new(
			Output: output,
			Error: error,
			ServicesFactory: AvaloniaCompositionRoot.CreateDefault,
			HelpContentProvider: new CommandLineHelpContentProvider(),
			VersionProvider: () => "test-version");
}
