namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class TerminalPickerPtyTests
{
	[Fact(Timeout = 60_000)]
	public async Task RussianFolderPickerUsesDevProjexLocalizationAndReturnsToWelcome()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("notes.txt", "not a project marker");
		workspace.CreateDirectory("Проект с пробелами");
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
		picker = await terminal.WaitForScreenAsync(
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
			(Path.GetDirectoryName(workspace.Path) ?? string.Empty, "<TEMP_ROOT>"));
		TerminalVisualArtifactWriter.WriteIfRequested(
			"picker-folder-ru-120x30",
			terminal);

		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		var welcome = await terminal.WaitForScreenAsync(
			"Недавние рабочие пространства",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.DoesNotContain("Текущая папка", welcome, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

}
