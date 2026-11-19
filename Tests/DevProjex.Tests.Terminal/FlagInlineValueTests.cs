using System.CommandLine;

namespace DevProjex.Tests.Terminal;

public sealed class FlagInlineValueTests
{
	public static TheoryData<string[], string> FlagsWithInlineValues => new()
	{
		{ ["analyze", ".", "--plain=false"], "--plain" },
		{ ["analyze", ".", "--plain:false"], "--plain" },
		{ ["analyze", ".", "--plain=true"], "--plain" },
		{ ["analyze", ".", "--plain=x"], "--plain" },
		{ ["analyze", ".", "--quiet=false"], "-q" },
		{ ["analyze", ".", "-q=false"], "-q" },
		{ ["analyze", ".", "-q:false"], "-q" },
		{ ["analyze", ".", "--strict=false"], "--strict" },
		{ ["export", "context", ".", "-o", "../out.md", "--force=false"], "--force" },
		{ ["export", "context", ".", "--dry-run=false"], "--dry-run" },
		{ ["export", "context", ".", "-n=false"], "--dry-run" },
		{ ["export", "context", ".", "-n:0"], "--dry-run" },
		{ ["search", "needle", ".", "--regex=false"], "--regex" },
		{ ["mcp", "--live=false"], "--live" },
		{ ["--plain=false", "analyze", "."], "--plain" },
		{ ["analyze", ".", "--plain=="], "--plain" },
		{ ["analyze", ".", "--plain=:"], "--plain" }
	};

	public static TheoryData<string[]> AcceptedForms => new()
	{
		{ ["analyze", ".", "--plain"] },
		{ ["analyze", ".", "-q"] },
		{ ["analyze", ".", "--hide-secrets=on"] },
		{ ["analyze", ".", "--hide-secrets:off"] },
		{ ["analyze", ".", "--hide-secrets=false"] },
		{ ["analyze", ".", "--hide-secrets", "on"] },
		{ ["analyze", ".", "--compress-code=on"] },
		{ ["analyze", ".", "--strip-comments:true"] },
		{ ["analyze", ".", "--strip-blank-lines=off"] },
		{ ["analyze", ".", "--hide-private-data=off"] },
		{ ["mcp", "--unrestricted=false"] },
		{ ["mcp", "--unrestricted:on"] },
		{ ["analyze", ".", "--color=always"] },
		{ ["analyze", ".", "--color:never"] },
		{ ["analyze", ".", "--format=json"] },
		{ ["analyze", ".", "-f:json"] },
		{ ["analyze", ".", "--output=-"] },
		{ ["analyze", "--", "--plain=false"] },
		{ ["search", "--", "--quiet=false", "."] },
		{ ["analyze", ".", "--Plain=false"] },
		{ ["analyze", ".", "--unknown=false"] }
	};

	[Theory]
	[MemberData(nameof(FlagsWithInlineValues))]
	public void InlineValueOnAFlagIsUnexpected(string[] arguments, string expectedOption)
	{
		var (detected, error) = Check(arguments);

		Assert.True(detected, string.Join(' ', arguments));
		Assert.Equal(CliInputIntegrityErrorKind.UnexpectedFlagValue, error.Kind);
		Assert.Equal(expectedOption, error.SymbolName);
	}

	[Theory]
	[MemberData(nameof(AcceptedForms))]
	public void ValueTakingOptionsTogglesUnknownTokensAndDelimitedDataAreNotFlagged(string[] arguments)
	{
		var (detected, error) = Check(arguments);

		Assert.False(detected, $"{string.Join(' ', arguments)} -> {error}");
	}

	[Theory]
	[InlineData("--format:")]
	[InlineData("-f:")]
	[InlineData("--format=")]
	public void EmptyInlineValueAfterEitherDelimiterIsMissing(string token)
	{
		var (detected, error) = Check(["analyze", ".", token, "json"]);

		Assert.True(detected);
		Assert.Equal(CliInputIntegrityErrorKind.MissingOptionValue, error.Kind);
		Assert.Equal("--format", error.SymbolName);
	}

	[Fact]
	public void EveryPublicBoolFlagRejectsBothInlineValueForms()
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		var checkedFlags = 0;
		foreach (var (command, path) in EnumeratePublicCommands(root))
		{
			foreach (var option in command.Options.Where(static option =>
						 !option.Hidden && CliInputIntegrityGuard.IsFlag(option)))
			{
				foreach (var identifier in new[] { option.Name }.Concat(option.Aliases))
				{
					foreach (var token in new[] { $"{identifier}=false", $"{identifier}:true" })
					{
						string[] arguments = [.. path, token];
						var parseResult = root.Parse(arguments);

						var detected = CliInputIntegrityGuard.TryFindError(
							arguments,
							root,
							parseResult,
							out var error);

						Assert.True(detected, string.Join(' ', arguments));
						Assert.Equal(CliInputIntegrityErrorKind.UnexpectedFlagValue, error.Kind);
						Assert.Equal(option.Name, error.SymbolName);
						checkedFlags++;
					}
				}
			}
		}

		Assert.True(checkedFlags > 50, $"Only {checkedFlags} flag forms were checked.");
	}

	[Theory]
	[InlineData("--hide-secrets")]
	[InlineData("--hide-private-data")]
	[InlineData("--compress-code")]
	[InlineData("--strip-comments")]
	[InlineData("--strip-blank-lines")]
	[InlineData("--unrestricted")]
	public void DocumentedOnOffTogglesAreNotFlags(string name)
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		var options = EnumeratePublicCommands(root)
			.SelectMany(static item => item.Command.Options)
			.Where(option => option.Name == name)
			.ToArray();

		Assert.NotEmpty(options);
		Assert.All(options, static option => Assert.False(CliInputIntegrityGuard.IsFlag(option)));
	}

	[Theory]
	[InlineData("analyze|.|--plain=false", "--plain")]
	[InlineData("analyze|.|--quiet:false", "-q")]
	[InlineData("export|context|.|--dry-run=false", "--dry-run")]
	[InlineData("export|context|.|-o|../x.md|--force=false", "--force")]
	public async Task ApplicationReportsInvalidSyntaxBeforeRunning(string invocation, string option)
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			[.. invocation.Split('|'), "--language", "en"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains(
			$"error[DPX-CLI-INVALID-SYNTAX]: The option does not accept a value: {option}",
			environment.StandardError,
			StringComparison.Ordinal);
	}

	private static (bool Detected, CliInputIntegrityError Error) Check(string[] arguments)
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		var parseResult = root.Parse(arguments);
		var detected = CliInputIntegrityGuard.TryFindError(arguments, root, parseResult, out var error);
		return (detected, error);
	}

	private static IEnumerable<(Command Command, IReadOnlyList<string> Path)> EnumeratePublicCommands(
		Command command,
		IReadOnlyList<string>? path = null)
	{
		path ??= [];
		yield return (command, path);
		foreach (var child in command.Subcommands.Where(static child => !child.Hidden))
		{
			foreach (var item in EnumeratePublicCommands(child, [.. path, child.Name]))
				yield return item;
		}
	}
}
