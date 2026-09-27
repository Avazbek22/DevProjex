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
