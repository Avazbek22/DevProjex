namespace DevProjex.Tests.Terminal;

public sealed class LegacyMigrationContractTests
{
	[Theory]
	[InlineData("--path ./app --report -", "devprojex analyze ./app --format json -o -")]
	[InlineData("./app --export tree-content -o context.txt",
		"devprojex export context ./app --view tree-content --format text -o context.txt")]
	[InlineData("./app --copy zip -o app.zip",
		"devprojex export project ./app --as zip -o app.zip")]
	public async Task MajorLegacyActionsReturnExactMigrationCommand(
		string commandLine,
		string replacement)
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment)
			.RunAsync(
				commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries),
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Contains("DPX-CLI-LEGACY-SYNTAX", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains(replacement, environment.StandardError, StringComparison.Ordinal);
		Assert.Empty(environment.StandardOutput);
	}

	[Theory]
	[InlineData("git-ignore", "--git-mode gitignore")]
	[InlineData("git-tracked-only", "--git-mode tracked")]
	[InlineData("smart-ignore", "--exclude smart-ignore")]
	public async Task LegacyIgnoreValuesMapToSeparatedV1Options(string legacy, string expected)
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment)
			.RunAsync(
				["--path", ".", "--report", "-", "--ignore", legacy],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Contains(expected, environment.StandardError, StringComparison.Ordinal);
	}
}
