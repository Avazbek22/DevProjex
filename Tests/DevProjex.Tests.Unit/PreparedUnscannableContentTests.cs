using DevProjex.Application.Compression;
using DevProjex.Application.Context;
using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class PreparedUnscannableContentTests
{
	[Theory]
	[InlineData(FileContentClassification.TooLarge)]
	[InlineData(FileContentClassification.Unreadable)]
	[InlineData(FileContentClassification.UnsupportedEncoding)]
	[InlineData(FileContentClassification.AccessDenied)]
	public async Task PreparedAnalyzerNeverReturnsNewlyReadableUninspectedText(
		FileContentClassification classification)
	{
		using var workspace = new TemporaryDirectory();
		var sourcePath = workspace.CreateFile("project/source.txt", "secret that was never inspected");
		await using var prepared = CreateUnscannableOutput(sourcePath, classification);
		var analyzer = new PreparedSecretFileContentAnalyzer(new FileContentAnalyzer(), prepared);

		var read = await analyzer.ReadClassifiedAsync(
			sourcePath,
			SecretRedactionOutputPreparer.MaximumScannableFileBytes,
			TestContext.Current.CancellationToken);
		var boundedText = await analyzer.TryReadAsTextAsync(
			sourcePath,
			SecretRedactionOutputPreparer.MaximumScannableFileBytes,
			TestContext.Current.CancellationToken);
		var metrics = await analyzer.GetClassifiedMetricsAsync(
			sourcePath,
			TestContext.Current.CancellationToken);
		var isText = await analyzer.IsTextFileAsync(
			sourcePath,
			TestContext.Current.CancellationToken);

		Assert.Equal(classification, read.Classification);
		Assert.Null(read.Content);
		Assert.Null(boundedText);
		Assert.Equal(classification, metrics.Classification);
		Assert.Equal(classification == FileContentClassification.TooLarge, isText);
	}

	[Fact]
	public async Task BoundedContextDocumentDoesNotPublishTextWithheldDuringPreparation()
	{
		using var workspace = new TemporaryDirectory();
		var sourcePath = workspace.CreateFile("project/source.txt", "uninspected secret marker");
		var projectRoot = Path.GetDirectoryName(sourcePath)!;
		await using var prepared = CreateUnscannableOutput(
			sourcePath,
			FileContentClassification.Unreadable);
		var analyzer = new PreparedSecretFileContentAnalyzer(new FileContentAnalyzer(), prepared);
		var document = new ProjectContextDocumentService(new TreeExportService(), analyzer);

		var rendered = await document.BuildAsync(
			CreatePlan(projectRoot, sourcePath),
			ProjectContextView.Content,
			ProjectContextDocumentFormat.Json,
			new ProjectContextDocumentLimits(),
			TestContext.Current.CancellationToken);

		Assert.DoesNotContain("uninspected secret marker", rendered, StringComparison.Ordinal);
		using var json = JsonDocument.Parse(rendered);
		var file = Assert.Single(json.RootElement.GetProperty("files").EnumerateArray());
		Assert.Equal("unreadable", file.GetProperty("classification").GetString());
		Assert.Equal(JsonValueKind.Null, file.GetProperty("content").ValueKind);
	}

	[Fact]
	public async Task PreparedTooLargeFileRetainsItsOriginalEstimateAfterSourceReplacement()
	{
		using var workspace = new TemporaryDirectory();
		var projectRoot = workspace.CreateFolder("project");
		var sourcePath = Path.Combine(projectRoot, "source.txt");
		var block = new byte[1024 * 1024];
		Array.Fill(block, (byte)'a');
		await using (var source = new FileStream(sourcePath, FileMode.CreateNew, FileAccess.Write))
		{
			for (var index = 0; index < 17; index++)
				await source.WriteAsync(block, TestContext.Current.CancellationToken);
		}
		using var session = new SecretRedactionSession(new NoFindingsDetector());
		var preparer = new SecretRedactionOutputPreparer(new FileContentAnalyzer());
		await using var prepared = await preparer.PrepareAsync(
			new ContentTransformationContext(
				Compression: null,
				Redaction: new SecretRedactionContext(projectRoot, session)),
			[sourcePath],
			TestContext.Current.CancellationToken);
		Assert.Equal(FileContentClassification.TooLarge, prepared.GetFile(sourcePath).Classification);

		await File.WriteAllTextAsync(
			sourcePath,
			"new content must remain withheld",
			TestContext.Current.CancellationToken);
		var read = await preparer.CreatePreparedAnalyzer(prepared).ReadClassifiedAsync(
			sourcePath,
			SecretRedactionOutputPreparer.MaximumScannableFileBytes,
			TestContext.Current.CancellationToken);

		Assert.Equal(FileContentClassification.TooLarge, read.Classification);
		Assert.NotNull(read.Content);
		Assert.True(read.Content.IsEstimated);
		Assert.Equal(17L * 1024 * 1024, read.Content.SizeBytes);
		Assert.Empty(read.Content.Content);
	}

	private static PreparedSecretRedactionOutput CreateUnscannableOutput(
		string sourcePath,
		FileContentClassification classification) =>
		new(
			workingDirectory: null,
			files: new Dictionary<string, PreparedSecretFile>(ProjectTreePathIdentity.CanonicalComparer)
			{
				[sourcePath] = PreparedSecretFile.Unscannable(sourcePath, classification)
			},
			snapshot: null,
			unscannableFiles: [new UnscannableFile(sourcePath, classification)]);

	private static ProjectContextPlan CreatePlan(string projectRoot, string sourcePath)
	{
		var selection = new ProjectSelectionSpec(GitMode: GitFilteringMode.None, Exclusions: []);
		var file = new TreeNodeDescriptor("source.txt", sourcePath, false, false, "file", []);
		var tree = new TreeNodeDescriptor(
			Path.GetFileName(projectRoot), projectRoot, true, false, "folder", [file]);
		var analysis = new ProjectAnalysisReport(
			1,
			DateTimeOffset.UnixEpoch,
			projectRoot,
			new ProjectAnalysisSelectionReport([], [], []),
			new ProjectAnalysisInventoryReport(
				[], [".txt"], new ProjectTreeSummaryReport(1, 1, 0)),
			new ProjectAnalysisOutputMetricsReport(
				ProjectOutputMetricsReport.Empty, ProjectOutputMetricsReport.Empty),
			new ProjectAnalysisTimingReport(0, 0, 0),
			new ProjectAnalysisDiagnosticsReport(false, false, []));
		return new ProjectContextPlan(
			projectRoot,
			selection,
			[projectRoot],
			[projectRoot],
			[".txt"],
			[".txt"],
			tree,
			tree,
			new HashSet<string>([sourcePath], PathComparer.Default),
			[sourcePath],
			[projectRoot],
			analysis,
			[],
			new ProjectContextGitReadiness(GitFilteringMode.None, 1, true),
			"prepared-unscannable");
	}

	private sealed class NoFindingsDetector : ISecretDetector
	{
		public IReadOnlyList<DetectedSecret> Detect(
			string repositoryRelativePath,
			string content,
			CancellationToken cancellationToken = default) => [];
	}
}
