using DevProjex.Infrastructure.TerminalCommands;

namespace DevProjex.Tests.Unit;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class McpPackageConnectionTests
{
	[Theory]
	[InlineData(false, McpConnectionClient.ClaudeCode, McpConnectionMode.Standard)]
	[InlineData(false, McpConnectionClient.ClaudeCode, McpConnectionMode.Live)]
	[InlineData(false, McpConnectionClient.Codex, McpConnectionMode.Standard)]
	[InlineData(false, McpConnectionClient.Codex, McpConnectionMode.Live)]
	[InlineData(true, McpConnectionClient.ClaudeCode, McpConnectionMode.Standard)]
	[InlineData(true, McpConnectionClient.ClaudeCode, McpConnectionMode.Live)]
	[InlineData(true, McpConnectionClient.Codex, McpConnectionMode.Standard)]
	[InlineData(true, McpConnectionClient.Codex, McpConnectionMode.Live)]
	public async Task RegistersAndVerifiesTheLauncherThenKeepsItOnReconnect(bool nuget, McpConnectionClient client, McpConnectionMode mode)
	{
		using var setup = new Setup(nuget, client, mode);
		var result = await setup.Service.ConnectAsync(setup.Request, Token);
		Assert.Equal(McpConnectionStatus.Connected, result.Status);
		Assert.Equal(setup.Command, setup.Client.Command);
		Assert.Equal(setup.Arguments, setup.Client.Arguments);
		var add = setup.Client.Requests[0].Arguments;
		var separator = add.ToList().IndexOf("--");
		Assert.Equal(new[] { setup.Command }.Concat(setup.Arguments), add.Skip(separator + 1));
		Assert.Equal(client == McpConnectionClient.Codex ? 2 : 1, setup.Client.Requests.Count);
		var requestCount = setup.Client.Requests.Count;
		var repeated = await setup.Service.ConnectAsync(setup.Request, Token);
		Assert.True(repeated.Succeeded);
		Assert.True(repeated.KeptExistingEntry);
		Assert.Equal(requestCount, setup.Client.Requests.Count);
		Assert.Equal(setup.Command, setup.Client.Command);
	}

	[Theory]
	[InlineData(false, McpConnectionClient.ClaudeCode)]
	[InlineData(true, McpConnectionClient.ClaudeCode)]
	[InlineData(false, McpConnectionClient.Codex)]
	[InlineData(true, McpConnectionClient.Codex)]
	public async Task FailedReplacementRestoresTheOriginalAfterCreatingThePackageLauncher(bool nuget, McpConnectionClient client)
	{
		using var setup = new Setup(nuget, client, McpConnectionMode.Live);
		var original = Path.Combine(setup.Root.Path, "original-devprojex");
		setup.Client.Command = original;
		setup.Client.Arguments = ["mcp", "--root", setup.Root.Path];
		setup.Client.FailAdd = true;
		var result = await setup.Service.ConnectAsync(setup.Request, Token);
		Assert.Equal(McpConnectionStatus.ProcessFailed, result.Status);
		Assert.Equal(original, setup.Client.Command);
		Assert.Equal(["mcp", "--root", setup.Root.Path], setup.Client.Arguments);
		Assert.Equal(4, setup.Client.Requests.Count);
		Assert.Equal("remove", setup.Client.Requests[2].Arguments[1]);
	}

	[Theory]
	[InlineData(false, McpConnectionClient.ClaudeCode)]
	[InlineData(true, McpConnectionClient.Codex)]
	public async Task FailedAddDoesNotRemoveAConcurrentlyReplacedForeignEntry(bool nuget, McpConnectionClient client)
	{
		using var setup = new Setup(nuget, client, McpConnectionMode.Standard);
		setup.Client.Command = Path.Combine(setup.Root.Path, "original-devprojex");
		setup.Client.Arguments = ["mcp", "--root", setup.Root.Path];
		setup.Client.FailAdd = true;
		setup.Client.ReplaceWithForeignAfterAdd = true;
		var result = await setup.Service.ConnectAsync(setup.Request, Token);
		Assert.Equal(McpConnectionStatus.ProcessFailed, result.Status);
		Assert.Equal("foreign-server", setup.Client.Command);
		Assert.Equal(["server.js"], setup.Client.Arguments);
		Assert.Equal(2, setup.Client.Requests.Count);
	}

	[Theory]
	[InlineData(false, McpConnectionClient.ClaudeCode)]
	[InlineData(true, McpConnectionClient.Codex)]
	public async Task ExistingForeignEntryRequiresConfirmationAndIsNeverWrittenDirectly(bool nuget, McpConnectionClient client)
	{
		using var setup = new Setup(nuget, client, McpConnectionMode.Standard);
		setup.Client.Command = "node";
		setup.Client.Arguments = ["foreign-server.js"];
		var result = await setup.Service.ConnectAsync(setup.Request, Token);
		Assert.Equal(McpConnectionStatus.InvalidConfiguration, result.Status);
		Assert.NotNull(result.ExistingEntryConflict);
		Assert.Contains(setup.Command, result.ExistingEntryConflict.ProposedCommand, StringComparison.Ordinal);
		Assert.Empty(setup.Client.Requests);
		Assert.Equal("node", setup.Client.Command);
		Assert.Equal(["foreign-server.js"], setup.Client.Arguments);
	}

	[Theory]
	[InlineData(false, McpConnectionClient.Cursor)]
	[InlineData(false, McpConnectionClient.VsCode)]
	[InlineData(true, McpConnectionClient.Cursor)]
	[InlineData(true, McpConnectionClient.VsCode)]
	public async Task ProjectConfigurationWriterUsesTheLauncherAndKeepsOtherServers(bool nuget, McpConnectionClient client)
	{
		using var setup = new Setup(nuget, client, McpConnectionMode.Live);
		var container = client == McpConnectionClient.Cursor ? "mcpServers" : "servers";
		var directory = Path.Combine(setup.Root.Path, client == McpConnectionClient.Cursor ? ".cursor" : ".vscode");
		Directory.CreateDirectory(directory);
		var path = Path.Combine(directory, "mcp.json");
		await File.WriteAllTextAsync(path, "{\"" + container + "\":{\"other\":{\"command\":\"leave-alone\"}}}", Token);
		var result = await setup.Service.ConnectAsync(setup.Request, Token);
		Assert.True(result.Succeeded, result.UserMessage);
		var machinePathWarning = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En)["Mcp.Connect.ProjectConfigurationMachinePath"];
		Assert.DoesNotContain(machinePathWarning, result.UserMessage, StringComparison.Ordinal);
		var first = await File.ReadAllTextAsync(path, Token);
		using var json = JsonDocument.Parse(first);
		var servers = json.RootElement.GetProperty(container);
		Assert.Equal("leave-alone", servers.GetProperty("other").GetProperty("command").GetString());
		var entry = servers.GetProperty("devprojex");
		Assert.Equal(setup.Command, entry.GetProperty("command").GetString());
		Assert.Equal(setup.Arguments.Select(x => x == setup.Root.Path ? "${workspaceFolder}" : x), entry.GetProperty("args").EnumerateArray().Select(x => x.GetString()));
		var repeated = await setup.Service.ConnectAsync(setup.Request, Token);
		Assert.True(repeated.Succeeded, repeated.UserMessage);
		Assert.Equal(first, await File.ReadAllTextAsync(path, Token));
		Assert.Empty(setup.Client.Requests);
		Assert.False(File.Exists(Path.Combine(setup.Root.Path, ".claude.json")));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ExtendedCodexConfigurationUsesTheSameLauncherAndVerification(bool nuget)
	{
		using var setup = new Setup(nuget, McpConnectionClient.Codex, McpConnectionMode.Live);
		setup.Client.Command = Path.Combine(setup.Root.Path, "old-devprojex");
		setup.Client.Arguments = ["mcp", "--root", setup.Root.Path];
		setup.Client.HasExtendedFields = true;
		var result = await setup.Service.ConnectAsync(setup.Request, Token);
		Assert.Equal(McpConnectionStatus.Updated, result.Status);
		Assert.Equal(1, setup.Client.StoreWrites);
		Assert.Equal(setup.Command, setup.Client.Command);
		Assert.Equal(setup.Arguments, setup.Client.Arguments);
		Assert.Equal(["mcp", "get", "devprojex", "--json"], Assert.Single(setup.Client.Requests).Arguments);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ClaudeAcceptsAnEquivalentLauncherCreatedBeforeAnAlreadyExistsResponse(bool nuget)
	{
		using var setup = new Setup(nuget, McpConnectionClient.ClaudeCode, McpConnectionMode.Live);
		setup.Client.AlreadyExistsAfterAdd = true;
		var result = await setup.Service.ConnectAsync(setup.Request, Token);
		Assert.Equal(McpConnectionStatus.Updated, result.Status);
		Assert.Equal(setup.Command, setup.Client.Command);
		Assert.Equal(setup.Arguments, setup.Client.Arguments);
		Assert.Single(setup.Client.Requests);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task InvalidCodexVerificationRollsBackTheNewLauncher(bool nuget)
	{
		using var setup = new Setup(nuget, McpConnectionClient.Codex, McpConnectionMode.Standard);
		setup.Client.InvalidVerification = true;
		var result = await setup.Service.ConnectAsync(setup.Request, Token);
		Assert.Equal(McpConnectionStatus.ProcessFailed, result.Status);
		Assert.Null(setup.Client.Command);
		Assert.Equal(new[] { "add", "get", "remove" }, setup.Client.Requests.Select(x => x.Arguments[1]));
		Assert.NotNull(result.ManualConfiguration);
		Assert.DoesNotContain(setup.Request.ExecutablePath, result.ManualConfiguration, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(false, McpConnectionClient.ClaudeCode)]
	[InlineData(true, McpConnectionClient.ClaudeCode)]
	[InlineData(false, McpConnectionClient.Codex)]
	[InlineData(true, McpConnectionClient.Codex)]
	public async Task MissingClientProvidesThePackageConfigurationWithoutChangingAnything(bool nuget, McpConnectionClient client)
	{
		using var setup = new Setup(nuget, client, McpConnectionMode.Standard);
		var service = new McpConnectionService(
			new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En),
			new McpClientExecutableLocator(new McpClientExecutableLocatorOptions
			{
				Platform = TerminalCommandHostPlatform.Linux,
				PathVariableProvider = () => string.Empty,
				FileExists = _ => false,
				IsExecutable = _ => false
			}), setup.Client, new McpProjectConfigurationWriter(), setup.Client, setup.Client);
		var result = await service.ConnectAsync(setup.Request, Token);
		Assert.Equal(McpConnectionStatus.ClientNotFound, result.Status);
		Assert.NotNull(result.ManualConfiguration);
		Assert.Contains(setup.Command, result.ManualConfiguration, StringComparison.Ordinal);
		Assert.DoesNotContain(setup.Request.ExecutablePath, result.ManualConfiguration, StringComparison.Ordinal);
		Assert.Empty(setup.Client.Requests);
		Assert.Null(setup.Client.Command);
	}

	private static CancellationToken Token => TestContext.Current.CancellationToken;

	private sealed class Setup : IDisposable
	{
		public TemporaryDirectory Root { get; } = new();
		public ClientState Client { get; } = new();
		public McpConnectionService Service { get; }
		public McpConnectionRequest Request { get; }
		public string Command { get; }
		public string[] Arguments { get; }

		public Setup(bool nuget, McpConnectionClient client, McpConnectionMode mode)
		{
			var launcher = nuget ? "dnx" : "npx";
			// A Windows root makes a Windows connection, where the batch launchers go through cmd /c.
			var windows = OperatingSystem.IsWindows();
			Command = windows ? "cmd" : launcher;
			var binary = Path.Combine(Root.Path, nuget
				? ".nuget/packages/devprojex.linux-x64/5.2.0/tools/any/linux-x64/devprojex"
				: "npm cache/_npx/abc/node_modules/@devprojex/cli-linux-x64/bin/devprojex");
			Request = new(client, mode, binary, Root.Path);
			Arguments = [.. windows ? new[] { "/c", launcher } : [], .. nuget ? new[] { "devprojex" } : ["-y", "devprojex"],
				"mcp", "--root", Root.Path, .. mode == McpConnectionMode.Live ? new[] { "--live" } : []];
			var locator = new McpClientExecutableLocator(new McpClientExecutableLocatorOptions
			{
				Platform = TerminalCommandHostPlatform.Linux,
				PathVariableProvider = () => Root.Path,
				FileExists = _ => true,
				IsExecutable = _ => true
			});
			Service = new(new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En), locator, Client,
				new McpProjectConfigurationWriter(), Client, Client, () => false);
		}

		public void Dispose() => Root.Dispose();
	}

	private sealed class ClientState : IMcpConnectionProcessRunner, IMcpCodexUserConfigurationStore, IMcpClaudeUserConfigurationReader
	{
		public string? Command { get; set; }
		public string[] Arguments { get; set; } = [];
		public bool FailAdd { get; set; }
		public bool ReplaceWithForeignAfterAdd { get; set; }
		public bool HasExtendedFields { get; set; }
		public bool AlreadyExistsAfterAdd { get; set; }
		public bool InvalidVerification { get; set; }
		public int StoreWrites { get; private set; }
		public List<McpConnectionProcessRequest> Requests { get; } = [];

		McpCodexUserConfigurationRead IMcpCodexUserConfigurationReader.Read() =>
			new(true, Command is null ? null : new(Command, Arguments, new Dictionary<string, string>(), HasExtendedFields));

		McpClaudeUserConfigurationRead IMcpClaudeUserConfigurationReader.Read(string projectRoot) =>
			new(true, Command is null ? null : new(Command, Arguments, new Dictionary<string, string>(), JsonSerializer.Serialize(new { command = Command, args = Arguments })));

		public Task<McpConnectionProcessResult> RunAsync(McpConnectionProcessRequest request, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Requests.Add(request);
			var operation = request.Arguments[1];
			if (operation == "add")
			{
				var separator = request.Arguments.ToList().IndexOf("--");
				Command = request.Arguments[separator + 1];
				Arguments = request.Arguments.Skip(separator + 2).ToArray();
				if (ReplaceWithForeignAfterAdd)
				{
					Command = "foreign-server";
					Arguments = ["server.js"];
				}
				var failed = FailAdd;
				FailAdd = false;
				if (AlreadyExistsAfterAdd)
					return Task.FromResult(new McpConnectionProcessResult(1, string.Empty, "MCP server devprojex already exists in local config"));
				return Task.FromResult(new McpConnectionProcessResult(failed ? 1 : 0, string.Empty, failed ? "failed" : string.Empty));
			}
			if (operation == "add-json")
			{
				using var json = JsonDocument.Parse(request.Arguments[^1]);
				Command = json.RootElement.GetProperty("command").GetString();
				Arguments = json.RootElement.GetProperty("args").EnumerateArray().Select(x => x.GetString()!).ToArray();
				return Task.FromResult(new McpConnectionProcessResult(0, string.Empty, string.Empty));
			}
			if (operation == "remove")
			{
				Command = null;
				Arguments = [];
				return Task.FromResult(new McpConnectionProcessResult(0, string.Empty, string.Empty));
			}
			Assert.Equal("get", operation);
			if (InvalidVerification)
				return Task.FromResult(new McpConnectionProcessResult(0, "not-json", string.Empty));
			return Task.FromResult(new McpConnectionProcessResult(0, JsonSerializer.Serialize(new
			{
				transport = new { type = "stdio", command = Command, args = Arguments }
			}), string.Empty));
		}

		public Task<McpCodexUserConfigurationWrite> UpdateConnectionAsync(McpCodexUserConfigurationRead snapshot,
			string command, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> requiredEnvironment, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			StoreWrites++;
			Command = command;
			Arguments = arguments.ToArray();
			return Task.FromResult(new McpCodexUserConfigurationWrite(true));
		}

		public Task<bool> RestoreAsync(McpCodexUserConfigurationRead snapshot, CancellationToken cancellationToken) =>
			throw new InvalidOperationException("Unexpected restore.");
	}
}
