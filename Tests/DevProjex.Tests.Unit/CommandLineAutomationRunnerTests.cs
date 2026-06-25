using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit;

public sealed class CommandLineAutomationRunnerTests
{
	[Fact]
	public async Task RunUtilityOrHeadlessAsync_HelpWinsOverOtherParseErrorsAndWritesStdout()
	{
		using var output = new StringWriter();
		using var error = new StringWriter();
		var context = CreateContext(output, error);
		var parseResult = CommandLineOptions.Parse([CommandLineOptionTokens.Help, "--unknown"]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			context,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Contains("Usage:", output.ToString(), StringComparison.Ordinal);
		Assert.Equal(string.Empty, error.ToString());
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_VersionWritesStdout()
	{
		using var output = new StringWriter();
		using var error = new StringWriter();
		var context = CreateContext(output, error, version: "9.8.7-test");
		var parseResult = CommandLineOptions.Parse([CommandLineOptionTokens.Version]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			context,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal($"9.8.7-test{Environment.NewLine}", output.ToString());
		Assert.Equal(string.Empty, error.ToString());
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ParseErrorWritesStderrAndUsageExitCode()
	{
		using var output = new StringWriter();
		using var error = new StringWriter();
		var context = CreateContext(output, error);
		var parseResult = CommandLineOptions.Parse(["--unknown"]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			context,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Equal(string.Empty, output.ToString());
		Assert.Contains("DevProjex: Unknown option '--unknown'.", error.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_NoUiWithoutPathWritesUsageError()
	{
		using var output = new StringWriter();
		using var error = new StringWriter();
		var context = CreateContext(output, error);
		var parseResult = CommandLineOptions.Parse([CommandLineOptionTokens.NoUi, CommandLineOptionTokens.Report]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			context,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Equal(string.Empty, output.ToString());
		Assert.Contains("Headless analysis requires --path", error.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_NoUiWithoutReportOrExportWritesUsageError()
	{
		using var output = new StringWriter();
		using var error = new StringWriter();
		var context = CreateContext(output, error);
		var parseResult = CommandLineOptions.Parse([CommandLineOptionTokens.NoUi, CommandLineOptionTokens.Path, "/tmp/project"]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			context,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Equal(string.Empty, output.ToString());
		Assert.Contains("requires --report, --report-path, or --export", error.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_OutputWithoutExportWritesUsageErrorBeforeCreatingServices()
	{
		using var output = new StringWriter();
		using var error = new StringWriter();
		var context = CreateContext(output, error);
		var parseResult = CommandLineOptions.Parse([
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Path, "/tmp/project",
			CommandLineOptionTokens.Output, "/tmp/context.txt"
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			context,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Equal(string.Empty, output.ToString());
		Assert.Contains("--output and --export-format require --export", error.ToString(), StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("ascii")]
	[InlineData("json")]
	public async Task RunUtilityOrHeadlessAsync_FormatAliasWithoutExportWritesUsageErrorBeforeCreatingServices(string format)
	{
		using var output = new StringWriter();
		using var error = new StringWriter();
		var context = CreateContext(output, error);
		var parseResult = CommandLineOptions.Parse([
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Path, "/tmp/project",
			CommandLineOptionTokens.Format, format
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			context,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Equal(string.Empty, output.ToString());
		Assert.Contains("--output and --export-format require --export", error.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_RejectsCompetingStdoutPayloadsBeforeCreatingServices()
	{
		using var output = new StringWriter();
		using var error = new StringWriter();
		var context = CreateContext(output, error);
		var parseResult = CommandLineOptions.Parse([
			CommandLineOptionTokens.Path, "/tmp/project",
			CommandLineOptionTokens.Report, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.Export, "tree"
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			context,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Equal(string.Empty, output.ToString());
		Assert.Contains("Cannot combine --report - with --export", error.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_RejectsSameReportAndExportOutputPathBeforeCreatingServices()
	{
		using var output = new StringWriter();
		using var error = new StringWriter();
		var context = CreateContext(output, error);
		var parseResult = CommandLineOptions.Parse([
			CommandLineOptionTokens.Path, "/tmp/project",
			CommandLineOptionTokens.ReportPath, "/tmp/result.txt",
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Output, "/tmp/result.txt"
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			context,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Equal(string.Empty, output.ToString());
		Assert.Contains("--report-path and --output must point to different files", error.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_RejectsJsonFormatForContentExportBeforeCreatingServices()
	{
		using var output = new StringWriter();
		using var error = new StringWriter();
		var context = CreateContext(output, error);
		var parseResult = CommandLineOptions.Parse([
			CommandLineOptionTokens.Path, "/tmp/project",
			CommandLineOptionTokens.Export, "content",
			CommandLineOptionTokens.ExportFormat, "json"
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			context,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Equal(string.Empty, output.ToString());
		Assert.Contains("--export-format applies only to tree", error.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_HeadlessRuntimeFailureWritesStderrAndRuntimeExitCode()
	{
		using var output = new StringWriter();
		using var error = new StringWriter();
		var context = CreateContext(
			output,
			error,
			servicesFactory: _ => throw new IOException("Synthetic report failure."));
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Path, "/tmp/project",
			CommandLineOptionTokens.Report
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			context,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.RuntimeError, exitCode);
		Assert.Equal(string.Empty, output.ToString());
		Assert.Contains("Synthetic report failure.", error.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_HeadlessCancellationWritesStderrAndCanceledExitCode()
	{
		using var output = new StringWriter();
		using var error = new StringWriter();
		var context = CreateContext(
			output,
			error,
			servicesFactory: _ => throw new OperationCanceledException());
		var parseResult = CommandLineOptions.Parse(
		[
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Path, "/tmp/project",
			CommandLineOptionTokens.Report
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			context,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Canceled, exitCode);
		Assert.Equal(string.Empty, output.ToString());
		Assert.Contains("Operation was canceled.", error.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public void ShouldRunBeforeAvalonia_ReturnsTrueOnlyForUtilityOrHeadlessModes()
	{
		Assert.False(CommandLineAutomationRunner.ShouldRunBeforeAvalonia(CommandLineOptions.Parse([])));
		Assert.False(CommandLineAutomationRunner.ShouldRunBeforeAvalonia(CommandLineOptions.Parse([CommandLineOptionTokens.Path, "/tmp/project"])));
		Assert.True(CommandLineAutomationRunner.ShouldRunBeforeAvalonia(CommandLineOptions.Parse(["--unknown"])));
		Assert.True(CommandLineAutomationRunner.ShouldRunBeforeAvalonia(CommandLineOptions.Parse([CommandLineOptionTokens.Help])));
		Assert.True(CommandLineAutomationRunner.ShouldRunBeforeAvalonia(CommandLineOptions.Parse([CommandLineOptionTokens.Version])));
		Assert.True(CommandLineAutomationRunner.ShouldRunBeforeAvalonia(CommandLineOptions.Parse([CommandLineOptionTokens.NoUi])));
		Assert.True(CommandLineAutomationRunner.ShouldRunBeforeAvalonia(CommandLineOptions.Parse([CommandLineOptionTokens.Export, "tree"])));
		Assert.True(CommandLineAutomationRunner.ShouldRunBeforeAvalonia(CommandLineOptions.Parse([CommandLineOptionTokens.Output, "/tmp/context.txt"])));
		Assert.True(CommandLineAutomationRunner.ShouldRunBeforeAvalonia(CommandLineOptions.Parse([CommandLineOptionTokens.ShortOutput, "/tmp/context.txt"])));
		Assert.True(CommandLineAutomationRunner.ShouldRunBeforeAvalonia(CommandLineOptions.Parse([CommandLineOptionTokens.Format, "ascii"])));
		Assert.True(CommandLineAutomationRunner.ShouldRunBeforeAvalonia(CommandLineOptions.Parse([CommandLineOptionTokens.Format, "json"])));
	}

	private static CommandLineAutomationContext CreateContext(
		TextWriter output,
		TextWriter error,
		string version = "test-version",
		Func<CommandLineOptions, AvaloniaAppServices>? servicesFactory = null) =>
		new(
			Output: output,
			Error: error,
			ServicesFactory: servicesFactory ?? (_ => throw new InvalidOperationException("Services should not be created for this command-line scenario.")),
			HelpContentProvider: new CommandLineHelpContentProvider(),
			VersionProvider: () => version);
}
