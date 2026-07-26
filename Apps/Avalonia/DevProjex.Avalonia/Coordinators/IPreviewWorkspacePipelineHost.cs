using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia.Coordinators;

internal interface IPreviewWorkspacePipelineHost
{
    MainWindowViewModel ViewModel { get; }

    bool IsPreviewModeSwitchInProgress { get; }

    bool EnsurePreviewTreeReady();

    void ApplyPreviewNoDataText();

    long BeginPreviewBuildOperation(CancellationTokenSource previewCts);

    void CompletePreviewBuildOperation(long operationId);

    PreviewRefreshInput CapturePreviewRefreshInput();

    bool IsCurrentPreviewCacheHit(PreviewCacheKeyData key);

    IPreviewTextDocument? CurrentPreviewDocument { get; }

    void ApplyPreviewDocument(IPreviewTextDocument document);

    void ApplyPreviewText(string text);

    void ApplyPreviewText(string text, int lineCount);

    void ClearPreviewDocument();

    Task<PreviewWarmupSnapshot?> TryBuildPreviewWarmupSnapshotAsync(
        PreviewRefreshInput input,
        CancellationToken cancellationToken);

    PreviewBuildResult BuildPreviewDocument(
        PreviewRefreshInput input,
        CancellationToken cancellationToken);

    void CachePreview(PreviewCacheKeyData key);

    void InvalidatePreviewCache();

    void SchedulePreviewMemoryCleanup();

    void SchedulePreviewRebuildMemoryCleanup();
}
