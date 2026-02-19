namespace DevProjex.Tests.Integration;

public sealed class ThemeHighlightWiringIntegrationTests
{
    [Fact]
    public void MainWindow_OnSetLightTheme_UsesSingleHighlightRefreshPath()
    {
        var content = ReadMainWindowCode();
        var body = ExtractMethodBody(
            content,
            "private void OnSetLightTheme(",
            "private void OnSetDarkTheme(");

        Assert.Contains("RefreshThemeHighlightsForActiveQuery();", body);
        Assert.DoesNotContain("_searchCoordinator.UpdateHighlights(_viewModel.SearchQuery);", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_searchCoordinator.UpdateHighlights(_viewModel.NameFilter);", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_OnSetDarkTheme_UsesSingleHighlightRefreshPath()
    {
        var content = ReadMainWindowCode();
        var body = ExtractMethodBody(
            content,
            "private void OnSetDarkTheme(",
            "private void OnToggleMica(");

        Assert.Contains("RefreshThemeHighlightsForActiveQuery();", body);
        Assert.DoesNotContain("_searchCoordinator.UpdateHighlights(_viewModel.SearchQuery);", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_searchCoordinator.UpdateHighlights(_viewModel.NameFilter);", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_RefreshThemeHighlightsForActiveQuery_PrioritizesNameFilterOverSearchQuery()
    {
        var content = ReadMainWindowCode();
        var body = ExtractMethodBody(
            content,
            "private void RefreshThemeHighlightsForActiveQuery()",
            "private void OnWindowPropertyChanged(");

        Assert.Contains("_viewModel.NameFilter", body);
        Assert.Contains("_viewModel.SearchQuery", body);
        Assert.Contains("_searchCoordinator.UpdateHighlights(effectiveQuery);", body);
    }

    private static string ReadMainWindowCode()
    {
        var repoRoot = FindRepositoryRoot();
        var file = Path.Combine(repoRoot, "Apps", "Avalonia", "DevProjex.Avalonia", "MainWindow.axaml.cs");
        return File.ReadAllText(file);
    }

    private static string ExtractMethodBody(string content, string methodStartMarker, string methodEndMarker)
    {
        var start = content.IndexOf(methodStartMarker, StringComparison.Ordinal);
        var end = start >= 0
            ? content.IndexOf(methodEndMarker, start, StringComparison.Ordinal)
            : -1;

        Assert.True(start >= 0, $"Method start marker not found: {methodStartMarker}");
        Assert.True(end > start, $"Method end marker not found for: {methodStartMarker}");

        return content.Substring(start, end - start);
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
