using DevProjex.Infrastructure.ResourceStore;

namespace DevProjex.Tests.Terminal;

/// <summary>
/// Every human-readable command error opens with the same header: the localized error label of
/// the selected interface language followed by the language-independent DPX code.
/// </summary>
public sealed class ErrorHeaderLocalizationTests
{
	public static TheoryData<string> InterfaceLanguages()
	{
		var languages = new TheoryData<string>();
		foreach (var token in CliChoiceSets.Language.Tokens)
			languages.Add(token);
		return languages;
	}

	[Theory]
	[MemberData(nameof(InterfaceLanguages))]
	public async Task ParserErrorHeaderFollowsTheInterfaceLanguage(string language)
	{
		using var workspace = new TemporaryDirectory();
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"analyze", workspace.Path, "--plain", "--color", "always", "--language", language);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		AssertHeader(environment.StandardError, language, "DPX-CLI-INVALID-VALUE");
	}

	[Theory]
	[MemberData(nameof(InterfaceLanguages))]
	public async Task DomainErrorHeaderFollowsTheInterfaceLanguage(string language)
	{
		using var workspace = new TemporaryDirectory();
		var environment = new TestTerminalEnvironment();

		var exitCode = await RunAsync(
			workspace,
			environment,
			"analyze", Path.Combine(workspace.Path, "missing-project"), "--language", language);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		AssertHeader(environment.StandardError, language, "DPX-PROJECT-NOT-FOUND");
	}

	[Theory]
	[InlineData("DPX-CLI-INVALID-VALUE", "mcp|connect|.|--client|bogus|--print")]
	[InlineData("DPX-CLI-INVALID-VALUE", "mcp|connect|.|--print|--open")]
	[InlineData("DPX-CLI-INVALID-VALUE", "search|needle|.|--regex|--symbols")]
	[InlineData("DPX-CLI-INVALID-VALUE", "analyze|.|-q|--verbosity|normal")]
	[InlineData("DPX-CLI-UNKNOWN-OPTION", "analyze|.|--bogus-option")]
	[InlineData("DPX-CLI-UNKNOWN-COMMAND", "help|nonsense")]
	[InlineData("DPX-CLI-MISSING-VALUE", "export|project|--as")]
	[InlineData("DPX-CLI-LEGACY-SYNTAX", "--path|.")]
	[InlineData("DPX-CLI-JOURNAL-SESSION-REQUIRED", "mcp|log|.|--format|markdown")]
	[InlineData("DPX-CLI-JOURNAL-NOT-FOUND", "mcp|log|.|--last")]
	[InlineData("DPX-TUI-NOT-INTERACTIVE", "tui|.")]
	public async Task EveryErrorPathPrintsTheRussianHeader(string code, string invocation)
	{
		using var workspace = new TemporaryDirectory();
		var environment = new TestTerminalEnvironment();
		var arguments = invocation
			.Split('|')
			.Select(argument => argument == "." ? workspace.Path : argument)
			.Select(argument => argument == "missing-profile.json"
				? Path.Combine(workspace.Path, argument)
				: argument)
			.ToArray();

		var exitCode = await RunAsync(workspace, environment, [.. arguments, "--language", "ru"]);

		Assert.NotEqual(CommandLineExitCodes.Success, exitCode);
		AssertHeader(environment.StandardError, "ru", code);
	}

	[Fact]
	public void TerminalSourcesDoNotHardCodeTheEnglishErrorHeader()
	{
		var terminalRoot = Path.Combine(PublishedApplicationLocator.FindRepositoryRoot(), "Apps", "Terminal");
		var offenders = Directory
			.EnumerateFiles(terminalRoot, "*.cs", SearchOption.AllDirectories)
			.Where(static file =>
				!file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
				!file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
			.Where(static file => File.ReadAllText(file).Contains("\"error[", StringComparison.Ordinal))
			.Select(file => Path.GetRelativePath(terminalRoot, file))
			.ToArray();

		Assert.True(
			offenders.Length == 0,
			"Use TerminalErrorHeader instead of a literal English header in: " + string.Join(", ", offenders));
	}

	private static void AssertHeader(string standardError, string language, string code)
	{
		Assert.True(CliChoiceSets.Language.TryParse(language, out var appLanguage));
		var label = new LocalizationService(new JsonLocalizationCatalog(), appLanguage)["Terminal.Label.Error"];
		Assert.StartsWith($"{label}[{code}]:", standardError, StringComparison.Ordinal);
		if (!string.Equals(label, "error", StringComparison.Ordinal))
			Assert.DoesNotContain("error[", standardError, StringComparison.Ordinal);
	}

	private static Task<int> RunAsync(
		TemporaryDirectory workspace,
		TestTerminalEnvironment environment,
		params string[] arguments) =>
		new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")))
			.RunAsync(arguments, TestContext.Current.CancellationToken);
}
