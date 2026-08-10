using System.Collections.Concurrent;
using System.Diagnostics;
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
	private readonly IReadOnlyDictionary<string, ContentReadFact> _facts;

	internal ContentReadFactSnapshot(
		ContentSelectionSnapshot selection,
		IReadOnlyDictionary<string, ContentReadFact> facts,
		long retainedBytes)
	{
		Selection = selection;
		_facts = facts;
		RetainedBytes = retainedBytes;
	}

	public ContentSelectionSnapshot Selection { get; }
	public long RetainedBytes { get; }
	public int Count => _facts.Count;

	public bool TryGet(string path, out ContentReadFact fact) =>
		_facts.TryGetValue(path, out fact!);
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
	private const long MaximumRetainedReadFactBytes = 64L * 1024 * 1024;
	private const int ByteBudgetUnit = 64 * 1024;
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
				new ContentReadFactSnapshot(selection, new Dictionary<string, ContentReadFact>(), 0));
		}

		var retainedPaths = BuildRetainedPathSet(candidates);
		var retainedFacts = new ConcurrentDictionary<string, ContentReadFact>(PathComparer.Default);
		var parserWorkers = Math.Max(1, Math.Min(context.Session.AnalysisWorkerCapacity, candidates.Count));
		var channel = Channel.CreateBounded<WarmWorkItem>(new BoundedChannelOptions(parserWorkers * 2)
		{
			FullMode = BoundedChannelFullMode.Wait,
			SingleReader = parserWorkers == 1,
			SingleWriter = false,
			AllowSynchronousContinuations = false
		});
		using var byteBudget = new WeightedByteBudget(MaximumInFlightBytes, ByteBudgetUnit);
		var warmed = 0;
		var skipped = 0;
		var failed = 0;
		var processed = 0;
		var nextIndex = -1;

		var workers = Enumerable.Range(0, parserWorkers)
			.Select(_ => AnalyzeAsync())
			.ToArray();
		var producerCount = Math.Min(
			Math.Min(MaximumIoParallelism, Math.Max(1, Environment.ProcessorCount)),
			candidates.Count);
		var producers = Enumerable.Range(0, producerCount)
			.Select(_ => ProduceAsync())
			.ToArray();
		try
		{
			await Task.WhenAll(producers).ConfigureAwait(false);
			channel.Writer.TryComplete();
			await Task.WhenAll(workers).ConfigureAwait(false);
		}
		catch
		{
			channel.Writer.TryComplete();
			throw;
		}

		var snapshot = scope.Complete();
		Debug.Assert(snapshot.TotalFiles == Volatile.Read(ref warmed));
		stopwatch.Stop();
		var retainedBytes = retainedFacts.Values.Sum(static fact => fact.ApproximateRetainedBytes);
		return new CodeCompressionWarmupResult(
			candidates.Count,
			Volatile.Read(ref warmed),
			Volatile.Read(ref skipped),
			Volatile.Read(ref failed),
			stopwatch.Elapsed,
			new ContentReadFactSnapshot(
				selection,
				new Dictionary<string, ContentReadFact>(retainedFacts, PathComparer.Default),
				retainedBytes));

		async Task ProduceAsync()
		{
			while (true)
			{
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

					lease = await byteBudget.AcquireAsync(EstimateInFlightBytes(path), cancellationToken)
						.ConfigureAwait(false);
					var fact = await contentAnalyzer
						.ReadFactAsync(path, MaximumReadBytes, cancellationToken)
						.ConfigureAwait(false);
					if (!fact.IsMaterializedText || fact.Fingerprint is not { } fingerprint)
					{
						Increment(WarmFileOutcome.Skipped, ref warmed, ref skipped, ref failed);
						ReportProgress(progress, ref processed, candidates.Count);
						continue;
					}

					if (retainedPaths.Contains(path))
						retainedFacts[path] = fact;
					var relativePath = BuildRelativePath(context.ProjectRoot, path);
					if (!context.Session.IsSupported(relativePath))
					{
						var recorded = scope.Warm(
							path,
							relativePath,
							fact.Content!,
							fingerprint,
							cancellationToken);
						Increment(
							recorded ? WarmFileOutcome.Warmed : WarmFileOutcome.Skipped,
							ref warmed,
							ref skipped,
							ref failed);
						ReportProgress(progress, ref processed, candidates.Count);
						continue;
					}
					if (scope.TryWarmCached(path, relativePath, fact.Content!, fingerprint))
					{
						Increment(WarmFileOutcome.Warmed, ref warmed, ref skipped, ref failed);
						ReportProgress(progress, ref processed, candidates.Count);
						continue;
					}

					await channel.Writer.WriteAsync(
						new WarmWorkItem(path, relativePath, fact, fingerprint, lease),
						cancellationToken).ConfigureAwait(false);
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

		async Task AnalyzeAsync()
		{
			await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
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
							cancellationToken);
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
	}

	private HashSet<string> BuildRetainedPathSet(IReadOnlyList<string> paths)
	{
		var retained = new HashSet<string>(PathComparer.Default);
		long bytes = 0;
		foreach (var path in paths)
		{
			if (contentAnalyzer.ClassifyWithoutReading(path) == FileContentClassification.Binary ||
			    !TryGetLength(path, out var length) ||
			    length > MaximumReadBytes)
				continue;
			var estimate = 128L + length * sizeof(char);
			if (bytes + estimate > MaximumRetainedReadFactBytes)
				continue;
			retained.Add(path);
			bytes += estimate;
		}
		return retained;
	}

	private static long EstimateInFlightBytes(string path) =>
		TryGetLength(path, out var length)
			? Math.Clamp(length * sizeof(char) + 128, 1, MaximumInFlightBytes)
			: ByteBudgetUnit;

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

	private sealed class WeightedByteBudget : IDisposable
	{
		private readonly SemaphoreSlim _units;
		private readonly SemaphoreSlim _acquisitionGate = new(1, 1);
		private readonly int _unitBytes;
		private readonly int _maximumUnits;

		public WeightedByteBudget(long maximumBytes, int unitBytes)
		{
			_unitBytes = unitBytes;
			_maximumUnits = checked((int)Math.Max(1, maximumBytes / unitBytes));
			_units = new SemaphoreSlim(_maximumUnits, _maximumUnits);
		}

		public async ValueTask<Lease> AcquireAsync(long bytes, CancellationToken cancellationToken)
		{
			var requested = Math.Min(
				_maximumUnits,
				Math.Max(1, checked((int)((bytes + _unitBytes - 1) / _unitBytes))));
			var acquired = 0;
			await _acquisitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				for (; acquired < requested; acquired++)
					await _units.WaitAsync(cancellationToken).ConfigureAwait(false);
				return new Lease(this, requested);
			}
			catch
			{
				if (acquired > 0)
					_units.Release(acquired);
				throw;
			}
			finally
			{
				_acquisitionGate.Release();
			}
		}

		public void Dispose()
		{
			_acquisitionGate.Dispose();
			_units.Dispose();
		}

		public sealed class Lease(WeightedByteBudget owner, int units) : IDisposable
		{
			private WeightedByteBudget? _owner = owner;

			public void Dispose() => Interlocked.Exchange(ref _owner, null)?._units.Release(units);
		}
	}
}
