using DevProjex.Application.Diagnostics;

namespace DevProjex.Tests.Unit;

public sealed class IgnorePipelineDiagnosticsTests
{
	[Fact]
	public void NestedMeasurements_RestoreTheParentWithoutMixingCounters()
	{
		using var outer = IgnorePipelineDiagnostics.BeginMeasurement();
		IgnorePipelineDiagnostics.RecordWorkspaceScan();

		using (var inner = IgnorePipelineDiagnostics.BeginMeasurement())
		{
			IgnorePipelineDiagnostics.RecordWorkspaceScan();
			IgnorePipelineDiagnostics.RecordWorkspaceScan();
			Assert.Equal(2, inner.Capture().WorkspaceScans);
		}

		IgnorePipelineDiagnostics.RecordWorkspaceScan();
		Assert.Equal(2, outer.Capture().WorkspaceScans);
	}

	[Fact]
	public async Task Measurement_FlowsIntoParallelWorkAndAggregatesAtomically()
	{
		using var measurement = IgnorePipelineDiagnostics.BeginMeasurement();
		await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
		{
			for (var index = 0; index < 100; index++)
				IgnorePipelineDiagnostics.RecordDirectoryEnumeration();
		})));

		Assert.Equal(6_400, measurement.Capture().DirectoryEnumerations);
	}
}
