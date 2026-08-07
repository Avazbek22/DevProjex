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
	private readonly Dictionary<string, CachedPlan> _cache = new(StringComparer.Ordinal);
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
		return new CodeCompressionScope(this, compressor.CreateScope(projectRoot), BuildSelectionKey(projectRoot, orderedFilePaths));
	}

	internal CodeCompressionPlan GetOrCreatePlan(
		ICodeCompressionScope scope,
		string fullPath,
		string relativePath,
		string content,
		CancellationToken cancellationToken)
	{
		// Keyed on the content itself rather than on file metadata: the same bytes always produce
		// the same plan, and a stat-based key would serve a stale plan after an in-place edit.
		var key = $"{relativePath}\0{content.Length}\0{content.GetHashCode(StringComparison.Ordinal)}\0{compressor.TransformIdentity}";
		lock (_sync)
		{
			if (_cache.TryGetValue(key, out var cached) && cached.Length == content.Length)
				return cached.Plan;
		}

		var plan = scope.Plan(fullPath, relativePath, content, cancellationToken);
		lock (_sync)
		{
			if (_cache.Count > 4096)
				_cache.Clear();
			_cache[key] = new CachedPlan(plan, content.Length);
		}

		return plan;
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
			_snapshot = CodeCompressionSnapshot.Empty;
		}
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		Reset();
	}

	public static string BuildSelectionKey(string projectRoot, IReadOnlyList<string> orderedFilePaths) =>
		$"{projectRoot}\0{orderedFilePaths.Count}\0{string.Join("\0", orderedFilePaths).GetHashCode(StringComparison.Ordinal)}";

	private sealed record CachedPlan(CodeCompressionPlan Plan, int Length);
}

/// <summary>
/// One output operation. Accumulates per-file outcomes so the summary counts what actually left the
/// application rather than what the engine could theoretically do.
/// </summary>
public sealed class CodeCompressionScope(
	CodeCompressionSession session,
	ICodeCompressionScope inner,
	string selectionKey) : IDisposable
{
	private readonly List<CodeCompressionFileOutcome> _unchanged = [];
	// One scope is shared by the parallel metrics scan, and the underlying parser is native state
	// that cannot be entered twice. Serializing here rather than at each call site also keeps the
	// accumulated counts from tearing.
	private readonly Lock _sync = new();
	private int _compressed;
	private long _sourceCharacters;
	private long _transformedCharacters;

	public CodeCompressionResult Transform(
		string fullPath,
		string relativePath,
		string content,
		CancellationToken cancellationToken)
	{
		lock (_sync)
			return TransformCore(fullPath, relativePath, content, cancellationToken);
	}

	private CodeCompressionResult TransformCore(
		string fullPath,
		string relativePath,
		string content,
		CancellationToken cancellationToken)
	{
		var plan = session.GetOrCreatePlan(inner, fullPath, relativePath, content, cancellationToken);
		_sourceCharacters += plan.SourceLength;
		if (plan.Outcome == CodeCompressionOutcome.Compressed)
		{
			_compressed++;
			_transformedCharacters += plan.TransformedLength;
			return plan.Apply(content);
		}

		_transformedCharacters += plan.SourceLength;
		_unchanged.Add(new CodeCompressionFileOutcome(
			relativePath,
			plan.LanguageId,
			plan.Outcome,
			plan.SourceLength,
			plan.SourceLength));
		return new CodeCompressionResult(content, ContentTransformMap.Identity);
	}

	public CodeCompressionSnapshot Complete()
	{
		lock (_sync)
			return CompleteCore();
	}

	private CodeCompressionSnapshot CompleteCore()
	{
		var snapshot = new CodeCompressionSnapshot(
			selectionKey,
			_compressed,
			_unchanged.Count,
			_sourceCharacters,
			_transformedCharacters,
			_unchanged.ToArray());
		session.Publish(snapshot);
		return snapshot;
	}

	public void Dispose() => inner.Dispose();
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
