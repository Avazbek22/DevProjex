using System.Diagnostics;

namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class TerminalTreeMousePtyTests
{
	[Fact(Timeout = 90_000)]
	public async Task MouseHitZonesKeepSelectionAndCheckboxStateIndependent()
	{
		using var project = CreateProject();
		var projectName = Path.GetFileName(project.Path);
		await using var terminal = await TerminalPtyHarness.StartAsync(
			project.Path,
			[
				"tui",
				project.Path,
				"--profile",
				"standard",
				"--screen",
				"inline",
				"--language",
				"en"
			],
			columns: 120,
			rows: 30,
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			$"[x] {projectName}",
			cancellationToken: TestContext.Current.CancellationToken);
		var rootRow = FindVisibleTreeRow(terminal.CaptureScreen(), $"[x] {projectName}");
		Assert.True(rootRow >= 0);

		// Clicking a name only changes focus/selection.
		await terminal.SendMouseClickAsync(
			column: 12,
			row: rootRow,
			cancellationToken: TestContext.Current.CancellationToken);
		var selectedOnly = await terminal.WaitForScreenAsync(
			$"[x] {projectName}",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("src", selectedOnly, StringComparison.Ordinal);

		// The checkbox has its own hit zone and does not collapse the node.
		await terminal.SendMouseClickAsync(
			column: 4,
			row: rootRow,
			cancellationToken: TestContext.Current.CancellationToken);
		var uncheckedRoot = await terminal.WaitForScreenAsync(
			$"[ ] {projectName}",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("src", uncheckedRoot, StringComparison.Ordinal);
		await terminal.WaitForScreenAsync(
			"Files 0",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendMouseClickAsync(
			column: 4,
			row: rootRow,
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			$"[x] {projectName}",
			cancellationToken: TestContext.Current.CancellationToken);

		// The disclosure glyph changes expansion without touching selection.
		await terminal.SendMouseClickAsync(
			column: 1,
			row: rootRow,
			cancellationToken: TestContext.Current.CancellationToken);
		var collapsed = await terminal.WaitForStableScreenAsync(
			$"[x] {projectName}",
			forbidden: "│  > [x] src",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains($"[x] {projectName}", collapsed, StringComparison.Ordinal);
		await terminal.SendMouseClickAsync(
			column: 12,
			row: rootRow,
			clickCount: 2,
			cancellationToken: TestContext.Current.CancellationToken);
		var expanded = await terminal.WaitForScreenAsync(
			"│  > [x] src",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains($"[x] {projectName}", expanded, StringComparison.Ordinal);

		var sourceRow = FindVisibleTreeRow(terminal.CaptureScreen(), "[x] src");
		Assert.True(sourceRow >= 0);
		await terminal.SendMouseClickAsync(
			column: 3,
			row: sourceRow,
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"File001.cs",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendPageDownAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"File025.cs",
			cancellationToken: TestContext.Current.CancellationToken);
		var fileRow = await WaitForVisibleTreeRowAsync(
			terminal,
			"File025.cs",
			TestContext.Current.CancellationToken);
		Assert.True(fileRow >= 0);
		await terminal.SendMouseClickAsync(
			column: 8,
			row: fileRow,
			cancellationToken: TestContext.Current.CancellationToken);
		var toggledFile = await terminal.WaitForScreenAsync(
			"[ ] File025.cs",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Files 40",
			cancellationToken: TestContext.Current.CancellationToken);
		await Task.Delay(400, TestContext.Current.CancellationToken);
		toggledFile = terminal.CaptureScreen();
		Assert.Contains("File025.cs", toggledFile, StringComparison.Ordinal);
		Assert.NotEqual(-1, FindVisibleTreeRow(toggledFile, "File025.cs"));
		Assert.Equal(-1, FindVisibleTreeRow(toggledFile, "File001.cs"));
		Assert.False(terminal.HasExited);

		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	[Fact(Timeout = 90_000)]
	public async Task MouseCanChangeGitModeAndExclusionsInParameters()
	{
		using var project = CreateGitProject();
		await using var terminal = await TerminalPtyHarness.StartAsync(
			project.Path,
			[
				"tui",
				project.Path,
				"--profile",
				"standard",
				"--screen",
				"inline",
				"--language",
				"en"
			],
			columns: 160,
			rows: 40,
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			"[x] Smart ignore",
			cancellationToken: TestContext.Current.CancellationToken);
		var initial = await WaitForStableScreenAsync(
			terminal,
			TestContext.Current.CancellationToken);
		var (contentColumn, contentRow) = FindVisibleCell(initial, "[ ] Hide secrets", 1);
		Assert.True(contentColumn >= 0 && contentRow >= 0);
		await terminal.SendMouseClickAsync(
			contentColumn,
			contentRow,
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"[x] Hide secrets",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendMouseClickAsync(
			contentColumn,
			contentRow,
			cancellationToken: TestContext.Current.CancellationToken);
		initial = await terminal.WaitForScreenAsync(
			"[ ] Hide secrets",
			cancellationToken: TestContext.Current.CancellationToken);
		var (smartColumn, smartRow) = FindVisibleCell(initial, "[x] Smart ignore", 1);
		Assert.True(smartColumn >= 0 && smartRow >= 0);
		await terminal.SendMouseClickAsync(
			smartColumn,
			smartRow,
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"[ ] Smart ignore",
			cancellationToken: TestContext.Current.CancellationToken);
		var exclusionChanged = await WaitForStableScreenAsync(
			terminal,
			TestContext.Current.CancellationToken);
		Assert.True(
			exclusionChanged.Contains("> PARAMETERS", StringComparison.Ordinal),
			exclusionChanged);

		var (gitColumn, gitRow) = FindVisibleCell(
			exclusionChanged,
			"[x] Use .gitignore",
			1);
		Assert.True(gitColumn >= 0 && gitRow >= 0);
		await terminal.SendMouseClickAsync(
			gitColumn,
			gitRow,
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"[ ] Use .gitignore",
			cancellationToken: TestContext.Current.CancellationToken);
		var gitDisabled = await WaitForStableScreenAsync(
			terminal,
			TestContext.Current.CancellationToken);
		Assert.Contains("[ ] Tracked Git files only", gitDisabled, StringComparison.Ordinal);
		Assert.Contains("[ ] Smart ignore", gitDisabled, StringComparison.Ordinal);

		var (trackedColumn, trackedRow) = FindVisibleCell(
			gitDisabled,
			"[ ] Tracked Git files only",
			1);
		Assert.True(trackedColumn >= 0 && trackedRow >= 0);
		await terminal.SendMouseClickAsync(
			trackedColumn,
			trackedRow,
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"[x] Tracked Git files only",
			cancellationToken: TestContext.Current.CancellationToken);
		var trackedEnabled = await WaitForStableScreenAsync(
			terminal,
			TestContext.Current.CancellationToken);
		Assert.Contains("[ ] Use .gitignore", trackedEnabled, StringComparison.Ordinal);
		await terminal.SendMouseClickAsync(
			trackedColumn,
			trackedRow,
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"[ ] Tracked Git files only",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendMouseClickAsync(
			gitColumn,
			gitRow,
			cancellationToken: TestContext.Current.CancellationToken);
		var gitChanged = await terminal.WaitForScreenAsync(
			"[x] Use .gitignore",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("[ ] Tracked Git files only", gitChanged, StringComparison.Ordinal);
		Assert.Contains("> PARAMETERS", gitChanged, StringComparison.Ordinal);

		var (exclusionAllColumn, exclusionAllRow) = FindVisibleCell(gitChanged, "[ ] All", 1);
		Assert.True(exclusionAllColumn >= 0 && exclusionAllRow >= 0);
		await terminal.SendMouseClickAsync(
			exclusionAllColumn,
			exclusionAllRow,
			cancellationToken: TestContext.Current.CancellationToken);
		var allExclusionsEnabled = await terminal.WaitForScreenAsync(
			"[x] Smart ignore",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("[x] All", allExclusionsEnabled, StringComparison.Ordinal);
		await terminal.SendMouseClickAsync(
			exclusionAllColumn,
			exclusionAllRow,
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"[ ] Use .gitignore",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendMouseClickAsync(
			exclusionAllColumn,
			exclusionAllRow,
			cancellationToken: TestContext.Current.CancellationToken);
		gitChanged = await terminal.WaitForScreenAsync(
			"[x] Smart ignore",
			cancellationToken: TestContext.Current.CancellationToken);

		var (extensionAllColumn, extensionAllRow) = FindVisibleCell(
			gitChanged,
			"[x] All",
			1,
			useLastOccurrence: true);
		Assert.True(extensionAllColumn >= 0 && extensionAllRow >= 0);
		await terminal.SendMouseClickAsync(
			extensionAllColumn,
			extensionAllRow,
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Files 0",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendMouseClickAsync(
			extensionAllColumn,
			extensionAllRow,
			cancellationToken: TestContext.Current.CancellationToken);
		gitChanged = await terminal.WaitForScreenAsync(
			"Files 41",
			cancellationToken: TestContext.Current.CancellationToken);

		var (extensionColumn, extensionRow) = FindVisibleCell(
			gitChanged,
			"[x] .cs",
			1);
		Assert.True(extensionColumn >= 0 && extensionRow >= 0);
		await terminal.SendMouseClickAsync(
			extensionColumn,
			extensionRow,
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"[ ] .cs",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Files 1",
			cancellationToken: TestContext.Current.CancellationToken);
		var extensionChanged = await WaitForStableScreenAsync(
			terminal,
			TestContext.Current.CancellationToken);
		Assert.Contains("> PARAMETERS", extensionChanged, StringComparison.Ordinal);
		await terminal.SendMouseClickAsync(
			extensionColumn,
			extensionRow,
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"[x] .cs",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Files 41",
			cancellationToken: TestContext.Current.CancellationToken);
		var extensionRestored = await WaitForStableScreenAsync(
			terminal,
			TestContext.Current.CancellationToken);
		Assert.Contains("Files 41", extensionRestored, StringComparison.Ordinal);
		Assert.DoesNotContain("ROOT FOLDERS", extensionRestored, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);

		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	[Fact(Timeout = 90_000)]
	public async Task MouseWheelScrollsOnlyTheOverflowingMiniListAndKeepsAllPinned()
	{
		using var project = CreateGitProject();
		await using var terminal = await TerminalPtyHarness.StartAsync(
			project.Path,
			[
				"tui",
				project.Path,
				"--profile",
				"standard",
				"--screen",
				"inline",
				"--language",
				"en"
			],
			columns: 70,
			rows: 24,
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("X", TestContext.Current.CancellationToken);
		var initial = await terminal.WaitForScreenAsync(
			"[x] Use .gitignore",
			cancellationToken: TestContext.Current.CancellationToken);
		var (column, row) = FindVisibleCell(initial, "[x] Use .gitignore", 8);
		Assert.True(column >= 0 && row >= 0);

		for (var step = 0; step < 12; step++)
		{
			await terminal.SendMouseWheelDownAsync(
				column,
				row,
				TestContext.Current.CancellationToken);
		}
		var scrolled = await terminal.WaitForScreenAsync(
			"Files without extension",
			timeout: TimeSpan.FromSeconds(10),
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("[x] All", ExtractPanel(scrolled, "Exclusions", "File types"), StringComparison.Ordinal);

		for (var step = 0; step < 12; step++)
		{
			await terminal.SendMouseWheelUpAsync(
				column,
				row,
				TestContext.Current.CancellationToken);
		}
		var restored = await terminal.WaitForScreenAsync(
			"[x] Use .gitignore",
			timeout: TimeSpan.FromSeconds(10),
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("[x] All", ExtractPanel(restored, "Exclusions", "File types"), StringComparison.Ordinal);
		Assert.False(terminal.HasExited);

		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	private static TemporaryDirectory CreateProject()
	{
		var project = new TemporaryDirectory();
		project.WriteFile("global.json", "{}");
		for (var index = 1; index <= 40; index++)
		{
			project.WriteFile(
				$"src/File{index:D3}.cs",
				$"internal sealed class MouseMarker{index:D3} {{ }}");
		}
		return project;
	}

	private static TemporaryDirectory CreateGitProject()
	{
		var project = CreateProject();
		RunGit(project.Path, "init", "--initial-branch=main");
		RunGit(project.Path, "config", "user.email", "terminal-tests@devprojex.local");
		RunGit(project.Path, "config", "user.name", "DevProjex Terminal Tests");
		RunGit(project.Path, "add", ".");
		RunGit(project.Path, "commit", "-m", "Initial test project");
		return project;
	}

	private static void RunGit(string workingDirectory, params string[] arguments)
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
	}

	private static async Task<int> WaitForVisibleTreeRowAsync(
		TerminalPtyHarness terminal,
		string expected,
		CancellationToken cancellationToken)
	{
		var timeout = Stopwatch.StartNew();
		while (timeout.Elapsed < TimeSpan.FromSeconds(5))
		{
			var row = FindVisibleTreeRow(terminal.CaptureScreen(), expected);
			if (row >= 0)
				return row;
			await Task.Delay(40, cancellationToken);
		}
		return -1;
	}

	private static async Task<string> WaitForStableScreenAsync(
		TerminalPtyHarness terminal,
		CancellationToken cancellationToken)
	{
		var timeout = Stopwatch.StartNew();
		var stable = Stopwatch.StartNew();
		var previous = terminal.CaptureScreen();
		while (timeout.Elapsed < TimeSpan.FromSeconds(10))
		{
			await Task.Delay(75, cancellationToken);
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

		throw new Xunit.Sdk.XunitException(
			$"Terminal screen did not settle.\n{terminal.CaptureScreen()}");
	}

	private static int FindVisibleTreeRow(string screen, string expected)
	{
		var lines = screen.Split('\n');
		for (var row = 0; row < lines.Length; row++)
		{
			var separator = lines[row].IndexOf("││", StringComparison.Ordinal);
			var tree = separator >= 0 ? lines[row][..separator] : lines[row];
			if (tree.Contains(expected, StringComparison.Ordinal))
				return row;
		}
		return -1;
	}

	private static (int Column, int Row) FindVisibleCell(
		string screen,
		string expected,
		int columnOffset,
		bool useLastOccurrence = false)
	{
		var lines = screen.Split('\n');
		var result = (-1, -1);
		for (var row = 0; row < lines.Length; row++)
		{
			var column = useLastOccurrence
				? lines[row].LastIndexOf(expected, StringComparison.Ordinal)
				: lines[row].IndexOf(expected, StringComparison.Ordinal);
			if (column >= 0)
			{
				result = (column + columnOffset, row);
				if (!useLastOccurrence)
					return result;
			}
		}
		return result;
	}

	private static string ExtractPanel(string screen, string title, string nextTitle)
	{
		var lines = screen.Split('\n');
		var start = Array.FindIndex(lines, line => line.Contains(title, StringComparison.Ordinal));
		var end = Array.FindIndex(
			lines,
			start + 1,
			line => line.Contains(nextTitle, StringComparison.Ordinal));
		Assert.True(start >= 0 && end > start, screen);
		return string.Join('\n', lines[start..end]);
	}
}
