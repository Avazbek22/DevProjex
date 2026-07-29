using DevProjex.Avalonia.Services;
using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.DesktopControl;

namespace DevProjex.Avalonia;

internal static class Program
{
    // Conservative GPU cache limit to avoid long-session native memory growth.
    private const long SkiaGpuCacheLimitBytes = 96L * 1024 * 1024;

    [STAThread]
    public static int Main(string[] args)
    {
        args = DesktopLaunchRequestStore.PromoteInternalInvocation(args);
        var hasConsole = WindowsConsoleBridge.EnsureAttached();
        var environment = new InvocationEnvironment(hasConsole);
        var route = ProcessInvocationRouter.Resolve(
            args,
            environment,
            DesktopLaunchRequestStore.HasPendingRequest ||
            DesktopDiagnosticRequestStore.HasPendingRequest,
            isFrameworkDependentLaunch: !ProcessEntryPointResolver.IsSingleFile());
        if (route == ProcessInvocationMode.Terminal)
        {
            using var cancellation = TerminalCancellationCoordinator.Register();
            return new TerminalApplication(
                    environment,
                    developerCommandRunner: new AvaloniaDeveloperCommandRunner(
                        environment.Output,
                        environment.Error))
                .RunAsync(args, cancellation.Token)
                .GetAwaiter()
                .GetResult();
        }

        return BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime([]);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new SkiaOptions
            {
                MaxGpuResourceSizeBytes = SkiaGpuCacheLimitBytes
            });

        if (OperatingSystem.IsWindows())
            builder = builder.With(CreateWin32PlatformOptions());

#if DEBUG
        builder = builder.LogToTrace();
#endif

        return builder;
    }

    private static Win32PlatformOptions CreateWin32PlatformOptions()
    {
        var options = new Win32PlatformOptions
        {
            // Keep decorated main windows sharp by default. Popup-like and borderless
            // surfaces opt into their own radii right before Avalonia creates their
            // WinUI composition backdrop; using one rounded default reopens the tiny
            // menu-belt corner holes under the custom title bar on Windows.
            WinUICompositionBackdropCornerRadius = null
        };

        CompositionBackdropCornerRadiusCoordinator.Attach(options);
        return options;
    }
}
