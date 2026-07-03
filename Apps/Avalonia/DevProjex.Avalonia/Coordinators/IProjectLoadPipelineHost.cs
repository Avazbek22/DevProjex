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

    void ClearPreviousProjectState(bool forceCompactingGc);

    void SetProjectLoadIdentity(string path, bool fromDialog);

    void UpdateTitle();

    Task ReloadProjectAsync(CancellationToken cancellationToken, bool applyStoredProfile);

    void RecordRecentFolder(string path);

    void DeleteRepositoryDirectory(string path);

    void ClearCurrentCachedRepoPath();

    void ClearProjectLoadCancellation();

    bool TryApplyActiveProjectLoadCancellationFallback();

    void ScheduleProjectLoadMemoryCleanup(bool hadLoadedProjectBefore);

    void ShowLoadCanceledToast();
}
