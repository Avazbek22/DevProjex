namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class TerminalPreviewRevisionPtyTests
{
	[Fact(Timeout = 90_000)]
	public async Task LatestViewFormatAndSelectionWinDuringRapidInput()
	{
		using var project = new TemporaryDirectory();
		project.WriteFile("global.json", "{}");
		project.WriteFile(
			"src/App.cs",
			"internal sealed class LatestContentMarker { }");
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
			columns: 120,
			rows: 30,
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.WaitForScreenAsync(
			"CONTEXT PREVIEW · Tree · ASCII",
			cancellationToken: TestContext.Current.CancellationToken);

		await SelectFormatAsync(terminal, downCount: 1); // JSON
		await SelectFormatAsync(terminal, downCount: 2); // XML supersedes JSON
		var xml = await terminal.WaitForScreenAsync(
			"CONTEXT PREVIEW · Tree · XML",
			cancellationToken: TestContext.Current.CancellationToken);
		xml = await terminal.WaitForScreenAsync(
			"<d n=",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.DoesNotContain("\"children\"", xml, StringComparison.Ordinal);

		await terminal.SendAsync("3", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"LatestContentMarker",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("1", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"LatestContentMarker",
			cancellationToken: TestContext.Current.CancellationToken);
		var latestView = await terminal.WaitForScreenAsync(
			"<d n=",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("CONTEXT PREVIEW · Tree · XML", latestView, StringComparison.Ordinal);
		Assert.Contains("<d n=", latestView, StringComparison.Ordinal);
		Assert.DoesNotContain("LatestContentMarker", latestView, StringComparison.Ordinal);

		await terminal.SendSpaceAsync(TestContext.Current.CancellationToken);
		await terminal.SendSpaceAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"[x]",
			cancellationToken: TestContext.Current.CancellationToken);
		await Task.Delay(650, TestContext.Current.CancellationToken);
		var final = terminal.CaptureScreen();
		Assert.Contains("CONTEXT PREVIEW · Tree · XML", final, StringComparison.Ordinal);
		Assert.Contains("<d n=", final, StringComparison.Ordinal);
		Assert.Contains("Files 2", final, StringComparison.Ordinal);
		Assert.DoesNotContain("DPX-TUI-PREVIEW-FAILED", final, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);

		await terminal.SendAsync("q", TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	private static async Task SelectFormatAsync(
		TerminalPtyHarness terminal,
		int downCount)
	{
		await terminal.SendAsync("F", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Choose ASCII, JSON, XML, or Markdown for the tree.",
			cancellationToken: TestContext.Current.CancellationToken);
		for (var index = 0; index < downCount; index++)
			await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Choose ASCII, JSON, XML, or Markdown for the tree.",
			cancellationToken: TestContext.Current.CancellationToken);
	}
}
