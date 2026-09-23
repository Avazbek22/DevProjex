using DevProjex.Infrastructure.ProjectProfiles;

namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class TerminalPreviewRevisionPtyTests
{
	[Fact(Timeout = 90_000)]
	public async Task CtrlUShowsUncheckedTreeWhileRetainingWholeTreePreview()
	{
		using var project = new TemporaryDirectory();
		project.WriteFile("first.cs", "internal sealed class First { }");
		project.WriteFile("second.cs", "internal sealed class Second { }");
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
			"Files 2",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendCtrlAAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"[x]",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("3", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"internal sealed class First",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("\u0015", TestContext.Current.CancellationToken);
		var uncheckedTree = await terminal.WaitForStableScreenAsync(
			required: "internal sealed class First",
			forbidden: "[x]",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("[ ]", uncheckedTree, StringComparison.Ordinal);
		Assert.Contains("Files 2", uncheckedTree, StringComparison.Ordinal);
		Assert.Contains("second.cs", uncheckedTree, StringComparison.Ordinal);

		await terminal.SendCtrlAAsync(TestContext.Current.CancellationToken);
		var restored = await terminal.WaitForStableScreenAsync(
			required: "[x]",
			forbidden: "[ ]",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("[x]", restored, StringComparison.Ordinal);
		Assert.Contains("Files 2", restored, StringComparison.Ordinal);
		Assert.Contains("internal sealed class First", restored, StringComparison.Ordinal);

		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	[Fact(Timeout = 90_000)]
	public async Task ExplicitlyEmptyLocalSelectionStartsWithNoPreviewUntilTreeChanges()
	{
		using var project = new TemporaryDirectory();
		project.WriteFile("first.cs", "internal sealed class First { }");
		project.WriteFile("second.cs", "internal sealed class Second { }");
		await using var terminal = await TerminalPtyHarness.StartAsync(
			project.Path,
			[
				"tui",
				project.Path,
				"--profile",
				"local",
				"--screen",
				"inline",
				"--no-mouse",
				"--language",
				"en"
			],
			columns: 120,
			rows: 30,
			initializeDataRoot: dataRoot => new ProjectProfileStore(() => dataRoot).SaveProfile(
				project.Path,
				new ProjectSelectionProfile(
					SelectedRootFolders: [],
					SelectedExtensions: [".cs"],
					SelectedIgnoreOptions: [],
					ExtensionStates: new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
					{
						[".cs"] = true
					},
					SelectedPaths: [])),
			cancellationToken: TestContext.Current.CancellationToken);

		var initial = await terminal.WaitForStableScreenAsync(
			screen =>
				screen.Contains("PROJECT TREE", StringComparison.Ordinal) &&
				screen.Contains("Files 0", StringComparison.Ordinal),
			"showing an explicitly empty local selection",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("[ ]", initial, StringComparison.Ordinal);

		await terminal.SendAsync("3", TestContext.Current.CancellationToken);
		var emptyPreview = await terminal.WaitForStableScreenAsync(
			screen =>
				screen.Contains("CONTEXT PREVIEW · Tree + content", StringComparison.Ordinal) &&
				screen.Contains("Files 0", StringComparison.Ordinal),
			"showing no content for an explicitly empty local selection",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.DoesNotContain("internal sealed class First", emptyPreview, StringComparison.Ordinal);
		Assert.DoesNotContain("internal sealed class Second", emptyPreview, StringComparison.Ordinal);
		Assert.DoesNotContain("DPX-TUI-PREVIEW-FAILED", emptyPreview, StringComparison.Ordinal);

		await terminal.SendCtrlAAsync(TestContext.Current.CancellationToken);
		var selected = await terminal.WaitForStableScreenAsync(
			required: "internal sealed class First",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("Files 2", selected, StringComparison.Ordinal);
		Assert.Contains("internal sealed class Second", selected, StringComparison.Ordinal);

		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	[Fact(Timeout = 90_000)]
	public async Task LatestViewFormatAndSelectionWinDuringRapidInput()
	{
		using var project = new TemporaryDirectory();
		project.WriteFile(
			"global.json",
			"{\"SelectionWinner\":\"LatestSelectionMarker\"}");
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

		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendSpaceAsync(TestContext.Current.CancellationToken);
		// Switch view while the debounced selection reprojection is still pending. The final
		// document must be generated from the resulting plan, not the pre-projection plan.
		await terminal.SendAsync("3", TestContext.Current.CancellationToken);
		var selectedContent = await terminal.WaitForStableScreenAsync(
			required: "LatestContentMarker",
			forbidden: "LatestSelectionMarker",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains(
			"CONTEXT PREVIEW · Tree + content · XML",
			selectedContent,
			StringComparison.Ordinal);
		Assert.Contains("<d n=", selectedContent, StringComparison.Ordinal);
		Assert.Contains("[x]", selectedContent, StringComparison.Ordinal);
		Assert.Contains("Files 1", selectedContent, StringComparison.Ordinal);
		Assert.DoesNotContain(
			"LatestSelectionMarker",
			selectedContent,
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			"DPX-TUI-PREVIEW-FAILED",
			selectedContent,
			StringComparison.Ordinal);
		Assert.False(terminal.HasExited);

		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
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
