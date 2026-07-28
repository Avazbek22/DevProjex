using System.Xml.Linq;
using DevProjex.Application.Preview;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalWorkspaceContractTests
{
	[Theory]
	[InlineData(true, "dotnet")]
	[InlineData(false, null)]
	public void TerminalDriverSelectionAvoidsUnsafeDarwinAnsiRawMode(
		bool isMacOs,
		string? expected)
	{
		Assert.Equal(expected, TerminalWorkspace.SelectTerminalDriver(isMacOs));
	}

	[Fact]
	public void CanceledTextDialogNeverReturnsItsSuggestedValue()
	{
		Assert.Null(TerminalWorkspace.CompletePrompt(
			accepted: false,
			"C:\\Exports\\project.zip"));
		Assert.Equal(
			"C:\\Exports\\project.zip",
			TerminalWorkspace.CompletePrompt(
				accepted: true,
				"C:\\Exports\\project.zip"));
	}

	[Fact]
	public void WelcomeNeverOffersFilesystemRootOrUserHomeForImplicitScanning()
	{
		var root = Path.GetPathRoot(Path.GetFullPath(Environment.CurrentDirectory))!;
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

		Assert.False(TerminalWelcomePolicy.IsSafeProjectWorkspace(root));
		if (!string.IsNullOrWhiteSpace(home))
			Assert.False(TerminalWelcomePolicy.IsSafeProjectWorkspace(home));
	}

	[Fact]
	public void WelcomeRecognizesProjectMarkerAndRetainsUnavailableRecentEntries()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("package.json", "{}");
		workspace.WriteFile("nested/deep/file.cs", "class C {}");
		var unavailable = Path.Combine(workspace.Path, "not-a-directory");

		var context = TerminalWelcomePolicy.Create(
			workspace.Path,
			[workspace.Path, Path.Combine(workspace.Path, "."), unavailable]);

		Assert.True(context.CanOpenCurrentDirectory);
		Assert.Equal([Path.GetFullPath(unavailable)], context.RecentProjects);
	}

	[Fact]
	public void MarkerlessDirectoryRequiresExplicitBrowseChoice()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("notes.txt", "plain directory");

		Assert.False(TerminalWelcomePolicy.IsSafeProjectWorkspace(workspace.Path));
	}

	[Theory]
	[InlineData(TerminalScreenMode.Inline, false, false, TerminalScreenMode.Inline)]
	[InlineData(TerminalScreenMode.Alternate, true, true, TerminalScreenMode.Alternate)]
	[InlineData(TerminalScreenMode.Auto, true, false, TerminalScreenMode.Inline)]
	[InlineData(TerminalScreenMode.Auto, false, true, TerminalScreenMode.Inline)]
	[InlineData(TerminalScreenMode.Auto, false, false, TerminalScreenMode.Alternate)]
	public void ScreenModeUsesExplicitChoiceAndSafeMultiplexerFallback(
		TerminalScreenMode requested,
		bool tmux,
		bool ci,
		TerminalScreenMode expected)
	{
		var environment = new TestTerminalEnvironment
		{
			IsCi = ci,
			Variables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
			{
				["TMUX"] = tmux ? "session" : null
			}
		};

		Assert.Equal(expected, TerminalScreenModeResolver.Resolve(requested, environment));
	}

	[Fact]
	public async Task TuiPreviewIsBoundedWhileDirectDocumentRemainsComplete()
	{
		using var workspace = new TemporaryDirectory();
		for (var index = 0; index < 100; index++)
			workspace.WriteFile($"src/file-{index:D3}.txt", new string('x', 64));

		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		var state = await controller.OpenAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);

		await controller.RefreshPreviewAsync(
			state,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Json,
			TestContext.Current.CancellationToken);
		using var bounded = JsonDocument.Parse(state.PreviewText);
		Assert.True(bounded.RootElement.GetProperty("truncated").GetBoolean());
		Assert.Equal(80, bounded.RootElement.GetProperty("files").GetArrayLength());

		var completePayload = await services.ContextDocumentService.BuildAsync(
			state.Plan,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Json,
			TestContext.Current.CancellationToken);
		using var complete = JsonDocument.Parse(completePayload);
		Assert.Equal(100, complete.RootElement.GetProperty("files").GetArrayLength());
		Assert.False(complete.RootElement.TryGetProperty("truncated", out _));
	}

	[Fact]
	public async Task PreviewViewAndFormatChangesProduceValidIndependentDocuments()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/app.cs", "class App {}");
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		var state = await controller.OpenAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);

		await controller.RefreshPreviewAsync(
			state,
			ProjectContextView.Tree,
			ProjectContextDocumentFormat.Json,
			TestContext.Current.CancellationToken);
		using (var tree = JsonDocument.Parse(state.PreviewText))
		{
			Assert.NotEqual(JsonValueKind.Null, tree.RootElement.GetProperty("tree").ValueKind);
			Assert.Equal(0, tree.RootElement.GetProperty("files").GetArrayLength());
		}

		await controller.RefreshPreviewAsync(
			state,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Xml,
			TestContext.Current.CancellationToken);
		var content = XDocument.Parse(state.PreviewText);
		Assert.Empty(content.Descendants("tree").Elements());
		Assert.Single(content.Descendants("file"));
	}

	[Fact]
	public async Task ReadableLargePreviewIsFileBackedAndIndexesFirstMiddleAndFinalFiles()
	{
		using var workspace = new TemporaryDirectory();
		const int fileCount = 120;
		for (var index = 0; index < fileCount; index++)
		{
			workspace.WriteFile(
				$"src/file-{index:D3}.txt",
				$"marker-{index:D3}\n{new string((char)('a' + index % 26), 6_000)}");
		}

		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);

		await controller.RefreshPreviewAsync(
			state,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Markdown,
			TerminalPreviewPresentation.Readable,
			TestContext.Current.CancellationToken);

		var document = Assert.IsType<FileBackedPreviewTextDocument>(state.PreviewDocument);
		Assert.Equal(fileCount, document.Sections.Count);
		Assert.True(document.CharacterCount > 500_000);
		Assert.Contains(
			"marker-000",
			document.GetLineRangeText(
				document.Sections[0].ContentStartLine,
				document.Sections[0].EndLine),
			StringComparison.Ordinal);
		Assert.Contains(
			"marker-060",
			document.GetLineRangeText(
				document.Sections[60].ContentStartLine,
				document.Sections[60].EndLine),
			StringComparison.Ordinal);
		Assert.Contains(
			"marker-119",
			document.GetLineRangeText(
				document.Sections[^1].ContentStartLine,
				document.Sections[^1].EndLine),
			StringComparison.Ordinal);

		var view = new TerminalVirtualizedPreviewView();
		try
		{
			view.SetDocument(document, preserveViewport: false);
			var match = view.FindNext("marker-119", startLine: 0);
			Assert.True(match >= document.Sections[^1].ContentStartLine - 1);
		}
		finally
		{
			view.Dispose();
		}
	}

	[Fact]
	public async Task ReadablePreviewMakesBinaryAndOversizedTextOmissionsExplicit()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/large.txt", new string('x', 10 * 1024 * 1024 + 1));
		var binaryPath = Path.Combine(workspace.Path, "src", "binary.dat");
		Directory.CreateDirectory(Path.GetDirectoryName(binaryPath)!);
		File.WriteAllBytes(binaryPath, [0, 1, 2, 3]);
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);

		await controller.RefreshPreviewAsync(
			state,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Markdown,
			TerminalPreviewPresentation.Readable,
			TestContext.Current.CancellationToken);

		Assert.Equal(2, state.PreviewDocument.Sections.Count);
		Assert.Contains(
			"[Binary or unreadable file; content omitted]",
			state.PreviewText,
			StringComparison.Ordinal);
		Assert.Contains(
			"[Large text file; open Raw output or export to inspect complete content]",
			state.PreviewText,
			StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(ProjectContextDocumentFormat.Text)]
	[InlineData(ProjectContextDocumentFormat.Markdown)]
	[InlineData(ProjectContextDocumentFormat.Json)]
	[InlineData(ProjectContextDocumentFormat.Xml)]
	public async Task RawPreviewMatchesCompleteExportPayload(
		ProjectContextDocumentFormat format)
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/app.cs", "class App {}");
		workspace.WriteFile("README.md", "# App");
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);

		await controller.RefreshPreviewAsync(
			state,
			ProjectContextView.TreeContent,
			format,
			TerminalPreviewPresentation.RawOutput,
			TestContext.Current.CancellationToken);
		var exported = await services.ContextDocumentService.BuildAsync(
			state.Plan,
			ProjectContextView.TreeContent,
			format,
			TestContext.Current.CancellationToken);

		Assert.Equal(exported, state.PreviewText);
	}

	[Fact]
	public async Task LargeRawPreviewStreamsToFileAndKeepsFinalFileReachable()
	{
		using var workspace = new TemporaryDirectory();
		const int fileCount = 120;
		for (var index = 0; index < fileCount; index++)
		{
			workspace.WriteFile(
				$"src/raw-{index:D3}.txt",
				$"raw-marker-{index:D3}\n{new string((char)('a' + index % 26), 6_000)}");
		}

		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);

		await controller.RefreshPreviewAsync(
			state,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Markdown,
			TerminalPreviewPresentation.RawOutput,
			TestContext.Current.CancellationToken);

		var document = Assert.IsType<FileBackedPreviewTextDocument>(state.PreviewDocument);
		Assert.True(document.CharacterCount > 500_000);
		Assert.Contains(
			"raw-marker-119",
			document.GetLineRangeText(
				Math.Max(1, document.LineCount - 8),
				document.LineCount),
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task TuiContextExportRejectsDestinationInsideProjectBeforeCreatingPayload()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/app.cs", "class App {}");
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		var state = await controller.OpenAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);
		var destination = Path.Combine(workspace.Path, "context.md");

		var exception = await Assert.ThrowsAsync<ProjectCopyExportException>(() =>
			controller.ExportContextAsync(
				state,
				ProjectContextView.TreeContent,
				ProjectContextDocumentFormat.Markdown,
				destination,
				overwrite: false,
				TestContext.Current.CancellationToken));

		Assert.Equal(ProjectCopyExportError.DestinationInsideSource, exception.Error);
		Assert.False(File.Exists(destination));
		Assert.Empty(Directory.EnumerateFiles(workspace.Path, ".*.tmp"));
	}

	[Fact]
	public async Task TuiContextExportUsesExactConflictPolicy()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/app.cs", "class App {}");
		var destination = workspace.WriteFile("output/context.md", "existing");
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		var state = await controller.OpenAsync(
			project,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);

		await Assert.ThrowsAsync<OutputDestinationConflictException>(() =>
			controller.ExportContextAsync(
				state,
				ProjectContextView.TreeContent,
				ProjectContextDocumentFormat.Markdown,
				destination,
				overwrite: false,
				TestContext.Current.CancellationToken));

		Assert.Equal("existing", File.ReadAllText(destination));
	}

	[Fact]
	public async Task PreparedContextExportSummarizesCurrentSelectionWithoutWriting()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/app.cs", "class App {}");
		workspace.WriteFile("project/README.md", "# App");
		var destination = Path.Combine(workspace.CreateDirectory("output"), "context.json");
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		var state = await controller.OpenAsync(
			project,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);

		var summary = await controller.PrepareContextExportAsync(
			state,
			ProjectContextView.TreeContent,
			ProjectContextDocumentFormat.Json,
			destination,
			overwrite: false,
			TestContext.Current.CancellationToken);

		Assert.Equal(TerminalExportKind.Context, summary.Kind);
		Assert.Equal(ProjectContextView.TreeContent, summary.View);
		Assert.Equal(ProjectContextDocumentFormat.Json, summary.DocumentFormat);
		Assert.Equal(TerminalExportDestinationState.Ready, summary.DestinationState);
		Assert.Equal(2, summary.FileCount);
		Assert.True(summary.FolderCount >= 2);
		Assert.Equal(
			new FileInfo(Path.Combine(project, "src", "app.cs")).Length +
			new FileInfo(Path.Combine(project, "README.md")).Length,
			summary.Bytes);
		Assert.True(summary.Characters > 0);
		Assert.True(summary.EstimatedTokens > 0);
		Assert.False(File.Exists(destination));
	}

	[Fact]
	public async Task PreparedExportReportsConflictAndLocalizedSummaryWithoutOverwriting()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/app.cs", "class App {}");
		var destination = workspace.WriteFile("output/context.md", "existing");
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.Ru);
		var environment = new TestTerminalEnvironment();
		var controller = new TerminalWorkspaceController(services, environment);
		var state = await controller.OpenAsync(
			project,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);

		var summary = await controller.PrepareContextExportAsync(
			state,
			ProjectContextView.Tree,
			ProjectContextDocumentFormat.Markdown,
			destination,
			overwrite: false,
			TestContext.Current.CancellationToken);
		var text = new TerminalWorkspace(services, environment).BuildExportSummaryText(summary);

		Assert.Equal(TerminalExportDestinationState.Conflict, summary.DestinationState);
		Assert.Contains("Сводка", services.Localization["Terminal.Tui.ExportSummary"], StringComparison.Ordinal);
		Assert.Contains("Конфликт", text, StringComparison.Ordinal);
		Assert.Contains(Path.GetFullPath(destination), text, StringComparison.Ordinal);
		Assert.Equal("existing", File.ReadAllText(destination));
	}

	[Fact]
	public async Task TuiProjectExportReportsMeasuredEntryProgress()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/app.cs", "class App {}");
		workspace.WriteFile("project/README.md", "# App");
		var destination = Path.Combine(workspace.CreateDirectory("output"), "project.zip");
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		var state = await controller.OpenAsync(
			project,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);
		var updates = new List<ProjectCopyExportProgress>();
		var progress = new SynchronousProgress<ProjectCopyExportProgress>(updates.Add);

		var result = await controller.ExportProjectAsync(
			state,
			ProjectCopyExportFormat.Zip,
			destination,
			TestContext.Current.CancellationToken,
			progress);

		Assert.Equal(Path.GetFullPath(destination), result);
		Assert.NotEmpty(updates);
		Assert.Equal(updates[^1].TotalEntryCount, updates[^1].ProcessedEntryCount);
		Assert.True(updates[^1].BytesWritten > 0);
	}

	private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
	{
		public void Report(T value) => report(value);
	}
}
