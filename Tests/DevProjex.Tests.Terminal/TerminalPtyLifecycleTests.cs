using System.Text.RegularExpressions;

namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class TerminalPtyLifecycleTests
{
	[Fact(Timeout = 60_000)]
	public async Task ExplicitTuiOpensReadableMarkerlessDirectory()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("notes.txt", "markerless directory");
		await using var terminal = await TerminalPtyHarness.StartAsync(
			workspace.Path,
			[
				"tui",
				workspace.Path,
				"--profile",
				"standard",
				"--screen",
				"inline",
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
		await terminal.WaitForScreenAsync(
			"notes.txt",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);

		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(cancellationToken: TestContext.Current.CancellationToken));
	}

	[Fact(Timeout = 60_000)]
	public async Task CtrlCAndQAtWelcomeUseTheSameExitConfirmation()
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
		await terminal.WaitForScreenAsync(
			"Exit DevProjex Terminal?",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	[Theory(Timeout = 90_000)]
	[InlineData(60, 20)]
	[InlineData(60, 24)]
	[InlineData(80, 24)]
	[InlineData(100, 30)]
	[InlineData(120, 30)]
	[InlineData(150, 35)]
	[InlineData(160, 40)]
	public async Task SupportedTerminalSizeMatrixRemainsKeyboardUsableAndWithinViewport(
		int columns,
		int rows)
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
			columns,
			rows,
			cancellationToken: TestContext.Current.CancellationToken);

		var treeScreen = await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.DoesNotContain("Terminal too small", treeScreen, StringComparison.Ordinal);
		AssertViewportWidth(treeScreen, columns);

		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		var previewScreen = await terminal.WaitForScreenAsync(
			"CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);
		AssertViewportWidth(previewScreen, columns);
		Assert.False(terminal.HasExited);

		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
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
	[InlineData("zh-cn", "最近的工作空间", "退出")]
	[InlineData("ja", "最近のワークスペース", "終了")]
	[InlineData("ko", "최근 작업공간", "종료")]
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
		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
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

		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
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

		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
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
			cancellationToken: TestContext.Current.CancellationToken,
			writeShellCompletionMarker: true);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);
		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
		await terminal.CompleteShellRestorationHandshakeAsync(
			TestContext.Current.CancellationToken);

		var output = terminal.RawOutput;
		TerminalPtyStateAssertions.AssertRestoredAtShellCompletion(output, screenMode);
		Assert.Contains("\u001b[?25l", output, StringComparison.Ordinal);
		Assert.Contains("\u001b[?25h", output, StringComparison.Ordinal);
		Assert.Contains("\u001b[?2004h", output, StringComparison.Ordinal);
		Assert.Contains("\u001b[?2004l", output, StringComparison.Ordinal);
		await terminal.ReleaseParentShellAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(cancellationToken: TestContext.Current.CancellationToken));
	}

	[Fact(Timeout = 90_000)]
	public async Task ExplicitNoMouseModeNeverEmitsMouseEnableSequence()
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
			columns: 80,
			rows: 24,
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(cancellationToken: TestContext.Current.CancellationToken));

		var output = terminal.RawOutput;
		if (TerminalPtyStateAssertions.MatchesKnownTerminalGuiNoMouseInitialization(output))
		{
			Assert.Skip(
				"Terminal.Gui 2.4.17 AnsiOutput unconditionally enables mouse tracking " +
				"before the application mouse policy is available. DevProjex disables it " +
				"before input starts, but cannot certify the stricter no-sequence invariant. " +
				"Upstream source: https://github.com/tui-cs/Terminal.Gui/blob/" +
				"d0a0ed9b150d3fc8aacf4ab07b7f7d91264fe6d6/Terminal.Gui/Drivers/" +
				"AnsiDriver/AnsiOutput.cs#L128-L150");
		}

		Assert.False(
			MouseTrackingWasEnabled(output),
			$"Mouse tracking was enabled despite --no-mouse. Trace: {DescribeMouseTrackingTransitions(output)}");
	}

	[Fact(Timeout = 90_000)]
	public async Task ExplicitMouseModeEnablesTrackingAndRestoresItOnExit()
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
				"--mouse",
				"--language",
				"en"
			],
			columns: 80,
			rows: 24,
			cancellationToken: TestContext.Current.CancellationToken,
			writeShellCompletionMarker: true);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
		await terminal.CompleteShellRestorationHandshakeAsync(
			TestContext.Current.CancellationToken);

		var output = terminal.RawOutput;
		Assert.True(MouseTrackingWasEnabled(output), "Mouse tracking was not enabled.");
		TerminalPtyStateAssertions.AssertRestoredAtShellCompletion(output, "inline");
		await terminal.ReleaseParentShellAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(cancellationToken: TestContext.Current.CancellationToken));
	}

	[Fact(Timeout = 90_000)]
	public async Task ExplicitNoMouseModeIgnoresMouseInput()
	{
		using var project = CreateProject();
		var projectName = Path.GetFileName(project.Path);
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
			columns: 80,
			rows: 24,
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			$"[x] {projectName}",
			cancellationToken: TestContext.Current.CancellationToken);
		var rootRow = FindVisibleTreeRow(
			terminal.CaptureScreen(),
			$"[x] {projectName}");
		Assert.True(rootRow >= 0);

		await terminal.SendMouseClickAsync(
			column: 4,
			row: rootRow,
			cancellationToken: TestContext.Current.CancellationToken);
		await Task.Delay(250, TestContext.Current.CancellationToken);

		var screen = terminal.CaptureScreen();
		Assert.Contains($"[x] {projectName}", screen, StringComparison.Ordinal);
		Assert.DoesNotContain($"[ ] {projectName}", screen, StringComparison.Ordinal);
		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(cancellationToken: TestContext.Current.CancellationToken));
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

		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
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

	private static void AssertViewportWidth(string screen, int columns)
	{
		Assert.All(
			screen.Split('\n'),
			line => Assert.True(
				TerminalCellWidth.Measure(line.TrimEnd('\r')) <= columns,
				$"Rendered line exceeds {columns} terminal cells: {line}"));
	}

	private static int FindVisibleTreeRow(string screen, string expected)
	{
		var lines = screen.Split('\n');
		for (var row = 0; row < lines.Length; row++)
		{
			var separator = lines[row].IndexOf("││", StringComparison.Ordinal);
			var tree = separator >= 0 ? lines[row][..separator] : lines[row];
			if (tree.Contains(expected, StringComparison.Ordinal))
				return row;
		}
		return -1;
	}

	private static bool MouseTrackingWasEnabled(string output) =>
		output.Contains("\u001b[?1003;1006h", StringComparison.Ordinal) ||
		output.Contains("\u001b[?1003h", StringComparison.Ordinal) &&
		output.Contains("\u001b[?1006h", StringComparison.Ordinal);

	private static string DescribeMouseTrackingTransitions(string output) =>
		string.Join(
			", ",
			Regex.Matches(
					output,
					"\u001b\\[\\?(?:1003;1006|1003|1006|1015)[hl]",
					RegexOptions.CultureInvariant)
				.Cast<Match>()
				.Select(match => $"{match.Index}:{match.Value.Replace("\u001b", "<ESC>", StringComparison.Ordinal)}"));

}
