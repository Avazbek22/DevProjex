using System;
using System.IO;
using Xunit;

namespace DevProjex.Tests.Integration;

public sealed class MetricsBaselineFallbackWiringIntegrationTests
{
    [Fact]
    public void MainWindow_ContainsBaselineStateFieldAndDecisionGuard()
    {
        var content = ReadMainWindowCode();

        Assert.Contains("private volatile bool _hasCompleteMetricsBaseline;", content);
        Assert.Contains("if (!ShouldProceedWithMetricsCalculation(hasAnyChecked, hasCompleteMetricsBaseline))", content);
    }

    [Fact]
    public void MainWindow_MetricsCancellationAndSuccess_UpdateBaselineState()
    {
        var content = ReadMainWindowCode();

        Assert.Contains("_hasCompleteMetricsBaseline = false;", content);
        Assert.Contains("_hasCompleteMetricsBaseline = true;", content);
        Assert.Contains("_metricsCancellationRequestedByUser = true;", content);
    }

    private static string ReadMainWindowCode()
    {
        var repoRoot = FindRepositoryRoot();
        var file = Path.Combine(repoRoot, "Apps", "Avalonia", "DevProjex.Avalonia", "MainWindow.axaml.cs");
        return File.ReadAllText(file);
    }

    private static string FindRepositoryRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) ||
                File.Exists(Path.Combine(dir, "DevProjex.sln")))
                return dir;

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
