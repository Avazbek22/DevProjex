namespace DevProjex.Avalonia.Services;

public sealed class NoopTaskbarProgressService : ITaskbarProgressService
{
    public bool IsSupported => false;

    public void Attach(Window window)
    {
    }

    public void SetIndeterminate()
    {
    }

    public void SetProgress(double percent)
    {
    }

    public void SetPaused()
    {
    }

    public void SetError()
    {
    }

    public void Clear()
    {
    }

    public void Dispose()
    {
    }
}
