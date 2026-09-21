using DevProjex.Kernel.Abstractions;
using DevProjex.Infrastructure.ResourceStore;

namespace DevProjex.Tests.Terminal;

public sealed class McpConnectionCommandTests
{
	[Fact]
	public async Task LiveConnectionStopsWhenSelectionPersistenceFailsAndRetryIsCanceled()
	{
		var releasePersistence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var persistenceStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var connectionStarted = false;

		var operation = TerminalWorkspaceSession.RunAfterSelectionPersistenceAsync(
			McpConnectionMode.Live,
			async cancellationToken =>
			{
				persistenceStarted.TrySetResult();
				await releasePersistence.Task.WaitAsync(cancellationToken);
				return false;
			},
			() => Task.FromResult(false),
			_ =>
			{
				connectionStarted = true;
				return Task.FromResult("connected");
			},
			TestContext.Current.CancellationToken);

		await persistenceStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
		Assert.False(connectionStarted);
		releasePersistence.TrySetResult();

		Assert.Null(await operation);
		Assert.False(connectionStarted);
	}

	[Fact]
	public async Task LiveConnectionRetriesSelectionPersistenceBeforeConnecting()
	{
		var flushAttempts = 0;
		var retryPrompts = 0;

		var result = await TerminalWorkspaceSession.RunAfterSelectionPersistenceAsync(
			McpConnectionMode.Live,
			_ => Task.FromResult(++flushAttempts == 2),
			() =>
			{
				retryPrompts++;
				return Task.FromResult(true);
			},
			_ => Task.FromResult("connected"),
			TestContext.Current.CancellationToken);

		Assert.Equal("connected", result);
		Assert.Equal(2, flushAttempts);
		Assert.Equal(1, retryPrompts);
	}

	[Fact]
	public async Task StandardConnectionDoesNotFlushSelectionPersistence()
	{
		var flushCalled = false;

		var result = await TerminalWorkspaceSession.RunAfterSelectionPersistenceAsync(
			McpConnectionMode.Standard,
			_ =>
			{
				flushCalled = true;
				return Task.FromResult(true);
			},
			() => Task.FromResult(false),
			_ => Task.FromResult("connected"),
			TestContext.Current.CancellationToken);

		Assert.Equal("connected", result);
		Assert.False(flushCalled);
	}

	[Fact]
	public void TuiConnectionOutput_PrintsTheManualNextStepSeparately()
	{
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);
		var output = TerminalWorkspaceSession.BuildMcpConnectionOutput(
			new McpConnectionResult(
				McpConnectionStatus.Updated,
				"Codex connection updated.",
				NextCommand: "codex",
				CommandOutput: "get: old registration\nremove: removed\nadd: connected"),
			localization);

		Assert.StartsWith("Codex connection updated.", output, StringComparison.Ordinal);
		Assert.Contains("Run codex in the project folder.", output, StringComparison.Ordinal);
		Assert.DoesNotContain("get: old registration", output, StringComparison.Ordinal);
		Assert.DoesNotContain("remove: removed", output, StringComparison.Ordinal);
		Assert.DoesNotContain("add: connected", output, StringComparison.Ordinal);
	}

	[Fact]
	public async Task HelpDocumentsClientsPrintModeAndRuntimeFailure()
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			["mcp", "connect", "--help", "--language", "en"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Contains("claude-code, codex, cursor, vscode, or json", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("--print", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("--open", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("1   Runtime or filesystem failure", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task Connect_DefaultExecutesTheClientWithLiveModeAndPrintsTheResult()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var connectionService = new StubMcpConnectionService
		{
			Result = new McpConnectionResult(
				McpConnectionStatus.Connected,
				"Claude Code connected.",
				NextCommand: "claude",
				CommandOutput: "add: connected")
		};

		var run = await RunAsync(
			workspace,
			connectionService,
			["mcp", "connect", project, "--client", "claude-code", "--language", "en"]);

		Assert.Equal(CommandLineExitCodes.Success, run.ExitCode);
		var request = Assert.Single(connectionService.ConnectRequests);
		Assert.Equal(McpConnectionClient.ClaudeCode, request.Client);
		Assert.Equal(McpConnectionMode.Live, request.Mode);
		Assert.Equal(Path.GetFullPath(project), request.ProjectRoot);
		Assert.Equal(run.ExecutablePath, request.ExecutablePath);
		Assert.Contains("Claude Code connected", run.Environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("Run claude in the project folder.", run.Environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("add: connected", run.Environment.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(run.Environment.StandardError);
		Assert.Empty(connectionService.PrintRequests);
		Assert.Empty(run.LaunchService.Requests);
	}

	[Fact]
	public async Task Connect_ClientAndModeChoicesAreCaseInsensitive()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var connectionService = new StubMcpConnectionService
		{
			Result = new McpConnectionResult(McpConnectionStatus.Connected, "Codex connected.")
		};

		var run = await RunAsync(
			workspace,
			connectionService,
			["mcp", "connect", project, "--client", "CODEX", "--mode", "STANDARD", "--language", "en"]);

		Assert.Equal(CommandLineExitCodes.Success, run.ExitCode);
		var request = Assert.Single(connectionService.ConnectRequests);
		Assert.Equal(McpConnectionClient.Codex, request.Client);
		Assert.Equal(McpConnectionMode.Standard, request.Mode);
	}

	[Fact]
	public async Task Connect_OpenLaunchesTheRegisteredClientOnlyAfterSuccess()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var connectionService = new StubMcpConnectionService
		{
			Result = new McpConnectionResult(
				McpConnectionStatus.Connected,
				"Codex connected.",
				NextCommand: "codex",
				CommandOutput: "add: connected")
		};

		var run = await RunAsync(
			workspace,
			connectionService,
			["mcp", "connect", project, "--client", "codex", "--open", "--language", "en"]);

		Assert.Equal(CommandLineExitCodes.Success, run.ExitCode);
		var request = Assert.Single(run.LaunchService.Requests);
		Assert.Equal(McpConnectionClient.Codex, request.Client);
		Assert.Equal(Path.GetFullPath(project), request.ProjectRoot);
		Assert.Contains("connected", run.Environment.StandardOutput, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("add: connected", run.Environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("Codex", run.Environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("opened", run.Environment.StandardOutput, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("Run codex in the project folder.", run.Environment.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Connect_OpenFailureReportsManualCommandAfterRegistration()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var launchService = new StubMcpClientLaunchService
		{
			Result = new McpClientLaunchResult(
				McpClientLaunchStatus.Failed,
				"No supported terminal application was found.",
				"codex")
		};

		var run = await RunAsync(
			workspace,
			new StubMcpConnectionService
			{
				Result = new McpConnectionResult(McpConnectionStatus.Connected, "Codex connected.")
			},
			["mcp", "connect", project, "--client", "codex", "--open", "--language", "en"],
			launchService);

		Assert.Equal(CommandLineExitCodes.RuntimeError, run.ExitCode);
		Assert.Contains("server is connected", run.Environment.StandardOutput, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("No supported terminal", run.Environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("codex", run.Environment.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Connect_OpenDoesNotLaunchAfterRegistrationFailure()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");

		var run = await RunAsync(
			workspace,
			new StubMcpConnectionService
			{
				Result = new McpConnectionResult(McpConnectionStatus.ProcessFailed, "Registration failed.")
			},
			["mcp", "connect", project, "--client", "codex", "--open", "--language", "en"]);

		Assert.Equal(CommandLineExitCodes.RuntimeError, run.ExitCode);
		Assert.Empty(run.LaunchService.Requests);
	}

	[Fact]
	public async Task Connect_PrintAndOpenAreRejectedTogether()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var connectionService = new StubMcpConnectionService();

		var run = await RunAsync(
			workspace,
			connectionService,
			["mcp", "connect", project, "--print", "--open", "--language", "en"]);

		Assert.Equal(CommandLineExitCodes.UsageError, run.ExitCode);
		Assert.Contains("--print", run.Environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("--open", run.Environment.StandardError, StringComparison.Ordinal);
		Assert.Empty(connectionService.ConnectRequests);
		Assert.Empty(connectionService.PrintRequests);
		Assert.Empty(run.LaunchService.Requests);
	}

	[Fact]
	public async Task Connect_JsonAndOpenAreRejectedBeforeManualConfiguration()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var connectionService = new StubMcpConnectionService();

		var run = await RunAsync(
			workspace,
			connectionService,
			["mcp", "connect", project, "--client", "JSON", "--open", "--language", "en"]);

		Assert.Equal(CommandLineExitCodes.UsageError, run.ExitCode);
		Assert.Contains("--open", run.Environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("json", run.Environment.StandardError, StringComparison.OrdinalIgnoreCase);
		Assert.Empty(connectionService.ConnectRequests);
		Assert.Empty(connectionService.PrintRequests);
		Assert.Empty(run.LaunchService.Requests);
	}

	[Theory]
	[InlineData("claude-code", (int)McpConnectionClient.ClaudeCode)]
	[InlineData("codex", (int)McpConnectionClient.Codex)]
	[InlineData("cursor", (int)McpConnectionClient.Cursor)]
	[InlineData("vscode", (int)McpConnectionClient.VsCode)]
	[InlineData("json", (int)McpConnectionClient.Json)]
	public async Task Print_PreservesFragmentOnlyBehaviorForEveryClient(
		string clientToken,
		int expectedClientValue)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var connectionService = new StubMcpConnectionService
		{
			PrintableConfiguration = $"fragment:{clientToken}"
		};

		var run = await RunAsync(
			workspace,
			connectionService,
			[
				"mcp", "connect", project,
				"--client", clientToken,
				"--mode", "standard",
				"--print",
				"--language", "en"
			]);

		Assert.Equal(CommandLineExitCodes.Success, run.ExitCode);
		Assert.Equal($"fragment:{clientToken}{Environment.NewLine}", run.Environment.StandardOutput);
		Assert.Empty(run.Environment.StandardError);
		Assert.Empty(connectionService.ConnectRequests);
		var request = Assert.Single(connectionService.PrintRequests);
		Assert.Equal((McpConnectionClient)expectedClientValue, request.Client);
		Assert.Equal(McpConnectionMode.Standard, request.Mode);
	}

	[Fact]
	public async Task Connect_ClientFailurePrintsTheManualFallbackAndReturnsRuntimeError()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var connectionService = new StubMcpConnectionService
		{
			Result = new McpConnectionResult(
				McpConnectionStatus.ClientNotFound,
				"Codex was not found.",
				ManualConfiguration: "[mcp_servers.devprojex]")
		};

		var run = await RunAsync(
			workspace,
			connectionService,
			["mcp", "connect", project, "--client", "codex", "--language", "en"]);

		Assert.Equal(CommandLineExitCodes.RuntimeError, run.ExitCode);
		Assert.Contains("Codex was not found.", run.Environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("[mcp_servers.devprojex]", run.Environment.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(run.Environment.StandardError);
	}

	[Fact]
	public async Task Connect_ManualJsonConfigurationIsASuccessfulResult()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var connectionService = new StubMcpConnectionService
		{
			Result = new McpConnectionResult(
				McpConnectionStatus.ManualConfiguration,
				"Use this configuration for another client.",
				ManualConfiguration: "{\"mcpServers\":{}}",
				SuggestedConfigPaths: ["path-one", "path-two"])
		};

		var run = await RunAsync(
			workspace,
			connectionService,
			["mcp", "connect", project, "--client", "json", "--language", "en"]);

		Assert.Equal(CommandLineExitCodes.Success, run.ExitCode);
		Assert.Contains("path-one", run.Environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("path-two", run.Environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("mcpServers", run.Environment.StandardOutput, StringComparison.Ordinal);
	}

	private static async Task<CommandRun> RunAsync(
		TemporaryDirectory workspace,
		StubMcpConnectionService connectionService,
		IReadOnlyList<string> arguments,
		StubMcpClientLaunchService? launchService = null)
	{
		launchService ??= new StubMcpClientLaunchService();
		var dataRoot = workspace.CreateDirectory("app-data");
		var executablePath = workspace.WriteFile("DevProjex.exe", string.Empty);
		using var ownedServices = new TerminalServiceFactory(() => dataRoot).Create(AppLanguage.En);
		var services = ownedServices with
		{
			McpConnectionService = connectionService,
			McpClientLaunchService = launchService,
			TerminalCommandSetupService = new StubTerminalCommandSetupService(executablePath)
		};
		var environment = new TestTerminalEnvironment();
		var application = new TerminalApplication(
			environment,
			new TerminalServiceFactory(_ => services));

		var exitCode = await application.RunAsync(arguments, TestContext.Current.CancellationToken);
		return new CommandRun(exitCode, environment, executablePath, launchService);
	}

	private sealed class StubMcpClientLaunchService : IMcpClientLaunchService
	{
		public McpClientLaunchResult Result { get; init; } = new(McpClientLaunchStatus.Opened);
		public List<McpClientLaunchRequest> Requests { get; } = [];

		public Task<McpClientLaunchResult> OpenAsync(
			McpClientLaunchRequest request,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Requests.Add(request);
			return Task.FromResult(Result);
		}
	}

	private sealed class StubMcpConnectionService : IMcpConnectionService
	{
		public McpConnectionResult Result { get; init; } = new(
			McpConnectionStatus.Connected,
			"Connected");
		public string PrintableConfiguration { get; init; } = "configuration";
		public List<McpConnectionRequest> ConnectRequests { get; } = [];
		public List<McpConnectionRequest> PrintRequests { get; } = [];

		public Task<McpConnectionResult> ConnectAsync(
			McpConnectionRequest request,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ConnectRequests.Add(request);
			return Task.FromResult(Result);
		}

		public string CreatePrintableConfiguration(McpConnectionRequest request)
		{
			PrintRequests.Add(request);
			return PrintableConfiguration;
		}
	}

	private sealed class StubTerminalCommandSetupService(string executablePath)
		: ITerminalCommandSetupService
	{
		public TerminalCommandSetupSnapshot Probe() => new(
			"devprojex",
			TerminalCommandSetupState.Installed,
			CommandPath: executablePath,
			TargetExecutablePath: executablePath,
			InstalledTargetExecutablePath: executablePath,
			UserBinDirectory: Path.GetDirectoryName(executablePath),
			UserBinDirectoryIsInPath: true,
			CanInstall: false,
			CanRepair: false,
			ShellProfileHint: null);

		public TerminalCommandInstallResult InstallOrRepair() => throw new NotSupportedException();

		public TerminalCommandPathSetupResult ConfigurePath() => throw new NotSupportedException();

		public TerminalCommandInstallResult Reinstall() => throw new NotSupportedException();
	}

	private sealed record CommandRun(
		int ExitCode,
		TestTerminalEnvironment Environment,
		string ExecutablePath,
		StubMcpClientLaunchService LaunchService);
}
