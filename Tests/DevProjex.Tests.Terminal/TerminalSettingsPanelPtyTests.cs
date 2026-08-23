using System.Diagnostics;
using DevProjex.Infrastructure.ProjectProfiles;

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
		var wide = await VerifyLayoutAsync(
			terminal,
			project.Path,
			"workspace-settings-en-160x50",
			expectWide: true);
		Assert.DoesNotContain("Saved settings:", wide, StringComparison.Ordinal);

		await terminal.ResizeAsync(150, 45, TestContext.Current.CancellationToken);
		await VerifyLayoutAsync(
			terminal,
			project.Path,
			"workspace-settings-en-150x45",
			expectWide: true);

		await terminal.ResizeAsync(130, 40, TestContext.Current.CancellationToken);
		await VerifyLayoutAsync(terminal, project.Path, "workspace-settings-en-130x40");

		await terminal.ResizeAsync(100, 30, TestContext.Current.CancellationToken);
		await VerifyLayoutAsync(terminal, project.Path, "workspace-settings-en-100x30");

		await terminal.ResizeAsync(80, 24, TestContext.Current.CancellationToken);
		await VerifyLayoutAsync(terminal, project.Path, "workspace-settings-en-80x24");

		await terminal.ResizeAsync(70, 24, TestContext.Current.CancellationToken);
		var compact = await VerifyLayoutAsync(
			terminal,
			project.Path,
			"workspace-settings-en-70x24");
		Assert.Contains('▲', ExtractPanel(compact, "Exclusions", "File types"));

		await terminal.ResizeAsync(59, 19, TestContext.Current.CancellationToken);
		var tooSmall = await WaitForStableScreenAsync(terminal, "Terminal too small");
		TerminalScreenSnapshot.Verify(
			"workspace-settings-too-small-en-59x19",
			tooSmall,
			(project.Path, "<PROJECT_ROOT>"));
		Assert.DoesNotContain("Content processing", tooSmall, StringComparison.Ordinal);

		await terminal.ResizeAsync(160, 50, TestContext.Current.CancellationToken);
		await VerifyLayoutAsync(
			terminal,
			project.Path,
			"workspace-settings-en-160x50",
			expectWide: true);

		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task LocalProfileIndicatorUsesOnlyItsOwnLayoutRow()
	{
		using var project = CreatePanelProject();
		await using var terminal = await StartAsync(
			project.Path,
			columns: 100,
			rows: 30,
			profile: "local",
			initializeDataRoot: dataRoot => new ProjectProfileStore(() => dataRoot).SaveProfile(
				project.Path,
				new ProjectSelectionProfile(
					SelectedRootFolders: [],
					SelectedExtensions: [".cs", ".md"],
					SelectedIgnoreOptions: [])));

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);

		var parameters = await WaitForStableScreenAsync(terminal, "Saved settings:");
		Assert.Contains("Saved project set", parameters, StringComparison.Ordinal);
		Assert.Contains("Content processing", parameters, StringComparison.Ordinal);
		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task AggregateRowsRemainVisibleWhileTheirListsScroll()
	{
		using var project = CreatePanelProject();
		await using var terminal = await StartAsync(project.Path, columns: 70, rows: 24);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("X", TestContext.Current.CancellationToken);
		await terminal.SendEndAsync(TestContext.Current.CancellationToken);
		var exclusions = await WaitForStableScreenAsync(terminal, "Exclusions");
		Assert.Contains("[x] All", ExtractPanel(exclusions, "Exclusions", "File types"));

		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendEndAsync(TestContext.Current.CancellationToken);
		var extensions = await WaitForStableScreenAsync(terminal, "File types");
		Assert.Contains("[x] All", ExtractPanel(extensions, "File types", null));
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
		await WaitForPanelContainsAsync(terminal, "Exclusions", "File types", "[ ] All");
		await WaitForStableScreenAsync(terminal, "[ ] All");
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		var exclusionsCleared = await WaitForStableScreenAsync(terminal, "[ ] Use .gitignore");
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
		string snapshotName,
		bool expectWide = false)
	{
		var screen = await WaitForStableScreenAsync(
			terminal,
			"Content processing",
			value => IsCompletedSettingsLayout(value, expectWide));
		Assert.True(screen.Contains("Exclusions", StringComparison.Ordinal), screen);
		Assert.True(screen.Contains("File types", StringComparison.Ordinal), screen);
		Assert.True(screen.Contains("Hide private data", StringComparison.Ordinal), screen);
		Assert.True(screen.Contains("Strip blank lines", StringComparison.Ordinal), screen);
		Assert.DoesNotContain("ROOT FOLDERS", screen, StringComparison.Ordinal);
		TerminalScreenSnapshot.Verify(
			snapshotName,
			screen,
			(projectPath, "<PROJECT_ROOT>"));
		return screen;
	}

	private static bool IsCompletedSettingsLayout(string screen, bool expectWide)
	{
		var lines = screen.Split('\n');
		if (lines.Length < 3 ||
			!lines[0].StartsWith(" DevProjex Terminal", StringComparison.Ordinal))
		{
			return false;
		}

		return expectWide
			? lines.Any(line =>
				line.StartsWith("┌┤  PROJECT TREE", StringComparison.Ordinal) &&
				line.Contains("CONTEXT PREVIEW", StringComparison.Ordinal) &&
				line.Contains("PARAMETERS", StringComparison.Ordinal))
			: lines.Any(line => line.StartsWith("┌┤> PARAMETERS", StringComparison.Ordinal)) &&
			  !screen.Contains("PROJECT TREE", StringComparison.Ordinal) &&
			  !screen.Contains("CONTEXT PREVIEW", StringComparison.Ordinal);
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
		string expected,
		Func<string, bool>? isExpectedLayout = null)
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
			if (isExpectedLayout is not null && !isExpectedLayout(current))
			{
				previous = current;
				stable.Restart();
				continue;
			}
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

	private static async Task WaitForPanelContainsAsync(
		TerminalPtyHarness terminal,
		string title,
		string? nextTitle,
		string expected)
	{
		var timeout = Stopwatch.StartNew();
		while (timeout.Elapsed < TimeSpan.FromSeconds(15))
		{
			var panel = ExtractPanel(terminal.CaptureScreen(), title, nextTitle);
			if (panel.Contains(expected, StringComparison.Ordinal))
				return;
			await Task.Delay(75, TestContext.Current.CancellationToken);
		}

		throw new TimeoutException(
			$"Timed out waiting for '{expected}' in panel '{title}'.\n{terminal.CaptureScreen()}");
	}

	private static Task<TerminalPtyHarness> StartAsync(
		string projectPath,
		int columns,
		int rows,
		string profile = "standard",
		Action<string>? initializeDataRoot = null) =>
		TerminalPtyHarness.StartAsync(
			projectPath,
			[
				"tui",
				projectPath,
				"--profile",
				profile,
				"--screen",
				"inline",
				"--no-mouse",
				"--language",
				"en"
			],
			columns,
			rows,
			initializeDataRoot: initializeDataRoot,
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
