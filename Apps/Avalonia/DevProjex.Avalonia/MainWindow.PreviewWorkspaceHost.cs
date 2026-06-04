using DevProjex.Application.Preview;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia;

public partial class MainWindow : IPreviewWorkspacePipelineHost
{
    MainWindowViewModel IPreviewWorkspacePipelineHost.ViewModel => _viewModel;

    bool IPreviewWorkspacePipelineHost.IsPreviewModeSwitchInProgress => _previewModeSwitchInProgress;

    IPreviewTextDocument? IPreviewWorkspacePipelineHost.CurrentPreviewDocument => _viewModel.PreviewDocument;

    bool IPreviewWorkspacePipelineHost.EnsurePreviewTreeReady() => EnsureTreeReady();

    void IPreviewWorkspacePipelineHost.ApplyPreviewNoDataText() =>
        ApplyPreviewText(_viewModel.PreviewNoDataText);

    long IPreviewWorkspacePipelineHost.BeginPreviewBuildOperation(CancellationTokenSource previewCts)
    {
        return _statusOperations.Begin(
            _viewModel.StatusOperationPreparingPreview,
            indeterminate: true,
            operationType: StatusOperationType.PreviewBuild,
            cancelAction: () =>
            {
                previewCts.Cancel();
                _toastService.Show(_viewModel.ToastPreviewCanceled);
            });
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
        var currentTreeRoot = _currentTree?.Root;
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
            PathPresentation: pathPresentation,
            CacheKey: cacheKey);
    }

    bool IPreviewWorkspacePipelineHost.IsCurrentPreviewCacheHit(PreviewCacheKeyData key) =>
        IsCurrentPreviewCacheHit(key);

    void IPreviewWorkspacePipelineHost.ApplyPreviewDocument(IPreviewTextDocument document) =>
        ApplyPreviewDocument(document);

    void IPreviewWorkspacePipelineHost.ApplyPreviewText(string text) =>
        ApplyPreviewText(text);

    void IPreviewWorkspacePipelineHost.ApplyPreviewText(string text, int lineCount) =>
        ApplyPreviewText(text, lineCount);

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
            input.PathPresentation,
            cancellationToken);
    }

    void IPreviewWorkspacePipelineHost.CachePreview(PreviewCacheKeyData key) =>
        CachePreview(key);

    void IPreviewWorkspacePipelineHost.InvalidatePreviewCache() =>
        InvalidatePreviewCache();

    void IPreviewWorkspacePipelineHost.SchedulePreviewMemoryCleanup(bool force) =>
        SchedulePreviewMemoryCleanup(force);

    void IPreviewWorkspacePipelineHost.SchedulePreviewMemoryCleanupForDocument(IPreviewTextDocument document)
    {
        SchedulePreviewMemoryCleanup(
            force: PreviewFileCollectionPolicy.ShouldForcePreviewMemoryCleanup(
                document.CharacterCount,
                document.LineCount));
    }
}
