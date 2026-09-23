using System.Diagnostics;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia;

public partial class MainWindow
{
    private bool _allowCloseAfterSelectionPersistence;
    private bool _selectionPersistenceClosePending;

    internal Task ShutdownCompletion => _shutdownCompletion.Task;

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_allowCloseAfterSelectionPersistence &&
            (_treeSelectionProfiles.State.Phase != SelectionPersistencePhase.Idle ||
             _projectProfiles.HasPendingWrites))
        {
            e.Cancel = true;
            if (_selectionPersistenceClosePending)
                return;

            _selectionPersistenceClosePending = true;
            var canClose = await ResolvePendingSelectionBeforeTransitionAsync(exiting: true);
            _selectionPersistenceClosePending = false;
            if (!canClose)
                return;

            _allowCloseAfterSelectionPersistence = true;
            Dispatcher.Post(Close, DispatcherPriority.Send);
            return;
        }

        if (!_allowCloseAfterManualSecretMarkPersistence &&
            _previewSurfaceController.HasPendingManualMarkOperations)
        {
            e.Cancel = true;
            if (_manualSecretMarkClosePending)
                return;

            _manualSecretMarkClosePending = true;
            await _previewSurfaceController.WaitForPendingManualMarkOperationsAsync();
            _allowCloseAfterManualSecretMarkPersistence = true;
            // Never re-enter Close while Avalonia is still dispatching the cancelled Closing event.
            Dispatcher.Post(Close, DispatcherPriority.Send);
            return;
        }

        if (!_allowCloseAfterProjectCopyExportCleanup && _projectCopyExportCts is not null)
        {
            e.Cancel = true;
            if (_projectCopyExportClosePending)
                return;

            _projectCopyExportClosePending = true;
            var completion = _projectCopyExportCompletion?.Task;
            try
            {
                _projectCopyExportCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Export completion won the race with window shutdown.
            }

            if (completion is not null)
                await completion;

            _allowCloseAfterProjectCopyExportCleanup = true;
            Close();
            return;
        }

        if (!_allowCloseAfterGitOperationCleanup && HasActiveGitOperations())
        {
            e.Cancel = true;
            if (_gitOperationClosePending)
                return;

            _gitOperationClosePending = true;
            CancelActiveGitOperations();
            await WaitForActiveGitOperationsAsync();
            _allowCloseAfterGitOperationCleanup = true;
            Dispatcher.Post(Close, DispatcherPriority.Send);
            return;
        }

        if (_allowCloseAfterDesktopControlServerCleanup)
            return;

        Interlocked.Exchange(ref _desktopControlServerShutdownRequested, 1);
        if (_desktopControlServerClosePending)
        {
            e.Cancel = true;
            return;
        }

        var desktopControlServer = Interlocked.Exchange(ref _desktopControlServer, null);
        if (desktopControlServer is null)
        {
            _allowCloseAfterDesktopControlServerCleanup = true;
            return;
        }

        e.Cancel = true;
        _desktopControlServerClosePending = true;
        try
        {
            _projectLoadPipeline.CancelActiveLoad();
            _refreshPipeline.CancelActiveRefresh();
            await desktopControlServer.DisposeAsync();
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Desktop control shutdown failed: {0}", exception.GetType().Name);
        }
        finally
        {
            _allowCloseAfterDesktopControlServerCleanup = true;
            Dispatcher.Post(Close, DispatcherPriority.Send);
        }
    }

    private async Task<bool> ResolvePendingSelectionBeforeTransitionAsync(bool exiting)
    {
        var cancellationToken = _windowLifetimeCts?.Token ?? CancellationToken.None;
        while (true)
        {
            var treeSelectionSaved = await _treeSelectionProfiles.FlushAsync(cancellationToken);
            var projectProfileFlush = await _projectProfiles.FlushPendingAsync(
                TimeSpan.FromSeconds(2),
                cancellationToken);
            if (treeSelectionSaved && projectProfileFlush.Succeeded)
                return true;

            var decision = await MessageDialog.ShowChoiceAsync(
                this,
                _localization["Terminal.Tui.ProfileSaveFailure.Title"],
                _localization["Terminal.Tui.ProfileSaveFailure.Message"],
                _localization["Terminal.Tui.ProfileSaveFailure.Stay"],
                exiting
                    ? _localization["Terminal.Tui.ProfileSaveFailure.ExitWithoutSaving"]
                    : _localization["Terminal.Tui.ProfileSaveFailure.ContinueWithoutSaving"],
                _localization["Terminal.Tui.Retry"]);
            switch (decision)
            {
                case 2:
                    continue;
                case 1:
                    if (!await _projectProfiles.DiscardPendingWritesAsync(
                            TimeSpan.FromSeconds(2),
                            cancellationToken))
                    {
                        Trace.TraceWarning(
                            "Project profile pending writes could not be discarded before the transition.");
                        return false;
                    }
                    _treeSelectionProfiles.CancelPending();
                    return true;
                default:
                    return false;
            }
        }
    }

    private void CancelAndDisposeWindowOperations()
    {
        CancelAndDispose(ref _windowLifetimeCts);
        _metrics.Dispose();
        _projectLoadPipeline.Dispose();
        _previewPipeline.Dispose();
        _refreshPipeline.Dispose();

        _memoryCleanup.CancelAll();
        _previewWorkspaceController.CancelModeSwitch();

        CancelAndDispose(ref _projectOperationCts);
        CancelAndDispose(ref _applySettingsCts);
        CancelAndDispose(ref _gitCloneCts);
        CancelAndDispose(ref _gitCloneCatalogCts);
        CancelAndDispose(ref _gitOperationCts);
        CancelAndDispose(ref _projectCopyExportCts);
        CancelAndDispose(ref _orderedSelectionProjectionCts);
        CancelSecretRedactionDiscovery();
    }

    private static void CancelAndDispose(ref CancellationTokenSource? source)
    {
        var current = Interlocked.Exchange(ref source, null);

        if (current is null)
            return;

        // Closing the window is the ownership boundary for all in-flight UI work.
        // Cancel before disposing so background continuations can observe shutdown.
        try
        {
            current.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        current.Dispose();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        try
        {
            CancelAndDispose(ref _windowLifetimeCts);
            CompleteSessionMetricsRecording();
            if (!_treeSelectionProfiles.Flush(TimeSpan.FromSeconds(2)))
                Trace.TraceWarning("Project tree selection persistence did not finish before shutdown.");
            FlushPersistedStateOnWindowClose();

            // Unsubscribe from window events
            PropertyChanged -= OnWindowPropertyChanged;
            ScalingChanged -= OnWindowScalingChanged;

            // Unsubscribe from localization service
            if (_languageChangedHandler is not null)
                _localization.LanguageChanged -= _languageChangedHandler;

            // Unsubscribe from application theme changes
            var app = global::Avalonia.Application.Current;
            if (app is not null && _themeChangedHandler is not null)
                app.ActualThemeVariantChanged -= _themeChangedHandler;

            // Unsubscribe from ViewModel
            if (_viewModelPropertyChangedHandler is not null)
                _viewModel.PropertyChanged -= _viewModelPropertyChangedHandler;

            // Unsubscribe from tree checkbox changes for metrics

            // Unsubscribe from DragDrop events
            if (_dropZoneContainer is not null)
            {
                _dropZoneContainer.RemoveHandler(DragDrop.DragEnterEvent, OnDropZoneDragEnter);
                _dropZoneContainer.RemoveHandler(DragDrop.DragOverEvent, OnDropZoneDragOver);
                _dropZoneContainer.RemoveHandler(DragDrop.DragLeaveEvent, OnDropZoneDragLeave);
                _dropZoneContainer.RemoveHandler(DragDrop.DropEvent, OnDropZoneDrop);
            }

            // Unsubscribe from tree pointer events
            if (_treeView is not null)
                _treeView.PointerEntered -= OnTreePointerEntered;
            if (_previewSegmentGrid is not null)
                _previewSegmentGrid.SizeChanged -= OnPreviewSegmentGridSizeChanged;
            if (_previewBar is not null)
                _previewBar.SizeChanged -= OnPreviewBarSizeChanged;
            DetachRecentMenuHandlers();
            DetachTreeFontMenuHandlers();
            ClearAgentActivityPresentation();
            StopLiveSessionObservation();
            (_agentJournalReader as IDisposable)?.Dispose();
            _secretRedactionSession.SnapshotPublished -= OnSecretRedactionSnapshotPublished;
            _codeCompressionSession.SnapshotPublished -= OnCodeCompressionSnapshotPublished;
            _secretRedactionSession.Reset();
            _codeCompressionSession.Reset();

            // Unsubscribe from tunneled/bubbled events
            RemoveHandler(PointerPressedEvent, OnWindowPointerPressedForPreviewNavigation);
            RemoveHandler(PointerWheelChangedEvent, OnWindowPointerWheelChanged);
            RemoveHandler(KeyDownEvent, OnKeyDown);
            RemoveHandler(MenuItem.SubmenuOpenedEvent, _themeBrushCoordinator.HandleSubmenuOpened);
            RemoveHandler(MenuItem.SubmenuOpenedEvent, MenuScrollBehavior.HandleSubmenuOpened);

            // Unsubscribe from window lifecycle events
            Opened -= OnOpened;
            Closing -= OnWindowClosing;
            Closed -= OnWindowClosed;
            Activated -= OnActivated;
            Deactivated -= OnDeactivated;

            _previewSurfaceController.Dispose();
            _treeContextMenu.Dispose();
            CancelAndDisposeWindowOperations();

            _searchFilterController.ClearProjectState();

            // Dispose coordinators
            _memoryCleanup.Dispose();
            _backgroundTasks.Dispose();
            _previewWorkspaceController.Dispose();
            _previewSearchController.Dispose();
            _searchFilterController.Dispose();
            _workspacePresentation.Dispose();
            _selectionCoordinator.Dispose();
            _treeSelectionProfiles.StateChanged -= OnTreeSelectionPersistenceStateChanged;
            _treeSelectionProfiles.Dispose();
            _themeBrushCoordinator.Dispose();
            _applicationUpdates.Dispose();
            _statusOperations.Dispose();
            _secretRedactionSession.Dispose();
            _codeCompressionSession.Dispose();

            // Dispose ViewModel to clean up collection event handlers
            _viewModel.Dispose();

            // Dispose icon cache to release bitmap resources
            _iconCache.Dispose();

            // Dispose toast service to cancel pending dismiss timers
            if (_toastService is IDisposable toastDisposable)
                toastDisposable.Dispose();

            // Clear tree references and release memory
            foreach (var node in _viewModel.TreeNodes)
                node.ClearRecursive();
            _viewModel.TreeNodes.Clear();
            _currentTree = null;
            _filterBaseTree = null;
            _currentTreeInventory = null;
            _gitScopePresentationRefreshContext = null;
            ResetPreviewTreePaneVisualState();
            ResetInteractiveFilterCache();
            _metrics.InvalidateComputedCaches();

            // Clear file metrics cache
            _metrics.ClearFileMetricsCache(trimCapacity: true);

            // Releasing the file-handle lease makes this checkout eligible for silent cache GC.
            Interlocked.Exchange(ref _currentRepositorySession, null)?.Dispose();
            _repoCacheService.RequestGarbageCollection();
            _repoCacheService.Dispose();

            _taskbarProgress.Dispose();
            _desktopInteractionGate.Dispose();

            // Dispose ZipDownloadService
            if (_zipDownloadService is IDisposable disposable)
                disposable.Dispose();

            _sessionMetrics.Dispose();
            _shutdownCompletion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            _shutdownCompletion.TrySetException(exception);
            throw;
        }
    }
}
