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
	IReadOnlyList<CodeCompressionFileOutcome> Unchanged)
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
	long MaximumRetainedCacheBytes);

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

	public string TransformIdentity => compressor.TransformIdentity;

	public bool IsSupported(string relativePath) => compressor.IsSupported(relativePath);

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
				_maximumRetainedCacheBytes);
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
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var generation = CaptureGeneration();
		return new CodeCompressionScope(
			this,
			compressor.CreateScope(projectRoot),
			BuildSelectionKey(projectRoot, orderedFilePaths),
			orderedFilePaths,
			generation.Version);
	}

	internal CodeCompressionExecution Transform(
		ICodeCompressionScope scope,
		string fullPath,
		string relativePath,
		string content,
		long generation,
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
				compressor.TransformIdentity);
			return new CodeCompressionExecution(
				empty,
				new CodeCompressionResult(content, ContentTransformMap.Identity));
		}

		if (!compressor.IsSupported(relativePath))
		{
			Interlocked.Increment(ref _unsupportedFastPaths);
			var unsupported = CodeCompressionPlan.Unchanged(
				relativePath,
				"unknown",
				CodeCompressionOutcome.UnchangedUnsupportedLanguage,
				content.Length,
				compressor.TransformIdentity);
			return new CodeCompressionExecution(
				unsupported,
				new CodeCompressionResult(content, ContentTransformMap.Identity));
		}
		if (!IsCurrentGeneration(generation))
		{
			var staleAnalysis = scope.Analyze(fullPath, relativePath, content, cancellationToken);
			return CreateExecution(
				CreatePreparedPlan(staleAnalysis),
				relativePath,
				content,
				staleAnalysis.ValidatedResult);
		}

		var key = CodeCompressionCacheKey.Create(
			fullPath,
			content,
			compressor.TransformIdentity);
		Interlocked.Increment(ref _hashComputations);
		if (TryGetCachedPlan(key, out var cached))
		{
			Interlocked.Increment(ref _cacheHits);
			return CreateExecution(cached.Prepared, relativePath, content);
		}

		if (_prewarmInFlight.TryGetValue(key, out var warming))
		{
			Interlocked.Increment(ref _prewarmReuses);
			var analysis = warming.Value;
			cancellationToken.ThrowIfCancellationRequested();
			var warmed = CacheAnalysisOrCreatePrepared(key, analysis, generation);
			return CreateExecution(warmed, relativePath, content, analysis.ValidatedResult);
		}

		// Warmup publishes into the cache before removing its in-flight entry. If it completed
		// between the first cache lookup and the in-flight lookup, observe that result instead of
		// starting the same native parse again.
		if (TryGetCachedPlan(key, out cached))
		{
			Interlocked.Increment(ref _cacheHits);
			return CreateExecution(cached.Prepared, relativePath, content);
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
			return CreateExecution(cachedAnalysis, relativePath, content, analysis.ValidatedResult);
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
		long scopeGeneration,
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
				compressor.TransformIdentity);
		}

		if (!compressor.IsSupported(relativePath))
		{
			Interlocked.Increment(ref _unsupportedFastPaths);
			return CodeCompressionPlan.Unchanged(
				relativePath,
				"unknown",
				CodeCompressionOutcome.UnchangedUnsupportedLanguage,
				content.Length,
				compressor.TransformIdentity);
		}

		var generation = CaptureGeneration();
		if (generation.Version != scopeGeneration)
			return null;

		Interlocked.Increment(ref _prewarmRequests);
		var key = CodeCompressionCacheKey.Create(fullPath, content, compressor.TransformIdentity);
		Interlocked.Increment(ref _hashComputations);
		if (TryGetCachedPlan(key, out var cached))
		{
			Interlocked.Increment(ref _prewarmCacheHits);
			return WithRelativePath(cached.Prepared.Plan, relativePath);
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
				return WithRelativePath(prepared.Plan, relativePath);
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
			return WithRelativePath(prepared.Plan, relativePath);
		}
		finally
		{
			if (_prewarmInFlight.TryGetValue(key, out var current) && ReferenceEquals(current, pending))
				_prewarmInFlight.TryRemove(key, out _);
		}
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
		CodeCompressionResult? validatedResult = null)
	{
		var outputPlan = WithRelativePath(prepared.Plan, relativePath);
		return new CodeCompressionExecution(
			outputPlan,
			validatedResult ?? prepared.Plan.Apply(content, prepared.Map));
	}

	private static CodeCompressionPlan WithRelativePath(
		CodeCompressionPlan plan,
		string relativePath) =>
		string.Equals(plan.RelativePath, relativePath, StringComparison.Ordinal)
			? plan
			: plan with { RelativePath = relativePath };

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
	{
		var canonicalPaths = orderedFilePaths
			.OrderBy(static path => path, PathComparer.Default)
			.ToArray();
		var builder = new StringBuilder(projectRoot.Length + canonicalPaths.Sum(static path => path.Length + 12));
		AppendLengthPrefixed(builder, projectRoot);
		builder.Append(canonicalPaths.Length).Append(':');
		foreach (var path in canonicalPaths)
			AppendLengthPrefixed(builder, path);

		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
	}

	private static void AppendLengthPrefixed(StringBuilder builder, string value) =>
		builder.Append(value.Length).Append(':').Append(value);

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
	}
}

/// <summary>
/// One complete selection evaluation. Both prewarm and output use it, so the background pass can
/// publish exact facts without materializing transformed strings and every output later reports the
/// same plans after applying them.
/// </summary>
public sealed class CodeCompressionScope(
	CodeCompressionSession session,
	ICodeCompressionScope inner,
	string selectionKey,
	IReadOnlyList<string> orderedFilePaths,
	long generation) : IDisposable
{
	private readonly ConcurrentQueue<OrderedCompressionOutcome> _unchanged = [];
	private readonly IReadOnlyDictionary<string, int> _fileOrder = orderedFilePaths
		.Select(static (path, index) => (path, index))
		.ToDictionary(static item => item.path, static item => item.index, PathComparer.Default);
	private int _compressed;
	private long _sourceCharacters;
	private long _transformedCharacters;
	private int _completed;

	public CodeCompressionResult Transform(
		string fullPath,
		string relativePath,
		string content,
		CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _completed) != 0, this);
		var execution = session.Transform(inner, fullPath, relativePath, content, generation, cancellationToken);
		var plan = execution.Plan;
		RecordPlan(fullPath, plan);
		if (plan.Outcome == CodeCompressionOutcome.Compressed)
			return execution.Output;

		return new CodeCompressionResult(content, ContentTransformMap.Identity);
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
			generation,
			cancellationToken);
		if (plan is null)
			return false;

		RecordPlan(fullPath, plan);
		return true;
	}

	private void RecordPlan(string fullPath, CodeCompressionPlan plan)
	{
		Interlocked.Add(ref _sourceCharacters, plan.SourceLength);
		if (plan.Outcome == CodeCompressionOutcome.Compressed)
		{
			Interlocked.Increment(ref _compressed);
			Interlocked.Add(ref _transformedCharacters, plan.TransformedLength);
			return;
		}

		Interlocked.Add(ref _transformedCharacters, plan.SourceLength);
		_unchanged.Enqueue(new OrderedCompressionOutcome(
			fullPath,
			new CodeCompressionFileOutcome(
				plan.RelativePath,
				plan.LanguageId,
				plan.Outcome,
				plan.SourceLength,
				plan.SourceLength)));
	}

	public CodeCompressionSnapshot Complete()
	{
		if (Interlocked.Exchange(ref _completed, 1) != 0)
			throw new InvalidOperationException("The compression scope has already completed.");
		var unchanged = _unchanged
			.OrderBy(outcome => _fileOrder.GetValueOrDefault(outcome.FullPath, int.MaxValue))
			.ThenBy(static outcome => outcome.Outcome.RelativePath, PathComparer.Default)
			.Select(static outcome => outcome.Outcome)
			.ToArray();
		var snapshot = new CodeCompressionSnapshot(
			selectionKey,
			Volatile.Read(ref _compressed),
			unchanged.Length,
			Interlocked.Read(ref _sourceCharacters),
			Interlocked.Read(ref _transformedCharacters),
			unchanged);
		session.Publish(snapshot, generation);
		return snapshot;
	}

	public void Dispose() => inner.Dispose();

	private sealed record OrderedCompressionOutcome(
		string FullPath,
		CodeCompressionFileOutcome Outcome);
}

/// <summary>
/// Identifies an enabled compression operation. A null context is the deliberate fast path: no
/// grammar is loaded and existing output stays byte-for-byte unchanged.
/// </summary>
public sealed record CodeCompressionContext(string ProjectRoot, CodeCompressionSession Session)
{
	public CodeCompressionScope BeginOutput(IReadOnlyList<string> orderedFilePaths) =>
		Session.BeginOutput(ProjectRoot, orderedFilePaths);
}
