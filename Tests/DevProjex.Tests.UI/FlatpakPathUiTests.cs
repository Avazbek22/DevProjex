using Avalonia.VisualTree;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class FlatpakPathUiTests(UiWorkspaceFixture workspace)
{
	[AvaloniaFact]
	public async Task UnavailableFolderExplainsTheOverrideWithoutLosingTheOpenProject()
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, "io.github.Avazbek22.DevProjex"));
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);
		var originalTitle = window.Title;
		try
		{
			var missing = Path.Combine(workspace.Project.RootPath, "unavailable-'project");
			var opening = await UiTestDriver.BeginOpenFolderAsync(window, missing);
			await UiTestDriver.WaitForConditionAsync(window, () => window.OwnedWindows.Count == 1,
				"the sandbox access explanation");
			var dialog = Assert.Single(window.OwnedWindows);
			var text = string.Join('\n', dialog.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text));
			Assert.Contains("may not exist or may be inaccessible", text);
			Assert.Contains(FlatpakEnvironment.OverrideCommand(missing), text);
			await UiTestDriver.CloseTopLevelWindowAsync(dialog);
			Assert.False(await Assert.IsAssignableFrom<Task<bool>>(opening));
			Assert.Equal(originalTitle, window.Title);
			Assert.True(UiTestDriver.GetViewModel(window).IsProjectLoaded);
		}
		finally
		{
			foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
			await UiTestDriver.CloseWindowAsync(window);
		}
	}
}
