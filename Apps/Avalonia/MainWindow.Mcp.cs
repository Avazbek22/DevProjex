using DevProjex.Avalonia.Services;
using DevProjex.Avalonia.Views;
using DevProjex.Application;
using DevProjex.Infrastructure.TerminalCommands;

namespace DevProjex.Avalonia;

public partial class MainWindow
{
    private readonly IAgentJournalReader _agentJournalReader;
    private readonly IAgentJournalReceiptFormatter _agentJournalReceiptFormatter;
    private AgentJournalWindow? _agentJournalWindow;

    private void OnMcpJournalRequested(object? sender, RoutedEventArgs e)
    {
        if (_agentJournalWindow is { } existing)
        {
            existing.Show();
            existing.Activate();
            e.Handled = true;
            return;
        }

        var window = new AgentJournalWindow(
            this,
            _agentJournalReader,
            _agentJournalReceiptFormatter,
            _localization,
            _viewModel.IsProjectLoaded ? _currentPath : null);
        _agentJournalWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_agentJournalWindow, window))
                _agentJournalWindow = null;
        };
        window.Show(this);
        e.Handled = true;
    }

    private async void OnMcpConnectionRequested(object? sender, McpConnectionRequestedEventArgs e)
    {
        McpConnectionRequest? request = null;
        try
        {
            if (string.IsNullOrWhiteSpace(_currentPath) || !_viewModel.IsProjectLoaded)
                return;

            var snapshot = _terminalCommandSetupService.Probe();
            var executablePath = McpConnectionExecutablePathResolver.Resolve(
                snapshot,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            request = new McpConnectionRequest(
                e.Client,
                e.Mode,
                executablePath,
                Path.GetFullPath(_currentPath));
            var cancellationToken = _windowLifetimeCts?.Token ?? CancellationToken.None;
            var result = await ConnectMcpClientAsync(request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (result.Succeeded)
            {
                if (result.Replaced)
                    _toastService.Show(result.UserMessage);
                var launchResult = await _mcpClientLaunchService.OpenAsync(
                    new McpClientLaunchRequest(request.Client, request.ProjectRoot),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!launchResult.Succeeded)
                    await ShowMcpLaunchFailureAsync(request, launchResult);
                await ShowMcpConnectionPathPromptAsync(snapshot);
                return;
            }

            await ShowMcpManualConfigurationAsync(result, request);
        }
        catch (OperationCanceledException) when (_windowLifetimeCts?.IsCancellationRequested == true)
        {
            // Window shutdown owns cancellation of an in-flight connection.
        }
        catch (Exception exception)
        {
            if (request is null)
            {
                await ShowErrorAsync(ResolveDesktopExceptionMessage(exception));
                return;
            }

            var result = new McpConnectionResult(
                McpConnectionStatus.ProcessFailed,
                ResolveDesktopExceptionMessage(exception),
                ManualConfiguration: TryCreatePrintableConfiguration(request));
            await ShowMcpManualConfigurationAsync(result, request);
        }
    }

    private async Task<McpConnectionResult> ConnectMcpClientAsync(
        McpConnectionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Client != McpConnectionClient.Codex ||
            _mcpConnectionService is not IMcpConnectionReplacementService replacementService)
        {
            return await _mcpConnectionService.ConnectAsync(request, cancellationToken);
        }

        var inspection = await replacementService.InspectAsync(request, cancellationToken);
        if (!inspection.RequiresProjectReplacement ||
            string.IsNullOrWhiteSpace(inspection.ExistingProjectRoot))
        {
            return await _mcpConnectionService.ConnectAsync(request, cancellationToken);
        }

        var confirmed = await MessageDialog.ShowConfirmationAsync(
            this,
            _localization["Mcp.Connect.ReplaceTitle"],
            _localization.Format(
                "Mcp.Connect.ReplacePrompt",
                inspection.ExistingProjectRoot,
                request.ProjectRoot),
            _localization["Mcp.Connect.ReplaceConfirm"],
            _localization["Dialog.Cancel"]);
        if (!confirmed)
        {
            return new McpConnectionResult(
                McpConnectionStatus.InvalidConfiguration,
                _localization["Mcp.Connect.ReplaceCanceled"]);
        }

        return await replacementService.ReplaceAsync(
            request,
            inspection.ExistingProjectRoot,
            cancellationToken);
    }

    private void OnMcpDocumentationRequested(object? sender, RoutedEventArgs e)
    {
        OpenExternalLink(ProjectLinks.McpDocumentationUrl);
        e.Handled = true;
    }

    private async Task ShowMcpManualConfigurationAsync(
        McpConnectionResult result,
        McpConnectionRequest request,
        McpManualPayloadPresentation presentation = McpManualPayloadPresentation.Configuration)
    {
        var content = McpManualConfigurationDialog.CreateContent(
            _localization,
            result,
            TryCreatePrintableConfiguration(request),
            presentation);
        await McpManualConfigurationDialog.ShowAsync(this, content);
    }

    private Task ShowMcpLaunchFailureAsync(
        McpConnectionRequest request,
        McpClientLaunchResult launchResult)
    {
        var error = string.IsNullOrWhiteSpace(launchResult.ErrorMessage)
            ? _localization["Mcp.Connect.UnknownError"]
            : launchResult.ErrorMessage;
        var result = new McpConnectionResult(
            McpConnectionStatus.ProcessFailed,
            _localization.Format(
                "Mcp.Open.FailedAfterConnection",
                GetMcpClientDisplayName(request.Client),
                error),
            ManualConfiguration: launchResult.ManualCommand);
        return ShowMcpManualConfigurationAsync(
            result,
            request,
            McpManualPayloadPresentation.Command);
    }

    private static string GetMcpClientDisplayName(McpConnectionClient client) => client switch
    {
        McpConnectionClient.ClaudeCode => "Claude Code",
        McpConnectionClient.Codex => "Codex",
        McpConnectionClient.Cursor => "Cursor",
        McpConnectionClient.VsCode => "VS Code",
        McpConnectionClient.Json => "JSON",
        _ => throw new ArgumentOutOfRangeException(nameof(client), client, null)
    };

    private string TryCreatePrintableConfiguration(McpConnectionRequest request)
    {
        try
        {
            return _mcpConnectionService.CreatePrintableConfiguration(request);
        }
        catch
        {
            return string.Empty;
        }
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
