using System.Diagnostics;
using DevProjex.Infrastructure.ProjectProfiles;

namespace DevProjex.Tests.Terminal;

[Collection(TerminalProcessCollection.Name)]
public sealed class TerminalWorkspaceCommandLinePtyTests
{
	[Fact(Timeout = 120_000)]
	public async Task CopyCommandPublishesAnInlineSuccessResult()
	{
		using var project = CreateProject();
		await using var terminal = await StartAsync(project.Path, columns: 160, rows: 36);
		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync(":copy\r", TestContext.Current.CancellationToken);
		var result = await terminal.WaitForScreenAsync(
			"Copied: Tree",
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains("characters", result, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		await QuitAsync(terminal);
	}

	[Fact(Timeout = 120_000)]
	public async Task CopyThenCjkLanguageSwitchKeepsThePublishedWorkspaceInteractive()
	{
		using var project = CreateProject();
		await using var terminal = await StartAsync(project.Path, columns: 120, rows: 30);
		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync(":copy\r", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Copied: Tree",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync(":language ja\r", TestContext.Current.CancellationToken);
		var japanese = await terminal.WaitForScreenAsync(
			"↑/↓ 移動",
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains("プロジェクトツリー", japanese, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		await QuitAsync(terminal);
	}

	[Fact(Timeout = 240_000)]
	public async Task CopyCommandRejectsAnOversizedDocumentBeforeStringMaterialization()
	{
		using var project = CreateOversizedCopyProject();
		await using var terminal = await StartAsync(project.Path, columns: 120, rows: 30);
		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			timeout: TimeSpan.FromSeconds(45),
			cancellationToken: TestContext.Current.CancellationToken);
		var peakBefore = terminal.PeakWorkingSetBytes;
		var stopwatch = Stopwatch.StartNew();

		await terminal.SendAsync(":copy content text\r", TestContext.Current.CancellationToken);
		var result = await terminal.WaitForScreenAsync(
			"Use :export instead",
			timeout: TimeSpan.FromSeconds(75),
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains("too large", result, StringComparison.OrdinalIgnoreCase);
		Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(75));
		Assert.True(
			terminal.PeakWorkingSetBytes - peakBefore < 192L * 1024 * 1024,
			"The copy path allocated memory proportional to the oversized UTF-16 payload.");
		Assert.False(terminal.HasExited);
		await QuitAsync(terminal);
	}

	[Fact(Timeout = 120_000)]
	public async Task AnalyzeCommandPublishesEveryMetricOnOneResultLine()
	{
		using var project = CreateProject();
		await using var terminal = await StartAsync(
			project.Path,
			columns: 160,
			rows: 30,
			language: "ru");
		await terminal.WaitForScreenAsync(
			"ДЕРЕВО ПРОЕКТА",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync(":analyze\r", TestContext.Current.CancellationToken);
		var result = await terminal.WaitForScreenAsync(
			"Символы",
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);
		var resultLine = Assert.Single(
			result.Split('\n'),
			static line => line.Contains("Символы", StringComparison.Ordinal));

		Assert.Contains("Примерное число токенов", resultLine, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		await QuitAsync(terminal);
	}

	[Fact(Timeout = 120_000)]
	public async Task ProfileSaveWithQuotedNamePersistsAValidProfileOutsideTheProject()
	{
		using var workspace = new TemporaryDirectory();
		var projectPath = workspace.CreateDirectory("project");
		workspace.WriteFile("project/global.json", "{}");
		workspace.WriteFile("project/src/App.cs", "namespace Sample;");
		var profilePath = Path.Combine(workspace.Path, "My Name.json");
		string? internalDataRoot = null;
		await using var terminal = await StartAsync(
			projectPath,
			columns: 160,
			rows: 36,
			initializeDataRoot: dataRoot => internalDataRoot = dataRoot);
		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync(
			":profile save \"My Name\"\r",
			TestContext.Current.CancellationToken);
		var result = await terminal.WaitForScreenAsync(
			profilePath,
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains("My Name.json", result, StringComparison.Ordinal);
		Assert.NotNull(internalDataRoot);
		Assert.False(PathUtility.IsPathInside(internalDataRoot, projectPath));
		Assert.True(File.Exists(profilePath));
		Assert.False(File.Exists(Path.Combine(projectPath, "My Name.json")));
		var profile = await new PortableProjectProfileService().LoadAsync(
			profilePath,
			TestContext.Current.CancellationToken);
		Assert.Contains(".cs", profile.Extensions ?? [], StringComparer.OrdinalIgnoreCase);
		Assert.False(terminal.HasExited);
		await QuitAsync(terminal);
	}

	[Fact(Timeout = 120_000)]
	public async Task RefreshSelectsANewExtensionAndPreservesAnExplicitDisabledExtension()
	{
		using var project = CreateProject();
		await using var terminal = await StartAsync(project.Path, columns: 160, rows: 50);
		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await ExecuteAsync(terminal, "type .md off", ".md: disabled");
		project.WriteFile("notes.md", "# Added while the workspace is open");
		project.WriteFile("config.toml", "enabled = true");

		await terminal.SendAsync(":refresh\r", TestContext.Current.CancellationToken);
		var refreshed = await terminal.WaitForScreenAsync(
			"Project refreshed.",
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.DoesNotContain("Processing request", refreshed, StringComparison.Ordinal);
		var tree = await terminal.WaitForScreenAsync(
			"config.toml",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.DoesNotContain("notes.md", tree, StringComparison.Ordinal);

		await OpenAndCancelAsync(terminal);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		var parameters = await terminal.WaitForScreenAsync(
			"[x] .toml",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("[ ] .md", parameters, StringComparison.Ordinal);
		Assert.DoesNotContain("Processing request", parameters, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		await QuitAsync(terminal);
	}

	[Fact(Timeout = 120_000)]
	public async Task QuitDoesNotWaitForTheCommandResultTimer()
	{
		using var project = CreateProject();
		await using var terminal = await StartAsync(project.Path, columns: 120, rows: 30);
		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync(":unknown-command\r", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Unknown command",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("q", TestContext.Current.CancellationToken);

		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				timeout: TimeSpan.FromSeconds(2),
				cancellationToken: TestContext.Current.CancellationToken));
	}

	[Theory(Timeout = 120_000)]
	[InlineData(160, 40, false)]
	[InlineData(100, 30, false)]
	[InlineData(100, 30, true)]
	public async Task InputUsesTheWorkspaceBackgroundWithoutAFullWidthFill(
		int columns,
		int rows,
		bool noColor)
	{
		using var project = CreateProject();
		var environment = noColor
			? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["NO_COLOR"] = "1" }
			: null;
		await using var terminal = await StartAsync(
			project.Path,
			columns,
			rows,
			environment);
		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync(":vie", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			":view",
			cancellationToken: TestContext.Current.CancellationToken);

		var commandRow = terminal.FindVisibleRow(":view");
		var metricsRow = terminal.FindVisibleRow("Files 3 · Folders 2");
		Assert.True(commandRow >= 0);
		Assert.True(metricsRow >= 0);
		AssertSameBackground(
			terminal.CaptureCellStyle(commandRow, Math.Min(columns - 2, 80)),
			terminal.CaptureCellStyle(metricsRow, Math.Min(columns - 2, 80)));

		await terminal.SendMouseClickAsync(
			column: 4,
			row: 4,
			cancellationToken: TestContext.Current.CancellationToken);
		await Task.Delay(100, TestContext.Current.CancellationToken);
		AssertSameBackground(
			terminal.CaptureCellStyle(commandRow, Math.Min(columns - 2, 80)),
			terminal.CaptureCellStyle(metricsRow, Math.Min(columns - 2, 80)));
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await QuitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task ActionPaletteReportsAnEmptyExclusionSelectionAsZero()
	{
		using var project = CreateProject();
		await using var terminal = await StartAsync(project.Path, columns: 120, rows: 30);
		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await ExecuteAsync(terminal, "all exclusions off", "All: disabled");

		await terminal.SendAsync("\u0010", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Filter actions:",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync(
			"Exclusions mini-panel",
			TestContext.Current.CancellationToken);
		var palette = await terminal.WaitForScreenAsync(
			"Focus the Exclusions mini-panel.",
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(
			palette.Split('\n').Any(static line =>
				line.Contains("Exclusions:", StringComparison.Ordinal) &&
				line.Contains('0')),
			$"The Exclusions palette row did not report zero selected rules.\n{palette}");
		Assert.DoesNotContain("none available", palette, StringComparison.OrdinalIgnoreCase);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForStableScreenAsync(
			required: "PROJECT TREE",
			forbidden: "Building preview…",
			cancellationToken: TestContext.Current.CancellationToken);
		await QuitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task CompletionEditingEscapeAndHistoryRemainResponsive()
	{
		using var project = CreateProject();
		await using var terminal = await StartAsync(project.Path, columns: 120, rows: 30);
		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync(":set ", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"<option> <on|off>",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			":set hide-secrets",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			":set hide-private-data",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			":set hide-private-data",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync(":vie", TestContext.Current.CancellationToken);
		var ghost = await terminal.WaitForScreenAsync(
			":view",
			cancellationToken: TestContext.Current.CancellationToken);
		Verify("workspace-command-ghost-en-120x30", ghost, project.Path);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendAsync(" con", TestContext.Current.CancellationToken);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"CONTEXT PREVIEW: Content",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync(":view treee", TestContext.Current.CancellationToken);
		await terminal.SendAsync("\u007f", TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"CONTEXT PREVIEW: Tree",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync(":search draft", TestContext.Current.CancellationToken);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		var canceled = await terminal.WaitForScreenWithoutAsync(
			":search draft",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("Files 3", canceled, StringComparison.Ordinal);

		await terminal.SendAsync(":", TestContext.Current.CancellationToken);
		await terminal.SendUpAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			":view tree",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await QuitAsync(terminal);
	}

	[Fact(Timeout = 120_000)]
	public async Task CommandHistoryPersistsAcrossIndependentTerminalProcesses()
	{
		using var project = CreateProject();
		using var settings = new TemporaryDirectory();
		var environment = CreateSharedSettingsEnvironment(settings.Path);

		await using (var first = await StartAsync(
			             project.Path,
			             columns: 120,
			             rows: 30,
			             environment))
		{
			await first.WaitForScreenAsync(
				"PROJECT TREE",
				cancellationToken: TestContext.Current.CancellationToken);
			await first.SendAsync(":view content\r", TestContext.Current.CancellationToken);
			await first.WaitForScreenAsync(
				"CONTEXT PREVIEW: Content",
				cancellationToken: TestContext.Current.CancellationToken);
			await QuitAsync(first);
		}

		await using var second = await StartAsync(
			project.Path,
			columns: 120,
			rows: 30,
			environment);
		await second.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await second.SendAsync(":", TestContext.Current.CancellationToken);
		await second.WaitForScreenAsync(
			":set",
			cancellationToken: TestContext.Current.CancellationToken);
		await second.SendUpAsync(TestContext.Current.CancellationToken);
		await second.WaitForScreenAsync(
			":quit",
			cancellationToken: TestContext.Current.CancellationToken);
		await second.SendUpAsync(TestContext.Current.CancellationToken);
		await second.WaitForScreenAsync(
			":view content",
			cancellationToken: TestContext.Current.CancellationToken);
		await second.SendEscapeAsync(TestContext.Current.CancellationToken);
		await QuitAsync(second);
	}

	[Fact(Timeout = 240_000)]
	public async Task LanguageSwitchRefreshesTheWorkspaceAndHonorsStartupPrecedence()
	{
		using var project = CreateProject();
		using var settings = new TemporaryDirectory();
		var environment = CreateSharedSettingsEnvironment(settings.Path);

		await using (var first = await StartAsync(
			             project.Path,
			             columns: 160,
			             rows: 36,
			             environment,
			             language: "en"))
		{
			await first.WaitForScreenAsync(
				"PROJECT TREE",
				cancellationToken: TestContext.Current.CancellationToken);

			await first.SendAsync(":language\r", TestContext.Current.CancellationToken);
			var overview = await first.WaitForScreenAsync(
				"Current language: en",
				cancellationToken: TestContext.Current.CancellationToken);
			foreach (var code in CliChoiceSets.Language.Tokens)
				Assert.Contains(code, overview, StringComparison.Ordinal);
			await first.SendEscapeAsync(TestContext.Current.CancellationToken);

			await first.SendAsync(":language klingon\r", TestContext.Current.CancellationToken);
			var error = await first.WaitForScreenAsync(
				"Unknown language code 'klingon'",
				cancellationToken: TestContext.Current.CancellationToken);
			Assert.Contains("en ru de", error, StringComparison.Ordinal);

			await first.SendAsync(":language ja\r", TestContext.Current.CancellationToken);
			await first.WaitForScreenAsync(
				"言語をjaに切り替えました。",
				cancellationToken: TestContext.Current.CancellationToken);
			var japaneseWorkspace = await first.WaitForScreenAsync(
				"↑/↓ 移動",
				timeout: TimeSpan.FromSeconds(10),
				cancellationToken: TestContext.Current.CancellationToken);
			Assert.Contains("プロジェクトツリー", japaneseWorkspace, StringComparison.Ordinal);
			Assert.Contains("コンテンツ処理", japaneseWorkspace, StringComparison.Ordinal);
			await first.SendAsync(":language ", TestContext.Current.CancellationToken);
			await first.WaitForScreenAsync(
				"[コード]",
				cancellationToken: TestContext.Current.CancellationToken);
			await first.SendEscapeAsync(TestContext.Current.CancellationToken);

			await first.SendAsync(":help\r", TestContext.Current.CancellationToken);
			var help = await first.WaitForScreenAsync(
				"ワークスペースコマンド",
				cancellationToken: TestContext.Current.CancellationToken);
			Assert.Contains("プロジェクトツリー", help, StringComparison.Ordinal);
			await first.SendEscapeAsync(TestContext.Current.CancellationToken);
			await first.SendAsync("\u0010", TestContext.Current.CancellationToken);
			await first.WaitForScreenAsync(
				"アクションパレット",
				cancellationToken: TestContext.Current.CancellationToken);
			await first.SendAsync("言語を切り替える", TestContext.Current.CancellationToken);
			var palette = await first.WaitForScreenAsync(
				"language [code]",
				cancellationToken: TestContext.Current.CancellationToken);
			Assert.Contains("言語を切り替える", palette, StringComparison.Ordinal);
			await first.SendEscapeAsync(TestContext.Current.CancellationToken);
			await QuitAsync(first);
		}

		await using (var persisted = await StartAsync(
			             project.Path,
			             columns: 160,
			             rows: 36,
			             environment,
			             language: null))
		{
			await persisted.WaitForScreenAsync(
				"プロジェクトツリー",
				cancellationToken: TestContext.Current.CancellationToken);
			await QuitAsync(persisted);
		}

		await using (var overridden = await StartAsync(
			             project.Path,
			             columns: 160,
			             rows: 36,
			             environment,
			             language: "ru"))
		{
			var russianWorkspace = await overridden.WaitForScreenAsync(
				"ДЕРЕВО ПРОЕКТА",
				cancellationToken: TestContext.Current.CancellationToken);
			Assert.DoesNotContain("プロジェクトツリー", russianWorkspace, StringComparison.Ordinal);
			await QuitAsync(overridden);
		}

		await using var restored = await StartAsync(
			project.Path,
			columns: 160,
			rows: 36,
			environment,
			language: null);
		await restored.WaitForScreenAsync(
			"プロジェクトツリー",
			cancellationToken: TestContext.Current.CancellationToken);
		await QuitAsync(restored);
	}

	[Fact(Timeout = 120_000)]
	public async Task LanguageCommandDoesNotDelayExitWhenTheSettingsLockIsBusy()
	{
		using var project = CreateProject();
		FileStream? heldLock = null;
		try
		{
			await using var terminal = await StartAsync(
				project.Path,
				columns: 120,
				rows: 30,
				initializeDataRoot: dataRoot =>
				{
					var store = new TerminalSettingsStore(() => dataRoot);
					Directory.CreateDirectory(Path.GetDirectoryName(store.GetPath())!);
					heldLock = new FileStream(
						store.GetPath() + ".lock",
						FileMode.OpenOrCreate,
						FileAccess.ReadWrite,
						FileShare.None);
				});
			await terminal.WaitForScreenAsync(
				"PROJECT TREE",
				cancellationToken: TestContext.Current.CancellationToken);
			await terminal.SendAsync(":language ja\r", TestContext.Current.CancellationToken);
			await terminal.WaitForScreenAsync(
				"言語をjaに切り替えました。",
				cancellationToken: TestContext.Current.CancellationToken);

			var stopwatch = Stopwatch.StartNew();
			await terminal.SendAsync(":quit\r", TestContext.Current.CancellationToken);
			var exitCode = await terminal.WaitForExitAsync(
				timeout: TimeSpan.FromSeconds(5),
				cancellationToken: TestContext.Current.CancellationToken);

			Assert.Equal(CommandLineExitCodes.Success, exitCode);
			Assert.True(
				stopwatch.Elapsed < TimeSpan.FromSeconds(3),
				$"TUI exit waited {stopwatch.Elapsed} for best-effort settings persistence.");
		}
		finally
		{
			heldLock?.Dispose();
		}
	}

	[Fact(Timeout = 180_000)]
	public async Task EveryVerbExecutesInOneLiveWorkspaceWithoutStateDivergence()
	{
		using var project = CreateProject();
		using var output = new TemporaryDirectory();
		var destination = Path.Combine(output.Path, "command context.md");
		await using var terminal = await StartAsync(project.Path, columns: 160, rows: 40);
		await terminal.WaitForScreenAsync(
			"Content processing",
			cancellationToken: TestContext.Current.CancellationToken);

		await ExecuteAsync(terminal, "set hide-secrets on", "Hide secrets: enabled");
		Assert.Contains(
			"[x] Hide secrets",
			terminal.CaptureScreen(),
			StringComparison.Ordinal);
		await ExecuteAsync(terminal, "set smart-ignore off", "Smart ignore: disabled");
		await ExecuteAsync(terminal, "set gitignore off", "Use .gitignore: disabled");
		await ExecuteAsync(terminal, "all content off", "All: disabled");
		await ExecuteAsync(terminal, "type .cs off", ".cs: disabled");
		await ExecuteAsync(terminal, "all types off", "All: disabled");
		await terminal.WaitForScreenAsync(
			"No visible items",
			cancellationToken: TestContext.Current.CancellationToken);
		await ExecuteAsync(terminal, "all types on", "All: enabled");
		await terminal.WaitForScreenAsync(
			"App.cs",
			cancellationToken: TestContext.Current.CancellationToken);
		await ExecuteAsync(terminal, "all exclusions off", "All: disabled");
		await ExecuteAsync(terminal, "view content", "CONTEXT PREVIEW: Content");
		await ExecuteAsync(terminal, "format json", "Preview format: JSON");
		await ExecuteAsync(terminal, "search command", "Preview search: command");
		await ExecuteAsync(terminal, "search", "Preview search cleared");
		await ExecuteAsync(terminal, "filter src", "Tree filter: src");
		await ExecuteAsync(terminal, "filter", "Tree filter cleared");

		await terminal.SendAsync(":help set\r", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"set <option> <on|off>",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);

		await terminal.SendAsync(
			$":export context markdown \"{destination}\"\r",
			TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Export?",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Export completed",
			timeout: TimeSpan.FromSeconds(45),
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.True(File.Exists(destination));
		Assert.Contains("App.cs", File.ReadAllText(destination), StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		await QuitAsync(terminal);
	}

	[Fact(Timeout = 180_000)]
	public async Task FullCommandJourneyExportsRedactedContextAndRestoresAnEmptyTree()
	{
		using var project = CreateProject();
		using var output = new TemporaryDirectory();
		var destination = Path.Combine(output.Path, "redacted context.md");
		await using var terminal = await StartAsync(project.Path, columns: 160, rows: 40);
		await terminal.WaitForScreenAsync(
			"Content processing",
			cancellationToken: TestContext.Current.CancellationToken);

		await ExecuteAsync(terminal, "set hide-secrets on", "Hide secrets: enabled");
		await ExecuteAsync(terminal, "set hide-private-data on", "Hide private data: enabled");
		await ExecuteAsync(terminal, "view content", "CONTEXT PREVIEW: Content");
		await terminal.SendAsync(
			$":export context markdown \"{destination}\"\r",
			TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Export?",
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Export completed",
			timeout: TimeSpan.FromSeconds(45),
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(File.Exists(destination));
		var exported = File.ReadAllText(destination);
		Assert.Contains("App.cs", exported, StringComparison.Ordinal);
		Assert.DoesNotContain(
			"ghp_a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL",
			exported,
			StringComparison.Ordinal);

		await ExecuteAsync(terminal, "all types off", "All: disabled");
		await terminal.WaitForScreenAsync(
			"No visible items",
			cancellationToken: TestContext.Current.CancellationToken);
		await ExecuteAsync(terminal, "all types on", "All: enabled");
		await terminal.WaitForScreenAsync(
			"App.cs",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);
		await QuitAsync(terminal);
	}

	[Fact(Timeout = 120_000)]
	public async Task CjkArgumentEditingKeepsExactCommandText()
	{
		using var project = CreateProject();
		await using var terminal = await StartAsync(project.Path, columns: 100, rows: 30);
		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync(":search 本語", TestContext.Current.CancellationToken);
		await terminal.SendHomeAsync(TestContext.Current.CancellationToken);
		for (var index = 0; index < "search ".Length; index++)
			await terminal.SendRightAsync(TestContext.Current.CancellationToken);
		await terminal.SendAsync("日", TestContext.Current.CancellationToken);
		await terminal.SendEndAsync(TestContext.Current.CancellationToken);
		await terminal.SendAsync("x\u007f", TestContext.Current.CancellationToken);

		var edited = await terminal.WaitForScreenAsync(
			":search 日本語",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.DoesNotContain(":search 日本語x", edited, StringComparison.Ordinal);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Preview search: 日本語",
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.False(terminal.HasExited);
		await QuitAsync(terminal);
	}

	[Fact(Timeout = 120_000)]
	public async Task ActivationWorksFromEveryPaneAndSurvivesResizeAndPlainMode()
	{
		using var project = CreateProject();
		await using var terminal = await StartAsync(project.Path, columns: 160, rows: 40);
		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);

		await OpenAndCancelAsync(terminal);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> CONTEXT PREVIEW",
			cancellationToken: TestContext.Current.CancellationToken);
		await OpenAndCancelAsync(terminal);
		await terminal.SendTabAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"> PARAMETERS",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync(":search unicode-данные", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			":search unicode-данные",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.ResizeAsync(80, 24, TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			":search unicode-данные",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.ResizeAsync(160, 40, TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			":search unicode-данные",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await QuitAsync(terminal);

		await using var plain = await StartAsync(
			project.Path,
			columns: 100,
			rows: 30,
			plain: true);
		await plain.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await plain.SendAsync(":set ", TestContext.Current.CancellationToken);
		var plainGhost = await plain.WaitForScreenAsync(
			"[<option> <on|off>]",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.DoesNotContain('✓', plainGhost);
		await plain.SendEscapeAsync(TestContext.Current.CancellationToken);
		await QuitAsync(plain);
	}

	[Fact(Timeout = 90_000)]
	public async Task TooSmallLayoutDoesNotOpenTheCommandLine()
	{
		using var project = CreateProject();
		await using var terminal = await StartAsync(project.Path, columns: 59, rows: 19);
		await terminal.WaitForScreenAsync(
			"Terminal too small",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync(":set hide-secrets on", TestContext.Current.CancellationToken);
		await Task.Delay(250, TestContext.Current.CancellationToken);
		var screen = terminal.CaptureScreen();
		Assert.Contains("Terminal too small", screen, StringComparison.Ordinal);
		Assert.DoesNotContain(":set hide-secrets on", screen, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);

		await terminal.ResizeAsync(80, 24, TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);
		await QuitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task PastedCommandPacketIsAcceptedWithoutDroppingCharacters()
	{
		using var project = CreateProject();
		await using var terminal = await StartAsync(project.Path, columns: 120, rows: 30);
		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync(
			":view content\r",
			TestContext.Current.CancellationToken);

		var result = await terminal.WaitForScreenAsync(
			"CONTEXT PREVIEW: Content",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("CONTEXT PREVIEW: Content", result, StringComparison.Ordinal);
		await terminal.WaitForScreenAsync(
			"ghp_a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);
		await QuitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task CommandLineExecutesSettingsAndReportsStrictTokenErrors()
	{
		using var project = CreateProject();
		await using var terminal = await StartAsync(project.Path, columns: 160, rows: 40);
		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync(":", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			":set",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync("set hide-secrets on", TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var enabled = await terminal.WaitForScreenAsync(
			"Hide secrets: enabled",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("[x] Hide secrets", enabled, StringComparison.Ordinal);
		var applied = await terminal.WaitForStableScreenAsync(
			"Hide secrets (1): enabled",
			forbidden: "Building preview…",
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);
		Verify("workspace-command-result-en-160x40", applied, project.Path);

		await terminal.SendAsync(":", TestContext.Current.CancellationToken);
		await terminal.SendAsync("set hide-secret on", TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		var error = await terminal.WaitForScreenAsync(
			"Similar: hide-secrets",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("Unknown token 'hide-secret'", error, StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		Verify("workspace-command-error-en-160x40", error, project.Path);

		await terminal.SendAsync(":", TestContext.Current.CancellationToken);
		await terminal.SendAsync("quit", TestContext.Current.CancellationToken);
		await terminal.SendEnterAsync(TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				timeout: TimeSpan.FromSeconds(30),
				cancellationToken: TestContext.Current.CancellationToken));
	}

	[Fact(Timeout = 150_000)]
	public async Task ContentCommandsAndGitPairFollowTheSameTransitionsAsThePanel()
	{
		using var project = CreateGitProject();
		await using var terminal = await StartAsync(project.Path, columns: 160, rows: 40);
		await terminal.WaitForScreenAsync(
			"Content processing",
			cancellationToken: TestContext.Current.CancellationToken);

		var contentOptions = new (string Token, string Label)[]
		{
			("hide-secrets", "Hide secrets"),
			("hide-private-data", "Hide private data"),
			("compress-code", "Compress code"),
			("strip-comments", "Strip comments"),
			("strip-blank-lines", "Strip blank lines")
		};
		foreach (var (token, label) in contentOptions)
		{
			await ExecuteAsync(terminal, $"set {token} on", $"{label}: enabled");
			Assert.Contains($"[x] {label}", terminal.CaptureScreen(), StringComparison.Ordinal);
			await ExecuteAsync(terminal, $"set {token} off", $"{label}: disabled");
			Assert.Contains($"[ ] {label}", terminal.CaptureScreen(), StringComparison.Ordinal);
		}

		await ExecuteAsync(terminal, "all content on", "All: enabled");
		foreach (var (_, label) in contentOptions)
			Assert.Contains($"[x] {label}", terminal.CaptureScreen(), StringComparison.Ordinal);
		await ExecuteAsync(terminal, "all content off", "All: disabled");
		foreach (var (_, label) in contentOptions)
			Assert.Contains($"[ ] {label}", terminal.CaptureScreen(), StringComparison.Ordinal);

		await ExecuteAsync(terminal, "set gitignore on", "Use .gitignore: enabled");
		Assert.Contains("[x] Use .gitignore", terminal.CaptureScreen(), StringComparison.Ordinal);
		await ExecuteAsync(terminal, "set tracked on", "Tracked Git files only: enabled");
		var tracked = terminal.CaptureScreen();
		Assert.Contains("[ ] Use .gitignore", tracked, StringComparison.Ordinal);
		Assert.Contains("[x] Tracked Git files only", tracked, StringComparison.Ordinal);
		await ExecuteAsync(terminal, "set tracked off", "Tracked Git files only: disabled");
		var disabled = terminal.CaptureScreen();
		Assert.Contains("[ ] Use .gitignore", disabled, StringComparison.Ordinal);
		Assert.Contains("[ ] Tracked Git files only", disabled, StringComparison.Ordinal);
		await ExecuteAsync(terminal, "set gitignore off", "Use .gitignore: disabled");
		Assert.Contains("[ ] Use .gitignore", terminal.CaptureScreen(), StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		await QuitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task CommandExecutionFailureStaysInlineAndLeavesTheWorkspaceResponsive()
	{
		using var project = CreateProject();
		var unsafeDestination = Path.Combine(project.Path, "unsafe-context.md");
		await using var terminal = await StartAsync(project.Path, columns: 160, rows: 40);
		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync(
			$":export context markdown \"{unsafeDestination}\"\r",
			TestContext.Current.CancellationToken);
		var failure = await terminal.WaitForScreenAsync(
			"DPX-EXPORT-UNSAFE-DESTINATION",
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.DoesNotContain("┌┤Error├", failure, StringComparison.Ordinal);
		Assert.False(File.Exists(unsafeDestination));
		Assert.False(terminal.HasExited);
		await ExecuteAsync(terminal, "view content", "CONTEXT PREVIEW: Content");
		await QuitAsync(terminal);
	}

	[Fact(Timeout = 90_000)]
	public async Task ActiveOverlayBlocksCommandActivationAndNoColorModeRemainsUsable()
	{
		using var project = CreateProject();
		var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			["NO_COLOR"] = "1"
		};
		await using var terminal = await StartAsync(
			project.Path,
			columns: 160,
			rows: 40,
			environment);
		await terminal.WaitForScreenAsync(
			"PROJECT TREE",
			cancellationToken: TestContext.Current.CancellationToken);

		await terminal.SendAsync(":help set\r", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			"Workspace commands",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendAsync(":", TestContext.Current.CancellationToken);
		await Task.Delay(250, TestContext.Current.CancellationToken);
		var overlay = terminal.CaptureScreen();
		Assert.Contains("Workspace commands", overlay, StringComparison.Ordinal);

		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		var workspace = await terminal.WaitForScreenWithoutAsync(
			"Workspace commands",
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains("[ ] Hide secrets", workspace, StringComparison.Ordinal);
		await ExecuteAsync(terminal, "set hide-secrets on", "Hide secrets: enabled");
		Assert.Contains("[x] Hide secrets", terminal.CaptureScreen(), StringComparison.Ordinal);
		Assert.False(terminal.HasExited);
		await QuitAsync(terminal);
	}

	private static Task<TerminalPtyHarness> StartAsync(
		string projectPath,
		int columns,
		int rows,
		IReadOnlyDictionary<string, string>? environment = null,
		bool plain = false,
		string? language = "en",
		Action<string>? initializeDataRoot = null)
	{
		var arguments = new List<string>
		{
			"tui",
			projectPath,
			"--profile",
			"standard",
			"--screen",
			"inline",
			"--no-mouse"
		};
		if (language is not null)
		{
			arguments.Add("--language");
			arguments.Add(language);
		}
		if (plain)
			arguments.Add("--plain");
		return TerminalPtyHarness.StartAsync(
			projectPath,
			arguments,
			columns,
			rows,
			environment,
			cancellationToken: TestContext.Current.CancellationToken,
			initializeDataRoot: initializeDataRoot);
	}

	private static TemporaryDirectory CreateProject()
	{
		var project = new TemporaryDirectory();
		project.WriteFile("global.json", "{}");
		project.WriteFile(
			"src/App.cs",
			"const string ApiKey = \"ghp_a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL\";");
		project.WriteFile("readme.md", "# Command line test");
		return project;
	}

	private static TemporaryDirectory CreateOversizedCopyProject()
	{
		var project = new TemporaryDirectory();
		project.WriteFile("global.json", "{}");
		var path = Path.Combine(project.Path, "oversized.txt");
		var targetBytes = TerminalWorkspaceController.MaximumClipboardPayloadBytes /
		                  sizeof(char) + 1024;
		var buffer = Enumerable.Repeat((byte)'x', 80 * 1024).ToArray();
		for (var index = 4095; index < buffer.Length; index += 4096)
			buffer[index] = (byte)'\n';
		using var stream = new FileStream(
			path,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.None,
			buffer.Length,
			FileOptions.SequentialScan);
		for (long written = 0; written < targetBytes;)
		{
			var count = (int)Math.Min(buffer.Length, targetBytes - written);
			stream.Write(buffer, 0, count);
			written += count;
		}
		return project;
	}

	private static TemporaryDirectory CreateGitProject()
	{
		var project = CreateProject();
		RunGit(project.Path, "init", "--initial-branch=main");
		RunGit(project.Path, "config", "user.email", "terminal-tests@devprojex.local");
		RunGit(project.Path, "config", "user.name", "DevProjex Terminal Tests");
		RunGit(project.Path, "add", ".");
		RunGit(project.Path, "commit", "-m", "Initial test project");
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
		Assert.True(
			result.ExitCode == 0,
			$"git {string.Join(' ', arguments)} failed with exit code {result.ExitCode}.\n" +
			$"{result.StandardOutput}\n{result.StandardError}");
	}

	private static async Task QuitAsync(TerminalPtyHarness terminal)
	{
		await terminal.SendAsync(":quit\r", TestContext.Current.CancellationToken);
		Assert.Equal(
			CommandLineExitCodes.Success,
			await terminal.WaitForExitAsync(
				timeout: TimeSpan.FromSeconds(30),
				cancellationToken: TestContext.Current.CancellationToken));
	}

	private static async Task ExecuteAsync(
		TerminalPtyHarness terminal,
		string command,
		string expected)
	{
		await terminal.SendAsync($":{command}\r", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			expected,
			timeout: TimeSpan.FromSeconds(30),
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(terminal.HasExited);
	}

	private static async Task OpenAndCancelAsync(TerminalPtyHarness terminal)
	{
		await terminal.SendAsync(":", TestContext.Current.CancellationToken);
		await terminal.WaitForScreenAsync(
			":set",
			cancellationToken: TestContext.Current.CancellationToken);
		await terminal.SendEscapeAsync(TestContext.Current.CancellationToken);
		await terminal.WaitForScreenWithoutAsync(
			":set",
			cancellationToken: TestContext.Current.CancellationToken);
	}

	private static IReadOnlyDictionary<string, string> CreateSharedSettingsEnvironment(
		string root) => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		["DEVPROJEX_INTERNAL_DATA_ROOT"] = Path.Combine(root, "devprojex"),
		["XDG_CONFIG_HOME"] = Path.Combine(root, "config"),
		["XDG_DATA_HOME"] = Path.Combine(root, "data"),
		["XDG_CACHE_HOME"] = Path.Combine(root, "cache"),
		["APPDATA"] = Path.Combine(root, "roaming"),
		["LOCALAPPDATA"] = Path.Combine(root, "local")
	};

	private static void Verify(string name, string screen, string projectPath) =>
		TerminalScreenSnapshot.Verify(
			name,
			screen,
			(projectPath, "<PROJECT_ROOT>"));

	private static void AssertSameBackground(TerminalCellStyle actual, TerminalCellStyle expected)
	{
		Assert.Equal(expected.BackgroundMode, actual.BackgroundMode);
		Assert.Equal(expected.Background, actual.Background);
		Assert.Equal(expected.Inverse, actual.Inverse);
	}
}
