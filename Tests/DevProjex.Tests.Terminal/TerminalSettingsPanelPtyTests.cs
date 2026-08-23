using System.Diagnostics;

namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class TerminalSettingsPanelPtyTests
{
	[Fact(Timeout = 90_000)]
	public async Task MiniPanelsRenderAcrossEveryWorkspaceLayout()
	{
		using var project = CreatePanelProject();
		await using var terminal = await StartAsync(project.Path, columns: 160, rows: 50);

		await WaitForStableScreenAsync(terminal, "PROJECT TREE");
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "> PARAMETERS");
		await VerifyLayoutAsync(terminal, project.Path, "workspace-settings-en-160x50");

		await terminal.ResizeAsync(130, 40, TestContext.Current.CancellationToken);
		await VerifyLayoutAsync(terminal, project.Path, "workspace-settings-en-130x40");

		await terminal.ResizeAsync(100, 30, TestContext.Current.CancellationToken);
		await VerifyLayoutAsync(terminal, project.Path, "workspace-settings-en-100x30");

		await terminal.ResizeAsync(70, 24, TestContext.Current.CancellationToken);
		var compact = await VerifyLayoutAsync(
			terminal,
			project.Path,
			"workspace-settings-en-70x24");
		Assert.Contains('▲', ExtractPanel(compact, "Exclusions", "File types"));

		await terminal.ResizeAsync(50, 15, TestContext.Current.CancellationToken);
		var tooSmall = await WaitForStableScreenAsync(terminal, "Terminal too small");
		TerminalScreenSnapshot.Verify(
			"workspace-settings-too-small-en-50x15",
			tooSmall,
			(project.Path, "<PROJECT_ROOT>"));
		Assert.DoesNotContain("Content processing", tooSmall, StringComparison.Ordinal);

		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task ArrowKeysCrossMiniPanelBoundariesAndAllAffectsOnlyItsPanel()
	{
		using var project = CreatePanelProject();
		await using var terminal = await StartAsync(project.Path, columns: 100, rows: 30);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> PARAMETERS",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendEndAsync(TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await Task.Delay(100, TestContext.Current.CancellationToken);
		await terminal.SendEndAsync(TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await Task.Delay(100, TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Files 0",
			cancellationToken: TestContext.Current.CancellationToken);
		var extensionsCleared = await WaitForStableScreenAsync(terminal, "[ ] .cs");
		Assert.Contains("[ ] Hide secrets", extensionsCleared, StringComparison.Ordinal);
		Assert.Contains("[ ] All", ExtractPanel(extensionsCleared, "File types", null));

		await terminal.SendAsync("X", TestContext.Current.CancellationToken);
		await terminal.SendHomeAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var exclusionsCleared = await WaitForStableScreenAsync(terminal, "[ ] Smart ignore");
		Assert.Contains("[ ] Use .gitignore", exclusionsCleared, StringComparison.Ordinal);
		Assert.Contains("[ ] .cs", exclusionsCleared, StringComparison.Ordinal);

		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task RedactionRowsPublishCountersAfterTheirPreviewScansComplete()
	{
		using var project = CreatePanelProject(includeFindings: true);
		await using var terminal = await StartAsync(project.Path, columns: 100, rows: 30);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> PARAMETERS",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendHomeAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var secrets = await terminal.WaitForScreenAsync(
			"Hide secrets (",
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Matches(@"Hide secrets \([1-9][0-9]*(?:/[1-9][0-9]*)?\)", secrets);

		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var privateData = await terminal.WaitForScreenAsync(
			"Hide private data (",
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Matches(@"Hide private data \([1-9][0-9]*(?:/[1-9][0-9]*)?\)", privateData);

		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task TreeScrollBarsAppearOnlyWhileTheExpandedTreeOverflows()
	{
		using var project = CreateScrollableTreeProject();
		await using var terminal = await StartAsync(project.Path, columns: 160, rows: 50);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var expanded = await WaitForStableScreenAsync(terminal, "ExtremelyLongFileName001");
		var expandedTree = ExtractFirstPanel(expanded);
		Assert.Contains('▲', expandedTree);
		Assert.Contains('▼', expandedTree);
		Assert.Contains('◄', expandedTree);
		Assert.Contains('►', expandedTree);

		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var collapsedTree = ExtractFirstPanel(
			await WaitForStableScreenAsync(terminal, "> [x] src"));
		Assert.DoesNotContain('▲', collapsedTree);
		Assert.DoesNotContain('▼', collapsedTree);
		Assert.DoesNotContain('◄', collapsedTree);
		Assert.DoesNotContain('►', collapsedTree);

		await ExitAsync(terminal);
	}

	private static async Task<string> VerifyLayoutAsync(
		TerminalPtyHarness terminal,
		string projectPath,
		string snapshotName)
	{
		var screen = await WaitForStableScreenAsync(terminal, "Content processing");
		Assert.Contains("Exclusions", screen, StringComparison.Ordinal);
		Assert.Contains("File types", screen, StringComparison.Ordinal);
		Assert.Contains("Hide private data", screen, StringComparison.Ordinal);
		Assert.Contains("Strip blank lines", screen, StringComparison.Ordinal);
		Assert.DoesNotContain("ROOT FOLDERS", screen, StringComparison.Ordinal);
		TerminalScreenSnapshot.Verify(
			snapshotName,
			screen,
			(projectPath, "<PROJECT_ROOT>"));
		return screen;
	}

	private static string ExtractPanel(string screen, string title, string? nextTitle)
	{
		var lines = screen.Split('\n');
		var start = Array.FindIndex(lines, line => line.Contains(title, StringComparison.Ordinal));
		Assert.True(start >= 0, $"Panel '{title}' was not rendered.\n{screen}");
		var end = nextTitle is null
			? lines.Length
			: Array.FindIndex(
				lines,
				start + 1,
				line => line.Contains(nextTitle, StringComparison.Ordinal));
		if (end < 0)
			end = lines.Length;
		return string.Join('\n', lines[start..end]);
	}

	private static string ExtractFirstPanel(string screen)
	{
		return string.Join(
			'\n',
			screen.Split('\n').Select(line =>
			{
				var separator = line.IndexOf("││", StringComparison.Ordinal);
				if (separator < 0)
					separator = line.IndexOf("┐┌", StringComparison.Ordinal);
				return separator < 0 ? line : line[..(separator + 1)];
			}));
	}

	private static async Task<string> WaitForStableScreenAsync(
		TerminalPtyHarness terminal,
		string expected)
	{
		await terminal.WaitForScreenAsync(
			expected,
			cancellationToken: TestContext.Current.CancellationToken);
		var timeout = Stopwatch.StartNew();
		var stable = Stopwatch.StartNew();
		var previous = terminal.CaptureScreen();
		while (timeout.Elapsed < TimeSpan.FromSeconds(15))
		{
			await Task.Delay(75, TestContext.Current.CancellationToken);
			var current = terminal.CaptureScreen();
			if (!string.Equals(previous, current, StringComparison.Ordinal))
			{
				previous = current;
				stable.Restart();
				continue;
			}
			if (stable.Elapsed >= TimeSpan.FromMilliseconds(375))
				return current;
		}

		throw new TimeoutException($"Terminal screen did not settle.\n{terminal.CaptureScreen()}");
	}

	private static Task<TerminalPtyHarness> StartAsync(
		string projectPath,
		int columns,
		int rows) =>
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
			columns,
			rows,
			cancellationToken: TestContext.Current.CancellationToken);

	private static TemporaryDirectory CreatePanelProject(bool includeFindings = false)
	{
		var project = new TemporaryDirectory();
		project.WriteFile("global.json", "{}");
		project.WriteFile(
			"src/App.cs",
			includeFindings
				? "const string token = \"ghp_a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL\";\n" +
				  "const string email = \"ivan.petrov@corp.internal\";\n"
				: "internal sealed class App { }");
		project.WriteFile("readme.md", "# Project");
		return project;
	}

	private static TemporaryDirectory CreateScrollableTreeProject()
	{
		var project = new TemporaryDirectory();
		project.WriteFile("global.json", "{}");
		for (var index = 1; index <= 80; index++)
		{
			project.WriteFile(
				$"src/ExtremelyLongFileName{index:D3}WithHorizontalOverflow.cs",
				$"internal sealed class Marker{index:D3} {{ }}");
		}
		return project;
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
