namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class TerminalLargePreviewPtyTests
{
	[Fact(Timeout = 120_000)]
	public async Task FileBackedPreviewReachesFirstMiddleAndFinalSectionsWithDistinctScrollbars()
	{
		using var project = CreateLargeProject();
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
			"PROJECT TREE",
			timeout: TimeSpan.FromSeconds(45),
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("2", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"LargeMarker001",
			timeout: TimeSpan.FromSeconds(45),
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		var first = await terminal.WaitForScreenAsync(
			"> CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("Files 1-", first, StringComparison.Ordinal);
		await terminal.WaitForScreenAsync(
			"j/k Scroll",
			cancellationToken: TestContext.Current.CancellationToken);
		first = terminal.CaptureScreen();
		Assert.Contains("┃", first, StringComparison.Ordinal);
		Assert.Contains("━", first, StringComparison.Ordinal);
		Assert.Contains("·", first, StringComparison.Ordinal);
		Assert.DoesNotContain("░", first, StringComparison.Ordinal);
		Verify("large-preview-first-en-120x30", terminal, project.Path);

		await terminal.SendRightAsync(TestContext.Current.CancellationToken);
		var horizontallyScrolled = await terminal.WaitForScreenAsync(
			"5-66/",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("> CONTEXT PREVIEW", horizontallyScrolled, StringComparison.Ordinal);
		Assert.Contains("━", horizontallyScrolled, StringComparison.Ordinal);
		await terminal.SendHomeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"1-62/",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync("/", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Find text across the complete context:",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("LargeMarker060", TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			"Find text across the complete context:",
			cancellationToken: TestContext.Current.CancellationToken);
		var middle = await terminal.WaitForScreenAsync(
			"LargeMarker060",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("/120", middle, StringComparison.Ordinal);
		await terminal.WaitForScreenAsync(
			"F 60-",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"j/k Scroll",
			cancellationToken: TestContext.Current.CancellationToken);
		Verify("large-preview-middle-search-en-120x30", terminal, project.Path);

		await terminal.SendEndAsync(TestContext.Current.CancellationToken);
		var final = await terminal.WaitForScreenAsync(
			"LargeMarker120",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("Files ", final, StringComparison.Ordinal);
		Assert.Contains("/120", final, StringComparison.Ordinal);
		var finalRange = await terminal.WaitForScreenAsync(
			"F 118-120/120",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("C 1-62/5", finalRange, StringComparison.Ordinal);
		await terminal.WaitForScreenAsync(
			"j/k Scroll",
			cancellationToken: TestContext.Current.CancellationToken);
		Verify("large-preview-final-en-120x30", terminal, project.Path);
		Assert.False(terminal.HasExited);

		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	private static TemporaryDirectory CreateLargeProject()
	{
		var project = new TemporaryDirectory();
		for (var index = 1; index <= 120; index++)
		{
			project.WriteFile(
				$"src/File{index:D3}.cs",
				$"internal sealed class LargeMarker{index:D3} {{ }}\n" +
				new string('x', 5_500));
		}
		return project;
	}

	private static void Verify(
		string name,
		TerminalPtyHarness terminal,
		string projectPath)
	{
		TerminalScreenSnapshot.Verify(
			name,
			terminal.CaptureScreen(),
			(projectPath, "<PROJECT_ROOT>"),
			(Path.GetDirectoryName(projectPath) ?? string.Empty, "<TEMP_ROOT>"),
			(Path.GetFileName(projectPath), "<PROJECT>"));
		TerminalVisualArtifactWriter.WriteIfRequested(name, terminal);
	}
}
