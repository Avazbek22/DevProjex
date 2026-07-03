namespace DevProjex.Avalonia.Services;

public interface ITaskbarProgressService : IDisposable
{
    bool IsSupported { get; }

    void Attach(Window window);

    void SetIndeterminate();

    void SetProgress(double percent);

    void SetPaused();

    void SetError();

    void Clear();
}
