namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class TerminalPickerPtyTests
{
	private const int SnapshotProjectPathLength = 91;
	private const int ClippedWelcomePathLength = 6;

	[Fact(Timeout = 60_000)]
	public async Task RussianFolderPickerUsesDevProjexLocalizationAndReturnsToWelcome()
	{
		using var workspace = new FixedLengthSnapshotDirectory(
			SnapshotProjectPathLength,
			Guid.NewGuid().ToString("N"));
		File.WriteAllText(
			Path.Combine(workspace.Path, "notes.txt"),
			"not a project marker",
			new UTF8Encoding(false));
		Directory.CreateDirectory(Path.Combine(workspace.Path, "Проект с пробелами"));
		await using var terminal = await TerminalPtyHarness.StartAsync(
			workspace.Path,
			["--language", "ru"],
			columns: 120,
			rows: 30,
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			"Выбрать папку",
			cancellationToken: TestContext.Current.CancellationToken);
		var browseRow = terminal.FindVisibleRow("Выбрать папку");
		Assert.True(browseRow >= 0);
		await terminal.SendMouseClickAsync(
			column: 12,
			row: browseRow,
			clickCount: 2,
			cancellationToken: TestContext.Current.CancellationToken);
		var picker = await terminal.WaitForScreenAsync(
			"Текущая папка",
			cancellationToken: TestContext.Current.CancellationToken);
		picker = await terminal.WaitForScreenAsync(
			"Проект с пробелами",
			cancellationToken: TestContext.Current.CancellationToken);
		picker = await terminal.WaitForScreenAsync(
			"Назад",
			cancellationToken: TestContext.Current.CancellationToken);
		picker = await terminal.WaitForScreenAsync(
			"Открыть",
			cancellationToken: TestContext.Current.CancellationToken);
		picker = await terminal.WaitForStableScreenAsync(
			"Отмена",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("Назад", picker, StringComparison.Ordinal);
		Assert.Contains("Открыть", picker, StringComparison.Ordinal);
		Assert.Contains("Отмена", picker, StringComparison.Ordinal);
		Assert.DoesNotContain("Filename", picker, StringComparison.Ordinal);
		Assert.DoesNotContain("Modified", picker, StringComparison.Ordinal);
		Assert.DoesNotContain("Cancel", picker, StringComparison.Ordinal);
		Assert.DoesNotContain("[[Terminal.Tui.", picker, StringComparison.Ordinal);
		TerminalScreenSnapshot.Verify(
			"picker-folder-ru-120x30",
			picker,
			(workspace.Path, "<PROJECT_ROOT>"),
			(Path.GetDirectoryName(workspace.Path) ?? string.Empty, "<TEMP_ROOT>"),
			(workspace.Path[..ClippedWelcomePathLength], "<PATH>"));
		TerminalVisualArtifactWriter.WriteIfRequested(
			"picker-folder-ru-120x30",
			terminal);

		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		var welcome = await terminal.WaitForScreenWithoutAsync(
			"Enter открывает папку.",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains(
			"Недавние рабочие пространства",
			welcome,
			StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	[Fact(Timeout = 90_000)]
	public async Task FolderPickerEntersFoldersGoesBackAndOpensTheChosenFolder()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("notes.txt", "not a project marker");
		workspace.CreateDirectory("OtherFolder");
		workspace.WriteFile(Path.Combine("PickedProject", "src", "PickedMarker.cs"), "class Picked {}");
		await using var terminal = await StartOnWelcomeAsync(workspace.Path, cancellationToken);

		await OpenWelcomeActionAsync(terminal, "Browse folder", cancellationToken);
		await terminal.WaitForScreenAsync("[D]  PickedProject", cancellationToken: cancellationToken);
		// Rows: [..], OtherFolder, PickedProject.
		await terminal.SendDownAsync(cancellationToken);
		await terminal.SendDownAsync(cancellationToken);
		await terminal.SendEnterAsync(cancellationToken);
		var inside = await terminal.WaitForScreenAsync("[D]  src", cancellationToken: cancellationToken);
		Assert.Contains("Current folder", inside, StringComparison.Ordinal);

		await terminal.ClickLabelOnRowAsync("Cancel", "Back", cancellationToken: cancellationToken);
		await terminal.WaitForScreenAsync("[D]  OtherFolder", cancellationToken: cancellationToken);

		await terminal.ClickLabelOnRowAsync(
			"[D]  PickedProject",
			"PickedProject",
			clickCount: 2,
			cancellationToken: cancellationToken);
		await terminal.WaitForScreenAsync("[D]  src", cancellationToken: cancellationToken);

		await terminal.ClickLabelOnRowAsync("Cancel", "Open", cancellationToken: cancellationToken);
		await terminal.WaitForScreenAsync("PROJECT TREE", cancellationToken: cancellationToken);
		await terminal.WaitForScreenAsync("PickedMarker.cs", cancellationToken: cancellationToken);
		await ExitAsync(terminal, cancellationToken);
	}

	[Fact(Timeout = 90_000)]
	public async Task SettingsFilePickerEntersFoldersAndPicksTheFileUnderTheCursor()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("notes.txt", "not a project marker");
		workspace.WriteFile(Path.Combine("settings", "team.json"), "{}");
		await using var terminal = await StartOnWelcomeAsync(workspace.Path, cancellationToken);

		await OpenWelcomeActionAsync(terminal, "Open project with settings file", cancellationToken);
		await terminal.WaitForScreenAsync("[D]  settings", cancellationToken: cancellationToken);
		// Rows: [..], settings.
		await terminal.SendDownAsync(cancellationToken);
		await terminal.SendEnterAsync(cancellationToken);
		await terminal.WaitForScreenAsync("[F]  team.json", cancellationToken: cancellationToken);
		// Rows: [..], team.json.
		await terminal.SendDownAsync(cancellationToken);
		await terminal.SendEnterAsync(cancellationToken);

		var projectPicker = await terminal.WaitForScreenAsync(
			"Project directory:",
			cancellationToken: cancellationToken);
		Assert.Contains("[D]  settings", projectPicker, StringComparison.Ordinal);
		await terminal.SendEscapeAsync(cancellationToken);
		await terminal.WaitForScreenWithoutAsync("Project directory:", cancellationToken: cancellationToken);
		await ExitAsync(terminal, cancellationToken);
	}

	private static Task<TerminalPtyHarness> StartOnWelcomeAsync(
		string workingDirectory,
		CancellationToken cancellationToken) =>
		TerminalPtyHarness.StartAsync(
			workingDirectory,
			["--language", "en"],
			columns: 120,
			rows: 30,
			cancellationToken: cancellationToken);

	private static async Task OpenWelcomeActionAsync(
		TerminalPtyHarness terminal,
		string action,
		CancellationToken cancellationToken)
	{
		await terminal.WaitForScreenAsync(action, cancellationToken: cancellationToken);
		await terminal.ClickLabelOnRowAsync(action, action, clickCount: 2, cancellationToken: cancellationToken);
		await terminal.WaitForScreenAsync("Current folder", cancellationToken: cancellationToken);
	}

	private static async Task ExitAsync(TerminalPtyHarness terminal, CancellationToken cancellationToken)
	{
		await terminal.SendQuitAndConfirmAsync(cancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(cancellationToken: cancellationToken));
	}
}
