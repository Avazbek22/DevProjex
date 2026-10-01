using Avalonia.Interactivity;
using Avalonia.VisualTree;
using DevProjex.Application.Services;
using DevProjex.Infrastructure.Persistence;
using DevProjex.Infrastructure.ResourceStore;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class StoreMigrationRecoveryUiTests
{
	[AvaloniaFact]
	public async Task RetryKeepsRecoveryOnlyUntilMigrationSucceeds()
	{
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.Ru);
		var firstProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var probeCount = 0;
		var readyCount = 0;
		var exitCount = 0;
		var window = StoreMigrationRecoveryWindow.Create(
			localization,
			() =>
			{
				var count = Interlocked.Increment(ref probeCount);
				if (count == 1)
					firstProbe.TrySetResult();
				return count == 1
					? StoreUserDataMigrationStatus.TemporarilyUnavailable
					: StoreUserDataMigrationStatus.Migrated;
			},
			() => readyCount++,
			() => exitCount++);
		UiTestDriver.TrackTopLevelWindow(window);
		window.Show();
		var retry = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
			button => button.Name == "StoreMigrationRetryButton");
		var exit = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
			button => button.Name == "StoreMigrationExitButton");

		Assert.Equal(localization["Dialog.StoreMigration.Title"], window.Title);
		Assert.Equal(localization["Terminal.Tui.Retry"], retry.Content);
		Assert.Equal(localization["Menu.File.Exit"], exit.Content);
		retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		await firstProbe.Task.WaitAsync(TestContext.Current.CancellationToken);
		await WaitUntilAsync(() => retry.IsEnabled);
		Assert.True(window.IsVisible);
		Assert.Equal(0, readyCount);
		Assert.Equal(0, exitCount);

		retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		await WaitUntilAsync(() => readyCount == 1);
		Assert.False(window.IsVisible);
		Assert.Equal(0, exitCount);
	}

	[AvaloniaFact]
	public async Task UnexpectedRetryFailureLeavesExitAvailable()
	{
		var localization = new LocalizationService(new JsonLocalizationCatalog(), AppLanguage.Ru);
		var probeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var exits = 0;
		var window = StoreMigrationRecoveryWindow.Create(
			localization,
			() =>
			{
				probeEntered.TrySetResult();
				throw new InvalidOperationException("Probe failed.");
			},
			() => Assert.Fail("Application must remain closed."),
			() => exits++);
		UiTestDriver.TrackTopLevelWindow(window);
		window.Show();
		var retry = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
			button => button.Name == "StoreMigrationRetryButton");
		var exit = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
			button => button.Name == "StoreMigrationExitButton");

		retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		await probeEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
		await WaitUntilAsync(() => retry.IsEnabled && exit.IsEnabled);
		Assert.True(window.IsVisible);
		exit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		Assert.Equal(1, exits);
		Assert.False(window.IsVisible);
	}

	private static async Task WaitUntilAsync(Func<bool> predicate)
	{
		for (var attempt = 0; attempt < 100; attempt++)
		{
			await UiTestDriver.WaitForSettledFramesAsync(1);
			if (predicate())
				return;
		}

		Assert.Fail("Recovery window did not reach the expected state.");
	}
}
