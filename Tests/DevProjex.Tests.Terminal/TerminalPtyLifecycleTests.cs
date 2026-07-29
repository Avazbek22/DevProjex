namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class TerminalPtyLifecycleTests
{
	[Fact(Timeout = 60_000)]
	public async Task CtrlCAtWelcomeRequiresConfirmationAndDoesNotTerminateSession()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("notes.txt", "markerless directory");
		await using var terminal = await TerminalPtyHarness.StartAsync(
			workspace.Path,
			["--language", "en"],
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			"Choose a workspace action",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendCtrlCAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Exit DevProjex Terminal?",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Exit DevProjex Terminal?",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	[Theory(Timeout = 60_000)]
	[InlineData("tg", "Фазоҳои кории охирин", "Баромад")]
	[InlineData("ru", "Недавние рабочие пространства", "Выход")]
	[InlineData("fr", "Espaces de travail récents", "Quitter")]
	[InlineData("de", "Letzte Arbeitsbereiche", "Beenden")]
	public async Task CompactWelcomeShowsLongestLocalizedActionsWithoutClipping(
		string language,
		string recentWorkspacesAction,
		string exitAction)
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("notes.txt", "markerless directory");
		await using var terminal = await TerminalPtyHarness.StartAsync(
			workspace.Path,
			["--language", language],
			columns: 80,
			rows: 24,
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			recentWorkspacesAction,
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			exitAction,
			cancellationToken: TestContext.Current.CancellationToken);
		var screen = terminal.CaptureScreen();
		Assert.Contains(recentWorkspacesAction, screen, StringComparison.Ordinal);
		Assert.DoesNotContain("[[", screen, StringComparison.Ordinal);
		Assert.Contains(exitAction, screen, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	[Fact(Timeout = 60_000)]
	public async Task TooSmallTerminalRecoversAfterResizeWithoutRestarting()
	{
		using var project = CreateProject();
		await using var terminal = await TerminalPtyHarness.StartAsync(
			project.Path,
			[
				"tui",
				project.Path,
				"--profile",
				"standard",
				"--screen",
				"inline",
				"--no-mouse",
				"--language",
				"en"
			],
			columns: 50,
			rows: 15,
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			"Terminal too small",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);

		await terminal.ResizeAsync(80, 24, TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Terminal too small",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);

		await terminal.ResizeAsync(50, 15, TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Terminal too small",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.ResizeAsync(120, 30, TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(cancellationToken: TestContext.Current.CancellationToken));
	}

	[Fact(Timeout = 60_000)]
	public async Task LargeTerminalKeepsBothWorkspacePanesAndContextualHelpUsable()
	{
		using var project = CreateProject();
		await using var terminal = await TerminalPtyHarness.StartAsync(
			project.Path,
			[
				"tui",
				project.Path,
				"--profile",
				"standard",
				"--screen",
				"inline",
				"--no-mouse",
				"--language",
				"en"
			],
			columns: 160,
			rows: 40,
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		var screen = await terminal.WaitForScreenAsync(
			"Files 2",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("CONTEXT PREVIEW", screen, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);

		await terminal.SendAsync("?", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"WORKSPACE",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"ACTION PALETTE",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(cancellationToken: TestContext.Current.CancellationToken));
	}

	[Theory(Timeout = 90_000)]
	[InlineData("inline")]
	[InlineData("alternate")]
	[InlineData("auto")]
	public async Task ScreenModesExitExplicitlyAndRestoreTerminalState(string screenMode)
	{
		using var project = CreateProject();
		await using var terminal = await TerminalPtyHarness.StartAsync(
			project.Path,
			[
				"tui",
				project.Path,
				"--profile",
				"standard",
				"--screen",
				screenMode,
				"--no-mouse",
				"--language",
				"en"
			],
			columns: 80,
			rows: 24,
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);
		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(cancellationToken: TestContext.Current.CancellationToken));

		Assert.Contains("\u001b[?25l", terminal.RawOutput, StringComparison.Ordinal);
		Assert.Contains("\u001b[?25h", terminal.RawOutput, StringComparison.Ordinal);
		Assert.Contains("\u001b[?2004h", terminal.RawOutput, StringComparison.Ordinal);
		Assert.Contains("\u001b[?2004l", terminal.RawOutput, StringComparison.Ordinal);
		Assert.True(
			terminal.RawOutput.Contains("\u001b[?1003;1006h", StringComparison.Ordinal) ||
			(terminal.RawOutput.Contains("\u001b[?1003h", StringComparison.Ordinal) &&
			 terminal.RawOutput.Contains("\u001b[?1006h", StringComparison.Ordinal)),
			"Mouse tracking was not enabled.");
		Assert.True(
			terminal.RawOutput.Contains("\u001b[?1003;1006l", StringComparison.Ordinal) ||
			(terminal.RawOutput.Contains("\u001b[?1003l", StringComparison.Ordinal) &&
			 terminal.RawOutput.Contains("\u001b[?1006l", StringComparison.Ordinal)),
			"Mouse tracking was not restored.");
	}

	[Fact(Timeout = 60_000)]
	public async Task InvalidProjectShowsRecoverableErrorAndReturnsToWelcome()
	{
		using var workspace = new TemporaryDirectory();
		var missingProject = Path.Combine(workspace.Path, "missing-project");
		await using var terminal = await TerminalPtyHarness.StartAsync(
			workspace.Path,
			[
				"tui",
				missingProject,
				"--profile",
				"standard",
				"--screen",
				"inline",
				"--no-mouse",
				"--language",
				"en"
			],
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			"DPX-PROJECT-NOT-FOUND",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"DPX-PROJECT-NOT-FOUND",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Choose a workspace action",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);

		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(cancellationToken: TestContext.Current.CancellationToken));
	}

	private static TemporaryDirectory CreateProject()
	{
		var project = new TemporaryDirectory();
		project.WriteFile("global.json", "{}");
		project.WriteFile("src/App.cs", "internal sealed class App {}");
		return project;
	}
}
