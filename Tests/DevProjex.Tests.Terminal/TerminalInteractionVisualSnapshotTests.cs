using System.Diagnostics;
using DevProjex.Infrastructure.RecentProjects;

namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class TerminalInteractionVisualSnapshotTests
{
	[Fact(Timeout = 90_000)]
	public async Task PopulatedRecentProjectSnapshotsCoverSelectionLoadingAndWorkspace()
	{
		using var projects = new TemporaryDirectory();
		var firstProject = CreateProject(projects, "AlphaProject", "AlphaMarker.cs");
		var secondProject = CreateProject(projects, "Beta Project", "BetaMarker.cs");
		using var welcomeDirectory = new TemporaryDirectory();
		welcomeDirectory.WriteFile("notes.txt", "not a project");
		string? dataRoot = null;

		await using var terminal = await TerminalPtyHarness.StartAsync(
			welcomeDirectory.Path,
			["--language", "en"],
			columns: 120,
			rows: 30,
			environment: new Dictionary<string, string>
			{
				[TerminalProgressTestCheckpoint.PhasesVariable] = "project-loading"
			},
			initializeDataRoot: root =>
			{
				dataRoot = root;
				var store = new RecentProjectsStore(() => root);
				var snapshot = store.AddFolder(null, secondProject);
				store.AddFolder(snapshot, firstProject);
			},
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			"> Recent projects",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "AlphaProject");
		Verify(
			"recent-populated-en-120x30",
			terminal,
			projects.Path,
			welcomeDirectory.Path);

		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "> [+] Beta Project");
		Verify(
			"recent-selected-en-120x30",
			terminal,
			projects.Path,
			welcomeDirectory.Path);

		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		Assert.NotNull(dataRoot);
		var checkpointRoot = Path.Combine(dataRoot, "tui-progress-checkpoints");
		await WaitForCheckpointAsync(
			checkpointRoot,
			"project-loading",
			TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "Loading project");
		Verify(
			"recent-loading-en-120x30",
			terminal,
			projects.Path,
			welcomeDirectory.Path);
		ReleaseCheckpoint(checkpointRoot, "project-loading");

		await WaitForStableScreenAsync(terminal, "BetaMarker.cs");
		Verify(
			"recent-workspace-en-120x30",
			terminal,
			projects.Path,
			welcomeDirectory.Path);
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
		await WaitForStableScreenAsync(terminal, "Tab/F6 Tree   ? Help");
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
		TemporaryDirectory owner,
		string directoryName,
		string markerFile)
	{
		var project = owner.CreateDirectory(directoryName);
		File.WriteAllText(
			Path.Combine(project, "global.json"),
			"{}",
			new UTF8Encoding(false));
		Directory.CreateDirectory(Path.Combine(project, "src"));
		File.WriteAllText(
			Path.Combine(project, "src", markerFile),
			"internal sealed class Marker {}",
			new UTF8Encoding(false));
		return project;
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
		var reached = Path.Combine(root, $"reached-{checkpoint}");
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
			Path.Combine(root, $"release-{checkpoint}"),
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

	private static async Task WaitForStableScreenAsync(
		TerminalPtyHarness terminal,
		string expected)
	{
		var stableSamples = 0;
		var timeout = Stopwatch.StartNew();
		while (timeout.Elapsed < TimeSpan.FromSeconds(10))
		{
			var screen = terminal.CaptureScreen();
			if (!string.IsNullOrWhiteSpace(screen) &&
			    screen.Contains(expected, StringComparison.Ordinal))
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
			$"Terminal screen did not remain visibly stable for '{expected}'.\n" +
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
		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}
}
