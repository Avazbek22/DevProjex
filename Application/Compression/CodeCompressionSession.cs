using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DevProjex.Application.Compression;

/// <summary>How one file fared, in a shape the UI can explain without leaking internal codes.</summary>
public sealed record CodeCompressionFileOutcome(
	string RelativePath,
	string LanguageId,
	CodeCompressionOutcome Outcome,
	int SourceCharacters,
	int TransformedCharacters);

/// <summary>
/// What the user is told after a complete selection evaluation. Counts come from validated plans,
/// not capability guesses: the UI must never say the project "is compressed", only how many text
/// files in the selected output will be transformed and how many will remain unchanged.
/// </summary>
public sealed record CodeCompressionSnapshot(
	string SelectionKey,
	int CompressedFiles,
	int UnchangedFiles,
	long SourceCharacters,
	long TransformedCharacters,
	IReadOnlyList<CodeCompressionFileOutcome> Unchanged,
	IReadOnlyDictionary<CodeCompressionOutcome, int>? UnchangedOutcomeCounts = null,
	int AdditionalUnchangedFiles = 0,
	int BodyTransformedFiles = 0,
	int CommentTransformedFiles = 0,
	string TransformIdentity = "")
{
	public static CodeCompressionSnapshot Empty { get; } = new(string.Empty, 0, 0, 0, 0, []);

	public int TotalFiles => CompressedFiles + UnchangedFiles;

	/// <summary>Same estimator the export metrics use, so the summary agrees with the status bar.</summary>
	public static long EstimateTokens(long characters) =>
		characters <= 0 ? 0 : (characters / 4) + (characters % 4 == 0 ? 0 : 1);
}

public sealed record CodeCompressionDiagnosticsSnapshot(
	long HashComputations,
	long CacheHits,
	long CacheMisses,
	long AnalysisExecutions,
	long PrewarmRequests,
	long PrewarmCacheHits,
	long PrewarmAnalyses,
	long PrewarmReuses,
	long UnsupportedFastPaths,
	int CacheEntries,
	long RetainedCacheBytes,
	int MaximumCacheEntries,
	long MaximumRetainedCacheBytes,
	CodeCompressionRuntimeDiagnosticSnapshot Runtime);

internal enum CodeCompressionScopeMode
{
	Output,
	Prewarm,
	Measurement
}

/// <summary>
/// Window-lifetime state for code compression: the compressor, a plan cache and the last published
/// snapshot. Deliberately shaped like <see cref="Secrets.SecretRedactionSession"/> - same
/// session/scope/snapshot split, same cache-key composition - because every consumer already knows
/// that shape.
/// </summary>
public sealed class CodeCompressionSession(ICodeCompressor compressor) : IDisposable
{
	internal const int PlanCacheCapacity = 16_384;
	internal const long MaximumRetainedPlanCacheBytes = 64L * 1024 * 1024;
	private readonly Dictionary<CodeCompressionCacheKey, LinkedListNode<CachedPlan>> _cache = [];
	private readonly LinkedList<CachedPlan> _cacheRecency = [];
	private readonly ConcurrentDictionary<InFlightCompressionKey, Lazy<CodeCompressionAnalysis>> _inFlight = [];
	private readonly ConcurrentDictionary<CodeCompressionCacheKey, Lazy<CodeCompressionAnalysis>> _prewarmInFlight = [];
	private readonly object _sync = new();
	private readonly int _maximumCacheEntries = PlanCacheCapacity;
	private readonly long _maximumRetainedCacheBytes = MaximumRetainedPlanCacheBytes;
	private readonly string _bodiesTransformIdentity =
		CodeTransformIdentity.Create(compressor.TransformIdentity, CodeTransformKinds.Bodies);
	private readonly string _commentsTransformIdentity =
		CodeTransformIdentity.Create(compressor.TransformIdentity, CodeTransformKinds.Comments);
	private readonly string _combinedTransformIdentity =
		CodeTransformIdentity.Create(
			compressor.TransformIdentity,
			CodeTransformKinds.Bodies | CodeTransformKinds.Comments);
	private CodeCompressionSnapshot _snapshot = CodeCompressionSnapshot.Empty;
	private CancellationTokenSource _generationCts = new();
	private long _generation;
	private long _hashComputations;
	private long _cacheHits;
	private long _cacheMisses;
	private long _analysisExecutions;
	private long _prewarmRequests;
	private long _prewarmCacheHits;
	private long _prewarmAnalyses;
	private long _prewarmReuses;
	private long _unsupportedFastPaths;
	private long _retainedCacheBytes;
	private bool _disposed;

	internal CodeCompressionSession(
		ICodeCompressor compressor,
		int maximumCacheEntries,
		long maximumRetainedCacheBytes)
		: this(compressor)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCacheEntries);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRetainedCacheBytes);
		_maximumCacheEntries = maximumCacheEntries;
		_maximumRetainedCacheBytes = maximumRetainedCacheBytes;
	}

	public event EventHandler? SnapshotPublished;

	public string TransformIdentity => _bodiesTransformIdentity;

	public string GetTransformIdentity(CodeTransformKinds kinds) =>
		kinds switch
		{
			CodeTransformKinds.Bodies => _bodiesTransformIdentity,
			CodeTransformKinds.Comments => _commentsTransformIdentity,
			CodeTransformKinds.Bodies | CodeTransformKinds.Comments => _combinedTransformIdentity,
			_ => throw new ArgumentOutOfRangeException(nameof(kinds), kinds, null)
		};

	public bool IsSupported(string relativePath) => compressor.IsSupported(relativePath);

	public bool IsSupported(string relativePath, CodeTransformKinds kinds) =>
		compressor.IsSupported(relativePath, kinds);

	public int AnalysisWorkerCapacity =>
		(compressor as ICodeCompressionRuntimeDiagnosticsProvider)?.AnalysisWorkerCapacity ?? 1;

	public CodeCompressionDiagnosticsSnapshot Diagnostics
	{
		get
		{
			int cacheEntries;
			long retainedCacheBytes;
			lock (_sync)
			{
				cacheEntries = _cache.Count;
				retainedCacheBytes = _retainedCacheBytes;
			}
			return new CodeCompressionDiagnosticsSnapshot(
				Interlocked.Read(ref _hashComputations),
				Interlocked.Read(ref _cacheHits),
				Interlocked.Read(ref _cacheMisses),
				Interlocked.Read(ref _analysisExecutions),
				Interlocked.Read(ref _prewarmRequests),
				Interlocked.Read(ref _prewarmCacheHits),
				Interlocked.Read(ref _prewarmAnalyses),
				Interlocked.Read(ref _prewarmReuses),
				Interlocked.Read(ref _unsupportedFastPaths),
				cacheEntries,
				retainedCacheBytes,
				_maximumCacheEntries,
				_maximumRetainedCacheBytes,
				(compressor as ICodeCompressionRuntimeDiagnosticsProvider)
					?.CaptureRuntimeDiagnostics() ?? CodeCompressionRuntimeDiagnosticSnapshot.Empty);
		}
	}

	public CodeCompressionSnapshot Snapshot
	{
		get
		{
			lock (_sync)
				return _snapshot;
		}
	}

	public CodeCompressionScope BeginOutput(string projectRoot, IReadOnlyList<string> orderedFilePaths)
		=> BeginOutput(
			projectRoot,
			ContentSelectionSnapshot.Create(projectRoot, orderedFilePaths),
			CodeTransformKinds.Bodies);

	public CodeCompressionScope BeginOutput(
		string projectRoot,
		ContentSelectionSnapshot selection) =>
		BeginOutput(projectRoot, selection, CodeTransformKinds.Bodies);

	public CodeCompressionScope BeginOutput(
		string projectRoot,
		ContentSelectionSnapshot selection,
		CodeTransformKinds kinds) =>
		BeginScope(projectRoot, selection, CodeCompressionScopeMode.Output, kinds);

	internal CodeCompressionScope BeginPrewarm(
		string projectRoot,
		IReadOnlyList<string> orderedFilePaths) =>
		BeginPrewarm(
			projectRoot,
			ContentSelectionSnapshot.Create(projectRoot, orderedFilePaths),
			CodeTransformKinds.Bodies);

	internal CodeCompressionScope BeginPrewarm(
		string projectRoot,
		ContentSelectionSnapshot selection) =>
		BeginPrewarm(projectRoot, selection, CodeTransformKinds.Bodies);

	internal CodeCompressionScope BeginPrewarm(
		string projectRoot,
		ContentSelectionSnapshot selection,
		CodeTransformKinds kinds) =>
		BeginScope(projectRoot, selection, CodeCompressionScopeMode.Prewarm, kinds);

	public CodeCompressionScope BeginMeasurement(string projectRoot) =>
		BeginMeasurement(projectRoot, CodeTransformKinds.Bodies);

	public CodeCompressionScope BeginMeasurement(
		string projectRoot,
		CodeTransformKinds kinds) =>
		BeginScope(
			projectRoot,
			new ContentSelectionSnapshot(0, [], string.Empty),
			CodeCompressionScopeMode.Measurement,
			kinds);

	private CodeCompressionScope BeginScope(
		string projectRoot,
		ContentSelectionSnapshot selection,
		CodeCompressionScopeMode mode,
		CodeTransformKinds kinds)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var transformIdentity = GetTransformIdentity(kinds);
		var generation = CaptureGeneration();
		return new CodeCompressionScope(
			this,
			compressor.CreateScope(projectRoot, kinds),
			mode == CodeCompressionScopeMode.Measurement
				? string.Empty
				: selection.SelectionFingerprint,
			selection.OrderedPaths,
			generation.Version,
			mode,
			kinds,
			transformIdentity);
	}

	internal CodeCompressionExecution Transform(
		ICodeCompressionScope scope,
		string fullPath,
		string relativePath,
		string content,
		ContentFingerprint? fingerprint,
		long generation,
		bool materializeOutput,
		CodeTransformKinds kinds,
		string transformIdentity,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (content.Length == 0)
		{
			var empty = CodeCompressionPlan.Unchanged(
				relativePath,
				"unknown",
				CodeCompressionOutcome.UnchangedNoBenefit,
				0,
				transformIdentity);
			return new CodeCompressionExecution(
				empty,
				materializeOutput
					? new CodeCompressionResult(content, ContentTransformMap.Identity)
					: null);
		}

		if (!compressor.IsSupported(relativePath, kinds))
		{
			var unsupported = CreateUnsupportedPlan(
				relativePath,
				content.Length,
				transformIdentity);
			return new CodeCompressionExecution(
				unsupported,
				materializeOutput
					? new CodeCompressionResult(content, ContentTransformMap.Identity)
					: null);
		}
		if (!IsCurrentGeneration(generation))
		{
			var staleAnalysis = scope.Analyze(fullPath, relativePath, content, cancellationToken);
			return CreateExecution(
				CreatePreparedPlan(staleAnalysis),
				relativePath,
				content,
				materializeOutput,
				transformIdentity,
				staleAnalysis.ValidatedResult);
		}

		var cacheTransformIdentity = GetCacheTransformIdentity(relativePath, kinds, transformIdentity);
		var key = fingerprint is { } knownFingerprint
			? CodeCompressionCacheKey.Create(
				fullPath,
				content.Length,
				knownFingerprint,
				cacheTransformIdentity)
			: CodeCompressionCacheKey.Create(
				fullPath,
				content,
				cacheTransformIdentity);
		if (fingerprint is null)
			Interlocked.Increment(ref _hashComputations);
		if (TryGetCachedPlan(key, out var cached))
		{
			Interlocked.Increment(ref _cacheHits);
			return CreateExecution(
				cached.Prepared,
				relativePath,
				content,
				materializeOutput,
				transformIdentity);
		}

		if (_prewarmInFlight.TryGetValue(key, out var warming))
		{
			Interlocked.Increment(ref _prewarmReuses);
			var analysis = warming.Value;
			cancellationToken.ThrowIfCancellationRequested();
			var warmed = CacheAnalysisOrCreatePrepared(key, analysis, generation);
			return CreateExecution(
				warmed,
				relativePath,
				content,
				materializeOutput,
				transformIdentity,
				analysis.ValidatedResult);
		}

		// Warmup publishes into the cache before removing its in-flight entry. If it completed
		// between the first cache lookup and the in-flight lookup, observe that result instead of
		// starting the same native parse again.
		if (TryGetCachedPlan(key, out cached))
		{
			Interlocked.Increment(ref _cacheHits);
			return CreateExecution(
				cached.Prepared,
				relativePath,
				content,
				materializeOutput,
				transformIdentity);
		}

		Interlocked.Increment(ref _cacheMisses);

		// Sharing is deliberately scoped to one output operation. Different operations own different
		// cancellation tokens; allowing one canceled preview to fail a simultaneous export would make
		// the cache an observable source of cross-surface coupling.
		var inFlightKey = new InFlightCompressionKey(key, scope);
		var candidate = new Lazy<CodeCompressionAnalysis>(
			() =>
			{
				Interlocked.Increment(ref _analysisExecutions);
				return scope.Analyze(fullPath, relativePath, content, cancellationToken);
			},
			LazyThreadSafetyMode.ExecutionAndPublication);
		var pending = _inFlight.GetOrAdd(inFlightKey, candidate);
		try
		{
			var analysis = pending.Value;
			cancellationToken.ThrowIfCancellationRequested();
			var cachedAnalysis = CacheAnalysisOrCreatePrepared(key, analysis, generation);
			return CreateExecution(
				cachedAnalysis,
				relativePath,
				content,
				materializeOutput,
				transformIdentity,
				analysis.ValidatedResult);
		}
		finally
		{
			if (_inFlight.TryGetValue(inFlightKey, out var current) && ReferenceEquals(current, pending))
				_inFlight.TryRemove(inFlightKey, out _);
		}
	}

	internal CodeCompressionPlan? Warm(
		ICodeCompressionScope scope,
		string fullPath,
		string relativePath,
		string content,
		ContentFingerprint? fingerprint,
		long scopeGeneration,
		CodeTransformKinds kinds,
		string transformIdentity,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (content.Length == 0)
		{
			return CodeCompressionPlan.Unchanged(
				relativePath,
				"unknown",
				CodeCompressionOutcome.UnchangedNoBenefit,
				0,
				transformIdentity);
		}

		if (!compressor.IsSupported(relativePath, kinds))
			return CreateUnsupportedPlan(relativePath, content.Length, transformIdentity);

		var generation = CaptureGeneration();
		if (generation.Version != scopeGeneration)
			return null;

		Interlocked.Increment(ref _prewarmRequests);
		var cacheTransformIdentity = GetCacheTransformIdentity(relativePath, kinds, transformIdentity);
		var key = fingerprint is { } knownFingerprint
			? CodeCompressionCacheKey.Create(
				fullPath,
				content.Length,
				knownFingerprint,
				cacheTransformIdentity)
			: CodeCompressionCacheKey.Create(fullPath, content, cacheTransformIdentity);
		if (fingerprint is null)
			Interlocked.Increment(ref _hashComputations);
		if (TryGetCachedPlan(key, out var cached))
		{
			Interlocked.Increment(ref _prewarmCacheHits);
			return WithOutputContext(cached.Prepared.Plan, relativePath, transformIdentity);
		}

		if (TryGetActiveOutputAnalysis(key, out var activeOutput))
		{
			Interlocked.Increment(ref _prewarmReuses);
			try
			{
				var analysis = activeOutput.Value;
				var prepared = CacheAnalysisOrCreatePrepared(
					key,
					analysis,
					generation.Version);
				cancellationToken.ThrowIfCancellationRequested();
				return WithOutputContext(prepared.Plan, relativePath, transformIdentity);
			}
			catch (OperationCanceledException) when (
				!generation.Token.IsCancellationRequested &&
				!cancellationToken.IsCancellationRequested)
			{
				// The foreground owner was canceled. Warmup owns an independent generation token,
				// so it can retry without allowing that cancellation to poison the shared cache.
			}
		}

		var candidate = new Lazy<CodeCompressionAnalysis>(
			() =>
			{
				Interlocked.Increment(ref _analysisExecutions);
				Interlocked.Increment(ref _prewarmAnalyses);
				return scope.Analyze(fullPath, relativePath, content, generation.Token);
			},
			LazyThreadSafetyMode.ExecutionAndPublication);
		var pending = _prewarmInFlight.GetOrAdd(key, candidate);
		try
		{
			var analysis = pending.Value;
			var prepared = CacheAnalysisOrCreatePrepared(
				key,
				analysis,
				generation.Version);
			cancellationToken.ThrowIfCancellationRequested();
			return WithOutputContext(prepared.Plan, relativePath, transformIdentity);
		}
		finally
		{
			if (_prewarmInFlight.TryGetValue(key, out var current) && ReferenceEquals(current, pending))
				_prewarmInFlight.TryRemove(key, out _);
		}
	}

	internal CodeCompressionPlan CreateUnsupportedPlan(
		string relativePath,
		int sourceLength,
		string transformIdentity)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(sourceLength);
		if (sourceLength == 0)
		{
			return CodeCompressionPlan.Unchanged(
				relativePath,
				"unknown",
				CodeCompressionOutcome.UnchangedNoBenefit,
				0,
				transformIdentity);
		}

		Interlocked.Increment(ref _unsupportedFastPaths);
		return CodeCompressionPlan.Unchanged(
			relativePath,
			"unknown",
			CodeCompressionOutcome.UnchangedUnsupportedLanguage,
			sourceLength,
			transformIdentity);
	}

	internal bool TryGetWarmCachedPlan(
		string fullPath,
		string relativePath,
		int contentLength,
		ContentFingerprint fingerprint,
		long scopeGeneration,
		CodeTransformKinds kinds,
		string transformIdentity,
		out CodeCompressionPlan plan)
	{
		if (!compressor.IsSupported(relativePath, kinds) ||
		    !IsCurrentGeneration(scopeGeneration))
		{
			plan = null!;
			return false;
		}

		var key = CodeCompressionCacheKey.Create(
			fullPath,
			contentLength,
			fingerprint,
			GetCacheTransformIdentity(relativePath, kinds, transformIdentity));
		if (!TryGetCachedPlan(key, out var cached))
		{
			plan = null!;
			return false;
		}

		Interlocked.Increment(ref _prewarmRequests);
		Interlocked.Increment(ref _prewarmCacheHits);
		plan = WithOutputContext(cached.Prepared.Plan, relativePath, transformIdentity);
		return true;
	}

	private bool TryGetActiveOutputAnalysis(
		CodeCompressionCacheKey key,
		out Lazy<CodeCompressionAnalysis> analysis)
	{
		foreach (var pair in _inFlight)
		{
			if (pair.Key.CacheKey.Equals(key))
			{
				analysis = pair.Value;
				return true;
			}
		}

		analysis = null!;
		return false;
	}

	private bool TryGetCachedPlan(CodeCompressionCacheKey key, out CachedPlan plan)
	{
		lock (_sync)
		{
			if (!_cache.TryGetValue(key, out var node))
			{
				plan = null!;
				return false;
			}

			_cacheRecency.Remove(node);
			_cacheRecency.AddFirst(node);
			plan = node.Value;
			return true;
		}
	}

	private PreparedPlan CacheAnalysisOrCreatePrepared(
		CodeCompressionCacheKey key,
		CodeCompressionAnalysis analysis,
		long generation) =>
		TryCacheAnalysis(key, analysis, generation, out var cached)
			? cached.Prepared
			: CreatePreparedPlan(analysis);

	private bool TryCacheAnalysis(
		CodeCompressionCacheKey key,
		CodeCompressionAnalysis analysis,
		long generation,
		out CachedPlan cachedPlan)
	{
		lock (_sync)
		{
			if (_disposed || generation != _generation)
			{
				cachedPlan = null!;
				return false;
			}

			if (_cache.TryGetValue(key, out var existing))
			{
				_cacheRecency.Remove(existing);
				_cacheRecency.AddFirst(existing);
				cachedPlan = existing.Value;
				return true;
			}

			var prepared = CreatePreparedPlan(analysis);
			cachedPlan = new CachedPlan(
				key,
				prepared,
				EstimateRetainedBytes(key, prepared.Plan));
			if (cachedPlan.ApproximateRetainedBytes > _maximumRetainedCacheBytes)
				return false;

			var node = _cacheRecency.AddFirst(cachedPlan);
			_cache.Add(key, node);
			_retainedCacheBytes += cachedPlan.ApproximateRetainedBytes;
			while (_cache.Count > _maximumCacheEntries ||
			       _retainedCacheBytes > _maximumRetainedCacheBytes)
			{
				var leastRecent = _cacheRecency.Last!;
				_cacheRecency.RemoveLast();
				_cache.Remove(leastRecent.Value.Key);
				_retainedCacheBytes -= leastRecent.Value.ApproximateRetainedBytes;
			}

			cachedPlan = node.Value;
			return true;
		}
	}

	private static PreparedPlan CreatePreparedPlan(CodeCompressionAnalysis analysis)
	{
		var plan = analysis.Plan;
		var map = analysis.ValidatedResult?.Map ??
		          (plan.HasEdits
			          ? ContentTransformMap.Create(plan.Edits, plan.SourceLength)
			          : ContentTransformMap.Identity);
		return new PreparedPlan(plan, map);
	}

	private static long EstimateRetainedBytes(
		CodeCompressionCacheKey key,
		CodeCompressionPlan plan)
	{
		var bytes = 256L +
		            (key.FullPath.Length + key.TransformIdentity.Length +
		             plan.RelativePath.Length + plan.LanguageId.Length +
		             plan.TransformIdentity.Length) * sizeof(char);
		foreach (var edit in plan.Edits)
			bytes += 80L + edit.Replacement.Length * sizeof(char);
		// Four integer arrays in ContentTransformMap, plus the edit collection references.
		bytes += plan.Edits.Count * (4L * sizeof(int) + IntPtr.Size);
		return bytes;
	}

	private static CodeCompressionExecution CreateExecution(
		PreparedPlan prepared,
		string relativePath,
		string content,
		bool materializeOutput,
		string transformIdentity,
		CodeCompressionResult? validatedResult = null)
	{
		var outputPlan = WithOutputContext(prepared.Plan, relativePath, transformIdentity);
		return new CodeCompressionExecution(
			outputPlan,
			materializeOutput
				? validatedResult ?? prepared.Plan.Apply(content, prepared.Map)
				: null);
	}

	private static CodeCompressionPlan WithOutputContext(
		CodeCompressionPlan plan,
		string relativePath,
		string transformIdentity) =>
		string.Equals(plan.RelativePath, relativePath, StringComparison.Ordinal) &&
		string.Equals(plan.TransformIdentity, transformIdentity, StringComparison.Ordinal)
			? plan
			: plan with
			{
				RelativePath = relativePath,
				TransformIdentity = transformIdentity
			};

	private string GetCacheTransformIdentity(
		string relativePath,
		CodeTransformKinds requestedKinds,
		string operationTransformIdentity)
	{
		var effectiveKinds = compressor.GetEffectiveTransformKinds(relativePath, requestedKinds);
		if (effectiveKinds == CodeTransformKinds.None ||
		    (effectiveKinds & requestedKinds) != effectiveKinds)
		{
			return operationTransformIdentity;
		}

		return effectiveKinds == requestedKinds
			? operationTransformIdentity
			: GetTransformIdentity(effectiveKinds);
	}

	internal void Publish(CodeCompressionSnapshot snapshot, long generation)
	{
		lock (_sync)
		{
			if (_disposed || generation != _generation)
				return;
			_snapshot = snapshot;
		}
		SnapshotPublished?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>Drops cached plans and the published snapshot; used when the project changes.</summary>
	public void Reset()
	{
		CancellationTokenSource previousGeneration;
		lock (_sync)
		{
			previousGeneration = _generationCts;
			_generationCts = new CancellationTokenSource();
			_generation++;
			_cache.Clear();
			_cacheRecency.Clear();
			_retainedCacheBytes = 0;
			_snapshot = CodeCompressionSnapshot.Empty;
		}
		previousGeneration.Cancel();
		previousGeneration.Dispose();
		_inFlight.Clear();
		_prewarmInFlight.Clear();
		ResetDiagnostics();
	}

	public void Dispose()
	{
		CancellationTokenSource generation;
		lock (_sync)
		{
			if (_disposed)
				return;
			_disposed = true;
			generation = _generationCts;
			_cache.Clear();
			_cacheRecency.Clear();
			_retainedCacheBytes = 0;
			_snapshot = CodeCompressionSnapshot.Empty;
		}
		generation.Cancel();
		generation.Dispose();
		_inFlight.Clear();
		_prewarmInFlight.Clear();
		if (compressor is IDisposable disposable)
			disposable.Dispose();
	}

	public static string BuildSelectionKey(string projectRoot, IReadOnlyList<string> orderedFilePaths)
		=> ContentSelectionSnapshot.Create(projectRoot, orderedFilePaths).SelectionFingerprint;

	private GenerationSnapshot CaptureGeneration()
	{
		lock (_sync)
			return new GenerationSnapshot(_generation, _generationCts.Token);
	}

	private bool IsCurrentGeneration(long generation) =>
		generation == Volatile.Read(ref _generation) && !_disposed;

	private void ResetDiagnostics()
	{
		Interlocked.Exchange(ref _hashComputations, 0);
		Interlocked.Exchange(ref _cacheHits, 0);
		Interlocked.Exchange(ref _cacheMisses, 0);
		Interlocked.Exchange(ref _analysisExecutions, 0);
		Interlocked.Exchange(ref _prewarmRequests, 0);
		Interlocked.Exchange(ref _prewarmCacheHits, 0);
		Interlocked.Exchange(ref _prewarmAnalyses, 0);
		Interlocked.Exchange(ref _prewarmReuses, 0);
		Interlocked.Exchange(ref _unsupportedFastPaths, 0);
	}

	private sealed record CachedPlan(
		CodeCompressionCacheKey Key,
		PreparedPlan Prepared,
		long ApproximateRetainedBytes);
	private sealed record PreparedPlan(
		CodeCompressionPlan Plan,
		ContentTransformMap Map);
	private readonly record struct GenerationSnapshot(long Version, CancellationToken Token);
	private readonly record struct InFlightCompressionKey(
		CodeCompressionCacheKey CacheKey,
		ICodeCompressionScope Scope);

	private readonly record struct CodeCompressionCacheKey(
		string FullPath,
		int ContentLength,
		ulong Hash0,
		ulong Hash1,
		ulong Hash2,
		ulong Hash3,
		string TransformIdentity)
	{
		public static CodeCompressionCacheKey Create(
			string fullPath,
			string content,
			string transformIdentity)
		{
			Span<byte> hash = stackalloc byte[32];
			SHA256.HashData(MemoryMarshal.AsBytes(content.AsSpan()), hash);
			return new CodeCompressionCacheKey(
				fullPath,
				content.Length,
				BinaryPrimitives.ReadUInt64LittleEndian(hash),
				BinaryPrimitives.ReadUInt64LittleEndian(hash[8..]),
				BinaryPrimitives.ReadUInt64LittleEndian(hash[16..]),
				BinaryPrimitives.ReadUInt64LittleEndian(hash[24..]),
				transformIdentity);
		}

		public static CodeCompressionCacheKey Create(
			string fullPath,
			int contentLength,
			ContentFingerprint fingerprint,
			string transformIdentity) =>
			new(
				fullPath,
				contentLength,
				fingerprint.Part0,
				fingerprint.Part1,
				fingerprint.Part2,
				fingerprint.Part3,
				transformIdentity);
	}
}

/// <summary>
/// One complete selection evaluation. Both prewarm and output use it, so the background pass can
/// publish exact facts without materializing transformed strings and every output later reports the
/// same plans after applying them.
/// </summary>
public sealed class CodeCompressionScope : IDisposable
{
	internal const int MaximumUnchangedDiagnosticExamples = 256;
	private readonly CodeCompressionSession session;
	private readonly ICodeCompressionScope inner;
	private readonly string selectionKey;
	private readonly long generation;
	private readonly CodeCompressionScopeMode mode;
	private readonly CodeTransformKinds kinds;
	private readonly string transformIdentity;
	private readonly SortedDictionary<DiagnosticOrderKey, CodeCompressionFileOutcome>? _unchangedExamples;
	private readonly int[]? _unchangedOutcomeCounts;
	private readonly IReadOnlyDictionary<string, int>? _fileOrder;
	private readonly object _diagnosticsSync = new();
	private int _compressed;
	private int _bodyTransformed;
	private int _commentTransformed;
	private int _unchangedFiles;
	private long _sourceCharacters;
	private long _transformedCharacters;
	private int _completed;

	internal CodeCompressionScope(
		CodeCompressionSession session,
		ICodeCompressionScope inner,
		string selectionKey,
		IReadOnlyList<string> orderedFilePaths,
		long generation,
		CodeCompressionScopeMode mode,
		CodeTransformKinds kinds,
		string transformIdentity)
	{
		this.session = session;
		this.inner = inner;
		this.selectionKey = selectionKey;
		this.generation = generation;
		this.mode = mode;
		this.kinds = kinds;
		this.transformIdentity = transformIdentity;
		if (mode == CodeCompressionScopeMode.Measurement)
			return;

		_unchangedExamples = new SortedDictionary<DiagnosticOrderKey, CodeCompressionFileOutcome>(
			DiagnosticOrderKeyComparer.Instance);
		_unchangedOutcomeCounts = new int[Enum.GetValues<CodeCompressionOutcome>().Length];
		_fileOrder = orderedFilePaths
			.Select(static (path, index) => (path, index))
			.ToDictionary(static item => item.path, static item => item.index, PathComparer.Default);
	}

	public CodeCompressionResult Transform(
		string fullPath,
		string relativePath,
		string content,
		CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _completed) != 0, this);
		var execution = session.Transform(
			inner,
			fullPath,
			relativePath,
			content,
			fingerprint: null,
			generation,
			materializeOutput: true,
			kinds,
			transformIdentity,
			cancellationToken);
		var plan = execution.Plan;
		RecordPlan(fullPath, plan);
		if (plan.Outcome == CodeCompressionOutcome.Compressed)
			return execution.Output!;

		return new CodeCompressionResult(content, ContentTransformMap.Identity);
	}

	public CodeCompressionResult Transform(
		string fullPath,
		string relativePath,
		string content,
		ContentFingerprint fingerprint,
		CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _completed) != 0, this);
		var execution = session.Transform(
			inner,
			fullPath,
			relativePath,
			content,
			fingerprint,
			generation,
			materializeOutput: true,
			kinds,
			transformIdentity,
			cancellationToken);
		var plan = execution.Plan;
		RecordPlan(fullPath, plan);
		return plan.Outcome == CodeCompressionOutcome.Compressed
			? execution.Output!
			: new CodeCompressionResult(content, ContentTransformMap.Identity);
	}

	public CodeCompressionPlan ResolvePlan(
		string fullPath,
		string relativePath,
		string content,
		CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _completed) != 0, this);
		var execution = session.Transform(
			inner,
			fullPath,
			relativePath,
			content,
			fingerprint: null,
			generation,
			materializeOutput: false,
			kinds,
			transformIdentity,
			cancellationToken);
		RecordPlan(fullPath, execution.Plan);
		return execution.Plan;
	}

	public CodeCompressionPlan ResolvePlan(
		string fullPath,
		string relativePath,
		string content,
		ContentFingerprint fingerprint,
		CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _completed) != 0, this);
		var execution = session.Transform(
			inner,
			fullPath,
			relativePath,
			content,
			fingerprint,
			generation,
			materializeOutput: false,
			kinds,
			transformIdentity,
			cancellationToken);
		RecordPlan(fullPath, execution.Plan);
		return execution.Plan;
	}

	internal bool Warm(
		string fullPath,
		string relativePath,
		string content,
		CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _completed) != 0, this);
		var plan = session.Warm(
			inner,
			fullPath,
			relativePath,
			content,
			fingerprint: null,
			generation,
			kinds,
			transformIdentity,
			cancellationToken);
		if (plan is null)
			return false;

		RecordPlan(fullPath, plan);
		return true;
	}

	internal bool TryWarmCached(
		string fullPath,
		string relativePath,
		string content,
		ContentFingerprint fingerprint)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _completed) != 0, this);
		if (!session.TryGetWarmCachedPlan(
				fullPath,
				relativePath,
				content.Length,
				fingerprint,
				generation,
				kinds,
				transformIdentity,
				out var plan))
		{
			return false;
		}
		RecordPlan(fullPath, plan);
		return true;
	}

	internal bool Warm(
		string fullPath,
		string relativePath,
		string content,
		ContentFingerprint fingerprint,
		CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _completed) != 0, this);
		var plan = session.Warm(
			inner,
			fullPath,
			relativePath,
			content,
			fingerprint,
			generation,
			kinds,
			transformIdentity,
			cancellationToken);
		if (plan is null)
			return false;
		RecordPlan(fullPath, plan);
		return true;
	}

	internal void RecordUnsupported(
		string fullPath,
		string relativePath,
		int sourceLength)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _completed) != 0, this);
		RecordPlan(
			fullPath,
			session.CreateUnsupportedPlan(relativePath, sourceLength, transformIdentity));
	}

	private void RecordPlan(string fullPath, CodeCompressionPlan plan)
	{
		if (mode == CodeCompressionScopeMode.Measurement)
			return;

		Interlocked.Add(ref _sourceCharacters, plan.SourceLength);
		if (plan.Outcome == CodeCompressionOutcome.Compressed)
		{
			Interlocked.Increment(ref _compressed);
			if ((plan.AffectedKinds & CodeTransformKinds.Bodies) != 0)
				Interlocked.Increment(ref _bodyTransformed);
			if ((plan.AffectedKinds & CodeTransformKinds.Comments) != 0)
				Interlocked.Increment(ref _commentTransformed);
			Interlocked.Add(ref _transformedCharacters, plan.TransformedLength);
			return;
		}

		Interlocked.Add(ref _transformedCharacters, plan.SourceLength);
		Interlocked.Increment(ref _unchangedFiles);
		Interlocked.Increment(ref _unchangedOutcomeCounts![(int)plan.Outcome]);
		var order = new DiagnosticOrderKey(
			_fileOrder!.GetValueOrDefault(fullPath, int.MaxValue),
			plan.RelativePath);
		lock (_diagnosticsSync)
		{
			if (_unchangedExamples!.Count >= MaximumUnchangedDiagnosticExamples &&
			    DiagnosticOrderKeyComparer.Instance.Compare(
				    order,
				    _unchangedExamples.Last().Key) >= 0)
			{
				return;
			}
			_unchangedExamples[order] = new CodeCompressionFileOutcome(
				plan.RelativePath,
				plan.LanguageId,
				plan.Outcome,
				plan.SourceLength,
				plan.SourceLength);
			if (_unchangedExamples.Count > MaximumUnchangedDiagnosticExamples)
				_unchangedExamples.Remove(_unchangedExamples.Last().Key);
		}
	}

	public CodeCompressionSnapshot Complete()
	{
		if (mode == CodeCompressionScopeMode.Measurement)
			throw new InvalidOperationException("A measurement scope cannot publish a compression snapshot.");
		if (Interlocked.Exchange(ref _completed, 1) != 0)
			throw new InvalidOperationException("The compression scope has already completed.");
		CodeCompressionFileOutcome[] unchanged;
		lock (_diagnosticsSync)
			unchanged = _unchangedExamples!.Values.ToArray();
		var unchangedFiles = Volatile.Read(ref _unchangedFiles);
		var outcomeCounts = Enum.GetValues<CodeCompressionOutcome>()
			.Where(static outcome => outcome != CodeCompressionOutcome.Compressed)
			.ToDictionary(
				static outcome => outcome,
				outcome => Volatile.Read(ref _unchangedOutcomeCounts![(int)outcome]));
		var snapshot = new CodeCompressionSnapshot(
			selectionKey,
			Volatile.Read(ref _compressed),
			unchangedFiles,
			Interlocked.Read(ref _sourceCharacters),
			Interlocked.Read(ref _transformedCharacters),
			unchanged,
			outcomeCounts,
			Math.Max(0, unchangedFiles - unchanged.Length),
			Volatile.Read(ref _bodyTransformed),
			Volatile.Read(ref _commentTransformed),
			transformIdentity);
		session.Publish(snapshot, generation);
		return snapshot;
	}

	public void Dispose() => inner.Dispose();

	private readonly record struct DiagnosticOrderKey(int Order, string RelativePath);

	private sealed class DiagnosticOrderKeyComparer : IComparer<DiagnosticOrderKey>
	{
		public static DiagnosticOrderKeyComparer Instance { get; } = new();

		public int Compare(DiagnosticOrderKey left, DiagnosticOrderKey right)
		{
			var order = left.Order.CompareTo(right.Order);
			return order != 0
				? order
				: PathComparer.Default.Compare(left.RelativePath, right.RelativePath);
		}
	}

}

/// <summary>
/// Identifies an enabled compression operation. A null context is the deliberate fast path: no
/// grammar is loaded and existing output stays byte-for-byte unchanged.
/// </summary>
public sealed record CodeCompressionContext(
	string ProjectRoot,
	CodeCompressionSession Session,
	CodeTransformKinds Kinds = CodeTransformKinds.Bodies)
{
	public string TransformIdentity => Session.GetTransformIdentity(Kinds);

	public bool IsSupported(string relativePath) => Session.IsSupported(relativePath, Kinds);

	public CodeCompressionScope BeginOutput(IReadOnlyList<string> orderedFilePaths) =>
		Session.BeginOutput(
			ProjectRoot,
			ContentSelectionSnapshot.Create(ProjectRoot, orderedFilePaths),
			Kinds);

	public CodeCompressionScope BeginOutput(ContentSelectionSnapshot selection) =>
		Session.BeginOutput(ProjectRoot, selection, Kinds);

	internal CodeCompressionScope BeginPrewarm(IReadOnlyList<string> orderedFilePaths) =>
		Session.BeginPrewarm(
			ProjectRoot,
			ContentSelectionSnapshot.Create(ProjectRoot, orderedFilePaths),
			Kinds);

	internal CodeCompressionScope BeginPrewarm(ContentSelectionSnapshot selection) =>
		Session.BeginPrewarm(ProjectRoot, selection, Kinds);

	public CodeCompressionScope BeginMeasurement() =>
		Session.BeginMeasurement(ProjectRoot, Kinds);
}
