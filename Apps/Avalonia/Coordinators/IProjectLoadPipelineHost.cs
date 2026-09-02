namespace DevProjex.Avalonia.Coordinators;

internal interface IProjectLoadPipelineHost
{
    MainWindowViewModel ViewModel { get; }

    string? CurrentCachedRepoPath { get; }

    void CaptureProjectLoadCancellationSnapshot();

    Task PrepareSearchAndFilterForProjectLoadAsync();

    void CancelBackgroundMemoryCleanup();

    void CancelPreviewRefresh();

    Task YieldProjectLoadStartupFrameAsync(CancellationToken cancellationToken);

    void ClearPreviousProjectState(bool forceCompactingGc, bool preserveProjectSessions);

    void SetProjectLoadIdentity(string path, bool fromDialog);

    void UpdateTitle();

    Task<bool> ReloadProjectAsync(CancellationToken cancellationToken, bool applyStoredProfile);

    Task RecordRecentFolderAsync(string path, CancellationToken cancellationToken);

    void ReleaseCurrentRepositorySession();

    void ClearProjectLoadCancellation();

    bool TryApplyActiveProjectLoadCancellationFallback();

    void ScheduleProjectLoadMemoryCleanup(bool hadLoadedProjectBefore);

    void ShowLoadCanceledToast();
}
