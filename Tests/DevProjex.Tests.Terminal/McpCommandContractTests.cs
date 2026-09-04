using System.Xml.Linq;

namespace DevProjex.Tests.Terminal;

public sealed class McpCommandContractTests
{
	[Fact]
	public void RootResolutionUsesExplicitThenClaudeProjectThenCurrentDirectory()
	{
		var variables = new Dictionary<string, string?>
		{
			["CLAUDE_PROJECT_DIR"] = "/claude/project"
		};

		Assert.Equal(
			["/explicit/one", "/explicit/two"],
			McpRootSourceResolver.Resolve(["/explicit/one", "/explicit/two"], variables, "/current"));
		Assert.Equal(
			["/claude/project"],
			McpRootSourceResolver.Resolve([], variables, "/current"));
		Assert.Equal(
			["/current"],
			McpRootSourceResolver.Resolve([], new Dictionary<string, string?>(), "/current"));
	}

	[Fact]
	public async Task McpCommandAndRepeatableRootOptionAreVisibleInHelp()
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			["mcp", "--help", "--language", "en"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Contains("devprojex mcp", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("--root", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("--hide-private-data", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("--allow-remote", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("--git-mode", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("--exclude", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("--unrestricted", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("--allow-agent-exclusions", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("Run the local read-only MCP stdio server.", environment.StandardOutput, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("hide-secrets")]
	[InlineData("hide-private-data")]
	[InlineData("nonsense")]
	public async Task McpServerBaselineRejectsUnknownAndRedactionExclusions(string exclusion)
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			["mcp", "--exclude", exclusion, "--language", "en"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("Unknown exclusion", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task McpServerBaselineRejectsNoneCombinedWithAnotherExclusion()
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			["mcp", "--exclude", "none", "--exclude", "dot-files", "--language", "en"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("cannot be combined", environment.StandardError, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("--exclude", "dot-files")]
	[InlineData("--exclude", "none")]
	[InlineData("--git-mode", "tracked")]
	public async Task McpServerRejectsUnrestrictedCombinedWithBaselineFlags(string flag, string value)
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			["mcp", "--unrestricted", flag, value, "--language", "en"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("--unrestricted cannot be combined", environment.StandardError, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("staged")]
	[InlineData("changes")]
	[InlineData("diff:main..feature")]
	public async Task McpServerBaselineRejectsMomentaryGitModes(string mode)
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			["mcp", "--git-mode", mode, "--language", "en"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("none", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("tracked", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task McpStartupErrorsEscapeControlCharactersFromRootPaths()
	{
		var environment = new TestTerminalEnvironment();
		var invalidRoot = Path.Combine(
			Path.GetTempPath(),
			$"missing-\r\n\u001b[31m-{Guid.NewGuid():N}");

		var exitCode = await new TerminalApplication(environment).RunAsync(
			["mcp", "--root", invalidRoot, "--language", "en"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.StartsWith("error[DPX-MCP-STARTUP]: ", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("\\r\\n\\u001B[31m", environment.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain('\u001b', environment.StandardError);
		Assert.Single(environment.StandardError.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
	}

	[Fact]
	public async Task McpCancellationUsesTheCliCanceledExitContract()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var environment = new TestTerminalEnvironment();
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			["mcp", "--root", project, "--language", "en"],
			cancellation.Token);

		Assert.Equal(CommandLineExitCodes.Canceled, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("DPX-CLI-CANCELED", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public void PackageLocalizationAndSolutionContractsArePinned()
	{
		var repository = PublishedApplicationLocator.FindRepositoryRoot();
		var packages = XDocument.Load(Path.Combine(repository, "Directory.Packages.props"));
		var package = packages.Descendants("PackageVersion")
			.Single(element => element.Attribute("Include")?.Value == "ModelContextProtocol");
		Assert.Equal("2.2.0", package.Attribute("Version")?.Value);

		var localizationDirectory = Path.Combine(repository, "Assets", "Localization");
		var localeFiles = Directory.EnumerateFiles(localizationDirectory, "*.json").OrderBy(static path => path).ToArray();
		Assert.Equal(Enum.GetValues<AppLanguage>().Length, localeFiles.Length);
		Assert.All(localeFiles, static path =>
		{
			using var document = JsonDocument.Parse(File.ReadAllText(path));
			Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("Terminal.Command.Mcp").GetString()));
			Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("Terminal.Option.McpRoot").GetString()));
			Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("Terminal.Option.McpGitMode").GetString()));
			Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("Terminal.Option.McpExclude").GetString()));
			Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("Terminal.Option.McpUnrestricted").GetString()));
			Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("Terminal.Option.McpAgentExclusions").GetString()));
			Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("Terminal.Validation.UnrestrictedConflict").GetString()));
		});

		var solution = File.ReadAllText(Path.Combine(repository, "DevProjex.sln"));
		Assert.Contains("Apps\\Mcp\\DevProjex.Mcp.csproj", solution, StringComparison.Ordinal);
	}
}
