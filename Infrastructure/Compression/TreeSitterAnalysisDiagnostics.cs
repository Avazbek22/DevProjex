using System.Diagnostics;
using System.Numerics;

namespace DevProjex.Infrastructure.Compression;

internal sealed class TreeSitterAnalysisDiagnosticsSession : IDisposable
{
	private const int DefaultTopCapacity = 10;
	private readonly PhaseAccumulator[] _phases;
	private readonly FileAccumulator _files;
	private readonly Action<TreeSitterAnalysisDiagnosticsSession> _release;
	private readonly object _sync = new();
	private long _preserveCaptures;
	private long _bodyCaptures;
	private long _commentCaptures;
	private long _originalDeclarations;
	private long _originalDefects;
	private long _originalVisitedNodes;
	private long _rawEdits;
	private long _finalEdits;
	private long _reverseDeclarations;
	private long _reverseDefects;
	private long _reverseVisitedNodes;
	private long _completedFiles;
	private long _cancelledFiles;
	private long _activeFiles;
	private long _droppedLateSamples;
	private TreeSitterAnalysisDiagnosticsSnapshot? _frozenSnapshot;
	private int _disposed;

	internal TreeSitterAnalysisDiagnosticsSession(
		Action<TreeSitterAnalysisDiagnosticsSession> release,
		int topCapacity = DefaultTopCapacity)
	{
		ArgumentNullException.ThrowIfNull(release);
		ArgumentOutOfRangeException.ThrowIfLessThan(topCapacity, 1);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(topCapacity, 64);
		_release = release;
		var phaseCount = Enum.GetValues<TreeSitterAnalysisPhase>().Length;
		_phases = new PhaseAccumulator[phaseCount];
		for (var index = 0; index < _phases.Length; index++)
			_phases[index] = new PhaseAccumulator(topCapacity);
		_files = new FileAccumulator(topCapacity);
	}

	internal TreeSitterFileAnalysisTiming BeginFile(string relativePath, int sourceCharacters)
	{
		lock (_sync)
		{
			if (_disposed != 0)
				return new TreeSitterFileAnalysisTiming(relativePath, sourceCharacters, isRegistered: false);
			_activeFiles++;
			return new TreeSitterFileAnalysisTiming(relativePath, sourceCharacters, isRegistered: true);
		}
	}

	internal long StartPhase() => Stopwatch.GetTimestamp();

	internal void CompletePhase(
		TreeSitterAnalysisPhase phase,
		long startedAt,
		string relativePath,
		int sourceCharacters,
		ref TreeSitterFileAnalysisTiming file)
	{
		var elapsed = Math.Max(0, Stopwatch.GetTimestamp() - startedAt);
		file.RecordPhase(phase, elapsed);
	}

	internal void RecordFile(ref TreeSitterFileAnalysisTiming file)
	{
		if (!file.IsRegistered)
			return;

		lock (_sync)
		{
			_activeFiles--;
			file.IsRegistered = false;
			if (_disposed != 0)
				return;

			if (file.IsCancelled)
			{
				_cancelledFiles++;
				return;
			}

			_completedFiles++;
			RecordCompletedFilePhases(file);
			_preserveCaptures += file.PreserveCaptures;
			_bodyCaptures += file.BodyCaptures;
			_commentCaptures += file.CommentCaptures;
			_originalDeclarations += file.OriginalDeclarations;
			_originalDefects += file.OriginalDefects;
			_originalVisitedNodes += file.OriginalVisitedNodes;
			_rawEdits += file.RawEdits;
			_finalEdits += file.FinalEdits;
			_reverseDeclarations += file.ReverseDeclarations;
			_reverseDefects += file.ReverseDefects;
			_reverseVisitedNodes += file.ReverseVisitedNodes;
			_files.Record(file);
		}
	}

	private void RecordCompletedFilePhases(in TreeSitterFileAnalysisTiming file)
	{
		foreach (var phase in TreeSitterFileAnalysisTiming.ReportedPhases)
		{
			if (!file.HasPhase(phase))
				continue;
			_phases[(int)phase].Record(
				file.GetPhaseTicks(phase),
				file.RelativePath ?? string.Empty,
				file.SourceCharacters);
		}
	}

	internal TreeSitterAnalysisDiagnosticsSnapshot Capture()
	{
		lock (_sync)
		{
			return _frozenSnapshot ?? CaptureLocked();
		}
	}

	private TreeSitterAnalysisDiagnosticsSnapshot CaptureLocked()
	{
		var phases = new TreeSitterAnalysisPhaseSnapshot[_phases.Length];
		for (var index = 0; index < phases.Length; index++)
			phases[index] = _phases[index].Capture((TreeSitterAnalysisPhase)index);

		return new TreeSitterAnalysisDiagnosticsSnapshot(
			phases,
			_files.Capture(),
			_completedFiles,
			_cancelledFiles,
			_droppedLateSamples,
			new TreeSitterAnalysisWorkSnapshot(
				_preserveCaptures,
				_bodyCaptures,
				_commentCaptures,
				_originalDeclarations,
				_originalDefects,
				_originalVisitedNodes,
				_rawEdits,
				_finalEdits,
				_reverseDeclarations,
				_reverseDefects,
				_reverseVisitedNodes));
	}

	public void Dispose()
	{
		lock (_sync)
		{
			if (_disposed != 0)
				return;
			_disposed = 1;
			_droppedLateSamples = _activeFiles;
			_frozenSnapshot = CaptureLocked();
		}
		_release(this);
	}

	private sealed class FileAccumulator
	{
		private readonly TreeSitterFileAnalysisTiming[] _top;
		private long _thresholdTicks;
		private int _count;

		public FileAccumulator(int capacity)
		{
			_top = new TreeSitterFileAnalysisTiming[capacity];
		}

		public void Record(TreeSitterFileAnalysisTiming file)
		{
			var elapsedTicks = file.TotalAnalyzeTicks;
			if (elapsedTicks == 0)
				return;
			if (_count == _top.Length && elapsedTicks < _thresholdTicks)
			{
				return;
			}

			if (_count == _top.Length && elapsedTicks < _top[^1].TotalAnalyzeTicks)
				return;

			var insertAt = 0;
			while (insertAt < _count && ComesBefore(_top[insertAt], file))
				insertAt++;
			if (insertAt >= _top.Length)
				return;

			var copyCount = Math.Min(_count, _top.Length - 1) - insertAt;
			if (copyCount > 0)
				Array.Copy(_top, insertAt, _top, insertAt + 1, copyCount);
			_top[insertAt] = file;
			if (_count < _top.Length)
				_count++;
			_thresholdTicks = _top[_count - 1].TotalAnalyzeTicks;
		}

		public IReadOnlyList<TreeSitterFileAnalysisSnapshot> Capture()
		{
			var result = new TreeSitterFileAnalysisSnapshot[_count];
			for (var index = 0; index < result.Length; index++)
				result[index] = _top[index].ToSnapshot();
			return result;
		}

		private static bool ComesBefore(
			in TreeSitterFileAnalysisTiming existing,
			in TreeSitterFileAnalysisTiming candidate) =>
			existing.TotalAnalyzeTicks > candidate.TotalAnalyzeTicks ||
			(existing.TotalAnalyzeTicks == candidate.TotalAnalyzeTicks &&
			 string.CompareOrdinal(existing.RelativePath, candidate.RelativePath) <= 0);
	}

	private sealed class PhaseAccumulator
	{
		private const int ExactBucketCount = 8;
		private const int SubBucketsPerPowerOfTwo = 8;
		private const int HistogramBucketCount = 488;
		private readonly long[] _histogram = new long[HistogramBucketCount];
		private readonly TopSample[] _top;
		private long _count;
		private long _totalTicks;
		private long _maximumTicks;
		private long _topThresholdTicks;
		private int _topCount;

		public PhaseAccumulator(int topCapacity)
		{
			_top = new TopSample[topCapacity];
		}

		public void Record(long elapsedTicks, string relativePath, int sourceCharacters)
		{
			_count++;
			_totalTicks += elapsedTicks;
			_maximumTicks = Math.Max(_maximumTicks, elapsedTicks);
			_histogram[ToBucketIndex(elapsedTicks)]++;

			if (_topCount == _top.Length && elapsedTicks < _topThresholdTicks)
			{
				return;
			}

			if (_topCount == _top.Length && elapsedTicks < _top[^1].ElapsedTicks)
				return;

			var insertAt = 0;
			while (insertAt < _topCount && ComesBefore(_top[insertAt], elapsedTicks, relativePath))
				insertAt++;
			if (insertAt >= _top.Length)
				return;

			var copyCount = Math.Min(_topCount, _top.Length - 1) - insertAt;
			if (copyCount > 0)
				Array.Copy(_top, insertAt, _top, insertAt + 1, copyCount);
			_top[insertAt] = new TopSample(relativePath, sourceCharacters, elapsedTicks);
			if (_topCount < _top.Length)
				_topCount++;
			_topThresholdTicks = _top[_topCount - 1].ElapsedTicks;
		}

		public TreeSitterAnalysisPhaseSnapshot Capture(TreeSitterAnalysisPhase phase)
		{
			var count = _count;
			var totalTicks = _totalTicks;
			var top = new TopSample[_topCount];
			Array.Copy(_top, top, _topCount);

			return new TreeSitterAnalysisPhaseSnapshot(
				phase,
				count,
				ToMilliseconds(totalTicks),
				count == 0 ? 0 : ToMilliseconds(totalTicks) / count,
				QuantileMilliseconds(count, 0.50),
				QuantileMilliseconds(count, 0.95),
				QuantileMilliseconds(count, 0.99),
				ToMilliseconds(_maximumTicks),
				top.Select(static sample => new TreeSitterAnalysisTopSample(
					sample.RelativePath,
					sample.SourceCharacters,
					ToMilliseconds(sample.ElapsedTicks))).ToArray());
		}

		private double QuantileMilliseconds(long count, double quantile)
		{
			if (count == 0)
				return 0;
			var target = Math.Max(1L, (long)Math.Ceiling(count * quantile));
			long observed = 0;
			for (var index = 0; index < _histogram.Length; index++)
			{
				observed += _histogram[index];
				if (observed >= target)
					return ToMilliseconds(Math.Min(
						BucketUpperBound(index),
						_maximumTicks));
			}
			return ToMilliseconds(_maximumTicks);
		}

		private static int ToBucketIndex(long ticks)
		{
			if (ticks <= 0)
				return 0;
			if (ticks < ExactBucketCount)
				return (int)ticks;

			var value = (ulong)ticks;
			var exponent = BitOperations.Log2(value);
			var lowerBound = 1UL << exponent;
			var subBucket = (int)((value - lowerBound) / (lowerBound / SubBucketsPerPowerOfTwo));
			return Math.Min(
				HistogramBucketCount - 1,
				ExactBucketCount + (exponent - 3) * SubBucketsPerPowerOfTwo + subBucket);
		}

		private static long BucketUpperBound(int index)
		{
			if (index < ExactBucketCount)
				return index;
			var relative = index - ExactBucketCount;
			var exponent = relative / SubBucketsPerPowerOfTwo + 3;
			var subBucket = relative % SubBucketsPerPowerOfTwo;
			var lowerBound = 1UL << exponent;
			var upperBound = lowerBound +
			                 (ulong)(subBucket + 1) * (lowerBound / SubBucketsPerPowerOfTwo) - 1;
			return upperBound > long.MaxValue ? long.MaxValue : (long)upperBound;
		}

		private static bool ComesBefore(TopSample existing, long elapsedTicks, string relativePath) =>
			existing.ElapsedTicks > elapsedTicks ||
			(existing.ElapsedTicks == elapsedTicks &&
			 string.CompareOrdinal(existing.RelativePath, relativePath) <= 0);

		private static double ToMilliseconds(long ticks) =>
			ticks * 1000d / Stopwatch.Frequency;

		private readonly record struct TopSample(
			string RelativePath,
			int SourceCharacters,
			long ElapsedTicks);
	}
}

internal struct TreeSitterFileAnalysisTiming
{
	internal static readonly TreeSitterAnalysisPhase[] ReportedPhases =
	[
		TreeSitterAnalysisPhase.OriginalParse,
		TreeSitterAnalysisPhase.PreserveQuery,
		TreeSitterAnalysisPhase.BodyQuery,
		TreeSitterAnalysisPhase.CommentQuery,
		TreeSitterAnalysisPhase.OriginalDeclarations,
		TreeSitterAnalysisPhase.OriginalDefectWalk,
		TreeSitterAnalysisPhase.EditShaping,
		TreeSitterAnalysisPhase.PlanBuild,
		TreeSitterAnalysisPhase.PlanApply,
		TreeSitterAnalysisPhase.ReverseParse,
		TreeSitterAnalysisPhase.ReverseDeclarations,
		TreeSitterAnalysisPhase.ReverseDefectWalk,
		TreeSitterAnalysisPhase.StructureGate
	];

	private uint _completedPhaseMask;

	internal TreeSitterFileAnalysisTiming(
		string relativePath,
		int sourceCharacters,
		bool isRegistered = false)
	{
		RelativePath = relativePath;
		SourceCharacters = sourceCharacters;
		IsRegistered = isRegistered;
	}

	internal string? RelativePath { get; }
	internal int SourceCharacters { get; }
	internal bool IsCancelled { get; set; }
	internal bool IsRegistered { get; set; }
	internal int PreserveCaptures { get; set; }
	internal int BodyCaptures { get; set; }
	internal int CommentCaptures { get; set; }
	internal int OriginalDeclarations { get; set; }
	internal int OriginalDefects { get; set; }
	internal int OriginalVisitedNodes { get; set; }
	internal int RawEdits { get; set; }
	internal int FinalEdits { get; set; }
	internal int ReverseDeclarations { get; set; }
	internal int ReverseDefects { get; set; }
	internal int ReverseVisitedNodes { get; set; }
	internal long OriginalParseTicks { get; private set; }
	internal long PreserveQueryTicks { get; private set; }
	internal long BodyQueryTicks { get; private set; }
	internal long CommentQueryTicks { get; private set; }
	internal long OriginalDeclarationsTicks { get; private set; }
	internal long OriginalDefectWalkTicks { get; private set; }
	internal long EditShapingTicks { get; private set; }
	internal long PlanBuildTicks { get; private set; }
	internal long PlanApplyTicks { get; private set; }
	internal long ReverseParseTicks { get; private set; }
	internal long ReverseDeclarationsTicks { get; private set; }
	internal long ReverseDefectWalkTicks { get; private set; }
	internal long StructureGateTicks { get; private set; }

	internal long TotalAnalyzeTicks =>
		OriginalParseTicks +
		PreserveQueryTicks +
		BodyQueryTicks +
		CommentQueryTicks +
		OriginalDeclarationsTicks +
		OriginalDefectWalkTicks +
		EditShapingTicks +
		PlanBuildTicks +
		PlanApplyTicks +
		ReverseParseTicks +
		ReverseDeclarationsTicks +
		ReverseDefectWalkTicks +
		StructureGateTicks;

	internal void RecordPhase(TreeSitterAnalysisPhase phase, long ticks)
	{
		switch (phase)
		{
			case TreeSitterAnalysisPhase.OriginalParse:
				OriginalParseTicks += ticks;
				break;
			case TreeSitterAnalysisPhase.PreserveQuery:
				PreserveQueryTicks += ticks;
				break;
			case TreeSitterAnalysisPhase.BodyQuery:
				BodyQueryTicks += ticks;
				break;
			case TreeSitterAnalysisPhase.CommentQuery:
				CommentQueryTicks += ticks;
				break;
			case TreeSitterAnalysisPhase.OriginalDeclarations:
				OriginalDeclarationsTicks += ticks;
				break;
			case TreeSitterAnalysisPhase.OriginalDefectWalk:
				OriginalDefectWalkTicks += ticks;
				break;
			case TreeSitterAnalysisPhase.EditShaping:
				EditShapingTicks += ticks;
				break;
			case TreeSitterAnalysisPhase.PlanBuild:
				PlanBuildTicks += ticks;
				break;
			case TreeSitterAnalysisPhase.PlanApply:
				PlanApplyTicks += ticks;
				break;
			case TreeSitterAnalysisPhase.ReverseParse:
				ReverseParseTicks += ticks;
				break;
			case TreeSitterAnalysisPhase.ReverseDeclarations:
				ReverseDeclarationsTicks += ticks;
				break;
			case TreeSitterAnalysisPhase.ReverseDefectWalk:
				ReverseDefectWalkTicks += ticks;
				break;
			case TreeSitterAnalysisPhase.StructureGate:
				StructureGateTicks += ticks;
				break;
			default:
				return;
		}

		_completedPhaseMask |= 1U << (int)phase;
	}

	internal readonly bool HasPhase(TreeSitterAnalysisPhase phase) =>
		(_completedPhaseMask & (1U << (int)phase)) != 0;

	internal readonly long GetPhaseTicks(TreeSitterAnalysisPhase phase) =>
		phase switch
		{
			TreeSitterAnalysisPhase.OriginalParse => OriginalParseTicks,
			TreeSitterAnalysisPhase.PreserveQuery => PreserveQueryTicks,
			TreeSitterAnalysisPhase.BodyQuery => BodyQueryTicks,
			TreeSitterAnalysisPhase.CommentQuery => CommentQueryTicks,
			TreeSitterAnalysisPhase.OriginalDeclarations => OriginalDeclarationsTicks,
			TreeSitterAnalysisPhase.OriginalDefectWalk => OriginalDefectWalkTicks,
			TreeSitterAnalysisPhase.EditShaping => EditShapingTicks,
			TreeSitterAnalysisPhase.PlanBuild => PlanBuildTicks,
			TreeSitterAnalysisPhase.PlanApply => PlanApplyTicks,
			TreeSitterAnalysisPhase.ReverseParse => ReverseParseTicks,
			TreeSitterAnalysisPhase.ReverseDeclarations => ReverseDeclarationsTicks,
			TreeSitterAnalysisPhase.ReverseDefectWalk => ReverseDefectWalkTicks,
			TreeSitterAnalysisPhase.StructureGate => StructureGateTicks,
			_ => 0
		};

	internal TreeSitterFileAnalysisSnapshot ToSnapshot() =>
		new(
			RelativePath ?? string.Empty,
			SourceCharacters,
			ToMilliseconds(TotalAnalyzeTicks),
			[
				Phase(TreeSitterAnalysisPhase.OriginalParse, OriginalParseTicks),
				Phase(TreeSitterAnalysisPhase.PreserveQuery, PreserveQueryTicks),
				Phase(TreeSitterAnalysisPhase.BodyQuery, BodyQueryTicks),
				Phase(TreeSitterAnalysisPhase.CommentQuery, CommentQueryTicks),
				Phase(TreeSitterAnalysisPhase.OriginalDeclarations, OriginalDeclarationsTicks),
				Phase(TreeSitterAnalysisPhase.OriginalDefectWalk, OriginalDefectWalkTicks),
				Phase(TreeSitterAnalysisPhase.EditShaping, EditShapingTicks),
				Phase(TreeSitterAnalysisPhase.PlanBuild, PlanBuildTicks),
				Phase(TreeSitterAnalysisPhase.PlanApply, PlanApplyTicks),
				Phase(TreeSitterAnalysisPhase.ReverseParse, ReverseParseTicks),
				Phase(TreeSitterAnalysisPhase.ReverseDeclarations, ReverseDeclarationsTicks),
				Phase(TreeSitterAnalysisPhase.ReverseDefectWalk, ReverseDefectWalkTicks),
				Phase(TreeSitterAnalysisPhase.StructureGate, StructureGateTicks)
			],
			new TreeSitterAnalysisWorkSnapshot(
				PreserveCaptures,
				BodyCaptures,
				CommentCaptures,
				OriginalDeclarations,
				OriginalDefects,
				OriginalVisitedNodes,
				RawEdits,
				FinalEdits,
				ReverseDeclarations,
				ReverseDefects,
				ReverseVisitedNodes));

	private static TreeSitterFilePhaseTiming Phase(TreeSitterAnalysisPhase phase, long ticks) =>
		new(phase, ToMilliseconds(ticks));

	private static double ToMilliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;
}

internal sealed record TreeSitterAnalysisDiagnosticsSnapshot(
	IReadOnlyList<TreeSitterAnalysisPhaseSnapshot> Phases,
	IReadOnlyList<TreeSitterFileAnalysisSnapshot> SlowestFiles,
	long CompletedFiles,
	long CancelledFiles,
	long DroppedLateSamples,
	TreeSitterAnalysisWorkSnapshot Work);

internal sealed record TreeSitterAnalysisPhaseSnapshot(
	TreeSitterAnalysisPhase Phase,
	long Count,
	double TotalMilliseconds,
	double MeanMilliseconds,
	double P50Milliseconds,
	double P95Milliseconds,
	double P99Milliseconds,
	double MaximumMilliseconds,
	IReadOnlyList<TreeSitterAnalysisTopSample> Top);

internal sealed record TreeSitterAnalysisTopSample(
	string RelativePath,
	int SourceCharacters,
	double ElapsedMilliseconds);

internal sealed record TreeSitterFileAnalysisSnapshot(
	string RelativePath,
	int SourceCharacters,
	double TotalMilliseconds,
	IReadOnlyList<TreeSitterFilePhaseTiming> Phases,
	TreeSitterAnalysisWorkSnapshot Work);

internal sealed record TreeSitterFilePhaseTiming(
	TreeSitterAnalysisPhase Phase,
	double ElapsedMilliseconds);

internal sealed record TreeSitterAnalysisWorkSnapshot(
	long PreserveCaptures,
	long BodyCaptures,
	long CommentCaptures,
	long OriginalDeclarations,
	long OriginalDefects,
	long OriginalVisitedNodes,
	long RawEdits,
	long FinalEdits,
	long ReverseDeclarations,
	long ReverseDefects,
	long ReverseVisitedNodes);
