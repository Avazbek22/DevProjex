using DevProjex.Kernel.Abstractions;
using DevProjex.Infrastructure.ProjectProfiles;
using DevProjex.Infrastructure.ResourceStore;
using DevProjex.Infrastructure.TerminalCommands;

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
	public void TuiConnectionOutput_ShowsEachMessageLineWithoutEscapedLineBreaks()
	{
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);
		var output = TerminalWorkspaceSession.BuildMcpConnectionOutput(
			new McpConnectionResult(
				McpConnectionStatus.Connected,
				"Cursor: configuration was written to .cursor/mcp.json." + Environment.NewLine +
				localization["Mcp.Connect.ProjectConfigurationMachinePath"]),
			localization);

		Assert.StartsWith(
			"Cursor: configuration was written to .cursor/mcp.json." + Environment.NewLine +
			localization["Mcp.Connect.ProjectConfigurationMachinePath"],
			output,
			StringComparison.Ordinal);
		Assert.DoesNotContain("\\r", output, StringComparison.Ordinal);
		Assert.DoesNotContain("\\n", output, StringComparison.Ordinal);
	}

	[Fact]
	public void TuiConnectionOutput_ShowsAnIdeNextStepAsWritten()
	{
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.Ru);
		var nextStep = localization["Mcp.Connect.Cursor.NextStep"];
		var output = TerminalWorkspaceSession.BuildMcpConnectionOutput(
			new McpConnectionResult(
				McpConnectionStatus.Connected,
				"Cursor connected.",
				NextStep: nextStep),
			localization);

		Assert.Contains(Environment.NewLine + nextStep, output, StringComparison.Ordinal);
		Assert.DoesNotContain(
			localization.Format("Mcp.Connect.RunInProject", nextStep),
			output,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task Connect_IdeClientPrintsItsNextStepAsWritten()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);
		var nextStep = localization["Mcp.Connect.VsCode.NextStep"];
		var connectionService = new StubMcpConnectionService
		{
			Result = new McpConnectionResult(
				McpConnectionStatus.Connected,
				"VS Code: configuration was written to .vscode/mcp.json.",
				NextStep: nextStep)
		};

		var run = await RunAsync(
			workspace,
			connectionService,
			["mcp", "connect", project, "--client", "vscode", "--language", "en"]);

		Assert.Equal(CommandLineExitCodes.Success, run.ExitCode);
		Assert.Contains(nextStep, run.Environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("in the project folder", run.Environment.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public async Task CursorConnectionRequiresReplaceFlagBeforeDiscardingAdditionalFields()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		const string original = """
			{
			  "mcpServers": {
			    "devprojex": {
			      "command": "old",
			      "args": [],
			      "envFile": ".env",
			      "cwd": "keep-me"
			    }
			  }
			}
			""";
		var configurationPath = workspace.WriteFile(Path.Combine("project", ".cursor", "mcp.json"), original);
		var service = new McpConnectionService(new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En));
		var arguments = new[] { "mcp", "connect", project, "--client", "cursor", "--language", "en" };

		var rejected = await RunAsync(workspace, service, arguments);
		Assert.Equal(CommandLineExitCodes.RuntimeError, rejected.ExitCode);
		Assert.Contains("cwd, envFile", rejected.Environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("--replace", rejected.Environment.StandardError, StringComparison.Ordinal);
		Assert.Equal(original, await File.ReadAllTextAsync(configurationPath, TestContext.Current.CancellationToken));

		var replaced = await RunAsync(workspace, service, [.. arguments, "--replace"]);
		Assert.Equal(CommandLineExitCodes.Success, replaced.ExitCode);
		Assert.Contains("Replaced fields: cwd, envFile.", replaced.Environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("envFile", await File.ReadAllTextAsync(configurationPath, TestContext.Current.CancellationToken));
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

	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public async Task Connect_RefusesAMissingOrFileProjectBeforeAnyClientWork(bool pointAtFile, bool print)
	{
		using var workspace = new TemporaryDirectory();
		var project = Path.GetFullPath(pointAtFile
			? workspace.WriteFile("project.txt", "not a folder")
			: Path.Combine(workspace.Path, "missing"));
		var connectionService = new StubMcpConnectionService();
		string[] arguments = print
			? ["mcp", "connect", project, "--client", "cursor", "--print", "--language", "en"]
			: ["mcp", "connect", project, "--client", "cursor", "--language", "en"];

		var run = await RunAsync(workspace, connectionService, arguments);

		Assert.Equal(CommandLineExitCodes.UsageError, run.ExitCode);
		Assert.Empty(run.Environment.StandardOutput);
		Assert.Contains("DPX-PROJECT-NOT-FOUND", run.Environment.StandardError, StringComparison.Ordinal);
		Assert.Contains(
			pointAtFile
				? "The project path is a file, not a folder."
				: "The project folder does not exist.",
			run.Environment.StandardError,
			StringComparison.Ordinal);
		Assert.Contains($"path: {project}", run.Environment.StandardError, StringComparison.Ordinal);
		Assert.Empty(connectionService.ConnectRequests);
		Assert.Empty(connectionService.PrintRequests);
		Assert.Empty(run.LaunchService.Requests);
		Assert.Equal(!pointAtFile, !File.Exists(project) && !Directory.Exists(project));
	}

	[Fact]
	public async Task Connect_MissingProjectMessageFollowsTheRequestedLanguage()
	{
		using var workspace = new TemporaryDirectory();
		var project = Path.GetFullPath(Path.Combine(workspace.Path, "missing"));

		var run = await RunAsync(
			workspace,
			new StubMcpConnectionService(),
			["mcp", "connect", project, "--print", "--language", "ru"]);

		Assert.Equal(CommandLineExitCodes.UsageError, run.ExitCode);
		Assert.Contains("Папка проекта не существует.", run.Environment.StandardError, StringComparison.Ordinal);
		Assert.Contains($"путь: {project}", run.Environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public void TuiConnectionOutput_ShowsTheMissingProjectMessage()
	{
		using var workspace = new TemporaryDirectory();
		var project = Path.GetFullPath(Path.Combine(workspace.Path, "gone"));
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);
		var refusal = Assert.IsType<McpConnectionResult>(McpConnectionProjectRoot.CreateRefusal(localization, project));

		var output = TerminalWorkspaceSession.BuildMcpConnectionOutput(refusal, localization);

		Assert.Equal("The project folder does not exist." + Environment.NewLine + project, output);
	}

	[Fact]
	public async Task Connect_DefaultExecutesTheClientWithStandardModeAndPrintsTheResult()
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
		Assert.Equal(McpConnectionMode.Standard, request.Mode);
		Assert.Equal(Path.GetFullPath(project), request.ProjectRoot);
		Assert.Equal(run.ExecutablePath, request.ExecutablePath);
		Assert.Equal(
			[
				"Claude Code connected.",
				$"Client: Claude Code · mode: standard · project: {Path.GetFullPath(project)}",
				"Run claude in the project folder.",
				"If a Claude Code session is already running, restart it to load this server."
			],
			OutputLines(run));
		Assert.DoesNotContain("add: connected", run.Environment.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(run.Environment.StandardError);
		Assert.Empty(connectionService.PrintRequests);
		Assert.Empty(run.LaunchService.Requests);
	}

	[Fact]
	public async Task Connect_LiveModeStatesWhenTheSelectionWasSaved()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var store = new ProjectProfileStore(() => workspace.CreateDirectory("app-data"));
		Assert.True(store.TrySaveProfile(
			Path.GetFullPath(project),
			new ProjectSelectionProfile([], [], [], SelectedPaths: ["src"]),
			new DateTimeOffset(2026, 9, 12, 8, 5, 0, TimeSpan.Zero)));

		var run = await RunAsync(
			workspace,
			new StubMcpConnectionService
			{
				Result = new McpConnectionResult(McpConnectionStatus.Connected, "Cursor connected.")
			},
			["mcp", "connect", project, "--client", "cursor", "--mode", "live", "--language", "en"]);

		Assert.Equal(CommandLineExitCodes.Success, run.ExitCode);
		var lines = OutputLines(run);
		Assert.Contains($"Client: Cursor · mode: live · project: {Path.GetFullPath(project)}", lines);
		Assert.Contains(
			"Live mode follows the selection saved for this project on 2026-09-12 08:05 UTC: checked items set " +
			"the agent's focus, and the window's filters limit what it can read.",
			lines);
		Assert.Equal("If a Cursor session is already running, restart it to load this server.", lines[^1]);
	}

	[Fact]
	public async Task Connect_LiveModeWithoutASavedSelectionSaysTheServerUsesDefaults()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");

		var run = await RunAsync(
			workspace,
			new StubMcpConnectionService
			{
				Result = new McpConnectionResult(McpConnectionStatus.Connected, "Claude Code connected.")
			},
			["mcp", "connect", project, "--mode", "live", "--language", "en"]);

		Assert.Equal(CommandLineExitCodes.Success, run.ExitCode);
		Assert.Contains(
			"Live mode: no selection is saved for this project yet, so the server uses its standard defaults " +
			"until you open the project in DevProjex.",
			OutputLines(run));
	}

	[Fact]
	public async Task Connect_StandardModeDoesNotDescribeALiveSelection()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");

		var run = await RunAsync(
			workspace,
			new StubMcpConnectionService
			{
				Result = new McpConnectionResult(McpConnectionStatus.Connected, "Claude Code connected.")
			},
			["mcp", "connect", project, "--language", "en"]);

		Assert.DoesNotContain("Live mode", run.Environment.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Connect_CodexToAnotherProjectRequiresReplaceAndNamesBothRoots()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var previous = Path.GetFullPath(workspace.CreateDirectory("previous"));
		var service = new ReplacementMcpConnectionService(previous);

		var refused = await RunAsync(
			workspace,
			service,
			["mcp", "connect", project, "--client", "codex", "--language", "en"]);

		Assert.Equal(CommandLineExitCodes.RuntimeError, refused.ExitCode);
		Assert.Empty(refused.Environment.StandardOutput);
		Assert.Contains(
			$"Codex is connected to {previous}. Codex keeps one global devprojex entry; run again with --replace " +
			$"to point it at {Path.GetFullPath(project)}.",
			refused.Environment.StandardError,
			StringComparison.Ordinal);
		Assert.Empty(service.ConnectRequests);
		Assert.Empty(service.ReplaceRequests);

		var replaced = await RunAsync(
			workspace,
			service,
			["mcp", "connect", project, "--client", "codex", "--replace", "--language", "en"]);

		Assert.Equal(CommandLineExitCodes.Success, replaced.ExitCode);
		var replacement = Assert.Single(service.ReplaceRequests);
		Assert.Equal(previous, replacement.ExpectedExistingProjectRoot);
		Assert.Equal(Path.GetFullPath(project), replacement.Request.ProjectRoot);
		Assert.Empty(service.ConnectRequests);
		Assert.Contains(
			"Codex keeps one global devprojex entry, so it serves one project at a time; connecting another " +
			"project replaces it.",
			OutputLines(replaced));
	}

	[Fact]
	public async Task Connect_CodexForTheSameProjectConnectsWithoutReplace()
	{
		using var workspace = new TemporaryDirectory();
		var project = Path.GetFullPath(workspace.CreateDirectory("project"));
		var service = new ReplacementMcpConnectionService(project);

		var run = await RunAsync(
			workspace,
			service,
			["mcp", "connect", project, "--client", "codex", "--language", "en"]);

		Assert.Equal(CommandLineExitCodes.Success, run.ExitCode);
		Assert.Single(service.ConnectRequests);
		Assert.Empty(service.ReplaceRequests);
	}

	[Fact]
	public async Task Connect_MultilineResultMessageIsWrittenLineByLine()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");

		var run = await RunAsync(
			workspace,
			new StubMcpConnectionService
			{
				Result = new McpConnectionResult(
					McpConnectionStatus.Updated,
					"VS Code: configuration was written to .vscode/mcp.json." + Environment.NewLine +
					"The file belongs to this computer.")
			},
			["mcp", "connect", project, "--client", "vscode", "--language", "en"]);

		var lines = OutputLines(run);
		Assert.Equal("VS Code: configuration was written to .vscode/mcp.json.", lines[0]);
		Assert.Equal("The file belongs to this computer.", lines[1]);
		Assert.DoesNotContain("\\r\\n", run.Environment.StandardOutput, StringComparison.Ordinal);
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
		Assert.Empty(run.Environment.StandardOutput);
		Assert.Contains("server is connected", run.Environment.StandardError, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("No supported terminal", run.Environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("codex", run.Environment.StandardError, StringComparison.Ordinal);
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
		Assert.Empty(run.Environment.StandardOutput);
		Assert.Contains("Codex was not found.", run.Environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("[mcp_servers.devprojex]", run.Environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Connect_RestorationExplanationIsWrittenToStandardError()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var connectionService = new StubMcpConnectionService
		{
			Result = new McpConnectionResult(
				McpConnectionStatus.ProcessFailed,
				"Codex registration failed. The previous configuration was restored.")
		};

		var run = await RunAsync(
			workspace,
			connectionService,
			["mcp", "connect", project, "--client", "codex", "--language", "en"]);

		Assert.Equal(CommandLineExitCodes.RuntimeError, run.ExitCode);
		Assert.Empty(run.Environment.StandardOutput);
		Assert.Contains("previous configuration was restored", run.Environment.StandardError, StringComparison.Ordinal);
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
		Assert.Empty(run.Environment.StandardError);
	}

	private static async Task<CommandRun> RunAsync(
		TemporaryDirectory workspace,
		IMcpConnectionService connectionService,
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

	private static string[] OutputLines(CommandRun run) =>
		run.Environment.StandardOutput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

	private sealed class ReplacementMcpConnectionService(string existingProjectRoot)
		: IMcpConnectionService, IMcpConnectionReplacementService
	{
		public List<McpConnectionRequest> ConnectRequests { get; } = [];
		public List<(McpConnectionRequest Request, string ExpectedExistingProjectRoot)> ReplaceRequests { get; } = [];

		public Task<McpConnectionResult> ConnectAsync(
			McpConnectionRequest request,
			CancellationToken cancellationToken = default)
		{
			ConnectRequests.Add(request);
			return Task.FromResult(new McpConnectionResult(McpConnectionStatus.Connected, "Codex connected."));
		}

		public string CreatePrintableConfiguration(McpConnectionRequest request) => "configuration";

		public Task<McpConnectionInspection> InspectAsync(
			McpConnectionRequest request,
			CancellationToken cancellationToken = default) =>
			Task.FromResult(new McpConnectionInspection(
				Exists: true,
				existingProjectRoot,
				RequiresProjectReplacement: !string.Equals(
					existingProjectRoot,
					request.ProjectRoot,
					StringComparison.OrdinalIgnoreCase)));

		public Task<McpConnectionResult> ReplaceAsync(
			McpConnectionRequest request,
			string expectedExistingProjectRoot,
			CancellationToken cancellationToken = default)
		{
			ReplaceRequests.Add((request, expectedExistingProjectRoot));
			return Task.FromResult(new McpConnectionResult(
				McpConnectionStatus.Updated,
				"Codex updated.",
				Replaced: true));
		}
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
