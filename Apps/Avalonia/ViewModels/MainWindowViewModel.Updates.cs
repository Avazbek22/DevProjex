using DevProjex.Application.Updates;

namespace DevProjex.Avalonia.ViewModels;

public enum UpdateCheckPresentationState
{
    Ready,
    Checking,
    UpToDate,
    UpdateAvailable,
    CurrentVersionNewer,
    Failed
}

public sealed partial class MainWindowViewModel
{
    private bool _updatePopoverOpen;
    private bool _automaticUpdateChecksEnabled;
    private UpdateCheckPresentationState _updateCheckState;
    private string _currentApplicationVersion = string.Empty;
    private string _latestApplicationVersion = string.Empty;

    public bool UpdatePopoverOpen
    {
        get => _updatePopoverOpen;
        set
        {
            if (_updatePopoverOpen == value)
                return;

            _updatePopoverOpen = value;
            RaisePropertyChanged();
        }
    }

    public bool AutomaticUpdateChecksEnabled
    {
        get => _automaticUpdateChecksEnabled;
        set
        {
            if (_automaticUpdateChecksEnabled == value)
                return;

            _automaticUpdateChecksEnabled = value;
            RaisePropertyChanged();
        }
    }

    public UpdateCheckPresentationState UpdateCheckState
    {
        get => _updateCheckState;
        private set
        {
            if (_updateCheckState == value)
                return;

            _updateCheckState = value;
            RaisePropertyChanged();
            RaiseUpdateCheckStatePropertiesChanged();
        }
    }

    public bool IsUpdateCheckReady => UpdateCheckState == UpdateCheckPresentationState.Ready;
    public bool IsUpdateCheckInProgress => UpdateCheckState == UpdateCheckPresentationState.Checking;
    public bool IsUpdateAvailable => UpdateCheckState == UpdateCheckPresentationState.UpdateAvailable;
    public bool IsApplicationUpToDate => UpdateCheckState == UpdateCheckPresentationState.UpToDate;
    public bool IsCurrentApplicationVersionNewer =>
        UpdateCheckState == UpdateCheckPresentationState.CurrentVersionNewer;
    public bool HasUpdateCheckFailed => UpdateCheckState == UpdateCheckPresentationState.Failed;
    public bool IsUpdateCheckButtonVisible => true;
    public bool IsLatestApplicationVersionVisible =>
        IsUpdateAvailable || IsApplicationUpToDate || IsCurrentApplicationVersionNewer;

    public string CurrentApplicationVersionText =>
        _localization.Format(
            "Update.CurrentVersion",
            FormatApplicationVersion(_currentApplicationVersion));

    public string LatestApplicationVersionText =>
        _localization.Format(
            "Update.LatestVersion",
            FormatApplicationVersion(_latestApplicationVersion));

    public string UpdateCheckActionText => UpdateCheckState switch
    {
        UpdateCheckPresentationState.Checking => UpdateCheckingButton,
        UpdateCheckPresentationState.Failed => UpdateRetryButton,
        UpdateCheckPresentationState.Ready => UpdateCheckButton,
        _ => UpdateCheckAgainButton
    };

    public string MenuHelpCheckUpdates { get; private set; } = string.Empty;
    public string UpdateTitle { get; private set; } = string.Empty;
    public string UpdatePrompt { get; private set; } = string.Empty;
    public string UpdateAutomaticWeekly { get; private set; } = string.Empty;
    public string UpdateAvailableTitle { get; private set; } = string.Empty;
    public string UpdateUpToDateTitle { get; private set; } = string.Empty;
    public string UpdateCurrentVersionNewerTitle { get; private set; } = string.Empty;
    public string UpdateFailedTitle { get; private set; } = string.Empty;
    public string UpdateFailedMessage { get; private set; } = string.Empty;
    public string UpdateCheckButton { get; private set; } = string.Empty;
    public string UpdateCheckAgainButton { get; private set; } = string.Empty;
    public string UpdateCheckingButton { get; private set; } = string.Empty;
    public string UpdateRetryButton { get; private set; } = string.Empty;
    public string UpdateOpenRepository { get; private set; } = string.Empty;

    public void SetCurrentApplicationVersion(string version)
    {
        _currentApplicationVersion = version;
        RaisePropertyChanged(nameof(CurrentApplicationVersionText));
    }

    public void PrepareManualUpdateCheck()
    {
        _latestApplicationVersion = string.Empty;
        UpdateCheckState = UpdateCheckPresentationState.Ready;
        RaisePropertyChanged(nameof(LatestApplicationVersionText));
    }

    public void BeginUpdateCheck()
        => UpdateCheckState = UpdateCheckPresentationState.Checking;

    public void CompleteUpdateCheck(ApplicationUpdateCheckResult result)
    {
        _currentApplicationVersion = result.CurrentVersion;
        _latestApplicationVersion = result.LatestVersion ?? string.Empty;
        UpdateCheckState = result.Availability switch
        {
            ApplicationUpdateAvailability.UpdateAvailable =>
                UpdateCheckPresentationState.UpdateAvailable,
            ApplicationUpdateAvailability.UpToDate =>
                UpdateCheckPresentationState.UpToDate,
            ApplicationUpdateAvailability.CurrentVersionNewer =>
                UpdateCheckPresentationState.CurrentVersionNewer,
            _ => UpdateCheckPresentationState.Failed
        };
        RaisePropertyChanged(nameof(CurrentApplicationVersionText));
        RaisePropertyChanged(nameof(LatestApplicationVersionText));
    }

    private void UpdateApplicationUpdateLocalization()
    {
        MenuHelpCheckUpdates = _localization["Menu.Help.CheckUpdates"];
        UpdateTitle = _localization["Update.Title"];
        UpdatePrompt = _localization["Update.Prompt"];
        UpdateAutomaticWeekly = _localization["Update.AutomaticWeekly"];
        UpdateAvailableTitle = _localization["Update.Available"];
        UpdateUpToDateTitle = _localization["Update.UpToDate"];
        UpdateCurrentVersionNewerTitle = _localization["Update.CurrentVersionNewer"];
        UpdateFailedTitle = _localization["Update.Failed"];
        UpdateFailedMessage = _localization["Update.FailedMessage"];
        UpdateCheckButton = _localization["Update.Check"];
        UpdateCheckAgainButton = _localization["Update.CheckAgain"];
        UpdateCheckingButton = _localization["Update.Checking"];
        UpdateRetryButton = _localization["Update.Retry"];
        UpdateOpenRepository = _localization["Update.OpenRepository"];

        RaisePropertyChanged(nameof(MenuHelpCheckUpdates));
        RaisePropertyChanged(nameof(UpdateTitle));
        RaisePropertyChanged(nameof(UpdatePrompt));
        RaisePropertyChanged(nameof(UpdateAutomaticWeekly));
        RaisePropertyChanged(nameof(UpdateAvailableTitle));
        RaisePropertyChanged(nameof(UpdateUpToDateTitle));
        RaisePropertyChanged(nameof(UpdateCurrentVersionNewerTitle));
        RaisePropertyChanged(nameof(UpdateFailedTitle));
        RaisePropertyChanged(nameof(UpdateFailedMessage));
        RaisePropertyChanged(nameof(UpdateCheckButton));
        RaisePropertyChanged(nameof(UpdateCheckAgainButton));
        RaisePropertyChanged(nameof(UpdateCheckingButton));
        RaisePropertyChanged(nameof(UpdateRetryButton));
        RaisePropertyChanged(nameof(UpdateOpenRepository));
        RaisePropertyChanged(nameof(CurrentApplicationVersionText));
        RaisePropertyChanged(nameof(LatestApplicationVersionText));
        RaisePropertyChanged(nameof(UpdateCheckActionText));
    }

    private void RaiseUpdateCheckStatePropertiesChanged()
    {
        RaisePropertyChanged(nameof(IsUpdateCheckReady));
        RaisePropertyChanged(nameof(IsUpdateCheckInProgress));
        RaisePropertyChanged(nameof(IsUpdateAvailable));
        RaisePropertyChanged(nameof(IsApplicationUpToDate));
        RaisePropertyChanged(nameof(IsCurrentApplicationVersionNewer));
        RaisePropertyChanged(nameof(HasUpdateCheckFailed));
        RaisePropertyChanged(nameof(IsUpdateCheckButtonVisible));
        RaisePropertyChanged(nameof(IsLatestApplicationVersionVisible));
        RaisePropertyChanged(nameof(UpdateCheckActionText));
    }

    private static string FormatApplicationVersion(string version)
        => string.IsNullOrWhiteSpace(version) ? "—" : $"v{version}";
}
