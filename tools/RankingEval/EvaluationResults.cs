namespace DevProjex.RankingEval;

public sealed record EvaluationRunResult(
	string Protocol,
	string RunDate,
	string ProductSha,
	string EvaluatorSha,
	IReadOnlyList<RepositoryEvaluationResult> Repositories,
	ReleaseCriterionResult Criterion);

public sealed record RepositoryEvaluationResult(
	string Id,
	string Commit,
	int CandidateFiles,
	IReadOnlyList<EvaluationCellResult> Cells,
	IReadOnlyList<PerformanceResult> Performance);

public sealed record EvaluationCellResult(
	string Task,
	long Budget,
	IReadOnlyDictionary<string, OrderEvaluationResult> Orders);

public sealed record OrderEvaluationResult(
	bool Available,
	double? RecallNew,
	bool? AllRequired,
	double? IrrelevantTokenShare,
	bool Oracle,
	bool OracleAfterSeeds);

public sealed record PerformanceResult(
	string Mode,
	string Order,
	int Repetitions,
	double MedianElapsedMilliseconds,
	long MedianPeakWorkingSetBytes,
	IReadOnlyList<double> ElapsedMilliseconds,
	IReadOnlyList<long> PeakWorkingSetBytes);

public sealed record ReleaseCriterionResult(
	int EligibleCells,
	double OverallRecallNewDelta,
	IReadOnlyDictionary<string, double> RepositoryRecallNewDelta,
	int AllRequiredRegressions,
	IReadOnlyDictionary<string, double> WarmIncrementalMilliseconds,
	IReadOnlyDictionary<string, double> WarmIncrementalPercent,
	IReadOnlyDictionary<string, double> MaximumPeakWorkingSetGrowthPercent,
	bool RecallPassed,
	bool AllRequiredPassed,
	bool TimePassed,
	bool MemoryPassed,
	bool Passed);

internal sealed record ContentCatalog(
	IReadOnlyList<string> CurrentOrder,
	IReadOnlyDictionary<string, int> CharacterCounts,
	IReadOnlyDictionary<string, long> TokenCosts);

internal sealed record MeasureOneResult(double ElapsedMilliseconds, long PeakWorkingSetBytes);
