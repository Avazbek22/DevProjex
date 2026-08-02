using DevProjex.Application.Updates;
using DevProjex.Infrastructure.ThemePresets;

namespace DevProjex.Tests.Unit;

public sealed class ApplicationUpdateCoordinatorTests
{
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

    private sealed class RecordingUpdateService(ApplicationUpdateCheckResult result)
        : IApplicationUpdateService
    {
        public int CallCount { get; private set; }

        public Task<ApplicationUpdateCheckResult> CheckAsync(
            string currentVersion,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(result with { CurrentVersion = currentVersion });
        }
    }
}
