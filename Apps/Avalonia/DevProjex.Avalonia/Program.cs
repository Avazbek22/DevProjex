using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia;

internal static class Program
{
    // Conservative GPU cache limit to avoid long-session native memory growth.
    private const long SkiaGpuCacheLimitBytes = 96L * 1024 * 1024;
    private const float PopupBackdropCornerRadius = 8f;

    [STAThread]
    public static int Main(string[] args)
    {
        var parseResult = CommandLineOptions.Parse(args);
        if (CommandLineAutomationRunner.ShouldRunBeforeAvalonia(parseResult))
        {
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

    private static Win32PlatformOptions CreateWin32PlatformOptions()
    {
        return new Win32PlatformOptions
        {
            // Composition blur brushes do not inherit Border.CornerRadius from popup content.
            WinUICompositionBackdropCornerRadius = PopupBackdropCornerRadius
        };
    }
}
