using DevProjex.Application.Compression;
using DevProjex.Infrastructure.ProjectProfiles;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowCompressionPreviewPerformanceUiTests
{
    [AvaloniaFact]
    public async Task CompressionCheckbox_UpdatesVisiblePreviewAndRestoresFullSourceWhenDisabled()
    {
        using var project = UiTestProject.CreateDefault();
        var sourcePath = Path.Combine(project.RootPath, "src", "AppHost", "Program.cs");
        var sourceBytes = await File.ReadAllBytesAsync(
            sourcePath,
            TestContext.Current.CancellationToken);
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);
        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);
            var option = Assert.Single(
                viewModel.ContentProcessingOptions,
                static candidate => candidate.Id == IgnoreOptionId.CompressCode);
            Assert.False(option.IsChecked);

            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);
            Assert.Contains(
                "return \"app-value-1\";",
                UiTestDriver.ComputeCurrentPreviewCopyPayload(window),
                StringComparison.Ordinal);

            var checkBox = UiTestDriver.GetRequiredIgnoreOptionCheckBox(
                window,
                IgnoreOptionId.CompressCode);
            await UiTestDriver.ClickAsync(window, checkBox);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => option.IsChecked &&
                      !UiTestDriver.ComputeCurrentPreviewCopyPayload(window).Contains(
                          "return \"app-value-1\";",
                          StringComparison.Ordinal),
                "compression to remove implementation bodies from the visible Preview");
            var compressedPreview = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
            Assert.Contains("BuildAppValue1()", compressedPreview, StringComparison.Ordinal);
            Assert.True(UiTestDriver.GetCodeCompressionDiagnostics(window).AnalysisExecutions > 0);

            await UiTestDriver.ClickAsync(window, checkBox);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => !option.IsChecked &&
                      UiTestDriver.ComputeCurrentPreviewCopyPayload(window).Contains(
                          "return \"app-value-1\";",
                          StringComparison.Ordinal),
                "disabling compression to restore full source in the visible Preview");

            Assert.Equal(
                sourceBytes,
                await File.ReadAllBytesAsync(
                    sourcePath,
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

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
