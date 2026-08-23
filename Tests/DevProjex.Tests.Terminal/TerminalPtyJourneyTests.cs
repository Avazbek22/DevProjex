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
			cancellationToken: TestContext.Current.CancellationToken,
			writeShellCompletionMarker: true,
			verifyExecutableRelaunch: true);

		await terminal.WaitForScreenAsync(
			"> Open current directory",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		var workspaceScreen = await terminal.WaitForScreenAsync(
			"App.cs",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("PROJECT TREE", workspaceScreen, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);

		await terminal.SendF6Async(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.ResizeAsync(80, 24, TestContext.Current.CancellationToken);
		var compactPreview = await terminal.WaitForStableScreenAsync(
			required: "> CONTEXT PREVIEW",
			forbidden: "PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Equal(80, terminal.Columns);
		Assert.Equal(24, terminal.Rows);
		Assert.DoesNotContain("PROJECT TREE", compactPreview, StringComparison.Ordinal);
		await terminal.ResizeAsync(120, 30, TestContext.Current.CancellationToken);
		await terminal.WaitForStableScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendShiftF6Async(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		await terminal.CompleteShellRestorationHandshakeAsync(
			TestContext.Current.CancellationToken);
		TerminalPtyStateAssertions.AssertRestoredAtShellCompletion(
			terminal.RawOutput,
			"alternate");
		await terminal.ReleaseParentShellAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	[Fact(Timeout = 60_000)]
	public async Task DynamicTerminalIdentityPreservesLiteralUnderscores()
	{
		const int terminalColumns = 180;
		using var owner = new TemporaryDirectory();
		var welcomePath = owner.CreateDirectory("Welcome_With_Multiple_Underscores");
		File.WriteAllText(
			Path.Combine(welcomePath, "notes.txt"),
			"markerless directory",
			new UTF8Encoding(false));
		await using var welcome = await TerminalPtyHarness.StartAsync(
			welcomePath,
			["--language", "en"],
			columns: terminalColumns,
			rows: 35,
			cancellationToken: TestContext.Current.CancellationToken);
		var expectedWelcomePath =
			TerminalWorkspaceSession.FitPathToWidth(
				welcomePath,
				terminalColumns - 4);
		await welcome.WaitForStableScreenAsync(
			"Choose a workspace action",
			cancellationToken: TestContext.Current.CancellationToken);
		var welcomeScreen = await welcome.WaitForStableScreenAsync(
			expectedWelcomePath,
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains(
			"Choose a workspace action",
			welcomeScreen,
			StringComparison.Ordinal);
		Assert.Contains(
			expectedWelcomePath,
			welcomeScreen,
			StringComparison.Ordinal);
		TerminalVisualArtifactWriter.WriteIfRequested(
			"literal-underscores-welcome-en-180x35",
			welcome);
		await welcome.SendAsync("q", TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await welcome.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));

		var projectName = "Project_With_Multiple_Underscores";
		var projectPath = owner.CreateDirectory(projectName);
		File.WriteAllText(
			Path.Combine(projectPath, "global.json"),
			"{}",
			new UTF8Encoding(false));
		Directory.CreateDirectory(Path.Combine(projectPath, "src"));
		File.WriteAllText(
			Path.Combine(projectPath, "src", "Marker_With_Underscores.cs"),
			"internal sealed class Marker_With_Underscores {}",
			new UTF8Encoding(false));

		await using var terminal = await TerminalPtyHarness.StartAsync(
			projectPath,
			[
				"tui",
				projectPath,
				"--profile",
				"standard",
				"--screen",
				"inline",
				"--no-mouse",
				"--language",
				"en"
			],
			columns: terminalColumns,
			rows: 35,
			cancellationToken: TestContext.Current.CancellationToken);

		var screen = await terminal.WaitForScreenAsync(
			"Marker_With_Underscores.cs",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains(
			$"DevProjex Terminal · {projectName}",
			screen,
			StringComparison.Ordinal);
		Assert.Contains(
			TerminalWorkspaceSession.FitPathToWidth(projectPath, terminalColumns - 2),
			screen,
			StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		TerminalVisualArtifactWriter.WriteIfRequested(
			"literal-underscores-workspace-en-180x35",
			terminal);

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
		await terminal.WaitForScreenAsync(
			"Clone repository",
			cancellationToken: TestContext.Current.CancellationToken);
		await SelectWelcomeActionAsync(
			terminal,
			"Clone repository",
			TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Repository URL",
			cancellationToken: TestContext.Current.CancellationToken);
		var sourceUri = new Uri(source.Path).AbsoluteUri;
		await terminal.SendAsync(
			sourceUri,
			TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			Path.GetFileName(source.Path),
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);
		var workspace = await terminal.WaitForScreenAsync(
			"App.cs",
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

		var welcome = await terminal.WaitForStableScreenAsync(
			"Browse folder",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("Choose a workspace action", welcome, StringComparison.Ordinal);
		Assert.Contains("Browse folder", welcome, StringComparison.Ordinal);
		Assert.Contains("Clone repository", welcome, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		Assert.Contains("Recent workspaces", welcome, StringComparison.Ordinal);
		Assert.DoesNotContain("Recent Git repositories", welcome, StringComparison.Ordinal);
		Assert.DoesNotContain("Open saved profile", welcome, StringComparison.Ordinal);
		await SelectWelcomeActionAsync(
			terminal,
			"Recent workspaces",
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
			"Prepare controlled project context without leaving the terminal.",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Prepare controlled project context without leaving the terminal.",
			cancellationToken: TestContext.Current.CancellationToken);

		await SelectWelcomeActionAsync(
			terminal,
			"Browse folder",
			TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var browseDialog = await terminal.WaitForStableScreenAsync(
			"Open selects the current folder; Esc cancels.",
			"Filter actions:",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains(
			"Open selects the current folder; Esc cancels.",
			browseDialog,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"Filter actions:",
			browseDialog,
			StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		var restoredWelcome = await terminal.WaitForStableScreenAsync(
			"Choose a workspace action",
			"Open selects the current folder",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains(
			"Choose a workspace action",
			restoredWelcome,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"Open selects the current folder",
			restoredWelcome,
			StringComparison.Ordinal);

		await terminal.SendAsync("\u0010", TestContext.Current.CancellationToken);
		var actionPalette = await terminal.WaitForStableScreenAsync(
			"Filter actions:",
			"Open selects the current folder",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.DoesNotContain(
			"Open selects the current folder",
			actionPalette,
			StringComparison.Ordinal);
		await terminal.SendAsync(
			"Open project with settings file",
			TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Open project with settings file",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Only JSON settings files are shown",
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
			"WORKSPACE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"ACTION PALETTE",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync("\t", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"j/k Scroll",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("1", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"CONTEXT PREVIEW · Tree · ASCII",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("\t", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> PARAMETERS",
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
			"[x] Use .gitignore",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendShiftTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("X", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> PARAMETERS",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendShiftTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync("R", TestContext.Current.CancellationToken);
		Assert.DoesNotContain("ROOT FOLDERS", terminal.CaptureScreen(), StringComparison.Ordinal);
		await terminal.SendAsync("T", TestContext.Current.CancellationToken);
		var fileTypes = await terminal.WaitForScreenAsync(
			"> PARAMETERS",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("File types", fileTypes, StringComparison.Ordinal);
		await terminal.SendShiftTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync("A", TestContext.Current.CancellationToken);
		var analysis = await terminal.WaitForScreenAsync(
			"Files:",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.DoesNotContain("Fingerprint", analysis, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Files:",
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
			"Export?",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendShiftTabAsync(TestContext.Current.CancellationToken);
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
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		var compact = await terminal.WaitForScreenAsync(
			"CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("CONTEXT PREVIEW", compact, StringComparison.Ordinal);
		Assert.DoesNotContain("PROJECT TREE", compact, StringComparison.Ordinal);
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
		await terminal.WaitForScreenAsync(
			action,
			cancellationToken: cancellationToken);
		var timeout = Stopwatch.StartNew();
		while (timeout.Elapsed < TimeSpan.FromSeconds(10))
		{
			var lines = terminal.CaptureScreen().Split('\n');
			var targetRow = Array.FindIndex(
				lines,
				line => line.Contains(action, StringComparison.Ordinal));
			var selectedRow = Array.FindIndex(
				lines,
				line => line.Contains("│> ", StringComparison.Ordinal));
			if (targetRow < 0 || selectedRow < 0)
			{
				await Task.Delay(25, cancellationToken);
				continue;
			}
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
