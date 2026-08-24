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
		Assert.Contains("Run the local read-only MCP stdio server.", environment.StandardOutput, StringComparison.Ordinal);
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
		Assert.Equal(11, localeFiles.Length);
		Assert.All(localeFiles, static path =>
		{
			using var document = JsonDocument.Parse(File.ReadAllText(path));
			Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("Terminal.Command.Mcp").GetString()));
			Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("Terminal.Option.McpRoot").GetString()));
		});

		var solution = File.ReadAllText(Path.Combine(repository, "DevProjex.sln"));
		Assert.Contains("Apps\\Mcp\\DevProjex.Mcp.csproj", solution, StringComparison.Ordinal);
	}
}
