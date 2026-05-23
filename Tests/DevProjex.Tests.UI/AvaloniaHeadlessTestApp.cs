[assembly: AvaloniaTestApplication(typeof(DevProjex.Tests.UI.AvaloniaHeadlessTestApp))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]
[assembly: CollectionBehavior(DisableTestParallelization = true, MaxParallelThreads = 1)]

namespace DevProjex.Tests.UI;

public static class AvaloniaHeadlessTestApp
{
    public static AppBuilder BuildAvaloniaApp()
    {
        Environment.SetEnvironmentVariable("DEVPROJEX_FAST_UI_TESTS", "1");
        return AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}
