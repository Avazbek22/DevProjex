using System.CommandLine;

namespace DevProjex.Tests.Terminal;

public sealed class EmptyEqualsParserRegressionTests
{
	public static TheoryData<string[], string> EmptyAssignments =>
		new()
		{
			{
				["--language=", "en", "analyze", "."],
				"--language"
			},
			{
				["analyze", ".", "--format=", "json"],
				"--format"
			},
			{
				["analyze", ".", "--top-files=", "10"],
				"--top-files"
			},
			{
				["analyze", ".", "--profile=", "standard"],
				"--profile"
			},
			{
				["analyze", ".", "--root=", "src"],
				"--root"
			},
			{
				["analyze", ".", "--extension=", ".cs"],
				"--extension"
			},
			{
				["analyze", ".", "--select=", "src/App.cs"],
				"--select"
			},
			{
				["analyze", ".", "--git-mode=", "none"],
				"--git-mode"
			},
			{
				["analyze", ".", "--exclude=", "none"],
				"--exclude"
			},
			{
				["analyze", ".", "--max-file-bytes=", "1m"],
				"--max-file-bytes"
			},
			{
				["analyze", ".", "--color=", "never"],
				"--color"
			},
			{
				["analyze", ".", "--progress=", "never"],
				"--progress"
			},
			{
				["analyze", ".", "--verbosity=", "normal"],
				"--verbosity"
			},
			{
				["analyze", ".", "--output=", "--help"],
				"--output"
			},
			{
				["analyze", ".", "-o=", "--help"],
				"--output"
			},
			{
				["export", "context", ".", "--view=", "tree"],
				"--view"
			},
			{
				["tui", ".", "--screen=", "inline"],
				"--screen"
			},
			{
				["export", "project", ".", "--as=", "zip", "-o", "../out.zip"],
				"--as"
			},
			{
				["export", "project", ".", "--as", "zip", "--output=", "--force"],
				"--output"
			},
			{
				["open", ".", "--filter=", "--preview"],
				"--filter"
			},
			{
				["open", ".", "--tree-format=", "json"],
				"--tree-format"
			},
			{
				["open", ".", "--search=", "App"],
				"--search"
			},
			{
				["profile", "export", ".", "--output=", "--help"],
				"--output"
			},
			{
				["ui", "status", "--instance=", "--help"],
				"--instance"
			},
			{
				["ui", "status", "--project=", "."],
				"--project"
			},
			{
				["ui", "status", "--timeout=", "10s"],
				"--timeout"
			}
		};

	public static TheoryData<string[]> NonEmptyOrArgumentData =>
		new()
		{
			{ ["analyze", ".", "--format=json"] },
			{ ["analyze", ".", "--output=-"] },
			{ ["analyze", ".", "--output=--help"] },
			{ ["analyze", ".", "--output=="] },
			{ ["analyze", ".", "--unknown=", "--help"] },
			{ ["analyze", ".", "--Output=", "--help"] },
			{ ["analyze", ".", "--as=", "--help"] },
			{ ["analyze", "--", "--output="] }
		};

	private static readonly string[] ExpectedPublicValueOptionTokens =
	[
		"-b",
		"-e",
		"-f",
		"-o",
		"-p",
		"-r",
		"-s",
		"-x",
		"--as",
		"--branch",
		"--color",
		"--exclude",
		"--extension",
		"--filter",
		"--format",
		"--git-mode",
		"--instance",
		"--kind",
		"--language",
		"--limit",
		"--max-file-bytes",
		"--output",
		"--profile",
		"--progress",
		"--project",
		"--root",
		"--screen",
		"--search",
		"--select",
		"--select-from",
		"--timeout",
		"--top-files",
		"--tree-format",
		"--verbosity",
		"--view"
	];

	[Theory]
	[MemberData(nameof(EmptyAssignments))]
	public void EveryApplicableValueOptionRejectsAnEmptyInlineAssignment(
		string[] arguments,
		string expectedOption)
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		var parseResult = root.Parse(arguments);

		var detected = CliInputIntegrityGuard.TryFindError(
			arguments,
			root,
			parseResult,
			out var error);

		Assert.True(detected);
		Assert.Equal(CliInputIntegrityErrorKind.MissingOptionValue, error.Kind);
		Assert.Equal(expectedOption, error.SymbolName);
	}

	[Fact]
	public void RequiredValueFollowedByARecognizedOptionIsMissing()
	{
		string[] arguments = ["export", "project", "--as", "--language", "en"];
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		var parseResult = root.Parse(arguments);

		var detected = CliInputIntegrityGuard.TryFindError(
			arguments,
			root,
			parseResult,
			out var error);

		Assert.True(detected);
		Assert.Equal(CliInputIntegrityErrorKind.MissingOptionValue, error.Kind);
		Assert.Equal("--as", error.SymbolName);
	}

	[Fact]
	public void PublicValueOptionTokensAndAritiesAreFrozen()
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		var actual = EnumeratePublicCommands(root)
			.SelectMany(static item => item.Command.Options)
			.Where(static option =>
				!option.Hidden &&
				option.Arity.MinimumNumberOfValues > 0)
			.SelectMany(static option => new[] { option.Name }.Concat(option.Aliases))
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal)
			.ToArray();

		Assert.Equal(
			ExpectedPublicValueOptionTokens.Order(StringComparer.Ordinal),
			actual);
	}

	[Fact]
	public void EveryPublicOptionAndAliasIsCoveredByTheInlineAssignmentGuard()
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		foreach (var (command, path) in EnumeratePublicCommands(root))
		{
			foreach (var option in command.Options.Where(static option =>
				         !option.Hidden))
			{
				foreach (var identifier in new[] { option.Name }.Concat(option.Aliases))
				{
					var arguments = path
						.Concat([$"{identifier}=", "__next"])
						.ToArray();
					var parseResult = root.Parse(arguments);

					var detected = CliInputIntegrityGuard.TryFindError(
						arguments,
						root,
						parseResult,
						out var error);

					Assert.True(
						detected,
						$"Empty inline assignment was not covered: {string.Join(' ', arguments)}");
					Assert.Equal(option.Name, error.SymbolName);
					Assert.Equal(
						option.Arity.MinimumNumberOfValues > 0
							? CliInputIntegrityErrorKind.MissingOptionValue
							: CliInputIntegrityErrorKind.UnexpectedFlagValue,
						error.Kind);
				}
			}
		}
	}

	[Theory]
	[MemberData(nameof(NonEmptyOrArgumentData))]
	public void GuardDoesNotReinterpretValidValuesUnknownOptionsOrDelimiterData(
		string[] arguments)
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		var parseResult = root.Parse(arguments);

		var detected = CliInputIntegrityGuard.TryFindError(
			arguments,
			root,
			parseResult,
			out var error);

		Assert.False(detected);
		Assert.Equal(default, error);
	}

	[Fact]
	public async Task ApplicationReturnsLocalizedMissingValueBeforeHelpOrAction()
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			["ui", "status", "--instance=", "--help", "--language", "en"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains(
			"error[DPX-CLI-MISSING-VALUE]: A value is required for option: --instance",
			environment.StandardError,
			StringComparison.Ordinal);
		Assert.DoesNotContain("USAGE", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task EqualsSuffixOnFlagIsInvalidSyntaxInsteadOfFlagPresence()
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			["analyze", ".", "--plain=", "--help", "--language", "en"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains(
			"error[DPX-CLI-INVALID-SYNTAX]: The option does not accept a value: --plain",
			environment.StandardError,
			StringComparison.Ordinal);
		Assert.DoesNotContain("USAGE", environment.StandardError, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("ui|status|--instance||--help", "DPX-CLI-MISSING-VALUE", "--instance")]
	[InlineData("analyze|.|--format||--help", "DPX-CLI-MISSING-VALUE", "--format")]
	[InlineData("ui|filter|set||--help", "DPX-CLI-INVALID-SYNTAX", "QUERY")]
	[InlineData("analyze||--help", "DPX-CLI-INVALID-SYNTAX", "PROJECT")]
	public async Task ExplicitEmptyArgvValuesAreUsageErrors(
		string invocation,
		string expectedCode,
		string expectedSymbol)
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			invocation.Split('|'),
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains($"error[{expectedCode}]", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains(expectedSymbol, environment.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain("USAGE", environment.StandardError, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("analyze|.|--Output=|--help", "DPX-CLI-UNKNOWN-OPTION")]
	[InlineData("analyze|.|--as=|--help", "DPX-CLI-UNKNOWN-OPTION")]
	[InlineData("nonsense|--language=", "DPX-CLI-MISSING-VALUE")]
	[InlineData("analyze|.|--unknown|--output=", "DPX-CLI-MISSING-VALUE")]
	public async Task IntegrityErrorPrecedenceAndCommandScopeAreStable(
		string invocation,
		string expectedCode)
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			[.. invocation.Split('|'), "--language", "en"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains($"error[{expectedCode}]", environment.StandardError, StringComparison.Ordinal);
	}

	private static IEnumerable<(Command Command, IReadOnlyList<string> Path)>
		EnumeratePublicCommands(
			Command command,
			IReadOnlyList<string>? path = null)
	{
		path ??= [];
		yield return (command, path);
		foreach (var child in command.Subcommands.Where(static child => !child.Hidden))
		{
			foreach (var result in EnumeratePublicCommands(
				         child,
				         [.. path, child.Name]))
			{
				yield return result;
			}
		}
	}
}
