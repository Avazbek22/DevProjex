using DevProjex.Infrastructure.LiveContext;
using DevProjex.Terminal.DesktopControl;
using System.Reflection;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowLiveContextCompositionUiTests
{
	[Fact]
	public void DataRootResolution_PrefersCaptureThenAcceptsOnlyAnAbsoluteInternalRoot()
	{
		using var project = UiTestProject.CreateDefault();
		var captureRoot = Path.Combine(project.AppDataPath, "capture");
		var internalRoot = Directory.CreateDirectory(
			Path.Combine(project.AppDataPath, "internal")).FullName;
		var missingRoot = Path.Combine(project.AppDataPath, "missing");
		var fileRoot = Path.Combine(project.AppDataPath, "data-file");
		File.WriteAllText(fileRoot, "content");
		var capture = new StoreScreenshotCaptureRequest(
			project.RootPath,
			project.AppDataPath,
			captureRoot,
			"en");

		var captureProvider = AvaloniaCompositionRoot.ResolveAppDataPathProvider(
			capture,
			_ => internalRoot);
		var internalProvider = AvaloniaCompositionRoot.ResolveAppDataPathProvider(
			storeCaptureRequest: null,
			_ => internalRoot);
		var relativeProvider = AvaloniaCompositionRoot.ResolveAppDataPathProvider(
			storeCaptureRequest: null,
			_ => "relative-root");
		var missingProvider = AvaloniaCompositionRoot.ResolveAppDataPathProvider(
			storeCaptureRequest: null,
			_ => missingRoot);
		var fileProvider = AvaloniaCompositionRoot.ResolveAppDataPathProvider(
			storeCaptureRequest: null,
			_ => fileRoot);

		Assert.NotNull(captureProvider);
		Assert.Equal(Path.GetFullPath(captureRoot), captureProvider());
		Assert.NotNull(internalProvider);
		Assert.Equal(Path.GetFullPath(internalRoot), internalProvider());
		Assert.Null(relativeProvider);
		Assert.Null(missingProvider);
		Assert.Null(fileProvider);
	}

	[AvaloniaFact]
	public async Task WindowUsesTheLiveSessionRegistryFromItsInjectedDataRoot()
	{
		using var project = UiTestProject.CreateDefault();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(appDataPath);
		var registry = new LiveSessionRegistry(() => appDataPath);
		await using var session = registry.Start([project.RootPath]);
		session.UpdateClient("test-client", "1");

		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			appDataPathOverride: appDataPath);
		try
		{
			var field = typeof(MainWindow).GetField(
				"_liveSessionRegistry",
				BindingFlags.Instance | BindingFlags.NonPublic);
			var windowRegistry = Assert.IsType<LiveSessionRegistry>(field?.GetValue(window));

			Assert.Equal(registry.DirectoryPath, windowRegistry.DirectoryPath);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() => window.Title?.Contains("Live context (test-client)", StringComparison.Ordinal) == true,
				"window title to reflect the session from the injected data root");
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
		}
	}
}
