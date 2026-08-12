using DevProjex.Avalonia.Services;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class MemoryCleanupTraceTests
{
    [Fact]
    public void Capture_IncludesRetainedPlanAndReadFactBytes()
    {
        var trace = new MemoryCleanupTrace(
            static () => new MemoryCleanupRetentionSnapshot(1234, 5678),
            static _ => { });

        var measurement = trace.Capture();

        Assert.Equal(1234, measurement.PlanCacheBytes);
        Assert.Equal(5678, measurement.ReadFactBytes);
        Assert.True(measurement.ManagedHeapBytes >= 0);
        Assert.True(measurement.PrivateBytes > 0);
        Assert.True(measurement.WorkingSetBytes > 0);
    }

    [Fact]
    public void Write_EmitsReasonStageAndEveryRequestedMeasurement()
    {
        string? output = null;
        var trace = new MemoryCleanupTrace(
            static () => MemoryCleanupRetentionSnapshot.Empty,
            line => output = line);
        var before = new MemoryCleanupTraceMeasurement(1, 2, 3, 4, 5, 6, 7, 8);
        var after = new MemoryCleanupTraceMeasurement(11, 12, 13, 14, 15, 16, 17, 18);

        trace.Write(
            MemoryCleanupReason.ApplySettingsWorkCompleted,
            MemoryCleanupCollectionMode.Compacting,
            before,
            after);

        Assert.Equal(
            "[MEMORY_CLEANUP] reason=ApplySettingsWorkCompleted stage=Compacting " +
            "managed=1->11 heap=2->12 fragmented=3->13 committed=4->14 " +
            "private=5->15 workingSet=6->16 planCache=7->17 readFacts=8->18",
            output);
    }
}
