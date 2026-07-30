using System.Diagnostics;
using DevProjex.Infrastructure.RecentProjects;

namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class TerminalRecentRepositoriesPtyTests
{
	private const string RepositoryUrl = "https://github.com/Avazbek22/DevProjex";
	private const string CacheFolderName = "DevProjex_8DEEC71CEE019B1";

	[Fact(Timeout = 90_000)]
	public async Task PopulatedCachedRepositoryOpensOfflineWithCleanIdentity()
	{
		using var welcomeDirectory = new TemporaryDirectory();
		welcomeDirectory.WriteFile("notes.txt", "markerless directory");
		await using var terminal = await TerminalPtyHarness.StartAsync(
			welcomeDirectory.Path,
			["--language", "en"],
			columns: 150,
			rows: 35,
			cancellationToken: TestContext.Current.CancellationToken,
			initializeDataRoot: SeedCachedRepository);

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
		Verify(
			"recent-repositories-workspace-en-150x35",
			terminal,
			welcomeDirectory.Path);

		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
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
		var repositoryList = await terminal.WaitForScreenAsync(
			RepositoryUrl,
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains(RepositoryUrl, repositoryList, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);

		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			RepositoryUrl,
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains(
			"Choose a workspace action",
			terminal.CaptureScreen(),
			StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	private static void Verify(
		string name,
		TerminalPtyHarness terminal,
		string welcomeDirectory)
	{
		TerminalScreenSnapshot.Verify(
			name,
			terminal.CaptureScreen(),
			(welcomeDirectory, "<WELCOME_ROOT>"),
			(Path.GetDirectoryName(welcomeDirectory) ?? string.Empty, "<TEMP_ROOT>"));
		TerminalVisualArtifactWriter.WriteIfRequested(name, terminal);
	}

	private static void SeedCachedRepository(string dataRoot)
	{
		var cachePath = Path.Combine(dataRoot, "RepoCache", CacheFolderName);
		var gitDirectory = Path.Combine(cachePath, ".git");
		Directory.CreateDirectory(gitDirectory);
		Directory.CreateDirectory(Path.Combine(gitDirectory, "objects"));
		Directory.CreateDirectory(Path.Combine(gitDirectory, "refs", "heads"));
		File.WriteAllText(
			Path.Combine(gitDirectory, "config"),
			$"""
			[core]
				repositoryformatversion = 0
				bare = false
			[remote "origin"]
				url = {RepositoryUrl}
			""",
			new UTF8Encoding(false));
		File.WriteAllText(
			Path.Combine(gitDirectory, "HEAD"),
			"ref: refs/heads/main\n",
			new UTF8Encoding(false));
		Directory.CreateDirectory(Path.Combine(cachePath, "src"));
		File.WriteAllText(
			Path.Combine(cachePath, "src", "RepositoryMarker.cs"),
			"internal sealed class RepositoryMarker {}",
			new UTF8Encoding(false));
		File.WriteAllText(
			Path.Combine(cachePath, "README.md"),
			"# DevProjex",
			new UTF8Encoding(false));
		var store = new RecentProjectsStore(() => dataRoot);
		store.AddRepository(store.Load(), RepositoryUrl);
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
