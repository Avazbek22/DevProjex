namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class TerminalPtyRecoveryTests
{
	[Fact(Timeout = 90_000)]
	public async Task AcceptingDefaultContextDestinationFromTuiDotCreatesOnlyAnExternalArtifact()
	{
		using var workspace = new TemporaryDirectory();
		var source = workspace.CreateDirectory("Project");
		File.WriteAllText(
			Path.Combine(source, "global.json"),
			"{}",
			new UTF8Encoding(false));
		var sourceFile = Path.Combine(source, "App.cs");
		File.WriteAllText(
			sourceFile,
			"internal sealed class App {}",
			new UTF8Encoding(false));
		var before = await File.ReadAllBytesAsync(
			sourceFile,
			TestContext.Current.CancellationToken);
		var destination = Path.Combine(workspace.Path, "Project-context.txt");
		await using var terminal = await StartProjectAsync(
			source,
			[],
			TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("E", TestContext.Current.CancellationToken);
		var prompt = await terminal.WaitForScreenAsync(
			Path.GetFileName(destination),
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("Destination:", prompt, StringComparison.Ordinal);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Destination state: Ready",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Equivalent command:",
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(File.Exists(destination));
		Assert.Equal(
			before,
			await File.ReadAllBytesAsync(
				sourceFile,
				TestContext.Current.CancellationToken));
		Assert.DoesNotContain(
			Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories),
			path => Path.GetFileName(path).Equals(
				"Project-context.txt",
				StringComparison.OrdinalIgnoreCase));
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Equivalent command:",
			cancellationToken: TestContext.Current.CancellationToken);
		await ExitAsync(terminal);
	}

	[Fact(Timeout = 60_000)]
	public async Task InvalidPortableProfileShowsSpecificErrorAndReturnsToWelcome()
	{
		using var project = CreateProject();
		var profile = project.WriteFile("broken-profile.json", "{");
		await using var terminal = await StartProjectAsync(
			project.Path,
			["--profile", profile],
			TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			"DPX-CLI-PROFILE-INVALID",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains(
			"portable profile is invalid",
			terminal.CaptureScreen(),
			StringComparison.OrdinalIgnoreCase);
		Assert.False(terminal.HasExited);

		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"DPX-CLI-PROFILE-INVALID",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Choose a workspace action",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);
		await ExitAsync(terminal);
	}

	[Fact(Timeout = 60_000)]
	public async Task UnavailableTrackedModeShowsErrorAndPreservesLastUsablePlan()
	{
		using var project = CreateProject();
		await using var terminal = await StartProjectAsync(
			project.Path,
			[],
			TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync("M", TestContext.Current.CancellationToken);
		var initialMode = await terminal.WaitForScreenAsync(
			"Tracked Git files only",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("(*) Use .gitignore", initialMode, StringComparison.Ordinal);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"DPX-GIT-TRACKED-INDEX-UNAVAILABLE",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);

		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"DPX-GIT-TRACKED-INDEX-UNAVAILABLE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("M", TestContext.Current.CancellationToken);
		var recoveredMode = await terminal.WaitForScreenAsync(
			"Tracked Git files only",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("(*) Use .gitignore", recoveredMode, StringComparison.Ordinal);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Tracked Git files only",
			cancellationToken: TestContext.Current.CancellationToken);
		var recovered = await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("Files 4", recovered, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task DestinationConflictReturnsToWorkspaceAndProjectFolderExportCompletes()
	{
		using var project = CreateProject();
		using var output = new TemporaryDirectory();
		var conflict = output.WriteFile("context.md", "existing");
		var folderDestination = Path.Combine(output.Path, "project-export");
		await using var terminal = await StartProjectAsync(
			project.Path,
			[],
			TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("E", TestContext.Current.CancellationToken);
		await ReplacePromptTextAsync(terminal, conflict);
		await terminal.WaitForScreenAsync(
			"DPX-EXPORT-DESTINATION-EXISTS",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"DPX-EXPORT-DESTINATION-EXISTS",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync("Z", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Choose the physical output kind",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await ReplacePromptTextAsync(terminal, folderDestination, "Exact destination:");
		await terminal.WaitForScreenAsync(
			"Destination state: Ready",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var completed = await terminal.WaitForScreenAsync(
			"Equivalent command:",
			cancellationToken: TestContext.Current.CancellationToken);
		if (!completed.Contains("project-export", StringComparison.Ordinal))
		{
			completed = await terminal.WaitForScreenAsync(
				"project-export",
				cancellationToken: TestContext.Current.CancellationToken);
		}
		Assert.Contains("Equivalent command:", completed, StringComparison.Ordinal);

		Assert.True(File.Exists(Path.Combine(folderDestination, "src", "App.cs")));
		Assert.Equal(
			"internal sealed class App {}",
			await File.ReadAllTextAsync(
				Path.Combine(folderDestination, "src", "App.cs"),
				TestContext.Current.CancellationToken));
		Assert.False(terminal.HasExited);
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
	public async Task MouseCanSelectWelcomeActionWithoutTerminatingTheSession()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("notes.txt", "not a project marker");
		await using var terminal = await TerminalPtyHarness.StartAsync(
			workspace.Path,
			["--language", "en"],
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			"│  Help",
			cancellationToken: TestContext.Current.CancellationToken);
		var helpRow = terminal.FindVisibleRow("│  Help");
		Assert.True(helpRow >= 0);
		await terminal.SendMouseClickAsync(
			8,
			helpRow,
			clickCount: 2,
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Prepare controlled project context without leaving the terminal.",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Prepare controlled project context without leaving the terminal.",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Choose a workspace action",
			cancellationToken: TestContext.Current.CancellationToken);
		await ExitAsync(terminal);
	}

	private static Task<TerminalPtyHarness> StartProjectAsync(
		string projectPath,
		IReadOnlyList<string> additionalArguments,
		CancellationToken cancellationToken) =>
		TerminalPtyHarness.StartAsync(
			projectPath,
			[
				"tui",
				projectPath,
				.. additionalArguments,
				"--screen",
				"inline",
				"--no-mouse",
				"--language",
				"en"
			],
			cancellationToken: cancellationToken);

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

	private static async Task ExitAsync(TerminalPtyHarness terminal)
	{
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
		project.WriteFile("src/App.cs", "internal sealed class App {}");
		project.WriteFile("src/Feature/Handler.cs", "internal sealed class Handler {}");
		project.WriteFile("README.md", "# Test project");
		return project;
	}
}
