using System.Text.Json;

namespace DevProjex.Tests.Integration.Performance;

internal sealed class PerfBaselineStore
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const double MinActionableTimeDeltaMs = 1.0;
    private const long MinActionableAllocationDeltaBytes = 64 * 1024;

    private readonly string _baselinePath;

    public PerfBaselineStore(string baselinePath)
    {
        _baselinePath = baselinePath ?? throw new ArgumentNullException(nameof(baselinePath));
    }

    public PerfVerdict Evaluate(
        string scenarioId,
        PerfMeasurement current,
        double maxMedianTimeRegressionPercent = 30.0,
        double maxMedianAllocationRegressionPercent = 35.0,
        double accelerationNoticePercent = 8.0)
    {
        if (string.IsNullOrWhiteSpace(scenarioId))
            throw new ArgumentException("Scenario id is required.", nameof(scenarioId));

        lock (Sync)
        {
            var snapshot = Load();
            if (!snapshot.Scenarios.TryGetValue(scenarioId, out var baseline))
            {
                snapshot.Scenarios[scenarioId] = PerfBaselineScenario.FromMeasurement(current);
                Save(snapshot);

                return new PerfVerdict(
                    IsRegression: false,
                    IsAcceleration: false,
                    Message: $"[baseline-created] {scenarioId} | " +
                             $"median={current.MedianMilliseconds:F2} ms, alloc={FormatBytes(current.MedianAllocatedBytes)}");
            }

            var medianTimeDeltaPercent = CalculateDeltaPercent(baseline.MedianMilliseconds, current.MedianMilliseconds);
            var medianAllocDeltaPercent = CalculateDeltaPercent(baseline.MedianAllocatedBytes, current.MedianAllocatedBytes);
            var medianTimeDeltaMs = current.MedianMilliseconds - baseline.MedianMilliseconds;
            var medianAllocDeltaBytes = current.MedianAllocatedBytes - baseline.MedianAllocatedBytes;

            var isTimeRegression = medianTimeDeltaPercent > maxMedianTimeRegressionPercent &&
                                   medianTimeDeltaMs >= MinActionableTimeDeltaMs;
            var isAllocationRegression = medianAllocDeltaPercent > maxMedianAllocationRegressionPercent &&
                                         medianAllocDeltaBytes >= MinActionableAllocationDeltaBytes;
            var isRegression = isTimeRegression || isAllocationRegression;

            var isTimeAcceleration = medianTimeDeltaPercent < -accelerationNoticePercent &&
                                     Math.Abs(medianTimeDeltaMs) >= MinActionableTimeDeltaMs;
            var isAllocationAcceleration = medianAllocDeltaPercent < -accelerationNoticePercent &&
                                           Math.Abs(medianAllocDeltaBytes) >= MinActionableAllocationDeltaBytes;
            var isAcceleration = isTimeAcceleration || isAllocationAcceleration;

            if (LocalPerformanceSettings.ShouldUpdateBaseline)
            {
                snapshot.Scenarios[scenarioId] = PerfBaselineScenario.FromMeasurement(current);
                Save(snapshot);
            }

            var state = isRegression
                ? "regression"
                : isAcceleration
                    ? "acceleration"
                    : "stable";

            var message =
                $"[{state}] {scenarioId} | " +
                $"median {current.MedianMilliseconds:F2} ms ({SignedPercent(medianTimeDeltaPercent)}), " +
                $"alloc {FormatBytes(current.MedianAllocatedBytes)} ({SignedPercent(medianAllocDeltaPercent)}) | " +
                $"baseline median {baseline.MedianMilliseconds:F2} ms, alloc {FormatBytes(baseline.MedianAllocatedBytes)}";

            return new PerfVerdict(isRegression, isAcceleration, message);
        }
    }

    private PerfBaselineSnapshot Load()
    {
        try
        {
            if (!File.Exists(_baselinePath))
                return new PerfBaselineSnapshot();

            var json = File.ReadAllText(_baselinePath);
            if (string.IsNullOrWhiteSpace(json))
                return new PerfBaselineSnapshot();

            return JsonSerializer.Deserialize<PerfBaselineSnapshot>(json, JsonOptions) ?? new PerfBaselineSnapshot();
        }
        catch
        {
            // Corrupted baseline should never break test execution.
            return new PerfBaselineSnapshot();
        }
    }

    private void Save(PerfBaselineSnapshot snapshot)
    {
        var directoryPath = Path.GetDirectoryName(_baselinePath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
            Directory.CreateDirectory(directoryPath);

        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        File.WriteAllText(_baselinePath, json);
    }

    private static double CalculateDeltaPercent(double baseline, double current)
    {
        if (Math.Abs(baseline) < 0.0001)
            return 0;

        return ((current - baseline) / baseline) * 100.0;
    }

    private static double CalculateDeltaPercent(long baseline, long current)
    {
        if (baseline == 0)
            return 0;

        return ((double)(current - baseline) / baseline) * 100.0;
    }

    private static string SignedPercent(double value) => value >= 0 ? $"+{value:F1}%" : $"{value:F1}%";

    private static string FormatBytes(long bytes)
    {
        const double kb = 1024.0;
        const double mb = kb * 1024.0;

        if (bytes >= mb)
            return $"{bytes / mb:F2} MB";
        if (bytes >= kb)
            return $"{bytes / kb:F2} KB";
        return $"{bytes} B";
    }
}

internal sealed class PerfBaselineSnapshot
{
    public Dictionary<string, PerfBaselineScenario> Scenarios { get; init; } = new(StringComparer.Ordinal);
}

internal sealed class PerfBaselineScenario
{
    public double MedianMilliseconds { get; init; }
    public long MedianAllocatedBytes { get; init; }

    public static PerfBaselineScenario FromMeasurement(PerfMeasurement measurement)
        => new()
        {
            MedianMilliseconds = measurement.MedianMilliseconds,
            MedianAllocatedBytes = measurement.MedianAllocatedBytes
        };
}

internal readonly record struct PerfVerdict(bool IsRegression, bool IsAcceleration, string Message);
