using DevProjex.Avalonia;
using DevProjex.Avalonia.ViewModels;
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
		var launcherPath = Path.Combine(temp.Path, CommandLineExecutableAliases.WindowsPortableCommandFileName);
		var appExecutablePath = GetNativeAppHostExecutablePath();
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
	public async Task WindowsPortableLauncher_UserLevelVersionAndFilteredContentExport()
	{
		if (!OperatingSystem.IsWindows())
			return;

		using var temp = new TemporaryDirectory();
		var launcherPath = Path.Combine(temp.Path, CommandLineExecutableAliases.WindowsPortableCommandFileName);
		var appExecutablePath = GetNativeAppHostExecutablePath();
		await File.WriteAllTextAsync(
			launcherPath,
			TerminalCommandSetupService.BuildWindowsLauncherContent(appExecutablePath),
			TestContext.Current.CancellationToken);
		SeedUserLevelProject(temp);

		var versionResult = await RunWindowsCommandAsync(launcherPath, CommandLineOptionTokens.Version);
		Assert.Equal(CommandLineExitCodes.Success, versionResult.ExitCode);
		Assert.Equal($"{MainWindowViewModel.TitleVersion}{Environment.NewLine}", versionResult.Stdout);
		Assert.Equal(string.Empty, versionResult.Stderr);

		var exportResult = await RunWindowsCommandAsync(
			launcherPath,
			temp.Path,
			CommandLineOptionTokens.Export, "content",
			CommandLineOptionTokens.Roots, "src",
			CommandLineOptionTokens.Extensions, "cs",
			CommandLineOptionTokens.ShortOutput, CommandLineOptionTokens.StandardOutputReportPath);

		Assert.Equal(CommandLineExitCodes.Success, exportResult.ExitCode);
		Assert.Equal(string.Empty, exportResult.Stderr);
		Assert.Contains("public static class App", exportResult.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("readme text", exportResult.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("LICENSE", exportResult.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("bin placeholder", exportResult.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("generated", exportResult.Stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task UnixPortableWrapper_UserLevelTreeContentExportUsesCurrentExecutableAndShellSemantics()
	{
		if (OperatingSystem.IsWindows())
			return;

		using var temp = new TemporaryDirectory();
		SeedUserLevelProject(temp);
		var appExecutablePath = GetNativeAppHostExecutablePath();
		var wrapperPath = Path.Combine(temp.Path, CommandLineExecutableAliases.UnixCommand);
		await File.WriteAllTextAsync(
			wrapperPath,
			TerminalCommandSetupService.BuildWrapperContent(appExecutablePath),
			new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
			TestContext.Current.CancellationToken);
		File.SetUnixFileMode(
			wrapperPath,
			UnixFileMode.UserRead |
			UnixFileMode.UserWrite |
			UnixFileMode.UserExecute |
			UnixFileMode.GroupRead |
			UnixFileMode.GroupExecute |
			UnixFileMode.OtherRead |
			UnixFileMode.OtherExecute);
		var relativeOutputPath = Path.Combine("exports", "context.txt");
		var expectedOutputPath = Path.GetFullPath(Path.Combine(temp.Path, relativeOutputPath));

		var result = await RunExecutableWithWorkingDirectoryAsync(
			wrapperPath,
			temp.Path,
			temp.Path,
			CommandLineOptionTokens.Export, "tree-content",
			CommandLineOptionTokens.Roots, "src",
			CommandLineOptionTokens.Extensions, "cs",
			CommandLineOptionTokens.ShortOutput, relativeOutputPath);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		var printedOutputPath = AssertSingleOutputLine(result.Stdout);
		AssertRelativeOutputPathResolvedFromWorkingDirectory(
			printedOutputPath,
			expectedOutputPath,
			temp.Path,
			relativeOutputPath);
		var payload = await File.ReadAllTextAsync(printedOutputPath, TestContext.Current.CancellationToken);
		Assert.Contains("App.cs", payload, StringComparison.Ordinal);
		Assert.Contains("public static class App", payload, StringComparison.Ordinal);
		Assert.DoesNotContain("Readme.txt", payload, StringComparison.Ordinal);
		Assert.DoesNotContain("LICENSE", payload, StringComparison.Ordinal);
		Assert.DoesNotContain("bin placeholder", payload, StringComparison.Ordinal);
		Assert.DoesNotContain("generated", payload, StringComparison.Ordinal);
	}

	[Fact]
	public async Task UnixPathCommand_UserLevelResolvesWrapperFromPathAndRunsAutomation()
	{
		if (OperatingSystem.IsWindows())
			return;

		using var temp = new TemporaryDirectory();
		var binDirectory = temp.CreateDirectory("bin");
		var projectPath = temp.CreateDirectory("project with spaces");
		SeedComplexUserProject(projectPath);
		var wrapperPath = Path.Combine(binDirectory, CommandLineExecutableAliases.UnixCommand);
		await CreateUnixWrapperAsync(wrapperPath);
		var relativeOutputPath = Path.Combine("exports", "path-command-context.txt");
		var expectedOutputPath = Path.GetFullPath(Path.Combine(temp.Path, relativeOutputPath));

		var helpResult = await RunPathCommandAsync(
			CommandLineExecutableAliases.UnixCommand,
			binDirectory,
			temp.Path,
			CommandLineOptionTokens.Help);
		Assert.Equal(CommandLineExitCodes.Success, helpResult.ExitCode);
		Assert.Contains("Usage:", helpResult.Stdout, StringComparison.Ordinal);
		Assert.Equal(string.Empty, helpResult.Stderr);

		var versionResult = await RunPathCommandAsync(
			CommandLineExecutableAliases.UnixCommand,
			binDirectory,
			temp.Path,
			CommandLineOptionTokens.Version);
		Assert.Equal(CommandLineExitCodes.Success, versionResult.ExitCode);
		Assert.Equal($"{MainWindowViewModel.TitleVersion}{Environment.NewLine}", versionResult.Stdout);
		Assert.Equal(string.Empty, versionResult.Stderr);

		var exportResult = await RunPathCommandAsync(
			CommandLineExecutableAliases.UnixCommand,
			binDirectory,
			temp.Path,
			projectPath,
			CommandLineOptionTokens.Export, "tree-content",
			CommandLineOptionTokens.ShortOutput, relativeOutputPath,
			CommandLineOptionTokens.Roots, "src app",
			CommandLineOptionTokens.Extensions, "cs");

		Assert.Equal(CommandLineExitCodes.Success, exportResult.ExitCode);
		Assert.Equal(string.Empty, exportResult.Stderr);
		var printedOutputPath = AssertSingleOutputLine(exportResult.Stdout);
		AssertRelativeOutputPathResolvedFromWorkingDirectory(
			printedOutputPath,
			expectedOutputPath,
			projectPath,
			relativeOutputPath);
		var payload = await File.ReadAllTextAsync(printedOutputPath, TestContext.Current.CancellationToken);
		Assert.Contains("Program.cs", payload, StringComparison.Ordinal);
		Assert.Contains("public sealed class Program", payload, StringComparison.Ordinal);
		Assert.DoesNotContain("Generated.g.cs", payload, StringComparison.Ordinal);
		Assert.DoesNotContain("LICENSE", payload, StringComparison.Ordinal);
	}

	[Fact]
	public async Task UnixPortableWrapper_TargetPathWithApostropheRunsRealAppHost()
	{
		if (OperatingSystem.IsWindows())
			return;

		using var temp = new TemporaryDirectory();
		var appDirectory = temp.CreateDirectory("DevProjex's copied build");
		CopyAppBuildOutputToDirectory(appDirectory);
		var copiedAppHostPath = GetNativeAppHostExecutablePath(appDirectory);
		MakeExecutableIfUnix(copiedAppHostPath);
		var wrapperPath = Path.Combine(temp.Path, CommandLineExecutableAliases.UnixCommand);
		await CreateUnixWrapperAsync(wrapperPath, copiedAppHostPath);

		var result = await RunExecutableAsync(wrapperPath, CommandLineOptionTokens.Version);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal($"{MainWindowViewModel.TitleVersion}{Environment.NewLine}", result.Stdout);
		Assert.Equal(string.Empty, result.Stderr);
	}

	[Fact]
	public async Task WindowsExecutable_HelpPrintsHelpToRedirectedStdout()
	{
		if (!OperatingSystem.IsWindows())
			return;

		var appExecutablePath = GetNativeAppHostExecutablePath();

		var result = await RunExecutableAsync(appExecutablePath, CommandLineOptionTokens.Help);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Contains("Usage:", result.Stdout, StringComparison.Ordinal);
		Assert.Contains(CommandLineOptionTokens.Help, result.Stdout, StringComparison.Ordinal);
		Assert.Equal(string.Empty, result.Stderr);
	}

	[Fact]
	public async Task WindowsPathCommand_UserLevelResolvesLauncherFromPathAndHandlesSpecialCharacters()
	{
		if (!OperatingSystem.IsWindows())
			return;

		using var temp = new TemporaryDirectory();
		var binDirectory = temp.CreateDirectory("bin & tools");
		var projectPath = temp.CreateDirectory("project & source");
		SeedComplexUserProject(projectPath);
		var launcherPath = Path.Combine(binDirectory, CommandLineExecutableAliases.WindowsPortableCommandFileName);
		await CreateWindowsLauncherAsync(launcherPath);
		var relativeOutputPath = Path.Combine("exports & logs", "context & output.txt");
		var expectedOutputPath = Path.GetFullPath(Path.Combine(temp.Path, relativeOutputPath));

		var versionResult = await RunWindowsPathCommandAsync(
			"devprojex",
			binDirectory,
			temp.Path,
			CommandLineOptionTokens.Version);
		Assert.Equal(CommandLineExitCodes.Success, versionResult.ExitCode);
		Assert.Equal($"{MainWindowViewModel.TitleVersion}{Environment.NewLine}", versionResult.Stdout);
		Assert.Equal(string.Empty, versionResult.Stderr);

		var exportResult = await RunWindowsPathCommandAsync(
			"devprojex",
			binDirectory,
			temp.Path,
			projectPath,
			CommandLineOptionTokens.Export, "tree-content",
			CommandLineOptionTokens.ShortOutput, relativeOutputPath,
			CommandLineOptionTokens.Roots, "src app",
			CommandLineOptionTokens.Extensions, "cs");

		Assert.Equal(CommandLineExitCodes.Success, exportResult.ExitCode);
		Assert.Equal(string.Empty, exportResult.Stderr);
		var printedOutputPath = AssertSingleOutputLine(exportResult.Stdout);
		AssertRelativeOutputPathResolvedFromWorkingDirectory(
			printedOutputPath,
			expectedOutputPath,
			projectPath,
			relativeOutputPath);
		var payload = await File.ReadAllTextAsync(printedOutputPath, TestContext.Current.CancellationToken);
		Assert.Contains("Program.cs", payload, StringComparison.Ordinal);
		Assert.Contains("public sealed class Program", payload, StringComparison.Ordinal);
		Assert.DoesNotContain("Generated.g.cs", payload, StringComparison.Ordinal);
		Assert.DoesNotContain("LICENSE", payload, StringComparison.Ordinal);
	}

	[Fact]
	public async Task WindowsPortableLauncher_TargetPathWithBatchMetacharactersRunsRealAppHost()
	{
		if (!OperatingSystem.IsWindows())
			return;

		using var temp = new TemporaryDirectory();
		var appDirectory = temp.CreateDirectory("DevProjex & copied build");
		CopyAppBuildOutputToDirectory(appDirectory);
		var copiedAppHostPath = GetNativeAppHostExecutablePath(appDirectory);
		var launcherPath = Path.Combine(temp.Path, CommandLineExecutableAliases.WindowsPortableCommandFileName);
		await CreateWindowsLauncherAsync(launcherPath, copiedAppHostPath);

		var result = await RunWindowsCommandAsync(launcherPath, CommandLineOptionTokens.Version);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal($"{MainWindowViewModel.TitleVersion}{Environment.NewLine}", result.Stdout);
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
		Assert.Equal(MainWindowViewModel.TitleVersion, version);
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
	public async Task Process_ExportTreeWithRootAndExtensionFilters_UsesDefaultDynamicIgnores()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		temp.CreateFile(Path.Combine("src", "Readme.txt"), "readme\n");
		temp.CreateFile("LICENSE", "license\n");
		temp.CreateFile(Path.Combine(".dotfolder", "Secret.cs"), "class Secret {}\n");

		var result = await RunAppAsync(
			temp.Path,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Roots, "src",
			CommandLineOptionTokens.Extensions, "cs");

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		Assert.Contains("App.cs", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("Readme.txt", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("LICENSE", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain(".dotfolder", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("Secret.cs", result.Stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_ExportContentWithRootAndExtensionFilters_UsesDefaultDynamicIgnores()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		temp.CreateFile(Path.Combine("src", "Readme.txt"), "readme\n");
		temp.CreateFile("LICENSE", "license\n");

		var result = await RunAppAsync(
			temp.Path,
			CommandLineOptionTokens.Export, "content",
			CommandLineOptionTokens.Roots, "src",
			CommandLineOptionTokens.Extensions, "cs");

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		Assert.Contains("class App", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("Readme.txt", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("readme", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("LICENSE", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("license", result.Stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_ExportContentWithIgnoreNone_AllowsExtensionlessRootFilesByContract()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		temp.CreateFile("LICENSE", "license\n");

		var result = await RunAppAsync(
			temp.Path,
			CommandLineOptionTokens.Export, "content",
			CommandLineOptionTokens.Roots, "src",
			CommandLineOptionTokens.Extensions, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		Assert.Contains("class App", result.Stdout, StringComparison.Ordinal);
		Assert.Contains("LICENSE", result.Stdout, StringComparison.Ordinal);
		Assert.Contains("license", result.Stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_UserLevelDefaultIgnoresHideSmartGitDotEmptyAndExtensionlessNoise()
	{
		using var temp = new TemporaryDirectory();
		var projectPath = temp.CreateDirectory("real project");
		SeedComplexUserProject(projectPath);

		var result = await RunAppAsync(
			projectPath,
			CommandLineOptionTokens.Export, "tree");

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		Assert.Contains("Program.cs", result.Stdout, StringComparison.Ordinal);
		Assert.Contains("Guide.md", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("Generated.g.cs", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("DevProjex.dll", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("cache.tmp", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain(".cache", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("Cached.cs", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("LICENSE", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("Empty.cs", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("empty-folder", result.Stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_UserLevelIgnoreNoneShowsEveryUsuallyIgnoredEntry()
	{
		using var temp = new TemporaryDirectory();
		var projectPath = temp.CreateDirectory("real project");
		SeedComplexUserProject(projectPath);

		var result = await RunAppAsync(
			projectPath,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		Assert.Contains("Program.cs", result.Stdout, StringComparison.Ordinal);
		Assert.Contains("Generated.g.cs", result.Stdout, StringComparison.Ordinal);
		Assert.Contains("DevProjex.dll", result.Stdout, StringComparison.Ordinal);
		Assert.Contains("cache.tmp", result.Stdout, StringComparison.Ordinal);
		Assert.Contains(".cache", result.Stdout, StringComparison.Ordinal);
		Assert.Contains("Cached.cs", result.Stdout, StringComparison.Ordinal);
		Assert.Contains("LICENSE", result.Stdout, StringComparison.Ordinal);
		Assert.Contains("Empty.cs", result.Stdout, StringComparison.Ordinal);
		Assert.Contains("empty-folder", result.Stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_UserLevelTreeContentExportIsReadOnlyAndResolvesRelativeOutputFromWorkingDirectory()
	{
		using var temp = new TemporaryDirectory();
		var projectPath = temp.CreateDirectory("real project");
		SeedComplexUserProject(projectPath);
		var before = CaptureProjectFiles(projectPath);
		var relativeOutputPath = Path.Combine("exports", "context.txt");
		var expectedOutputPath = Path.GetFullPath(Path.Combine(temp.Path, relativeOutputPath));

		var result = await RunAppWithWorkingDirectoryAsync(
			temp.Path,
			projectPath,
			CommandLineOptionTokens.Export, "tree-content",
			CommandLineOptionTokens.ShortOutput, relativeOutputPath,
			CommandLineOptionTokens.Roots, "src app",
			CommandLineOptionTokens.Extensions, "cs");

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		var printedOutputPath = AssertSingleOutputLine(result.Stdout);
		AssertRelativeOutputPathResolvedFromWorkingDirectory(
			printedOutputPath,
			expectedOutputPath,
			projectPath,
			relativeOutputPath);
		var payload = await File.ReadAllTextAsync(printedOutputPath, TestContext.Current.CancellationToken);
		Assert.Contains("Program.cs", payload, StringComparison.Ordinal);
		Assert.Contains("public sealed class Program", payload, StringComparison.Ordinal);
		Assert.DoesNotContain("Readme.txt", payload, StringComparison.Ordinal);
		Assert.DoesNotContain("Generated.g.cs", payload, StringComparison.Ordinal);
		Assert.DoesNotContain("LICENSE", payload, StringComparison.Ordinal);
		AssertProjectFilesUnchanged(projectPath, before);
	}

	[Fact]
	public async Task NativeExecutable_UserLevelInvalidCommandReturnsUsageErrorWithoutArtifacts()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var outputPath = Path.Combine(temp.Path, "context.txt");
		var appExecutablePath = GetNativeAppHostExecutablePath();

		var result = await RunExecutableAsync(
			appExecutablePath,
			temp.Path,
			CommandLineOptionTokens.Export, "zip",
			CommandLineOptionTokens.ShortOutput, outputPath);

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Equal(string.Empty, result.Stdout);
		Assert.Contains("Unsupported export mode 'zip'.", result.Stderr, StringComparison.Ordinal);
		Assert.False(File.Exists(outputPath));
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

	private static void SeedUserLevelProject(TemporaryDirectory temp)
	{
		SeedUserLevelProject(temp.Path);
	}

	private static void SeedUserLevelProject(string projectPath)
	{
		WriteProjectFile(projectPath, ".gitignore", "[Bb]in/\n[Oo]bj/\n*.g.cs\n");
		WriteProjectFile(projectPath, Path.Combine("src", "App.cs"), "namespace Smoke;\npublic static class App { }\n");
		WriteProjectFile(projectPath, Path.Combine("src", "Readme.txt"), "readme text\n");
		WriteProjectFile(projectPath, Path.Combine("docs", "Guide.md"), "# Guide\n");
		WriteProjectFile(projectPath, Path.Combine("bin", "Debug", "DevProjex.dll"), "bin placeholder\n");
		WriteProjectFile(projectPath, Path.Combine("obj", "cache.tmp"), "obj placeholder\n");
		WriteProjectFile(projectPath, Path.Combine("generated", "Generated.g.cs"), "// generated\n");
		WriteProjectFile(projectPath, "LICENSE", "license text\n");
	}

	private static void SeedComplexUserProject(string projectPath)
	{
		WriteProjectFile(projectPath, ".gitignore", "[Bb]in/\n[Oo]bj/\n*.g.cs\n");
		WriteProjectFile(projectPath, Path.Combine("src app", "Program.cs"), "namespace Smoke;\npublic sealed class Program { }\n");
		WriteProjectFile(projectPath, Path.Combine("src app", "Readme.txt"), "readme text\n");
		WriteProjectFile(projectPath, Path.Combine("src app", "Generated.g.cs"), "// generated\n");
		WriteProjectFile(projectPath, Path.Combine("src app", "Empty.cs"), string.Empty);
		Directory.CreateDirectory(Path.Combine(projectPath, "src app", "empty-folder"));
		WriteProjectFile(projectPath, Path.Combine("docs", "Guide.md"), "# Guide\n");
		WriteProjectFile(projectPath, Path.Combine("bin", "Debug", "DevProjex.dll"), "bin placeholder\n");
		WriteProjectFile(projectPath, Path.Combine("obj", "cache.tmp"), "obj placeholder\n");
		WriteProjectFile(projectPath, Path.Combine(".cache", "Cached.cs"), "class Cached { }\n");
		WriteProjectFile(projectPath, "LICENSE", "license text\n");
	}

	private static void WriteProjectFile(string rootPath, string relativePath, string content)
	{
		var path = Path.Combine(rootPath, relativePath);
		var directory = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(directory))
			Directory.CreateDirectory(directory);

		File.WriteAllText(path, content);
	}

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

	private static string GetNativeAppHostExecutablePath()
		=> GetNativeAppHostExecutablePath(Path.GetDirectoryName(typeof(App).Assembly.Location)!);

	private static string GetNativeAppHostExecutablePath(string appDirectory)
	{
		var appHostPath = OperatingSystem.IsWindows()
			? Path.Combine(appDirectory, CommandLineExecutableAliases.WindowsPortableExecutable)
			: Path.Combine(appDirectory, CommandLineExecutableAliases.DisplayName);

		Assert.True(File.Exists(appHostPath), $"Expected apphost executable at {appHostPath}.");
		return appHostPath;
	}

	private static async Task CreateWindowsLauncherAsync(string launcherPath, string? appExecutablePath = null)
	{
		await File.WriteAllTextAsync(
			launcherPath,
			TerminalCommandSetupService.BuildWindowsLauncherContent(appExecutablePath ?? GetNativeAppHostExecutablePath()),
			TestContext.Current.CancellationToken);
	}

	private static async Task CreateUnixWrapperAsync(string wrapperPath, string? appExecutablePath = null)
	{
		var content = TerminalCommandSetupService
			.BuildWrapperContent(appExecutablePath ?? GetNativeAppHostExecutablePath())
			.Replace("\r\n", "\n", StringComparison.Ordinal);

		await File.WriteAllTextAsync(
			wrapperPath,
			content,
			new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
			TestContext.Current.CancellationToken);
		MakeExecutableIfUnix(wrapperPath);
	}

	private static void MakeExecutableIfUnix(string path)
	{
		if (OperatingSystem.IsWindows())
			return;

		File.SetUnixFileMode(
			path,
			UnixFileMode.UserRead |
			UnixFileMode.UserWrite |
			UnixFileMode.UserExecute |
			UnixFileMode.GroupRead |
			UnixFileMode.GroupExecute |
			UnixFileMode.OtherRead |
			UnixFileMode.OtherExecute);
	}

	private static void CopyAppBuildOutputToDirectory(string destinationDirectory)
	{
		var sourceDirectory = Path.GetDirectoryName(typeof(App).Assembly.Location)
			?? throw new InvalidOperationException("Could not resolve DevProjex app output directory.");

		CopyDirectory(sourceDirectory, destinationDirectory);
	}

	private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
	{
		Directory.CreateDirectory(destinationDirectory);
		foreach (var sourceFilePath in Directory.EnumerateFiles(sourceDirectory))
		{
			var destinationFilePath = Path.Combine(destinationDirectory, Path.GetFileName(sourceFilePath));
			File.Copy(sourceFilePath, destinationFilePath, overwrite: true);
		}

		foreach (var sourceChildDirectory in Directory.EnumerateDirectories(sourceDirectory))
		{
			var destinationChildDirectory = Path.Combine(destinationDirectory, Path.GetFileName(sourceChildDirectory));
			CopyDirectory(sourceChildDirectory, destinationChildDirectory);
		}
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

	private static async Task<CommandLineProcessResult> RunWindowsPathCommandAsync(
		string commandName,
		string binDirectory,
		string? workingDirectory,
		params string[] args)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = "cmd.exe",
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};

		if (!string.IsNullOrWhiteSpace(workingDirectory))
			startInfo.WorkingDirectory = workingDirectory;

		startInfo.Environment["PATH"] = PrependPath(binDirectory, startInfo.Environment["PATH"]);
		startInfo.ArgumentList.Add("/d");
		startInfo.ArgumentList.Add("/c");
		startInfo.ArgumentList.Add(commandName);
		foreach (var arg in args)
			startInfo.ArgumentList.Add(arg);

		return await RunProcessAsync(startInfo);
	}

	private static async Task<CommandLineProcessResult> RunPathCommandAsync(
		string commandName,
		string binDirectory,
		string? workingDirectory,
		params string[] args)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = File.Exists("/usr/bin/env") ? "/usr/bin/env" : "env",
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};

		if (!string.IsNullOrWhiteSpace(workingDirectory))
			startInfo.WorkingDirectory = workingDirectory;

		startInfo.Environment["PATH"] = PrependPath(binDirectory, startInfo.Environment["PATH"]);
		startInfo.ArgumentList.Add(commandName);
		foreach (var arg in args)
			startInfo.ArgumentList.Add(arg);

		return await RunProcessAsync(startInfo);
	}

	private static string PrependPath(string directory, string? currentPath)
	{
		if (string.IsNullOrEmpty(currentPath))
			return directory;

		return directory + Path.PathSeparator + currentPath;
	}

	private static async Task<CommandLineProcessResult> RunExecutableAsync(string executablePath, params string[] args)
		=> await RunExecutableWithWorkingDirectoryAsync(executablePath, workingDirectory: null, args);

	private static async Task<CommandLineProcessResult> RunExecutableWithWorkingDirectoryAsync(
		string executablePath,
		string? workingDirectory,
		params string[] args)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = executablePath,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};

		if (!string.IsNullOrWhiteSpace(workingDirectory))
			startInfo.WorkingDirectory = workingDirectory;

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

	private static ProjectFileSnapshot[] CaptureProjectFiles(string projectPath) =>
		Directory.EnumerateFiles(projectPath, "*", SearchOption.AllDirectories)
			.Select(path => new ProjectFileSnapshot(
				Path.GetRelativePath(projectPath, path),
				File.ReadAllText(path)))
			.OrderBy(static item => item.RelativePath, StringComparer.Ordinal)
			.ToArray();

	private static void AssertProjectFilesUnchanged(string projectPath, ProjectFileSnapshot[] expected)
	{
		var actual = CaptureProjectFiles(projectPath);
		Assert.Equal(expected.Select(static item => item.RelativePath), actual.Select(static item => item.RelativePath));

		foreach (var expectedFile in expected)
		{
			var actualFile = Assert.Single(actual, item => item.RelativePath == expectedFile.RelativePath);
			Assert.Equal(expectedFile.Content, actualFile.Content);
		}
	}

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

	private sealed record ProjectFileSnapshot(string RelativePath, string Content);
}
