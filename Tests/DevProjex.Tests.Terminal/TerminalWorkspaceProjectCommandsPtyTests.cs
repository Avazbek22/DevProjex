using System.Diagnostics;
using System.Text;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalWorkspaceProjectCommandsPtyTests
{
	[Fact(Timeout = 120_000)]
	public async Task SelectCommandSupportsQuotedUnicodePathsGlobsAndMissingWarnings()
	{
		using var project = new TemporaryDirectory();
		project.WriteFile("global.json", "{}");
		project.WriteFile("данные проекта/Первый.cs", "class First { }");
		project.WriteFile("src/Second.cs", "class Second { }");
		project.WriteFile("src/readme.md", "# Notes");
		await using var terminal = await StartWorkspaceAsync(project.Path);
		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);

		await ExecuteAsync(
			terminal,
			"select \"данные проекта\" \"src/*.cs\" missing on",
			"DPX-SELECTION-PATH-MISSING");
		var result = terminal.CaptureScreen();
		Assert.Contains("paths not found", result, StringComparison.Ordinal);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);

		await ExecuteAsync(terminal, "profile show", "Selected paths:");
		var profile = terminal.CaptureScreen();
		Assert.Contains("src/Second.cs", profile, StringComparison.Ordinal);
		Assert.Contains("данные проекта", profile, StringComparison.Ordinal);
		Assert.DoesNotContain("src/readme.md", profile, StringComparison.Ordinal);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Selected paths:",
			cancellationToken: TestContext.Current.CancellationToken);
		await QuitAsync(terminal);
	}

	[Fact(Timeout = 120_000)]
	public async Task OpenCommandOpensAQuotedLocalFolderFromWelcome()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("notes.txt", "welcome");
		var project = workspace.CreateDirectory("Проект с пробелами");
		workspace.WriteFile("Проект с пробелами/Marker.cs", "class Marker { }");
		await using var terminal = await StartWelcomeAsync(workspace.Path);
		await terminal.WaitForScreenAsync(
			"Choose a workspace action",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync(
			$":open \"{project}\"\r",
			TestContext.Current.CancellationToken);
		var opened = await terminal.WaitForScreenAsync(
			"Marker.cs",
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains("Проект с пробелами", opened, StringComparison.Ordinal);
		await QuitAsync(terminal);
	}

	[Fact(Timeout = 150_000)]
	public async Task OpenCommandClonesAndOpensALocalRepositoryAfterConfirmation()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("notes.txt", "welcome");
		var origin = workspace.CreateDirectory("LocalRepository");
		workspace.WriteFile("LocalRepository/CloneMarker.cs", "class CloneMarker { }");
		InitializeGitRepository(origin);
		await using var terminal = await StartWelcomeAsync(
			workspace.Path,
			allowFileGitTransport: true);
		await terminal.WaitForScreenAsync(
			"Choose a workspace action",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync(
			$":open \"{new Uri(origin).AbsoluteUri}\"\r",
			TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Clone and open this repository?",
			cancellationToken: TestContext.Current.CancellationToken);
		await AcceptDialogAsync(terminal);
		var opened = await terminal.WaitForScreenAsync(
			"CloneMarker.cs",
			timeout: TimeSpan.FromSeconds(45),
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains("LocalRepository", opened, StringComparison.Ordinal);
		await QuitAsync(terminal);
	}

	[Fact(Timeout = 150_000)]
	public async Task ProfileCommandsLoadShowAndResetTheCurrentWorkspace()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/global.json", "{}");
		workspace.WriteFile("project/src/Inside.cs", "class Inside { }");
		workspace.WriteFile("project/docs/Outside.cs", "class Outside { }");
		var profilePath = workspace.WriteFile(
			"Профиль с пробелами.json",
			"""
			{
			  "schemaVersion": 2,
			  "kind": "devprojex-profile",
			  "selection": {
			    "roots": null,
			    "extensions": [".cs"],
			    "selectedPaths": ["src"],
			    "gitMode": "gitignore",
			    "exclusions": [],
			    "hideSecrets": true,
			    "hidePrivateData": false,
			    "compressCode": false,
			    "stripComments": false,
			    "stripBlankLines": false
			  }
			}
			""");
		await using var terminal = await StartWorkspaceAsync(project);
		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync(
			":profile load \"Профиль с пробелами\"\r",
			TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"[x] Hide secrets",
			timeout: TimeSpan.FromSeconds(45),
			cancellationToken: TestContext.Current.CancellationToken);
		await ExecuteAsync(terminal, "profile show", "Selected paths: src");
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Selected paths: src",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync(":profile reset\r", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Reset this project's settings and selection to the defaults?",
			cancellationToken: TestContext.Current.CancellationToken);
		await AcceptDialogAsync(terminal);
		await terminal.WaitForScreenAsync(
			"[ ] Hide secrets",
			timeout: TimeSpan.FromSeconds(45),
			cancellationToken: TestContext.Current.CancellationToken);
		await ExecuteAsync(terminal, "profile show", "Selected paths: all");
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Selected paths: all",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync(
			$":profile load \"{profilePath}\"\r",
			TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"[x] Hide secrets",
			timeout: TimeSpan.FromSeconds(45),
			cancellationToken: TestContext.Current.CancellationToken);
		await QuitAsync(terminal);
	}

	[Fact(Timeout = 120_000)]
	public async Task NewCommandsReportStrictTokensAndNearestCandidates()
	{
		using var project = new TemporaryDirectory();
		project.WriteFile("global.json", "{}");
		project.WriteFile("src/App.cs", "class App { }");
		await using var terminal = await StartWorkspaceAsync(project.Path);
		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);

		await ExecuteAsync(terminal, "select src maybe", "Similar: off, on");
		await ExecuteAsync(terminal, "opne .", "Similar: open");
		await ExecuteAsync(terminal, "profile loads profile", "Similar: load");
		await QuitAsync(terminal);
	}

	private static Task<TerminalPtyHarness> StartWorkspaceAsync(string projectPath) =>
		TerminalPtyHarness.StartAsync(
			projectPath,
			["tui", projectPath, "--profile", "standard", "--screen", "inline", "--no-mouse", "--language", "en"],
			columns: 160,
			rows: 40,
			cancellationToken: TestContext.Current.CancellationToken);

	private static Task<TerminalPtyHarness> StartWelcomeAsync(
		string workingDirectory,
		bool allowFileGitTransport = false) =>
		TerminalPtyHarness.StartAsync(
			workingDirectory,
			["--language", "en"],
			columns: 120,
			rows: 30,
			cancellationToken: TestContext.Current.CancellationToken,
			allowFileGitTransport: allowFileGitTransport);

	private static async Task ExecuteAsync(
		TerminalPtyHarness terminal,
		string command,
		string expected)
	{
		await terminal.SendAsync($":{command}\r", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			expected,
			timeout: TimeSpan.FromSeconds(45),
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);
	}

	private static async Task AcceptDialogAsync(TerminalPtyHarness terminal)
	{
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
	}

	private static async Task QuitAsync(TerminalPtyHarness terminal)
	{
		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				timeout: TimeSpan.FromSeconds(30),
				cancellationToken: TestContext.Current.CancellationToken));
	}

	private static void InitializeGitRepository(string path)
	{
		RunGit(path, "init", "--initial-branch=main");
		RunGit(path, "config", "user.email", "terminal-tests@devprojex.local");
		RunGit(path, "config", "user.name", "DevProjex Terminal Tests");
		RunGit(path, "add", ".");
		RunGit(path, "commit", "-m", "Initial fixture");
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
		Assert.True(
			result.ExitCode == 0,
			$"git {string.Join(' ', arguments)} failed.\n{result.StandardOutput}\n{result.StandardError}");
	}
}
