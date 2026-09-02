namespace DevProjex.Avalonia.Services;

internal readonly record struct MemoryCleanupRetentionSnapshot(
    long PlanCacheBytes,
    long ReadFactBytes)
{
    public static MemoryCleanupRetentionSnapshot Empty { get; } = new();
}

internal readonly record struct MemoryCleanupTraceMeasurement(
    long ManagedHeapBytes,
    long HeapSizeBytes,
    long FragmentedBytes,
    long TotalCommittedBytes,
    long PrivateBytes,
    long WorkingSetBytes,
    long PlanCacheBytes,
    long ReadFactBytes)
{
    public static MemoryCleanupTraceMeasurement Capture(
        Func<MemoryCleanupRetentionSnapshot> captureRetention)
    {
        var gc = GC.GetGCMemoryInfo();
        var retention = captureRetention();
        using var process = Process.GetCurrentProcess();
        return new MemoryCleanupTraceMeasurement(
            GC.GetTotalMemory(forceFullCollection: false),
            gc.HeapSizeBytes,
            gc.FragmentedBytes,
            gc.TotalCommittedBytes,
            process.PrivateMemorySize64,
            process.WorkingSet64,
            retention.PlanCacheBytes,
            retention.ReadFactBytes);
    }
}

internal sealed class MemoryCleanupTrace
{
    internal const string EnvironmentVariableName = "DEVPROJEX_MEMORY_CLEANUP_TRACE";
    private readonly Func<MemoryCleanupRetentionSnapshot> _captureRetention;
    private readonly Action<string> _writeLine;

    internal MemoryCleanupTrace(
        Func<MemoryCleanupRetentionSnapshot> captureRetention,
        Action<string> writeLine)
    {
        _captureRetention = captureRetention;
        _writeLine = writeLine;
    }

    public static MemoryCleanupTrace? Create(
        Func<MemoryCleanupRetentionSnapshot>? captureRetention)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnvironmentVariableName),
                "1",
                StringComparison.Ordinal))
        {
            return null;
        }

        return new MemoryCleanupTrace(
            captureRetention ?? (static () => MemoryCleanupRetentionSnapshot.Empty),
            static line => Trace.WriteLine(line));
    }

    public MemoryCleanupTraceMeasurement Capture() =>
        MemoryCleanupTraceMeasurement.Capture(_captureRetention);

    public void Write(
        MemoryCleanupReason? reason,
        MemoryCleanupCollectionMode stage,
        MemoryCleanupTraceMeasurement before,
        MemoryCleanupTraceMeasurement after)
    {
        _writeLine(string.Create(
            CultureInfo.InvariantCulture,
            $"[MEMORY_CLEANUP] reason={reason?.ToString() ?? "Immediate"} stage={stage} " +
            $"managed={before.ManagedHeapBytes}->{after.ManagedHeapBytes} " +
            $"heap={before.HeapSizeBytes}->{after.HeapSizeBytes} " +
            $"fragmented={before.FragmentedBytes}->{after.FragmentedBytes} " +
            $"committed={before.TotalCommittedBytes}->{after.TotalCommittedBytes} " +
            $"private={before.PrivateBytes}->{after.PrivateBytes} " +
            $"workingSet={before.WorkingSetBytes}->{after.WorkingSetBytes} " +
            $"planCache={before.PlanCacheBytes}->{after.PlanCacheBytes} " +
            $"readFacts={before.ReadFactBytes}->{after.ReadFactBytes}"));
    }
}
