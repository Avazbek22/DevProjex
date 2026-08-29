using System.Diagnostics;

namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class TerminalCornerProgressPtyTests
{
	[Fact(Timeout = 90_000)]
	public async Task DelayedRefreshUsesTheHeaderSlotAcrossResizeAndThenDisappears()
	{
		using var project = CreateProject();
		string? dataRoot = null;
		await using var terminal = await StartAsync(
			project.Path,
			columns: 160,
			rows: 50,
			plain: false,
			new Dictionary<string, string>
			{
				[TerminalProgressCheckpointProtocol.PhasesVariable] = "background-refresh"
			},
			path => dataRoot = path,
			useProgressCheckpointHost: true);

		await FocusFirstContentOptionAsync(terminal);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var optimistic = await terminal.WaitForScreenAsync(
			"[x] Hide secrets",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.DoesNotContain("Processing request", optimistic, StringComparison.Ordinal);
		var checkpointRoot = GetCheckpointRoot(dataRoot);
		await WaitForCheckpointAsync(checkpointRoot, "background-refresh");
		var wide = await terminal.WaitForScreenAsync(
			"Updating options…",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.DoesNotContain("Processing request", wide, StringComparison.Ordinal);
		TerminalScreenSnapshot.Verify(
			"workspace-corner-progress-en-160x50",
			wide,
			(project.Path, "<PROJECT_ROOT>"));

		await terminal.ResizeAsync(100, 30, TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> PARAMETERS",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Updating options…",
			cancellationToken: TestContext.Current.CancellationToken);
		await Task.Delay(250, TestContext.Current.CancellationToken);
		var tabbed = terminal.CaptureScreen();
		TerminalScreenSnapshot.Verify(
			"workspace-corner-progress-en-100x30",
			tabbed,
			(project.Path, "<PROJECT_ROOT>"));

		await terminal.ResizeAsync(59, 19, TestContext.Current.CancellationToken);
		var tooSmall = await terminal.WaitForScreenAsync(
			"Terminal too small",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.DoesNotContain("Updating options", tooSmall, StringComparison.Ordinal);

		await terminal.ResizeAsync(160, 50, TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Updating options…",
			cancellationToken: TestContext.Current.CancellationToken);
		ReleaseCheckpoint(checkpointRoot, "background-refresh");
		await terminal.WaitForScreenWithoutAsync(
			"Updating options…",
			cancellationToken: TestContext.Current.CancellationToken);
		var completed = terminal.CaptureScreen();
		Assert.Contains("[x] Hide secrets", completed, StringComparison.Ordinal);
		Assert.DoesNotContain("Updating options", completed, StringComparison.Ordinal);
		Assert.DoesNotContain("Processing request", completed, StringComparison.Ordinal);
		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task PlainModeUsesStaticCornerTextWithoutASpinner()
	{
		using var project = CreateProject();
		string? dataRoot = null;
		await using var terminal = await StartAsync(
			project.Path,
			columns: 100,
			rows: 30,
			plain: true,
			new Dictionary<string, string>
			{
				[TerminalProgressCheckpointProtocol.PhasesVariable] = "background-refresh"
			},
			path => dataRoot = path,
			useProgressCheckpointHost: true);

		await FocusFirstContentOptionAsync(terminal, plain: true);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var checkpointRoot = GetCheckpointRoot(dataRoot);
		await WaitForCheckpointAsync(checkpointRoot, "background-refresh");
		var screen = await terminal.WaitForScreenAsync(
			"Updating options...",
			cancellationToken: TestContext.Current.CancellationToken);
		var heading = screen.Split('\n')[0];
		Assert.DoesNotContain('⠋', heading);
		Assert.DoesNotContain("| Updating options", heading, StringComparison.Ordinal);
		Assert.DoesNotContain("/ Updating options", heading, StringComparison.Ordinal);
		Assert.DoesNotContain("- Updating options", heading, StringComparison.Ordinal);
		Assert.DoesNotContain("\\ Updating options", heading, StringComparison.Ordinal);
		Assert.DoesNotContain("Processing request", screen, StringComparison.Ordinal);
		ReleaseCheckpoint(checkpointRoot, "background-refresh");
		await terminal.WaitForScreenWithoutAsync(
			"Updating options...",
			cancellationToken: TestContext.Current.CancellationToken);
		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task FastSettingsRefreshDoesNotPaintTheOptionsPhase()
	{
		using var project = CreateProject();
		await using var terminal = await StartAsync(
			project.Path,
			columns: 100,
			rows: 30,
			plain: false,
			environment: null,
			initializeDataRoot: null,
			useProgressCheckpointHost: false);

		await FocusFirstContentOptionAsync(terminal);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);

		var stopwatch = Stopwatch.StartNew();
		var painted = false;
		while (stopwatch.Elapsed < TimeSpan.FromMilliseconds(750))
		{
			var screen = terminal.CaptureScreen();
			painted |= screen.Contains("Updating options…", StringComparison.Ordinal);
			Assert.DoesNotContain("Processing request", screen, StringComparison.Ordinal);
			Assert.False(terminal.HasExited);
			await Task.Delay(10, TestContext.Current.CancellationToken);
		}

		Assert.False(painted);
		Assert.Contains("[x] Compress code", terminal.CaptureScreen(), StringComparison.Ordinal);
		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task FailedBackgroundRefreshClearsTheCornerBeforeShowingTheError()
	{
		using var project = CreateGitProject();
		string? dataRoot = null;
		await using var terminal = await StartAsync(
			project.Path,
			columns: 100,
			rows: 30,
			plain: false,
			new Dictionary<string, string>
			{
				[TerminalProgressCheckpointProtocol.PhasesVariable] = "background-refresh"
			},
			path => dataRoot = path,
			useProgressCheckpointHost: true);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		File.WriteAllText(Path.Combine(project.Path, ".git", "index"), "not-a-git-index");
		await terminal.SendAsync(":set git tracked\r", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Tracked Git files only",
			cancellationToken: TestContext.Current.CancellationToken);

		var checkpointRoot = GetCheckpointRoot(dataRoot);
		await WaitForCheckpointAsync(checkpointRoot, "background-refresh");
		await terminal.WaitForScreenAsync(
			"Building tree…",
			cancellationToken: TestContext.Current.CancellationToken);
		ReleaseCheckpoint(checkpointRoot, "background-refresh");

		var error = await terminal.WaitForScreenAsync(
			"DPX-GIT-TRACKED-INDEX-UNAVAILABLE",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.DoesNotContain("Building tree", error, StringComparison.Ordinal);
		Assert.DoesNotContain("Processing request", error, StringComparison.Ordinal);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"DPX-GIT-TRACKED-INDEX-UNAVAILABLE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> PARAMETERS",
			cancellationToken: TestContext.Current.CancellationToken);
		var rolledBack = terminal.CaptureScreen();
		Assert.Contains("(•) Use .gitignore", rolledBack, StringComparison.Ordinal);
		Assert.Contains("( ) Tracked Git files only", rolledBack, StringComparison.Ordinal);
		await ExitAsync(terminal);
	}

	private static async Task FocusFirstContentOptionAsync(
		TerminalPtyHarness terminal,
		bool plain = false)
	{
		await terminal.WaitForScreenAsync(
			plain ? "> PROJECT TREE" : "PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			plain ? "> PARAMETERS" : "> PARAMETERS",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendHomeAsync(TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
	}

	private static Task<TerminalPtyHarness> StartAsync(
		string projectPath,
		int columns,
		int rows,
		bool plain,
		IReadOnlyDictionary<string, string>? environment,
		Action<string>? initializeDataRoot,
		bool useProgressCheckpointHost)
	{
		var arguments = new List<string>
		{
			"tui",
			projectPath,
			"--profile",
			"standard",
			"--screen",
			"inline",
			"--no-mouse",
			"--language",
			"en"
		};
		if (plain)
			arguments.Add("--plain");
		return TerminalPtyHarness.StartAsync(
			projectPath,
			arguments,
			columns,
			rows,
			environment,
			TestContext.Current.CancellationToken,
			initializeDataRoot,
			useProgressCheckpointHost: useProgressCheckpointHost);
	}

	private static TemporaryDirectory CreateProject()
	{
		var project = new TemporaryDirectory();
		project.WriteFile("global.json", "{}");
		project.WriteFile("src/App.cs", "internal sealed class App { }");
		project.WriteFile("readme.md", "# Project");
		return project;
	}

	private static TemporaryDirectory CreateGitProject()
	{
		var project = CreateProject();
		RunGit(project.Path, "init", "--quiet");
		RunGit(project.Path, "config", "user.email", "terminal-tests@devprojex.local");
		RunGit(project.Path, "config", "user.name", "DevProjex Terminal Tests");
		RunGit(project.Path, "add", "--all");
		RunGit(project.Path, "commit", "--quiet", "-m", "Initial test project");
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
		var result = TerminalTestProcess.Run(startInfo);
		Assert.Equal(0, result.ExitCode);
	}

	private static string GetCheckpointRoot(string? dataRoot)
	{
		Assert.False(string.IsNullOrWhiteSpace(dataRoot));
		return Path.Combine(dataRoot!, TerminalProgressCheckpointProtocol.DirectoryName);
	}

	private static async Task WaitForCheckpointAsync(string root, string checkpoint)
	{
		var path = Path.Combine(
			root,
			TerminalProgressCheckpointProtocol.GetReachedFileName(checkpoint));
		var stopwatch = Stopwatch.StartNew();
		while (stopwatch.Elapsed < TimeSpan.FromSeconds(15))
		{
			if (File.Exists(path))
				return;
			await Task.Delay(25, TestContext.Current.CancellationToken);
		}
		throw new TimeoutException($"Timed out waiting for progress checkpoint: {path}");
	}

	private static void ReleaseCheckpoint(string root, string checkpoint) =>
		File.WriteAllText(
			Path.Combine(root, TerminalProgressCheckpointProtocol.GetReleaseFileName(checkpoint)),
			checkpoint);

	private static async Task ExitAsync(TerminalPtyHarness terminal)
	{
		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}
}
