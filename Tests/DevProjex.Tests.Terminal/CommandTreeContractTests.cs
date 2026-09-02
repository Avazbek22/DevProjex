using System.CommandLine;
using DevProjex.Terminal.DesktopControl;

namespace DevProjex.Tests.Terminal;

public sealed class CommandTreeContractTests
{
	[Fact]
	public void RootCommandExposesOnlyTheV1Hierarchy()
	{
		var environment = new TestTerminalEnvironment();
		var root = new DevProjexCommandTree(environment).Build();

		Assert.Equal(
			[
				"analyze", "cache", "completion", "doctor", "export", "help", "mcp", "open", "profile",
				"recent", "tree", "tui", "ui"
			],
			root.Subcommands
				.Where(static command => !command.Hidden)
				.Select(static command => command.Name)
				.OrderBy(static name => name, StringComparer.Ordinal));
		Assert.True(root.Subcommands.Single(static command => command.Name == "dev").Hidden);
		Assert.DoesNotContain(root.Options, static option => option.Name is "path" or "no-ui" or "silent");
	}

	[Fact]
	public async Task EveryPublicCommandHasStructuredPlainHelp()
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		foreach (var path in EnumeratePublicCommandPaths(root))
		{
			var environment = new TestTerminalEnvironment { Width = 80 };
			var arguments = path.Concat(["--language", "en", "--help"]).ToArray();
			var exitCode = await new TerminalApplication(environment)
				.RunAsync(arguments, TestContext.Current.CancellationToken);

			Assert.Equal(CommandLineExitCodes.Success, exitCode);
			Assert.Contains("USAGE", environment.StandardOutput, StringComparison.Ordinal);
			Assert.Contains("DESCRIPTION", environment.StandardOutput, StringComparison.Ordinal);
			Assert.Contains("EXAMPLES", environment.StandardOutput, StringComparison.Ordinal);
			Assert.Contains("EXIT CODES", environment.StandardOutput, StringComparison.Ordinal);
			Assert.Contains("--language", environment.StandardOutput, StringComparison.Ordinal);
			Assert.DoesNotContain("\u001b", environment.StandardOutput, StringComparison.Ordinal);
			Assert.Empty(environment.StandardError);
			Assert.All(
				environment.StandardOutput.Split(Environment.NewLine),
				static line => Assert.True(line.Length <= 82, $"Help line is too wide: {line}"));
		}
	}

	[Theory]
	[InlineData("export")]
	[InlineData("cache")]
	[InlineData("profile")]
	[InlineData("ui")]
	public async Task ParentCommandWithoutChildPrintsHelp(string command)
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment)
			.RunAsync([command, "--language", "en"], TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Contains("COMMANDS", environment.StandardOutput, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RootLevelArbitraryProjectPathIsRejected()
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment)
			.RunAsync(["some-project"], TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Contains("DPX-CLI-UNKNOWN-COMMAND", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task UnknownCommandProvidesDeterministicSuggestion()
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment)
			.RunAsync(["analze"], TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Contains("devprojex analyze", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task UnknownNestedCommandProvidesDeterministicSuggestion()
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment)
			.RunAsync(["export", "contex"], TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Contains("devprojex export context", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task UnknownOptionProvidesCommandScopedSuggestion()
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment)
			.RunAsync(["analyze", ".", "--formt", "json"], TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Contains("devprojex analyze --format", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ParserErrorsEscapeControlCharactersFromArguments()
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment)
			.RunAsync(
				["analyze", ".", "--forged\r\nline\t\u001b[31m", "--language", "en"],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Contains("--forged\\r\\nline\\t\\u001B[31m", environment.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain('\u001b', environment.StandardError);
		Assert.Equal(
			2,
			environment.StandardError
				.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
				.Length);
	}

	[Theory]
	[InlineData("--version")]
	[InlineData("-v")]
	public async Task VersionAliasesReturnTheSameCleanValue(string option)
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment)
			.RunAsync([option], TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Matches(@"^\d+\.\d+(?:\.\d+)?$", environment.StandardOutput.Trim());
		Assert.Empty(environment.StandardError);
	}

	[Theory]
	[InlineData("--help")]
	[InlineData("-h")]
	[InlineData("-?")]
	[InlineData("/?")]
	[InlineData("/h")]
	public async Task FrozenHelpAliasesReturnTheSameCleanLeafHelp(string option)
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment)
			.RunAsync(["analyze", option, "--language", "en"], TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Contains("devprojex analyze", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("--format <text|json>", environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("\u001b", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Empty(environment.StandardError);
	}

	[Fact]
	public async Task SingleDashVersionProducesOneTargetedErrorAndOneHint()
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment)
			.RunAsync(["-version", "--language", "en"], TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Equal(
			2,
			environment.StandardError
				.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
				.Length);
		Assert.Contains(
			"error[DPX-CLI-UNKNOWN-OPTION]: Unknown option: -version",
			environment.StandardError,
			StringComparison.Ordinal);
		Assert.Contains("devprojex --version", environment.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain("DPX-CLI-INVALID-SYNTAX", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task MissingOptionValueKeepsItsSpecificCategory()
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment)
			.RunAsync(["export", "project", "--as"], TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Contains("DPX-CLI-MISSING-VALUE", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ParserOwnedErrorIsLocalizedInsteadOfLeakingEnglishDetails()
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment)
			.RunAsync(["analze", "--language", "ru"], TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Contains(
			"Неизвестная команда: analze",
			environment.StandardError,
			StringComparison.Ordinal);
		Assert.DoesNotContain("Unrecognized", environment.StandardError, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("Required", environment.StandardError, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task LocalizedValidatorKeepsItsSpecificMessage()
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			["analyze", ".", "--exclude=none", "--exclude", "smart-ignore", "--language", "ru"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Contains(
			"--exclude none нельзя сочетать",
			environment.StandardError,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"Команда, параметр или значение указаны неверно.",
			environment.StandardError,
			StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("0")]
	[InlineData("1001")]
	public async Task AnalyzeTopFilesRejectsValuesOutsideThePublishedRange(string value)
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			["analyze", ".", "--top-files", value, "--language", "en"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Contains("--top-files must be between 1 and 1000.", environment.StandardError, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("1", 1L)]
	[InlineData("1024", 1024L)]
	[InlineData("1k", 1024L)]
	[InlineData("2KB", 2048L)]
	[InlineData("3mib", 3L * 1024 * 1024)]
	[InlineData("1G", 1024L * 1024 * 1024)]
	public void MaximumFileBytesAcceptsBytesAndBinarySuffixes(string token, long expected)
	{
		Assert.True(SelectionOptions.TryParseFileSize(token, out var actual));
		Assert.Equal(expected, actual);
	}

	[Theory]
	[InlineData("0")]
	[InlineData("-1")]
	[InlineData("1.5m")]
	[InlineData("1b")]
	[InlineData("9223372036854775807g")]
	public async Task MaximumFileBytesRejectsInvalidSizesWithLocalizedGuidance(string value)
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			["analyze", ".", "--max-file-bytes", value, "--language", "en"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Contains("--max-file-bytes must be at least 1 byte", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public void MaximumFileBytesIsScopedToThePublishedSelectionCommands()
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		var analyze = root.Subcommands.Single(static command => command.Name == "analyze");
		var tree = root.Subcommands.Single(static command => command.Name == "tree");
		var export = root.Subcommands.Single(static command => command.Name == "export");
		var context = export.Subcommands.Single(static command => command.Name == "context");
		var project = export.Subcommands.Single(static command => command.Name == "project");
		var profile = root.Subcommands.Single(static command => command.Name == "profile");
		var profileSave = profile.Subcommands.Single(static command => command.Name == "save");
		var open = root.Subcommands.Single(static command => command.Name == "open");

		Assert.Contains(analyze.Options, static option => option.Name == "--max-file-bytes");
		Assert.Contains(tree.Options, static option => option.Name == "--max-file-bytes");
		Assert.Contains(context.Options, static option => option.Name == "--max-file-bytes");
		Assert.DoesNotContain(project.Options, static option => option.Name == "--max-file-bytes");
		Assert.DoesNotContain(profileSave.Options, static option => option.Name == "--max-file-bytes");
		Assert.DoesNotContain(open.Options, static option => option.Name == "--max-file-bytes");
	}

	[Fact]
	public async Task KnownValidationErrorDoesNotAddIrrelevantCommandSuggestion()
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment)
			.RunAsync(["open", ".", "--last"], TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.DoesNotContain("Did you mean", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public void OpenLastDoesNotConflictWithTheImplicitCurrentDirectoryDefault()
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();

		var parseResult = root.Parse(["open", "--last"]);

		Assert.Empty(parseResult.Errors);
	}

	[Fact]
	public async Task HelpTokenAfterDelimiterIsTreatedAsAnArgument()
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment)
			.RunAsync(["analyze", "--", "--help"], TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.DoesNotContain("USAGE", environment.StandardOutput, StringComparison.Ordinal);
		Assert.Contains("DPX-PROJECT-NOT-FOUND", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public void DelimiterAllowsAProjectPathBeginningWithDash()
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		var analyze = root.Subcommands.Single(static command => command.Name == "analyze");
		var project = (Argument<string?>)analyze.Arguments.Single(
			static argument => argument.Name == "PROJECT");

		var parseResult = root.Parse(["analyze", "--", "-project"]);

		Assert.Empty(parseResult.Errors);
		Assert.Equal("-project", parseResult.GetValue(project));
	}

	[Fact]
	public async Task ExcludeNoneCannotBeCombinedWithAnotherExclusion()
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			["analyze", ".", "--exclude=none", "--exclude", "smart-ignore", "--language", "en"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Contains("--exclude none cannot be combined", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public void PublicOptionsHaveNoDuplicateAliasesWithinACommand()
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		var stack = new Stack<Command>();
		stack.Push(root);
		while (stack.Count > 0)
		{
			var command = stack.Pop();
			var aliases = command.Options
				.SelectMany(static option => option.Aliases)
				.ToArray();
			Assert.Equal(
				aliases.Length,
				aliases.Distinct(StringComparer.Ordinal).Count());
			foreach (var child in command.Subcommands)
				stack.Push(child);
		}
	}

	[Fact]
	public void PublicCommandTreeDoesNotExposeLegacyOrInternalTokens()
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		var publicTokens = EnumerateCommands(root)
			.Where(static command => !command.Hidden)
			.SelectMany(static command => command.Options.Where(static option => !option.Hidden))
			.SelectMany(static option => new[] { option.Name }.Concat(option.Aliases))
			.ToHashSet(StringComparer.Ordinal);

		Assert.DoesNotContain("--path", publicTokens);
		Assert.DoesNotContain("--no-ui", publicTokens);
		Assert.DoesNotContain("--silent", publicTokens);
		Assert.DoesNotContain("--report", publicTokens);
		Assert.DoesNotContain("--copy", publicTokens);
		Assert.DoesNotContain("--internal-elevation-attempted", publicTokens);
		Assert.DoesNotContain(
			DesktopLaunchRequestStore.InternalRequestArgument,
			publicTokens);
	}

	[Fact]
	public void ProjectDefaultsToCurrentDirectoryAndOutputAliasIsStable()
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		var analyze = root.Subcommands.Single(static command => command.Name == "analyze");
		var parseResult = root.Parse(["analyze", "--format=json"]);
		var project = analyze.Arguments.Single(static argument => argument.Name == "PROJECT");
		var output = analyze.Options.Single(static option => option.Name == "--output");

		Assert.Equal(Directory.GetCurrentDirectory(), parseResult.GetValue((Argument<string?>)project));
		Assert.Contains("-o", output.Aliases);
		Assert.DoesNotContain("-h", output.Aliases);
	}

	[Fact]
	public async Task UnicodeAndWhitespaceProjectPathWorksWithOptionsBeforeProject()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("Проект с пробелом");
		File.WriteAllText(Path.Combine(project, "app.cs"), "class App {}");
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")))
			.RunAsync(
			[
				"analyze",
				"--format=json",
				"--git-mode=none",
				"--exclude=none",
				project
			],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		Assert.Equal(
			Path.GetFullPath(project).Replace('\\', '/'),
			document.RootElement.GetProperty("project").GetProperty("root").GetString());
	}

	private static IEnumerable<IReadOnlyList<string>> EnumeratePublicCommandPaths(RootCommand root)
	{
		yield return [];
		var stack = new Stack<(Command Command, string[] Path)>();
		foreach (var command in root.Subcommands.Reverse())
		{
			if (!command.Hidden)
				stack.Push((command, [command.Name]));
		}

		while (stack.Count > 0)
		{
			var (command, path) = stack.Pop();
			yield return path;
			foreach (var child in command.Subcommands.Reverse())
			{
				if (!child.Hidden)
					stack.Push((child, [.. path, child.Name]));
			}
		}
	}

	private static IEnumerable<Command> EnumerateCommands(Command root)
	{
		var stack = new Stack<Command>();
		stack.Push(root);
		while (stack.Count > 0)
		{
			var command = stack.Pop();
			yield return command;
			foreach (var child in command.Subcommands)
				stack.Push(child);
		}
	}
}
