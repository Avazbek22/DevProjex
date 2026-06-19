using DevProjex.Avalonia;

namespace DevProjex.Tests.Integration;

public sealed class CommandLineProcessSmokeIntegrationTests
{
	[Fact]
	public async Task Process_HelpPrintsHelpAndExitsZero()
	{
		var result = await RunAppAsync(CommandLineOptionTokens.Help);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Contains("Usage:", result.Stdout, StringComparison.Ordinal);
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
		Assert.Single(result.Stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
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
		Assert.Equal($"{expectedReportPath}{Environment.NewLine}", result.Stdout);
		Assert.Equal(string.Empty, result.Stderr);
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
	public async Task Process_NoUiInvalidCombinationWritesStderrAndUsageExitCode()
	{
		var result = await RunAppAsync(CommandLineOptionTokens.NoUi, CommandLineOptionTokens.Report);

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Equal(string.Empty, result.Stdout);
		Assert.Contains("--no-ui requires --path", result.Stderr, StringComparison.Ordinal);
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

	private sealed record CommandLineProcessResult(int ExitCode, string Stdout, string Stderr);
}
