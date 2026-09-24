using DevProjex.Infrastructure.TerminalCommands;
using DevProjex.Infrastructure.Processes;

namespace DevProjex.Tests.Unit;

public sealed class McpClientLaunchServiceTests
{
	[Theory]
	[InlineData((int)McpConnectionClient.ClaudeCode, "claude")]
	[InlineData((int)McpConnectionClient.Codex, "codex")]
	public async Task Open_WindowsUsesWindowsTerminalWithResolvedClientPath(
		int clientValue,
		string commandName)
	{
		using var project = new TemporaryDirectory();
		var projectRoot = project.CreateFolder("project with spaces Пример");
		var setup = CreateService(
			TerminalCommandHostPlatform.Windows,
			availableCommands: [commandName, "wt"]);

		var result = await setup.Service.OpenAsync(
			new McpClientLaunchRequest((McpConnectionClient)clientValue, projectRoot),
			TestContext.Current.CancellationToken);

		Assert.True(result.Succeeded);
		var request = Assert.Single(setup.ProcessRunner.Requests);
		Assert.Equal(setup.Executables["wt"], request.ExecutablePath);
		Assert.Equal(["-d", projectRoot, setup.Executables[commandName]], request.Arguments);
		Assert.Equal(projectRoot, request.WorkingDirectory);
		AssertObservesDispatcher(request);
		Assert.Empty(setup.ExternalLinkLauncher.Urls);
	}

	[Fact]
	public async Task Open_WindowsFallsBackToCommandProcessorWhenWindowsTerminalIsMissing()
	{
		using var project = new TemporaryDirectory();
		var projectRoot = project.CreateFolder("project ! caret ^ with spaces Пример");
		var setup = CreateService(
			TerminalCommandHostPlatform.Windows,
			availableCommands: ["claude"],
			windowsCommandProcessor: @"C:\Windows\System32\cmd.exe");

		var result = await setup.Service.OpenAsync(
			new McpClientLaunchRequest(McpConnectionClient.ClaudeCode, projectRoot),
			TestContext.Current.CancellationToken);

		Assert.True(result.Succeeded);
		var request = Assert.Single(setup.ProcessRunner.Requests);
		Assert.Equal(@"C:\Windows\System32\cmd.exe", request.ExecutablePath);
		Assert.Empty(request.Arguments);
		Assert.Contains("/c start \"\"", request.RawArguments, StringComparison.Ordinal);
		Assert.Contains("/k \"\"%DEVPROJEX_MCP_CLIENT%\"\"", request.RawArguments, StringComparison.Ordinal);
		Assert.Equal(setup.Executables["claude"], request.EnvironmentVariables!["DEVPROJEX_MCP_CLIENT"]);
		Assert.Equal(projectRoot, request.EnvironmentVariables["DEVPROJEX_MCP_PROJECT_ROOT"]);
		AssertObservesDispatcher(request);
		Assert.True(request.CreateNoWindow);
		Assert.Equal(projectRoot, request.WorkingDirectory);
	}

	[Fact]
	public async Task Open_WindowsTerminalRunsCommandShimThroughCommandProcessor()
	{
		using var project = new TemporaryDirectory();
		var projectRoot = project.CreateFolder("project ! caret ^ Пример");
		var toolsDirectory = project.CreateFolder("client ! tools ^ Пример");
		var claude = Path.Combine(toolsDirectory, "claude.cmd");
		var windowsTerminal = Path.Combine(toolsDirectory, "wt.exe");
		var locator = new McpClientExecutableLocator(new McpClientExecutableLocatorOptions
		{
			Platform = TerminalCommandHostPlatform.Windows,
			PathVariableProvider = () => toolsDirectory,
			PathExtensionsProvider = () => ".CMD;.EXE",
			FileExists = candidate => string.Equals(candidate, claude, StringComparison.OrdinalIgnoreCase) ||
									  string.Equals(candidate, windowsTerminal, StringComparison.OrdinalIgnoreCase)
		});
		var processRunner = new RecordingLaunchProcessRunner([]);
		var service = new McpClientLaunchService(
			CreateLocalization(),
			locator,
			processRunner,
			new RecordingExternalLinkLauncher([]),
			new McpClientLaunchServiceOptions
			{
				Platform = TerminalCommandHostPlatform.Windows,
				WindowsCommandProcessorProvider = () => @"C:\Windows\System32\cmd.exe"
			});

		var result = await service.OpenAsync(
			new McpClientLaunchRequest(McpConnectionClient.ClaudeCode, projectRoot),
			TestContext.Current.CancellationToken);

		Assert.True(result.Succeeded);
		var request = Assert.Single(processRunner.Requests);
		Assert.Equal(windowsTerminal, request.ExecutablePath);
		Assert.Equal(
			[
				"-d", projectRoot, @"C:\Windows\System32\cmd.exe",
				"/d", "/v:off", "/s", "/k", "\"%DEVPROJEX_MCP_CLIENT%\""
			],
			request.Arguments);
		Assert.Equal(claude, request.EnvironmentVariables!["DEVPROJEX_MCP_CLIENT"]);
		AssertObservesDispatcher(request);
	}

	[Fact]
	public async Task Open_MacUsesTerminalAppleScriptWithoutEmbeddingPathsInScript()
	{
		using var project = new TemporaryDirectory();
		var projectRoot = project.CreateFolder("project with spaces Пример");
		var setup = CreateService(
			TerminalCommandHostPlatform.MacOS,
			availableCommands: ["codex", "osascript"]);

		var result = await setup.Service.OpenAsync(
			new McpClientLaunchRequest(McpConnectionClient.Codex, projectRoot),
			TestContext.Current.CancellationToken);

		Assert.True(result.Succeeded);
		var request = Assert.Single(setup.ProcessRunner.Requests);
		Assert.Equal(setup.Executables["osascript"], request.ExecutablePath);
		Assert.Equal("-e", request.Arguments[0]);
		Assert.Contains("quoted form of projectRoot", request.Arguments[1], StringComparison.Ordinal);
		Assert.Contains("quoted form of clientPath", request.Arguments[1], StringComparison.Ordinal);
		Assert.DoesNotContain(projectRoot, request.Arguments[1], StringComparison.Ordinal);
		Assert.Equal(projectRoot, request.Arguments[2]);
		Assert.Equal(setup.Executables["codex"], request.Arguments[3]);
		Assert.Equal(projectRoot, request.WorkingDirectory);
		AssertObservesDispatcher(request);
		Assert.True(request.CreateNoWindow);
	}

	[Fact]
	public async Task Open_MacReportsEarlyAppleScriptFailure()
	{
		using var project = new TemporaryDirectory();
		var setup = CreateService(
			TerminalCommandHostPlatform.MacOS,
			availableCommands: ["codex", "osascript"],
			processResults: [new McpClientLaunchAttemptResult(false, "Terminal rejected the script")]);

		var result = await setup.Service.OpenAsync(
			new McpClientLaunchRequest(McpConnectionClient.Codex, project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpClientLaunchStatus.Failed, result.Status);
		Assert.Equal("Terminal rejected the script", result.ErrorMessage);
		Assert.Contains(project.Path, result.ManualCommand, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData((int)TerminalCommandHostPlatform.Windows, "wt")]
	[InlineData((int)TerminalCommandHostPlatform.Linux, "xterm")]
	public async Task Open_TerminalDispatcherReportsEarlyNonZeroExit(
		int platformValue,
		string dispatcherCommand)
	{
		using var project = new TemporaryDirectory();
		var setup = CreateService(
			(TerminalCommandHostPlatform)platformValue,
			availableCommands: ["claude", dispatcherCommand],
			processResults:
			[
				new McpClientLaunchAttemptResult(
					false,
					Failure: McpClientLaunchFailure.DispatcherExited,
					ExitCode: 23)
			]);

		var result = await setup.Service.OpenAsync(
			new McpClientLaunchRequest(McpConnectionClient.ClaudeCode, project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpClientLaunchStatus.Failed, result.Status);
		Assert.Equal("The client launcher exited with code 23.", result.ErrorMessage);
		AssertObservesDispatcher(Assert.Single(setup.ProcessRunner.Requests));
	}

	[Theory]
	[InlineData("x-terminal-emulator", "-e")]
	[InlineData("gnome-terminal", "--")]
	[InlineData("konsole", "-e")]
	[InlineData("xfce4-terminal", "--execute")]
	[InlineData("xterm", "-e")]
	public async Task Open_LinuxUsesFirstAvailableTerminalWithLiteralClientArgument(
		string terminalCommand,
		string executeArgument)
	{
		using var project = new TemporaryDirectory();
		var projectRoot = project.CreateFolder("project with spaces Пример");
		var setup = CreateService(
			TerminalCommandHostPlatform.Linux,
			availableCommands: ["claude", terminalCommand]);

		var result = await setup.Service.OpenAsync(
			new McpClientLaunchRequest(McpConnectionClient.ClaudeCode, projectRoot),
			TestContext.Current.CancellationToken);

		Assert.True(result.Succeeded);
		var request = Assert.Single(setup.ProcessRunner.Requests);
		Assert.Equal(setup.Executables[terminalCommand], request.ExecutablePath);
		Assert.Equal([executeArgument, setup.Executables["claude"]], request.Arguments);
		Assert.Equal(projectRoot, request.WorkingDirectory);
		AssertObservesDispatcher(request);
	}

	[Fact]
	public async Task Open_LinuxPrefersTerminalsInDocumentedOrder()
	{
		using var project = new TemporaryDirectory();
		var setup = CreateService(
			TerminalCommandHostPlatform.Linux,
			availableCommands: ["codex", "xterm", "konsole", "gnome-terminal"]);

		var result = await setup.Service.OpenAsync(
			new McpClientLaunchRequest(McpConnectionClient.Codex, project.Path),
			TestContext.Current.CancellationToken);

		Assert.True(result.Succeeded);
		Assert.Equal(
			setup.Executables["gnome-terminal"],
			Assert.Single(setup.ProcessRunner.Requests).ExecutablePath);
	}

	[Fact]
	public async Task Open_LinuxTriesTheNextAvailableTerminalAfterAnEarlyFailure()
	{
		using var project = new TemporaryDirectory();
		var setup = CreateService(
			TerminalCommandHostPlatform.Linux,
			availableCommands: ["codex", "x-terminal-emulator", "gnome-terminal"],
			processResults:
			[
				new McpClientLaunchAttemptResult(false, "first terminal unavailable"),
				new McpClientLaunchAttemptResult(true)
			]);

		var result = await setup.Service.OpenAsync(
			new McpClientLaunchRequest(McpConnectionClient.Codex, project.Path),
			TestContext.Current.CancellationToken);

		Assert.True(result.Succeeded);
		Assert.Equal(
			[
				setup.Executables["x-terminal-emulator"],
				setup.Executables["gnome-terminal"]
			],
			setup.ProcessRunner.Requests.Select(static request => request.ExecutablePath));
	}

	[Theory]
	[InlineData((int)McpConnectionClient.Cursor, "cursor", "cursor")]
	[InlineData((int)McpConnectionClient.VsCode, "vscode", "code")]
	public async Task Open_EditorUsesEscapedProjectFolderUrlBeforeCli(
		int clientValue,
		string scheme,
		string commandName)
	{
		using var project = new TemporaryDirectory();
		var projectRoot = project.CreateFolder("project with spaces Пример");
		var setup = CreateService(
			TerminalCommandHostPlatform.Windows,
			availableCommands: [commandName]);

		var result = await setup.Service.OpenAsync(
			new McpClientLaunchRequest((McpConnectionClient)clientValue, projectRoot),
			TestContext.Current.CancellationToken);

		Assert.True(result.Succeeded);
		Assert.Empty(setup.ProcessRunner.Requests);
		var url = Assert.Single(setup.ExternalLinkLauncher.Urls);
		Assert.StartsWith($"{scheme}://file/", url, StringComparison.Ordinal);
		Assert.EndsWith("/", url, StringComparison.Ordinal);
		Assert.Contains("%20", url, StringComparison.Ordinal);
		Assert.DoesNotContain(" ", url, StringComparison.Ordinal);
		Assert.DoesNotContain("Пример", url, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Open_EditorUrlRoundTripsReservedCharactersAndUnicode()
	{
		using var project = new TemporaryDirectory();
		var projectRoot = project.CreateFolder("project # 100% Пример");
		var setup = CreateService(TerminalCommandHostPlatform.Linux, availableCommands: []);

		var result = await setup.Service.OpenAsync(
			new McpClientLaunchRequest(McpConnectionClient.VsCode, projectRoot),
			TestContext.Current.CancellationToken);

		Assert.True(result.Succeeded);
		var url = Assert.Single(setup.ExternalLinkLauncher.Urls);
		var uri = new Uri(url, UriKind.Absolute);
		var expectedPath = projectRoot.Replace('\\', '/').TrimEnd('/') + "/";
		if (!expectedPath.StartsWith('/'))
			expectedPath = "/" + expectedPath;
		Assert.Equal(expectedPath, Uri.UnescapeDataString(uri.AbsolutePath));
		Assert.Equal(string.Empty, uri.Fragment);
		Assert.Equal(string.Empty, uri.Query);
		Assert.Contains("%23", url, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("%25", url, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task Open_EditorUrlRoundTripsUncProjectRoot()
	{
		if (!OperatingSystem.IsWindows())
			return;

		const string projectRoot = @"\\server\share\project # Пример";
		var setup = CreateService(TerminalCommandHostPlatform.Windows, availableCommands: []);

		var result = await setup.Service.OpenAsync(
			new McpClientLaunchRequest(McpConnectionClient.Cursor, projectRoot),
			TestContext.Current.CancellationToken);

		Assert.True(result.Succeeded);
		var uri = new Uri(Assert.Single(setup.ExternalLinkLauncher.Urls), UriKind.Absolute);
		Assert.Equal(
			"//server/share/project # Пример/",
			Uri.UnescapeDataString(uri.AbsolutePath));
	}

	[Theory]
	[InlineData((int)McpConnectionClient.Cursor, "cursor", (int)TerminalCommandHostPlatform.Windows)]
	[InlineData((int)McpConnectionClient.VsCode, "code", (int)TerminalCommandHostPlatform.Windows)]
	[InlineData((int)McpConnectionClient.Cursor, "cursor", (int)TerminalCommandHostPlatform.Linux)]
	[InlineData((int)McpConnectionClient.VsCode, "code", (int)TerminalCommandHostPlatform.Linux)]
	public async Task Open_EditorFallsBackToResolvedCliWhenUrlHandlerFails(
		int clientValue,
		string commandName,
		int platformValue)
	{
		using var project = new TemporaryDirectory();
		var projectRoot = project.CreateFolder("project with spaces Пример");
		var setup = CreateService(
			(TerminalCommandHostPlatform)platformValue,
			availableCommands: [commandName],
			externalLinkResults: [new McpClientLaunchAttemptResult(false, "no URL handler")]);

		var result = await setup.Service.OpenAsync(
			new McpClientLaunchRequest((McpConnectionClient)clientValue, projectRoot),
			TestContext.Current.CancellationToken);

		Assert.True(result.Succeeded);
		Assert.Single(setup.ExternalLinkLauncher.Urls);
		var request = Assert.Single(setup.ProcessRunner.Requests);
		Assert.Equal(setup.Executables[commandName], request.ExecutablePath);
		Assert.Equal([projectRoot], request.Arguments);
		Assert.Equal(projectRoot, request.WorkingDirectory);
		AssertObservesDispatcher(request);
	}

	[Theory]
	[InlineData((int)McpConnectionClient.Cursor, "cursor")]
	[InlineData((int)McpConnectionClient.VsCode, "code")]
	public async Task Open_EditorWindowsCommandShimUsesHiddenRawCommandProcessor(
		int clientValue,
		string commandName)
	{
		using var project = new TemporaryDirectory();
		var projectRoot = project.CreateFolder("project 100% ! caret ^ Пример");
		var toolsDirectory = project.CreateFolder("client ! tools ^ Пример");
		var clientShim = Path.Combine(toolsDirectory, commandName + ".cmd");
		var locator = new McpClientExecutableLocator(new McpClientExecutableLocatorOptions
		{
			Platform = TerminalCommandHostPlatform.Windows,
			PathVariableProvider = () => toolsDirectory,
			PathExtensionsProvider = () => ".CMD",
			FileExists = candidate => string.Equals(candidate, clientShim, StringComparison.OrdinalIgnoreCase)
		});
		var processRunner = new RecordingLaunchProcessRunner([]);
		var service = new McpClientLaunchService(
			CreateLocalization(),
			locator,
			processRunner,
			new RecordingExternalLinkLauncher([new McpClientLaunchAttemptResult(false, "URL failed")]),
			new McpClientLaunchServiceOptions
			{
				Platform = TerminalCommandHostPlatform.Windows,
				WindowsCommandProcessorProvider = () => @"C:\Windows\System32\cmd.exe"
			});

		var result = await service.OpenAsync(
			new McpClientLaunchRequest((McpConnectionClient)clientValue, projectRoot),
			TestContext.Current.CancellationToken);

		Assert.True(result.Succeeded);
		var request = Assert.Single(processRunner.Requests);
		Assert.Equal(@"C:\Windows\System32\cmd.exe", request.ExecutablePath);
		Assert.Empty(request.Arguments);
		Assert.Contains("/c \"\"%DEVPROJEX_MCP_CLIENT%\"", request.RawArguments, StringComparison.Ordinal);
		Assert.Equal(clientShim, request.EnvironmentVariables!["DEVPROJEX_MCP_CLIENT"]);
		Assert.Equal(projectRoot, request.EnvironmentVariables["DEVPROJEX_MCP_PROJECT_ROOT"]);
		AssertObservesDispatcher(request);
		Assert.True(request.CreateNoWindow);
		Assert.False(request.CaptureDispatcherOutput);
	}

	[Theory]
	[InlineData((int)McpConnectionClient.Cursor, "cursor")]
	[InlineData((int)McpConnectionClient.VsCode, "code")]
	public async Task Open_EditorWindowsCommandShimRoundTripsProjectRootThroughRealCmd(
		int clientValue,
		string commandName)
	{
		if (!OperatingSystem.IsWindows())
			return;

		using var project = new TemporaryDirectory();
		var projectRoot = project.CreateFolder("project 100% ! caret ^ Пример");
		var toolsDirectory = project.CreateFolder("client ! tools ^ Пример");
		var clientShim = Path.Combine(toolsDirectory, commandName + ".cmd");
		var capturedPath = Path.Combine(toolsDirectory, "captured.txt");
		await File.WriteAllTextAsync(
			clientShim,
			"@echo off\r\n" +
			"chcp 65001 >nul\r\n" +
			"> \"%~dp0captured.tmp\" <nul set /p \"=%~1\"\r\n" +
			"move /y \"%~dp0captured.tmp\" \"%~dp0captured.txt\" >nul\r\n" +
			"exit /b 0\r\n",
			TestContext.Current.CancellationToken);
		var captureReady = new TaskCompletionSource(
			TaskCreationOptions.RunContinuationsAsynchronously);
		using var captureWatcher = new FileSystemWatcher(
			toolsDirectory,
			Path.GetFileName(capturedPath))
		{
			EnableRaisingEvents = true
		};
		captureWatcher.Created += (_, _) => captureReady.TrySetResult();
		captureWatcher.Renamed += (_, _) => captureReady.TrySetResult();
		var locator = new McpClientExecutableLocator(new McpClientExecutableLocatorOptions
		{
			Platform = TerminalCommandHostPlatform.Windows,
			PathVariableProvider = () => toolsDirectory,
			PathExtensionsProvider = () => ".CMD",
			FileExists = File.Exists
		});
		var service = new McpClientLaunchService(
			CreateLocalization(),
			locator,
			new McpClientLaunchProcessRunner(),
			new RecordingExternalLinkLauncher([new McpClientLaunchAttemptResult(false, "URL failed")]),
			new McpClientLaunchServiceOptions
			{
				Platform = TerminalCommandHostPlatform.Windows,
				WindowsCommandProcessorProvider = () =>
					Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
			});

		var result = await service.OpenAsync(
			new McpClientLaunchRequest((McpConnectionClient)clientValue, projectRoot),
			TestContext.Current.CancellationToken);

		Assert.True(result.Succeeded, result.ErrorMessage);
		if (File.Exists(capturedPath))
			captureReady.TrySetResult();
		await captureReady.Task.WaitAsync(
			TimeSpan.FromSeconds(10),
			TestContext.Current.CancellationToken);
		Assert.Equal(projectRoot, await File.ReadAllTextAsync(
			capturedPath,
			TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task Open_EditorReportsBothUrlAndCliFailuresWithManualCommand()
	{
		using var project = new TemporaryDirectory();
		var projectRoot = project.CreateFolder("project with spaces Пример");
		var setup = CreateService(
			TerminalCommandHostPlatform.Linux,
			availableCommands: ["code"],
			processResults: [new McpClientLaunchAttemptResult(false, "CLI failed")],
			externalLinkResults: [new McpClientLaunchAttemptResult(false, "URL failed")]);

		var result = await setup.Service.OpenAsync(
			new McpClientLaunchRequest(McpConnectionClient.VsCode, projectRoot),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpClientLaunchStatus.Failed, result.Status);
		Assert.Equal("URL failed CLI failed", result.ErrorMessage);
		Assert.Equal($"'{setup.Executables["code"]}' '{projectRoot}'", result.ManualCommand);
	}

	[Fact]
	public async Task Open_EditorUsesFallbackErrorWhenLaunchersReturnNoDetail()
	{
		using var project = new TemporaryDirectory();
		var setup = CreateService(
			TerminalCommandHostPlatform.Linux,
			availableCommands: ["cursor"],
			processResults: [new McpClientLaunchAttemptResult(false)],
			externalLinkResults: [new McpClientLaunchAttemptResult(false)]);

		var result = await setup.Service.OpenAsync(
			new McpClientLaunchRequest(McpConnectionClient.Cursor, project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpClientLaunchStatus.Failed, result.Status);
		Assert.Equal("The URL handler and client command did not start.", result.ErrorMessage);
	}

	[Fact]
	public async Task Open_EditorReportsMissingCliAfterUrlFailure()
	{
		using var project = new TemporaryDirectory();
		var setup = CreateService(
			TerminalCommandHostPlatform.Windows,
			availableCommands: [],
			externalLinkResults: [new McpClientLaunchAttemptResult(false, "URL failed")]);

		var result = await setup.Service.OpenAsync(
			new McpClientLaunchRequest(McpConnectionClient.Cursor, project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpClientLaunchStatus.ClientNotFound, result.Status);
		Assert.Contains("URL failed", result.ErrorMessage, StringComparison.Ordinal);
		Assert.Contains("not found on PATH", result.ErrorMessage, StringComparison.Ordinal);
		Assert.Equal($"\"cursor\" \"{project.Path}\"", result.ManualCommand);
		Assert.Empty(setup.ProcessRunner.Requests);
	}

	[Fact]
	public async Task Open_TerminalClientReportsMissingExecutableWithoutStartingTerminal()
	{
		using var project = new TemporaryDirectory();
		var setup = CreateService(
			TerminalCommandHostPlatform.Linux,
			availableCommands: ["xterm"]);

		var result = await setup.Service.OpenAsync(
			new McpClientLaunchRequest(McpConnectionClient.Codex, project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpClientLaunchStatus.ClientNotFound, result.Status);
		Assert.Equal($"cd '{project.Path}' && exec 'codex'", result.ManualCommand);
		Assert.Empty(setup.ProcessRunner.Requests);
	}

	[Fact]
	public async Task Open_TerminalClientReportsMissingTerminalWithResolvedManualCommand()
	{
		using var project = new TemporaryDirectory();
		var setup = CreateService(
			TerminalCommandHostPlatform.Linux,
			availableCommands: ["claude"]);

		var result = await setup.Service.OpenAsync(
			new McpClientLaunchRequest(McpConnectionClient.ClaudeCode, project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpClientLaunchStatus.Failed, result.Status);
		Assert.Contains("terminal", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
		Assert.Equal($"cd '{project.Path}' && exec '{setup.Executables["claude"]}'", result.ManualCommand);
		Assert.Empty(setup.ProcessRunner.Requests);
	}

	[Theory]
	[InlineData((int)AppLanguage.En, "Claude Code was not found on PATH.")]
	[InlineData((int)AppLanguage.Ru, "Claude Code не найден в PATH.")]
	public async Task Open_ClientNotFoundUsesSelectedLanguage(int languageValue, string expected)
	{
		using var project = new TemporaryDirectory();
		var setup = CreateService(
			TerminalCommandHostPlatform.Linux,
			availableCommands: [],
			language: (AppLanguage)languageValue);

		var result = await setup.Service.OpenAsync(
			new McpClientLaunchRequest(McpConnectionClient.ClaudeCode, project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(expected, result.ErrorMessage);
	}

	[Theory]
	[InlineData((int)AppLanguage.En, "No supported terminal application was found.")]
	[InlineData((int)AppLanguage.Ru, "Поддерживаемое приложение терминала не найдено.")]
	public async Task Open_TerminalNotFoundUsesSelectedLanguage(int languageValue, string expected)
	{
		using var project = new TemporaryDirectory();
		var setup = CreateService(
			TerminalCommandHostPlatform.Linux,
			availableCommands: ["codex"],
			language: (AppLanguage)languageValue);

		var result = await setup.Service.OpenAsync(
			new McpClientLaunchRequest(McpConnectionClient.Codex, project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(expected, result.ErrorMessage);
	}

	[Theory]
	[InlineData((int)AppLanguage.En, "The client launcher did not finish within 5 seconds.")]
	[InlineData((int)AppLanguage.Ru, "Средство запуска клиента не завершилось за 5 с.")]
	public async Task Open_DispatcherTimeoutFallbackUsesSelectedLanguage(int languageValue, string expected)
	{
		using var project = new TemporaryDirectory();
		var setup = CreateService(
			TerminalCommandHostPlatform.Linux,
			availableCommands: ["codex", "xterm"],
			processResults:
			[
				new McpClientLaunchAttemptResult(
					false,
					Failure: McpClientLaunchFailure.DispatcherTimedOut,
					TimeoutSeconds: 5)
			],
			language: (AppLanguage)languageValue);

		var result = await setup.Service.OpenAsync(
			new McpClientLaunchRequest(McpConnectionClient.Codex, project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(expected, result.ErrorMessage);
	}

	[Theory]
	[InlineData((int)AppLanguage.En, "The client launcher exited with code 23.")]
	[InlineData((int)AppLanguage.Ru, "Средство запуска клиента завершилось с кодом 23.")]
	public async Task Open_DispatcherExitFallbackUsesSelectedLanguage(int languageValue, string expected)
	{
		using var project = new TemporaryDirectory();
		var setup = CreateService(
			TerminalCommandHostPlatform.Linux,
			availableCommands: ["claude", "xterm"],
			processResults:
			[
				new McpClientLaunchAttemptResult(
					false,
					Failure: McpClientLaunchFailure.DispatcherExited,
					ExitCode: 23)
			],
			language: (AppLanguage)languageValue);

		var result = await setup.Service.OpenAsync(
			new McpClientLaunchRequest(McpConnectionClient.ClaudeCode, project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(expected, result.ErrorMessage);
	}

	[Fact]
	public async Task Open_JsonReportsUnsupportedWithoutLaunchingAnything()
	{
		using var project = new TemporaryDirectory();
		var setup = CreateService(TerminalCommandHostPlatform.Windows, availableCommands: []);

		var result = await setup.Service.OpenAsync(
			new McpClientLaunchRequest(McpConnectionClient.Json, project.Path),
			TestContext.Current.CancellationToken);

		Assert.Equal(McpClientLaunchStatus.UnsupportedClient, result.Status);
		Assert.Empty(setup.ProcessRunner.Requests);
		Assert.Empty(setup.ExternalLinkLauncher.Urls);
	}

	[Fact]
	public async Task Open_RejectsRelativeProjectRoot()
	{
		var setup = CreateService(TerminalCommandHostPlatform.Windows, availableCommands: []);

		await Assert.ThrowsAsync<ArgumentException>(() => setup.Service.OpenAsync(
			new McpClientLaunchRequest(McpConnectionClient.Cursor, "relative/project"),
			TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task Open_CancellationStopsBeforeTryingUrlHandler()
	{
		using var project = new TemporaryDirectory();
		var setup = CreateService(TerminalCommandHostPlatform.Windows, availableCommands: []);
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.Service.OpenAsync(
			new McpClientLaunchRequest(McpConnectionClient.Cursor, project.Path),
			cancellation.Token));

		Assert.Empty(setup.ExternalLinkLauncher.Urls);
		Assert.Empty(setup.ProcessRunner.Requests);
	}

	[Fact]
	public async Task ProcessRunner_WindowsRawCommandPreservesQuotedUnicodePaths()
	{
		if (!OperatingSystem.IsWindows())
			return;

		using var project = new TemporaryDirectory();
		var projectRoot = project.CreateFolder("cmd 100% Пример");
		var scriptPath = Path.Combine(projectRoot, "write result.cmd");
		var outputPath = Path.Combine(projectRoot, "result file.txt");
		await File.WriteAllTextAsync(
			scriptPath,
			"@echo off\r\n> \"%~1\" <nul set /p \"=parsed\"\r\nexit /b 0\r\n",
			TestContext.Current.CancellationToken);
		var commandProcessor = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
		var runner = new McpClientLaunchProcessRunner();

		var result = await runner.StartAsync(
			new McpClientLaunchProcessRequest(
				commandProcessor,
				[],
				projectRoot,
				RawArguments: "/d /v:off /s /c \"\"%DEVPROJEX_TEST_CLIENT%\" \"%DEVPROJEX_TEST_OUTPUT%\"\"",
				EnvironmentVariables: new Dictionary<string, string>
				{
					["DEVPROJEX_TEST_CLIENT"] = scriptPath,
					["DEVPROJEX_TEST_OUTPUT"] = outputPath
				},
				WaitForDispatcher: true,
				DispatcherTimeout: TimeSpan.FromSeconds(5),
				CreateNoWindow: true),
			TestContext.Current.CancellationToken);

		Assert.True(result.Succeeded, result.ErrorMessage);
		Assert.Equal("parsed", await File.ReadAllTextAsync(
			outputPath,
			TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task ProcessRunner_WindowsReportsNonZeroDispatcherExitAndStderr()
	{
		if (!OperatingSystem.IsWindows())
			return;

		using var project = new TemporaryDirectory();
		var scriptPath = project.CreateFile(
			"fail dispatcher.cmd",
			"@echo off\r\n>&2 echo dispatcher failed\r\nexit /b 23\r\n");
		var commandProcessor = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
		var runner = new McpClientLaunchProcessRunner();

		var result = await runner.StartAsync(
			new McpClientLaunchProcessRequest(
				commandProcessor,
				[],
				project.Path,
				RawArguments: "/d /v:off /s /c \"\"%DEVPROJEX_TEST_CLIENT%\"\"",
				EnvironmentVariables: new Dictionary<string, string>
				{
					["DEVPROJEX_TEST_CLIENT"] = scriptPath
				},
				WaitForDispatcher: true,
				DispatcherTimeout: TimeSpan.FromSeconds(5),
				CreateNoWindow: true),
			TestContext.Current.CancellationToken);

		Assert.False(result.Succeeded);
		Assert.Contains("dispatcher failed", result.ErrorMessage, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ProcessRunner_OutputCloseIsBoundedWhenReaderWaitsForCancellation()
	{
		using var cancellation = new CancellationTokenSource();
		var completion = new TaskCompletionSource<BoundedTextReadResult>(
			TaskCreationOptions.RunContinuationsAsynchronously);
		using var registration = cancellation.Token.Register(
			() => completion.TrySetCanceled(cancellation.Token));

		var result = await McpClientLaunchProcessRunner.ReadCompletedOutputAsync(
			completion.Task,
			cancellation,
			TimeSpan.FromMilliseconds(10));

		Assert.False(result.Completed);
		Assert.True(cancellation.IsCancellationRequested);
	}

	[Fact]
	public async Task ExternalLinkLauncherObservesDispatcherOnEveryPlatform()
	{
		using var project = new TemporaryDirectory();
		var runner = new RecordingLaunchProcessRunner(
			[new McpClientLaunchAttemptResult(false, "dispatcher failed")]);
		var launcher = new McpClientExternalLinkLauncher(runner);

		var result = await launcher.TryOpenAsync(
			"vscode://file/project/",
			project.Path,
			TestContext.Current.CancellationToken);

		Assert.False(result.Succeeded);
		var request = Assert.Single(runner.Requests);
		Assert.True(request.UseShellExecute);
		AssertObservesDispatcher(request);
		Assert.False(request.CaptureDispatcherOutput);
	}

	private static void AssertObservesDispatcher(McpClientLaunchProcessRequest request)
	{
		Assert.True(request.WaitForDispatcher);
		Assert.Equal(McpClientLaunchTiming.DispatcherObservationTimeout, request.DispatcherTimeout);
		Assert.True(request.DispatcherTimeoutMeansSuccess);
		Assert.False(request.TerminateOnCancellation);
	}

	private static LaunchServiceSetup CreateService(
		TerminalCommandHostPlatform platform,
		IReadOnlyList<string> availableCommands,
		string? windowsCommandProcessor = null,
		IReadOnlyList<McpClientLaunchAttemptResult>? processResults = null,
		IReadOnlyList<McpClientLaunchAttemptResult>? externalLinkResults = null,
		AppLanguage language = AppLanguage.En)
	{
		var pathEntry = platform == TerminalCommandHostPlatform.Windows
			? Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mcp launch tools Пример"))
			: "/mcp-launch-tools-Пример";
		var extension = platform == TerminalCommandHostPlatform.Windows ? ".exe" : string.Empty;
		var executables = availableCommands
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToDictionary(
				static command => command,
				command => Path.GetFullPath(Path.Combine(pathEntry, command + extension)),
				StringComparer.OrdinalIgnoreCase);
		var locator = new McpClientExecutableLocator(new McpClientExecutableLocatorOptions
		{
			Platform = platform,
			PathVariableProvider = () => pathEntry,
			PathExtensionsProvider = () => ".EXE;.CMD;.BAT",
			FileExists = candidate => executables.Values.Contains(
				candidate,
				StringComparer.OrdinalIgnoreCase),
			IsExecutable = _ => true
		});
		var processRunner = new RecordingLaunchProcessRunner(processResults ?? []);
		var externalLinkLauncher = new RecordingExternalLinkLauncher(externalLinkResults ?? []);
		var service = new McpClientLaunchService(
			CreateLocalization(language),
			locator,
			processRunner,
			externalLinkLauncher,
			new McpClientLaunchServiceOptions
			{
				Platform = platform,
				WindowsCommandProcessorProvider = () => windowsCommandProcessor
			});
		return new LaunchServiceSetup(service, processRunner, externalLinkLauncher, executables);
	}

	private static LocalizationService CreateLocalization(AppLanguage language = AppLanguage.En)
	{
		var english = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["Mcp.Open.ClientNotFound"] = "{0} was not found on PATH.",
			["Mcp.Open.TerminalNotFound"] = "No supported terminal application was found.",
			["Mcp.Open.ProcessStartFailed"] = "The client process did not start.",
			["Mcp.Open.DispatcherTimedOut"] = "The client launcher did not finish within {0} seconds.",
			["Mcp.Open.DispatcherExited"] = "The client launcher exited with code {0}.",
			["Mcp.Open.TerminalStartFailed"] = "The terminal application did not start.",
			["Mcp.Open.EditorStartFailed"] = "The URL handler and client command did not start.",
			["Mcp.Open.ManualJsonUnsupported"] = "Manual JSON configurations cannot be opened automatically."
		};
		var russian = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["Mcp.Open.ClientNotFound"] = "{0} не найден в PATH.",
			["Mcp.Open.TerminalNotFound"] = "Поддерживаемое приложение терминала не найдено.",
			["Mcp.Open.ProcessStartFailed"] = "Не удалось запустить процесс клиента.",
			["Mcp.Open.DispatcherTimedOut"] = "Средство запуска клиента не завершилось за {0} с.",
			["Mcp.Open.DispatcherExited"] = "Средство запуска клиента завершилось с кодом {0}.",
			["Mcp.Open.TerminalStartFailed"] = "Не удалось запустить приложение терминала.",
			["Mcp.Open.EditorStartFailed"] = "Не удалось запустить обработчик URL и команду клиента.",
			["Mcp.Open.ManualJsonUnsupported"] = "Ручную конфигурацию JSON нельзя открыть автоматически."
		};
		return new LocalizationService(
			new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
			{
				[AppLanguage.En] = english,
				[AppLanguage.Ru] = russian
			}),
			language);
	}

	private sealed record LaunchServiceSetup(
		McpClientLaunchService Service,
		RecordingLaunchProcessRunner ProcessRunner,
		RecordingExternalLinkLauncher ExternalLinkLauncher,
		IReadOnlyDictionary<string, string> Executables);

	private sealed class RecordingLaunchProcessRunner(
		IReadOnlyList<McpClientLaunchAttemptResult> results) : IMcpClientLaunchProcessRunner
	{
		private readonly Queue<McpClientLaunchAttemptResult> _results = new(results);

		public List<McpClientLaunchProcessRequest> Requests { get; } = [];

		public Task<McpClientLaunchAttemptResult> StartAsync(
			McpClientLaunchProcessRequest request,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Requests.Add(request);
			return Task.FromResult(_results.Count == 0
				? new McpClientLaunchAttemptResult(true)
				: _results.Dequeue());
		}
	}

	private sealed class RecordingExternalLinkLauncher(
		IReadOnlyList<McpClientLaunchAttemptResult> results) : IMcpClientExternalLinkLauncher
	{
		private readonly Queue<McpClientLaunchAttemptResult> _results = new(results);

		public List<string> Urls { get; } = [];

		public Task<McpClientLaunchAttemptResult> TryOpenAsync(
			string url,
			string workingDirectory,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Urls.Add(url);
			return Task.FromResult(_results.Count == 0
				? new McpClientLaunchAttemptResult(true)
				: _results.Dequeue());
		}
	}
}
