namespace DevProjex.Tests.Integration;

public sealed class GitCloneProgressLocalizationWiringIntegrationTests
{
    [Fact]
    public void MainWindow_GitCloneFlow_InitialStatus_UsesLocalizedCheckingGit()
    {
        var methodBody = ReadOnGitCloneStartBody();

        Assert.Contains("_viewModel.GitCloneStatus = _viewModel.GitCloneProgressCheckingGit;", methodBody);
    }

    [Fact]
    public void MainWindow_GitCloneProgressHandler_DoesNotAssignRawStatusDirectly()
    {
        var methodBody = ReadOnGitCloneStartBody();

        Assert.DoesNotContain("_viewModel.GitCloneStatus = status;", methodBody, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_GitCloneProgressHandler_FormatsPercentWithLocalizedOperation()
    {
        var methodBody = ReadOnGitCloneStartBody();

        Assert.Contains("status.EndsWith('%')", methodBody);
        Assert.Contains("_viewModel.GitCloneStatus = $\"{currentOperation} {status}\";", methodBody);
    }

    [Fact]
    public void MainWindow_GitCloneProgressHandler_UsesLocalizedOperationForNonPercentUpdates()
    {
        var methodBody = ReadOnGitCloneStartBody();

        Assert.Contains("else if (!string.IsNullOrEmpty(currentOperation))", methodBody);
        Assert.Contains("_viewModel.GitCloneStatus = currentOperation;", methodBody);
    }

    [Fact]
    public void MainWindow_GitCloneProgressHandler_UsesLocalizedExtractingPhase()
    {
        var methodBody = ReadOnGitCloneStartBody();

        Assert.Contains("if (status == \"::EXTRACTING::\")", methodBody);
        Assert.Contains("currentOperation = _viewModel.GitCloneProgressExtracting;", methodBody);
    }

    [Fact]
    public void MainWindow_GitCloneFlow_SetsLocalizedCloningStatus_BeforeCloneAsync()
    {
        var methodBody = ReadOnGitCloneStartBody();

        var operationAssign = methodBody.IndexOf("currentOperation = _viewModel.GitCloneProgressCloning;", StringComparison.Ordinal);
        var statusAssign = methodBody.IndexOf("_viewModel.GitCloneStatus = currentOperation;", operationAssign, StringComparison.Ordinal);
        var cloneCall = methodBody.IndexOf("result = await _gitService.CloneAsync(", StringComparison.Ordinal);

        Assert.True(operationAssign >= 0, "Localized cloning operation assignment not found.");
        Assert.True(statusAssign > operationAssign, "Status must be set after localized cloning operation assignment.");
        Assert.True(cloneCall > statusAssign, "CloneAsync must start after localized status is set.");
    }

    [Fact]
    public void MainWindow_GitCloneFlow_SetsLocalizedDownloadingStatus_BeforeZipFallback()
    {
        var methodBody = ReadOnGitCloneStartBody();

        var operationAssign = methodBody.IndexOf("currentOperation = _viewModel.GitCloneProgressDownloading;", StringComparison.Ordinal);
        var statusAssign = methodBody.IndexOf("_viewModel.GitCloneStatus = currentOperation;", operationAssign, StringComparison.Ordinal);
        var zipCall = methodBody.IndexOf("result = await _zipDownloadService.DownloadAndExtractAsync(", StringComparison.Ordinal);

        Assert.True(operationAssign >= 0, "Localized downloading operation assignment not found.");
        Assert.True(statusAssign > operationAssign, "Status must be set after localized downloading operation assignment.");
        Assert.True(zipCall > statusAssign, "ZIP fallback must start after localized status is set.");
    }

    private static string ReadOnGitCloneStartBody()
    {
        var content = ReadMainWindowCode();
        var methodStart = content.IndexOf("private async void OnGitCloneStart(", StringComparison.Ordinal);
        var methodEnd = content.IndexOf("private void OnGitCloneCancel(", StringComparison.Ordinal);

        Assert.True(methodStart >= 0, "OnGitCloneStart method not found.");
        Assert.True(methodEnd > methodStart, "OnGitCloneStart method boundary not found.");

        return content.Substring(methodStart, methodEnd - methodStart);
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
