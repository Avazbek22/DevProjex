using DevProjex.Terminal.Rendering;

namespace DevProjex.Tests.Terminal;

[Collection(EnvironmentVariableCollection.Name)]
public sealed class AnsiConsoleFactoryContractTests
{
	[Fact]
	public void ExplicitInteractiveCapabilityIsNotOverriddenByGitHubActionsEnrichment()
	{
		using var githubActions = new EnvironmentVariableScope("GITHUB_ACTIONS", "true");
		var environment = new TestTerminalEnvironment
		{
			IsErrorInteractive = true,
			Width = 100
		};
		var capabilities = TerminalCapabilities.Resolve(
			environment,
			new TerminalOutputOptions(
				Color: TerminalColorMode.Always,
				Progress: TerminalProgressMode.Always),
			forStandardError: true);

		Assert.True(capabilities.UseInteractiveProgress);

		var console = AnsiConsoleFactory.Create(environment.Error, capabilities);

		Assert.True(console.Profile.Capabilities.Interactive);
		Assert.True(console.Profile.Capabilities.Ansi);
	}

	[Fact]
	public async Task ImmediateMeasuredCompletionRemainsVisibleUnderGitHubActions()
	{
		using var githubActions = new EnvironmentVariableScope("GITHUB_ACTIONS", "true");
		using var appData = new TemporaryDirectory();
		var environment = new TestTerminalEnvironment
		{
			IsErrorInteractive = true,
			Width = 100
		};
		var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		var renderer = new ProgressRenderer(
			environment,
			new TerminalOutputOptions(
				Color: TerminalColorMode.Always,
				Progress: TerminalProgressMode.Always),
			services.Localization);

		var result = await renderer.RunProjectExportAsync(progress =>
		{
			Assert.NotNull(progress);
			progress.Report(new ProjectCopyExportProgress(4, 4, 4_096, 100));
			return Task.FromResult(42);
		});

		Assert.Equal(42, result);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains(
			"Exporting project 4/4 (4 KB)",
			environment.StandardError,
			StringComparison.Ordinal);
		Assert.Contains("100%", environment.StandardError, StringComparison.Ordinal);
	}

	private sealed class EnvironmentVariableScope : IDisposable
	{
		private readonly string _name;
		private readonly string? _previousValue;

		public EnvironmentVariableScope(string name, string value)
		{
			_name = name;
			_previousValue = Environment.GetEnvironmentVariable(name);
			Environment.SetEnvironmentVariable(name, value);
		}

		public void Dispose() => Environment.SetEnvironmentVariable(_name, _previousValue);
	}
}
