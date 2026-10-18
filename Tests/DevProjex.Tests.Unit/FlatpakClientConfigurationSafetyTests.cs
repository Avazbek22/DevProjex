using DevProjex.Infrastructure.TerminalCommands;

namespace DevProjex.Tests.Unit;

public sealed class FlatpakClientConfigurationSafetyTests
{
	[Theory]
	[InlineData(0, "2.1.71 (Claude Code)", false, true)]
	[InlineData(0, "codex-cli 0.115.0", false, true)]
	[InlineData(0, "v1.2.3-beta.4", false, true)]
	[InlineData(0, "V1.2.3", false, true)]
	[InlineData(0, "\u001b[32m2.1.71\u001b[0m (Claude Code)", false, true)]
	[InlineData(0, "1.2.3", true, false)]
	[InlineData(1, "1.2.3", false, false)]
	[InlineData(0, "not a version", false, false)]
	public void VersionProbeAcceptsCompleteClientVersionsButNotFailedOrTruncatedOutput(int exitCode, string output, bool incomplete, bool expected)
	{
		var method = typeof(DevProjex.Infrastructure.TerminalCommands.McpConnectionService).GetMethod("IsUsableFlatpakClientVersion", BindingFlags.NonPublic | BindingFlags.Static);
		Assert.NotNull(method);
		Assert.Equal(expected, method.Invoke(null, [new McpConnectionProcessResult(exitCode, output, "", OutputIncomplete: incomplete)]));
	}

	[Theory]
	[InlineData(McpConnectionClient.ClaudeCode, "not-json RAW-ERROR")]
	[InlineData(McpConnectionClient.ClaudeCode, "{\"mcpServers\":")]
	[InlineData(McpConnectionClient.ClaudeCode, "[]")]
	[InlineData(McpConnectionClient.Codex, "not-json RAW-ERROR")]
	[InlineData(McpConnectionClient.Codex, "{\"transport\":")]
	[InlineData(McpConnectionClient.Codex, "[]")]
	public async Task MalformedCliResponsesReturnOnlyTheManualCommandAndPreserveForeignConfiguration(McpConnectionClient client, string response)
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, "test.app"));
		using var directory = new TemporaryDirectory();
		var files = new[] { ".claude.json", ".codex/config.toml", ".cursor/mcp.json", ".vscode/mcp.json" }
			.Select(name => Path.GetFullPath(directory.CreateFile(name, "foreign bytes\r\n"))).ToDictionary(name => name, File.ReadAllBytes);
		var runner = new Runner(response);
		var service = new DevProjex.Infrastructure.TerminalCommands.McpConnectionService(new(new JsonLocalizationCatalog(), AppLanguage.En),
			new McpClientExecutableLocator(new McpClientExecutableLocatorOptions
			{
				Platform = OperatingSystem.IsWindows() ? TerminalCommandHostPlatform.Windows : TerminalCommandHostPlatform.Linux,
				PathVariableProvider = () => directory.Path, PathExtensionsProvider = () => ".EXE", FileExists = _ => true, IsExecutable = _ => true
			}), runner, new McpProjectConfigurationWriter(), claudeUserConfigurationReader: new RejectReader());
		var request = new McpConnectionRequest(client, McpConnectionMode.Live, Path.Combine(directory.Path, "DevProjex"), directory.Path);
		var result = await service.ConnectAsync(request, TestContext.Current.CancellationToken);
		Assert.Equal(McpConnectionStatus.ManualConfiguration, result.Status);
		Assert.Equal(McpConnectionFragmentGenerator.GenerateHostClientCommand(request), result.ManualConfiguration);
		Assert.Null(result.CommandOutput);
		Assert.DoesNotContain("RAW-ERROR", result.UserMessage);
		Assert.Single(runner.Calls);
		Assert.Equal(new[] { "--version" }, runner.Calls[0].Arguments);
		foreach (var file in files) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
		Assert.Equal(files.Keys.Order(), Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories).Order());
	}

	private sealed class Runner(string response) : IMcpConnectionProcessRunner
	{
		public List<McpConnectionProcessRequest> Calls { get; } = [];
		public Task<McpConnectionProcessResult> RunAsync(McpConnectionProcessRequest request, CancellationToken cancellationToken)
		{
			Calls.Add(request);
			return Task.FromResult(new McpConnectionProcessResult(0, response, ""));
		}
	}

	private sealed class RejectReader : IMcpClaudeUserConfigurationReader
	{
		public McpClaudeUserConfigurationRead Read(string projectRoot) => throw new InvalidOperationException("An unusable client must not inspect configuration.");
	}
}
