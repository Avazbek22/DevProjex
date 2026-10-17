using DevProjex.Infrastructure.TerminalCommands;

namespace DevProjex.Tests.Unit;

public sealed class FlatpakClientFallbackTests
{
	[Theory]
	[InlineData(McpConnectionClient.ClaudeCode, "missing")]
	[InlineData(McpConnectionClient.ClaudeCode, "start")]
	[InlineData(McpConnectionClient.ClaudeCode, "exit")]
	[InlineData(McpConnectionClient.ClaudeCode, "timeout")]
	[InlineData(McpConnectionClient.Codex, "missing")]
	[InlineData(McpConnectionClient.Codex, "start")]
	[InlineData(McpConnectionClient.Codex, "exit")]
	[InlineData(McpConnectionClient.Codex, "timeout")]
	public async Task UnavailableClientNeverGetsOrChangesARegistration(McpConnectionClient client, string failure)
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, "io.github.Avazbek22.DevProjex"));
		using var directory = new TemporaryDirectory();
		var runner = new Runner(failure);
		var service = new DevProjex.Infrastructure.TerminalCommands.McpConnectionService(
			new(new JsonLocalizationCatalog(), AppLanguage.En),
			new McpClientExecutableLocator(new McpClientExecutableLocatorOptions
			{
				Platform = TerminalCommandHostPlatform.Linux,
				PathVariableProvider = () => directory.Path,
				FileExists = _ => failure != "missing",
				IsExecutable = _ => true
			}), runner, new McpProjectConfigurationWriter());
		var request = new McpConnectionRequest(client, McpConnectionMode.Live,
			Path.Combine(directory.Path, "DevProjex"), directory.Path);
		var inspection = await service.InspectAsync(request, TestContext.Current.CancellationToken);
		Assert.False(inspection.Succeeded);
		Assert.Contains("Could not verify", inspection.ErrorMessage);
		var result = await service.ConnectAsync(request, TestContext.Current.CancellationToken);
		Assert.Equal(McpConnectionStatus.ManualConfiguration, result.Status);
		Assert.Equal(McpConnectionFragmentGenerator.GenerateHostClientCommand(request), result.ManualConfiguration);
		Assert.Null(result.CommandOutput);
		Assert.DoesNotContain("RAW-ERROR", result.UserMessage);
		Assert.All(runner.Calls, call =>
		{
			Assert.Equal(new[] { "--version" }, call.Arguments);
			Assert.InRange(call.Timeout, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(2));
		});
		Assert.Equal(failure == "missing" ? 0 : 2, runner.Calls.Count);
		Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
	}

	private sealed class Runner(string failure) : IMcpConnectionProcessRunner
	{
		public List<McpConnectionProcessRequest> Calls { get; } = [];
		public Task<McpConnectionProcessResult> RunAsync(McpConnectionProcessRequest request, CancellationToken cancellationToken)
		{
			Calls.Add(request);
			return Task.FromResult(failure switch
			{
				"start" => new McpConnectionProcessResult(null, "", "", StartError: "RAW-ERROR"),
				"timeout" => new McpConnectionProcessResult(null, "RAW-ERROR", "", TimedOut: true),
				_ => new McpConnectionProcessResult(1, "", "RAW-ERROR")
			});
		}
	}
}
