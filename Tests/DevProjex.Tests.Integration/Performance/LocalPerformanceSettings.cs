using System;
using System.IO;

namespace DevProjex.Tests.Integration.Performance;

internal static class LocalPerformanceSettings
{
    public const string RunPerfEnvVar = "DEVPROJEX_RUN_LOCAL_PERF";
    public const string UpdateBaselineEnvVar = "DEVPROJEX_PERF_UPDATE_BASELINE";
    public const string BaselinePathEnvVar = "DEVPROJEX_PERF_BASELINE_PATH";
    public const string EnforceBaselineRegressionEnvVar = "DEVPROJEX_PERF_ENFORCE_BASELINE";

    public static bool IsPerformanceRunEnabled => IsTrue(Environment.GetEnvironmentVariable(RunPerfEnvVar));

    public static bool ShouldUpdateBaseline => IsTrue(Environment.GetEnvironmentVariable(UpdateBaselineEnvVar));
    public static bool ShouldEnforceBaselineRegression => IsTrue(Environment.GetEnvironmentVariable(EnforceBaselineRegressionEnvVar));

    public static string BaselineFilePath
    {
        get
        {
            var customPath = Environment.GetEnvironmentVariable(BaselinePathEnvVar);
            if (!string.IsNullOrWhiteSpace(customPath))
                return customPath;

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
                localAppData = Path.GetTempPath();

            return Path.Combine(localAppData, "DevProjex", "Performance", "perf-baseline.local.json");
        }
    }

    private static bool IsTrue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}
