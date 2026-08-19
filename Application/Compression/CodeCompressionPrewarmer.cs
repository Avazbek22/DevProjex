using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace DevProjex.Application.Compression;

public sealed record CodeCompressionWarmupResult(
	int CandidateFiles,
	int WarmedFiles,
	int SkippedFiles,
	int FailedFiles,
	TimeSpan Elapsed,
	ContentReadFactSnapshot? ReadFacts = null);

public readonly record struct CodeCompressionWarmupProgress(
	int ProcessedFiles,
	int TotalFiles);

/// <summary>Bounded operation-local content reused by the immediately following metrics phase.</summary>
public sealed class ContentReadFactSnapshot
{
	private readonly IReadOnlyDictionary<string, RetainedContentReadFact> _facts;

	internal ContentReadFactSnapshot(
		ContentSelectionSnapshot selection,
		IReadOnlyDictionary<string, RetainedContentReadFact> facts,
		long retainedBytes)
	{
		Selection = selection;
		_facts = facts;
		RetainedBytes = retainedBytes;
	}

	public ContentSelectionSnapshot Selection { get; }
	public long RetainedBytes { get; }
	public int Count => _facts.Count;

	public bool TryGet(string path, out ContentReadFact fact)
	{
		if (_facts.TryGetValue(path, out var retained) && retained.Identity.IsCurrent(path))
		{
			fact = retained.Fact;
			return true;
		}

		fact = null!;
		return false;
	}

	internal sealed record RetainedContentReadFact(
		ContentReadFact Fact,
		FileContentIdentity Identity);
}

/// <summary>
/// Builds file-local compression plans and publishes their exact aggregate without materializing
/// transformed output. Decode and native analysis have separate bounds so read strings never pile
/// up behind the process-wide parser budget.
/// </summary>
public sealed class CodeCompressionPrewarmer(IFileContentAnalyzer contentAnalyzer)
{
	private const long MaximumReadBytes = 10L * 1024 * 1024;
	private const long MaximumInFlightBytes = 32L * 1024 * 1024;
	private const long MaximumMaterializedFactBytes = 128L + MaximumReadBytes * sizeof(char);
	private const long MaximumRetainedReadFactBytes = 64L * 1024 * 1024;
	private const int MaximumIoParallelism = 4;

	public Task<CodeCompressionWarmupResult> WarmAsync(
		CodeCompressionContext context,
		IReadOnlyList<string> orderedFilePaths,
		CancellationToken cancellationToken = default,
		IProgress<CodeCompressionWarmupProgress>? progress = null)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(orderedFilePaths);
		return WarmAsync(
			context,
			ContentSelectionSnapshot.Create(context.ProjectRoot, orderedFilePaths),
			cancellationToken,
			progress);
	}

	public async Task<CodeCompressionWarmupResult> WarmAsync(
		CodeCompressionContext context,
		ContentSelectionSnapshot selection,
		CancellationToken cancellationToken = default,
		IProgress<CodeCompressionWarmupProgress>? progress = null)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(selection);
		var stopwatch = Stopwatch.StartNew();
		var candidates = selection.OrderedPaths;
		using var scope = context.BeginPrewarm(selection);
		if (candidates.Count == 0)
		{
			scope.Complete();
			return new CodeCompressionWarmupResult(
				0,
				0,
				0,
				0,
				stopwatch.Elapsed,
				new ContentReadFactSnapshot(
					selection,
					new Dictionary<string, ContentReadFactSnapshot.RetainedContentReadFact>(),
					0));
		}

		var retainedPaths = BuildRetainedPathSet(context, candidates);
		var retainedFacts = new ConcurrentDictionary<
			string,
			ContentReadFactSnapshot.RetainedContentReadFact>(PathComparer.Default);
		var retainedFactsSync = new object();
		long retainedFactBytes = 0;
		var parserWorkers = Math.Max(1, Math.Min(context.Session.AnalysisWorkerCapacity, candidates.Count));
		var channel = Channel.CreateBounded<WarmWorkItem>(new BoundedChannelOptions(parserWorkers * 2)
		{
			FullMode = BoundedChannelFullMode.Wait,
			SingleReader = parserWorkers == 1,
			SingleWriter = false,
			AllowSynchronousContinuations = false
		});
		using var pipelineCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		var pipelineToken = pipelineCancellation.Token;
		using var byteBudget = new WeightedByteBudget(MaximumInFlightBytes);
		// Only one decoder may hold its temporary pooled character buffer outside the retained-byte
		// accounting. Materialized facts themselves are covered by their weighted leases.
		using var decodeScratchGate = new SemaphoreSlim(1, 1);
		var warmed = 0;
		var skipped = 0;
		var failed = 0;
		var processed = 0;
		var nextIndex = -1;

		ExceptionDispatchInfo? primaryPipelineFailure = null;
		var workers = Enumerable.Range(0, parserWorkers)
			.Select(_ => RunAnalysisWorkerAsync())
			.ToArray();
		var producerCount = Math.Min(
			Math.Min(MaximumIoParallelism, Math.Max(1, Environment.ProcessorCount)),
			candidates.Count);
		var producers = Enumerable.Range(0, producerCount)
			.Select(_ => Task.Run(RunProducerAsync, CancellationToken.None))
			.ToArray();
		try
		{
			await Task.WhenAll(producers).ConfigureAwait(false);
			channel.Writer.TryComplete();
			await Task.WhenAll(workers).ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			var observedFailure = ExceptionDispatchInfo.Capture(exception);
			pipelineCancellation.Cancel();
			channel.Writer.TryComplete();
			await ObserveCompletionAsync(producers).ConfigureAwait(false);
			await ObserveCompletionAsync(workers).ConfigureAwait(false);
			while (channel.Reader.TryRead(out var abandoned))
				abandoned.Lease.Dispose();

			cancellationToken.ThrowIfCancellationRequested();
			Volatile.Read(ref primaryPipelineFailure)?.Throw();
			observedFailure.Throw();
			throw new UnreachableException();
		}

		cancellationToken.ThrowIfCancellationRequested();
		var snapshot = scope.Complete();
		Debug.Assert(snapshot.TotalFiles == Volatile.Read(ref warmed));
		stopwatch.Stop();
		var retainedBytes = Volatile.Read(ref retainedFactBytes);
		return new CodeCompressionWarmupResult(
			candidates.Count,
			Volatile.Read(ref warmed),
			Volatile.Read(ref skipped),
			Volatile.Read(ref failed),
			stopwatch.Elapsed,
			new ContentReadFactSnapshot(
				selection,
				new Dictionary<string, ContentReadFactSnapshot.RetainedContentReadFact>(
					retainedFacts,
					PathComparer.Default),
				retainedBytes));

		async Task RunProducerAsync()
		{
			try
			{
				await ProduceAsync().ConfigureAwait(false);
			}
			catch (Exception exception)
			{
				RecordPipelineFailure(exception);
				throw;
			}
		}

		async Task ProduceAsync()
		{
			while (true)
			{
				pipelineToken.ThrowIfCancellationRequested();
				var index = Interlocked.Increment(ref nextIndex);
				if (index >= candidates.Count)
					return;
				var path = candidates[index];
				WeightedByteBudget.Lease? lease = null;
				try
				{
					if (contentAnalyzer.ClassifyWithoutReading(path) == FileContentClassification.Binary)
					{
						Increment(WarmFileOutcome.Skipped, ref warmed, ref skipped, ref failed);
						ReportProgress(progress, ref processed, candidates.Count);
						continue;
					}

					var relativePath = BuildRelativePath(context.ProjectRoot, path);
					if (!context.IsSupported(relativePath))
					{
						var identifiedMetrics = contentAnalyzer is IPrewarmFileContentAnalyzer coherentAnalyzer
							? await coherentAnalyzer
								.GetClassifiedMetricsWithIdentityAsync(path, pipelineToken)
								.ConfigureAwait(false)
							: new IdentifiedFileContentMetricsResult(
								await contentAnalyzer
									.GetClassifiedMetricsAsync(path, pipelineToken)
									.ConfigureAwait(false),
								null);
						pipelineToken.ThrowIfCancellationRequested();
						var metricsResult = identifiedMetrics.Result;
						if (metricsResult.Classification != FileContentClassification.Text ||
						    metricsResult.Metrics is not { IsEstimated: false } metrics)
						{
							Increment(WarmFileOutcome.Skipped, ref warmed, ref skipped, ref failed);
							ReportProgress(progress, ref processed, candidates.Count);
							continue;
						}

						if (retainedPaths.Contains(path) && identifiedMetrics.Identity is { } metricsIdentity)
						{
							TryRetainFact(path, new ContentReadFactSnapshot.RetainedContentReadFact(
								new ContentReadFact(
									Content: null,
									Classification: FileContentClassification.Text,
									RawMetrics: metrics,
									Fingerprint: null),
								metricsIdentity));
						}
						scope.RecordUnsupported(path, relativePath, metrics.CharCount);
						Increment(WarmFileOutcome.Warmed, ref warmed, ref skipped, ref failed);
						ReportProgress(progress, ref processed, candidates.Count);
						continue;
					}

					ContentReadFact fact;
					FileContentIdentity? identity;
					if (contentAnalyzer is IPrewarmFileContentAnalyzer budgetedAnalyzer)
					{
						var read = await budgetedAnalyzer.ReadFactWithBudgetAsync(
							path,
							MaximumReadBytes,
							byteBudget,
							decodeScratchGate,
							pipelineToken).ConfigureAwait(false);
						fact = read.Fact;
						identity = read.Identity;
						lease = read.Lease;
					}
					else
					{
						// Unknown analyzers cannot prove a same-handle length. Reserve the maximum
						// materialized fact before they decode instead of trusting a separate stat.
						lease = await byteBudget.AcquireAsync(MaximumMaterializedFactBytes, pipelineToken)
							.ConfigureAwait(false);
						await decodeScratchGate.WaitAsync(pipelineToken).ConfigureAwait(false);
						try
						{
							fact = await contentAnalyzer
								.ReadFactAsync(path, MaximumReadBytes, pipelineToken)
								.ConfigureAwait(false);
						}
						finally
						{
							decodeScratchGate.Release();
						}
						identity = null;
					}
					pipelineToken.ThrowIfCancellationRequested();
					if (!fact.IsMaterializedText || fact.Fingerprint is not { } fingerprint)
					{
						Increment(WarmFileOutcome.Skipped, ref warmed, ref skipped, ref failed);
						ReportProgress(progress, ref processed, candidates.Count);
						continue;
					}

					if (retainedPaths.Contains(path) && identity is { } contentIdentity)
					{
						TryRetainFact(path, new ContentReadFactSnapshot.RetainedContentReadFact(
							fact,
							contentIdentity));
					}
					if (scope.TryWarmCached(path, relativePath, fact.Content!, fingerprint))
					{
						Increment(WarmFileOutcome.Warmed, ref warmed, ref skipped, ref failed);
						ReportProgress(progress, ref processed, candidates.Count);
						continue;
					}

					await channel.Writer.WriteAsync(
						new WarmWorkItem(
							path,
							relativePath,
							fact,
							fingerprint,
							lease ?? throw new InvalidOperationException("A materialized fact has no byte-budget lease.")),
						pipelineToken).ConfigureAwait(false);
					lease = null;
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception exception) when (
					exception is IOException or UnauthorizedAccessException or NotSupportedException)
				{
					Increment(WarmFileOutcome.Failed, ref warmed, ref skipped, ref failed);
					ReportProgress(progress, ref processed, candidates.Count);
				}
				finally
				{
					lease?.Dispose();
				}
			}
		}

		async Task RunAnalysisWorkerAsync()
		{
			try
			{
				await AnalyzeAsync().ConfigureAwait(false);
			}
			catch (Exception exception)
			{
				RecordPipelineFailure(exception);
				throw;
			}
		}

		async Task AnalyzeAsync()
		{
			await foreach (var item in channel.Reader.ReadAllAsync(pipelineToken).ConfigureAwait(false))
			{
				using (item.Lease)
				{
					try
					{
						var recorded = scope.Warm(
							item.Path,
							item.RelativePath,
							item.Fact.Content!,
							item.Fingerprint,
							pipelineToken);
						Increment(
							recorded ? WarmFileOutcome.Warmed : WarmFileOutcome.Skipped,
							ref warmed,
							ref skipped,
							ref failed);
					}
					catch (OperationCanceledException)
					{
						throw;
					}
					catch (Exception exception) when (
						exception is IOException or UnauthorizedAccessException or NotSupportedException)
					{
						Increment(WarmFileOutcome.Failed, ref warmed, ref skipped, ref failed);
					}
					finally
					{
						ReportProgress(progress, ref processed, candidates.Count);
					}
				}
			}
		}

		void RecordPipelineFailure(Exception exception)
		{
			if (exception is OperationCanceledException &&
			    (cancellationToken.IsCancellationRequested || pipelineToken.IsCancellationRequested))
			{
				return;
			}

			var captured = ExceptionDispatchInfo.Capture(exception);
			if (Interlocked.CompareExchange(ref primaryPipelineFailure, captured, null) is not null)
				return;

			channel.Writer.TryComplete();
			pipelineCancellation.Cancel();
		}

		void TryRetainFact(
			string path,
			ContentReadFactSnapshot.RetainedContentReadFact retainedFact)
		{
			var actualBytes = retainedFact.Fact.ApproximateRetainedBytes;
			lock (retainedFactsSync)
			{
				if (actualBytes > MaximumRetainedReadFactBytes - retainedFactBytes ||
				    !retainedFacts.TryAdd(path, retainedFact))
				{
					return;
				}
				retainedFactBytes += actualBytes;
			}
		}

		static async Task ObserveCompletionAsync(Task[] tasks)
		{
			try
			{
				await Task.WhenAll(tasks).ConfigureAwait(false);
			}
			catch
			{
				// The primary failure is rethrown after both pipeline sides have released their resources.
			}
		}
	}

	private HashSet<string> BuildRetainedPathSet(
		CodeCompressionContext context,
		IReadOnlyList<string> paths)
	{
		var retained = new HashSet<string>(PathComparer.Default);
		long bytes = 0;
		foreach (var path in paths)
		{
			if (contentAnalyzer.ClassifyWithoutReading(path) == FileContentClassification.Binary)
				continue;
			var relativePath = BuildRelativePath(context.ProjectRoot, path);
			if (!context.IsSupported(relativePath))
			{
				if (bytes + 128L > MaximumRetainedReadFactBytes)
					continue;
				retained.Add(path);
				bytes += 128L;
				continue;
			}
			if (!TryGetLength(path, out var length) || length > MaximumReadBytes)
				continue;
			var estimate = 128L + length * sizeof(char);
			if (bytes + estimate > MaximumRetainedReadFactBytes)
				continue;
			retained.Add(path);
			bytes += estimate;
		}
		return retained;
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
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or NotSupportedException)
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

	private static void ReportProgress(
		IProgress<CodeCompressionWarmupProgress>? progress,
		ref int processed,
		int total)
	{
		var current = Interlocked.Increment(ref processed);
		progress?.Report(new CodeCompressionWarmupProgress(current, total));
	}

	private sealed record WarmWorkItem(
		string Path,
		string RelativePath,
		ContentReadFact Fact,
		ContentFingerprint Fingerprint,
		WeightedByteBudget.Lease Lease);

	private enum WarmFileOutcome
	{
		Warmed,
		Skipped,
		Failed
	}

}
