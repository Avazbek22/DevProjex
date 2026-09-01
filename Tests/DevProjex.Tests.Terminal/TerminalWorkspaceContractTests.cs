using System.Diagnostics;
using System.Xml.Linq;
using DevProjex.Application.Preview;
using DevProjex.Infrastructure.Git;

namespace DevProjex.Tests.Terminal;

public sealed class TerminalWorkspaceContractTests
{
	[Fact]
	public void ExportDestinationHistoryIsIndependentForContextFolderAndZip()
	{
		var history = new TerminalExportDestinationHistory();
		history.Remember(TerminalExportKind.Context, "context.md");
		history.Remember(TerminalExportKind.Folder, "project-folder");

		Assert.Equal("context.md", history.Resolve(TerminalExportKind.Context, "default.md"));
		Assert.Equal("project-folder", history.Resolve(TerminalExportKind.Folder, "default-folder"));
		Assert.Equal("default.zip", history.Resolve(TerminalExportKind.Zip, "default.zip"));
	}
	[Fact]
	public void CancellationSourceCleanupIsIdempotentAcrossBackgroundCompletion()
	{
		CancellationTokenSource? active = new();
		var token = active.Token;
		TerminalWorkspaceSession.CancelAndDispose(ref active);

		Assert.Null(active);
		Assert.True(token.IsCancellationRequested);

		CancellationTokenSource? completed = new();
		completed.Dispose();
		TerminalWorkspaceSession.CancelAndDispose(ref completed);
		Assert.Null(completed);
	}

	[Fact]
	public void ProjectOpenErrorsHideManagedRepositoryCachePaths()
	{
		var cachePath = Path.Combine(Path.GetTempPath(), "DevProjex", "cache", "owner-repo");
		var repository = new ProjectSourceIdentity(
			"repo",
			ProjectSourceType.GitClone,
			"https://example.test/owner/repo.git",
			"https://example.test/owner/repo.git",
			IsCachedRepository: true);

		Assert.Equal(
			"https://example.test/owner/repo.git",
			TerminalWorkspaceSession.ResolveProjectOpenErrorDetail(cachePath, repository));
		Assert.Equal(
			cachePath,
			TerminalWorkspaceSession.ResolveProjectOpenErrorDetail(cachePath, sourceIdentity: null));
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
	public void DefaultExportPathMovesOutsideTheSourceWhenTuiRunsFromTheProject()
	{
		using var workspace = new TemporaryDirectory();
		var source = workspace.CreateDirectory("Project");

		Assert.Equal(
			Path.Combine(workspace.Path, "Project-context.txt"),
			TerminalWorkspaceSession.BuildDefaultExportPath(
				source,
				source,
				"Project-context.txt"));
		Assert.Equal(
			Path.Combine(workspace.Path, "Project.zip"),
			TerminalWorkspaceSession.BuildDefaultExportPath(
				source,
				source,
				"Project.zip"));
		Assert.Equal(
			Path.Combine(workspace.Path, "devprojex-profile.json"),
			TerminalWorkspaceSession.BuildDefaultExportPath(
				source,
				source,
				"devprojex-profile.json"));

		var externalCurrentDirectory = workspace.CreateDirectory("external");
		Assert.Equal(
			Path.Combine(externalCurrentDirectory, "devprojex-profile.json"),
			TerminalWorkspaceSession.BuildDefaultExportPath(
				source,
				externalCurrentDirectory,
				"devprojex-profile.json"));
	}

	[Fact]
	public void DefaultExportPathIsEmptyWhenSourceSafetyCannotBeEstablished()
	{
		using var workspace = new TemporaryDirectory();
		var missingParent = Path.Combine(workspace.Path, "missing");
		var source = Path.Combine(missingParent, "Project");

		Assert.Empty(
			TerminalWorkspaceSession.BuildDefaultExportPath(
				source,
				source,
				"Project-context.txt"));
	}

	[Fact]
	public void DefaultExportPathValidatesAliasesAndKeepsAStableRequestedLocation()
	{
		using var workspace = new TemporaryDirectory();
		var source = workspace.CreateDirectory("Project");
		var safeTarget = workspace.CreateDirectory("safe-target");
		var sourceAlias = Path.Combine(workspace.Path, "source-alias");
		var safeAlias = Path.Combine(workspace.Path, "safe-alias");
		CreateDirectoryAliasOrSkip(sourceAlias, source);
		CreateDirectoryAliasOrSkip(safeAlias, safeTarget);

		try
		{
			Assert.Equal(
				Path.Combine(workspace.Path, "devprojex-profile.json"),
				TerminalWorkspaceSession.BuildDefaultExportPath(
					source,
					sourceAlias,
					"devprojex-profile.json"));
			Assert.Equal(
				Path.Combine(safeAlias, "devprojex-profile.json"),
				TerminalWorkspaceSession.BuildDefaultExportPath(
					source,
					safeAlias,
					"devprojex-profile.json"));
		}
		finally
		{
			DeleteDirectoryAlias(sourceAlias);
			DeleteDirectoryAlias(safeAlias);
		}
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

	private static void CreateDirectoryAliasOrSkip(string aliasPath, string targetPath)
	{
		if (!OperatingSystem.IsWindows())
		{
			try
			{
				Directory.CreateSymbolicLink(aliasPath, targetPath);
				return;
			}
			catch (Exception exception) when (exception is
					   UnauthorizedAccessException or
					   IOException or
					   PlatformNotSupportedException)
			{
				Assert.Skip(
					$"Directory symbolic links are unavailable: {exception.GetType().Name}.");
			}
		}

		using var process = new Process
		{
			StartInfo = new ProcessStartInfo("cmd.exe")
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			}
		};
		process.StartInfo.ArgumentList.Add("/c");
		process.StartInfo.ArgumentList.Add("mklink");
		process.StartInfo.ArgumentList.Add("/J");
		process.StartInfo.ArgumentList.Add(aliasPath);
		process.StartInfo.ArgumentList.Add(targetPath);

		try
		{
			process.Start();
			process.WaitForExit();
			if (process.ExitCode != 0 || !Directory.Exists(aliasPath))
				Assert.Skip("The test environment did not allow creating a Windows junction.");
		}
		catch (Exception exception) when (exception is
				   InvalidOperationException or
				   IOException or
				   System.ComponentModel.Win32Exception)
		{
			Assert.Skip($"Windows junction creation is unavailable: {exception.GetType().Name}.");
		}
	}

	private static void DeleteDirectoryAlias(string aliasPath)
	{
		try
		{
			if (Directory.Exists(aliasPath))
				Directory.Delete(aliasPath);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// The temporary workspace performs final best-effort cleanup.
		}
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
	public void MarkerlessReadableDirectoryIsAcceptedAsCurrentWorkspace()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("notes.txt", "plain directory");

		Assert.True(TerminalWelcomePolicy.IsSafeProjectWorkspace(workspace.Path));
	}

	[Fact]
	public async Task WorkspaceControllerRejectsUnknownPreviewChoicesInsteadOfUsingDefaults()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/app.cs", "class App {}");
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);

		var formatException = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
			controller.BuildPreviewDocumentAsync(
				state,
				ProjectContextView.Tree,
				(ProjectContextDocumentFormat)int.MaxValue,
				TestContext.Current.CancellationToken));
		var viewException = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
			controller.BuildPreviewDocumentAsync(
				state,
				(ProjectContextView)int.MaxValue,
				ProjectContextDocumentFormat.Text,
				TestContext.Current.CancellationToken));

		Assert.Equal("format", formatException.ParamName);
		Assert.Equal("view", viewException.ParamName);
	}

	[Fact]
	public async Task WorkspaceControllerAppliesSourceSafetyToPortableProfileExport()
	{
		using var workspace = new TemporaryDirectory();
		var source = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/app.cs", "class App {}\n");
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(
			services,
			new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			source,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);
		var insideDestination = Path.Combine(source, "portable.json");

		var exception = await Assert.ThrowsAsync<ProjectCopyExportException>(() =>
			controller.SavePortableProfileAsync(
				state,
				insideDestination,
				overwrite: false,
				TestContext.Current.CancellationToken));

		Assert.Contains(
			exception.Error,
			new[]
			{
				ProjectCopyExportError.DestinationInsideSource,
				ProjectCopyExportError.UnsafeDestinationPath
			});
		Assert.False(File.Exists(insideDestination));

		var outputDirectory = workspace.CreateDirectory("profiles");
		var externalDestination = Path.Combine(outputDirectory, "portable.json");
		var result = await controller.SavePortableProfileAsync(
			state,
			externalDestination,
			overwrite: false,
			TestContext.Current.CancellationToken);

		Assert.Equal(Path.GetFullPath(externalDestination), result);
		Assert.True(File.Exists(externalDestination));
		Assert.Empty(Directory.EnumerateFiles(
			outputDirectory,
			".portable.json.*.tmp"));
	}

	[Fact]
	public async Task EquivalentProjectCommandRejectsUnknownOutputKind()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/app.cs", "class App {}");
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);

		var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
			TerminalWorkspaceController.BuildEquivalentProjectCommand(
				state,
				(ProjectCopyExportFormat)int.MaxValue,
				Path.Combine(workspace.Path, "output")));

		Assert.Equal("format", exception.ParamName);
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

		var completePayload = await CompleteContextDocumentTestHelper.BuildAsync(
			services.ContextDocumentService,
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
	public async Task InteractiveLargePreviewIsFileBackedAndIndexesFirstMiddleAndFinalFiles()
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

		var build = await controller.BuildPreviewDocumentWithMetricsAsync(
			state,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Markdown,
			TestContext.Current.CancellationToken);
		Assert.Equal(
			await ExportOutputMetricsCalculator.FromDocumentAsync(
				build.Document,
				TestContext.Current.CancellationToken),
			build.Metrics);
		await SetPreviewDocumentAsync(state, build.Document);

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
	public async Task InteractivePreviewDistinguishesBinaryAndOversizedText()
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

		var preview = await controller.BuildPreviewDocumentAsync(
			state,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Markdown,
			TestContext.Current.CancellationToken);
		await SetPreviewDocumentAsync(state, preview);

		Assert.Equal(2, state.PreviewDocument.Sections.Count);
		Assert.Contains(
			"[Binary file; content omitted.]",
			state.PreviewText,
			StringComparison.Ordinal);
		Assert.Contains(
			"[File is too large for interactive preview; export to inspect complete content.]",
			state.PreviewText,
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task InteractivePreviewLocalizesDistinctContentOmissionReasons()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/large.txt", new string('x', 10 * 1024 * 1024 + 1));
		var binaryPath = Path.Combine(workspace.Path, "src", "binary.dat");
		Directory.CreateDirectory(Path.GetDirectoryName(binaryPath)!);
		File.WriteAllBytes(binaryPath, [0, 1, 2, 3]);
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.Ru);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);

		var localizedPreview = await controller.BuildPreviewDocumentAsync(
			state,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Markdown,
			TestContext.Current.CancellationToken);
		await SetPreviewDocumentAsync(state, localizedPreview);

		Assert.Contains(
			"[Двоичный файл — содержимое пропущено.]",
			state.PreviewText,
			StringComparison.Ordinal);
		Assert.Contains(
			"[Файл слишком большой для интерактивного предпросмотра; экспортируйте его для полного просмотра.]",
			state.PreviewText,
			StringComparison.Ordinal);
		Assert.DoesNotContain("Binary file", state.PreviewText, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(ProjectContextDocumentFormat.Text)]
	[InlineData(ProjectContextDocumentFormat.Markdown)]
	[InlineData(ProjectContextDocumentFormat.Json)]
	[InlineData(ProjectContextDocumentFormat.Xml)]
	public async Task ExactExportDocumentMatchesCompleteExportPayload(
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

		var preview = await controller.BuildExactExportDocumentAsync(
			state,
			ProjectContextView.TreeContent,
			format,
			TestContext.Current.CancellationToken);
		await SetPreviewDocumentAsync(state, preview);
		var exported = await CompleteContextDocumentTestHelper.BuildAsync(
			services.ContextDocumentService,
			state.Plan,
			ProjectContextView.TreeContent,
			format,
			TestContext.Current.CancellationToken);

		Assert.Equal(exported, state.PreviewText);
	}

	[Fact]
	public async Task LargeExactExportDocumentStreamsToFileAndKeepsFinalFileReachable()
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

		var preview = await controller.BuildExactExportDocumentAsync(
			state,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Markdown,
			TestContext.Current.CancellationToken);
		await SetPreviewDocumentAsync(state, preview);

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
	public async Task AtomicOutputWriterReportsStableAliasForFirstRevalidationConflict()
	{
		using var workspace = new TemporaryDirectory();
		var source = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/app.cs", "class App {}");
		var physicalOutput = workspace.CreateDirectory("physical-output");
		var alias = Path.Combine(workspace.Path, "output-alias");
		CreateDirectoryAliasOrSkip(alias, physicalOutput);
		var requestedDestination = Path.Combine(alias, "context.md");
		var physicalDestination = Path.Combine(physicalOutput, "context.md");
		var validationCount = 0;
		var writeInvoked = false;

		try
		{
			var exception = await Assert.ThrowsAsync<OutputDestinationConflictException>(
				() => AtomicOutputWriter.WriteAsync(
					requestedDestination,
					overwrite: false,
					(_, _) =>
					{
						writeInvoked = true;
						return Task.CompletedTask;
					},
					TestContext.Current.CancellationToken,
					path =>
					{
						validationCount++;
						if (validationCount == 2)
							File.WriteAllText(physicalDestination, "EXISTING");

						return ExactOutputDestinationValidator.ValidateContext(
							source,
							path,
							overwrite: false);
					}));

			Assert.Equal(2, validationCount);
			Assert.False(writeInvoked);
			Assert.Equal(Path.GetFullPath(requestedDestination), exception.Path);
			Assert.Equal("EXISTING", File.ReadAllText(physicalDestination));
			Assert.Empty(Directory.EnumerateFiles(physicalOutput, ".*.tmp"));
		}
		finally
		{
			DeleteDirectoryAlias(alias);
		}
	}

	[Fact]
	public async Task TuiContextExportReportsStableRequestedAlias()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/app.cs", "class App {}");
		var outputDirectory = workspace.CreateDirectory("output");
		var alias = Path.Combine(workspace.Path, "output-alias");
		try
		{
			Directory.CreateSymbolicLink(alias, outputDirectory);
		}
		catch (Exception exception) when (exception is
				   UnauthorizedAccessException or
				   IOException or
				   PlatformNotSupportedException)
		{
			Assert.Skip(
				$"Directory symbolic links are unavailable: {exception.GetType().Name}.");
		}

		try
		{
			var destination = Path.Combine(alias, "context.md");
			var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
				.Create(AppLanguage.En);
			var controller = new TerminalWorkspaceController(
				services,
				new TestTerminalEnvironment());
			using var state = await controller.OpenAsync(
				project,
				ProjectProfileReference.Standard,
				TestContext.Current.CancellationToken);

			var result = await controller.ExportContextAsync(
				state,
				ProjectContextView.TreeContent,
				ProjectContextDocumentFormat.Markdown,
				destination,
				overwrite: false,
				TestContext.Current.CancellationToken);

			Assert.Equal(Path.GetFullPath(destination), result);
			Assert.True(File.Exists(Path.Combine(outputDirectory, "context.md")));
		}
		finally
		{
			Directory.Delete(alias);
		}
	}

	[Fact]
	public async Task PlainTuiContextExportUsesAsciiTreePresentation()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile("project/src/app.cs", "class App {}");
		var destination = Path.Combine(workspace.Path, "context.txt");
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			project,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);

		await controller.ExportContextAsync(
			state,
			ProjectContextView.Tree,
			ProjectContextDocumentFormat.Text,
			destination,
			overwrite: false,
			TestContext.Current.CancellationToken,
			plain: true);

		var payload = await File.ReadAllTextAsync(
			destination,
			TestContext.Current.CancellationToken);
		Assert.Contains("`-- src", payload, StringComparison.Ordinal);
		Assert.DoesNotContain("|-- project", payload, StringComparison.Ordinal);
		Assert.DoesNotContain("├", payload, StringComparison.Ordinal);
		Assert.DoesNotContain("└", payload, StringComparison.Ordinal);
		Assert.DoesNotContain("│", payload, StringComparison.Ordinal);
		Assert.DoesNotContain("─", payload, StringComparison.Ordinal);
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

	[Theory]
	[InlineData(ProjectContextDocumentFormat.Text, true)]
	[InlineData(ProjectContextDocumentFormat.Markdown, false)]
	[InlineData(ProjectContextDocumentFormat.Json, false)]
	[InlineData(ProjectContextDocumentFormat.Xml, false)]
	public async Task PreparedContextExportMeasuresTheExactTransformedPayload(
		ProjectContextDocumentFormat format,
		bool plain)
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile(
			"project/src/App.cs",
			"""
			public sealed class App
			{

				// This comment is removed.
				public void Run()
				{
					Console.WriteLine("Привет 🙂");
				}
			}
			""");
		var extension = format switch
		{
			ProjectContextDocumentFormat.Text => ".txt",
			ProjectContextDocumentFormat.Markdown => ".md",
			ProjectContextDocumentFormat.Json => ".json",
			ProjectContextDocumentFormat.Xml => ".xml",
			_ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
		};
		var destination = Path.Combine(workspace.CreateDirectory("output"), "context" + extension);
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			project,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);
		foreach (var option in new[]
			{
				IgnoreOptionId.CompressCode,
				IgnoreOptionId.StripComments,
				IgnoreOptionId.StripBlankLines
			})
		{
			controller.SetContentTransformation(
				state,
				option,
				enabled: true,
				TestContext.Current.CancellationToken);
		}

		var summary = await controller.PrepareContextExportAsync(
			state,
			ProjectContextView.TreeContent,
			format,
			destination,
			overwrite: false,
			TestContext.Current.CancellationToken,
			plain);
		await controller.ExportContextAsync(
			state,
			ProjectContextView.TreeContent,
			format,
			destination,
			overwrite: false,
			TestContext.Current.CancellationToken,
			plain);
		var payload = await File.ReadAllTextAsync(
			destination,
			TestContext.Current.CancellationToken);
		var expected = ExportOutputMetricsCalculator.FromText(payload);

		Assert.Equal(expected.Chars, summary.Characters);
		Assert.Equal(expected.Tokens, summary.EstimatedTokens);
		Assert.DoesNotContain("This comment is removed", payload, StringComparison.Ordinal);
		Assert.DoesNotContain("Console.WriteLine", payload, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(ProjectContextView.Tree, ProjectContextDocumentFormat.Text)]
	[InlineData(ProjectContextView.Content, ProjectContextDocumentFormat.Json)]
	public async Task CopyPayloadUsesTheExactContextPipelineForDefaultsAndOverrides(
		ProjectContextView view,
		ProjectContextDocumentFormat format)
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/app.cs", "class App {}");
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);

		var payload = await controller.BuildCopyPayloadAsync(
			state,
			view,
			format,
			TestContext.Current.CancellationToken);
		var expected = await CompleteContextDocumentTestHelper.BuildAsync(
			services.ContextDocumentService,
			state.Plan,
			view,
			format,
			TestContext.Current.CancellationToken);

		using var expectedDocument = new InMemoryPreviewTextDocument(expected);
		Assert.Equal(
			PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(expectedDocument),
			payload);
	}

	[Fact]
	public void CopyPayloadRejectsAnOversizedDocumentBeforeReadingItsText()
	{
		using var document = new NonMaterializablePreviewDocument(
			TerminalWorkspaceController.MaximumClipboardPayloadBytes / sizeof(char) + 1);

		var payload = TerminalWorkspaceController.MaterializeCopyPayload(document);

		Assert.Null(payload);
	}

	[Fact]
	public async Task BuildCurrentPlanPublishesTheReprojectedPlanToWorkspaceState()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("kept.cs", "class Kept {}");
		workspace.WriteFile("cleared.cs", "class Cleared {}");
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);
		var clearedIndex = state.VisibleRows
			.Select((row, index) => (row, index))
			.Single(item => Path.GetFileName(item.row.Node.FullPath) == "cleared.cs")
			.index;
		state.ToggleSelection(clearedIndex);

		var rebuilt = await controller.BuildCurrentPlanAsync(
			state,
			TestContext.Current.CancellationToken);

		Assert.Same(rebuilt, state.Plan);
		Assert.DoesNotContain(
			state.Plan.IncludedFiles,
			path => Path.GetFileName(path) == "cleared.cs");
	}

	[Fact]
	public async Task RefreshSelectsANewFileAndPreservesAnExplicitlyClearedFile()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("src/kept.cs", "class Kept {}");
		workspace.WriteFile("src/cleared.cs", "class Cleared {}");
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);
		var sourceIndex = state.VisibleRows
			.Select((row, index) => (row, index))
			.Single(item => Path.GetFileName(item.row.Node.FullPath) == "src")
			.index;
		state.Expand(sourceIndex);
		var clearedIndex = state.VisibleRows
			.Select((row, index) => (row, index))
			.Single(item => Path.GetFileName(item.row.Node.FullPath) == "cleared.cs")
			.index;
		state.ToggleSelection(clearedIndex);
		workspace.WriteFile("src/new.cs", "class New {}");

		await controller.RefreshProjectAsync(state, TestContext.Current.CancellationToken);

		Assert.Contains(state.Plan.IncludedFiles, path => Path.GetFileName(path) == "kept.cs");
		Assert.Contains(state.Plan.IncludedFiles, path => Path.GetFileName(path) == "new.cs");
		Assert.DoesNotContain(state.Plan.IncludedFiles, path => Path.GetFileName(path) == "cleared.cs");
	}

	[Fact]
	public async Task EmptyMomentaryGitScopeRetainsBroadSelectionAcrossRefresh()
	{
		if (!TryRunGit(Directory.GetCurrentDirectory(), "--version"))
			Assert.Skip("Git is required for this regression test.");
		using var workspace = new TemporaryDirectory();
		using var appData = new TemporaryDirectory();
		workspace.WriteFile("Baseline.cs", "class Baseline {}\n");
		Assert.True(TryRunGit(workspace.Path, "init", "--quiet"));
		Assert.True(TryRunGit(workspace.Path, "config", "user.name", "DevProjex Tests"));
		Assert.True(TryRunGit(workspace.Path, "config", "user.email", "devprojex@example.invalid"));
		Assert.True(TryRunGit(workspace.Path, "add", "--", "Baseline.cs"));
		Assert.True(TryRunGit(workspace.Path, "commit", "--quiet", "-m", "baseline"));
		var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);

		await controller.SetGitModeAsync(
			state,
			GitFilteringMode.Staged,
			TestContext.Current.CancellationToken);
		Assert.Empty(state.Plan.IncludedFiles);
		Assert.Null(services.ContextPlanner.GetSelectedRelativePathFrontier(state.Plan));

		workspace.WriteFile("New.cs", "class New {}\n");
		Assert.True(TryRunGit(workspace.Path, "add", "--", "New.cs"));
		await controller.RefreshProjectAsync(state, TestContext.Current.CancellationToken);

		Assert.Equal("New.cs", Path.GetFileName(Assert.Single(state.Plan.IncludedFiles)));
		await controller.SetGitModeAsync(
			state,
			GitFilteringMode.None,
			TestContext.Current.CancellationToken);
		Assert.Equal(
			["Baseline.cs", "New.cs"],
			state.Plan.IncludedFiles
				.Select(static path => Path.GetFileName(path)!)
				.Order(StringComparer.Ordinal)
				.ToArray());
	}

	[Fact]
	public async Task ExplicitEmptySelectionRemainsEmptyWhenMomentaryGitScopeChanges()
	{
		if (!TryRunGit(Directory.GetCurrentDirectory(), "--version"))
			Assert.Skip("Git is required for this regression test.");
		using var workspace = new TemporaryDirectory();
		using var appData = new TemporaryDirectory();
		workspace.WriteFile("First.cs", "class First {}\n");
		Assert.True(TryRunGit(workspace.Path, "init", "--quiet"));
		Assert.True(TryRunGit(workspace.Path, "add", "--", "First.cs"));
		var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);
		await controller.SetGitModeAsync(
			state,
			GitFilteringMode.Staged,
			TestContext.Current.CancellationToken);
		Assert.Single(state.Plan.IncludedFiles);

		state.SelectNone();
		await controller.ReprojectSelectionAsync(state, TestContext.Current.CancellationToken);
		Assert.Empty(services.ContextPlanner.GetSelectedRelativePathFrontier(state.Plan)!);
		workspace.WriteFile("Second.cs", "class Second {}\n");
		Assert.True(TryRunGit(workspace.Path, "add", "--", "Second.cs"));

		await controller.RefreshProjectAsync(state, TestContext.Current.CancellationToken);

		Assert.Empty(state.Plan.IncludedFiles);
		Assert.Empty(services.ContextPlanner.GetSelectedRelativePathFrontier(state.Plan)!);
	}

	[Fact]
	public async Task StructuralRefreshBuildsOnlyPlansRequiredBySelectionEvolution()
	{
		using var workspace = new TemporaryDirectory();
		using var appData = new TemporaryDirectory();
		workspace.WriteFile("src/app.cs", "class App {}");
		var services = new TerminalServiceFactory(() => appData.Path)
			.Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);

		var unchanged = await controller.BuildStructuralRefreshAsync(
			controller.CaptureStructuralRefresh(state, state.BuildSelection()),
			TestContext.Current.CancellationToken);
		Assert.Equal(1, unchanged.PlanBuildCount);
		TerminalWorkspaceController.ApplyStructuralRefresh(state, unchanged);

		workspace.WriteFile("src/config.json", "{}");
		var newExtension = await controller.BuildStructuralRefreshAsync(
			controller.CaptureStructuralRefresh(state, state.BuildSelection()),
			TestContext.Current.CancellationToken);
		Assert.Equal(1, newExtension.PlanBuildCount);
		Assert.Contains(".json", newExtension.Plan.SelectedExtensions);
		Assert.Contains(
			newExtension.Plan.IncludedFiles,
			path => Path.GetFileName(path) == "config.json");
		TerminalWorkspaceController.ApplyStructuralRefresh(state, newExtension);

		var sourceIndex = state.VisibleRows
			.Select((row, index) => (row, index))
			.Single(item => Path.GetFileName(item.row.Node.FullPath) == "src")
			.index;
		state.Expand(sourceIndex);
		var jsonIndex = state.VisibleRows
			.Select((row, index) => (row, index))
			.Single(item => Path.GetFileName(item.row.Node.FullPath) == "config.json")
			.index;
		state.ToggleSelection(jsonIndex);
		var partialSelection = await controller.BuildStructuralRefreshAsync(
			controller.CaptureStructuralRefresh(state, state.BuildSelection()),
			TestContext.Current.CancellationToken);
		Assert.Equal(1, partialSelection.PlanBuildCount);
	}

	[Fact]
	public async Task StructuralRefreshFallsBackWhenTheActiveRepositoryBoundaryDisappears()
	{
		if (!TryRunGit(Directory.GetCurrentDirectory(), "--version"))
			Assert.Skip("Git is required for this regression test.");
		using var workspace = new TemporaryDirectory();
		using var appData = new TemporaryDirectory();
		workspace.WriteFile("App.cs", "class App {}\n");
		Assert.True(TryRunGit(workspace.Path, "init", "--quiet"));
		Assert.True(TryRunGit(workspace.Path, "add", "--", "App.cs"));
		var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);
		await controller.SetGitModeAsync(
			state,
			GitFilteringMode.Staged,
			TestContext.Current.CancellationToken);
		Assert.Equal(GitFilteringMode.Staged, state.Plan.Selection.GitMode);

		var gitPath = Path.Combine(workspace.Path, ".git");
		var detachedGitPath = Path.Combine(
			Path.GetTempPath(),
			$"devprojex-terminal-git-{Guid.NewGuid():N}");
		try
		{
			Directory.Move(gitPath, detachedGitPath);
			var result = await controller.BuildStructuralRefreshAsync(
				controller.CaptureStructuralRefresh(
					state,
					state.BuildSelection(),
					GitFilteringMode.RespectGitIgnore),
				TestContext.Current.CancellationToken);

			Assert.Equal(GitFilteringMode.RespectGitIgnore, result.Plan.Selection.GitMode);
			Assert.False(result.Plan.GitReadiness.HasRepositoryBoundary);
			Assert.DoesNotContain(
				result.Plan.Diagnostics,
				static diagnostic => diagnostic.Severity == ContextDiagnosticSeverity.Error);
			Assert.Contains(result.Plan.IncludedFiles, path => Path.GetFileName(path) == "App.cs");
			Assert.True(result.PlanBuildCount >= 2);
		}
		finally
		{
			if (Directory.Exists(detachedGitPath) && !Directory.Exists(gitPath))
				Directory.Move(detachedGitPath, gitPath);
		}
	}

	[Theory]
	[InlineData(GitFilteringMode.RespectGitIgnore, GitFilteringMode.RespectGitIgnore)]
	[InlineData(GitFilteringMode.TrackedFilesOnly, GitFilteringMode.None)]
	public async Task SettingsRefreshUsesTheStickyFallbackWhenTheRepositoryDisappears(
		GitFilteringMode preferredMode,
		GitFilteringMode expectedMode)
	{
		if (!TryRunGit(Directory.GetCurrentDirectory(), "--version"))
			Assert.Skip("Git is required for this regression test.");
		using var workspace = new TemporaryDirectory();
		using var appData = new TemporaryDirectory();
		workspace.WriteFile("App.cs", "class App {}\n");
		Assert.True(TryRunGit(workspace.Path, "init", "--quiet"));
		Assert.True(TryRunGit(workspace.Path, "add", "--", "App.cs"));
		var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);
		await controller.SetGitModeAsync(
			state,
			GitFilteringMode.Staged,
			TestContext.Current.CancellationToken);

		var gitPath = Path.Combine(workspace.Path, ".git");
		var detachedGitPath = Path.Combine(
			Path.GetTempPath(),
			$"devprojex-terminal-git-{Guid.NewGuid():N}");
		try
		{
			Directory.Move(gitPath, detachedGitPath);
			var candidate = state.BuildSelection() with { Exclusions = [] };
			var result = await controller.BuildSettingsPlanAsync(
				state.Plan,
				candidate,
				state.ExtensionOptionStates,
				state.BuildSelectedItemRelativePaths(),
				state.PathOptionStates,
				preferredMode,
				TestContext.Current.CancellationToken);

			Assert.Equal(expectedMode, result.Plan.Selection.GitMode);
			Assert.False(result.Plan.GitReadiness.HasRepositoryBoundary);
			Assert.DoesNotContain(
				result.Plan.Diagnostics,
				static diagnostic => diagnostic.Severity == ContextDiagnosticSeverity.Error);
		}
		finally
		{
			if (Directory.Exists(detachedGitPath) && !Directory.Exists(gitPath))
				Directory.Move(detachedGitPath, gitPath);
		}
	}

	[Fact]
	public async Task ApplyingStructuralRefreshPublishesVisibleRowsOnTheCallingThread()
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("app.cs", "class App {}");
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);
		var originalPlan = state.Plan;
		var originalRevision = state.Revision;
		var collectionEventCount = 0;
		var notificationThreadId = -1;
		state.VisibleRows.CollectionChanged += (_, _) =>
		{
			collectionEventCount++;
			notificationThreadId = Environment.CurrentManagedThreadId;
		};
		workspace.WriteFile("new.cs", "class New {}");
		var result = await controller.BuildStructuralRefreshAsync(
			controller.CaptureStructuralRefresh(state, state.BuildSelection()),
			TestContext.Current.CancellationToken);

		Assert.Same(originalPlan, state.Plan);
		Assert.Equal(originalRevision, state.Revision);
		Assert.Equal(0, collectionEventCount);
		var callerThreadId = Environment.CurrentManagedThreadId;

		TerminalWorkspaceController.ApplyStructuralRefresh(state, result);

		Assert.NotSame(originalPlan, state.Plan);
		Assert.Contains(state.Plan.IncludedFiles, path => Path.GetFileName(path) == "new.cs");
		Assert.Equal(1, collectionEventCount);
		Assert.Equal(callerThreadId, notificationThreadId);
	}

	[Fact]
	public void ExportSummaryDistinguishesAnEmptySelectionFromUnavailableValues()
	{
		using var workspace = new TemporaryDirectory();
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var summary = new TerminalExportSummary(
			TerminalExportKind.Context,
			ProjectContextView.Tree,
			ProjectContextDocumentFormat.Markdown,
			"stdout",
			TerminalExportDestinationState.Ready,
			FileCount: 0,
			FolderCount: 1,
			Bytes: 0,
			Characters: 0,
			EstimatedTokens: 0,
			GitFilteringMode.None,
			Exclusions: [],
			DiagnosticCount: 0);

		var text = new TerminalWorkspace(
			services,
			new TestTerminalEnvironment()).BuildExportSummaryText(summary);

		Assert.Contains("No Git filtering; None", text, StringComparison.Ordinal);
		Assert.DoesNotContain("none available", text, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task InteractiveContentPreviewPreservesRootWhenSelectionHasNoFiles()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("empty-project");
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			project,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);

		var build = await controller.BuildPreviewDocumentWithMetricsAsync(
			state,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Markdown,
			TestContext.Current.CancellationToken);
		using var document = build.Document;
		var expected = ContextRootPresentation.FormatLine(project);

		Assert.Equal(expected, document.GetFullText());
		Assert.Equal(ExportOutputMetricsCalculator.FromText(expected), build.Metrics);
	}

	[Fact]
	public async Task StructuralGitRefreshQueriesOnlyTheRepositoryOwningTheManualSelection()
	{
		if (!TryRunGit(Directory.GetCurrentDirectory(), "--version"))
			Assert.Skip("Git is required for this regression test.");
		using var workspace = new TemporaryDirectory();
		using var appData = new TemporaryDirectory();
		workspace.WriteFile("Outer.txt", "outer\n");
		Assert.True(TryRunGit(workspace.Path, "init", "--quiet"));
		Assert.True(TryRunGit(workspace.Path, "config", "user.name", "DevProjex Tests"));
		Assert.True(TryRunGit(workspace.Path, "config", "user.email", "devprojex@example.invalid"));
		Assert.True(TryRunGit(workspace.Path, "add", "--", "Outer.txt"));
		Assert.True(TryRunGit(workspace.Path, "commit", "--quiet", "-m", "outer baseline"));
		var nestedRoot = workspace.CreateDirectory("nested");
		workspace.WriteFile("nested/App.cs", "v1\n");
		Assert.True(TryRunGit(nestedRoot, "init", "--quiet"));
		Assert.True(TryRunGit(nestedRoot, "config", "user.name", "DevProjex Tests"));
		Assert.True(TryRunGit(nestedRoot, "config", "user.email", "devprojex@example.invalid"));
		Assert.True(TryRunGit(nestedRoot, "add", "--", "App.cs"));
		Assert.True(TryRunGit(nestedRoot, "commit", "--quiet", "-m", "nested baseline"));
		workspace.WriteFile("nested/App.cs", "v2\n");
		Assert.True(TryRunGit(nestedRoot, "add", "--", "App.cs"));
		Assert.True(TryRunGit(nestedRoot, "commit", "--quiet", "-m", "nested change"));
		var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);
		var outerIndex = state.VisibleRows
			.Select((row, index) => (row, index))
			.Single(item => item.row.Node.DisplayName == "Outer.txt")
			.index;
		state.ToggleSelection(outerIndex);
		var candidate = GitScopeSelection.WithMode(
			state.BuildSelection(),
			GitFilteringMode.Diff,
			"HEAD~1..HEAD");

		var result = await controller.BuildSettingsPlanAsync(
			state.Plan,
			candidate,
			state.ExtensionOptionStates,
			state.BuildSelectedItemRelativePaths(),
			state.PathOptionStates,
			TestContext.Current.CancellationToken);

		Assert.False(result.Plan.HasErrors);
		Assert.Equal("App.cs", Path.GetFileName(Assert.Single(result.Plan.IncludedFiles)));
		Assert.Equal(GitFilteringMode.Diff, result.Plan.Selection.GitMode);
	}

	[Fact]
	public async Task ExplicitEmptySelectionDoesNotQueryGitScopeDuringSettingsRefresh()
	{
		using var workspace = new TemporaryDirectory();
		using var appData = new TemporaryDirectory();
		workspace.WriteFile("App.cs", "class App {}\n");
		var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		var provider = new FailingGitScopePathProvider();
		services = services with
		{
			ContextFactory = new TerminalProjectContextFactory(
				services.ContextPlanner,
				services.SourceIdentityResolver,
				services.SecretRedactionSession,
				provider,
				new GitRemoteDiffRangeResolver())
		};
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			workspace.Path,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);
		state.SelectNone();
		var candidate = GitScopeSelection.WithMode(
			state.BuildSelection(),
			GitFilteringMode.Staged);

		var result = await controller.BuildSettingsPlanAsync(
			state.Plan,
			candidate,
			state.ExtensionOptionStates,
			state.BuildSelectedItemRelativePaths(),
			state.PathOptionStates,
			TestContext.Current.CancellationToken);

		Assert.Empty(result.Plan.IncludedFiles);
		Assert.False(result.Plan.HasErrors);
		Assert.Equal(GitFilteringMode.Staged, result.Plan.Selection.GitMode);
		Assert.Equal(0, provider.CallCount);
	}

	[Fact]
	public async Task CachedRemoteWorkspaceHydratesShallowDiffWithoutChangingItsCheckout()
	{
		if (!TryRunGit(Directory.GetCurrentDirectory(), "--version"))
			Assert.Skip("Git is required for this regression test.");
		using var workspace = new TemporaryDirectory();
		using var appData = new TemporaryDirectory();
		var source = workspace.CreateDirectory("source");
		Assert.True(TryRunGit(source, "init", "--quiet", "--initial-branch=main"));
		Assert.True(TryRunGit(source, "config", "user.name", "DevProjex Tests"));
		Assert.True(TryRunGit(source, "config", "user.email", "devprojex@example.invalid"));
		workspace.WriteFile("source/Baseline.txt", "baseline\n");
		Assert.True(TryRunGit(source, "add", "."));
		Assert.True(TryRunGit(source, "commit", "--quiet", "-m", "baseline"));
		workspace.WriteFile("source/Middle.txt", "middle\n");
		Assert.True(TryRunGit(source, "add", "."));
		Assert.True(TryRunGit(source, "commit", "--quiet", "-m", "middle"));
		workspace.WriteFile("source/Last.txt", "last\n");
		Assert.True(TryRunGit(source, "add", "."));
		Assert.True(TryRunGit(source, "commit", "--quiet", "-m", "last"));
		var bare = Path.Combine(workspace.Path, "origin.git");
		Assert.True(TryRunGit(workspace.Path, "clone", "--quiet", "--bare", source, bare));
		var repositoryUrl = new Uri(bare + Path.DirectorySeparatorChar).AbsoluteUri;
		var services = new TerminalServiceFactory(() => appData.Path).Create(AppLanguage.En);
		await using var resolvedSource = await new TerminalProjectSourceResolver(
				services,
				new TestTerminalEnvironment(),
				new TerminalOutputOptions { Progress = TerminalProgressMode.Never })
			.ResolveAsync(repositoryUrl, "main", TestContext.Current.CancellationToken);
		var checkout = resolvedSource.ProjectPath;
		var headBefore = ReadGit(checkout, "rev-parse", "HEAD");
		var statusBefore = ReadGit(checkout, "status", "--porcelain=v1");
		var contentBefore = File.ReadAllBytes(Path.Combine(checkout, "Last.txt"));
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			checkout,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken,
			ProjectSourceIdentityResolver.CreateCloneIdentity(repositoryUrl, "origin", "main"));
		var candidate = GitScopeSelection.WithMode(
			state.BuildSelection(),
			GitFilteringMode.Diff,
			"HEAD~1..HEAD");

		var result = await controller.BuildSettingsPlanAsync(
			state.Plan,
			candidate,
			state.ExtensionOptionStates,
			state.BuildSelectedItemRelativePaths(),
			state.PathOptionStates,
			TestContext.Current.CancellationToken);

		Assert.False(result.Plan.HasErrors);
		Assert.Equal("Last.txt", Path.GetFileName(Assert.Single(result.Plan.IncludedFiles)));
		Assert.Equal(headBefore, ReadGit(checkout, "rev-parse", "HEAD"));
		Assert.Equal(statusBefore, ReadGit(checkout, "status", "--porcelain=v1"));
		Assert.Equal(contentBefore, File.ReadAllBytes(Path.Combine(checkout, "Last.txt")));
		var diffResolver = new GitRemoteDiffRangeResolver();
		var repeated = await Task.WhenAll(
			diffResolver.ResolveAsync(
				checkout,
				repositoryUrl,
				"HEAD~1..HEAD",
				"main",
				TestContext.Current.CancellationToken),
			diffResolver.ResolveAsync(
				checkout,
				repositoryUrl,
				"HEAD~1..HEAD",
				"main",
				TestContext.Current.CancellationToken));
		Assert.All(repeated, static range => Assert.NotNull(range));
		Assert.Equal(repeated[0], repeated[1]);
	}

	[Theory]
	[InlineData(false, false, null)]
	[InlineData(
		true,
		false,
		"Secrets are redacted in the exported artifact. Private data is not redacted.")]
	[InlineData(
		false,
		true,
		"Private data is redacted in the exported artifact. Secrets are not redacted.")]
	[InlineData(
		true,
		true,
		"Secrets and private data are redacted in the exported artifact.")]
	public void ExportSummaryNamesExactlyTheEnabledRedactionPolicies(
		bool secretsRedacted,
		bool privateDataRedacted,
		string? expected)
	{
		using var workspace = new TemporaryDirectory();
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var summary = new TerminalExportSummary(
			TerminalExportKind.Context,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Markdown,
			"stdout",
			TerminalExportDestinationState.Ready,
			FileCount: 1,
			FolderCount: 0,
			Bytes: 1,
			Characters: 1,
			EstimatedTokens: 1,
			GitFilteringMode.None,
			Exclusions: [],
			DiagnosticCount: 0,
			SecretsRedacted: secretsRedacted,
			PrivateDataRedacted: privateDataRedacted);

		var text = new TerminalWorkspace(
			services,
			new TestTerminalEnvironment()).BuildExportSummaryText(summary);

		if (expected is null)
		{
			Assert.DoesNotContain("Redaction", text, StringComparison.Ordinal);
			return;
		}

		Assert.Contains(expected, text, StringComparison.Ordinal);
		Assert.Equal(
			1,
			text.Split(Environment.NewLine)
				.Count(static line => line.StartsWith("Redaction", StringComparison.Ordinal)));
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
		Assert.Contains("Путь назначения", text, StringComparison.Ordinal);
		Assert.Contains("Файлы", text, StringComparison.Ordinal);
		Assert.Contains("Фильтры", text, StringComparison.Ordinal);
		Assert.Contains(Path.GetFullPath(destination), text, StringComparison.Ordinal);
		Assert.DoesNotContain("Конфликт", text, StringComparison.Ordinal);
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

	[Fact]
	public async Task TuiProjectExportAppliesEverySelectedContentTransformation()
	{
		const string secret = "ghp_a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL";
		const string privateEmail = "ivan.petrov@corp.internal";
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		workspace.WriteFile(
			"project/src/Config.cs",
			$$"""
			namespace Sample;

			// remove this comment
			public sealed class Config
			{
				public const string Token = "{{secret}}";
				public const string Email = "{{privateEmail}}";

				public void Run()
				{
					Console.WriteLine(Token);
				}
			}
			""");
		var destination = Path.Combine(workspace.CreateDirectory("output"), "project-copy");
		var services = new TerminalServiceFactory(() => workspace.CreateDirectory("app-data"))
			.Create(AppLanguage.En);
		var controller = new TerminalWorkspaceController(services, new TestTerminalEnvironment());
		using var state = await controller.OpenAsync(
			project,
			ProjectProfileReference.Standard,
			TestContext.Current.CancellationToken);
		foreach (var optionId in new[]
			{
				IgnoreOptionId.HideSecrets,
				IgnoreOptionId.HidePrivateData,
				IgnoreOptionId.CompressCode,
				IgnoreOptionId.StripComments,
				IgnoreOptionId.StripBlankLines
			})
		{
			controller.SetContentTransformation(
				state,
				optionId,
				enabled: true,
				TestContext.Current.CancellationToken);
		}

		await controller.ExportProjectAsync(
			state,
			ProjectCopyExportFormat.Folder,
			destination,
			TestContext.Current.CancellationToken);

		var exported = await File.ReadAllTextAsync(
			Path.Combine(destination, "src", "Config.cs"),
			TestContext.Current.CancellationToken);
		Assert.DoesNotContain(secret, exported, StringComparison.Ordinal);
		Assert.DoesNotContain(privateEmail, exported, StringComparison.Ordinal);
		Assert.DoesNotContain("remove this comment", exported, StringComparison.Ordinal);
		Assert.DoesNotContain($"{Environment.NewLine}{Environment.NewLine}", exported, StringComparison.Ordinal);
		Assert.DoesNotContain("Console.WriteLine(Token)", exported, StringComparison.Ordinal);
	}

	private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
	{
		public void Report(T value) => report(value);
	}

	private static bool TryRunGit(string workingDirectory, params string[] arguments)
	{
		try
		{
			var startInfo = new ProcessStartInfo("git")
			{
				WorkingDirectory = workingDirectory,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};
			foreach (var argument in arguments)
				startInfo.ArgumentList.Add(argument);
			using var process = Process.Start(startInfo);
			if (process is null)
				return false;
			if (!process.WaitForExit(10_000))
			{
				process.Kill(entireProcessTree: true);
				return false;
			}
			return process.ExitCode == 0;
		}
		catch (Exception exception) when (
			exception is InvalidOperationException or IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static string ReadGit(string workingDirectory, params string[] arguments)
	{
		var startInfo = new ProcessStartInfo("git")
		{
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add(argument);
		using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
		var output = process.StandardOutput.ReadToEnd();
		var error = process.StandardError.ReadToEnd();
		Assert.True(process.WaitForExit(10_000));
		Assert.True(process.ExitCode == 0, error);
		return output.Trim();
	}

	private static async Task SetPreviewDocumentAsync(
		TerminalWorkspaceState state,
		IPreviewTextDocument document)
	{
		var metrics = await ExportOutputMetricsCalculator.FromDocumentAsync(
			document,
			TestContext.Current.CancellationToken);
		state.SetPreviewDocument(document, metrics);
	}

	private sealed class NonMaterializablePreviewDocument(long characterCount) : IPreviewTextDocument
	{
		public int LineCount => 1;
		public int MaxLineLength => 1;
		public long CharacterCount => characterCount;
		public IReadOnlyList<PreviewDocumentSection> Sections => [];

		public string GetFullText() => throw new InvalidOperationException("Text must not be materialized.");
		public string GetLineText(int lineNumber) =>
			throw new InvalidOperationException("Text must not be materialized.");
		public string GetLineRangeText(int firstLine, int lastLine) =>
			throw new InvalidOperationException("Text must not be materialized.");
		public void Dispose()
		{
		}
	}

	private sealed class FailingGitScopePathProvider : IGitScopePathProvider
	{
		private int _callCount;

		public int CallCount => Volatile.Read(ref _callCount);

		public Task<GitScopePathResult> ResolveAsync(
			string projectRoot,
			GitFilteringMode mode,
			string? diffRange,
			CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _callCount);
			throw new InvalidOperationException("The Git scope provider must not run for an explicit empty selection.");
		}
	}
}
