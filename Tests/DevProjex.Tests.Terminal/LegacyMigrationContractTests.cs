namespace DevProjex.Tests.Terminal;

public sealed class LegacyMigrationContractTests
{
	public static TheoryData<string, string[]> MajorLegacyActions => new()
	{
		{
			"--path ./app --report -",
			["devprojex", "analyze", "./app", "--format", "json", "-o", "-"]
		},
		{
			"./app --export tree-content -o context.txt",
			[
				"devprojex", "export", "context", "./app",
				"--view", "tree-content", "--format", "text", "-o", "context.txt"
			]
		},
		{
			"./app --copy zip -o app.zip",
			["devprojex", "export", "project", "./app", "--as", "zip", "-o", "app.zip"]
		}
	};

	[Theory]
	[MemberData(nameof(MajorLegacyActions))]
	public async Task MajorLegacyActionsReturnExactStructuredArgumentVector(
		string commandLine,
		string[] expectedArguments)
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment)
			.RunAsync(
				commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries),
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Contains("DPX-CLI-LEGACY-SYNTAX", environment.StandardError, StringComparison.Ordinal);
		Assert.Equal(expectedArguments, ReadArgumentVector(environment.StandardError));
		Assert.Empty(environment.StandardOutput);
	}

	[Theory]
	[InlineData("--ignore", "git-ignore", "--git-mode", "gitignore")]
	[InlineData("--ignore=git-tracked-only", null, "--git-mode", "tracked")]
	[InlineData("--ignore", "smart-ignore", "--exclude", "smart-ignore")]
	public async Task LegacyIgnoreValuesMapToSeparatedV1Options(
		string option,
		string? value,
		string expectedOption,
		string expectedValue)
	{
		var environment = new TestTerminalEnvironment();
		var arguments = new List<string> { "--path", ".", "--report", "-" };
		arguments.Add(option);
		if (value is not null)
			arguments.Add(value);

		var exitCode = await new TerminalApplication(environment)
			.RunAsync(arguments, TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		var replacement = ReadArgumentVector(environment.StandardError);
		var optionIndex = Array.IndexOf(replacement, expectedOption);
		Assert.True(optionIndex >= 0);
		Assert.Equal(expectedValue, replacement[optionIndex + 1]);
	}

	[Fact]
	public async Task StructuredMigrationPreservesMetacharactersUnicodeAndLanguage()
	{
		const string project = "project space'$HOME&%DPX%!^()`Ж";
		const string output = "output space'$HOME&%DPX%!^()`Ж.zip";
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment)
			.RunAsync(
				[
					"--language=RU",
					"--copy=ZIP",
					"--path",
					project,
					"-o",
					output
				],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Equal(
			[
				"devprojex", "export", "project", project,
				"--as", "zip", "-o", output, "--language", "ru"
			],
			ReadArgumentVector(environment.StandardError));
		Assert.DoesNotContain(
			$"project {project} --as",
			environment.StandardError,
			StringComparison.Ordinal);
		Assert.Empty(environment.StandardOutput);
	}

	[Theory]
	[InlineData("--copy")]
	[InlineData("--copy tar -o output.tar")]
	[InlineData("--copy zip --copy folder -o output")]
	[InlineData("--copyish zip -o output.zip")]
	[InlineData("--copy zip")]
	[InlineData("--report - --format text")]
	public async Task AmbiguousOrIncompleteLegacyShapeDoesNotInventAReplacement(
		string commandLine)
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment)
			.RunAsync(
				commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries),
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.DoesNotContain(
			"DPX-CLI-LEGACY-SYNTAX",
			environment.StandardError,
			StringComparison.Ordinal);
		Assert.Empty(ReadArgumentVector(environment.StandardError));
		Assert.Empty(environment.StandardOutput);
	}

	[Theory]
	[InlineData("--copy|zip|-o|../out.zip|--|trailing-data")]
	[InlineData("--path|.|--report|-|--")]
	[InlineData("--|--copy|zip|-o|../out.zip")]
	public async Task DelimiterMakesTheWholeInvocationIneligibleForLegacyMigration(
		string invocation)
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment)
			.RunAsync(
				invocation.Split('|'),
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.DoesNotContain(
			"DPX-CLI-LEGACY-SYNTAX",
			environment.StandardError,
			StringComparison.Ordinal);
		Assert.Empty(ReadArgumentVector(environment.StandardError));
		Assert.Empty(environment.StandardOutput);
	}

	private static string[] ReadArgumentVector(string output) =>
		output
			.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
			.Select(static line => line.Trim())
			.Where(static line => line.StartsWith("argv[", StringComparison.Ordinal))
			.Select(static line =>
			{
				var separator = line.IndexOf(" = ", StringComparison.Ordinal);
				Assert.True(separator > 0, $"Malformed argument-vector line: {line}");
				return JsonSerializer.Deserialize<string>(line[(separator + 3)..])!;
			})
			.ToArray();
}
