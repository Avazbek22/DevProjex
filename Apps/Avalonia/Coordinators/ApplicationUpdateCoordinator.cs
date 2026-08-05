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
            if (!await PersistSettingsAsync(settings, cancellationToken))
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
            var settings = await EnsureSettingsLoadedAsync(cancellationToken);
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
            var wasAlreadyNotified = WasVersionAlreadyNotified(
                settings.UpdateCheckSettings.LastNotifiedVersion,
                result.LatestVersion);
            await RecordCheckAsync(
                settings,
                result,
                markAvailableVersionAsNotified: false,
                cancellationToken);
            if (result.Availability != ApplicationUpdateAvailability.CheckFailed)
                _viewModel.CompleteUpdateCheck(result);

            if (result.Availability != ApplicationUpdateAvailability.UpdateAvailable ||
                wasAlreadyNotified)
            {
                return;
            }

            settings.UpdateCheckSettings = settings.UpdateCheckSettings with
            {
                LastNotifiedVersion = result.LatestVersion ?? string.Empty
            };
            await PersistSettingsAsync(settings, cancellationToken);
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

        _settings = await Task.Run(_settingsStore.Load, cancellationToken);
        return _settings;
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

        var lastNotifiedVersion = markAvailableVersionAsNotified &&
                                  result.Availability == ApplicationUpdateAvailability.UpdateAvailable
            ? result.LatestVersion ?? string.Empty
            : settings.UpdateCheckSettings.LastNotifiedVersion;
        settings.UpdateCheckSettings = settings.UpdateCheckSettings with
        {
            LastCheckUtc = _utcNow(),
            LatestKnownVersion = latestVersion.ToString(),
            LastNotifiedVersion = lastNotifiedVersion
        };
        await PersistSettingsAsync(settings, cancellationToken);
    }

    private async Task<bool> PersistSettingsAsync(
        UserSettingsDb settings,
        CancellationToken cancellationToken)
    {
        return await Task.Run(
            () => _settingsStore.TryPersistUpdateCheckSettings(settings),
            cancellationToken);
    }

    private static bool WasVersionAlreadyNotified(
        string lastNotifiedVersion,
        string? latestVersion)
        => ApplicationReleaseVersion.TryParse(lastNotifiedVersion, out var notified) &&
           ApplicationReleaseVersion.TryParse(latestVersion, out var latest) &&
           notified.Equals(latest);

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
