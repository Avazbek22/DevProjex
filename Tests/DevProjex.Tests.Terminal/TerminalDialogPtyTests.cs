namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class TerminalDialogPtyTests
{
	[Fact(Timeout = 90_000)]
	public async Task DoubleClickPicksAChoiceAndThePaletteRunButtonExecutesTheAction()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var project = CreateProject();
		await using var terminal = await StartWorkspaceAsync(project.Path, cancellationToken);
		await terminal.WaitForScreenAsync("CONTEXT PREVIEW · Tree · ASCII", cancellationToken: cancellationToken);

		await terminal.SendAsync("F", cancellationToken);
		await terminal.WaitForScreenAsync("│ Markdown ", cancellationToken: cancellationToken);
		// The list row, not the description above it that also names JSON.
		await terminal.ClickLabelOnRowAsync("│ JSON ", "JSON", clickCount: 2, cancellationToken: cancellationToken);
		await terminal.WaitForScreenAsync("CONTEXT PREVIEW · Tree · JSON", cancellationToken: cancellationToken);

		await terminal.SendAsync("\u0010", cancellationToken);
		await terminal.WaitForScreenAsync("Filter actions:", cancellationToken: cancellationToken);
		await terminal.SendAsync("Preview format", cancellationToken);
		await terminal.WaitForScreenAsync("Preview format: JSON", cancellationToken: cancellationToken);
		await terminal.ClickLabelOnRowAsync("Back", "Run", cancellationToken: cancellationToken);
		await terminal.WaitForScreenWithoutAsync("Filter actions:", cancellationToken: cancellationToken);
		await terminal.WaitForScreenAsync("│ Markdown ", cancellationToken: cancellationToken);

		await terminal.SendEscapeAsync(cancellationToken);
		await terminal.WaitForScreenWithoutAsync("│ Markdown ", cancellationToken: cancellationToken);
		await ExitAsync(terminal, cancellationToken);
	}

	[Fact(Timeout = 90_000)]
	public async Task DoubleClickOnAClosingDialogButtonLeavesTheWelcomeRowBehindItAlone()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("notes.txt", "not a project marker");
		await using var terminal = await TerminalPtyHarness.StartAsync(
			workspace.Path,
			["--language", "en"],
			columns: 80,
			rows: 22,
			cancellationToken: cancellationToken);
		await terminal.WaitForScreenAsync("Clone repository", cancellationToken: cancellationToken);
		// Rows: Open current folder, Recent workspaces, Browse folder, settings file, Clone repository.
		for (var row = 0; row < 4; row++)
			await terminal.SendDownAsync(cancellationToken);
		await Task.Delay(300, cancellationToken);
		var welcome = await terminal.WaitForStableScreenAsync("Clone repository", cancellationToken: cancellationToken);

		await terminal.SendEnterAsync(cancellationToken);
		await terminal.WaitForScreenAsync("Repository URL:", cancellationToken: cancellationToken);
		// At 80x22 the Cancel button covers the Welcome settings-file row. A human double-click
		// leaves the prompt time to close before the second press arrives.
		await terminal.ClickLabelOnRowAsync(
			"Cancel",
			"Cancel",
			clickCount: 2,
			pauseBetweenClicksMilliseconds: 150,
			cancellationToken: cancellationToken);
		await terminal.WaitForScreenWithoutAsync("Repository URL:", cancellationToken: cancellationToken);
		// A leaked click opens a picker or moves the selection; give it time to show.
		await Task.Delay(800, cancellationToken);
		var after = await terminal.WaitForStableScreenAsync("Clone repository", cancellationToken: cancellationToken);

		Assert.Equal(welcome, after);
		await ExitAsync(terminal, cancellationToken);
	}

	private static Task<TerminalPtyHarness> StartWorkspaceAsync(
		string projectPath,
		CancellationToken cancellationToken) =>
		TerminalPtyHarness.StartAsync(
			projectPath,
			["tui", projectPath, "--profile", "standard", "--screen", "inline", "--language", "en"],
			columns: 120,
			rows: 30,
			cancellationToken: cancellationToken);

	private static TemporaryDirectory CreateProject()
	{
		var project = new TemporaryDirectory();
		project.WriteFile("global.json", "{}");
		project.WriteFile("src/App.cs", "internal sealed class DialogMarker { }");
		return project;
	}

	private static async Task ExitAsync(TerminalPtyHarness terminal, CancellationToken cancellationToken)
	{
		await terminal.SendQuitAndConfirmAsync(cancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(cancellationToken: cancellationToken));
	}
}
