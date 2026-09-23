using System.Reflection;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Infrastructure.ThemePresets;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowSettingsStartupUiTests
{
	[AvaloniaFact]
	public async Task CloseAfterTransientThemeRead_DoesNotOverwritePersistedTheme()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Skip("This test relies on Windows file-sharing behavior.");
			return;
		}

		using var project = UiTestProject.CreateDefault();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		var store = new ThemeSettingsStore(() => appDataPath);
		var document = store.Load();
		document.SelectedThemeMode = ThemeSelectionMode.Light;
		document.LightThemeEffect = ThemeEffectMode.Solid;
		document.SelectedPreset = "Light.Solid";
		Assert.True(store.TrySave(document));
		var primaryPath = store.GetPath();
		var backupPath = primaryPath + ".bak";
		File.WriteAllText(backupPath, "{ invalid-backup");
		var originalPrimary = File.ReadAllBytes(primaryPath);
		var originalBackup = File.ReadAllBytes(backupPath);

		MainWindow window;
		using (var primaryReadBlock = new FileStream(
				   primaryPath, FileMode.Open, FileAccess.Write, FileShare.Delete))
		{
			window = await UiTestDriver.CreateLoadedMainWindowAsync(
				project,
				appDataPathOverride: appDataPath);
		}

		await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);

		Assert.Equal(originalPrimary, File.ReadAllBytes(primaryPath));
		Assert.Equal(originalBackup, File.ReadAllBytes(backupPath));
	}

	[AvaloniaFact]
	public async Task ViewToggleAfterTransientSettingsRead_PreservesOtherPersistedPreferences()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Skip("This test relies on Windows file-sharing behavior.");
			return;
		}

		using var project = UiTestProject.CreateDefault();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		var store = new UserSettingsStore(() => appDataPath);
		Assert.True(store.TrySave(new UserSettingsDb
		{
			ViewSettings = new AppViewSettings
			{
				IsCompactMode = true,
				IsToolAnimationEnabled = false,
				PreferredLanguage = AppLanguage.It
			}
		}));
		var primaryPath = store.GetPath();
		File.WriteAllText(primaryPath + ".bak", "{ invalid-backup");

		MainWindow window;
		using (var primaryReadBlock = new FileStream(
				   primaryPath, FileMode.Open, FileAccess.Write, FileShare.Delete))
		{
			window = await UiTestDriver.CreateLoadedMainWindowAsync(
				project,
				appDataPathOverride: appDataPath);
		}

		try
		{
			GetAppearanceController(window).ToggleStatusMetricsAnimation();
			var viewModel = UiTestDriver.GetViewModel(window);
			Assert.True(viewModel.IsCompactMode);
			Assert.False(viewModel.IsToolAnimationEnabled);
			Assert.False(viewModel.IsStatusMetricsAnimationEnabled);
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
		}

		var persisted = store.Load().ViewSettings;
		Assert.True(persisted.IsCompactMode);
		Assert.False(persisted.IsToolAnimationEnabled);
		Assert.False(persisted.IsStatusMetricsAnimationEnabled);
		Assert.Equal(AppLanguage.It, persisted.PreferredLanguage);
	}

	[AvaloniaFact]
	public async Task ResetAfterTransientSettingsRead_PreservesDismissedTerminalPrompt()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Skip("This test relies on Windows file-sharing behavior.");
			return;
		}

		using var project = UiTestProject.CreateDefault();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		var store = new UserSettingsStore(() => appDataPath);
		Assert.True(store.TrySave(new UserSettingsDb
		{
			ViewSettings = new AppViewSettings
			{
				IsCompactMode = true,
				IsTerminalCommandPromptDismissed = true,
				PreferredLanguage = AppLanguage.It
			}
		}));
		var primaryPath = store.GetPath();
		File.WriteAllText(primaryPath + ".bak", "{ invalid-backup");

		MainWindow window;
		using (var primaryReadBlock = new FileStream(
				   primaryPath, FileMode.Open, FileAccess.Write, FileShare.Delete))
		{
			window = await UiTestDriver.CreateLoadedMainWindowAsync(
				project,
				appDataPathOverride: appDataPath);
		}

		try
		{
			Assert.True(GetAppearanceController(window).ResetThemeSettings());
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
		}

		var persisted = store.Load().ViewSettings;
		Assert.False(persisted.IsCompactMode);
		Assert.True(persisted.IsTerminalCommandPromptDismissed);
		Assert.Null(persisted.PreferredLanguage);
	}

	[AvaloniaFact]
	public async Task ResetThemeSettings_WhenThemeWriteFails_LeavesGuiAndStoresUnchanged()
	{
		using var project = UiTestProject.CreateDefault();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		var themeStore = new ThemeSettingsStore(() => appDataPath);
		var theme = themeStore.Load();
		theme.SelectedThemeMode = ThemeSelectionMode.Light;
		theme.LightThemeEffect = ThemeEffectMode.Solid;
		theme.SelectedPreset = "Light.Solid";
		Assert.True(themeStore.TrySave(theme));
		var userStore = new UserSettingsStore(() => appDataPath);
		Assert.True(userStore.TrySave(new UserSettingsDb
		{
			ViewSettings = new AppViewSettings { IsCompactMode = true }
		}));
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			appDataPathOverride: appDataPath);

		try
		{
			var viewModel = UiTestDriver.GetViewModel(window);
			Assert.Equal(ThemeSelectionMode.Light, viewModel.SelectedThemeMode);
			Assert.True(viewModel.IsCompactMode);
			var originalTheme = File.ReadAllBytes(themeStore.GetPath());
			var originalUser = File.ReadAllBytes(userStore.GetPath());

			using (var heldLock = new FileStream(
			           themeStore.GetPath() + ".lock",
			           FileMode.OpenOrCreate,
			           FileAccess.ReadWrite,
			           FileShare.None))
			{
				Assert.False(GetAppearanceController(window).ResetThemeSettings());
				Assert.Equal(ThemeSelectionMode.Light, viewModel.SelectedThemeMode);
				Assert.True(viewModel.IsCompactMode);
				Assert.Equal(originalTheme, File.ReadAllBytes(themeStore.GetPath()));
				Assert.Equal(originalUser, File.ReadAllBytes(userStore.GetPath()));
			}
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
		}

		Assert.Equal(ThemeSelectionMode.Light, themeStore.Load().SelectedThemeMode);
		Assert.True(userStore.Load().ViewSettings.IsCompactMode);
	}

	[AvaloniaFact]
	public async Task ResetThemeSettings_WhenViewSettingsWriteFails_ReportsPartialResetAndRetriesOnClose()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Skip("This test relies on Windows file-sharing behavior.");
			return;
		}

		using var project = UiTestProject.CreateDefault();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		var themeStore = new ThemeSettingsStore(() => appDataPath);
		var theme = themeStore.Load();
		theme.SelectedThemeMode = ThemeSelectionMode.Light;
		theme.SelectedPreset = "Light.Solid";
		Assert.True(themeStore.TrySave(theme));
		var userStore = new UserSettingsStore(() => appDataPath);
		Assert.True(userStore.TrySave(new UserSettingsDb
		{
			ViewSettings = new AppViewSettings { IsCompactMode = true }
		}));
		var primaryPath = userStore.GetPath();
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			appDataPathOverride: appDataPath);

		try
		{
			var originalUser = File.ReadAllBytes(primaryPath);
			File.WriteAllText(primaryPath + ".bak", "{ invalid-backup");
			using (var primaryReadBlock = new FileStream(
			           primaryPath, FileMode.Open, FileAccess.Write, FileShare.Delete))
			{
				Assert.False(GetAppearanceController(window).ResetThemeSettings());
				var viewModel = UiTestDriver.GetViewModel(window);
				Assert.Equal(ThemeSelectionMode.System, viewModel.SelectedThemeMode);
				Assert.False(viewModel.IsCompactMode);
			}
			Assert.Equal(originalUser, File.ReadAllBytes(primaryPath));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
		}

		Assert.Equal(ThemeSelectionMode.System, themeStore.Load().SelectedThemeMode);
		Assert.False(userStore.Load().ViewSettings.IsCompactMode);
	}

	[AvaloniaFact]
	public async Task FailedViewChangeWrite_RetriesOnlyChangedPreferenceOnClose()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Skip("This test relies on Windows file-sharing behavior.");
			return;
		}

		using var project = UiTestProject.CreateDefault();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		var store = new UserSettingsStore(() => appDataPath);
		Assert.True(store.TrySave(new UserSettingsDb
		{
			ViewSettings = new AppViewSettings
			{
				IsCompactMode = true,
				IsToolAnimationEnabled = false,
				PreferredLanguage = AppLanguage.It
			}
		}));
		var primaryPath = store.GetPath();
		File.WriteAllText(primaryPath + ".bak", "{ invalid-backup");
		var window = await UiTestDriver.CreateLoadedMainWindowAsync(
			project,
			appDataPathOverride: appDataPath);

		try
		{
			var originalPrimary = File.ReadAllBytes(primaryPath);
			using (var primaryReadBlock = new FileStream(
					   primaryPath, FileMode.Open, FileAccess.Write, FileShare.Delete))
			{
				GetAppearanceController(window).ToggleStatusMetricsAnimation();
			}

			Assert.Equal(originalPrimary, File.ReadAllBytes(primaryPath));
		}
		finally
		{
			await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);
		}

		var persisted = store.Load().ViewSettings;
		Assert.True(persisted.IsCompactMode);
		Assert.False(persisted.IsToolAnimationEnabled);
		Assert.False(persisted.IsStatusMetricsAnimationEnabled);
		Assert.Equal(AppLanguage.It, persisted.PreferredLanguage);
	}

	[AvaloniaFact]
	public async Task CloseAfterTransientSettingsRead_DoesNotOverwritePersistedViewSettings()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Skip("This test relies on Windows file-sharing behavior.");
			return;
		}

		using var project = UiTestProject.CreateDefault();
		var appDataPath = Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
		var store = new UserSettingsStore(() => appDataPath);
		Assert.True(store.TrySave(new UserSettingsDb
		{
			ViewSettings = new AppViewSettings
			{
				IsCompactMode = true,
				IsToolAnimationEnabled = false,
				PreferredLanguage = AppLanguage.It
			}
		}));
		var primaryPath = store.GetPath();
		var backupPath = primaryPath + ".bak";
		File.WriteAllText(backupPath, "{ invalid-backup");
		var originalPrimary = File.ReadAllBytes(primaryPath);
		var originalBackup = File.ReadAllBytes(backupPath);

		MainWindow window;
		using (var primaryReadBlock = new FileStream(
				   primaryPath, FileMode.Open, FileAccess.Write, FileShare.Delete))
		{
			window = await UiTestDriver.CreateLoadedMainWindowAsync(
				project,
				appDataPathOverride: appDataPath);
		}

		await UiTestDriver.CloseWindowAsync(window, cleanupAppData: false);

		Assert.Equal(originalPrimary, File.ReadAllBytes(primaryPath));
		Assert.Equal(originalBackup, File.ReadAllBytes(backupPath));
	}

	private static AppearanceSettingsController GetAppearanceController(MainWindow window)
	{
		var field = typeof(MainWindow).GetField(
			"_appearanceSettings",
			BindingFlags.Instance | BindingFlags.NonPublic);
		return Assert.IsType<AppearanceSettingsController>(field?.GetValue(window));
	}
}
