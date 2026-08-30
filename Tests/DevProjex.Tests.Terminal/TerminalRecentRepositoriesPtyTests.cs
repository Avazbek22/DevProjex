using System.Diagnostics;
using System.Text.RegularExpressions;
using DevProjex.Infrastructure.Git;
using DevProjex.Infrastructure.RecentProjects;

namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed partial class TerminalRecentRepositoriesPtyTests
{
	private const string RepositoryUrl = "https://github.com/Avazbek22/DevProjex";
	private const string CacheFolderName = "DevProjex_8DEEC71CEE019B1";

	[Fact(Timeout = 90_000)]
	public async Task PopulatedCachedRepositoryOpensOfflineWithCleanIdentity()
	{
		using var welcomeDirectory = new TemporaryDirectory();
		welcomeDirectory.WriteFile("notes.txt", "markerless directory");
		string? internalDataRoot = null;
		await using var terminal = await TerminalPtyHarness.StartAsync(
			welcomeDirectory.Path,
			["--language", "en"],
			columns: 150,
			rows: 35,
			cancellationToken: TestContext.Current.CancellationToken,
			initializeDataRoot: dataRoot =>
			{
				internalDataRoot = dataRoot;
				SeedCachedRepository(dataRoot);
			});

		await terminal.WaitForScreenAsync(
			"Choose a workspace action",
			cancellationToken: TestContext.Current.CancellationToken);
		await SelectWelcomeActionAsync(
			terminal,
			"Recent workspaces",
			TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		const string staleInspectionStatus =
			"Inspecting the local DevProjex repository cache.";
		var repositoryList = await terminal.WaitForStableScreenAsync(
			required: "Remove entry",
			forbidden: staleInspectionStatus,
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("DevProjex", repositoryList, StringComparison.Ordinal);
		Assert.Contains("Git", repositoryList, StringComparison.Ordinal);
		Assert.Contains("Recent workspaces", repositoryList, StringComparison.Ordinal);
		Assert.Contains(RepositoryUrl, repositoryList, StringComparison.Ordinal);
		Assert.DoesNotContain(staleInspectionStatus, repositoryList, StringComparison.Ordinal);
		Assert.DoesNotContain("Cached and ready", repositoryList, StringComparison.Ordinal);
		Assert.DoesNotContain("Not cached", repositoryList, StringComparison.Ordinal);
		Assert.DoesNotContain(CacheFolderName, repositoryList, StringComparison.Ordinal);
		Verify(
			"recent-repositories-cached-en-150x35",
			terminal,
			welcomeDirectory.Path);

		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var workspace = await terminal.WaitForStableScreenAsync(
			required: "RepositoryMarker.cs",
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains("DevProjex Terminal · DevProjex", workspace, StringComparison.Ordinal);
		Assert.Contains(RepositoryUrl, workspace, StringComparison.Ordinal);
		Assert.Contains("PROJECT TREE", workspace, StringComparison.Ordinal);
		Assert.Contains("PARAMETERS", workspace, StringComparison.Ordinal);
		Assert.DoesNotContain(CacheFolderName, workspace, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		Assert.NotNull(internalDataRoot);
		var retained = new RepoCacheService(Path.Combine(internalDataRoot, "RepoCache"))
			.ClearAllCacheWithResult();
		Assert.Equal(new CacheClearResult(0, 1, 0), retained);
		Verify(
			"recent-repositories-workspace-en-150x35",
			terminal,
			welcomeDirectory.Path);

		await terminal.SendAsync("\u0010", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Filter actions:",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("metadata", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Inspect project source and repository metadata.",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var details = await terminal.WaitForStableScreenAsync(
			"Last opened",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains(RepositoryUrl, details, StringComparison.Ordinal);
		Assert.Contains("Branch: main", details, StringComparison.Ordinal);
		Assert.Contains("Size:", details, StringComparison.Ordinal);
		Assert.DoesNotContain("Internal cache path", details, StringComparison.Ordinal);
		Assert.DoesNotContain(CacheFolderName, details, StringComparison.Ordinal);
		Assert.DoesNotContain("Source reference", details, StringComparison.Ordinal);
		Verify(
			"repository-source-details-en-150x35",
			terminal,
			welcomeDirectory.Path,
			normalizeRepositorySize: true);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Last opened",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	[Fact(Timeout = 90_000)]
	public async Task MissingRepositoryCacheRequiresExplicitRecoveryAndBackKeepsSessionAlive()
	{
		using var welcomeDirectory = new TemporaryDirectory();
		welcomeDirectory.WriteFile("notes.txt", "markerless directory");
		await using var terminal = await TerminalPtyHarness.StartAsync(
			welcomeDirectory.Path,
			["--language", "en"],
			cancellationToken: TestContext.Current.CancellationToken,
			initializeDataRoot: dataRoot =>
			{
				var store = new RecentProjectsStore(() => dataRoot);
				store.AddRepository(store.Load(), RepositoryUrl);
			});

		await terminal.WaitForScreenAsync(
			"Choose a workspace action",
			cancellationToken: TestContext.Current.CancellationToken);
		await SelectWelcomeActionAsync(
			terminal,
			"Recent workspaces",
			TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForStableScreenAsync(
			required: RepositoryUrl,
			cancellationToken: TestContext.Current.CancellationToken);
		Verify(
			"recent-repositories-missing-en-120x30",
			terminal,
			welcomeDirectory.Path);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForStableScreenAsync(
			required: "network clone is required",
			cancellationToken: TestContext.Current.CancellationToken);
		Verify(
			"recent-repositories-recovery-en-120x30",
			terminal,
			welcomeDirectory.Path);

		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		var repositoryList = await terminal.WaitForStableScreenAsync(
			required: "Last opened:",
			forbidden: "network clone is required",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains(RepositoryUrl, repositoryList, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);

		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForStableScreenAsync(
			required: "Choose a workspace action",
			forbidden: RepositoryUrl,
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains(
			"Choose a workspace action",
			terminal.CaptureScreen(),
			StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	private static void Verify(
		string name,
		TerminalPtyHarness terminal,
		string welcomeDirectory,
		bool normalizeRepositorySize = false)
	{
		var screen = terminal.CaptureScreen();
		if (normalizeRepositorySize)
		{
			screen = RepositorySizePattern().Replace(
				screen,
				static match =>
					(match.Groups["prefix"].Value + "<REPOSITORY_SIZE>")
					.PadRight(match.Length));
		}
		TerminalScreenSnapshot.Verify(
			name,
			screen,
			(welcomeDirectory, "<WELCOME_ROOT>"),
			(Path.GetDirectoryName(welcomeDirectory) ?? string.Empty, "<TEMP_ROOT>"));
		TerminalVisualArtifactWriter.WriteIfRequested(name, terminal);
	}

	[GeneratedRegex(@"(?<prefix> Size:\s*)[^\r\n│]*(?=│)", RegexOptions.CultureInvariant)]
	private static partial Regex RepositorySizePattern();

	private static void SeedCachedRepository(string dataRoot)
	{
		var cachePath = Path.Combine(dataRoot, "RepoCache", CacheFolderName);
		Directory.CreateDirectory(Path.Combine(cachePath, "src"));
		File.WriteAllText(
			Path.Combine(cachePath, "src", "RepositoryMarker.cs"),
			"internal sealed class RepositoryMarker {}",
			new UTF8Encoding(false));
		File.WriteAllText(
			Path.Combine(cachePath, "README.md"),
			"# DevProjex",
			new UTF8Encoding(false));
		RunGit(cachePath, "init", "--initial-branch=main");
		RunGit(cachePath, "config", "user.email", "terminal-tests@devprojex.local");
		RunGit(cachePath, "config", "user.name", "DevProjex Terminal Tests");
		RunGit(cachePath, "add", ".");
		RunGit(cachePath, "commit", "-m", "seed cached repository");
		RunGit(cachePath, "remote", "add", "origin", RepositoryUrl);
		var commit = RunGit(cachePath, "rev-parse", "HEAD").Trim();
		var cache = new RepoCacheService(Path.Combine(dataRoot, "RepoCache"));
		cache.RecordIndexedRepository(
			RepositoryUrl,
			cachePath,
			"main",
			commit);
		var store = new RecentProjectsStore(() => dataRoot);
		store.AddRepository(store.Load(), RepositoryUrl);
	}

	private static string RunGit(string workingDirectory, params string[] arguments)
	{
		var startInfo = new ProcessStartInfo("git")
		{
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
			$"git {string.Join(' ', arguments)} failed: " +
			$"{result.StandardOutput}{result.StandardError}");
		return result.StandardOutput;
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
				if (terminal.CaptureScreen()
				    .Split('\n')
				    .Any(line => line.Contains($"> {action}", StringComparison.Ordinal)))
				{
					return;
				}
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
}
