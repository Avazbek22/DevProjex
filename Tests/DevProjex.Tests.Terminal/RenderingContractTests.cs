using DevProjex.Terminal.Rendering;

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
}
