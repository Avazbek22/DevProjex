using DevProjex.Application.Updates;
using DevProjex.Infrastructure.ThemePresets;

namespace DevProjex.Avalonia.Coordinators;

internal sealed class ApplicationUpdateCoordinator : IDisposable
{
    private readonly MainWindowViewModel _viewModel;
    private readonly IApplicationUpdateService _updateService;
    private readonly UserSettingsStore _settingsStore;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly string _currentVersion;

    private UserSettingsDb? _settings;
    private bool _disposed;

    public ApplicationUpdateCoordinator(
        MainWindowViewModel viewModel,
        IApplicationUpdateService updateService,
        UserSettingsStore settingsStore,
        string currentVersion,
        Func<DateTimeOffset>? utcNow = null)
    {
        _viewModel = viewModel;
        _updateService = updateService;
        _settingsStore = settingsStore;
        _currentVersion = NormalizeCurrentVersion(currentVersion);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _viewModel.SetCurrentApplicationVersion(_currentVersion);
    }

    public async Task OpenManualCheckAsync(CancellationToken cancellationToken)
    {
        // Opening the popover is a UI action and must not wait behind disk locking.
        // The persisted opt-in state is loaded asynchronously after the surface is visible.
        _viewModel.UpdatePopoverOpen = true;
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var settings = await EnsureSettingsLoadedAsync(cancellationToken);
            _viewModel.AutomaticUpdateChecksEnabled =
                settings.UpdateCheckSettings.IsAutomaticCheckEnabled;
            if (_viewModel.UpdateCheckState is
                UpdateCheckPresentationState.Ready or
                UpdateCheckPresentationState.Failed)
            {
                RestoreLastSuccessfulResult(settings.UpdateCheckSettings);
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task CheckManuallyAsync(CancellationToken cancellationToken)
    {
        if (!await _operationGate.WaitAsync(0, cancellationToken))
            return;

        try
        {
            var settings = await EnsureSettingsLoadedAsync(cancellationToken);
            _viewModel.BeginUpdateCheck();
            var result = await _updateService.CheckAsync(_currentVersion, cancellationToken);
            _viewModel.CompleteUpdateCheck(result);
            await RecordCheckAsync(
                settings,
                result,
                markAvailableVersionAsNotified: true,
                cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task SetAutomaticCheckEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var settings = await EnsureSettingsLoadedAsync(cancellationToken);
            var previousValue = settings.UpdateCheckSettings.IsAutomaticCheckEnabled;
            settings.UpdateCheckSettings = settings.UpdateCheckSettings with
            {
                IsAutomaticCheckEnabled = enabled
            };
            _viewModel.AutomaticUpdateChecksEnabled = enabled;
            if (!await PersistSettingsAsync(
                    settings,
                    current => current with { IsAutomaticCheckEnabled = enabled },
                    cancellationToken))
            {
                settings.UpdateCheckSettings = settings.UpdateCheckSettings with
                {
                    IsAutomaticCheckEnabled = previousValue
                };
                _viewModel.AutomaticUpdateChecksEnabled = previousValue;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task RunAutomaticCheckIfDueAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var settings = await RefreshSettingsForAutomaticCheckAsync(cancellationToken);
            if (settings is null)
            {
                if (_settings is { } cached)
                    RestoreLastSuccessfulResult(cached.UpdateCheckSettings);
                return;
            }
            var preferences = settings.UpdateCheckSettings;
            _viewModel.AutomaticUpdateChecksEnabled = preferences.IsAutomaticCheckEnabled;
            // Hydrate the passive update indicator from the last successful result even
            // when automatic network checks are disabled or not due yet. The stored
            // release version remains the source of truth; no separate UI flag is saved.
            RestoreLastSuccessfulResult(preferences);
            if (!ApplicationUpdateSchedule.IsDue(
                    preferences.IsAutomaticCheckEnabled,
                    preferences.LastCheckUtc,
                    _utcNow()))
            {
                return;
            }

            var result = await _updateService.CheckAsync(_currentVersion, cancellationToken);
            await RecordCheckAsync(
                settings,
                result,
                markAvailableVersionAsNotified: false,
                cancellationToken);
            PresentAutomaticCheckResult(settings.UpdateCheckSettings, result);

            if (result.Availability != ApplicationUpdateAvailability.UpdateAvailable)
                return;

            var newlyNotified = false;
            var persisted = await PersistSettingsAsync(
                settings,
                current =>
                {
                    if (WasVersionAlreadyNotified(current.LastNotifiedVersion, result.LatestVersion))
                        return current;
                    newlyNotified = true;
                    return current with
                    {
                        LastNotifiedVersion = result.LatestVersion ?? string.Empty
                    };
                },
                cancellationToken);
            if (!persisted || newlyNotified)
                _viewModel.UpdatePopoverOpen = true;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        // Window shutdown cancels in-flight work before this coordinator is disposed.
        // SemaphoreSlim remains undisposed so a canceled continuation can still release
        // its lease without racing the visual-tree teardown.
        if (_updateService is IDisposable disposable)
            disposable.Dispose();
    }

    private async Task<UserSettingsDb> EnsureSettingsLoadedAsync(
        CancellationToken cancellationToken)
    {
        if (_settings is not null)
            return _settings;

        var (loaded, settings) = await Task.Run(() =>
        {
            var loaded = _settingsStore.TryLoad(out var settings);
            return (loaded, settings);
        }, cancellationToken);
        if (loaded)
            _settings = settings;
        return settings;
    }

    private async Task<UserSettingsDb?> RefreshSettingsForAutomaticCheckAsync(
        CancellationToken cancellationToken)
    {
        var (loaded, settings) = await Task.Run(() =>
        {
            var loaded = _settingsStore.TryLoad(out var settings);
            return (loaded, settings);
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!loaded)
            return null;
        _settings = settings;
        return settings;
    }

    private async Task RecordCheckAsync(
        UserSettingsDb settings,
        ApplicationUpdateCheckResult result,
        bool markAvailableVersionAsNotified,
        CancellationToken cancellationToken)
    {
        // A failed request must not erase the last successful snapshot. Keeping that
        // snapshot separate from notification history makes the result durable across
        // process restarts without presenting a failed attempt as fresh release data.
        if (result.Availability == ApplicationUpdateAvailability.CheckFailed ||
            !ApplicationReleaseVersion.TryParse(result.LatestVersion, out var latestVersion))
        {
            return;
        }

        var shouldMarkNotified = markAvailableVersionAsNotified &&
                                 result.Availability == ApplicationUpdateAvailability.UpdateAvailable;
        var lastNotifiedVersion = shouldMarkNotified
            ? result.LatestVersion ?? string.Empty
            : settings.UpdateCheckSettings.LastNotifiedVersion;
        var checkTime = _utcNow();
        var latestKnownVersion = latestVersion.ToString();
        settings.UpdateCheckSettings = settings.UpdateCheckSettings with
        {
            LastCheckUtc = checkTime,
            LatestKnownVersion = latestKnownVersion,
            LastNotifiedVersion = lastNotifiedVersion
        };
        await PersistSettingsAsync(
            settings,
            current => current with
            {
                LastCheckUtc = checkTime,
                LatestKnownVersion = ApplicationReleaseVersion.TryParse(
                        current.LatestKnownVersion,
                        out var storedVersion) &&
                    storedVersion.CompareTo(latestVersion) > 0
                        ? storedVersion.ToString()
                        : latestKnownVersion,
                LastNotifiedVersion = shouldMarkNotified
                    ? lastNotifiedVersion
                    : current.LastNotifiedVersion
            },
            cancellationToken);
    }

    private async Task<bool> PersistSettingsAsync(
        UserSettingsDb settings,
        Func<UpdateCheckSettings, UpdateCheckSettings> applyChanges,
        CancellationToken cancellationToken)
    {
        return await Task.Run(
            () => _settingsStore.TryPersistUpdateCheckSettings(settings, applyChanges),
            cancellationToken);
    }

    private static bool WasVersionAlreadyNotified(
        string lastNotifiedVersion,
        string? latestVersion)
        => ApplicationReleaseVersion.TryParse(lastNotifiedVersion, out var notified) &&
           ApplicationReleaseVersion.TryParse(latestVersion, out var latest) &&
           notified.CompareTo(latest) >= 0;

    private void PresentAutomaticCheckResult(
        UpdateCheckSettings settings,
        ApplicationUpdateCheckResult result)
    {
        if (result.Availability == ApplicationUpdateAvailability.CheckFailed)
            return;

        if (ApplicationReleaseVersion.TryParse(result.LatestVersion, out var reported) &&
            ApplicationReleaseVersion.TryParse(settings.LatestKnownVersion, out var stored) &&
            stored.CompareTo(reported) > 0 &&
            RestoreLastSuccessfulResult(settings))
        {
            return;
        }

        _viewModel.CompleteUpdateCheck(result);
    }

    private bool RestoreLastSuccessfulResult(UpdateCheckSettings settings)
    {
        if (settings.LastCheckUtc is null ||
            !ApplicationReleaseVersion.TryParse(_currentVersion, out var current) ||
            !ApplicationReleaseVersion.TryParse(settings.LatestKnownVersion, out var latest))
        {
            return false;
        }

        var availability = current.CompareTo(latest) switch
        {
            < 0 => ApplicationUpdateAvailability.UpdateAvailable,
            > 0 => ApplicationUpdateAvailability.CurrentVersionNewer,
            _ => ApplicationUpdateAvailability.UpToDate
        };
        _viewModel.CompleteUpdateCheck(new ApplicationUpdateCheckResult(
            availability,
            current.ToString(),
            latest.ToString()));
        return true;
    }

    private static string NormalizeCurrentVersion(string currentVersion)
        => ApplicationReleaseVersion.TryParse(currentVersion, out var parsed)
            ? parsed.ToString()
            : currentVersion;
}
