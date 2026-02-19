namespace DevProjex.Tests.Integration.Performance;

internal sealed class LocalPerformanceFactAttribute : FactAttribute
{
    public LocalPerformanceFactAttribute()
    {
        if (!LocalPerformanceSettings.IsPerformanceRunEnabled)
            Skip = "Local performance tests are disabled. Set DEVPROJEX_RUN_LOCAL_PERF=1 to run.";
    }
}

internal sealed class LocalPerformanceTheoryAttribute : TheoryAttribute
{
    public LocalPerformanceTheoryAttribute()
    {
        if (!LocalPerformanceSettings.IsPerformanceRunEnabled)
            Skip = "Local performance tests are disabled. Set DEVPROJEX_RUN_LOCAL_PERF=1 to run.";
    }
}
