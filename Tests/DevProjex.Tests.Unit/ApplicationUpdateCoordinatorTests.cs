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
        Assert.Equal(now, settingsStore.Load().UpdateCheckSettings.LastCheckUtc);
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
        Assert.Equal("4.10.0", persisted.UpdateCheckSettings.LastNotifiedVersion);
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
                [AppLanguage.En] = new Dictionary<string, string>()
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
