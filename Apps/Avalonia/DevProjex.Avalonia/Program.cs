using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia;

internal static class Program
{
    // Conservative GPU cache limit to avoid long-session native memory growth.
    private const long SkiaGpuCacheLimitBytes = 96L * 1024 * 1024;

    [STAThread]
    public static int Main(string[] args)
    {
        var parseResult = CommandLineOptions.Parse(args);
        if (CommandLineAutomationRunner.ShouldRunBeforeAvalonia(parseResult))
        {
            ConfigureCommandLineEncoding();
            WindowsParentConsole.AttachForCommandLine();
            return CommandLineAutomationRunner.RunUtilityOrHeadlessAsync(parseResult)
                .GetAwaiter()
                .GetResult();
        }

        return BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
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

    private static void ConfigureCommandLineEncoding()
    {
        try
        {
            // Headless export can be piped or redirected on Windows runners whose active
            // code page is not UTF-8. Pin stdout/stderr to UTF-8 so Unicode paths remain
            // machine-readable instead of being downgraded to question marks.
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.OutputEncoding = utf8NoBom;
        }
        catch
        {
            // Some hosts do not allow changing console encoding. Export still continues;
            // process-level smoke tests cover the normal redirected stdout path.
        }
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
