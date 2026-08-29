using System.Diagnostics;
using DevProjex.Application.Presentation;
using DevProjex.Infrastructure.ProjectProfiles;

namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class TerminalSettingsPanelPtyTests
{
	[Fact(Timeout = 90_000)]
	public async Task MiniPanelsRenderAcrossEveryWorkspaceLayout()
	{
		using var project = CreatePanelProject();
		await using var terminal = await StartAsync(project.Path, columns: 160, rows: 50);

		await WaitForStableScreenAsync(terminal, "PROJECT TREE");
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "> PARAMETERS");
		var wide = await VerifyLayoutAsync(
			terminal,
			project.Path,
			"workspace-settings-en-160x50",
			expectWide: true);
		Assert.DoesNotContain("Saved settings:", wide, StringComparison.Ordinal);

		await terminal.ResizeAsync(150, 45, TestContext.Current.CancellationToken);
		await VerifyLayoutAsync(
			terminal,
			project.Path,
			"workspace-settings-en-150x45",
			expectWide: true);

		await terminal.ResizeAsync(130, 40, TestContext.Current.CancellationToken);
		await VerifyLayoutAsync(terminal, project.Path, "workspace-settings-en-130x40");

		await terminal.ResizeAsync(100, 30, TestContext.Current.CancellationToken);
		await VerifyLayoutAsync(terminal, project.Path, "workspace-settings-en-100x30");

		await terminal.ResizeAsync(80, 24, TestContext.Current.CancellationToken);
		await VerifyLayoutAsync(terminal, project.Path, "workspace-settings-en-80x24");

		await terminal.ResizeAsync(70, 24, TestContext.Current.CancellationToken);
		var compact = await VerifyLayoutAsync(
			terminal,
			project.Path,
			"workspace-settings-en-70x24");
		Assert.Contains('▲', ExtractPanel(compact, "Exclusions", "File types"));

		await terminal.ResizeAsync(59, 19, TestContext.Current.CancellationToken);
		var tooSmall = await WaitForStableScreenAsync(terminal, "Terminal too small");
		TerminalScreenSnapshot.Verify(
			"workspace-settings-too-small-en-59x19",
			tooSmall,
			(project.Path, "<PROJECT_ROOT>"));
		Assert.DoesNotContain("Content processing", tooSmall, StringComparison.Ordinal);

		await terminal.ResizeAsync(160, 50, TestContext.Current.CancellationToken);
		await VerifyLayoutAsync(
			terminal,
			project.Path,
			"workspace-settings-en-160x50",
			expectWide: true);

		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task LocalProfileDoesNotAddASettingsPanelRow()
	{
		using var project = CreatePanelProject();
		await using var terminal = await StartAsync(
			project.Path,
			columns: 100,
			rows: 30,
			profile: "local",
			initializeDataRoot: dataRoot => new ProjectProfileStore(() => dataRoot).SaveProfile(
				project.Path,
				new ProjectSelectionProfile(
					SelectedRootFolders: [],
					SelectedExtensions: [".cs", ".md"],
					SelectedIgnoreOptions: [])));

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);

		var parameters = await WaitForStableScreenAsync(terminal, "Content processing");
		Assert.DoesNotContain("Saved settings", parameters, StringComparison.Ordinal);
		Assert.Contains("Content processing", parameters, StringComparison.Ordinal);
		TerminalScreenSnapshot.Verify(
			"workspace-settings-local-en-100x30",
			parameters,
			(project.Path, "<PROJECT_ROOT>"));
		Assert.Contains('▲', ExtractPanel(parameters, "Exclusions", "File types"));
		var fileTypes = ExtractPanel(parameters, "File types", null);
		Assert.DoesNotContain('▲', fileTypes);
		Assert.DoesNotContain('▼', fileTypes);

		await terminal.ResizeAsync(160, 30, TestContext.Current.CancellationToken);
		var wide = await WaitForStableScreenAsync(terminal, "Content processing");
		Assert.DoesNotContain("Saved settings", wide, StringComparison.Ordinal);
		Assert.Contains("Content processing", wide, StringComparison.Ordinal);
		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task AggregateControlsRenderOnFramesAndOnlyFocusedSectionHighlightsSelection()
	{
		using var project = CreatePanelProject();
		await using var terminal = await StartAsync(project.Path, columns: 160, rows: 50);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		var contentFocused = await WaitForStableScreenAsync(terminal, "> PARAMETERS");
		AssertFrameAggregate(contentFocused, "Content processing", expectedCount: 5);
		AssertFrameAggregate(contentFocused, "Exclusions", ExpectedExclusionCount);
		AssertFrameAggregate(contentFocused, "File types", expectedCount: 3);
		Assert.DoesNotContain("Content processing:", contentFocused, StringComparison.Ordinal);
		AssertOnlyRowIsHighlighted(
			terminal,
			contentFocused,
			activeText: "Hide secrets",
			inactiveTexts: ["Use .gitignore", ".cs"]);
		TerminalScreenSnapshot.Verify(
			"workspace-settings-content-focused-en-160x50",
			contentFocused,
			(project.Path, "<PROJECT_ROOT>"));

		await terminal.SendAsync("X", TestContext.Current.CancellationToken);
		var exclusionsFocused = await WaitForStableScreenAsync(terminal, "Exclusions");
		AssertOnlyRowIsHighlighted(
			terminal,
			exclusionsFocused,
			activeText: "[x] All",
			inactiveTexts: ["Hide secrets", ".cs"]);
		TerminalScreenSnapshot.Verify(
			"workspace-settings-exclusions-focused-en-160x50",
			exclusionsFocused,
			(project.Path, "<PROJECT_ROOT>"));

		await terminal.SendAsync("T", TestContext.Current.CancellationToken);
		var extensionsFocused = await WaitForStableScreenAsync(terminal, "File types");
		AssertOnlyRowIsHighlighted(
			terminal,
			extensionsFocused,
			activeText: "[x] All",
			inactiveTexts: ["Hide secrets", "Use .gitignore"],
			lastOccurrence: true);
		TerminalScreenSnapshot.Verify(
			"workspace-settings-extensions-focused-en-160x50",
			extensionsFocused,
			(project.Path, "<PROJECT_ROOT>"));

		await ExitAsync(terminal);
	}

	[Fact(Timeout = 120_000)]
	public async Task GitShortcutKeepsEmptyStagedScopeAndItsSettingsMetadataConsistent()
	{
		using var project = CreatePanelProject(initializeGit: true);
		project.WriteFile(".unrelated/Noise.cs", "class Noise {}\n");
		project.WriteFile(".metadata", "metadata\n");
		project.WriteFile("NOTICE", "notice\n");
		RunGit(project.Path, "add", "--all");
		RunGit(project.Path, "commit", "--quiet", "-m", "Add unrelated baseline noise");
		await using var terminal = await StartAsync(project.Path, columns: 160, rows: 50);

		await WaitForStableScreenAsync(terminal, "PROJECT TREE");
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await WaitForStableScreenAsync(terminal, "> PARAMETERS");
		await terminal.SendAsync(":type .cs off\r", TestContext.Current.CancellationToken);
		await WaitForAppliedCommandAsync(terminal, ".cs: disabled", "[ ] .cs");
		await terminal.SendAsync("X", TestContext.Current.CancellationToken);
		await terminal.SendAsync("M", TestContext.Current.CancellationToken);
		var tracked = await WaitForStableScreenAsync(terminal, "(•) Tracked Git files only");
		Assert.Contains("( ) Use .gitignore", tracked, StringComparison.Ordinal);
		Assert.Contains("dot folders (1)", tracked, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("dot files (1)", tracked, StringComparison.OrdinalIgnoreCase);

		await terminal.SendAsync("M", TestContext.Current.CancellationToken);
		var staged = await WaitForStableScreenAsync(terminal, "No visible items");
		Assert.Contains("(•) Staged Git files", staged, StringComparison.Ordinal);
		Assert.Contains("( ) Tracked Git files only", staged, StringComparison.Ordinal);
		Assert.DoesNotContain("Smart ignore", staged, StringComparison.Ordinal);
		Assert.DoesNotContain("Dot folders (", staged, StringComparison.Ordinal);
		Assert.DoesNotContain("Dot files (", staged, StringComparison.Ordinal);
		Assert.DoesNotContain("Extensionless files (", staged, StringComparison.Ordinal);
		Assert.Contains("[ ] All", staged, StringComparison.Ordinal);

		await terminal.SendAsync("M", TestContext.Current.CancellationToken);
		var changes = await WaitForStableScreenAsync(terminal, "(•) Current Git changes");
		Assert.Contains("No visible items", changes, StringComparison.Ordinal);
		Assert.DoesNotContain("Smart ignore", changes, StringComparison.Ordinal);
		Assert.DoesNotContain("Dot folders (", changes, StringComparison.Ordinal);
		Assert.DoesNotContain("Dot files (", changes, StringComparison.Ordinal);
		Assert.DoesNotContain("Extensionless files (", changes, StringComparison.Ordinal);
		Assert.Contains("[ ] All", changes, StringComparison.Ordinal);

		await terminal.SendAsync(":set git staged\r", TestContext.Current.CancellationToken);
		await WaitForAppliedCommandAsync(
			terminal,
			"Staged Git files",
			"(•) Staged Git files");

		project.WriteFile(".scoped/Staged.cs", "class Staged {}\n");
		RunGit(project.Path, "add", "--", ".scoped/Staged.cs");
		await terminal.SendAsync(":refresh\r", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Project refreshed.",
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);
		var scoped = await WaitForStableScreenAsync(terminal, "dot folders (1)");
		Assert.Contains("[x] All (1)", scoped, StringComparison.Ordinal);
		Assert.DoesNotContain("Dot files (", scoped, StringComparison.Ordinal);
		Assert.DoesNotContain("Extensionless files (", scoped, StringComparison.Ordinal);
		Assert.DoesNotContain("Staged.cs", scoped, StringComparison.Ordinal);

		await terminal.SendAsync("X", TestContext.Current.CancellationToken);
		await terminal.SendHomeAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var unblockedPath = await terminal.WaitForScreenAsync(
			"[ ] .cs",
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("(•) Staged Git files", unblockedPath, StringComparison.Ordinal);
		Assert.Contains("[ ] All (1)", unblockedPath, StringComparison.Ordinal);
		Assert.DoesNotContain("Staged.cs", unblockedPath, StringComparison.Ordinal);

		await terminal.SendAsync(":type .cs on\r", TestContext.Current.CancellationToken);
		var revealedByType = await WaitForAppliedCommandAsync(
			terminal,
			".cs: enabled",
			"Staged.cs");
		Assert.Contains("[x] .cs", revealedByType, StringComparison.Ordinal);

		await terminal.SendAsync(":all exclusions on\r", TestContext.Current.CancellationToken);
		var hiddenByCommand = await WaitForAppliedCommandAsync(
			terminal,
			"All: enabled",
			"No visible items");
		Assert.Contains("(•) Staged Git files", hiddenByCommand, StringComparison.Ordinal);
		Assert.True(
			hiddenByCommand.Contains("[x] All (1)", StringComparison.Ordinal),
			$"The exclusion aggregate did not reflect the active scoped blocker.{Environment.NewLine}{hiddenByCommand}");

		await terminal.SendAsync(":set dot-folders off\r", TestContext.Current.CancellationToken);
		var revealed = await WaitForAppliedCommandAsync(
			terminal,
			"dot folders: disabled",
			"Staged.cs");
		Assert.Contains("[x] .cs", revealed, StringComparison.Ordinal);
		Assert.Contains("[ ] All (1)", revealed, StringComparison.Ordinal);
		Assert.DoesNotContain(".metadata", revealed, StringComparison.Ordinal);
		Assert.DoesNotContain("NOTICE", revealed, StringComparison.Ordinal);

		await terminal.SendAsync(":set dot-folders on\r", TestContext.Current.CancellationToken);
		var hiddenAgain = await WaitForAppliedCommandAsync(
			terminal,
			"dot folders: enabled",
			"No visible items");
		Assert.DoesNotContain("Staged.cs", hiddenAgain, StringComparison.Ordinal);

		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task MouseClickOnFrameAggregateTogglesOnlyItsSection()
	{
		using var project = CreatePanelProject();
		project.WriteFile(".hidden.cs", "internal sealed class Hidden { }");
		await using var terminal = await StartAsync(
			project.Path,
			columns: 160,
			rows: 50,
			mouse: true);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		var screen = await WaitForStableScreenAsync(terminal, "Exclusions");
		Assert.Contains("(•) Use .gitignore", screen, StringComparison.Ordinal);
		Assert.DoesNotContain(".hidden.cs", screen, StringComparison.Ordinal);
		var (row, column) = FindFrameAggregate(screen, "Exclusions", "[x] All");
		await terminal.SendMouseClickAsync(
			column,
			row,
			cancellationToken: TestContext.Current.CancellationToken);
		var cleared = await WaitForFrameAggregateAsync(
			terminal,
			"Exclusions",
			"[ ] All");
		cleared = await WaitForStableScreenAsync(terminal, ".hidden.cs");
		Assert.Contains("(•) Use .gitignore", cleared, StringComparison.Ordinal);
		Assert.Contains(".hidden.cs", cleared, StringComparison.Ordinal);
		Assert.Contains("[x] .cs", cleared, StringComparison.Ordinal);

		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task MouseClickBelowTheLastFileTypeLeavesSelectionUnchanged()
	{
		using var project = CreatePanelProject();
		await using var terminal = await StartAsync(
			project.Path,
			columns: 100,
			rows: 30,
			mouse: true);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("T", TestContext.Current.CancellationToken);
		var before = await WaitForStableScreenAsync(terminal, "[x] .md");
		var lines = before.Split('\n');
		var lastItemRow = Array.FindIndex(lines, static line =>
			line.Contains("[x] .md", StringComparison.Ordinal));
		Assert.True(lastItemRow >= 0, before);
		var markerColumn = lines[lastItemRow].IndexOf("[x] .md", StringComparison.Ordinal);
		Assert.True(markerColumn >= 0, before);

		await terminal.SendMouseClickAsync(
			markerColumn + 1,
			lastItemRow + 1,
			cancellationToken: TestContext.Current.CancellationToken);
		await Task.Delay(500, TestContext.Current.CancellationToken);

		var after = terminal.CaptureScreen();
		Assert.False(terminal.HasExited);
		Assert.Contains("[x] .cs", after, StringComparison.Ordinal);
		Assert.Contains("[x] .json", after, StringComparison.Ordinal);
		Assert.Contains("[x] .md", after, StringComparison.Ordinal);
		Assert.Contains("[x] All (3)", ExtractPanel(after, "File types", null), StringComparison.Ordinal);
		Assert.DoesNotContain("Updating options…", after, StringComparison.Ordinal);
		Assert.DoesNotContain("Building tree…", after, StringComparison.Ordinal);

		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task ContentAggregateTogglesAllFiveTransformations()
	{
		using var project = CreatePanelProject();
		await using var terminal = await StartAsync(project.Path, columns: 100, rows: 30);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> PARAMETERS",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendHomeAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);

		var enabled = await terminal.WaitForScreenAsync(
			"[x] All (5)",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.All(
			new[]
			{
				"Hide secrets",
				"Hide private data",
				"Compress code",
				"Strip comments",
				"Strip blank lines"
			},
			label => Assert.Contains($"[x] {label}", enabled, StringComparison.Ordinal));

		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var disabled = await terminal.WaitForScreenAsync(
			"[ ] All (5)",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.All(
			new[]
			{
				"Hide secrets",
				"Hide private data",
				"Compress code",
				"Strip comments",
				"Strip blank lines"
			},
			label => Assert.Contains($"[ ] {label}", disabled, StringComparison.Ordinal));

		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task PlainModeKeepsAggregateControlsAsPinnedFirstRows()
	{
		using var project = CreatePanelProject();
		await using var terminal = await StartAsync(
			project.Path,
			columns: 80,
			rows: 24,
			plain: true);

		await terminal.WaitForScreenAsync(
			"> PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		var screen = await WaitForStableScreenAsync(terminal, "> PARAMETERS");
		var lines = screen.Split('\n');
		var aggregateRows = lines
			.Select((line, index) => (line, index))
			.Where(static pair => pair.line.Contains("] All (", StringComparison.Ordinal))
			.Select(static pair => pair.index)
			.ToArray();
		Assert.Equal(3, aggregateRows.Length);
		Assert.True(aggregateRows[0] < Array.FindIndex(
			lines,
			line => line.Contains("Hide secrets", StringComparison.Ordinal)));
		Assert.True(aggregateRows[1] < Array.FindIndex(
			lines,
			line => line.Contains("Use .gitignore", StringComparison.Ordinal)));
		Assert.True(aggregateRows[2] < Array.FindIndex(
			lines,
			line => line.Contains("[x] .cs", StringComparison.Ordinal)));
		TerminalScreenSnapshot.Verify(
			"workspace-settings-plain-en-80x24",
			screen,
			(project.Path, "<PROJECT_ROOT>"));

		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task RealProcessPanelExitRestoresParentTerminal()
	{
		using var project = CreatePanelProject();
		await using var terminal = await StartAsync(
			project.Path,
			columns: 100,
			rows: 30,
			writeShellCompletionMarker: true);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		var screen = await WaitForStableScreenAsync(terminal, "> PARAMETERS");
		AssertFrameAggregate(screen, "Content processing", expectedCount: 5);
		AssertFrameAggregate(screen, "Exclusions", ExpectedExclusionCount);
		AssertFrameAggregate(screen, "File types", expectedCount: 3);

		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
		await terminal.CompleteShellRestorationHandshakeAsync(
			TestContext.Current.CancellationToken);
		TerminalPtyStateAssertions.AssertRestoredAtShellCompletion(
			terminal.RawOutput,
			"inline");
		await terminal.ReleaseParentShellAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}

	[Fact(Timeout = 90_000)]
	public async Task AggregateRowsRemainVisibleWhileTheirListsScroll()
	{
		using var project = CreatePanelProject();
		await using var terminal = await StartAsync(project.Path, columns: 70, rows: 24);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("X", TestContext.Current.CancellationToken);
		await terminal.SendEndAsync(TestContext.Current.CancellationToken);
		var exclusions = await WaitForStableScreenAsync(terminal, "Exclusions");
		Assert.Contains("[x] All", ExtractPanel(exclusions, "Exclusions", "File types"));

		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendEndAsync(TestContext.Current.CancellationToken);
		var extensions = await WaitForStableScreenAsync(terminal, "File types");
		Assert.Contains("[x] All", ExtractPanel(extensions, "File types", null));
		await ExitAsync(terminal);
	}

	[Theory(Timeout = 90_000)]
	[InlineData("standard", "workspace-empty-tree-standard-en-160x50")]
	[InlineData("local", "workspace-empty-tree-local-en-160x50")]
	public async Task EmptyTreeHintKeepsTheRootForStandardAndLocalProfiles(
		string profile,
		string snapshot)
	{
		using var project = CreatePanelProject();
		await using var terminal = await StartAsync(
			project.Path,
			columns: 160,
			rows: 50,
			profile: profile,
			initializeDataRoot: profile == "local"
				? dataRoot => new ProjectProfileStore(() => dataRoot).SaveProfile(
					project.Path,
					new ProjectSelectionProfile(
						SelectedRootFolders: [],
						SelectedExtensions: [".cs", ".json", ".md"],
						SelectedIgnoreOptions: []))
				: null);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> PARAMETERS",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("T", TestContext.Current.CancellationToken);
		await terminal.SendHomeAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var empty = await WaitForStableScreenAsync(
			terminal,
			"No visible items",
			screen => screen.Contains("Lines 1-2/2", StringComparison.Ordinal));
		Assert.Contains("Files 0", empty, StringComparison.Ordinal);
		Assert.Contains("Folders 0", empty, StringComparison.Ordinal);
		Assert.Matches(@"~[1-9][0-9]* tokens", empty);
		Assert.Contains("v [x]", ExtractFirstPanel(empty), StringComparison.Ordinal);
		Assert.Contains("[ ] All (3)", ExtractPanel(empty, "File types", null), StringComparison.Ordinal);
		Assert.DoesNotContain("Processing request", empty, StringComparison.Ordinal);
		TerminalScreenSnapshot.Verify(snapshot, empty, (project.Path, "<PROJECT_ROOT>"));
		foreach (var viewShortcut in new[] { "1", "2", "3" })
		{
			await terminal.SendAsync(viewShortcut, TestContext.Current.CancellationToken);
			await Task.Delay(250, TestContext.Current.CancellationToken);
			var view = terminal.CaptureScreen();
			Assert.False(terminal.HasExited);
			Assert.Contains("Files 0", view, StringComparison.Ordinal);
			Assert.DoesNotContain("DPX-TUI-PREVIEW-FAILED", view, StringComparison.Ordinal);
		}

		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var restored = await terminal.WaitForScreenAsync(
			"Files 3",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.DoesNotContain("No visible items", restored, StringComparison.Ordinal);
		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task ArrowKeysCrossMiniPanelBoundariesAndAllAffectsOnlyItsPanel()
	{
		using var project = CreatePanelProject();
		await using var terminal = await StartAsync(project.Path, columns: 100, rows: 30);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> PARAMETERS",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendEndAsync(TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await Task.Delay(100, TestContext.Current.CancellationToken);
		await terminal.SendEndAsync(TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await Task.Delay(100, TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Files 0",
			cancellationToken: TestContext.Current.CancellationToken);
		var controlsWithExtensionsCleared = await WaitForStableScreenAsync(terminal, "[ ] .cs");
		Assert.DoesNotContain("Processing request", controlsWithExtensionsCleared, StringComparison.Ordinal);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		var extensionsCleared = await WaitForStableScreenAsync(
			terminal,
			"No visible items — check file types and exclusions");
		Assert.Contains("[ ] Hide secrets", controlsWithExtensionsCleared, StringComparison.Ordinal);
		Assert.Contains("[ ] All", ExtractPanel(controlsWithExtensionsCleared, "File types", null));
		Assert.Contains("project", ExtractFirstPanel(extensionsCleared), StringComparison.OrdinalIgnoreCase);
		Assert.Contains("Files 0", extensionsCleared, StringComparison.Ordinal);
		Assert.Contains("Folders 0", extensionsCleared, StringComparison.Ordinal);
		Assert.Matches(@"~[1-9][0-9]* tokens", extensionsCleared);
		Assert.DoesNotContain("Processing request", extensionsCleared, StringComparison.Ordinal);

		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var extensionsRestored = await WaitForStableScreenAsync(terminal, "[x] .cs");
		Assert.DoesNotContain(
			"No visible items — check file types and exclusions",
			extensionsRestored,
			StringComparison.Ordinal);
		Assert.Contains("Files 3", extensionsRestored, StringComparison.Ordinal);

		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"No visible items — check file types and exclusions",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> PARAMETERS",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("X", TestContext.Current.CancellationToken);
		await terminal.SendHomeAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await WaitForPanelContainsAsync(terminal, "Exclusions", "File types", "[ ] All");
		await WaitForStableScreenAsync(terminal, "[ ] All");
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		var exclusionsCleared = await WaitForStableScreenAsync(terminal, "(•) No Git filtering");
		Assert.Contains("( ) Use .gitignore", exclusionsCleared, StringComparison.Ordinal);
		Assert.Contains("[ ] .cs", exclusionsCleared, StringComparison.Ordinal);

		await terminal.SendUpAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await WaitForPanelContainsAsync(terminal, "Exclusions", "File types", "[x] All");
		var exclusionsRestored = await WaitForStableScreenAsync(terminal, "(•) Use .gitignore");
		Assert.Contains("[x] Smart ignore", exclusionsRestored, StringComparison.Ordinal);
		Assert.Contains("[ ] .cs", exclusionsRestored, StringComparison.Ordinal);

		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task RedactionRowsPublishCountersAfterTheirPreviewScansComplete()
	{
		using var project = CreatePanelProject(includeFindings: true);
		await using var terminal = await StartAsync(project.Path, columns: 100, rows: 30);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> PARAMETERS",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendHomeAsync(TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var secrets = await terminal.WaitForScreenAsync(
			"Hide secrets (",
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Matches(@"Hide secrets \([1-9][0-9]*(?:/[1-9][0-9]*)?\)", secrets);

		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var privateData = await terminal.WaitForScreenAsync(
			"Hide private data (",
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Matches(@"Hide private data \([1-9][0-9]*(?:/[1-9][0-9]*)?\)", privateData);

		await ExitAsync(terminal);
	}

	[Fact(Timeout = 120_000)]
	public async Task SelectedRowSurvivesRefreshInEveryMiniPanel()
	{
		using var project = CreatePanelProject(includeFindings: true);
		await using var terminal = await StartAsync(project.Path, columns: 100, rows: 30);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> PARAMETERS",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendHomeAsync(TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Hide private data (",
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await WaitForPanelContainsAsync(
			terminal,
			"Content processing",
			"Exclusions",
			"[ ] Hide private data");

		await terminal.SendAsync("X", TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await WaitForPanelContainsAsync(
			terminal,
			"Exclusions",
			"File types",
			"[ ] Smart ignore");
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await WaitForPanelContainsAsync(
			terminal,
			"Exclusions",
			"File types",
			"[x] Smart ignore");

		await terminal.SendAsync("T", TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await WaitForPanelContainsAsync(
			terminal,
			"File types",
			null,
			"[ ] .cs");
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await WaitForPanelContainsAsync(
			terminal,
			"File types",
			null,
			"[x] .cs");

		await ExitAsync(terminal);
	}

	[Theory(Timeout = 90_000)]
	[InlineData("ru", "Обработка содержи…", "Исключения")]
	[InlineData("uz", "Kontentni qa…", "Istisnolar")]
	public async Task LocalizedRedactionLabelsKeepTheirCountersWhenEllipsized(
		string language,
		string contentTitle,
		string exclusionsTitle)
	{
		using var project = CreatePanelProject(includeFindings: true);
		await using var terminal = await StartAsync(
			project.Path,
			columns: 160,
			rows: 40,
			language: language);

		await terminal.WaitForScreenAsync(
			contentTitle,
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendHomeAsync(TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);

		var line = await WaitForSelectedCounterLineAsync(
			terminal,
			contentTitle,
			exclusionsTitle);
		Assert.Matches(@"\([1-9][0-9]*(?:/[0-9]+)?\)\s*│", line);
		await ExitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task TreeScrollBarsAppearOnlyWhileTheExpandedTreeOverflows()
	{
		using var project = CreateScrollableTreeProject();
		await using var terminal = await StartAsync(project.Path, columns: 160, rows: 50);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var expanded = await WaitForStableScreenAsync(terminal, "ExtremelyLongFileName001");
		var expandedTree = ExtractFirstPanel(expanded);
		Assert.Contains('▲', expandedTree);
		Assert.Contains('▼', expandedTree);
		Assert.Contains('◄', expandedTree);
		Assert.Contains('►', expandedTree);

		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var collapsedTree = ExtractFirstPanel(
			await WaitForStableScreenAsync(terminal, "> [x] src"));
		Assert.DoesNotContain('▲', collapsedTree);
		Assert.DoesNotContain('▼', collapsedTree);
		Assert.DoesNotContain('◄', collapsedTree);
		Assert.DoesNotContain('►', collapsedTree);

		await ExitAsync(terminal);
	}

	[Fact(Timeout = 120_000)]
	public async Task KeyboardEnablesEveryTransformationAndExportsTheTransformedProject()
	{
		const string secret = "ghp_a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL";
		const string privateEmail = "ivan.petrov@corp.internal";
		using var project = CreatePanelProject();
		project.WriteFile(
			"src/App.cs",
			$$"""
			namespace Sample;

			// remove this comment
			internal sealed class App
			{
				private const string Token = "{{secret}}";
				private const string Email = "{{privateEmail}}";

				public void Run()
				{
					Console.WriteLine(Token);
				}
			}
			""");
		using var output = new TemporaryDirectory();
		var destination = Path.Combine(output.Path, "transformed-project");
		await using var terminal = await StartAsync(project.Path, columns: 100, rows: 30);

		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> PARAMETERS",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendHomeAsync(TestContext.Current.CancellationToken);
		await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		foreach (var expected in new[]
			{
				"Hide secrets",
				"Hide private data",
				"Compress code",
				"Strip comments",
				"Strip blank lines"
			})
		{
			await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
			await WaitForPanelContainsAsync(
				terminal,
				"Content processing",
				"Exclusions",
				$"[x] {expected}");
			if (expected != "Strip blank lines")
				await terminal.SendDownAsync(TestContext.Current.CancellationToken);
		}

		await terminal.SendAsync("z", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Exact destination:",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendCtrlAAsync(TestContext.Current.CancellationToken);
		await terminal.SendAsync(destination, TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var summary = await terminal.WaitForScreenAsync(
			"Redaction",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("Export?", summary, StringComparison.Ordinal);
		Assert.Contains(
			"Secrets and private data are redacted",
			summary,
			StringComparison.Ordinal);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Export completed:",
			timeout: TimeSpan.FromSeconds(45),
			cancellationToken: TestContext.Current.CancellationToken);

		var exported = await File.ReadAllTextAsync(
			Path.Combine(destination, "src", "App.cs"),
			TestContext.Current.CancellationToken);
		Assert.DoesNotContain(secret, exported, StringComparison.Ordinal);
		Assert.DoesNotContain(privateEmail, exported, StringComparison.Ordinal);
		Assert.DoesNotContain("remove this comment", exported, StringComparison.Ordinal);
		Assert.DoesNotContain("\n\n", exported.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
		Assert.DoesNotContain("Console.WriteLine(Token)", exported, StringComparison.Ordinal);
		await ExitAsync(terminal);
	}

	private static async Task<string> VerifyLayoutAsync(
		TerminalPtyHarness terminal,
		string projectPath,
		string snapshotName,
		bool expectWide = false)
	{
		var screen = await WaitForStableScreenAsync(
			terminal,
			"Content processing",
			value => IsCompletedSettingsLayout(value, expectWide));
		Assert.True(screen.Contains("Exclusions", StringComparison.Ordinal), screen);
		Assert.True(screen.Contains("File types", StringComparison.Ordinal), screen);
		Assert.True(screen.Contains("Hide private data", StringComparison.Ordinal), screen);
		Assert.True(screen.Contains("Strip blank lines", StringComparison.Ordinal), screen);
		Assert.DoesNotContain("Content processing:", screen, StringComparison.Ordinal);
		Assert.DoesNotContain("Saved settings", screen, StringComparison.Ordinal);
		AssertFrameAggregate(screen, "Content processing", expectedCount: 5);
		AssertFrameAggregate(screen, "Exclusions", ExpectedExclusionCount);
		AssertFrameAggregate(screen, "File types", expectedCount: 3);
		Assert.DoesNotContain("ROOT FOLDERS", screen, StringComparison.Ordinal);
		TerminalScreenSnapshot.Verify(
			snapshotName,
			screen,
			(projectPath, "<PROJECT_ROOT>"));
		return screen;
	}

	private static void AssertFrameAggregate(
		string screen,
		string title,
		int? expectedCount = null)
	{
		var marker = title == "Content processing" ? "[ ]" : "[x]";
		var suffix = expectedCount is { } count ? $" ({count})" : " (";
		var titleLine = screen.Split('\n').Single(line =>
			line.Contains(title, StringComparison.Ordinal) &&
			line.Contains($"{marker} All{suffix}", StringComparison.Ordinal));
		Assert.Contains($"{marker} All{suffix}", titleLine, StringComparison.Ordinal);
	}

	private static int ExpectedExclusionCount =>
		ProjectPresentationCatalog.Exclusions.Count + 1;

	private static (int Row, int Column) FindFrameAggregate(
		string screen,
		string title,
		string aggregate)
	{
		var lines = screen.Split('\n');
		var row = Array.FindIndex(
			lines,
			line => line.Contains(title, StringComparison.Ordinal) &&
			        line.Contains(aggregate, StringComparison.Ordinal));
		Assert.True(row >= 0, $"Frame aggregate '{aggregate}' was not rendered.\n{screen}");
		return (row, lines[row].IndexOf(aggregate, StringComparison.Ordinal) + 1);
	}

	private static async Task<string> WaitForFrameAggregateAsync(
		TerminalPtyHarness terminal,
		string title,
		string aggregate)
	{
		var timeout = Stopwatch.StartNew();
		while (timeout.Elapsed < TimeSpan.FromSeconds(15))
		{
			var screen = terminal.CaptureScreen();
			if (screen.Split('\n').Any(line =>
				    line.Contains(title, StringComparison.Ordinal) &&
				    line.Contains(aggregate, StringComparison.Ordinal)))
			{
				return screen;
			}
			await Task.Delay(75, TestContext.Current.CancellationToken);
		}
		throw new TimeoutException(
			$"Timed out waiting for '{aggregate}' on the '{title}' frame.\n" +
			terminal.CaptureScreen());
	}

	private static void AssertOnlyRowIsHighlighted(
		TerminalPtyHarness terminal,
		string screen,
		string activeText,
		IReadOnlyList<string> inactiveTexts,
		bool lastOccurrence = false)
	{
		var activeStyle = CaptureTextStyle(terminal, screen, activeText, lastOccurrence);
		var activeVisual = (activeStyle.BackgroundMode, activeStyle.Background, activeStyle.Inverse);
		var inactiveVisuals = new List<(int BackgroundMode, int Background, bool Inverse)>();
		foreach (var inactiveText in inactiveTexts)
		{
			var inactiveStyle = CaptureTextStyle(terminal, screen, inactiveText);
			var inactiveVisual = (
				inactiveStyle.BackgroundMode,
				inactiveStyle.Background,
				inactiveStyle.Inverse);
			Assert.NotEqual(activeVisual, inactiveVisual);
			inactiveVisuals.Add(inactiveVisual);
		}
		Assert.Single(inactiveVisuals.Distinct());
	}

	private static TerminalCellStyle CaptureTextStyle(
		TerminalPtyHarness terminal,
		string screen,
		string text,
		bool lastOccurrence = false)
	{
		var lines = screen.Split('\n');
		var row = lastOccurrence
			? Array.FindLastIndex(lines, line => line.Contains(text, StringComparison.Ordinal))
			: Array.FindIndex(lines, line => line.Contains(text, StringComparison.Ordinal));
		Assert.True(row >= 0, $"Text '{text}' was not rendered.\n{screen}");
		var column = lines[row].IndexOf(text, StringComparison.Ordinal);
		return terminal.CaptureCellStyle(row, column);
	}

	private static bool IsCompletedSettingsLayout(string screen, bool expectWide)
	{
		var lines = screen.Split('\n');
		if (lines.Length < 3 ||
			!lines[0].StartsWith(" DevProjex Terminal", StringComparison.Ordinal))
		{
			return false;
		}

		return expectWide
			? lines.Any(line =>
				line.StartsWith("┌┤  PROJECT TREE", StringComparison.Ordinal) &&
				line.Contains("CONTEXT PREVIEW", StringComparison.Ordinal) &&
				line.Contains("PARAMETERS", StringComparison.Ordinal))
			: lines.Any(line => line.StartsWith("┌┤> PARAMETERS", StringComparison.Ordinal)) &&
			  !screen.Contains("PROJECT TREE", StringComparison.Ordinal) &&
			  !screen.Contains("CONTEXT PREVIEW", StringComparison.Ordinal);
	}

	private static string ExtractPanel(string screen, string title, string? nextTitle)
	{
		var lines = screen.Split('\n');
		var start = Array.FindIndex(lines, line => line.Contains(title, StringComparison.Ordinal));
		Assert.True(start >= 0, $"Panel '{title}' was not rendered.\n{screen}");
		var end = nextTitle is null
			? lines.Length
			: Array.FindIndex(
				lines,
				start + 1,
				line => line.Contains(nextTitle, StringComparison.Ordinal));
		if (end < 0)
			end = lines.Length;
		return string.Join('\n', lines[start..end]);
	}

	private static string ExtractFirstPanel(string screen)
	{
		return string.Join(
			'\n',
			screen.Split('\n').Select(line =>
			{
				var separator = line.IndexOf("││", StringComparison.Ordinal);
				if (separator < 0)
					separator = line.IndexOf("┐┌", StringComparison.Ordinal);
				return separator < 0 ? line : line[..(separator + 1)];
			}));
	}

	private static async Task<string> WaitForStableScreenAsync(
		TerminalPtyHarness terminal,
		string expected,
		Func<string, bool>? isExpectedLayout = null)
	{
		await terminal.WaitForScreenAsync(
			expected,
			cancellationToken: TestContext.Current.CancellationToken);
		var timeout = Stopwatch.StartNew();
		var stable = Stopwatch.StartNew();
		var previous = terminal.CaptureScreen();
		while (timeout.Elapsed < TimeSpan.FromSeconds(15))
		{
			await Task.Delay(75, TestContext.Current.CancellationToken);
			var current = terminal.CaptureScreen();
			if (isExpectedLayout is not null && !isExpectedLayout(current))
			{
				previous = current;
				stable.Restart();
				continue;
			}
			if (!string.Equals(previous, current, StringComparison.Ordinal))
			{
				previous = current;
				stable.Restart();
				continue;
			}
			if (stable.Elapsed >= TimeSpan.FromMilliseconds(375))
				return current;
		}

		throw new TimeoutException($"Terminal screen did not settle.\n{terminal.CaptureScreen()}");
	}

	private static async Task<string> WaitForAppliedCommandAsync(
		TerminalPtyHarness terminal,
		string result,
		string stateMarker)
	{
		await terminal.WaitForScreenAsync(
			result,
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"C Content",
			timeout: TimeSpan.FromSeconds(10),
			cancellationToken: TestContext.Current.CancellationToken);
		return await WaitForStableScreenAsync(terminal, stateMarker);
	}

	private static async Task WaitForPanelContainsAsync(
		TerminalPtyHarness terminal,
		string title,
		string? nextTitle,
		string expected)
	{
		var timeout = Stopwatch.StartNew();
		while (timeout.Elapsed < TimeSpan.FromSeconds(15))
		{
			var panel = ExtractPanel(terminal.CaptureScreen(), title, nextTitle);
			if (panel.Contains(expected, StringComparison.Ordinal))
				return;
			await Task.Delay(75, TestContext.Current.CancellationToken);
		}

		throw new TimeoutException(
			$"Timed out waiting for '{expected}' in panel '{title}'.\n{terminal.CaptureScreen()}");
	}

	private static async Task<string> WaitForSelectedCounterLineAsync(
		TerminalPtyHarness terminal,
		string contentTitle,
		string exclusionsTitle)
	{
		var timeout = Stopwatch.StartNew();
		while (timeout.Elapsed < TimeSpan.FromSeconds(30))
		{
			var panel = ExtractPanel(terminal.CaptureScreen(), contentTitle, exclusionsTitle);
			var line = panel.Split('\n').FirstOrDefault(candidate =>
				candidate.Split('│').Any(segment =>
					segment.Contains("[x]", StringComparison.Ordinal) &&
					segment.Contains('(') &&
					segment.Contains(')')));
			if (line is not null)
				return line;
			await Task.Delay(75, TestContext.Current.CancellationToken);
		}

		throw new TimeoutException(
			$"Timed out waiting for a localized redaction counter.\n{terminal.CaptureScreen()}");
	}

	private static Task<TerminalPtyHarness> StartAsync(
		string projectPath,
		int columns,
		int rows,
		string profile = "standard",
		string language = "en",
		Action<string>? initializeDataRoot = null,
		bool mouse = false,
		bool plain = false,
		bool writeShellCompletionMarker = false)
	{
		var arguments = new List<string>
		{
			"tui",
			projectPath,
			"--profile",
			profile,
			"--screen",
			"inline",
			mouse ? "--mouse" : "--no-mouse",
			"--language",
			language
		};
		if (plain)
			arguments.Add("--plain");
		return TerminalPtyHarness.StartAsync(
			projectPath,
			arguments,
			columns,
			rows,
			initializeDataRoot: initializeDataRoot,
			writeShellCompletionMarker: writeShellCompletionMarker,
			cancellationToken: TestContext.Current.CancellationToken);
	}

	private static TemporaryDirectory CreatePanelProject(
		bool includeFindings = false,
		bool initializeGit = false)
	{
		var project = new TemporaryDirectory();
		project.WriteFile("global.json", "{}");
		project.WriteFile(
			"src/App.cs",
			includeFindings
				? "const string token = \"ghp_a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL\";\n" +
				  "const string email = \"ivan.petrov@corp.internal\";\n"
				: "internal sealed class App { }");
		project.WriteFile("readme.md", "# Project");
		if (initializeGit)
		{
			RunGit(project.Path, "init", "--quiet");
			RunGit(project.Path, "config", "user.email", "terminal-tests@devprojex.local");
			RunGit(project.Path, "config", "user.name", "DevProjex Terminal Tests");
			RunGit(project.Path, "add", ".");
			RunGit(project.Path, "commit", "--quiet", "-m", "Initial test project");
		}
		return project;
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

	private static TemporaryDirectory CreateScrollableTreeProject()
	{
		var project = new TemporaryDirectory();
		project.WriteFile("global.json", "{}");
		for (var index = 1; index <= 80; index++)
		{
			project.WriteFile(
				$"src/ExtremelyLongFileName{index:D3}WithHorizontalOverflow.cs",
				$"internal sealed class Marker{index:D3} {{ }}");
		}
		return project;
	}

	private static async Task ExitAsync(TerminalPtyHarness terminal)
	{
		await terminal.SendQuitAndConfirmAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				cancellationToken: TestContext.Current.CancellationToken));
	}
}
