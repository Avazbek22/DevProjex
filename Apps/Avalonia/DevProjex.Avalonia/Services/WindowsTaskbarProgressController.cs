namespace DevProjex.Avalonia.Services;

internal enum WindowsTaskbarProgressState
{
    NoProgress = 0,
    Indeterminate = 1,
    Normal = 2,
    Error = 4,
    Paused = 8
}

internal interface IWindowsTaskbarProgressNative : IDisposable
{
    bool TrySetProgressState(nint windowHandle, WindowsTaskbarProgressState state);

    bool TrySetProgressValue(nint windowHandle, ulong completed, ulong total);
}

internal sealed class WindowsTaskbarProgressController(IWindowsTaskbarProgressNative native) : IDisposable
{
    private const ulong ProgressScale = 10_000;

    private nint _windowHandle;
    private bool _attached;
    private bool _disposed;
    private WindowsTaskbarProgressState? _lastState;
    private ulong? _lastCompleted;

    public bool Attach(nint windowHandle)
    {
        if (_disposed || windowHandle == 0)
            return false;

        _windowHandle = windowHandle;
        _attached = true;
        _lastState = null;
        _lastCompleted = null;
        return true;
    }

    public bool SetIndeterminate() => SetState(WindowsTaskbarProgressState.Indeterminate);

    public bool SetPaused() => SetState(WindowsTaskbarProgressState.Paused);

    public bool SetError() => SetState(WindowsTaskbarProgressState.Error);

    public bool Clear()
    {
        _lastCompleted = null;
        return SetState(WindowsTaskbarProgressState.NoProgress);
    }

    public bool SetProgress(double percent)
    {
        if (!_attached || _disposed)
            return false;

        var completed = PercentToProgressValue(percent);
        var stateChanged = EnsureState(WindowsTaskbarProgressState.Normal);

        if (_lastCompleted == completed)
            return stateChanged;

        if (!native.TrySetProgressValue(_windowHandle, completed, ProgressScale))
            return false;

        _lastCompleted = completed;
        return true;
    }

    private bool SetState(WindowsTaskbarProgressState state)
    {
        if (!_attached || _disposed)
            return false;

        return EnsureState(state);
    }

    private bool EnsureState(WindowsTaskbarProgressState state)
    {
        if (_lastState == state)
            return true;

        if (!native.TrySetProgressState(_windowHandle, state))
            return false;

        _lastState = state;
        return true;
    }

    private static ulong PercentToProgressValue(double percent)
    {
        if (double.IsNaN(percent) || double.IsInfinity(percent))
            return 0;

        var clamped = Math.Clamp(percent, 0, 100);
        return (ulong)Math.Round(clamped / 100d * ProgressScale, MidpointRounding.AwayFromZero);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        try
        {
            Clear();
        }
        finally
        {
            _disposed = true;
            native.Dispose();
        }
    }
}
