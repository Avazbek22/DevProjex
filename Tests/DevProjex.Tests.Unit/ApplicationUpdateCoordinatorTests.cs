using DevProjex.Application.Updates;
using DevProjex.Infrastructure.ThemePresets;

namespace DevProjex.Tests.Unit;

public sealed class ApplicationUpdateCoordinatorTests
{
    [Fact]
    public async Task ManualCheckClickedWhileAutomaticCheckOwnsGate_RunsAfterItCompletes()
    {
        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);
        Assert.True(store.TrySave(new UserSettingsDb
        {
            UpdateCheckSettings = new UpdateCheckSettings
            {
                IsAutomaticCheckEnabled = true
            }
        }));
        var service = new PausedUpdateService(new ApplicationUpdateCheckResult(
            ApplicationUpdateAvailability.UpToDate,
            "5.0",
            "5.0"));
        using var viewModel = CreateViewModel();
        using var coordinator = new ApplicationUpdateCoordinator(
            viewModel, service, store, "5.0");
        var cancellationToken = TestContext.Current.CancellationToken;

        var automaticCheck = coordinator.RunAutomaticCheckIfDueAsync(cancellationToken);
        await service.Started.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        var openPopover = coordinator.OpenManualCheckAsync(cancellationToken);
        Assert.True(viewModel.UpdatePopoverOpen);
        var manualCheck = coordinator.CheckManuallyAsync(cancellationToken);
        var duplicateClick = coordinator.CheckManuallyAsync(cancellationToken);

        service.Complete();
        await Task.WhenAll(automaticCheck, openPopover, manualCheck, duplicateClick);

        Assert.Equal(2, service.CallCount);
        Assert.Equal(UpdateCheckPresentationState.UpToDate, viewModel.UpdateCheckState);
    }

    [Fact]
    public async Task CanceledPendingManualCheck_AllowsLaterManualCheck()
    {
        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);
        Assert.True(store.TrySave(new UserSettingsDb
        {
            UpdateCheckSettings = new UpdateCheckSettings
            {
                IsAutomaticCheckEnabled = true
            }
        }));
        var service = new PausedUpdateService(new ApplicationUpdateCheckResult(
            ApplicationUpdateAvailability.UpToDate,
            "5.0",
            "5.0"));
        using var viewModel = CreateViewModel();
        using var coordinator = new ApplicationUpdateCoordinator(
            viewModel, service, store, "5.0");
        var cancellationToken = TestContext.Current.CancellationToken;

        var automaticCheck = coordinator.RunAutomaticCheckIfDueAsync(cancellationToken);
        await service.Started.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        using var pendingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var canceledCheck = coordinator.CheckManuallyAsync(pendingCancellation.Token);
        pendingCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledCheck);

        service.Complete();
        await automaticCheck;
        await coordinator.CheckManuallyAsync(cancellationToken);

        Assert.Equal(2, service.CallCount);
    }

    [Fact]
    public async Task FailedManualCheck_AllowsLaterManualCheck()
    {
        using var temp = new TemporaryDirectory();
        var firstAttempt = true;
        var service = new RecordingUpdateService(
            UpdateAvailable("5.1"),
            () =>
            {
                if (!firstAttempt)
                    return;

                firstAttempt = false;
                throw new IOException("Simulated update service failure.");
            });
        using var viewModel = CreateViewModel();
        using var coordinator = new ApplicationUpdateCoordinator(
            viewModel,
            service,
            new UserSettingsStore(() => temp.Path),
            "5.0");
        var cancellationToken = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<IOException>(() =>
            coordinator.CheckManuallyAsync(cancellationToken));
        await coordinator.CheckManuallyAsync(cancellationToken);

        Assert.Equal(2, service.CallCount);
        Assert.Equal(UpdateCheckPresentationState.UpdateAvailable, viewModel.UpdateCheckState);
    }

    [Fact]
    public async Task RecoveredSettingsRead_DoesNotOverwriteCachedUpdateHistory()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("This test relies on Windows file-sharing behavior.");
            return;
        }

        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);
        var previousCheck = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.True(store.TrySave(new UserSettingsDb
        {
            UpdateCheckSettings = new UpdateCheckSettings
            {
                IsAutomaticCheckEnabled = true,
                LastCheckUtc = previousCheck,
                LatestKnownVersion = "5.1",
                LastNotifiedVersion = "5.1"
            }
        }));
        var primaryPath = store.GetPath();
        File.WriteAllText(primaryPath + ".bak", "{ invalid-backup");
        using var viewModel = CreateViewModel();
        var service = new RecordingUpdateService(UpdateAvailable("5.2"));
        using var coordinator = new ApplicationUpdateCoordinator(
            viewModel, service, store, "5.0");

        using (var primaryReadBlock = new FileStream(
                   primaryPath, FileMode.Open, FileAccess.Write, FileShare.Delete))
        {
            await coordinator.OpenManualCheckAsync(TestContext.Current.CancellationToken);
        }

        await coordinator.SetAutomaticCheckEnabledAsync(
            false,
            TestContext.Current.CancellationToken);

        var persisted = store.Load().UpdateCheckSettings;
        Assert.False(persisted.IsAutomaticCheckEnabled);
        Assert.Equal(previousCheck, persisted.LastCheckUtc);
        Assert.Equal("5.1", persisted.LatestKnownVersion);
        Assert.Equal("5.1", persisted.LastNotifiedVersion);
        Assert.Equal(0, service.CallCount);
    }

    [Fact]
    public async Task ManualCheckAfterTransientSettingsRead_PreservesUnchangedUpdatePreferences()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("This test relies on Windows file-sharing behavior.");
            return;
        }

        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);
        var previousCheck = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var now = previousCheck.AddDays(8);
        Assert.True(store.TrySave(new UserSettingsDb
        {
            UpdateCheckSettings = new UpdateCheckSettings
            {
                IsAutomaticCheckEnabled = true,
                LastCheckUtc = previousCheck,
                LatestKnownVersion = "5.1",
                LastNotifiedVersion = "5.1"
            }
        }));
        var primaryPath = store.GetPath();
        File.WriteAllText(primaryPath + ".bak", "{ invalid-backup");
        var service = new PausedUpdateService(new ApplicationUpdateCheckResult(
            ApplicationUpdateAvailability.UpToDate,
            "5.0",
            "5.0"));
        using var viewModel = CreateViewModel();
        using var coordinator = new ApplicationUpdateCoordinator(
            viewModel, service, store, "5.0", () => now);

        Task check;
        using (var primaryReadBlock = new FileStream(
                   primaryPath, FileMode.Open, FileAccess.Write, FileShare.Delete))
        {
            check = coordinator.CheckManuallyAsync(TestContext.Current.CancellationToken);
            await service.Started.WaitAsync(TestContext.Current.CancellationToken);
        }

        service.Complete();
        await check;

        var persisted = store.Load().UpdateCheckSettings;
        Assert.True(persisted.IsAutomaticCheckEnabled);
        Assert.Equal(now, persisted.LastCheckUtc);
        Assert.Equal("5.1", persisted.LatestKnownVersion);
        Assert.Equal("5.1", persisted.LastNotifiedVersion);
    }

    [Fact]
    public async Task CachedSettings_DoNotOverwriteAnotherWindowUpdateHistoryWhenOptingOut()
    {
        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);
        Assert.True(store.TrySave(new UserSettingsDb
        {
            UpdateCheckSettings = new UpdateCheckSettings
            {
                IsAutomaticCheckEnabled = true,
                LatestKnownVersion = "5.1",
                LastNotifiedVersion = "5.1"
            }
        }));
        using var viewModel = CreateViewModel();
        using var coordinator = new ApplicationUpdateCoordinator(
            viewModel,
            new RecordingUpdateService(UpdateAvailable("5.2")),
            store,
            "5.0");
        await coordinator.OpenManualCheckAsync(TestContext.Current.CancellationToken);

        var anotherWindow = new UserSettingsStore(() => temp.Path);
        var changed = anotherWindow.Load();
        changed.UpdateCheckSettings = changed.UpdateCheckSettings with
        {
            LatestKnownVersion = "5.2",
            LastNotifiedVersion = "5.2"
        };
        Assert.True(anotherWindow.TryPersistUpdateCheckSettings(changed));

        await coordinator.SetAutomaticCheckEnabledAsync(
            false,
            TestContext.Current.CancellationToken);

        var persisted = store.Load().UpdateCheckSettings;
        Assert.False(persisted.IsAutomaticCheckEnabled);
        Assert.Equal("5.2", persisted.LatestKnownVersion);
        Assert.Equal("5.2", persisted.LastNotifiedVersion);
    }

    [Fact]
    public async Task AutomaticCheck_PreservesConcurrentOptOutWhileRecordingAndNotifying()
    {
        using var temp = new TemporaryDirectory();
        var store = new UserSettingsStore(() => temp.Path);
        var now = new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
        Assert.True(store.TrySave(new UserSettingsDb
        {
            UpdateCheckSettings = new UpdateCheckSettings
            {
                IsAutomaticCheckEnabled = true,
                LastCheckUtc = now.AddDays(-8),
                LatestKnownVersion = "5.1",
                LastNotifiedVersion = "5.1"
            }
        }));
        var anotherWindow = new UserSettingsStore(() => temp.Path);
        var service = new RecordingUpdateService(UpdateAvailable("5.2"), () =>
        {
            var changed = anotherWindow.Load();
            changed.UpdateCheckSettings = changed.UpdateCheckSettings with
            {
                IsAutomaticCheckEnabled = false
            };
            Assert.True(anotherWindow.TryPersistUpdateCheckSettings(changed));
        });
        using var viewModel = CreateViewModel();
        using var coordinator = new ApplicationUpdateCoordinator(
            viewModel, service, store, "5.0", () => now);

        await coordinator.RunAutomaticCheckIfDueAsync(TestContext.Current.CancellationToken);

        var persisted = store.Load().UpdateCheckSettings;
        Assert.False(persisted.IsAutomaticCheckEnabled);
        Assert.Equal(now, persisted.LastCheckUtc);
        Assert.Equal("5.2", persisted.LatestKnownVersion);
        Assert.Equal("5.2", persisted.LastNotifiedVersion);
    }

    [Fact]
    public async Task AutomaticCheck_OptOut_PerformsNoNetworkRequest()
    {
        using var temp = new TemporaryDirectory();
        var service = new RecordingUpdateService(UpdateAvailable("4.10.0"));
        using var viewModel = CreateViewModel();
        using var coordinator = new ApplicationUpdateCoordinator(
            viewModel,
            service,
            new UserSettingsStore(() => temp.Path),
            "4.9.0");

        await coordinator.RunAutomaticCheckIfDueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, service.CallCount);
        Assert.False(viewModel.UpdatePopoverOpen);
        Assert.False(viewModel.AutomaticUpdateChecksEnabled);
    }

    [Fact]
    public async Task Startup_RestoresCachedUpdateIndicatorWithoutNetworkWhenChecksAreDisabled()
    {
        using var temp = new TemporaryDirectory();
        var settingsStore = new UserSettingsStore(() => temp.Path);
        Assert.True(settingsStore.TrySave(new UserSettingsDb
        {
            UpdateCheckSettings = new UpdateCheckSettings
            {
                IsAutomaticCheckEnabled = false,
                LastCheckUtc = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero),
                LatestKnownVersion = "5.1",
                LastNotifiedVersion = "5.1"
            }
        }));
        var service = new RecordingUpdateService(UpdateAvailable("5.2"));
        using var viewModel = CreateViewModel();
        using var coordinator = new ApplicationUpdateCoordinator(
            viewModel,
            service,
            settingsStore,
            "5.0");

        await coordinator.RunAutomaticCheckIfDueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, service.CallCount);
        Assert.True(viewModel.IsKnownUpdateAvailable);
        Assert.Equal(UpdateCheckPresentationState.UpdateAvailable, viewModel.UpdateCheckState);
        Assert.False(viewModel.UpdatePopoverOpen);
    }

    [Fact]
    public async Task AutomaticPreference_AppliesImmediatelyAndOptOutPreventsRequests()
    {
        using var temp = new TemporaryDirectory();
        var settingsStore = new UserSettingsStore(() => temp.Path);
        var service = new RecordingUpdateService(UpdateAvailable("4.10.0"));
        using var viewModel = CreateViewModel();
        using var coordinator = new ApplicationUpdateCoordinator(
            viewModel,
            service,
            settingsStore,
            "4.9.0");

        await coordinator.SetAutomaticCheckEnabledAsync(
            true,
            TestContext.Current.CancellationToken);

        Assert.True(viewModel.AutomaticUpdateChecksEnabled);
        Assert.True(settingsStore.Load().UpdateCheckSettings.IsAutomaticCheckEnabled);
        Assert.Equal(0, service.CallCount);

        await coordinator.SetAutomaticCheckEnabledAsync(
            false,
            TestContext.Current.CancellationToken);
        await coordinator.RunAutomaticCheckIfDueAsync(TestContext.Current.CancellationToken);

        Assert.False(viewModel.AutomaticUpdateChecksEnabled);
        Assert.False(settingsStore.Load().UpdateCheckSettings.IsAutomaticCheckEnabled);
        Assert.Equal(0, service.CallCount);
    }

    [Fact]
    public async Task AutomaticCheck_HonorsOptOutSavedByAnotherWindowAfterSettingsWereCached()
    {
        using var temp = new TemporaryDirectory();
        var settingsStore = new UserSettingsStore(() => temp.Path);
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        Assert.True(settingsStore.TrySave(new UserSettingsDb
        {
            UpdateCheckSettings = new UpdateCheckSettings
            {
                IsAutomaticCheckEnabled = true,
                LastCheckUtc = now.AddDays(-8)
            }
        }));
        var service = new RecordingUpdateService(UpdateAvailable("5.1"));
        using var viewModel = CreateViewModel();
        using var coordinator = new ApplicationUpdateCoordinator(
            viewModel,
            service,
            settingsStore,
            "5.0",
            () => now);

        await coordinator.OpenManualCheckAsync(TestContext.Current.CancellationToken);
        var anotherWindow = new UserSettingsStore(() => temp.Path);
        var changed = anotherWindow.Load();
        changed.UpdateCheckSettings = changed.UpdateCheckSettings with
        {
            IsAutomaticCheckEnabled = false
        };
        Assert.True(anotherWindow.TryPersistUpdateCheckSettings(changed));

        await coordinator.RunAutomaticCheckIfDueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, service.CallCount);
        Assert.False(viewModel.AutomaticUpdateChecksEnabled);
        Assert.False(settingsStore.Load().UpdateCheckSettings.IsAutomaticCheckEnabled);
    }

    [Theory]
    [InlineData("5.1", "5.1", "5.1")]
    [InlineData("5.2", "5.2", "5.2")]
    [InlineData("invalid", "5.1", "5.1")]
    public async Task AutomaticCheck_DoesNotNotifyVersionAlreadyMarkedByAnotherWindowDuringRequest(
        string storedLatestVersion,
        string previouslyNotifiedVersion,
        string expectedLatestVersion)
    {
        using var temp = new TemporaryDirectory();
        var settingsStore = new UserSettingsStore(() => temp.Path);
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        Assert.True(settingsStore.TrySave(new UserSettingsDb
        {
            UpdateCheckSettings = new UpdateCheckSettings
            {
                IsAutomaticCheckEnabled = true,
                LastCheckUtc = now.AddDays(-8)
            }
        }));
        var service = new PausedUpdateService(UpdateAvailable("5.1"));
        using var viewModel = CreateViewModel();
        using var coordinator = new ApplicationUpdateCoordinator(
            viewModel,
            service,
            settingsStore,
            "5.0",
            () => now);

        var check = coordinator.RunAutomaticCheckIfDueAsync(TestContext.Current.CancellationToken);
        await service.Started.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        try
        {
            var anotherWindow = new UserSettingsStore(() => temp.Path);
            var changed = anotherWindow.Load();
            changed.UpdateCheckSettings = changed.UpdateCheckSettings with
            {
                LatestKnownVersion = storedLatestVersion,
                LastNotifiedVersion = previouslyNotifiedVersion
            };
            Assert.True(anotherWindow.TryPersistUpdateCheckSettings(changed));
        }
        finally
        {
            service.Complete();
        }
        await check;

        Assert.False(viewModel.UpdatePopoverOpen);
        var persisted = settingsStore.Load().UpdateCheckSettings;
        Assert.Equal(now, persisted.LastCheckUtc);
        Assert.Equal(expectedLatestVersion, persisted.LatestKnownVersion);
        Assert.Equal(previouslyNotifiedVersion, persisted.LastNotifiedVersion);
    }

    [Fact]
    public async Task AutomaticCheck_OlderInFlightResponseDoesNotHideNewerPersistedUpdate()
    {
        using var temp = new TemporaryDirectory();
        var settingsStore = new UserSettingsStore(() => temp.Path);
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        Assert.True(settingsStore.TrySave(new UserSettingsDb
        {
            UpdateCheckSettings = new UpdateCheckSettings
            {
                IsAutomaticCheckEnabled = true,
                LastCheckUtc = now.AddDays(-8)
            }
        }));
        var service = new PausedUpdateService(new ApplicationUpdateCheckResult(
            ApplicationUpdateAvailability.UpToDate,
            "5.0",
            "5.0"));
        using var viewModel = CreateViewModel();
        using var coordinator = new ApplicationUpdateCoordinator(
            viewModel,
            service,
            settingsStore,
            "5.0",
            () => now);

        var check = coordinator.RunAutomaticCheckIfDueAsync(TestContext.Current.CancellationToken);
        await service.Started.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        try
        {
            var anotherWindow = new UserSettingsStore(() => temp.Path);
            var changed = anotherWindow.Load();
            changed.UpdateCheckSettings = changed.UpdateCheckSettings with
            {
                LatestKnownVersion = "5.2"
            };
            Assert.True(anotherWindow.TryPersistUpdateCheckSettings(changed));
        }
        finally
        {
            service.Complete();
        }
        await check;

        Assert.Equal("5.2", settingsStore.Load().UpdateCheckSettings.LatestKnownVersion);
        Assert.Equal(UpdateCheckPresentationState.UpdateAvailable, viewModel.UpdateCheckState);
        Assert.Equal("Latest version: v5.2", viewModel.LatestApplicationVersionText);
        Assert.True(viewModel.IsKnownUpdateAvailable);
        Assert.False(viewModel.UpdatePopoverOpen);
    }

    [Fact]
    public async Task AutomaticCheck_DueUpdate_NotifiesOnlyOncePerRelease()
    {
        using var temp = new TemporaryDirectory();
        var settingsStore = new UserSettingsStore(() => temp.Path);
        Assert.True(settingsStore.TrySave(new UserSettingsDb
        {
            UpdateCheckSettings = new UpdateCheckSettings
            {
                IsAutomaticCheckEnabled = true
            }
        }));
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var service = new RecordingUpdateService(UpdateAvailable("4.10.0"));
        using var viewModel = CreateViewModel();
        using var coordinator = new ApplicationUpdateCoordinator(
            viewModel,
            service,
            settingsStore,
            "4.9.0+local",
            () => now);

        await coordinator.RunAutomaticCheckIfDueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, service.CallCount);
        Assert.True(viewModel.UpdatePopoverOpen);
        Assert.True(viewModel.IsKnownUpdateAvailable);
        Assert.Equal(UpdateCheckPresentationState.UpdateAvailable, viewModel.UpdateCheckState);
        var persisted = settingsStore.Load();
        Assert.Equal(now, persisted.UpdateCheckSettings.LastCheckUtc);
        Assert.Equal("4.10.0", persisted.UpdateCheckSettings.LatestKnownVersion);
        Assert.Equal("4.10.0", persisted.UpdateCheckSettings.LastNotifiedVersion);

        viewModel.UpdatePopoverOpen = false;
        now = now.AddDays(8);
        await coordinator.RunAutomaticCheckIfDueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, service.CallCount);
        Assert.False(viewModel.UpdatePopoverOpen);
        Assert.True(viewModel.IsKnownUpdateAvailable);
        Assert.Equal(now, settingsStore.Load().UpdateCheckSettings.LastCheckUtc);
    }

    [Fact]
    public async Task AutomaticCheck_NoUpdate_IsSilentAndRecordsCadence()
    {
        using var temp = new TemporaryDirectory();
        var settingsStore = new UserSettingsStore(() => temp.Path);
        Assert.True(settingsStore.TrySave(new UserSettingsDb
        {
            UpdateCheckSettings = new UpdateCheckSettings
            {
                IsAutomaticCheckEnabled = true
            }
        }));
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var service = new RecordingUpdateService(new ApplicationUpdateCheckResult(
            ApplicationUpdateAvailability.UpToDate,
            "4.9.0",
            "4.9.0"));
        using var viewModel = CreateViewModel();
        using var coordinator = new ApplicationUpdateCoordinator(
            viewModel,
            service,
            settingsStore,
            "4.9.0",
            () => now);

        await coordinator.RunAutomaticCheckIfDueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, service.CallCount);
        Assert.False(viewModel.UpdatePopoverOpen);
        var persisted = settingsStore.Load();
        Assert.Equal(now, persisted.UpdateCheckSettings.LastCheckUtc);
        Assert.Equal("4.9.0", persisted.UpdateCheckSettings.LatestKnownVersion);
    }

    [Fact]
    public async Task AutomaticCheck_Failure_IsSilentAndDoesNotDelayNextStartupRetry()
    {
        using var temp = new TemporaryDirectory();
        var settingsStore = new UserSettingsStore(() => temp.Path);
        Assert.True(settingsStore.TrySave(new UserSettingsDb
        {
            UpdateCheckSettings = new UpdateCheckSettings
            {
                IsAutomaticCheckEnabled = true
            }
        }));
        var service = new RecordingUpdateService(new ApplicationUpdateCheckResult(
            ApplicationUpdateAvailability.CheckFailed,
            "4.9.0"));
        using var viewModel = CreateViewModel();
        using var coordinator = new ApplicationUpdateCoordinator(
            viewModel,
            service,
            settingsStore,
            "4.9.0");

        await coordinator.RunAutomaticCheckIfDueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, service.CallCount);
        Assert.False(viewModel.UpdatePopoverOpen);
        Assert.Null(settingsStore.Load().UpdateCheckSettings.LastCheckUtc);
    }

    [Fact]
    public async Task AutomaticCheck_AfterMonthRunsOnceWithoutReplayingMissedIntervals()
    {
        using var temp = new TemporaryDirectory();
        var settingsStore = new UserSettingsStore(() => temp.Path);
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        Assert.True(settingsStore.TrySave(new UserSettingsDb
        {
            UpdateCheckSettings = new UpdateCheckSettings
            {
                IsAutomaticCheckEnabled = true,
                LastCheckUtc = now.AddMonths(-1),
                LatestKnownVersion = "5.0"
            }
        }));
        var service = new RecordingUpdateService(new ApplicationUpdateCheckResult(
            ApplicationUpdateAvailability.UpToDate,
            "5.0",
            "5.0"));
        using var viewModel = CreateViewModel();
        using var coordinator = new ApplicationUpdateCoordinator(
            viewModel,
            service,
            settingsStore,
            "5.0",
            () => now);

        await coordinator.RunAutomaticCheckIfDueAsync(TestContext.Current.CancellationToken);
        await coordinator.RunAutomaticCheckIfDueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, service.CallCount);
        Assert.False(viewModel.UpdatePopoverOpen);
        Assert.Equal(now, settingsStore.Load().UpdateCheckSettings.LastCheckUtc);
    }

    [Fact]
    public async Task AutomaticCheck_NewerReleaseAfterPreviousNotificationNotifiesAgain()
    {
        using var temp = new TemporaryDirectory();
        var settingsStore = new UserSettingsStore(() => temp.Path);
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        Assert.True(settingsStore.TrySave(new UserSettingsDb
        {
            UpdateCheckSettings = new UpdateCheckSettings
            {
                IsAutomaticCheckEnabled = true,
                LastCheckUtc = now.AddDays(-8),
                LatestKnownVersion = "5.0",
                LastNotifiedVersion = "5.0"
            }
        }));
        var service = new RecordingUpdateService(UpdateAvailable("5.1"));
        using var viewModel = CreateViewModel();
        using var coordinator = new ApplicationUpdateCoordinator(
            viewModel,
            service,
            settingsStore,
            "5.0",
            () => now);

        await coordinator.RunAutomaticCheckIfDueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, service.CallCount);
        Assert.True(viewModel.UpdatePopoverOpen);
        Assert.Equal(UpdateCheckPresentationState.UpdateAvailable, viewModel.UpdateCheckState);
        var persisted = settingsStore.Load().UpdateCheckSettings;
        Assert.Equal("5.1", persisted.LatestKnownVersion);
        Assert.Equal("5.1", persisted.LastNotifiedVersion);
    }

    [Fact]
    public async Task AutomaticCheck_FailurePreservesSnapshotAndRemainsDueOnNextStartup()
    {
        using var temp = new TemporaryDirectory();
        var settingsStore = new UserSettingsStore(() => temp.Path);
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var previousCheck = now.AddDays(-8);
        Assert.True(settingsStore.TrySave(new UserSettingsDb
        {
            UpdateCheckSettings = new UpdateCheckSettings
            {
                IsAutomaticCheckEnabled = true,
                LastCheckUtc = previousCheck,
                LatestKnownVersion = "5.1",
                LastNotifiedVersion = "5.1"
            }
        }));
        var service = new RecordingUpdateService(new ApplicationUpdateCheckResult(
            ApplicationUpdateAvailability.CheckFailed,
            "5.0"));

        using (var firstViewModel = CreateViewModel())
        using (var firstCoordinator = new ApplicationUpdateCoordinator(
                   firstViewModel,
                   service,
                   settingsStore,
                   "5.0",
                   () => now))
        {
            await firstCoordinator.RunAutomaticCheckIfDueAsync(
                TestContext.Current.CancellationToken);
            Assert.False(firstViewModel.UpdatePopoverOpen);
        }

        using var secondViewModel = CreateViewModel();
        using var secondCoordinator = new ApplicationUpdateCoordinator(
            secondViewModel,
            service,
            new UserSettingsStore(() => temp.Path),
            "5.0",
            () => now);
        await secondCoordinator.RunAutomaticCheckIfDueAsync(TestContext.Current.CancellationToken);
        await secondCoordinator.OpenManualCheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, service.CallCount);
        Assert.Equal(UpdateCheckPresentationState.UpdateAvailable, secondViewModel.UpdateCheckState);
        var persisted = settingsStore.Load().UpdateCheckSettings;
        Assert.Equal(previousCheck, persisted.LastCheckUtc);
        Assert.Equal("5.1", persisted.LatestKnownVersion);
        Assert.Equal("5.1", persisted.LastNotifiedVersion);
    }

    [Fact]
    public async Task AutomaticCheck_DeletedSettingsResetsOptInAndPerformsNoRequest()
    {
        using var temp = new TemporaryDirectory();
        var settingsStore = new UserSettingsStore(() => temp.Path);
        Assert.True(settingsStore.TrySave(new UserSettingsDb
        {
            UpdateCheckSettings = new UpdateCheckSettings
            {
                IsAutomaticCheckEnabled = true,
                LastCheckUtc = DateTimeOffset.UtcNow,
                LatestKnownVersion = "5.0",
                LastNotifiedVersion = "5.0"
            }
        }));
        File.Delete(settingsStore.GetPath());
        File.Delete(settingsStore.GetPath() + ".bak");
        var service = new RecordingUpdateService(UpdateAvailable("5.1"));
        using var viewModel = CreateViewModel();
        using var coordinator = new ApplicationUpdateCoordinator(
            viewModel,
            service,
            new UserSettingsStore(() => temp.Path),
            "5.0");

        await coordinator.RunAutomaticCheckIfDueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, service.CallCount);
        Assert.False(viewModel.AutomaticUpdateChecksEnabled);
        Assert.False(viewModel.UpdatePopoverOpen);
    }

    [Fact]
    public async Task ManualCheck_OpensReadyStateThenShowsResultAndPersistsPreference()
    {
        using var temp = new TemporaryDirectory();
        var settingsStore = new UserSettingsStore(() => temp.Path);
        var service = new RecordingUpdateService(UpdateAvailable("4.10.0"));
        using var viewModel = CreateViewModel();
        using var coordinator = new ApplicationUpdateCoordinator(
            viewModel,
            service,
            settingsStore,
            "4.9.0");

        await coordinator.OpenManualCheckAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.UpdatePopoverOpen);
        Assert.Equal(UpdateCheckPresentationState.Ready, viewModel.UpdateCheckState);
        Assert.Equal(0, service.CallCount);

        await coordinator.SetAutomaticCheckEnabledAsync(
            true,
            TestContext.Current.CancellationToken);
        await coordinator.CheckManuallyAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, service.CallCount);
        Assert.Equal(UpdateCheckPresentationState.UpdateAvailable, viewModel.UpdateCheckState);
        var persisted = settingsStore.Load();
        Assert.True(persisted.UpdateCheckSettings.IsAutomaticCheckEnabled);
        Assert.Equal("4.10.0", persisted.UpdateCheckSettings.LatestKnownVersion);
        Assert.Equal("4.10.0", persisted.UpdateCheckSettings.LastNotifiedVersion);
    }

    [Fact]
    public async Task ManualCheck_SuccessfulResultSurvivesCoordinatorAndProcessStateRestart()
    {
        using var temp = new TemporaryDirectory();
        var settingsStore = new UserSettingsStore(() => temp.Path);
        var firstService = new RecordingUpdateService(UpdateAvailable("4.10.0"));
        using (var firstViewModel = CreateViewModel())
        using (var firstCoordinator = new ApplicationUpdateCoordinator(
                   firstViewModel,
                   firstService,
                   settingsStore,
                   "4.9.0"))
        {
            await firstCoordinator.OpenManualCheckAsync(TestContext.Current.CancellationToken);
            await firstCoordinator.CheckManuallyAsync(TestContext.Current.CancellationToken);
            Assert.Equal(UpdateCheckPresentationState.UpdateAvailable, firstViewModel.UpdateCheckState);
        }

        var secondService = new RecordingUpdateService(new ApplicationUpdateCheckResult(
            ApplicationUpdateAvailability.CheckFailed,
            "4.9.0"));
        using var secondViewModel = CreateViewModel();
        using var secondCoordinator = new ApplicationUpdateCoordinator(
            secondViewModel,
            secondService,
            new UserSettingsStore(() => temp.Path),
            "4.9.0");

        await secondCoordinator.OpenManualCheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, secondService.CallCount);
        Assert.True(secondViewModel.UpdatePopoverOpen);
        Assert.Equal(UpdateCheckPresentationState.UpdateAvailable, secondViewModel.UpdateCheckState);
        Assert.Equal("Latest version: v4.10.0", secondViewModel.LatestApplicationVersionText);
    }

    [Fact]
    public async Task ManualCheck_FailureDoesNotEraseLastSuccessfulResult()
    {
        using var temp = new TemporaryDirectory();
        var settingsStore = new UserSettingsStore(() => temp.Path);
        var checkedAt = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.True(settingsStore.TrySave(new UserSettingsDb
        {
            UpdateCheckSettings = new UpdateCheckSettings
            {
                LastCheckUtc = checkedAt,
                LatestKnownVersion = "4.10.0",
                LastNotifiedVersion = "4.10.0"
            }
        }));
        var service = new RecordingUpdateService(new ApplicationUpdateCheckResult(
            ApplicationUpdateAvailability.CheckFailed,
            "4.9.0"));
        using var viewModel = CreateViewModel();
        using var coordinator = new ApplicationUpdateCoordinator(
            viewModel,
            service,
            settingsStore,
            "4.9.0");

        await coordinator.OpenManualCheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(UpdateCheckPresentationState.UpdateAvailable, viewModel.UpdateCheckState);

        await coordinator.CheckManuallyAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckPresentationState.Failed, viewModel.UpdateCheckState);
        Assert.True(viewModel.IsKnownUpdateAvailable);
        var persisted = settingsStore.Load().UpdateCheckSettings;
        Assert.Equal(checkedAt, persisted.LastCheckUtc);
        Assert.Equal("4.10.0", persisted.LatestKnownVersion);
        Assert.Equal("4.10.0", persisted.LastNotifiedVersion);

        viewModel.UpdatePopoverOpen = false;
        await coordinator.OpenManualCheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, service.CallCount);
        Assert.Equal(UpdateCheckPresentationState.UpdateAvailable, viewModel.UpdateCheckState);
    }

    [Theory]
    [InlineData("4.9", UpdateCheckPresentationState.UpdateAvailable)]
    [InlineData("5.0", UpdateCheckPresentationState.UpToDate)]
    [InlineData("5.1", UpdateCheckPresentationState.CurrentVersionNewer)]
    public async Task CachedResult_IsReevaluatedAgainstEveryRunningApplicationVersion(
        string runningVersion,
        UpdateCheckPresentationState expectedState)
    {
        using var temp = new TemporaryDirectory();
        var settingsStore = new UserSettingsStore(() => temp.Path);
        Assert.True(settingsStore.TrySave(new UserSettingsDb
        {
            UpdateCheckSettings = new UpdateCheckSettings
            {
                LastCheckUtc = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero),
                LatestKnownVersion = "5.0",
                LastNotifiedVersion = "5.0"
            }
        }));
        var service = new RecordingUpdateService(new ApplicationUpdateCheckResult(
            ApplicationUpdateAvailability.CheckFailed,
            runningVersion));
        using var viewModel = CreateViewModel();
        using var coordinator = new ApplicationUpdateCoordinator(
            viewModel,
            service,
            settingsStore,
            runningVersion);

        await coordinator.OpenManualCheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, service.CallCount);
        Assert.Equal(expectedState, viewModel.UpdateCheckState);
        Assert.Equal($"Current version: v{runningVersion}", viewModel.CurrentApplicationVersionText);
        Assert.Equal("Latest version: v5.0", viewModel.LatestApplicationVersionText);
    }

    private static ApplicationUpdateCheckResult UpdateAvailable(string latestVersion)
        => new(
            ApplicationUpdateAvailability.UpdateAvailable,
            "4.9.0",
            latestVersion);

    private static MainWindowViewModel CreateViewModel()
    {
        var catalog = new StubLocalizationCatalog(
            new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
            {
                [AppLanguage.En] = new Dictionary<string, string>
                {
                    ["Update.CurrentVersion"] = "Current version: {0}",
                    ["Update.LatestVersion"] = "Latest version: {0}",
                    ["Update.Check"] = "Check",
                    ["Update.CheckAgain"] = "Check again",
                    ["Update.Checking"] = "Checking…",
                    ["Update.Retry"] = "Try again"
                }
            });
        return new MainWindowViewModel(
            new LocalizationService(catalog, AppLanguage.En),
            new HelpContentProvider());
    }

    private sealed class RecordingUpdateService(
        ApplicationUpdateCheckResult result,
        Action? beforeReturn = null)
        : IApplicationUpdateService
    {
        public int CallCount { get; private set; }

        public Task<ApplicationUpdateCheckResult> CheckAsync(
            string currentVersion,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            beforeReturn?.Invoke();
            return Task.FromResult(result with { CurrentVersion = currentVersion });
        }
    }

    private sealed class PausedUpdateService(ApplicationUpdateCheckResult result)
        : IApplicationUpdateService
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ApplicationUpdateCheckResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public Task Started => _started.Task;
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<ApplicationUpdateCheckResult> CheckAsync(
            string currentVersion,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            _started.TrySetResult();
            return _completion.Task.WaitAsync(cancellationToken);
        }

        public void Complete() => _completion.TrySetResult(result);
    }
}
