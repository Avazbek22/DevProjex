using System.IO.Compression;
using System.Xml.Linq;
using DevProjex.Application.Context;
using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.Secrets;

namespace DevProjex.Tests.Integration;

public sealed class SecretRedactionOutputContractIntegrationTests
{
	private const string GithubToken = "ghp_" + "a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL";
	private const string GithubTokenSecond = "ghp_" + "Z8y6X4w2V0u9T7s5R3q1P8n6M4k2J0h9G7f5";
	private const string AwsAccessKey = "AKIA" + "Z7M3Q5X2P6N4R7T5";
	private const string PrivateKeyBodyFragment =
		"MIIEvQIBADANBgkq" +
		"hkiG9w0BAQEFAASC" +
		"BKcwggSjAgEAAoIB" +
		"AQDAC4AWkdwKYSd8";
	private const string PrivateKeyPem =
		"-----BEGIN " + "PRIVATE KEY-----\n" +
		PrivateKeyBodyFragment + "\n" +
		"Ks14IReLcYgA" + "DhoXk56ZzXI=\n" +
		"-----END " + "PRIVATE KEY-----";

	[Fact]
	public async Task EnabledRedaction_RemovesEveryTextSecretAcrossPreviewContextFolderAndZip()
	{
		using var workspace = CreateWorkspace();
		var sourceBefore = CaptureSourceBytes(workspace.SourceRoot);
		var analyzer = new FileContentAnalyzer();
		var session = new SecretRedactionSession(new GitleaksSecretDetector());
		var context = new SecretRedactionContext(workspace.SourceRoot, session);
		var plan = await BuildPlanAsync(workspace.SourceRoot, hideSecrets: true);
		var treeExporter = new TreeExportService();
		var treeText = treeExporter.BuildFullTree(plan.SourceRoot, plan.ProjectedTree);

		using var preview = await new PreviewDocumentBuilder(analyzer)
			.BuildTreeAndContentDocumentAsync(
				treeText,
				plan.IncludedFiles,
				TestContext.Current.CancellationToken,
				TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(plan.SourceRoot),
				includeOmissionMarkers: true,
				redactionContext: context);
		var previewPayload = PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(preview);
		var selectedContent = await new SelectedContentExportService(analyzer).BuildAsync(
			plan.IncludedFiles,
			TestContext.Current.CancellationToken,
			TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(plan.SourceRoot),
			context);
		var contextDocuments = await BuildContextDocumentsAsync(plan, analyzer, session);
		var folder = await ExportProjectAsync(
			workspace,
			plan,
			analyzer,
			session,
			ProjectCopyExportFormat.Folder);
		var zip = await ExportProjectAsync(
			workspace,
			plan,
			analyzer,
			session,
			ProjectCopyExportFormat.Zip);

		Assert.Equal(4, preview.Redactions.Count);
		Assert.Equal(4, preview.RedactionSummary?.RedactedCount);
		Assert.Equal(4, folder.RedactedValueCount);
		Assert.Equal(4, zip.RedactedValueCount);
		Assert.All(
			new[] { previewPayload, selectedContent }.Concat(contextDocuments.Values),
			AssertNoTextSecret);
		Assert.All(
			new[] { previewPayload, selectedContent }.Concat(contextDocuments.Values),
			AssertExpectedPlaceholderIdentities);

		AssertNativeLegends(contextDocuments);
		AssertFolderCopy(workspace, folder);
		AssertZipCopy(workspace, zip);
		AssertSourceBytesUnchanged(sourceBefore, workspace.SourceRoot);
	}

	[Fact]
	public async Task KeepAsIsOverride_ChangesOnlyOneOccurrenceAndEveryOutputUsesThatDecision()
	{
		using var workspace = CreateWorkspace(repeatedGithubOnly: true);
		var analyzer = new FileContentAnalyzer();
		var session = new SecretRedactionSession(new GitleaksSecretDetector());
		var context = new SecretRedactionContext(workspace.SourceRoot, session);
		var plan = await BuildPlanAsync(workspace.SourceRoot, hideSecrets: true);
		var previewBuilder = new PreviewDocumentBuilder(analyzer);

		using (var initialPreview = await previewBuilder.BuildContentDocumentAsync(
			       plan.IncludedFiles,
			       TestContext.Current.CancellationToken,
			       TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(plan.SourceRoot),
			       includeOmissionMarkers: true,
			       redactionContext: context))
		{
			var occurrence = Assert.Single(
				initialPreview!.Redactions
					.GroupBy(static span => span.OccurrenceId, StringComparer.Ordinal)
					.First());
			Assert.True(session.ToggleKeepAsIs(occurrence.OccurrenceId));
		}

		using var decidedPreview = await previewBuilder.BuildContentDocumentAsync(
			plan.IncludedFiles,
			TestContext.Current.CancellationToken,
			TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(plan.SourceRoot),
			includeOmissionMarkers: true,
			redactionContext: context);
		var previewPayload = PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(decidedPreview);
		var contextDocuments = await BuildContextDocumentsAsync(plan, analyzer, session);
		var folder = await ExportProjectAsync(
			workspace,
			plan,
			analyzer,
			session,
			ProjectCopyExportFormat.Folder);
		var zip = await ExportProjectAsync(
			workspace,
			plan,
			analyzer,
			session,
			ProjectCopyExportFormat.Zip);

		Assert.Equal(1, decidedPreview!.RedactionSummary?.RedactedCount);
		Assert.Equal(1, decidedPreview.Redactions.Count(static span =>
			span.State == SecretPreviewSpanState.KeptAsIs));
		AssertDecision(previewPayload);
		Assert.All(contextDocuments.Values, AssertDecision);
		AssertKeptOccurrence(File.ReadAllText(Path.Combine(folder.DestinationPath, "a-kept.cs")));
		AssertRedactedOccurrence(File.ReadAllText(Path.Combine(folder.DestinationPath, "b-redacted.cs")));
		using var archive = ZipFile.OpenRead(zip.DestinationPath);
		AssertKeptOccurrence(ReadZipText(archive, "a-kept.cs"));
		AssertRedactedOccurrence(ReadZipText(archive, "b-redacted.cs"));
	}

	[Fact]
	public async Task DisabledRedaction_DoesNotInvokeDetectorOrChangeExistingOutput()
	{
		using var workspace = CreateWorkspace();
		var analyzer = new FileContentAnalyzer();
		var detector = new CountingDetector();
		var session = new SecretRedactionSession(detector);
		var plan = await BuildPlanAsync(workspace.SourceRoot, hideSecrets: false);
		var treeExporter = new TreeExportService();
		var baselineService = new ProjectContextDocumentService(treeExporter, analyzer);
		var configuredService = new ProjectContextDocumentService(
			treeExporter,
			analyzer,
			secretRedactionSession: session);

		var baseline = await WriteContextAsync(
			baselineService,
			plan,
			ProjectContextDocumentFormat.Json);
		var configured = await WriteContextAsync(
			configuredService,
			plan,
			ProjectContextDocumentFormat.Json);
		using var preview = await new PreviewDocumentBuilder(analyzer).BuildContentDocumentAsync(
			plan.IncludedFiles,
			TestContext.Current.CancellationToken,
			TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(plan.SourceRoot));
		var folder = await ExportProjectAsync(
			workspace,
			plan,
			analyzer,
			session,
			ProjectCopyExportFormat.Folder,
			redactSecrets: false);
		var zip = await ExportProjectAsync(
			workspace,
			plan,
			analyzer,
			session,
			ProjectCopyExportFormat.Zip,
			redactSecrets: false);

		Assert.Equal(baseline, configured);
		Assert.Contains(GithubToken, configured, StringComparison.Ordinal);
		Assert.Contains(GithubToken, PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(preview), StringComparison.Ordinal);
		Assert.Contains(
			GithubToken,
			File.ReadAllText(Path.Combine(folder.DestinationPath, "src", "app.cs")),
			StringComparison.Ordinal);
		Assert.Null(folder.RedactionLegendPath);
		using (var archive = ZipFile.OpenRead(zip.DestinationPath))
			Assert.Contains(GithubToken, ReadZipText(archive, "src/app.cs"), StringComparison.Ordinal);
		Assert.Null(zip.RedactionLegendPath);
		Assert.Equal(0, detector.CallCount);
	}

	[Fact]
	public async Task RepeatedRuns_AssignByteIdenticalPlaceholdersAndIndexes()
	{
		using var workspace = CreateWorkspace();
		var analyzer = new FileContentAnalyzer();
		var plan = await BuildPlanAsync(workspace.SourceRoot, hideSecrets: true);
		var first = await BuildContextDocumentsAsync(
			plan,
			analyzer,
			new SecretRedactionSession(new GitleaksSecretDetector()));
		var second = await BuildContextDocumentsAsync(
			plan,
			analyzer,
			new SecretRedactionSession(new GitleaksSecretDetector()));

		Assert.Equal(first.Keys, second.Keys);
		foreach (var format in first.Keys)
			Assert.Equal(first[format], second[format]);
		Assert.Contains(
			"DEVPROJEX_REDACTED[github-pat#1]",
			first[ProjectContextDocumentFormat.Text],
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task RedactedProjectCopy_PreservesSupportedTextEncodingsAndByteOrderMarks()
	{
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("encoded-project");
		var exportRoot = temporary.CreateDirectory("encoded-exports");
		var encodings = new (string Name, Encoding Encoding)[]
		{
			("utf8.txt", new UTF8Encoding(false, true)),
			("utf8-bom.txt", new UTF8Encoding(true, true)),
			("utf16-le.txt", new UnicodeEncoding(false, true, true)),
			("utf16-be.txt", new UnicodeEncoding(true, true, true)),
			("utf32-le.txt", new UTF32Encoding(false, true, true)),
			("utf32-be.txt", new UTF32Encoding(true, true, true))
		};
		foreach (var (name, encoding) in encodings)
		{
			var path = Path.Combine(sourceRoot, name);
			File.WriteAllText(path, $"awsAccessKey={AwsAccessKey}\r\nnext=value\n", encoding);
		}

		var binaryPath = Path.Combine(sourceRoot, "blob.bin");
		File.WriteAllBytes(binaryPath, [0, 1, 2, 0]);
		using var workspace = new Workspace(temporary, sourceRoot, exportRoot, binaryPath, ownsTemporary: false);
		var sourceBefore = CaptureSourceBytes(sourceRoot);
		var analyzer = new FileContentAnalyzer();
		var session = new SecretRedactionSession(new GitleaksSecretDetector());
		var plan = await BuildPlanAsync(sourceRoot, hideSecrets: true);

		var folder = await ExportProjectAsync(
			workspace,
			plan,
			analyzer,
			session,
			ProjectCopyExportFormat.Folder);

		foreach (var (name, encoding) in encodings)
		{
			var sourceBytes = File.ReadAllBytes(Path.Combine(sourceRoot, name));
			var outputBytes = File.ReadAllBytes(Path.Combine(folder.DestinationPath, name));
			Assert.Equal(encoding.GetPreamble(), outputBytes.AsSpan(0, encoding.GetPreamble().Length).ToArray());
			Assert.Equal(
				encoding.GetPreamble(),
				sourceBytes.AsSpan(0, encoding.GetPreamble().Length).ToArray());
			var outputText = encoding.GetString(outputBytes.AsSpan(encoding.GetPreamble().Length));
			Assert.DoesNotContain(AwsAccessKey, outputText, StringComparison.Ordinal);
			Assert.Contains("DEVPROJEX_REDACTED[aws-access-token#1]", outputText, StringComparison.Ordinal);
			Assert.Contains("\r\nnext=value\n", outputText, StringComparison.Ordinal);
		}

		AssertSourceBytesUnchanged(sourceBefore, sourceRoot);
	}

	[Fact]
	public async Task ProjectCopyLegend_UsesDeterministicNonCollidingRootName()
	{
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("legend-project");
		var exportRoot = temporary.CreateDirectory("legend-exports");
		temporary.CreateFile(
			"legend-project/DEVPROJEX_REDACTIONS.txt",
			"source-owned file\n");
		Directory.CreateDirectory(Path.Combine(sourceRoot, "DEVPROJEX_REDACTIONS-1.txt"));
		temporary.CreateFile(
			"legend-project/src/config.cs",
			$"const string token = \"{GithubToken}\";\n");
		var binaryPath = Path.Combine(sourceRoot, "blob.bin");
		File.WriteAllBytes(binaryPath, [0, 1, 0]);
		using var workspace = new Workspace(temporary, sourceRoot, exportRoot, binaryPath, ownsTemporary: false);
		var analyzer = new FileContentAnalyzer();
		var session = new SecretRedactionSession(new GitleaksSecretDetector());
		var plan = await BuildPlanAsync(sourceRoot, hideSecrets: true);

		var folder = await ExportProjectAsync(
			workspace,
			plan,
			analyzer,
			session,
			ProjectCopyExportFormat.Folder);
		var zip = await ExportProjectAsync(
			workspace,
			plan,
			analyzer,
			session,
			ProjectCopyExportFormat.Zip);

		Assert.Equal("DEVPROJEX_REDACTIONS-2.txt", folder.RedactionLegendPath);
		Assert.Equal("source-owned file\n", File.ReadAllText(
			Path.Combine(folder.DestinationPath, "DEVPROJEX_REDACTIONS.txt")));
		Assert.True(Directory.Exists(Path.Combine(folder.DestinationPath, "DEVPROJEX_REDACTIONS-1.txt")));
		Assert.True(File.Exists(Path.Combine(folder.DestinationPath, folder.RedactionLegendPath!)));
		Assert.Equal("DEVPROJEX_REDACTIONS-2.txt", zip.RedactionLegendPath);
		using var archive = ZipFile.OpenRead(zip.DestinationPath);
		Assert.Contains(
			archive.Entries,
			entry => entry.FullName.EndsWith("/DEVPROJEX_REDACTIONS-2.txt", StringComparison.Ordinal));
	}

	[Fact]
	public async Task OversizedSelectedText_FailsClosedBeforeWritingAnyOutput()
	{
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("oversized-project");
		var exportRoot = temporary.CreateDirectory("oversized-exports");
		var oversizedPath = Path.Combine(sourceRoot, "oversized.txt");
		await File.WriteAllTextAsync(
			oversizedPath,
			new string('a', checked((int)SecretRedactionOutputPreparer.MaximumScannableFileBytes + 1)),
			TestContext.Current.CancellationToken);
		var binaryPath = Path.Combine(sourceRoot, "blob.bin");
		File.WriteAllBytes(binaryPath, [0, 1, 0]);
		using var workspace = new Workspace(temporary, sourceRoot, exportRoot, binaryPath, ownsTemporary: false);
		var analyzer = new FileContentAnalyzer();
		var session = new SecretRedactionSession(new GitleaksSecretDetector());
		var plan = await BuildPlanAsync(sourceRoot, hideSecrets: true);
		var contextService = new ProjectContextDocumentService(
			new TreeExportService(),
			analyzer,
			secretRedactionSession: session);
		using var contextDestination = new MemoryStream();

		await Assert.ThrowsAsync<SecretScanLimitExceededException>(() =>
			contextService.WriteCompleteAsync(
				plan,
				ProjectContextView.Content,
				ProjectContextDocumentFormat.Text,
				contextDestination,
				TestContext.Current.CancellationToken,
				plain: true));
		Assert.Equal(0, contextDestination.Length);

		var copyDestination = Path.Combine(exportRoot, "copy");
		var copyException = await Assert.ThrowsAsync<ProjectCopyExportException>(() =>
			new ProjectCopyExportService(new ProjectCopyExportPlanBuilder(), analyzer, session)
				.ExportAsync(
					new ProjectCopyExportRequest(
						plan.SourceRoot,
						"project",
						plan.ProjectedTree,
						new HashSet<string>(PathComparer.Default),
						copyDestination,
						ProjectCopyExportFormat.Folder,
						ProjectCopyDestinationMode.Exact,
						RedactSecrets: true),
					cancellationToken: TestContext.Current.CancellationToken));
		Assert.Equal(ProjectCopyExportError.SecretScanLimitExceeded, copyException.Error);
		Assert.False(Path.Exists(copyDestination));
	}

	[Fact]
	public void LocalProfile_RoundTripsHideSecretsAndResolvesTheSameExclusion()
	{
		using var temporary = new TemporaryDirectory();
		var projectRoot = temporary.CreateDirectory("profile-project");
		var store = new ProjectProfileStore(() => temporary.CreateDirectory("profile-data"));
		var profile = new ProjectSelectionProfile(
			SelectedRootFolders: [],
			SelectedExtensions: [],
			SelectedIgnoreOptions: [IgnoreOptionId.HideSecrets],
			IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
			{
				[IgnoreOptionId.HideSecrets] = true,
				[IgnoreOptionId.SmartIgnore] = false
			});

		Assert.True(store.TrySaveProfile(projectRoot, profile));
		Assert.True(store.TryLoadProfile(projectRoot, out var loaded));
		Assert.Contains(IgnoreOptionId.HideSecrets, loaded.SelectedIgnoreOptions);
		Assert.True(loaded.IgnoreOptionStates![IgnoreOptionId.HideSecrets]);
		var selection = ProjectSelectionAdapter.FromLegacyProfile(
			loaded,
			ProjectProfileReference.Local);
		Assert.Contains(ProjectExclusion.HideSecrets, selection.Exclusions!);
	}

	[Fact]
	public async Task PreparedRedactionSnapshot_IsolatedFromLaterSourceMutationAndRemovedOnDispose()
	{
		using var workspace = CreateWorkspace();
		var analyzer = new FileContentAnalyzer();
		var session = new SecretRedactionSession(new GitleaksSecretDetector());
		var plan = await BuildPlanAsync(workspace.SourceRoot, hideSecrets: true);
		var prepared = await new SecretRedactionOutputPreparer(analyzer).PrepareAsync(
			new SecretRedactionContext(workspace.SourceRoot, session),
			plan.IncludedFiles,
			TestContext.Current.CancellationToken);
		var sourcePath = Path.Combine(workspace.SourceRoot, "src", "app.cs");
		var preparedPath = prepared.GetFile(sourcePath).ContentPath;
		var workingDirectory = Path.GetDirectoryName(preparedPath)!;
		Assert.True(Directory.Exists(workingDirectory));
		if (!OperatingSystem.IsWindows())
		{
			Assert.Equal(
				UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
				File.GetUnixFileMode(workingDirectory));
			Assert.Equal(
				UnixFileMode.UserRead | UnixFileMode.UserWrite,
				File.GetUnixFileMode(preparedPath));
		}

		await File.WriteAllTextAsync(
			sourcePath,
			$"const string changed = \"{GithubTokenSecond}\";\n",
			TestContext.Current.CancellationToken);
		var snapshotRead = await new PreparedSecretFileContentAnalyzer(analyzer, prepared)
			.ReadClassifiedAsync(
				sourcePath,
				SecretRedactionOutputPreparer.MaximumScannableFileBytes,
				TestContext.Current.CancellationToken);
		Assert.Equal(FileContentClassification.Text, snapshotRead.Classification);
		Assert.Contains(
			"DEVPROJEX_REDACTED[github-pat#",
			snapshotRead.Content!.Content,
			StringComparison.Ordinal);
		Assert.DoesNotContain(GithubToken, snapshotRead.Content.Content, StringComparison.Ordinal);
		Assert.DoesNotContain(GithubTokenSecond, snapshotRead.Content.Content, StringComparison.Ordinal);

		await prepared.DisposeAsync();

		Assert.False(Directory.Exists(workingDirectory));
	}

	[Fact]
	public async Task BoundedDocument_LegendCountsOnlyRedactionsPresentInThePayload()
	{
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("bounded-project");
		temporary.CreateFile("bounded-project/a-visible.cs", $"token={GithubToken}\n");
		temporary.CreateFile("bounded-project/z-omitted.cs", $"token={GithubTokenSecond}\n");
		var analyzer = new FileContentAnalyzer();
		var session = new SecretRedactionSession(new GitleaksSecretDetector());
		var plan = await BuildPlanAsync(sourceRoot, hideSecrets: true);
		var service = new ProjectContextDocumentService(
			new TreeExportService(),
			analyzer,
			secretRedactionSession: session);

		var document = await service.BuildAsync(
			plan,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Json,
			new ProjectContextDocumentLimits(
				MaximumTreeNodes: 10,
				MaximumFiles: 1,
				MaximumCharacters: 4096,
				MaximumFileBytes: 4096),
			TestContext.Current.CancellationToken);

		using var json = JsonDocument.Parse(document);
		Assert.Equal(1, json.RootElement.GetProperty("redaction").GetProperty("count").GetInt32());
		Assert.Contains("DEVPROJEX_REDACTED[github-pat#1]", document, StringComparison.Ordinal);
		Assert.DoesNotContain("DEVPROJEX_REDACTED[github-pat#2]", document, StringComparison.Ordinal);
	}

	[Fact]
	public async Task BoundedDocument_NeverCutsAGeneratedPlaceholderOrContinuesPastTheTruncatedFile()
	{
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("bounded-project");
		temporary.CreateFile("bounded-project/a.cs", $"value={GithubToken}");
		temporary.CreateFile("bounded-project/z-after.cs", "must-not-enter-bounded-prefix");
		var analyzer = new FileContentAnalyzer();
		var session = new SecretRedactionSession(new GitleaksSecretDetector());
		var plan = await BuildPlanAsync(sourceRoot, hideSecrets: true);
		var service = new ProjectContextDocumentService(
			new TreeExportService(),
			analyzer,
			secretRedactionSession: session);

		var document = await service.BuildAsync(
			plan,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Json,
			new ProjectContextDocumentLimits(
				MaximumTreeNodes: 10,
				MaximumFiles: 2,
				MaximumCharacters: 10,
				MaximumFileBytes: 4096),
			TestContext.Current.CancellationToken);

		using var json = JsonDocument.Parse(document);
		Assert.False(json.RootElement.TryGetProperty("redaction", out _));
		var file = Assert.Single(json.RootElement.GetProperty("files").EnumerateArray());
		Assert.Equal("a.cs", file.GetProperty("path").GetString());
		Assert.Equal("value=", file.GetProperty("content").GetString());
		Assert.DoesNotContain("DEVPROJEX_", document, StringComparison.Ordinal);
		Assert.DoesNotContain(GithubToken, document, StringComparison.Ordinal);
		Assert.DoesNotContain("must-not-enter", document, StringComparison.Ordinal);
	}

	private static async Task<ProjectContextPlan> BuildPlanAsync(string projectRoot, bool hideSecrets)
	{
		var analysisService = new ProjectAnalysisService(
			new ScanOptionsUseCase(new FileSystemScanner()),
			ProjectLoadWorkflowRuntime.CreateBuildTreeUseCase(),
			new FilterOptionSelectionService(),
			ProjectLoadWorkflowRuntime.CreateIgnoreOptionsService(),
			ProjectLoadWorkflowRuntime.CreateIgnoreRulesService(),
			new TreeExportService(),
			new FileContentAnalyzer());
		var exclusions = hideSecrets
			? new[] { ProjectExclusion.HideSecrets }
			: [];
		return await new ProjectContextPlanner(analysisService).BuildAsync(
			new ProjectContextRequest(
				projectRoot,
				new ProjectSelectionSpec(
					GitMode: GitFilteringMode.None,
					Exclusions: exclusions)),
			TestContext.Current.CancellationToken);
	}

	private static async Task<Dictionary<ProjectContextDocumentFormat, string>> BuildContextDocumentsAsync(
		ProjectContextPlan plan,
		IFileContentAnalyzer analyzer,
		SecretRedactionSession session)
	{
		var service = new ProjectContextDocumentService(
			new TreeExportService(),
			analyzer,
			secretRedactionSession: session);
		var output = new Dictionary<ProjectContextDocumentFormat, string>();
		foreach (var format in Enum.GetValues<ProjectContextDocumentFormat>())
			output[format] = await WriteContextAsync(service, plan, format);
		return output;
	}

	private static async Task<string> WriteContextAsync(
		ProjectContextDocumentService service,
		ProjectContextPlan plan,
		ProjectContextDocumentFormat format)
	{
		using var destination = new MemoryStream();
		await service.WriteCompleteAsync(
			plan,
			ProjectContextView.TreeContent,
			format,
			destination,
			TestContext.Current.CancellationToken,
			plain: true);
		return Encoding.UTF8.GetString(destination.ToArray());
	}

	private static async Task<ProjectCopyExportResult> ExportProjectAsync(
		Workspace workspace,
		ProjectContextPlan plan,
		IFileContentAnalyzer analyzer,
		SecretRedactionSession session,
		ProjectCopyExportFormat format,
		bool redactSecrets = true)
	{
		var destination = format == ProjectCopyExportFormat.Folder
			? Path.Combine(workspace.ExportRoot, $"folder-{Guid.NewGuid():N}")
			: Path.Combine(workspace.ExportRoot, $"archive-{Guid.NewGuid():N}.zip");
		return await new ProjectCopyExportService(
				new ProjectCopyExportPlanBuilder(),
				analyzer,
				session)
			.ExportAsync(
				new ProjectCopyExportRequest(
					plan.SourceRoot,
					"project",
					plan.ProjectedTree,
					new HashSet<string>(PathComparer.Default),
					destination,
					format,
					ProjectCopyDestinationMode.Exact,
					ProjectCopyConflictPolicy.Fail,
					RedactSecrets: redactSecrets),
				cancellationToken: TestContext.Current.CancellationToken);
	}

	private static void AssertNativeLegends(
		IReadOnlyDictionary<ProjectContextDocumentFormat, string> documents)
	{
		Assert.StartsWith("Values redacted by DevProjex before export: 4", documents[ProjectContextDocumentFormat.Text], StringComparison.Ordinal);
		Assert.StartsWith("<!--", documents[ProjectContextDocumentFormat.Markdown], StringComparison.Ordinal);
		using var json = JsonDocument.Parse(documents[ProjectContextDocumentFormat.Json]);
		Assert.Equal(4, json.RootElement.GetProperty("redaction").GetProperty("count").GetInt32());
		var xml = XDocument.Parse(documents[ProjectContextDocumentFormat.Xml]);
		Assert.Equal("4", xml.Root?.Element("redaction")?.Element("count")?.Value);
	}

	private static void AssertFolderCopy(Workspace workspace, ProjectCopyExportResult result)
	{
		Assert.NotNull(result.RedactionLegendPath);
		Assert.True(File.Exists(Path.Combine(result.DestinationPath, result.RedactionLegendPath!)));
		var appContent = File.ReadAllText(Path.Combine(result.DestinationPath, "src", "app.cs"));
		var documentationContent = File.ReadAllText(Path.Combine(result.DestinationPath, "docs", "example.md"));
		AssertNoTextSecret(appContent);
		AssertNoTextSecret(documentationContent);
		Assert.Contains(
			"DEVPROJEX_REDACTED[github-pat#2]",
			appContent,
			StringComparison.Ordinal);
		AssertNoTextSecret(File.ReadAllText(Path.Combine(result.DestinationPath, "config", "settings.json")));
		AssertNoTextSecret(File.ReadAllText(Path.Combine(result.DestinationPath, "secrets", "private.pem")));
		Assert.Contains(
			"DEVPROJEX_REDACTED[private-key#1]",
			File.ReadAllText(Path.Combine(result.DestinationPath, "secrets", "private.pem")),
			StringComparison.Ordinal);
		Assert.Contains(
			"DEVPROJEX_REDACTED[github-pat#1]",
			documentationContent,
			StringComparison.Ordinal);
		Assert.Equal(
			File.ReadAllBytes(workspace.BinaryPath),
			File.ReadAllBytes(Path.Combine(result.DestinationPath, "assets", "blob.bin")));
	}

	private static void AssertZipCopy(Workspace workspace, ProjectCopyExportResult result)
	{
		Assert.NotNull(result.RedactionLegendPath);
		using var archive = ZipFile.OpenRead(result.DestinationPath);
		var appContent = ReadZipText(archive, "src/app.cs");
		var documentationContent = ReadZipText(archive, "docs/example.md");
		AssertNoTextSecret(appContent);
		AssertNoTextSecret(documentationContent);
		Assert.Contains(
			"DEVPROJEX_REDACTED[github-pat#2]",
			appContent,
			StringComparison.Ordinal);
		AssertNoTextSecret(ReadZipText(archive, "config/settings.json"));
		var privateKey = ReadZipText(archive, "secrets/private.pem");
		AssertNoTextSecret(privateKey);
		Assert.Contains("DEVPROJEX_REDACTED[private-key#1]", privateKey, StringComparison.Ordinal);
		Assert.Contains(
			"DEVPROJEX_REDACTED[github-pat#1]",
			documentationContent,
			StringComparison.Ordinal);
		Assert.NotNull(archive.Entries.SingleOrDefault(entry =>
			entry.FullName.EndsWith(result.RedactionLegendPath!, StringComparison.Ordinal)));
		var binary = Assert.Single(archive.Entries, entry =>
			entry.FullName.EndsWith("assets/blob.bin", StringComparison.Ordinal));
		using var stream = binary.Open();
		using var bytes = new MemoryStream();
		stream.CopyTo(bytes);
		Assert.Equal(File.ReadAllBytes(workspace.BinaryPath), bytes.ToArray());
	}

	private static string ReadZipText(ZipArchive archive, string suffix)
	{
		var entry = Assert.Single(archive.Entries, item =>
			item.FullName.EndsWith(suffix, StringComparison.Ordinal));
		using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		return reader.ReadToEnd();
	}

	private static void AssertDecision(string text)
	{
		Assert.Equal(1, CountOccurrences(text, GithubToken));
		Assert.Contains("DEVPROJEX_REDACTED[github-pat#1]", text, StringComparison.Ordinal);
	}

	private static void AssertKeptOccurrence(string text)
	{
		Assert.Contains(GithubToken, text, StringComparison.Ordinal);
		Assert.DoesNotContain(SecretRedactionLegend.PlaceholderPrefix, text, StringComparison.Ordinal);
	}

	private static void AssertRedactedOccurrence(string text)
	{
		Assert.DoesNotContain(GithubToken, text, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED[github-pat#1]", text, StringComparison.Ordinal);
	}

	private static void AssertNoTextSecret(string text)
	{
		Assert.DoesNotContain(GithubToken, text, StringComparison.Ordinal);
		Assert.DoesNotContain(GithubTokenSecond, text, StringComparison.Ordinal);
		Assert.DoesNotContain(AwsAccessKey, text, StringComparison.Ordinal);
		// Checking the body separately prevents a header-only PEM replacement from passing.
		Assert.DoesNotContain(PrivateKeyBodyFragment, text, StringComparison.Ordinal);
	}

	private static void AssertExpectedPlaceholderIdentities(string text)
	{
		AssertNoTextSecret(text);
		Assert.Contains("DEVPROJEX_REDACTED[github-pat#1]", text, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED[github-pat#2]", text, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED[aws-access-token#1]", text, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED[private-key#1]", text, StringComparison.Ordinal);
	}

	private static int CountOccurrences(string value, string search)
	{
		var count = 0;
		for (var offset = 0; (offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0; offset += search.Length)
			count++;
		return count;
	}

	private static IReadOnlyDictionary<string, byte[]> CaptureSourceBytes(string sourceRoot) =>
		Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
			.ToDictionary(
				path => Path.GetRelativePath(sourceRoot, path).Replace('\\', '/'),
				File.ReadAllBytes,
				StringComparer.Ordinal);

	private static void AssertSourceBytesUnchanged(
		IReadOnlyDictionary<string, byte[]> before,
		string sourceRoot)
	{
		var after = CaptureSourceBytes(sourceRoot);
		Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
		foreach (var path in before.Keys)
			Assert.Equal(before[path], after[path]);
	}

	private static Workspace CreateWorkspace(bool repeatedGithubOnly = false)
	{
		var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("project");
		if (repeatedGithubOnly)
		{
			temporary.CreateFile("project/a-kept.cs", $"const string token = \"{GithubToken}\";\n");
			temporary.CreateFile("project/b-redacted.cs", $"const string token = \"{GithubToken}\";\n");
		}
		else
		{
			temporary.CreateFile("project/src/app.cs", $"const string token = \"{GithubToken}\";\n");
			temporary.CreateFile("project/config/settings.json", $"{{\"awsAccessKey\":\"{AwsAccessKey}\"}}\n");
			temporary.CreateFile("project/docs/example.md", $"token: {GithubTokenSecond}\n");
			temporary.CreateFile("project/secrets/private.pem", PrivateKeyPem + "\n");
		}

		var binaryPath = Path.Combine(sourceRoot, "assets", "blob.bin");
		Directory.CreateDirectory(Path.GetDirectoryName(binaryPath)!);
		File.WriteAllBytes(binaryPath, [0x00, 0x01, 0xFF, 0x42, 0x00, 0x7F]);
		return new Workspace(
			temporary,
			sourceRoot,
			temporary.CreateDirectory("exports"),
			binaryPath);
	}

	private sealed class CountingDetector : ISecretDetector
	{
		public int CallCount { get; private set; }

		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default)
		{
			CallCount++;
			throw new InvalidOperationException("The detector must not run while Hide Secrets is disabled.");
		}
	}

	private sealed class Workspace(
		TemporaryDirectory temporary,
		string sourceRoot,
		string exportRoot,
		string binaryPath,
		bool ownsTemporary = true) : IDisposable
	{
		public string SourceRoot { get; } = sourceRoot;
		public string ExportRoot { get; } = exportRoot;
		public string BinaryPath { get; } = binaryPath;
		public void Dispose()
		{
			if (ownsTemporary)
				temporary.Dispose();
		}
	}
}
