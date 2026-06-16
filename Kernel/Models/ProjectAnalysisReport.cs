namespace DevProjex.Kernel.Models;

public sealed record ProjectAnalysisReport(
	int SchemaVersion,
	DateTimeOffset GeneratedUtc,
	string RootPath,
	ProjectAnalysisSelectionReport Selection,
	ProjectAnalysisInventoryReport Inventory,
	ProjectAnalysisOutputMetricsReport Metrics,
	ProjectAnalysisTimingReport Timing,
	ProjectAnalysisDiagnosticsReport Diagnostics)
{
	public const int CurrentSchemaVersion = 1;
}

public sealed record ProjectAnalysisSelectionReport(
	IReadOnlyList<string> SelectedRootFolders,
	IReadOnlyList<string> SelectedExtensions,
	IReadOnlyList<IgnoreOptionId> SelectedIgnoreOptions);

public sealed record ProjectAnalysisInventoryReport(
	IReadOnlyList<string> AvailableRootFolders,
	IReadOnlyList<string> AvailableExtensions,
	ProjectTreeSummaryReport Tree);

public sealed record ProjectTreeSummaryReport(
	int DirectoryCount,
	int FileCount,
	int AccessDeniedDirectoryCount);

public sealed record ProjectAnalysisOutputMetricsReport(
	ProjectOutputMetricsReport Tree,
	ProjectOutputMetricsReport Content);

public sealed record ProjectOutputMetricsReport(
	int Lines,
	int Chars,
	int Tokens)
{
	public static ProjectOutputMetricsReport Empty { get; } = new(0, 0, 0);
}

public sealed record ProjectAnalysisTimingReport(
	double LoadingMilliseconds,
	double AnalysisMilliseconds,
	double TotalMilliseconds);

public sealed record ProjectAnalysisDiagnosticsReport(
	bool RootAccessDenied,
	bool HadAccessDenied,
	IReadOnlyList<string> Warnings);
