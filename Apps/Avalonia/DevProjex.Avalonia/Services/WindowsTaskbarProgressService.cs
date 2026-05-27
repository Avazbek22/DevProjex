namespace DevProjex.Avalonia.Services;

public sealed class WindowsTaskbarProgressService : ITaskbarProgressService
{
    private const string WindowsHandleDescriptor = "HWND";

    private readonly Func<IWindowsTaskbarProgressNative?> _nativeFactory;
    private WindowsTaskbarProgressController? _controller;
    private bool _disposed;

    public WindowsTaskbarProgressService()
        : this(CreateNativeClient)
    {
    }

    internal WindowsTaskbarProgressService(Func<IWindowsTaskbarProgressNative?> nativeFactory)
    {
        _nativeFactory = nativeFactory;
    }

    public bool IsSupported => OperatingSystem.IsWindows();

    public void Attach(Window window)
    {
        if (_disposed || !OperatingSystem.IsWindows())
            return;

        var platformHandle = window.TryGetPlatformHandle();
        if (platformHandle is null ||
            platformHandle.Handle == 0 ||
            !string.Equals(platformHandle.HandleDescriptor, WindowsHandleDescriptor, StringComparison.Ordinal))
        {
            return;
        }

        var controller = GetOrCreateController();
        controller?.Attach(platformHandle.Handle);
    }

    public void SetIndeterminate() => _controller?.SetIndeterminate();

    public void SetProgress(double percent) => _controller?.SetProgress(percent);

    public void SetPaused() => _controller?.SetPaused();

    public void SetError() => _controller?.SetError();

    public void Clear() => _controller?.Clear();

    private WindowsTaskbarProgressController? GetOrCreateController()
    {
        if (_controller is not null)
            return _controller;

        try
        {
            var native = _nativeFactory();
            if (native is null)
                return null;

            _controller = new WindowsTaskbarProgressController(native);
            return _controller;
        }
        catch
        {
            // Shell integration is optional. A COM failure must never block the app startup.
            return null;
        }
    }

    private static IWindowsTaskbarProgressNative? CreateNativeClient()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        return new WindowsTaskbarProgressNative();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _controller?.Dispose();
        _controller = null;
    }
}
