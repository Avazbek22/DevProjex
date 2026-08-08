using DevProjex.Application.Compression;
using DevProjex.Infrastructure.ProjectProfiles;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowCompressionPreviewPerformanceUiTests
{
    [AvaloniaFact]
    public async Task PersistedCompression_PrewarmsBeforeTreeToBothPreviewTransition()
    {
        using var project = UiTestProject.CreateDefault();
        var appDataPath = Path.Combine(project.AppDataPath, "compression-profile");
        Directory.CreateDirectory(appDataPath);
        new ProjectProfileStore(() => appDataPath).SaveProfile(
            project.RootPath,
            new ProjectSelectionProfile(
                SelectedRootFolders: [],
                SelectedExtensions: [],
                SelectedIgnoreOptions: [IgnoreOptionId.CompressCode],
                IgnoreOptionStates: new Dictionary<IgnoreOptionId, bool>
                {
                    [IgnoreOptionId.CompressCode] = true
                }));

        var window = await UiTestDriver.CreateLoadedMainWindowAsync(
            project,
            appDataPathOverride: appDataPath);
        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);
            Assert.Contains(
                viewModel.ContentProcessingOptions,
                static option => option.Id == IgnoreOptionId.CompressCode && option.IsChecked);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => UiTestDriver.GetCodeCompressionDiagnostics(window).PrewarmRequests >= 10,
                "persisted compression selection to start background prewarm");

            await UiTestDriver.OpenPreviewAsync(window);
            Assert.Equal(PreviewContentMode.Tree, viewModel.SelectedPreviewContentMode);
            var beforeBoth = UiTestDriver.GetCodeCompressionDiagnostics(window);

            await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.TreeAndContent);

            var afterBoth = UiTestDriver.GetCodeCompressionDiagnostics(window);
            Assert.Equal(beforeBoth.AnalysisExecutions, afterBoth.AnalysisExecutions);
            Assert.True(
                afterBoth.CacheHits + afterBoth.PrewarmReuses >
                beforeBoth.CacheHits + beforeBoth.PrewarmReuses);
            Assert.Contains(
                "BuildAppValue",
                UiTestDriver.ComputeCurrentPreviewCopyPayload(window),
                StringComparison.Ordinal);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }
}
