using DevProjex.Application.Compression;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using TreeSitter;

namespace DevProjex.Infrastructure.Compression;

/// <summary>One language this build can compress, as seen from outside Infrastructure.</summary>
public sealed record CompressionLanguageInfo(
	string Id,
	string DisplayName,
	IReadOnlyList<string> Extensions,
	string GrammarLibrary,
	string GrammarExport,
	CodeTransformKinds TransformCapabilities);

internal enum TreeSitterAnalysisPhase
{
	LanguageResolution,
	WorkerRent,
	OriginalParse,
	PreserveQuery,
	BodyQuery,
	CommentQuery,
	OriginalDeclarations,
	OriginalDefectWalk,
	EditShaping,
	PlanBuild,
	PlanApply,
	ReverseParse,
	ReverseDeclarations,
	ReverseDefectWalk,
	StructureGate
}

internal interface ITreeSitterAnalysisPhaseObserver
{
	void OnPhaseCompleted(TreeSitterAnalysisPhase phase);
}

internal enum TreeSitterAnalysisCheckpoint
{
	TrailingWhitespaceScan,
	ContainingBodyAncestorWalk,
	CommentClusterScan
}

internal interface ITreeSitterAnalysisCheckpointObserver
{
	void OnCheckpoint(TreeSitterAnalysisCheckpoint checkpoint, int iteration);
}

/// <summary>
/// Builds compression plans with tree-sitter. Everything language-specific is data: the grammar,
/// the queries and the placeholder come from <see cref="CompressionLanguagePack"/>.
/// </summary>
public sealed class TreeSitterCodeCompressor :
	ICodeCompressor,
	ICodeCompressionRuntimeDiagnosticsProvider,
	IDisposable
{
	/// <summary>
	/// Parsing cannot be interrupted. tree-sitter 0.26.3 removed ts_parser_set_timeout_micros and
	/// ts_parser_set_cancellation_flag, and the binding does not expose the replacement
	/// ts_parser_parse_with_options, so a pathological file can neither be bounded nor aborted once
	/// started. A size cap before parsing is the only defence there is; files above it are reported
	/// as UnchangedTooLarge so the user sees a reason rather than an unexplained refusal.
	/// </summary>
	public const int MaximumParsableCharacters = 2 * 1024 * 1024;
	internal const uint MaximumQueryMatchLimit = 4096;
	internal const int MaximumBodyCapturesPerFile = 20_000;
	internal const int MaximumCommentCapturesPerFile = 20_000;
	internal const int MaximumPreservedRangesPerFile = 20_000;
	internal const int MaximumEditsPerFile = 10_000;
	internal const int MaximumDeclarationsPerFile = 25_000;
	internal const int MaximumDefectsPerFile = 4_096;
	internal const int MaximumVisitedSyntaxNodesPerFile = 500_000;

	private readonly IGrammarLibraryLocator _locator;
	private readonly IReadOnlyList<CompressionLanguagePack> _packs;
	private readonly Dictionary<string, IReadOnlyList<CompressionLanguagePack>> _byExtension;
	private readonly Dictionary<string, LanguageWorkerPool> _languagePools = [];
	private readonly LanguageWorkerBudget _workerBudget;
	private readonly uint _queryMatchLimit;
	private readonly ITreeSitterAnalysisPhaseObserver? _analysisPhaseObserver;
	private readonly object _lifetimeSync = new();
	private TreeSitterAnalysisDiagnosticsSession? _analysisDiagnostics;
	private long _nextScopeId;
	private int _activeScopes;
	private bool _disposeRequested;
	private bool _resourcesDisposed;

	public TreeSitterCodeCompressor(IGrammarLibraryLocator locator)
		: this(locator, CompressionLanguagePack.LoadAll())
	{
	}

	internal TreeSitterCodeCompressor(
		IGrammarLibraryLocator locator,
		IReadOnlyList<CompressionLanguagePack> packs,
		uint queryMatchLimit = MaximumQueryMatchLimit,
		ITreeSitterAnalysisPhaseObserver? analysisPhaseObserver = null)
	{
		ArgumentOutOfRangeException.ThrowIfZero(queryMatchLimit);
		_locator = locator;
		_packs = packs;
		_queryMatchLimit = queryMatchLimit;
		_analysisPhaseObserver = analysisPhaseObserver;
		_workerBudget = new LanguageWorkerBudget(
			packs.Select(static pack => pack.Id).Distinct(StringComparer.Ordinal).Count());
		_byExtension = packs
			.SelectMany(pack => pack.Extensions.Select(extension => (extension, pack)))
			.GroupBy(static pair => pair.extension, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(
				static group => group.Key,
				static group => (IReadOnlyList<CompressionLanguagePack>)group
					.Select(static pair => pair.pack)
					.OrderBy(static pack => pack.Id, StringComparer.Ordinal)
					.ToArray(),
				StringComparer.OrdinalIgnoreCase);
		TransformIdentity = "tree-sitter:" + string.Join(",", packs.Select(static pack => pack.Identity));
	}

	public string TransformIdentity { get; }

	public int AnalysisWorkerCapacity => _workerBudget.Diagnostics.Capacity;

	public CodeCompressionRuntimeDiagnosticSnapshot CaptureRuntimeDiagnostics()
	{
		var diagnostics = RuntimeDiagnostics;
		return new CodeCompressionRuntimeDiagnosticSnapshot(
			diagnostics.CompiledQuerySets,
			diagnostics.MaterializedWorkers,
			diagnostics.AvailableWorkers,
			diagnostics.LeasedWorkers,
			diagnostics.GlobalWorkerCapacity,
			diagnostics.GlobalActiveWorkers,
			diagnostics.GlobalPeakActiveWorkers,
			diagnostics.GlobalRetainedWorkers,
			diagnostics.GlobalRetainedWorkerCapacity);
	}

	internal IReadOnlyList<CompressionLanguagePack> Packs => _packs;

	internal CodeCompressionRuntimeDiagnostics RuntimeDiagnostics
	{
		get
		{
			LanguagePoolDiagnostics[] diagnostics;
			lock (_lifetimeSync)
			{
				diagnostics = _languagePools.Values
					.Select(static pool => pool.Diagnostics)
					.ToArray();
			}
			var budget = _workerBudget.Diagnostics;
			return new CodeCompressionRuntimeDiagnostics(
				diagnostics.Sum(static value => value.CompiledQuerySets),
				diagnostics.Sum(static value => value.MaterializedWorkers),
				diagnostics.Sum(static value => value.AvailableWorkers),
				diagnostics.Sum(static value => value.LeasedWorkers),
				budget.Capacity,
				budget.Active,
				budget.PeakActive,
				budget.Retained,
				budget.RetainedCapacity);
		}
	}

	/// <summary>
	/// What this build can actually compress. Shipping a grammar is not the same as supporting a
	/// language, so this is the list the UI and the delivery check must both read - never a
	/// hard-coded one that can drift from what is embedded.
	/// </summary>
	public IReadOnlyList<CompressionLanguageInfo> Languages =>
		_packs.Select(static pack => new CompressionLanguageInfo(
			pack.Id,
			pack.DisplayName,
			pack.Extensions,
			pack.Library,
			pack.Export,
			pack.TransformCapabilities)).ToArray();

	public bool IsSupported(string relativePath) =>
		IsSupported(relativePath, CodeTransformKinds.Bodies);

	public bool IsSupported(string relativePath, CodeTransformKinds kinds) =>
		GetEffectiveTransformKinds(relativePath, kinds) != CodeTransformKinds.None;

	public CodeTransformKinds GetEffectiveTransformKinds(string relativePath, CodeTransformKinds kinds)
	{
		ValidateTransformKinds(kinds);
		if (!_byExtension.TryGetValue(Path.GetExtension(relativePath), out var candidates))
			return CodeTransformKinds.None;

		var capabilities = CodeTransformKinds.None;
		foreach (var candidate in candidates)
			capabilities |= candidate.TransformCapabilities;
		return kinds & capabilities;
	}

	internal TreeSitterAnalysisDiagnosticsSession BeginAnalysisDiagnostics(int topCapacity = 10)
	{
		lock (_lifetimeSync)
		{
			ObjectDisposedException.ThrowIf(_disposeRequested, this);
			if (_analysisDiagnostics is not null)
				throw new InvalidOperationException("An analysis diagnostics session is already active.");

			var diagnostics = new TreeSitterAnalysisDiagnosticsSession(
				ReleaseAnalysisDiagnostics,
				topCapacity);
			_analysisDiagnostics = diagnostics;
			return diagnostics;
		}
	}

	public ICodeCompressionScope CreateScope(string projectRoot) =>
		CreateScope(projectRoot, CodeTransformKinds.Bodies);

	public ICodeCompressionScope CreateScope(string projectRoot, CodeTransformKinds kinds)
	{
		ValidateTransformKinds(kinds);

		lock (_lifetimeSync)
		{
			ObjectDisposedException.ThrowIf(_disposeRequested, this);
			_activeScopes++;
			return new TreeSitterCompressionScope(
				_byExtension,
				CodeTransformIdentity.Create(TransformIdentity, kinds),
				kinds,
				Interlocked.Increment(ref _nextScopeId),
				GetLanguagePool,
				ReleaseScope,
				_analysisPhaseObserver,
				_analysisDiagnostics);
		}
	}

	private void ReleaseAnalysisDiagnostics(TreeSitterAnalysisDiagnosticsSession diagnostics)
	{
		lock (_lifetimeSync)
		{
			if (ReferenceEquals(_analysisDiagnostics, diagnostics))
				_analysisDiagnostics = null;
		}
	}

	private LanguageWorkerPool GetLanguagePool(CompressionLanguagePack pack)
	{
		lock (_lifetimeSync)
		{
			ObjectDisposedException.ThrowIf(_resourcesDisposed, this);
			if (_languagePools.TryGetValue(pack.Id, out var existing))
				return existing;

			var created = new LanguageWorkerPool(
				_locator,
				pack,
				_workerBudget,
				queryMatchLimit: _queryMatchLimit);
			_languagePools.Add(pack.Id, created);
			return created;
		}
	}

	public void Dispose()
	{
		lock (_lifetimeSync)
		{
			if (_disposeRequested)
				return;
			_disposeRequested = true;
			if (_activeScopes != 0)
				return;
			DisposeResourcesLocked();
		}
	}

	private void ReleaseScope()
	{
		lock (_lifetimeSync)
		{
			if (_activeScopes > 0)
				_activeScopes--;
			if (_disposeRequested && _activeScopes == 0)
				DisposeResourcesLocked();
		}
	}

	private void DisposeResourcesLocked()
	{
		if (_resourcesDisposed)
			return;
		_resourcesDisposed = true;
		foreach (var pool in _languagePools.Values)
			pool.Dispose();
		_languagePools.Clear();
		_workerBudget.Dispose();
	}

	private static void ValidateTransformKinds(CodeTransformKinds kinds)
	{
		if (kinds is CodeTransformKinds.None ||
		    (kinds & ~(
			    CodeTransformKinds.Bodies |
			    CodeTransformKinds.Comments |
			    CodeTransformKinds.BlankLines)) != 0)
		{
			throw new ArgumentOutOfRangeException(nameof(kinds), kinds, null);
		}
	}
}

/// <summary>
/// One output operation. It borrows process-lifetime parser workers lazily, only for languages that
/// actually appear; the scope itself owns no native parser and is safe for parallel file analysis.
/// </summary>
internal sealed class TreeSitterCompressionScope(
	IReadOnlyDictionary<string, IReadOnlyList<CompressionLanguagePack>> byExtension,
	string transformIdentity,
	CodeTransformKinds transformKinds,
	long operationId,
	Func<CompressionLanguagePack, LanguageWorkerPool> languagePoolProvider,
	Action releaseScope,
	ITreeSitterAnalysisPhaseObserver? analysisPhaseObserver = null,
	TreeSitterAnalysisDiagnosticsSession? analysisDiagnostics = null) : ICodeCompressionScope
{
	private const int CancellationCheckBatchMask = 1023;
	private const int CancellationCheckBatchSize = CancellationCheckBatchMask + 1;
	private int _disposed;

	public CodeCompressionAnalysis Analyze(
		string fullPath,
		string relativePath,
		string content,
		CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		cancellationToken.ThrowIfCancellationRequested();
		if (analysisDiagnostics is null)
		{
			var unmeasured = default(TreeSitterFileAnalysisTiming);
			return AnalyzeCore(relativePath, content, cancellationToken, ref unmeasured);
		}

		var timing = analysisDiagnostics.BeginFile(relativePath, content.Length);
		try
		{
			var analysis = AnalyzeCore(relativePath, content, cancellationToken, ref timing);
			analysisDiagnostics.RecordFile(ref timing);
			return analysis;
		}
		catch (OperationCanceledException)
		{
			timing.IsCancelled = true;
			analysisDiagnostics.RecordFile(ref timing);
			throw;
		}
	}

	private CodeCompressionAnalysis AnalyzeCore(
		string relativePath,
		string content,
		CancellationToken cancellationToken,
		ref TreeSitterFileAnalysisTiming timing)
	{

		if (!byExtension.TryGetValue(Path.GetExtension(relativePath), out var candidates))
			return Refused(relativePath, "unknown", CodeCompressionOutcome.UnchangedUnsupportedLanguage, content.Length);
		var firstCapable = candidates.FirstOrDefault(
			candidate => (candidate.TransformCapabilities & transformKinds) != CodeTransformKinds.None);
		if (firstCapable is null)
			return Refused(relativePath, "unknown", CodeCompressionOutcome.UnchangedUnsupportedLanguage, content.Length);

		if (content.Length > TreeSitterCodeCompressor.MaximumParsableCharacters)
		{
			return Refused(
				relativePath,
				firstCapable.Id,
				CodeCompressionOutcome.UnchangedTooLarge,
				content.Length);
		}

		var pack = ResolveLanguagePack(candidates, relativePath, content, transformKinds, cancellationToken);
		var effectiveKinds = transformKinds & pack.TransformCapabilities;
		CompletePhase(TreeSitterAnalysisPhase.LanguageResolution, cancellationToken);

		LanguageWorkerLease lease;
		try
		{
			lease = languagePoolProvider(pack).Rent(
				operationId,
				(effectiveKinds & CodeTransformKinds.Comments) != 0,
				cancellationToken);
		}
		catch (Exception exception) when (IsLanguageRuntimeFailure(exception))
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Refused(relativePath, pack.Id, CodeCompressionOutcome.UnchangedParseFailed, content.Length);
		}

		using var languageLease = lease;
		CompletePhase(TreeSitterAnalysisPhase.WorkerRent, cancellationToken);
		var language = languageLease.Worker;
		var source = content;
		var phaseStartedAt = StartMeasuredPhase();
		var original = language.Parser.Parse(source);
		if (original is null)
		{
			CompleteMeasuredPhase(
				TreeSitterAnalysisPhase.OriginalParse,
				phaseStartedAt,
				relativePath,
				source.Length,
				ref timing,
				cancellationToken);
			return Refused(relativePath, pack.Id, CodeCompressionOutcome.UnchangedParseFailed, content.Length);
		}

		CodeCompressionPlan plan;
		List<CodeDeclaration>? originalDeclarations;
		List<CodeParseDefect>? originalDefects;
		try
		{
			CompleteMeasuredPhase(
				TreeSitterAnalysisPhase.OriginalParse,
				phaseStartedAt,
				relativePath,
				source.Length,
				ref timing,
				cancellationToken);
			var edits = CollectEdits(
				pack,
				language,
				original,
				source,
				effectiveKinds,
				cancellationToken,
				analysisPhaseObserver,
				analysisDiagnostics,
				relativePath,
				ref timing,
				out var originalInspection);
			cancellationToken.ThrowIfCancellationRequested();
			if (edits is null)
				return Refused(relativePath, pack.Id, CodeCompressionOutcome.UnchangedGateRejected, source.Length);
			phaseStartedAt = StartMeasuredPhase();
			try
			{
				plan = CodeCompressionPlan.CreateForAnalysis(
					relativePath,
					pack.Id,
					edits,
					source.Length,
					transformIdentity,
					cancellationToken);
			}
			catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
			{
				CompleteMeasuredPhase(
					TreeSitterAnalysisPhase.PlanBuild,
					phaseStartedAt,
					relativePath,
					source.Length,
					ref timing,
					cancellationToken);
				// A malformed pack must refuse one file rather than take down the output operation.
				return Refused(relativePath, pack.Id, CodeCompressionOutcome.UnchangedGateRejected, source.Length);
			}
			CompleteMeasuredPhase(
				TreeSitterAnalysisPhase.PlanBuild,
				phaseStartedAt,
				relativePath,
				source.Length,
				ref timing,
				cancellationToken);

			if (plan.Outcome != CodeCompressionOutcome.Compressed)
				return new CodeCompressionAnalysis(plan, null);

			phaseStartedAt = StartMeasuredPhase();
			originalDeclarations = ReadDeclarations(
				language,
				original.RootNode,
				ContentTransformMap.Identity,
				cancellationToken,
				out var originalDeclarationCount);
			timing.OriginalDeclarations = originalDeclarationCount;
			CompleteMeasuredPhase(
				TreeSitterAnalysisPhase.OriginalDeclarations,
				phaseStartedAt,
				relativePath,
				source.Length,
				ref timing,
				cancellationToken);
			if (originalInspection is { } inspection)
			{
				originalDefects = inspection.Defects;
			}
			else
			{
				phaseStartedAt = StartMeasuredPhase();
				originalDefects = ReadDefects(
					original.RootNode,
					ContentTransformMap.Identity,
					cancellationToken,
					out var originalDefectCount,
					out var originalVisitedNodes);
				timing.OriginalDefects = originalDefectCount;
				timing.OriginalVisitedNodes = originalVisitedNodes;
				CompleteMeasuredPhase(
					TreeSitterAnalysisPhase.OriginalDefectWalk,
					phaseStartedAt,
					relativePath,
					source.Length,
					ref timing,
					cancellationToken);
			}
		}
		finally
		{
			try
			{
				language.ReleaseQueryTreeReferences();
			}
			finally
			{
				original.Dispose();
			}
		}

		if (originalDeclarations is null || originalDefects is null)
		{
			return new CodeCompressionAnalysis(
				plan.ToUnchanged(CodeCompressionOutcome.UnchangedGateRejected),
				null);
		}

		phaseStartedAt = StartMeasuredPhase();
		var applied = plan.ApplyForAnalysis(source, cancellationToken);
		CompleteMeasuredPhase(
			TreeSitterAnalysisPhase.PlanApply,
			phaseStartedAt,
			relativePath,
			source.Length,
			ref timing,
			cancellationToken);
		phaseStartedAt = StartMeasuredPhase();
		var compressed = language.Parser.Parse(applied.Text);
		if (compressed is null)
		{
			CompleteMeasuredPhase(
				TreeSitterAnalysisPhase.ReverseParse,
				phaseStartedAt,
				relativePath,
				source.Length,
				ref timing,
				cancellationToken);
			return new CodeCompressionAnalysis(
				plan.ToUnchanged(CodeCompressionOutcome.UnchangedGateRejected),
				null);
		}

		List<CodeDeclaration>? compressedDeclarations;
		List<CodeParseDefect>? compressedDefects;
		try
		{
			CompleteMeasuredPhase(
				TreeSitterAnalysisPhase.ReverseParse,
				phaseStartedAt,
				relativePath,
				source.Length,
				ref timing,
				cancellationToken);
			phaseStartedAt = StartMeasuredPhase();
			compressedDeclarations = ReadDeclarations(
				language,
				compressed.RootNode,
				applied.Map,
				cancellationToken,
				out var reverseDeclarationCount);
			timing.ReverseDeclarations = reverseDeclarationCount;
			CompleteMeasuredPhase(
				TreeSitterAnalysisPhase.ReverseDeclarations,
				phaseStartedAt,
				relativePath,
				source.Length,
				ref timing,
				cancellationToken);
			phaseStartedAt = StartMeasuredPhase();
			compressedDefects = ReadDefects(
				compressed.RootNode,
				applied.Map,
				cancellationToken,
				out var reverseDefectCount,
				out var reverseVisitedNodes);
			timing.ReverseDefects = reverseDefectCount;
			timing.ReverseVisitedNodes = reverseVisitedNodes;
			CompleteMeasuredPhase(
				TreeSitterAnalysisPhase.ReverseDefectWalk,
				phaseStartedAt,
				relativePath,
				source.Length,
				ref timing,
				cancellationToken);
		}
		finally
		{
			try
			{
				language.ReleaseQueryTreeReferences();
			}
			finally
			{
				compressed.Dispose();
			}
		}
		if (compressedDeclarations is null || compressedDefects is null)
		{
			return new CodeCompressionAnalysis(
				plan.ToUnchanged(CodeCompressionOutcome.UnchangedGateRejected),
				null);
		}

		phaseStartedAt = StartMeasuredPhase();
		var verdict = CodeStructureGate.Evaluate(
			originalDeclarations,
			compressedDeclarations,
			originalDefects,
			compressedDefects,
			plan.Edits,
			pack.ExecutableOwnerKinds,
			cancellationToken);
		CompleteMeasuredPhase(
			TreeSitterAnalysisPhase.StructureGate,
			phaseStartedAt,
			relativePath,
			source.Length,
			ref timing,
			cancellationToken);

		return verdict == CodeStructureGateVerdict.Accepted
			? new CodeCompressionAnalysis(plan, applied)
			: new CodeCompressionAnalysis(
				plan.ToUnchanged(CodeCompressionOutcome.UnchangedGateRejected),
				null);
	}

	private long StartMeasuredPhase() => analysisDiagnostics?.StartPhase() ?? 0;

	private void CompleteMeasuredPhase(
		TreeSitterAnalysisPhase phase,
		long startedAt,
		string relativePath,
		int sourceCharacters,
		ref TreeSitterFileAnalysisTiming timing,
		CancellationToken cancellationToken)
		=> CompleteMeasuredPhase(
			analysisDiagnostics,
			analysisPhaseObserver,
			phase,
			startedAt,
			relativePath,
			sourceCharacters,
			ref timing,
			cancellationToken);

	private static void CompleteMeasuredPhase(
		TreeSitterAnalysisDiagnosticsSession? diagnostics,
		ITreeSitterAnalysisPhaseObserver? observer,
		TreeSitterAnalysisPhase phase,
		long startedAt,
		string relativePath,
		int sourceCharacters,
		ref TreeSitterFileAnalysisTiming timing,
		CancellationToken cancellationToken)
	{
		diagnostics?.CompletePhase(
			phase,
			startedAt,
			relativePath,
			sourceCharacters,
			ref timing);
		CompletePhase(observer, phase, cancellationToken);
	}

	private void CompletePhase(TreeSitterAnalysisPhase phase, CancellationToken cancellationToken)
		=> CompletePhase(analysisPhaseObserver, phase, cancellationToken);

	private static void CompletePhase(
		ITreeSitterAnalysisPhaseObserver? observer,
		TreeSitterAnalysisPhase phase,
		CancellationToken cancellationToken)
	{
		observer?.OnPhaseCompleted(phase);
		cancellationToken.ThrowIfCancellationRequested();
	}

	private static void ThrowIfCancellationRequestedPeriodically(
		CancellationToken cancellationToken,
		int iteration)
	{
		if (cancellationToken.CanBeCanceled &&
		    iteration != 0 &&
		    (iteration & CancellationCheckBatchMask) == 0)
			cancellationToken.ThrowIfCancellationRequested();
	}

	private static void ThrowIfCancellationRequestedAtCheckpoint(
		CancellationToken cancellationToken,
		int iteration,
		ITreeSitterAnalysisPhaseObserver? observer,
		TreeSitterAnalysisCheckpoint checkpoint)
	{
		if (!cancellationToken.CanBeCanceled ||
		    iteration == 0 ||
		    (iteration & CancellationCheckBatchMask) != 0)
		{
			return;
		}

		(observer as ITreeSitterAnalysisCheckpointObserver)?.OnCheckpoint(checkpoint, iteration);
		cancellationToken.ThrowIfCancellationRequested();
	}

	private CodeCompressionAnalysis Refused(
		string relativePath,
		string languageId,
		CodeCompressionOutcome outcome,
		int length) =>
		new(CodeCompressionPlan.Unchanged(relativePath, languageId, outcome, length, transformIdentity), null);

	private static CompressionLanguagePack ResolveLanguagePack(
		IReadOnlyList<CompressionLanguagePack> candidates,
		string relativePath,
		string source,
		CodeTransformKinds transformKinds,
		CancellationToken cancellationToken)
	{
		if (candidates.Count == 1)
			return candidates[0];

		if (Path.GetExtension(relativePath).Equals(".h", StringComparison.OrdinalIgnoreCase))
		{
			var languageId = ContainsCppHeaderEvidence(source, cancellationToken) ? "cpp" : "c";
			var selected = candidates.FirstOrDefault(candidate =>
				(candidate.TransformCapabilities & transformKinds) != CodeTransformKinds.None &&
				candidate.Id.Equals(languageId, StringComparison.Ordinal));
			if (selected is not null)
				return selected;
		}

		return candidates.First(candidate =>
			(candidate.TransformCapabilities & transformKinds) != CodeTransformKinds.None);
	}

	private static bool ContainsCppHeaderEvidence(string source, CancellationToken cancellationToken)
	{
		var buffer = ArrayPool<char>.Shared.Rent(Math.Max(1, source.Length));
		try
		{
			var code = buffer.AsSpan(0, source.Length);
			StripCommentsStringsAndPreprocessor(source.AsSpan(), code, cancellationToken);
			return ContainsWord(code, "namespace", cancellationToken) ||
			       ContainsWord(code, "template", cancellationToken) ||
			       ContainsWord(code, "class", cancellationToken) ||
			       ContainsWord(code, "constexpr", cancellationToken) ||
			       ContainsWord(code, "typename", cancellationToken) ||
			       code.Contains("::", StringComparison.Ordinal) ||
			       ContainsAccessLabel(code, "public", cancellationToken) ||
			       ContainsAccessLabel(code, "private", cancellationToken) ||
			       ContainsAccessLabel(code, "protected", cancellationToken) ||
			       ContainsInlineStructMethod(code, cancellationToken);
		}
		finally
		{
			ArrayPool<char>.Shared.Return(buffer);
		}
	}

	private static void StripCommentsStringsAndPreprocessor(
		ReadOnlySpan<char> source,
		Span<char> destination,
		CancellationToken cancellationToken)
	{
		destination.Fill(' ');
		var state = HeaderLexicalState.Code;
		var lineHasCode = false;
		var escaped = false;
		var lastNonWhitespace = '\0';
		for (var index = 0; index < source.Length; index++)
		{
			ThrowIfCancellationRequestedPeriodically(cancellationToken, index);
			var current = source[index];
			var next = index + 1 < source.Length ? source[index + 1] : '\0';
			if (current == '\n')
			{
				destination[index] = current;
				var continuesLogicalLine =
					state is HeaderLexicalState.LineComment or HeaderLexicalState.Preprocessor &&
					lastNonWhitespace == '\\';
				if (!continuesLogicalLine &&
				    state is HeaderLexicalState.LineComment or HeaderLexicalState.Preprocessor)
				{
					state = HeaderLexicalState.Code;
				}
				lineHasCode = false;
				escaped = false;
				lastNonWhitespace = '\0';
				continue;
			}

			switch (state)
			{
				case HeaderLexicalState.LineComment:
				case HeaderLexicalState.Preprocessor:
					if (!char.IsWhiteSpace(current))
						lastNonWhitespace = current;
					continue;
				case HeaderLexicalState.BlockComment:
					if (current == '*' && next == '/')
					{
						state = HeaderLexicalState.Code;
						index++;
					}
					continue;
				case HeaderLexicalState.String:
					if (!escaped && current == '"')
						state = HeaderLexicalState.Code;
					escaped = !escaped && current == '\\';
					continue;
				case HeaderLexicalState.Character:
					if (!escaped && current == '\'')
						state = HeaderLexicalState.Code;
					escaped = !escaped && current == '\\';
					continue;
			}

			if (!lineHasCode && current == '#')
			{
				state = HeaderLexicalState.Preprocessor;
				continue;
			}
			if (current == '/' && next == '/')
			{
				state = HeaderLexicalState.LineComment;
				index++;
				continue;
			}
			if (current == '/' && next == '*')
			{
				state = HeaderLexicalState.BlockComment;
				index++;
				continue;
			}
			if (current == '"')
			{
				state = HeaderLexicalState.String;
				escaped = false;
				continue;
			}
			if (current == '\'')
			{
				state = HeaderLexicalState.Character;
				escaped = false;
				continue;
			}

			destination[index] = current;
			if (!char.IsWhiteSpace(current))
				lineHasCode = true;
		}
	}

	private static bool ContainsWord(
		ReadOnlySpan<char> code,
		ReadOnlySpan<char> word,
		CancellationToken cancellationToken)
	{
		var offset = 0;
		while (offset <= code.Length - word.Length)
		{
			ThrowIfCancellationRequestedPeriodically(cancellationToken, offset);
			var relative = code[offset..].IndexOf(word, StringComparison.Ordinal);
			if (relative < 0)
				return false;
			var start = offset + relative;
			var end = start + word.Length;
			if ((start == 0 || !IsIdentifierCharacter(code[start - 1])) &&
			    (end == code.Length || !IsIdentifierCharacter(code[end])))
			{
				return true;
			}
			offset = start + 1;
		}
		return false;
	}

	private static bool ContainsAccessLabel(
		ReadOnlySpan<char> code,
		ReadOnlySpan<char> label,
		CancellationToken cancellationToken)
	{
		var offset = 0;
		while (offset <= code.Length - label.Length)
		{
			ThrowIfCancellationRequestedPeriodically(cancellationToken, offset);
			var relative = code[offset..].IndexOf(label, StringComparison.Ordinal);
			if (relative < 0)
				return false;
			var start = offset + relative;
			var end = start + label.Length;
			if ((start == 0 || !IsIdentifierCharacter(code[start - 1])) &&
			    (end == code.Length || !IsIdentifierCharacter(code[end])))
			{
				while (end < code.Length && char.IsWhiteSpace(code[end]))
				{
					ThrowIfCancellationRequestedPeriodically(cancellationToken, end - start);
					end++;
				}
				if (end < code.Length && code[end] == ':')
					return true;
			}
			offset = start + 1;
		}
		return false;
	}

	private static bool ContainsInlineStructMethod(
		ReadOnlySpan<char> code,
		CancellationToken cancellationToken)
	{
		var searchOffset = 0;
		while (searchOffset < code.Length)
		{
			ThrowIfCancellationRequestedPeriodically(cancellationToken, searchOffset);
			var relative = code[searchOffset..].IndexOf("struct", StringComparison.Ordinal);
			if (relative < 0)
				return false;
			var start = searchOffset + relative;
			var wordEnd = start + "struct".Length;
			searchOffset = start + 1;
			if ((start > 0 && IsIdentifierCharacter(code[start - 1])) ||
			    (wordEnd < code.Length && IsIdentifierCharacter(code[wordEnd])))
			{
				continue;
			}

			var open = code[wordEnd..].IndexOf('{');
			var terminator = code[wordEnd..].IndexOf(';');
			if (open < 0 || (terminator >= 0 && terminator < open))
				continue;
			open += wordEnd;
			var depth = 1;
			for (var index = open + 1; index < code.Length && depth > 0; index++)
			{
				ThrowIfCancellationRequestedPeriodically(cancellationToken, index);
				if (code[index] == '{')
				{
					depth++;
					continue;
				}
				if (code[index] == '}')
				{
					depth--;
					continue;
				}
				if (depth != 1 || code[index] != ')')
					continue;
				if (HasInlineDefinitionAfter(code, index + 1, cancellationToken))
					return true;
			}
		}
		return false;
	}

	private static bool HasInlineDefinitionAfter(
		ReadOnlySpan<char> code,
		int offset,
		CancellationToken cancellationToken)
	{
		for (var index = offset; index < code.Length; index++)
		{
			ThrowIfCancellationRequestedPeriodically(cancellationToken, index);
			if (code[index] == '{')
				return true;
			if (code[index] is ';' or '}')
				return false;
		}

		return false;
	}

	private static bool IsIdentifierCharacter(char value) =>
		char.IsAsciiLetterOrDigit(value) || value == '_';

	private enum HeaderLexicalState
	{
		Code,
		LineComment,
		BlockComment,
		String,
		Character,
		Preprocessor
	}

	private static List<CodeCompressionEdit>? CollectEdits(
		CompressionLanguagePack pack,
		LoadedLanguage language,
		Tree tree,
		string source,
		CodeTransformKinds transformKinds,
		CancellationToken cancellationToken,
		ITreeSitterAnalysisPhaseObserver? observer,
		TreeSitterAnalysisDiagnosticsSession? diagnostics,
		string relativePath,
		ref TreeSitterFileAnalysisTiming timing,
		out OriginalTreeInspection? originalInspection)
	{
		originalInspection = null;
		var phaseStartedAt = diagnostics?.StartPhase() ?? 0;
		var preserveCaptureCount = 0;
		var preservedRanges = (transformKinds & (CodeTransformKinds.Bodies | CodeTransformKinds.Comments)) != 0
			? ReadPreservedRanges(
				language,
				tree.RootNode,
				cancellationToken,
				out preserveCaptureCount)
			: [];
		timing.PreserveCaptures = preserveCaptureCount;
		CompleteMeasuredPhase(
			diagnostics,
			observer,
			TreeSitterAnalysisPhase.PreserveQuery,
			phaseStartedAt,
			relativePath,
			source.Length,
			ref timing,
			cancellationToken);
		if (preservedRanges is null)
			return null;

		var bodyCaptures = new List<Node>();
		var expressionCaptures = new List<Node>();
		var bodyQueryValid = true;
		var bodyCaptureCount = 0;
		phaseStartedAt = diagnostics?.StartPhase() ?? 0;
		if ((transformKinds & CodeTransformKinds.Bodies) != 0)
		{
			if (language.Runtime.Bodies is not { } bodies || language.BodiesCursor is not { } bodiesCursor)
			{
				bodyQueryValid = false;
			}
			else
			{
				var captureIndex = 0;
				bodiesCursor.Execute(bodies, tree.RootNode);
				foreach (var capture in bodiesCursor.Captures)
				{
					ThrowIfCancellationRequestedPeriodically(cancellationToken, captureIndex++);
					if (capture.Name is not ("body" or "expression"))
						continue;
					if (bodyCaptureCount >= TreeSitterCodeCompressor.MaximumBodyCapturesPerFile)
					{
						bodyQueryValid = false;
						break;
					}
					bodyCaptureCount++;
					(capture.Name.Equals("body", StringComparison.Ordinal)
						? bodyCaptures
						: expressionCaptures).Add(capture.Node);
				}
				if (bodiesCursor.IsMatchLimitExceeded)
					bodyQueryValid = false;
			}
		}
		timing.BodyCaptures = bodyCaptureCount;
		CompleteMeasuredPhase(
			diagnostics,
			observer,
			TreeSitterAnalysisPhase.BodyQuery,
			phaseStartedAt,
			relativePath,
			source.Length,
			ref timing,
			cancellationToken);
		if (!bodyQueryValid)
			return null;

		var raw = new List<RawCompressionEdit>(bodyCaptures.Count + expressionCaptures.Count);
		var needsBodyRanges = expressionCaptures.Count > 0 ||
		                      (transformKinds & CodeTransformKinds.Comments) != 0;
		HashSet<SourceRange>? bodyRanges = null;
		var deferCommentShaping = diagnostics is not null || observer is not null;
		if (!deferCommentShaping &&
		    !TryAppendBodyEdits(
			    pack,
			    source,
			    transformKinds,
			    bodyCaptures,
			    expressionCaptures,
			    needsBodyRanges,
			    raw,
			    cancellationToken,
			    out bodyRanges))
		{
			return null;
		}

		var deferredComments = default(DeferredCommentBuffer);
		var deferredCommentCount = 0;
		List<CommentLineCollapseCandidate>? collapseCandidates = null;
		var commentQueryValid = true;
		phaseStartedAt = diagnostics?.StartPhase() ?? 0;
		try
		{
			if ((transformKinds & CodeTransformKinds.Comments) != 0)
			{
				var commentsCursor = language.ExecuteComments(tree.RootNode);
				if (commentsCursor is null)
				{
					commentQueryValid = false;
				}
				else
				{
					var commentCaptureIndex = 0;
					foreach (var capture in commentsCursor.Captures)
					{
						ThrowIfCancellationRequestedPeriodically(cancellationToken, commentCaptureIndex++);
						if (capture.Name is not ("comment" or "docstring"))
							continue;
						if (deferredCommentCount >= TreeSitterCodeCompressor.MaximumCommentCapturesPerFile)
						{
							commentQueryValid = false;
							break;
						}
						var isDocstring = capture.Name.Equals("docstring", StringComparison.Ordinal);
						if (deferCommentShaping)
						{
							deferredComments.Add(new CommentCapture(capture.Node, isDocstring));
						}
						else
						{
							AppendCommentEdit(
								pack,
								capture.Node,
								isDocstring,
								bodyRanges,
								source,
								raw,
								ref collapseCandidates,
								cancellationToken,
								observer);
						}
						deferredCommentCount++;
					}
					if (commentsCursor.IsMatchLimitExceeded)
						commentQueryValid = false;
				}
			}
			timing.CommentCaptures = deferredCommentCount;
			CompleteMeasuredPhase(
				diagnostics,
				observer,
				TreeSitterAnalysisPhase.CommentQuery,
				phaseStartedAt,
				relativePath,
				source.Length,
				ref timing,
				cancellationToken);
			if (!commentQueryValid)
				return null;

			IReadOnlyList<SourceRange> protectedMultilineLeaves = Array.Empty<SourceRange>();
			if ((transformKinds & CodeTransformKinds.BlankLines) != 0)
			{
				phaseStartedAt = diagnostics?.StartPhase() ?? 0;
				var defects = ReadDefects(
					tree.RootNode,
					ContentTransformMap.Identity,
					cancellationToken,
					out var originalDefectCount,
					out var originalVisitedNodes,
					out protectedMultilineLeaves,
					collectProtectedMultilineLeaves: true);
				timing.OriginalDefects = originalDefectCount;
				timing.OriginalVisitedNodes = originalVisitedNodes;
				originalInspection = new OriginalTreeInspection(defects);
				CompleteMeasuredPhase(
					diagnostics,
					observer,
					TreeSitterAnalysisPhase.OriginalDefectWalk,
					phaseStartedAt,
					relativePath,
					source.Length,
					ref timing,
					cancellationToken);
				if (defects is null)
					protectedMultilineLeaves = Array.Empty<SourceRange>();
				else
					protectedMultilineLeaves = MergePreservedRanges(
						protectedMultilineLeaves,
						cancellationToken);
			}

			phaseStartedAt = diagnostics?.StartPhase() ?? 0;
			preservedRanges = MergePreservedRanges(preservedRanges, cancellationToken);
			if (deferCommentShaping)
			{
				if (!TryAppendBodyEdits(
					    pack,
					    source,
					    transformKinds,
					    bodyCaptures,
					    expressionCaptures,
					    needsBodyRanges,
					    raw,
					    cancellationToken,
					    out bodyRanges))
				{
					CompleteEditShapingPhase(
						diagnostics,
						observer,
						phaseStartedAt,
						relativePath,
						source.Length,
						raw.Count,
						finalEditCount: 0,
						ref timing,
						cancellationToken);
					return null;
				}
				for (var index = 0; index < deferredCommentCount; index++)
				{
					ThrowIfCancellationRequestedPeriodically(cancellationToken, index);
					var capture = deferredComments[index];
					AppendCommentEdit(
						pack,
						capture.Node,
						capture.IsDocstring,
						bodyRanges,
						source,
						raw,
						ref collapseCandidates,
						cancellationToken,
						observer);
				}
			}

			if ((transformKinds & CodeTransformKinds.Comments) != 0)
			{
				raw = CollapseBlankLinesAroundRemovedComments(
					source,
					raw,
					preservedRanges,
					collapseCandidates,
					cancellationToken,
					observer);
			}
			if ((transformKinds & CodeTransformKinds.BlankLines) != 0 &&
			    originalInspection is { Defects: not null })
			{
				AppendBlankLineEdits(
					source,
					protectedMultilineLeaves,
					raw,
					cancellationToken);
			}

			// Outermost wins: a lambda body inside a method body must not be spliced twice.
			cancellationToken.ThrowIfCancellationRequested();
			raw.Sort(static (left, right) =>
				left.Start != right.Start
					? left.Start.CompareTo(right.Start)
					: right.End.CompareTo(left.End));
			cancellationToken.ThrowIfCancellationRequested();
			var edits = new List<CodeCompressionEdit>(raw.Count);
			var reach = -1;
			var preservedIndex = 0;
			var rawEditIndex = 0;
			foreach (var rawEdit in raw)
			{
				ThrowIfCancellationRequestedPeriodically(cancellationToken, rawEditIndex++);
				var start = rawEdit.Start;
				var end = rawEdit.End;
				var replacement = rawEdit.Replacement;
				var kinds = rawEdit.Kinds;
				if (start < reach)
				{
					if (edits.Count > 0 && end <= edits[^1].SourceEnd)
						edits[^1] = edits[^1] with { Kinds = edits[^1].Kinds | kinds };
					continue;
				}
				while (preservedIndex < preservedRanges.Count && preservedRanges[preservedIndex].End <= start)
					preservedIndex++;
				if ((kinds & ~CodeTransformKinds.BlankLines) != CodeTransformKinds.None &&
				    preservedIndex < preservedRanges.Count &&
				    preservedRanges[preservedIndex].Start <= start &&
				    end <= preservedRanges[preservedIndex].End)
				{
					continue;
				}
				reach = end;
				if (edits.Count >= TreeSitterCodeCompressor.MaximumEditsPerFile)
				{
					CompleteEditShapingPhase(
						diagnostics,
						observer,
						phaseStartedAt,
						relativePath,
						source.Length,
						raw.Count,
						edits.Count,
						ref timing,
						cancellationToken);
					return null;
				}
				if (edits.Count > 0 &&
				    edits[^1].SourceEnd == start &&
				    edits[^1].Replacement.Length == 0 &&
				    replacement.Length == 0)
				{
					var previous = edits[^1];
					edits[^1] = new CodeCompressionEdit(
						previous.SourceStart,
						end - previous.SourceStart,
						string.Empty)
					{
						Kinds = previous.Kinds | kinds
					};
					continue;
				}
				edits.Add(new CodeCompressionEdit(start, end - start, replacement) { Kinds = kinds });
			}

			CompleteEditShapingPhase(
				diagnostics,
				observer,
				phaseStartedAt,
				relativePath,
				source.Length,
				raw.Count,
				edits.Count,
				ref timing,
				cancellationToken);
			return edits;
		}
		finally
		{
			deferredComments.Dispose();
		}
	}

	private static void AppendBlankLineEdits(
		string source,
		IReadOnlyList<SourceRange> protectedRanges,
		List<RawCompressionEdit> edits,
		CancellationToken cancellationToken)
	{
		var lineStart = 0;
		var protectedIndex = 0;
		while (lineStart < source.Length)
		{
			var cursor = lineStart;
			var isBlank = true;
			while (cursor < source.Length && source[cursor] is not ('\r' or '\n'))
			{
				ThrowIfCancellationRequestedPeriodically(cancellationToken, cursor - lineStart);
				if (!char.IsWhiteSpace(source[cursor]))
					isBlank = false;
				cursor++;
			}

			var lineEnd = cursor;
			if (lineEnd < source.Length && source[lineEnd] == '\r')
			{
				lineEnd++;
				if (lineEnd < source.Length && source[lineEnd] == '\n')
					lineEnd++;
			}
			else if (lineEnd < source.Length)
			{
				lineEnd++;
			}

			while (protectedIndex < protectedRanges.Count &&
			       protectedRanges[protectedIndex].End <= lineStart)
			{
				protectedIndex++;
			}
			var overlapsProtectedRange =
				protectedIndex < protectedRanges.Count &&
				protectedRanges[protectedIndex].Start < lineEnd &&
				lineStart < protectedRanges[protectedIndex].End;
			if (isBlank && lineEnd > lineStart && !overlapsProtectedRange)
				AddBlankLineRemoval(edits, lineStart, lineEnd);

			if (lineEnd == cursor)
				break;
			lineStart = lineEnd;
		}
		cancellationToken.ThrowIfCancellationRequested();
	}

	private static void AddBlankLineRemoval(
		List<RawCompressionEdit> edits,
		int start,
		int end)
	{
		if (edits.Count > 0 &&
		    edits[^1] is { Kinds: CodeTransformKinds.BlankLines } previous &&
		    previous.End == start)
		{
			edits[^1] = previous with { End = end };
			return;
		}

		edits.Add(new RawCompressionEdit(
			start,
			end,
			string.Empty,
			CodeTransformKinds.BlankLines));
	}

	private static void CompleteEditShapingPhase(
		TreeSitterAnalysisDiagnosticsSession? diagnostics,
		ITreeSitterAnalysisPhaseObserver? observer,
		long phaseStartedAt,
		string relativePath,
		int sourceCharacters,
		int rawEditCount,
		int finalEditCount,
		ref TreeSitterFileAnalysisTiming timing,
		CancellationToken cancellationToken)
	{
		timing.RawEdits = rawEditCount;
		timing.FinalEdits = finalEditCount;
		CompleteMeasuredPhase(
			diagnostics,
			observer,
			TreeSitterAnalysisPhase.EditShaping,
			phaseStartedAt,
			relativePath,
			sourceCharacters,
			ref timing,
			cancellationToken);
	}

	private static bool TryAppendBodyEdits(
		CompressionLanguagePack pack,
		string source,
		CodeTransformKinds transformKinds,
		IReadOnlyList<Node> bodyCaptures,
		IReadOnlyList<Node> expressionCaptures,
		bool needsBodyRanges,
		List<RawCompressionEdit> raw,
		CancellationToken cancellationToken,
		out HashSet<SourceRange>? bodyRanges)
	{
		bodyRanges = null;
		if (bodyCaptures.Count > 0 && needsBodyRanges)
		{
			bodyRanges = new HashSet<SourceRange>(bodyCaptures.Count);
			for (var index = 0; index < bodyCaptures.Count; index++)
			{
				ThrowIfCancellationRequestedPeriodically(cancellationToken, index);
				var node = bodyCaptures[index];
				bodyRanges.Add(new SourceRange(node.StartIndex, node.EndIndex));
			}
			cancellationToken.ThrowIfCancellationRequested();
		}

		for (var index = 0; index < bodyCaptures.Count; index++)
		{
			ThrowIfCancellationRequestedPeriodically(cancellationToken, index);
			var node = bodyCaptures[index];
			// Defence in depth. The queries are anchored on the parent declaration's body: field, so
			// a container should be unreachable - but a grammar upgrade must fail loudly, not
			// silently delete a class body.
			if (pack.ContainerNodeTypes.Contains(node.Type))
				continue;
			if (IsMisparsedCppClassBody(pack, node, source, cancellationToken))
				continue;

			var start = node.StartIndex;
			if (pack.PreserveLeadingDocstring &&
			    (transformKinds & CodeTransformKinds.Comments) == 0)
			{
				if (!TryResolveContentAfterLeadingDocstring(
						node,
						source,
						out start,
						out var hasLeadingDocstring,
						cancellationToken))
				{
					continue;
				}
				if (!hasLeadingDocstring)
					start = ExtendOverLeadingPythonComments(source, node, cancellationToken);
			}

			if (start < 0 || node.EndIndex > source.Length || node.EndIndex <= start)
				continue;
			raw.Add(ResolveBlockEdit(pack, source, start, node.EndIndex, cancellationToken));
		}

		for (var index = 0; index < expressionCaptures.Count; index++)
		{
			ThrowIfCancellationRequestedPeriodically(cancellationToken, index);
			var node = expressionCaptures[index];
			if (bodyRanges?.Contains(new SourceRange(node.StartIndex, node.EndIndex)) == true)
				continue;
			if (node.StartPosition.Row == node.EndPosition.Row)
				continue;
			if (!TryResolveExpressionEdit(pack, node, source.Length, out var expressionEdit))
				return false;
			raw.Add(expressionEdit);
		}

		return true;
	}

	private static void AppendCommentEdit(
		CompressionLanguagePack pack,
		Node node,
		bool isDocstring,
		HashSet<SourceRange>? bodyRanges,
		string source,
		List<RawCompressionEdit> raw,
		ref List<CommentLineCollapseCandidate>? collapseCandidates,
		CancellationToken cancellationToken,
		ITreeSitterAnalysisPhaseObserver? observer)
	{
		if (!TryResolveCommentEdit(
				pack,
				node,
				isDocstring,
				bodyRanges,
				source,
			out var commentEdit,
			out var collapseBounds,
			cancellationToken,
			observer))
		{
			return;
		}

		var editIndex = raw.Count;
		raw.Add(commentEdit);
		if (collapseBounds is { } bounds)
			(collapseCandidates ??= []).Add(new CommentLineCollapseCandidate(editIndex, bounds));
	}

	private static bool TryResolveExpressionEdit(
		CompressionLanguagePack pack,
		Node expression,
		int sourceLength,
		out RawCompressionEdit edit)
	{
		var start = expression.StartIndex;
		var end = expression.EndIndex;
		switch (pack.ExpressionBodyStyle)
		{
			case ExpressionBodyStyle.Inline:
				break;
			case ExpressionBodyStyle.Declaration:
				var expressionContainer = expression.Parent;
				var owner = expressionContainer?.Parent;
				if (expressionContainer is null ||
				    owner is null ||
				    !pack.ExecutableOwnerKinds.Contains(owner.Type))
				{
					edit = default;
					return false;
				}

				start = expressionContainer.StartIndex;
				end = owner.EndIndex;
				break;
			default:
				edit = default;
				return false;
		}

		if (start < 0 || end > sourceLength || end <= start)
		{
			edit = default;
			return false;
		}

		edit = new RawCompressionEdit(start, end, pack.BlockPlaceholder, CodeTransformKinds.Bodies);
		return true;
	}

	private static bool TryResolveCommentEdit(
		CompressionLanguagePack pack,
		Node node,
		bool isDocstring,
		IReadOnlySet<SourceRange>? capturedBodyRanges,
		string source,
		out RawCompressionEdit edit,
		out SourceRange? collapseBounds,
		CancellationToken cancellationToken,
		ITreeSitterAnalysisPhaseObserver? observer)
	{
		collapseBounds = null;
		if (isDocstring &&
		    (!IsLeadingDocstring(node, cancellationToken) || !IsSimpleStringStatement(node, source)))
		{
			edit = default;
			return false;
		}

		var start = node.StartIndex;
		var end = node.EndIndex;
		if (start < 0 || end > source.Length || end <= start)
		{
			edit = default;
			return false;
		}

		if (!isDocstring && start == 0 && source.AsSpan(start, end - start).StartsWith("#!"))
		{
			edit = default;
			return false;
		}

		if (isDocstring && RequiresSuitePlaceholder(node, cancellationToken))
		{
			edit = new RawCompressionEdit(
				start,
				end,
				pack.BlockPlaceholder,
				CodeTransformKinds.Comments);
			return true;
		}

		// Some scanners include the CR in a line-comment node while others stop before it.
		// Normalize the editable syntax span so a trailing comment can never consume half of CRLF.
		var syntaxEnd = end > start &&
		                end < source.Length &&
		                source[end - 1] == '\r' &&
		                source[end] == '\n'
			? end - 1
			: end;
		var lineStart = source.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
		var editableLineStart = lineStart == 0 && source.Length > 0 && source[0] == '\uFEFF'
			? 1
			: lineStart;
		var nextLineFeed = source.IndexOf('\n', syntaxEnd);
		var lineEnd = nextLineFeed >= 0 ? nextLineFeed : source.Length;
		var contentEnd = lineEnd > lineStart && source[lineEnd - 1] == '\r'
			? lineEnd - 1
			: lineEnd;
		var hasOnlyWhitespaceBefore = ContainsOnlyWhitespace(
			source,
			editableLineStart,
			start,
			cancellationToken);
		var hasOnlyWhitespaceAfter = syntaxEnd <= contentEnd &&
		                             ContainsOnlyWhitespace(
			                             source,
			                             syntaxEnd,
			                             contentEnd,
			                             cancellationToken);
		if (hasOnlyWhitespaceBefore && hasOnlyWhitespaceAfter)
		{
			// A body node can start at the first syntax token while its indentation belongs to the
			// surrounding declaration. Keep that indentation when the body edit will absorb this
			// comment; otherwise the two edits partially overlap and the body placeholder is lost.
			var containingBody = FindContainingBodyRange(
				node,
				capturedBodyRanges,
				cancellationToken,
				observer);
			var expandedStart = containingBody is { } body
				? Math.Max(editableLineStart, body.Start)
				: editableLineStart;
			var expandedEnd = nextLineFeed >= 0 ? nextLineFeed + 1 : source.Length;
			var documentStart = source.Length > 0 && source[0] == '\uFEFF' ? 1 : 0;
			collapseBounds = expandedStart == editableLineStart &&
			                 (containingBody is null || expandedEnd <= containingBody.Value.End)
				? containingBody ?? new SourceRange(documentStart, source.Length)
				: null;
			edit = new RawCompressionEdit(
				expandedStart,
				expandedEnd,
				string.Empty,
				CodeTransformKinds.Comments);
			return true;
		}

		// A trailing comment owns the separator before its marker, but never the line ending.
		// Inline block comments retain their exact syntax-node span so surrounding code is untouched.
		if (hasOnlyWhitespaceAfter)
		{
			var scannedWhitespace = 0;
			while (start > lineStart && source[start - 1] is ' ' or '\t')
			{
				start--;
				ThrowIfCancellationRequestedAtCheckpoint(
					cancellationToken,
					++scannedWhitespace,
					observer,
					TreeSitterAnalysisCheckpoint.TrailingWhitespaceScan);
			}
		}

		edit = new RawCompressionEdit(
			start,
			syntaxEnd,
			string.Empty,
			CodeTransformKinds.Comments);
		return true;
	}

	private static SourceRange? FindContainingBodyRange(
		Node node,
		IReadOnlySet<SourceRange>? capturedBodyRanges,
		CancellationToken cancellationToken,
		ITreeSitterAnalysisPhaseObserver? observer)
	{
		if (capturedBodyRanges is null)
			return null;

		var depth = 0;
		for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
		{
			ThrowIfCancellationRequestedAtCheckpoint(
				cancellationToken,
				++depth,
				observer,
				TreeSitterAnalysisCheckpoint.ContainingBodyAncestorWalk);
			var range = new SourceRange(ancestor.StartIndex, ancestor.EndIndex);
			if (capturedBodyRanges.Contains(range))
				return range;
		}

		return null;
	}

	private static List<RawCompressionEdit> CollapseBlankLinesAroundRemovedComments(
		string source,
		List<RawCompressionEdit> edits,
		IReadOnlyList<SourceRange> preservedRanges,
		List<CommentLineCollapseCandidate>? candidates,
		CancellationToken cancellationToken,
		ITreeSitterAnalysisPhaseObserver? observer)
	{
		if (candidates is null)
			return edits;

		var eligibleCount = 0;
		for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
		{
			ThrowIfCancellationRequestedPeriodically(cancellationToken, candidateIndex);
			var candidate = candidates[candidateIndex];
			var edit = edits[candidate.EditIndex];
			if (IsContainedInPreservedRange(edit.Start, edit.End, preservedRanges))
				continue;

			candidates[eligibleCount++] = candidate;
		}

		if (eligibleCount == 0)
			return edits;
		if (eligibleCount < candidates.Count)
			candidates.RemoveRange(eligibleCount, candidates.Count - eligibleCount);

		candidates.Sort((left, right) =>
		{
			var leftEdit = edits[left.EditIndex];
			var rightEdit = edits[right.EditIndex];
			return leftEdit.Start != rightEdit.Start
				? leftEdit.Start.CompareTo(rightEdit.Start)
				: leftEdit.End.CompareTo(rightEdit.End);
		});
		bool[]? replaced = null;
		var collapsed = new List<RawCompressionEdit>();
		for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
		{
			ThrowIfCancellationRequestedPeriodically(cancellationToken, candidateIndex);
			var clusterFirstCandidateIndex = candidateIndex;
			var first = candidates[candidateIndex];
			var firstEdit = edits[first.EditIndex];
			var bounds = first.Bounds;
			var clusterStart = firstEdit.Start;
			var clusterEnd = firstEdit.End;

			while (candidateIndex + 1 < candidates.Count)
			{
				ThrowIfCancellationRequestedAtCheckpoint(
					cancellationToken,
					candidateIndex - clusterFirstCandidateIndex + 1,
					observer,
					TreeSitterAnalysisCheckpoint.CommentClusterScan);
				var next = candidates[candidateIndex + 1];
				var nextEdit = edits[next.EditIndex];
				if (next.Bounds != bounds ||
				    nextEdit.Start < clusterEnd ||
				    !ContainsOnlyBlankLineCharacters(
					    source,
					    clusterEnd,
					    nextEdit.Start,
					    cancellationToken))
				{
					break;
				}

				candidateIndex++;
				clusterEnd = nextEdit.End;
			}

			var collapsedCount = collapsed.Count;
			AddCollapsedCommentClusterEdits(
				source,
				clusterStart,
				clusterEnd,
				bounds,
				collapsed,
				cancellationToken);
			if (clusterFirstCandidateIndex == candidateIndex &&
			    collapsed.Count == collapsedCount + 1 &&
			    collapsed[^1].Start == firstEdit.Start &&
			    collapsed[^1].End == firstEdit.End)
			{
				collapsed.RemoveAt(collapsed.Count - 1);
				continue;
			}

			replaced ??= new bool[edits.Count];
			for (var clusterIndex = clusterFirstCandidateIndex;
			     clusterIndex <= candidateIndex;
			     clusterIndex++)
			{
				ThrowIfCancellationRequestedPeriodically(
					cancellationToken,
					clusterIndex - clusterFirstCandidateIndex);
				replaced[candidates[clusterIndex].EditIndex] = true;
			}
		}

		if (replaced is null)
			return edits;

		var writeIndex = 0;
		for (var readIndex = 0; readIndex < edits.Count; readIndex++)
		{
			ThrowIfCancellationRequestedPeriodically(cancellationToken, readIndex);
			if (!replaced[readIndex])
				edits[writeIndex++] = edits[readIndex];
		}
		if (writeIndex < edits.Count)
			edits.RemoveRange(writeIndex, edits.Count - writeIndex);
		edits.AddRange(collapsed);
		return edits;
	}

	private static void AddCollapsedCommentClusterEdits(
		string source,
		int clusterStart,
		int clusterEnd,
		SourceRange bounds,
		List<RawCompressionEdit> destination,
		CancellationToken cancellationToken)
	{
		var prefixStart = clusterStart;
		SourceRange? retainedPrefixBlank = null;
		var scannedLines = 0;
		while (TryReadBlankLineBefore(
			       source,
			       prefixStart,
			       bounds.Start,
			       out var blank,
			       cancellationToken))
		{
			ThrowIfCancellationRequestedPeriodically(cancellationToken, scannedLines++);
			if (retainedPrefixBlank is null && IsEmptyBlankLine(source, blank))
				retainedPrefixBlank = blank;
			prefixStart = blank.Start;
		}

		var suffixEnd = clusterEnd;
		SourceRange? retainedSuffixBlank = null;
		scannedLines = 0;
		while (TryReadBlankLineAfter(
			       source,
			       suffixEnd,
			       bounds.End,
			       out var blank,
			       cancellationToken))
		{
			ThrowIfCancellationRequestedPeriodically(cancellationToken, scannedLines++);
			if (retainedSuffixBlank is null && IsEmptyBlankLine(source, blank))
				retainedSuffixBlank = blank;
			suffixEnd = blank.End;
		}

		var hasContentBefore = prefixStart > bounds.Start;
		var hasContentAfter = suffixEnd < bounds.End;
		if (!hasContentBefore || !hasContentAfter)
		{
			AddCommentRemoval(destination, prefixStart, suffixEnd);
			return;
		}

		// Keep an existing empty line only. Retaining indentation-only whitespace leaves an
		// artifact, while creating a clean separator would make this a formatter.
		var retainedBlank = retainedPrefixBlank ?? retainedSuffixBlank;
		if (retainedBlank is { } blankLine)
		{
			AddCommentRemoval(destination, prefixStart, blankLine.Start);
			AddCommentRemoval(destination, blankLine.End, suffixEnd);
			return;
		}

		AddCommentRemoval(destination, prefixStart, suffixEnd);
	}

	private static void AddCommentRemoval(
		List<RawCompressionEdit> destination,
		int start,
		int end)
	{
		if (end <= start)
			return;

		destination.Add(new RawCompressionEdit(
			start,
			end,
			string.Empty,
			CodeTransformKinds.Comments));
	}

	private static bool TryReadBlankLineBefore(
		string source,
		int end,
		int lowerBound,
		out SourceRange blank,
		CancellationToken cancellationToken)
	{
		if (end <= lowerBound)
		{
			blank = default;
			return false;
		}

		var contentEnd = end;
		if (source[contentEnd - 1] == '\n')
		{
			contentEnd--;
			if (contentEnd > lowerBound && source[contentEnd - 1] == '\r')
				contentEnd--;
		}

		var previousLineFeed = contentEnd > lowerBound
			? source.LastIndexOf('\n', contentEnd - 1)
			: -1;
		var start = previousLineFeed + 1;
		if (start < lowerBound ||
		    !ContainsOnlyHorizontalWhitespace(source, start, contentEnd, cancellationToken))
		{
			blank = default;
			return false;
		}

		blank = new SourceRange(start, end);
		return true;
	}

	private static bool TryReadBlankLineAfter(
		string source,
		int start,
		int upperBound,
		out SourceRange blank,
		CancellationToken cancellationToken)
	{
		if (start >= upperBound)
		{
			blank = default;
			return false;
		}

		var nextLineFeed = source.IndexOf('\n', start, upperBound - start);
		if (nextLineFeed < 0 && upperBound < source.Length)
		{
			blank = default;
			return false;
		}

		var end = nextLineFeed >= 0 ? nextLineFeed + 1 : upperBound;
		var contentEnd = nextLineFeed >= 0 ? nextLineFeed : upperBound;
		if (contentEnd > start && source[contentEnd - 1] == '\r')
			contentEnd--;
		if (!ContainsOnlyHorizontalWhitespace(source, start, contentEnd, cancellationToken))
		{
			blank = default;
			return false;
		}

		blank = new SourceRange(start, end);
		return true;
	}

	private static bool ContainsOnlyHorizontalWhitespace(
		string source,
		int start,
		int end,
		CancellationToken cancellationToken)
	{
		if (!cancellationToken.CanBeCanceled)
		{
			for (var index = start; index < end; index++)
			{
				if (source[index] is not (' ' or '\t'))
					return false;
			}

			return true;
		}

		cancellationToken.ThrowIfCancellationRequested();
		for (var index = start; index < end; index += CancellationCheckBatchSize)
		{
			var length = Math.Min(CancellationCheckBatchSize, end - index);
			if (source.AsSpan(index, length).IndexOfAnyExcept(' ', '\t') >= 0)
				return false;
			cancellationToken.ThrowIfCancellationRequested();
		}

		return true;
	}

	private static bool ContainsOnlyWhitespace(
		string source,
		int start,
		int end,
		CancellationToken cancellationToken)
	{
		if (!cancellationToken.CanBeCanceled)
			return source.AsSpan(start, end - start).IsWhiteSpace();

		cancellationToken.ThrowIfCancellationRequested();
		for (var index = start; index < end; index += CancellationCheckBatchSize)
		{
			var length = Math.Min(CancellationCheckBatchSize, end - index);
			if (!source.AsSpan(index, length).IsWhiteSpace())
				return false;
			cancellationToken.ThrowIfCancellationRequested();
		}

		return true;
	}

	private static bool IsEmptyBlankLine(string source, SourceRange line)
	{
		var contentEnd = line.End;
		if (contentEnd > line.Start && source[contentEnd - 1] == '\n')
		{
			contentEnd--;
			if (contentEnd > line.Start && source[contentEnd - 1] == '\r')
				contentEnd--;
		}

		return contentEnd == line.Start;
	}

	private static bool ContainsOnlyBlankLineCharacters(
		string source,
		int start,
		int end,
		CancellationToken cancellationToken)
	{
		if (!cancellationToken.CanBeCanceled)
		{
			for (var index = start; index < end; index++)
			{
				if (source[index] is not (' ' or '\t' or '\r' or '\n'))
					return false;
			}

			return true;
		}

		for (var index = start; index < end; index++)
		{
			ThrowIfCancellationRequestedPeriodically(cancellationToken, index - start);
			if (source[index] is not (' ' or '\t' or '\r' or '\n'))
				return false;
		}

		return true;
	}

	private static bool IsContainedInPreservedRange(
		int start,
		int end,
		IReadOnlyList<SourceRange> preservedRanges)
	{
		var lower = 0;
		var upper = preservedRanges.Count - 1;
		while (lower <= upper)
		{
			var middle = lower + (upper - lower) / 2;
			var range = preservedRanges[middle];
			if (range.Start > start)
			{
				upper = middle - 1;
				continue;
			}

			if (range.End <= start)
			{
				lower = middle + 1;
				continue;
			}

			return end <= range.End;
		}

		return false;
	}

	private static bool IsLeadingDocstring(Node docstring, CancellationToken cancellationToken)
	{
		var container = docstring.Parent;
		if (container is null)
			return false;

		var childIndex = 0;
		foreach (var child in container.Children)
		{
			ThrowIfCancellationRequestedPeriodically(cancellationToken, childIndex++);
			if (!child.IsNamed || child.Type.Contains("comment", StringComparison.OrdinalIgnoreCase))
				continue;
			return child.StartIndex == docstring.StartIndex && child.EndIndex == docstring.EndIndex;
		}

		return false;
	}

	private static bool RequiresSuitePlaceholder(Node docstring, CancellationToken cancellationToken)
	{
		var suite = docstring.Parent;
		if (suite is null || !suite.Type.Equals("block", StringComparison.Ordinal))
			return false;

		var childIndex = 0;
		foreach (var child in suite.Children)
		{
			ThrowIfCancellationRequestedPeriodically(cancellationToken, childIndex++);
			if (!child.IsNamed || child.StartIndex == docstring.StartIndex && child.EndIndex == docstring.EndIndex)
				continue;
			if (!child.Type.Contains("comment", StringComparison.OrdinalIgnoreCase))
				return false;
		}

		return true;
	}

	private static bool TryResolveContentAfterLeadingDocstring(
		Node body,
		string source,
		out int start,
		out bool hasLeadingDocstring,
		CancellationToken cancellationToken)
	{
		start = body.StartIndex;
		hasLeadingDocstring = false;
		Node? firstStatement = null;
		Node? secondStatement = null;
		var childIndex = 0;
		foreach (var child in body.Children)
		{
			ThrowIfCancellationRequestedPeriodically(cancellationToken, childIndex++);
			if (!child.IsNamed)
				continue;
			if (firstStatement is null)
			{
				firstStatement = child;
				continue;
			}

			secondStatement = child;
			break;
		}

		if (firstStatement is null || !IsSimpleStringStatement(firstStatement, source))
			return true;
		hasLeadingDocstring = true;
		if (secondStatement is null)
			return false;

		start = secondStatement.StartIndex;
		return true;
	}

	private static bool IsSimpleStringStatement(Node statement, string source)
	{
		var stringNode = statement.Type.Equals("string", StringComparison.Ordinal)
			? statement
			: statement.Type.Equals("expression_statement", StringComparison.Ordinal)
				? statement.Children.FirstOrDefault(static child => child.IsNamed)
				: null;
		if (stringNode is null || !stringNode.Type.Equals("string", StringComparison.Ordinal))
			return false;
		if (stringNode.StartIndex < 0 ||
		    stringNode.EndIndex > source.Length ||
		    stringNode.EndIndex <= stringNode.StartIndex)
		{
			return false;
		}

		var text = source.AsSpan(stringNode.StartIndex, stringNode.EndIndex - stringNode.StartIndex).TrimStart();
		var quoteIndex = text.IndexOfAny('\'', '"');
		if (quoteIndex < 0)
			return false;
		var prefix = text[..quoteIndex];
		return !prefix.Contains('f') &&
		       !prefix.Contains('F') &&
		       !prefix.Contains('b') &&
		       !prefix.Contains('B');
	}

	private static IReadOnlyList<SourceRange>? ReadPreservedRanges(
		LoadedLanguage language,
		Node root,
		CancellationToken cancellationToken,
		out int captureCount)
	{
		captureCount = 0;
		if (language.Runtime.Preserves is null || language.PreservesCursor is null)
			return Array.Empty<SourceRange>();

		List<SourceRange>? ranges = null;
		language.PreservesCursor.Execute(language.Runtime.Preserves, root);
		var captureIndex = 0;
		foreach (var capture in language.PreservesCursor.Captures)
		{
			ThrowIfCancellationRequestedPeriodically(cancellationToken, captureIndex++);
			if (!capture.Name.Equals("preserve", StringComparison.Ordinal))
				continue;
			if (captureCount >= TreeSitterCodeCompressor.MaximumPreservedRangesPerFile)
				return null;
			captureCount++;
			if (capture.Node.StartIndex < 0 || capture.Node.EndIndex <= capture.Node.StartIndex)
				continue;
			(ranges ??= []).Add(new SourceRange(capture.Node.StartIndex, capture.Node.EndIndex));
		}
		if (language.PreservesCursor.IsMatchLimitExceeded)
			return null;
		if (ranges is null)
			return Array.Empty<SourceRange>();
		return ranges;
	}

	private static IReadOnlyList<SourceRange> MergePreservedRanges(
		IReadOnlyList<SourceRange> sourceRanges,
		CancellationToken cancellationToken)
	{
		if (sourceRanges.Count < 2)
			return sourceRanges;

		var ranges = sourceRanges as List<SourceRange> ?? [.. sourceRanges];
		cancellationToken.ThrowIfCancellationRequested();
		ranges.Sort(static (left, right) =>
			left.Start != right.Start
				? left.Start.CompareTo(right.Start)
				: right.End.CompareTo(left.End));
		var writeIndex = 0;
		for (var readIndex = 1; readIndex < ranges.Count; readIndex++)
		{
			ThrowIfCancellationRequestedPeriodically(cancellationToken, readIndex);
			var current = ranges[writeIndex];
			var next = ranges[readIndex];
			if (next.Start <= current.End)
			{
				ranges[writeIndex] = new SourceRange(current.Start, Math.Max(current.End, next.End));
				continue;
			}

			ranges[++writeIndex] = next;
		}
		ranges.RemoveRange(writeIndex + 1, ranges.Count - writeIndex - 1);
		cancellationToken.ThrowIfCancellationRequested();
		return ranges;
	}

	private readonly record struct SourceRange(int Start, int End);
	private readonly record struct CommentCapture(Node Node, bool IsDocstring);

	private struct DeferredCommentBuffer
	{
		private const int InlineCapacity = 8;
		private CommentCapture _capture0;
		private CommentCapture _capture1;
		private CommentCapture _capture2;
		private CommentCapture _capture3;
		private CommentCapture _capture4;
		private CommentCapture _capture5;
		private CommentCapture _capture6;
		private CommentCapture _capture7;
		private CommentCapture[]? _spill;

		public int Count { get; private set; }

		public readonly CommentCapture this[int index] => index switch
		{
			0 => _capture0,
			1 => _capture1,
			2 => _capture2,
			3 => _capture3,
			4 => _capture4,
			5 => _capture5,
			6 => _capture6,
			7 => _capture7,
			_ when index >= InlineCapacity && index < Count => _spill![index - InlineCapacity],
			_ => throw new ArgumentOutOfRangeException(nameof(index))
		};

		public void Add(CommentCapture capture)
		{
			if (Count < InlineCapacity)
			{
				SetInline(Count, capture);
				Count++;
				return;
			}

			var spillIndex = Count - InlineCapacity;
			if (_spill is null)
			{
				_spill = ArrayPool<CommentCapture>.Shared.Rent(128);
			}
			else if (spillIndex == _spill.Length)
			{
				var expanded = ArrayPool<CommentCapture>.Shared.Rent(
					Math.Min(
						TreeSitterCodeCompressor.MaximumCommentCapturesPerFile - InlineCapacity,
						_spill.Length * 2));
				Array.Copy(_spill, expanded, spillIndex);
				Array.Clear(_spill, 0, spillIndex);
				ArrayPool<CommentCapture>.Shared.Return(_spill);
				_spill = expanded;
			}

			_spill[spillIndex] = capture;
			Count++;
		}

		public void Dispose()
		{
			_capture0 = default;
			_capture1 = default;
			_capture2 = default;
			_capture3 = default;
			_capture4 = default;
			_capture5 = default;
			_capture6 = default;
			_capture7 = default;
			if (_spill is not null)
			{
				Array.Clear(_spill, 0, Math.Max(0, Count - InlineCapacity));
				ArrayPool<CommentCapture>.Shared.Return(_spill);
				_spill = null;
			}
			Count = 0;
		}

		private void SetInline(int index, CommentCapture capture)
		{
			switch (index)
			{
				case 0: _capture0 = capture; break;
				case 1: _capture1 = capture; break;
				case 2: _capture2 = capture; break;
				case 3: _capture3 = capture; break;
				case 4: _capture4 = capture; break;
				case 5: _capture5 = capture; break;
				case 6: _capture6 = capture; break;
				case 7: _capture7 = capture; break;
				default: throw new ArgumentOutOfRangeException(nameof(index));
			}
		}
	}

	private readonly record struct CommentLineCollapseCandidate(
		int EditIndex,
		SourceRange Bounds);

	private readonly record struct RawCompressionEdit(
		int Start,
		int End,
		string Replacement,
		CodeTransformKinds Kinds);

	private static bool IsMisparsedCppClassBody(
		CompressionLanguagePack pack,
		Node node,
		string source,
		CancellationToken cancellationToken)
	{
		if (!pack.Id.Equals("cpp", StringComparison.Ordinal) ||
		    node.Parent is not { Type: "function_definition" } function)
		{
			return false;
		}

		var childIndex = 0;
		foreach (var child in node.Children)
		{
			ThrowIfCancellationRequestedPeriodically(cancellationToken, childIndex++);
			if (IsCppAccessLabel(child, source))
				return true;
		}

		var declarator = function.GetChildForField("declarator");
		return declarator is null ||
		       declarator.EndIndex > node.StartIndex ||
		       !ContainsNodeTypeBefore(
			       declarator,
			       "parameter_list",
			       node.StartIndex,
			       cancellationToken);
	}

	private static bool IsCppAccessLabel(Node node, string source)
	{
		if (!node.Type.Equals("labeled_statement", StringComparison.Ordinal) ||
		    node.StartIndex < 0 ||
		    node.EndIndex > source.Length)
		{
			return false;
		}

		var label = source.AsSpan(node.StartIndex, Math.Min(node.EndIndex - node.StartIndex, 16)).TrimStart();
		return label.StartsWith("public:", StringComparison.Ordinal) ||
		       label.StartsWith("private:", StringComparison.Ordinal) ||
		       label.StartsWith("protected:", StringComparison.Ordinal);
	}

	private static bool ContainsNodeTypeBefore(
		Node root,
		string nodeType,
		int maximumEnd,
		CancellationToken cancellationToken)
	{
		var stack = new Stack<Node>();
		stack.Push(root);
		var visitedNodes = 0;
		while (stack.Count > 0)
		{
			ThrowIfCancellationRequestedPeriodically(cancellationToken, visitedNodes++);
			var node = stack.Pop();
			if (node.EndIndex <= maximumEnd && node.Type.Equals(nodeType, StringComparison.Ordinal))
				return true;
			foreach (var child in node.Children)
				stack.Push(child);
		}

		return false;
	}

	/// <summary>
	/// Python comments before the first statement are extra nodes outside the block captured by the
	/// query. Include only those complete lines, never preprocessor directives from other languages.
	/// </summary>
	private static int ExtendOverLeadingPythonComments(
		string source,
		Node body,
		CancellationToken cancellationToken)
	{
		var limit = body.Parent?.StartIndex ?? 0;
		var start = body.StartIndex;
		var scannedLines = 0;
		while (true)
		{
			ThrowIfCancellationRequestedPeriodically(cancellationToken, scannedLines++);
			var lineStart = source.LastIndexOf('\n', Math.Max(0, start - 1));
			if (lineStart < 0 || lineStart <= limit)
				return start;

			var previousLineStart = source.LastIndexOf('\n', Math.Max(0, lineStart - 1));
			if (previousLineStart < limit)
				return start;

			var line = source[(previousLineStart + 1)..lineStart].Trim();
			if (line.Length == 0 || !line.StartsWith('#'))
				return start;

			start = previousLineStart + 1;
		}
	}

	private static RawCompressionEdit ResolveBlockEdit(
		CompressionLanguagePack pack,
		string source,
		int start,
		int end,
		CancellationToken cancellationToken)
	{
		return pack.BlockBodyStyle switch
		{
			BlockBodyStyle.Inline => new RawCompressionEdit(
				start,
				end,
				pack.BlockPlaceholder,
				CodeTransformKinds.Bodies),
			BlockBodyStyle.IndentedStatement => new RawCompressionEdit(
				start,
				end,
				IndentedStatementPlaceholder(
					pack.BlockPlaceholder,
					source,
					start,
					end,
					cancellationToken),
				CodeTransformKinds.Bodies),
			BlockBodyStyle.RemoveCompleteLines => RemoveCompleteBodyLines(
				source,
				start,
				end,
				cancellationToken),
			_ => throw new InvalidOperationException($"Unsupported block body style '{pack.BlockBodyStyle}'.")
		};
	}

	/// <summary>
	/// Indentation-sensitive grammars need a valid statement at the original suite depth. A retained
	/// leading declaration can move the edit, so the indentation is always derived from the edit.
	/// </summary>
	private static string IndentedStatementPlaceholder(
		string placeholder,
		string source,
		int start,
		int end,
		CancellationToken cancellationToken)
	{

		// An indented placeholder keeps an indentation-sensitive suite non-empty. Reusing the first
		// removed line's indentation keeps the result parseable after retained leading declarations.
		var lineStart = source.LastIndexOf('\n', Math.Max(0, Math.Min(start, source.Length - 1)));
		var indentation = new string(' ', 4);
		if (lineStart >= 0 && lineStart + 1 < source.Length)
		{
			var scan = lineStart + 1;
			while (scan < source.Length && (source[scan] == ' ' || source[scan] == '\t'))
			{
				ThrowIfCancellationRequestedPeriodically(cancellationToken, scan - lineStart - 1);
				scan++;
			}
			if (scan > lineStart + 1)
				indentation = source[(lineStart + 1)..scan];
		}

		var lineEnding = ResolveLineEnding(source, lineStart);
		var trailingNewline = end > 0 && end <= source.Length && source[end - 1] == '\n'
			? end > 1 && source[end - 2] == '\r' ? "\r\n" : "\n"
			: string.Empty;

		// What the retained text already ends with decides what the placeholder has to supply. A
		// tree-sitter block starts at its first statement, so the newline and indentation before it
		// are kept - emitting them again would leave a blank, whitespace-only line above the "...".
		// After the edit was extended back over leading comments the start sits at column zero, and
		// only the indentation is missing.
		var retainedEndsAtLineStart = start > 0 && source[start - 1] == '\n';
		var retainedEndsWithIndentation =
			!retainedEndsAtLineStart &&
			lineStart >= 0 &&
			start > lineStart + 1 &&
			ContainsOnlyWhitespace(
				source,
				lineStart + 1,
				start,
				cancellationToken);
		var leading = retainedEndsWithIndentation
			? string.Empty
			: retainedEndsAtLineStart
				? indentation
				: $"{lineEnding}{indentation}";
		return $"{leading}{placeholder}{trailingNewline}";
	}

	private static RawCompressionEdit RemoveCompleteBodyLines(
		string source,
		int start,
		int end,
		CancellationToken cancellationToken)
	{
		var expandedStart = start;
		var lineStart = source.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
		if (ContainsOnlyWhitespace(source, lineStart, start, cancellationToken))
			expandedStart = lineStart;

		var expandedEnd = end;
		while (expandedEnd < source.Length && source[expandedEnd] is ' ' or '\t')
		{
			ThrowIfCancellationRequestedPeriodically(cancellationToken, expandedEnd - end);
			expandedEnd++;
		}
		if (expandedEnd < source.Length && source[expandedEnd] == '\r')
		{
			expandedEnd++;
			if (expandedEnd < source.Length && source[expandedEnd] == '\n')
				expandedEnd++;
		}
		else if (expandedEnd < source.Length && source[expandedEnd] == '\n')
		{
			expandedEnd++;
		}

		return new RawCompressionEdit(
			expandedStart,
			expandedEnd,
			string.Empty,
			CodeTransformKinds.Bodies);
	}

	private static string ResolveLineEnding(string source, int precedingLineFeed)
	{
		if (precedingLineFeed >= 0)
			return precedingLineFeed > 0 && source[precedingLineFeed - 1] == '\r' ? "\r\n" : "\n";

		var nextLineFeed = source.IndexOf('\n');
		return nextLineFeed > 0 && source[nextLineFeed - 1] == '\r' ? "\r\n" : "\n";
	}

	private static List<CodeDeclaration>? ReadDeclarations(
		LoadedLanguage language,
		Node root,
		ContentTransformMap map,
		CancellationToken cancellationToken,
		out int declarationCount)
	{
		var declarations = new List<CodeDeclaration>();
		declarationCount = 0;
		language.DeclarationsCursor.Execute(language.Runtime.Declarations, root);
		var matchIndex = 0;
		foreach (var match in language.DeclarationsCursor.Matches)
		{
			ThrowIfCancellationRequestedPeriodically(cancellationToken, matchIndex++);
			Node? declaration = null;
			string? name = null;
			var captureIndex = 0;
			foreach (var capture in match.Captures)
			{
				ThrowIfCancellationRequestedPeriodically(cancellationToken, captureIndex++);
				if (capture.Name.Equals("declaration", StringComparison.Ordinal))
					declaration = capture.Node;
				else if (capture.Name.Equals("name", StringComparison.Ordinal))
					name = capture.Node.Text;
			}

			if (declaration is null)
				continue;
			if (declarations.Count >= TreeSitterCodeCompressor.MaximumDeclarationsPerFile)
				return null;
			if (!map.TryToSource(declaration.StartIndex, out var start))
				continue;
			if (!map.TryToSource(declaration.EndIndex, out var end))
				end = start;
			declarations.Add(new CodeDeclaration(declaration.Type, name ?? string.Empty, start, Math.Max(0, end - start)));
			declarationCount++;
		}
		if (language.DeclarationsCursor.IsMatchLimitExceeded)
			return null;

		cancellationToken.ThrowIfCancellationRequested();
		declarations.Sort(static (left, right) =>
		{
			var result = left.Start.CompareTo(right.Start);
			if (result != 0)
				return result;

			result = string.CompareOrdinal(left.Kind, right.Kind);
			return result != 0 ? result : string.CompareOrdinal(left.Name, right.Name);
		});
		cancellationToken.ThrowIfCancellationRequested();
		return declarations;
	}

	private static List<CodeParseDefect>? ReadDefects(
		Node root,
		ContentTransformMap map,
		CancellationToken cancellationToken,
		out int defectCount,
		out int visitedNodes)
		=> ReadDefects(
			root,
			map,
			cancellationToken,
			out defectCount,
			out visitedNodes,
			out _,
			collectProtectedMultilineLeaves: false);

	private static List<CodeParseDefect>? ReadDefects(
		Node root,
		ContentTransformMap map,
		CancellationToken cancellationToken,
		out int defectCount,
		out int visitedNodes,
		out IReadOnlyList<SourceRange> protectedMultilineLeaves,
		bool collectProtectedMultilineLeaves)
	{
		var defects = new List<CodeParseDefect>();
		List<SourceRange>? protectedRanges = null;
		using var cursor = new TreeCursor(root);
		defectCount = 0;
		visitedNodes = 0;
		protectedMultilineLeaves = Array.Empty<SourceRange>();
		while (true)
		{
			if (++visitedNodes > TreeSitterCodeCompressor.MaximumVisitedSyntaxNodesPerFile)
				return null;
			ThrowIfCancellationRequestedPeriodically(cancellationToken, visitedNodes);
			var node = cursor.CurrentNode;
			if (collectProtectedMultilineLeaves && IsProtectedMultilineLeaf(node))
			{
				(protectedRanges ??= []).Add(new SourceRange(node.StartIndex, node.EndIndex));
			}
			// See CodeParseDefect: HasError lies in both directions against the shipped grammars,
			// and a MISSING node surfaces here only as a named node of zero width.
			var isDefect = node.IsError ||
			               node.IsMissing ||
			               (node.IsNamed && node.StartIndex == node.EndIndex && node.Parent is not null);
			if (isDefect)
			{
				if (defects.Count >= TreeSitterCodeCompressor.MaximumDefectsPerFile)
					return null;
				var kind = node.IsError ? "ERROR" : "MISSING";
				defects.Add(new CodeParseDefect(kind, map.TryToSource(node.StartIndex, out var source) ? source : -1));
				defectCount++;
			}

			if (cursor.GotoFirstChild())
				continue;

			while (!cursor.GotoNextSibling())
			{
				if (!cursor.GotoParent())
				{
					cancellationToken.ThrowIfCancellationRequested();
					protectedMultilineLeaves = protectedRanges is null
						? Array.Empty<SourceRange>()
						: protectedRanges;
					return defects;
				}
			}
		}
	}

	private static bool IsProtectedMultilineLeaf(Node node)
	{
		if (node.StartPosition.Row == node.EndPosition.Row || node.EndIndex <= node.StartIndex)
			return false;
		if (node.FirstChild is null)
			return true;

		// Some external-scanner tokens expose an anonymous prefix marker but keep their payload in
		// the owning named node. Treat that shape as a semantic leaf only when every child ends on
		// the opening row; paired delimiters spanning the node (for example an empty block) remain
		// structural and therefore do not protect whitespace between them.
		return node.IsNamed &&
		       node.FirstNamedChild is null &&
		       node.LastChild is { } lastChild &&
		       lastChild.EndPosition.Row == node.StartPosition.Row;
	}

	private readonly record struct OriginalTreeInspection(List<CodeParseDefect>? Defects);

	private static bool IsLanguageRuntimeFailure(Exception exception) =>
		exception is DllNotFoundException or
			InvalidOperationException or
			IOException or
			UnauthorizedAccessException or
			BadImageFormatException or
			EntryPointNotFoundException;

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 0)
			releaseScope();
	}
}

internal readonly record struct CodeCompressionRuntimeDiagnostics(
	int CompiledQuerySets,
	int MaterializedWorkers,
	int AvailableWorkers,
	int LeasedWorkers,
	int GlobalWorkerCapacity,
	int GlobalActiveWorkers,
	int GlobalPeakActiveWorkers,
	int GlobalRetainedWorkers,
	int GlobalRetainedWorkerCapacity);

/// <summary>
/// Bounded process-lifetime parser pool. Grammar verification and materialization happen once;
/// native parser state is never entered concurrently.
/// </summary>
internal sealed class LanguageWorkerPool : IDisposable
{
	private const int MaximumRetainedWorkers = 2;
	private readonly IGrammarLibraryLocator _locator;
	private readonly CompressionLanguagePack _pack;
	private readonly LanguageWorkerBudget _workerBudget;
	private readonly bool _ownsWorkerBudget;
	private readonly uint _queryMatchLimit;
	private readonly ConcurrentBag<LoadedLanguage> _available = [];
	private readonly object _sync = new();
	private LanguageRuntime? _runtime;
	private ExceptionDispatchInfo? _permanentRuntimeFailure;
	private ExceptionDispatchInfo? _transientRuntimeFailure;
	private long _transientFailureOperationId = -1;
	private int _leasedWorkers;
	private int _materializedWorkers;
	private bool _disposed;

	public LanguageWorkerPool(IGrammarLibraryLocator locator, CompressionLanguagePack pack)
		: this(locator, pack, new LanguageWorkerBudget(), ownsWorkerBudget: true)
	{
	}

	internal LanguageWorkerPool(
		IGrammarLibraryLocator locator,
		CompressionLanguagePack pack,
		LanguageWorkerBudget workerBudget,
		bool ownsWorkerBudget = false,
		uint queryMatchLimit = TreeSitterCodeCompressor.MaximumQueryMatchLimit)
	{
		ArgumentOutOfRangeException.ThrowIfZero(queryMatchLimit);
		_locator = locator;
		_pack = pack;
		_workerBudget = workerBudget;
		_ownsWorkerBudget = ownsWorkerBudget;
		_queryMatchLimit = queryMatchLimit;
	}

	internal LanguagePoolDiagnostics Diagnostics
	{
		get
		{
			lock (_sync)
			{
				return new LanguagePoolDiagnostics(
					_runtime is not null ? 1 : 0,
					_materializedWorkers,
					_available.Count,
					_leasedWorkers);
			}
		}
	}

	public LanguageWorkerLease Rent(CancellationToken cancellationToken) =>
		Rent(operationId: 0, needsComments: false, cancellationToken);

	internal LanguageWorkerLease Rent(
		long operationId,
		bool needsComments,
		CancellationToken cancellationToken)
	{
		var budgetLease = _workerBudget.Rent(cancellationToken);
		LoadedLanguage? worker = null;
		var leaseRegistered = false;
		try
		{
			lock (_sync)
			{
				ObjectDisposedException.ThrowIf(_disposed, this);
				_leasedWorkers++;
				leaseRegistered = true;
				if (_available.TryTake(out worker))
					_workerBudget.ReleaseRetained();
			}

			if (worker is null)
			{
				worker = LoadedLanguage.Create(
					GetOrCreateRuntime(operationId),
					_queryMatchLimit);
				lock (_sync)
					_materializedWorkers++;
			}
			if (needsComments)
				worker.EnsureCommentsCursor(_queryMatchLimit);
			lock (_sync)
				ObjectDisposedException.ThrowIf(_disposed, this);
			return new LanguageWorkerLease(this, worker, budgetLease);
		}
		catch
		{
			if (worker is not null)
			{
				worker.Dispose();
				lock (_sync)
					_materializedWorkers--;
			}
			LanguageRuntime? runtimeToDispose;
			lock (_sync)
			{
				if (leaseRegistered)
					_leasedWorkers--;
				runtimeToDispose = TakeRuntimeForDisposalLocked();
			}
			runtimeToDispose?.Dispose();
			budgetLease.Dispose();
			throw;
		}
	}

	internal void Return(LoadedLanguage worker, IDisposable budgetLease)
	{
		var reusable = true;
		try
		{
			worker.PrepareForReturn();
		}
		catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
		{
			reusable = false;
		}

		var disposeWorker = !reusable;
		LanguageRuntime? runtimeToDispose;
		lock (_sync)
		{
			_leasedWorkers--;
			if (_disposed)
				disposeWorker = true;
			else if (reusable &&
			         _available.Count < MaximumRetainedWorkers &&
			         _workerBudget.TryRetain())
			{
				_available.Add(worker);
			}
			else
			{
				disposeWorker = true;
			}
			if (disposeWorker)
				_materializedWorkers--;
			runtimeToDispose = TakeRuntimeForDisposalLocked();
		}

		if (disposeWorker)
			worker.Dispose();
		budgetLease.Dispose();
		runtimeToDispose?.Dispose();
	}

	public void Dispose()
	{
		List<LoadedLanguage> workers;
		LanguageRuntime? runtimeToDispose;
		lock (_sync)
		{
			if (_disposed)
				return;
			_disposed = true;
			workers = new List<LoadedLanguage>(_available.Count);
			while (_available.TryTake(out var worker))
				workers.Add(worker);
			_materializedWorkers -= workers.Count;
			runtimeToDispose = TakeRuntimeForDisposalLocked();
		}

		foreach (var worker in workers)
			worker.Dispose();
		_workerBudget.ReleaseRetained(workers.Count);
		if (_ownsWorkerBudget)
			_workerBudget.Dispose();
		runtimeToDispose?.Dispose();
	}

	private LanguageRuntime GetOrCreateRuntime(long operationId)
	{
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_runtime is not null)
				return _runtime;
			_permanentRuntimeFailure?.Throw();
			if (_transientFailureOperationId == operationId)
				_transientRuntimeFailure?.Throw();

			try
			{
				_runtime = LanguageRuntime.Create(
					_locator.Resolve(_pack.Library),
					_pack);
				_transientRuntimeFailure = null;
				_transientFailureOperationId = -1;
				return _runtime;
			}
			catch (Exception exception) when (IsTransientInitializationFailure(exception))
			{
				_transientRuntimeFailure = ExceptionDispatchInfo.Capture(exception);
				_transientFailureOperationId = operationId;
				throw;
			}
			catch (Exception exception)
			{
				_permanentRuntimeFailure = ExceptionDispatchInfo.Capture(exception);
				throw;
			}
		}
	}

	private static bool IsTransientInitializationFailure(Exception exception) =>
		exception is IOException or UnauthorizedAccessException or DllNotFoundException;

	private LanguageRuntime? TakeRuntimeForDisposalLocked()
	{
		if (!_disposed ||
		    _leasedWorkers != 0 ||
		    _runtime is null)
		{
			return null;
		}

		var runtime = _runtime;
		_runtime = null;
		return runtime;
	}
}

internal readonly record struct LanguagePoolDiagnostics(
	int CompiledQuerySets,
	int MaterializedWorkers,
	int AvailableWorkers,
	int LeasedWorkers);

internal sealed class LanguageWorkerLease(
	LanguageWorkerPool owner,
	LoadedLanguage worker,
	IDisposable budgetLease) : IDisposable
{
	private LanguageWorkerPool? _owner = owner;

	public LoadedLanguage Worker { get; } = worker;

	public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Return(Worker, budgetLease);
}

internal sealed class LanguageWorkerBudget : IDisposable
{
	private const int MaximumActiveWorkers = 8;
	private readonly SemaphoreSlim _capacity;
	private readonly CancellationTokenSource _shutdown = new();
	private int _active;
	private int _peakActive;
	private int _retained;
	private int _disposed;

	public LanguageWorkerBudget(int minimumRetainedWorkers = 1)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumRetainedWorkers);
		Capacity = Math.Clamp(Environment.ProcessorCount, 1, MaximumActiveWorkers);
		RetainedCapacity = Math.Max(minimumRetainedWorkers, Capacity * 2);
		_capacity = new SemaphoreSlim(Capacity, Capacity);
	}

	public int Capacity { get; }
	public int RetainedCapacity { get; }

	internal LanguageWorkerBudgetDiagnostics Diagnostics => new(
		Capacity,
		Volatile.Read(ref _active),
		Volatile.Read(ref _peakActive),
		Volatile.Read(ref _retained),
		RetainedCapacity);

	public bool TryRetain()
	{
		while (true)
		{
			var retained = Volatile.Read(ref _retained);
			if (retained >= RetainedCapacity)
				return false;
			if (Interlocked.CompareExchange(ref _retained, retained + 1, retained) == retained)
				return true;
		}
	}

	public void ReleaseRetained(int count = 1)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(count);
		if (count == 0)
			return;
		var retained = Interlocked.Add(ref _retained, -count);
		if (retained < 0)
			throw new InvalidOperationException("The retained worker budget was released more than it was acquired.");
	}

	public IDisposable Rent(CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(
			cancellationToken,
			_shutdown.Token);
		try
		{
			_capacity.Wait(linked.Token);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new ObjectDisposedException(nameof(LanguageWorkerBudget));
		}

		if (Volatile.Read(ref _disposed) != 0)
		{
			_capacity.Release();
			throw new ObjectDisposedException(nameof(LanguageWorkerBudget));
		}

		var active = Interlocked.Increment(ref _active);
		UpdatePeak(active);
		return new BudgetLease(this);
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 0)
			_shutdown.Cancel();
	}

	private void Return()
	{
		Interlocked.Decrement(ref _active);
		_capacity.Release();
	}

	private void UpdatePeak(int active)
	{
		while (true)
		{
			var current = Volatile.Read(ref _peakActive);
			if (current >= active ||
			    Interlocked.CompareExchange(ref _peakActive, active, current) == current)
			{
				return;
			}
		}
	}

	private sealed class BudgetLease(LanguageWorkerBudget owner) : IDisposable
	{
		private LanguageWorkerBudget? _owner = owner;

		public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Return();
	}
}

internal readonly record struct LanguageWorkerBudgetDiagnostics(
	int Capacity,
	int Active,
	int PeakActive,
	int Retained,
	int RetainedCapacity);

/// <summary>
/// One immutable language/query set shared by every parser worker. Tree-sitter explicitly permits
/// sharing TSQuery between threads; execution state remains isolated in each worker's cursors.
/// </summary>
internal sealed record LanguageRuntime(
	Language Language,
	Query? Bodies,
	Query Declarations,
	Query? Preserves,
	Lazy<Query?> Comments) : IDisposable
{
	public static LanguageRuntime Create(string libraryPath, CompressionLanguagePack pack)
	{
		var language = new Language(libraryPath, pack.Export);
		Query? bodies = null;
		Query? declarations = null;
		Query? preserves = null;
		try
		{
			bodies = pack.BodiesQuery is null
				? null
				: new Query(language, pack.BodiesQuery);
			declarations = new Query(language, pack.DeclarationsQuery);
			preserves = pack.PreservesQuery is null
				? null
				: new Query(language, pack.PreservesQuery);
			var comments = new Lazy<Query?>(
				() => pack.CommentsQuery is null
					? null
					: new Query(language, pack.CommentsQuery),
				LazyThreadSafetyMode.ExecutionAndPublication);
			return new LanguageRuntime(language, bodies, declarations, preserves, comments);
		}
		catch
		{
			preserves?.Dispose();
			declarations?.Dispose();
			bodies?.Dispose();
			language.Dispose();
			throw;
		}
	}

	public void Dispose()
	{
		if (Comments.IsValueCreated)
			Comments.Value?.Dispose();
		Preserves?.Dispose();
		Declarations.Dispose();
		Bodies?.Dispose();
		Language.Dispose();
	}
}

/// <summary>One non-thread-safe parser and one reusable cursor per query.</summary>
internal sealed record LoadedLanguage(
	LanguageRuntime Runtime,
	Parser Parser,
	Tree IdleTree,
	QueryCursor? BodiesCursor,
	QueryCursor DeclarationsCursor,
	QueryCursor? PreservesCursor) : IDisposable
{
	private bool _commentsCursorHasTreeReference;

	public QueryCursor? CommentsCursor { get; private set; }

	public static LoadedLanguage Create(LanguageRuntime runtime, uint queryMatchLimit)
	{
		var parser = new Parser(runtime.Language);
		Tree? idleTree = null;
		QueryCursor? bodiesCursor = null;
		QueryCursor? declarationsCursor = null;
		QueryCursor? preservesCursor = null;
		try
		{
			idleTree = parser.Parse(string.Empty) ??
			           throw new InvalidOperationException("Tree-sitter could not create an idle tree.");
			bodiesCursor = runtime.Bodies is null
				? null
				: CreateBoundedCursor(queryMatchLimit);
			declarationsCursor = CreateBoundedCursor(queryMatchLimit);
			preservesCursor = runtime.Preserves is null
				? null
				: CreateBoundedCursor(queryMatchLimit);
			return new LoadedLanguage(
				runtime,
				parser,
				idleTree,
				bodiesCursor,
				declarationsCursor,
				preservesCursor);
		}
		catch
		{
			preservesCursor?.Dispose();
			declarationsCursor?.Dispose();
			bodiesCursor?.Dispose();
			idleTree?.Dispose();
			parser.Dispose();
			throw;
		}
	}

	private static QueryCursor CreateBoundedCursor(uint queryMatchLimit) =>
		new() { MatchLimit = queryMatchLimit };

	public void EnsureCommentsCursor(uint queryMatchLimit)
	{
		if (CommentsCursor is not null)
			return;

		if (Runtime.Comments.Value is not null)
			CommentsCursor = CreateBoundedCursor(queryMatchLimit);
	}

	public QueryCursor? ExecuteComments(Node root)
	{
		var comments = Runtime.Comments.Value;
		if (CommentsCursor is null || comments is null)
			return null;

		CommentsCursor.Execute(comments, root);
		_commentsCursorHasTreeReference = true;
		return CommentsCursor;
	}

	/// <summary>
	/// QueryCursor retains its last Tree and Tree retains the parsed string. Rebinding to a tiny
	/// process-lifetime tree prevents an idle pool from retaining the last large files it parsed.
	/// </summary>
	public void PrepareForReturn()
		=> ReleaseQueryTreeReferences();

	public void ReleaseQueryTreeReferences()
	{
		var root = IdleTree.RootNode;
		if (BodiesCursor is not null && Runtime.Bodies is not null)
			BodiesCursor.Execute(Runtime.Bodies, root);
		DeclarationsCursor.Execute(Runtime.Declarations, root);
		if (PreservesCursor is not null && Runtime.Preserves is not null)
			PreservesCursor.Execute(Runtime.Preserves, root);
		if (_commentsCursorHasTreeReference &&
		    CommentsCursor is not null &&
		    Runtime.Comments.Value is { } comments)
		{
			CommentsCursor.Execute(comments, root);
			_commentsCursorHasTreeReference = false;
		}
	}

	public void Dispose()
	{
		CommentsCursor?.Dispose();
		PreservesCursor?.Dispose();
		DeclarationsCursor.Dispose();
		BodiesCursor?.Dispose();
		IdleTree.Dispose();
		Parser.Dispose();
	}
}
