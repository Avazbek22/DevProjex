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
/// What the user is told after a run. Counts are facts, not claims: the UI must never say the
/// project "is compressed", only how many files were and how many were not.
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

/// <summary>
/// Window-lifetime state for code compression: the compressor, a plan cache and the last published
/// snapshot. Deliberately shaped like <see cref="Secrets.SecretRedactionSession"/> - same
/// session/scope/snapshot split, same cache-key composition - because every consumer already knows
/// that shape.
/// </summary>
public sealed class CodeCompressionSession(ICodeCompressor compressor) : IDisposable
{
	private const int MaximumCachedPlans = 4096;
	private readonly Dictionary<CodeCompressionCacheKey, LinkedListNode<CachedPlan>> _cache = [];
	private readonly LinkedList<CachedPlan> _cacheRecency = [];
	private readonly ConcurrentDictionary<InFlightCompressionKey, Lazy<CodeCompressionAnalysis>> _inFlight = [];
	private readonly object _sync = new();
	private CodeCompressionSnapshot _snapshot = CodeCompressionSnapshot.Empty;
	private bool _disposed;

	public event EventHandler? SnapshotPublished;

	public string TransformIdentity => compressor.TransformIdentity;

	public bool IsSupported(string relativePath) => compressor.IsSupported(relativePath);

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
		return new CodeCompressionScope(
			this,
			compressor.CreateScope(projectRoot),
			BuildSelectionKey(projectRoot, orderedFilePaths),
			orderedFilePaths);
	}

	internal CodeCompressionExecution Transform(
		ICodeCompressionScope scope,
		string fullPath,
		string relativePath,
		string content,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var key = CodeCompressionCacheKey.Create(
			relativePath,
			content,
			compressor.TransformIdentity);
		if (TryGetCachedPlan(key, out var cached))
			return new CodeCompressionExecution(cached, cached.Apply(content));

		// Sharing is deliberately scoped to one output operation. Different operations own different
		// cancellation tokens; allowing one canceled preview to fail a simultaneous export would make
		// the cache an observable source of cross-surface coupling.
		var inFlightKey = new InFlightCompressionKey(key, scope);
		var candidate = new Lazy<CodeCompressionAnalysis>(
			() => scope.Analyze(fullPath, relativePath, content, cancellationToken),
			LazyThreadSafetyMode.ExecutionAndPublication);
		var pending = _inFlight.GetOrAdd(inFlightKey, candidate);
		try
		{
			var analysis = pending.Value;
			cancellationToken.ThrowIfCancellationRequested();
			CachePlan(key, analysis.Plan);
			return new CodeCompressionExecution(analysis.Plan, analysis.GetResult(content));
		}
		finally
		{
			if (_inFlight.TryGetValue(inFlightKey, out var current) && ReferenceEquals(current, pending))
				_inFlight.TryRemove(inFlightKey, out _);
		}
	}

	private bool TryGetCachedPlan(CodeCompressionCacheKey key, out CodeCompressionPlan plan)
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
			plan = node.Value.Plan;
			return true;
		}
	}

	private void CachePlan(CodeCompressionCacheKey key, CodeCompressionPlan plan)
	{
		lock (_sync)
		{
			if (_cache.TryGetValue(key, out var existing))
			{
				_cacheRecency.Remove(existing);
				existing.Value = new CachedPlan(key, plan);
				_cacheRecency.AddFirst(existing);
				return;
			}

			var node = _cacheRecency.AddFirst(new CachedPlan(key, plan));
			_cache.Add(key, node);
			while (_cache.Count > MaximumCachedPlans)
			{
				var leastRecent = _cacheRecency.Last!;
				_cacheRecency.RemoveLast();
				_cache.Remove(leastRecent.Value.Key);
			}
		}
	}

	internal void Publish(CodeCompressionSnapshot snapshot)
	{
		lock (_sync)
			_snapshot = snapshot;
		SnapshotPublished?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>Drops cached plans and the published snapshot; used when the project changes.</summary>
	public void Reset()
	{
		lock (_sync)
		{
			_cache.Clear();
			_cacheRecency.Clear();
			_snapshot = CodeCompressionSnapshot.Empty;
		}
		_inFlight.Clear();
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		Reset();
		if (compressor is IDisposable disposable)
			disposable.Dispose();
	}

	public static string BuildSelectionKey(string projectRoot, IReadOnlyList<string> orderedFilePaths)
	{
		var builder = new StringBuilder(projectRoot.Length + orderedFilePaths.Sum(static path => path.Length + 12));
		AppendLengthPrefixed(builder, projectRoot);
		builder.Append(orderedFilePaths.Count).Append(':');
		foreach (var path in orderedFilePaths)
			AppendLengthPrefixed(builder, path);

		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
	}

	private static void AppendLengthPrefixed(StringBuilder builder, string value) =>
		builder.Append(value.Length).Append(':').Append(value);

	private sealed record CachedPlan(CodeCompressionCacheKey Key, CodeCompressionPlan Plan);
	private readonly record struct InFlightCompressionKey(
		CodeCompressionCacheKey CacheKey,
		ICodeCompressionScope Scope);

	private readonly record struct CodeCompressionCacheKey(
		string RelativePath,
		int ContentLength,
		ulong Hash0,
		ulong Hash1,
		ulong Hash2,
		ulong Hash3,
		string TransformIdentity)
	{
		public static CodeCompressionCacheKey Create(
			string relativePath,
			string content,
			string transformIdentity)
		{
			Span<byte> hash = stackalloc byte[32];
			SHA256.HashData(MemoryMarshal.AsBytes(content.AsSpan()), hash);
			return new CodeCompressionCacheKey(
				relativePath,
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
/// One output operation. Accumulates per-file outcomes so the summary counts what actually left the
/// application rather than what the engine could theoretically do.
/// </summary>
public sealed class CodeCompressionScope(
	CodeCompressionSession session,
	ICodeCompressionScope inner,
	string selectionKey,
	IReadOnlyList<string> orderedFilePaths) : IDisposable
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
		var execution = session.Transform(inner, fullPath, relativePath, content, cancellationToken);
		var plan = execution.Plan;
		Interlocked.Add(ref _sourceCharacters, plan.SourceLength);
		if (plan.Outcome == CodeCompressionOutcome.Compressed)
		{
			Interlocked.Increment(ref _compressed);
			Interlocked.Add(ref _transformedCharacters, plan.TransformedLength);
			return execution.Output;
		}

		Interlocked.Add(ref _transformedCharacters, plan.SourceLength);
		_unchanged.Enqueue(new OrderedCompressionOutcome(
			fullPath,
			new CodeCompressionFileOutcome(
				relativePath,
				plan.LanguageId,
				plan.Outcome,
				plan.SourceLength,
				plan.SourceLength)));
		return new CodeCompressionResult(content, ContentTransformMap.Identity);
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
		session.Publish(snapshot);
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
