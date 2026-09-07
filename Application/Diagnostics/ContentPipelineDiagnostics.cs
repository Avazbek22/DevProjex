using System.Diagnostics;
using System.Diagnostics.Tracing;

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

	public static ContentPipelineStageMeasurement MeasureStage(ContentPipelineStage stage)
	{
		var state = GetCurrentState();
		var emitEvent = ContentPipelineEventSource.Log.IsEnabled();
		return state is null && !emitEvent
			? default
			: new ContentPipelineStageMeasurement(state, stage, Stopwatch.GetTimestamp(), emitEvent);
	}

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

	public static void RecordSourceRead(long bytes)
	{
		RecordBytes(static state => ref state.SourceReadBytes, bytes);
		ContentPipelineEventSource.Log.FileIo("source-read", bytes);
	}

	public static void RecordPreparedRead(long bytes)
	{
		RecordBytes(static state => ref state.PreparedReadBytes, bytes);
		ContentPipelineEventSource.Log.FileIo("prepared-read", bytes);
	}

	public static void RecordPreparedWrite(long bytes)
	{
		RecordBytes(static state => ref state.PreparedWriteBytes, bytes);
		ContentPipelineEventSource.Log.FileIo("prepared-write", bytes);
	}

	public static void RecordQueueWait(long elapsedStopwatchTicks)
	{
		if (elapsedStopwatchTicks <= 0)
			return;
		var state = GetCurrentState();
		if (state is not null)
			Interlocked.Add(ref state.QueueWaitStopwatchTicks, elapsedStopwatchTicks);
		ContentPipelineEventSource.Log.QueueWait(ToTimeSpanTicks(elapsedStopwatchTicks));
	}

	public static void RecordByteBudgetWait(long elapsedStopwatchTicks)
	{
		if (elapsedStopwatchTicks <= 0)
			return;
		var state = GetCurrentState();
		if (state is not null)
			Interlocked.Add(ref state.ByteBudgetWaitStopwatchTicks, elapsedStopwatchTicks);
		ContentPipelineEventSource.Log.ByteBudgetWait(ToTimeSpanTicks(elapsedStopwatchTicks));
	}

	public static void RecordByteBudgetLease(long bytes)
	{
		if (bytes <= 0)
			return;
		var state = GetCurrentState();
		if (state is not null)
		{
			Interlocked.Add(ref state.ByteBudgetRequestedBytes, bytes);
			var current = Interlocked.Add(ref state.InFlightBytes, bytes);
			UpdateMaximum(ref state.PeakInFlightBytes, current);
		}
		ContentPipelineEventSource.Log.ByteBudgetLease(bytes);
	}

	public static void RecordByteBudgetRelease(long bytes)
	{
		if (bytes <= 0)
			return;
		var state = GetCurrentState();
		if (state is not null)
			Interlocked.Add(ref state.InFlightBytes, -bytes);
	}

	public static void RecordContentFingerprint() =>
		Increment(static state => ref state.ContentFingerprintComputations);

	public static void RecordPlanApply() =>
		Increment(static state => ref state.PlanApplications);

	public static void RecordOccurrenceIdComputation() =>
		Increment(static state => ref state.OccurrenceIdComputations);

	private delegate ref long CounterSelector(MeasurementState state);
	private delegate ref long ByteCounterSelector(MeasurementState state);

	private static void Increment(CounterSelector selector)
	{
		var state = GetCurrentState();
		if (state is not null)
			Interlocked.Increment(ref selector(state));
	}

	private static void RecordBytes(ByteCounterSelector selector, long bytes)
	{
		if (bytes <= 0)
			return;
		var state = GetCurrentState();
		if (state is not null)
			Interlocked.Add(ref selector(state), bytes);
	}

	private static void UpdateMaximum(ref long target, long candidate)
	{
		var current = Volatile.Read(ref target);
		while (candidate > current)
		{
			var observed = Interlocked.CompareExchange(ref target, candidate, current);
			if (observed == current)
				return;
			current = observed;
		}
	}

	internal static long ToTimeSpanTicks(long stopwatchTicks) =>
		(long)(stopwatchTicks * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency));

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
		private readonly TimeSpan _initialCpuTime;
		private readonly long _initialAllocatedBytes;
		private readonly TimeSpan _initialGcPauseTime;

		public MeasurementState()
		{
			using var process = Process.GetCurrentProcess();
			_initialCpuTime = process.TotalProcessorTime;
			_initialAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
			_initialGcPauseTime = GC.GetTotalPauseDuration();
		}

		public long FullFileReads;
		public long FullFileReadBytes;
		public long ContentFingerprintComputations;
		public long PlanApplications;
		public long OccurrenceIdComputations;
		public long SourceReadBytes;
		public long PreparedReadBytes;
		public long PreparedWriteBytes;
		public long QueueWaitStopwatchTicks;
		public long ByteBudgetWaitStopwatchTicks;
		public long ByteBudgetRequestedBytes;
		public long InFlightBytes;
		public long PeakInFlightBytes;
		public long[] StageStopwatchTicks { get; } = new long[Enum.GetValues<ContentPipelineStage>().Length];
		public long[] StageInvocationCounts { get; } = new long[Enum.GetValues<ContentPipelineStage>().Length];

		public ContentPipelineDiagnosticSnapshot Capture()
		{
			using var process = Process.GetCurrentProcess();
			var stages = Enum.GetValues<ContentPipelineStage>().ToDictionary(
				static stage => stage,
				stage => new ContentPipelineStageDiagnosticSnapshot(
					ToTimeSpanTicks(Volatile.Read(ref StageStopwatchTicks[(int)stage])),
					Volatile.Read(ref StageInvocationCounts[(int)stage])));
			return new ContentPipelineDiagnosticSnapshot(
				Volatile.Read(ref FullFileReads),
				Volatile.Read(ref FullFileReadBytes),
				Volatile.Read(ref ContentFingerprintComputations),
				Volatile.Read(ref PlanApplications),
				Volatile.Read(ref OccurrenceIdComputations))
			{
				Stages = stages,
				CpuTimeTicks = Math.Max(0, (process.TotalProcessorTime - _initialCpuTime).Ticks),
				AllocatedBytes = Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - _initialAllocatedBytes),
				GcPauseTimeTicks = Math.Max(0, (GC.GetTotalPauseDuration() - _initialGcPauseTime).Ticks),
				PeakWorkingSetBytes = Math.Max(process.PeakWorkingSet64, process.WorkingSet64),
				PeakInFlightBytes = Volatile.Read(ref PeakInFlightBytes),
				SourceReadBytes = Volatile.Read(ref SourceReadBytes),
				PreparedReadBytes = Volatile.Read(ref PreparedReadBytes),
				PreparedWriteBytes = Volatile.Read(ref PreparedWriteBytes),
				QueueWaitTimeTicks = ToTimeSpanTicks(Volatile.Read(ref QueueWaitStopwatchTicks)),
				ByteBudgetWaitTimeTicks = ToTimeSpanTicks(Volatile.Read(ref ByteBudgetWaitStopwatchTicks)),
				ByteBudgetRequestedBytes = Volatile.Read(ref ByteBudgetRequestedBytes)
			};
		}
	}
}

public enum ContentPipelineStage : byte
{
	Selection,
	SourceRead,
	Compression,
	DetectorInitialization,
	Detection,
	RedactionAndOutput,
	Cleanup
}

public readonly struct ContentPipelineStageMeasurement : IDisposable
{
	private readonly ContentPipelineDiagnostics.MeasurementState? _state;
	private readonly ContentPipelineStage _stage;
	private readonly long _started;
	private readonly bool _emitEvent;

	internal ContentPipelineStageMeasurement(
		ContentPipelineDiagnostics.MeasurementState? state,
		ContentPipelineStage stage,
		long started,
		bool emitEvent)
	{
		_state = state;
		_stage = stage;
		_started = started;
		_emitEvent = emitEvent;
	}

	public void Dispose()
	{
		if (_started == 0)
			return;
		var elapsed = Stopwatch.GetTimestamp() - _started;
		if (_state is not null)
		{
			Interlocked.Add(ref _state.StageStopwatchTicks[(int)_stage], elapsed);
			Interlocked.Increment(ref _state.StageInvocationCounts[(int)_stage]);
		}
		if (_emitEvent)
			ContentPipelineEventSource.Log.StageElapsed(_stage.ToString(), ContentPipelineDiagnostics.ToTimeSpanTicks(elapsed));
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
	long PlanApplications,
	long OccurrenceIdComputations)
{
	public IReadOnlyDictionary<ContentPipelineStage, ContentPipelineStageDiagnosticSnapshot> Stages { get; init; } =
		new Dictionary<ContentPipelineStage, ContentPipelineStageDiagnosticSnapshot>();
	public long CpuTimeTicks { get; init; }
	public long AllocatedBytes { get; init; }
	public long GcPauseTimeTicks { get; init; }
	public long PeakWorkingSetBytes { get; init; }
	public long PeakInFlightBytes { get; init; }
	public long SourceReadBytes { get; init; }
	public long PreparedReadBytes { get; init; }
	public long PreparedWriteBytes { get; init; }
	public long QueueWaitTimeTicks { get; init; }
	public long ByteBudgetWaitTimeTicks { get; init; }
	public long ByteBudgetRequestedBytes { get; init; }
}

public readonly record struct ContentPipelineStageDiagnosticSnapshot(long ElapsedTicks, long InvocationCount);

[EventSource(Name = "DevProjex-ContentPipeline")]
internal sealed class ContentPipelineEventSource : EventSource
{
	internal static readonly ContentPipelineEventSource Log = new();

	[Event(1, Level = EventLevel.Informational)]
	public void StageElapsed(string stage, long elapsedTicks)
	{
		if (IsEnabled()) WriteEvent(1, stage, elapsedTicks);
	}

	[Event(2, Level = EventLevel.Verbose)]
	public void FileIo(string kind, long bytes)
	{
		if (IsEnabled()) WriteEvent(2, kind, bytes);
	}

	[Event(3, Level = EventLevel.Verbose)]
	public void QueueWait(long elapsedTicks)
	{
		if (IsEnabled()) WriteEvent(3, elapsedTicks);
	}

	[Event(4, Level = EventLevel.Verbose)]
	public void ByteBudgetWait(long elapsedTicks)
	{
		if (IsEnabled()) WriteEvent(4, elapsedTicks);
	}

	[Event(5, Level = EventLevel.Verbose)]
	public void ByteBudgetLease(long bytes)
	{
		if (IsEnabled()) WriteEvent(5, bytes);
	}
}
