using System.Diagnostics;

namespace DevProjex.Application.Compression;

public sealed record CodeCompressionWarmupResult(
	int CandidateFiles,
	int WarmedFiles,
	int SkippedFiles,
	int FailedFiles,
	TimeSpan Elapsed);

/// <summary>
/// Populates file-local compression plans without producing output or publishing user-facing counts.
/// This is intentionally separate from metrics so visual-stability pacing cannot delay readiness.
/// </summary>
public sealed class CodeCompressionPrewarmer(IFileContentAnalyzer contentAnalyzer)
{
	private const long MaximumParallelFileBytes = 1024 * 1024;
	private const int MaximumParallelism = 16;

	public async Task<CodeCompressionWarmupResult> WarmAsync(
		CodeCompressionContext context,
		IReadOnlyList<string> orderedFilePaths,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(orderedFilePaths);

		var stopwatch = Stopwatch.StartNew();
		var candidates = BuildCandidates(context, orderedFilePaths);
		if (candidates.Count == 0)
			return new CodeCompressionWarmupResult(0, 0, 0, 0, stopwatch.Elapsed);

		var parallel = new List<string>(candidates.Count);
		var serial = new List<string>();
		foreach (var path in candidates)
		{
			if (TryGetLength(path, out var length) && length <= MaximumParallelFileBytes)
				parallel.Add(path);
			else
				serial.Add(path);
		}

		var warmed = 0;
		var skipped = 0;
		var failed = 0;
		using var scope = context.BeginOutput(candidates);
		if (parallel.Count > 0)
		{
			await Parallel.ForEachAsync(
				parallel,
				new ParallelOptions
				{
					CancellationToken = cancellationToken,
					MaxDegreeOfParallelism = ResolveBackgroundParallelism()
				},
				async (path, token) =>
				{
					var outcome = await WarmFileAsync(context, scope, path, token).ConfigureAwait(false);
					Increment(outcome, ref warmed, ref skipped, ref failed);
				}).ConfigureAwait(false);
		}

		foreach (var path in serial)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var outcome = await WarmFileAsync(context, scope, path, cancellationToken).ConfigureAwait(false);
			Increment(outcome, ref warmed, ref skipped, ref failed);
		}

		stopwatch.Stop();
		return new CodeCompressionWarmupResult(
			candidates.Count,
			Volatile.Read(ref warmed),
			Volatile.Read(ref skipped),
			Volatile.Read(ref failed),
			stopwatch.Elapsed);
	}

	private static int ResolveBackgroundParallelism()
	{
		var processorCount = Math.Max(1, Environment.ProcessorCount);
		var availableWorkers = processorCount > 1 ? processorCount - 1 : 1;
		return Math.Min(MaximumParallelism, availableWorkers);
	}

	private static List<string> BuildCandidates(
		CodeCompressionContext context,
		IReadOnlyList<string> orderedFilePaths)
	{
		var unique = new HashSet<string>(PathComparer.Default);
		var candidates = new List<string>(orderedFilePaths.Count);
		foreach (var path in orderedFilePaths)
		{
			if (candidates.Count == CodeCompressionSession.PlanCacheCapacity)
				break;
			if (string.IsNullOrWhiteSpace(path) ||
			    !unique.Add(path) ||
			    !context.Session.IsSupported(path))
			{
				continue;
			}

			candidates.Add(path);
		}

		return candidates;
	}

	private async ValueTask<WarmFileOutcome> WarmFileAsync(
		CodeCompressionContext context,
		CodeCompressionScope scope,
		string path,
		CancellationToken cancellationToken)
	{
		try
		{
			if (contentAnalyzer.ClassifyWithoutReading(path) == FileContentClassification.Binary)
				return WarmFileOutcome.Skipped;

			var content = await contentAnalyzer.TryReadAsTextAsync(path, cancellationToken).ConfigureAwait(false);
			if (content is null || content.IsEstimated || content.Content.Length == 0)
				return WarmFileOutcome.Skipped;

			scope.Warm(
				path,
				BuildRelativePath(context.ProjectRoot, path),
				content.Content,
				cancellationToken);
			return WarmFileOutcome.Warmed;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			return WarmFileOutcome.Failed;
		}
	}

	private static string BuildRelativePath(string projectRoot, string fullPath)
	{
		try
		{
			return Path.GetRelativePath(projectRoot, fullPath);
		}
		catch (ArgumentException)
		{
			return fullPath;
		}
	}

	private static bool TryGetLength(string path, out long length)
	{
		try
		{
			length = new FileInfo(path).Length;
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			length = 0;
			return false;
		}
	}

	private static void Increment(
		WarmFileOutcome outcome,
		ref int warmed,
		ref int skipped,
		ref int failed)
	{
		switch (outcome)
		{
			case WarmFileOutcome.Warmed:
				Interlocked.Increment(ref warmed);
				break;
			case WarmFileOutcome.Skipped:
				Interlocked.Increment(ref skipped);
				break;
			case WarmFileOutcome.Failed:
				Interlocked.Increment(ref failed);
				break;
		}
	}

	private enum WarmFileOutcome
	{
		Warmed,
		Skipped,
		Failed
	}
}
