using System.Diagnostics;
using DevProjex.Application.Diagnostics;

namespace DevProjex.Tests.Unit;

public sealed class ContentPipelineDiagnosticsTests
{
	[Fact]
	public void Measurement_ReportsStagesIoRuntimeAndBoundedWork()
	{
		using var measurement = ContentPipelineDiagnostics.BeginMeasurement();
		using (ContentPipelineDiagnostics.MeasureStage(ContentPipelineStage.Detection))
			Thread.SpinWait(10_000);

		ContentPipelineDiagnostics.RecordSourceRead(100);
		ContentPipelineDiagnostics.RecordPreparedRead(70);
		ContentPipelineDiagnostics.RecordPreparedWrite(80);
		ContentPipelineDiagnostics.RecordQueueWait(Stopwatch.Frequency / 100);
		ContentPipelineDiagnostics.RecordByteBudgetWait(Stopwatch.Frequency / 200);
		ContentPipelineDiagnostics.RecordByteBudgetLease(64);
		ContentPipelineDiagnostics.RecordByteBudgetRelease(64);

		var snapshot = measurement.Capture();

		Assert.Equal(1, snapshot.Stages[ContentPipelineStage.Detection].InvocationCount);
		Assert.True(snapshot.Stages[ContentPipelineStage.Detection].ElapsedTicks > 0);
		Assert.Equal(100, snapshot.SourceReadBytes);
		Assert.Equal(70, snapshot.PreparedReadBytes);
		Assert.Equal(80, snapshot.PreparedWriteBytes);
		Assert.True(snapshot.QueueWaitTimeTicks > 0);
		Assert.True(snapshot.ByteBudgetWaitTimeTicks > 0);
		Assert.Equal(64, snapshot.ByteBudgetRequestedBytes);
		Assert.Equal(64, snapshot.PeakInFlightBytes);
		Assert.True(snapshot.CpuTimeTicks >= 0);
		Assert.True(snapshot.AllocatedBytes >= 0);
		Assert.True(snapshot.GcPauseTimeTicks >= 0);
		Assert.True(snapshot.PeakWorkingSetBytes > 0);
	}
}
