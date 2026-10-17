using System.Text.RegularExpressions;
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
		var originalTitle = WithoutLoadTiming(window.Title);
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
			Assert.Equal(originalTitle, WithoutLoadTiming(window.Title));
			Assert.DoesNotContain("unavailable-", window.Title, StringComparison.Ordinal);
			Assert.True(UiTestDriver.GetViewModel(window).IsProjectLoaded);
		}
		finally
		{
			foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	[AvaloniaFact]
	public async Task UnavailableFolderDialogCopiesTheOverrideCommandAndStaysOpen()
	{
		using var sandbox = FlatpakEnvironment.UseForTesting(new(true, "io.github.Avazbek22.DevProjex"));
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);
		try
		{
			var missing = Path.Combine(workspace.Project.RootPath, "unavailable 'copy' project");
			var expected = FlatpakEnvironment.OverrideCommand(missing);
			var opening = await UiTestDriver.BeginOpenFolderAsync(window, missing);
			await UiTestDriver.WaitForConditionAsync(window, () => window.OwnedWindows.Count == 1,
				"the sandbox access explanation");
			var dialog = Assert.Single(window.OwnedWindows);

			Assert.Equal(expected, Assert.Single(dialog.GetVisualDescendants().OfType<SelectableTextBlock>()).Text);
			Assert.DoesNotContain(
				dialog.GetVisualDescendants().OfType<TextBlock>().Where(static block => block is not SelectableTextBlock),
				block => block.Text?.Contains("flatpak override", StringComparison.Ordinal) == true);

			await window.Clipboard!.SetTextAsync("previous clipboard");
			var copy = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(),
				static button => Equals(button.Content, "Copy command"));
			await UiTestDriver.RaiseButtonClickAsync(copy);
			Dispatcher.UIThread.RunJobs();

			Assert.Equal(expected, await ClipboardExtensions.TryGetTextAsync(window.Clipboard!));
			Assert.Same(dialog, Assert.Single(window.OwnedWindows));
			await UiTestDriver.CloseTopLevelWindowAsync(dialog);
			Assert.False(await Assert.IsAssignableFrom<Task<bool>>(opening));
		}
		finally
		{
			foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
			await UiTestDriver.CloseWindowAsync(window);
		}
	}

	// Builds with project-load timing append the measured durations to the title after the load
	// completes, so the suffix may or may not be present when the title is first read.
	private static string WithoutLoadTiming(string? title) =>
		Regex.Replace(title ?? string.Empty, @" \[\d+\.\d{3} \+ \d+\.\d{3} = \d+\.\d{3}\]$", string.Empty);
}
