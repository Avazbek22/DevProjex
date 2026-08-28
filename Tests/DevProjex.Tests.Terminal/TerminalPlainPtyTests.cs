using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed partial class TerminalPlainPtyTests
{
	private const string ForbiddenBoxDrawing = "╭╮╰╯│─┌┐└┘├┤┬┴┼";

	[Theory(Timeout = 90_000)]
	[InlineData(80, 24, "workspace-plain-en-80x24")]
	[InlineData(120, 30, "workspace-plain-en-120x30")]
	public async Task PlainWorkspaceIsStableKeyboardAccessibleAndRestoresTerminal(
		int columns,
		int rows,
		string snapshotName)
	{
		using var project = CreateProject();
		await using var terminal = await StartPlainProjectAsync(
			project.Path,
			columns,
			rows,
			writeShellCompletionMarker: true);

		await terminal.WaitForScreenAsync(
			"> PROJECT TREE",
			timeout: TimeSpan.FromSeconds(45),
			cancellationToken: TestContext.Current.CancellationToken);
		if (columns >= 100)
		{
			await terminal.WaitForScreenAsync(
				"|-- src",
				cancellationToken: TestContext.Current.CancellationToken);
			await terminal.WaitForScreenAsync(
				"Lines 1-5/5",
				cancellationToken: TestContext.Current.CancellationToken);
		}
		else
		{
			await Task.Delay(500, TestContext.Current.CancellationToken);
		}
		var first = terminal.CaptureScreen();
		await Task.Delay(400, TestContext.Current.CancellationToken);
		var settled = terminal.CaptureScreen();

		Assert.Equal(first, settled);
		AssertPlainScreen(settled);
		AssertMonochromeCells(terminal, settled);
		Verify(snapshotName, terminal, project.Path);

		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);
		AssertPlainScreen(terminal.CaptureScreen());
		await terminal.SendAsync("?", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"ACTION PALETTE",
			cancellationToken: TestContext.Current.CancellationToken);
		AssertPlainScreen(terminal.CaptureScreen());
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
		await terminal.CompleteShellRestorationHandshakeAsync(
			TestContext.Current.CancellationToken);

		var output = terminal.RawOutput;
		AssertPlainRawOutput(output);
		TerminalPtyStateAssertions.AssertRestoredAtShellCompletion(output, "inline");
		await terminal.ReleaseParentShellAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	[Fact(Timeout = 120_000)]
	public async Task PlainIndeterminateContextProgressUsesStaticAsciiFrame()
	{
		using var project = CreateProject();
		using var output = new TemporaryDirectory();
		var destination = Path.Combine(output.Path, "context.md");
		string? dataRoot = null;
		await using var terminal = await StartPlainProjectAsync(
			project.Path,
			120,
			30,
			new Dictionary<string, string>
			{
				[TerminalProgressCheckpointProtocol.PhasesVariable] = "writing-context"
			},
			path => dataRoot = path,
			useProgressCheckpointHost: true);

		await terminal.WaitForScreenAsync(
			"> PROJECT TREE",
			timeout: TimeSpan.FromSeconds(45),
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("E", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Destination:",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendCtrlAAsync(TestContext.Current.CancellationToken);
		await terminal.SendAsync(destination, TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await ConfirmExportSummaryAsync(terminal);

		var checkpointRoot = Path.Combine(
			Assert.IsType<string>(dataRoot),
			TerminalProgressCheckpointProtocol.DirectoryName);
		await WaitForCheckpointAsync(checkpointRoot, "writing-context");
		await terminal.WaitForScreenAsync(
			"Writing context document",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Export context",
			cancellationToken: TestContext.Current.CancellationToken);
		var first = terminal.CaptureScreen();
		await Task.Delay(600, TestContext.Current.CancellationToken);
		var settled = terminal.CaptureScreen();

		Assert.Equal(first, settled);
		Assert.True(
			settled.Contains("[...]", StringComparison.Ordinal),
			$"Plain indeterminate progress marker is missing:{Environment.NewLine}{settled}");
		AssertPlainScreen(settled);
		ReleaseCheckpoint(checkpointRoot, "writing-context");

		await terminal.WaitForStableScreenAsync(
			"Export completed:",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.True(File.Exists(destination));
		var restoredWorkspace = terminal.CaptureScreen();
		Assert.Contains("> PROJECT TREE", restoredWorkspace, StringComparison.Ordinal);
		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
		AssertPlainRawOutput(terminal.RawOutput);
	}

	private static Task<TerminalPtyHarness> StartPlainProjectAsync(
		string projectPath,
		int columns,
		int rows,
		IReadOnlyDictionary<string, string>? environment = null,
		Action<string>? initializeDataRoot = null,
		bool writeShellCompletionMarker = false,
		bool useProgressCheckpointHost = false) =>
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
				"--plain",
				"--language",
				"en"
			],
			columns,
			rows,
			environment,
			TestContext.Current.CancellationToken,
			initializeDataRoot,
			writeShellCompletionMarker,
			useProgressCheckpointHost);

	private static async Task ConfirmExportSummaryAsync(TerminalPtyHarness terminal)
	{
		await terminal.WaitForScreenAsync(
			"Export?",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
	}

	private static async Task WaitForCheckpointAsync(string root, string checkpoint)
	{
		var path = Path.Combine(
			root,
			TerminalProgressCheckpointProtocol.GetReachedFileName(checkpoint));
		var timeout = Stopwatch.StartNew();
		while (timeout.Elapsed < TimeSpan.FromSeconds(45))
		{
			if (File.Exists(path))
				return;
			await Task.Delay(25, TestContext.Current.CancellationToken);
		}
		throw new TimeoutException($"Timed out waiting for progress checkpoint: {path}");
	}

	private static void ReleaseCheckpoint(string root, string checkpoint) =>
		File.WriteAllText(
			Path.Combine(
				root,
				TerminalProgressCheckpointProtocol.GetReleaseFileName(checkpoint)),
			string.Empty,
			new UTF8Encoding(false));

	private static void AssertPlainScreen(string screen)
	{
		Assert.True(
			!screen.Any(static character => ForbiddenBoxDrawing.Contains(character)),
			$"Plain screen contains box-drawing characters:{Environment.NewLine}{screen}");
		Assert.DoesNotMatch(EmojiPattern(), screen);
		Assert.DoesNotContain(" · ", screen, StringComparison.Ordinal);
		Assert.DoesNotContain("↑", screen, StringComparison.Ordinal);
		Assert.DoesNotContain("↓", screen, StringComparison.Ordinal);
	}

	private static void AssertPlainRawOutput(string output)
	{
		Assert.DoesNotContain(
			output,
			static character => ForbiddenBoxDrawing.Contains(character));
		Assert.DoesNotMatch(EmojiPattern(), output);
	}

	private static void AssertMonochromeCells(
		TerminalPtyHarness terminal,
		string screen)
	{
		var styles = new HashSet<(int FgMode, int Fg, int BgMode, int Bg)>();
		var lines = screen.Split('\n');
		for (var row = 0; row < lines.Length; row++)
		{
			for (var column = 0; column < lines[row].Length; column++)
			{
				if (char.IsWhiteSpace(lines[row][column]))
					continue;
				var style = terminal.CaptureCellStyle(row, column);
				styles.Add((
					style.ForegroundMode,
					style.Foreground,
					style.BackgroundMode,
					style.Background));
			}
		}

		Assert.True(
			styles.Count <= 1,
			$"Plain TUI used multiple color pairs: {string.Join(", ", styles)}");
		Assert.All(
			styles,
			style =>
			{
				Assert.Equal(0, style.FgMode);
				Assert.Equal(0, style.BgMode);
			});
	}

	private static void Verify(
		string name,
		TerminalPtyHarness terminal,
		string projectPath) =>
		TerminalScreenSnapshot.Verify(
			name,
			terminal.CaptureScreen(),
			(projectPath, "<PROJECT_ROOT>"),
			(Path.GetDirectoryName(projectPath) ?? string.Empty, "<TEMP_ROOT>"),
			(Path.GetFileName(projectPath), "<PROJECT>"));

	private static TemporaryDirectory CreateProject()
	{
		var project = new TemporaryDirectory();
		project.WriteFile("src/App.cs", "namespace Sample; public sealed class App {}\n");
		project.WriteFile("README.md", "# Sample\n");
		return project;
	}

	[GeneratedRegex(
		@"[\u2600-\u27BF]|[\uD83C-\uDBFF][\uDC00-\uDFFF]",
		RegexOptions.CultureInvariant)]
	private static partial Regex EmojiPattern();
}
