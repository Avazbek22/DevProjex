using Avalonia.Controls.ApplicationLifetimes;
using DevProjex.Application.DesktopControl;
using DevProjex.Avalonia.Services;
using DevProjex.Terminal.DesktopControl;

namespace DevProjex.Avalonia;

public sealed class App : global::Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var desktopRequest = DesktopLaunchRequestStore
                .TryConsumeFromEnvironmentAsync()
                .GetAwaiter()
                .GetResult();
            var diagnosticRequest = DesktopDiagnosticRequestStore.TryConsume();
            var startupOptions = new DesktopStartupOptions(
                OpenRequest: diagnosticRequest is null
                    ? desktopRequest
                    : new DesktopOpenRequest(
                        ProjectPath: diagnosticRequest.ProjectPath,
                        Language: desktopRequest?.Language),
                SessionMetrics: diagnosticRequest is null
                    ? SessionMetricsOptions.Disabled
                    : new SessionMetricsOptions(
                        Enabled: true,
                        ProjectPath: diagnosticRequest.ProjectPath,
                        OutputPath: diagnosticRequest.OutputPath),
                DiagnosticScenario: diagnosticRequest is null
                    ? null
                    : ParseDiagnosticScenario(diagnosticRequest.Scenario),
                ElevationAttempted: desktopRequest?.ElevationAttempted == true);

            var services = AvaloniaCompositionRoot.CreateDefault(startupOptions);
            desktop.MainWindow = new MainWindow(
                startupOptions,
                services);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static DesktopDiagnosticScenario ParseDiagnosticScenario(string scenario) =>
        scenario.Trim().ToLowerInvariant() switch
        {
            "preview-search-retention" => DesktopDiagnosticScenario.PreviewSearchRetention,
            "project-memory-lifecycle" => DesktopDiagnosticScenario.ProjectMemoryLifecycle,
            _ => DesktopDiagnosticScenario.Standard
        };
}
