using DevProjex.Terminal.Rendering;
using DevProjex.Infrastructure.ResourceStore;

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
	public async Task RussianHumanDiagnosticNeverFallsBackToInternalEnglishMessage()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/app.cs", "class App {}\n");
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

	[Fact]
	public async Task NoColorProjectExportProgressRemainsReadableWithoutAnsi()
	{
		using var appData = new TemporaryDirectory();
		var environment = new TestTerminalEnvironment
		{
			IsErrorInteractive = true,
			IsNoColor = true,
			Width = 100
		};
		var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		var renderer = new ProgressRenderer(
			environment,
			new TerminalOutputOptions(Progress: TerminalProgressMode.Always),
			services.Localization);

		await renderer.RunProjectExportAsync(async progress =>
		{
			Assert.NotNull(progress);
			progress.Report(new ProjectCopyExportProgress(2, 4, 2_048, 50));
			await Task.Delay(300, TestContext.Current.CancellationToken);
			return true;
		});

		Assert.Empty(environment.StandardOutput);
		Assert.Contains("Exporting project 2/4 (2 KB)", environment.StandardError, StringComparison.Ordinal);
		Assert.Contains("50%", environment.StandardError, StringComparison.Ordinal);
		Assert.DoesNotContain("\u001b", environment.StandardError, StringComparison.Ordinal);
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
}
