namespace DevProjex.Application.Dependencies;

internal static class DependencyEngineDiagnostics
{
	private static readonly AsyncLocal<State?> Current = new();
	private static int _activeMeasurements;

	public static bool IsEnabled => Volatile.Read(ref _activeMeasurements) != 0;

	public static Measurement BeginMeasurement()
	{
		var state = new State();
		var previous = Current.Value;
		Current.Value = state;
		Interlocked.Increment(ref _activeMeasurements);
		return new Measurement(state, previous);
	}

	public static void RecordPathNormalization() => Increment(static state => ref state.PathNormalizations);
	public static void RecordManifestSort() => Increment(static state => ref state.ManifestSorts);
	public static void RecordDictionaryBuild() => Increment(static state => ref state.DictionaryBuilds);
	public static void RecordFileFactsClone() => Increment(static state => ref state.FileFactsClones);
	public static void RecordGraphBuild() => Increment(static state => ref state.GraphBuilds);
	public static void RecordResolverCandidateProbes(int count) => Add(static state => ref state.ResolverCandidateProbes, count);
	public static void RecordFileCacheHit() => Increment(static state => ref state.FileCacheHits);
	public static void RecordResolutionCacheHit() => Increment(static state => ref state.ResolutionCacheHits);

	private delegate ref long Counter(State state);

	private static void Increment(Counter counter) => Add(counter, 1);

	private static void Add(Counter counter, long value)
	{
		if (!IsEnabled || value == 0 || Current.Value is not { } state)
			return;
		Interlocked.Add(ref counter(state), value);
	}

	internal sealed class State
	{
		public long PathNormalizations;
		public long ManifestSorts;
		public long DictionaryBuilds;
		public long FileFactsClones;
		public long GraphBuilds;
		public long ResolverCandidateProbes;
		public long FileCacheHits;
		public long ResolutionCacheHits;

		public DependencyEngineDiagnosticSnapshot Capture() => new(
			Volatile.Read(ref PathNormalizations),
			Volatile.Read(ref ManifestSorts),
			Volatile.Read(ref DictionaryBuilds),
			Volatile.Read(ref FileFactsClones),
			Volatile.Read(ref GraphBuilds),
			Volatile.Read(ref ResolverCandidateProbes),
			Volatile.Read(ref FileCacheHits),
			Volatile.Read(ref ResolutionCacheHits));
	}

	internal sealed class Measurement(State state, State? previous) : IDisposable
	{
		private int _disposed;

		public DependencyEngineDiagnosticSnapshot Capture() => state.Capture();

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) != 0)
				return;
			if (ReferenceEquals(Current.Value, state))
				Current.Value = previous;
			Interlocked.Decrement(ref _activeMeasurements);
		}
	}
}

internal readonly record struct DependencyEngineDiagnosticSnapshot(
	long PathNormalizations,
	long ManifestSorts,
	long DictionaryBuilds,
	long FileFactsClones,
	long GraphBuilds,
	long ResolverCandidateProbes,
	long FileCacheHits,
	long ResolutionCacheHits);
