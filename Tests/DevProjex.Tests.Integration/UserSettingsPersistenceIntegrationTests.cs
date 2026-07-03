using DevProjex.Infrastructure.ThemePresets;

namespace DevProjex.Tests.Integration;

public sealed class UserSettingsPersistenceIntegrationTests
{
    [Fact]
    public async Task IndependentInstances_ConcurrentSaves_DoNotCorruptUserSettingsDocument()
    {
        using var temp = new TemporaryDirectory();
        var storeA = new UserSettingsStore(() => temp.Path);
        var storeB = new UserSettingsStore(() => temp.Path);
        var firstSnapshot = storeA.Load();
        var secondSnapshot = storeB.Load();
        firstSnapshot.LastSelected = "Light.Acrylic";
        firstSnapshot.ViewSettings = new AppViewSettings
        {
            IsCompactMode = true,
            IsTreeAnimationEnabled = false,
            IsAdvancedIgnoreCountsEnabled = false,
            PreferredLanguage = AppLanguage.De
        };
        secondSnapshot.LastSelected = "Dark.Mica";
        secondSnapshot.ViewSettings = new AppViewSettings
        {
            IsCompactMode = false,
            IsTreeAnimationEnabled = true,
            IsAdvancedIgnoreCountsEnabled = true,
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
            string.Equals(reloaded.LastSelected, firstSnapshot.LastSelected, StringComparison.Ordinal) &&
            reloaded.ViewSettings.IsCompactMode &&
            !reloaded.ViewSettings.IsTreeAnimationEnabled &&
            reloaded.ViewSettings.IsAdvancedIgnoreCountsEnabled &&
            reloaded.ViewSettings.PreferredLanguage == AppLanguage.De;
        var matchesSecondSnapshot =
            string.Equals(reloaded.LastSelected, secondSnapshot.LastSelected, StringComparison.Ordinal) &&
            !reloaded.ViewSettings.IsCompactMode &&
            reloaded.ViewSettings.IsTreeAnimationEnabled &&
            reloaded.ViewSettings.IsAdvancedIgnoreCountsEnabled &&
            reloaded.ViewSettings.PreferredLanguage == AppLanguage.Fr;

        Assert.True(File.Exists(storeA.GetPath()));
        Assert.True(File.Exists(storeA.GetPath() + ".bak"));
        Assert.True(matchesFirstSnapshot || matchesSecondSnapshot);
    }
}
