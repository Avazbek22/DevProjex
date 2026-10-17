using DevProjex.Infrastructure.FileSystem;
using DevProjex.Infrastructure.Git;
using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Avalonia;

public partial class MainWindow
{
    private readonly FlatpakHostPathResolver _flatpakHostPaths = new(Services.FlatpakDocumentPortal.GetHostPathAsync);
    private readonly Dictionary<string, string> _flatpakDisplayPaths = new(StringComparer.Ordinal);

    private string? GetFlatpakDisplayPath(string? path) =>
        FlatpakEnvironment.Current.IsActive && path is not null && _flatpakDisplayPaths.TryGetValue(path, out var host)
            ? host
            : path;

    private async Task ResolveFlatpakProjectPresentationAsync(CancellationToken cancellationToken)
    {
        if (!FlatpakEnvironment.Current.IsActive || _currentPath is null)
            return;
        var path = _currentPath;
        var hostPath = await _flatpakHostPaths.ResolveAsync(path, cancellationToken);
        if (_currentPath != path)
            return;
        if (_flatpakDisplayPaths.Count >= 64)
            _flatpakDisplayPaths.Remove(_flatpakDisplayPaths.Keys.First());
        _flatpakDisplayPaths[path] = hostPath;
        ApplyWindowTitle();
    }

    private async Task InitializeFlatpakDiagnosticsAsync(CancellationToken cancellationToken)
    {
        if (!FlatpakEnvironment.Current.IsActive)
            return;
        var version = await Task.Run(() => GitRuntimeInformation.VersionDisplay, cancellationToken);
        _viewModel.SetFlatpakDiagnostics(version, UserDataPathResolver.GetLocalDataRoot());
    }
}
