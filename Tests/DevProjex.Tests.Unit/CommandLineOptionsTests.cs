namespace DevProjex.Tests.Unit;

[Trait("Category", "TerminalCommand")]
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
	public void Parse_ReadsInlineOptionValues()
	{
		var result = CommandLineOptions.Parse([
			"--path=/tmp/root",
			"--lang=ru",
			"--report-path=/tmp/report.json",
			"--report-format=json",
			"--export=tree-content",
			"--output=/tmp/context.txt",
			"--export-format=json",
			"--include-root=src",
			"--include-extension=cs",
			"--ignore=dot-folders"
		]);

		AssertValid(result);
		Assert.Equal("/tmp/root", result.Options.Path);
		Assert.Equal(AppLanguage.Ru, result.Options.Language);
		Assert.True(result.Options.Report.Enabled);
		Assert.Equal("/tmp/report.json", result.Options.Report.Path);
		Assert.Equal(StartupReportFormat.Json, result.Options.Report.Format);
		Assert.False(result.Options.Benchmark.Enabled);
		Assert.True(result.Options.Export.Enabled);
		Assert.Equal(StartupExportMode.TreeContent, result.Options.Export.Mode);
		Assert.Equal("/tmp/context.txt", result.Options.Export.Path);
		Assert.Equal(TreeTextFormat.Json, result.Options.Export.Format);
		Assert.True(result.Options.Export.FormatSpecified);
		Assert.Equal(["src"], result.Options.IncludeRootFolders);
		Assert.Equal([".cs"], result.Options.IncludeExtensions);
		Assert.Equal([IgnoreOptionId.DotFolders], result.Options.IgnoreOptions);
	}

	[Theory]
	[MemberData(nameof(InlineEquivalentValueOptions))]
	public void Parse_InlineValueSyntaxMatchesSeparatedValueSyntax(string optionName, string value)
	{
		var separated = CommandLineOptions.Parse([optionName, value]);
		var inline = CommandLineOptions.Parse([$"{optionName}={value}"]);

		AssertValid(separated);
		AssertValid(inline);
		AssertEquivalentOptions(separated.Options, inline.Options);
	}

	[Fact]
	public void Parse_ReadsInlineReportPathFromReportOption()
	{
		var result = CommandLineOptions.Parse(["--report=/tmp/report.json"]);

		AssertValid(result);
		Assert.True(result.Options.Report.Enabled);
		Assert.Equal("/tmp/report.json", result.Options.Report.Path);
	}

	[Fact]
	public void Parse_EmptyInlineReportValueUsesDefaultReportPath()
	{
		var result = CommandLineOptions.Parse(["--report="]);

		AssertValid(result);
		Assert.True(result.Options.Report.Enabled);
		Assert.Null(result.Options.Report.Path);
	}

	[Fact]
	public void Parse_InlineReportPathMayStartWithDash()
	{
		var result = CommandLineOptions.Parse(["--report=--report.json"]);

		AssertValid(result);
		Assert.True(result.Options.Report.Enabled);
		Assert.Equal("--report.json", result.Options.Report.Path);
	}

	[Fact]
	public void Parse_InlineValuePreservesEqualsInsidePath()
	{
		var result = CommandLineOptions.Parse(["--path=/tmp/root=name"]);

		AssertValid(result);
		Assert.Equal("/tmp/root=name", result.Options.Path);
	}

	[Fact]
	public void Parse_InlineRequiredValueMayStartWithDash()
	{
		var result = CommandLineOptions.Parse(["--path=--folder"]);

		AssertValid(result);
		Assert.Equal("--folder", result.Options.Path);
	}

	[Fact]
	public void Parse_RejectsEmptyInlineRequiredValue()
	{
		var result = CommandLineOptions.Parse(["--path="]);

		AssertInvalid(result, "missing-value");
		Assert.Null(result.Options.Path);
	}

	[Fact]
	public void Parse_UnknownInlineOptionDoesNotConsumeFollowingPositionalPath()
	{
		var result = CommandLineOptions.Parse(["--unknown=value", "/tmp/root"]);

		AssertInvalid(result, "unknown-option");
		Assert.Equal("/tmp/root", result.Options.Path);
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
	public void Parse_ReadsRootedPositionalPathThatLooksLikeUnixPath()
	{
		var result = CommandLineOptions.Parse(["/tmp/no-ui"]);

		AssertValid(result);
		Assert.Equal("/tmp/no-ui", result.Options.Path);
	}

	[Theory]
	[InlineData("no-ui", CommandLineOptionTokens.NoUi)]
	[InlineData("no_ui", CommandLineOptionTokens.NoUi)]
	[InlineData("noui", CommandLineOptionTokens.NoUi)]
	[InlineData("no-uii", CommandLineOptionTokens.NoUi)]
	[InlineData("-no-ui", CommandLineOptionTokens.NoUi)]
	[InlineData("-no-uii", CommandLineOptionTokens.NoUi)]
	[InlineData("silent", CommandLineOptionTokens.Silent)]
	[InlineData("help", CommandLineOptionTokens.Help)]
	[InlineData("version", CommandLineOptionTokens.Version)]
	[InlineData("export", CommandLineOptionTokens.Export)]
	[InlineData("report", CommandLineOptionTokens.Report)]
	[InlineData("tree-format", CommandLineOptionTokens.TreeFormat)]
	[InlineData("tree-formt", CommandLineOptionTokens.TreeFormat)]
	[InlineData("preview-search", CommandLineOptionTokens.PreviewSearch)]
	public void Parse_RejectsKnownLongOptionNamesWithoutPrefix(string value, string expectedSuggestion)
	{
		var result = CommandLineOptions.Parse([value]);

		AssertInvalid(result, "missing-option-prefix");
		Assert.Null(result.Options.Path);
		var error = Assert.Single(result.Errors);
		Assert.Contains($"Did you mean '{expectedSuggestion}'?", error.Message, StringComparison.Ordinal);
		Assert.Contains($"Use --path {value}", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Parse_CommandStyleExportReportsMissingOptionPrefixInsteadOfLaunchingUi()
	{
		var result = CommandLineOptions.Parse(["export", "tree"]);

		AssertInvalid(result, "missing-option-prefix");
		Assert.Contains(
			result.Errors,
			static error => error.Message.Contains("Did you mean '--export'?", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData("/no-ui", CommandLineOptionTokens.NoUi)]
	[InlineData("/no_ui", CommandLineOptionTokens.NoUi)]
	[InlineData("/no-uii", CommandLineOptionTokens.NoUi)]
	[InlineData("/help", CommandLineOptionTokens.Help)]
	[InlineData("/version", CommandLineOptionTokens.Version)]
	[InlineData("/export", CommandLineOptionTokens.Export)]
	[InlineData("/preview-serch", CommandLineOptionTokens.PreviewSearch)]
	public void Parse_RejectsSlashStyleOptionTyposWithoutTreatingThemAsPaths(string value, string expectedSuggestion)
	{
		var result = CommandLineOptions.Parse([value]);

		AssertInvalid(result, "missing-option-prefix");
		Assert.Null(result.Options.Path);
		var error = Assert.Single(result.Errors);
		Assert.Contains($"Did you mean '{expectedSuggestion}'?", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Parse_AllowsKnownOptionNameAsExplicitPathValue()
	{
		var result = CommandLineOptions.Parse([CommandLineOptionTokens.Path, "no-ui"]);

		AssertValid(result);
		Assert.Equal("no-ui", result.Options.Path);
	}

	[Theory]
	[InlineData("./no-ui")]
	[InlineData("../report")]
	[InlineData(".\\silent")]
	public void Parse_AllowsPathLikePositionalValuesThatResembleOptionNames(string value)
	{
		var result = CommandLineOptions.Parse([value]);

		AssertValid(result);
		Assert.Equal(value, result.Options.Path);
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

	[Theory]
	[InlineData("--help=true")]
	[InlineData("-h=true")]
	[InlineData("--version=true")]
	[InlineData("--no-ui=true")]
	[InlineData("--silent=false")]
	[InlineData("--strict=true")]
	[InlineData("--last=true")]
	[InlineData("--preview=true")]
	[InlineData("--elevation-attempted=true")]
	public void Parse_RejectsInlineValuesForValueLessFlags(string value)
	{
		var result = CommandLineOptions.Parse([value]);

		AssertInvalid(result, "unexpected-value");
		var error = Assert.Single(result.Errors);
		Assert.Contains("does not accept a value", error.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("--no-uii", CommandLineOptionTokens.NoUi)]
	[InlineData("--no_ui", CommandLineOptionTokens.NoUi)]
	[InlineData("--noui", CommandLineOptionTokens.NoUi)]
	[InlineData("--silet", CommandLineOptionTokens.Silent)]
	[InlineData("--prevew", CommandLineOptionTokens.Preview)]
	[InlineData("--preview-serch", CommandLineOptionTokens.PreviewSearch)]
	[InlineData("--tree-fomat", CommandLineOptionTokens.TreeFormat)]
	[InlineData("--export-formt", CommandLineOptionTokens.ExportFormat)]
	public void Parse_UnknownOptionSuggestsClosestKnownLongOption(string value, string expectedSuggestion)
	{
		var result = CommandLineOptions.Parse([value]);

		AssertInvalid(result, "unknown-option");
		var error = Assert.Single(result.Errors);
		Assert.Contains($"Did you mean '{expectedSuggestion}'?", error.Message, StringComparison.Ordinal);
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
	public void Parse_ReadsJsonReportFormat()
	{
		var result = CommandLineOptions.Parse(["--path", "/tmp/root", "--report-format", "json"]);

		AssertValid(result);
		Assert.True(result.Options.Report.Enabled);
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
	public void Parse_ReadsStandardOutputReportTarget()
	{
		var result = CommandLineOptions.Parse(["--path", "/tmp/root", "--report", CommandLineOptionTokens.StandardOutputReportPath]);

		AssertValid(result);
		Assert.True(result.Options.Report.Enabled);
		Assert.True(result.Options.Report.WriteToStandardOutput);
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
	public void Parse_ReadsBenchmarkPathAndOutput()
	{
		var result = CommandLineOptions.Parse([
			CommandLineOptionTokens.Benchmark, "/tmp/root",
			CommandLineOptionTokens.BenchmarkOutput, "/tmp/result.json"
		]);

		AssertValid(result);
		Assert.True(result.Options.Benchmark.Enabled);
		Assert.Equal("/tmp/root", result.Options.Benchmark.Path);
		Assert.Equal("/tmp/result.json", result.Options.Benchmark.OutputPath);
	}

	[Fact]
	public void Parse_ReadsInlineBenchmarkValues()
	{
		var result = CommandLineOptions.Parse([
			$"{CommandLineOptionTokens.Benchmark}=/tmp/root",
			$"{CommandLineOptionTokens.BenchmarkOutput}=/tmp/result.json"
		]);

		AssertValid(result);
		Assert.True(result.Options.Benchmark.Enabled);
		Assert.Equal("/tmp/root", result.Options.Benchmark.Path);
		Assert.Equal("/tmp/result.json", result.Options.Benchmark.OutputPath);
	}

	[Fact]
	public void Parse_RejectsBenchmarkOutputWithoutBenchmark()
	{
		var result = CommandLineOptions.Parse([CommandLineOptionTokens.BenchmarkOutput, "/tmp/result.json"]);

		AssertInvalid(result, "benchmark-output-requires-benchmark");
	}

	[Theory]
	[InlineData("--path")]
	[InlineData("positional")]
	public void Parse_RejectsBenchmarkWithSeparateProjectTarget(string targetStyle)
	{
		var args = targetStyle == "--path"
			? new[] { CommandLineOptionTokens.Benchmark, "/tmp/root", CommandLineOptionTokens.Path, "/tmp/other" }
			: new[] { CommandLineOptionTokens.Benchmark, "/tmp/root", "/tmp/other" };

		var result = CommandLineOptions.Parse(args);

		AssertInvalid(result, "conflicting-benchmark-path");
	}

	[Theory]
	[InlineData("--no-ui")]
	[InlineData("--silent")]
	[InlineData("--strict")]
	[InlineData("--report")]
	[InlineData("--export")]
	[InlineData("--include-root")]
	[InlineData("--include-extension")]
	[InlineData("--ignore")]
	[InlineData("--preview")]
	[InlineData("--tree-filter")]
	public void Parse_RejectsBenchmarkWithNonStandardScenarioOptions(string option)
	{
		var args = option switch
		{
			"--export" => [CommandLineOptionTokens.Benchmark, "/tmp/root", CommandLineOptionTokens.Export, "tree"],
			"--include-root" => [CommandLineOptionTokens.Benchmark, "/tmp/root", CommandLineOptionTokens.IncludeRoot, "src"],
			"--include-extension" => [CommandLineOptionTokens.Benchmark, "/tmp/root", CommandLineOptionTokens.IncludeExtension, "cs"],
			"--ignore" => [CommandLineOptionTokens.Benchmark, "/tmp/root", CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone],
			"--tree-filter" => [CommandLineOptionTokens.Benchmark, "/tmp/root", CommandLineOptionTokens.TreeFilter, "src"],
			_ => new[] { CommandLineOptionTokens.Benchmark, "/tmp/root", option }
		};

		var result = CommandLineOptions.Parse(args);

		AssertInvalid(result, "conflicting-benchmark-options");
	}

	[Theory]
	[InlineData("tree", StartupExportMode.Tree)]
	[InlineData("content", StartupExportMode.Content)]
	[InlineData("tree-content", StartupExportMode.TreeContent)]
	[InlineData("tree-and-content", StartupExportMode.TreeContent)]
	[InlineData("all", StartupExportMode.TreeContent)]
	public void Parse_ReadsExportModes(string value, StartupExportMode expectedMode)
	{
		var result = CommandLineOptions.Parse([CommandLineOptionTokens.Export, value]);

		AssertValid(result);
		Assert.True(result.Options.Export.Enabled);
		Assert.Equal(expectedMode, result.Options.Export.Mode);
		Assert.True(result.Options.Export.WriteToStandardOutput);
	}

	[Theory]
	[InlineData("ascii", TreeTextFormat.Ascii)]
	[InlineData("text", TreeTextFormat.Ascii)]
	[InlineData("json", TreeTextFormat.Json)]
	[InlineData("xml", TreeTextFormat.Xml)]
	[InlineData("md", TreeTextFormat.Markdown)]
	[InlineData("markdown", TreeTextFormat.Markdown)]
	public void Parse_ReadsExportFormat(string value, TreeTextFormat expectedFormat)
	{
		var result = CommandLineOptions.Parse([
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.ExportFormat, value
		]);

		AssertValid(result);
		Assert.Equal(expectedFormat, result.Options.Export.Format);
		Assert.True(result.Options.Export.FormatSpecified);
	}

	[Fact]
	public void Parse_ReadsExportOutputFile()
	{
		var result = CommandLineOptions.Parse([
			CommandLineOptionTokens.Export, "content",
			CommandLineOptionTokens.Output, "/tmp/context.md"
		]);

		AssertValid(result);
		Assert.True(result.Options.Export.Enabled);
		Assert.Equal(StartupExportMode.Content, result.Options.Export.Mode);
		Assert.Equal("/tmp/context.md", result.Options.Export.Path);
		Assert.False(result.Options.Export.WriteToStandardOutput);
	}

	[Fact]
	public void Parse_ReadsExportOutputDashAsStdout()
	{
		var result = CommandLineOptions.Parse([
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.Output, CommandLineOptionTokens.StandardOutputReportPath
		]);

		AssertValid(result);
		Assert.True(result.Options.Export.WriteToStandardOutput);
	}

	[Fact]
	public void Parse_ReadsConvenienceAliasesForExportAndSelection()
	{
		var result = CommandLineOptions.Parse([
			CommandLineOptionTokens.Export, "tree-content",
			CommandLineOptionTokens.ShortOutput, "/tmp/context.md",
			CommandLineOptionTokens.Format, "json",
			CommandLineOptionTokens.Roots, "src",
			CommandLineOptionTokens.Roots, "tests",
			CommandLineOptionTokens.Extensions, "cs",
			CommandLineOptionTokens.Extensions, "json"
		]);

		AssertValid(result);
		Assert.True(result.Options.Export.Enabled);
		Assert.Equal(StartupExportMode.TreeContent, result.Options.Export.Mode);
		Assert.Equal("/tmp/context.md", result.Options.Export.Path);
		Assert.Equal(TreeTextFormat.Json, result.Options.Export.Format);
		Assert.True(result.Options.Export.FormatSpecified);
		Assert.Equal(["src", "tests"], result.Options.IncludeRootFolders);
		Assert.Equal([".cs", ".json"], result.Options.IncludeExtensions);
	}

	[Fact]
	public void Parse_ReadsDesktopStartupOptions()
	{
		var result = CommandLineOptions.Parse([
			"/tmp/root",
			CommandLineOptionTokens.PreviewMode, "tree-content",
			CommandLineOptionTokens.TreeFormat, "md",
			CommandLineOptionTokens.TreeFilter, "Services"
		]);

		AssertValid(result);
		Assert.Equal("/tmp/root", result.Options.Path);
		Assert.True(result.Options.Ui.OpenPreview);
		Assert.Equal(StartupPreviewMode.TreeContent, result.Options.Ui.PreviewMode);
		Assert.Equal(TreeTextFormat.Markdown, result.Options.Ui.TreeFormat);
		Assert.Equal("Services", result.Options.Ui.TreeFilter);
		Assert.Null(result.Options.Ui.PreviewSearch);
	}

	[Fact]
	public void Parse_ReadsLastAndPreviewSearchAsDesktopStartupOptions()
	{
		var result = CommandLineOptions.Parse([
			CommandLineOptionTokens.Last,
			CommandLineOptionTokens.PreviewSearch, "ProjectAnalysisService"
		]);

		AssertValid(result);
		Assert.True(result.Options.Ui.OpenLastProject);
		Assert.True(result.Options.Ui.OpenPreview);
		Assert.Equal("ProjectAnalysisService", result.Options.Ui.PreviewSearch);
	}

	[Fact]
	public void Parse_PreviewFlagWithProjectOpensDefaultPreviewWithoutChangingModeOrSearch()
	{
		var result = CommandLineOptions.Parse([
			CommandLineOptionTokens.Path, "/tmp/root",
			CommandLineOptionTokens.Preview
		]);

		AssertValid(result);
		Assert.True(result.Options.Ui.OpenPreview);
		Assert.Null(result.Options.Ui.PreviewMode);
		Assert.Null(result.Options.Ui.TreeFormat);
		Assert.Null(result.Options.Ui.TreeFilter);
		Assert.Null(result.Options.Ui.PreviewSearch);
	}

	[Theory]
	[InlineData("tree", StartupPreviewMode.Tree)]
	[InlineData("content", StartupPreviewMode.Content)]
	[InlineData("tree-content", StartupPreviewMode.TreeContent)]
	[InlineData("tree-and-content", StartupPreviewMode.TreeContent)]
	[InlineData("all", StartupPreviewMode.TreeContent)]
	public void Parse_ReadsEveryDesktopPreviewModeAlias(string value, StartupPreviewMode expectedMode)
	{
		var result = CommandLineOptions.Parse([
			CommandLineOptionTokens.Path, "/tmp/root",
			CommandLineOptionTokens.PreviewMode, value
		]);

		AssertValid(result);
		Assert.True(result.Options.Ui.OpenPreview);
		Assert.Equal(expectedMode, result.Options.Ui.PreviewMode);
	}

	[Theory]
	[InlineData("ascii", TreeTextFormat.Ascii)]
	[InlineData("text", TreeTextFormat.Ascii)]
	[InlineData("json", TreeTextFormat.Json)]
	[InlineData("xml", TreeTextFormat.Xml)]
	[InlineData("md", TreeTextFormat.Markdown)]
	[InlineData("markdown", TreeTextFormat.Markdown)]
	public void Parse_ReadsEveryDesktopTreeFormatAlias(string value, TreeTextFormat expectedFormat)
	{
		var result = CommandLineOptions.Parse([
			CommandLineOptionTokens.Path, "/tmp/root",
			CommandLineOptionTokens.TreeFormat, value
		]);

		AssertValid(result);
		Assert.Equal(expectedFormat, result.Options.Ui.TreeFormat);
	}

	[Fact]
	public void Parse_DesktopStartupInlineValuesMatchSeparatedValues()
	{
		var separated = CommandLineOptions.Parse([
			CommandLineOptionTokens.Path, "/tmp/root",
			CommandLineOptionTokens.PreviewMode, "tree-content",
			CommandLineOptionTokens.TreeFormat, "md",
			CommandLineOptionTokens.TreeFilter, "Services"
		]);
		var inline = CommandLineOptions.Parse([
			$"{CommandLineOptionTokens.Path}=/tmp/root",
			$"{CommandLineOptionTokens.PreviewMode}=tree-content",
			$"{CommandLineOptionTokens.TreeFormat}=md",
			$"{CommandLineOptionTokens.TreeFilter}=Services"
		]);

		AssertValid(separated);
		AssertValid(inline);
		AssertEquivalentOptions(separated.Options, inline.Options);
	}

	[Fact]
	public void Parse_DesktopStartupInlineValuesCanContainSpacesAndEquals()
	{
		var result = CommandLineOptions.Parse([
			$"{CommandLineOptionTokens.Path}=/tmp/root",
			$"{CommandLineOptionTokens.TreeFilter}=Project Services=Core",
			$"{CommandLineOptionTokens.TreeFormat}=markdown"
		]);

		AssertValid(result);
		Assert.Equal("Project Services=Core", result.Options.Ui.TreeFilter);
		Assert.Equal(TreeTextFormat.Markdown, result.Options.Ui.TreeFormat);
	}

	[Fact]
	public void Parse_RejectsDesktopStartupOptionsWithoutProjectTarget()
	{
		var result = CommandLineOptions.Parse([CommandLineOptionTokens.Preview]);

		AssertInvalid(result, "ui-startup-requires-project");
	}

	[Fact]
	public void Parse_RejectsLastWithExplicitPath()
	{
		var result = CommandLineOptions.Parse([
			CommandLineOptionTokens.Last,
			CommandLineOptionTokens.Path, "/tmp/root"
		]);

		AssertInvalid(result, "conflicting-startup-target");
	}

	[Fact]
	public void Parse_RejectsLastWithPositionalPath()
	{
		var result = CommandLineOptions.Parse([
			CommandLineOptionTokens.Last,
			"/tmp/root"
		]);

		AssertInvalid(result, "conflicting-startup-target");
	}

	[Fact]
	public void Parse_RejectsTreeFilterAndPreviewSearchTogether()
	{
		var result = CommandLineOptions.Parse([
			"/tmp/root",
			CommandLineOptionTokens.TreeFilter, "Services",
			CommandLineOptionTokens.PreviewSearch, "Program"
		]);

		AssertInvalid(result, "conflicting-search-and-filter");
	}

	[Fact]
	public void Parse_ReadsShortOutputInlineAssignment()
	{
		var result = CommandLineOptions.Parse([
			CommandLineOptionTokens.Export, "tree",
			$"{CommandLineOptionTokens.ShortOutput}=/tmp/context.md"
		]);

		AssertValid(result);
		Assert.Equal("/tmp/context.md", result.Options.Export.Path);
	}

	[Fact]
	public void Parse_RejectsEmptyShortOutputInlineAssignment()
	{
		var result = CommandLineOptions.Parse([
			CommandLineOptionTokens.Export, "tree",
			$"{CommandLineOptionTokens.ShortOutput}="
		]);

		AssertInvalid(result, "missing-value");
		Assert.Contains(result.Errors, static error => error.Token == CommandLineOptionTokens.ShortOutput);
	}

	[Fact]
	public void Parse_FormatAliasTargetsExportFormatAndDoesNotEnableReport()
	{
		var result = CommandLineOptions.Parse([
			CommandLineOptionTokens.Format, "json"
		]);

		AssertValid(result);
		Assert.False(result.Options.Report.Enabled);
		Assert.False(result.Options.Export.Enabled);
		Assert.Equal(TreeTextFormat.Json, result.Options.Export.Format);
		Assert.True(result.Options.Export.FormatSpecified);
	}

	[Fact]
	public void Parse_FormatAliasAsciiRecordsExplicitOptionEvenThoughValueMatchesDefault()
	{
		var result = CommandLineOptions.Parse([
			CommandLineOptionTokens.Format, "ascii"
		]);

		AssertValid(result);
		Assert.False(result.Options.Export.Enabled);
		Assert.Equal(TreeTextFormat.Ascii, result.Options.Export.Format);
		Assert.True(result.Options.Export.FormatSpecified);
	}

	[Fact]
	public void Parse_RejectsUnsupportedFormatAliasValue()
	{
		var result = CommandLineOptions.Parse([
			CommandLineOptionTokens.Format, "yaml"
		]);

		AssertInvalid(result, "invalid-export-format");
		Assert.Contains(result.Errors, static error => error.Token == CommandLineOptionTokens.Format);
	}

	[Fact]
	public void Parse_RejectsUnsupportedExportMode()
	{
		var result = CommandLineOptions.Parse([CommandLineOptionTokens.Export, "zip"]);

		AssertInvalid(result, "invalid-export-mode");
		Assert.False(result.Options.Export.Enabled);
	}

	[Fact]
	public void Parse_RejectsUnsupportedExportFormat()
	{
		var result = CommandLineOptions.Parse([
			CommandLineOptionTokens.Export, "tree",
			CommandLineOptionTokens.ExportFormat, "yaml"
		]);

		AssertInvalid(result, "invalid-export-format");
		Assert.True(result.Options.Export.Enabled);
	}

	[Fact]
	public void Parse_RejectsUnsupportedTreeFormat()
	{
		var result = CommandLineOptions.Parse([
			"/tmp/root",
			CommandLineOptionTokens.TreeFormat, "yaml"
		]);

		AssertInvalid(result, "invalid-tree-format");
	}

	[Fact]
	public void Parse_RejectsUnsupportedPreviewMode()
	{
		var result = CommandLineOptions.Parse([
			"/tmp/root",
			CommandLineOptionTokens.PreviewMode, "split"
		]);

		AssertInvalid(result, "invalid-preview-mode");
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
	public void Parse_FullAutomationCommand_NormalizesSelectionsAndReportOptions()
	{
		var result = CommandLineOptions.Parse([
			CommandLineOptionTokens.Silent,
			CommandLineOptionTokens.Strict,
			CommandLineOptionTokens.Path, @"C:\Projects\Dev Projex",
			CommandLineOptionTokens.ReportPath, @"C:\Reports\devprojex-report.json",
			CommandLineOptionTokens.ReportFormat, "json",
			CommandLineOptionTokens.IncludeRoot, "src app",
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.IncludeExtension, ".CS",
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreSmartIgnore,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreDotFolders
		]);

		AssertValid(result);
		Assert.True(result.Options.NoUi);
		Assert.True(result.Options.Strict);
		Assert.Equal(@"C:\Projects\Dev Projex", result.Options.Path);
		Assert.True(result.Options.Report.Enabled);
		Assert.Equal(@"C:\Reports\devprojex-report.json", result.Options.Report.Path);
		Assert.Equal(StartupReportFormat.Json, result.Options.Report.Format);
		Assert.Equal(["src app"], result.Options.IncludeRootFolders);
		Assert.Equal([".cs"], result.Options.IncludeExtensions);
		Assert.True(result.Options.IgnoreOptionsSpecified);
		Assert.Equal([IgnoreOptionId.DotFolders], result.Options.IgnoreOptions);
	}

	[Fact]
	public void Parse_ReadsNoUiLongForm()
	{
		var result = CommandLineOptions.Parse([CommandLineOptionTokens.NoUi]);

		AssertValid(result);
		Assert.True(result.Options.NoUi);
	}

	[Fact]
	public void Parse_ReadsStrictMode()
	{
		var result = CommandLineOptions.Parse([CommandLineOptionTokens.Strict]);

		AssertValid(result);
		Assert.True(result.Options.Strict);
	}

	[Fact]
	public void Parse_DeduplicatesExtensionsCaseInsensitivelyAfterNormalization()
	{
		var result = CommandLineOptions.Parse([
			CommandLineOptionTokens.IncludeExtension, "cs",
			CommandLineOptionTokens.IncludeExtension, ".CS",
			CommandLineOptionTokens.IncludeExtension, "json"
		]);

		AssertValid(result);
		Assert.Equal([".cs", ".json"], result.Options.IncludeExtensions);
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
	public void Parse_IgnoreNoneClearsPreviouslySelectedIgnoreOptions()
	{
		var result = CommandLineOptions.Parse([
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreSmartIgnore,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreGitIgnore,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone
		]);

		AssertValid(result);
		Assert.True(result.Options.IgnoreOptionsSpecified);
		Assert.Empty(result.Options.IgnoreOptions);
	}

	[Fact]
	public void Parse_IgnoreOptionAfterNoneStartsNewExplicitSet()
	{
		var result = CommandLineOptions.Parse([
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreSmartIgnore,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreNone,
			CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreDotFolders
		]);

		AssertValid(result);
		Assert.True(result.Options.IgnoreOptionsSpecified);
		Assert.Equal([IgnoreOptionId.DotFolders], result.Options.IgnoreOptions);
	}

	[Theory]
	[InlineData(CommandLineOptionTokens.Help)]
	[InlineData(CommandLineOptionTokens.ShortHelp)]
	[InlineData(CommandLineOptionTokens.WindowsHelp)]
	public void Parse_ReadsHelpAliases(string token)
	{
		var result = CommandLineOptions.Parse([token]);

		AssertValid(result);
		Assert.True(result.Options.ShowHelp);
	}

	[Fact]
	public void Parse_ReadsVersion()
	{
		var result = CommandLineOptions.Parse([CommandLineOptionTokens.Version]);

		AssertValid(result);
		Assert.True(result.Options.ShowVersion);
	}

	[Theory]
	[MemberData(nameof(PublicIgnoreOptionNames))]
	public void Parse_MapsEveryDocumentedIgnoreOptionNameToExpectedOption(string optionName, IgnoreOptionId? expectedOption)
	{
		var result = CommandLineOptions.Parse([CommandLineOptionTokens.Ignore, optionName]);

		AssertValid(result);
		Assert.True(result.Options.IgnoreOptionsSpecified);
		if (expectedOption is null)
		{
			Assert.Empty(result.Options.IgnoreOptions);
			return;
		}

		var actualOption = Assert.Single(result.Options.IgnoreOptions);
		Assert.Equal(expectedOption, actualOption);
	}

	[Fact]
	public void ToArguments_UsesDocumentedCanonicalIgnoreOptionNames()
	{
		var options = CommandLineOptions.Empty with
		{
			IgnoreOptionsSpecified = true,
			IgnoreOptions =
			[
				IgnoreOptionId.SmartIgnore,
				IgnoreOptionId.UseGitIgnore,
				IgnoreOptionId.HiddenFolders,
				IgnoreOptionId.HiddenFiles,
				IgnoreOptionId.DotFolders,
				IgnoreOptionId.DotFiles,
				IgnoreOptionId.EmptyFolders,
				IgnoreOptionId.EmptyFiles,
				IgnoreOptionId.ExtensionlessFiles
			]
		};

		var args = options.ToArguments();

		foreach (var optionName in CommandLineOptionTokens.PublicIgnoreOptionNames.Where(static name => name != CommandLineOptionTokens.IgnoreNone))
			Assert.Contains(optionName, args, StringComparison.Ordinal);
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
			Strict = true,
			Report = new StartupReportOptions(true, "/tmp/report folder/report.json", StartupReportFormat.Json),
			Export = new StartupExportOptions(true, StartupExportMode.TreeContent, "/tmp/export folder/context.txt", TreeTextFormat.Json),
			IncludeRootFolders = ["src"],
			IncludeExtensions = [".cs"],
			IgnoreOptions = [IgnoreOptionId.DotFolders],
			IgnoreOptionsSpecified = true
		};

		var args = options.ToArguments();

		Assert.Contains("--no-ui", args);
		Assert.Contains("--strict", args);
		Assert.Contains("--report", args);
		Assert.Contains("\"/tmp/report folder/report.json\"", args);
		Assert.Contains("--export", args);
		Assert.Contains("tree-content", args);
		Assert.Contains("--output", args);
		Assert.Contains("\"/tmp/export folder/context.txt\"", args);
		Assert.Contains("--export-format", args);
		Assert.Contains("json", args);
		Assert.Contains("--include-root", args);
		Assert.Contains("src", args);
		Assert.Contains("--include-extension", args);
		Assert.Contains(".cs", args);
		Assert.Contains("--ignore", args);
		Assert.Contains("dot-folders", args);
	}

	[Fact]
	public void ToArguments_PreservesBenchmarkCommand()
	{
		var options = CommandLineOptions.Empty with
		{
			Benchmark = new StartupBenchmarkOptions(true, "/tmp/root folder", "/tmp/result folder/benchmark.json")
		};

		var args = options.ToArguments();

		Assert.Contains("--benchmark", args);
		Assert.Contains("\"/tmp/root folder\"", args);
		Assert.Contains("--benchmark-output", args);
		Assert.Contains("\"/tmp/result folder/benchmark.json\"", args);
	}

	[Fact]
	public void ToArguments_PreservesExplicitAsciiExportFormatForRelaunch()
	{
		var options = CommandLineOptions.Empty with
		{
			Export = StartupExportOptions.Disabled with
			{
				Format = TreeTextFormat.Ascii,
				FormatSpecified = true
			}
		};

		var args = options.ToArguments();

		Assert.Contains("--export-format", args);
		Assert.Contains("ascii", args);
	}

	[Fact]
	public void ToArguments_PreservesPreviewOnlyForRelaunch()
	{
		var options = new CommandLineOptions("/tmp/root", AppLanguage.En, false)
		{
			Ui = StartupUiOptions.Default with { OpenPreview = true }
		};

		var args = options.ToArguments();

		Assert.Contains("--preview", args);
		Assert.DoesNotContain("--preview-mode", args);
		Assert.DoesNotContain("--preview-search", args);
	}

	[Fact]
	public void ToArguments_PreservesDesktopStartupOptionsForRelaunch()
	{
		var options = new CommandLineOptions("/tmp/root folder", AppLanguage.En, false)
		{
			Ui = StartupUiOptions.Default with
			{
				OpenLastProject = false,
				OpenPreview = true,
				PreviewMode = StartupPreviewMode.TreeContent,
				TreeFormat = TreeTextFormat.Markdown,
				TreeFilter = "App Services"
			}
		};

		var args = options.ToArguments();

		Assert.Contains("--path", args);
		Assert.Contains("\"/tmp/root folder\"", args);
		Assert.Contains("--preview-mode", args);
		Assert.Contains("tree-content", args);
		Assert.Contains("--tree-format", args);
		Assert.Contains("md", args);
		Assert.Contains("--tree-filter", args);
		Assert.Contains("\"App Services\"", args);
	}

	[Fact]
	public void ToArguments_CanonicalizesDesktopStartupAliasesForRelaunch()
	{
		var options = new CommandLineOptions("/tmp/root", AppLanguage.En, false)
		{
			Ui = StartupUiOptions.Default with
			{
				OpenPreview = true,
				PreviewMode = StartupPreviewMode.TreeContent,
				TreeFormat = TreeTextFormat.Markdown
			}
		};

		var args = options.ToArguments();

		Assert.Contains("--preview-mode", args);
		Assert.Contains("tree-content", args);
		Assert.Contains("--tree-format", args);
		Assert.Contains("md", args);
		Assert.DoesNotContain("tree-and-content", args);
		Assert.DoesNotContain("markdown", args);
	}

	[Fact]
	public void ToArguments_PreservesLastAndPreviewSearchForRelaunch()
	{
		var options = CommandLineOptions.Empty with
		{
			Ui = StartupUiOptions.Default with
			{
				OpenLastProject = true,
				PreviewSearch = "Project Analysis"
			}
		};

		var args = options.ToArguments();

		Assert.Contains("--last", args);
		Assert.Contains("--preview-search", args);
		Assert.Contains("\"Project Analysis\"", args);
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

	[Theory]
	[MemberData(nameof(ValueOptions))]
	public void Parse_RejectsMissingRequiredValues(string optionName)
	{
		var result = CommandLineOptions.Parse([optionName]);

		AssertInvalid(result, "missing-value");
	}

	[Theory]
	[MemberData(nameof(ValueOptions))]
	public void Parse_RejectsOptionTokenAsRequiredValue(string optionName)
	{
		var result = CommandLineOptions.Parse([optionName, CommandLineOptionTokens.Version]);

		AssertInvalid(result, "missing-value");
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

	private static void AssertEquivalentOptions(CommandLineOptions expected, CommandLineOptions actual)
	{
		Assert.Equal(expected.Path, actual.Path);
		Assert.Equal(expected.Language, actual.Language);
		Assert.Equal(expected.ElevationAttempted, actual.ElevationAttempted);
		Assert.Equal(expected.NoUi, actual.NoUi);
		Assert.Equal(expected.ShowHelp, actual.ShowHelp);
		Assert.Equal(expected.ShowVersion, actual.ShowVersion);
		Assert.Equal(expected.Report, actual.Report);
		Assert.Equal(expected.Benchmark, actual.Benchmark);
		Assert.Equal(expected.Export, actual.Export);
		Assert.Equal(expected.Export.FormatSpecified, actual.Export.FormatSpecified);
		Assert.Equal(expected.Ui, actual.Ui);
		Assert.Equal(expected.IncludeRootFolders, actual.IncludeRootFolders);
		Assert.Equal(expected.IncludeExtensions, actual.IncludeExtensions);
		Assert.Equal(expected.IgnoreOptions, actual.IgnoreOptions);
		Assert.Equal(expected.IgnoreOptionsSpecified, actual.IgnoreOptionsSpecified);
		Assert.Equal(expected.Strict, actual.Strict);
	}

	public static TheoryData<string, IgnoreOptionId?> PublicIgnoreOptionNames() => new()
	{
		{ CommandLineOptionTokens.IgnoreSmartIgnore, IgnoreOptionId.SmartIgnore },
		{ CommandLineOptionTokens.IgnoreGitIgnore, IgnoreOptionId.UseGitIgnore },
		{ CommandLineOptionTokens.IgnoreHiddenFolders, IgnoreOptionId.HiddenFolders },
		{ CommandLineOptionTokens.IgnoreHiddenFiles, IgnoreOptionId.HiddenFiles },
		{ CommandLineOptionTokens.IgnoreDotFolders, IgnoreOptionId.DotFolders },
		{ CommandLineOptionTokens.IgnoreDotFiles, IgnoreOptionId.DotFiles },
		{ CommandLineOptionTokens.IgnoreEmptyFolders, IgnoreOptionId.EmptyFolders },
		{ CommandLineOptionTokens.IgnoreEmptyFiles, IgnoreOptionId.EmptyFiles },
		{ CommandLineOptionTokens.IgnoreExtensionlessFiles, IgnoreOptionId.ExtensionlessFiles },
		{ CommandLineOptionTokens.IgnoreNone, null }
	};

	public static TheoryData<string, string> InlineEquivalentValueOptions() => new()
	{
		{ CommandLineOptionTokens.Path, "/tmp/root" },
		{ CommandLineOptionTokens.Language, "ru" },
		{ CommandLineOptionTokens.ReportPath, "/tmp/report.json" },
		{ CommandLineOptionTokens.ReportFormat, "json" },
		{ CommandLineOptionTokens.Benchmark, "/tmp/root" },
		{ CommandLineOptionTokens.Export, "tree-content" },
		{ CommandLineOptionTokens.Output, "/tmp/context.txt" },
		{ CommandLineOptionTokens.ShortOutput, "/tmp/context.txt" },
		{ CommandLineOptionTokens.ExportFormat, "json" },
		{ CommandLineOptionTokens.Format, "json" },
		{ CommandLineOptionTokens.IncludeRoot, "src" },
		{ CommandLineOptionTokens.Roots, "src" },
		{ CommandLineOptionTokens.IncludeExtension, "cs" },
		{ CommandLineOptionTokens.Extensions, "cs" },
		{ CommandLineOptionTokens.Ignore, CommandLineOptionTokens.IgnoreDotFolders }
	};

	public static TheoryData<string> ValueOptions() => new()
	{
		CommandLineOptionTokens.Path,
		CommandLineOptionTokens.Language,
		CommandLineOptionTokens.ReportPath,
		CommandLineOptionTokens.ReportFormat,
		CommandLineOptionTokens.Benchmark,
		CommandLineOptionTokens.BenchmarkOutput,
		CommandLineOptionTokens.Export,
		CommandLineOptionTokens.Output,
		CommandLineOptionTokens.ShortOutput,
		CommandLineOptionTokens.ExportFormat,
		CommandLineOptionTokens.Format,
		CommandLineOptionTokens.PreviewMode,
		CommandLineOptionTokens.TreeFormat,
		CommandLineOptionTokens.TreeFilter,
		CommandLineOptionTokens.PreviewSearch,
		CommandLineOptionTokens.IncludeRoot,
		CommandLineOptionTokens.Roots,
		CommandLineOptionTokens.IncludeExtension,
		CommandLineOptionTokens.Extensions,
		CommandLineOptionTokens.Ignore
	};
}
