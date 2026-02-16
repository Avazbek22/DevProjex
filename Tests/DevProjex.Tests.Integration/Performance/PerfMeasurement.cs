using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace DevProjex.Tests.Integration.Performance;

internal readonly record struct PerfMeasurement(
    double MedianMilliseconds,
    double P95Milliseconds,
    long MedianAllocatedBytes,
    long MaxAllocatedBytes,
    int Iterations);

internal static class PerfMeasurementRunner
{
    public static PerfMeasurement Measure(Action action, int warmupIterations = 1, int measuredIterations = 3)
    {
        if (action is null)
            throw new ArgumentNullException(nameof(action));
        if (measuredIterations <= 0)
            throw new ArgumentOutOfRangeException(nameof(measuredIterations));

        for (var i = 0; i < warmupIterations; i++)
            action();

        var elapsedSamples = new List<double>(measuredIterations);
        var allocationSamples = new List<long>(measuredIterations);

        for (var i = 0; i < measuredIterations; i++)
        {
            ForceGc();
            var beforeAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true);

            var stopwatch = Stopwatch.StartNew();
            action();
            stopwatch.Stop();

            var afterAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true);
            var allocated = Math.Max(0L, afterAllocatedBytes - beforeAllocatedBytes);

            elapsedSamples.Add(stopwatch.Elapsed.TotalMilliseconds);
            allocationSamples.Add(allocated);
        }

        elapsedSamples.Sort();
        allocationSamples.Sort();

        var medianIndex = elapsedSamples.Count / 2;
        var p95Index = Math.Min(elapsedSamples.Count - 1, (int)Math.Ceiling(elapsedSamples.Count * 0.95) - 1);

        return new PerfMeasurement(
            MedianMilliseconds: elapsedSamples[medianIndex],
            P95Milliseconds: elapsedSamples[p95Index],
            MedianAllocatedBytes: allocationSamples[medianIndex],
            MaxAllocatedBytes: allocationSamples[^1],
            Iterations: measuredIterations);
    }

    private static void ForceGc()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }
}
