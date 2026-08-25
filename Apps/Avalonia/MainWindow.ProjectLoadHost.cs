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

    void IProjectLoadPipelineHost.ClearPreviousProjectState(
		bool forceCompactingGc,
		bool preserveProjectSessions) =>
        ClearPreviousProjectState(forceCompactingGc, preserveProjectSessions);

    void IProjectLoadPipelineHost.SetProjectLoadIdentity(string path, bool fromDialog)
    {
        _currentPath = path;
		_viewModel.IsProjectLoadInProgress = true;
        var shouldAnimateProjectTools =
            _viewModel.IsToolAnimationEnabled &&
            (_workspacePresentation.IsSettingsAnimating ||
             SettingsPanelRevealPolicy.ShouldRunInitialReveal(
                 settingsVisible: true,
                 settingsAnimating: false,
                 _workspacePresentation.HasVisibleSettingsPanelWidth()));
        _topMenuBar?.PrepareProjectToolsReveal(shouldAnimateProjectTools);
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

    Task<bool> IProjectLoadPipelineHost.ReloadProjectAsync(CancellationToken cancellationToken, bool applyStoredProfile) =>
        ReloadProjectAsync(
            cancellationToken,
            applyStoredProfile,
            preserveTreeState: false);

    Task IProjectLoadPipelineHost.RecordRecentFolderAsync(
        string path,
        CancellationToken cancellationToken) =>
        RecordRecentFolderAsync(path, cancellationToken);

    void IProjectLoadPipelineHost.ReleaseCurrentRepositorySession()
    {
        Interlocked.Exchange(ref _currentRepositorySession, null)?.Dispose();
        _currentCachedRepoPath = null;
    }

    void IProjectLoadPipelineHost.ClearProjectLoadCancellation() =>
        _projectLoadCancellation.Clear();

    bool IProjectLoadPipelineHost.TryApplyActiveProjectLoadCancellationFallback() =>
        TryApplyActiveProjectLoadCancellationFallback();

    void IProjectLoadPipelineHost.ScheduleProjectLoadMemoryCleanup(bool hadLoadedProjectBefore)
    {
        // Cleanup's own delay runs concurrently with this task, so it is not a substitute for the
        // shared visual gate. Compaction must remain ineligible until the final layout has settled.
        ScheduleBackgroundMemoryCleanup(
            hadLoadedProjectBefore
                ? MemoryCleanupReason.ProjectSwitchPostLoad
                : MemoryCleanupReason.InitialProjectLoad,
            _postLoadVisualReadyTask);
    }

    void IProjectLoadPipelineHost.ShowLoadCanceledToast() =>
        _toastService.Show(_localization["Toast.Operation.LoadCanceled"]);
}
