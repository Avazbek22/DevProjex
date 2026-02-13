using System;
using System.IO;
using Xunit;

namespace DevProjex.Tests.Integration;

public sealed class OperationCancellationWiringIntegrationTests
{
    [Fact]
    public void MainWindow_StatusCancelHandler_CancelsAllOperationTokens()
    {
        var content = ReadMainWindowCode();

        Assert.Contains("_projectOperationCts?.Cancel();", content);
        Assert.Contains("_refreshCts?.Cancel();", content);
        Assert.Contains("_gitCloneCts?.Cancel();", content);
        Assert.Contains("_gitOperationCts?.Cancel();", content);
    }

    [Fact]
    public void MainWindow_StatusCancelHandler_HasSpecificFallbacksForMetricsAndProjectLoad()
    {
        var content = ReadMainWindowCode();

        Assert.Contains("if (activeOperationType == StatusOperationType.MetricsCalculation)", content);
        Assert.Contains("CancelBackgroundMetricsCalculation();", content);
        Assert.Contains("Toast.Operation.MetricsCanceled", content);

        Assert.Contains("if (activeOperationType == StatusOperationType.LoadProject)", content);
        Assert.Contains("TryApplyActiveProjectLoadCancellationFallback()", content);
        Assert.Contains("Toast.Operation.LoadCanceled", content);
    }

    [Fact]
    public void MainWindow_GitCloneFlow_HasDedicatedOperationCanceledCatchBeforeGenericErrorCatch()
    {
        var content = ReadMainWindowCode();
        var methodStart = content.IndexOf("private async void OnGitCloneStart(", StringComparison.Ordinal);
        var methodEnd = content.IndexOf("private void OnGitCloneCancel(", StringComparison.Ordinal);

        Assert.True(methodStart >= 0, "OnGitCloneStart method not found.");
        Assert.True(methodEnd > methodStart, "OnGitCloneStart method boundary not found.");

        var methodBody = content.Substring(methodStart, methodEnd - methodStart);
        var operationCanceledIndex = methodBody.IndexOf("catch (OperationCanceledException)", StringComparison.Ordinal);
        var genericExceptionIndex = methodBody.IndexOf("catch (Exception ex)", StringComparison.Ordinal);

        Assert.True(operationCanceledIndex >= 0, "OperationCanceledException catch was not found.");
        Assert.True(genericExceptionIndex > operationCanceledIndex, "Generic exception catch must come after cancellation catch.");
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
