using DevProjex.Avalonia.Coordinators;
using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia;

public partial class MainWindow : IPreviewWorkspacePipelineHost
{
    MainWindowViewModel IPreviewWorkspacePipelineHost.ViewModel => _viewModel;

    bool IPreviewWorkspacePipelineHost.IsPreviewModeSwitchInProgress =>
        _previewWorkspaceController.IsModeSwitchInProgress;

    IPreviewTextDocument? IPreviewWorkspacePipelineHost.CurrentPreviewDocument => _viewModel.PreviewDocument;

    bool IPreviewWorkspacePipelineHost.EnsurePreviewTreeReady() => EnsureTreeReady();

    void IPreviewWorkspacePipelineHost.ApplyPreviewNoDataText() =>
        ApplyPreviewText(_viewModel.PreviewNoDataText);

    long IPreviewWorkspacePipelineHost.BeginPreviewBuildOperation(CancellationTokenSource previewCts)
    {
        _sessionMetrics.RecordPreviewBuildStarted(
            _viewModel.SelectedPreviewContentMode);
        return _statusOperations.Begin(
            _viewModel.StatusOperationPreparingPreview,
            indeterminate: true,
            operationType: StatusOperationType.PreviewBuild,
            cancelAction: () =>
            {
                previewCts.Cancel();
                _toastService.Show(_viewModel.ToastPreviewCanceled);
            },
            presentation: StatusOperationPresentation.ExtendedDelay);
    }

    void IPreviewWorkspacePipelineHost.CompletePreviewBuildOperation(long operationId) =>
        _statusOperations.Complete(operationId);

    PreviewRefreshInput IPreviewWorkspacePipelineHost.CapturePreviewRefreshInput()
    {
        // Capture every UI-dependent input once. This keeps a preview refresh stable even if
        // selection changes while the background document build is already running.
        var selectedPaths = GetCheckedPaths();
        var selectedMode = _viewModel.SelectedPreviewContentMode;
        var treeFormat = GetCurrentTreeTextFormat();
        var hasSelection = selectedPaths.Count > 0;
        var currentPath = _currentPath;
        var currentTree = _currentTree;
        var currentTreeRoot = currentTree?.Root;
        var pathPresentation = CreateExportPathPresentation();
        var cacheKey = PreviewFileCollectionPolicy.BuildPreviewCacheKey(
            projectPath: currentPath,
            treeRoot: currentTreeRoot,
            mode: selectedMode,
            treeFormat: treeFormat,
            selectedPaths: selectedPaths);

        return new PreviewRefreshInput(
            SelectedMode: selectedMode,
            SelectedPaths: selectedPaths,
            HasSelection: hasSelection,
            TreeFormat: treeFormat,
            NoCheckedFilesText: _localization["Msg.NoCheckedFilesShort"],
            NoTextContentText: _localization["Msg.NoTextContent"],
            NoDataText: _viewModel.PreviewNoDataText,
            CurrentPath: currentPath,
            CurrentTreeRoot: currentTreeRoot,
            CurrentTreeOrderedFilePaths: currentTree?.OrderedFilePaths,
            PathPresentation: pathPresentation,
            CacheKey: cacheKey);
    }

    bool IPreviewWorkspacePipelineHost.IsCurrentPreviewCacheHit(PreviewCacheKeyData key) =>
        IsCurrentPreviewCacheHit(key);

    void IPreviewWorkspacePipelineHost.ApplyPreviewDocument(IPreviewTextDocument document)
    {
        ApplyPreviewDocument(document);
        _sessionMetrics.RecordPreviewContentPublished(
            _viewModel.SelectedPreviewContentMode,
            document.CharacterCount,
            document.LineCount);
    }

    void IPreviewWorkspacePipelineHost.ApplyPreviewText(string text)
    {
        ApplyPreviewText(text);
        _sessionMetrics.RecordPreviewContentPublished(
            _viewModel.SelectedPreviewContentMode,
            text.Length,
            PreviewFileCollectionPolicy.CountPreviewLines(text));
    }

    void IPreviewWorkspacePipelineHost.ApplyPreviewText(string text, int lineCount)
    {
        ApplyPreviewText(text, lineCount);
        _sessionMetrics.RecordPreviewContentPublished(
            _viewModel.SelectedPreviewContentMode,
            text.Length,
            lineCount);
    }

    string IPreviewWorkspacePipelineHost.ResolvePreviewErrorMessage(Exception exception) =>
        ResolveUserFacingOutputErrorMessage(exception);

	void IPreviewWorkspacePipelineHost.HandlePreviewBuildFailure(Exception exception) =>
		HandlePreviewSecretAnalysisFailure(exception);

    void IPreviewWorkspacePipelineHost.ClearPreviewDocument() =>
        ClearPreviewDocument();

    Task<PreviewWarmupSnapshot?> IPreviewWorkspacePipelineHost.TryBuildPreviewWarmupSnapshotAsync(
        PreviewRefreshInput input,
        CancellationToken cancellationToken)
    {
        return TryBuildPreviewWarmupSnapshotAsync(
            mode: input.SelectedMode,
            treeFormat: input.TreeFormat,
            hasSelection: input.HasSelection,
            selectedPaths: input.SelectedPaths,
            currentPath: input.CurrentPath,
            currentTreeRoot: input.CurrentTreeRoot,
            currentTreeOrderedFilePaths: input.CurrentTreeOrderedFilePaths,
            pathPresentation: input.PathPresentation,
            noTextContentText: input.NoTextContentText,
            noCheckedFilesText: input.NoCheckedFilesText,
            cancellationToken: cancellationToken);
    }

    PreviewBuildResult IPreviewWorkspacePipelineHost.BuildPreviewDocument(
        PreviewRefreshInput input,
        CancellationToken cancellationToken)
    {
        return BuildPreviewDocument(
            input.SelectedMode,
            input.SelectedPaths,
            input.HasSelection,
            input.TreeFormat,
            input.NoCheckedFilesText,
            input.NoTextContentText,
            input.NoDataText,
            input.CurrentPath,
            input.CurrentTreeRoot,
            input.CurrentTreeOrderedFilePaths,
            input.PathPresentation,
            cancellationToken);
    }

    void IPreviewWorkspacePipelineHost.CachePreview(PreviewCacheKeyData key) =>
        CachePreview(key);

    void IPreviewWorkspacePipelineHost.InvalidatePreviewCache() =>
        InvalidatePreviewCache();

    void IPreviewWorkspacePipelineHost.SchedulePreviewMemoryCleanup() =>
        SchedulePreviewMemoryCleanup();

    void IPreviewWorkspacePipelineHost.SchedulePreviewRebuildMemoryCleanup() =>
        SchedulePreviewRebuildMemoryCleanup();
}
