using DevProjex.Application.Diagnostics;
using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit;

public sealed class CommandLineBenchmarkDiagnosticsTests
{
	[Fact]
	public void DiagnosticSummary_UsesPerCounterMedianAndReportsUnstableStructure()
	{
		var runs = new[]
		{
			CreateRun(1, IgnorePipelineDiagnosticSnapshot.Empty with
			{
				WorkspaceScans = 1,
				GitIgnoreLoadExecutions = 5
			}),
			CreateRun(2, IgnorePipelineDiagnosticSnapshot.Empty with
			{
				WorkspaceScans = 5,
				GitIgnoreLoadExecutions = 1
			}),
			CreateRun(3, IgnorePipelineDiagnosticSnapshot.Empty with
			{
				WorkspaceScans = 3,
				GitIgnoreLoadExecutions = 3
			})
		};

		var summary = CommandLineBenchmarkDiagnosticSummary.FromRuns(runs);

		Assert.Equal(3, summary.Count);
		Assert.False(summary.Consistent);
		Assert.Equal(1, summary.Minimum.WorkspaceScans);
		Assert.Equal(3, summary.Median.WorkspaceScans);
		Assert.Equal(5, summary.Maximum.WorkspaceScans);
		Assert.Equal(3, summary.Median.GitIgnoreLoadExecutions);
	}

	[Fact]
	public void ContentDiagnosticSummary_UsesPerCounterMedian()
	{
		var runs = new[]
		{
			CreateRun(1, IgnorePipelineDiagnosticSnapshot.Empty,
				new ContentPipelineDiagnosticSnapshot(1, 50, 2, 8, 3)),
			CreateRun(2, IgnorePipelineDiagnosticSnapshot.Empty,
				new ContentPipelineDiagnosticSnapshot(5, 10, 6, 4, 7)),
			CreateRun(3, IgnorePipelineDiagnosticSnapshot.Empty,
				new ContentPipelineDiagnosticSnapshot(3, 30, 4, 6, 5))
		};

		var summary = CommandLineBenchmarkContentDiagnosticSummary.FromRuns(runs);

		Assert.Equal(3, summary.Count);
		Assert.False(summary.Consistent);
		Assert.Equal(3, summary.Median.FullFileReads);
		Assert.Equal(30, summary.Median.FullFileReadBytes);
		Assert.Equal(4, summary.Median.ContentFingerprintComputations);
		Assert.Equal(6, summary.Median.PlanApplications);
		Assert.Equal(5, summary.Median.OccurrenceIdComputations);
	}

	private static CommandLineBenchmarkPipelineRun CreateRun(
		int index,
		IgnorePipelineDiagnosticSnapshot diagnostics,
		ContentPipelineDiagnosticSnapshot? contentDiagnostics = null) =>
		new(
			Index: index,
			IsWarmup: false,
			StartedAt: DateTimeOffset.UnixEpoch,
			WallMilliseconds: 1,
			CpuMilliseconds: 1,
			WorkingSetBytes: 1,
			PrivateMemoryBytes: 1,
			ManagedMemoryBeforeBytes: 1,
			ManagedMemoryAfterBytes: 1,
			ManagedMemoryDeltaBytes: 0,
			AllocatedBytes: 1,
			Gen0Collections: 0,
			Gen1Collections: 0,
			Gen2Collections: 0,
			StdoutCharacters: 0,
			StdoutBytes: 0,
			LoadingMilliseconds: 1,
			AnalysisMilliseconds: 1,
			ReportedTotalMilliseconds: 1,
			ExportMilliseconds: 1,
			ExportBytes: 1,
			Workload: null,
			Diagnostics: diagnostics,
			ContentDiagnostics: contentDiagnostics ?? new ContentPipelineDiagnosticSnapshot(0, 0, 0, 0, 0),
			ExitCode: 0,
			Error: null);
}
