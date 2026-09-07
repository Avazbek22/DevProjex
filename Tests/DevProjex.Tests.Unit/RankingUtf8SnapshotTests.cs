using DevProjex.Application.Context;
using DevProjex.Application.Ranking;
using DevProjex.Application.Secrets;

namespace DevProjex.Tests.Unit;

public sealed class RankingUtf8SnapshotTests
{
	[Fact]
	public async Task RankedMarkdownPreservesUtf8SnapshotPathAndValidatesTheIndexedSourceVersion()
	{
		using var workspace = new TemporaryDirectory();
		var sourcePath = workspace.CreateFile("project/A.txt", "alpha\n");
		var projectRoot = Path.GetDirectoryName(sourcePath)!;
		var snapshot = new TrackingUtf8Snapshot(sourcePath, "alpha\n");
		var service = new ProjectContextDocumentService(
			new TreeExportService(),
			new SnapshotAnalyzer(snapshot));
		var plan = CreatePlan(projectRoot, sourcePath);
		var capturedVersion = RankingSourceVersion.Capture(sourcePath);
		var ranking = CreateRanking(sourcePath, capturedVersion);
		await using var destination = new MemoryStream();

		IOException? exception = null;
		try
		{
			await service.WriteCompleteWithReportAsync(
				plan,
				ProjectContextView.Content,
				ProjectContextDocumentFormat.Markdown,
				destination,
				TestContext.Current.CancellationToken,
				ranking: ranking);
		}
		catch (IOException caught)
		{
			exception = caught;
		}

		Assert.Equal(1, snapshot.Utf8CopyCount);
		Assert.Equal(0, snapshot.TextCopyCount);
		Assert.Contains("changed", File.ReadAllText(sourcePath), StringComparison.Ordinal);
		Assert.NotEqual(capturedVersion, RankingSourceVersion.Capture(sourcePath));
		Assert.NotNull(exception);
		Assert.Contains("changed after importance facts were indexed", exception.Message, StringComparison.Ordinal);
		Assert.True(snapshot.IsDisposed);
	}

	private static ProjectContextPlan CreatePlan(string projectRoot, string sourcePath)
	{
		var selection = new ProjectSelectionSpec(GitMode: GitFilteringMode.None, Exclusions: []);
		var file = new TreeNodeDescriptor("A.txt", sourcePath, false, false, "file", []);
		var tree = new TreeNodeDescriptor(
			Path.GetFileName(projectRoot),
			projectRoot,
			true,
			false,
			"folder",
			[file]);
		var analysis = new ProjectAnalysisReport(
			1,
			DateTimeOffset.UnixEpoch,
			projectRoot,
			new ProjectAnalysisSelectionReport([], [], []),
			new ProjectAnalysisInventoryReport(
				[],
				[".txt"],
				new ProjectTreeSummaryReport(1, 1, 0)),
			new ProjectAnalysisOutputMetricsReport(
				ProjectOutputMetricsReport.Empty,
				ProjectOutputMetricsReport.Empty),
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
			"ranking-utf8");
	}

	private static ImportanceRankingReport CreateRanking(
		string sourcePath,
		RankingSourceVersion capturedVersion)
	{
		var entry = new ImportanceRankingEntry(
			sourcePath,
			"A.txt",
			1,
			1,
			0,
			0,
			1,
			1,
			ImportanceFileRole.Source,
			true,
			true);
		return new ImportanceRankingReport(
			ImportanceRankingService.AlgorithmId,
			[entry],
			[entry],
			1,
			1,
			0,
			1,
			200,
			1,
			ProjectGitHistoryUnavailableReason.None,
			false,
			ImportanceRankingService.GraphVariant)
		{
			SourceVersions = new Dictionary<string, RankingSourceVersion>(PathComparer.Default)
			{
				[Path.GetFullPath(sourcePath)] = capturedVersion
			}
		};
	}

	private sealed class SnapshotAnalyzer(TrackingUtf8Snapshot snapshot) : IFileContentAnalyzer
	{
		public ValueTask<bool> IsTextFileAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult(true);

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<TextFileMetrics?>(snapshot.Result.Metrics);

		public ValueTask<IFileContentSnapshot> OpenCompleteSnapshotAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<IFileContentSnapshot>(snapshot);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<TextFileContent?>(snapshot.Content);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			ValueTask.FromResult<TextFileContent?>(snapshot.Content);
	}

	private sealed class TrackingUtf8Snapshot(string sourcePath, string content) :
		IFileContentSnapshot,
		IUtf8FileContentSnapshot
	{
		private readonly byte[] utf8 = Encoding.UTF8.GetBytes(content);

		public int TextCopyCount { get; private set; }
		public int Utf8CopyCount { get; private set; }
		public bool IsDisposed { get; private set; }
		public TextFileContent Content { get; } = new(
			content,
			Encoding.UTF8.GetByteCount(content),
			2,
			content.Length,
			false,
			false,
			TrailingNewlineChars: 1,
			TrailingNewlineLineBreaks: 1);
		public FileContentMetricsResult Result { get; } = new(
			FileContentClassification.Text,
			new TextFileMetrics(
				Encoding.UTF8.GetByteCount(content),
				2,
				content.Length,
				false,
				false,
				TrailingNewlineChars: 1,
				TrailingNewlineLineBreaks: 1));

		public async ValueTask CopyTextToAsync(
			int maximumCharacters,
			Func<ReadOnlyMemory<char>, CancellationToken, ValueTask> writeChunk,
			CancellationToken cancellationToken = default)
		{
			TextCopyCount++;
			await writeChunk(content.AsMemory(0, maximumCharacters), cancellationToken).ConfigureAwait(false);
		}

		public async ValueTask CopyUtf8ToAsync(
			int maximumCharacters,
			Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> writeChunk,
			CancellationToken cancellationToken = default)
		{
			Utf8CopyCount++;
			await writeChunk(utf8, cancellationToken).ConfigureAwait(false);
			File.AppendAllText(sourcePath, "changed");
		}

		public ValueTask DisposeAsync()
		{
			IsDisposed = true;
			return ValueTask.CompletedTask;
		}
	}
}
