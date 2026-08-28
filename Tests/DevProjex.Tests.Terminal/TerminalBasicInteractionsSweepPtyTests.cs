using System.Diagnostics;
using System.Text.RegularExpressions;
using DevProjex.Application.Presentation;
using DevProjex.Application.Services;
using DevProjex.Infrastructure.ResourceStore;
using Terminal.Gui.Text;

namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed partial class TerminalBasicInteractionsSweepPtyTests
{
	[Theory(Timeout = 300_000)]
	[InlineData(160, 50)]
	[InlineData(100, 30)]
	public async Task EveryBasicSettingRoundTripsWithoutModalProgressOrTreeDamage(
		int columns,
		int rows)
	{
		using var project = CreateSweepProject();
		await using var terminal = await StartAsync(project.Path, columns, rows);
		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		var baselineTreeRows = CountTreeRows(terminal.CaptureScreen());
		Assert.True(baselineTreeRows > 1);

		await FocusControlsAsync(terminal);
		await SweepContentOptionsAsync(terminal, baselineTreeRows, columns);
		await SweepGitModesAsync(terminal, baselineTreeRows, columns);
		await SweepEveryExclusionAsync(terminal, baselineTreeRows, columns);
		await SweepAllExclusionsAsync(terminal, baselineTreeRows, columns);
		await SweepAllExtensionsAsync(terminal, baselineTreeRows, columns);
		await SweepIndividualExtensionsAsync(terminal, baselineTreeRows, columns);

		Assert.False(terminal.HasExited);
		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	private static async Task SweepContentOptionsAsync(
		TerminalPtyHarness terminal,
		int baselineTreeRows,
		int columns)
	{
		var labels = new[]
		{
			"Hide secrets",
			"Hide private data",
			"Compress code",
			"Strip comments",
			"Strip blank lines"
		};
		await terminal.SendHomeAsync(TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		for (var index = 0; index < labels.Length; index++)
		{
			await ToggleCurrentRowAndRestoreAsync(
				terminal,
				labels[index],
				initiallySelected: false,
				baselineTreeRows,
				columns);
			if (index + 1 < labels.Length)
				await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		}
	}

	private static async Task SweepGitModesAsync(
		TerminalPtyHarness terminal,
		int baselineTreeRows,
		int columns)
	{
		await terminal.SendAsync("X", TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);

		await ToggleAndWaitAsync(
			terminal,
			screen => screen.Contains("[ ] Use .gitignore", StringComparison.Ordinal) &&
			          screen.Contains("[x] .generated", StringComparison.Ordinal));
		var unfiltered = terminal.CaptureScreen();
		Assert.Contains("[x] .generated", unfiltered, StringComparison.Ordinal);
		Assert.Contains("[x] All (4)", ExtractPanel(unfiltered, "File types", null), StringComparison.Ordinal);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await ToggleAndWaitAsync(
			terminal,
			screen => screen.Contains("[x] Tracked Git files only", StringComparison.Ordinal) &&
			          !screen.Contains(".generated", StringComparison.Ordinal));
		var tracked = terminal.CaptureScreen();
		Assert.Contains("[ ] Use .gitignore", tracked, StringComparison.Ordinal);

		await ToggleAndWaitAsync(
			terminal,
			screen => screen.Contains("[ ] Tracked Git files only", StringComparison.Ordinal) &&
			          screen.Contains("[x] .generated", StringComparison.Ordinal));
		var bothOff = terminal.CaptureScreen();
		Assert.Contains("[ ] Use .gitignore", bothOff, StringComparison.Ordinal);
		Assert.Contains("[x] .generated", bothOff, StringComparison.Ordinal);

		await terminal.SendUpAsync(TestContext.Current.CancellationToken);
		await ToggleAndWaitAsync(
			terminal,
			screen => screen.Contains("[x] Use .gitignore", StringComparison.Ordinal) &&
			          !screen.Contains(".generated", StringComparison.Ordinal));
		await AssertTreeRestoredAsync(terminal, baselineTreeRows, columns);
	}

	private static async Task SweepEveryExclusionAsync(
		TerminalPtyHarness terminal,
		int baselineTreeRows,
		int columns)
	{
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.En);
		await terminal.SendAsync("X", TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);

		var descriptors = ProjectPresentationCatalog.Exclusions;
		for (var index = 0; index < descriptors.Count; index++)
		{
			var label = localization[descriptors[index].LabelKey];
			await ToggleCurrentRowAndRestoreAsync(
				terminal,
				label,
				initiallySelected: true,
				baselineTreeRows,
				columns);
			if (index + 1 < descriptors.Count)
				await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		}
	}

	private static async Task SweepAllExclusionsAsync(
		TerminalPtyHarness terminal,
		int baselineTreeRows,
		int columns)
	{
		await terminal.SendAsync("X", TestContext.Current.CancellationToken);
		await terminal.SendHomeAsync(TestContext.Current.CancellationToken);
		await ToggleAndWaitAsync(
			terminal,
			screen => ExtractPanel(screen, "Exclusions", "File types")
				.Contains("[ ] All", StringComparison.Ordinal));
		await ToggleAndWaitAsync(
			terminal,
			screen => ExtractPanel(screen, "Exclusions", "File types")
				.Contains("[x] All", StringComparison.Ordinal));
		await AssertTreeRestoredAsync(terminal, baselineTreeRows, columns);
	}

	private static async Task SweepAllExtensionsAsync(
		TerminalPtyHarness terminal,
		int baselineTreeRows,
		int columns)
	{
		await terminal.SendAsync("T", TestContext.Current.CancellationToken);
		await terminal.SendHomeAsync(TestContext.Current.CancellationToken);
		await ToggleAndWaitAsync(
			terminal,
			screen => ExtractPanel(screen, "File types", null)
				.Contains("[ ] All", StringComparison.Ordinal));
		var emptyTreeRows = await InspectTreeAsync(terminal, columns, expectEmpty: true);
		Assert.Equal(1, emptyTreeRows);

		await ToggleAndWaitAsync(
			terminal,
			screen => ExtractPanel(screen, "File types", null)
				.Contains("[x] All", StringComparison.Ordinal));
		await AssertTreeRestoredAsync(terminal, baselineTreeRows, columns);
	}

	private static async Task SweepIndividualExtensionsAsync(
		TerminalPtyHarness terminal,
		int baselineTreeRows,
		int columns)
	{
		await terminal.SendAsync("T", TestContext.Current.CancellationToken);
		await terminal.SendHomeAsync(TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		var labels = new[] { ".cs", ".json", ".md" };
		for (var index = 0; index < labels.Length; index++)
		{
			await ToggleCurrentRowAndRestoreAsync(
				terminal,
				labels[index],
				initiallySelected: true,
				baselineTreeRows,
				columns);
			if (index + 1 < labels.Length)
				await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		}
	}

	private static async Task ToggleCurrentRowAndRestoreAsync(
		TerminalPtyHarness terminal,
		string label,
		bool initiallySelected,
		int baselineTreeRows,
		int columns)
	{
		var changedMarker = initiallySelected ? "[ ]" : "[x]";
		var restoredMarker = initiallySelected ? "[x]" : "[ ]";
		await ToggleAndWaitAsync(
			terminal,
			screen => screen.Contains($"{changedMarker} {label}", StringComparison.Ordinal));
		await ToggleAndWaitAsync(
			terminal,
			screen => screen.Contains($"{restoredMarker} {label}", StringComparison.Ordinal));
		await AssertTreeRestoredAsync(terminal, baselineTreeRows, columns);
	}

	private static async Task ToggleAndWaitAsync(
		TerminalPtyHarness terminal,
		Func<string, bool> completed)
	{
		Assert.Contains("> PARAMETERS", terminal.CaptureScreen(), StringComparison.Ordinal);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var stopwatch = Stopwatch.StartNew();
		var stable = Stopwatch.StartNew();
		var previous = string.Empty;
		while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
		{
			var screen = terminal.CaptureScreen();
			Assert.False(terminal.HasExited);
			Assert.DoesNotContain("Processing request", screen, StringComparison.Ordinal);
			Assert.DoesNotContain("Elapsed:", screen, StringComparison.Ordinal);
			Assert.DoesNotContain("Esc or Ctrl+C cancels this operation", screen, StringComparison.Ordinal);
			AssertPanelHasNoDrawingArtifacts(screen, terminal.Columns);

			if (completed(screen) && !HasBackgroundRefresh(screen))
			{
				if (!string.Equals(previous, screen, StringComparison.Ordinal))
					stable.Restart();
				if (stable.Elapsed >= TimeSpan.FromMilliseconds(250))
				{
					AssertStatusIsCoherent(screen);
					return;
				}
			}
			else
			{
				stable.Restart();
			}
			previous = screen;
			await Task.Delay(25, TestContext.Current.CancellationToken);
		}

		throw new TimeoutException($"The setting operation did not settle.\n{terminal.CaptureScreen()}");
	}

	private static bool HasBackgroundRefresh(string screen) =>
		screen.Contains("Updating options…", StringComparison.Ordinal) ||
		screen.Contains("Building tree…", StringComparison.Ordinal) ||
		screen.Contains("Building preview…", StringComparison.Ordinal);

	private static async Task AssertTreeRestoredAsync(
		TerminalPtyHarness terminal,
		int expectedRows,
		int columns)
	{
		var rows = await InspectTreeAsync(terminal, columns, expectEmpty: false);
		Assert.Equal(expectedRows, rows);
	}

	private static async Task<int> InspectTreeAsync(
		TerminalPtyHarness terminal,
		int columns,
		bool expectEmpty)
	{
		var usesPersistentWideTree = columns >= 150;
		if (!usesPersistentWideTree)
		{
			await terminal.SendTabAsync(TestContext.Current.CancellationToken);
			await terminal.WaitForScreenAsync(
				"> PROJECT TREE",
				cancellationToken: TestContext.Current.CancellationToken);
		}
		await Task.Delay(100, TestContext.Current.CancellationToken);
		var tree = terminal.CaptureScreen();
		AssertPanelHasNoDrawingArtifacts(tree, columns);
		AssertStatusIsCoherent(tree);
		var treeRows = CountTreeRows(tree);
		Assert.True(treeRows >= 1);
		if (expectEmpty)
		{
			Assert.Contains(
				"No visible items",
				tree,
				StringComparison.Ordinal);
			Assert.Contains("Files 0", tree, StringComparison.Ordinal);
			Assert.Contains("Folders 0", tree, StringComparison.Ordinal);
		}
		else
		{
			Assert.DoesNotContain(
				"No visible items",
				tree,
				StringComparison.Ordinal);
		}

		if (!usesPersistentWideTree)
		{
			await terminal.SendTabAsync(TestContext.Current.CancellationToken);
			await terminal.SendTabAsync(TestContext.Current.CancellationToken);
			await terminal.WaitForScreenAsync(
				"> PARAMETERS",
				cancellationToken: TestContext.Current.CancellationToken);
		}
		return treeRows;
	}

	private static int CountTreeRows(string screen)
	{
		var panel = ExtractFirstPanel(screen);
		return panel.Split('\n').Count(static line => TreeRowPattern().IsMatch(line));
	}

	private static void AssertStatusIsCoherent(string screen)
	{
		var match = StatusPattern().Match(screen);
		Assert.True(match.Success, $"Status metrics were not rendered.\n{screen}");
		Assert.True(int.Parse(match.Groups["files"].Value) >= 0);
		Assert.True(int.Parse(match.Groups["folders"].Value) >= 0);
	}

	private static void AssertPanelHasNoDrawingArtifacts(string screen, int columns)
	{
		Assert.DoesNotContain('\uFFFD', screen);
		Assert.All(
			screen.Split('\n'),
			line => Assert.True(
				line.GetColumns() <= columns,
				$"Rendered line exceeds {columns} columns: {line}"));
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

	private static string ExtractFirstPanel(string screen) =>
		string.Join(
			'\n',
			screen.Split('\n').Select(line =>
			{
				var separator = line.IndexOf("││", StringComparison.Ordinal);
				if (separator < 0)
					separator = line.IndexOf("┐┌", StringComparison.Ordinal);
				return separator < 0 ? line : line[..(separator + 1)];
			}));

	private static async Task FocusControlsAsync(TerminalPtyHarness terminal)
	{
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> PARAMETERS",
			cancellationToken: TestContext.Current.CancellationToken);
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

	private static TemporaryDirectory CreateSweepProject()
	{
		var project = new TemporaryDirectory();
		project.WriteFile("global.json", "{}");
		project.WriteFile("src/App.cs", "internal sealed class App { }");
		project.WriteFile("docs/readme.md", "# Project");
		project.WriteFile("config/settings.json", "{}");
		project.WriteFile("src/ignored.generated", "generated");
		project.WriteFile(".gitignore", "*.generated\n");
		project.WriteFile(".hidden.cs", "internal sealed class Hidden { }");
		project.WriteFile("src/no-extension", "content");
		RunGit(project.Path, "init", "--quiet");
		RunGit(project.Path, "add", "--all");
		return project;
	}

	private static void RunGit(string workingDirectory, params string[] arguments)
	{
		var startInfo = new ProcessStartInfo
		{
			FileName = "git",
			WorkingDirectory = workingDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		var result = TerminalTestProcess.Run(startInfo);
		Assert.True(
			result.ExitCode == 0,
			$"git {string.Join(' ', arguments)} failed: {result.StandardOutput}{result.StandardError}");
	}

	[GeneratedRegex(@"\[[x \-]\]\s+\S")]
	private static partial Regex TreeRowPattern();

	[GeneratedRegex(@"Files\s+(?<files>\d+).*Folders\s+(?<folders>\d+)", RegexOptions.Singleline)]
	private static partial Regex StatusPattern();
}
