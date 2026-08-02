using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia.Coordinators;

public sealed class TaskbarProgressCoordinator(
    MainWindowViewModel viewModel,
    ITaskbarProgressService taskbarProgressService)
    : IDisposable
{
    private bool _gitCloneProgressActive;

    public void Attach(Window window)
    {
        taskbarProgressService.Attach(window);
        SyncWithStatusBar();
    }

    public void SyncWithStatusBar()
    {
        if (_gitCloneProgressActive)
            return;

        if (!viewModel.StatusOperationVisible)
        {
            taskbarProgressService.Clear();
            return;
        }

        if (viewModel.StatusProgressIsIndeterminate)
        {
            taskbarProgressService.SetIndeterminate();
            return;
        }

        taskbarProgressService.SetProgress(viewModel.StatusProgressValue);
    }

    public void BeginGitClone()
    {
        _gitCloneProgressActive = true;
        taskbarProgressService.SetIndeterminate();
    }

    public void UpdateGitClone(string status)
    {
        if (!_gitCloneProgressActive)
            return;

        if (GitProgressStatusParser.TryParseTrailingPercent(status, out var percent))
        {
            taskbarProgressService.SetProgress(percent);
            return;
        }

        taskbarProgressService.SetIndeterminate();
    }

    public void SetGitCloneIndeterminate()
    {
        if (_gitCloneProgressActive)
            taskbarProgressService.SetIndeterminate();
    }

    public void MarkGitCloneError()
    {
        if (_gitCloneProgressActive)
            taskbarProgressService.SetError();
    }

    public void CompleteGitClone()
    {
        if (!_gitCloneProgressActive)
            return;

        _gitCloneProgressActive = false;
        SyncWithStatusBar();
    }

    public void Dispose()
    {
        taskbarProgressService.Clear();
        taskbarProgressService.Dispose();
    }
}
