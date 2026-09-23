using Avalonia.Controls.ApplicationLifetimes;
using DevProjex.Application.DesktopControl;
using DevProjex.Avalonia.Services;
using DevProjex.Infrastructure.Persistence;
using DevProjex.Terminal.DesktopControl;

namespace DevProjex.Avalonia;

public sealed class App : global::Avalonia.Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ThemedToolTipService.Initialize();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var storeCaptureRequest = StoreScreenshotCaptureRequestStore.TryConsume();
            var isolatedDataRoot = AvaloniaCompositionRoot.ResolveAppDataPathProvider(storeCaptureRequest);
            if (isolatedDataRoot is null)
            {
                var status = StoreUserDataMigrationAdmission.Run();
                if (!StoreUserDataMigrationAdmission.IsReady(status))
                {
                    var language = storeCaptureRequest is not null &&
                                   AppLanguageUtility.TryParseCode(storeCaptureRequest.LanguageCode, out var parsedLanguage)
                        ? parsedLanguage
                        : AppLanguageUtility.DetectSystemLanguage();
                    desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    desktop.MainWindow = StoreMigrationRecoveryWindow.Create(
                        new LocalizationService(new JsonLocalizationCatalog(), language),
                        () => StoreUserDataMigrationAdmission.Run(),
                        () =>
                        {
                            var mainWindow = CreateMainWindow(storeCaptureRequest);
                            desktop.MainWindow = mainWindow;
                            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                            mainWindow.Show();
                        },
                        () => desktop.Shutdown(1));
                    base.OnFrameworkInitializationCompleted();
                    return;
                }
            }

            desktop.MainWindow = CreateMainWindow(storeCaptureRequest);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static MainWindow CreateMainWindow(StoreScreenshotCaptureRequest? storeCaptureRequest)
    {
        var desktopRequest = DesktopLaunchRequestStore
            .TryConsumeFromEnvironmentAsync()
            .GetAwaiter()
            .GetResult();
        var diagnosticRequest = DesktopDiagnosticRequestStore.TryConsume();
        var captureLanguage = storeCaptureRequest is not null &&
                              AppLanguageUtility.TryParseCode(
                                  storeCaptureRequest.LanguageCode,
                                  out var parsedCaptureLanguage)
            ? parsedCaptureLanguage
            : (AppLanguage?)null;
        var startupOptions = new DesktopStartupOptions(
            OpenRequest: storeCaptureRequest is not null
                ? new DesktopOpenRequest(Language: captureLanguage)
                : diagnosticRequest is null
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
            StoreScreenshotCapture: storeCaptureRequest,
            ElevationAttempted: desktopRequest?.ElevationAttempted == true);

        var services = AvaloniaCompositionRoot.CreateAfterMigrationAdmission(startupOptions);
        return new MainWindow(startupOptions, services);
    }

    private static DesktopDiagnosticScenario ParseDiagnosticScenario(string scenario) =>
        scenario.Trim().ToLowerInvariant() switch
        {
            "preview-search-retention" => DesktopDiagnosticScenario.PreviewSearchRetention,
            "project-memory-lifecycle" => DesktopDiagnosticScenario.ProjectMemoryLifecycle,
            _ => DesktopDiagnosticScenario.Standard
        };
}
