using Avalonia.Controls.Primitives;
using DevProjex.Kernel;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class FlatpakUpdateUiTests(UiWorkspaceFixture workspace)
{
	[AvaloniaFact]
	public async Task SandboxHidesUpdateMenuAndRejectsOpeningItsPopup()
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, "io.github.Avazbek22.DevProjex"));
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);
		try
		{
			var menu = UiTestDriver.GetRequiredTopMenuControl<MenuItem>(window, "UpdateMenuItem");
			var model = UiTestDriver.GetViewModel(window);
			Assert.False(menu.IsVisible);
			model.UpdatePopoverOpen = true;
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 3);
			Assert.False(UiTestDriver.GetRequiredTopMenuControl<Popup>(window, "UpdatePopup").IsOpen);
			Assert.Contains("Flathub", model.HelpAboutBody);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window);
		}
	}
}
