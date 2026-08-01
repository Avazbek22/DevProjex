namespace DevProjex.Application.Diagnostics;

/// <summary>
/// Provides opt-in structural measurements for the developer benchmark and contract tests.
/// The production hot path pays only a disabled guard unless a measurement is active.
/// </summary>
public static class IgnorePipelineDiagnostics
{
	private static readonly AsyncLocal<MeasurementState?> CurrentState = new();
	private static int _activeMeasurements;

	public static bool IsEnabled => Volatile.Read(ref _activeMeasurements) != 0;

	public static IgnorePipelineMeasurement BeginMeasurement()
	{
		var state = new MeasurementState();
		var previousState = CurrentState.Value;
		CurrentState.Value = state;
		Interlocked.Increment(ref _activeMeasurements);
		return new IgnorePipelineMeasurement(state, previousState, CompleteMeasurement);
	}

	public static void RecordRootFactsRequest() => Increment(static state => ref state.RootFactsRequests);

	public static void RecordRootFactsCacheHit() => Increment(static state => ref state.RootFactsCacheHits);

	public static void RecordRootFactsBuild() => Increment(static state => ref state.RootFactsBuilds);

	public static void RecordRootFactsEviction() => Increment(static state => ref state.RootFactsEvictions);

	public static void RecordProjectScopeDiscovery() => Increment(static state => ref state.ProjectScopeDiscoveries);

	public static void RecordIgnoreRulesBuild() => Increment(static state => ref state.IgnoreRulesBuilds);

	public static void RecordFullSelectionRefresh() => Increment(static state => ref state.FullSelectionRefreshes);

	public static void RecordLiveSelectionRefresh() => Increment(static state => ref state.LiveSelectionRefreshes);

	public static void RecordDynamicSelectionPass() => Increment(static state => ref state.DynamicSelectionPasses);

	public static void RecordWorkspaceScan() => Increment(static state => ref state.WorkspaceScans);

	public static void RecordDirectoryEnumeration() => Increment(static state => ref state.DirectoryEnumerations);

	public static void RecordFileEnumeration() => Increment(static state => ref state.FileEnumerations);

	public static void RecordCombinedEntryEnumeration() =>
		Increment(static state => ref state.CombinedEntryEnumerations);

	public static void RecordGitIgnoreSourceReadRequest() =>
		Increment(static state => ref state.GitIgnoreSourceReadRequests);

	public static void RecordGitIgnoreSourceBytes(long bytes)
	{
		if (bytes <= 0)
			return;

		var state = GetCurrentState();
		if (state is not null)
			Interlocked.Add(ref state.GitIgnoreSourceBytes, bytes);
	}

	public static void RecordGitIgnoreLoadRequest() =>
		Increment(static state => ref state.GitIgnoreLoadRequests);

	public static void RecordGitIgnoreLoadExecution() =>
		Increment(static state => ref state.GitIgnoreLoadExecutions);

	public static void RecordGitIgnoreLoadReuse() =>
		Increment(static state => ref state.GitIgnoreLoadReuses);

	private delegate ref long CounterSelector(MeasurementState state);

	private static void Increment(CounterSelector counterSelector)
	{
		var state = GetCurrentState();
		if (state is not null)
			Interlocked.Increment(ref counterSelector(state));
	}

	private static MeasurementState? GetCurrentState() => IsEnabled ? CurrentState.Value : null;

	private static void CompleteMeasurement(MeasurementState state, MeasurementState? previousState)
	{
		// Restoring the parent makes nested measurements deterministic without leaking
		// benchmark state into later application operations on the same async flow.
		if (ReferenceEquals(CurrentState.Value, state))
			CurrentState.Value = previousState;
		Interlocked.Decrement(ref _activeMeasurements);
	}

	internal sealed class MeasurementState
	{
		public long RootFactsRequests;
		public long RootFactsCacheHits;
		public long RootFactsBuilds;
		public long RootFactsEvictions;
		public long ProjectScopeDiscoveries;
		public long IgnoreRulesBuilds;
		public long FullSelectionRefreshes;
		public long LiveSelectionRefreshes;
		public long DynamicSelectionPasses;
		public long WorkspaceScans;
		public long DirectoryEnumerations;
		public long FileEnumerations;
		public long CombinedEntryEnumerations;
		public long GitIgnoreSourceReadRequests;
		public long GitIgnoreSourceBytes;
		public long GitIgnoreLoadRequests;
		public long GitIgnoreLoadExecutions;
		public long GitIgnoreLoadReuses;

		public IgnorePipelineDiagnosticSnapshot Capture()
		{
			return new IgnorePipelineDiagnosticSnapshot(
				Volatile.Read(ref RootFactsRequests),
				Volatile.Read(ref RootFactsCacheHits),
				Volatile.Read(ref RootFactsBuilds),
				Volatile.Read(ref RootFactsEvictions),
				Volatile.Read(ref ProjectScopeDiscoveries),
				Volatile.Read(ref IgnoreRulesBuilds),
				Volatile.Read(ref FullSelectionRefreshes),
				Volatile.Read(ref LiveSelectionRefreshes),
				Volatile.Read(ref DynamicSelectionPasses),
				Volatile.Read(ref WorkspaceScans),
				Volatile.Read(ref DirectoryEnumerations),
				Volatile.Read(ref FileEnumerations),
				Volatile.Read(ref CombinedEntryEnumerations),
				Volatile.Read(ref GitIgnoreSourceReadRequests),
				Volatile.Read(ref GitIgnoreSourceBytes),
				Volatile.Read(ref GitIgnoreLoadRequests),
				Volatile.Read(ref GitIgnoreLoadExecutions),
				Volatile.Read(ref GitIgnoreLoadReuses));
		}
	}
}

public sealed class IgnorePipelineMeasurement : IDisposable
{
	private readonly IgnorePipelineDiagnostics.MeasurementState _state;
	private readonly IgnorePipelineDiagnostics.MeasurementState? _previousState;
	private readonly Action<IgnorePipelineDiagnostics.MeasurementState, IgnorePipelineDiagnostics.MeasurementState?>
		_complete;
	private int _disposed;

	internal IgnorePipelineMeasurement(
		IgnorePipelineDiagnostics.MeasurementState state,
		IgnorePipelineDiagnostics.MeasurementState? previousState,
		Action<IgnorePipelineDiagnostics.MeasurementState, IgnorePipelineDiagnostics.MeasurementState?> complete)
	{
		_state = state;
		_previousState = previousState;
		_complete = complete;
	}

	public IgnorePipelineDiagnosticSnapshot Capture() => _state.Capture();

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;

		_complete(_state, _previousState);
	}
}

public sealed record IgnorePipelineDiagnosticSnapshot(
	long RootFactsRequests,
	long RootFactsCacheHits,
	long RootFactsBuilds,
	long RootFactsEvictions,
	long ProjectScopeDiscoveries,
	long IgnoreRulesBuilds,
	long FullSelectionRefreshes,
	long LiveSelectionRefreshes,
	long DynamicSelectionPasses,
	long WorkspaceScans,
	long DirectoryEnumerations,
	long FileEnumerations,
	long CombinedEntryEnumerations,
	long GitIgnoreSourceReadRequests,
	long GitIgnoreSourceBytes,
	long GitIgnoreLoadRequests,
	long GitIgnoreLoadExecutions,
	long GitIgnoreLoadReuses)
{
	public static IgnorePipelineDiagnosticSnapshot Empty { get; } = new(
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}
