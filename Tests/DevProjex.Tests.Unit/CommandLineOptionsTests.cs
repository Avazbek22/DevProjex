namespace DevProjex.Tests.Unit;

public sealed class CommandLineOptionsTests
{
	[Fact]
	public void Parse_ReturnsEmptySuccessfulResultWhenNoArgs()
	{
		var result = CommandLineOptions.Parse([]);

		Assert.True(result.Success);
		Assert.Equal(CommandLineOptions.Empty, result.Options);
	}

	[Fact]
	public void Parse_ReadsPathLanguageAndElevation()
	{
		var result = CommandLineOptions.Parse(["--path", "/tmp/root", "--lang", "ru", "--elevation-attempted"]);

		AssertValid(result);
		Assert.Equal("/tmp/root", result.Options.Path);
		Assert.Equal(AppLanguage.Ru, result.Options.Language);
		Assert.True(result.Options.ElevationAttempted);
	}

	[Fact]
	public void Parse_ReadsLegacyElevationAttemptedFlagForExistingRelaunches()
	{
		var result = CommandLineOptions.Parse(["--path", "/tmp/root", "--elevationAttempted"]);

		AssertValid(result);
		Assert.True(result.Options.ElevationAttempted);
	}

	[Fact]
	public void Parse_ReadsSinglePositionalPath()
	{
		var result = CommandLineOptions.Parse(["/tmp/root"]);

		AssertValid(result);
		Assert.Equal("/tmp/root", result.Options.Path);
	}

	[Fact]
	public void Parse_RejectsSecondPositionalPath()
	{
		var result = CommandLineOptions.Parse(["/tmp/one", "/tmp/two"]);

		AssertInvalid(result, "unexpected-argument");
		Assert.Equal("/tmp/one", result.Options.Path);
	}

	[Fact]
	public void Parse_RejectsUnknownOptions()
	{
		var result = CommandLineOptions.Parse(["--unknown", "value"]);

		AssertInvalid(result, "unknown-option");
		Assert.Null(result.Options.Path);
	}

	[Fact]
	public void Parse_RejectsMissingPathValueInsteadOfTreatingNextOptionAsPath()
	{
		var result = CommandLineOptions.Parse(["--path", "--lang"]);

		AssertInvalid(result, "missing-value");
		Assert.Null(result.Options.Path);
		Assert.Null(result.Options.Language);
	}

	[Fact]
	public void Parse_RejectsUnsupportedLanguage()
	{
		var result = CommandLineOptions.Parse(["--lang", "xx"]);

		AssertInvalid(result, "invalid-language");
		Assert.Null(result.Options.Language);
	}

	[Fact]
	public void Parse_ReadsReportWithoutExplicitPath()
	{
		var result = CommandLineOptions.Parse(["--path", "/tmp/root", "--report"]);

		AssertValid(result);
		Assert.True(result.Options.Report.Enabled);
		Assert.Null(result.Options.Report.Path);
		Assert.Equal(StartupReportFormat.Json, result.Options.Report.Format);
	}

	[Fact]
	public void Parse_ReadsReportPathFromReportOption()
	{
		var result = CommandLineOptions.Parse(["--path", "/tmp/root", "--report", "/tmp/report.json"]);

		AssertValid(result);
		Assert.True(result.Options.Report.Enabled);
		Assert.Equal("/tmp/report.json", result.Options.Report.Path);
	}

	[Fact]
	public void Parse_ReadsReportPathFromDedicatedOption()
	{
		var result = CommandLineOptions.Parse(["--path", "/tmp/root", "--report-path", "/tmp/report.json"]);

		AssertValid(result);
		Assert.True(result.Options.Report.Enabled);
		Assert.Equal("/tmp/report.json", result.Options.Report.Path);
	}

	[Fact]
	public void Parse_RejectsUnsupportedReportFormat()
	{
		var result = CommandLineOptions.Parse(["--path", "/tmp/root", "--report-format", "xml"]);

		AssertInvalid(result, "invalid-report-format");
		Assert.True(result.Options.Report.Enabled);
	}

	[Fact]
	public void Parse_ReadsNoUiAliasAndSelectionOverrides()
	{
		var result = CommandLineOptions.Parse([
			"--path", "/tmp/root",
			"--silent",
			"--include-root", "src",
			"--include-root", "tests",
			"--include-extension", "cs",
			"--include-extension", ".json",
			"--ignore", "smart-ignore",
			"--ignore", "gitignore",
			"--ignore", "smart-ignore"
		]);

		AssertValid(result);
		Assert.True(result.Options.NoUi);
		Assert.Equal(["src", "tests"], result.Options.IncludeRootFolders);
		Assert.Equal([".cs", ".json"], result.Options.IncludeExtensions);
		Assert.True(result.Options.IgnoreOptionsSpecified);
		Assert.Equal([IgnoreOptionId.SmartIgnore, IgnoreOptionId.UseGitIgnore], result.Options.IgnoreOptions);
	}

	[Fact]
	public void Parse_IgnoreNoneRepresentsExplicitEmptyIgnoreOverride()
	{
		var result = CommandLineOptions.Parse(["--path", "/tmp/root", "--ignore", "none"]);

		AssertValid(result);
		Assert.True(result.Options.IgnoreOptionsSpecified);
		Assert.Empty(result.Options.IgnoreOptions);
		Assert.True(result.Options.HasIgnoreOverrides);
	}

	[Fact]
	public void Parse_ReadsHelpAndVersion()
	{
		var help = CommandLineOptions.Parse(["-h"]);
		var version = CommandLineOptions.Parse(["--version"]);

		AssertValid(help);
		Assert.True(help.Options.ShowHelp);
		AssertValid(version);
		Assert.True(version.Options.ShowVersion);
	}

	[Fact]
	public void WithElevationAttempted_SetsFlag()
	{
		var result = CommandLineOptions.Empty.WithElevationAttempted();

		Assert.True(result.ElevationAttempted);
	}

	[Fact]
	public void ToArguments_QuotesPathsWithSpaces()
	{
		var options = new CommandLineOptions("/tmp/root folder", AppLanguage.En, true);

		var args = options.ToArguments();

		Assert.Contains("--path", args);
		Assert.Contains("\"/tmp/root folder\"", args);
		Assert.Contains("--lang", args);
		Assert.Contains("en", args);
		Assert.Contains("--elevation-attempted", args);
	}

	[Fact]
	public void ToArguments_PreservesReportAndSelectionOptionsForRelaunch()
	{
		var options = new CommandLineOptions("/tmp/root folder", AppLanguage.En, true)
		{
			NoUi = true,
			Report = new StartupReportOptions(true, "/tmp/report folder/report.json", StartupReportFormat.Json),
			IncludeRootFolders = ["src"],
			IncludeExtensions = [".cs"],
			IgnoreOptions = [IgnoreOptionId.DotFolders],
			IgnoreOptionsSpecified = true
		};

		var args = options.ToArguments();

		Assert.Contains("--no-ui", args);
		Assert.Contains("--report", args);
		Assert.Contains("\"/tmp/report folder/report.json\"", args);
		Assert.Contains("--include-root", args);
		Assert.Contains("src", args);
		Assert.Contains("--include-extension", args);
		Assert.Contains(".cs", args);
		Assert.Contains("--ignore", args);
		Assert.Contains("dot-folders", args);
	}

	[Fact]
	public void ToArguments_PreservesExplicitIgnoreNone()
	{
		var options = CommandLineOptions.Empty with
		{
			IgnoreOptionsSpecified = true,
			IgnoreOptions = []
		};

		var args = options.ToArguments();

		Assert.Contains("--ignore", args);
		Assert.Contains("none", args);
	}

	[Fact]
	public void ParseLanguage_ReturnsNullForUnknown()
	{
		Assert.Null(CommandLineOptions.ParseLanguage("xx"));
	}

	[Fact]
	public void LanguageToCode_UsesEnglishFallback()
	{
		var value = CommandLineOptions.LanguageToCode((AppLanguage)999);

		Assert.Equal("en", value);
	}

	[Fact]
	public void DetectSystemLanguage_ReturnsExpectedForCulture()
	{
		var original = CultureInfo.CurrentUICulture;
		try
		{
			CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");

			var detected = CommandLineOptions.DetectSystemLanguage();

			Assert.Equal(AppLanguage.Fr, detected);
		}
		finally
		{
			CultureInfo.CurrentUICulture = original;
		}
	}

	[Fact]
	public void ToArguments_ReturnsEmptyWhenNoOptions()
	{
		var args = CommandLineOptions.Empty.ToArguments();

		Assert.Equal(string.Empty, args);
	}

	[Fact]
	public void ParseLanguage_TrimsWhitespaceAndCase()
	{
		var result = CommandLineOptions.ParseLanguage(" RU ");

		Assert.Equal(AppLanguage.Ru, result);
	}

	[Fact]
	public void ToArguments_EscapesQuotesInPath()
	{
		var options = new CommandLineOptions("C:\\My \"Project\"", AppLanguage.En, false);

		var args = options.ToArguments();

		Assert.Contains("--path", args);
		Assert.Contains("\\\"", args);
	}

	private static void AssertValid(CommandLineParseResult result)
	{
		Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors.Select(static error => error.Message)));
		Assert.Empty(result.Errors);
	}

	private static void AssertInvalid(CommandLineParseResult result, string expectedCode)
	{
		Assert.False(result.Success);
		Assert.Contains(result.Errors, error => error.Code == expectedCode);
	}
}
