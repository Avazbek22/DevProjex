using DevProjex.Avalonia.Coordinators;
using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia;

public partial class MainWindow : IProjectLoadPipelineHost
{
    MainWindowViewModel IProjectLoadPipelineHost.ViewModel => _viewModel;

    string? IProjectLoadPipelineHost.CurrentCachedRepoPath => _currentCachedRepoPath;

    void IProjectLoadPipelineHost.CaptureProjectLoadCancellationSnapshot()
    {
        _projectLoadCancellation.Capture(CaptureProjectLoadCancellationSnapshot());
    }

    Task IProjectLoadPipelineHost.PrepareSearchAndFilterForProjectLoadAsync() =>
        PrepareSearchAndFilterForProjectLoadAsync();

    void IProjectLoadPipelineHost.CancelBackgroundMemoryCleanup() =>
        CancelBackgroundMemoryCleanup();

    void IProjectLoadPipelineHost.CancelPreviewRefresh() =>
        CancelPreviewRefresh();

    Task IProjectLoadPipelineHost.YieldProjectLoadStartupFrameAsync(CancellationToken cancellationToken) =>
        YieldProjectLoadStartupFrameAsync(cancellationToken);

    void IProjectLoadPipelineHost.ClearPreviousProjectState(bool forceCompactingGc) =>
        ClearPreviousProjectState(forceCompactingGc);

    void IProjectLoadPipelineHost.SetProjectLoadIdentity(string path, bool fromDialog)
    {
        _currentPath = path;
        _viewModel.IsProjectLoaded = true;
        _viewModel.SettingsVisible = true;
        _viewModel.SearchVisible = false;

        if (!fromDialog)
            return;

        _viewModel.ProjectSourceType = ProjectSourceType.LocalFolder;
        _viewModel.CurrentBranch = string.Empty;
        _viewModel.GitBranches.Clear();
        _currentProjectDisplayName = null;
        _currentRepositoryUrl = null;
    }

    void IProjectLoadPipelineHost.UpdateTitle() =>
        UpdateTitle();

    Task IProjectLoadPipelineHost.ReloadProjectAsync(CancellationToken cancellationToken, bool applyStoredProfile) =>
        ReloadProjectAsync(cancellationToken, applyStoredProfile);

    void IProjectLoadPipelineHost.RecordRecentFolder(string path) =>
        RecordRecentFolder(path);

    Task IProjectLoadPipelineHost.DeleteRepositoryDirectoryAsync(
        string path,
        CancellationToken cancellationToken) =>
        Task.Run(
            () => _repoCacheService.DeleteRepositoryDirectory(path),
            cancellationToken);

    void IProjectLoadPipelineHost.ClearCurrentCachedRepoPath()
    {
        _currentCachedRepoPath = null;
    }

    void IProjectLoadPipelineHost.ClearProjectLoadCancellation() =>
        _projectLoadCancellation.Clear();

    bool IProjectLoadPipelineHost.TryApplyActiveProjectLoadCancellationFallback() =>
        TryApplyActiveProjectLoadCancellationFallback();

    void IProjectLoadPipelineHost.ScheduleProjectLoadMemoryCleanup(bool hadLoadedProjectBefore)
    {
        ScheduleBackgroundMemoryCleanup(
            hadLoadedProjectBefore
                ? MemoryCleanupReason.ProjectSwitchPostLoad
                : MemoryCleanupReason.InitialProjectLoad);
    }

    void IProjectLoadPipelineHost.ShowLoadCanceledToast() =>
        _toastService.Show(_localization["Toast.Operation.LoadCanceled"]);
}
