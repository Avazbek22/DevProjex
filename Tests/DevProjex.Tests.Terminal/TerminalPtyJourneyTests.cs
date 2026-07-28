using System.Diagnostics;

namespace DevProjex.Tests.Terminal;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TerminalProcessCollection
{
	public const string Name = "terminal-process";
}

[Collection(TerminalProcessCollection.Name)]
public sealed class TerminalPtyJourneyTests
{
	[Fact(Timeout = 60_000)]
	public async Task ImplicitTuiPreservesExplicitlySavedInlineScreenMode()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("notes.txt", "markerless directory");
		string? dataRoot = null;
		await using var terminal = await TerminalPtyHarness.StartAsync(
			workspace.Path,
			["--language", "en"],
			cancellationToken: TestContext.Current.CancellationToken,
			initializeDataRoot: root =>
			{
				dataRoot = root;
				new TerminalSettingsStore(() => root)
					.SaveScreenModeAsync(
						TerminalScreenMode.Inline,
						TestContext.Current.CancellationToken)
					.GetAwaiter()
					.GetResult();
			});

		await terminal.WaitForScreenAsync(
			"q Exit",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.DoesNotContain("\u001b[?1049h", terminal.RawOutput, StringComparison.Ordinal);
		Assert.Equal(
			TerminalScreenMode.Inline,
			new TerminalSettingsStore(() => dataRoot!).LoadScreenMode());

		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	[Fact(Timeout = 60_000)]
	public async Task OpenCurrentProjectFromWelcomeReachesWorkspaceAndStaysAlive()
	{
		using var workspace = CreateProject();
		workspace.WriteFile("package.json", "{}");
		await using var terminal = await TerminalPtyHarness.StartAsync(
			workspace.Path,
			["--language", "en"],
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			"> Open current directory",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var workspaceScreen = await terminal.WaitForScreenAsync(
			"App.cs",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("PROJECT TREE", workspaceScreen, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);

		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	[Fact(Timeout = 90_000)]
	public async Task SuccessfulLocalCloneOpensWorkspaceWithoutClosingTerminal()
	{
		using var source = CreateGitProject();
		using var welcomeDirectory = new TemporaryDirectory();
		welcomeDirectory.WriteFile("notes.txt", "markerless directory");
		await using var terminal = await TerminalPtyHarness.StartAsync(
			welcomeDirectory.Path,
			["--language", "en"],
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			"Choose a workspace action",
			cancellationToken: TestContext.Current.CancellationToken);
		await SelectWelcomeActionAsync(
			terminal,
			"Clone repository",
			TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Repository URL",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync(
			new Uri(source.Path).AbsoluteUri,
			TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);

		var workspace = await terminal.WaitForScreenAsync(
			"App.cs",
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("PROJECT TREE", workspace, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		await terminal.SendF6Async(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendShiftF6Async(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	[Fact(Timeout = 60_000)]
	public async Task WelcomeJourneysReturnToRootUntilExplicitExit()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("notes.txt", "markerless directory");
		await using var terminal = await TerminalPtyHarness.StartAsync(
			workspace.Path,
			["--language", "en"],
			cancellationToken: TestContext.Current.CancellationToken);

		var welcome = await terminal.WaitForScreenAsync(
			"Choose a workspace action",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("Browse folder", welcome, StringComparison.Ordinal);
		Assert.Contains("Clone repository", welcome, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		Assert.Contains("Recent projects", welcome, StringComparison.Ordinal);
		await SelectWelcomeActionAsync(
			terminal,
			"Recent projects",
			TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"(none available)",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"(none available)",
			cancellationToken: TestContext.Current.CancellationToken);

		await SelectWelcomeActionAsync(
			terminal,
			"Browse folder",
			TestContext.Current.CancellationToken);
		AssertSelectionIsVisible(terminal, "Browse folder", "Clone repository");

		await terminal.SendAsync("?", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Prepare a controlled project context without leaving the terminal.",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Prepare a controlled project context without leaving the terminal.",
			cancellationToken: TestContext.Current.CancellationToken);

		await SelectWelcomeActionAsync(
			terminal,
			"Browse folder",
			TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Select folder",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Select folder",
			cancellationToken: TestContext.Current.CancellationToken);

		await SelectWelcomeActionAsync(
			terminal,
			"Open saved profile",
			TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Find",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Find",
			cancellationToken: TestContext.Current.CancellationToken);

		await SelectWelcomeActionAsync(
			terminal,
			"Clone repository",
			TestContext.Current.CancellationToken);
		AssertSelectionIsVisible(terminal, "Clone repository", "Browse folder");
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Repository URL",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("not-a-repository", TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"DPX-TUI-GIT-URL-INVALID",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"DPX-TUI-GIT-URL-INVALID",
			cancellationToken: TestContext.Current.CancellationToken);

		await SelectWelcomeActionAsync(
			terminal,
			"Exit",
			TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);

		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	[Fact(Timeout = 60_000)]
	public async Task ProjectWorkspaceSupportsNavigationOverlaysAndLiveResize()
	{
		using var workspace = CreateProject();
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
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		var initialWorkspace = await terminal.WaitForScreenAsync(
			"Files 4",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("CONTEXT PREVIEW", initialWorkspace, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);

		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"v [x] src",
			cancellationToken: TestContext.Current.CancellationToken);
		await Task.Delay(150, TestContext.Current.CancellationToken);
		AssertSelectionIsVisible(terminal, "src", "Feature");
		await terminal.SendSpaceAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Files 2",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendSpaceAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Files 4",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync("?", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"WORKSPACE MODEL",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"WORKSPACE MODEL",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync("\t", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"j/k Scroll",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("1", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"CONTEXT PREVIEW · Readable · Tree",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("\t", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> CONTEXT CONTROLS",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("\t", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync("/", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Name contains:",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("Handler", TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Name contains:",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Handler.cs",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendSpaceAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Files 3",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendSpaceAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Files 4",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("/", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Name contains:",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Name contains:",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync("M", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"No Git filtering",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"No Git filtering",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("X", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Toggle all changes only this section",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Toggle all changes only this section",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync("R", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Toggle all changes only this section",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Toggle all changes only this section",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("T", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Toggle all changes only this section",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Toggle all changes only this section",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync("A", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Fingerprint",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Fingerprint",
			cancellationToken: TestContext.Current.CancellationToken);

		var dryRunDestination = Path.Combine(
			Path.GetTempPath(),
			$"devprojex-tui-{Guid.NewGuid():N}.md");
		await terminal.SendAsync("E", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Destination:",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendCtrlAAsync(TestContext.Current.CancellationToken);
		await terminal.SendAsync(dryRunDestination, TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Destination state:",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Validation completed",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendCtrlEndAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"--dry-run",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(File.Exists(dryRunDestination));
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"--dry-run",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.ResizeAsync(80, 24, TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);
		var compact = await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("PROJECT TREE", compact, StringComparison.Ordinal);
		Assert.DoesNotContain("CONTEXT PREVIEW", compact, StringComparison.Ordinal);
		await terminal.ResizeAsync(120, 30, TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);

		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	private static TemporaryDirectory CreateProject()
	{
		var workspace = new TemporaryDirectory();
		workspace.WriteFile("global.json", "{}");
		workspace.WriteFile("src/App.cs", "internal sealed class App {}");
		workspace.WriteFile("src/Feature/Handler.cs", "internal sealed class Handler {}");
		workspace.WriteFile("README.md", "# Test project");
		return workspace;
	}

	private static TemporaryDirectory CreateGitProject()
	{
		var project = CreateProject();
		RunGit(project.Path, "init", "--initial-branch=main");
		RunGit(project.Path, "config", "user.email", "terminal-tests@devprojex.local");
		RunGit(project.Path, "config", "user.name", "DevProjex Terminal Tests");
		RunGit(project.Path, "add", ".");
		RunGit(project.Path, "commit", "-m", "Initial test project");
		return project;
	}

	private static void RunGit(string workingDirectory, params string[] arguments)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = OperatingSystem.IsWindows() ? "git.exe" : "git",
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		using var process = Process.Start(startInfo);
		Assert.NotNull(process);
		var standardOutput = process.StandardOutput.ReadToEnd();
		var standardError = process.StandardError.ReadToEnd();
		process.WaitForExit();
		Assert.True(
			process.ExitCode == 0,
			$"git {string.Join(' ', arguments)} failed with exit code {process.ExitCode}.\n" +
			$"{standardOutput}\n{standardError}");
	}

	private static void AssertSelectionIsVisible(
		TerminalPtyHarness terminal,
		string selectedText,
		string otherText)
	{
		var selectedRow = terminal.FindVisibleRow(selectedText);
		var otherRow = terminal.FindVisibleRow(otherText);
		Assert.True(selectedRow >= 0);
		Assert.True(otherRow >= 0);
		var selectedColumn = terminal.CaptureScreen()
			.Split('\n')[selectedRow]
			.IndexOf(selectedText, StringComparison.Ordinal);
		var otherColumn = terminal.CaptureScreen()
			.Split('\n')[otherRow]
			.IndexOf(otherText, StringComparison.Ordinal);
		var selectedStyle = terminal.CaptureCellStyle(selectedRow, selectedColumn);
		var otherStyle = terminal.CaptureCellStyle(otherRow, otherColumn);
		var otherVisual = (otherStyle.BackgroundMode, otherStyle.Background, otherStyle.Inverse);
		var selectedVisual = (
			selectedStyle.BackgroundMode,
			selectedStyle.Background,
			selectedStyle.Inverse);
		Assert.True(
			otherVisual != selectedVisual,
			$"Selected row has no visible focus style. Selected={selectedVisual}, Other={otherVisual}.{Environment.NewLine}" +
			terminal.CaptureScreen());
	}

	private static async Task SelectWelcomeActionAsync(
		TerminalPtyHarness terminal,
		string action,
		CancellationToken cancellationToken)
	{
		for (var index = 0; index < 20; index++)
		{
			var lines = terminal.CaptureScreen().Split('\n');
			var targetRow = Array.FindIndex(
				lines,
				line => line.Contains(action, StringComparison.Ordinal));
			var selectedRow = Array.FindIndex(
				lines,
				line => line.Contains("│> ", StringComparison.Ordinal));
			if (targetRow < 0 || selectedRow < 0)
				break;
			if (targetRow == selectedRow)
			{
				await Task.Delay(150, cancellationToken);
				if (IsSelected(terminal, action))
					return;
				continue;
			}

			if (targetRow < selectedRow)
				await terminal.SendUpAsync(cancellationToken);
			else
				await terminal.SendDownAsync(cancellationToken);
			await WaitForSelectionToMoveAsync(terminal, selectedRow, cancellationToken);
		}
		throw new Xunit.Sdk.XunitException(
			$"Welcome action '{action}' was not found.\n{terminal.CaptureScreen()}");
	}

	private static bool IsSelected(TerminalPtyHarness terminal, string action) =>
		terminal.CaptureScreen()
			.Split('\n')
			.Any(line => line.Contains($"> {action}", StringComparison.Ordinal));

	private static async Task WaitForSelectionToMoveAsync(
		TerminalPtyHarness terminal,
		int previousRow,
		CancellationToken cancellationToken)
	{
		for (var attempt = 0; attempt < 20; attempt++)
		{
			var selectedRow = Array.FindIndex(
				terminal.CaptureScreen().Split('\n'),
				line => line.Contains("│> ", StringComparison.Ordinal));
			if (selectedRow >= 0 && selectedRow != previousRow)
				return;
			await Task.Delay(25, cancellationToken);
		}
	}
}
