namespace DevProjex.Tests.Terminal;

public sealed class CliLanguageValidationTests
{
	[Theory]
	[InlineData("--help")]
	[InlineData("--version")]
	[InlineData("cache", "path")]
	[InlineData("export", "context", "--help")]
	public async Task UnsupportedLanguageCodeFailsForEveryInvocation(params string[] invocation)
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			["--language", "xx", .. invocation],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.StartsWith("error[DPX-CLI-INVALID-VALUE]: --language must be one of: en, ru,", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task UnsupportedInlineLanguageCodeFailsBeforeHelp()
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			["analyze", "--help", "--language=xx"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("[DPX-CLI-INVALID-VALUE]", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public void LanguageTokenAfterDelimiterIsNotValidated()
	{
		Assert.False(TerminalLanguageResolver.TryFindUnsupportedExplicitLanguage(
			["search", "--", "--language", "xx"],
			out _));
		Assert.True(TerminalLanguageResolver.TryFindUnsupportedExplicitLanguage(
			["--language", "xx", "--help"],
			out var value));
		Assert.Equal("xx", value);
	}
}
