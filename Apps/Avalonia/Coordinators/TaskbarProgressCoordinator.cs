using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia.Coordinators;

public sealed class TaskbarProgressCoordinator(
    MainWindowViewModel viewModel,
    ITaskbarProgressService taskbarProgressService)
    : IDisposable
{
    private bool _gitCloneProgressActive;
    private bool _gitCloneProgressIsIndeterminate;
    private double? _lastGitClonePercent;

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
        _gitCloneProgressIsIndeterminate = true;
        _lastGitClonePercent = null;
        taskbarProgressService.SetIndeterminate();
    }

    public void UpdateGitClone(string status)
    {
        if (!_gitCloneProgressActive)
            return;

        if (!GitProgressStatusParser.TryParsePercent(status, out var percent) ||
            _lastGitClonePercent == percent)
            return;

        _lastGitClonePercent = percent;
        _gitCloneProgressIsIndeterminate = false;
        taskbarProgressService.SetProgress(percent);
    }

    public void SetGitCloneIndeterminate()
    {
        if (!_gitCloneProgressActive || _gitCloneProgressIsIndeterminate)
            return;

        _gitCloneProgressIsIndeterminate = true;
        _lastGitClonePercent = null;
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
        _gitCloneProgressIsIndeterminate = false;
        _lastGitClonePercent = null;
        SyncWithStatusBar();
    }

    public void Dispose()
    {
        taskbarProgressService.Clear();
        taskbarProgressService.Dispose();
    }
}
