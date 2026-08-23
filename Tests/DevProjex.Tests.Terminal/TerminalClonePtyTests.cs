using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class TerminalClonePtyTests
{
	[Fact(Timeout = 120_000)]
	public async Task SshCloneCannotPromptThroughTheParentPty()
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Skip(
				"The fake OpenSSH /dev/tty probe requires a POSIX test host.");
			return;
		}

		using var fakeTransportRoot = new TemporaryDirectory();
		var fakeBin = fakeTransportRoot.CreateDirectory("fake ssh bin");
		var fakeSsh = Path.Combine(fakeBin, "ssh");
		var batchMarker = Path.Combine(
			fakeTransportRoot.Path,
			"batch-mode-observed");
		File.WriteAllText(
			fakeSsh,
			"#!/bin/sh\n" +
			"case \" $* \" in\n" +
			"  *\" -o BatchMode=yes \"*)\n" +
			"    printf '%s' batch > \"$DPX_FAKE_SSH_BATCH_MARKER\"\n" +
			"    exit 73\n" +
			"    ;;\n" +
			"esac\n" +
			"printf '__DEVPROJEX_FAKE_SSH_TTY_PROMPT__' > /dev/tty\n" +
			"IFS= read -r dpx_secret < /dev/tty\n" +
			"exit 74\n",
			new UTF8Encoding(false));
		File.SetUnixFileMode(
			fakeSsh,
			UnixFileMode.UserRead |
			UnixFileMode.UserWrite |
			UnixFileMode.UserExecute);
		using var welcomeDirectory = new TemporaryDirectory();
		welcomeDirectory.WriteFile("notes.txt", "markerless directory");
		var inheritedPath =
			Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

		await using var terminal = await TerminalPtyHarness.StartAsync(
			welcomeDirectory.Path,
			["--language", "en"],
			columns: 120,
			rows: 30,
			environment: new Dictionary<string, string>
			{
				["PATH"] = fakeBin + Path.PathSeparator + inheritedPath,
				["DPX_FAKE_SSH_BATCH_MARKER"] = batchMarker,
				["GIT_SSH_COMMAND"] = "interactive-user-override",
				["GIT_SSH_VARIANT"] = "simple"
			},
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			"Choose a workspace action",
			cancellationToken: TestContext.Current.CancellationToken);
		await StartCloneAsync(
			terminal,
			"ssh://git@example.invalid/owner/repository.git",
			TestContext.Current.CancellationToken);
		await WaitForFileAsync(
			batchMarker,
			TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"The repository could not be cloned.",
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.DoesNotContain(
			"__DEVPROJEX_FAKE_SSH_TTY_PROMPT__",
			terminal.RawOutput,
			StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"The repository could not be cloned.",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);
		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	[Fact(Timeout = 120_000)]
	public async Task LocalRepositoryCloneThroughApplicationBinaryOpensWorkspaceAndPreservesSource()
	{
		using var originRoot = new TemporaryDirectory();
		var origin = originRoot.CreateDirectory("PublishedCloneRepository");
		File.WriteAllText(
			Path.Combine(origin, "PublishedCloneMarker.cs"),
			"internal sealed class PublishedCloneMarker {}",
			new UTF8Encoding(false));
		InitializeGitRepository(origin);
		var sourceFingerprint = ComputeWorkingTreeFingerprint(origin);
		var sourceHead = RunGit(origin, "rev-parse", "HEAD");
		Assert.Empty(RunGit(origin, "status", "--porcelain=v1", "--untracked-files=all"));
		using var welcomeDirectory = new TemporaryDirectory();
		welcomeDirectory.WriteFile("notes.txt", "markerless directory");

		await using var terminal = await TerminalPtyHarness.StartAsync(
			welcomeDirectory.Path,
			["--language", "en"],
			columns: 120,
			rows: 30,
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			"Choose a workspace action",
			cancellationToken: TestContext.Current.CancellationToken);
		await StartCloneAsync(
			terminal,
			new Uri(origin).AbsoluteUri,
			TestContext.Current.CancellationToken);
		var workspace = await terminal.WaitForScreenAsync(
			"PublishedCloneMarker.cs",
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains(
			"DevProjex Terminal · PublishedCloneRepository",
			workspace,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"PublishedCloneRepository_",
			workspace,
			StringComparison.Ordinal);

		await terminal.SendAsync("3", TestContext.Current.CancellationToken);
		var preview = await terminal.WaitForScreenAsync(
			"internal sealed class PublishedCloneMarker",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("PublishedCloneMarker.cs", preview, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);

		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
		Assert.Equal(sourceFingerprint, ComputeWorkingTreeFingerprint(origin));
		Assert.Equal(sourceHead, RunGit(origin, "rev-parse", "HEAD"));
		Assert.Empty(RunGit(origin, "status", "--porcelain=v1", "--untracked-files=all"));
	}

	[Fact(Timeout = 120_000)]
	public async Task UrlWorkspaceAppliesEveryTransformationAndExportsRedactedZip()
	{
		const string secret = "ghp_a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL";
		const string privateEmail = "ivan.petrov@corp.internal";
		using var originRoot = new TemporaryDirectory();
		var origin = originRoot.CreateDirectory("TransformationRepository");
		Directory.CreateDirectory(Path.Combine(origin, "src"));
		File.WriteAllText(
			Path.Combine(origin, "src", "Config.cs"),
			$$"""
			namespace Sample;

			// remove this comment
			internal sealed class Config
			{
				private const string Token = "{{secret}}";
				private const string Email = "{{privateEmail}}";

				public void Run()
				{
					Console.WriteLine(Token);
				}
			}
			""",
			new UTF8Encoding(false));
		InitializeGitRepository(origin);
		using var welcomeDirectory = new TemporaryDirectory();
		welcomeDirectory.WriteFile("notes.txt", "markerless directory");
		using var output = new TemporaryDirectory();
		var destination = Path.Combine(output.Path, "transformed-project.zip");
		await using var terminal = await TerminalPtyHarness.StartAsync(
			welcomeDirectory.Path,
			["--language", "en"],
			columns: 120,
			rows: 30,
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			"Choose a workspace action",
			cancellationToken: TestContext.Current.CancellationToken);
		await StartCloneAsync(
			terminal,
			new Uri(origin).AbsoluteUri,
			TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Config.cs",
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> PARAMETERS",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendHomeAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"[x] All (5)",
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);
		foreach (var expected in new[]
			{
				"Hide secrets",
				"Hide private data",
				"Compress code",
				"Strip comments",
				"Strip blank lines"
			})
		{
			Assert.Contains(
				$"[x] {expected}",
				terminal.CaptureScreen(),
				StringComparison.Ordinal);
		}

		await terminal.SendAsync("Z", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Choose the physical output kind",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Project copy with hidden data",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Exact destination:",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendCtrlAAsync(TestContext.Current.CancellationToken);
		await terminal.SendAsync(destination, TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Export?",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Export completed:",
			timeout: TimeSpan.FromSeconds(45),
			cancellationToken: TestContext.Current.CancellationToken);

		var extracted = output.CreateDirectory("extracted");
		ZipFile.ExtractToDirectory(destination, extracted);
		var exportedPath = Assert.Single(Directory.EnumerateFiles(
			extracted,
			"Config.cs",
			SearchOption.AllDirectories));
		var exported = await File.ReadAllTextAsync(
			exportedPath,
			TestContext.Current.CancellationToken);
		Assert.DoesNotContain(secret, exported, StringComparison.Ordinal);
		Assert.DoesNotContain(privateEmail, exported, StringComparison.Ordinal);
		Assert.DoesNotContain("remove this comment", exported, StringComparison.Ordinal);
		Assert.DoesNotContain("\n\n", exported.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
		Assert.DoesNotContain("Console.WriteLine(Token)", exported, StringComparison.Ordinal);

		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

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
				[TerminalProgressCheckpointProtocol.PhasesVariable] = "clone-connecting"
			},
			cancellationToken: TestContext.Current.CancellationToken,
			initializeDataRoot: dataRoot => internalDataRoot = dataRoot,
			useProgressCheckpointHost: true);

		await terminal.WaitForScreenAsync(
			"Choose a workspace action",
			cancellationToken: TestContext.Current.CancellationToken);
		await StartCloneAsync(
			terminal,
			new Uri(origin).AbsoluteUri,
			TestContext.Current.CancellationToken);
		var checkpointRoot = Path.Combine(
			internalDataRoot!,
			TerminalProgressCheckpointProtocol.DirectoryName);
		await WaitForFileAsync(
			Path.Combine(
				checkpointRoot,
				TerminalProgressCheckpointProtocol.GetReachedFileName(
					"clone-connecting")),
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
			() => HasNoIncompleteRepository(cacheRoot),
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
		await terminal.SendAsync("3", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"internal sealed class CloneMarker",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Lines 1-",
			cancellationToken: TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(
			terminal,
			"internal sealed class CloneMarker",
			TestContext.Current.CancellationToken);
		Assert.Contains(
			"DevProjex Terminal · CombatRepository",
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

	private static string RunGit(string workingDirectory, params string[] arguments)
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
		return standardOutput.Trim();
	}

	private static string ComputeWorkingTreeFingerprint(string root)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		foreach (var path in Directory
			         .EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
			         .OrderBy(
				         path => Path.GetRelativePath(root, path),
				         StringComparer.Ordinal))
		{
			var relativePath = Path
				.GetRelativePath(root, path)
				.Replace('\\', '/');
			if (relativePath.Equals(".git", StringComparison.Ordinal) ||
			    relativePath.StartsWith(".git/", StringComparison.Ordinal))
			{
				continue;
			}
			hash.AppendData(
				Directory.Exists(path)
					? [(byte)'D']
					: [(byte)'F']);
			hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
			if (File.Exists(path))
				hash.AppendData(File.ReadAllBytes(path));
		}

		return Convert.ToHexString(hash.GetHashAndReset());
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

	private static bool HasNoIncompleteRepository(string cacheRoot)
	{
		if (!Directory.Exists(cacheRoot))
			return true;

		foreach (var directory in Directory.EnumerateDirectories(cacheRoot))
		{
			if (!string.Equals(
				    Path.GetFileName(directory),
				    ".staging",
				    StringComparison.Ordinal))
			{
				return false;
			}

			if (Directory.EnumerateFileSystemEntries(directory).Any())
				return false;
		}

		return true;
	}
}
