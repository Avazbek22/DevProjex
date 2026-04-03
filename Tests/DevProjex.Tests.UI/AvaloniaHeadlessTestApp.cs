[assembly: AvaloniaTestApplication(typeof(DevProjex.Tests.UI.AvaloniaHeadlessTestApp))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerTest)]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace DevProjex.Tests.UI;

public static class AvaloniaHeadlessTestApp
{
    public static AppBuilder BuildAvaloniaApp()
    {
        Environment.SetEnvironmentVariable("DEVPROJEX_FAST_UI_TESTS", "1");
        return Program.BuildAvaloniaApp().UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}
