using System.Diagnostics;

namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class TerminalSelectionEvolutionPtyTests
{
	[Fact(Timeout = 120_000)]
	public async Task DisablingGitIgnoreChecksNewExtensionsAndRemembersExplicitState()
	{
		using var project = CreateGitIgnoreProject();
		string? dataRoot = null;
		await using var terminal = await StartAsync(
			project.Path,
			new Dictionary<string, string>
			{
				[TerminalProgressCheckpointProtocol.PhasesVariable] = "background-refresh"
			},
			path => dataRoot = path);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> PARAMETERS",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("X", TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		var before = await terminal.WaitForScreenAsync(
			"[x] Use .gitignore",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.DoesNotContain(".generated", before, StringComparison.Ordinal);
		AssertFrameAggregate(before, "File types", "[x] All (3)");

		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var optimistic = await terminal.WaitForScreenAsync(
			"[ ] Use .gitignore",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.DoesNotContain("Processing request", optimistic, StringComparison.Ordinal);
		var checkpointRoot = GetCheckpointRoot(dataRoot);
		await WaitForCheckpointAsync(checkpointRoot, "background-refresh");
		Assert.Contains("[ ] Use .gitignore", terminal.CaptureScreen(), StringComparison.Ordinal);
		ReleaseCheckpoint(checkpointRoot, "background-refresh");

		var revealed = await terminal.WaitForScreenAsync(
			"[x] .generated",
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);
		AssertFrameAggregate(revealed, "File types", "[x] All (4)");
		TerminalScreenSnapshot.Verify(
			"workspace-selection-evolution-en-160x50",
			revealed,
			(project.Path, "<PROJECT_ROOT>"));

		await terminal.SendAsync("T", TestContext.Current.CancellationToken);
		await terminal.SendHomeAsync(TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"[ ] .generated",
			cancellationToken: TestContext.Current.CancellationToken);

		await ToggleGitIgnoreAsync(terminal, expectedSelected: true);
		await terminal.WaitForScreenWithoutAsync(
			".generated",
			cancellationToken: TestContext.Current.CancellationToken);
		AssertFrameAggregate(terminal.CaptureScreen(), "File types", "[x] All (3)");

		await ToggleGitIgnoreAsync(terminal, expectedSelected: false);
		var returned = await terminal.WaitForScreenAsync(
			"[ ] .generated",
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);
		AssertFrameAggregate(returned, "File types", "[ ] All (4)");
		Assert.False(terminal.HasExited);
		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task RapidTogglesCoalesceIntoOneFinalSettingsRefresh()
	{
		using var project = CreateGitIgnoreProject();
		string? dataRoot = null;
		await using var terminal = await StartAsync(
			project.Path,
			new Dictionary<string, string>
			{
				[TerminalProgressCheckpointProtocol.PhasesVariable] = "background-refresh"
			},
			path => dataRoot = path);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> PARAMETERS",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendHomeAsync(TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);

		await terminal.SendAsync(
			"\r\u001b[B\r\u001b[B\r\u001b[B\r",
			TestContext.Current.CancellationToken);
		var optimistic = await terminal.WaitForScreenAsync(
			"[x] Strip comments",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.DoesNotContain("Processing request", optimistic, StringComparison.Ordinal);
		Assert.Contains("[x] Hide secrets", optimistic, StringComparison.Ordinal);
		Assert.Contains("[x] Hide private data", optimistic, StringComparison.Ordinal);
		Assert.Contains("[x] Compress code", optimistic, StringComparison.Ordinal);
		Assert.Contains("[ ] Strip blank lines", optimistic, StringComparison.Ordinal);

		var checkpointRoot = GetCheckpointRoot(dataRoot);
		await WaitForCheckpointAsync(checkpointRoot, "background-refresh");
		await Task.Delay(250, TestContext.Current.CancellationToken);
		Assert.Equal(1, CountObservations(checkpointRoot, "background-refresh"));
		ReleaseCheckpoint(checkpointRoot, "background-refresh");
		await terminal.WaitForScreenWithoutAsync(
			"Updating options…",
			cancellationToken: TestContext.Current.CancellationToken);
		var completed = terminal.CaptureScreen();
		Assert.Contains("[x] Hide secrets", completed, StringComparison.Ordinal);
		Assert.Contains("[x] Hide private data", completed, StringComparison.Ordinal);
		Assert.Contains("[x] Compress code", completed, StringComparison.Ordinal);
		Assert.Contains("[x] Strip comments", completed, StringComparison.Ordinal);
		Assert.Contains("[ ] Strip blank lines", completed, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task RapidStructuralTogglesCoalesceIntoOneTreeRefresh()
	{
		using var project = CreateGitIgnoreProject();
		string? dataRoot = null;
		await using var terminal = await StartAsync(
			project.Path,
			new Dictionary<string, string>
			{
				[TerminalProgressCheckpointProtocol.PhasesVariable] = "background-refresh"
			},
			path => dataRoot = path);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> PARAMETERS",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("X", TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);

		await terminal.SendAsync(
			"\r\u001b[B\u001b[B\r\u001b[B\r\u001b[B\r",
			TestContext.Current.CancellationToken);
		var optimistic = await terminal.WaitForScreenAsync(
			"[ ] Empty files",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("[ ] Use .gitignore", optimistic, StringComparison.Ordinal);
		Assert.Contains("[ ] Smart ignore", optimistic, StringComparison.Ordinal);
		Assert.Contains("[ ] Empty folders", optimistic, StringComparison.Ordinal);
		Assert.DoesNotContain("Processing request", optimistic, StringComparison.Ordinal);

		var checkpointRoot = GetCheckpointRoot(dataRoot);
		await WaitForCheckpointAsync(checkpointRoot, "background-refresh");
		await terminal.WaitForScreenAsync(
			"Building tree…",
			cancellationToken: TestContext.Current.CancellationToken);
		await Task.Delay(250, TestContext.Current.CancellationToken);
		Assert.Equal(1, CountObservations(checkpointRoot, "background-refresh"));
		ReleaseCheckpoint(checkpointRoot, "background-refresh");

		var completed = await terminal.WaitForScreenAsync(
			"[x] .generated",
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("[ ] Use .gitignore", completed, StringComparison.Ordinal);
		Assert.Contains("[ ] Smart ignore", completed, StringComparison.Ordinal);
		Assert.Contains("[ ] Empty folders", completed, StringComparison.Ordinal);
		Assert.Contains("[ ] Empty files", completed, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		await ExitAsync(terminal);
	}

	private static async Task ToggleGitIgnoreAsync(
		TerminalPtyHarness terminal,
		bool expectedSelected)
	{
		await terminal.SendAsync("X", TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			expectedSelected ? "[x] Use .gitignore" : "[ ] Use .gitignore",
			cancellationToken: TestContext.Current.CancellationToken);
	}

	private static void AssertFrameAggregate(string screen, string title, string aggregate)
	{
		var titleLine = screen.Split('\n').Single(line =>
			line.Contains(title, StringComparison.Ordinal));
		Assert.Contains(aggregate, titleLine, StringComparison.Ordinal);
	}

	private static Task<TerminalPtyHarness> StartAsync(
		string projectPath,
		IReadOnlyDictionary<string, string> environment,
		Action<string> initializeDataRoot) =>
		TerminalPtyHarness.StartAsync(
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
			columns: 160,
			rows: 50,
			environment: environment,
			cancellationToken: TestContext.Current.CancellationToken,
			initializeDataRoot: initializeDataRoot,
			useProgressCheckpointHost: true);

	private static TemporaryDirectory CreateGitIgnoreProject()
	{
		var project = new TemporaryDirectory();
		project.WriteFile("global.json", "{}");
		project.WriteFile("src/App.cs", "internal sealed class App { }");
		project.WriteFile("src/settings.json", "{}");
		project.WriteFile("docs/readme.md", "# Project");
		project.WriteFile("src/ignored.generated", "generated");
		project.WriteFile(".gitignore", "*.generated\n");
		return project;
	}

	private static string GetCheckpointRoot(string? dataRoot)
	{
		Assert.False(string.IsNullOrWhiteSpace(dataRoot));
		return Path.Combine(dataRoot!, TerminalProgressCheckpointProtocol.DirectoryName);
	}

	private static int CountObservations(string root, string checkpoint)
	{
		var path = Path.Combine(
			root,
			TerminalProgressCheckpointProtocol.GetObservedFileName(checkpoint));
		return File.Exists(path) ? File.ReadAllLines(path).Length : 0;
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
		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}
}
