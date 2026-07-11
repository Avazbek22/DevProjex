using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit;

[Trait("Category", "TerminalCommand")]
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

	[Theory]
	[InlineData("no-ui", CommandLineOptionTokens.NoUi)]
	[InlineData("no-uii", CommandLineOptionTokens.NoUi)]
	[InlineData("-no-ui", CommandLineOptionTokens.NoUi)]
	[InlineData("/no-ui", CommandLineOptionTokens.NoUi)]
	[InlineData("/preview-serch", CommandLineOptionTokens.PreviewSearch)]
	[InlineData("--no-uii", CommandLineOptionTokens.NoUi)]
	[InlineData("--preview-serch", CommandLineOptionTokens.PreviewSearch)]
	public async Task RunUtilityOrHeadlessAsync_OptionTyposWriteStderrAndDoNotCreateServices(
		string value,
		string expectedSuggestion)
	{
		using var output = new StringWriter();
		using var error = new StringWriter();
		var servicesCreated = false;
		var context = CreateContext(
			output,
			error,
			servicesFactory: _ =>
			{
				servicesCreated = true;
				throw new InvalidOperationException("Services must not be created for parser errors.");
			});
		var parseResult = CommandLineOptions.Parse([value]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			context,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.False(servicesCreated);
		Assert.Equal(string.Empty, output.ToString());
		Assert.Contains("DevProjex: ", error.ToString(), StringComparison.Ordinal);
		Assert.Contains($"Did you mean '{expectedSuggestion}'?", error.ToString(), StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("--no-ui=true")]
	[InlineData("--help=true")]
	[InlineData("--preview=true")]
	public async Task RunUtilityOrHeadlessAsync_ValueLessFlagWithInlineValueWritesStderrAndDoesNotCreateServices(string value)
	{
		using var output = new StringWriter();
		using var error = new StringWriter();
		var servicesCreated = false;
		var context = CreateContext(
			output,
			error,
			servicesFactory: _ =>
			{
				servicesCreated = true;
				throw new InvalidOperationException("Services must not be created for parser errors.");
			});
		var parseResult = CommandLineOptions.Parse([value]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			context,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.False(servicesCreated);
		Assert.Equal(string.Empty, output.ToString());
		Assert.Contains("does not accept a value", error.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_CommandStyleExportWritesStderrAndDoesNotCreateServices()
	{
		using var output = new StringWriter();
		using var error = new StringWriter();
		var servicesCreated = false;
		var context = CreateContext(
			output,
			error,
			servicesFactory: _ =>
			{
				servicesCreated = true;
				throw new InvalidOperationException("Services must not be created for parser errors.");
			});
		var parseResult = CommandLineOptions.Parse(["export", "tree"]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			context,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.False(servicesCreated);
		Assert.Equal(string.Empty, output.ToString());
		Assert.Contains("Did you mean '--export'?", error.ToString(), StringComparison.Ordinal);
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
	public async Task RunUtilityOrHeadlessAsync_NoUiWithoutReportOrExportStartsImplicitStdoutReportAnalysis()
	{
		using var output = new StringWriter();
		using var error = new StringWriter();
		var servicesCreated = false;
		var context = CreateContext(
			output,
			error,
			servicesFactory: _ =>
			{
				servicesCreated = true;
				throw new IOException("Synthetic implicit report failure.");
			});
		var parseResult = CommandLineOptions.Parse([CommandLineOptionTokens.NoUi, CommandLineOptionTokens.Path, "/tmp/project"]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			context,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.RuntimeError, exitCode);
		Assert.True(servicesCreated);
		Assert.Equal(string.Empty, output.ToString());
		Assert.Contains("Synthetic implicit report failure.", error.ToString(), StringComparison.Ordinal);
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
	[InlineData("xml")]
	[InlineData("md")]
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
	public async Task RunUtilityOrHeadlessAsync_NoUiWithDesktopStartupOptionsWritesUsageErrorBeforeCreatingServices()
	{
		using var output = new StringWriter();
		using var error = new StringWriter();
		var context = CreateContext(output, error);
		var parseResult = CommandLineOptions.Parse([
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Path, "/tmp/project",
			CommandLineOptionTokens.Preview
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			context,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Equal(string.Empty, output.ToString());
		Assert.Contains("UI startup options cannot be combined", error.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ExportWithDesktopStartupOptionsWritesUsageErrorBeforeCreatingServices()
	{
		using var output = new StringWriter();
		using var error = new StringWriter();
		var context = CreateContext(output, error);
		var parseResult = CommandLineOptions.Parse([
			CommandLineOptionTokens.Path, "/tmp/project",
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.TreeFormat, "md"
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			context,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Equal(string.Empty, output.ToString());
		Assert.Contains("UI startup options cannot be combined", error.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_SilentWithPreviewSearchWritesUsageErrorBeforeCreatingServices()
	{
		using var output = new StringWriter();
		using var error = new StringWriter();
		var servicesCreated = false;
		var context = CreateContext(
			output,
			error,
			servicesFactory: _ =>
			{
				servicesCreated = true;
				throw new InvalidOperationException("Services must not be created.");
			});
		var parseResult = CommandLineOptions.Parse([
			CommandLineOptionTokens.Silent,
			CommandLineOptionTokens.Path, "/tmp/project",
			CommandLineOptionTokens.PreviewSearch, "Program"
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			context,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.False(servicesCreated);
		Assert.Equal(string.Empty, output.ToString());
		Assert.Contains("UI startup options cannot be combined", error.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_ParseErrorFromCompetingDesktopSearchToolsWinsBeforeServices()
	{
		using var output = new StringWriter();
		using var error = new StringWriter();
		var context = CreateContext(output, error);
		var parseResult = CommandLineOptions.Parse([
			CommandLineOptionTokens.Path, "/tmp/project",
			CommandLineOptionTokens.TreeFilter, "src",
			CommandLineOptionTokens.PreviewSearch, "Program"
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			context,
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Equal(string.Empty, output.ToString());
		Assert.Contains("--tree-filter and --preview-search cannot be used together", error.ToString(), StringComparison.Ordinal);
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
	public async Task RunUtilityOrHeadlessAsync_BenchmarkWritesSummaryAndDetailedJsonReport()
	{
		using var runCountOverride = TemporaryEnvironmentVariable.Set("DEVPROJEX_BENCHMARK_RUNS", "2");
		using var warmupOverride = TemporaryEnvironmentVariable.Set("DEVPROJEX_BENCHMARK_WARMUP", "1");
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var reportPath = Path.Combine(temp.Path, "benchmark", "result.json");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var processRunner = new FakeBenchmarkProcessRunner();
		var parseResult = CommandLineOptions.Parse([
			CommandLineOptionTokens.Benchmark, temp.Path,
			CommandLineOptionTokens.BenchmarkOutput, reportPath
		]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(
				output,
				error,
				servicesFactory: AvaloniaCompositionRoot.CreateDefault,
				benchmarkProcessRunner: processRunner),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(string.Empty, error.ToString());
		var summary = output.ToString();
		Assert.Contains("DevProjex benchmark", summary, StringComparison.Ordinal);
		Assert.Contains("Cold process:", summary, StringComparison.Ordinal);
		Assert.Contains("Warm pipeline:", summary, StringComparison.Ordinal);
		Assert.Contains(Path.GetFullPath(reportPath).Replace('\\', '/'), summary, StringComparison.Ordinal);
		Assert.DoesNotContain("{", summary, StringComparison.Ordinal);
		Assert.True(File.Exists(reportPath));
		Assert.Equal(3, processRunner.Requests.Count);
		Assert.All(processRunner.Requests, request =>
		{
			Assert.Contains(CommandLineOptionTokens.NoUi, request.Arguments);
			Assert.Contains(CommandLineOptionTokens.Path, request.Arguments);
			Assert.Contains(temp.Path, request.Arguments);
			Assert.Contains(CommandLineOptionTokens.Report, request.Arguments);
			Assert.Contains(CommandLineOptionTokens.StandardOutputReportPath, request.Arguments);
			Assert.DoesNotContain(CommandLineOptionTokens.Benchmark, request.Arguments);
		});

		using var document = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken));
		var root = document.RootElement;
		Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
		Assert.Equal(Path.GetFullPath(temp.Path).Replace('\\', '/'), root.GetProperty("targetPath").GetString());
		Assert.Equal(2, root.GetProperty("configuration").GetProperty("runs").GetInt32());
		Assert.Equal(1, root.GetProperty("configuration").GetProperty("warmup").GetInt32());
		Assert.False(root.GetProperty("hasFailures").GetBoolean());
		Assert.Equal(1, root.GetProperty("coldProcess").GetProperty("warmupRuns").GetArrayLength());
		Assert.Equal(2, root.GetProperty("coldProcess").GetProperty("runs").GetArrayLength());
		Assert.Equal(1, root.GetProperty("warmPipeline").GetProperty("warmupRuns").GetArrayLength());
		Assert.Equal(2, root.GetProperty("warmPipeline").GetProperty("runs").GetArrayLength());
		Assert.True(root.GetProperty("warmPipeline").GetProperty("runs")[0].GetProperty("stdoutBytes").GetInt32() > 0);
		Assert.Contains("--report -", root.GetProperty("executable").GetProperty("commandLine").GetString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_BenchmarkMissingTargetWritesRuntimeErrorBeforeCreatingServicesOrChildProcess()
	{
		using var temp = new TemporaryDirectory();
		var missingPath = Path.Combine(temp.Path, "missing");
		using var output = new StringWriter();
		using var error = new StringWriter();
		var servicesCreated = false;
		var processRunner = new FakeBenchmarkProcessRunner();
		var parseResult = CommandLineOptions.Parse([CommandLineOptionTokens.Benchmark, missingPath]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(
				output,
				error,
				servicesFactory: _ =>
				{
					servicesCreated = true;
					throw new InvalidOperationException("Services must not be created.");
				},
				benchmarkProcessRunner: processRunner),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.RuntimeError, exitCode);
		Assert.False(servicesCreated);
		Assert.Empty(processRunner.Requests);
		Assert.Equal(string.Empty, output.ToString());
		Assert.Contains("Benchmark target folder was not found", error.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunUtilityOrHeadlessAsync_BenchmarkWithoutOutputWritesJsonToDefaultBenchmarksFolder()
	{
		using var runCountOverride = TemporaryEnvironmentVariable.Set("DEVPROJEX_BENCHMARK_RUNS", "1");
		using var warmupOverride = TemporaryEnvironmentVariable.Set("DEVPROJEX_BENCHMARK_WARMUP", "0");
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var appDataPath = Directory.CreateDirectory(Path.Combine(temp.Path, "Local AppData")).FullName;
		using var output = new StringWriter();
		using var error = new StringWriter();
		var parseResult = CommandLineOptions.Parse([CommandLineOptionTokens.Benchmark, temp.Path]);

		var exitCode = await CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(
			parseResult,
			CreateContext(
				output,
				error,
				servicesFactory: AvaloniaCompositionRoot.CreateDefault,
				benchmarkProcessRunner: new FakeBenchmarkProcessRunner(),
				benchmarkLocalAppDataProvider: () => appDataPath),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Equal(string.Empty, error.ToString());
		var benchmarkDirectory = Path.Combine(appDataPath, "DevProjex", "Benchmarks");
		var reportPath = Assert.Single(Directory.EnumerateFiles(benchmarkDirectory, "benchmark-*.json"));
		Assert.Contains(Path.GetFullPath(reportPath).Replace('\\', '/'), output.ToString(), StringComparison.Ordinal);
		using var document = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken));
		Assert.Equal(Path.GetFullPath(temp.Path).Replace('\\', '/'), document.RootElement.GetProperty("targetPath").GetString());
	}

	[Fact]
	public void ShouldRunBeforeAvalonia_ReturnsTrueOnlyForUtilityOrHeadlessModes()
	{
		Assert.False(CommandLineAutomationRunner.ShouldRunBeforeAvalonia(CommandLineOptions.Parse([])));
		Assert.False(CommandLineAutomationRunner.ShouldRunBeforeAvalonia(CommandLineOptions.Parse([CommandLineOptionTokens.Path, "/tmp/project"])));
		Assert.False(CommandLineAutomationRunner.ShouldRunBeforeAvalonia(CommandLineOptions.Parse([
			CommandLineOptionTokens.Path, "/tmp/project",
			CommandLineOptionTokens.PreviewMode, "tree-content",
			CommandLineOptionTokens.TreeFormat, "md"
		])));
		Assert.False(CommandLineAutomationRunner.ShouldRunBeforeAvalonia(CommandLineOptions.Parse([
			CommandLineOptionTokens.SessionMetrics, "/tmp/project",
			CommandLineOptionTokens.Preview,
			CommandLineOptionTokens.TreeFormat, "md"
		])));
		Assert.True(CommandLineAutomationRunner.ShouldRunBeforeAvalonia(CommandLineOptions.Parse(["--unknown"])));
		Assert.True(CommandLineAutomationRunner.ShouldRunBeforeAvalonia(CommandLineOptions.Parse([CommandLineOptionTokens.Help])));
		Assert.True(CommandLineAutomationRunner.ShouldRunBeforeAvalonia(CommandLineOptions.Parse([CommandLineOptionTokens.Version])));
		Assert.True(CommandLineAutomationRunner.ShouldRunBeforeAvalonia(CommandLineOptions.Parse([CommandLineOptionTokens.NoUi])));
		Assert.True(CommandLineAutomationRunner.ShouldRunBeforeAvalonia(CommandLineOptions.Parse([CommandLineOptionTokens.Export, "tree"])));
		Assert.True(CommandLineAutomationRunner.ShouldRunBeforeAvalonia(CommandLineOptions.Parse([CommandLineOptionTokens.Output, "/tmp/context.txt"])));
		Assert.True(CommandLineAutomationRunner.ShouldRunBeforeAvalonia(CommandLineOptions.Parse([CommandLineOptionTokens.ShortOutput, "/tmp/context.txt"])));
		Assert.True(CommandLineAutomationRunner.ShouldRunBeforeAvalonia(CommandLineOptions.Parse([CommandLineOptionTokens.Format, "ascii"])));
		Assert.True(CommandLineAutomationRunner.ShouldRunBeforeAvalonia(CommandLineOptions.Parse([CommandLineOptionTokens.Format, "json"])));
		Assert.True(CommandLineAutomationRunner.ShouldRunBeforeAvalonia(CommandLineOptions.Parse([CommandLineOptionTokens.Format, "xml"])));
		Assert.True(CommandLineAutomationRunner.ShouldRunBeforeAvalonia(CommandLineOptions.Parse([CommandLineOptionTokens.Format, "md"])));
		Assert.True(CommandLineAutomationRunner.ShouldRunBeforeAvalonia(CommandLineOptions.Parse([CommandLineOptionTokens.Benchmark, "/tmp/project"])));
		Assert.True(CommandLineAutomationRunner.ShouldRunBeforeAvalonia(CommandLineOptions.Parse([
			CommandLineOptionTokens.SessionMetrics,
			"/tmp/project",
			CommandLineOptionTokens.NoUi
		])));
	}

	private static CommandLineAutomationContext CreateContext(
		TextWriter output,
		TextWriter error,
		string version = "test-version",
		Func<CommandLineOptions, AvaloniaAppServices>? servicesFactory = null,
		ICommandLineBenchmarkProcessRunner? benchmarkProcessRunner = null,
		Func<string>? benchmarkLocalAppDataProvider = null) =>
		new(
			Output: output,
			Error: error,
			ServicesFactory: servicesFactory ?? (_ => throw new InvalidOperationException("Services should not be created for this command-line scenario.")),
			HelpContentProvider: new CommandLineHelpContentProvider(),
			VersionProvider: () => version,
			BenchmarkProcessRunner: benchmarkProcessRunner,
			BenchmarkLocalAppDataProvider: benchmarkLocalAppDataProvider);

	private sealed class FakeBenchmarkProcessRunner : ICommandLineBenchmarkProcessRunner
	{
		public List<CommandLineBenchmarkProcessRequest> Requests { get; } = [];

		public Task<CommandLineBenchmarkProcessRun> RunAsync(
			CommandLineBenchmarkProcessRequest request,
			int index,
			bool isWarmup,
			CancellationToken cancellationToken)
		{
			Requests.Add(request);
			const string stdout = """
			{
			  "rootPath": "fake"
			}
			""";
			var run = new CommandLineBenchmarkProcessRun(
				Index: index,
				IsWarmup: isWarmup,
				StartedAt: DateTimeOffset.Now,
				WallMilliseconds: 100 + index,
				CpuMilliseconds: 50 + index,
				PeakWorkingSetBytes: 128 * 1024 * 1024,
				PeakPrivateMemoryBytes: 96 * 1024 * 1024,
				StdoutCharacters: stdout.Length,
				StdoutBytes: Encoding.UTF8.GetByteCount(stdout),
				StderrCharacters: 0,
				ExitCode: CommandLineExitCodes.Success,
				Error: null);
			return Task.FromResult(run);
		}
	}

	private sealed class TemporaryEnvironmentVariable : IDisposable
	{
		private readonly string _name;
		private readonly string? _previousValue;

		private TemporaryEnvironmentVariable(string name, string value)
		{
			_name = name;
			_previousValue = Environment.GetEnvironmentVariable(name);
			Environment.SetEnvironmentVariable(name, value);
		}

		public static TemporaryEnvironmentVariable Set(string name, string value) => new(name, value);

		public void Dispose() => Environment.SetEnvironmentVariable(_name, _previousValue);
	}
}
