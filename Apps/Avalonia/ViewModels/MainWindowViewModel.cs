using DevProjex.Avalonia.Collections;
using ThemeEffectMode = DevProjex.Infrastructure.ThemePresets.ThemeEffectMode;
using ThemeSelectionMode = DevProjex.Infrastructure.ThemePresets.ThemeSelectionMode;

namespace DevProjex.Avalonia.ViewModels;

/// <summary>
/// Specifies the tree format used by desktop preview, copy, and export actions.
/// </summary>
public enum ExportFormat
{
    Ascii,
    Json,
    Xml,
    Markdown
}

public enum PreviewContentMode
{
    Tree,
    Content,
    TreeAndContent
}

public enum PreviewWorkspaceMode
{
    Off,
    TreeAndPreview,
    PreviewOnly
}

public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    public const string TitleVersion = "4.9";
    public const string BaseTitle = "DevProjex v" + TitleVersion;
    public const string BaseTitleWithAuthor = "DevProjex by Olimoff v" + TitleVersion;
    public const double DefaultTreeFontSize = 15;
    public const double DefaultPreviewFontSize = 15;

    private readonly LocalizationService _localization;
    private readonly HelpContentProvider _helpContentProvider;

    // Event handler delegates for proper cleanup
    private readonly NotifyCollectionChangedEventHandler _ignoreOptionsChangedHandler;
    private readonly NotifyCollectionChangedEventHandler _extensionsChangedHandler;
    private readonly NotifyCollectionChangedEventHandler _rootFoldersChangedHandler;
    private readonly NotifyCollectionChangedEventHandler _recentFoldersChangedHandler;
    private readonly NotifyCollectionChangedEventHandler _recentRepositoriesChangedHandler;
    private bool _disposed;

    private string _title;
    private bool _isProjectLoaded;
    private bool _settingsVisible;
    private bool _searchVisible;
    private bool _isSearchInProgress;
    private bool _isFilterInProgress;
    private string _searchQuery = string.Empty;
    private int _searchCurrentMatchIndex;
    private int _searchTotalMatches;
    private int _filterMatchCount;
    private string _nameFilter = string.Empty;

    private FontFamily? _selectedFontFamily;
    private FontFamily? _pendingFontFamily;

    private double _treeFontSize = DefaultTreeFontSize;
    private double _previewFontSize = DefaultPreviewFontSize;

    private bool _allExtensionsChecked;
    private bool _allRootFoldersChecked;
    private bool _allIgnoreChecked;
    private bool _isDarkTheme = true;
    private ThemeSelectionMode _selectedThemeMode = ThemeSelectionMode.System;
    private bool _isCompactMode;
    private bool _isTreeExpansionAnimationEnabled = true;
    private bool _filterVisible;
    private ExportFormat _selectedExportFormat = ExportFormat.Ascii;
    private PreviewContentMode _selectedPreviewContentMode = PreviewContentMode.Tree;
    private bool _isMicaEnabled;
    private bool _isMicaAvailable = true;
    private bool _isAcrylicEnabled;
    private bool _isAcrylicAvailable = true;
    private bool _isTransparentEnabled = true;
    private PreviewWorkspaceMode _previewWorkspaceMode;
    private bool _isPreviewCompactModeActive;
    private bool _isPreviewLoading;
    private string _previewText = string.Empty;
    private IPreviewTextDocument? _previewDocument;
    private int _previewLineCount = 1;

    private double _backgroundTransparency = 65;
    private double _panelContrast = 50;
    private double _borderVisibility = 50;
    private double _menuTransparency = 50;

    private bool _themePopoverOpen;
    private bool _helpPopoverOpen;
    private bool _helpDocsPopoverOpen;

    // Git state
    private ProjectSourceType _projectSourceType = ProjectSourceType.LocalFolder;
    private string _currentBranch = string.Empty;
    private string _gitCloneUrl = string.Empty;
    private string _gitCloneStatus = string.Empty;
    private bool _gitCloneInProgress;
    private string _menuFileRecent = string.Empty;
    private string _menuFileRecentEmpty = string.Empty;
    private string _menuFileOpenNewWindow = string.Empty;
    private string _gitCloneRecentRepositoriesLabel = string.Empty;
    private double _helpPopoverMaxWidth = 800;
    private double _helpPopoverMaxHeight = 680;
    private double _aboutPopoverMaxWidth = 520;
    private double _aboutPopoverMaxHeight = 380;
    private string _statusTreeStatsText = string.Empty;
    private string _statusContentStatsText = string.Empty;
    private string _statusPreviewSelectionStatsText = string.Empty;
    private string _statusOperationText = string.Empty;
    private bool _statusBusy;
    private bool _statusPresentationReady = true;
    private bool _applySettingsBusyDelayElapsed;
    private CancellationTokenSource? _applySettingsBusyDelayCts;
    private bool _hasPendingFilterSettingsChanges;
    private bool _statusMetricsVisible;
    private bool _statusPreviewSelectionVisible;
    private bool _statusProgressIsIndeterminate = true;
    private double _statusProgressValue;
    private bool _isProjectCopyExportInProgress;

    public MainWindowViewModel(LocalizationService localization, HelpContentProvider helpContentProvider)
    {
        _localization = localization;
        _helpContentProvider = helpContentProvider;
        _title = BaseTitleWithAuthor;
        _allExtensionsChecked = true;
        _allRootFoldersChecked = true;
        _allIgnoreChecked = true;
        UpdateLocalization();

        // Create named handlers for proper cleanup
        _ignoreOptionsChangedHandler = (_, _) => UpdateAllCheckboxLabels();
        _extensionsChangedHandler = (_, _) => UpdateAllCheckboxLabels();
        _rootFoldersChangedHandler = (_, _) => UpdateAllCheckboxLabels();
        _recentFoldersChangedHandler = (_, _) =>
        {
            RaisePropertyChanged(nameof(HasRecentFolders));
            RaisePropertyChanged(nameof(RecentFoldersMenuVisible));
        };
        _recentRepositoriesChangedHandler = (_, _) =>
        {
            RaisePropertyChanged(nameof(HasRecentRepositories));
            RaisePropertyChanged(nameof(GitCloneRecentRepositoriesVisible));
        };

        // Subscribe to collection changes to update "All" checkbox labels with counts
        IgnoreOptions.CollectionChanged += _ignoreOptionsChangedHandler;
        Extensions.CollectionChanged += _extensionsChangedHandler;
        RootFolders.CollectionChanged += _rootFoldersChangedHandler;
        RecentFolders.CollectionChanged += _recentFoldersChangedHandler;
        RecentRepositories.CollectionChanged += _recentRepositoriesChangedHandler;
        ToastItems.CollectionChanged += OnToastItemsCollectionChanged;
    }

    private ObservableCollection<TreeNodeViewModel> _treeNodes = [];

    public ObservableCollection<TreeNodeViewModel> TreeNodes
    {
        get => _treeNodes;
        private set
        {
            if (ReferenceEquals(_treeNodes, value)) return;
            _treeNodes = value;
            RaisePropertyChanged();
        }
    }
    public ObservableCollection<SelectionOptionViewModel> RootFolders { get; } = new ResettableObservableCollection<SelectionOptionViewModel>();
    public ObservableCollection<SelectionOptionViewModel> Extensions { get; } = new ResettableObservableCollection<SelectionOptionViewModel>();
    public ObservableCollection<IgnoreOptionViewModel> IgnoreOptions { get; } = new ResettableObservableCollection<IgnoreOptionViewModel>();
    public ObservableCollection<FontFamily> FontFamilies { get; } = [];
    public ObservableCollection<RecentProjectEntryViewModel> RecentFolders { get; } = [];
    public ObservableCollection<RecentProjectEntryViewModel> RecentRepositories { get; } = [];

    public void ResetTreeNodes()
    {
        TreeNodes = [];
    }

    public string StatusTreeStatsText
    {
        get => _statusTreeStatsText;
        set
        {
            if (_statusTreeStatsText == value) return;
            _statusTreeStatsText = value;
            RaisePropertyChanged();
        }
    }

    public string StatusContentStatsText
    {
        get => _statusContentStatsText;
        set
        {
            if (_statusContentStatsText == value) return;
            _statusContentStatsText = value;
            RaisePropertyChanged();
        }
    }

    public string StatusOperationText
    {
        get => _statusOperationText;
        set
        {
            if (_statusOperationText == value) return;
            _statusOperationText = value;
            RaisePropertyChanged();
        }
    }

    public string StatusPreviewSelectionStatsText
    {
        get => _statusPreviewSelectionStatsText;
        set
        {
            if (_statusPreviewSelectionStatsText == value) return;
            _statusPreviewSelectionStatsText = value;
            RaisePropertyChanged();
        }
    }

    public bool StatusBusy
    {
        get => _statusBusy;
        set
        {
            if (_statusBusy == value) return;
            _statusBusy = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(StatusOperationVisible));
            RaisePropertyChanged(nameof(StatusProgressVisible));
            RaisePropertyChanged(nameof(StatusProgressPercentVisible));
            RaisePropertyChanged(nameof(CenteredPreviewSelectionMetricsVisible));
            // Also update IsIndeterminate since it depends on StatusBusy
            // This stops the indeterminate animation when progress bar is hidden
            RaisePropertyChanged(nameof(StatusProgressIsIndeterminate));
            RaisePropertyChanged(nameof(IsApplySettingsAttentionActive));
            UpdateApplySettingsBusyState(value);
        }
    }

    public bool StatusOperationVisible => _statusBusy && _statusPresentationReady;

    internal bool StatusPresentationReady
    {
        get => _statusPresentationReady;
        set
        {
            if (_statusPresentationReady == value)
                return;

            _statusPresentationReady = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(StatusOperationVisible));
            RaisePropertyChanged(nameof(StatusProgressVisible));
            RaisePropertyChanged(nameof(StatusProgressPercentVisible));
            RaisePropertyChanged(nameof(StatusProgressIsIndeterminate));
        }
    }

    public bool StatusProgressIsIndeterminate
    {
        // Only return true when also visible - prevents animation running when hidden
        get => _statusProgressIsIndeterminate && StatusOperationVisible;
        set
        {
            if (_statusProgressIsIndeterminate == value) return;
            _statusProgressIsIndeterminate = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(StatusProgressPercentVisible));
        }
    }

    public double StatusProgressValue
    {
        get => _statusProgressValue;
        set
        {
            if (Math.Abs(_statusProgressValue - value) < 0.1) return;
            _statusProgressValue = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(StatusProgressPercentText));
        }
    }

    public bool StatusProgressVisible => StatusOperationVisible;

    public bool StatusProgressPercentVisible =>
        StatusOperationVisible && !_statusProgressIsIndeterminate;

    public string StatusProgressPercentText => $"{Math.Clamp((int)Math.Round(_statusProgressValue), 0, 100)}%";

    public bool StatusMetricsVisible
    {
        get => _statusMetricsVisible;
        set
        {
            if (_statusMetricsVisible == value) return;
            _statusMetricsVisible = value;
            RaisePropertyChanged();
        }
    }

    public string Title
    {
        get => _title;
        set
        {
            if (_title == value) return;
            _title = value;
            RaisePropertyChanged();
        }
    }

    public bool IsProjectLoaded
    {
        get => _isProjectLoaded;
        set
        {
            if (_isProjectLoaded == value) return;
            _isProjectLoaded = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsSearchAvailable));
            RaisePropertyChanged(nameof(IsSearchFilterAvailable));
            RaisePropertyChanged(nameof(AreFilterSettingsEnabled));
            RaisePropertyChanged(nameof(CanApplySettings));
            RaisePropertyChanged(nameof(IsApplySettingsAttentionActive));
            RaisePropertyChanged(nameof(CanExportProjectCopy));
            RaisePropertyChanged(nameof(CanUseProjectWorkspaceActions));
            RaisePropertyChanged(nameof(CanTogglePreview));
            RaisePropertyChanged(nameof(CanRefreshLocalProject));
            RaisePropertyChanged(nameof(CanGetGitUpdates));
        }
    }

    public PreviewWorkspaceMode PreviewWorkspaceMode
    {
        get => _previewWorkspaceMode;
        set
        {
            if (_previewWorkspaceMode == value) return;
            var previousIsCompactModeEffective = IsCompactModeEffective;
            var previousCanToggleCompactMode = CanToggleCompactMode;
            _previewWorkspaceMode = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsPreviewMode));
            RaisePropertyChanged(nameof(IsPreviewTreeVisible));
            RaisePropertyChanged(nameof(IsPreviewOnlyMode));
            RaisePropertyChanged(nameof(CanTogglePreview));
            RaisePreviewStatePropertiesChanged();

            if (previousCanToggleCompactMode != CanToggleCompactMode)
                RaisePropertyChanged(nameof(CanToggleCompactMode));

            if (previousIsCompactModeEffective != IsCompactModeEffective)
            {
                RaiseCompactModePropertiesChanged();
            }
        }
    }

    public bool IsPreviewMode => _previewWorkspaceMode != PreviewWorkspaceMode.Off;

    public bool IsPreviewTreeVisible => _previewWorkspaceMode == PreviewWorkspaceMode.TreeAndPreview;

    public bool IsPreviewOnlyMode => _previewWorkspaceMode == PreviewWorkspaceMode.PreviewOnly;

    public bool IsAnyPreviewVisible => IsPreviewMode;

    public bool IsPreviewPaneVisible => IsPreviewMode;

    public bool IsTreePaneVisible => _previewWorkspaceMode != PreviewWorkspaceMode.PreviewOnly;

    public bool IsProjectCopyExportInProgress
    {
        get => _isProjectCopyExportInProgress;
        set
        {
            if (_isProjectCopyExportInProgress == value) return;
            _isProjectCopyExportInProgress = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(CanChangeProjectTree));
            RaisePropertyChanged(nameof(CanExportProjectCopy));
            RaisePropertyChanged(nameof(CanUseProjectWorkspaceActions));
            RaisePropertyChanged(nameof(CanTogglePreview));
            RaisePropertyChanged(nameof(IsSearchFilterAvailable));
            RaisePropertyChanged(nameof(AreFilterSettingsEnabled));
            RaisePropertyChanged(nameof(CanApplySettings));
            RaisePropertyChanged(nameof(IsApplySettingsAttentionActive));
            RaisePropertyChanged(nameof(CanRefreshLocalProject));
            RaisePropertyChanged(nameof(CanGetGitUpdates));
        }
    }

    public bool CanChangeProjectTree => !_isProjectCopyExportInProgress;

    public bool CanExportProjectCopy => _isProjectLoaded && !_isProjectCopyExportInProgress;

    public bool CanUseProjectWorkspaceActions => _isProjectLoaded && !_isProjectCopyExportInProgress;

    public bool CanTogglePreview => _isProjectLoaded && (!_isProjectCopyExportInProgress || IsPreviewMode);

    public bool IsSearchAvailable => _isProjectLoaded && IsTreePaneVisible;

    public bool IsSearchFilterAvailable => _isProjectLoaded && IsTreePaneVisible && !_isProjectCopyExportInProgress;

    public bool AreFilterSettingsEnabled => _isProjectLoaded && !_isProjectCopyExportInProgress;

    public bool CanApplySettings => _isProjectLoaded && !_applySettingsBusyDelayElapsed && !_isProjectCopyExportInProgress;

    public bool HasPendingFilterSettingsChanges => _hasPendingFilterSettingsChanges;

    public bool IsApplySettingsAttentionActive =>
        _hasPendingFilterSettingsChanges && CanApplySettings && !_statusBusy;

    internal void SetPendingFilterSettingsChanges(bool value)
    {
        if (_hasPendingFilterSettingsChanges == value)
            return;

        _hasPendingFilterSettingsChanges = value;
        RaisePropertyChanged(nameof(HasPendingFilterSettingsChanges));
        RaisePropertyChanged(nameof(IsApplySettingsAttentionActive));
    }

    private void UpdateApplySettingsBusyState(bool isBusy)
    {
        var previous = Interlocked.Exchange(
            ref _applySettingsBusyDelayCts,
            isBusy ? new CancellationTokenSource() : null);
        previous?.Cancel();
        previous?.Dispose();

        if (!isBusy)
        {
            if (!_applySettingsBusyDelayElapsed)
                return;

            _applySettingsBusyDelayElapsed = false;
            RaisePropertyChanged(nameof(CanApplySettings));
            RaisePropertyChanged(nameof(IsApplySettingsAttentionActive));
            return;
        }

        _ = DelayApplySettingsBusyStateAsync(_applySettingsBusyDelayCts!);
    }

    private async Task DelayApplySettingsBusyStateAsync(CancellationTokenSource delayCts)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), delayCts.Token).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_statusBusy || delayCts.IsCancellationRequested ||
                    !ReferenceEquals(Volatile.Read(ref _applySettingsBusyDelayCts), delayCts))
                {
                    return;
                }

                _applySettingsBusyDelayElapsed = true;
                RaisePropertyChanged(nameof(CanApplySettings));
                RaisePropertyChanged(nameof(IsApplySettingsAttentionActive));
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    public bool SettingsVisible
    {
        get => _settingsVisible;
        set
        {
            if (_settingsVisible == value) return;
            _settingsVisible = value;
            RaisePropertyChanged();
        }
    }

    public bool SearchVisible
    {
        get => _searchVisible;
        set
        {
            if (_searchVisible == value) return;
            _searchVisible = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(SearchMatchSummaryVisible));
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (_searchQuery == value) return;
            _searchQuery = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(SearchMatchSummaryVisible));
        }
    }

    public int SearchCurrentMatchIndex => _searchCurrentMatchIndex;

    public int SearchTotalMatches => _searchTotalMatches;

    public bool IsSearchInProgress => _isSearchInProgress;

    public bool SearchMatchSummaryVisible =>
        SearchVisible &&
        !_isSearchInProgress &&
        !string.IsNullOrWhiteSpace(_searchQuery) &&
        _searchTotalMatches > 0;

    public string SearchMatchSummaryText => $"({_searchCurrentMatchIndex} / {_searchTotalMatches})";

    public string NameFilter
    {
        get => _nameFilter;
        set
        {
            if (_nameFilter == value) return;
            _nameFilter = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(FilterMatchSummaryVisible));
        }
    }

    public int FilterMatchCount => _filterMatchCount;

    public bool IsFilterInProgress => _isFilterInProgress;

    public bool FilterMatchSummaryVisible =>
        FilterVisible &&
        !_isFilterInProgress &&
        !string.IsNullOrWhiteSpace(_nameFilter) &&
        _filterMatchCount > 0;

    public string FilterMatchSummaryText => $"({_filterMatchCount})";

    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set
        {
            if (_isDarkTheme == value) return;
            _isDarkTheme = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsLightTheme));
            RaisePropertyChanged(nameof(DropZoneShadeOpacity));
        }
    }

    public bool IsLightTheme => !_isDarkTheme;

    public ThemeSelectionMode SelectedThemeMode
    {
        get => _selectedThemeMode;
        set
        {
            if (_selectedThemeMode != value)
            {
                _selectedThemeMode = value;
                RaisePropertyChanged();
            }

            // The selector uses checkbox visuals. Re-publish the derived values so clicking
            // the already selected item cannot leave its local checked state toggled off.
            RaisePropertyChanged(nameof(IsSystemThemeSelected));
            RaisePropertyChanged(nameof(IsLightThemeSelected));
            RaisePropertyChanged(nameof(IsDarkThemeSelected));
        }
    }

    public bool IsSystemThemeSelected => _selectedThemeMode == ThemeSelectionMode.System;
    public bool IsLightThemeSelected => _selectedThemeMode == ThemeSelectionMode.Light;
    public bool IsDarkThemeSelected => _selectedThemeMode == ThemeSelectionMode.Dark;

    public bool IsCompactMode
    {
        get => _isCompactMode;
        set
        {
            if (_isCompactMode == value) return;
            _isCompactMode = value;
            RaisePropertyChanged();
            RaiseCompactModePropertiesChanged();
        }
    }

    public bool IsCompactModeEffective => _isCompactMode || (IsPreviewMode && _isPreviewCompactModeActive);

    public bool CanToggleCompactMode => !IsPreviewMode;

    public void SetPreviewCompactModeActive(bool active)
    {
        if (_isPreviewCompactModeActive == active)
            return;

        var previousIsCompactModeEffective = IsCompactModeEffective;
        _isPreviewCompactModeActive = active;
        if (previousIsCompactModeEffective != IsCompactModeEffective)
            RaiseCompactModePropertiesChanged();
    }

    public bool IsTreeExpansionAnimationEnabled
    {
        get => _isTreeExpansionAnimationEnabled;
        set
        {
            if (_isTreeExpansionAnimationEnabled == value) return;
            _isTreeExpansionAnimationEnabled = value;
            RaisePropertyChanged();
        }
    }

    public bool FilterVisible
    {
        get => _filterVisible;
        set
        {
            if (_filterVisible == value) return;
            _filterVisible = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(FilterMatchSummaryVisible));
        }
    }

    public ExportFormat SelectedExportFormat
    {
        get => _selectedExportFormat;
        set
        {
            if (_selectedExportFormat == value) return;
            _selectedExportFormat = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsAsciiFormatSelected));
            RaisePropertyChanged(nameof(IsJsonFormatSelected));
            RaisePropertyChanged(nameof(IsXmlFormatSelected));
            RaisePropertyChanged(nameof(IsMarkdownFormatSelected));
        }
    }

    public bool IsAsciiFormatSelected
    {
        get => _selectedExportFormat == ExportFormat.Ascii;
        set
        {
            if (value) SelectedExportFormat = ExportFormat.Ascii;
        }
    }

    public bool IsJsonFormatSelected
    {
        get => _selectedExportFormat == ExportFormat.Json;
        set
        {
            if (value) SelectedExportFormat = ExportFormat.Json;
        }
    }

    public bool IsXmlFormatSelected
    {
        get => _selectedExportFormat == ExportFormat.Xml;
        set
        {
            if (value) SelectedExportFormat = ExportFormat.Xml;
        }
    }

    public bool IsMarkdownFormatSelected
    {
        get => _selectedExportFormat == ExportFormat.Markdown;
        set
        {
            if (value) SelectedExportFormat = ExportFormat.Markdown;
        }
    }

    public PreviewContentMode SelectedPreviewContentMode
    {
        get => _selectedPreviewContentMode;
        set
        {
            if (_selectedPreviewContentMode == value) return;
            _selectedPreviewContentMode = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsPreviewTreeSelected));
            RaisePropertyChanged(nameof(IsPreviewContentSelected));
            RaisePropertyChanged(nameof(IsPreviewTreeAndContentSelected));
            RaisePropertyChanged(nameof(PreviewCopyCurrentModeTooltip));
        }
    }

    public bool IsPreviewTreeSelected => _selectedPreviewContentMode == PreviewContentMode.Tree;

    public bool IsPreviewContentSelected => _selectedPreviewContentMode == PreviewContentMode.Content;

    public bool IsPreviewTreeAndContentSelected => _selectedPreviewContentMode == PreviewContentMode.TreeAndContent;

    public string PreviewText
    {
        get => _previewText;
        set
        {
            if (_previewText == value) return;
            _previewText = value;
            RaisePropertyChanged();
        }
    }

    public IPreviewTextDocument? PreviewDocument
    {
        get => _previewDocument;
        set
        {
            if (ReferenceEquals(_previewDocument, value)) return;
            _previewDocument = value;
            RaisePropertyChanged();
        }
    }

    public bool StatusPreviewSelectionVisible
    {
        get => _statusPreviewSelectionVisible;
        set
        {
            if (_statusPreviewSelectionVisible == value) return;
            _statusPreviewSelectionVisible = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(CenteredPreviewSelectionMetricsVisible));
        }
    }

    public bool CenteredPreviewSelectionMetricsVisible => _statusPreviewSelectionVisible && !_statusBusy;

    public int PreviewLineCount
    {
        get => _previewLineCount;
        set
        {
            var normalized = Math.Max(1, value);
            if (_previewLineCount == normalized) return;
            _previewLineCount = normalized;
            RaisePropertyChanged();
        }
    }

    public bool IsPreviewLoading
    {
        get => _isPreviewLoading;
        set
        {
            if (_isPreviewLoading == value) return;
            _isPreviewLoading = value;
            RaisePropertyChanged();
        }
    }

    public bool IsMicaEnabled
    {
        get => _isMicaEnabled;
        set
        {
            if (_isMicaEnabled == value) return;
            _isMicaEnabled = value;
            if (value)
            {
                _isAcrylicEnabled = false;
                _isTransparentEnabled = false;
            }
            RaiseEffectPropertiesChanged();
        }
    }

    public bool IsAcrylicEnabled
    {
        get => _isAcrylicEnabled;
        set
        {
            if (_isAcrylicEnabled == value) return;
            _isAcrylicEnabled = value;
            if (value)
            {
                _isMicaEnabled = false;
                _isTransparentEnabled = false;
            }
            RaiseEffectPropertiesChanged();
        }
    }

    public bool IsTransparentEnabled
    {
        get => _isTransparentEnabled;
        set
        {
            if (_isTransparentEnabled == value) return;
            _isTransparentEnabled = value;
            if (value)
            {
                _isMicaEnabled = false;
                _isAcrylicEnabled = false;
            }
            RaiseEffectPropertiesChanged();
        }
    }

    // Computed: any effect is enabled
    public bool HasAnyEffect => _isTransparentEnabled || _isMicaEnabled || _isAcrylicEnabled;

    public ThemeEffectMode ActiveThemeEffect => !HasAnyEffect
        ? ThemeEffectMode.Solid
        : _isMicaEnabled
            ? ThemeEffectMode.Mica
            : _isAcrylicEnabled
                ? ThemeEffectMode.Acrylic
                : ThemeEffectMode.Transparent;

    public double DropZoneShadeOpacity =>
        !_isDarkTheme && ActiveThemeEffect is ThemeEffectMode.Solid or ThemeEffectMode.Mica
            ? 0.06
            : 0.15;

    public bool ShowBackgroundTransparencySlider => _isTransparentEnabled || _isAcrylicEnabled;

    private void RaiseEffectPropertiesChanged()
    {
        RaisePropertyChanged(nameof(IsMicaEnabled));
        RaisePropertyChanged(nameof(IsAcrylicEnabled));
        RaisePropertyChanged(nameof(IsTransparentEnabled));
        RaisePropertyChanged(nameof(HasAnyEffect));
        RaisePropertyChanged(nameof(ActiveThemeEffect));
        RaisePropertyChanged(nameof(DropZoneShadeOpacity));
        RaisePropertyChanged(nameof(ShowBackgroundTransparencySlider));
    }

    public bool IsMicaAvailable
    {
        get => _isMicaAvailable;
        private set
        {
            if (_isMicaAvailable == value) return;
            _isMicaAvailable = value;
            RaisePropertyChanged();
        }
    }

    public void SetMicaAvailability(bool isAvailable)
    {
        IsMicaAvailable = isAvailable;
        if (!isAvailable && _isMicaEnabled)
            SetThemeEffects(transparent: false, mica: false, acrylic: IsAcrylicAvailable);
    }

    public bool IsAcrylicAvailable
    {
        get => _isAcrylicAvailable;
        private set
        {
            if (_isAcrylicAvailable == value) return;
            _isAcrylicAvailable = value;
            RaisePropertyChanged();
        }
    }

    public void SetAcrylicAvailability(bool isAvailable)
    {
        IsAcrylicAvailable = isAvailable;
        if (!isAvailable && _isAcrylicEnabled)
            SetThemeEffects(transparent: false, mica: IsMicaAvailable, acrylic: false);
    }

    public void SetThemeEffects(bool transparent, bool mica, bool acrylic)
    {
        if ((transparent ? 1 : 0) + (mica ? 1 : 0) + (acrylic ? 1 : 0) > 1)
            throw new ArgumentException("Only one theme effect can be active at a time.");

        if (_isTransparentEnabled == transparent &&
            _isMicaEnabled == mica &&
            _isAcrylicEnabled == acrylic)
        {
            return;
        }

        _isTransparentEnabled = transparent;
        _isMicaEnabled = mica;
        _isAcrylicEnabled = acrylic;
        RaiseEffectPropertiesChanged();
    }

    private void RaisePreviewStatePropertiesChanged()
    {
        RaisePropertyChanged(nameof(IsAnyPreviewVisible));
        RaisePropertyChanged(nameof(IsPreviewPaneVisible));
        RaisePropertyChanged(nameof(IsTreePaneVisible));
        RaisePropertyChanged(nameof(IsPreviewTreeVisible));
        RaisePropertyChanged(nameof(IsPreviewOnlyMode));
        RaisePropertyChanged(nameof(IsSearchAvailable));
        RaisePropertyChanged(nameof(IsSearchFilterAvailable));
        RaisePropertyChanged(nameof(AreFilterSettingsEnabled));
    }

    private void RaiseCompactModePropertiesChanged()
    {
        RaisePropertyChanged(nameof(IsCompactModeEffective));
        RaisePropertyChanged(nameof(CanToggleCompactMode));
        RaisePropertyChanged(nameof(TreeItemSpacing));
        RaisePropertyChanged(nameof(TreeItemPadding));
        RaisePropertyChanged(nameof(TreeTextMargin));
    }

    // Methods for toggle behavior (click on active = disable)
    public void ToggleTransparent()
    {
        if (_isTransparentEnabled)
            SetThemeEffects(transparent: false, mica: false, acrylic: false);
        else
            SetThemeEffects(transparent: true, mica: false, acrylic: false);
    }

    public void ToggleMica()
    {
        if (!IsMicaAvailable)
            return;

        if (_isMicaEnabled)
            SetThemeEffects(transparent: false, mica: false, acrylic: false);
        else
            SetThemeEffects(transparent: false, mica: true, acrylic: false);
    }

    public void ToggleAcrylic()
    {
        if (!IsAcrylicAvailable)
            return;

        if (_isAcrylicEnabled)
            SetThemeEffects(transparent: false, mica: false, acrylic: false);
        else
            SetThemeEffects(transparent: false, mica: false, acrylic: true);
    }

    public bool ThemePopoverOpen
    {
        get => _themePopoverOpen;
        set
        {
            if (_themePopoverOpen == value) return;
            _themePopoverOpen = value;
            RaisePropertyChanged();
        }
    }

    public bool HelpPopoverOpen
    {
        get => _helpPopoverOpen;
        set
        {
            if (_helpPopoverOpen == value) return;
            _helpPopoverOpen = value;
            RaisePropertyChanged();
        }
    }

    public bool HelpDocsPopoverOpen
    {
        get => _helpDocsPopoverOpen;
        set
        {
            if (_helpDocsPopoverOpen == value) return;
            _helpDocsPopoverOpen = value;
            RaisePropertyChanged();
        }
    }

    // Git properties
    public ProjectSourceType ProjectSourceType
    {
        get => _projectSourceType;
        set
        {
            if (_projectSourceType == value) return;
            _projectSourceType = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsGitMode));
            RaisePropertyChanged(nameof(CanRefreshLocalProject));
            RaisePropertyChanged(nameof(CanGetGitUpdates));
        }
    }

    public bool IsGitMode => _projectSourceType == ProjectSourceType.GitClone;

    public bool CanRefreshLocalProject => _isProjectLoaded && !IsGitMode && !_isProjectCopyExportInProgress;

    public bool CanGetGitUpdates => _isProjectLoaded && IsGitMode && !_isProjectCopyExportInProgress;

    public string CurrentBranch
    {
        get => _currentBranch;
        set
        {
            if (_currentBranch == value) return;
            _currentBranch = value;
            RaisePropertyChanged();
        }
    }

    public ObservableCollection<GitBranch> GitBranches { get; } = [];

    public string GitCloneUrl
    {
        get => _gitCloneUrl;
        set
        {
            if (_gitCloneUrl == value) return;
            _gitCloneUrl = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(CanStartGitClone));
        }
    }

    public string GitCloneStatus
    {
        get => _gitCloneStatus;
        set
        {
            if (_gitCloneStatus == value) return;
            _gitCloneStatus = value;
            RaisePropertyChanged();
        }
    }

    public bool GitCloneInProgress
    {
        get => _gitCloneInProgress;
        set
        {
            if (_gitCloneInProgress == value) return;
            _gitCloneInProgress = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(CanStartGitClone));
            RaisePropertyChanged(nameof(GitCloneRecentRepositoriesVisible));
        }
    }

    public double HelpPopoverMaxWidth
    {
        get => _helpPopoverMaxWidth;
        set
        {
            if (Math.Abs(_helpPopoverMaxWidth - value) < 0.1) return;
            _helpPopoverMaxWidth = value;
            RaisePropertyChanged();
        }
    }

    public double HelpPopoverMaxHeight
    {
        get => _helpPopoverMaxHeight;
        set
        {
            if (Math.Abs(_helpPopoverMaxHeight - value) < 0.1) return;
            _helpPopoverMaxHeight = value;
            RaisePropertyChanged();
        }
    }

    public double AboutPopoverMaxWidth
    {
        get => _aboutPopoverMaxWidth;
        set
        {
            if (Math.Abs(_aboutPopoverMaxWidth - value) < 0.1) return;
            _aboutPopoverMaxWidth = value;
            RaisePropertyChanged();
        }
    }

    public double AboutPopoverMaxHeight
    {
        get => _aboutPopoverMaxHeight;
        set
        {
            if (Math.Abs(_aboutPopoverMaxHeight - value) < 0.1) return;
            _aboutPopoverMaxHeight = value;
            RaisePropertyChanged();
        }
    }

    public void UpdateHelpPopoverMaxSize(Size bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        const double padding = 16;
        var maxHelpWidth = Math.Max(260, Math.Min(800, (bounds.Width - padding) * 0.8));
        var maxHelpHeight = Math.Max(220, Math.Min(680, (bounds.Height - padding) * 0.9));
        var maxAboutWidth = Math.Min(624, (bounds.Width - padding) * 0.7);
        var maxAboutHeight = Math.Min(456, (bounds.Height - padding) * 0.7);

        HelpPopoverMaxWidth = maxHelpWidth;
        HelpPopoverMaxHeight = maxHelpHeight;
        AboutPopoverMaxWidth = maxAboutWidth;
        AboutPopoverMaxHeight = maxAboutHeight;
    }

    public double BackgroundTransparency
    {
        get => _backgroundTransparency;
        set
        {
            if (Math.Abs(_backgroundTransparency - value) < 0.1) return;
            _backgroundTransparency = value;
            RaisePropertyChanged();
        }
    }

    public double PanelContrast
    {
        get => _panelContrast;
        set
        {
            if (Math.Abs(_panelContrast - value) < 0.1) return;
            _panelContrast = value;
            RaisePropertyChanged();
        }
    }

    public double BorderVisibility
    {
        get => _borderVisibility;
        set
        {
            if (Math.Abs(_borderVisibility - value) < 0.1) return;
            _borderVisibility = value;
            RaisePropertyChanged();
        }
    }

    public double MenuTransparency
    {
        get => _menuTransparency;
        set
        {
            if (Math.Abs(_menuTransparency - value) < 0.1) return;
            _menuTransparency = value;
            RaisePropertyChanged();
        }
    }

    // Applied font (TreeView reads it from here)
    public FontFamily? SelectedFontFamily
    {
        get => _selectedFontFamily;
        set
        {
            if (_selectedFontFamily == value) return;
            _selectedFontFamily = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(TreeIconSize));
            RaisePropertyChanged(nameof(TreeTextMargin));
        }
    }

    // Staged tree font selection; Apply commits it to SelectedFontFamily.
    public FontFamily? PendingFontFamily
    {
        get => _pendingFontFamily;
        set
        {
            if (_pendingFontFamily == value) return;
            _pendingFontFamily = value;
            RaisePropertyChanged();
        }
    }

    public double TreeFontSize
    {
        get => _treeFontSize;
        set
        {
            if (Math.Abs(_treeFontSize - value) < 0.1) return;
            _treeFontSize = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(TreeIconSize));
        }
    }

    public double PreviewFontSize
    {
        get => _previewFontSize;
        set
        {
            if (Math.Abs(_previewFontSize - value) < 0.1) return;
            _previewFontSize = value;
            RaisePropertyChanged();
        }
    }

    private double TreeIconScale =>
        string.Equals(_selectedFontFamily?.Name, "Consolas", StringComparison.OrdinalIgnoreCase) ? 1.35 : 1.25;

    public double TreeIconSize => Math.Max(12, Math.Round(TreeFontSize * TreeIconScale, 0));

    // Change "top" parameter in Thickness for vertical width
    public Thickness TreeTextMargin =>
        string.Equals(_selectedFontFamily?.Name, "Consolas", StringComparison.OrdinalIgnoreCase)
            ? (IsCompactModeEffective
                ? new Thickness(0, 3, 0, 0)
                : new Thickness(0, 9, 0, 0))
            : new Thickness(0);

    // Tree row spacing is controlled in VM so compact mode is a single switch.
    public double TreeItemSpacing => IsCompactModeEffective ? 2 : 6;

    // Compact rows should stay dense without using negative padding, because
    // virtualized trees rely on stable item measurement for correct scroll extents.
    public Thickness TreeItemPadding => IsCompactModeEffective ? new Thickness(0) : new Thickness(4, 1);

    public void UpdateSearchMatchSummary(int currentIndex, int totalMatches)
    {
        var normalizedTotal = Math.Max(0, totalMatches);
        var normalizedCurrent = normalizedTotal == 0
            ? 0
            : Math.Clamp(currentIndex, 1, normalizedTotal);

        if (_searchCurrentMatchIndex == normalizedCurrent && _searchTotalMatches == normalizedTotal)
            return;

        _searchCurrentMatchIndex = normalizedCurrent;
        _searchTotalMatches = normalizedTotal;
        RaisePropertyChanged(nameof(SearchCurrentMatchIndex));
        RaisePropertyChanged(nameof(SearchTotalMatches));
        RaisePropertyChanged(nameof(SearchMatchSummaryText));
        RaisePropertyChanged(nameof(SearchMatchSummaryVisible));
    }

    public void SetSearchInProgress(bool isInProgress)
    {
        if (_isSearchInProgress == isInProgress)
            return;

        _isSearchInProgress = isInProgress;
        RaisePropertyChanged(nameof(IsSearchInProgress));
        RaisePropertyChanged(nameof(SearchMatchSummaryVisible));
    }

    public void UpdateFilterMatchSummary(int matchCount)
    {
        var normalizedCount = Math.Max(0, matchCount);
        if (_filterMatchCount == normalizedCount)
            return;

        _filterMatchCount = normalizedCount;
        RaisePropertyChanged(nameof(FilterMatchCount));
        RaisePropertyChanged(nameof(FilterMatchSummaryText));
        RaisePropertyChanged(nameof(FilterMatchSummaryVisible));
    }

    public void SetFilterInProgress(bool isInProgress)
    {
        if (_isFilterInProgress == isInProgress)
            return;

        _isFilterInProgress = isInProgress;
        RaisePropertyChanged(nameof(IsFilterInProgress));
        RaisePropertyChanged(nameof(FilterMatchSummaryVisible));
    }

    public bool AllExtensionsChecked
    {
        get => _allExtensionsChecked;
        set
        {
            if (_allExtensionsChecked == value) return;
            _allExtensionsChecked = value;
            RaisePropertyChanged();
        }
    }

    public bool AllRootFoldersChecked
    {
        get => _allRootFoldersChecked;
        set
        {
            if (_allRootFoldersChecked == value) return;
            _allRootFoldersChecked = value;
            RaisePropertyChanged();
        }
    }

    public bool HasRootFolderOptions => RootFolders.Count > 0;

    public bool HasRecentFolders => RecentFolders.Count > 0;

    // Expose File > Recent as soon as at least one persisted folder exists.
    // Hiding a single entry makes the feature look broken after restart.
    public bool RecentFoldersMenuVisible => HasRecentFolders;

    public bool HasRecentRepositories => RecentRepositories.Count > 0;

    // Hide the clone recent list while cloning is in progress to avoid
    // exposing stale selections during the active git operation.
    public bool GitCloneRecentRepositoriesVisible => !GitCloneInProgress && HasRecentRepositories;

    public bool CanStartGitClone => !GitCloneInProgress && !string.IsNullOrWhiteSpace(GitCloneUrl);

    public bool AllIgnoreChecked
    {
        get => _allIgnoreChecked;
        set
        {
            if (_allIgnoreChecked == value) return;
            _allIgnoreChecked = value;
            RaisePropertyChanged();
        }
    }

    public string MenuFile { get; private set; } = string.Empty;
    public string MenuFileOpen { get; private set; } = string.Empty;
    public string MenuFileOpenNewWindow
    {
        get => _menuFileOpenNewWindow;
        private set
        {
            if (_menuFileOpenNewWindow == value) return;
            _menuFileOpenNewWindow = value;
            RaisePropertyChanged();
        }
    }
    public string MenuFileRecent
    {
        get => _menuFileRecent;
        private set
        {
            if (_menuFileRecent == value) return;
            _menuFileRecent = value;
            RaisePropertyChanged();
        }
    }

    public string MenuFileRecentEmpty
    {
        get => _menuFileRecentEmpty;
        private set
        {
            if (_menuFileRecentEmpty == value) return;
            _menuFileRecentEmpty = value;
            RaisePropertyChanged();
        }
    }

    public string MenuFileRefresh { get; private set; } = string.Empty;
    public string MenuFileExport { get; private set; } = string.Empty;
    public string MenuFileExportTree { get; private set; } = string.Empty;
    public string MenuFileExportContent { get; private set; } = string.Empty;
    public string MenuFileExportTreeAndContent { get; private set; } = string.Empty;
    public string MenuFileExportProjectCopy { get; private set; } = string.Empty;
    public string MenuFileExportProjectCopyFolder { get; private set; } = string.Empty;
    public string MenuFileExportProjectCopyZip { get; private set; } = string.Empty;
    public string MenuFileExportProjectCopyFolderHelp { get; private set; } = string.Empty;
    public string MenuFileExportProjectCopyZipHelp { get; private set; } = string.Empty;
    public string MenuFileExit { get; private set; } = string.Empty;
    public string MenuCopy { get; private set; } = string.Empty;
    public string MenuCopyTree { get; private set; } = string.Empty;
    public string MenuCopyContent { get; private set; } = string.Empty;
    public string MenuCopyTreeAndContent { get; private set; } = string.Empty;
    public ObservableCollection<ToastMessageViewModel> ToastItems { get; private set; } = [];
    public bool HasToastItems => ToastItems.Count > 0;
    public string MenuView { get; private set; } = string.Empty;
    public string MenuViewExpandAll { get; private set; } = string.Empty;
    public string MenuViewCollapseAll { get; private set; } = string.Empty;
    public string MenuViewZoomIn { get; private set; } = string.Empty;
    public string MenuViewZoomOut { get; private set; } = string.Empty;
    public string MenuViewZoomReset { get; private set; } = string.Empty;
    public string MenuViewTreeFont { get; private set; } = string.Empty;
    public string MenuViewThemeTitle { get; private set; } = string.Empty;
    public string MenuViewLightTheme { get; private set; } = string.Empty;
    public string MenuViewDarkTheme { get; private set; } = string.Empty;
    public string MenuViewMica { get; private set; } = string.Empty;
    public string MenuViewAcrylic { get; private set; } = string.Empty;
    public string MenuViewCompactMode { get; private set; } = string.Empty;
    public string MenuViewTreeExpansionAnimation { get; private set; } = string.Empty;
    public string MenuOptions { get; private set; } = string.Empty;
    public string MenuOptionsTreeSettings { get; private set; } = string.Empty;
    public string MenuLanguage { get; private set; } = string.Empty;
    public string MenuHelp { get; private set; } = string.Empty;
    public string MenuHelpHelp { get; private set; } = string.Empty;
    public string MenuHelpTerminalCommand { get; private set; } = string.Empty;
    public string MenuHelpAbout { get; private set; } = string.Empty;
    public string MenuHelpResetSettings { get; private set; } = string.Empty;
    public string MenuHelpResetData { get; private set; } = string.Empty;
    public string HelpHelpTitle { get; private set; } = string.Empty;
    public string HelpHelpBody { get; private set; } = string.Empty;
    public string HelpHelpCopyAll { get; private set; } = string.Empty;
    public string HelpAboutTitle { get; private set; } = string.Empty;
    public string HelpAboutBody { get; private set; } = string.Empty;
    public string HelpAboutSupport { get; private set; } = string.Empty;
    public string HelpAboutOpenLink { get; private set; } = string.Empty;
    public string MenuTheme { get; private set; } = string.Empty;
    public string ThemeModeLabel { get; private set; } = string.Empty;
    public string ThemeSystemLabel { get; private set; } = string.Empty;
    public string ThemeEffectsLabel { get; private set; } = string.Empty;
    public string ThemeLightLabel { get; private set; } = string.Empty;
    public string ThemeDarkLabel { get; private set; } = string.Empty;
    public string ThemeTransparentLabel { get; private set; } = string.Empty;
    public string ThemeMicaLabel { get; private set; } = string.Empty;
    public string ThemeBlurLabel { get; private set; } = string.Empty;
    public string ThemeBackgroundTransparency { get; private set; } = string.Empty;
    public string ThemePanelContrast { get; private set; } = string.Empty;
    public string ThemeBorderVisibility { get; private set; } = string.Empty;
    public string ThemeMenuTransparency { get; private set; } = string.Empty;
    public string SettingsIgnoreTitle { get; private set; } = string.Empty;
    public string SettingsAll { get; private set; } = string.Empty;
    public string SettingsAllIgnore { get; private set; } = string.Empty;
    public string SettingsAllExtensions { get; private set; } = string.Empty;
    public string SettingsAllRootFolders { get; private set; } = string.Empty;
    public string SettingsExtensions { get; private set; } = string.Empty;
    public string SettingsRootFolders { get; private set; } = string.Empty;
    public string SettingsFont { get; private set; } = string.Empty;
    public string SettingsFontDefault { get; private set; } = string.Empty;
    public string SettingsApply { get; private set; } = string.Empty;
    public string MenuSearch { get; private set; } = string.Empty;
    public string FilterByNamePlaceholder { get; private set; } = string.Empty;
    public string FilterTooltip { get; private set; } = string.Empty;
    public string CopyFormatTooltip { get; private set; } = string.Empty;
    public string PreviewTooltip { get; private set; } = string.Empty;
    public string PreviewHideTreeTooltip { get; private set; } = string.Empty;
    public string PreviewModesLabel { get; private set; } = string.Empty;
    public string PreviewModeTree { get; private set; } = string.Empty;
    public string PreviewModeContent { get; private set; } = string.Empty;
    public string PreviewModeTreeAndContent { get; private set; } = string.Empty;
    public string PreviewModeTreeShort { get; private set; } = string.Empty;
    public string PreviewModeContentShort { get; private set; } = string.Empty;
    public string PreviewModeTreeAndContentShort { get; private set; } = string.Empty;
    public string PreviewLoadingText { get; private set; } = string.Empty;
    public string PreviewNoDataText { get; private set; } = string.Empty;
    public string PreviewSelectionCopy { get; private set; } = string.Empty;
    public string PreviewSelectionSelectAll { get; private set; } = string.Empty;
    public string PreviewSelectionClear { get; private set; } = string.Empty;
    public string PreviewCopyFilePathTooltip { get; private set; } = string.Empty;
    public string PreviewCopyCurrentModeTooltip => _selectedPreviewContentMode switch
    {
        PreviewContentMode.Tree => MenuCopyTree,
        PreviewContentMode.Content => MenuCopyContent,
        _ => MenuCopyTreeAndContent
    };

    // StatusBar labels
    public string StatusTreeLabel { get; private set; } = string.Empty;
    public string StatusContentLabel { get; private set; } = string.Empty;

    // Drop Zone localization
    public string DropZoneTitle { get; private set; } = string.Empty;
    public string DropZoneButtonText { get; private set; } = string.Empty;
    public string DropZoneHotkeyHint { get; private set; } = string.Empty;
    public string DropZoneCloneButtonText { get; private set; } = string.Empty;

    public string StatusOperationLoadingProject { get; private set; } = string.Empty;
    public string StatusOperationRefreshingProject { get; private set; } = string.Empty;
    public string StatusOperationGettingUpdates { get; private set; } = string.Empty;
    public string StatusOperationGettingUpdatesBranch { get; private set; } = string.Empty;
    public string StatusOperationSwitchingBranch { get; private set; } = string.Empty;
    public string StatusOperationCalculatingData { get; private set; } = string.Empty;
    public string StatusOperationUpdatingOptions { get; private set; } = string.Empty;
    public string StatusOperationApplyingSettings { get; private set; } = string.Empty;
    public string StatusOperationPreparingPreview { get; private set; } = string.Empty;
    public string ToastPreviewCanceled { get; private set; } = string.Empty;

    // Git menu localization
    public string MenuGitClone { get; private set; } = string.Empty;
    public string MenuGitBranch { get; private set; } = string.Empty;
    public string MenuGitGetUpdates { get; private set; } = string.Empty;

    // Git clone dialog localization
    public string GitCloneTitle { get; private set; } = string.Empty;
    public string GitCloneDescription { get; private set; } = string.Empty;
    public string GitCloneUrlPlaceholder { get; private set; } = string.Empty;
    public string GitCloneRecentRepositoriesLabel
    {
        get => _gitCloneRecentRepositoriesLabel;
        private set
        {
            if (_gitCloneRecentRepositoriesLabel == value) return;
            _gitCloneRecentRepositoriesLabel = value;
            RaisePropertyChanged();
        }
    }
    public string GitCloneProgressCheckingGit { get; private set; } = string.Empty;
    public string GitCloneProgressCloning { get; private set; } = string.Empty;
    public string GitCloneProgressDownloading { get; private set; } = string.Empty;
    public string GitCloneProgressExtracting { get; private set; } = string.Empty;
    public string GitCloneProgressPreparing { get; private set; } = string.Empty;
    public string GitCloneProgressSwitchingBranch { get; private set; } = string.Empty;

    // Git error messages
    public string GitErrorGitNotFound { get; private set; } = string.Empty;
    public string GitErrorCloneFailed { get; private set; } = string.Empty;
    public string GitErrorInvalidUrl { get; private set; } = string.Empty;
    public string GitErrorNetworkError { get; private set; } = string.Empty;
    public string GitErrorNoInternetConnection { get; private set; } = string.Empty;
    public string GitErrorBranchSwitchFailed { get; private set; } = string.Empty;
    public string GitErrorUpdateFailed { get; private set; } = string.Empty;

    // Dialog buttons
    public string DialogOK { get; private set; } = string.Empty;
    public string DialogCancel { get; private set; } = string.Empty;

    public void UpdateLocalization()
    {
        MenuFile = _localization["Menu.File"];
        MenuFileOpen = _localization["Menu.File.Open"];
        MenuFileOpenNewWindow = _localization["Menu.File.OpenNewWindow"];
        MenuFileRecent = _localization["Menu.File.Recent"];
        MenuFileRecentEmpty = _localization["Menu.File.Recent.Empty"];
        MenuFileRefresh = _localization["Menu.File.Refresh"];
        MenuFileExport = _localization["Menu.File.Export"];
        MenuFileExportTree = _localization["Menu.File.Export.Tree"];
        MenuFileExportContent = _localization["Menu.File.Export.Content"];
        MenuFileExportTreeAndContent = _localization["Menu.File.Export.TreeAndContent"];
        MenuFileExportProjectCopy = _localization["Menu.File.ExportProjectCopy"];
        MenuFileExportProjectCopyFolder = _localization["Menu.File.ExportProjectCopy.Folder"];
        MenuFileExportProjectCopyZip = _localization["Menu.File.ExportProjectCopy.Zip"];
        MenuFileExportProjectCopyFolderHelp = _localization["Menu.File.ExportProjectCopy.Folder.Help"];
        MenuFileExportProjectCopyZipHelp = _localization["Menu.File.ExportProjectCopy.Zip.Help"];
        MenuFileExit = _localization["Menu.File.Exit"];
        MenuCopy = _localization["Menu.Copy"];
        MenuCopyTree = _localization["Menu.Copy.Tree"];
        MenuCopyContent = _localization["Menu.Copy.Content"];
        MenuCopyTreeAndContent = _localization["Menu.Copy.TreeAndContent"];
        MenuView = _localization["Menu.View"];
        MenuViewExpandAll = _localization["Menu.View.ExpandAll"];
        MenuViewCollapseAll = _localization["Menu.View.CollapseAll"];
        MenuViewZoomIn = _localization["Menu.View.ZoomIn"];
        MenuViewZoomOut = _localization["Menu.View.ZoomOut"];
        MenuViewZoomReset = _localization["Menu.View.ZoomReset"];
        MenuViewTreeFont = _localization["Menu.View.TreeFont"];
        MenuViewThemeTitle = _localization["Menu.View.Theme"];
        MenuViewLightTheme = _localization["Menu.View.LightTheme"];
        MenuViewDarkTheme = _localization["Menu.View.DarkTheme"];
        MenuViewMica = _localization["Menu.View.Mica"];
        MenuViewAcrylic = _localization["Menu.View.Acrylic"];
        MenuViewCompactMode = _localization["Menu.View.CompactMode"];
        MenuViewTreeExpansionAnimation =
            _localization["Menu.View.TreeExpansionAnimation"];
        MenuOptions = _localization["Menu.Options"];
        MenuOptionsTreeSettings = _localization["Menu.Options.TreeSettings"];
        MenuLanguage = _localization["Menu.Language"];
        MenuHelp = _localization["Menu.Help"];
        MenuHelpHelp = _localization["Menu.Help.Help"];
        MenuHelpTerminalCommand = _localization["Menu.Help.TerminalCommand"];
        MenuHelpAbout = _localization["Menu.Help.About"];
        MenuHelpResetSettings = _localization["Menu.Help.ResetSettings"];
        MenuHelpResetData = _localization["Menu.Help.ResetData"];
        HelpHelpTitle = _localization["Help.Help.Title"];
        HelpHelpBody = _helpContentProvider.GetHelpBody(_localization.CurrentLanguage);
        HelpHelpCopyAll = _localization["Help.Help.CopyAll"];
        HelpAboutTitle = _localization["Help.About.Title"];
        HelpAboutBody = _localization["Help.About.Body"];
        HelpAboutSupport = _localization["Help.About.Support"];
        HelpAboutOpenLink = _localization["Help.About.OpenLink"];
        SettingsIgnoreTitle = _localization["Settings.IgnoreTitle"];
        SettingsAll = _localization["Settings.All"];
        UpdateAllCheckboxLabels();
        SettingsExtensions = _localization["Settings.Extensions"];
        SettingsRootFolders = _localization["Settings.RootFolders"];
        SettingsFont = _localization["Settings.Font"];
        SettingsFontDefault = _localization["Settings.Font.Default"];
        SettingsApply = _localization["Settings.Apply"];
        MenuSearch = _localization["Menu.Search"];
        FilterByNamePlaceholder = _localization["Filter.ByName"];
        FilterTooltip = _localization["Filter.Tooltip"];
        CopyFormatTooltip = _localization["CopyFormat.Tooltip"];
        PreviewTooltip = _localization["Preview.Tooltip"];
        PreviewHideTreeTooltip = _localization["Preview.HideTree.Tooltip"];
        PreviewModesLabel = _localization["Preview.Modes.Label"];
        PreviewModeTree = _localization["Preview.Mode.Tree"];
        PreviewModeContent = _localization["Preview.Mode.Content"];
        PreviewModeTreeAndContent = _localization["Preview.Mode.TreeAndContent"];
        PreviewModeTreeShort = _localization["Preview.Mode.Tree.Short"];
        PreviewModeContentShort = _localization["Preview.Mode.Content.Short"];
        PreviewModeTreeAndContentShort = _localization["Preview.Mode.TreeAndContent.Short"];
        PreviewLoadingText = _localization["Preview.Loading"];
        PreviewNoDataText = _localization["Preview.NoData"];
        PreviewSelectionCopy = _localization["Preview.Selection.Copy"];
        PreviewSelectionSelectAll = _localization["Preview.Selection.SelectAll"];
        PreviewSelectionClear = _localization["Preview.Selection.Clear"];
        PreviewCopyFilePathTooltip = _localization["Preview.FilePath.Copy.Tooltip"];

        // StatusBar labels
        StatusTreeLabel = _localization["Status.Tree.Label"];
        StatusContentLabel = _localization["Status.Content.Label"];

        // Drop Zone localization
        DropZoneTitle = _localization["DropZone.Title"];
        DropZoneButtonText = _localization["DropZone.Button"];
        DropZoneHotkeyHint = _localization["DropZone.HotkeyHint"];
        DropZoneCloneButtonText = _localization["DropZone.CloneButton"];

        StatusOperationLoadingProject = _localization["Status.Operation.LoadingProject"];
        StatusOperationRefreshingProject = _localization["Status.Operation.RefreshingProject"];
        StatusOperationGettingUpdates = _localization["Status.Operation.GettingUpdates"];
        StatusOperationGettingUpdatesBranch = _localization["Status.Operation.GettingUpdatesBranch"];
        StatusOperationSwitchingBranch = _localization["Status.Operation.SwitchingBranch"];
        StatusOperationCalculatingData = _localization["Status.Operation.CalculatingData"];
        StatusOperationUpdatingOptions = _localization["Status.Operation.UpdatingOptions"];
        StatusOperationApplyingSettings = _localization["Status.Operation.ApplyingSettings"];
        StatusOperationPreparingPreview = _localization["Status.Operation.PreparingPreview"];
        ToastPreviewCanceled = _localization["Toast.Operation.PreviewCanceled"];

        // Git menu localization
        MenuGitClone = _localization["Menu.Git.Clone"];
        MenuGitBranch = _localization["Menu.Git.Branch"];
        MenuGitGetUpdates = _localization["Menu.Git.GetUpdates"];

        // Git clone dialog localization
        GitCloneTitle = _localization["Git.Clone.Title"];
        GitCloneDescription = _localization["Git.Clone.Description"];
        GitCloneUrlPlaceholder = _localization["Git.Clone.UrlPlaceholder"];
        GitCloneRecentRepositoriesLabel = _localization["Git.Clone.Recent"];
        GitCloneProgressCheckingGit = _localization["Git.Clone.Progress.CheckingGit"];
        GitCloneProgressCloning = _localization["Git.Clone.Progress.Cloning"];
        GitCloneProgressDownloading = _localization["Git.Clone.Progress.Downloading"];
        GitCloneProgressExtracting = _localization["Git.Clone.Progress.Extracting"];
        GitCloneProgressPreparing = _localization["Git.Clone.Progress.Preparing"];
        GitCloneProgressSwitchingBranch = _localization["Git.Clone.Progress.SwitchingBranch"];

        // Git error messages
        GitErrorGitNotFound = _localization["Git.Error.GitNotFound"];
        GitErrorCloneFailed = _localization["Git.Error.CloneFailed"];
        GitErrorInvalidUrl = _localization["Git.Error.InvalidUrl"];
        GitErrorNetworkError = _localization["Git.Error.NetworkError"];
        GitErrorNoInternetConnection = _localization["Git.Error.NoInternetConnection"];
        GitErrorBranchSwitchFailed = _localization["Git.Error.BranchSwitchFailed"];
        GitErrorUpdateFailed = _localization["Git.Error.UpdateFailed"];

        // Dialog buttons
        DialogOK = _localization["Dialog.OK"];
        DialogCancel = _localization["Dialog.Cancel"];

        // Theme popover localization
        MenuTheme = _localization["Menu.Theme"];
        ThemeModeLabel = _localization["Theme.ModeLabel"];
        ThemeSystemLabel = _localization["Theme.System"];
        ThemeEffectsLabel = _localization["Theme.EffectsLabel"];
        ThemeLightLabel = _localization["Theme.Light"];
        ThemeDarkLabel = _localization["Theme.Dark"];
        ThemeTransparentLabel = _localization["Theme.Transparent"];
        ThemeMicaLabel = _localization["Theme.Mica"];
        ThemeBlurLabel = _localization["Theme.Blur"];
        ThemeBackgroundTransparency = _localization["Theme.BackgroundTransparency"];
        ThemePanelContrast = _localization["Theme.PanelContrast"];
        ThemeBorderVisibility = _localization["Theme.BorderVisibility"];
        ThemeMenuTransparency = _localization["Theme.MenuTransparency"];

        RaisePropertyChanged(nameof(MenuFile));
        RaisePropertyChanged(nameof(MenuFileOpen));
        RaisePropertyChanged(nameof(MenuFileOpenNewWindow));
        RaisePropertyChanged(nameof(MenuFileRecent));
        RaisePropertyChanged(nameof(MenuFileRecentEmpty));
        RaisePropertyChanged(nameof(MenuFileRefresh));
        RaisePropertyChanged(nameof(MenuFileExport));
        RaisePropertyChanged(nameof(MenuFileExportTree));
        RaisePropertyChanged(nameof(MenuFileExportContent));
        RaisePropertyChanged(nameof(MenuFileExportTreeAndContent));
        RaisePropertyChanged(nameof(MenuFileExportProjectCopy));
        RaisePropertyChanged(nameof(MenuFileExportProjectCopyFolder));
        RaisePropertyChanged(nameof(MenuFileExportProjectCopyZip));
        RaisePropertyChanged(nameof(MenuFileExportProjectCopyFolderHelp));
        RaisePropertyChanged(nameof(MenuFileExportProjectCopyZipHelp));
        RaisePropertyChanged(nameof(MenuFileExit));
        RaisePropertyChanged(nameof(MenuCopy));
        RaisePropertyChanged(nameof(MenuCopyTree));
        RaisePropertyChanged(nameof(MenuCopyContent));
        RaisePropertyChanged(nameof(MenuCopyTreeAndContent));
        RaisePropertyChanged(nameof(MenuView));
        RaisePropertyChanged(nameof(MenuViewExpandAll));
        RaisePropertyChanged(nameof(MenuViewCollapseAll));
        RaisePropertyChanged(nameof(MenuViewZoomIn));
        RaisePropertyChanged(nameof(MenuViewZoomOut));
        RaisePropertyChanged(nameof(MenuViewZoomReset));
        RaisePropertyChanged(nameof(MenuViewTreeFont));
        RaisePropertyChanged(nameof(MenuViewThemeTitle));
        RaisePropertyChanged(nameof(MenuViewLightTheme));
        RaisePropertyChanged(nameof(MenuViewDarkTheme));
        RaisePropertyChanged(nameof(MenuViewMica));
        RaisePropertyChanged(nameof(MenuViewAcrylic));
        RaisePropertyChanged(nameof(MenuViewCompactMode));
        RaisePropertyChanged(nameof(MenuViewTreeExpansionAnimation));
        RaisePropertyChanged(nameof(MenuOptions));
        RaisePropertyChanged(nameof(MenuOptionsTreeSettings));
        RaisePropertyChanged(nameof(MenuLanguage));
        RaisePropertyChanged(nameof(MenuHelp));
        RaisePropertyChanged(nameof(MenuHelpHelp));
        RaisePropertyChanged(nameof(MenuHelpTerminalCommand));
        RaisePropertyChanged(nameof(MenuHelpAbout));
        RaisePropertyChanged(nameof(MenuHelpResetSettings));
        RaisePropertyChanged(nameof(MenuHelpResetData));
        RaisePropertyChanged(nameof(HelpHelpTitle));
        RaisePropertyChanged(nameof(HelpHelpBody));
        RaisePropertyChanged(nameof(HelpHelpCopyAll));
        RaisePropertyChanged(nameof(HelpAboutTitle));
        RaisePropertyChanged(nameof(HelpAboutBody));
        RaisePropertyChanged(nameof(HelpAboutSupport));
        RaisePropertyChanged(nameof(HelpAboutOpenLink));
        RaisePropertyChanged(nameof(SettingsIgnoreTitle));
        RaisePropertyChanged(nameof(SettingsAll));
        RaisePropertyChanged(nameof(SettingsExtensions));
        RaisePropertyChanged(nameof(SettingsRootFolders));
        RaisePropertyChanged(nameof(SettingsFont));
        RaisePropertyChanged(nameof(SettingsFontDefault));
        RaisePropertyChanged(nameof(SettingsApply));
        RaisePropertyChanged(nameof(MenuSearch));
        RaisePropertyChanged(nameof(FilterByNamePlaceholder));
        RaisePropertyChanged(nameof(FilterTooltip));
        RaisePropertyChanged(nameof(CopyFormatTooltip));
        RaisePropertyChanged(nameof(PreviewTooltip));
        RaisePropertyChanged(nameof(PreviewHideTreeTooltip));
        RaisePropertyChanged(nameof(PreviewModesLabel));
        RaisePropertyChanged(nameof(PreviewModeTree));
        RaisePropertyChanged(nameof(PreviewModeContent));
        RaisePropertyChanged(nameof(PreviewModeTreeAndContent));
        RaisePropertyChanged(nameof(PreviewModeTreeShort));
        RaisePropertyChanged(nameof(PreviewModeContentShort));
        RaisePropertyChanged(nameof(PreviewModeTreeAndContentShort));
        RaisePropertyChanged(nameof(PreviewLoadingText));
        RaisePropertyChanged(nameof(PreviewNoDataText));
        RaisePropertyChanged(nameof(PreviewSelectionCopy));
        RaisePropertyChanged(nameof(PreviewSelectionSelectAll));
        RaisePropertyChanged(nameof(PreviewSelectionClear));
        RaisePropertyChanged(nameof(PreviewCopyFilePathTooltip));
        RaisePropertyChanged(nameof(PreviewCopyCurrentModeTooltip));

        // StatusBar labels
        RaisePropertyChanged(nameof(StatusTreeLabel));
        RaisePropertyChanged(nameof(StatusContentLabel));

        // Drop Zone localization
        RaisePropertyChanged(nameof(DropZoneTitle));
        RaisePropertyChanged(nameof(DropZoneButtonText));
        RaisePropertyChanged(nameof(DropZoneHotkeyHint));
        RaisePropertyChanged(nameof(DropZoneCloneButtonText));

        RaisePropertyChanged(nameof(StatusOperationLoadingProject));
        RaisePropertyChanged(nameof(StatusOperationRefreshingProject));
        RaisePropertyChanged(nameof(StatusOperationGettingUpdates));
        RaisePropertyChanged(nameof(StatusOperationGettingUpdatesBranch));
        RaisePropertyChanged(nameof(StatusOperationSwitchingBranch));
        RaisePropertyChanged(nameof(StatusOperationCalculatingData));
        RaisePropertyChanged(nameof(StatusOperationUpdatingOptions));
        RaisePropertyChanged(nameof(StatusOperationApplyingSettings));
        RaisePropertyChanged(nameof(StatusOperationPreparingPreview));
        RaisePropertyChanged(nameof(ToastPreviewCanceled));

        // Theme popover localization
        RaisePropertyChanged(nameof(MenuTheme));
        RaisePropertyChanged(nameof(ThemeModeLabel));
        RaisePropertyChanged(nameof(ThemeSystemLabel));
        RaisePropertyChanged(nameof(ThemeEffectsLabel));
        RaisePropertyChanged(nameof(ThemeLightLabel));
        RaisePropertyChanged(nameof(ThemeDarkLabel));
        RaisePropertyChanged(nameof(ThemeTransparentLabel));
        RaisePropertyChanged(nameof(ThemeMicaLabel));
        RaisePropertyChanged(nameof(ThemeBlurLabel));
        RaisePropertyChanged(nameof(ThemeBackgroundTransparency));
        RaisePropertyChanged(nameof(ThemePanelContrast));
        RaisePropertyChanged(nameof(ThemeBorderVisibility));
        RaisePropertyChanged(nameof(ThemeMenuTransparency));

        // Git localization
        RaisePropertyChanged(nameof(MenuGitClone));
        RaisePropertyChanged(nameof(MenuGitBranch));
        RaisePropertyChanged(nameof(MenuGitGetUpdates));
        RaisePropertyChanged(nameof(GitCloneTitle));
        RaisePropertyChanged(nameof(GitCloneDescription));
        RaisePropertyChanged(nameof(GitCloneUrlPlaceholder));
        RaisePropertyChanged(nameof(GitCloneRecentRepositoriesLabel));
        RaisePropertyChanged(nameof(GitCloneProgressCheckingGit));
        RaisePropertyChanged(nameof(GitCloneProgressCloning));
        RaisePropertyChanged(nameof(GitCloneProgressDownloading));
        RaisePropertyChanged(nameof(GitCloneProgressExtracting));
        RaisePropertyChanged(nameof(GitCloneProgressPreparing));
        RaisePropertyChanged(nameof(GitCloneProgressSwitchingBranch));
        RaisePropertyChanged(nameof(GitErrorGitNotFound));
        RaisePropertyChanged(nameof(GitErrorCloneFailed));
        RaisePropertyChanged(nameof(GitErrorInvalidUrl));
        RaisePropertyChanged(nameof(GitErrorNetworkError));
        RaisePropertyChanged(nameof(GitErrorNoInternetConnection));
        RaisePropertyChanged(nameof(GitErrorBranchSwitchFailed));
        RaisePropertyChanged(nameof(GitErrorUpdateFailed));
        RaisePropertyChanged(nameof(DialogOK));
        RaisePropertyChanged(nameof(DialogCancel));
    }

    public void SetToastItems(ObservableCollection<ToastMessageViewModel> items)
    {
        if (ReferenceEquals(ToastItems, items))
            return;

        ToastItems.CollectionChanged -= OnToastItemsCollectionChanged;
        ToastItems = items;
        ToastItems.CollectionChanged += OnToastItemsCollectionChanged;
        RaisePropertyChanged(nameof(ToastItems));
        RaisePropertyChanged(nameof(HasToastItems));
    }

    private void OnToastItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RaisePropertyChanged(nameof(HasToastItems));

    /// <summary>
    /// Updates the "All" checkbox labels with item counts.
    /// Shows "<localized All> (N)" if count > 0, otherwise just "<localized All>".
    /// </summary>
    public void UpdateAllCheckboxLabels()
    {
        var baseText = SettingsAll;
        if (string.IsNullOrEmpty(baseText))
            baseText = _localization["Settings.All"];

        SettingsAllIgnore = IgnoreOptions.Count > 0 ? $"{baseText} ({IgnoreOptions.Count})" : baseText;
        SettingsAllExtensions = Extensions.Count > 0 ? $"{baseText} ({Extensions.Count})" : baseText;
        SettingsAllRootFolders = RootFolders.Count > 0 ? $"{baseText} ({RootFolders.Count})" : baseText;

        RaisePropertyChanged(nameof(SettingsAllIgnore));
        RaisePropertyChanged(nameof(SettingsAllExtensions));
        RaisePropertyChanged(nameof(SettingsAllRootFolders));
        RaisePropertyChanged(nameof(HasRootFolderOptions));
    }

    /// <summary>
    /// Cleans up event subscriptions and resources to prevent memory leaks.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var applySettingsBusyDelayCts = Interlocked.Exchange(ref _applySettingsBusyDelayCts, null);
        applySettingsBusyDelayCts?.Cancel();
        applySettingsBusyDelayCts?.Dispose();

        // Unsubscribe from collection change events
        IgnoreOptions.CollectionChanged -= _ignoreOptionsChangedHandler;
        Extensions.CollectionChanged -= _extensionsChangedHandler;
        RootFolders.CollectionChanged -= _rootFoldersChangedHandler;
        RecentFolders.CollectionChanged -= _recentFoldersChangedHandler;
        RecentRepositories.CollectionChanged -= _recentRepositoriesChangedHandler;
        ToastItems.CollectionChanged -= OnToastItemsCollectionChanged;

        // Clear collections to release references
        TreeNodes.Clear();
        IgnoreOptions.Clear();
        Extensions.Clear();
        RootFolders.Clear();
        FontFamilies.Clear();
        GitBranches.Clear();
        ToastItems.Clear();

        // Clear large strings
        _previewDocument?.Dispose();
        _previewDocument = null;
        _previewText = string.Empty;
        _previewLineCount = 1;
    }
}
