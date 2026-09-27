using System.CommandLine;

namespace DevProjex.Tests.Terminal;

public sealed class SearchCommandContractTests
{
	[Fact]
	public void SearchCommandPublishesTheCompleteOptionContract()
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		var search = root.Subcommands.Single(command => command.Name == "search");

		Assert.Equal(["PATTERN", "PROJECT"], search.Arguments.Select(argument => argument.Name));
		Assert.Contains(search.Options, option => option.Name == "--regex");
		Assert.Contains(search.Options, option => option.Name == "--symbols");
		Assert.Contains(search.Options, option => option.Name == "--max");
		Assert.Contains(search.Options, option => option.Name == "--search-body-chars");
		Assert.Contains(search.Options, option => option.Name == "--profile");
		Assert.Contains(search.Options, option => option.Name == "--root");
		Assert.Contains(search.Options, option => option.Name == "--select");
		Assert.Contains(search.Options, option => option.Name == "--exclude");
		Assert.Contains(search.Options, option => option.Name == "--git-mode");
		Assert.Contains(search.Options, option => option.Name == "--hide-secrets");
		Assert.Contains(search.Options, option => option.Name == "--format");
		Assert.Contains(search.Options, option => option.Name == "--output");
		Assert.DoesNotContain(search.Options, option => option.Name == "--hide-private-data");
		Assert.DoesNotContain(search.Options, option => option.Name == "--compress-code");
	}

	[Fact]
	public void SearchCommandAcceptsEveryPublishedOption()
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();

		var parsed = root.Parse([
			"search", "Widget.*Run", ".", "--regex", "--max", "17",
			"--search-body-chars", "900", "--profile", "standard", "--root", "src",
			"--select", "src/App.cs", "--exclude", "none", "--git-mode", "none",
			"--hide-secrets", "--format", "markdown", "--output", "result.md", "--plain"
		]);

		Assert.Empty(parsed.Errors);
		Assert.Equal("search", parsed.CommandResult.Command.Name);
	}

	[Theory]
	[InlineData("--max", "0")]
	[InlineData("--max", "201")]
	[InlineData("--search-body-chars", "0")]
	[InlineData("--search-body-chars", "16001")]
	public void SearchCommandRejectsValuesOutsidePublishedLimits(string option, string value)
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();

		var parsed = root.Parse(["search", "needle", ".", option, value]);

		Assert.NotEmpty(parsed.Errors);
	}

	[Fact]
	public void SearchCommandRejectsCompetingModes()
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();

		var parsed = root.Parse(["search", "needle", ".", "--regex", "--symbols"]);

		Assert.NotEmpty(parsed.Errors);
	}

	[Theory]
	[InlineData("search", false)]
	[InlineData("analyze", true)]
	public async Task DestinationConflictHintOffersForceOnlyWhereItExists(string command, bool forceAvailable)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/App.cs", "internal sealed class Widget {}\n");
		var destination = Path.Combine(workspace.Path, "existing.txt");
		File.WriteAllText(destination, "keep");
		string[] arguments = command == "search"
			? ["search", "Widget", project, "-o", destination]
			: ["analyze", project, "-o", destination];
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")))
			.RunAsync(
				[.. arguments, "--git-mode", "none", "--exclude", "none", "--language", "en"],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.DestinationConflict, exitCode);
		Assert.Contains("DPX-EXPORT-DESTINATION-EXISTS", environment.StandardError, StringComparison.Ordinal);
		Assert.Equal(forceAvailable, environment.StandardError.Contains("--force", StringComparison.Ordinal));
		Assert.Equal("keep", File.ReadAllText(destination));
	}
}
