using DevProjex.Terminal.Rendering;
using DevProjex.Infrastructure.ResourceStore;
using DevProjex.Application.Secrets;
using DevProjex.Kernel.Abstractions;

namespace DevProjex.Tests.Terminal;

public sealed class RenderingContractTests
{
	[Fact]
	public void InteractiveColorOutputUsesAnsiAndEscapesMarkup()
	{
		var environment = new TestTerminalEnvironment
		{
			IsErrorInteractive = true,
			SupportsUnicode = true
		};
		var renderer = new ErrorRenderer(
			environment,
			new TerminalOutputOptions(Color: TerminalColorMode.Always));

		renderer.Write(new TerminalError(
			"DPX-TEST-[PATH]",
			"Destination [draft]/file.txt",
			ContextPath: "[workspace]/file.txt"));

		Assert.Contains("\u001b[", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("DPX-TEST-[PATH]", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("Destination [draft]/file.txt", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("[workspace]/file.txt", environment.StandardError, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(false, false, false, false, false)]
	[InlineData(true, false, false, false, true)]
	[InlineData(true, true, false, false, false)]
	[InlineData(true, false, true, false, false)]
	[InlineData(true, false, false, true, false)]
	public void AutoColorRespectsRedirectionNoColorTermDumbAndPlain(
		bool interactive,
		bool noColor,
		bool termDumb,
		bool plain,
		bool expectedAnsi)
	{
		var environment = new TestTerminalEnvironment
		{
			IsOutputInteractive = interactive,
			IsNoColor = noColor,
			IsTermDumb = termDumb
		};

		var capabilities = TerminalCapabilities.Resolve(
			environment,
			new TerminalOutputOptions(Plain: plain),
			forStandardError: false);

		Assert.Equal(expectedAnsi, capabilities.UseAnsi);
	}

	[Theory]
	[InlineData(true, false, false, true)]
	[InlineData(true, true, false, false)]
	[InlineData(true, false, true, false)]
	[InlineData(false, false, false, false)]
	public void AutoProgressRunsOnlyOnInteractiveNonCiStderr(
		bool errorInteractive,
		bool ci,
		bool termDumb,
		bool expectedProgress)
	{
		var environment = new TestTerminalEnvironment
		{
			IsErrorInteractive = errorInteractive,
			IsCi = ci,
			IsTermDumb = termDumb
		};

		var capabilities = TerminalCapabilities.Resolve(
			environment,
			new TerminalOutputOptions(),
			forStandardError: true);

		Assert.Equal(expectedProgress, capabilities.UseInteractiveProgress);
	}

	[Theory]
	[InlineData(TerminalVerbosity.Quiet, false)]
	[InlineData(TerminalVerbosity.Minimal, false)]
	[InlineData(TerminalVerbosity.Normal, true)]
	[InlineData(TerminalVerbosity.Detailed, true)]
	[InlineData(TerminalVerbosity.Diagnostic, true)]
	public void VerbosityControlsOptionalStatusAndProgress(
		TerminalVerbosity verbosity,
		bool expectedProgress)
	{
		var environment = new TestTerminalEnvironment
		{
			IsErrorInteractive = true
		};

		var capabilities = TerminalCapabilities.Resolve(
			environment,
			new TerminalOutputOptions(
				Progress: TerminalProgressMode.Always,
				Verbosity: verbosity),
			forStandardError: true);

		Assert.Equal(expectedProgress, capabilities.UseInteractiveProgress);
	}

	[Theory]
	[InlineData(TerminalVerbosity.Quiet, false, false, true)]
	[InlineData(TerminalVerbosity.Minimal, false, true, true)]
	[InlineData(TerminalVerbosity.Normal, true, true, true)]
	public void VerbosityFiltersDiagnosticsBySeverity(
		TerminalVerbosity verbosity,
		bool expectsInformation,
		bool expectsWarning,
		bool expectsError)
	{
		var environment = new TestTerminalEnvironment();
		var renderer = new ContextDiagnosticRenderer(
			environment,
			new TerminalOutputOptions(Verbosity: verbosity),
			new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En));

		renderer.Write(
		[
			new ContextDiagnostic("DPX-TEST-INFO", ContextDiagnosticSeverity.Information, "ignored"),
			new ContextDiagnostic("DPX-TEST-WARNING", ContextDiagnosticSeverity.Warning, "ignored"),
			new ContextDiagnostic("DPX-TEST-ERROR", ContextDiagnosticSeverity.Error, "ignored")
		]);

		Assert.Equal(
			expectsInformation,
			environment.StandardError.Contains("DPX-TEST-INFO", StringComparison.Ordinal));
		Assert.Equal(
			expectsWarning,
			environment.StandardError.Contains("DPX-TEST-WARNING", StringComparison.Ordinal));
		Assert.Equal(
			expectsError,
			environment.StandardError.Contains("DPX-TEST-ERROR", StringComparison.Ordinal));
	}

	[Fact]
	public void RedirectedDiagnosticKeepsEscapedLongPathOnOnePhysicalLine()
	{
		var environment = new TestTerminalEnvironment
		{
			Width = 40
		};
		var renderer = new ContextDiagnosticRenderer(
			environment,
			new TerminalOutputOptions(),
			new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En));
		var diagnosticPath = Path.Combine(
			"diagnostic-path-start",
			new string('a', 80),
			"leaf") + "\nforged\rsegment\t\u001bcontrol\u2028end";

		renderer.Write(
		[
			new ContextDiagnostic(
				"DPX-PROJECT-SELECTION-WARNING",
				ContextDiagnosticSeverity.Warning,
				"Selection warning.",
				diagnosticPath)
		]);

		var lines = environment.StandardError
			.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
		var pathLine = Assert.Single(
			lines,
			static line => line.Contains("diagnostic-path-start", StringComparison.Ordinal));
		Assert.True(pathLine.Length > environment.Width);
		Assert.Contains(
			"\\nforged\\rsegment\\t\\u001Bcontrol\\u2028end",
			pathLine,
			StringComparison.Ordinal);
		Assert.DoesNotContain('\n', pathLine);
		Assert.DoesNotContain('\r', pathLine);
		Assert.DoesNotContain('\t', pathLine);
		Assert.DoesNotContain('\u001b', pathLine);
		Assert.DoesNotContain('\u2028', pathLine);
	}

	[Fact]
	public async Task RussianHumanDiagnosticNeverFallsBackToInternalEnglishMessage()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("missing.cs", "class App {}\n");
		workspace.WriteFile("README.md", "# Project\n");
		var environment = new TestTerminalEnvironment();

		var exitCode = await new TerminalApplication(
				environment,
				new TerminalServiceFactory(() => workspace.CreateDirectory("app-data")))
			.RunAsync(
			[
				"analyze",
				workspace.Path,
				"--select",
				"missing.cs",
				"--extension",
				"md",
				"--git-mode",
				"none",
				"--exclude",
				"none",
				"--language",
				"ru"
			],
				TestContext.Current.CancellationToken);

		Assert.Equal(CommandLineExitCodes.Success, exitCode);
		Assert.Contains(
			"Выбранный путь отсутствует в эффективном дереве.",
			environment.StandardError,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"A selected path is absent from the effective tree.",
			environment.StandardError,
			StringComparison.Ordinal);
		Assert.DoesNotContain("\u001b", environment.StandardOutput, StringComparison.Ordinal);
		Assert.DoesNotContain("\u001b", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public void DiagnosticVerbosityShowsExceptionTypeButNotRawMessage()
	{
		const string rawMessage = "RAW_PLATFORM_MESSAGE";
		var environment = new TestTerminalEnvironment();
		var renderer = new ErrorRenderer(
			environment,
			new TerminalOutputOptions(Verbosity: TerminalVerbosity.Diagnostic));

		renderer.Write(new TerminalError(
			"DPX-IO-FAILURE",
			"Safe localized message.",
			Exception: new IOException(rawMessage)));

		Assert.Contains(typeof(IOException).FullName!, environment.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain(rawMessage, environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public void RedirectedErrorKeepsLongContextPathOnOneLine()
	{
		var environment = new TestTerminalEnvironment
		{
			Width = 40
		};
		var renderer = new ErrorRenderer(
			environment,
			new TerminalOutputOptions());
		var contextPath = Path.Combine(
			Path.GetTempPath(),
			"DevProjex.Tests.Terminal",
			new string('a', 80),
			"external-alias",
			"submission.zip");

		renderer.Write(new TerminalError(
			"DPX-EXPORT-DESTINATION-EXISTS",
			"The destination already exists.",
			ContextPath: contextPath));

		Assert.Contains(
			contextPath + Environment.NewLine,
			environment.StandardError,
			StringComparison.Ordinal);
	}

	[Fact]
	public void RedirectedErrorEscapesControlCharactersInContextPath()
	{
		var environment = new TestTerminalEnvironment();
		var renderer = new ErrorRenderer(
			environment,
			new TerminalOutputOptions(Plain: true));
		const string contextPath =
			"safe\nforged\rsegment\t\u001b]8;;https://example.invalid\u0007link\u001b\\\u2028end";

		renderer.Write(new TerminalError(
			"DPX-TEST",
			"Failure.",
			ContextPath: contextPath));

		var lines = environment.StandardError
			.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
		var pathLine = Assert.Single(
			lines,
			static line => line.Contains("safe\\nforged", StringComparison.Ordinal));
		Assert.EndsWith(
			"safe\\nforged\\rsegment\\t\\u001B]8;;https://example.invalid" +
			"\\u0007link\\u001B\\\\u2028end",
			pathLine);
		Assert.DoesNotContain('\u001b', environment.StandardError);
		Assert.DoesNotContain('\u0007', environment.StandardError);
		Assert.DoesNotContain('\u2028', environment.StandardError);
	}

	[Fact]
	public void RedirectedErrorEscapesControlCharactersInMessageAndHint()
	{
		var environment = new TestTerminalEnvironment();
		var renderer = new ErrorRenderer(
			environment,
			new TerminalOutputOptions(Plain: true));

		renderer.Write(new TerminalError(
			"DPX-TEST",
			"safe\r\nforged\u001b[31m",
			"retry\twithout\u0007control"));

		Assert.Contains("safe\\r\\nforged\\u001B[31m", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("retry\\twithout\\u0007control", environment.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain('\u001b', environment.StandardError);
		Assert.DoesNotContain('\u0007', environment.StandardError);
	}

	[Fact]
	public void TextFindingEscapesPathControlCharactersIntoOneLine()
	{
		var finding = new EffectiveRedactionFinding(
			"rule-id",
			RedactionFindingCategory.Secrets,
			"safe\nforged\rsegment\t\u001bname.cs",
			42);

		var formatted = AnalysisTextFormatter.FormatFinding(finding);

		Assert.Equal("secret  rule-id  safe\\nforged\\rsegment\\t\\u001Bname.cs:42", formatted);
		Assert.DoesNotContain('\n', formatted);
		Assert.DoesNotContain('\r', formatted);
		Assert.DoesNotContain('\t', formatted);
		Assert.DoesNotContain('\u001b', formatted);
	}

	[Fact]
	public async Task AnalysisRowsEscapeControlCharactersInUserDerivedFields()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("App.cs", "internal sealed class App {}");
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var selection = await services.SelectionResolver.ResolveAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			new ProjectSelectionSpec(),
			TestContext.Current.CancellationToken);
		var plan = await services.ContextPlanner.BuildAsync(
			new ProjectContextRequest(workspace.Path, selection),
			TestContext.Current.CancellationToken);
		plan = plan with
		{
			Selection = plan.Selection with
			{
				ProfileSource = new ProjectProfileReference(
					ProjectProfileSourceKind.Portable,
					"profile\rforged.json")
			},
			SelectedRoots = ["root\nforged"],
			SelectedExtensions = [".cs\tforged"],
			SourceIdentity = new ProjectSourceIdentity(
				"project\nforged",
				ProjectSourceType.GitClone,
				"source",
				"https://example.invalid/repo\rforged")
		};

		var rows = AnalysisTextFormatter.BuildRows(
			plan,
			new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En));

		Assert.Contains(rows, static row => row.Value == "project\\nforged");
		Assert.Contains(rows, static row => row.Value == "https://example.invalid/repo\\rforged");
		Assert.Contains(rows, static row => row.Value == "profile\\rforged.json");
		Assert.Contains(rows, static row => row.Value == "root\\nforged");
		Assert.Contains(rows, static row => row.Value == ".cs\\tforged");
		Assert.All(rows, static row =>
		{
			Assert.DoesNotContain('\r', row.Value);
			Assert.DoesNotContain('\n', row.Value);
			Assert.DoesNotContain('\t', row.Value);
		});
	}

	[Fact]
	public void DryRunEscapesControlCharactersInDestination()
	{
		var environment = new TestTerminalEnvironment();
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);

		DryRunRenderer.WritePlan(environment, localization, "safe\nforged\tpath");

		Assert.Contains("safe\\nforged\\tpath", environment.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain('\n', environment.StandardError.TrimEnd('\r', '\n'));
		Assert.DoesNotContain('\t', environment.StandardError);
	}

	[Fact]
	public void UnscannableSummaryEscapesControlCharactersInPaths()
	{
		var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DevProjexUnscannable"));
		var path = Path.Combine(root, "safe\nforged\tfile.cs");
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);

		var summary = UnscannableFileOutput.FormatSummary(
			root,
			[new UnscannableFile(path, FileContentClassification.TooLarge)],
			localization);

		Assert.Contains("safe\\nforged\\tfile.cs", summary, StringComparison.Ordinal);
		Assert.DoesNotContain('\n', summary);
		Assert.DoesNotContain('\t', summary);
	}

	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	public void DeletedGitStateDiagnosticUsesCountNeutralGrammar(int count)
	{
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);
		var diagnostic = GitScopeFilter.CreateDeletedDiagnostic("project", count);

		Assert.Equal(
			$"Deleted files excluded from the Git state: {count}.",
			ContextDiagnosticRenderer.ResolveMessage(localization, diagnostic));
	}

	[Fact]
	public void TerminalColumnsAlignWideRunesWithoutTabs()
	{
		var lines = TerminalColumnLayout.Format(
		[
			["界", "first"],
			["aa", "second"]
		]);

		Assert.Equal(["界  first", "aa  second"], lines);
		Assert.All(lines, static line => Assert.DoesNotContain('\t', line));
	}

	[Fact]
	public void FindingTableSnapshotUsesLocalizedThreeColumnLayout()
	{
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);
		var lines = AnalysisTextFormatter.BuildFindingTable(
		[
			new EffectiveRedactionFinding(
				"github-pat",
				RedactionFindingCategory.Secrets,
				"src/a.cs",
				4),
			new EffectiveRedactionFinding(
				"email",
				RedactionFindingCategory.PrivateData,
				"src/界.cs",
				20)
		],
			localization);

		Assert.Equal(
		[
			"Category      Rule        File:line",
			"secret        github-pat  src/a.cs:4",
			"private-data  email       src/界.cs:20"
		], lines);
		Assert.All(lines, static line => Assert.DoesNotContain('\t', line));
	}

	[Fact]
	public void InteractiveGitProgressRewritesPadsAndClearsOneLine()
	{
		var environment = new TestTerminalEnvironment
		{
			IsErrorInteractive = true,
			Width = 40
		};
		using var renderer = Assert.IsType<GitOperationProgressRenderer>(
			GitOperationProgressRenderer.Create(
				environment,
				new TerminalOutputOptions(Progress: TerminalProgressMode.Always),
				"clone",
				"complete"));

		renderer.Start();
		renderer.Report("abcdefghij 25%");
		renderer.Report("x 50%");
		var beforeCompletion = environment.StandardError;
		renderer.Complete();

		Assert.Equal(
			"\rclone\rabcdefghij 25%\rx 50%" + new string(' ', 9),
			beforeCompletion);
		Assert.Equal(beforeCompletion + "\r" + new string(' ', 5) + "\r", environment.StandardError);
		Assert.DoesNotContain('\n', environment.StandardError);
		Assert.DoesNotContain('\u001b', environment.StandardError);
		Assert.Empty(environment.StandardOutput);
	}

	[Fact]
	public void InteractiveGitProgressRereadsWidthAndTruncatesWideTextByColumns()
	{
		var environment = new TestTerminalEnvironment
		{
			IsErrorInteractive = true,
			Width = 9
		};
		using var renderer = Assert.IsType<GitOperationProgressRenderer>(
			GitOperationProgressRenderer.Create(
				environment,
				new TerminalOutputOptions(),
				"go",
				"complete"));

		renderer.Start();
		renderer.Report("界界界界界");
		environment.Width = 5;
		renderer.Report("界界界界界");

		Assert.Contains("\r界界界界", environment.StandardError, StringComparison.Ordinal);
		Assert.EndsWith("\r界界", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public void RedirectedGitProgressSplitsCrAndLfAndEmitsOnlyBoundedMilestones()
	{
		var environment = new TestTerminalEnvironment();
		using var renderer = Assert.IsType<GitOperationProgressRenderer>(
			GitOperationProgressRenderer.Create(
				environment,
				new TerminalOutputOptions(),
				"Cloning safe-url...",
				"Clone completed."));

		renderer.Start();
		renderer.Report(
			"remote: Enumerating objects: 100%\rReceiving objects: 24%\r" +
			"Receiving objects: 25%\u001b\rReceiving objects: 55%\n" +
			"Receiving objects: 76%\rResolving deltas: 100%");
		renderer.Complete();

		var lines = environment.StandardError
			.ReplaceLineEndings("\n")
			.Split('\n', StringSplitOptions.RemoveEmptyEntries);
		Assert.Equal(5, lines.Length);
		Assert.Equal("Cloning safe-url...", lines[0]);
		Assert.Contains("25%", lines[1], StringComparison.Ordinal);
		Assert.Contains("55%", lines[2], StringComparison.Ordinal);
		Assert.Contains("76%", lines[3], StringComparison.Ordinal);
		Assert.Equal("Clone completed.", lines[4]);
		Assert.DoesNotContain('\u001b', environment.StandardError);
		Assert.Contains("\\u001B", lines[1], StringComparison.Ordinal);
		Assert.Empty(environment.StandardOutput);
	}

	[Theory]
	[InlineData(TerminalProgressMode.Never, TerminalVerbosity.Normal)]
	[InlineData(TerminalProgressMode.Always, TerminalVerbosity.Quiet)]
	[InlineData(TerminalProgressMode.Always, TerminalVerbosity.Minimal)]
	public void DisabledOrQuietGitProgressWritesNoBytes(
		TerminalProgressMode progressMode,
		TerminalVerbosity verbosity)
	{
		var environment = new TestTerminalEnvironment { IsErrorInteractive = true };
		using var renderer = GitOperationProgressRenderer.Create(
			environment,
			new TerminalOutputOptions(Progress: progressMode, Verbosity: verbosity),
			"start",
			"complete");

		renderer?.Start();
		renderer?.Report("Receiving objects: 50%");
		renderer?.Complete();

		Assert.Empty(environment.StandardError);
		Assert.Empty(environment.StandardOutput);
	}

	[Fact]
	public void PlainGitProgressUsesMilestonesInsteadOfCarriageReturnFrames()
	{
		var environment = new TestTerminalEnvironment { IsErrorInteractive = true };
		using var renderer = Assert.IsType<GitOperationProgressRenderer>(
			GitOperationProgressRenderer.Create(
				environment,
				new TerminalOutputOptions(Plain: true),
				"start",
				"complete"));

		renderer.Start();
		renderer.Report("Receiving objects: 50%");
		renderer.Complete();

		Assert.Equal(
			$"start{Environment.NewLine}Receiving objects: 50%{Environment.NewLine}complete{Environment.NewLine}",
			environment.StandardError);
	}

	[Fact]
	public void RedirectedGitProgressStopsWritingAfterDiagnosticsPipeCloses()
	{
		var error = new BrokenPipeTextWriter(failOnAttempt: 2);
		var environment = new TestTerminalEnvironment { ErrorOverride = error };
		using var renderer = Assert.IsType<GitOperationProgressRenderer>(
			GitOperationProgressRenderer.Create(
				environment,
				new TerminalOutputOptions(),
				"start",
				"complete"));

		renderer.Start();
		renderer.Report("Receiving objects: 25%");
		renderer.Report("Receiving objects: 50%");
		renderer.Complete();

		Assert.Equal(2, error.WriteAttempts);
	}

	[Fact]
	public async Task InteractiveStatusUsesOnlyStandardError()
	{
		var environment = new TestTerminalEnvironment
		{
			IsErrorInteractive = true
		};
		var renderer = new StatusRenderer(
			environment,
			new TerminalOutputOptions(
				Color: TerminalColorMode.Never,
				Progress: TerminalProgressMode.Always));

		var result = await renderer.RunAsync(
			"Analyzing project",
			async () =>
			{
				await Task.Delay(20, TestContext.Current.CancellationToken);
				return 42;
			});

		Assert.Equal(42, result);
		Assert.Contains("Analyzing project", environment.StandardError, StringComparison.Ordinal);
		Assert.Empty(environment.StandardOutput);
	}

	[Fact]
	public async Task PlainOrDisabledProgressSuppressesStatusAnimation()
	{
		var environment = new TestTerminalEnvironment
		{
			IsErrorInteractive = true
		};
		var renderer = new StatusRenderer(
			environment,
			new TerminalOutputOptions(
				Progress: TerminalProgressMode.Never,
				Plain: true));

		var result = await renderer.RunAsync("Hidden status", () => Task.FromResult("ok"));

		Assert.Equal("ok", result);
		Assert.Empty(environment.StandardError);
		Assert.Empty(environment.StandardOutput);
	}

	[Theory]
	[InlineData(false, false, true)]
	[InlineData(false, true, true)]
	[InlineData(true, false, true)]
	public async Task PlainNonInteractiveOrDumbExplicitStatusIsStatic(
		bool plain,
		bool termDumb,
		bool expectStatus)
	{
		var environment = new TestTerminalEnvironment
		{
			IsErrorInteractive = false,
			IsTermDumb = termDumb
		};
		var renderer = new StatusRenderer(
			environment,
			new TerminalOutputOptions(
				Color: TerminalColorMode.Always,
				Progress: TerminalProgressMode.Always,
				Plain: plain));

		var result = await renderer.RunAsync(
			"Analyzing project",
			() => Task.FromResult(42));

		Assert.Equal(42, result);
		Assert.Equal(
			expectStatus ? $"Analyzing project{Environment.NewLine}" : string.Empty,
			environment.StandardError);
		Assert.DoesNotContain(
			"\r",
			environment.StandardError.Replace("\r\n", string.Empty, StringComparison.Ordinal),
			StringComparison.Ordinal);
		Assert.DoesNotContain('\u001b', environment.StandardError);
		Assert.Empty(environment.StandardOutput);
	}

	[Fact]
	public async Task InteractiveProjectExportProgressShowsMeasuredStateOnlyOnStandardError()
	{
		using var appData = new TemporaryDirectory();
		var environment = new TestTerminalEnvironment
		{
			IsErrorInteractive = true,
			Width = 100
		};
		var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		var renderer = new ProgressRenderer(
			environment,
			new TerminalOutputOptions(
				Color: TerminalColorMode.Always,
				Progress: TerminalProgressMode.Always),
			services.Localization);

		var result = await renderer.RunProjectExportAsync(progress =>
		{
			Assert.NotNull(progress);
			progress.Report(new ProjectCopyExportProgress(1, 4, 1_024, 25));
			progress.Report(new ProjectCopyExportProgress(2, 4, 2_048, 50));
			progress.Report(new ProjectCopyExportProgress(4, 4, 4_096, 100));
			return Task.FromResult(42);
		});

		Assert.Equal(42, result);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("Exporting project 4/4 (4 KB)", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("100%", environment.StandardError, StringComparison.Ordinal);
		Assert.Matches(@"\d+:\d{2}", environment.StandardError);
	}

	[Fact]
	public async Task InteractiveProjectExportProgressDoesNotManufactureCompletion()
	{
		using var appData = new TemporaryDirectory();
		var environment = new TestTerminalEnvironment
		{
			IsErrorInteractive = true,
			Width = 100
		};
		var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		var renderer = new ProgressRenderer(
			environment,
			new TerminalOutputOptions(
				Color: TerminalColorMode.Always,
				Progress: TerminalProgressMode.Always),
			services.Localization);

		var result = await renderer.RunProjectExportAsync(progress =>
		{
			Assert.NotNull(progress);
			progress.Report(new ProjectCopyExportProgress(2, 4, 2_048, 50));
			return Task.FromResult(42);
		});

		Assert.Equal(42, result);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("Exporting project 2/4 (2 KB)", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("50%", environment.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain("100%", environment.StandardError, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(TerminalColorMode.Auto, true)]
	[InlineData(TerminalColorMode.Never, false)]
	public async Task InteractiveMonochromeProjectExportProgressRewritesAndClearsOneLine(
		TerminalColorMode color,
		bool noColor)
	{
		using var appData = new TemporaryDirectory();
		var environment = new TestTerminalEnvironment
		{
			IsErrorInteractive = true,
			IsNoColor = noColor,
			Width = 100
		};
		var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		var renderer = new ProgressRenderer(
			environment,
			new TerminalOutputOptions(
				Color: color,
				Progress: TerminalProgressMode.Always),
			services.Localization);

		var result = await renderer.RunProjectExportAsync(progress =>
		{
			Assert.NotNull(progress);
			progress.Report(new ProjectCopyExportProgress(1, 4, 1_024, 25));
			progress.Report(new ProjectCopyExportProgress(2, 4, 2_048, 50));
			return Task.FromResult(42);
		});

		Assert.Equal(42, result);
		Assert.Empty(environment.StandardOutput);
		Assert.Contains("Exporting project 2/4 (2 KB)", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("50%", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains('\r', environment.StandardError);
		Assert.DoesNotContain('\n', environment.StandardError);
		Assert.DoesNotContain('\u001b', environment.StandardError);
		Assert.EndsWith("\r", environment.StandardError, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task InteractiveProjectExportProgressNeverReportsSuccessForFailedOperation(
		bool canceled)
	{
		using var appData = new TemporaryDirectory();
		var environment = new TestTerminalEnvironment
		{
			IsErrorInteractive = true,
			Width = 100
		};
		var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		var renderer = new ProgressRenderer(
			environment,
			new TerminalOutputOptions(
				Color: TerminalColorMode.Always,
				Progress: TerminalProgressMode.Always),
			services.Localization);

		Task<int> Operation(IProgress<ProjectCopyExportProgress>? progress)
		{
			Assert.NotNull(progress);
			progress.Report(new ProjectCopyExportProgress(1, 4, 1_024, 25));
			return canceled
				? Task.FromCanceled<int>(new CancellationToken(canceled: true))
				: Task.FromException<int>(new IOException("simulated failure"));
		}

		if (canceled)
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
				renderer.RunProjectExportAsync(Operation));
		else
			await Assert.ThrowsAsync<IOException>(() =>
				renderer.RunProjectExportAsync(Operation));

		Assert.Empty(environment.StandardOutput);
		Assert.DoesNotContain("100%", environment.StandardError, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RedirectedExplicitProjectProgressIsStaticEvenWhenColorIsAlways()
	{
		using var appData = new TemporaryDirectory();
		var environment = new TestTerminalEnvironment
		{
			IsErrorInteractive = false,
			Width = 100
		};
		var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		var renderer = new ProgressRenderer(
			environment,
			new TerminalOutputOptions(
				Color: TerminalColorMode.Always,
				Progress: TerminalProgressMode.Always),
			services.Localization);

		await renderer.RunProjectExportAsync(progress =>
		{
			Assert.NotNull(progress);
			progress.Report(new ProjectCopyExportProgress(1, 2, 1_024, 50));
			progress.Report(new ProjectCopyExportProgress(2, 2, 2_048, 100));
			return Task.FromResult(true);
		});

		Assert.Empty(environment.StandardOutput);
		Assert.Contains("Exporting project 2/2 (2 KB)", environment.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain(
			"\r",
			environment.StandardError.Replace("\r\n", string.Empty, StringComparison.Ordinal),
			StringComparison.Ordinal);
		Assert.DoesNotContain('\u001b', environment.StandardError);
		Assert.InRange(
			environment.StandardError.Split(
				Environment.NewLine,
				StringSplitOptions.RemoveEmptyEntries).Length,
			1,
			2);
	}

	private sealed class BrokenPipeTextWriter(int failOnAttempt) : TextWriter
	{
		private int _writeAttempts;

		public override Encoding Encoding => Encoding.UTF8;
		public int WriteAttempts => _writeAttempts;

		public override void WriteLine(string? value)
		{
			if (++_writeAttempts >= failOnAttempt)
				throw new TerminalBrokenPipeException();
		}
	}
}
