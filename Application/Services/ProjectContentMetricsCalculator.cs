namespace DevProjex.Application.Services;

/// <summary>Calculates rendered content metrics from any source or prepared-file analyzer.</summary>
public static class ProjectContentMetricsCalculator
{
	private const int MaximumConcurrentReads = 16;
	private const int BatchSize = 1024;

	public static Task<ExportOutputMetrics> CalculateAsync(
		IFileContentAnalyzer analyzer,
		IReadOnlyList<string>? orderedFilePaths,
		CancellationToken cancellationToken = default)
		=> CalculateAsync(
			analyzer,
			orderedFilePaths,
			progress: null,
			cancellationToken);

	public static Task<ExportOutputMetrics> CalculateAsync(
		IFileContentAnalyzer analyzer,
		IReadOnlyList<string>? orderedFilePaths,
		IProgress<ProjectCopyExportProgress>? progress,
		CancellationToken cancellationToken = default)
		=> CalculateAsync(
			analyzer,
			orderedFilePaths,
			fileMetricsObserver: null,
			progress,
			cancellationToken);

	public static async Task<ExportOutputMetrics> CalculateAsync(
		IFileContentAnalyzer analyzer,
		IReadOnlyList<string>? orderedFilePaths,
		Action<ContentFileMetrics>? fileMetricsObserver,
		IProgress<ProjectCopyExportProgress>? progress,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(analyzer);
		if (orderedFilePaths is null || orderedFilePaths.Count == 0)
			return ExportOutputMetrics.Empty;

		var parallelOptions = new ParallelOptions
		{
			MaxDegreeOfParallelism = Math.Min(
				MaximumConcurrentReads,
				ScanParallelismPolicy.MaxDegreeOfParallelism),
			CancellationToken = cancellationToken
		};
		var accumulator = new ExportOutputMetricsCalculator.OrderedContentMetricsAccumulator();
		var batchMetrics = new FileContentMetricsResult?[Math.Min(BatchSize, orderedFilePaths.Count)];
		var processedFiles = 0;
		var progressSync = progress is null ? null : new object();
		for (var batchStart = 0; batchStart < orderedFilePaths.Count; batchStart += batchMetrics.Length)
		{
			var batchCount = Math.Min(batchMetrics.Length, orderedFilePaths.Count - batchStart);
			await Parallel.ForAsync(
				0,
				batchCount,
				parallelOptions,
				async (batchIndex, token) =>
				{
					batchMetrics[batchIndex] = await analyzer
						.GetClassifiedMetricsAsync(orderedFilePaths[batchStart + batchIndex], token)
						.ConfigureAwait(false);
					if (progress is not null)
					{
						lock (progressSync!)
						{
							processedFiles++;
							var percentage = processedFiles * 100d / orderedFilePaths.Count;
							progress.Report(new ProjectCopyExportProgress(
								processedFiles,
								orderedFilePaths.Count,
								BytesWritten: 0,
								Percentage: percentage));
						}
					}
				}).ConfigureAwait(false);

			for (var batchIndex = 0; batchIndex < batchCount; batchIndex++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var result = batchMetrics[batchIndex];
				var metrics = result?.IsText == true ? result.Metrics : null;
				if (metrics is null)
					continue;

				var fileMetrics = new ContentFileMetrics(
					Path: orderedFilePaths[batchStart + batchIndex],
					SizeBytes: metrics.SizeBytes,
					LineCount: metrics.LineCount,
					CharCount: metrics.CharCount,
					IsEmpty: metrics.IsEmpty,
					IsWhitespaceOnly: metrics.IsWhitespaceOnly,
					IsEstimated: metrics.IsEstimated,
					CrLfPairCount: metrics.CrLfPairCount,
					TrailingNewlineChars: metrics.TrailingNewlineChars,
					TrailingNewlineLineBreaks: metrics.TrailingNewlineLineBreaks);
				accumulator.AppendFile(fileMetrics);
				fileMetricsObserver?.Invoke(fileMetrics);
			}

			Array.Clear(batchMetrics, 0, batchCount);
		}

		return accumulator.ToMetrics();
	}
}
