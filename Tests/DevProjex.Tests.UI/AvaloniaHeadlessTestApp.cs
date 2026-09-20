[assembly: AvaloniaTestApplication(typeof(DevProjex.Tests.UI.AvaloniaHeadlessTestApp))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]
[assembly: CollectionBehavior(DisableTestParallelization = true, MaxParallelThreads = 1)]

namespace DevProjex.Tests.UI;

public static class AvaloniaHeadlessTestApp
{
    public static AppBuilder BuildAvaloniaApp()
    {
        Environment.SetEnvironmentVariable("DEVPROJEX_FAST_UI_TESTS", "1");
        var captureSnapshots = string.Equals(
            Environment.GetEnvironmentVariable("DEVPROJEX_CAPTURE_AGENT_JOURNAL"),
            "1",
            StringComparison.Ordinal);
        var builder = AppBuilder.Configure<App>();
        if (captureSnapshots)
            builder = builder.UseSkia();
        return builder.UseHeadless(new AvaloniaHeadlessPlatformOptions
        {
            Fps = 120,
            ShouldRenderOnUIThread = true,
            UseHeadlessDrawing = !captureSnapshots
        });
    }
}
