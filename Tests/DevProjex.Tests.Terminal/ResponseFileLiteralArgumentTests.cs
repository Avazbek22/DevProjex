namespace DevProjex.Tests.Terminal;

public sealed class ResponseFileLiteralArgumentTests
{
	[Theory]
	[InlineData("@Override")]
	[InlineData("@")]
	[InlineData("@@double")]
	[InlineData("@scope/pkg")]
	public void LeadingAtSignIsKeptAsArgumentData(string pattern)
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();

		var parseResult = root.Parse(["search", pattern, "."]);

		Assert.Empty(parseResult.Errors);
		Assert.Equal(pattern, parseResult.GetValue<string>("PATTERN"));
	}

	[Fact]
	public void ExistingFileNamedByAnAtArgumentIsNotRead()
	{
		using var workspace = new TemporaryDirectory();
		var responseFile = workspace.WriteFile("arguments.rsp", "--help\n--version\n");
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();

		var parseResult = root.Parse(["search", "@" + responseFile, "."]);

		Assert.Empty(parseResult.Errors);
		Assert.Equal("@" + responseFile, parseResult.GetValue<string>("PATTERN"));
		Assert.Equal("search", parseResult.CommandResult.Command.Name);
	}

	[Fact]
	public void AtPrefixedSelectedPathIsAnOptionValue()
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();

		var parseResult = root.Parse(["tree", ".", "-s", "@scope/pkg", "--select", "@types/node"]);

		Assert.Empty(parseResult.Errors);
		Assert.Equal(
			["@scope/pkg", "@types/node"],
			parseResult.GetValue<string[]>("--select")!);
	}

	[Fact]
	public async Task ApplicationSearchesTheLiteralAtPatternInsteadOfTheFileContent()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/Widget.java", "class Widget {\n    String value = \"needle\";\n}\n");
		var responseFile = workspace.WriteFile("pattern.rsp", "needle\n");
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("data")))
			.RunAsync(
				[
					"search", "@" + responseFile, project,
					"--format", "json",
					"--git-mode", "none",
					"--exclude", "none",
					"--language", "en"
				],
				TestContext.Current.CancellationToken);

		Assert.True(exitCode == CommandLineExitCodes.Success, environment.StandardError);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		Assert.Empty(document.RootElement.GetProperty("matches").EnumerateArray());
	}

	[Fact]
	public void CompletionDoesNotExpandAnAtArgumentIntoFileContent()
	{
		using var workspace = new TemporaryDirectory();
		var responseFile = workspace.WriteFile("completion.rsp", "export\ncontext\n");
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		var expanded = "devprojex export context --vi";
		var literal = $"devprojex @{responseFile} --vi";

		var expandedCompletions = ContextAwareCompletionEngine.Complete(root, expanded, expanded.Length);
		var literalCompletions = ContextAwareCompletionEngine.Complete(root, literal, literal.Length);

		Assert.Contains("--view", expandedCompletions);
		Assert.DoesNotContain("--view", literalCompletions);
	}
}
