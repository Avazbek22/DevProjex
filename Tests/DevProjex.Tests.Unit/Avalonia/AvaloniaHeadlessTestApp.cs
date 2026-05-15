[assembly: AvaloniaTestApplication(typeof(DevProjex.Tests.Unit.Avalonia.AvaloniaHeadlessTestApp))]

namespace DevProjex.Tests.Unit.Avalonia;

public static class AvaloniaHeadlessTestApp
{
	public static AppBuilder BuildAvaloniaApp()
		=> AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
