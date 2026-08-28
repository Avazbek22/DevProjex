using System.Diagnostics;
using DevProjex.Infrastructure.RecentProjects;

namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class TerminalInteractionVisualSnapshotTests
{
	private const int SnapshotProjectPathLength = 91;
	private const int SnapshotIdentifierLength = 32;
	private const int SnapshotOwnerPathLength =
		SnapshotProjectPathLength - SnapshotIdentifierLength - 1;

	[Fact(Timeout = 90_000)]
	public async Task PopulatedRecentProjectSnapshotsCoverSelectionLoadingAndWorkspace()
	{
		using var snapshotOwner = new FixedLengthSnapshotDirectory(
			SnapshotOwnerPathLength);
		var projects = Directory.CreateDirectory(
			Path.Combine(snapshotOwner.Path, Guid.NewGuid().ToString("N"))).FullName;
		var firstProject = CreateProject(projects, "AlphaProject", "AlphaMarker.cs");
		var secondProject = CreateProject(projects, "Beta Project", "BetaMarker.cs");
		var welcomeDirectory = Directory.CreateDirectory(
			Path.Combine(snapshotOwner.Path, Guid.NewGuid().ToString("N"))).FullName;
		WriteFile(welcomeDirectory, "notes.txt", "not a project");
		string? dataRoot = null;

		await using var terminal = await TerminalPtyHarness.StartAsync(
			welcomeDirectory,
			["--language", "en"],
			columns: 120,
			rows: 30,
			environment: new Dictionary<string, string>
			{
				[TerminalProgressCheckpointProtocol.PhasesVariable] = "project-loading"
			},
			initializeDataRoot: root =>
			{
				dataRoot = root;
				var store = new RecentProjectsStore(() => root);
				var snapshot = store.AddFolder(null, secondProject);
				store.AddFolder(snapshot, firstProject);
			},
			useProgressCheckpointHost: true,
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			"Choose a workspace action",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> Recent workspaces",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "AlphaProject");
		Verify(
			"recent-populated-en-120x30",
			terminal,
			projects,
			welcomeDirectory);

		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(
			terminal,
			screen => screen.Split('\n').Any(
				line => line.Contains("> Folder", StringComparison.Ordinal) &&
				        line.Contains("Beta Project", StringComparison.Ordinal)),
			"selected recent workspace 'Beta Project'");
		Verify(
			"recent-selected-en-120x30",
			terminal,
			projects,
			welcomeDirectory);

		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		Assert.NotNull(dataRoot);
		var checkpointRoot = Path.Combine(
			dataRoot,
			TerminalProgressCheckpointProtocol.DirectoryName);
		await WaitForCheckpointAsync(
			checkpointRoot,
			"project-loading",
			TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "Loading project");
		Verify(
			"recent-loading-en-120x30",
			terminal,
			projects,
			welcomeDirectory);
		ReleaseCheckpoint(checkpointRoot, "project-loading");

		await WaitForStableScreenAsync(terminal, "BetaMarker.cs");
		Verify(
			"recent-workspace-en-120x30",
			terminal,
			projects,
			welcomeDirectory);
		Assert.False(terminal.HasExited);
		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task PreviewSnapshotsProveFocusScrollingAndCompactNavigation()
	{
		using var project = CreateScrollableProject();
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
			columns: 120,
			rows: 30,
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			timeout: TimeSpan.FromSeconds(45),
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("2", TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "ContentMarker001");
		Verify("workspace-tree-focused-en-120x30", terminal, project.Path);

		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "> CONTEXT PREVIEW");
		Verify("workspace-preview-focused-en-120x30", terminal, project.Path);

		var before = terminal.CaptureScreen();
		await terminal.SendPageDownAsync(TestContext.Current.CancellationToken);
		await WaitForScreenChangeAsync(
			terminal,
			before,
			TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "ContentMarker");
		Verify("workspace-preview-scrolled-en-120x30", terminal, project.Path);

		await terminal.ResizeAsync(80, 24, TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "Tab/F6 Parameters");
		Assert.Contains(
			"> CONTEXT PREVIEW",
			terminal.CaptureScreen(),
			StringComparison.Ordinal);
		Verify("workspace-preview-focused-en-80x24", terminal, project.Path);
		Assert.DoesNotContain("> PROJECT TREE", terminal.CaptureScreen(), StringComparison.Ordinal);

		await terminal.ResizeAsync(120, 30, TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(
			terminal,
			"1/2/3 View");
		await terminal.WaitForScreenAsync(
			"> CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);
		await ExitAsync(terminal);
	}

	private static string CreateProject(
		string owner,
		string directoryName,
		string markerFile)
	{
		var project = Path.Combine(owner, directoryName);
		WriteFile(project, "global.json", "{}");
		WriteFile(
			project,
			Path.Combine("src", markerFile),
			"internal sealed class Marker {}");
		return project;
	}

	private static void WriteFile(
		string root,
		string relativePath,
		string content)
	{
		var path = Path.Combine(root, relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content, new UTF8Encoding(false));
	}

	private static TemporaryDirectory CreateScrollableProject()
	{
		var project = new TemporaryDirectory();
		project.WriteFile("global.json", "{}");
		for (var index = 1; index <= 60; index++)
		{
			project.WriteFile(
				$"src/Feature{index:D3}.cs",
				$"internal sealed class ContentMarker{index:D3} {{ }}");
		}
		return project;
	}

	private static async Task WaitForCheckpointAsync(
		string root,
		string checkpoint,
		CancellationToken cancellationToken)
	{
		var reached = Path.Combine(
			root,
			TerminalProgressCheckpointProtocol.GetReachedFileName(checkpoint));
		var timeout = Stopwatch.StartNew();
		while (timeout.Elapsed < TimeSpan.FromSeconds(45))
		{
			if (File.Exists(reached))
				return;
			await Task.Delay(25, cancellationToken);
		}
		throw new TimeoutException($"Timed out waiting for checkpoint: {reached}");
	}

	private static void ReleaseCheckpoint(string root, string checkpoint) =>
		File.WriteAllText(
			Path.Combine(
				root,
				TerminalProgressCheckpointProtocol.GetReleaseFileName(checkpoint)),
			string.Empty,
			new UTF8Encoding(false));

	private static async Task WaitForScreenChangeAsync(
		TerminalPtyHarness terminal,
		string previous,
		CancellationToken cancellationToken)
	{
		var timeout = Stopwatch.StartNew();
		while (timeout.Elapsed < TimeSpan.FromSeconds(5))
		{
			var current = terminal.CaptureScreen();
			if (!string.Equals(previous, current, StringComparison.Ordinal))
				return;
			await Task.Delay(40, cancellationToken);
		}
		throw new TimeoutException("Preview viewport did not move.");
	}

	private static Task WaitForStableScreenAsync(
		TerminalPtyHarness terminal,
		string expected) =>
		WaitForStableScreenAsync(
			terminal,
			screen => screen.Contains(expected, StringComparison.Ordinal),
			$"'{expected}'");

	private static async Task WaitForStableScreenAsync(
		TerminalPtyHarness terminal,
		Func<string, bool> matches,
		string expectation)
	{
		var stableSamples = 0;
		var timeout = Stopwatch.StartNew();
		while (timeout.Elapsed < TimeSpan.FromSeconds(10))
		{
			var screen = terminal.CaptureScreen();
			if (!string.IsNullOrWhiteSpace(screen) &&
			    matches(screen))
			{
				stableSamples++;
				if (stableSamples >= 3)
					return;
			}
			else
			{
				stableSamples = 0;
			}

			await Task.Delay(80, TestContext.Current.CancellationToken);
		}

		throw new TimeoutException(
			$"Terminal screen did not remain visibly stable for {expectation}.\n" +
			terminal.CaptureScreen());
	}

	private static void Verify(
		string name,
		TerminalPtyHarness terminal,
		string primaryRoot,
		string? secondaryRoot = null)
	{
		var replacements = new List<(string Value, string Replacement)>
		{
			(primaryRoot, "<PROJECTS_ROOT>"),
			(Path.GetDirectoryName(primaryRoot) ?? string.Empty, "<TEMP_ROOT>")
		};
		if (!string.IsNullOrWhiteSpace(secondaryRoot))
			replacements.Add((secondaryRoot, "<WELCOME_ROOT>"));
		TerminalScreenSnapshot.Verify(name, terminal.CaptureScreen(), replacements.ToArray());
		TerminalVisualArtifactWriter.WriteIfRequested(name, terminal);
	}

	private static async Task ExitAsync(TerminalPtyHarness terminal)
	{
		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}
}
