using DevProjex.Infrastructure.ThemePresets;

namespace DevProjex.Tests.Unit;

public sealed class UserSettingsStoreTests
{
    [Fact]
    public void Load_MissingFile_ReturnsViewDefaultsWithoutCreatingStorage()
    {
        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);

        var database = store.Load();

        Assert.False(File.Exists(store.GetPath()));
        Assert.False(database.ViewSettings.IsCompactMode);
        Assert.True(database.ViewSettings.IsTreeExpansionAnimationEnabled);
        Assert.False(database.ViewSettings.IsTerminalCommandPromptDismissed);
        Assert.Null(database.ViewSettings.PreferredLanguage);
        Assert.False(database.UpdateCheckSettings.IsAutomaticCheckEnabled);
        Assert.Null(database.UpdateCheckSettings.LastCheckUtc);
        Assert.Empty(database.UpdateCheckSettings.LatestKnownVersion);
        Assert.Empty(database.UpdateCheckSettings.LastNotifiedVersion);
    }

    [Fact]
    public void LoadForStartup_WhenStoreLockIsHeld_ReturnsViewDefaultsWithinBoundedTime()
    {
        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);
        var lockPath = store.GetPath() + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        using var heldLock = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var loaded = store.LoadForStartup(TimeSpan.FromMilliseconds(25));

        stopwatch.Stop();
        Assert.Equal(new AppViewSettings(), loaded.ViewSettings);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Startup load took {stopwatch.Elapsed}.");
    }

    [Fact]
    public void LoadForStartup_WhenStoreIsAvailable_ReturnsEveryPersistedViewSetting()
    {
        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);
        var expected = new AppViewSettings
        {
            IsCompactMode = true,
            IsTreeExpansionAnimationEnabled = false,
            IsTerminalCommandPromptDismissed = true,
            PreferredLanguage = AppLanguage.It
        };
        Assert.True(store.TrySave(new UserSettingsDb { ViewSettings = expected }));

        var loaded = store.LoadForStartup(TimeSpan.FromMilliseconds(25));

        Assert.Equal(expected, loaded.ViewSettings);
    }

    [Fact]
    public void EnsureStorageExists_CreatesCleanViewOnlyDocumentAndBackup()
    {
        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);

        Assert.True(store.EnsureStorageExists());

        Assert.True(File.Exists(store.GetPath()));
        Assert.True(File.Exists(store.GetPath() + ".bak"));
        using var json = JsonDocument.Parse(File.ReadAllText(store.GetPath()));
        var root = json.RootElement;
        Assert.Equal(8, root.GetProperty("schemaVersion").GetInt32());
        Assert.True(root.TryGetProperty("viewSettings", out _));
        Assert.True(root.TryGetProperty("updateCheckSettings", out _));
        Assert.False(root.TryGetProperty("presets", out _));
        Assert.False(root.TryGetProperty("lastSelected", out _));
        Assert.DoesNotContain("isAdvancedIgnoreCountsEnabled", root.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Load_LegacyCombinedDocument_PreservesStableViewSettingsAndRemovesThemePayload()
    {
        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);
        WriteJson(store.GetPath(), """
        {
          "schemaVersion": 5,
          "presets": { "Dark.Transparent": { "materialIntensity": 99 } },
          "lastSelected": "Dark.Transparent",
          "viewSettings": {
            "isCompactMode": true,
            "isTreeAnimationEnabled": false,
            "isAdvancedIgnoreCountsEnabled": false,
            "isTerminalCommandPromptDismissed": true,
            "preferredLanguage": "de"
          }
        }
        """);

        var loaded = store.LoadForStartup(TimeSpan.FromSeconds(1));

        Assert.True(loaded.ViewSettings.IsCompactMode);
        Assert.True(loaded.ViewSettings.IsTreeExpansionAnimationEnabled);
        Assert.True(loaded.ViewSettings.IsTerminalCommandPromptDismissed);
        Assert.Equal(AppLanguage.De, loaded.ViewSettings.PreferredLanguage);
        using var rewritten = JsonDocument.Parse(File.ReadAllText(store.GetPath()));
        Assert.Equal(8, rewritten.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(
            rewritten.RootElement
                .GetProperty("viewSettings")
                .GetProperty("isTreeExpansionAnimationEnabled")
                .GetBoolean());
        Assert.DoesNotContain(
            "isTreeAnimationEnabled",
            rewritten.RootElement.GetRawText(),
            StringComparison.Ordinal);
        Assert.False(rewritten.RootElement.TryGetProperty("presets", out _));
        Assert.False(rewritten.RootElement.TryGetProperty("lastSelected", out _));
        Assert.DoesNotContain(
            "isAdvancedIgnoreCountsEnabled",
            rewritten.RootElement.GetRawText(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Load_SchemaSixDoesNotTransferLegacyHoverPreferenceToExpansionAnimation()
    {
        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);
        WriteJson(store.GetPath(), """
        {
          "schemaVersion": 6,
          "viewSettings": {
            "isCompactMode": true,
            "isTreeAnimationEnabled": false
          }
        }
        """);

        var loaded = store.LoadForStartup(TimeSpan.FromSeconds(1));

        Assert.True(loaded.ViewSettings.IsCompactMode);
        Assert.True(loaded.ViewSettings.IsTreeExpansionAnimationEnabled);
        using var rewritten = JsonDocument.Parse(File.ReadAllText(store.GetPath()));
        var viewSettings = rewritten.RootElement.GetProperty("viewSettings");
        Assert.Equal(8, rewritten.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(viewSettings.GetProperty("isTreeExpansionAnimationEnabled").GetBoolean());
        Assert.False(viewSettings.TryGetProperty("isTreeAnimationEnabled", out _));
    }

    [Theory]
    [InlineData(false, false, false, null)]
    [InlineData(true, false, true, AppLanguage.Ru)]
    [InlineData(false, true, true, AppLanguage.Fr)]
    public void SaveLoad_RoundTripsEveryActiveViewSetting(
        bool compact,
        bool treeExpansionAnimation,
        bool terminalPromptDismissed,
        AppLanguage? language)
    {
        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);
        var database = store.Load();
        database.ViewSettings = new AppViewSettings
        {
            IsCompactMode = compact,
            IsTreeExpansionAnimationEnabled = treeExpansionAnimation,
            IsTerminalCommandPromptDismissed = terminalPromptDismissed,
            PreferredLanguage = language
        };

        Assert.True(store.TryPersistViewSettings(database));
        var reloaded = new UserSettingsStore(() => temp.Path).Load();

        Assert.Equal(database.ViewSettings, reloaded.ViewSettings);
    }

    [Fact]
    public void PersistUpdateCheckSettings_RoundTripsAndPreservesViewSettings()
    {
        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);
        var database = new UserSettingsDb
        {
            ViewSettings = new AppViewSettings
            {
                IsCompactMode = true,
                PreferredLanguage = AppLanguage.Ru
            }
        };
        Assert.True(store.TrySave(database));
        var checkedAt = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        database.UpdateCheckSettings = new UpdateCheckSettings
        {
            IsAutomaticCheckEnabled = true,
            LastCheckUtc = checkedAt,
            LatestKnownVersion = " v4.10.0 ",
            LastNotifiedVersion = " v4.10.0 "
        };

        Assert.True(store.TryPersistUpdateCheckSettings(database));
        var reloaded = new UserSettingsStore(() => temp.Path).Load();

        Assert.True(reloaded.ViewSettings.IsCompactMode);
        Assert.Equal(AppLanguage.Ru, reloaded.ViewSettings.PreferredLanguage);
        Assert.True(reloaded.UpdateCheckSettings.IsAutomaticCheckEnabled);
        Assert.Equal(checkedAt, reloaded.UpdateCheckSettings.LastCheckUtc);
        Assert.Equal("v4.10.0", reloaded.UpdateCheckSettings.LatestKnownVersion);
        Assert.Equal("v4.10.0", reloaded.UpdateCheckSettings.LastNotifiedVersion);
    }

    [Fact]
    public void Load_InvalidLanguage_NormalizesToAutomaticLanguageSelection()
    {
        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);
        WriteJson(store.GetPath(), """
        {
          "schemaVersion": 6,
          "viewSettings": { "preferredLanguage": 999 }
        }
        """);

        var loaded = store.Load();

        Assert.Null(loaded.ViewSettings.PreferredLanguage);
    }

    [Fact]
    public void Load_CorruptPrimary_RecoversValidBackup()
    {
        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);
        var database = new UserSettingsDb
        {
            ViewSettings = new AppViewSettings
            {
                IsCompactMode = true,
                PreferredLanguage = AppLanguage.It
            },
            UpdateCheckSettings = new UpdateCheckSettings
            {
                IsAutomaticCheckEnabled = true,
                LastCheckUtc = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero),
                LatestKnownVersion = "5.0",
                LastNotifiedVersion = "5.0"
            }
        };
        Assert.True(store.TrySave(database));
        File.WriteAllText(store.GetPath(), "{ invalid");

        var recovered = new UserSettingsStore(() => temp.Path).Load();

        Assert.True(recovered.ViewSettings.IsCompactMode);
        Assert.Equal(AppLanguage.It, recovered.ViewSettings.PreferredLanguage);
        Assert.True(recovered.UpdateCheckSettings.IsAutomaticCheckEnabled);
        Assert.Equal("5.0", recovered.UpdateCheckSettings.LatestKnownVersion);
        Assert.Equal("5.0", recovered.UpdateCheckSettings.LastNotifiedVersion);
        Assert.Equal(File.ReadAllText(store.GetPath()), File.ReadAllText(store.GetPath() + ".bak"));
    }

    [Fact]
    public void FutureSchema_IsNeverOverwrittenByOlderApplication()
    {
        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);
        const string futureJson = """
        { "schemaVersion": 999, "futureProperty": "keep-me" }
        """;
        WriteJson(store.GetPath(), futureJson);

        var loaded = store.LoadForStartup(TimeSpan.FromSeconds(1));
        loaded.ViewSettings = loaded.ViewSettings with { IsCompactMode = true };

        Assert.False(store.TryPersistViewSettings(loaded));
        Assert.Equal(futureJson, File.ReadAllText(store.GetPath()));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("current")]
    public void FutureSchemaInBackup_BlocksRecoveryEnsureAndEveryWrite(string primaryState)
    {
        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);
        var primaryPath = store.GetPath();
        var backupPath = primaryPath + ".bak";
        const string futureJson = """
        { "schemaVersion": 999, "futureProperty": "keep-backup" }
        """;

        var primaryJson = primaryState switch
        {
            "corrupt" => "{ invalid-primary",
            "current" => """
                         { "schemaVersion": 8, "viewSettings": { "isCompactMode": true } }
                         """,
            _ => null
        };
        if (primaryJson is not null)
            WriteJson(primaryPath, primaryJson);
        WriteJson(backupPath, futureJson);

        var loaded = store.LoadForStartup(TimeSpan.FromSeconds(1));
        loaded.ViewSettings = loaded.ViewSettings with
        {
            IsTreeExpansionAnimationEnabled = false
        };

        Assert.False(store.TrySave(loaded));
        Assert.False(store.TryPersistViewSettings(loaded));
        Assert.True(store.EnsureStorageExists());
        Assert.Equal(primaryJson is not null, File.Exists(primaryPath));
        if (primaryJson is not null)
            Assert.Equal(primaryJson, File.ReadAllText(primaryPath));
        Assert.Equal(futureJson, File.ReadAllText(backupPath));
    }

    [Fact]
    public async Task IndependentInstances_ConcurrentSaves_LeaveOneCompleteValidSnapshot()
    {
        using var temp = new TemporaryDirectory();
        var storeA = new UserSettingsStore(() => temp.Path);
        var storeB = new UserSettingsStore(() => temp.Path);
        var databaseA = new UserSettingsDb
        {
            ViewSettings = new AppViewSettings { IsCompactMode = true, PreferredLanguage = AppLanguage.De }
        };
        var databaseB = new UserSettingsDb
        {
            ViewSettings = new AppViewSettings
            {
                IsTreeExpansionAnimationEnabled = false,
                PreferredLanguage = AppLanguage.Fr
            }
        };
        using var start = new ManualResetEventSlim(false);

        var taskA = Task.Run(() =>
        {
            start.Wait();
            storeA.TrySave(databaseA);
        }, TestContext.Current.CancellationToken);
        var taskB = Task.Run(() =>
        {
            start.Wait();
            storeB.TrySave(databaseB);
        }, TestContext.Current.CancellationToken);

        start.Set();
        await Task.WhenAll(taskA, taskB);
        var reloaded = new UserSettingsStore(() => temp.Path).Load();

        Assert.True(reloaded.ViewSettings == databaseA.ViewSettings || reloaded.ViewSettings == databaseB.ViewSettings);
        using var document = JsonDocument.Parse(File.ReadAllText(storeA.GetPath()));
        Assert.Equal(8, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    private static void WriteJson(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }
}
