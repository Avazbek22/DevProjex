using System.IO.Compression;
using System.Globalization;
using DevProjex.Application.Compression;
using DevProjex.Application.Context;
using DevProjex.Application.Diagnostics;
using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.Compression;
using DevProjex.Infrastructure.Secrets;

namespace DevProjex.Tests.Integration;

public sealed class SecretRedactionOutputContractIntegrationTests
{
	private const string GithubToken = "ghp_" + "a7D9mQ2xK4vN8sR6tY3uW5zB1cE0fG2hJ9pL";
	private const string GithubTokenSecond = "ghp_" + "Z8y6X4w2V0u9T7s5R3q1P8n6M4k2J0h9G7f5";
	private const string AwsAccessKey = "AKIA" + "Z7M3Q5X2P6N4R7T5";
	private const string UriPassword = "u7!p";
	private const string ConnectionPassword = "p0st!";
	private const string ConfigurationPassword = "Admin123!";
	private const string EnvironmentSecret = "this is my signing key";
	private const string ContainerSecret = "container-pass-42";
	private const string AuthorizationCredential = "sanitized.header.token";
	private const string HttpSessionCookie = "opaque-session-value-41";
	private const string HttpKeptCookie = "opaque-csrf-value-52";
	private const string PrivateEmail = "employee" + "@corp.internal";
	private const string PrivateIpv4 = "93.184." + "216.34";
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
		var session = new SecretRedactionSession(CreateDetector());
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
				transformationContext: context);
		var previewPayload = PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(preview);
		using var contentPreview = await new PreviewDocumentBuilder(analyzer)
			.BuildContentDocumentAsync(
				plan.IncludedFiles,
				TestContext.Current.CancellationToken,
				TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(plan.SourceRoot),
				includeOmissionMarkers: false,
				transformationContext: context);
		var contentPreviewPayload = PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(contentPreview);
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

		Assert.Equal(10, preview.Redactions.Count);
		Assert.Equal(10, preview.Redactions.Count(static span =>
			span.State == SecretPreviewSpanState.Redacted));
		Assert.Equal(10, folder.RedactedValueCount);
		Assert.Equal(10, zip.RedactedValueCount);
		Assert.Equal(NormalizeForClipboard(selectedContent), contentPreviewPayload);
		Assert.All(
			new[] { previewPayload, contentPreviewPayload, selectedContent }.Concat(contextDocuments.Values),
			AssertNoTextSecret);
		Assert.All(
			new[] { previewPayload, contentPreviewPayload, selectedContent }.Concat(contextDocuments.Values),
			AssertExpectedPlaceholderIdentities);

		AssertNoOutputLegends(
			new[] { previewPayload, contentPreviewPayload, selectedContent }.Concat(contextDocuments.Values));
		AssertFolderCopy(workspace, folder);
		AssertZipCopy(workspace, zip);
		AssertSourceBytesUnchanged(sourceBefore, workspace.SourceRoot);
	}

	[Fact]
	public async Task KeepAsIsOverride_ChangesOnlyOneOccurrenceAndEveryOutputUsesThatDecision()
	{
		using var workspace = CreateWorkspace(repeatedGithubOnly: true);
		var analyzer = new FileContentAnalyzer();
		var session = new SecretRedactionSession(CreateDetector());
		var context = new SecretRedactionContext(workspace.SourceRoot, session);
		var plan = await BuildPlanAsync(workspace.SourceRoot, hideSecrets: true);
		var previewBuilder = new PreviewDocumentBuilder(analyzer);
		var initialLineCount = 0;
		var toggledLineNumber = 0;

		using (var initialPreview = await previewBuilder.BuildContentDocumentAsync(
			       plan.IncludedFiles,
			       TestContext.Current.CancellationToken,
			       TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(plan.SourceRoot),
			       includeOmissionMarkers: true,
			       transformationContext: context))
		{
			var occurrence = Assert.Single(
				initialPreview!.Redactions
					.GroupBy(static span => span.OccurrenceId, StringComparer.Ordinal)
					.First());
			initialLineCount = initialPreview.LineCount;
			toggledLineNumber = occurrence.LineNumber;
			Assert.True(session.ToggleKeepAsIs(occurrence.OccurrenceId));
		}

		using var decidedPreview = await previewBuilder.BuildContentDocumentAsync(
			plan.IncludedFiles,
			TestContext.Current.CancellationToken,
			TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(plan.SourceRoot),
			includeOmissionMarkers: true,
			transformationContext: context);
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

		Assert.Equal(1, decidedPreview!.Redactions.Count(static span =>
			span.State == SecretPreviewSpanState.Redacted));
		var keptSpan = Assert.Single(
			decidedPreview.Redactions,
			static span => span.State == SecretPreviewSpanState.KeptAsIs);
		Assert.Equal(initialLineCount, decidedPreview.LineCount);
		Assert.Equal(toggledLineNumber, keptSpan.LineNumber);
		AssertDecision(previewPayload);
		Assert.All(contextDocuments.Values, AssertDecision);
		AssertKeptOccurrence(File.ReadAllText(Path.Combine(folder.DestinationPath, "a-kept.cs")));
		AssertRedactedOccurrence(File.ReadAllText(Path.Combine(folder.DestinationPath, "b-redacted.cs")));
		using var archive = ZipFile.OpenRead(zip.DestinationPath);
		AssertKeptOccurrence(ReadZipText(archive, "a-kept.cs"));
		AssertRedactedOccurrence(ReadZipText(archive, "b-redacted.cs"));
	}

	[Fact]
	public async Task HttpCookieHeaders_KeepOneOccurrenceAndUseDeterministicIndexesAcrossEveryOutput()
	{
		var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("http-cookie-project");
		var exportRoot = temporary.CreateDirectory("http-cookie-exports");
		const string relativePath = "requests/session.http";
		var sourceContent =
			$"GET https://localhost/health\n" +
			$"Authorization: Bearer {AuthorizationCredential}\n" +
			$"Cookie: session={HttpSessionCookie}; csrf={HttpKeptCookie}; theme=dark\n";
		var sourcePath = temporary.CreateFile($"http-cookie-project/{relativePath}", sourceContent);
		var sourceBytes = File.ReadAllBytes(sourcePath);
		using var workspace = new Workspace(temporary, sourceRoot, exportRoot, sourcePath);
		var analyzer = new FileContentAnalyzer();
		var session = new SecretRedactionSession(CreateDetector());
		var context = new SecretRedactionContext(sourceRoot, session);
		var plan = await BuildPlanAsync(sourceRoot, hideSecrets: true);
		var previewBuilder = new PreviewDocumentBuilder(analyzer);

		using (var initialPreview = await previewBuilder.BuildContentDocumentAsync(
			       plan.IncludedFiles,
			       TestContext.Current.CancellationToken,
			       TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(sourceRoot),
			       includeOmissionMarkers: false,
			       transformationContext: context))
		{
			var cookieSpans = initialPreview!.Redactions
				.Where(static span => span.RuleId == "http-cookie")
				.OrderBy(static span => span.LineNumber)
				.ThenBy(static span => span.StartColumn)
				.ToArray();
			Assert.Equal(3, cookieSpans.Length);
			Assert.All(cookieSpans, static span => Assert.Equal(SecretPreviewSpanState.Redacted, span.State));
			AssertHttpCookieOutput(
				PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(initialPreview),
				keepCsrfCookie: false);
			Assert.True(session.ToggleKeepAsIs(cookieSpans[1].OccurrenceId));
		}

		using var decidedPreview = await previewBuilder.BuildContentDocumentAsync(
			plan.IncludedFiles,
			TestContext.Current.CancellationToken,
			TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(sourceRoot),
			includeOmissionMarkers: false,
			transformationContext: context);
		var previewAndClipboard = PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(decidedPreview);
		var selectedContent = await new SelectedContentExportService(analyzer).BuildAsync(
			plan.IncludedFiles,
			TestContext.Current.CancellationToken,
			TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(sourceRoot),
			context);
		var firstContextDocuments = await BuildContextDocumentsAsync(plan, analyzer, session);
		var secondContextDocuments = await BuildContextDocumentsAsync(plan, analyzer, session);
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
		var folderContent = File.ReadAllText(Path.Combine(folder.DestinationPath, "requests", "session.http"));
		string zipContent;
		using (var archive = ZipFile.OpenRead(zip.DestinationPath))
			zipContent = ReadZipText(archive, relativePath);

		Assert.Equal(4, decidedPreview!.Redactions.Count);
		Assert.Equal(3, decidedPreview.Redactions.Count(static span => span.State == SecretPreviewSpanState.Redacted));
		Assert.Single(decidedPreview.Redactions, static span =>
			span.RuleId == "http-cookie" && span.State == SecretPreviewSpanState.KeptAsIs);
		Assert.Equal(3, folder.RedactedValueCount);
		Assert.Equal(3, zip.RedactedValueCount);
		Assert.Equal(NormalizeForClipboard(selectedContent), previewAndClipboard);
		Assert.Equal(folderContent, zipContent);
		Assert.Equal(firstContextDocuments.Keys, secondContextDocuments.Keys);
		foreach (var format in firstContextDocuments.Keys)
			Assert.Equal(firstContextDocuments[format], secondContextDocuments[format]);

		var outputs = new[] { previewAndClipboard, selectedContent, folderContent, zipContent }
			.Concat(firstContextDocuments.Values);
		Assert.All(outputs, static output => AssertHttpCookieOutput(output, keepCsrfCookie: true));
		Assert.Equal(sourceBytes, File.ReadAllBytes(sourcePath));
	}

	[Theory]
	[InlineData(true, false)]
	[InlineData(false, true)]
	[InlineData(true, true)]
	public async Task RedactionFeatureModes_UseOneDecisionAcrossEveryOutputSurface(
		bool hideSecrets,
		bool hidePrivateData)
	{
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("redaction-feature-project");
		var exportRoot = temporary.CreateDirectory("redaction-feature-exports");
		const string relativePath = "requests/private.http";
		var content = $"GET https://localhost/health\n" +
		              $"Authorization: Bearer {AuthorizationCredential}\n" +
		              $"contact={PrivateEmail}\nserver={PrivateIpv4}\n";
		var sourcePath = temporary.CreateFile($"redaction-feature-project/{relativePath}", content);
		var sourceBytes = File.ReadAllBytes(sourcePath);
		using var workspace = new Workspace(temporary, sourceRoot, exportRoot, sourcePath);
		var analyzer = new FileContentAnalyzer();
		using var session = SecretRedactionSession.CreateWithPrivateData(CreateDetector(), new PrivateDataDetector());
		var features = SecretRedactionFeatureSelection.Resolve(hideSecrets, hidePrivateData);
		var context = new SecretRedactionContext(sourceRoot, session, features);
		var plan = await BuildPlanAsync(sourceRoot, hideSecrets, hidePrivateData);

		using var preview = await new PreviewDocumentBuilder(analyzer).BuildContentDocumentAsync(
			plan.IncludedFiles,
			TestContext.Current.CancellationToken,
			TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(sourceRoot),
			includeOmissionMarkers: false,
			transformationContext: context);
		var clipboard = PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(preview);
		var selectedContent = await new SelectedContentExportService(analyzer).BuildAsync(
			plan.IncludedFiles,
			TestContext.Current.CancellationToken,
			TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(sourceRoot),
			context);
		var contextDocuments = await BuildContextDocumentsAsync(plan, analyzer, session);
		var folder = await ExportProjectAsync(
			workspace,
			plan,
			analyzer,
			session,
			ProjectCopyExportFormat.Folder,
			hideSecrets,
			hidePrivateData);
		var zip = await ExportProjectAsync(
			workspace,
			plan,
			analyzer,
			session,
			ProjectCopyExportFormat.Zip,
			hideSecrets,
			hidePrivateData);
		var folderText = File.ReadAllText(Path.Combine(folder.DestinationPath, "requests", "private.http"));
		using var archive = ZipFile.OpenRead(zip.DestinationPath);
		var zipText = ReadZipText(archive, relativePath);
		var outputs = new[] { clipboard, selectedContent, folderText, zipText }
			.Concat(contextDocuments.Values)
			.ToArray();

		Assert.Equal(NormalizeForClipboard(selectedContent), clipboard);
		Assert.Equal(folderText, zipText);
		Assert.All(outputs, output =>
		{
			Assert.Equal(!hideSecrets, output.Contains(AuthorizationCredential, StringComparison.Ordinal));
			Assert.Equal(!hidePrivateData, output.Contains(PrivateEmail, StringComparison.Ordinal));
			Assert.Equal(!hidePrivateData, output.Contains(PrivateIpv4, StringComparison.Ordinal));
		});
		Assert.Equal(hideSecrets ? 1 : 0, preview!.Redactions.Count(span => span.RuleId == "authorization-bearer"));
		Assert.Equal(hidePrivateData ? 1 : 0, preview.Redactions.Count(span => span.RuleId == "email"));
		Assert.Equal(hidePrivateData ? 1 : 0, preview.Redactions.Count(span => span.RuleId == "ipv4"));
		Assert.Equal(sourceBytes, File.ReadAllBytes(sourcePath));
	}

	[Fact]
	public async Task CombinedRedaction_UsesOneFileReadAndOneContentFingerprint()
	{
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("single-pass-redaction-project");
		var path = temporary.CreateFile(
			"single-pass-redaction-project/request.http",
			$"Authorization: Bearer {AuthorizationCredential}\ncontact={PrivateEmail}\n");
		using var session = SecretRedactionSession.CreateWithPrivateData(CreateDetector(), new PrivateDataDetector());
		var context = new SecretRedactionContext(
			sourceRoot,
			session,
			SecretRedactionFeatures.Secrets | SecretRedactionFeatures.PrivateData);
		using var measurement = ContentPipelineDiagnostics.BeginMeasurement();

		var snapshot = await new SecretRedactionOutputPreparer(new FileContentAnalyzer()).AnalyzeAsync(
			context,
			[path],
			TestContext.Current.CancellationToken);
		var diagnostics = measurement.Capture();

		Assert.Equal(1, snapshot.SecretDetectedCount);
		Assert.Equal(1, snapshot.PrivateDataDetectedCount);
		Assert.Equal(1, diagnostics.FullFileReads);
		Assert.Equal(1, diagnostics.ContentFingerprintComputations);
	}

	[Fact]
	public async Task PrivateDataKeepAsIs_IsSharedByPreviewClipboardContextFolderAndZip()
	{
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("private-keep-project");
		var exportRoot = temporary.CreateDirectory("private-keep-exports");
		const string relativePath = "contact.txt";
		var content = $"contact={PrivateEmail}\nserver={PrivateIpv4}\n";
		var sourcePath = temporary.CreateFile($"private-keep-project/{relativePath}", content);
		using var workspace = new Workspace(temporary, sourceRoot, exportRoot, sourcePath);
		var analyzer = new FileContentAnalyzer();
		using var session = SecretRedactionSession.CreateWithPrivateData(CreateDetector(), new PrivateDataDetector());
		var context = new SecretRedactionContext(
			sourceRoot,
			session,
			SecretRedactionFeatures.PrivateData);
		var plan = await BuildPlanAsync(sourceRoot, hideSecrets: false, hidePrivateData: true);
		var previewBuilder = new PreviewDocumentBuilder(analyzer);

		using (var initial = await previewBuilder.BuildContentDocumentAsync(
			       plan.IncludedFiles,
			       TestContext.Current.CancellationToken,
			       TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(sourceRoot),
			       includeOmissionMarkers: false,
			       transformationContext: context))
		{
			var email = Assert.Single(initial!.Redactions, static span => span.RuleId == "email");
			Assert.True(session.ToggleKeepAsIs(email.OccurrenceId));
		}

		using var preview = await previewBuilder.BuildContentDocumentAsync(
			plan.IncludedFiles,
			TestContext.Current.CancellationToken,
			TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(sourceRoot),
			includeOmissionMarkers: false,
			transformationContext: context);
		var clipboard = PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(preview);
		var contextDocuments = await BuildContextDocumentsAsync(plan, analyzer, session);
		var folder = await ExportProjectAsync(
			workspace,
			plan,
			analyzer,
			session,
			ProjectCopyExportFormat.Folder,
			redactSecrets: false,
			redactPrivateData: true);
		var zip = await ExportProjectAsync(
			workspace,
			plan,
			analyzer,
			session,
			ProjectCopyExportFormat.Zip,
			redactSecrets: false,
			redactPrivateData: true);
		var folderText = File.ReadAllText(Path.Combine(folder.DestinationPath, relativePath));
		using var archive = ZipFile.OpenRead(zip.DestinationPath);
		var outputs = new[] { clipboard, folderText, ReadZipText(archive, relativePath) }
			.Concat(contextDocuments.Values);

		Assert.All(outputs, output =>
		{
			Assert.Contains(PrivateEmail, output, StringComparison.Ordinal);
			Assert.DoesNotContain(PrivateIpv4, output, StringComparison.Ordinal);
			Assert.Contains("DEVPROJEX_REDACTED[ipv4#1]", output, StringComparison.Ordinal);
		});
		Assert.Single(preview!.Redactions, static span =>
			span.RuleId == "email" && span.State == SecretPreviewSpanState.KeptAsIs);
	}

	[Fact]
	public async Task ExactOverlapCascade_UsesThePrivateFallbackIdenticallyAcrossEveryOutputSurface()
	{
		const string value = "shared-overlap-value";
		const string content = "value=" + value + "\n";
		const string relativePath = "overlap.txt";
		const string privatePlaceholder = "DEVPROJEX_REDACTED[private-overlap#1]";
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("overlap-project");
		var exportRoot = temporary.CreateDirectory("overlap-exports");
		var sourcePath = temporary.CreateFile($"overlap-project/{relativePath}", content);
		using var workspace = new Workspace(temporary, sourceRoot, exportRoot, sourcePath);
		var analyzer = new FileContentAnalyzer();
		using var session = SecretRedactionSession.CreateWithPrivateData(
			new CategorizedExactValueDetector(
				value,
				"secret-overlap",
				"catalog:smart-secrets-v4",
				RedactionFindingCategory.Secrets),
			new CategorizedExactValueDetector(
				value,
				"private-overlap",
				"private-data-v1",
				RedactionFindingCategory.PrivateData));
		var context = new SecretRedactionContext(
			sourceRoot,
			session,
			SecretRedactionFeatures.Secrets | SecretRedactionFeatures.PrivateData);
		var plan = await BuildPlanAsync(sourceRoot, hideSecrets: true, hidePrivateData: true);
		var previewBuilder = new PreviewDocumentBuilder(analyzer);

		using (var initial = await previewBuilder.BuildContentDocumentAsync(
			       plan.IncludedFiles,
			       TestContext.Current.CancellationToken,
			       TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(sourceRoot),
			       includeOmissionMarkers: false,
			       transformationContext: context))
		{
			var secret = Assert.Single(initial!.Redactions, static span => span.RuleId == "secret-overlap");
			Assert.True(session.ToggleKeepAsIs(secret.OccurrenceId));
		}

		using var preview = await previewBuilder.BuildContentDocumentAsync(
			plan.IncludedFiles,
			TestContext.Current.CancellationToken,
			TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(sourceRoot),
			includeOmissionMarkers: false,
			transformationContext: context);
		var clipboard = PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(preview);
		var contextDocuments = await BuildContextDocumentsAsync(plan, analyzer, session);
		var folder = await ExportProjectAsync(
			workspace,
			plan,
			analyzer,
			session,
			ProjectCopyExportFormat.Folder,
			redactSecrets: true,
			redactPrivateData: true);
		var zip = await ExportProjectAsync(
			workspace,
			plan,
			analyzer,
			session,
			ProjectCopyExportFormat.Zip,
			redactSecrets: true,
			redactPrivateData: true);
		var folderText = File.ReadAllText(Path.Combine(folder.DestinationPath, relativePath));
		using var archive = ZipFile.OpenRead(zip.DestinationPath);
		var outputs = new[] { clipboard, folderText, ReadZipText(archive, relativePath) }
			.Concat(contextDocuments.Values)
			.ToArray();

		Assert.All(outputs, output =>
		{
			Assert.Contains(privatePlaceholder, output, StringComparison.Ordinal);
			Assert.DoesNotContain(value, output, StringComparison.Ordinal);
			Assert.DoesNotContain("DEVPROJEX_REDACTED[secret-overlap", output, StringComparison.Ordinal);
		});
		Assert.Equal(folderText, ReadZipText(archive, relativePath));
		var privateSpan = Assert.Single(preview!.Redactions);
		Assert.Equal("private-overlap", privateSpan.RuleId);
		Assert.Equal(SecretPreviewSpanState.Redacted, privateSpan.State);
	}

	[Fact]
	public async Task SegmentedOverlap_ReusesOneCandidateIdentityAcrossEveryOutputSurface()
	{
		const string value = "private-prefix-SECRET-private-suffix";
		const string secretValue = "SECRET";
		const string content = "value=" + value + "\n";
		const string relativePath = "segmented-overlap.txt";
		const string privatePlaceholder = "DEVPROJEX_REDACTED[private-overlap#1]";
		const string secretPlaceholder = "DEVPROJEX_REDACTED[secret-overlap#1]";
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("segmented-project");
		var exportRoot = temporary.CreateDirectory("segmented-exports");
		var sourcePath = temporary.CreateFile($"segmented-project/{relativePath}", content);
		using var workspace = new Workspace(temporary, sourceRoot, exportRoot, sourcePath);
		var analyzer = new FileContentAnalyzer();
		using var session = SecretRedactionSession.CreateWithPrivateData(
			new CategorizedExactValueDetector(
				secretValue,
				"secret-overlap",
				"catalog:smart-secrets-v4",
				RedactionFindingCategory.Secrets),
			new CategorizedExactValueDetector(
				value,
				"private-overlap",
				"private-data-v1",
				RedactionFindingCategory.PrivateData));
		var context = new SecretRedactionContext(
			sourceRoot,
			session,
			SecretRedactionFeatures.Secrets | SecretRedactionFeatures.PrivateData);
		var plan = await BuildPlanAsync(sourceRoot, hideSecrets: true, hidePrivateData: true);
		var previewBuilder = new PreviewDocumentBuilder(analyzer);
		using var preview = await previewBuilder.BuildContentDocumentAsync(
			plan.IncludedFiles,
			TestContext.Current.CancellationToken,
			TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(sourceRoot),
			includeOmissionMarkers: false,
			transformationContext: context);
		var clipboard = PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(preview);
		var contextDocuments = await BuildContextDocumentsAsync(plan, analyzer, session);
		var folder = await ExportProjectAsync(
			workspace,
			plan,
			analyzer,
			session,
			ProjectCopyExportFormat.Folder,
			redactSecrets: true,
			redactPrivateData: true);
		var zip = await ExportProjectAsync(
			workspace,
			plan,
			analyzer,
			session,
			ProjectCopyExportFormat.Zip,
			redactSecrets: true,
			redactPrivateData: true);
		var folderText = File.ReadAllText(Path.Combine(folder.DestinationPath, relativePath));
		using var archive = ZipFile.OpenRead(zip.DestinationPath);
		var zipText = ReadZipText(archive, relativePath);
		var outputs = new[] { clipboard, folderText, zipText }
			.Concat(contextDocuments.Values)
			.ToArray();

		Assert.All(outputs, output =>
		{
			Assert.Equal(2, CountOccurrences(output, privatePlaceholder));
			Assert.Equal(1, CountOccurrences(output, secretPlaceholder));
			Assert.DoesNotContain(value, output, StringComparison.Ordinal);
		});
		Assert.Equal(folderText, zipText);
		Assert.Equal(3, preview!.Redactions.Count);
		var privateSpans = preview.Redactions.Where(static span => span.RuleId == "private-overlap").ToArray();
		Assert.Equal(2, privateSpans.Length);
		Assert.Single(privateSpans.Select(static span => span.OccurrenceId).Distinct(StringComparer.Ordinal));
	}

	[Fact]
	public async Task ProjectPlanFingerprint_ChangesWithHidePrivateDataAndIsStableForSameSelection()
	{
		using var workspace = CreateWorkspace(repeatedGithubOnly: true);

		var disabled = await BuildPlanAsync(workspace.SourceRoot, hideSecrets: false, hidePrivateData: false);
		var enabled = await BuildPlanAsync(workspace.SourceRoot, hideSecrets: false, hidePrivateData: true);
		var repeated = await BuildPlanAsync(workspace.SourceRoot, hideSecrets: false, hidePrivateData: true);

		Assert.NotEqual(disabled.Fingerprint, enabled.Fingerprint);
		Assert.Equal(enabled.Fingerprint, repeated.Fingerprint);
	}

	[Fact]
	public async Task DisabledRedaction_DoesNotInvokeDetectorOrChangeExistingOutput()
	{
		using var workspace = CreateWorkspace();
		var analyzer = new FileContentAnalyzer();
		var detector = new CountingDetector();
		var privateDataDetector = new CountingDetector();
		var session = SecretRedactionSession.CreateWithPrivateData(detector, privateDataDetector);
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
		using (var archive = ZipFile.OpenRead(zip.DestinationPath))
			Assert.Contains(GithubToken, ReadZipText(archive, "src/app.cs"), StringComparison.Ordinal);
		Assert.Equal(0, detector.CallCount);
		Assert.Equal(0, privateDataDetector.CallCount);
	}

	[Fact(Timeout = 10_000)]
	public async Task CountPipeline_CompletesSharedDetectorWarmUpBeforeReadingSourceContent()
	{
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("warmup-project");
		var path = temporary.CreateFile("warmup-project/config.txt", "ordinary content");
		var detector = new BlockingWarmUpDetector();
		var analyzer = new WarmUpGuardAnalyzer(new FileContentAnalyzer(), detector);
		using var session = new SecretRedactionSession(detector);
		var preparer = new SecretRedactionOutputPreparer(analyzer);
		var context = new SecretRedactionContext(sourceRoot, session);

		var operation = preparer.AnalyzeAsync(context, [path], TestContext.Current.CancellationToken);
		try
		{
			await detector.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
			Assert.Equal(0, analyzer.ReadCount);
			Assert.Same(session.BeginWarmUp(), session.BeginWarmUp());
		}
		finally
		{
			detector.Release();
		}

		await operation;
		Assert.Equal(1, detector.WarmUpCount);
		Assert.Equal(1, analyzer.ReadCount);
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
			new SecretRedactionSession(CreateDetector()));
		var second = await BuildContextDocumentsAsync(
			plan,
			analyzer,
			new SecretRedactionSession(CreateDetector()));

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
		var session = new SecretRedactionSession(CreateDetector());
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

	/// <summary>
	/// The scan limit is per file, so it costs the user that file - never the rest of the project.
	/// A document omits text this large whether Hide Secrets is on or off, so there is nothing to
	/// protect by refusing: the guarantee is that unscanned text never ships, and omitting it keeps
	/// that promise while still producing the output the user asked for.
	/// </summary>
	[Fact]
	public async Task OversizedSelectedText_IsOmittedWhileTheRestOfTheSelectionIsStillRedacted()
	{
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("oversized-project");
		var exportRoot = temporary.CreateDirectory("oversized-exports");
		await WriteOversizedProjectAsync(sourceRoot);
		using var workspace = new Workspace(
			temporary,
			sourceRoot,
			exportRoot,
			Path.Combine(sourceRoot, "blob.bin"),
			ownsTemporary: false);
		var analyzer = new FileContentAnalyzer();
		var session = new SecretRedactionSession(CreateDetector());
		var plan = await BuildPlanAsync(sourceRoot, hideSecrets: true);
		var contextService = new ProjectContextDocumentService(
			new TreeExportService(),
			analyzer,
			secretRedactionSession: session);
		using var contextDestination = new MemoryStream();

		await contextService.WriteCompleteAsync(
			plan,
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Text,
			contextDestination,
			TestContext.Current.CancellationToken,
			plain: true);

		var document = Encoding.UTF8.GetString(contextDestination.ToArray());
		Assert.NotEqual(0, contextDestination.Length);
		Assert.DoesNotContain(GithubToken, document, StringComparison.Ordinal);
		Assert.Contains("settings.txt", document, StringComparison.Ordinal);
		// The oversized file keeps its place in the document; only its text is absent.
		Assert.Contains("oversized.txt", document, StringComparison.Ordinal);
		Assert.DoesNotContain(new string('a', 4096), document, StringComparison.Ordinal);
	}

	/// <summary>
	/// A copy reproduces bytes, so it cannot ship text the scanner never read. Refusing the whole
	/// copy over one file is the worse trade: the user asked for a copy of the project and would
	/// get nothing. The file is left out instead, and the notice names it - dropping it silently is
	/// what the notice exists to prevent.
	/// </summary>
	[Fact]
	public async Task OversizedSelectedText_IsLeftOutOfTheProjectCopyAndNamedInTheNotice()
	{
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("oversized-copy-project");
		var exportRoot = temporary.CreateDirectory("oversized-copy-exports");
		await WriteOversizedProjectAsync(sourceRoot);
		using var workspace = new Workspace(
			temporary,
			sourceRoot,
			exportRoot,
			Path.Combine(sourceRoot, "blob.bin"),
			ownsTemporary: false);
		var analyzer = new FileContentAnalyzer();
		var session = new SecretRedactionSession(CreateDetector());
		var plan = await BuildPlanAsync(sourceRoot, hideSecrets: true);
		var copyDestination = Path.Combine(exportRoot, "copy");

		var result = await new ProjectCopyExportService(
				new ProjectCopyExportPlanBuilder(),
				analyzer,
				session)
			.ExportAsync(
				new ProjectCopyExportRequest(
					plan.SourceRoot,
					"project",
					plan.ProjectedTree,
					new HashSet<string>(PathComparer.Default),
					copyDestination,
					ProjectCopyExportFormat.Folder,
					ProjectCopyDestinationMode.Exact,
					RedactSecrets: true,
					NoticeText: new ProjectCopyNoticeText(
						"redaction notice",
						"compression notice",
						"excluded notice")),
				cancellationToken: TestContext.Current.CancellationToken);

		// The rest of the project is there, redacted.
		var settings = await File.ReadAllTextAsync(
			Path.Combine(result.DestinationPath, "settings.txt"),
			TestContext.Current.CancellationToken);
		Assert.DoesNotContain(GithubToken, settings, StringComparison.Ordinal);
		// The unreadable file is not, and no truncated stand-in was written in its place.
		Assert.False(File.Exists(Path.Combine(result.DestinationPath, "oversized.txt")));

		var notice = await File.ReadAllTextAsync(
			Path.Combine(result.DestinationPath, ProjectCopyExportService.TransformationNoticeFileName),
			TestContext.Current.CancellationToken);
		Assert.Contains("excluded notice", notice, StringComparison.Ordinal);
		Assert.Contains("oversized.txt", notice, StringComparison.Ordinal);
	}

	[Fact]
	public async Task TransformationNotice_EscapesControlCharactersInExcludedFileNames()
	{
		if (OperatingSystem.IsWindows())
			Assert.Skip("Windows does not allow line breaks in file names.");

		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("notice-path-project");
		var exportRoot = temporary.CreateDirectory("notice-path-exports");
		const string unsafeFileName = "legacy\nforged.txt";
		File.WriteAllBytes(
			Path.Combine(sourceRoot, unsafeFileName),
			[0xEF, 0xBB, 0xBF, 0xC3, 0x28]);
		var analyzer = new FileContentAnalyzer();
		using var session = new SecretRedactionSession(CreateDetector());
		var plan = await BuildPlanAsync(sourceRoot, hideSecrets: true);

		var result = await new ProjectCopyExportService(
				new ProjectCopyExportPlanBuilder(),
				analyzer,
				session)
			.ExportAsync(
				new ProjectCopyExportRequest(
					plan.SourceRoot,
					"project",
					plan.ProjectedTree,
					new HashSet<string>(PathComparer.Default),
					Path.Combine(exportRoot, "copy"),
					ProjectCopyExportFormat.Folder,
					ProjectCopyDestinationMode.Exact,
					RedactSecrets: true,
					NoticeText: new ProjectCopyNoticeText(
						"redaction notice",
						"compression notice",
						"excluded notice")),
				cancellationToken: TestContext.Current.CancellationToken);

		var notice = await File.ReadAllTextAsync(
			Path.Combine(result.DestinationPath, ProjectCopyExportService.TransformationNoticeFileName),
			TestContext.Current.CancellationToken);
		Assert.Contains("legacy\\nforged.txt", notice, StringComparison.Ordinal);
		Assert.DoesNotContain(unsafeFileName, notice, StringComparison.Ordinal);
	}

	/// <summary>
	/// The count behind the checkbox is advisory. A file it may not read is one file missing from
	/// the count, never a modal error and never a project the user cannot measure at all.
	/// </summary>
	[Fact]
	public async Task OversizedSelectedText_LeavesTheSecretCountScanAbleToFinish()
	{
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("oversized-count-project");
		await WriteOversizedProjectAsync(sourceRoot);
		var session = new SecretRedactionSession(CreateDetector());
		var plan = await BuildPlanAsync(sourceRoot, hideSecrets: true);

		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());
		var snapshot = await preparer.AnalyzeAsync(
			new SecretRedactionContext(sourceRoot, session),
			plan.IncludedFiles,
			TestContext.Current.CancellationToken);
		// The second pass is served from the scan cache. It must still know the file was never read,
		// or a repeated project-copy dry run would report readiness for a copy that then refuses.
		var cached = await preparer.AnalyzeAsync(
			new SecretRedactionContext(sourceRoot, session),
			plan.IncludedFiles,
			TestContext.Current.CancellationToken);

		Assert.True(
			snapshot.DetectedCount > 0,
			"the readable files must still contribute their findings to the count");
		Assert.Equal(1, snapshot.SkippedFileCount);
		Assert.Equal(0, snapshot.FailedFileCount);
		Assert.EndsWith("oversized.txt", snapshot.UnscannablePath);
		Assert.Equal(snapshot.SkippedFileCount, cached.SkippedFileCount);
		Assert.Equal(snapshot.UnscannablePath, cached.UnscannablePath);
	}

	[Fact]
	public async Task OversizedSelectedText_WithPrivateDataOnly_UsesTheSameLimitedCoverageContract()
	{
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("oversized-private-data-project");
		await WriteOversizedProjectAsync(sourceRoot);
		await File.WriteAllTextAsync(
			Path.Combine(sourceRoot, "contact.txt"),
			$"owner={PrivateEmail}\n",
			TestContext.Current.CancellationToken);
		using var session = SecretRedactionSession.CreateWithPrivateData(CreateDetector(), new PrivateDataDetector());
		var plan = await BuildPlanAsync(sourceRoot, hideSecrets: false, hidePrivateData: true);
		var context = new SecretRedactionContext(
			sourceRoot,
			session,
			SecretRedactionFeatures.PrivateData);
		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());

		var snapshot = await preparer.AnalyzeAsync(
			context,
			plan.IncludedFiles,
			TestContext.Current.CancellationToken);
		await using var prepared = await preparer.PrepareAsync(
			new ContentTransformationContext(null, context),
			plan.IncludedFiles,
			TestContext.Current.CancellationToken);

		Assert.Equal(0, snapshot.SecretDetectedCount);
		Assert.Equal(1, snapshot.PrivateDataDetectedCount);
		Assert.Equal(1, snapshot.PrivateDataRedactedCount);
		Assert.Equal(1, snapshot.SkippedFileCount);
		Assert.Equal(0, snapshot.FailedFileCount);
		Assert.EndsWith("oversized.txt", snapshot.UnscannablePath);
		var oversized = prepared.GetFile(Path.Combine(sourceRoot, "oversized.txt"));
		Assert.Equal(FileContentClassification.TooLarge, oversized.Classification);
		Assert.True(oversized.IsUnscannable);
		Assert.False(prepared.GetFile(Path.Combine(sourceRoot, "contact.txt")).IsUnscannable);
	}

	[Theory]
	[InlineData(true, false)]
	[InlineData(false, true)]
	public async Task UnsupportedEncoding_IsReportedAndWithheldAcrossContextFolderAndZip(
		bool hideSecrets,
		bool hidePrivateData)
	{
		const string legacySentinel = "LEGACY-CONTENT-MUST-NOT-ESCAPE";
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("unsupported-encoding-project");
		var exportRoot = temporary.CreateDirectory("unsupported-encoding-exports");
		var normalPath = temporary.CreateFile(
			"unsupported-encoding-project/config.txt",
			$"token={GithubToken}\nowner={PrivateEmail}\n");
		var legacyPath = Path.Combine(sourceRoot, "legacy-windows-1250.txt");
		var legacyPrefix = Encoding.ASCII.GetBytes(legacySentinel + "-");
		File.WriteAllBytes(legacyPath, [.. legacyPrefix, 0xE9, .. Encoding.ASCII.GetBytes("-END")]);
		var malformedBomPath = Path.Combine(sourceRoot, "malformed-utf8-bom.txt");
		File.WriteAllBytes(
			malformedBomPath,
			[0xEF, 0xBB, 0xBF, .. Encoding.ASCII.GetBytes(legacySentinel + "-"), 0xC3, 0x28]);
		var binaryPath = Path.Combine(sourceRoot, "payload.bin");
		var binaryBytes = new byte[] { 0x00, 0x01, 0xFF, 0x42 };
		File.WriteAllBytes(binaryPath, binaryBytes);
		using var workspace = new Workspace(
			temporary,
			sourceRoot,
			exportRoot,
			binaryPath,
			ownsTemporary: false);
		var analyzer = new FileContentAnalyzer();
		using var session = SecretRedactionSession.CreateWithPrivateData(
			CreateDetector(),
			new PrivateDataDetector());
		var features = SecretRedactionFeatureSelection.Resolve(hideSecrets, hidePrivateData);
		var redaction = new SecretRedactionContext(sourceRoot, session, features);
		var plan = await BuildPlanAsync(sourceRoot, hideSecrets, hidePrivateData);
		var preparer = new SecretRedactionOutputPreparer(analyzer);

		var analysis = await preparer.AnalyzeAsync(
			redaction,
			plan.IncludedFiles,
			TestContext.Current.CancellationToken);
		Assert.Equal(2, analysis.UnscannableFiles.Count);
		Assert.All(
			analysis.UnscannableFiles,
			file => Assert.Equal(FileContentClassification.UnsupportedEncoding, file.Classification));
		Assert.Contains(analysis.UnscannableFiles, file => PathComparer.Default.Equals(file.Path, legacyPath));
		Assert.Contains(analysis.UnscannableFiles, file => PathComparer.Default.Equals(file.Path, malformedBomPath));
		Assert.Equal(2, analysis.SkippedFileCount);
		Assert.Equal(0, analysis.FailedFileCount);

		var contextService = new ProjectContextDocumentService(
			new TreeExportService(),
			analyzer,
			secretRedactionSession: session);
		using var contextDestination = new MemoryStream();
		var contextReport = await contextService.WriteCompleteWithReportAsync(
			plan,
			ProjectContextView.TreeContent,
			ProjectContextDocumentFormat.Text,
			contextDestination,
			TestContext.Current.CancellationToken,
			plain: true);
		var contextText = Encoding.UTF8.GetString(contextDestination.ToArray());
		Assert.Equal(2, contextReport.UnscannableFiles.Count);
		Assert.All(
			contextReport.UnscannableFiles,
			file => Assert.Equal(FileContentClassification.UnsupportedEncoding, file.Classification));
		Assert.Contains("DEVPROJEX_REDACTED[", contextText, StringComparison.Ordinal);
		Assert.DoesNotContain(legacySentinel, contextText, StringComparison.Ordinal);

		var folder = await ExportProjectAsync(
			workspace,
			plan,
			analyzer,
			session,
			ProjectCopyExportFormat.Folder,
			hideSecrets,
			hidePrivateData);
		Assert.Equal(2, folder.UnscannableFiles!.Count);
		Assert.All(
			folder.UnscannableFiles,
			file => Assert.Equal(FileContentClassification.UnsupportedEncoding, file.Classification));
		Assert.False(File.Exists(Path.Combine(folder.DestinationPath, Path.GetFileName(legacyPath))));
		Assert.False(File.Exists(Path.Combine(folder.DestinationPath, Path.GetFileName(malformedBomPath))));
		Assert.Equal(binaryBytes, File.ReadAllBytes(Path.Combine(folder.DestinationPath, "payload.bin")));
		Assert.Contains(
			"DEVPROJEX_REDACTED[",
			File.ReadAllText(Path.Combine(folder.DestinationPath, Path.GetFileName(normalPath))),
			StringComparison.Ordinal);

		var zip = await ExportProjectAsync(
			workspace,
			plan,
			analyzer,
			session,
			ProjectCopyExportFormat.Zip,
			hideSecrets,
			hidePrivateData);
		Assert.Equal(2, zip.UnscannableFiles!.Count);
		Assert.All(
			zip.UnscannableFiles,
			file => Assert.Equal(FileContentClassification.UnsupportedEncoding, file.Classification));
		using var archive = ZipFile.OpenRead(zip.DestinationPath);
		Assert.Null(archive.GetEntry(Path.GetFileName(legacyPath)));
		Assert.Null(archive.GetEntry(Path.GetFileName(malformedBomPath)));
		Assert.Equal(binaryBytes, ReadZipBytes(archive, "payload.bin"));
		Assert.Contains(
			"DEVPROJEX_REDACTED[",
			ReadZipText(archive, Path.GetFileName(normalPath)),
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task StripComments_RemovesACommentSecretBeforeDetectionAndKeepsModeCachesIsolated()
	{
		const string secret = "comment-only-secret-value-42";
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("comment-secret-project");
		var path = temporary.CreateFile(
			"comment-secret-project/Commented.cs",
			$"// api_token = {secret}{Environment.NewLine}internal static class Commented {{ }}");
		var detector = new ExactValueDetector(secret);
		using var redactionSession = new SecretRedactionSession(detector);
		using var compressionSession = CodeCompressionFactory.CreateSession();
		var redaction = new SecretRedactionContext(sourceRoot, redactionSession);
		var plainContext = ContentTransformationContext.For(compression: null, redaction)!;
		var strippedContext = ContentTransformationContext.For(
			new CodeCompressionContext(
				sourceRoot,
				compressionSession,
				CodeTransformKinds.Comments),
			redaction)!;
		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());

		var plain = await preparer.AnalyzeAsync(
			plainContext,
			[path],
			TestContext.Current.CancellationToken);
		var stripped = await preparer.AnalyzeAsync(
			strippedContext,
			[path],
			TestContext.Current.CancellationToken);
		var plainAgain = await preparer.AnalyzeAsync(
			plainContext,
			[path],
			TestContext.Current.CancellationToken);

		Assert.Equal(1, plain.DetectedCount);
		Assert.Equal(1, plain.RedactedCount);
		Assert.Equal(0, stripped.DetectedCount);
		Assert.Equal(0, stripped.RedactedCount);
		Assert.Equal(plain.DetectedCount, plainAgain.DetectedCount);
		Assert.Equal(2, detector.CallCount);
		await using var prepared = await preparer.PrepareAsync(
			strippedContext,
			[path],
			TestContext.Current.CancellationToken);
		var output = await File.ReadAllTextAsync(
			prepared.GetFile(path).ContentPath,
			TestContext.Current.CancellationToken);
		Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
		Assert.Equal("internal static class Commented { }", output);
	}

	[Fact]
	public async Task StripComments_ReusesSecretFindingsWhenTheSyntaxPlanIsIdentity()
	{
		const string secret = "identity-secret-value-42";
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("identity-secret-project");
		var path = temporary.CreateFile(
			"identity-secret-project/State.cs",
			$"internal static class State {{ public const string Token = \"{secret}\"; }}");
		var detector = new ExactValueDetector(secret);
		using var redactionSession = new SecretRedactionSession(detector);
		using var compressionSession = CodeCompressionFactory.CreateSession();
		var redaction = new SecretRedactionContext(sourceRoot, redactionSession);
		var plainContext = ContentTransformationContext.For(compression: null, redaction)!;
		var strippedContext = ContentTransformationContext.For(
			new CodeCompressionContext(
				sourceRoot,
				compressionSession,
				CodeTransformKinds.Comments),
			redaction)!;
		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());

		var plain = await preparer.AnalyzeAsync(
			plainContext,
			[path],
			TestContext.Current.CancellationToken);
		var stripped = await preparer.AnalyzeAsync(
			strippedContext,
			[path],
			TestContext.Current.CancellationToken);

		Assert.Equal(1, plain.DetectedCount);
		Assert.Equal(1, stripped.DetectedCount);
		Assert.Equal(1, detector.CallCount);
	}

	[Fact]
	public async Task PrepareWithoutFindingCapturePublishesCountsWithoutMaterializingDescriptors()
	{
		const string secret = "capture-only-on-request-secret-value-42";
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("finding-capture-project");
		var path = temporary.CreateFile(
			"finding-capture-project/State.cs",
			$"internal static class State {{ public const string Token = \"{secret}\"; }}");
		using var redactionSession = new SecretRedactionSession(new ExactValueDetector(secret));
		var context = ContentTransformationContext.For(
			compression: null,
			redaction: new SecretRedactionContext(sourceRoot, redactionSession))!;
		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());

		await using var withoutCapture = await preparer.PrepareAsync(
			context,
			[path],
			TestContext.Current.CancellationToken);

		Assert.Equal(1, Assert.IsType<SecretRedactionSnapshot>(withoutCapture.Snapshot).DetectedCount);
		Assert.Empty(withoutCapture.GetEffectiveFindings());

		await using var withCapture = await preparer.PrepareAsync(
			context,
			[path],
			captureEffectiveFindings: true,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(1, Assert.IsType<SecretRedactionSnapshot>(withCapture.Snapshot).DetectedCount);
		Assert.Single(withCapture.GetEffectiveFindings());
	}

	[Fact]
	public async Task StripComments_KeepsLaterSecretOffsetsValidAfterBlankLineCollapse()
	{
		const string secret = "later-secret-value-42";
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("blank-line-secret-project");
		var path = temporary.CreateFile(
			"blank-line-secret-project/State.cs",
			"// removed header\n\n \n// removed details\n\t\n\n" +
			$"internal static class State {{ public const string Token = \"{secret}\"; }}");
		using var redactionSession = new SecretRedactionSession(new ExactValueDetector(secret));
		using var compressionSession = CodeCompressionFactory.CreateSession();
		var context = ContentTransformationContext.For(
			new CodeCompressionContext(
				sourceRoot,
				compressionSession,
				CodeTransformKinds.Comments),
			new SecretRedactionContext(sourceRoot, redactionSession))!;
		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());

		var snapshot = await preparer.AnalyzeAsync(
			context,
			[path],
			TestContext.Current.CancellationToken);
		await using var prepared = await preparer.PrepareAsync(
			context,
			[path],
			TestContext.Current.CancellationToken);
		var output = await File.ReadAllTextAsync(
			prepared.GetFile(path).ContentPath,
			TestContext.Current.CancellationToken);

		Assert.Equal(1, snapshot.DetectedCount);
		Assert.Equal(1, snapshot.RedactedCount);
		Assert.StartsWith("internal static class State", output, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED[exact-value#", output, StringComparison.Ordinal);
		Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("page.html", "<!-- api_token = {0} -->\n<main>safe</main>\n", "<main>safe</main>\n")]
	[InlineData("app.toml", "# api_token = {0}\nname = \"safe\"\n", "name = \"safe\"\n")]
	[InlineData("view.axaml", "<!-- api_token = {0} -->\n<Panel>safe</Panel>\n", "<Panel>safe</Panel>\n")]
	[InlineData("deployment.yaml", "# api_token = {0}\nname: safe\n", "name: safe\n")]
	public async Task StripComments_RemovesCommentSecretsFromNewLanguagePacksBeforeDetection(
		string fileName,
		string sourceTemplate,
		string expected)
	{
		const string secret = "comments-only-pack-secret-value-42";
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("comments-only-secret-project");
		var path = temporary.CreateFile(
			Path.Combine("comments-only-secret-project", fileName),
			string.Format(CultureInfo.InvariantCulture, sourceTemplate, secret));
		using var redactionSession = new SecretRedactionSession(new ExactValueDetector(secret));
		using var compressionSession = CodeCompressionFactory.CreateSession();
		var redaction = new SecretRedactionContext(sourceRoot, redactionSession);
		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());
		var plain = await preparer.AnalyzeAsync(
			ContentTransformationContext.For(compression: null, redaction)!,
			[path],
			TestContext.Current.CancellationToken);
		var strippedContext = ContentTransformationContext.For(
			new CodeCompressionContext(
				sourceRoot,
				compressionSession,
				CodeTransformKinds.Comments),
			redaction)!;
		var stripped = await preparer.AnalyzeAsync(
			strippedContext,
			[path],
			TestContext.Current.CancellationToken);

		Assert.Equal(1, plain.DetectedCount);
		Assert.Equal(0, stripped.DetectedCount);
		await using var prepared = await preparer.PrepareAsync(
			strippedContext,
			[path],
			TestContext.Current.CancellationToken);
		Assert.Equal(
			expected,
			await File.ReadAllTextAsync(
				prepared.GetFile(path).ContentPath,
				TestContext.Current.CancellationToken));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Discovery_ReportsOversizedTextAsLimitedCoverageInsteadOfFailure(
		bool compressionEnabled)
	{
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("oversized-discovery-project");
		await WriteOversizedProjectAsync(sourceRoot);
		using var redactionSession = new SecretRedactionSession(CreateDetector());
		using var compressionSession = compressionEnabled
			? new CodeCompressionSession(new UnsupportedCodeCompressor())
			: null;
		var redactionContext = new SecretRedactionContext(sourceRoot, redactionSession);
		var transformationContext = new ContentTransformationContext(
			compressionSession is null ? null : new CodeCompressionContext(sourceRoot, compressionSession),
			redactionContext);
		var plan = await BuildPlanAsync(sourceRoot, hideSecrets: true);
		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());

		var discovery = await preparer.DiscoverAsync(
			transformationContext,
			plan.IncludedFiles,
			TestContext.Current.CancellationToken);
		var cached = await preparer.DiscoverAsync(
			transformationContext,
			plan.IncludedFiles,
			SecretDiscoveryCacheMode.ReuseValidatedContent,
			TestContext.Current.CancellationToken);

		Assert.True(discovery.DetectedCount > 0);
		Assert.Equal(1, discovery.SkippedFileCount);
		Assert.Equal(0, discovery.FailedFileCount);
		Assert.Equal(1, discovery.IncompleteFileCount);
		Assert.True(discovery.HasLimitedCoverage);
		Assert.False(discovery.HasFailures);
		Assert.False(discovery.IsComplete);
		Assert.EndsWith("oversized.txt", discovery.UnscannablePath);
		Assert.Equal(discovery.SelectionKey, cached.SelectionKey);
		Assert.Equal(discovery.DetectedCount, cached.DetectedCount);
		Assert.Equal(discovery.RedactedCount, cached.RedactedCount);
		Assert.Equal(discovery.SkippedFileCount, cached.SkippedFileCount);
		Assert.Equal(discovery.FailedFileCount, cached.FailedFileCount);
		Assert.Equal(discovery.UnscannablePath, cached.UnscannablePath);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task GlobalPathAllowlist_DoesNotSuppressStructuredInspection(
		bool compressionEnabled)
	{
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("allowlisted-large-project");
		var svgPath = Path.Combine(sourceRoot, "diagram.svg");
		await WriteLargeSvgAsync(svgPath);
		var countingAnalyzer = new CountingSecretContentAnalyzer(new FileContentAnalyzer());
		using var redactionSession = new SecretRedactionSession(CreateDetector());
		using var compressionSession = compressionEnabled
			? new CodeCompressionSession(new UnsupportedCodeCompressor())
			: null;
		var transformationContext = new ContentTransformationContext(
			compressionSession is null ? null : new CodeCompressionContext(sourceRoot, compressionSession),
			new SecretRedactionContext(sourceRoot, redactionSession));
		var preparer = new SecretRedactionOutputPreparer(countingAnalyzer);

		var discovery = await preparer.DiscoverAsync(
			transformationContext,
			[svgPath],
			TestContext.Current.CancellationToken);

		Assert.Equal(1, countingAnalyzer.ContentReadCount);
		Assert.False(discovery.IsComplete);
		Assert.Equal(1, discovery.IncompleteFileCount);
		Assert.Equal(svgPath, discovery.UnscannablePath);
		var analysis = await preparer.AnalyzeAsync(
			transformationContext,
			[svgPath],
			TestContext.Current.CancellationToken);
		Assert.Equal(compressionEnabled ? 2 : 1, countingAnalyzer.ContentReadCount);
		Assert.False(analysis.IsComplete);
		Assert.Equal(1, analysis.IncompleteFileCount);
		await using var prepared = await preparer.PrepareAsync(
			transformationContext,
			[svgPath],
			TestContext.Current.CancellationToken);
		var preparedFile = prepared.GetFile(svgPath);

		Assert.Equal(FileContentClassification.TooLarge, preparedFile.Classification);
		Assert.True(preparedFile.IsUnscannable);
		Assert.Equal(svgPath, preparedFile.ContentPath);
		Assert.Equal(svgPath, Assert.Single(prepared.UnscannablePaths));
		Assert.NotNull(prepared.Snapshot);
		Assert.False(prepared.Snapshot!.IsComplete);
	}

	[Fact]
	public async Task GlobalPathAllowlist_DoesNotOverridePersistentManualSecretMarks()
	{
		const string manuallyMarked = "manual-svg-secret-42";
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("allowlisted-manual-project");
		var svgPath = temporary.CreateFile(
			"allowlisted-manual-project/diagram.svg",
			$"<svg><text>{manuallyMarked}</text></svg>");
		var countingAnalyzer = new CountingSecretContentAnalyzer(new FileContentAnalyzer());
		using var session = new SecretRedactionSession(CreateDetector());
		Assert.True(MarkedSecretValueNormalizer.TryCreate(
			manuallyMarked,
			out var normalized,
			out _));
		session.ReplaceMarkedSecrets([
			new MarkedSecretProfileEntry(normalized.Hash, null, normalized.Length)
		]);

		var snapshot = await new SecretRedactionOutputPreparer(countingAnalyzer).DiscoverAsync(
			new SecretRedactionContext(sourceRoot, session),
			[svgPath],
			TestContext.Current.CancellationToken);

		Assert.Equal(1, countingAnalyzer.ContentReadCount);
		Assert.Equal(1, snapshot.DetectedCount);
		Assert.Equal(1, snapshot.RedactedCount);
		Assert.True(snapshot.IsComplete);

		var preparer = new SecretRedactionOutputPreparer(countingAnalyzer);
		await using var prepared = await preparer.PrepareAsync(
			new ContentTransformationContext(
				null,
				new SecretRedactionContext(sourceRoot, session)),
			[svgPath],
			TestContext.Current.CancellationToken);
		var preparedContent = await preparer
			.CreatePreparedAnalyzer(prepared)
			.TryReadAsTextAsync(svgPath, TestContext.Current.CancellationToken);

		Assert.NotNull(preparedContent);
		Assert.DoesNotContain(manuallyMarked, preparedContent!.Content, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED", preparedContent.Content, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Discovery_SkipsOneMissingFileButStrictAnalysisStillFails(bool compressionEnabled)
	{
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("partial-discovery-project");
		var readablePath = temporary.CreateFile(
			"partial-discovery-project/settings.txt",
			$"token = {GithubToken}\n");
		var missingPath = Path.Combine(sourceRoot, "removed-during-scan.txt");
		using var redactionSession = new SecretRedactionSession(CreateDetector());
		using var compressionSession = compressionEnabled
			? new CodeCompressionSession(new UnsupportedCodeCompressor())
			: null;
		var redactionContext = new SecretRedactionContext(sourceRoot, redactionSession);
		var transformationContext = new ContentTransformationContext(
			compressionSession is null ? null : new CodeCompressionContext(sourceRoot, compressionSession),
			redactionContext);
		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());

		var discovery = await preparer.DiscoverAsync(
			transformationContext,
			[readablePath, missingPath],
			TestContext.Current.CancellationToken);

		Assert.True(discovery.DetectedCount > 0);
		Assert.Equal(0, discovery.SkippedFileCount);
		Assert.Equal(1, discovery.FailedFileCount);
		Assert.Equal(1, discovery.IncompleteFileCount);
		Assert.True(discovery.HasFailures);
		Assert.False(discovery.HasLimitedCoverage);
		Assert.False(discovery.IsComplete);
		await Assert.ThrowsAnyAsync<IOException>(() => preparer.AnalyzeAsync(
			redactionContext,
			[readablePath, missingPath],
			TestContext.Current.CancellationToken));
		await Assert.ThrowsAnyAsync<IOException>(() => preparer.PrepareAsync(
			transformationContext,
			[readablePath, missingPath],
			TestContext.Current.CancellationToken));
	}

	private static async Task WriteOversizedProjectAsync(string sourceRoot)
	{
		await File.WriteAllTextAsync(
			Path.Combine(sourceRoot, "oversized.txt"),
			new string('a', checked((int)SecretRedactionOutputPreparer.MaximumScannableFileBytes + 1)),
			TestContext.Current.CancellationToken);
		await File.WriteAllTextAsync(
			Path.Combine(sourceRoot, "settings.txt"),
			$"token = {GithubToken}\n",
			TestContext.Current.CancellationToken);
		File.WriteAllBytes(Path.Combine(sourceRoot, "blob.bin"), [0, 1, 0]);
	}

	private static async Task WriteLargeSvgAsync(string path)
	{
		var chunk = "<path d=\"M0 0h1v1z\"/>\n"u8.ToArray();
		await using var stream = new FileStream(
			path,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.None,
			bufferSize: 64 * 1024,
			useAsync: true);
		var remaining = SecretRedactionOutputPreparer.MaximumScannableFileBytes + 1;
		while (remaining > 0)
		{
			var count = (int)Math.Min(chunk.Length, remaining);
			await stream.WriteAsync(chunk.AsMemory(0, count), TestContext.Current.CancellationToken);
			remaining -= count;
		}
	}

	[Fact]
	public async Task OversizedSelectedBinary_PassesTheTextScanLimitUnchanged()
	{
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("oversized-binary-project");
		var binaryPath = Path.Combine(sourceRoot, "payload.asset");
		await using (var stream = new FileStream(
			binaryPath,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.None))
		{
			stream.WriteByte(0);
			stream.SetLength(SecretRedactionOutputPreparer.MaximumScannableFileBytes + 1);
		}

		using var session = new SecretRedactionSession(CreateDetector());
		var result = await new SecretRedactionOutputPreparer(new FileContentAnalyzer()).AnalyzeAsync(
			new SecretRedactionContext(sourceRoot, session),
			[binaryPath],
			TestContext.Current.CancellationToken);

		Assert.Equal(0, result.DetectedCount);
		Assert.Equal(0, result.RedactedCount);
		Assert.Equal(SecretRedactionOutputPreparer.MaximumScannableFileBytes + 1, new FileInfo(binaryPath).Length);
	}

	[Fact]
	public async Task PersistentManualMark_RedactsMarkdownAndPhysicalProjectCopy()
	{
		const string manuallyMarked = "ordinary-manual-value-42";
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("manual-project");
		var sourcePath = temporary.CreateFile(
			"manual-project/src/config.cs",
			$"const string value = \"{manuallyMarked}\";");
		var analyzer = new FileContentAnalyzer();
		using var session = new SecretRedactionSession(new NoFindingsDetector());
		Assert.True(MarkedSecretValueNormalizer.TryCreate(
			manuallyMarked,
			out var normalized,
			out _));
		session.ReplaceMarkedSecrets([
			new MarkedSecretProfileEntry(normalized.Hash, "value", normalized.Length)
		]);
		var plan = await BuildPlanAsync(sourceRoot, hideSecrets: true);
		var documentService = new ProjectContextDocumentService(
			new TreeExportService(),
			analyzer,
			secretRedactionSession: session);
		var markdown = await WriteContextAsync(
			documentService,
			plan,
			ProjectContextDocumentFormat.Markdown);
		var destination = Path.Combine(temporary.Path, "copy");
		var copy = await new ProjectCopyExportService(
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
					ProjectCopyExportFormat.Folder,
					ProjectCopyDestinationMode.Exact,
					ProjectCopyConflictPolicy.Fail,
					RedactSecrets: true),
				cancellationToken: TestContext.Current.CancellationToken);

		Assert.DoesNotContain(manuallyMarked, markdown, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED[manual-secret#1]", markdown, StringComparison.Ordinal);
		var copied = File.ReadAllText(Path.Combine(copy.DestinationPath, "src", "config.cs"));
		Assert.DoesNotContain(manuallyMarked, copied, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED[manual-secret#1]", copied, StringComparison.Ordinal);
		Assert.Equal($"const string value = \"{manuallyMarked}\";", File.ReadAllText(sourcePath));
	}

	[Theory]
	[InlineData(ManualRedactionClass.Secret, "manual-secret")]
	[InlineData(ManualRedactionClass.PrivateData, "manual-private-data")]
	public async Task ManualMarkClass_IsByteEquivalentAcrossPreviewClipboardContextFolderAndZip(
		ManualRedactionClass classification,
		string expectedRuleId)
	{
		const string manuallyMarked = "class-scoped-manual-value-42";
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("manual-class-project");
		var exportRoot = temporary.CreateDirectory("manual-class-exports");
		var sourcePath = temporary.CreateFile(
			"manual-class-project/src/config.cs",
			$"const string value = \"{manuallyMarked}\";");
		using var workspace = new Workspace(
			temporary,
			sourceRoot,
			exportRoot,
			sourcePath,
			ownsTemporary: false);
		var analyzer = new FileContentAnalyzer();
		using var session = SecretRedactionSession.CreateWithPrivateData(
			new NoFindingsDetector(),
			new NoFindingsDetector());
		Assert.True(MarkedSecretValueNormalizer.TryCreate(manuallyMarked, out var normalized, out _));
		session.ReplaceMarkedSecrets([
			new MarkedSecretProfileEntry(
				normalized.Hash,
				"value",
				normalized.Length,
				Class: classification)
		]);
		var hideSecrets = classification == ManualRedactionClass.Secret;
		var hidePrivateData = classification == ManualRedactionClass.PrivateData;
		var features = SecretRedactionFeatureSelection.Resolve(hideSecrets, hidePrivateData);
		var context = new SecretRedactionContext(sourceRoot, session, features);
		var plan = await BuildPlanAsync(sourceRoot, hideSecrets, hidePrivateData);

		using var preview = await new PreviewDocumentBuilder(analyzer).BuildContentDocumentAsync(
			plan.IncludedFiles,
			TestContext.Current.CancellationToken,
			TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(sourceRoot),
			includeOmissionMarkers: false,
			transformationContext: context);
		var previewAndClipboard = PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(preview);
		var selectedContent = await new SelectedContentExportService(analyzer).BuildAsync(
			plan.IncludedFiles,
			TestContext.Current.CancellationToken,
			TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(sourceRoot),
			context);
		var contextDocuments = await BuildContextDocumentsAsync(plan, analyzer, session);
		var folder = await ExportProjectAsync(
			workspace,
			plan,
			analyzer,
			session,
			ProjectCopyExportFormat.Folder,
			hideSecrets,
			hidePrivateData);
		var zip = await ExportProjectAsync(
			workspace,
			plan,
			analyzer,
			session,
			ProjectCopyExportFormat.Zip,
			hideSecrets,
			hidePrivateData);
		var placeholder = $"DEVPROJEX_REDACTED[{expectedRuleId}#1]";

		Assert.Equal(NormalizeForClipboard(selectedContent), previewAndClipboard);
		Assert.All(
			new[] { previewAndClipboard, selectedContent }.Concat(contextDocuments.Values),
			output => AssertClassScopedOutput(output));
		AssertClassScopedOutput(File.ReadAllText(Path.Combine(folder.DestinationPath, "src", "config.cs")));
		using var archive = ZipFile.OpenRead(zip.DestinationPath);
		AssertClassScopedOutput(ReadZipText(archive, "src/config.cs"));
		Assert.Equal($"const string value = \"{manuallyMarked}\";", File.ReadAllText(sourcePath));

		void AssertClassScopedOutput(string output)
		{
			Assert.DoesNotContain(manuallyMarked, output, StringComparison.Ordinal);
			Assert.Contains(placeholder, output, StringComparison.Ordinal);
		}
	}

	[Fact]
	public async Task PersistentManualMark_AfterStoreRestartRedactsEveryOutputSurface()
	{
		using var workspace = CreateWorkspace(repeatedGithubOnly: true);
		var appDataPath = workspace.ExportRoot;
		var firstStore = new ProjectProfileStore(() => appDataPath);
		Assert.True(firstStore.TrySaveProfile(
			workspace.SourceRoot,
			new ProjectSelectionProfile(
				SelectedRootFolders: [],
				SelectedExtensions: [],
				SelectedIgnoreOptions: [IgnoreOptionId.HideSecrets],
				IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
				{
					[IgnoreOptionId.HideSecrets] = true
				})));
		using (var identityProvider = new PersistentSecretIdentityProvider(() => appDataPath))
		{
			Assert.Equal(PersistentSecretIdentityAvailability.Ready, await identityProvider.EnsureAvailableAsync(TestContext.Current.CancellationToken));
			Assert.True(PersistentSecretIdentity.TryCreateV2(
				identityProvider,
				GithubToken,
				out var identity));
			var write = await firstStore.AddMarkAsync(
				workspace.SourceRoot,
				new MarkedSecretProfileEntry(identity, "token", GithubToken.Length),
				TestContext.Current.CancellationToken);
			Assert.True(write.Succeeded);
		}

		var reopenedStore = new ProjectProfileStore(() => appDataPath);
		var lookup = reopenedStore.LookupProfile(workspace.SourceRoot, TimeSpan.FromSeconds(1));
		Assert.Equal(ProjectProfileLookupStatus.Found, lookup.Status);
		Assert.Contains(IgnoreOptionId.HideSecrets, lookup.Profile!.SelectedIgnoreOptions);
		var loadedMarks = await reopenedStore.LoadMarksAsync(
			workspace.SourceRoot,
			TestContext.Current.CancellationToken);
		Assert.True(loadedMarks.Succeeded);
		using var session = new SecretRedactionSession(
			new NoFindingsDetector(),
			reopenedStore,
			new PersistentSecretIdentityProvider(() => appDataPath));
		session.ReplacePersistentMarks(workspace.SourceRoot, loadedMarks.Snapshot!);
		var analyzer = new FileContentAnalyzer();
		var context = new SecretRedactionContext(workspace.SourceRoot, session);
		var plan = await BuildPlanAsync(workspace.SourceRoot, hideSecrets: true);
		using var preview = await new PreviewDocumentBuilder(analyzer).BuildContentDocumentAsync(
			plan.IncludedFiles,
			TestContext.Current.CancellationToken,
			TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(plan.SourceRoot),
			includeOmissionMarkers: false,
			transformationContext: context);
		var previewAndClipboard = PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(preview);
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

		Assert.Equal(NormalizeForClipboard(selectedContent), previewAndClipboard);
		Assert.All(
			new[] { previewAndClipboard, selectedContent }.Concat(contextDocuments.Values),
			text =>
			{
				Assert.DoesNotContain(GithubToken, text, StringComparison.Ordinal);
				Assert.Contains("DEVPROJEX_REDACTED[manual-secret#", text, StringComparison.Ordinal);
			});
		Assert.DoesNotContain(
			GithubToken,
			File.ReadAllText(Path.Combine(folder.DestinationPath, "a-kept.cs")),
			StringComparison.Ordinal);
		Assert.DoesNotContain(
			GithubToken,
			File.ReadAllText(Path.Combine(folder.DestinationPath, "b-redacted.cs")),
			StringComparison.Ordinal);
		using var archive = ZipFile.OpenRead(zip.DestinationPath);
		Assert.DoesNotContain(GithubToken, ReadZipText(archive, "a-kept.cs"), StringComparison.Ordinal);
		Assert.DoesNotContain(GithubToken, ReadZipText(archive, "b-redacted.cs"), StringComparison.Ordinal);
	}

	[Fact]
	public async Task SourceBoundManualMark_AfterStoreRestartRedactsOnlySelectedOccurrenceEverywhere()
	{
		const string manualValue = "abcdefghij";
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("source-bound-project");
		var exportRoot = temporary.CreateDirectory("source-bound-exports");
		var sourceContent = $"const string first = \"{manualValue}\";\nconst string second = \"{manualValue}\";\n";
		var sourcePath = temporary.CreateFile("source-bound-project/src/config.cs", sourceContent);
		using var workspace = new Workspace(
			temporary,
			sourceRoot,
			exportRoot,
			sourcePath,
			ownsTemporary: false);
		var appDataPath = temporary.CreateDirectory("source-bound-app-data");
		var firstStore = new ProjectProfileStore(() => appDataPath);
		Assert.True(firstStore.TrySaveProfile(
			sourceRoot,
			new ProjectSelectionProfile(
				SelectedRootFolders: [],
				SelectedExtensions: [],
				SelectedIgnoreOptions: [IgnoreOptionId.HideSecrets],
				IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
				{
					[IgnoreOptionId.HideSecrets] = true
				})));
		using (var identityProvider = new PersistentSecretIdentityProvider(() => appDataPath))
		{
			Assert.Equal(
				PersistentSecretIdentityAvailability.Ready,
				await identityProvider.EnsureAvailableAsync(TestContext.Current.CancellationToken));
			Assert.True(PersistentSecretIdentity.TryCreateV2(identityProvider, manualValue, out var identity));
			var write = await firstStore.AddMarkAsync(
				sourceRoot,
				new MarkedSecretProfileEntry(
					identity,
					"first",
					manualValue.Length,
					"src/config.cs",
					sourceContent.IndexOf(manualValue, StringComparison.Ordinal)),
				TestContext.Current.CancellationToken);
			Assert.True(write.Succeeded);
		}

		var reopenedStore = new ProjectProfileStore(() => appDataPath);
		var loaded = await reopenedStore.LoadMarksAsync(sourceRoot, TestContext.Current.CancellationToken);
		Assert.True(loaded.Succeeded);
		using var identityProviderAfterRestart = new PersistentSecretIdentityProvider(() => appDataPath);
		Assert.Equal(
			PersistentSecretIdentityAvailability.Ready,
			await identityProviderAfterRestart.EnsureAvailableAsync(TestContext.Current.CancellationToken));
		using var session = new SecretRedactionSession(
			new NoFindingsDetector(),
			reopenedStore,
			identityProviderAfterRestart);
		session.ReplacePersistentMarks(sourceRoot, loaded.Snapshot!);
		var analyzer = new FileContentAnalyzer();
		var context = new SecretRedactionContext(sourceRoot, session);
		var plan = await BuildPlanAsync(sourceRoot, hideSecrets: true);
		using var preview = await new PreviewDocumentBuilder(analyzer).BuildContentDocumentAsync(
			plan.IncludedFiles,
			TestContext.Current.CancellationToken,
			TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(sourceRoot),
			includeOmissionMarkers: false,
			transformationContext: context);
		var previewAndClipboard = PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(preview);
		var selectedContent = await new SelectedContentExportService(analyzer).BuildAsync(
			plan.IncludedFiles,
			TestContext.Current.CancellationToken,
			TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(sourceRoot),
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

		Assert.Equal(NormalizeForClipboard(selectedContent), previewAndClipboard);
		Assert.All(
			new[] { previewAndClipboard, selectedContent }.Concat(contextDocuments.Values),
			AssertOnlySelectedOccurrenceIsRedacted);
		AssertOnlySelectedOccurrenceIsRedacted(
			File.ReadAllText(Path.Combine(folder.DestinationPath, "src", "config.cs")));
		using var archive = ZipFile.OpenRead(zip.DestinationPath);
		AssertOnlySelectedOccurrenceIsRedacted(ReadZipText(archive, "src/config.cs"));
		Assert.Equal(sourceContent, File.ReadAllText(sourcePath));

		static void AssertOnlySelectedOccurrenceIsRedacted(string output)
		{
			Assert.Equal(1, CountOccurrences(output, manualValue));
			Assert.Equal(1, CountOccurrences(output, "DEVPROJEX_REDACTED[manual-secret#1]"));
		}
	}

	[Fact]
	public async Task PendingPersistentMark_StaleStoreRefreshNeverLeaksAcrossOutputSurfaces()
	{
		const string manualValue = "ordinary-value-not-recognized-by-detector-77";
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("pending-project");
		var exportRoot = temporary.CreateDirectory("pending-exports");
		var sourcePath = temporary.CreateFile(
			"pending-project/src/config.cs",
			$"const string value = \"{manualValue}\";\n");
		using var workspace = new Workspace(
			temporary,
			sourceRoot,
			exportRoot,
			sourcePath,
			ownsTemporary: false);
		Assert.True(MarkedSecretValueNormalizer.TryCreate(manualValue, out var normalized, out _));
		var mark = new MarkedSecretProfileEntry(normalized.Hash, "value", normalized.Length);
		var store = new StaleSnapshotMarkStore(new PersistentSecretMarksSnapshot(11, []));
		using var session = new SecretRedactionSession(new NoFindingsDetector(), store);
		session.ReplacePersistentMarks(sourceRoot, store.Snapshot);
		var delta = PersistentSecretMarkDelta.Add(mark);
		Assert.True(session.StagePersistentMarkDelta(sourceRoot, delta).Staged);
		var analyzer = new FileContentAnalyzer();
		var context = new SecretRedactionContext(sourceRoot, session);
		var plan = await BuildPlanAsync(sourceRoot, hideSecrets: true);

		using var preview = await new PreviewDocumentBuilder(analyzer).BuildContentDocumentAsync(
			plan.IncludedFiles,
			TestContext.Current.CancellationToken,
			TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(sourceRoot),
			includeOmissionMarkers: false,
			transformationContext: context);
		var clipboard = PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(preview);
		var selectedContent = await new SelectedContentExportService(analyzer).BuildAsync(
			plan.IncludedFiles,
			TestContext.Current.CancellationToken,
			TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(sourceRoot),
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

		Assert.All(
			new[] { clipboard, selectedContent }.Concat(contextDocuments.Values),
			output => Assert.DoesNotContain(manualValue, output, StringComparison.Ordinal));
		Assert.DoesNotContain(
			manualValue,
			File.ReadAllText(Path.Combine(folder.DestinationPath, "src", "config.cs")),
			StringComparison.Ordinal);
		using var archive = ZipFile.OpenRead(zip.DestinationPath);
		Assert.DoesNotContain(manualValue, ReadZipText(archive, "src/config.cs"), StringComparison.Ordinal);
		Assert.True(store.LoadCount >= 4);
		Assert.Equal(mark, Assert.Single(session.GetMarkedSecrets()));
	}

	[Fact]
	public async Task StalePendingRemove_NewerDurableAddRemainsRedactedAcrossOutputSurfaces()
	{
		const string manualValue = "ordinary-value-not-recognized-by-detector-89";
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("stale-remove-project");
		var exportRoot = temporary.CreateDirectory("stale-remove-exports");
		var sourcePath = temporary.CreateFile(
			"stale-remove-project/src/config.cs",
			$"const string value = \"{manualValue}\";\n");
		using var workspace = new Workspace(
			temporary,
			sourceRoot,
			exportRoot,
			sourcePath,
			ownsTemporary: false);
		Assert.True(MarkedSecretValueNormalizer.TryCreate(manualValue, out var normalized, out _));
		var mark = new MarkedSecretProfileEntry(normalized.Hash, "value", normalized.Length);
		var identity = new PersistentSecretMarkId(mark.H, mark.Length);
		var initial = new PersistentSecretMarksSnapshot(
			10,
			[mark],
			new Dictionary<PersistentSecretMarkId, long> { [identity] = 10 });
		var refreshed = new PersistentSecretMarksSnapshot(
			11,
			[mark],
			new Dictionary<PersistentSecretMarkId, long> { [identity] = 11 });
		var store = new StaleSnapshotMarkStore(refreshed);
		using var session = new SecretRedactionSession(new NoFindingsDetector(), store);
		session.ReplacePersistentMarks(sourceRoot, initial);
		var remove = PersistentSecretMarkDelta.Remove(identity, observedRevision: 10);
		Assert.True(session.StagePersistentMarkDelta(sourceRoot, remove).EffectiveChanged);
		var analyzer = new FileContentAnalyzer();
		var context = new SecretRedactionContext(sourceRoot, session);
		var plan = await BuildPlanAsync(sourceRoot, hideSecrets: true);
		var contextDocuments = await BuildContextDocumentsAsync(plan, analyzer, session);

		using var preview = await new PreviewDocumentBuilder(analyzer).BuildContentDocumentAsync(
			plan.IncludedFiles,
			TestContext.Current.CancellationToken,
			TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(sourceRoot),
			includeOmissionMarkers: false,
			transformationContext: context);
		var clipboard = PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(preview);
		var selectedContent = await new SelectedContentExportService(analyzer).BuildAsync(
			plan.IncludedFiles,
			TestContext.Current.CancellationToken,
			TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(sourceRoot),
			context);
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

		Assert.All(
			new[] { clipboard, selectedContent }.Concat(contextDocuments.Values),
			output => Assert.DoesNotContain(manualValue, output, StringComparison.Ordinal));
		Assert.DoesNotContain(
			manualValue,
			File.ReadAllText(Path.Combine(folder.DestinationPath, "src", "config.cs")),
			StringComparison.Ordinal);
		using var archive = ZipFile.OpenRead(zip.DestinationPath);
		Assert.DoesNotContain(manualValue, ReadZipText(archive, "src/config.cs"), StringComparison.Ordinal);
	}

	[Fact]
	public void LocalProfile_RoundTripsHideSecretsAsContentTransformation()
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
		Assert.True(selection.HideSecrets);
		Assert.DoesNotContain(ProjectExclusion.HideSecrets, selection.Exclusions!);
	}

	[Fact]
	public async Task PreparedRedactionSnapshot_IsolatedFromLaterSourceMutationAndRemovedOnDispose()
	{
		using var workspace = CreateWorkspace();
		var analyzer = new FileContentAnalyzer();
		var session = new SecretRedactionSession(CreateDetector());
		var plan = await BuildPlanAsync(workspace.SourceRoot, hideSecrets: true);
		var prepared = await new SecretRedactionOutputPreparer(analyzer).PrepareAsync(
			new ContentTransformationContext(
				Compression: null,
				Redaction: new SecretRedactionContext(workspace.SourceRoot, session)),
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
	public async Task PreparedCleanRedactionSnapshot_IsolatedFromSecretInsertedAfterScan()
	{
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("clean-snapshot-project");
		var sourcePath = temporary.CreateFile("clean-snapshot-project/app.cs", "const string value = \"safe\";\n");
		var analyzer = new FileContentAnalyzer();
		var session = new SecretRedactionSession(CreateDetector());
		await using var prepared = await new SecretRedactionOutputPreparer(analyzer).PrepareAsync(
			new ContentTransformationContext(
				Compression: null,
				Redaction: new SecretRedactionContext(sourceRoot, session)),
			[sourcePath],
			TestContext.Current.CancellationToken);

		await File.WriteAllTextAsync(
			sourcePath,
			$"const string value = \"{GithubToken}\";\n",
			TestContext.Current.CancellationToken);
		var snapshotRead = await new PreparedSecretFileContentAnalyzer(analyzer, prepared)
			.ReadClassifiedAsync(
				sourcePath,
				SecretRedactionOutputPreparer.MaximumScannableFileBytes,
				TestContext.Current.CancellationToken);

		Assert.Equal(FileContentClassification.Text, snapshotRead.Classification);
		Assert.Contains("\"safe\"", snapshotRead.Content!.Content, StringComparison.Ordinal);
		Assert.DoesNotContain(GithubToken, snapshotRead.Content.Content, StringComparison.Ordinal);
	}

	[Fact]
	public async Task BoundedDocument_NeverCutsAGeneratedPlaceholderOrContinuesPastTheTruncatedFile()
	{
		using var temporary = new TemporaryDirectory();
		var sourceRoot = temporary.CreateDirectory("bounded-project");
		temporary.CreateFile("bounded-project/a.cs", $"value={GithubToken}");
		temporary.CreateFile("bounded-project/z-after.cs", "must-not-enter-bounded-prefix");
		var analyzer = new FileContentAnalyzer();
		var session = new SecretRedactionSession(CreateDetector());
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

	private static async Task<ProjectContextPlan> BuildPlanAsync(
		string projectRoot,
		bool hideSecrets,
		bool hidePrivateData = false)
	{
		var analysisService = new ProjectAnalysisService(
			new ScanOptionsUseCase(new FileSystemScanner()),
			ProjectLoadWorkflowRuntime.CreateBuildTreeUseCase(),
			new FilterOptionSelectionService(),
			ProjectLoadWorkflowRuntime.CreateIgnoreOptionsService(),
			ProjectLoadWorkflowRuntime.CreateIgnoreRulesService(),
			new TreeExportService(),
			new FileContentAnalyzer());
		return await new ProjectContextPlanner(analysisService).BuildAsync(
			new ProjectContextRequest(
				projectRoot,
				new ProjectSelectionSpec(
					GitMode: GitFilteringMode.None,
					Exclusions: [],
					HideSecrets: hideSecrets,
					HidePrivateData: hidePrivateData)),
			TestContext.Current.CancellationToken);
	}

	private sealed class NoFindingsDetector : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) => [];
	}

	private sealed class StaleSnapshotMarkStore(PersistentSecretMarksSnapshot snapshot)
		: IPersistentSecretMarkStore
	{
		public PersistentSecretMarksSnapshot Snapshot { get; } = snapshot;
		public int LoadCount { get; private set; }

		public ValueTask<PersistentSecretMarksLoadResult> LoadMarksAsync(
			string localProjectPath,
			CancellationToken cancellationToken = default)
		{
			LoadCount++;
			return ValueTask.FromResult(new PersistentSecretMarksLoadResult(
				PersistentSecretMarkStoreStatus.Success,
				Snapshot));
		}

		public ValueTask<PersistentSecretMarkWriteResult> AddMarkAsync(
			string localProjectPath,
			MarkedSecretProfileEntry mark,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public ValueTask<PersistentSecretMarkWriteResult> RemoveMarkAsync(
			string localProjectPath,
			PersistentSecretMarkId markId,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public ValueTask<PersistentSecretMarkWriteResult> ApplyMarkDeltaAsync(
			string localProjectPath,
			PersistentSecretMarkDelta delta,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class ExactValueDetector(string secret) : ISecretDetector
	{
		public int CallCount { get; private set; }

		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default)
		{
			CallCount++;
			var index = content.IndexOf(secret, StringComparison.Ordinal);
			return index < 0
				? []
				: [new DetectedSecret("exact-value", index, secret.Length, secret, RuleOrder: 0)];
		}
	}

	private sealed class UnsupportedCodeCompressor : ICodeCompressor
	{
		public string TransformIdentity => "unsupported:test:v1";

		public bool IsSupported(string relativePath) => false;

		public ICodeCompressionScope CreateScope(string projectRoot) => new Scope();

		private sealed class Scope : ICodeCompressionScope
		{
			public CodeCompressionAnalysis Analyze(
				string fullPath,
				string relativePath,
				string content,
				CancellationToken cancellationToken) =>
				throw new InvalidOperationException("Unsupported files must not reach the compressor.");

			public void Dispose()
			{
			}
		}
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
		bool redactSecrets = true,
		bool redactPrivateData = false)
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
					RedactSecrets: redactSecrets,
					RedactPrivateData: redactPrivateData),
				cancellationToken: TestContext.Current.CancellationToken);
	}

	private static void AssertNoOutputLegends(IEnumerable<string> outputs)
	{
		Assert.All(outputs, output =>
		{
			Assert.DoesNotContain("Values redacted by DevProjex", output, StringComparison.Ordinal);
			Assert.DoesNotContain("Placeholders like DEVPROJEX_REDACTED", output, StringComparison.Ordinal);
			Assert.DoesNotContain("Do not treat placeholder text as a real value.", output, StringComparison.Ordinal);
		});
	}

	private static void AssertFolderCopy(Workspace workspace, ProjectCopyExportResult result)
	{
		Assert.Empty(Directory.EnumerateFiles(
			result.DestinationPath,
			"DEVPROJEX_REDACTIONS*.txt",
			SearchOption.TopDirectoryOnly));
		var appContent = File.ReadAllText(Path.Combine(result.DestinationPath, "src", "app.cs"));
		var documentationContent = File.ReadAllText(Path.Combine(result.DestinationPath, "docs", "example.md"));
		AssertNoTextSecret(appContent);
		AssertNoTextSecret(documentationContent);
		Assert.Contains(
			"DEVPROJEX_REDACTED[github-pat#2]",
			appContent,
			StringComparison.Ordinal);
		AssertNoTextSecret(File.ReadAllText(Path.Combine(result.DestinationPath, "config", "settings.json")));
		var connection = File.ReadAllText(Path.Combine(result.DestinationPath, "config", "appsettings.json"));
		AssertNoTextSecret(connection);
		Assert.Contains(
			"Host=db;Username=admin;Pass" +
			"word=DEVPROJEX_REDACTED[connection-password#1];Database=app",
			connection,
			StringComparison.Ordinal);
		AssertNoTextSecret(File.ReadAllText(Path.Combine(result.DestinationPath, "config", "service.txt")));
		AssertNoTextSecret(File.ReadAllText(Path.Combine(result.DestinationPath, "config", "web.config")));
		AssertNoTextSecret(File.ReadAllText(Path.Combine(result.DestinationPath, ".env")));
		var dockerfile = File.ReadAllText(Path.Combine(result.DestinationPath, "container", "service.dockerfile"));
		AssertNoTextSecret(dockerfile);
		Assert.Contains("ENV DB_PASSWORD=DEVPROJEX_REDACTED[container-secret#1]", dockerfile, StringComparison.Ordinal);
		var request = File.ReadAllText(Path.Combine(result.DestinationPath, "requests", "health.http"));
		AssertNoTextSecret(request);
		Assert.Contains("Authorization: Bearer DEVPROJEX_REDACTED[authorization-bearer#1]", request, StringComparison.Ordinal);
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
		var connection = ReadZipText(archive, "config/appsettings.json");
		AssertNoTextSecret(connection);
		Assert.Contains(
			"Host=db;Username=admin;Pass" +
			"word=DEVPROJEX_REDACTED[connection-password#1];Database=app",
			connection,
			StringComparison.Ordinal);
		AssertNoTextSecret(ReadZipText(archive, "config/service.txt"));
		AssertNoTextSecret(ReadZipText(archive, "config/web.config"));
		AssertNoTextSecret(ReadZipText(archive, ".env"));
		var dockerfile = ReadZipText(archive, "container/service.dockerfile");
		AssertNoTextSecret(dockerfile);
		Assert.Contains("ENV DB_PASSWORD=DEVPROJEX_REDACTED[container-secret#1]", dockerfile, StringComparison.Ordinal);
		var request = ReadZipText(archive, "requests/health.http");
		AssertNoTextSecret(request);
		Assert.Contains("Authorization: Bearer DEVPROJEX_REDACTED[authorization-bearer#1]", request, StringComparison.Ordinal);
		var privateKey = ReadZipText(archive, "secrets/private.pem");
		AssertNoTextSecret(privateKey);
		Assert.Contains("DEVPROJEX_REDACTED[private-key#1]", privateKey, StringComparison.Ordinal);
		Assert.Contains(
			"DEVPROJEX_REDACTED[github-pat#1]",
			documentationContent,
			StringComparison.Ordinal);
		Assert.DoesNotContain(archive.Entries, entry =>
			Path.GetFileName(entry.FullName).StartsWith("DEVPROJEX_REDACTIONS", StringComparison.Ordinal));
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

	private sealed class CategorizedExactValueDetector(
		string value,
		string ruleId,
		string rulesIdentity,
		RedactionFindingCategory category) : ISecretDetector
	{
		public string RulesIdentity => rulesIdentity;

		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default)
		{
			var index = content.IndexOf(value, StringComparison.Ordinal);
			return index < 0
				? []
				: [new DetectedSecret(ruleId, index, value.Length, value, 0, Category: category)];
		}
	}

	private static byte[] ReadZipBytes(ZipArchive archive, string suffix)
	{
		var entry = Assert.Single(archive.Entries, item =>
			item.FullName.EndsWith(suffix, StringComparison.Ordinal));
		using var source = entry.Open();
		using var destination = new MemoryStream();
		source.CopyTo(destination);
		return destination.ToArray();
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
		Assert.DoesNotContain(UriPassword, text, StringComparison.Ordinal);
		Assert.DoesNotContain(ConnectionPassword, text, StringComparison.Ordinal);
		Assert.DoesNotContain(ConfigurationPassword, text, StringComparison.Ordinal);
		Assert.DoesNotContain(EnvironmentSecret, text, StringComparison.Ordinal);
		Assert.DoesNotContain(ContainerSecret, text, StringComparison.Ordinal);
		Assert.DoesNotContain(AuthorizationCredential, text, StringComparison.Ordinal);
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
		Assert.Contains("DEVPROJEX_REDACTED[credential-uri-password#1]", text, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED[connection-password#1]", text, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED[config-secret#1]", text, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED[environment-secret#1]", text, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED[container-secret#1]", text, StringComparison.Ordinal);
		Assert.Contains("DEVPROJEX_REDACTED[authorization-bearer#1]", text, StringComparison.Ordinal);
	}

	private static void AssertHttpCookieOutput(string text, bool keepCsrfCookie)
	{
		var csrf = keepCsrfCookie
			? HttpKeptCookie
			: "DEVPROJEX_REDACTED[http-cookie#2]";
		Assert.Contains(
			"Authorization: Bearer DEVPROJEX_REDACTED[authorization-bearer#1]",
			text,
			StringComparison.Ordinal);
		Assert.Contains(
			$"Cookie: session=DEVPROJEX_REDACTED[http-cookie#1]; csrf={csrf}; " +
			"theme=DEVPROJEX_REDACTED[http-cookie#3]",
			text,
			StringComparison.Ordinal);
		Assert.DoesNotContain(AuthorizationCredential, text, StringComparison.Ordinal);
		Assert.DoesNotContain(HttpSessionCookie, text, StringComparison.Ordinal);
		Assert.DoesNotContain("theme=dark", text, StringComparison.Ordinal);
		if (keepCsrfCookie)
			Assert.DoesNotContain("DEVPROJEX_REDACTED[http-cookie#2]", text, StringComparison.Ordinal);
		else
			Assert.DoesNotContain(HttpKeptCookie, text, StringComparison.Ordinal);
	}

	private static int CountOccurrences(string value, string search)
	{
		var count = 0;
		for (var offset = 0; (offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0; offset += search.Length)
			count++;
		return count;
	}

	private static string NormalizeForClipboard(string text)
	{
		var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
		return Environment.NewLine == "\n"
			? normalized
			: normalized.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
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
			temporary.CreateFile(
				"project/config/appsettings.json",
				$"{{\"ConnectionStrings\":{{\"Main\":\"Host=db;Username=admin;Pass" +
				$"word={ConnectionPassword};Database=app\"}}}}\n");
			temporary.CreateFile(
				"project/config/service.txt",
				$"postgres:" + $"//admin:{UriPassword}@db.local/app\n");
			temporary.CreateFile(
				"project/config/web.config",
				$"<appSettings><add key=\"Password\" value=\"{ConfigurationPassword}\" /></appSettings>\n");
			temporary.CreateFile("project/.env", $"JWT_SECRET=\"{EnvironmentSecret}\"\n");
			temporary.CreateFile(
				"project/container/service.dockerfile",
				$"FROM scratch\nENV DB_PASSWORD={ContainerSecret}\n");
			temporary.CreateFile(
				"project/requests/health.http",
				$"GET https://localhost/health\nAuthorization: Bearer {AuthorizationCredential}\n");
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

	private static SmartSecretsDetector CreateDetector() =>
		new(
			new GitleaksSecretDetector(),
			new SmartIgnoreService(
			[
				new CommonSmartIgnoreRule(),
				new FrontendArtifactsIgnoreRule(),
				new DotNetArtifactsIgnoreRule(),
				new PythonArtifactsIgnoreRule(),
				new JvmArtifactsIgnoreRule(),
				new RustArtifactsIgnoreRule(),
				new GoArtifactsIgnoreRule(),
				new PhpArtifactsIgnoreRule(),
				new RubyArtifactsIgnoreRule()
			]));

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

	private sealed class CountingSecretContentAnalyzer(IFileContentAnalyzer inner) : IFileContentAnalyzer
	{
		private int _contentReadCount;

		public int ContentReadCount => Volatile.Read(ref _contentReadCount);

		public FileContentClassification? ClassifyWithoutReading(string path) =>
			inner.ClassifyWithoutReading(path);

		public ValueTask<bool> IsTextFileAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.IsTextFileAsync(path, cancellationToken);

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.GetTextFileMetricsAsync(path, cancellationToken);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.TryReadAsTextAsync(path, cancellationToken);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			inner.TryReadAsTextAsync(path, maxSizeForFullRead, cancellationToken);

		public ValueTask<FileContentReadResult> ReadClassifiedAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _contentReadCount);
			return inner.ReadClassifiedAsync(path, maxSizeForFullRead, cancellationToken);
		}

		public ValueTask<ICompleteTextFileBuffer> OpenCompleteTextBufferAsync(
			string path,
			long maximumBytes,
			CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _contentReadCount);
			return inner.OpenCompleteTextBufferAsync(path, maximumBytes, cancellationToken);
		}
	}

	private sealed class BlockingWarmUpDetector : ISecretDetector
	{
		private readonly ManualResetEventSlim _release = new(false);
		private int _warmUpCount;
		private int _warmUpCompleted;

		public TaskCompletionSource Started { get; } = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		public int WarmUpCount => Volatile.Read(ref _warmUpCount);
		public bool IsWarmUpCompleted => Volatile.Read(ref _warmUpCompleted) != 0;

		public void WarmUp(CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref _warmUpCount);
			Started.TrySetResult();
			_release.Wait(cancellationToken);
			Volatile.Write(ref _warmUpCompleted, 1);
		}

		public void Release() => _release.Set();

		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) => [];
	}

	private sealed class WarmUpGuardAnalyzer(
		IFileContentAnalyzer inner,
		BlockingWarmUpDetector detector) : IFileContentAnalyzer
	{
		private int _readCount;

		public int ReadCount => Volatile.Read(ref _readCount);

		public FileContentClassification? ClassifyWithoutReading(string path) =>
			inner.ClassifyWithoutReading(path);

		public ValueTask<bool> IsTextFileAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.IsTextFileAsync(path, cancellationToken);

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.GetTextFileMetricsAsync(path, cancellationToken);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			inner.TryReadAsTextAsync(path, cancellationToken);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			inner.TryReadAsTextAsync(path, maxSizeForFullRead, cancellationToken);

		public ValueTask<ICompleteTextFileBuffer> OpenCompleteTextBufferAsync(
			string path,
			long maximumBytes,
			CancellationToken cancellationToken = default)
		{
			Assert.True(detector.IsWarmUpCompleted, "Source content was read before detector warm-up completed.");
			Interlocked.Increment(ref _readCount);
			return inner.OpenCompleteTextBufferAsync(path, maximumBytes, cancellationToken);
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
