using System.Text.Json.Nodes;
using DevProjex.Application.Services;
using DevProjex.Infrastructure.TerminalCommands;

namespace DevProjex.Tests.Unit;

/// <summary>
/// Connects Claude Code and Codex through the real connection service and the real readers of their
/// configuration files; fake client CLIs change those files the way the real ones do.
/// </summary>
public sealed class McpExistingEntryConnectionTests
{
	[Fact]
	public async Task ClaudeKeepsAnEquivalentNpxEntryWithoutRunningTheClient()
	{
		using var setup = new ClaudeSetup();
		setup.WriteEntry(Entry("npx", "-y", "devprojex", "mcp", "--root", setup.Project));
		var before = setup.ReadRawEntry();

		var result = await setup.Service().ConnectAsync(setup.Request(McpConnectionMode.Standard), Token);

		Assert.Equal(McpConnectionStatus.Connected, result.Status);
		Assert.True(result.Succeeded);
		Assert.True(result.KeptExistingEntry);
		Assert.Null(result.ExistingEntryConflict);
		Assert.Contains("npx -y devprojex mcp --root", result.UserMessage, StringComparison.Ordinal);
		Assert.Equal("claude", result.NextCommand);
		Assert.Empty(setup.Cli.Requests);
		Assert.Equal(before, setup.ReadRawEntry());
	}

	[Theory]
	[InlineData(false, true)]
	[InlineData(true, false)]
	public async Task ClaudeKeepsALiveLauncherOnlyWhenItReadsThisApplicationsSelection(bool isolated, bool expectedKept)
	{
		using var setup = new ClaudeSetup(applicationDataIsIsolated: isolated);
		setup.WriteEntry(Entry("npx", "-y", "devprojex", "mcp", "--root", setup.Project, "--live"));

		var result = await setup.Service().ConnectAsync(setup.Request(McpConnectionMode.Live), Token);

		Assert.Equal(expectedKept, result.KeptExistingEntry);
		Assert.Equal(expectedKept, result.ExistingEntryConflict is null);
		Assert.Empty(setup.Cli.Requests);
	}

	[Theory]
	[InlineData("other-root", McpConnectionMode.Standard, new string[0])]
	[InlineData("same-root", McpConnectionMode.Live, new string[0])]
	[InlineData("same-root-live", McpConnectionMode.Standard, new string[0])]
	[InlineData("same-root-extra", McpConnectionMode.Standard, new[] { "--hide-private-data", "--exclude", "default" })]
	[InlineData("relative-root", McpConnectionMode.Standard, new[] { "--root", "." })]
	public async Task ClaudeShowsADifferentLauncherWithItsReplacementBeforeChangingAnything(
		string variant,
		McpConnectionMode mode,
		string[] expectedUnkept)
	{
		using var setup = new ClaudeSetup();
		string[] arguments = variant switch
		{
			"other-root" => ["-y", "devprojex", "mcp", "--root", setup.OtherProject],
			"same-root" => ["-y", "devprojex", "mcp", "--root", setup.Project],
			"same-root-live" => ["-y", "devprojex", "mcp", "--root", setup.Project, "--live"],
			"same-root-extra" => ["-y", "devprojex", "mcp", "--root", setup.Project, "--hide-private-data", "--exclude", "default"],
			_ => ["-y", "devprojex", "mcp", "--root", "."]
		};
		setup.WriteEntry(Entry("npx", arguments));
		var before = setup.ReadRawEntry();
		var request = setup.Request(mode);

		var result = await setup.Service().ConnectAsync(request, Token);

		Assert.Equal(McpConnectionStatus.InvalidConfiguration, result.Status);
		Assert.False(result.Succeeded);
		Assert.False(result.KeptExistingEntry);
		var conflict = Assert.IsType<McpExistingEntryConflict>(result.ExistingEntryConflict);
		Assert.True(conflict.IsDevProjexLauncher);
		Assert.Equal(McpExistingEntryParser.FormatCommandLine("npx", arguments), conflict.ExistingCommand);
		Assert.Equal(
			McpExistingEntryParser.FormatCommandLine(request.ExecutablePath, McpConnectionFragmentGenerator.BuildServerArguments(mode, setup.Project)),
			conflict.ProposedCommand);
		Assert.Equal(expectedUnkept, conflict.UnkeptArguments);
		Assert.Matches("^[0-9A-F]{64}$", result.ExistingEntryFingerprint);
		Assert.False(string.IsNullOrWhiteSpace(result.ManualConfiguration));
		Assert.Contains(conflict.ExistingCommand, result.UserMessage, StringComparison.Ordinal);
		Assert.Contains("--replace", result.UserMessage, StringComparison.Ordinal);
		Assert.Empty(setup.Cli.Requests);
		Assert.Equal(before, setup.ReadRawEntry());
	}

	[Theory]
	[InlineData("node", "server.js")]
	[InlineData("python", "-m", "devprojex_proxy")]
	[InlineData("npx", "-y", "@acme/devprojex", "mcp")]
	public async Task ClaudeNeverChangesAForeignEntryWithoutConfirmation(string command, params string[] arguments)
	{
		using var setup = new ClaudeSetup();
		setup.WriteEntry(Entry(command, arguments, ("TOKEN", "secret-value")));
		var before = setup.ReadRawEntry();

		var result = await setup.Service().ConnectAsync(setup.Request(McpConnectionMode.Standard), Token);

		var conflict = Assert.IsType<McpExistingEntryConflict>(result.ExistingEntryConflict);
		Assert.False(conflict.IsDevProjexLauncher);
		Assert.Empty(conflict.UnkeptArguments);
		Assert.Equal(McpConnectionStatus.InvalidConfiguration, result.Status);
		Assert.DoesNotContain("could not be read", result.UserMessage, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("secret-value", result.UserMessage, StringComparison.Ordinal);
		Assert.DoesNotContain("secret-value", conflict.ExistingCommand, StringComparison.Ordinal);
		Assert.Empty(setup.Cli.Requests);
		Assert.Equal(before, setup.ReadRawEntry());
	}

	[Fact]
	public async Task ClaudeReplacesAConfirmedEntryWithTheNativeOne()
	{
		using var setup = new ClaudeSetup();
		setup.WriteEntry(Entry("cmd", "/c", "npx", "-y", "devprojex", "mcp", "--root", setup.OtherProject));
		var service = setup.Service();
		var request = setup.Request(McpConnectionMode.Live);
		var first = await service.ConnectAsync(request, Token);

		var confirmed = await service.ConnectAsync(
			request with { ReplaceExistingFields = true, ExpectedExistingEntryFingerprint = first.ExistingEntryFingerprint },
			Token);

		Assert.Equal(McpConnectionStatus.Updated, confirmed.Status);
		Assert.True(confirmed.Replaced);
		Assert.Null(confirmed.ExistingEntryConflict);
		Assert.Equal(2, setup.Cli.Requests.Count);
		Assert.Equal(new[] { "mcp", "remove", "-s", "local", "devprojex" }, setup.Cli.Requests[0].Arguments);
		Assert.Equal(
			new[] { "mcp", "add", "--scope", "local", "devprojex", "--", request.ExecutablePath, "mcp", "--root", setup.Project, "--live" },
			setup.Cli.Requests[1].Arguments);
		var entry = setup.ReadEntry()!;
		Assert.Equal(request.ExecutablePath, entry["command"]!.GetValue<string>());
		Assert.Equal(new[] { "mcp", "--root", setup.Project, "--live" },
			entry["args"]!.AsArray().Select(static item => item!.GetValue<string>()));
	}

	[Fact]
	public async Task TheCommandLineReplaceFlagConfirmsWithoutAFingerprint()
	{
		using var setup = new ClaudeSetup();
		setup.WriteEntry(Entry("dnx", "devprojex@5.2.1", "--yes", "--", "mcp", "--root", setup.Project));

		var result = await setup.Service().ConnectAsync(
			setup.Request(McpConnectionMode.Standard) with { ReplaceExistingFields = true },
			Token);

		Assert.Equal(McpConnectionStatus.Updated, result.Status);
		Assert.False(result.KeptExistingEntry);
		Assert.Equal(setup.Request(McpConnectionMode.Standard).ExecutablePath, setup.ReadEntry()!["command"]!.GetValue<string>());
	}

	[Fact]
	public async Task AnEntryChangedAfterTheQuestionIsNeverReplaced()
	{
		using var setup = new ClaudeSetup();
		setup.WriteEntry(Entry("npx", "-y", "devprojex", "mcp", "--root", setup.OtherProject));
		var service = setup.Service();
		var request = setup.Request(McpConnectionMode.Standard);
		var first = await service.ConnectAsync(request, Token);
		setup.WriteEntry(Entry("npx", "-y", "devprojex", "mcp", "--root", setup.OtherProject, "--hide-private-data"));
		var changed = setup.ReadRawEntry();

		var result = await service.ConnectAsync(
			request with { ReplaceExistingFields = true, ExpectedExistingEntryFingerprint = first.ExistingEntryFingerprint },
			Token);

		Assert.Equal(McpConnectionStatus.InvalidConfiguration, result.Status);
		Assert.Contains("changed while confirmation was open", result.UserMessage, StringComparison.Ordinal);
		Assert.Null(result.ExistingEntryConflict);
		Assert.Empty(setup.Cli.Requests);
		Assert.Equal(changed, setup.ReadRawEntry());
	}

	[Fact]
	public async Task AnEntryRemovedAfterTheQuestionIsReportedInsteadOfAddingSilently()
	{
		using var setup = new ClaudeSetup();
		setup.WriteEntry(Entry("npx", "-y", "devprojex", "mcp", "--root", setup.OtherProject));
		var service = setup.Service();
		var request = setup.Request(McpConnectionMode.Standard);
		var first = await service.ConnectAsync(request, Token);
		setup.WriteEntry(null);

		var result = await service.ConnectAsync(
			request with { ReplaceExistingFields = true, ExpectedExistingEntryFingerprint = first.ExistingEntryFingerprint },
			Token);

		Assert.Equal(McpConnectionStatus.InvalidConfiguration, result.Status);
		Assert.Empty(setup.Cli.Requests);
		Assert.Null(setup.ReadEntry());
	}

	[Fact]
	public async Task AFailedAddRestoresTheUsersEntryExactly()
	{
		using var setup = new ClaudeSetup();
		var original = Entry("npx", ["-y", "devprojex", "mcp", "--root", setup.OtherProject], ("NODE_OPTIONS", "--max-old-space-size=4096"));
		original["timeout"] = 120000;
		setup.WriteEntry(original);
		var before = setup.ReadEntry()!;
		setup.Cli.FailAdd = true;

		var result = await setup.Service().ConnectAsync(
			setup.Request(McpConnectionMode.Standard) with { ReplaceExistingFields = true },
			Token);

		Assert.Equal(McpConnectionStatus.ProcessFailed, result.Status);
		Assert.Contains("restored", result.UserMessage, StringComparison.OrdinalIgnoreCase);
		Assert.Equal(new[] { "remove", "add", "add-json" }, setup.Cli.Requests.Select(static request => request.Arguments[1]));
		Assert.True(JsonNode.DeepEquals(before, setup.ReadEntry()), setup.ReadRawEntry());
	}

	[Fact]
	public async Task AFailedRemoveLeavesTheEntryAndNeverAdds()
	{
		using var setup = new ClaudeSetup();
		setup.WriteEntry(Entry("npx", "-y", "devprojex", "mcp", "--root", setup.OtherProject));
		var before = setup.ReadRawEntry();
		setup.Cli.FailRemove = true;

		var result = await setup.Service().ConnectAsync(
			setup.Request(McpConnectionMode.Standard) with { ReplaceExistingFields = true },
			Token);

		Assert.False(result.Succeeded);
		Assert.Equal(new[] { "remove" }, setup.Cli.Requests.Select(static request => request.Arguments[1]));
		Assert.Equal(before, setup.ReadRawEntry());
	}

	[Fact]
	public async Task ANativeEntryKeepsItsSilentReplacement()
	{
		using var setup = new ClaudeSetup();
		var nativeExecutable = Path.Combine(setup.OtherProject, "DevProjex.exe");
		setup.WriteEntry(Entry(nativeExecutable, "mcp", "--root", setup.OtherProject));

		var result = await setup.Service().ConnectAsync(setup.Request(McpConnectionMode.Standard), Token);

		Assert.Equal(McpConnectionStatus.Updated, result.Status);
		Assert.Null(result.ExistingEntryConflict);
		Assert.False(result.KeptExistingEntry);
		Assert.Equal(new[] { "remove", "add" }, setup.Cli.Requests.Select(static request => request.Arguments[1]));
	}

	[Fact]
	public async Task AnUnreadableEntryStillFailsInsteadOfBeingReplaced()
	{
		using var setup = new ClaudeSetup();
		setup.WriteEntry(new JsonObject { ["type"] = "http", ["url"] = "https://example.invalid/mcp" });
		var before = setup.ReadRawEntry();

		var result = await setup.Service().ConnectAsync(setup.Request(McpConnectionMode.Standard), Token);

		Assert.Equal(McpConnectionStatus.ProcessFailed, result.Status);
		Assert.Null(result.ExistingEntryConflict);
		Assert.Empty(setup.Cli.Requests);
		Assert.Equal(before, setup.ReadRawEntry());
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task CodexInspectionLeavesAnUnrecognizedEntryToTheConnectionQuestion(bool launcher)
	{
		using var setup = new CodexSetup();
		setup.WriteEntry(launcher ? "npx" : "node", launcher
			? ["-y", "devprojex", "mcp", "--root", setup.OtherProject]
			: ["server.js"]);

		var inspection = await setup.Service().InspectAsync(setup.Request(McpConnectionMode.Standard), Token);

		Assert.True(inspection.Succeeded, inspection.ErrorMessage);
		Assert.True(inspection.Exists);
		Assert.False(inspection.RequiresProjectReplacement);
		Assert.Equal(launcher ? setup.OtherProject : null, inspection.ExistingProjectRoot);
		Assert.Empty(setup.Cli.Requests);
	}

	[Fact]
	public async Task CodexKeepsAnEquivalentDnxEntry()
	{
		using var setup = new CodexSetup();
		setup.WriteEntry("dnx", ["devprojex", "mcp", "--root", setup.Project]);
		var before = setup.ReadConfiguration();

		var result = await setup.Service().ConnectAsync(setup.Request(McpConnectionMode.Standard), Token);

		Assert.Equal(McpConnectionStatus.Connected, result.Status);
		Assert.True(result.KeptExistingEntry);
		Assert.Contains("dnx devprojex mcp --root", result.UserMessage, StringComparison.Ordinal);
		Assert.Contains("Codex may shorten", result.UserMessage, StringComparison.Ordinal);
		Assert.Empty(setup.Cli.Requests);
		Assert.Equal(before, setup.ReadConfiguration());
	}

	[Fact]
	public async Task CodexReplacesAConfirmedLauncherForAnotherProjectAndVerifiesIt()
	{
		using var setup = new CodexSetup();
		setup.WriteEntry("npx", ["-y", "devprojex", "mcp", "--root", setup.OtherProject]);
		var service = setup.Service();
		var request = setup.Request(McpConnectionMode.Standard);
		var first = await service.ConnectAsync(request, Token);
		Assert.NotNull(first.ExistingEntryConflict);
		Assert.Empty(setup.Cli.Requests);

		var confirmed = await service.ConnectAsync(
			request with { ReplaceExistingFields = true, ExpectedExistingEntryFingerprint = first.ExistingEntryFingerprint },
			Token);

		Assert.Equal(McpConnectionStatus.Updated, confirmed.Status);
		Assert.Equal(new[] { "remove", "add", "get" }, setup.Cli.Requests.Select(static item => item.Arguments[1]));
		Assert.Equal(request.ExecutablePath, setup.Cli.Command);
		Assert.Equal(new[] { "mcp", "--root", setup.Project }, setup.Cli.Arguments);
	}

	[Fact]
	public async Task CodexReplacesAConfirmedEntryWithExtraSettingsInPlaceAndKeepsThem()
	{
		using var setup = new CodexSetup();
		setup.WriteEntry("npx", ["-y", "devprojex", "mcp", "--root", setup.OtherProject], "startup_timeout_sec = 45");
		var service = setup.Service();
		var request = setup.Request(McpConnectionMode.Standard);
		var first = await service.ConnectAsync(request, Token);
		Assert.NotNull(first.ExistingEntryConflict);
		Assert.Empty(setup.Cli.Requests);

		var confirmed = await service.ConnectAsync(
			request with { ReplaceExistingFields = true, ExpectedExistingEntryFingerprint = first.ExistingEntryFingerprint },
			Token);

		Assert.Equal(McpConnectionStatus.Updated, confirmed.Status);
		Assert.Equal(new[] { "get" }, setup.Cli.Requests.Select(static item => item.Arguments[1]));
		var connection = setup.ReadConnection();
		Assert.NotNull(connection);
		Assert.Equal(request.ExecutablePath, connection.Command);
		Assert.Equal(new[] { "mcp", "--root", setup.Project }, connection.Arguments);
		var configuration = setup.ReadConfiguration();
		Assert.Contains("startup_timeout_sec = 45", configuration, StringComparison.Ordinal);
		Assert.StartsWith("model = \"gpt-5\"", configuration, StringComparison.Ordinal);
	}

	[Fact]
	public async Task CodexRestoresAnEntryWithExtraSettingsByteForByteWhenVerificationFails()
	{
		using var setup = new CodexSetup();
		setup.WriteEntry("node", ["server.js"], "startup_timeout_sec = 45");
		var before = setup.ReadConfiguration();
		var service = setup.Service();
		var request = setup.Request(McpConnectionMode.Standard);
		var first = await service.ConnectAsync(request, Token);
		Assert.NotNull(first.ExistingEntryConflict);
		setup.Cli.FailGet = true;

		var confirmed = await service.ConnectAsync(
			request with { ReplaceExistingFields = true, ExpectedExistingEntryFingerprint = first.ExistingEntryFingerprint },
			Token);

		Assert.Equal(McpConnectionStatus.ProcessFailed, confirmed.Status);
		Assert.Equal(new[] { "get" }, setup.Cli.Requests.Select(static item => item.Arguments[1]));
		Assert.Equal(before, setup.ReadConfiguration());
	}

	private static CancellationToken Token => TestContext.Current.CancellationToken;

	private static JsonObject Entry(string command, params string[] arguments) => Entry(command, arguments, []);

	private static JsonObject Entry(string command, string[] arguments, params (string Name, string Value)[] environment)
	{
		var entry = new JsonObject
		{
			["type"] = "stdio",
			["command"] = command,
			["args"] = new JsonArray(arguments.Select(static item => (JsonNode?)JsonValue.Create(item)).ToArray())
		};
		if (environment.Length > 0)
			entry["env"] = new JsonObject(environment.Select(static pair => KeyValuePair.Create(pair.Name, (JsonNode?)JsonValue.Create(pair.Value))));
		return entry;
	}

	private static McpClientExecutableLocator Locator(string bin, string commandName)
	{
		var executable = Path.GetFullPath(Path.Combine(bin, commandName + ".exe"));
		return new McpClientExecutableLocator(new McpClientExecutableLocatorOptions
		{
			Platform = TerminalCommandHostPlatform.Windows,
			PathVariableProvider = () => bin,
			FileExists = path => string.Equals(path, executable, StringComparison.OrdinalIgnoreCase)
		});
	}

	private sealed class ClaudeSetup : IDisposable
	{
		private readonly TemporaryDirectory _directory = new();
		private readonly bool _applicationDataIsIsolated;

		public ClaudeSetup(bool applicationDataIsIsolated = false)
		{
			_applicationDataIsIsolated = applicationDataIsIsolated;
			Project = Path.GetFullPath(_directory.CreateFolder("project"));
			OtherProject = Path.GetFullPath(_directory.CreateFolder("other project"));
			ConfigurationDirectory = Path.GetFullPath(_directory.CreateFolder("claude-home"));
			Cli = new FakeClaudeCli(this);
		}

		public string Project { get; }
		public string OtherProject { get; }
		public string ConfigurationDirectory { get; }
		public FakeClaudeCli Cli { get; }
		private string ConfigurationPath => Path.Combine(ConfigurationDirectory, ".claude.json");

		public McpConnectionService Service() =>
			new(
				new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En),
				Locator(Path.Combine(_directory.Path, "bin"), "claude"),
				Cli,
				new McpProjectConfigurationWriter(),
				new StubCodexReader(),
				new McpClaudeUserConfigurationReader(new McpClaudeUserConfigurationReaderOptions
				{
					ConfigurationDirectoryProvider = () => ConfigurationDirectory
				}),
				() => _applicationDataIsIsolated);

		public McpConnectionRequest Request(McpConnectionMode mode) =>
			new(McpConnectionClient.ClaudeCode, mode, Path.Combine(Project, "app", "DevProjex.exe"), Project);

		public void WriteEntry(JsonObject? entry)
		{
			var servers = new JsonObject();
			if (entry is not null)
				servers["devprojex"] = entry.DeepClone();
			var root = new JsonObject
			{
				["numStartups"] = 3,
				["projects"] = new JsonObject
				{
					[Project] = new JsonObject { ["mcpServers"] = servers, ["allowedTools"] = new JsonArray() }
				}
			};
			File.WriteAllText(ConfigurationPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
		}

		public JsonObject? ReadEntry()
		{
			var root = JsonNode.Parse(File.ReadAllText(ConfigurationPath))!;
			return root["projects"]![Project]!["mcpServers"]!["devprojex"]?.AsObject();
		}

		public string? ReadRawEntry() => ReadEntry()?.ToJsonString();

		public void Dispose() => _directory.Dispose();
	}

	/// <summary>Applies remove, add and add-json to the configuration file like the real Claude Code CLI.</summary>
	private sealed class FakeClaudeCli(ClaudeSetup setup) : IMcpConnectionProcessRunner
	{
		public List<McpConnectionProcessRequest> Requests { get; } = [];
		public bool FailAdd { get; set; }
		public bool FailRemove { get; set; }

		public Task<McpConnectionProcessResult> RunAsync(McpConnectionProcessRequest request, CancellationToken cancellationToken)
		{
			Requests.Add(request);
			var arguments = request.Arguments;
			switch (arguments[1])
			{
				case "remove" when FailRemove:
					return Task.FromResult(new McpConnectionProcessResult(1, string.Empty, "Error: permission denied"));
				case "remove":
					setup.WriteEntry(null);
					return Task.FromResult(new McpConnectionProcessResult(0, "Removed MCP server devprojex", string.Empty));
				case "add" when FailAdd:
					return Task.FromResult(new McpConnectionProcessResult(1, string.Empty, "Error: invalid configuration"));
				case "add":
					var separator = arguments.ToList().IndexOf("--");
					setup.WriteEntry(Entry(arguments[separator + 1], arguments.Skip(separator + 2).ToArray()));
					return Task.FromResult(new McpConnectionProcessResult(0, "Added stdio MCP server devprojex", string.Empty));
				case "add-json":
					setup.WriteEntry(JsonNode.Parse(arguments[^1])!.AsObject());
					return Task.FromResult(new McpConnectionProcessResult(0, "Added stdio MCP server devprojex", string.Empty));
				default:
					throw new InvalidOperationException("Unexpected Claude Code command: " + string.Join(' ', arguments));
			}
		}
	}

	private sealed class CodexSetup : IDisposable
	{
		private readonly TemporaryDirectory _directory = new();

		public CodexSetup()
		{
			Project = Path.GetFullPath(_directory.CreateFolder("project"));
			OtherProject = Path.GetFullPath(_directory.CreateFolder("other project"));
			CodexHome = Path.GetFullPath(_directory.CreateFolder("codex-home"));
			Cli = new FakeCodexCli(this);
		}

		public string Project { get; }
		public string OtherProject { get; }
		public string CodexHome { get; }
		public FakeCodexCli Cli { get; }
		private string ConfigurationPath => Path.Combine(CodexHome, "config.toml");

		public McpConnectionService Service() =>
			new(
				new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En),
				Locator(Path.Combine(_directory.Path, "bin"), "codex"),
				Cli,
				new McpProjectConfigurationWriter(),
				new McpCodexUserConfigurationReader(new McpCodexUserConfigurationReaderOptions
				{
					CodexHomeProvider = () => CodexHome
				}),
				new StubClaudeReader(),
				static () => false);

		public McpConnectionRequest Request(McpConnectionMode mode) =>
			new(McpConnectionClient.Codex, mode, Path.Combine(Project, "app", "DevProjex.exe"), Project);

		public void WriteEntry(string? command, IReadOnlyList<string>? arguments, string? extraSetting = null)
		{
			var text = new StringBuilder("model = \"gpt-5\"\n");
			if (command is not null)
			{
				text.Append("\n[mcp_servers.devprojex]\ncommand = ").Append(Toml(command)).Append('\n')
					.Append("args = [").Append(string.Join(", ", arguments!.Select(Toml))).Append("]\n");
				if (extraSetting is not null)
					text.Append(extraSetting).Append('\n');
			}
			File.WriteAllText(ConfigurationPath, text.ToString());
		}

		public McpCodexUserConnection? ReadConnection() =>
			new McpCodexUserConfigurationReader(new McpCodexUserConfigurationReaderOptions
			{
				CodexHomeProvider = () => CodexHome
			}).Read().Connection;

		public string ReadConfiguration() => File.ReadAllText(ConfigurationPath);

		private static string Toml(string value) =>
			"\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

		public void Dispose() => _directory.Dispose();
	}

	/// <summary>Applies remove and add to config.toml and answers get like the real Codex CLI.</summary>
	private sealed class FakeCodexCli(CodexSetup setup) : IMcpConnectionProcessRunner
	{
		public List<McpConnectionProcessRequest> Requests { get; } = [];
		public string? Command { get; private set; }
		public string[] Arguments { get; private set; } = [];
		public bool FailGet { get; set; }

		public Task<McpConnectionProcessResult> RunAsync(McpConnectionProcessRequest request, CancellationToken cancellationToken)
		{
			Requests.Add(request);
			var arguments = request.Arguments;
			switch (arguments[1])
			{
				case "remove":
					setup.WriteEntry(null, null);
					return Task.FromResult(new McpConnectionProcessResult(0, "Removed global MCP server 'devprojex'.", string.Empty));
				case "add":
					var separator = arguments.ToList().IndexOf("--");
					setup.WriteEntry(arguments[separator + 1], arguments.Skip(separator + 2).ToArray());
					return Task.FromResult(new McpConnectionProcessResult(0, "Added global MCP server 'devprojex'.", string.Empty));
				case "get" when FailGet:
					return Task.FromResult(new McpConnectionProcessResult(1, string.Empty, "Error: failed to load configuration"));
				case "get":
					// Codex answers from config.toml, so an entry edited in place is reported like an added one.
					var connection = setup.ReadConnection();
					Command = connection?.Command;
					Arguments = connection?.Arguments.ToArray() ?? [];
					var payload = JsonSerializer.Serialize(new
					{
						name = "devprojex",
						enabled = true,
						transport = new
						{
							type = "stdio",
							command = Command,
							args = Arguments,
							env = (Dictionary<string, string>?)null,
							env_vars = Array.Empty<string>(),
							cwd = (string?)null
						}
					});
					return Task.FromResult(new McpConnectionProcessResult(0, payload, string.Empty));
				default:
					throw new InvalidOperationException("Unexpected Codex command: " + string.Join(' ', arguments));
			}
		}
	}

	private sealed class StubCodexReader : IMcpCodexUserConfigurationReader
	{
		public McpCodexUserConfigurationRead Read() => new(true, null);
	}

	private sealed class StubClaudeReader : IMcpClaudeUserConfigurationReader
	{
		public McpClaudeUserConfigurationRead Read(string projectRoot) => new(true, null);
	}
}
