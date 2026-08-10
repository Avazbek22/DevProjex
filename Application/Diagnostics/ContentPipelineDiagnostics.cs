namespace DevProjex.Application.Diagnostics;

/// <summary>
/// Opt-in counters for the compression benchmark and regression tests. Production calls pay only
/// the disabled guard; measurement state follows the async operation into bounded worker tasks.
/// </summary>
public static class ContentPipelineDiagnostics
{
	private static readonly AsyncLocal<MeasurementState?> CurrentState = new();
	private static int _activeMeasurements;

	public static bool IsEnabled => Volatile.Read(ref _activeMeasurements) != 0;

	public static ContentPipelineMeasurement BeginMeasurement()
	{
		var state = new MeasurementState();
		var previous = CurrentState.Value;
		CurrentState.Value = state;
		Interlocked.Increment(ref _activeMeasurements);
		return new ContentPipelineMeasurement(state, previous, CompleteMeasurement);
	}

	public static void RecordFullFileRead(long bytes)
	{
		var state = GetCurrentState();
		if (state is null)
			return;

		Interlocked.Increment(ref state.FullFileReads);
		if (bytes > 0)
			Interlocked.Add(ref state.FullFileReadBytes, bytes);
	}

	public static void RecordContentFingerprint() =>
		Increment(static state => ref state.ContentFingerprintComputations);

	public static void RecordPlanApply() =>
		Increment(static state => ref state.PlanApplications);

	private delegate ref long CounterSelector(MeasurementState state);

	private static void Increment(CounterSelector selector)
	{
		var state = GetCurrentState();
		if (state is not null)
			Interlocked.Increment(ref selector(state));
	}

	private static MeasurementState? GetCurrentState() =>
		IsEnabled ? CurrentState.Value : null;

	private static void CompleteMeasurement(MeasurementState state, MeasurementState? previous)
	{
		if (ReferenceEquals(CurrentState.Value, state))
			CurrentState.Value = previous;
		Interlocked.Decrement(ref _activeMeasurements);
	}

	internal sealed class MeasurementState
	{
		public long FullFileReads;
		public long FullFileReadBytes;
		public long ContentFingerprintComputations;
		public long PlanApplications;

		public ContentPipelineDiagnosticSnapshot Capture() => new(
			Volatile.Read(ref FullFileReads),
			Volatile.Read(ref FullFileReadBytes),
			Volatile.Read(ref ContentFingerprintComputations),
			Volatile.Read(ref PlanApplications));
	}
}

public sealed class ContentPipelineMeasurement : IDisposable
{
	private readonly ContentPipelineDiagnostics.MeasurementState _state;
	private readonly ContentPipelineDiagnostics.MeasurementState? _previous;
	private readonly Action<ContentPipelineDiagnostics.MeasurementState, ContentPipelineDiagnostics.MeasurementState?>
		_complete;
	private int _disposed;

	internal ContentPipelineMeasurement(
		ContentPipelineDiagnostics.MeasurementState state,
		ContentPipelineDiagnostics.MeasurementState? previous,
		Action<ContentPipelineDiagnostics.MeasurementState, ContentPipelineDiagnostics.MeasurementState?> complete)
	{
		_state = state;
		_previous = previous;
		_complete = complete;
	}

	public ContentPipelineDiagnosticSnapshot Capture() => _state.Capture();

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 0)
			_complete(_state, _previous);
	}
}

public sealed record ContentPipelineDiagnosticSnapshot(
	long FullFileReads,
	long FullFileReadBytes,
	long ContentFingerprintComputations,
	long PlanApplications);
