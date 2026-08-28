using System.CommandLine;
using System.CommandLine.Parsing;
using System.Reflection;
using DevProjex.Application.Presentation;
using DevProjex.Infrastructure.ResourceStore;

namespace DevProjex.Tests.Terminal;

public sealed class CliV1ParserRegressionTests
{
	[Theory]
	[InlineData("analyze|.|--hide-secrets|on|--no-strip-comments")]
	[InlineData("analyze|.|-p|standard|-r|src|-e|.cs|-s|src/a.cs|-x|smart-ignore")]
	[InlineData("tree|https://github.com/owner/repo|-b|main|--force|-o|tree.txt")]
	[InlineData("cache|remove|https://github.com/owner/repo|-y|-n|-f|json")]
	[InlineData("cache|update|https://github.com/owner/repo")]
	[InlineData("profile|save|.|--hide-private-data|off")]
	[InlineData("profile|validate|profile.json|-f|json")]
	[InlineData("ui|list|--timeout|5s|--quiet")]
	public void TerminalPolishFormsParse(string invocation)
	{
		var result = new DevProjexCommandTree(new TestTerminalEnvironment())
			.Build()
			.Parse(invocation.Split('|'));
		Assert.Empty(result.Errors);
	}

	[Fact]
	public void PositiveAndNegativeBooleanFormsConflict()
	{
		var result = new DevProjexCommandTree(new TestTerminalEnvironment())
			.Build()
			.Parse(["analyze", ".", "--hide-secrets", "--no-hide-secrets"]);
		Assert.NotEmpty(result.Errors);
	}

	[Theory]
	[InlineData("--hide-secrets")]
	[InlineData("--hide-private-data")]
	[InlineData("--compress-code")]
	[InlineData("--strip-comments")]
	[InlineData("--strip-blank-lines")]
	public void BareTransformationFlagBeforeProjectDoesNotConsumeProject(string option)
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		var command = ResolveCommand(root, ["analyze"]);
		var project = Assert.IsType<Argument<string?>>(Assert.Single(command.Arguments));

		var result = root.Parse(["analyze", option, "project-root"]);

		Assert.Empty(result.Errors);
		Assert.Equal("project-root", result.GetValue(project));
	}

	[Fact]
	public void BareMisspelledProfileTokenIsRejectedAsAChoiceInsteadOfAPath()
	{
		var result = new DevProjexCommandTree(new TestTerminalEnvironment())
			.Build()
			.Parse(["analyze", ".", "--profile", "stanadrd"]);

		var error = Assert.Single(result.Errors);
		Assert.Contains(
			"standard, local, FILE",
			error.Message,
			StringComparison.Ordinal);
	}

	[Fact]
	public void DevProjexEnvironmentDefaultsApplyAndExplicitFlagsWin()
	{
		var environment = new TestTerminalEnvironment
		{
			Variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
			{
				["DEVPROJEX_COLOR"] = "always",
				["DEVPROJEX_PROGRESS"] = "never",
				["DEVPROJEX_VERBOSITY"] = "detailed",
				["DEVPROJEX_LANGUAGE"] = "uz"
			}
		};
		var root = new DevProjexCommandTree(environment).Build();
		var analyze = ResolveCommand(root, ["analyze"]);
		var defaults = root.Parse(["analyze", "."]);

		Assert.Equal(TerminalColorMode.Always,
			defaults.GetValue(Assert.IsType<Option<TerminalColorMode>>(
				root.Options.Single(static option => option.Name == "--color"))));
		Assert.Equal(TerminalProgressMode.Never,
			defaults.GetValue(Assert.IsType<Option<TerminalProgressMode>>(
				analyze.Options.Single(static option => option.Name == "--progress"))));
		Assert.Equal(TerminalVerbosity.Detailed,
			defaults.GetValue(Assert.IsType<Option<TerminalVerbosity>>(
				root.Options.Single(static option => option.Name == "--verbosity"))));
		Assert.Equal(AppLanguage.Uz,
			defaults.GetValue(Assert.IsType<Option<AppLanguage>>(
				root.Options.Single(static option => option.Name == "--language"))));

		var explicitValues = root.Parse([
			"analyze", ".",
			"--color", "never",
			"--progress", "always",
			"--verbosity", "minimal",
			"--language", "en"
		]);
		Assert.Empty(explicitValues.Errors);
		Assert.Equal(TerminalColorMode.Never,
			explicitValues.GetValue(Assert.IsType<Option<TerminalColorMode>>(
				root.Options.Single(static option => option.Name == "--color"))));
		Assert.Equal(TerminalProgressMode.Always,
			explicitValues.GetValue(Assert.IsType<Option<TerminalProgressMode>>(
				analyze.Options.Single(static option => option.Name == "--progress"))));
	}

	[Fact]
	public void RecursiveQuietAliasConflictsWithExplicitVerbosity()
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();

		var parseResult = root.Parse([
			"doctor",
			"--quiet",
			"--verbosity", "normal"
		]);

		Assert.NotEmpty(parseResult.Errors);
	}

	[Fact]
	public void DevProjexRootPrecedesClaudeProjectDirUnlessRootsAreExplicit()
	{
		var variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
		{
			["DEVPROJEX_ROOT"] = "devprojex-root",
			["CLAUDE_PROJECT_DIR"] = "claude-root"
		};

		Assert.Equal(
			["explicit-root"],
			McpRootSourceResolver.Resolve(["explicit-root"], variables, "working-root"));
		Assert.Equal(
			["devprojex-root"],
			McpRootSourceResolver.Resolve([], variables, "working-root"));
	}

	[Fact]
	public void SelectionChoiceSetsAreDerivedFromTheSharedCatalogAndRejectInvalidEnums()
	{
		var gitTokens = ProjectPresentationCatalog.GitFiltering
			.OrderBy(static descriptor => descriptor.Order)
			.Select(static descriptor => descriptor.Token)
			.ToArray();
		var exclusionTokens = ProjectPresentationCatalog.Exclusions
			.OrderBy(static descriptor => descriptor.Order)
			.Select(static descriptor => descriptor.Token)
			.ToArray();

		Assert.Equal(gitTokens, ProjectSelectionTokens.GitModes);
		Assert.Equal(gitTokens, CliChoiceSets.GitMode.Tokens);
		Assert.Equal(exclusionTokens, ProjectSelectionTokens.Exclusions);
		Assert.Equal(
			[ProjectPresentationCatalog.NoExclusionsToken, .. exclusionTokens],
			CliChoiceSets.Exclusion.Tokens);
		Assert.Throws<ArgumentOutOfRangeException>(
			() => ProjectSelectionTokens.ToToken((GitFilteringMode)int.MaxValue));
		Assert.Throws<ArgumentOutOfRangeException>(
			() => ProjectSelectionTokens.ToToken((ProjectExclusion)int.MaxValue));
	}

	[Fact]
	public void EveryTypedChoiceSetCoversItsEntireEnumAndRoundTripsCanonicalTokens()
	{
		AssertCompleteChoiceSet(CliChoiceSets.TextJson);
		AssertCompleteChoiceSet(CliChoiceSets.ContextView);
		AssertCompleteChoiceSet(CliChoiceSets.ContextDocumentFormat);
		AssertCompleteChoiceSet(CliChoiceSets.ProjectExportFormat);
		AssertCompleteChoiceSet(CliChoiceSets.ScreenMode);
		AssertCompleteChoiceSet(CliChoiceSets.ColorMode);
		AssertCompleteChoiceSet(CliChoiceSets.ProgressMode);
		AssertCompleteChoiceSet(CliChoiceSets.Verbosity);
		AssertCompleteChoiceSet(CliChoiceSets.DesktopView);
		AssertCompleteChoiceSet(CliChoiceSets.GitMode);
		AssertCompleteChoiceSet(CliChoiceSets.CompletionShell);
		AssertCompleteChoiceSet(CliChoiceSets.DeveloperScenario);
		AssertCompleteChoiceSet(CliChoiceSets.Language);
		AssertCompleteChoiceSet(CliChoiceSets.RecentKind);

		Assert.Equal(
			Enum.GetValues<ProjectExclusion>().Order(),
			ProjectPresentationCatalog.LegacyExclusionChoices
				.Select(static descriptor => descriptor.RequireId())
				.Order());
		Assert.Equal(
			Enum.GetValues<ProjectContextView>().Order(),
			ProjectPresentationCatalog.PreviewModes
				.Select(static descriptor => descriptor.Id)
				.Order());
		Assert.Equal(
			Enum.GetValues<ProjectContextDocumentFormat>().Order(),
			ProjectPresentationCatalog.Formats
				.Select(static descriptor => descriptor.Id)
				.Order());
	}

	[Theory]
	[InlineData("Analyze|.")]
	[InlineData("EXPORT|context|.")]
	[InlineData("export|Context|.")]
	[InlineData("Profile|show|.")]
	[InlineData("ui|Preview|open")]
	public void PublicCommandNamesAreCaseSensitive(string invocation)
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();

		var parseResult = root.Parse(invocation.Split('|'));

		Assert.NotEmpty(parseResult.Errors);
	}

	[Theory]
	[InlineData("analyze|.|--Format|json")]
	[InlineData("analyze|.|--FORMAT=json")]
	[InlineData("analyze|.|--Language|en")]
	[InlineData("export|context|.|--View|tree")]
	[InlineData("tui|.|--Screen|inline")]
	[InlineData("doctor|--FORMAT|json")]
	public void PublicOptionNamesAreCaseSensitive(string invocation)
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();

		var parseResult = root.Parse(invocation.Split('|'));

		Assert.NotEmpty(parseResult.Errors);
	}

	[Theory]
	[InlineData("analyze", "analyze|--format|JSON", "--format", "json")]
	[InlineData("analyze", "analyze|--format=JsOn", "--format", "json")]
	[InlineData("export context", "export|context|--view|TREE", "--view", "tree")]
	[InlineData("export context", "export|context|--view=TrEe", "--view", "tree")]
	[InlineData("export context", "export|context|--format|JSON", "--format", "json")]
	[InlineData("export context", "export|context|--format=JsOn", "--format", "json")]
	[InlineData("export project", "export|project|--as|ZIP|-o|output.zip", "--as", "zip")]
	[InlineData("export project", "export|project|--as=Zip|-o|output.zip", "--as", "zip")]
	[InlineData("tui", "tui|--screen|INLINE", "--screen", "inline")]
	[InlineData("tui", "tui|--screen=InLiNe", "--screen", "inline")]
	[InlineData("open", "open|--tree-format|JSON", "--tree-format", "json")]
	[InlineData("open", "open|--tree-format=JsOn", "--tree-format", "json")]
	[InlineData("open", "open|--view|TREE", "--view", "tree")]
	[InlineData("open", "open|--view=TrEe", "--view", "tree")]
	[InlineData("ui list", "ui|list|--format|JSON", "--format", "json")]
	[InlineData("doctor", "doctor|--format=JsOn", "--format", "json")]
	public void AcceptedChoiceIsCanonicalAtTheParserBoundary(
		string commandPath,
		string invocation,
		string optionName,
		string expectedToken)
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		var command = ResolveCommand(root, commandPath.Split(' '));
		var option = command.Options.Single(candidate => candidate.Name == optionName);

		var parseResult = root.Parse(invocation.Split('|'));

		Assert.Empty(parseResult.Errors);
		var optionResult = Assert.IsType<OptionResult>(parseResult.GetResult(option));
		Assert.Equal(expectedToken, ReadCanonicalToken(optionResult, option));
	}

	[Theory]
	[InlineData("--language|ru|analyze|.")]
	[InlineData("analyze|.|--language|ru")]
	[InlineData("--language=ru|analyze|.")]
	public void RecursiveLanguageOptionBindsBeforeOrAfterTheLeafCommand(
		string invocation)
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		var language = Assert.IsType<Option<AppLanguage>>(
			root.Options.Single(static option => option.Name == "--language"));

		var parseResult = root.Parse(invocation.Split('|'));

		Assert.Empty(parseResult.Errors);
		Assert.Equal("analyze", parseResult.CommandResult.Command.Name);
		Assert.Equal(AppLanguage.Ru, parseResult.GetValue(language));
	}

	[Theory]
	[InlineData("ui preview set-view", "ui|preview|set-view|TREE", "VIEW", "tree")]
	[InlineData("ui preview set-view", "ui|preview|set-view|TrEe", "VIEW", "tree")]
	[InlineData("ui tree set-format", "ui|tree|set-format|JSON", "FORMAT", "json")]
	[InlineData("ui tree set-format", "ui|tree|set-format|JsOn", "FORMAT", "json")]
	public void AcceptedChoiceArgumentIsCanonicalAtTheParserBoundary(
		string commandPath,
		string invocation,
		string argumentName,
		string expectedToken)
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		var command = ResolveCommand(root, commandPath.Split(' '));
		var argument = command.Arguments.Single(candidate => candidate.Name == argumentName);

		var parseResult = root.Parse(invocation.Split('|'));

		Assert.Empty(parseResult.Errors);
		var argumentResult = Assert.IsType<ArgumentResult>(parseResult.GetResult(argument));
		Assert.Equal(expectedToken, ReadCanonicalToken(argumentResult, argument));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task AnalyzeMixedCaseJsonProducesAJsonDocument(bool equalsSyntax)
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);
		var environment = new TestTerminalEnvironment();
		var arguments = new List<string> { "analyze", project };
		AddChoice(arguments, "--format", "JsOn", equalsSyntax);
		arguments.AddRange(["--git-mode", "none", "--exclude", "none", "--plain", "--language", "en"]);

		var exitCode = await CreateApplication(environment, workspace)
			.RunAsync(arguments, TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardError);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ContextMixedCaseFormatProducesAJsonDocument(bool equalsSyntax)
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);
		var environment = new TestTerminalEnvironment();
		var arguments = new List<string> { "export", "context", project };
		AddChoice(arguments, "--format", "JsOn", equalsSyntax);
		arguments.AddRange(["-o", "-", "--git-mode", "none", "--exclude", "none", "--plain", "--language", "en"]);

		var exitCode = await CreateApplication(environment, workspace)
			.RunAsync(arguments, TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardError);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		Assert.Equal("devprojex-context", document.RootElement.GetProperty("kind").GetString());
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ContextMixedCaseTreeViewOmitsFileDocuments(bool equalsSyntax)
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);
		var environment = new TestTerminalEnvironment();
		var arguments = new List<string>
		{
			"export", "context", project,
			"--format", "json",
			"-o", "-",
			"--git-mode", "none",
			"--exclude", "none",
			"--plain",
			"--language", "en"
		};
		AddChoice(arguments, "--view", "TrEe", equalsSyntax);

		var exitCode = await CreateApplication(environment, workspace)
			.RunAsync(arguments, TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardError);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		Assert.Empty(document.RootElement.GetProperty("files").EnumerateArray());
		Assert.NotEqual(JsonValueKind.Null, document.RootElement.GetProperty("tree").ValueKind);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task MixedCaseZipChoiceCreatesAZipFile(bool equalsSyntax)
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);
		var destination = Path.Combine(workspace.Path, $"result-{equalsSyntax}.zip");
		var environment = new TestTerminalEnvironment();
		var arguments = new List<string> { "export", "project", project };
		AddChoice(arguments, "--as", "ZiP", equalsSyntax);
		arguments.AddRange(
		[
			"-o", destination,
			"--git-mode", "none",
			"--exclude", "none",
			"--plain",
			"--language", "en"
		]);

		var exitCode = await CreateApplication(environment, workspace)
			.RunAsync(arguments, TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardError);
		Assert.True(File.Exists(destination));
		Assert.False(Directory.Exists(destination));
		Assert.Equal(Path.GetFullPath(destination) + Environment.NewLine, environment.StandardOutput);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task DoctorMixedCaseJsonProducesAJsonDocument(bool equalsSyntax)
	{
		using var workspace = new TemporaryDirectory();
		var environment = new TestTerminalEnvironment();
		var arguments = new List<string> { "doctor" };
		AddChoice(arguments, "--format", "JsOn", equalsSyntax);
		arguments.AddRange(["--language", "en"]);

		var exitCode = await CreateApplication(environment, workspace)
			.RunAsync(arguments, TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardError);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task UiListMixedCaseJsonProducesAJsonDocument(bool equalsSyntax)
	{
		var environment = new TestTerminalEnvironment();
		var arguments = new List<string> { "ui", "list" };
		AddChoice(arguments, "--format", "JsOn", equalsSyntax);
		arguments.AddRange(["--language", "en"]);

		var exitCode = await new TerminalApplication(environment)
			.RunAsync(arguments, TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardError);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
	}

	[Theory]
	[InlineData("analyze|--format|invalid")]
	[InlineData("analyze|--format=invalid")]
	[InlineData("export|context|--view|invalid")]
	[InlineData("export|context|--format=invalid")]
	[InlineData("export|project|--as|invalid|-o|output.zip")]
	[InlineData("tui|--screen=invalid")]
	[InlineData("open|--view|invalid")]
	[InlineData("open|--tree-format=invalid")]
	[InlineData("ui|list|--format|invalid")]
	[InlineData("ui|preview|open|--view=invalid")]
	[InlineData("ui|preview|set-view|invalid")]
	[InlineData("ui|tree|set-format|invalid")]
	[InlineData("doctor|--format=invalid")]
	[InlineData("completion|invalid")]
	[InlineData("analyze|--color=invalid")]
	[InlineData("analyze|--progress|invalid")]
	[InlineData("analyze|--verbosity=invalid")]
	[InlineData("analyze|--git-mode|invalid")]
	[InlineData("analyze|--exclude=invalid")]
	[InlineData("analyze|--language|invalid")]
	public void UnknownChoiceIsRejectedByTheProductionCommandTree(string invocation)
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();

		var parseResult = root.Parse(invocation.Split('|'));

		Assert.NotEmpty(parseResult.Errors);
	}

	[Fact]
	public async Task DelimiterPreventsLegacyDetection()
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			["analyze", "--language", "en", "--", "--copy"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.DoesNotContain("DPX-CLI-LEGACY-SYNTAX", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("DPX-PROJECT-NOT-FOUND", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ExcessDashPrefixedDataAfterDelimiterIsNotPresentedAsAnOption()
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			["--language", "en", "analyze", ".", "--", "--fornat"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("DPX-CLI-INVALID-SYNTAX", environment.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain("DPX-CLI-UNKNOWN-OPTION", environment.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain("devprojex analyze --format", environment.StandardError, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("--copy", "zip")]
	[InlineData("--copy=zip", null)]
	public async Task SupportedLegacyEqualsAndSpaceFormsUseTheSameMigration(
		string option,
		string? value)
	{
		var environment = new TestTerminalEnvironment();
		var arguments = new List<string> { "--language", "en", "--path", "." };
		arguments.Add(option);
		if (value is not null)
			arguments.Add(value);
		arguments.AddRange(["-o", "../legacy-output.zip"]);

		var exitCode = await new TerminalApplication(environment)
			.RunAsync(arguments, TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("DPX-CLI-LEGACY-SYNTAX", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("zip", environment.StandardError, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task RepeatedExcludeNoneIsIdempotent()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);
		var environment = new TestTerminalEnvironment();

		var exitCode = await CreateApplication(environment, workspace).RunAsync(
			[
				"analyze", project,
				"--format", "json",
				"--git-mode", "none",
				"--exclude", "none",
				"--exclude=NONE",
				"--plain",
				"--language", "en"
			],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Empty(environment.StandardError);
		using var document = JsonDocument.Parse(environment.StandardOutput);
		Assert.Empty(document.RootElement.GetProperty("selection").GetProperty("exclusions").EnumerateArray());
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ContextForceWithStdoutIsAUsageError(bool explicitStdout)
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);
		var environment = new TestTerminalEnvironment();
		var arguments = new List<string>
		{
			"export", "context", project,
			"--force",
			"--git-mode", "none",
			"--exclude", "none",
			"--plain",
			"--language", "en"
		};
		if (explicitStdout)
			arguments.AddRange(["-o", "-"]);

		var exitCode = await CreateApplication(environment, workspace)
			.RunAsync(arguments, TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("--force", environment.StandardError, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("folder", "output", true, true)]
	[InlineData("zip", "output.txt", false, true)]
	[InlineData("folder", "output", false, false)]
	[InlineData("zip", "output.ZIP", true, false)]
	public void ProjectExportUsageIsValidatedBeforeSourceResolution(
		string kind,
		string destination,
		bool force,
		bool expectsError)
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		var arguments = new List<string>
		{
			"export", "project", "https://example.com/owner/repository.git",
			"--as", kind,
			"--output", destination
		};
		if (force)
			arguments.Add("--force");

		var parseResult = root.Parse(arguments);

		Assert.Equal(expectsError, parseResult.Errors.Count > 0);
	}

	[Theory]
	[InlineData("--profile|standard")]
	[InlineData("--root|src")]
	[InlineData("--extension|.cs")]
	[InlineData("--select|src/app.cs")]
	[InlineData("--select-from|selection.txt")]
	[InlineData("--git-mode|none")]
	[InlineData("--exclude|none")]
	[InlineData("--hide-secrets")]
	[InlineData("--hide-private-data")]
	[InlineData("--compress-code")]
	[InlineData("--strip-comments")]
	[InlineData("--strip-blank-lines")]
	[InlineData("--branch|main")]
	public void OpenLastRejectsEveryExplicitSelectionOverride(string selectionOverride)
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		var arguments = new[] { "open", "--last" }
			.Concat(selectionOverride.Split('|'))
			.ToArray();

		var parseResult = root.Parse(arguments);

		Assert.NotEmpty(parseResult.Errors);
	}

	[Fact]
	public async Task UnknownTokenBeforeHelpRemainsAUsageError()
	{
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(environment).RunAsync(
			["nonsense", "analyze", "--help", "--language", "en"],
			TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("DPX-CLI-UNKNOWN-COMMAND", environment.StandardError, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task DiagnosticVerbositySupportsSpaceAndEqualsForms(bool equalsSyntax)
	{
		using var workspace = new TemporaryDirectory();
		var missingProject = Path.Combine(workspace.Path, "missing");
		var environment = new TestTerminalEnvironment();
		var arguments = new List<string> { "analyze", missingProject, "--language", "en" };
		AddChoice(arguments, "--verbosity", "diagnostic", equalsSyntax);

		var exitCode = await CreateApplication(environment, workspace)
			.RunAsync(arguments, TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("DPX-PROJECT-NOT-FOUND", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains(
			typeof(ProjectContextValidationException).FullName!,
			environment.StandardError,
			StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("1.5s", 1500)]
	[InlineData("2m", 120000)]
	[InlineData("00:00:03", 3000)]
	public void UiTimeoutIsConvertedOnceAtTheParserBoundary(
		string token,
		int expectedMilliseconds)
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		var command = ResolveCommand(root, ["ui", "status"]);
		var timeout = Assert.IsType<Option<TimeSpan>>(
			command.Options.Single(static option => option.Name == "--timeout"));

		var parseResult = root.Parse(["ui", "status", $"--timeout={token}"]);

		Assert.Empty(parseResult.Errors);
		Assert.Equal(
			TimeSpan.FromMilliseconds(expectedMilliseconds),
			parseResult.GetValue(timeout));
	}

	[Fact]
	public void InvalidUiTimeoutIsRejectedBeforeTheAction()
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();

		var parseResult = root.Parse(["ui", "status", "--timeout=soon"]);

		Assert.NotEmpty(parseResult.Errors);
	}

	[Fact]
	public void UiTimeoutBeyondCancellationTimerRangeIsRejectedBeforeTheAction()
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();

		var parseResult = root.Parse(["ui", "status", "--timeout=50.00:00:00"]);

		Assert.NotEmpty(parseResult.Errors);
	}

	[Theory]
	[InlineData("NaNs")]
	[InlineData("Infinitys")]
	[InlineData("999999999999999999999999s")]
	public void NonFiniteOrOverflowingUiTimeoutNeverEscapesTheParser(string token)
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		ParseResult? parseResult = null;

		var exception = Record.Exception(
			() => parseResult = root.Parse(["ui", "status", $"--timeout={token}"]));

		Assert.Null(exception);
		Assert.NotNull(parseResult);
		Assert.NotEmpty(parseResult.Errors);
	}

	[Theory]
	[InlineData("NaNs", false)]
	[InlineData("NaNs", true)]
	[InlineData("Infinitys", false)]
	[InlineData("Infinitys", true)]
	[InlineData("999999999999999999999999s", false)]
	[InlineData("999999999999999999999999s", true)]
	[InlineData("50.00:00:00", false)]
	[InlineData("50.00:00:00", true)]
	public async Task InvalidUiTimeoutIsASafeUsageError(
		string token,
		bool equalsSyntax)
	{
		var environment = new TestTerminalEnvironment();
		var arguments = equalsSyntax
			? new[] { "ui", "status", $"--timeout={token}", "--language", "en" }
			: new[] { "ui", "status", "--timeout", token, "--language", "en" };

		var exitCode = await new TerminalApplication(environment)
			.RunAsync(arguments, TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.UsageError, exitCode);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("DPX-CLI-INVALID-VALUE", environment.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain("ArgumentException", environment.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain("OverflowException", environment.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain(" at ", environment.StandardError, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("analyze")]
	[InlineData("export|context")]
	[InlineData("profile|show")]
	public void DirectCommandsRejectAutoProfileSpecialValue(string command)
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		var arguments = command.Split('|')
			.Concat(["--profile", "AUTO"])
			.ToArray();

		var parseResult = root.Parse(arguments);

		Assert.NotEmpty(parseResult.Errors);
	}

	[Theory]
	[InlineData("tui")]
	[InlineData("open")]
	public void InteractiveEntryPointsAcceptAutoProfileSpecialValue(string command)
	{
		var root = new DevProjexCommandTree(new TestTerminalEnvironment()).Build();
		var target = ResolveCommand(root, [command]);
		var profile = Assert.IsType<Option<CliProfileValue>>(
			target.Options.Single(static option => option.Name == "--profile"));

		var parseResult = root.Parse([command, "--profile=AUTO"]);

		Assert.Empty(parseResult.Errors);
		Assert.Equal(CliProfileSource.Auto, parseResult.GetValue(profile).Source);
	}

	[Fact]
	public async Task OpenAutoProfileResolvesTheExistingLocalSelection()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);
		var dataRoot = workspace.CreateDirectory("open-auto-data");
		var services = new TerminalServiceFactory(() => dataRoot).Create(AppLanguage.En);
		services.LocalProfileStore.SaveProfile(
			project,
			new ProjectSelectionProfile(
				SelectedRootFolders: [],
				SelectedExtensions: [".cs"],
				SelectedIgnoreOptions: []));
		var localization = new LocalizationService(
			new JsonLocalizationCatalog(),
			AppLanguage.En);
		var environment = new TestTerminalEnvironment();
		var selection = new SelectionOptions(localization, environment, "auto");
		var command = new RootCommand();
		selection.AddTo(command);
		var parseResult = command.Parse([]);

		var resolved = await selection.ResolveAsync(
			parseResult,
			project,
			services,
			TestContext.Current.CancellationToken);

		Assert.Equal(ProjectProfileSourceKind.Local, resolved.ProfileSource?.Kind);
		Assert.Equal([".cs"], resolved.Extensions);
	}

	[Fact]
	public async Task OpenSelectionResolvesExplicitHidePrivateDataIntent()
	{
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("open-private-data"))
			.Create(AppLanguage.En);
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);
		var environment = new TestTerminalEnvironment();
		var selection = new SelectionOptions(localization, environment, "auto");
		var command = new RootCommand();
		selection.AddTo(command);
		var parseResult = command.Parse(["--hide-private-data"]);

		var resolved = await selection.ResolveAsync(
			parseResult,
			project,
			services,
			TestContext.Current.CancellationToken);

		Assert.True(resolved.HidePrivateData);
		Assert.Equal(
			ProjectSelectionApplicationMode.ApplyResolvedValue,
			resolved.ApplicationIntent?.HidePrivateData);
	}

	private static TerminalApplication CreateApplication(
		TestTerminalEnvironment environment,
		TemporaryDirectory workspace) =>
		new(
			environment,
			new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")));

	private static string CreateProject(TemporaryDirectory workspace)
	{
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/app.cs", "internal sealed class App {}\n");
		return project;
	}

	private static void AddChoice(
		ICollection<string> arguments,
		string option,
		string value,
		bool equalsSyntax)
	{
		if (equalsSyntax)
		{
			arguments.Add($"{option}={value}");
			return;
		}

		arguments.Add(option);
		arguments.Add(value);
	}

	private static Command ResolveCommand(Command root, IReadOnlyList<string> path)
	{
		var current = root;
		foreach (var segment in path)
		{
			current = current.Subcommands.Single(command =>
				command.Name.Equals(segment, StringComparison.Ordinal));
		}

		return current;
	}

	private static string ReadCanonicalToken(SymbolResult result, Symbol symbol)
	{
		var symbolType = FindGenericSymbolType(symbol.GetType());
		var valueType = symbolType.GetGenericArguments()[0];
		var valueReader = result.GetType()
			.GetMethods(BindingFlags.Instance | BindingFlags.Public)
			.Single(method =>
				method.Name == nameof(OptionResult.GetValueOrDefault) &&
				method.IsGenericMethodDefinition &&
				method.GetParameters().Length == 0);
		var value = valueReader.MakeGenericMethod(valueType).Invoke(result, null);
		Assert.NotNull(value);
		if (value is string token)
			return token;

		return ToKebabCase(value.ToString()!);
	}

	private static Type FindGenericSymbolType(Type type)
	{
		for (var current = type; current is not null; current = current.BaseType)
		{
			if (!current.IsGenericType)
				continue;
			var definition = current.GetGenericTypeDefinition();
			if (definition == typeof(Option<>) || definition == typeof(Argument<>))
				return current;
		}

		throw new InvalidOperationException($"Unsupported command-line symbol type: {type.FullName}");
	}

	private static void AssertCompleteChoiceSet<T>(CliChoiceSet<T> choices)
		where T : struct, Enum
	{
		var values = Enum.GetValues<T>();
		Assert.Equal(values.Length, choices.Tokens.Count);
		Assert.Equal(
			choices.Tokens.Count,
			choices.Tokens.Distinct(StringComparer.OrdinalIgnoreCase).Count());

		foreach (var value in values)
		{
			var token = choices.ToToken(value);
			Assert.Equal(token.ToLowerInvariant(), token);
			Assert.Contains(token, choices.Tokens, StringComparer.Ordinal);
			Assert.True(choices.TryParse(token.ToUpperInvariant(), out var parsed));
			Assert.Equal(value, parsed);
		}

		var undefined = (T)Enum.ToObject(typeof(T), int.MaxValue);
		Assert.Throws<ArgumentOutOfRangeException>(() => choices.ToToken(undefined));
	}

	private static string ToKebabCase(string value)
	{
		var result = new StringBuilder(value.Length + 4);
		for (var index = 0; index < value.Length; index++)
		{
			var character = value[index];
			if (index > 0 &&
			    char.IsUpper(character) &&
			    (char.IsLower(value[index - 1]) || char.IsDigit(value[index - 1])))
			{
				result.Append('-');
			}

			result.Append(char.ToLowerInvariant(character));
		}

		return result.ToString();
	}
}
