using System.Collections;
using System.Reflection;
using DevProjex.Application.Ranking;
using DevProjex.Kernel.Abstractions;

namespace DevProjex.Tests.Terminal;

public sealed class ImportanceRankingSnapshotOwnershipTests
{
	[Fact]
	public async Task ChangedSourceAfterSnapshotOpenDisposesOwnedSnapshotExactlyOnce()
	{
		using var workspace = new TemporaryDirectory();
		var project = workspace.CreateDirectory("project");
		var path = workspace.WriteFile("project/Source.cs", "class VersionA;");
		using var services = new TerminalServiceFactory(
				() => workspace.CreateDirectory("data"))
			.Create(AppLanguage.En);
		var analyzer = new MutatingTrackingAnalyzer(path, "class VersionB;");
		var documentService = new ProjectContextDocumentService(
			services.TreeExportService,
			analyzer);
		var ranking = CreateRanking(path);
		await using var destination = new MemoryStream();

		var exception = await Assert.ThrowsAsync<IOException>(() =>
			documentService.WriteCompleteWithReportAsync(
				services.ContextFactory.BuildAsync(
					project,
					new ProjectSelectionSpec(GitMode: GitFilteringMode.None, Exclusions: []),
					cancellationToken: TestContext.Current.CancellationToken).GetAwaiter().GetResult(),
				ProjectContextView.Content,
				ProjectContextDocumentFormat.Text,
				destination,
				TestContext.Current.CancellationToken,
				ranking: ranking));

		Assert.Contains("repeat the export", exception.Message, StringComparison.Ordinal);
		Assert.Equal(1, analyzer.DisposeCount);
	}

	private static ImportanceRankingReport CreateRanking(string path)
	{
		var entry = new ImportanceRankingEntry(
			path,
			Path.GetFileName(path),
			1,
			1,
			0,
			0,
			null,
			null,
			ImportanceFileRole.Source,
			true,
			false);
		var report = new ImportanceRankingReport(
			ImportanceRankingService.AlgorithmId,
			[entry],
			[entry],
			1,
			1,
			0,
			1,
			200,
			0,
			ProjectGitHistoryUnavailableReason.NotRepository,
			true,
			ImportanceRankingService.GraphVariant);
		var versionType = typeof(ImportanceRankingReport).Assembly.GetType(
			"DevProjex.Application.Ranking.RankingSourceVersion",
			throwOnError: true)!;
		var version = versionType.GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic)!
			.Invoke(null, [path])!;
		var dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(string), versionType);
		var versions = (IDictionary)Activator.CreateInstance(dictionaryType)!;
		versions.Add(Path.GetFullPath(path), version);
		typeof(ImportanceRankingReport)
			.GetProperty("SourceVersions", BindingFlags.Instance | BindingFlags.NonPublic)!
			.SetValue(report, versions);
		return report;
	}

	private sealed class MutatingTrackingAnalyzer(string pathToMutate, string replacement) : IFileContentAnalyzer
	{
		private readonly FileContentAnalyzer _inner = new();

		public int DisposeCount { get; private set; }

		public async ValueTask<IFileContentSnapshot> OpenCompleteSnapshotAsync(
			string path,
			CancellationToken cancellationToken = default)
		{
			var snapshot = await _inner.OpenCompleteSnapshotAsync(path, cancellationToken)
				.ConfigureAwait(false);
			var writeTime = File.GetLastWriteTimeUtc(pathToMutate);
			var replacementPath = pathToMutate + ".replacement";
			File.WriteAllText(replacementPath, replacement, new UTF8Encoding(false));
			File.Replace(replacementPath, pathToMutate, destinationBackupFileName: null);
			File.SetLastWriteTimeUtc(pathToMutate, writeTime);
			return new TrackingSnapshot(snapshot, () => DisposeCount++);
		}

		public ValueTask<bool> IsTextFileAsync(string path, CancellationToken cancellationToken = default) =>
			_inner.IsTextFileAsync(path, cancellationToken);

		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			_inner.GetTextFileMetricsAsync(path, cancellationToken);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			CancellationToken cancellationToken = default) =>
			_inner.TryReadAsTextAsync(path, cancellationToken);

		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) =>
			_inner.TryReadAsTextAsync(path, maxSizeForFullRead, cancellationToken);
	}

	private sealed class TrackingSnapshot(IFileContentSnapshot inner, Action onDispose) : IFileContentSnapshot
	{
		private int _disposed;

		public FileContentMetricsResult Result => inner.Result;

		public ValueTask CopyTextToAsync(
			int maximumCharacters,
			Func<ReadOnlyMemory<char>, CancellationToken, ValueTask> writeChunk,
			CancellationToken cancellationToken = default) =>
			inner.CopyTextToAsync(maximumCharacters, writeChunk, cancellationToken);

		public async ValueTask DisposeAsync()
		{
			if (Interlocked.Exchange(ref _disposed, 1) != 0)
				return;
			onDispose();
			await inner.DisposeAsync().ConfigureAwait(false);
		}
	}
}
