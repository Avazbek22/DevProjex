using DevProjex.Infrastructure.ThemePresets;

namespace DevProjex.Tests.Unit;

public sealed class UserSettingsStoreBackupTests
{
    [Fact]
    public void Save_CreatesBackupSnapshotAlongsidePrimaryFile()
    {
        using var temp = new Helpers.TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);
        var db = store.Load();

        store.Save(db);

        Assert.True(File.Exists(store.GetPath()));
        Assert.True(File.Exists(store.GetPath() + ".bak"));
    }

    [Fact]
    public void Load_InvalidPrimaryFile_RecoversFromBackupAndRestoresPrimary()
    {
        using var temp = new Helpers.TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);
        var db = store.Load();
        db.LastSelected = "Light.Acrylic";
        db.ViewSettings = new AppViewSettings
        {
            IsCompactMode = true,
            IsTreeAnimationEnabled = false,
            IsAdvancedIgnoreCountsEnabled = false,
            PreferredLanguage = AppLanguage.De
        };

        store.Save(db);
        File.WriteAllText(store.GetPath(), "{ invalid");

        var recovered = store.Load();

        Assert.Equal("Light.Acrylic", recovered.LastSelected);
        Assert.True(recovered.ViewSettings.IsCompactMode);
        Assert.False(recovered.ViewSettings.IsTreeAnimationEnabled);
        Assert.False(recovered.ViewSettings.IsAdvancedIgnoreCountsEnabled);
        Assert.Equal(AppLanguage.De, recovered.ViewSettings.PreferredLanguage);
        Assert.DoesNotContain("{ invalid", File.ReadAllText(store.GetPath()), StringComparison.Ordinal);
    }
}
