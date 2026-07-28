namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class TerminalVisualSnapshotTests
{
	[Fact(Timeout = 60_000)]
	public async Task WelcomeWideSnapshotsCoverEnglishSelectionHelpAndRecentProjects()
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
		await WaitForStableScreenAsync(terminal, "> Browse folder");
		Verify("welcome-selected-en-120x30", terminal, workspace.Path);

		await terminal.SendAsync("?", TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "Only Exit or q");
		Verify("welcome-help-en-120x30", terminal, workspace.Path);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Only Exit or q",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendHomeAsync(TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "> Recent projects");
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
		using var project = CreateProject();
		await using var terminal = await StartProjectAsync(project.Path);

		await WaitForStableScreenAsync(terminal, "PROJECT TREE");
		Verify("workspace-en-120x30", terminal, project.Path);

		await terminal.SendAsync("M", TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "Choose exactly one mode");
		Verify("workspace-git-mode-en-120x30", terminal, project.Path);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Choose exactly one mode",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync("X", TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "Toggle all changes only this section");
		Verify("workspace-exclusions-en-120x30", terminal, project.Path);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Toggle all changes only this section",
			cancellationToken: TestContext.Current.CancellationToken);

		using var output = new FixedTemporaryDirectory("DevProjex-Tui-Snapshot-Context");
		var destination = Path.Combine(output.Path, "context-output.md");
		await terminal.SendAsync("E", TestContext.Current.CancellationToken);
		await ReplacePromptTextAsync(terminal, destination);
		await WaitForStableScreenAsync(terminal, "Destination state: Ready");
		Verify(
			"workspace-context-export-en-120x30",
			terminal,
			project.Path,
			(output.Path, "<OUTPUT_ROOT>"));
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Destination state: Ready",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.ResizeAsync(80, 24, TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "PROJECT TREE");
		Verify("workspace-en-80x24", terminal, project.Path);

		await terminal.ResizeAsync(120, 30, TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "CONTEXT PREVIEW");
		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task WorkspaceSnapshotsCoverRecoverableErrorAndProjectExportCompletion()
	{
		using var project = CreateProject();
		using var output = new FixedTemporaryDirectory("DevProjex-Tui-Snapshot-Export");
		await using var terminal = await StartProjectAsync(project.Path);

		await WaitForStableScreenAsync(terminal, "PROJECT TREE");
		await terminal.SendAsync("M", TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "Tracked Git files only");
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "DPX-GIT-TRACKED-INDEX-UNAVAILABLE");
		Verify("workspace-error-en-120x30", terminal, project.Path);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"DPX-GIT-TRACKED-INDEX-UNAVAILABLE",
			cancellationToken: TestContext.Current.CancellationToken);

		var destination = Path.Combine(output.Path, "project-export");
		await terminal.SendAsync("Z", TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "Choose the physical output kind");
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await ReplacePromptTextAsync(terminal, destination, "Exact destination:");
		await WaitForStableScreenAsync(terminal, "Destination state: Ready");
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "Equivalent command:");
		Verify(
			"workspace-project-export-success-en-120x30",
			terminal,
			project.Path,
			(output.Path, "<OUTPUT_ROOT>"));
		Assert.True(File.Exists(Path.Combine(destination, "src", "App.cs")));
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Equivalent command:",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
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
		var selectedRow = terminal.FindVisibleRow("> Recent projects");
		Assert.True(selectedRow >= 0);
		var selectedColumn = terminal.CaptureScreen()
			.Split('\n')[selectedRow]
			.IndexOf("Recent projects", StringComparison.Ordinal);
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
		await terminal.WaitForScreenAsync(
			expected,
			cancellationToken: TestContext.Current.CancellationToken);
		await Task.Delay(150, TestContext.Current.CancellationToken);
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
		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	private static TemporaryDirectory CreateMarkerlessWorkspace()
	{
		var workspace = new TemporaryDirectory();
		workspace.WriteFile("notes.txt", "not a project marker");
		return workspace;
	}

	private static TemporaryDirectory CreateProject()
	{
		var project = new TemporaryDirectory();
		project.WriteFile("global.json", "{}");
		project.WriteFile("src/App.cs", "internal sealed class App {}");
		project.WriteFile("src/Feature/Handler.cs", "internal sealed class Handler {}");
		project.WriteFile("README.md", "# Test project");
		return project;
	}

	private sealed class FixedTemporaryDirectory : IDisposable
	{
		public FixedTemporaryDirectory(string name)
		{
			Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name);
			Delete();
			Directory.CreateDirectory(Path);
		}

		public string Path { get; }

		public void Dispose() => Delete();

		private void Delete()
		{
			if (Directory.Exists(Path))
				Directory.Delete(Path, recursive: true);
		}
	}
}
