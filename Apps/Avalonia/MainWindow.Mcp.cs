using DevProjex.Avalonia.Services;
using DevProjex.Avalonia.Views;
using DevProjex.Application;
using DevProjex.Infrastructure.TerminalCommands;

namespace DevProjex.Avalonia;

public partial class MainWindow
{
    private async void OnMcpConnectionRequested(object? sender, McpConnectionRequestedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_currentPath) || !_viewModel.IsProjectLoaded)
                return;

            var snapshot = _terminalCommandSetupService.Probe();
            var executablePath = McpConnectionExecutablePathResolver.Resolve(
                snapshot,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            var fragment = McpConnectionFragmentGenerator.Generate(
                e.Client,
                e.Mode,
                executablePath,
                Path.GetFullPath(_currentPath));
            await SetClipboardTextAsync(fragment);
            _toastService.Show(_localization["Toast.Copy.Preview"]);
            await ShowMcpConnectionPathPromptAsync(snapshot);
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(ResolveDesktopExceptionMessage(exception));
        }
    }

    private void OnMcpDocumentationRequested(object? sender, RoutedEventArgs e)
    {
        OpenExternalLink(ProjectLinks.McpDocumentationUrl);
        e.Handled = true;
    }

    private async Task ShowMcpConnectionPathPromptAsync(TerminalCommandSetupSnapshot snapshot)
    {
        var platform = DetectTerminalCommandPlatform();
        if (!McpConnectionPathPromptPolicy.ShouldShow(snapshot.State, platform))
            return;

        var content = McpConnectionPathPromptPolicy.Create(_localization, snapshot, platform);
        var action = await McpConnectionPathDialog.ShowAsync(this, content);
        if (action == McpConnectionPathAction.None)
            return;

        if (action == McpConnectionPathAction.ConfigurePath)
        {
            var pathResult = await Task.Run(_terminalCommandSetupService.ConfigurePath);
            if (!pathResult.Success)
                await ShowErrorAsync(ResolveTerminalCommandSetupFailureMessage());
            return;
        }

        var installResult = await Task.Run(_terminalCommandSetupService.InstallOrRepair);
        if (!installResult.Success)
        {
            await ShowErrorAsync(ResolveTerminalCommandSetupFailureMessage());
            return;
        }

        if (RequiresTerminalCommandPathConfiguration(installResult.Snapshot))
        {
            var pathResult = await Task.Run(_terminalCommandSetupService.ConfigurePath);
            if (!pathResult.Success)
                await ShowErrorAsync(ResolveTerminalCommandSetupFailureMessage());
        }
    }

    internal static TerminalCommandHostPlatform DetectTerminalCommandPlatform()
    {
        if (OperatingSystem.IsWindows())
            return TerminalCommandHostPlatform.Windows;
        if (OperatingSystem.IsLinux())
            return TerminalCommandHostPlatform.Linux;
        if (OperatingSystem.IsMacOS())
            return TerminalCommandHostPlatform.MacOS;
        return TerminalCommandHostPlatform.Other;
    }
}
