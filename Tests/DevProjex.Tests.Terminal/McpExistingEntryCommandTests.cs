using System.Text.Json.Nodes;
using DevProjex.Kernel.Abstractions;
using DevProjex.Infrastructure.ResourceStore;
using DevProjex.Infrastructure.TerminalCommands;

namespace DevProjex.Tests.Terminal;

/// <summary>
/// The command line has no dialog: an unrecognized devprojex entry is reported with the manual configuration,
/// and --replace is the confirmation.
/// </summary>
public sealed class McpExistingEntryCommandTests
{
	[Fact]
	public async Task ALauncherEntryForAnotherProjectNeedsReplaceAndIsUntouchedUntilThen()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var other = workspace.CreateDirectory("other");
		var claudeHome = workspace.CreateDirectory("claude-home");
		var configuration = Path.Combine(claudeHome, ".claude.json");
		WriteEntry(configuration, project, ["-y", "devprojex", "mcp", "--root", other]);
		var original = await File.ReadAllTextAsync(configuration, TestContext.Current.CancellationToken);
		var cli = new FakeClaudeCli(configuration, project);
		var arguments = new[] { "mcp", "connect", project, "--client", "claude-code", "--language", "en" };

		var rejected = await RunAsync(workspace, claudeHome, cli, arguments);

		Assert.Equal(CommandLineExitCodes.RuntimeError, rejected.ExitCode);
		Assert.Contains("npx -y devprojex mcp --root", rejected.Environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("--replace", rejected.Environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("claude mcp add --scope local devprojex", rejected.Environment.StandardError, StringComparison.Ordinal);
		Assert.Empty(cli.Commands);
		Assert.Equal(original, await File.ReadAllTextAsync(configuration, TestContext.Current.CancellationToken));

		var replaced = await RunAsync(workspace, claudeHome, cli, [.. arguments, "--replace"]);

		Assert.Equal(CommandLineExitCodes.Success, replaced.ExitCode);
		Assert.Equal(new[] { "remove", "add" }, cli.Commands);
		var entry = JsonNode.Parse(await File.ReadAllTextAsync(configuration, TestContext.Current.CancellationToken))!
			["projects"]![project]!["mcpServers"]!["devprojex"]!;
		Assert.Equal(replaced.ExecutablePath, entry["command"]!.GetValue<string>());
	}

	[Fact]
	public async Task AnEquivalentLauncherEntryIsReportedAsConnectedWithoutChanges()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var claudeHome = workspace.CreateDirectory("claude-home");
		var configuration = Path.Combine(claudeHome, ".claude.json");
		WriteEntry(configuration, project, ["-y", "devprojex", "mcp", "--root", project]);
		var original = await File.ReadAllTextAsync(configuration, TestContext.Current.CancellationToken);
		var cli = new FakeClaudeCli(configuration, project);

		var run = await RunAsync(workspace, claudeHome, cli,
			["mcp", "connect", project, "--client", "claude-code", "--language", "en"]);

		Assert.Equal(CommandLineExitCodes.Success, run.ExitCode);
		Assert.Contains("already starts DevProjex for this project", run.Environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("npx -y devprojex mcp --root", run.Environment.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(run.Environment.StandardError);
		Assert.Empty(cli.Commands);
		Assert.Equal(original, await File.ReadAllTextAsync(configuration, TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task ControlCharactersInAForeignEntryNeverReachTheTerminal()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var claudeHome = workspace.CreateDirectory("claude-home");
		var configuration = Path.Combine(claudeHome, ".claude.json");
		WriteEntry(configuration, project, ["serve\u001b]0;owned\u0007\u001b[2J", "--port\n4000"], command: "proxy");
		var cli = new FakeClaudeCli(configuration, project);

		var run = await RunAsync(workspace, claudeHome, cli,
			["mcp", "connect", project, "--client", "claude-code", "--language", "en"]);

		Assert.Equal(CommandLineExitCodes.RuntimeError, run.ExitCode);
		Assert.DoesNotContain('\u001b', run.Environment.StandardError);
		Assert.DoesNotContain('\u0007', run.Environment.StandardError);
		Assert.Contains("proxy", run.Environment.StandardError, StringComparison.Ordinal);
		Assert.Empty(cli.Commands);
	}

	private static void WriteEntry(string configuration, string project, string[] arguments, string command = "npx")
	{
		var root = new JsonObject
		{
			["projects"] = new JsonObject
			{
				[project] = new JsonObject
				{
					["mcpServers"] = new JsonObject
					{
						["devprojex"] = new JsonObject
						{
							["type"] = "stdio",
							["command"] = command,
							["args"] = new JsonArray(arguments.Select(static item => (JsonNode?)JsonValue.Create(item)).ToArray())
						}
					}
				}
			}
		};
		File.WriteAllText(configuration, root.ToJsonString());
	}

	private static async Task<CommandRun> RunAsync(
		TemporaryDirectory workspace,
		string claudeHome,
		FakeClaudeCli cli,
		IReadOnlyList<string> arguments)
	{
		var dataRoot = workspace.CreateDirectory("app-data");
		var executablePath = workspace.WriteFile("DevProjex.exe", string.Empty);
		var bin = workspace.CreateDirectory("bin");
		var claude = Path.GetFullPath(Path.Combine(bin, "claude.exe"));
		var connection = new McpConnectionService(
			new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En),
			new McpClientExecutableLocator(new McpClientExecutableLocatorOptions
			{
				Platform = TerminalCommandHostPlatform.Windows,
				PathVariableProvider = () => bin,
				FileExists = path => string.Equals(path, claude, StringComparison.OrdinalIgnoreCase)
			}),
			cli,
			new McpProjectConfigurationWriter(),
			claudeUserConfigurationReader: new McpClaudeUserConfigurationReader(new McpClaudeUserConfigurationReaderOptions
			{
				ConfigurationDirectoryProvider = () => claudeHome
			}),
			applicationDataIsIsolated: static () => false);
		using var ownedServices = new TerminalServiceFactory(() => dataRoot).Create(AppLanguage.En);
		var services = ownedServices with
		{
			McpConnectionService = connection,
			TerminalCommandSetupService = new StubTerminalCommandSetupService(executablePath)
		};
		var environment = new TestTerminalEnvironment();
		var application = new TerminalApplication(environment, new TerminalServiceFactory(_ => services));

		var exitCode = await application.RunAsync(arguments, TestContext.Current.CancellationToken);
		return new CommandRun(exitCode, environment, executablePath);
	}

	private sealed class FakeClaudeCli(string configuration, string project) : IMcpConnectionProcessRunner
	{
		public List<string> Commands { get; } = [];

		public Task<McpConnectionProcessResult> RunAsync(McpConnectionProcessRequest request, CancellationToken cancellationToken)
		{
			var arguments = request.Arguments;
			Commands.Add(arguments[1]);
			var root = JsonNode.Parse(File.ReadAllText(configuration))!;
			var servers = root["projects"]![project]!["mcpServers"]!.AsObject();
			switch (arguments[1])
			{
				case "remove":
					servers.Remove("devprojex");
					break;
				case "add":
					var separator = arguments.ToList().IndexOf("--");
					servers["devprojex"] = new JsonObject
					{
						["type"] = "stdio",
						["command"] = arguments[separator + 1],
						["args"] = new JsonArray(arguments.Skip(separator + 2).Select(static item => (JsonNode?)JsonValue.Create(item)).ToArray())
					};
					break;
				default:
					throw new InvalidOperationException("Unexpected Claude Code command: " + string.Join(' ', arguments));
			}
			File.WriteAllText(configuration, root.ToJsonString());
			return Task.FromResult(new McpConnectionProcessResult(0, string.Empty, string.Empty));
		}
	}

	private sealed class StubTerminalCommandSetupService(string executablePath) : ITerminalCommandSetupService
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

	private sealed record CommandRun(int ExitCode, TestTerminalEnvironment Environment, string ExecutablePath);
}
