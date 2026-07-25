using DevProjex.Avalonia;
using DevProjex.Avalonia.ViewModels;
using DevProjex.Infrastructure.TerminalCommands;
using System.Xml.Linq;

namespace DevProjex.Tests.Integration;

[Trait("Category", "TerminalCommand")]
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
	public async Task UserLevelTerminalCommand_NoUiReportDashWritesOnlyJsonToStdout()
	{
		using var temp = new TemporaryDirectory();
		var projectPath = temp.CreateDirectory("project with spaces");
		SeedUserLevelProject(projectPath);

		var result = await RunUserLevelTerminalCommandAsync(
			temp,
			temp.Path,
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Path, projectPath,
			CommandLineOptionTokens.Report, CommandLineOptionTokens.StandardOutputReportPath);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		using var document = JsonDocument.Parse(result.Stdout);
		var root = document.RootElement;
		Assert.Equal(ProjectAnalysisReport.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
		Assert.Equal(
			GetComparablePath(projectPath),
			GetComparablePath(root.GetProperty("rootPath").GetString()!));
		Assert.Contains(".cs", ReadStringArray(root.GetProperty("inventory").GetProperty("availableExtensions")));
		Assert.DoesNotContain("Usage:", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("DevProjex:", result.Stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task UserLevelTerminalCommand_NoUiReportDoesNotAdvertiseIgnoredNumericExtensions()
	{
		using var temp = new TemporaryDirectory();
		var projectPath = temp.CreateDirectory("numeric extension project");
		temp.CreateFile(Path.Combine("numeric extension project", "App.csproj"), "<Project />\n");
		temp.CreateFile(Path.Combine("numeric extension project", "src", "App.cs"), "class App {}\n");
		temp.CreateFile(Path.Combine("numeric extension project", "empty.1770912967589"), string.Empty);
		temp.CreateFile(Path.Combine("numeric extension project", "src", ".transient.1770912967590"), "dot payload\n");
		temp.CreateFile(Path.Combine("numeric extension project", "src", "archive.1770912967591"), "visible payload\n");

		var result = await RunUserLevelTerminalCommandAsync(
			temp,
			temp.Path,
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Path, projectPath,
			CommandLineOptionTokens.Report, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreEmptyFiles,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreDotFiles);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		using var document = JsonDocument.Parse(result.Stdout);
		var root = document.RootElement;
		var availableExtensions = ReadStringArray(root.GetProperty("inventory").GetProperty("availableExtensions"));
		var selectedExtensions = ReadStringArray(root.GetProperty("selection").GetProperty("selectedExtensions"));

		Assert.Contains(".1770912967591", availableExtensions);
		Assert.DoesNotContain(".1770912967589", availableExtensions);
		Assert.DoesNotContain(".1770912967590", availableExtensions);
		Assert.DoesNotContain(".1770912967589", selectedExtensions);
		Assert.DoesNotContain(".1770912967590", selectedExtensions);
		Assert.Equal(3, root.GetProperty("inventory").GetProperty("tree").GetProperty("fileCount").GetInt32());
	}

	[Fact]
	public async Task UserLevelTerminalCommand_TreeContentJsonStdoutKeepsJsonTreeAndPlainTextContent()
	{
		using var temp = new TemporaryDirectory();
		var projectPath = temp.CreateDirectory("project with spaces");
		SeedUserLevelProject(projectPath);
		WriteProjectFile(projectPath, Path.Combine("src", "Пример.cs"), "namespace Smoke;\npublic sealed class Пример { }\n");

		var result = await RunUserLevelTerminalCommandAsync(
			temp,
			temp.Path,
			CommandLineOptionTokens.Path, projectPath,
			CommandLineOptionTokens.Export, "tree-content",
			CommandLineOptionTokens.ExportFormat, "json",
			CommandLineOptionTokens.Output, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.IncludeRoot, "src",
			CommandLineOptionTokens.IncludeExtension, "cs");

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		var (jsonPart, contentPart) = SplitTreeContentJsonStdout(result.Stdout);
		Assert.Contains("\"Пример.cs\"", jsonPart, StringComparison.Ordinal);
		Assert.DoesNotContain("\\u04", jsonPart, StringComparison.OrdinalIgnoreCase);
		using var document = JsonDocument.Parse(jsonPart);
		var root = document.RootElement;
		Assert.Equal(GetComparablePath(projectPath), GetComparablePath(root.GetProperty("rootPath").GetString()!));
		JsonTreeExportTestHelper.AssertOnlyRootPathAndTree(root);
		JsonTreeExportTestHelper.AssertNoLegacyTreeContract(root);
		var tree = JsonTreeExportTestHelper.GetTree(document);
		JsonTreeExportTestHelper.AssertRelativePathsUseForwardSlashes(tree);
		Assert.Equal(JsonValueKind.Array, tree.GetProperty("src").ValueKind);
		Assert.Equal(["src/App.cs", "src/Пример.cs"], JsonTreeExportTestHelper.ExtractFilePaths(tree));
		Assert.Contains("App.cs:", contentPart, StringComparison.Ordinal);
		Assert.Contains("public static class App", contentPart, StringComparison.Ordinal);
		Assert.Contains("Пример.cs:", contentPart, StringComparison.Ordinal);
		Assert.Contains("public sealed class Пример", contentPart, StringComparison.Ordinal);
		Assert.DoesNotContain("Readme.txt", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("LICENSE", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("bin placeholder", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("generated", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("Usage:", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("DevProjex:", result.Stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task UnixPortableWrapper_UserLevelTreeContentExportUsesCurrentExecutableAndShellSemantics()
	{
		if (OperatingSystem.IsWindows())
			return;

		using var temp = new TemporaryDirectory();
		var projectPath = temp.CreateDirectory("project with spaces");
		SeedUserLevelProject(projectPath);
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
			projectPath,
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
			projectPath,
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
	public async Task UnixShellPathCommand_UserLevelPreservesUnicodeArgumentsAndLeavesProjectReadOnly()
	{
		if (OperatingSystem.IsWindows())
			return;

		using var temp = new TemporaryDirectory();
		var binDirectory = temp.CreateDirectory("bin");
		var projectPath = temp.CreateDirectory("project with apostrophe ' and unicode ё");
		SeedComplexUserProject(projectPath);
		var projectFilesBefore = CaptureProjectFiles(projectPath);
		await CreateUnixWrapperAsync(Path.Combine(binDirectory, CommandLineExecutableAliases.UnixCommand));
		var relativeOutputPath = Path.Combine("exports", "контекст ё.txt");
		var expectedOutputPath = Path.GetFullPath(Path.Combine(temp.Path, relativeOutputPath));

		var result = await RunPosixShellPathCommandAsync(
			CommandLineExecutableAliases.UnixCommand,
			binDirectory,
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
		AssertProjectFilesUnchanged(projectPath, projectFilesBefore);
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
	public async Task WindowsPowerShellPathCommand_UserLevelPreservesUnicodeArgumentsAndLeavesProjectReadOnly()
	{
		if (!OperatingSystem.IsWindows())
			return;

		using var temp = new TemporaryDirectory();
		var binDirectory = temp.CreateDirectory("bin & tools");
		var projectPath = temp.CreateDirectory("project & unicode ё");
		SeedComplexUserProject(projectPath);
		var projectFilesBefore = CaptureProjectFiles(projectPath);
		await CreateWindowsLauncherAsync(
			Path.Combine(binDirectory, CommandLineExecutableAliases.WindowsPortableCommandFileName));
		var relativeOutputPath = Path.Combine("exports & logs", "контекст ё.txt");
		var expectedOutputPath = Path.GetFullPath(Path.Combine(temp.Path, relativeOutputPath));

		var result = await RunWindowsPowerShellPathCommandAsync(
			"devprojex",
			binDirectory,
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
		AssertProjectFilesUnchanged(projectPath, projectFilesBefore);
	}

	[Fact]
	public async Task WindowsPowerShellPathCommand_UserLevelWithoutArgumentsLaunchesTheConfiguredUiApp()
	{
		if (!OperatingSystem.IsWindows())
			return;

		using var temp = new TemporaryDirectory();
		var binDirectory = temp.CreateDirectory("bin");
		var copiedAppDirectory = temp.CreateDirectory("configured app");
		CopyAppBuildOutputToDirectory(copiedAppDirectory);
		var copiedAppHostPath = GetNativeAppHostExecutablePath(copiedAppDirectory);
		await CreateWindowsLauncherAsync(
			Path.Combine(binDirectory, CommandLineExecutableAliases.WindowsPortableCommandFileName),
			copiedAppHostPath);

		using var launcherProcess = StartWindowsPowerShellPathCommand(
			"devprojex",
			binDirectory,
			temp.Path);

		Process? startedApp = null;
		try
		{
			startedApp = await WaitForProcessStartedFromPathAsync(copiedAppHostPath);
			await launcherProcess
				.WaitForExitAsync(TestContext.Current.CancellationToken)
				.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

			Assert.Equal(CommandLineExitCodes.Success, launcherProcess.ExitCode);
			Assert.False(startedApp.HasExited);
		}
		finally
		{
			if (startedApp is not null)
			{
				TryKill(startedApp);
				await startedApp
					.WaitForExitAsync(TestContext.Current.CancellationToken)
					.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
				startedApp.Dispose();
			}
		}
	}

	[Fact]
	public async Task WindowsPortableLauncher_TargetPathWithBatchMetacharactersRunsRealAppHost()
	{
		if (!OperatingSystem.IsWindows())
			return;

		using var temp = new TemporaryDirectory();
		var appDirectory = temp.CreateDirectory("DevProjex & !missing! copied build");
		CopyAppBuildOutputToDirectory(appDirectory);
		var copiedAppHostPath = GetNativeAppHostExecutablePath(appDirectory);
		var launcherPath = Path.Combine(temp.Path, CommandLineExecutableAliases.WindowsPortableCommandFileName);
		await CreateWindowsLauncherAsync(launcherPath, copiedAppHostPath);

		var result = await RunWindowsCommandWithDelayedExpansionAsync(launcherPath, CommandLineOptionTokens.Version);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal($"{MainWindowViewModel.TitleVersion}{Environment.NewLine}", result.Stdout);
		Assert.Equal(string.Empty, result.Stderr);
	}

	[Fact]
	public async Task WindowsPortableLauncher_SingleFileFallbackPreservesStdoutAndExitCode()
	{
		if (!OperatingSystem.IsWindows())
			return;

		using var temp = new TemporaryDirectory();
		var targetPath = Path.Combine(temp.Path, "fake single file target.cmd");
		await File.WriteAllLinesAsync(
			targetPath,
			[
				"@echo off",
				"if \"%~1\"==\"--version\" (",
				"  echo 9.9-test",
				"  exit /b 0",
				")",
				"echo unexpected argument",
				"exit /b 7"
			],
			TestContext.Current.CancellationToken);
		var launcherPath = Path.Combine(temp.Path, CommandLineExecutableAliases.WindowsPortableCommandFileName);
		await CreateWindowsLauncherAsync(launcherPath, targetPath);

		var result = await RunWindowsCommandAsync(launcherPath, CommandLineOptionTokens.Version);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal($"9.9-test{Environment.NewLine}", result.Stdout);
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
		Assert.Contains(CommandLineOptionTokens.Last, result.Stdout, StringComparison.Ordinal);
		Assert.Contains(CommandLineOptionTokens.Preview, result.Stdout, StringComparison.Ordinal);
		Assert.Contains(CommandLineOptionTokens.PreviewMode, result.Stdout, StringComparison.Ordinal);
		Assert.Contains(CommandLineOptionTokens.TreeFormat, result.Stdout, StringComparison.Ordinal);
		Assert.Contains(CommandLineOptionTokens.TreeFilter, result.Stdout, StringComparison.Ordinal);
		Assert.Contains(CommandLineOptionTokens.PreviewSearch, result.Stdout, StringComparison.Ordinal);
		Assert.Contains(CommandLineOptionTokens.SessionMetrics, result.Stdout, StringComparison.Ordinal);
		Assert.Contains(CommandLineOptionTokens.SessionMetricsOutput, result.Stdout, StringComparison.Ordinal);
		Assert.Contains("ascii|json|xml|md", result.Stdout, StringComparison.Ordinal);
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
	public async Task Process_ExportTreeToStdout_IgnoreOverrideControlsGitIgnoredDotFile()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", ".env\n");
		temp.CreateFile("App.csproj", "<Project />\n");
		temp.CreateFile("Program.cs", "Console.WriteLine(\"ok\");\n");
		temp.CreateFile(".env", "SECRET=1\n");

		var gitIgnoreOnly = await RunAppWithWorkingDirectoryAsync(
			temp.Path,
			".",
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreGitIgnore);
		var allIgnoreOff = await RunAppWithWorkingDirectoryAsync(
			temp.Path,
			".",
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, gitIgnoreOnly.ExitCode);
		Assert.Equal(string.Empty, gitIgnoreOnly.Stderr);
		Assert.Contains("Program.cs", gitIgnoreOnly.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain(".env", gitIgnoreOnly.Stdout, StringComparison.Ordinal);

		Assert.Equal(CommandLineExitCodes.Success, allIgnoreOff.ExitCode);
		Assert.Equal(string.Empty, allIgnoreOff.Stderr);
		Assert.Contains("Program.cs", allIgnoreOff.Stdout, StringComparison.Ordinal);
		Assert.Contains(".env", allIgnoreOff.Stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_GitIgnoreOnly_TreeContentPrunesGitDatabaseAndKeepsOrdinaryDotPaths()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(".gitignore", "tests/\nlogs/\n");
		temp.CreateFile(".git/objects/pack/object.data", "GIT-OBJECT-SENTINEL\n");
		temp.CreateFile(".git/hooks/pre-commit.sample", "GIT-HOOK-SENTINEL\n");
		temp.CreateFile("src/vendor/.git/objects/nested.data", "NESTED-GIT-SENTINEL\n");
		temp.CreateFile(".github/workflows/ci.yml", "GITHUB-WORKFLOW-SENTINEL\n");
		temp.CreateFile(".git-owned/source.txt", "GIT-LOOKALIKE-SENTINEL\n");
		temp.CreateFile("tests/test_app.py", "TEST-SENTINEL\n");
		temp.CreateFile("src/app.py", "APP-SENTINEL\n");

		var gitIgnoreOnly = await RunAppAsync(
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "tree-content",
			CommandLineOptionTokens.Output, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreGitIgnore);
		var noIgnores = await RunAppAsync(
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "tree-content",
			CommandLineOptionTokens.Output, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, gitIgnoreOnly.ExitCode);
		Assert.Equal(string.Empty, gitIgnoreOnly.Stderr);
		Assert.Contains("APP-SENTINEL", gitIgnoreOnly.Stdout, StringComparison.Ordinal);
		Assert.Contains("GITHUB-WORKFLOW-SENTINEL", gitIgnoreOnly.Stdout, StringComparison.Ordinal);
		Assert.Contains("GIT-LOOKALIKE-SENTINEL", gitIgnoreOnly.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("GIT-OBJECT-SENTINEL", gitIgnoreOnly.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("GIT-HOOK-SENTINEL", gitIgnoreOnly.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("NESTED-GIT-SENTINEL", gitIgnoreOnly.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("TEST-SENTINEL", gitIgnoreOnly.Stdout, StringComparison.Ordinal);

		Assert.Equal(CommandLineExitCodes.Success, noIgnores.ExitCode);
		Assert.Equal(string.Empty, noIgnores.Stderr);
		Assert.Contains("GIT-OBJECT-SENTINEL", noIgnores.Stdout, StringComparison.Ordinal);
		Assert.Contains("GIT-HOOK-SENTINEL", noIgnores.Stdout, StringComparison.Ordinal);
		Assert.Contains("NESTED-GIT-SENTINEL", noIgnores.Stdout, StringComparison.Ordinal);
		Assert.Contains("TEST-SENTINEL", noIgnores.Stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_SmartIgnoreOverrideHidesPortableArtifactsAndPreservesSourceContracts()
	{
		using var temp = new TemporaryDirectory();
		SeedPortableSmartIgnoreProject(temp);

		var smartOnly = await RunAppAsync(
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Output, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreSmartIgnore);
		var noIgnores = await RunAppAsync(
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Output, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, smartOnly.ExitCode);
		Assert.Equal(string.Empty, smartOnly.Stderr);
		Assert.Contains("App.cs", smartOnly.Stdout, StringComparison.Ordinal);
		Assert.Contains("packages.config", smartOnly.Stdout, StringComparison.Ordinal);
		Assert.Contains("App.sln.DotSettings", smartOnly.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("repositories.config", smartOnly.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("Alpha.1.0.0", smartOnly.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("Beta.2.0.0", smartOnly.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("DotSettings.user", smartOnly.Stdout, StringComparison.Ordinal);

		Assert.Equal(CommandLineExitCodes.Success, noIgnores.ExitCode);
		Assert.Equal(string.Empty, noIgnores.Stderr);
		Assert.Contains("repositories.config", noIgnores.Stdout, StringComparison.Ordinal);
		Assert.Contains("Alpha.1.0.0.nupkg", noIgnores.Stdout, StringComparison.Ordinal);
		Assert.Contains("Beta.2.0.0.nupkg", noIgnores.Stdout, StringComparison.Ordinal);
		Assert.Contains("App.sln.DotSettings.user", noIgnores.Stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task UserLevelTerminalCommand_SmartIgnoreOverrideMatchesDirectProcessOutput()
	{
		using var temp = new TemporaryDirectory();
		var projectPath = temp.CreateDirectory("portable smart project");
		SeedPortableSmartIgnoreProject(projectPath);

		var result = await RunUserLevelTerminalCommandAsync(
			temp,
			temp.Path,
			projectPath,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Output, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreSmartIgnore);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		Assert.Contains("App.cs", result.Stdout, StringComparison.Ordinal);
		Assert.Contains("packages.config", result.Stdout, StringComparison.Ordinal);
		Assert.Contains("App.sln.DotSettings", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("repositories.config", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain(".nupkg", result.Stdout, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("DotSettings.user", result.Stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_NoUiReportSmartIgnoreExcludesArtifactInventoryAndKeepsSharedFiles()
	{
		using var temp = new TemporaryDirectory();
		SeedPortableSmartIgnoreProject(temp);

		var result = await RunAppAsync(
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Report, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreSmartIgnore);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		using var document = JsonDocument.Parse(result.Stdout);
		var inventory = document.RootElement.GetProperty("inventory");
		var availableExtensions = ReadStringArray(inventory.GetProperty("availableExtensions"));

		Assert.Contains(".cs", availableExtensions);
		Assert.Contains(".config", availableExtensions);
		Assert.Contains(".DotSettings", availableExtensions);
		Assert.DoesNotContain(".nupkg", availableExtensions);
		Assert.DoesNotContain(".dll", availableExtensions);
		Assert.DoesNotContain(".user", availableExtensions);
		Assert.Equal(3, inventory.GetProperty("tree").GetProperty("fileCount").GetInt32());
	}

	[Theory]
	[InlineData("json")]
	[InlineData("xml")]
	[InlineData("md")]
	public async Task Process_StructuredTreeFormatsApplySmartIgnoreBeforeSerialization(string format)
	{
		using var temp = new TemporaryDirectory();
		SeedPortableSmartIgnoreProject(temp);

		var result = await RunAppAsync(
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Format, format,
			CommandLineOptionTokens.Output, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreSmartIgnore);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		Assert.Contains("App.cs", result.Stdout, StringComparison.Ordinal);
		Assert.Contains("packages.config", result.Stdout, StringComparison.Ordinal);
		Assert.Contains("App.sln.DotSettings", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("repositories.config", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain(".nupkg", result.Stdout, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("DotSettings.user", result.Stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_ContentExportSmartIgnoreAndNoneProduceOppositeArtifactPayloads()
	{
		using var temp = new TemporaryDirectory();
		SeedPortableSmartIgnoreProject(temp);

		var smartOnly = await RunAppAsync(
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "content",
			CommandLineOptionTokens.Output, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreSmartIgnore);
		var noIgnores = await RunAppAsync(
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "content",
			CommandLineOptionTokens.Output, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, smartOnly.ExitCode);
		Assert.Equal(string.Empty, smartOnly.Stderr);
		Assert.Contains("SOURCE_SENTINEL", smartOnly.Stdout, StringComparison.Ordinal);
		Assert.Contains("SHARED_SETTINGS_SENTINEL", smartOnly.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("LOCAL_STATE_SENTINEL", smartOnly.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("REPOSITORY_SENTINEL", smartOnly.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("PACKAGE_SENTINEL", smartOnly.Stdout, StringComparison.Ordinal);

		Assert.Equal(CommandLineExitCodes.Success, noIgnores.ExitCode);
		Assert.Equal(string.Empty, noIgnores.Stderr);
		Assert.Contains("SOURCE_SENTINEL", noIgnores.Stdout, StringComparison.Ordinal);
		Assert.Contains("SHARED_SETTINGS_SENTINEL", noIgnores.Stdout, StringComparison.Ordinal);
		Assert.Contains("LOCAL_STATE_SENTINEL", noIgnores.Stdout, StringComparison.Ordinal);
		Assert.Contains("REPOSITORY_SENTINEL", noIgnores.Stdout, StringComparison.Ordinal);
		Assert.Contains("PACKAGE_SENTINEL", noIgnores.Stdout, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("ascii")]
	[InlineData("json")]
	[InlineData("xml")]
	[InlineData("md")]
	public async Task Process_TreeContentAllFormatsApplySmartIgnoreToTreeAndContent(string format)
	{
		using var temp = new TemporaryDirectory();
		SeedPortableSmartIgnoreProject(temp);
		var expectedFiles = new[]
		{
			"App.sln.DotSettings",
			"packages.config",
			"src/App.cs"
		};

		var result = await RunAppAsync(
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "tree-content",
			CommandLineOptionTokens.Format, format,
			CommandLineOptionTokens.Output, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreSmartIgnore);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		var (treePart, contentPart) = SplitTreeContentStdout(format, result.Stdout);
		AssertTreeOnlyStdoutContract(treePart, format, temp.Path, expectedFiles);
		Assert.Contains("SOURCE_SENTINEL", contentPart, StringComparison.Ordinal);
		Assert.Contains("SHARED_SETTINGS_SENTINEL", contentPart, StringComparison.Ordinal);
		Assert.DoesNotContain("LOCAL_STATE_SENTINEL", contentPart, StringComparison.Ordinal);
		Assert.DoesNotContain("REPOSITORY_SENTINEL", contentPart, StringComparison.Ordinal);
		Assert.DoesNotContain("PACKAGE_SENTINEL", contentPart, StringComparison.Ordinal);
		Assert.DoesNotContain("DotSettings.user", treePart, StringComparison.Ordinal);
		Assert.DoesNotContain("repositories.config", treePart, StringComparison.Ordinal);
		Assert.DoesNotContain(".nupkg", treePart, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Process_SmartIgnorePortableStoreMatrixPrunesOnlyConfirmedStores()
	{
		using var temp = new TemporaryDirectory();
		var projectPath = temp.CreateDirectory("portable-store-matrix");
		SeedPortableStoreMatrixProject(projectPath);
		var sourceFiles = new[]
		{
			"source/_cacache/Cache.cs",
			"source/modules-2/Modules.cs",
			"source/packages/Domain.cs",
			"source/registry/Registry.cs",
			"source/repository/Repository.cs",
			"src/App.cs"
		};
		var artifactFiles = new[]
		{
			".cargo/registry/index/config.json",
			".gradle/caches/modules-2/files-2.1/acme.jar",
			".m2/repository/acme/module.pom",
			".npm/_cacache/content-v2/sha",
			".nuget/packages/acme/Acme.nupkg"
		};

		var smartOnly = await RunAppAsync(
			CommandLineOptionTokens.Path, projectPath,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Format, "json",
			CommandLineOptionTokens.Output, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreSmartIgnore);
		var noIgnores = await RunAppAsync(
			CommandLineOptionTokens.Path, projectPath,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Format, "json",
			CommandLineOptionTokens.Output, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, smartOnly.ExitCode);
		Assert.Equal(string.Empty, smartOnly.Stderr);
		AssertTreeOnlyStdoutContract(smartOnly.Stdout, "json", projectPath, sourceFiles);

		Assert.Equal(CommandLineExitCodes.Success, noIgnores.ExitCode);
		Assert.Equal(string.Empty, noIgnores.Stderr);
		AssertTreeOnlyStdoutContract(
			noIgnores.Stdout,
			"json",
			projectPath,
			sourceFiles.Concat(artifactFiles).ToArray());
	}

	[Fact]
	public async Task Process_SmartIgnoreNegativeMatrixPreservesEverySourceLookalikeAndPrunesOnlyProvenArtifacts()
	{
		using var temp = new TemporaryDirectory();
		var projectPath = temp.CreateDirectory("smart-ignore-negative-matrix");
		SeedSmartIgnoreNegativeMatrixProject(projectPath);
		var sourceFiles = new[]
		{
			"App.csproj",
			"build/README.md",
			"build/docs/CMakeCache.txt",
			"cmake-build/CMakeCache.txt",
			"m2-backup/repository/service/package.json",
			"obj-backup/project.assets.json",
			"packages/Alpha/Alpha.nupkg",
			"src/App.cs",
			"vendor/src/autoload.php"
		};
		var provenArtifactFiles = new[]
		{
			"App.csproj.user",
			"obj/project.assets.json"
		};

		var smartOnly = await RunAppAsync(
			CommandLineOptionTokens.Path, projectPath,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Format, "json",
			CommandLineOptionTokens.Output, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreSmartIgnore);
		var noIgnores = await RunAppAsync(
			CommandLineOptionTokens.Path, projectPath,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Format, "json",
			CommandLineOptionTokens.Output, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, smartOnly.ExitCode);
		Assert.Equal(string.Empty, smartOnly.Stderr);
		AssertTreeOnlyStdoutContract(smartOnly.Stdout, "json", projectPath, sourceFiles);

		Assert.Equal(CommandLineExitCodes.Success, noIgnores.ExitCode);
		Assert.Equal(string.Empty, noIgnores.Stderr);
		AssertTreeOnlyStdoutContract(
			noIgnores.Stdout,
			"json",
			projectPath,
			sourceFiles.Concat(provenArtifactFiles).ToArray());
	}

	[Fact]
	public async Task Process_SmartIgnoreXmlFileExportMatchesStdoutTreeContract()
	{
		using var temp = new TemporaryDirectory();
		var projectPath = temp.CreateDirectory("portable smart project");
		SeedPortableSmartIgnoreProject(projectPath);
		var outputPath = Path.Combine(temp.Path, "exports", "smart-tree.xml");
		var expectedFiles = new[]
		{
			"App.sln.DotSettings",
			"packages.config",
			"src/App.cs"
		};

		var stdoutResult = await RunAppAsync(
			CommandLineOptionTokens.Path, projectPath,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Format, "xml",
			CommandLineOptionTokens.Output, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreSmartIgnore);
		var fileResult = await RunAppAsync(
			CommandLineOptionTokens.Path, projectPath,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Format, "xml",
			CommandLineOptionTokens.Output, outputPath,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreSmartIgnore);

		Assert.Equal(CommandLineExitCodes.Success, stdoutResult.ExitCode);
		Assert.Equal(string.Empty, stdoutResult.Stderr);
		AssertTreeOnlyStdoutContract(stdoutResult.Stdout, "xml", projectPath, expectedFiles);

		Assert.Equal(CommandLineExitCodes.Success, fileResult.ExitCode);
		Assert.Equal(string.Empty, fileResult.Stderr);
		Assert.Equal(Path.GetFullPath(outputPath), AssertSingleOutputLine(fileResult.Stdout));
		Assert.True(File.Exists(outputPath));
		var exported = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
		AssertTreeOnlyStdoutContract(exported, "xml", projectPath, expectedFiles);
		Assert.True(XNode.DeepEquals(XDocument.Parse(stdoutResult.Stdout), XDocument.Parse(exported)));
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
		Assert.Equal(Path.GetFullPath(temp.Path).Replace('\\', '/'), document.RootElement.GetProperty("rootPath").GetString());
		var tree = JsonTreeExportTestHelper.GetTree(document);
		Assert.Equal(JsonValueKind.Array, tree.GetProperty("src").ValueKind);
		Assert.Equal(["src/App.cs"], JsonTreeExportTestHelper.ExtractFilePaths(tree));
		Assert.DoesNotContain("class App", result.Stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_ExportJsonTreeForRepoLikeWorkspace_WritesParseableJsonTreeContract()
	{
		using var temp = new TemporaryDirectory();
		var projectPath = temp.CreateDirectory("repo like project");
		SeedRepoLikeJsonProject(projectPath);

		var result = await RunAppAsync(
			CommandLineOptionTokens.Path, projectPath,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Format, "json",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		Assert.Contains("\"файл.txt\"", result.Stdout, StringComparison.Ordinal);
		Assert.Contains("\"Пользователь.cs\"", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("\\u04", result.Stdout, StringComparison.OrdinalIgnoreCase);
		using var document = JsonDocument.Parse(result.Stdout);
		Assert.Equal(Path.GetFullPath(projectPath).Replace('\\', '/'), document.RootElement.GetProperty("rootPath").GetString());
		JsonTreeExportTestHelper.AssertOnlyRootPathAndTree(document.RootElement);
		JsonTreeExportTestHelper.AssertNoLegacyTreeContract(document.RootElement);

		var tree = JsonTreeExportTestHelper.GetTree(document);
		JsonTreeExportTestHelper.AssertRelativePathsUseForwardSlashes(tree);
		var paths = JsonTreeExportTestHelper.ExtractFilePaths(tree);
		var expectedPaths = new[]
		{
			".git/config",
			".git/HEAD",
			".git/refs/heads/main",
			".editorconfig",
			".gitignore",
			"docs/Guide.md",
			"docs/файл.txt",
			"global.json",
			"README.md",
			"scripts/build.ps1",
			"space dir/file with spaces.txt",
			"src/Models/Пользователь.cs",
			"src/Program.cs",
			"src/Services/UserService.cs"
		};
		Assert.Equal(
			expectedPaths.OrderBy(static path => path, StringComparer.Ordinal).ToArray(),
			paths.OrderBy(static path => path, StringComparer.Ordinal).ToArray());

		var rootFiles = tree.GetProperty("/").EnumerateArray().Select(static item => item.GetString()!).ToArray();
		Assert.Contains(".editorconfig", rootFiles);
		Assert.Contains(".gitignore", rootFiles);
		Assert.Contains("global.json", rootFiles);
		Assert.Contains("README.md", rootFiles);
		Assert.Equal(["Models", "Services", "/"], tree.GetProperty("src").EnumerateObject().Select(static property => property.Name).ToArray());
		Assert.Equal(["refs", "/"], tree.GetProperty(".git").EnumerateObject().Select(static property => property.Name).ToArray());
		Assert.Contains("empty-folder", JsonTreeExportTestHelper.ExtractEmptyFolderPaths(tree));
		Assert.DoesNotContain("namespace RepoLike", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("guide content", result.Stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_ExportJsonTreeWithUnicodePathsToFileWritesReadableUtf8AndRoundTrips()
	{
		using var temp = new TemporaryDirectory();
		var projectPath = temp.CreateDirectory("Проект JSON");
		WriteProjectFile(projectPath, Path.Combine("Документы", "Файл [финал].cs"), "namespace Пример;\n");
		WriteProjectFile(projectPath, Path.Combine("Документы", "Сводка.txt"), "сводка\n");
		var outputPath = Path.Combine(temp.Path, "экспорт", "дерево.json");

		var result = await RunAppAsync(
			CommandLineOptionTokens.Path, projectPath,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Format, "json",
			CommandLineOptionTokens.Output, outputPath,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		Assert.Equal(Path.GetFullPath(outputPath), AssertSingleOutputLine(result.Stdout));
		Assert.True(File.Exists(outputPath));

		var json = await File.ReadAllTextAsync(outputPath, Encoding.UTF8, TestContext.Current.CancellationToken);
		Assert.Contains("Проект JSON", json, StringComparison.Ordinal);
		Assert.Contains("\"Документы\"", json, StringComparison.Ordinal);
		Assert.Contains("Файл", json, StringComparison.Ordinal);
		Assert.Contains("Сводка.txt", json, StringComparison.Ordinal);
		Assert.DoesNotContain("\\u04", json, StringComparison.OrdinalIgnoreCase);

		using var document = JsonDocument.Parse(json);
		Assert.Equal(
			Path.GetFullPath(projectPath).Replace('\\', '/'),
			document.RootElement.GetProperty("rootPath").GetString());
		Assert.Equal(
			["Документы/Сводка.txt", "Документы/Файл [финал].cs"],
			JsonTreeExportTestHelper.ExtractFilePaths(JsonTreeExportTestHelper.GetTree(document))
				.OrderBy(static path => path, StringComparer.Ordinal)
				.ToArray());
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
		var tree = JsonTreeExportTestHelper.GetTree(document);
		Assert.Equal(JsonValueKind.Array, tree.GetProperty("src").ValueKind);
		Assert.Equal(["src/App.cs"], JsonTreeExportTestHelper.ExtractFilePaths(tree));
		Assert.DoesNotContain("class App", result.Stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_InlineStructuredExportCommandWritesXmlTreeToStdout()
	{
		using var temp = new TemporaryDirectory();
		var projectPath = temp.CreateDirectory("project inline xml");
		SeedStructuredFormatProject(projectPath);

		var result = await RunAppAsync(
			$"{CommandLineOptionTokens.Path}={projectPath}",
			$"{CommandLineOptionTokens.Export}=tree",
			$"{CommandLineOptionTokens.Format}=xml",
			$"{CommandLineOptionTokens.Roots}=src",
			$"{CommandLineOptionTokens.Extensions}=cs",
			$"{CommandLineOptionTokens.Ignore}={CommandLineOptionTokens.IgnoreNone}");

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		var document = XDocument.Parse(result.Stdout);
		Assert.Equal(Path.GetFullPath(projectPath).Replace('\\', '/'), document.Root?.Attribute("r")?.Value);
		Assert.Equal(["src/Program.cs", "src/Services/UserService.cs"], ExtractXmlFilePaths(document));
		Assert.DoesNotContain("docs & notes", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("namespace Smoke", result.Stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_MarkdownAliasWritesMarkdownTreeToStdout()
	{
		using var temp = new TemporaryDirectory();
		var projectPath = temp.CreateDirectory("project markdown alias");
		SeedStructuredFormatProject(projectPath);

		var result = await RunAppAsync(
			CommandLineOptionTokens.Path, projectPath,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Format, "markdown",
			CommandLineOptionTokens.Roots, "src",
			CommandLineOptionTokens.Extensions, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		Assert.StartsWith($"Root: {Path.GetFullPath(projectPath).Replace('\\', '/')}", result.Stdout, StringComparison.Ordinal);
		Assert.Contains("- src/", result.Stdout, StringComparison.Ordinal);
		Assert.Contains("  - Program.cs", result.Stdout, StringComparison.Ordinal);
		Assert.Contains("    - UserService.cs", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("docs & notes", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("namespace Smoke", result.Stdout, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("xml")]
	[InlineData("md")]
	public async Task UserLevelTerminalCommand_StructuredTreeFormatsRunThroughInstalledCommand(string format)
	{
		using var temp = new TemporaryDirectory();
		var projectPath = temp.CreateDirectory("project terminal structured");
		SeedStructuredFormatProject(projectPath);

		var result = await RunUserLevelTerminalCommandAsync(
			temp,
			temp.Path,
			projectPath,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Format, format,
			CommandLineOptionTokens.Roots, "src",
			CommandLineOptionTokens.Extensions, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		Assert.DoesNotContain("namespace Smoke", result.Stdout, StringComparison.Ordinal);
		if (format == "xml")
		{
			var document = XDocument.Parse(result.Stdout);
			Assert.Equal(["src/Program.cs", "src/Services/UserService.cs"], ExtractXmlFilePaths(document));
			return;
		}

		Assert.StartsWith("Root: ", result.Stdout, StringComparison.Ordinal);
		Assert.Contains("- src/", result.Stdout, StringComparison.Ordinal);
		Assert.Contains("  - Program.cs", result.Stdout, StringComparison.Ordinal);
		Assert.Contains("    - UserService.cs", result.Stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_ExportXmlTreeToStdoutWritesXmlOnlyAndRoundTripsSpecialNames()
	{
		using var temp = new TemporaryDirectory();
		var projectPath = temp.CreateDirectory("project & unicode");
		SeedStructuredFormatProject(projectPath);

		var result = await RunAppAsync(
			CommandLineOptionTokens.Path, projectPath,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Format, "xml",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		Assert.StartsWith("<t ", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("<?xml", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("namespace Smoke", result.Stdout, StringComparison.Ordinal);

		var document = XDocument.Parse(result.Stdout);
		Assert.Equal("t", document.Root?.Name.LocalName);
		Assert.Equal(Path.GetFullPath(projectPath).Replace('\\', '/'), document.Root?.Attribute("r")?.Value);
		Assert.Equal(
			[
				"-scripts/-build.ps1",
				"docs & notes/release [draft].md",
				"src/Program.cs",
				"src/Services/UserService.cs",
				"Документы/Файл.cs"
			],
			ExtractXmlFilePaths(document));
		Assert.Contains("EmptyFolder", ExtractXmlEmptyFolderPaths(document));
		Assert.All(document.Root!.DescendantsAndSelf(), static element => Assert.Contains(element.Name.LocalName, new[] { "t", "d", "f" }));
		Assert.Contains("&amp;", result.Stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_ExportMarkdownTreeToStdoutWritesMarkdownOnlyAndEscapesListMarkers()
	{
		using var temp = new TemporaryDirectory();
		var projectPath = temp.CreateDirectory("project with markdown");
		SeedStructuredFormatProject(projectPath);

		var result = await RunAppAsync(
			projectPath,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Format, "md",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		Assert.StartsWith($"Root: {Path.GetFullPath(projectPath).Replace('\\', '/')}", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("namespace Smoke", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("\t", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("- -scripts/", result.Stdout, StringComparison.Ordinal);
		Assert.Contains("- \\-scripts/", result.Stdout, StringComparison.Ordinal);
		Assert.Contains("  - \\-build.ps1", result.Stdout, StringComparison.Ordinal);

		var treeLines = result.Stdout.Split('\n').Skip(2).Select(static line => line.TrimEnd('\r')).ToArray();
		Assert.All(treeLines, static line => Assert.False(line.EndsWith(' '), $"Markdown tree line has trailing spaces: '{line}'."));
		Assert.Contains("- EmptyFolder/", treeLines);
		Assert.Contains("- src/", treeLines);
		Assert.Contains("  - Services/", treeLines);
		Assert.Contains("    - UserService.cs", treeLines);
		Assert.Contains("  - release [draft].md", treeLines);
	}

	[Theory]
	[InlineData("ascii")]
	[InlineData("json")]
	[InlineData("xml")]
	[InlineData("md")]
	public async Task Process_TreeExportStdoutContractsCoverAllFormats(string format)
	{
		using var temp = new TemporaryDirectory();
		var projectPath = temp.CreateDirectory("project stdout matrix");
		SeedStructuredFormatProject(projectPath);

		var result = await RunAppAsync(
			CommandLineOptionTokens.Path, projectPath,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Format, format,
			CommandLineOptionTokens.Output, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.Roots, "src",
			CommandLineOptionTokens.Extensions, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		AssertTreeOnlyStdoutContract(
			result.Stdout,
			format,
			projectPath,
			["src/Program.cs", "src/Services/UserService.cs"]);
	}

	[Theory]
	[InlineData("ascii")]
	[InlineData("json")]
	[InlineData("xml")]
	[InlineData("md")]
	public async Task UserLevelTerminalCommand_TreeExportStdoutContractsCoverAllFormats(string format)
	{
		using var temp = new TemporaryDirectory();
		var projectPath = temp.CreateDirectory("project terminal stdout matrix");
		SeedStructuredFormatProject(projectPath);

		var result = await RunUserLevelTerminalCommandAsync(
			temp,
			temp.Path,
			projectPath,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Format, format,
			CommandLineOptionTokens.Output, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.Roots, "src",
			CommandLineOptionTokens.Extensions, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		AssertTreeOnlyStdoutContract(
			result.Stdout,
			format,
			projectPath,
			["src/Program.cs", "src/Services/UserService.cs"]);
	}

	[Theory]
	[InlineData("ascii", "tree-output.txt")]
	[InlineData("json", "tree-output.json")]
	[InlineData("xml", "tree-output.xml")]
	[InlineData("md", "tree-output.md")]
	public async Task Process_TreeExportFileOutputPrintsOnlyResolvedPathForAllFormats(
		string format,
		string outputFileName)
	{
		using var temp = new TemporaryDirectory();
		var projectPath = temp.CreateDirectory("project file output matrix");
		SeedStructuredFormatProject(projectPath);
		var outputPath = Path.Combine(temp.Path, "exports", outputFileName);

		var result = await RunAppAsync(
			CommandLineOptionTokens.Path, projectPath,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Format, format,
			CommandLineOptionTokens.Output, outputPath,
			CommandLineOptionTokens.Roots, "src",
			CommandLineOptionTokens.Extensions, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		Assert.Equal(Path.GetFullPath(outputPath), AssertSingleOutputLine(result.Stdout));
		Assert.DoesNotContain("Program.cs", result.Stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("namespace Smoke", result.Stdout, StringComparison.Ordinal);
		Assert.True(File.Exists(outputPath));

		var payload = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
		AssertTreeOnlyStdoutContract(
			payload,
			format,
			projectPath,
			["src/Program.cs", "src/Services/UserService.cs"]);
	}

	[Theory]
	[InlineData("ascii")]
	[InlineData("json")]
	[InlineData("xml")]
	[InlineData("md")]
	public async Task Process_TreeContentStdoutContractsCoverAllFormats(string format)
	{
		using var temp = new TemporaryDirectory();
		var projectPath = temp.CreateDirectory("project tree content stdout matrix");
		SeedStructuredFormatProject(projectPath);

		var result = await RunAppAsync(
			CommandLineOptionTokens.Path, projectPath,
			CommandLineOptionTokens.Export, "tree-content",
			CommandLineOptionTokens.Format, format,
			CommandLineOptionTokens.Output, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.Roots, "src",
			CommandLineOptionTokens.Extensions, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		var (treePart, contentPart) = SplitTreeContentStdout(format, result.Stdout);
		AssertTreeOnlyStdoutContract(
			treePart,
			format,
			projectPath,
			["src/Program.cs", "src/Services/UserService.cs"]);
		Assert.Contains("src/Program.cs:", contentPart, StringComparison.Ordinal);
		Assert.Contains("src/Services/UserService.cs:", contentPart, StringComparison.Ordinal);
		Assert.DoesNotContain(Path.GetFullPath(projectPath).Replace('\\', '/'), contentPart.Replace('\\', '/'), StringComparison.Ordinal);
		Assert.DoesNotContain("src\\Program.cs:", contentPart, StringComparison.Ordinal);
		Assert.Contains("namespace Smoke", contentPart, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("unknown-option", "Unknown option '--unknown'.")]
	[InlineData("missing-path", "Headless analysis requires --path")]
	[InlineData("output-without-export", "--output requires --export or --copy")]
	[InlineData("content-json-format", "--export-format applies only to tree")]
	[InlineData("report-stdout-export", "Cannot combine --report - with --export")]
	[InlineData("export-stdout-report-file", "Cannot combine stdout export with --report")]
	public async Task Process_UsageErrorsWriteOnlyStderrAndLeaveStdoutEmpty(
		string scenario,
		string expectedError)
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");
		var outputPath = Path.Combine(temp.Path, "exports", "should-not-exist.txt");
		var reportPath = Path.Combine(temp.Path, "reports", "should-not-exist.json");

		var result = await RunAppAsync(BuildUsageErrorArgs(scenario, temp.Path, outputPath, reportPath));

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Equal(string.Empty, result.Stdout);
		Assert.Contains(expectedError, result.Stderr, StringComparison.Ordinal);
		Assert.StartsWith("DevProjex: ", result.Stderr, StringComparison.Ordinal);
		Assert.False(File.Exists(outputPath));
		Assert.False(File.Exists(reportPath));
	}

	[Theory]
	[InlineData("xml", "<t ")]
	[InlineData("md", "Root: ")]
	public async Task Process_TreeContentStructuredFormatsKeepTreeBlockAndRelativePlainTextHeaders(
		string format,
		string expectedTreeStart)
	{
		using var temp = new TemporaryDirectory();
		var projectPath = temp.CreateDirectory("project with spaces");
		SeedStructuredFormatProject(projectPath);

		var result = await RunAppAsync(
			CommandLineOptionTokens.Path, projectPath,
			CommandLineOptionTokens.Export, "tree-content",
			CommandLineOptionTokens.Format, format,
			CommandLineOptionTokens.Output, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.Roots, "src",
			CommandLineOptionTokens.Extensions, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		var (treePart, contentPart) = SplitTreeAndContentStdout(result.Stdout);
		Assert.StartsWith(expectedTreeStart, treePart, StringComparison.Ordinal);
		Assert.DoesNotContain("namespace Smoke", treePart, StringComparison.Ordinal);
		Assert.Contains("src/Program.cs:", contentPart, StringComparison.Ordinal);
		Assert.Contains("src/Services/UserService.cs:", contentPart, StringComparison.Ordinal);
		Assert.DoesNotContain(Path.GetFullPath(projectPath).Replace('\\', '/'), contentPart.Replace('\\', '/'), StringComparison.Ordinal);
		Assert.DoesNotContain("src\\Program.cs:", contentPart, StringComparison.Ordinal);
		Assert.Contains("namespace Smoke", contentPart, StringComparison.Ordinal);

		if (format == "xml")
		{
			var document = XDocument.Parse(treePart);
			Assert.Equal(["src/Program.cs", "src/Services/UserService.cs"], ExtractXmlFilePaths(document));
		}
		else
		{
			Assert.Contains("- src/", treePart, StringComparison.Ordinal);
			Assert.Contains("  - Program.cs", treePart, StringComparison.Ordinal);
			Assert.Contains("  - Services/", treePart, StringComparison.Ordinal);
			Assert.Contains("    - UserService.cs", treePart, StringComparison.Ordinal);
		}
	}

	[Theory]
	[InlineData("xml")]
	[InlineData("md")]
	public async Task Process_TreeContentStructuredFormatsPreserveUnicodeTreeNamesAndRelativeContentHeaders(string format)
	{
		using var temp = new TemporaryDirectory();
		var projectPath = temp.CreateDirectory("project unicode stdout");
		SeedStructuredFormatProject(projectPath);

		var result = await RunAppAsync(
			CommandLineOptionTokens.Path, projectPath,
			CommandLineOptionTokens.Export, "tree-content",
			CommandLineOptionTokens.Format, format,
			CommandLineOptionTokens.Output, CommandLineOptionTokens.StandardOutputReportPath,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);
		var (treePart, contentPart) = SplitTreeAndContentStdout(result.Stdout);

		if (format == "xml")
		{
			var document = XDocument.Parse(treePart);
			Assert.Contains("Документы/Файл.cs", ExtractXmlFilePaths(document));
		}
		else
		{
			Assert.Contains("- Документы/", treePart, StringComparison.Ordinal);
			Assert.Contains("  - Файл.cs", treePart, StringComparison.Ordinal);
		}

		Assert.Contains("Документы/Файл.cs:", contentPart, StringComparison.Ordinal);
		Assert.Contains("namespace Smoke.Документы;", contentPart, StringComparison.Ordinal);
		Assert.DoesNotContain("?????????/????.cs", result.Stdout, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(
		"<t r=\"C:/Project\"><d n=\"src\"><f>Program.cs</f></d></t>\n?\n?\nsrc/Program.cs:\nclass App {}\n",
		"<t ",
		"src/Program.cs:")]
	[InlineData(
		"Root: C:/Project\n\n- src/\n  - Program.cs\n?\n?\nsrc/Program.cs:\nclass App {}\n",
		"Root: ",
		"src/Program.cs:")]
	public void SplitTreeAndContentStdout_StructuredFormatsTolerateTranscodedSeparator(
		string stdout,
		string expectedTreeStart,
		string expectedContentHeader)
	{
		var (treePart, contentPart) = SplitTreeAndContentStdout(stdout);

		Assert.StartsWith(expectedTreeStart, treePart, StringComparison.Ordinal);
		Assert.DoesNotContain("\n?", treePart, StringComparison.Ordinal);
		Assert.StartsWith(expectedContentHeader, contentPart, StringComparison.Ordinal);
	}

	[Fact]
	public void SplitTreeAndContentStdout_NormalizesBomAnsiCrLfAndNbspSeparator()
	{
		var stdout = "\uFEFF\x1B[32mRoot: C:/Project\x1B[0m\r\n\r\n- src/\r\n  - Program.cs\r\n\u00A0\r\n\u00A0\r\nsrc/Program.cs:\r\nclass App {}\r\n";

		var (treePart, contentPart) = SplitTreeAndContentStdout(stdout);

		Assert.StartsWith("Root: C:/Project", treePart, StringComparison.Ordinal);
		Assert.Contains("  - Program.cs", treePart, StringComparison.Ordinal);
		Assert.StartsWith("src/Program.cs:", contentPart, StringComparison.Ordinal);
		Assert.DoesNotContain("\u001b[", treePart, StringComparison.Ordinal);
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
	public async Task Process_SilentWithoutReportWritesJsonToStdout()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");

		var result = await RunAppAsync(
			CommandLineOptionTokens.Silent,
			temp.Path,
			CommandLineOptionTokens.Roots, "src",
			CommandLineOptionTokens.Extensions, "cs",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone);

		Assert.Equal(CommandLineExitCodes.Success, result.ExitCode);
		Assert.Equal(string.Empty, result.Stderr);

		using var document = JsonDocument.Parse(result.Stdout);
		var root = document.RootElement;
		Assert.Equal(ProjectAnalysisReport.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
		Assert.Equal(temp.Path, root.GetProperty("rootPath").GetString());
		Assert.Equal(["src"], ReadStringArray(root.GetProperty("selection").GetProperty("selectedRootFolders")));
		Assert.Equal([".cs"], ReadStringArray(root.GetProperty("selection").GetProperty("selectedExtensions")));
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
			CommandLineOptionTokens.ExportFormat, "yaml");

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Equal(string.Empty, result.Stdout);
		Assert.Contains("Unsupported export format 'yaml'.", result.Stderr, StringComparison.Ordinal);
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
		Assert.Contains("--export-format and --format require --export", result.Stderr, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(CommandLineOptionTokens.Preview, null)]
	[InlineData(CommandLineOptionTokens.TreeFormat, "md")]
	[InlineData(CommandLineOptionTokens.PreviewMode, "tree-content")]
	[InlineData(CommandLineOptionTokens.TreeFilter, "Services")]
	[InlineData(CommandLineOptionTokens.PreviewSearch, "Program")]
	public async Task Process_DesktopStartupOptionWithoutProjectReturnsUsageErrorInsteadOfLaunchingUi(
		string option,
		string? value)
	{
		var result = value is null
			? await RunAppAsync(option)
			: await RunAppAsync(option, value);

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Equal(string.Empty, result.Stdout);
		Assert.Contains("UI startup options require --path", result.Stderr, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_DesktopStartupInlineOptionWithoutProjectReturnsUsageErrorInsteadOfLaunchingUi()
	{
		var result = await RunAppAsync($"{CommandLineOptionTokens.PreviewSearch}=Program");

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Equal(string.Empty, result.Stdout);
		Assert.Contains("UI startup options require --path", result.Stderr, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_LastWithExplicitPathReturnsUsageErrorInsteadOfLaunchingUi()
	{
		using var temp = new TemporaryDirectory();

		var result = await RunAppAsync(CommandLineOptionTokens.Last, CommandLineOptionTokens.Path, temp.Path);

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Equal(string.Empty, result.Stdout);
		Assert.Contains("Use either --last or --path", result.Stderr, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(CommandLineOptionTokens.PreviewMode, "split", "Unsupported preview mode 'split'.")]
	[InlineData(CommandLineOptionTokens.TreeFormat, "yaml", "Unsupported tree format 'yaml'.")]
	public async Task Process_InvalidDesktopStartupValueReturnsUsageErrorBeforeLaunchingUi(
		string option,
		string value,
		string expectedError)
	{
		using var temp = new TemporaryDirectory();

		var result = await RunAppAsync(CommandLineOptionTokens.Path, temp.Path, option, value);

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Equal(string.Empty, result.Stdout);
		Assert.Contains(expectedError, result.Stderr, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_DesktopStartupOptionsWithNoUiReturnUsageErrorInsteadOfRunningHeadless()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");

		var result = await RunAppAsync(
			CommandLineOptionTokens.NoUi,
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.PreviewMode, "tree-content",
			CommandLineOptionTokens.TreeFormat, "md");

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Equal(string.Empty, result.Stdout);
		Assert.Contains("UI startup options cannot be combined", result.Stderr, StringComparison.Ordinal);
		Assert.DoesNotContain("class App", result.Stdout, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_DesktopStartupOptionsWithExportReturnUsageErrorInsteadOfMixingContracts()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");

		var result = await RunAppAsync(
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.TreeFormat, "xml");

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Equal(string.Empty, result.Stdout);
		Assert.Contains("UI startup options cannot be combined", result.Stderr, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Process_TreeFilterAndPreviewSearchTogetherReturnUsageErrorBeforeLaunchingUi()
	{
		using var temp = new TemporaryDirectory();
		temp.CreateFile(Path.Combine("src", "App.cs"), "class App {}\n");

		var result = await RunAppAsync(
			CommandLineOptionTokens.Path, temp.Path,
			CommandLineOptionTokens.TreeFilter, "src",
			CommandLineOptionTokens.PreviewSearch, "App");

		Assert.Equal(CommandLineExitCodes.UsageError, result.ExitCode);
		Assert.Equal(string.Empty, result.Stdout);
		Assert.Contains("--tree-filter and --preview-search cannot be used together", result.Stderr, StringComparison.Ordinal);
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

	private static void SeedPortableSmartIgnoreProject(TemporaryDirectory temp) =>
		SeedPortableSmartIgnoreProject(temp.Path);

	private static void SeedPortableSmartIgnoreProject(string projectPath)
	{
		WriteProjectFile(projectPath, Path.Combine("src", "App.cs"), "// SOURCE_SENTINEL\nclass App {}\n");
		WriteProjectFile(projectPath, "packages.config", "<packages />\n");
		WriteProjectFile(projectPath, "App.sln.DotSettings", "SHARED_SETTINGS_SENTINEL\n");
		WriteProjectFile(projectPath, "App.sln.DotSettings.user", "LOCAL_STATE_SENTINEL\n");
		WriteProjectFile(projectPath, Path.Combine("packages", "repositories.config"), "REPOSITORY_SENTINEL\n");
		WriteProjectFile(projectPath, Path.Combine("packages", "Alpha.1.0.0", "Alpha.1.0.0.nupkg"), "PACKAGE_SENTINEL\n");
		WriteProjectFile(projectPath, Path.Combine("packages", "Alpha.1.0.0", "lib", "Alpha.dll"), "binary\n");
		WriteProjectFile(projectPath, Path.Combine("packages", "Beta.2.0.0", "Beta.2.0.0.nupkg"), "package\n");
		WriteProjectFile(projectPath, Path.Combine("packages", "Beta.2.0.0", "ref", "Beta.dll"), "binary\n");
	}

	private static void SeedPortableStoreMatrixProject(string projectPath)
	{
		WriteProjectFile(projectPath, Path.Combine("src", "App.cs"), "class App {}\n");
		WriteProjectFile(projectPath, Path.Combine("source", "packages", "Domain.cs"), "class Domain {}\n");
		WriteProjectFile(projectPath, Path.Combine("source", "repository", "Repository.cs"), "class Repository {}\n");
		WriteProjectFile(projectPath, Path.Combine("source", "registry", "Registry.cs"), "class Registry {}\n");
		WriteProjectFile(projectPath, Path.Combine("source", "_cacache", "Cache.cs"), "class Cache {}\n");
		WriteProjectFile(projectPath, Path.Combine("source", "modules-2", "Modules.cs"), "class Modules {}\n");
		WriteProjectFile(projectPath, Path.Combine(".nuget", "packages", "acme", "Acme.nupkg"), "package\n");
		WriteProjectFile(projectPath, Path.Combine(".m2", "repository", "acme", "module.pom"), "<project />\n");
		WriteProjectFile(projectPath, Path.Combine(".cargo", "registry", "index", "config.json"), "{}\n");
		WriteProjectFile(projectPath, Path.Combine(".npm", "_cacache", "content-v2", "sha"), "cache\n");
		WriteProjectFile(projectPath, Path.Combine(".gradle", "caches", "modules-2", "files-2.1", "acme.jar"), "binary\n");
	}

	private static void SeedSmartIgnoreNegativeMatrixProject(string projectPath)
	{
		WriteProjectFile(projectPath, "App.csproj", "<Project />\n");
		WriteProjectFile(projectPath, "App.csproj.user", "local state\n");
		WriteProjectFile(projectPath, Path.Combine("src", "App.cs"), "class App {}\n");
		WriteProjectFile(projectPath, Path.Combine("obj", "project.assets.json"), "{}\n");
		WriteProjectFile(projectPath, Path.Combine("obj-backup", "project.assets.json"), "{}\n");
		WriteProjectFile(projectPath, Path.Combine("build", "README.md"), "source build folder\n");
		WriteProjectFile(projectPath, Path.Combine("build", "docs", "CMakeCache.txt"), "source documentation\n");
		WriteProjectFile(projectPath, Path.Combine("vendor", "src", "autoload.php"), "<?php // source\n");
		WriteProjectFile(projectPath, Path.Combine("packages", "Alpha", "Alpha.nupkg"), "single incomplete package\n");
		Directory.CreateDirectory(Path.Combine(projectPath, "packages", "Alpha", "lib"));
		WriteProjectFile(projectPath, Path.Combine("m2-backup", "repository", "service", "package.json"), "{}\n");
		WriteProjectFile(projectPath, Path.Combine("cmake-build", "CMakeCache.txt"), "source fixture\n");
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

	private static void SeedRepoLikeJsonProject(string projectPath)
	{
		WriteProjectFile(projectPath, ".editorconfig", "root = true\n");
		WriteProjectFile(projectPath, ".gitignore", "bin/\nobj/\n");
		WriteProjectFile(projectPath, Path.Combine(".git", "HEAD"), "ref: refs/heads/main\n");
		WriteProjectFile(projectPath, Path.Combine(".git", "config"), "[core]\n\trepositoryformatversion = 0\n");
		WriteProjectFile(projectPath, Path.Combine(".git", "refs", "heads", "main"), "0000000000000000000000000000000000000000\n");
		WriteProjectFile(projectPath, "global.json", "{\"sdk\":{\"version\":\"10.0.300\"}}\n");
		WriteProjectFile(projectPath, "README.md", "# Repo Like\n");
		WriteProjectFile(projectPath, Path.Combine("docs", "Guide.md"), "guide content\n");
		WriteProjectFile(projectPath, Path.Combine("docs", "файл.txt"), "unicode file\n");
		WriteProjectFile(projectPath, Path.Combine("scripts", "build.ps1"), "dotnet build\n");
		WriteProjectFile(projectPath, Path.Combine("space dir", "file with spaces.txt"), "spaces\n");
		WriteProjectFile(projectPath, Path.Combine("src", "Program.cs"), "namespace RepoLike;\npublic static class Program { }\n");
		WriteProjectFile(projectPath, Path.Combine("src", "Models", "Пользователь.cs"), "namespace RepoLike.Models;\n");
		WriteProjectFile(projectPath, Path.Combine("src", "Services", "UserService.cs"), "namespace RepoLike.Services;\n");
		Directory.CreateDirectory(Path.Combine(projectPath, "empty-folder"));
	}

	private static void SeedStructuredFormatProject(string projectPath)
	{
		WriteProjectFile(projectPath, Path.Combine("src", "Program.cs"), "namespace Smoke;\npublic static class Program { }\n");
		WriteProjectFile(projectPath, Path.Combine("src", "Services", "UserService.cs"), "namespace Smoke.Services;\npublic sealed class UserService { }\n");
		WriteProjectFile(projectPath, Path.Combine("-scripts", "-build.ps1"), "dotnet build\n");
		WriteProjectFile(projectPath, Path.Combine("docs & notes", "release [draft].md"), "# Release\n");
		WriteProjectFile(projectPath, Path.Combine("Документы", "Файл.cs"), "namespace Smoke.Документы;\n");
		Directory.CreateDirectory(Path.Combine(projectPath, "EmptyFolder"));
	}

	private static void WriteProjectFile(string rootPath, string relativePath, string content)
	{
		var path = Path.Combine(rootPath, relativePath);
		var directory = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(directory))
			Directory.CreateDirectory(directory);

		File.WriteAllText(path, content);
	}

	private static string[] BuildUsageErrorArgs(
		string scenario,
		string projectPath,
		string outputPath,
		string reportPath) =>
		scenario switch
		{
			"unknown-option" => ["--unknown"],
			"missing-path" => [CommandLineOptionTokens.NoUi, CommandLineOptionTokens.Report],
			"output-without-export" =>
			[
				CommandLineOptionTokens.Path, projectPath,
				CommandLineOptionTokens.Output, outputPath
			],
			"content-json-format" =>
			[
				CommandLineOptionTokens.Path, projectPath,
				CommandLineOptionTokens.Export, "content",
				CommandLineOptionTokens.Format, "json"
			],
			"report-stdout-export" =>
			[
				CommandLineOptionTokens.Path, projectPath,
				CommandLineOptionTokens.Report, CommandLineOptionTokens.StandardOutputReportPath,
				CommandLineOptionTokens.Export, "tree"
			],
			"export-stdout-report-file" =>
			[
				CommandLineOptionTokens.Path, projectPath,
				CommandLineOptionTokens.ReportPath, reportPath,
				CommandLineOptionTokens.Export, "tree",
				CommandLineOptionTokens.Output, CommandLineOptionTokens.StandardOutputReportPath
			],
			_ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown stdout usage-error scenario.")
		};

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
		// The installed-command smoke tests validate wrapper semantics. They should not
		// depend on whether the runner preserved the apphost execute bit in build output.
		MakeExecutableIfUnix(appHostPath);
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

	private static async Task<CommandLineProcessResult> RunUserLevelTerminalCommandAsync(
		TemporaryDirectory temp,
		string workingDirectory,
		params string[] args)
	{
		if (OperatingSystem.IsWindows())
		{
			var binDirectory = temp.CreateDirectory("bin & tools");
			await CreateWindowsLauncherAsync(
				Path.Combine(binDirectory, CommandLineExecutableAliases.WindowsPortableCommandFileName));
			return await RunWindowsPathCommandAsync("devprojex", binDirectory, workingDirectory, args);
		}

		var unixBinDirectory = temp.CreateDirectory("bin");
		await CreateUnixWrapperAsync(Path.Combine(unixBinDirectory, CommandLineExecutableAliases.UnixCommand));
		return await RunPathCommandAsync(CommandLineExecutableAliases.UnixCommand, unixBinDirectory, workingDirectory, args);
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

	private static async Task<CommandLineProcessResult> RunWindowsCommandWithDelayedExpansionAsync(
		string commandPath,
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

		startInfo.ArgumentList.Add("/d");
		startInfo.ArgumentList.Add("/v:on");
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

	private static async Task<CommandLineProcessResult> RunPosixShellPathCommandAsync(
		string commandName,
		string binDirectory,
		string? workingDirectory,
		params string[] args)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = File.Exists("/bin/sh") ? "/bin/sh" : "sh",
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};

		if (!string.IsNullOrWhiteSpace(workingDirectory))
			startInfo.WorkingDirectory = workingDirectory;

		startInfo.Environment["PATH"] = PrependPath(binDirectory, startInfo.Environment["PATH"]);
		startInfo.Environment["DEVPROJEX_TEST_COMMAND"] = commandName;
		var argumentReferences = SetShellArgumentEnvironment(startInfo, args, "$DEVPROJEX_TEST_ARGUMENT_");
		startInfo.ArgumentList.Add("-c");
		startInfo.ArgumentList.Add(
			"exec \"$DEVPROJEX_TEST_COMMAND\"" +
			string.Concat(argumentReferences.Select(static reference => " \"" + reference + "\"")));

		return await RunProcessAsync(startInfo);
	}

	private static async Task<CommandLineProcessResult> RunWindowsPowerShellPathCommandAsync(
		string commandName,
		string binDirectory,
		string? workingDirectory,
		params string[] args)
	{
		return await RunProcessAsync(CreateWindowsPowerShellPathCommandStartInfo(
			commandName,
			binDirectory,
			workingDirectory,
			redirectStandardStreams: true,
			args: args));
	}

	private static Process StartWindowsPowerShellPathCommand(
		string commandName,
		string binDirectory,
		string? workingDirectory,
		params string[] args) =>
		Process.Start(CreateWindowsPowerShellPathCommandStartInfo(
			commandName,
			binDirectory,
			workingDirectory,
			redirectStandardStreams: false,
			args: args))
		?? throw new InvalidOperationException("Failed to start the Windows PowerShell terminal command test process.");

	private static ProcessStartInfo CreateWindowsPowerShellPathCommandStartInfo(
		string commandName,
		string binDirectory,
		string? workingDirectory,
		bool redirectStandardStreams,
		IReadOnlyList<string> args)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = "powershell.exe",
			RedirectStandardOutput = redirectStandardStreams,
			RedirectStandardError = redirectStandardStreams,
			UseShellExecute = false,
			CreateNoWindow = true
		};

		if (!string.IsNullOrWhiteSpace(workingDirectory))
			startInfo.WorkingDirectory = workingDirectory;

		startInfo.Environment["PATH"] = PrependPath(binDirectory, startInfo.Environment["PATH"]);
		startInfo.Environment["DEVPROJEX_TEST_COMMAND"] = commandName;
		var argumentReferences = SetShellArgumentEnvironment(startInfo, args, "$env:DEVPROJEX_TEST_ARGUMENT_");
		startInfo.ArgumentList.Add("-NoLogo");
		startInfo.ArgumentList.Add("-NoProfile");
		startInfo.ArgumentList.Add("-NonInteractive");
		startInfo.ArgumentList.Add("-Command");
		startInfo.ArgumentList.Add(
			"$ErrorActionPreference = 'Stop'; " +
			"[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); " +
			"$OutputEncoding = [Console]::OutputEncoding; " +
			"& $env:DEVPROJEX_TEST_COMMAND @(" +
			string.Join(", ", argumentReferences) + "); exit $LASTEXITCODE");

		return startInfo;
	}

	private static IReadOnlyList<string> SetShellArgumentEnvironment(
		ProcessStartInfo startInfo,
		IReadOnlyList<string> args,
		string referencePrefix)
	{
		var references = new string[args.Count];
		for (var index = 0; index < args.Count; index++)
		{
			var variableName = $"DEVPROJEX_TEST_ARGUMENT_{index}";
			startInfo.Environment[variableName] = args[index];
			references[index] = referencePrefix + index;
		}

		return references;
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
		if (startInfo.RedirectStandardOutput)
			startInfo.StandardOutputEncoding ??= new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
		if (startInfo.RedirectStandardError)
			startInfo.StandardErrorEncoding ??= new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

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

	private static async Task<Process> WaitForProcessStartedFromPathAsync(string executablePath)
	{
		var expectedPath = Path.GetFullPath(executablePath);
		var processName = Path.GetFileNameWithoutExtension(executablePath);
		var deadline = DateTime.UtcNow.AddSeconds(10);

		while (DateTime.UtcNow < deadline)
		{
			foreach (var process in Process.GetProcessesByName(processName))
			{
				try
				{
					var processPath = process.MainModule?.FileName;
					if (string.Equals(processPath, expectedPath, StringComparison.OrdinalIgnoreCase))
						return process;
				}
				catch (InvalidOperationException)
				{
					// The process exited while its executable path was being inspected.
				}
				catch (System.ComponentModel.Win32Exception)
				{
					// Process metadata can be temporarily unavailable during startup.
				}

				process.Dispose();
			}

			await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
		}

		throw new TimeoutException($"DevProjex did not start from the configured launcher target '{executablePath}'.");
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

	private static (string JsonPart, string ContentPart) SplitTreeContentJsonStdout(string stdout)
	{
		// Tree-content JSON intentionally keeps only the tree as JSON; selected file content stays plain text after it.
		// Windows command shells can transcode NBSP separators, so this smoke test splits on the JSON object boundary
		// instead of asserting the exact separator character that the lower-level export service already owns.
		var jsonEndIndex = FindTopLevelJsonObjectEnd(stdout);
		Assert.True(jsonEndIndex > 0, "Expected tree-content stdout to start with a complete JSON tree object.");
		var contentPart = stdout[jsonEndIndex..];
		Assert.False(string.IsNullOrWhiteSpace(contentPart), "Expected tree-content stdout to include plain text content after the JSON tree.");
		return (stdout[..jsonEndIndex], contentPart);
	}

	private static (string TreePart, string ContentPart) SplitTreeAndContentStdout(string stdout)
	{
		var normalized = NormalizeCapturedStdout(stdout);
		var separatorIndex = normalized.IndexOf('\u00A0');
		if (separatorIndex > 0)
		{
			var treePart = normalized[..separatorIndex].TrimEnd('\r', '\n');
			var contentPart = TrimLeadingTreeContentSeparator(normalized[separatorIndex..]);
			Assert.False(string.IsNullOrWhiteSpace(contentPart), "Expected tree-content stdout to include plain text content.");
			return (treePart, contentPart);
		}

		if (normalized.StartsWith("<t ", StringComparison.Ordinal))
			return SplitXmlTreeAndContentStdout(normalized);

		if (normalized.StartsWith("Root: ", StringComparison.Ordinal))
			return SplitMarkdownTreeAndContentStdout(normalized);

		Assert.Fail("Could not detect tree/content boundary in structured tree-content stdout.");
		return (string.Empty, string.Empty);
	}

	private static (string TreePart, string ContentPart) SplitTreeContentStdout(string format, string stdout) =>
		format switch
		{
			"ascii" => SplitAsciiTreeAndContentStdout(stdout),
			"json" => SplitTreeContentJsonStdout(stdout),
			"xml" or "md" => SplitTreeAndContentStdout(stdout),
			_ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported tree-content stdout format.")
		};

	private static (string TreePart, string ContentPart) SplitAsciiTreeAndContentStdout(string stdout)
	{
		var normalized = NormalizeCapturedStdout(stdout);
		var separatorIndex = normalized.IndexOf('\u00A0');
		if (separatorIndex > 0)
		{
			var treePart = normalized[..separatorIndex].TrimEnd('\r', '\n');
			var contentPart = TrimLeadingTreeContentSeparator(normalized[separatorIndex..]);
			Assert.False(string.IsNullOrWhiteSpace(contentPart), "Expected ASCII tree-content stdout to include plain text content.");
			return (treePart, contentPart);
		}

		var firstLineEndIndex = normalized.IndexOf('\n');
		var contentStartIndex = FindFirstPlainTextContentHeader(
			normalized,
			firstLineEndIndex < 0 ? 0 : firstLineEndIndex + 1);
		Assert.True(contentStartIndex > 0, "Expected ASCII tree-content stdout to contain a relative file content header.");

		var tree = TrimTrailingTreeContentSeparator(normalized[..contentStartIndex]);
		var content = TrimLeadingTreeContentSeparator(normalized[contentStartIndex..]);
		Assert.False(string.IsNullOrWhiteSpace(content), "Expected ASCII tree-content stdout to include plain text content.");
		return (tree, content);
	}

	private static void AssertTreeOnlyStdoutContract(
		string stdout,
		string format,
		string projectPath,
		IReadOnlyList<string> expectedRelativeFilePaths)
	{
		AssertNoCommandNoiseInStdout(stdout);
		Assert.DoesNotContain("namespace Smoke", stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("public static class", stdout, StringComparison.Ordinal);

		switch (format)
		{
			case "ascii":
				Assert.StartsWith($"{Path.GetFullPath(projectPath)}:", stdout, StringComparison.Ordinal);
				foreach (var expectedPath in expectedRelativeFilePaths)
					Assert.Contains(Path.GetFileName(expectedPath), stdout, StringComparison.Ordinal);
				break;
			case "json":
				AssertJsonTreeStdoutContract(stdout, projectPath, expectedRelativeFilePaths);
				break;
			case "xml":
				AssertXmlTreeStdoutContract(stdout, projectPath, expectedRelativeFilePaths);
				break;
			case "md":
				AssertMarkdownTreeStdoutContract(stdout, projectPath, expectedRelativeFilePaths);
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported stdout tree format.");
		}
	}

	private static void AssertJsonTreeStdoutContract(
		string stdout,
		string projectPath,
		IReadOnlyList<string> expectedRelativeFilePaths)
	{
		using var document = JsonDocument.Parse(stdout);
		Assert.Equal(Path.GetFullPath(projectPath).Replace('\\', '/'), document.RootElement.GetProperty("rootPath").GetString());
		JsonTreeExportTestHelper.AssertOnlyRootPathAndTree(document.RootElement);
		JsonTreeExportTestHelper.AssertNoLegacyTreeContract(document.RootElement);

		var tree = JsonTreeExportTestHelper.GetTree(document);
		JsonTreeExportTestHelper.AssertRelativePathsUseForwardSlashes(tree);
		Assert.Equal(
			expectedRelativeFilePaths.OrderBy(static path => path, StringComparer.Ordinal).ToArray(),
			JsonTreeExportTestHelper.ExtractFilePaths(tree).OrderBy(static path => path, StringComparer.Ordinal).ToArray());
	}

	private static void AssertXmlTreeStdoutContract(
		string stdout,
		string projectPath,
		IReadOnlyList<string> expectedRelativeFilePaths)
	{
		Assert.StartsWith("<t ", stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("<?xml", stdout, StringComparison.Ordinal);
		var document = XDocument.Parse(stdout);
		Assert.Equal("t", document.Root?.Name.LocalName);
		Assert.Equal(Path.GetFullPath(projectPath).Replace('\\', '/'), document.Root?.Attribute("r")?.Value);
		Assert.Equal(
			expectedRelativeFilePaths.OrderBy(static path => path, StringComparer.Ordinal).ToArray(),
			ExtractXmlFilePaths(document).OrderBy(static path => path, StringComparer.Ordinal).ToArray());
		Assert.All(document.Root!.DescendantsAndSelf(), static element => Assert.Contains(element.Name.LocalName, new[] { "t", "d", "f" }));
	}

	private static void AssertMarkdownTreeStdoutContract(
		string stdout,
		string projectPath,
		IReadOnlyList<string> expectedRelativeFilePaths)
	{
		Assert.StartsWith($"Root: {Path.GetFullPath(projectPath).Replace('\\', '/')}", stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("\t", stdout, StringComparison.Ordinal);
		var treeLines = stdout.Split('\n').Skip(2).Select(static line => line.TrimEnd('\r')).ToArray();
		Assert.All(treeLines, static line => Assert.False(line.EndsWith(' '), $"Markdown tree line has trailing spaces: '{line}'."));
		Assert.Equal(
			expectedRelativeFilePaths.OrderBy(static path => path, StringComparer.Ordinal).ToArray(),
			ExtractMarkdownFilePaths(stdout).OrderBy(static path => path, StringComparer.Ordinal).ToArray());
	}

	private static void AssertNoCommandNoiseInStdout(string stdout)
	{
		Assert.DoesNotContain("Usage:", stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("DevProjex:", stdout, StringComparison.Ordinal);
		Assert.DoesNotContain("Building output", stdout, StringComparison.Ordinal);
	}

	private static string NormalizeCapturedStdout(string stdout)
	{
		var normalized = stdout.Replace("\r\n", "\n", StringComparison.Ordinal);
		if (normalized.Length > 0 && normalized[0] == '\uFEFF')
			normalized = normalized[1..];

		return Regex.Replace(normalized, @"\x1B\[[0-9;]*[A-Za-z]", string.Empty);
	}

	private static (string TreePart, string ContentPart) SplitXmlTreeAndContentStdout(string stdout)
	{
		var rootEndIndex = stdout.IndexOf("</t>", StringComparison.Ordinal);
		Assert.True(rootEndIndex > 0, "Expected XML tree-content stdout to contain a complete <t> tree block.");

		var treeEndIndex = rootEndIndex + "</t>".Length;
		var treePart = stdout[..treeEndIndex];
		var contentPart = TrimLeadingTreeContentSeparator(stdout[treeEndIndex..]);
		Assert.False(string.IsNullOrWhiteSpace(contentPart), "Expected tree-content stdout to include plain text content.");
		return (treePart, contentPart);
	}

	private static (string TreePart, string ContentPart) SplitMarkdownTreeAndContentStdout(string stdout)
	{
		var contentStartIndex = FindFirstPlainTextContentHeader(stdout);
		Assert.True(contentStartIndex > 0, "Expected Markdown tree-content stdout to contain a relative file content header.");

		var treePart = TrimTrailingTreeContentSeparator(stdout[..contentStartIndex]);
		var contentPart = TrimLeadingTreeContentSeparator(stdout[contentStartIndex..]);
		Assert.False(string.IsNullOrWhiteSpace(contentPart), "Expected tree-content stdout to include plain text content.");
		return (treePart, contentPart);
	}

	private static int FindFirstPlainTextContentHeader(string text, int startIndex = 0)
	{
		var lineStart = startIndex;
		while (lineStart < text.Length)
		{
			var lineEnd = text.IndexOf('\n', lineStart);
			if (lineEnd < 0)
				lineEnd = text.Length;

			var line = text[lineStart..lineEnd].TrimEnd('\r');
			if (IsPlainTextContentHeaderLine(line))
				return lineStart;

			lineStart = lineEnd + 1;
		}

		return -1;
	}

	private static bool IsPlainTextContentHeaderLine(string line)
	{
		var trimmedStart = line.TrimStart(' ');
		if (trimmedStart.Length == 0 ||
		    trimmedStart.StartsWith("- ", StringComparison.Ordinal) ||
		    line.StartsWith("Root: ", StringComparison.Ordinal))
			return false;

		return line.EndsWith(':');
	}

	private static string TrimLeadingTreeContentSeparator(string value)
	{
		var start = 0;
		while (start < value.Length)
		{
			var lineEnd = value.IndexOf('\n', start);
			var nextStart = lineEnd < 0 ? value.Length : lineEnd + 1;
			var line = value[start..(lineEnd < 0 ? value.Length : lineEnd)].Trim();
			if (line.Length > 0 && line.Any(static character => character != '\u00A0' && character != '?'))
				break;

			start = nextStart;
		}

		return value[start..];
	}

	private static string TrimTrailingTreeContentSeparator(string value)
	{
		var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
		while (lines.Count > 0)
		{
			var line = lines[^1].Trim();
			if (line.Length > 0 && line.Any(static character => character != '\u00A0' && character != '?'))
				break;

			lines.RemoveAt(lines.Count - 1);
		}

		return string.Join('\n', lines).TrimEnd('\r', '\n');
	}

	private static string[] ExtractXmlFilePaths(XDocument document)
	{
		Assert.NotNull(document.Root);
		var paths = new List<string>();
		CollectXmlFilePaths(document.Root!, prefix: string.Empty, paths);
		return paths.OrderBy(static path => path, StringComparer.Ordinal).ToArray();
	}

	private static string[] ExtractXmlEmptyFolderPaths(XDocument document)
	{
		Assert.NotNull(document.Root);
		var paths = new List<string>();
		CollectXmlEmptyFolderPaths(document.Root!, prefix: string.Empty, paths);
		return paths.OrderBy(static path => path, StringComparer.Ordinal).ToArray();
	}

	private static string[] ExtractMarkdownFilePaths(string markdown)
	{
		var paths = new List<string>();
		var folderStack = new List<string>();
		foreach (var rawLine in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Skip(2))
		{
			var line = rawLine.TrimEnd('\r');
			if (line.Length == 0)
				continue;

			var indent = line.TakeWhile(static character => character == ' ').Count();
			Assert.Equal(0, indent % 2);
			var level = indent / 2;
			var item = line[indent..];
			Assert.StartsWith("- ", item, StringComparison.Ordinal);
			var name = UnescapeMarkdownTreeName(item[2..]);

			if (folderStack.Count > level)
				folderStack.RemoveRange(level, folderStack.Count - level);

			if (name.EndsWith("/", StringComparison.Ordinal))
			{
				var folderName = name[..^1];
				if (folderStack.Count == level)
					folderStack.Add(folderName);
				else
					folderStack[level] = folderName;
				continue;
			}

			var filePath = string.Join(
				"/",
				folderStack.Take(level).Concat([name]));
			paths.Add(filePath);
		}

		return paths.OrderBy(static path => path, StringComparer.Ordinal).ToArray();
	}

	private static string UnescapeMarkdownTreeName(string name) =>
		name.StartsWith("\\-", StringComparison.Ordinal)
			? name[1..]
			: name;

	private static void CollectXmlFilePaths(XElement node, string prefix, List<string> paths)
	{
		foreach (var child in node.Elements())
		{
			if (child.Name.LocalName == "f")
			{
				paths.Add(prefix + child.Value);
				continue;
			}

			if (child.Name.LocalName != "d")
				continue;

			var folderName = child.Attribute("n")?.Value ?? string.Empty;
			CollectXmlFilePaths(child, prefix + folderName + "/", paths);
		}
	}

	private static void CollectXmlEmptyFolderPaths(XElement node, string prefix, List<string> paths)
	{
		foreach (var child in node.Elements("d"))
		{
			var folderName = child.Attribute("n")?.Value ?? string.Empty;
			var path = prefix + folderName;
			if (!child.Elements().Any())
				paths.Add(path);

			CollectXmlEmptyFolderPaths(child, path + "/", paths);
		}
	}

	private static int FindTopLevelJsonObjectEnd(string value)
	{
		var started = false;
		var inString = false;
		var escaped = false;
		var depth = 0;

		for (var i = 0; i < value.Length; i++)
		{
			var current = value[i];
			if (!started)
			{
				if (char.IsWhiteSpace(current))
					continue;

				if (current != '{')
					return -1;

				started = true;
				depth = 1;
				continue;
			}

			if (inString)
			{
				if (escaped)
				{
					escaped = false;
					continue;
				}

				if (current == '\\')
				{
					escaped = true;
					continue;
				}

				if (current == '"')
					inString = false;

				continue;
			}

			if (current == '"')
			{
				inString = true;
				continue;
			}

			if (current == '{')
			{
				depth++;
				continue;
			}

			if (current != '}')
				continue;

			depth--;
			if (depth == 0)
				return i + 1;
		}

		return -1;
	}

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
