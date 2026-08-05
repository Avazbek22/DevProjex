using DevProjex.Avalonia.Coordinators;

namespace DevProjex.Avalonia;

public partial class MainWindow
{
    internal Task ShutdownCompletion => _shutdownCompletion.Task;

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowCloseAfterProjectCopyExportCleanup || _projectCopyExportCts is null)
            return;

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
        CancelAndDispose(ref _gitOperationCts);
        CancelAndDispose(ref _projectCopyExportCts);
		CancelAndDispose(ref _secretRedactionCountCts);
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

    private async void OnWindowClosed(object? sender, EventArgs e)
    {
        try
        {
            CancelAndDispose(ref _windowLifetimeCts);
            // Avalonia cannot await a Closed event handler. Persist critical session state
            // before the first asynchronous boundary so process shutdown cannot overtake it.
            CompleteSessionMetricsRecording();
            FlushPersistedStateOnWindowClose();
            if (_desktopControlServer is not null)
            {
                await _desktopControlServer.DisposeAsync();
                _desktopControlServer = null;
            }

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
			_secretRedactionSession.SnapshotPublished -= OnSecretRedactionSnapshotPublished;
			_secretRedactionSession.Reset();

            // Unsubscribe from tunneled/bubbled events
            RemoveHandler(PointerWheelChangedEvent, OnWindowPointerWheelChanged);
            RemoveHandler(KeyDownEvent, OnKeyDown);
            RemoveHandler(MenuItem.SubmenuOpenedEvent, _themeBrushCoordinator.HandleSubmenuOpened);
            RemoveHandler(MenuItem.SubmenuOpenedEvent, GitBranchMenuScrollBehavior.HandleSubmenuOpened);

            // Unsubscribe from window lifecycle events
            Opened -= OnOpened;
            Closing -= OnWindowClosing;
            Closed -= OnWindowClosed;
            Activated -= OnActivated;
            Deactivated -= OnDeactivated;

            _previewSurfaceController.Dispose();
            CancelAndDisposeWindowOperations();

            _searchFilterController.ClearProjectState();

            // Dispose coordinators
            _memoryCleanup.Dispose();
            _previewWorkspaceController.Dispose();
            _searchFilterController.Dispose();
            _workspacePresentation.Dispose();
            _selectionCoordinator.Dispose();
            _themeBrushCoordinator.Dispose();
            _applicationUpdates.Dispose();
            _statusOperations.Dispose();

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
            ResetPreviewTreePaneVisualState();
            ResetInteractiveFilterCache();
            _metrics.InvalidateComputedCaches();

            // Clear file metrics cache
            _metrics.ClearFileMetricsCache(trimCapacity: true);

            // Clean up repository cache on exit
            _repoCacheService.ClearAllCache();

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
