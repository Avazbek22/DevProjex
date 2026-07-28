using System.Diagnostics;

namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class TerminalClonePtyTests
{
	[Fact(Timeout = 120_000)]
	public async Task CloneProgressCancellationCleansCacheAndRetryOpensWorkspace()
	{
		using var originRoot = new TemporaryDirectory();
		var origin = originRoot.CreateDirectory("CombatRepository");
		File.WriteAllText(
			Path.Combine(origin, "CloneMarker.cs"),
			"internal sealed class CloneMarker {}",
			new UTF8Encoding(false));
		InitializeGitRepository(origin);
		using var welcomeDirectory = new TemporaryDirectory();
		welcomeDirectory.WriteFile("notes.txt", "markerless directory");
		string? internalDataRoot = null;
		await using var terminal = await TerminalPtyHarness.StartAsync(
			welcomeDirectory.Path,
			["--language", "en"],
			columns: 120,
			rows: 30,
			environment: new Dictionary<string, string>
			{
				[TerminalProgressTestCheckpoint.PhasesVariable] = "clone-connecting"
			},
			cancellationToken: TestContext.Current.CancellationToken,
			initializeDataRoot: dataRoot => internalDataRoot = dataRoot);

		await terminal.WaitForScreenAsync(
			"Choose a workspace action",
			cancellationToken: TestContext.Current.CancellationToken);
		await StartCloneAsync(
			terminal,
			new Uri(origin).AbsoluteUri,
			TestContext.Current.CancellationToken);
		var checkpointRoot = Path.Combine(
			internalDataRoot!,
			"tui-progress-checkpoints");
		await WaitForFileAsync(
			Path.Combine(checkpointRoot, "reached-clone-connecting"),
			TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Starting the existing Git clone engine.",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Choose a workspace action.",
			cancellationToken: TestContext.Current.CancellationToken);
		var active = terminal.CaptureScreen();
		Assert.Contains("Connecting", active, StringComparison.Ordinal);
		Assert.Contains("CombatRepository", active, StringComparison.Ordinal);
		Assert.Contains("file:///", active, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("Esc or Ctrl+C", active, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		Verify(
			"clone-progress-active-en-120x30",
			terminal,
			originRoot.Path,
			welcomeDirectory.Path);

		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(
			terminal,
			"Operation canceled",
			TestContext.Current.CancellationToken);
		Verify(
			"clone-progress-canceled-en-120x30",
			terminal,
			originRoot.Path,
			welcomeDirectory.Path);
		var cacheRoot = Path.Combine(internalDataRoot!, "RepoCache");
		await WaitUntilAsync(
			() => !Directory.Exists(cacheRoot) ||
			      !Directory.EnumerateDirectories(cacheRoot).Any(),
			TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);

		await StartCloneAsync(
			terminal,
			new Uri(origin).AbsoluteUri,
			TestContext.Current.CancellationToken);
		var workspace = await terminal.WaitForScreenAsync(
			"CloneMarker.cs",
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"internal sealed class CloneMarker",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Lines 1-7/7",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains(
			"DevProjex Terminal  CombatRepository",
			workspace,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"CombatRepository_",
			workspace,
			StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		Verify(
			"clone-workspace-clean-identity-en-120x30",
			terminal,
			originRoot.Path,
			welcomeDirectory.Path);

		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	private static void Verify(
		string name,
		TerminalPtyHarness terminal,
		string originRoot,
		string welcomeDirectory)
	{
		TerminalScreenSnapshot.Verify(
			name,
			terminal.CaptureScreen(),
			(originRoot, "<ORIGIN_ROOT>"),
			(welcomeDirectory, "<WELCOME_ROOT>"),
			(Path.GetDirectoryName(originRoot) ?? string.Empty, "<TEMP_ROOT>"));
		TerminalVisualArtifactWriter.WriteIfRequested(name, terminal);
	}

	private static async Task WaitForStableScreenAsync(
		TerminalPtyHarness terminal,
		string expected,
		CancellationToken cancellationToken)
	{
		var previous = string.Empty;
		var stableSamples = 0;
		var timeout = Stopwatch.StartNew();
		while (timeout.Elapsed < TimeSpan.FromSeconds(10))
		{
			var current = terminal.CaptureScreen();
			if (current.Contains(expected, StringComparison.Ordinal) &&
			    string.Equals(previous, current, StringComparison.Ordinal))
			{
				stableSamples++;
				if (stableSamples >= 3)
					return;
			}
			else
			{
				stableSamples = 0;
			}

			previous = current;
			await Task.Delay(80, cancellationToken);
		}

		throw new TimeoutException(
			$"Screen did not stabilize for '{expected}'.\n{terminal.CaptureScreen()}");
	}

	private static async Task StartCloneAsync(
		TerminalPtyHarness terminal,
		string source,
		CancellationToken cancellationToken)
	{
		await SelectWelcomeActionAsync(terminal, "Clone repository", cancellationToken);
		await terminal.SendEnterAsync(cancellationToken);
		await terminal.WaitForScreenAsync(
			"Repository URL",
			cancellationToken: cancellationToken);
		await terminal.SendAsync(source, cancellationToken);
		await terminal.SendEnterAsync(cancellationToken);
	}

	private static async Task SelectWelcomeActionAsync(
		TerminalPtyHarness terminal,
		string action,
		CancellationToken cancellationToken)
	{
		await terminal.WaitForScreenAsync(
			action,
			cancellationToken: cancellationToken);
		for (var attempt = 0; attempt < 20; attempt++)
		{
			var lines = terminal.CaptureScreen().Split('\n');
			var targetRow = Array.FindIndex(
				lines,
				line => line.Contains(action, StringComparison.Ordinal));
			var selectedRow = Array.FindIndex(
				lines,
				line => line.Contains("│> ", StringComparison.Ordinal));
			if (targetRow == selectedRow && targetRow >= 0)
			{
				await Task.Delay(150, cancellationToken);
				if (terminal.CaptureScreen()
				    .Split('\n')
				    .Any(line => line.Contains($"> {action}", StringComparison.Ordinal)))
				{
					return;
				}
				continue;
			}
			if (targetRow < 0 || selectedRow < 0)
			{
				await Task.Delay(50, cancellationToken);
				continue;
			}

			if (targetRow < selectedRow)
				await terminal.SendUpAsync(cancellationToken);
			else
				await terminal.SendDownAsync(cancellationToken);

			for (var wait = 0; wait < 20; wait++)
			{
				var movedRow = Array.FindIndex(
					terminal.CaptureScreen().Split('\n'),
					line => line.Contains("│> ", StringComparison.Ordinal));
				if (movedRow >= 0 && movedRow != selectedRow)
					break;
				await Task.Delay(25, cancellationToken);
			}
		}

		throw new Xunit.Sdk.XunitException(
			$"Welcome action '{action}' could not be selected.\n{terminal.CaptureScreen()}");
	}

	private static void InitializeGitRepository(string repository)
	{
		RunGit(repository, "init", "--initial-branch=main");
		RunGit(repository, "config", "user.email", "terminal-tests@devprojex.local");
		RunGit(repository, "config", "user.name", "DevProjex Terminal Tests");
		RunGit(repository, "add", ".");
		RunGit(repository, "commit", "-m", "Initial test project");
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

	private static async Task WaitForFileAsync(
		string path,
		CancellationToken cancellationToken)
	{
		await WaitUntilAsync(() => File.Exists(path), cancellationToken);
	}

	private static async Task WaitUntilAsync(
		Func<bool> condition,
		CancellationToken cancellationToken)
	{
		for (var attempt = 0; attempt < 200; attempt++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (condition())
				return;
			await Task.Delay(50, cancellationToken);
		}

		throw new TimeoutException("The expected clone test condition was not reached.");
	}
}
