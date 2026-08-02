using DevProjex.Infrastructure.ThemePresets;

namespace DevProjex.Tests.Integration;

public sealed class UserSettingsPersistenceIntegrationTests
{
    [Fact]
    public void UpgradeFromCombinedSettings_InstallsFactoryThemesAndPreservesGeneralViewSettings()
    {
        using var temp = new TemporaryDirectory();
        var userStore = new UserSettingsStore(() => temp.Path);
        var themeStore = new ThemeSettingsStore(() => temp.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(userStore.GetPath())!);
        File.WriteAllText(userStore.GetPath(), """
        {
          "schemaVersion": 5,
          "presets": {
            "Dark.Acrylic": {
              "materialIntensity": 1,
              "panelContrast": 2,
              "menuChildIntensity": 3,
              "borderStrength": 4
            }
          },
          "lastSelected": "Light.Solid",
          "viewSettings": {
            "isCompactMode": true,
            "isTreeAnimationEnabled": true,
            "isAdvancedIgnoreCountsEnabled": false,
            "isTerminalCommandPromptDismissed": true,
            "preferredLanguage": "uz"
          }
        }
        """);

        var upgradedUserSettings = userStore.LoadForStartup(TimeSpan.FromSeconds(1));
        var installedThemeSettings = themeStore.LoadForStartup(TimeSpan.FromSeconds(1));
        themeStore.ResetToDefaults();
        var restartedUserSettings = new UserSettingsStore(() => temp.Path).Load();
        var restartedThemeSettings = new ThemeSettingsStore(() => temp.Path).Load();

        Assert.True(upgradedUserSettings.ViewSettings.IsCompactMode);
        Assert.True(upgradedUserSettings.ViewSettings.IsTreeExpansionAnimationEnabled);
        Assert.True(upgradedUserSettings.ViewSettings.IsTerminalCommandPromptDismissed);
        Assert.Equal(AppLanguage.Uz, upgradedUserSettings.ViewSettings.PreferredLanguage);
        Assert.Equal(upgradedUserSettings.ViewSettings, restartedUserSettings.ViewSettings);
        Assert.Equal("Dark.Acrylic", installedThemeSettings.SelectedPreset);
        Assert.Equal("Dark.Acrylic", restartedThemeSettings.SelectedPreset);
        Assert.Equal(8, restartedThemeSettings.Presets.Count);
        Assert.Equal(
            84.77564102564102,
            restartedThemeSettings.Presets["Dark.Acrylic"].BackgroundTransparency);
        Assert.DoesNotContain("presets", File.ReadAllText(userStore.GetPath()), StringComparison.Ordinal);
        Assert.DoesNotContain("lastSelected", File.ReadAllText(userStore.GetPath()), StringComparison.Ordinal);
        Assert.DoesNotContain("materialIntensity", File.ReadAllText(themeStore.GetPath()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IndependentInstances_ConcurrentSaves_DoNotCorruptUserSettingsDocument()
    {
        using var temp = new TemporaryDirectory();
        var storeA = new UserSettingsStore(() => temp.Path);
        var storeB = new UserSettingsStore(() => temp.Path);
        var firstSnapshot = storeA.Load();
        var secondSnapshot = storeB.Load();
        firstSnapshot.ViewSettings = new AppViewSettings
        {
            IsCompactMode = true,
            IsTreeExpansionAnimationEnabled = false,
            PreferredLanguage = AppLanguage.De
        };
        secondSnapshot.ViewSettings = new AppViewSettings
        {
            IsCompactMode = false,
            IsTreeExpansionAnimationEnabled = true,
            PreferredLanguage = AppLanguage.Fr
        };
        using var startGate = new ManualResetEventSlim(false);

        var firstSaveTask = Task.Run(() =>
        {
            startGate.Wait();
            storeA.Save(firstSnapshot);
        }, cancellationToken: TestContext.Current.CancellationToken);

        var secondSaveTask = Task.Run(() =>
        {
            startGate.Wait();
            storeB.Save(secondSnapshot);
        }, cancellationToken: TestContext.Current.CancellationToken);

        startGate.Set();
        await Task.WhenAll(firstSaveTask, secondSaveTask);

        var reloaded = new UserSettingsStore(() => temp.Path).Load();
        var matchesFirstSnapshot =
            reloaded.ViewSettings.IsCompactMode &&
            !reloaded.ViewSettings.IsTreeExpansionAnimationEnabled &&
            reloaded.ViewSettings.PreferredLanguage == AppLanguage.De;
        var matchesSecondSnapshot =
            !reloaded.ViewSettings.IsCompactMode &&
            reloaded.ViewSettings.IsTreeExpansionAnimationEnabled &&
            reloaded.ViewSettings.PreferredLanguage == AppLanguage.Fr;

        Assert.True(File.Exists(storeA.GetPath()));
        Assert.True(File.Exists(storeA.GetPath() + ".bak"));
        Assert.True(matchesFirstSnapshot || matchesSecondSnapshot);
    }
}
