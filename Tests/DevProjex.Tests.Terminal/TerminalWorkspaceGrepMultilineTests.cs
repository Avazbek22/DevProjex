namespace DevProjex.Tests.Terminal;

public sealed class TerminalWorkspaceGrepMultilineTests
{
	[Fact]
	public async Task GrepMatchesAPatternThatSpansTwoLinesOnceWithTheCliRules()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/package.json", """
			{
			  "exports": {
			    ".": {
			      "import": "./index.js"
			    }
			  }
			}
			""");
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			project,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);
		var plan = await controller.BuildCurrentPlanAsync(state, TestContext.Current.CancellationToken);

		var result = await controller.SearchProjectAsync(
			plan,
			"\"exports\": \\{\\n\\s*\"\\.\": \\{",
			SearchMode.Regex,
			SearchCommandHandler.DefaultMaximumResults,
			TestContext.Current.CancellationToken);

		var match = Assert.Single(result.Matches);
		Assert.Equal("package.json", match.Path);
		Assert.Equal(2, match.Line);
		Assert.Equal(1, result.Boundary.EncounteredMatches);

		var output = TerminalProjectSearchOutput.Format(result, services.Localization);
		Assert.Contains("package.json:2", output, StringComparison.Ordinal);
	}
}
