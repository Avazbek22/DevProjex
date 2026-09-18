using DevProjex.Kernel.Abstractions;

namespace DevProjex.Tests.Terminal;

public sealed class McpConnectionCommandTests
{
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
				"Claude Code connected. Run claude in the project folder.",
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
		Assert.Contains("add: connected", run.Environment.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(run.Environment.StandardError);
		Assert.Empty(connectionService.PrintRequests);
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
		IReadOnlyList<string> arguments)
	{
		var dataRoot = workspace.CreateDirectory("app-data");
		var executablePath = workspace.WriteFile("DevProjex.exe", string.Empty);
		using var ownedServices = new TerminalServiceFactory(() => dataRoot).Create(AppLanguage.En);
		var services = ownedServices with
		{
			McpConnectionService = connectionService,
			TerminalCommandSetupService = new StubTerminalCommandSetupService(executablePath)
		};
		var environment = new TestTerminalEnvironment();
		var application = new TerminalApplication(
			environment,
			new TerminalServiceFactory(_ => services));

		var exitCode = await application.RunAsync(arguments, TestContext.Current.CancellationToken);
		return new CommandRun(exitCode, environment, executablePath);
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
		string ExecutablePath);
}
