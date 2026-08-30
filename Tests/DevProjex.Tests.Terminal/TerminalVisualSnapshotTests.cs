using System.Diagnostics;

namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class TerminalVisualSnapshotTests
{
	private const int SnapshotTemporaryRootLength = 33;
	private const int SnapshotProjectPathLength = 91;
	// Includes JSON quotes and preserves the committed equivalent-command wrap point.
	private const int SnapshotProjectArgumentJsonLength = 100;

	[Fact(Timeout = 60_000)]
	public async Task WelcomeWideSnapshotsCoverEnglishSelectionHelpAndRecentWorkspaces()
	{
		using var workspace = CreateMarkerlessWorkspace();
		await using var terminal = await StartWelcomeAsync(
			workspace.Path,
			"en",
			columns: 120,
			rows: 30);

		await WaitForStableScreenAsync(terminal, "Choose a workspace action");
		Verify("welcome-en-120x30", terminal, workspace.Path);

		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "> Browse folder");
		Verify("welcome-selected-en-120x30", terminal, workspace.Path);

		await terminal.SendAsync("?", TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(
			terminal,
			"Prepare controlled project context without leaving the terminal.");
		Verify("welcome-help-en-120x30", terminal, workspace.Path);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Prepare controlled project context without leaving the terminal.",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendHomeAsync(TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "> Recent workspaces");
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "(none available)");
		Verify("welcome-recent-en-120x30", terminal, workspace.Path);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"(none available)",
			cancellationToken: TestContext.Current.CancellationToken);
		await ExitAsync(terminal);
	}

	[Theory(Timeout = 60_000)]
	[InlineData("ru", 120, 30, "welcome-ru-120x30", "Выберите действие")]
	[InlineData("en", 80, 24, "welcome-en-80x24", "Choose a workspace action")]
	public async Task WelcomeSnapshotsCoverRussianAndCompactLayouts(
		string language,
		int columns,
		int rows,
		string snapshot,
		string readyText)
	{
		using var workspace = CreateMarkerlessWorkspace();
		await using var terminal = await StartWelcomeAsync(
			workspace.Path,
			language,
			columns,
			rows);

		await WaitForStableScreenAsync(terminal, readyText);
		Verify(snapshot, terminal, workspace.Path);
		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task WorkspaceSnapshotsCoverWideCompactAndCoreOverlays()
	{
		using var project = CreateProjectForPhysicalPathSnapshots();
		await using var terminal = await StartProjectAsync(project.Path);

		await WaitForStableScreenAsync(terminal, "PROJECT TREE");
		Verify("workspace-en-120x30", terminal, project.Path);

		await terminal.SendAsync("c", TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "> PARAMETERS");
		Verify("workspace-content-en-120x30", terminal, project.Path);
		await terminal.SendShiftTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync("> CONTEXT PREVIEW", cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync("X", TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "> PARAMETERS");
		Verify("workspace-exclusions-en-120x30", terminal, project.Path);
		await terminal.SendShiftTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync("> CONTEXT PREVIEW", cancellationToken: TestContext.Current.CancellationToken);

		using var output = new FixedTemporaryDirectory("DevProjex-Tui-Snapshot-Context");
		var destination = Path.Combine(output.Path, "context-output.md");
		await terminal.SendAsync("E", TestContext.Current.CancellationToken);
		await ReplacePromptTextAsync(terminal, destination);
		await WaitForStableScreenAsync(terminal, "Export?");
		Verify(
			"workspace-context-export-en-120x30",
			terminal,
			project.Path,
			(output.Path, "<OUTPUT_ROOT>"));
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Export?",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.ResizeAsync(80, 24, TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "> CONTEXT PREVIEW");
		Verify("workspace-en-80x24", terminal, project.Path);

		await terminal.ResizeAsync(120, 30, TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "CONTEXT PREVIEW");
		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task WorkspaceSnapshotsCoverRecoverableErrorAndProjectExportCompletion()
	{
		using var project = CreateProjectForEquivalentCommandSnapshot();
		InitializeGitRepository(project.Path);
		using var output = new FixedTemporaryDirectory("DevProjex-Tui-Snapshot-Export");
		await using var terminal = await StartProjectAsync(project.Path);

		await WaitForStableScreenAsync(terminal, "PROJECT TREE");
		File.WriteAllText(Path.Combine(project.Path, ".git", "index"), "not-a-git-index");
		await terminal.SendAsync(":set git tracked\r", TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "DPX-GIT-TRACKED-INDEX-UNAVAILABLE");
		Verify("workspace-error-en-120x30", terminal, project.Path);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"DPX-GIT-TRACKED-INDEX-UNAVAILABLE",
			cancellationToken: TestContext.Current.CancellationToken);

		var destination = Path.Combine(output.Path, "project-export");
		await terminal.SendAsync("z", TestContext.Current.CancellationToken);
		await ReplacePromptTextAsync(terminal, destination, "Exact destination:");
		await WaitForStableScreenAsync(terminal, "Export?");
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "Export completed:");
		Verify(
			"workspace-project-export-success-en-120x30",
			terminal,
			project.Path,
			(output.Path, "<OUTPUT_ROOT>"));
		Assert.True(File.Exists(Path.Combine(destination, "src", "App.cs")));
		await terminal.WaitForScreenAsync(
			"> PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task WideWorkspaceSnapshotsCoverParametersActionPaletteAndAllFormats()
	{
		using var project = CreateProjectForPhysicalPathSnapshots();
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
			columns: 160,
			rows: 40,
			cancellationToken: TestContext.Current.CancellationToken);

		await WaitForStableScreenAsync(terminal, "PARAMETERS");
		var parameters = terminal.CaptureScreen();
		Assert.Contains("Content processing", parameters, StringComparison.Ordinal);
		Assert.Contains("Exclusions", parameters, StringComparison.Ordinal);
		Assert.Contains("File types", parameters, StringComparison.Ordinal);
		Assert.DoesNotContain("ROOT FOLDERS", parameters, StringComparison.Ordinal);
		Assert.Contains("Hide private data", parameters, StringComparison.Ordinal);
		Assert.Contains("Compress code", parameters, StringComparison.Ordinal);
		Assert.DoesNotContain("Profile: Standard", parameters, StringComparison.Ordinal);
		Assert.DoesNotContain("Readable", parameters, StringComparison.Ordinal);
		Assert.DoesNotContain("Raw output", parameters, StringComparison.Ordinal);
		Verify("workspace-wide-parameters-en-160x40", terminal, project.Path);

		await terminal.SendAsync("\u0010", TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "Filter actions:");
		Verify("workspace-action-palette-en-160x40", terminal, project.Path);
		await terminal.SendAsync(
			"Preview format",
			TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(
			terminal,
			"Choose ASCII, JSON, XML, or Markdown for the tree.");
		Verify("workspace-action-palette-filtered-en-160x40", terminal, project.Path);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Filter actions:",
			cancellationToken: TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(
			terminal,
			"Choose ASCII, JSON, XML, or Markdown for the tree.");
		var formatSelector = terminal.CaptureScreen();
		Assert.Contains("ASCII", formatSelector, StringComparison.Ordinal);
		Assert.Contains("JSON", formatSelector, StringComparison.Ordinal);
		Assert.Contains("XML", formatSelector, StringComparison.Ordinal);
		Assert.Contains(
			formatSelector.Split('\n'),
			static line => line.Contains("│ Markdown", StringComparison.Ordinal));
		Verify("workspace-format-selector-en-160x40", terminal, project.Path);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "CONTEXT PREVIEW · Tree · XML");
		await WaitForStableScreenAsync(terminal, "<d n=");
		Verify("workspace-wide-xml-en-160x40", terminal, project.Path);

		await terminal.SendAsync("\u0010", TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "Filter actions:");
		await terminal.SendAsync("Open the visible project", TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "> PARAMETERS");
		Verify("workspace-controls-focused-en-160x40", terminal, project.Path);

		await terminal.SendAsync("?", TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(
			terminal,
			"Content Processing contains five immediate transformations");
		Verify("workspace-controls-help-en-160x40", terminal, project.Path);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Content Processing contains five immediate transformations",
			cancellationToken: TestContext.Current.CancellationToken);
		await ExitAsync(terminal);
	}

	[Fact(Timeout = 60_000)]
	public async Task RussianWideWorkspaceLocalizesParametersAndCompleteFormatSelector()
	{
		using var project = CreateProjectForPhysicalPathSnapshots();
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
				"ru"
			],
			columns: 160,
			rows: 40,
			cancellationToken: TestContext.Current.CancellationToken);

		await WaitForStableScreenAsync(terminal, "ПАРАМЕТРЫ");
		var workspace = terminal.CaptureScreen();
		Assert.Contains("Обработка содержи…", workspace, StringComparison.Ordinal);
		Assert.Contains("Исключения", workspace, StringComparison.Ordinal);
		Assert.Contains("Типы файлов", workspace, StringComparison.Ordinal);
		Assert.DoesNotContain("КОРНЕВЫЕ ПАПКИ", workspace, StringComparison.Ordinal);
		Assert.Contains("Использовать .gitignore", workspace, StringComparison.Ordinal);
		Assert.Contains("Скрытые папки", workspace, StringComparison.Ordinal);
		Assert.DoesNotContain("[[", workspace, StringComparison.Ordinal);
		Assert.DoesNotContain("smart-ignore", workspace, StringComparison.Ordinal);
		Assert.DoesNotContain("hidden-folders", workspace, StringComparison.Ordinal);
		Assert.DoesNotContain("extensionless-files", workspace, StringComparison.Ordinal);
		Verify("workspace-wide-parameters-ru-160x40", terminal, project.Path);

		await terminal.SendAsync("F", TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(
			terminal,
			"Выберите ASCII, JSON, XML или Markdown для дерева.");
		var selector = terminal.CaptureScreen();
		Assert.Contains("ASCII", selector, StringComparison.Ordinal);
		Assert.Contains("JSON", selector, StringComparison.Ordinal);
		Assert.Contains("XML", selector, StringComparison.Ordinal);
		Assert.Contains("Markdown", selector, StringComparison.Ordinal);
		Assert.DoesNotContain("[[", selector, StringComparison.Ordinal);
		Verify("workspace-format-selector-ru-160x40", terminal, project.Path);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Выберите ASCII, JSON, XML или Markdown для дерева.",
			cancellationToken: TestContext.Current.CancellationToken);
		await ExitAsync(terminal);
	}

	[Fact(Timeout = 60_000)]
	public async Task MonochromeWelcomeSnapshotPreservesVisibleFocus()
	{
		using var workspace = CreateMarkerlessWorkspace();
		await using var terminal = await StartWelcomeAsync(
			workspace.Path,
			"en",
			columns: 120,
			rows: 30,
			environment: new Dictionary<string, string> { ["NO_COLOR"] = "1" });

		await WaitForStableScreenAsync(terminal, "Choose a workspace action");
		Verify("welcome-monochrome-en-120x30", terminal, workspace.Path);
		var selectedRow = terminal.FindVisibleRow("> Open current directory");
		Assert.True(selectedRow >= 0);
		var selectedColumn = terminal.CaptureScreen()
			.Split('\n')[selectedRow]
			.IndexOf("Open current directory", StringComparison.Ordinal);
		Assert.True(terminal.CaptureCellStyle(selectedRow, selectedColumn).Inverse);
		await ExitAsync(terminal);
	}

	private static Task<TerminalPtyHarness> StartWelcomeAsync(
		string workingDirectory,
		string language,
		int columns,
		int rows,
		IReadOnlyDictionary<string, string>? environment = null) =>
		TerminalPtyHarness.StartAsync(
			workingDirectory,
			["--language", language],
			columns,
			rows,
			environment,
			TestContext.Current.CancellationToken);

	private static Task<TerminalPtyHarness> StartProjectAsync(string projectPath) =>
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
			cancellationToken: TestContext.Current.CancellationToken);

	private static async Task ReplacePromptTextAsync(
		TerminalPtyHarness terminal,
		string value,
		string prompt = "Destination:")
	{
		await terminal.WaitForScreenAsync(
			prompt,
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendCtrlAAsync(TestContext.Current.CancellationToken);
		await terminal.SendAsync(value, TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
	}

	private static async Task WaitForStableScreenAsync(
		TerminalPtyHarness terminal,
		string expected)
	{
		var previous = string.Empty;
		var stableSamples = 0;
		var timeout = Stopwatch.StartNew();
		while (timeout.Elapsed < TimeSpan.FromSeconds(10))
		{
			var current = terminal.CaptureScreen();
			if (current.Contains(expected, StringComparison.Ordinal) &&
				string.Equals(previous, current, StringComparison.Ordinal))
			{
				stableSamples++;
				if (stableSamples >= 3)
					return;
			}
			else
			{
				stableSamples = 0;
			}

			previous = current;
			await Task.Delay(80, TestContext.Current.CancellationToken);
		}

		throw new TimeoutException(
			$"Screen did not stabilize for '{expected}'.\n{terminal.CaptureScreen()}");
	}

	private static void Verify(
		string name,
		TerminalPtyHarness terminal,
		string projectPath,
		params (string Value, string Replacement)[] replacements)
	{
		var normalizedValues = new[]
			{
				(projectPath, "<PROJECT_ROOT>"),
				(Path.GetDirectoryName(projectPath) ?? string.Empty, "<TEMP_ROOT>"),
				(Path.GetFileName(projectPath), "<PROJECT>")
			}
			.Concat(replacements)
			.ToArray();
		TerminalScreenSnapshot.Verify(
			name,
			terminal.CaptureScreen(),
			normalizedValues);
		TerminalVisualArtifactWriter.WriteIfRequested(name, terminal);
	}

	private static async Task ExitAsync(TerminalPtyHarness terminal)
	{
		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				timeout: TimeSpan.FromSeconds(30),
				cancellationToken: TestContext.Current.CancellationToken));
	}

	private static OwnedProject CreateMarkerlessWorkspace()
	{
		var workspace = new FixedLengthSnapshotDirectory(
			SnapshotProjectPathLength,
			Guid.NewGuid().ToString("N"));
		WriteProjectFile(workspace.Path, "notes.txt", "not a project marker");
		return new OwnedProject(workspace, workspace.Path);
	}

	[Fact]
	public void PhysicalPathSnapshotFixtureKeepsTreeMetricInputLengthStable()
	{
		using var project = CreateProjectForPhysicalPathSnapshots();

		Assert.Equal(SnapshotProjectPathLength, project.Path.Length);
	}

	private static OwnedProject CreateProjectForPhysicalPathSnapshots() =>
		// Tree export metrics include the source root length, so this fixture must
		// stabilize the physical path rather than its platform-specific JSON form.
		CreateProject(new FixedLengthSnapshotDirectory(
			SnapshotProjectPathLength,
			Guid.NewGuid().ToString("N")));

	private static OwnedProject CreateProjectForEquivalentCommandSnapshot() =>
		// Equivalent-command wrapping observes JSON-escaped argv cell width.
		CreateProject(FixedLengthSnapshotDirectory.CreateForArgumentJsonLength(
			SnapshotProjectArgumentJsonLength,
			Guid.NewGuid().ToString("N")));

	private static OwnedProject CreateProject(FixedLengthSnapshotDirectory owner)
	{
		var projectPath = owner.Path;

		WriteProjectFile(projectPath, "global.json", "{}");
		WriteProjectFile(projectPath, "src/App.cs", "internal sealed class App {}");
		WriteProjectFile(
			projectPath,
			"src/Feature/Handler.cs",
			"internal sealed class Handler {}");
		WriteProjectFile(projectPath, "readme.md", "# Test project");
		return new OwnedProject(owner, projectPath);
	}

	private static void InitializeGitRepository(string projectPath)
	{
		RunGit(projectPath, "init", "--quiet");
		RunGit(projectPath, "config", "user.email", "terminal-tests@devprojex.local");
		RunGit(projectPath, "config", "user.name", "DevProjex Terminal Tests");
		RunGit(projectPath, "add", "--all");
		RunGit(projectPath, "commit", "--quiet", "-m", "Initial test project");
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
		var result = TerminalTestProcess.Run(startInfo);
		Assert.Equal(0, result.ExitCode);
	}

	private sealed class FixedTemporaryDirectory : IDisposable
	{
		private readonly FixedLengthSnapshotDirectory _owner;

		public FixedTemporaryDirectory(string name)
		{
			_owner = new FixedLengthSnapshotDirectory(
				SnapshotTemporaryRootLength + 1 + name.Length);
			Path = _owner.Path;
		}

		public string Path { get; }

		public void Dispose() => _owner.Dispose();
	}

	private sealed class OwnedProject(
		IDisposable owner,
		string path) : IDisposable
	{
		public string Path { get; } = path;

		public void Dispose() => owner.Dispose();
	}

	private static void WriteProjectFile(
		string projectPath,
		string relativePath,
		string content)
	{
		var path = System.IO.Path.Combine(projectPath, relativePath);
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content, new UTF8Encoding(false));
	}
}
