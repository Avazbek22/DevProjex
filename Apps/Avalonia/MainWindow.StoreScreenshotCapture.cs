using System.Text.Json;
using DevProjex.Terminal.DesktopControl;
using ThemeSelectionMode = DevProjex.Infrastructure.ThemePresets.ThemeSelectionMode;

namespace DevProjex.Avalonia;

public partial class MainWindow
{
    private static readonly TimeSpan StoreCaptureAcknowledgementTimeout =
        TimeSpan.FromMinutes(2);

    private async Task RunStoreScreenshotCaptureAsync(
        StoreScreenshotCaptureRequest request,
        CancellationToken cancellationToken)
    {
        var stage = "startup";
        try
        {
            // Store assets intentionally use the product's strongest calibrated presentation.
            // This touches only the capture session's isolated app-data directory.
            _appearanceSettings.SetTheme(ThemeSelectionMode.Dark);
            if (!_viewModel.IsAcrylicEnabled)
                _appearanceSettings.ToggleAcrylicEffect();

            WriteStoreCaptureState(request, "window-ready.json", new
            {
                processId = Environment.ProcessId,
                language = request.LanguageCode
            });
            await WaitForStoreCaptureMarkerAsync(
                request,
                "window-positioned",
                cancellationToken);

            stage = "open-project";
            if (!await TryOpenFolderAsync(
                    request.ProjectPath,
                    fromDialog: false,
                    recordRecentFolder: false))
            {
                throw new InvalidOperationException("The Store capture project could not be opened.");
            }

            await _projectLoadFinalizationTask.WaitAsync(cancellationToken);
            await _postLoadVisualReadyTask.WaitAsync(cancellationToken);
            await _selectionCoordinator.WaitForPendingRefreshesAsync();
            await _workspacePresentation.SettingsAnimationTask.WaitAsync(cancellationToken);

            stage = "tree-preview";
            _viewModel.SettingsVisible = false;
            await AnimateSettingsPanelAsync(show: false);
            _viewModel.SelectedPreviewContentMode = PreviewContentMode.TreeAndContent;
            await _previewWorkspaceController.OpenAsync();
            await CaptureStoreSceneAsync(request, 1, "Tree_Preview", cancellationToken);

            // The settings scene is captured before any agent session starts, so it keeps the
            // plain project state without the Live context title suffix or delivery markers.
            stage = "settings";
            await _previewWorkspaceController.SwitchModeAsync(PreviewContentMode.Tree);
            _viewModel.SettingsVisible = true;
            await AnimateSettingsPanelAsync(show: true);
            await CaptureStoreSceneAsync(request, 5, "Tree_Preview_Settings", cancellationToken);

            stage = "live-context";
            var deliveryMarker = await PrepareStoreLiveContextSceneAsync(request, cancellationToken);
            await CaptureStoreSceneAsync(request, 2, "Live_Context", cancellationToken);

            stage = "mcp-menu";
            await OpenStoreMcpMenuAsync(deliveryMarker, cancellationToken);
            await CaptureStoreSceneAsync(request, 3, "Mcp_Menu", cancellationToken);

            stage = "agent-journal";
            var journalWindow = await OpenStoreAgentJournalAsync(cancellationToken);
            await CaptureStoreSceneAsync(
                request,
                4,
                "Agent_Journal",
                cancellationToken,
                foregroundWindow: journalWindow);
            journalWindow.Close();

            WriteStoreCaptureState(request, "complete.json", new
            {
                success = true,
                sceneCount = 5
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            WriteStoreCaptureState(request, "failure.json", new
            {
                code = "DPX-STORE-CAPTURE-CANCELED",
                stage
            });
        }
        catch (Exception exception)
        {
            // This private protocol deliberately reports only the exception type and a fixed
            // stage name. Capture sessions can reference local paths, which must not leak into
            // generated assets.
            WriteStoreCaptureState(request, "failure.json", new
            {
                code = "DPX-STORE-CAPTURE-FAILED",
                stage,
                exceptionType = exception.GetType().Name
            });
        }
        finally
        {
            Close();
        }
    }

    private async Task CaptureStoreSceneAsync(
        StoreScreenshotCaptureRequest request,
        int index,
        string name,
        CancellationToken cancellationToken,
        Window? foregroundWindow = null)
    {
        await WaitForStoreCaptureVisualsAsync(cancellationToken);
        if (foregroundWindow is not null)
            await WaitForStoreCaptureTopLevelFrameAsync(foregroundWindow, cancellationToken);
        var stem = $"{index:D2}-{name}";
        WriteStoreCaptureState(request, $"ready-{stem}.json", new
        {
            index,
            name,
            projectLoaded = _viewModel.IsProjectLoaded,
            previewOpen = _viewModel.IsPreviewMode,
            settingsOpen = _viewModel.SettingsVisible,
            filter = _viewModel.NameFilter,
            // The controller re-activates the main window before each frame unless the scene
            // is presented by one of its owned windows, which then keeps the foreground.
            foreground = foregroundWindow is null ? "main" : "owned"
        });
        await WaitForStoreCaptureMarkerAsync(
            request,
            $"captured-{stem}",
            cancellationToken);
    }

    private async Task WaitForStoreCaptureVisualsAsync(
        CancellationToken cancellationToken)
    {
        var timeoutAt = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
        while (!IsSessionMetricsIdle() ||
               IsBackgroundMetricsActive() ||
               IsStoreCaptureStatusMetricAnimating())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= timeoutAt)
                throw new TimeoutException("The Store capture UI did not become idle.");

            await Task.Delay(TimeSpan.FromMilliseconds(40), cancellationToken);
        }

        await YieldUiAsync(DispatcherPriority.Render);
        await WaitForNextAnimationFrameAsync(cancellationToken);
        await YieldUiAsync(DispatcherPriority.Render);
        await TryWaitForRenderedCompositionBatchAsync(cancellationToken);
    }

    // Store media must not date itself, so capture mode drops the version from the window title.
    private string ApplyStoreCaptureTitlePolicy(string title) =>
        _startupOptions.StoreScreenshotCapture is null ? title : RemoveTitleVersion(title);

    private static string RemoveTitleVersion(string title) =>
        title.StartsWith(MainWindowViewModel.BaseTitle, StringComparison.Ordinal)
            ? MainWindowViewModel.ProductName + title[MainWindowViewModel.BaseTitle.Length..]
            : title;

    // Status metrics roll their digits after every recalculation. A frame taken mid-roll shows
    // glyphs at different baselines, so captures wait until both strips are at rest.
    private bool IsStoreCaptureStatusMetricAnimating() =>
        StatusTreeMetricsWave.IsAnimationActive ||
        StatusContentMetricsWave.IsAnimationActive;

    private static async Task WaitForStoreCaptureMarkerAsync(
        StoreScreenshotCaptureRequest request,
        string markerName,
        CancellationToken cancellationToken)
    {
        var markerPath = Path.Combine(request.SessionDirectory, markerName);
        var timeoutAt = DateTimeOffset.UtcNow + StoreCaptureAcknowledgementTimeout;
        while (!File.Exists(markerPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= timeoutAt)
                throw new TimeoutException("The Store capture controller did not acknowledge the frame.");

            await Task.Delay(TimeSpan.FromMilliseconds(40), cancellationToken);
        }
    }

    private static void WriteStoreCaptureState(
        StoreScreenshotCaptureRequest request,
        string fileName,
        object state)
    {
        Directory.CreateDirectory(request.SessionDirectory);
        var path = Path.Combine(request.SessionDirectory, fileName);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(state),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
