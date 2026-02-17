using Avalonia;
using Avalonia.Media;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using DevProjex.Application.Services;
using DevProjex.Infrastructure.ResourceStore;
using DevProjex.Kernel.Models;

namespace DevProjex.Avalonia.ViewModels;

/// <summary>
/// Specifies the export format for copying content.
/// </summary>
public enum ExportFormat
{
    Ascii,
    Json
}

public enum PreviewContentMode
{
    Tree,
    Content,
    TreeAndContent
}

public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    public const string BaseTitle = "DevProjex v4.5";
    public const string BaseTitleWithAuthor = "DevProjex by Olimoff v4.5";
    public const double DefaultTreeFontSize = 15;
    public const double DefaultPreviewFontSize = 15;

    private readonly LocalizationService _localization;
    private readonly HelpContentProvider _helpContentProvider;

    // Event handler delegates for proper cleanup
    private readonly NotifyCollectionChangedEventHandler _ignoreOptionsChangedHandler;
    private readonly NotifyCollectionChangedEventHandler _extensionsChangedHandler;
    private readonly NotifyCollectionChangedEventHandler _rootFoldersChangedHandler;
    private bool _disposed;

    private string _title;
    private bool _isProjectLoaded;
    private bool _settingsVisible;
    private bool _searchVisible;
    private string _searchQuery = string.Empty;
    private string _nameFilter = string.Empty;

    private FontFamily? _selectedFontFamily;
    private FontFamily? _pendingFontFamily;

    private double _treeFontSize = DefaultTreeFontSize;
    private double _previewFontSize = DefaultPreviewFontSize;

    private bool _allExtensionsChecked;
    private bool _allRootFoldersChecked;
    private bool _allIgnoreChecked;
    private bool _isDarkTheme = true;
    private bool _isCompactMode;
    private bool _isTreeAnimationEnabled;
    private bool _filterVisible;
    private ExportFormat _selectedExportFormat = ExportFormat.Ascii;
    private PreviewContentMode _selectedPreviewContentMode = PreviewContentMode.Tree;
    private bool _isMicaEnabled;
    private bool _isAcrylicEnabled;
    private bool _isTransparentEnabled = true;
    private bool _isPreviewMode;
    private bool _isPreviewLoading;
    private string _previewText = string.Empty;
    private int _previewLineCount = 1;

    // Theme intensity sliders (0-100)
    // MaterialIntensity: single slider controlling overall effect (transparency, depth, material feel)
    private double _materialIntensity = 65;
    private double _blurRadius = 30;
    private double _panelContrast = 50;
    private double _borderStrength = 50;
    private double _menuChildIntensity = 50;

    private bool _themePopoverOpen;
    private bool _helpPopoverOpen;
    private bool _helpDocsPopoverOpen;

    // Git state
    private ProjectSourceType _projectSourceType = ProjectSourceType.LocalFolder;
    private string _currentBranch = string.Empty;
    private string _gitCloneUrl = string.Empty;
    private string _gitCloneStatus = string.Empty;
    private bool _gitCloneInProgress;
    private double _helpPopoverMaxWidth = 800;
    private double _helpPopoverMaxHeight = 680;
    private double _aboutPopoverMaxWidth = 520;
    private double _aboutPopoverMaxHeight = 380;
    private string _statusTreeStatsText = string.Empty;
    private string _statusContentStatsText = string.Empty;
    private string _statusOperationText = string.Empty;
    private bool _statusBusy;
    private bool _statusMetricsVisible;
    private bool _statusProgressIsIndeterminate = true;
    private double _statusProgressValue;

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

        // Subscribe to collection changes to update "All" checkbox labels with counts
        IgnoreOptions.CollectionChanged += _ignoreOptionsChangedHandler;
        Extensions.CollectionChanged += _extensionsChangedHandler;
        RootFolders.CollectionChanged += _rootFoldersChangedHandler;
        ToastItems.CollectionChanged += OnToastItemsCollectionChanged;
    }

    private ObservableCollection<TreeNodeViewModel> _treeNodes = new();

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
    public ObservableCollection<SelectionOptionViewModel> RootFolders { get; } = new();
    public ObservableCollection<SelectionOptionViewModel> Extensions { get; } = new();
    public ObservableCollection<IgnoreOptionViewModel> IgnoreOptions { get; } = new();
    public ObservableCollection<FontFamily> FontFamilies { get; } = new();

    public void ResetTreeNodes()
    {
        TreeNodes = new ObservableCollection<TreeNodeViewModel>();
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

    public bool StatusBusy
    {
        get => _statusBusy;
        set
        {
            if (_statusBusy == value) return;
            _statusBusy = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(StatusProgressVisible));
            RaisePropertyChanged(nameof(StatusProgressPercentVisible));
            // Also update IsIndeterminate since it depends on StatusBusy
            // This stops the indeterminate animation when progress bar is hidden
            RaisePropertyChanged(nameof(StatusProgressIsIndeterminate));
        }
    }

    public bool StatusProgressIsIndeterminate
    {
        // Only return true when also visible - prevents animation running when hidden
        get => _statusProgressIsIndeterminate && _statusBusy;
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

    public bool StatusProgressVisible => _statusBusy;

    public bool StatusProgressPercentVisible => _statusBusy && !_statusProgressIsIndeterminate;

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
            RaisePropertyChanged(nameof(IsSearchFilterAvailable));
        }
    }

    public bool IsPreviewMode
    {
        get => _isPreviewMode;
        set
        {
            if (_isPreviewMode == value) return;
            _isPreviewMode = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsSearchFilterAvailable));
            RaisePropertyChanged(nameof(AreFilterSettingsEnabled));
        }
    }

    public bool IsSearchFilterAvailable => _isProjectLoaded && !_isPreviewMode;

    public bool AreFilterSettingsEnabled => !_isPreviewMode;

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
        }
    }

    public string NameFilter
    {
        get => _nameFilter;
        set
        {
            if (_nameFilter == value) return;
            _nameFilter = value;
            RaisePropertyChanged();
        }
    }

    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set
        {
            if (_isDarkTheme == value) return;
            _isDarkTheme = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsLightTheme));
        }
    }

    public bool IsLightTheme => !_isDarkTheme;

    public bool IsCompactMode
    {
        get => _isCompactMode;
        set
        {
            if (_isCompactMode == value) return;
            _isCompactMode = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(TreeItemSpacing));
            RaisePropertyChanged(nameof(TreeItemPadding));
            RaisePropertyChanged(nameof(SettingsListSpacing));
        }
    }

    public bool IsTreeAnimationEnabled
    {
        get => _isTreeAnimationEnabled;
        set
        {
            if (_isTreeAnimationEnabled == value) return;
            _isTreeAnimationEnabled = value;
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

    // Computed: show transparency-related sliders (only when any effect is active)
    public bool ShowTransparencySliders => HasAnyEffect;

    // Computed: show blur slider only in Transparent mode (Mica/Acrylic have built-in blur)
    public bool ShowBlurSlider => _isTransparentEnabled;

    private void RaiseEffectPropertiesChanged()
    {
        RaisePropertyChanged(nameof(IsMicaEnabled));
        RaisePropertyChanged(nameof(IsAcrylicEnabled));
        RaisePropertyChanged(nameof(IsTransparentEnabled));
        RaisePropertyChanged(nameof(HasAnyEffect));
        RaisePropertyChanged(nameof(ShowTransparencySliders));
        RaisePropertyChanged(nameof(ShowBlurSlider));
    }

    // Methods for toggle behavior (click on active = disable)
    public void ToggleTransparent()
    {
        if (_isTransparentEnabled)
        {
            // Disable all effects
            _isTransparentEnabled = false;
        }
        else
        {
            // Enable transparent, disable others
            _isTransparentEnabled = true;
            _isMicaEnabled = false;
            _isAcrylicEnabled = false;
        }
        RaiseEffectPropertiesChanged();
    }

    public void ToggleMica()
    {
        if (_isMicaEnabled)
        {
            // Disable all effects
            _isMicaEnabled = false;
        }
        else
        {
            // Enable mica, disable others
            _isMicaEnabled = true;
            _isTransparentEnabled = false;
            _isAcrylicEnabled = false;
        }
        RaiseEffectPropertiesChanged();
    }

    public void ToggleAcrylic()
    {
        if (_isAcrylicEnabled)
        {
            // Disable all effects
            _isAcrylicEnabled = false;
        }
        else
        {
            // Enable acrylic, disable others
            _isAcrylicEnabled = true;
            _isTransparentEnabled = false;
            _isMicaEnabled = false;
        }
        RaiseEffectPropertiesChanged();
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
        }
    }

    public bool IsGitMode => _projectSourceType == ProjectSourceType.GitClone;

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

    public ObservableCollection<GitBranch> GitBranches { get; } = new();

    public string GitCloneUrl
    {
        get => _gitCloneUrl;
        set
        {
            if (_gitCloneUrl == value) return;
            _gitCloneUrl = value;
            RaisePropertyChanged();
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

    // Material intensity: single slider for overall effect strength (transparency, depth, material feel)
    public double MaterialIntensity
    {
        get => _materialIntensity;
        set
        {
            if (Math.Abs(_materialIntensity - value) < 0.1) return;
            _materialIntensity = value;
            RaisePropertyChanged();
        }
    }

    // BlurRadius: controls blur intensity in Transparent mode (0=no blur, 100=max blur ~64px)
    public double BlurRadius
    {
        get => _blurRadius;
        set
        {
            if (Math.Abs(_blurRadius - value) < 0.1) return;
            _blurRadius = value;
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

    public double BorderStrength
    {
        get => _borderStrength;
        set
        {
            if (Math.Abs(_borderStrength - value) < 0.1) return;
            _borderStrength = value;
            RaisePropertyChanged();
        }
    }

    // MenuChildIntensity: controls the effect intensity for dropdown/child menu elements
    public double MenuChildIntensity
    {
        get => _menuChildIntensity;
        set
        {
            if (Math.Abs(_menuChildIntensity - value) < 0.1) return;
            _menuChildIntensity = value;
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

    // Selected in ComboBox (same as WinForms _pendingFontName)
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

    public Thickness TreeTextMargin =>
        string.Equals(_selectedFontFamily?.Name, "Consolas", StringComparison.OrdinalIgnoreCase)
            ? new Thickness(0, 9, 0, 0)
            : new Thickness(0);

    // Tree row spacing is controlled in VM so compact mode is a single switch.
    public double TreeItemSpacing => _isCompactMode ? 2 : 6;

    // TreeViewItem padding follows the same compact flag to keep row height tight.
    // Negative vertical padding in compact mode for tighter rows.
    public Thickness TreeItemPadding => _isCompactMode ? new Thickness(0, -20) : new Thickness(4, 1);

    // Settings lists use an ItemsPanel with explicit Spacing (can go negative to tighten).
    public double SettingsListSpacing => _isCompactMode ? -5 : -3;

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
    public string MenuFileRefresh { get; private set; } = string.Empty;
    public string MenuFileExport { get; private set; } = string.Empty;
    public string MenuFileExportTree { get; private set; } = string.Empty;
    public string MenuFileExportContent { get; private set; } = string.Empty;
    public string MenuFileExportTreeAndContent { get; private set; } = string.Empty;
    public string MenuFileExit { get; private set; } = string.Empty;
    public string MenuCopy { get; private set; } = string.Empty;
    public string MenuCopyTree { get; private set; } = string.Empty;
    public string MenuCopyContent { get; private set; } = string.Empty;
    public string MenuCopyTreeAndContent { get; private set; } = string.Empty;
    public ObservableCollection<ToastMessageViewModel> ToastItems { get; private set; } = new();
    public bool HasToastItems => ToastItems.Count > 0;
    public string MenuView { get; private set; } = string.Empty;
    public string MenuViewExpandAll { get; private set; } = string.Empty;
    public string MenuViewCollapseAll { get; private set; } = string.Empty;
    public string MenuViewZoomIn { get; private set; } = string.Empty;
    public string MenuViewZoomOut { get; private set; } = string.Empty;
    public string MenuViewZoomReset { get; private set; } = string.Empty;
    public string MenuViewThemeTitle { get; private set; } = string.Empty;
    public string MenuViewLightTheme { get; private set; } = string.Empty;
    public string MenuViewDarkTheme { get; private set; } = string.Empty;
    public string MenuViewMica { get; private set; } = string.Empty;
    public string MenuViewAcrylic { get; private set; } = string.Empty;
    public string MenuViewCompactMode { get; private set; } = string.Empty;
    public string MenuViewTreeAnimation { get; private set; } = string.Empty;
    public string MenuOptions { get; private set; } = string.Empty;
    public string MenuOptionsTreeSettings { get; private set; } = string.Empty;
    public string MenuLanguage { get; private set; } = string.Empty;
    public string MenuHelp { get; private set; } = string.Empty;
    public string MenuHelpHelp { get; private set; } = string.Empty;
    public string MenuHelpAbout { get; private set; } = string.Empty;
    public string MenuHelpResetSettings { get; private set; } = string.Empty;
    public string HelpHelpTitle { get; private set; } = string.Empty;
    public string HelpHelpBody { get; private set; } = string.Empty;
    public string HelpAboutTitle { get; private set; } = string.Empty;
    public string HelpAboutBody { get; private set; } = string.Empty;
    public string HelpAboutOpenLink { get; private set; } = string.Empty;
    public string HelpAboutCopyLink { get; private set; } = string.Empty;
    public string MenuTheme { get; private set; } = string.Empty;
    public string ThemeModeLabel { get; private set; } = string.Empty;
    public string ThemeEffectsLabel { get; private set; } = string.Empty;
    public string ThemeLightLabel { get; private set; } = string.Empty;
    public string ThemeDarkLabel { get; private set; } = string.Empty;
    public string ThemeTransparentLabel { get; private set; } = string.Empty;
    public string ThemeMicaLabel { get; private set; } = string.Empty;
    public string ThemeAcrylicLabel { get; private set; } = string.Empty;
    public string ThemeMaterialIntensity { get; private set; } = string.Empty;
    public string ThemeBlurRadius { get; private set; } = string.Empty;
    public string ThemePanelContrast { get; private set; } = string.Empty;
    public string ThemeBorderStrength { get; private set; } = string.Empty;
    public string ThemeMenuChildIntensity { get; private set; } = string.Empty;
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
    public string PreviewModesLabel { get; private set; } = string.Empty;
    public string PreviewModeTree { get; private set; } = string.Empty;
    public string PreviewModeContent { get; private set; } = string.Empty;
    public string PreviewModeTreeAndContent { get; private set; } = string.Empty;
    public string PreviewLoadingText { get; private set; } = string.Empty;
    public string PreviewNoDataText { get; private set; } = string.Empty;

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
        MenuFileRefresh = _localization["Menu.File.Refresh"];
        MenuFileExport = _localization["Menu.File.Export"];
        MenuFileExportTree = _localization["Menu.File.Export.Tree"];
        MenuFileExportContent = _localization["Menu.File.Export.Content"];
        MenuFileExportTreeAndContent = _localization["Menu.File.Export.TreeAndContent"];
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
        MenuViewThemeTitle = _localization["Menu.View.Theme"];
        MenuViewLightTheme = _localization["Menu.View.LightTheme"];
        MenuViewDarkTheme = _localization["Menu.View.DarkTheme"];
        MenuViewMica = _localization["Menu.View.Mica"];
        MenuViewAcrylic = _localization["Menu.View.Acrylic"];
        MenuViewCompactMode = _localization["Menu.View.CompactMode"];
        MenuViewTreeAnimation = _localization["Menu.View.TreeAnimation"];
        MenuOptions = _localization["Menu.Options"];
        MenuOptionsTreeSettings = _localization["Menu.Options.TreeSettings"];
        MenuLanguage = _localization["Menu.Language"];
        MenuHelp = _localization["Menu.Help"];
        MenuHelpHelp = _localization["Menu.Help.Help"];
        MenuHelpAbout = _localization["Menu.Help.About"];
        MenuHelpResetSettings = _localization["Menu.Help.ResetSettings"];
        HelpHelpTitle = _localization["Help.Help.Title"];
        HelpHelpBody = _helpContentProvider.GetHelpBody(_localization.CurrentLanguage);
        HelpAboutTitle = _localization["Help.About.Title"];
        HelpAboutBody = _localization["Help.About.Body"];
        HelpAboutOpenLink = _localization["Help.About.OpenLink"];
        HelpAboutCopyLink = _localization["Help.About.CopyLink"];
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
        PreviewModesLabel = _localization["Preview.Modes.Label"];
        PreviewModeTree = _localization["Preview.Mode.Tree"];
        PreviewModeContent = _localization["Preview.Mode.Content"];
        PreviewModeTreeAndContent = _localization["Preview.Mode.TreeAndContent"];
        PreviewLoadingText = _localization["Preview.Loading"];
        PreviewNoDataText = _localization["Preview.NoData"];

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
        ThemeEffectsLabel = _localization["Theme.EffectsLabel"];
        ThemeLightLabel = _localization["Theme.Light"];
        ThemeDarkLabel = _localization["Theme.Dark"];
        ThemeTransparentLabel = _localization["Theme.Transparent"];
        ThemeMicaLabel = _localization["Theme.Mica"];
        ThemeAcrylicLabel = _localization["Theme.Acrylic"];
        ThemeMaterialIntensity = _localization["Theme.MaterialIntensity"];
        ThemeBlurRadius = _localization["Theme.BlurRadius"] + " [Beta]";
        ThemePanelContrast = _localization["Theme.PanelContrast"];
        ThemeBorderStrength = _localization["Theme.BorderStrength"];
        ThemeMenuChildIntensity = _localization["Theme.MenuChildIntensity"] + " [Beta]";

        RaisePropertyChanged(nameof(MenuFile));
        RaisePropertyChanged(nameof(MenuFileOpen));
        RaisePropertyChanged(nameof(MenuFileRefresh));
        RaisePropertyChanged(nameof(MenuFileExport));
        RaisePropertyChanged(nameof(MenuFileExportTree));
        RaisePropertyChanged(nameof(MenuFileExportContent));
        RaisePropertyChanged(nameof(MenuFileExportTreeAndContent));
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
        RaisePropertyChanged(nameof(MenuViewThemeTitle));
        RaisePropertyChanged(nameof(MenuViewLightTheme));
        RaisePropertyChanged(nameof(MenuViewDarkTheme));
        RaisePropertyChanged(nameof(MenuViewMica));
        RaisePropertyChanged(nameof(MenuViewAcrylic));
        RaisePropertyChanged(nameof(MenuViewCompactMode));
        RaisePropertyChanged(nameof(MenuViewTreeAnimation));
        RaisePropertyChanged(nameof(MenuOptions));
        RaisePropertyChanged(nameof(MenuOptionsTreeSettings));
        RaisePropertyChanged(nameof(MenuLanguage));
        RaisePropertyChanged(nameof(MenuHelp));
        RaisePropertyChanged(nameof(MenuHelpHelp));
        RaisePropertyChanged(nameof(MenuHelpAbout));
        RaisePropertyChanged(nameof(MenuHelpResetSettings));
        RaisePropertyChanged(nameof(HelpHelpTitle));
        RaisePropertyChanged(nameof(HelpHelpBody));
        RaisePropertyChanged(nameof(HelpAboutTitle));
        RaisePropertyChanged(nameof(HelpAboutBody));
        RaisePropertyChanged(nameof(HelpAboutOpenLink));
        RaisePropertyChanged(nameof(HelpAboutCopyLink));
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
        RaisePropertyChanged(nameof(PreviewModesLabel));
        RaisePropertyChanged(nameof(PreviewModeTree));
        RaisePropertyChanged(nameof(PreviewModeContent));
        RaisePropertyChanged(nameof(PreviewModeTreeAndContent));
        RaisePropertyChanged(nameof(PreviewLoadingText));
        RaisePropertyChanged(nameof(PreviewNoDataText));

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
        RaisePropertyChanged(nameof(StatusOperationPreparingPreview));
        RaisePropertyChanged(nameof(ToastPreviewCanceled));

        // Theme popover localization
        RaisePropertyChanged(nameof(MenuTheme));
        RaisePropertyChanged(nameof(ThemeModeLabel));
        RaisePropertyChanged(nameof(ThemeEffectsLabel));
        RaisePropertyChanged(nameof(ThemeLightLabel));
        RaisePropertyChanged(nameof(ThemeDarkLabel));
        RaisePropertyChanged(nameof(ThemeTransparentLabel));
        RaisePropertyChanged(nameof(ThemeMicaLabel));
        RaisePropertyChanged(nameof(ThemeAcrylicLabel));
        RaisePropertyChanged(nameof(ThemeMaterialIntensity));
        RaisePropertyChanged(nameof(ThemeBlurRadius));
        RaisePropertyChanged(nameof(ThemePanelContrast));
        RaisePropertyChanged(nameof(ThemeBorderStrength));
        RaisePropertyChanged(nameof(ThemeMenuChildIntensity));

        // Git localization
        RaisePropertyChanged(nameof(MenuGitClone));
        RaisePropertyChanged(nameof(MenuGitBranch));
        RaisePropertyChanged(nameof(MenuGitGetUpdates));
        RaisePropertyChanged(nameof(GitCloneTitle));
        RaisePropertyChanged(nameof(GitCloneDescription));
        RaisePropertyChanged(nameof(GitCloneUrlPlaceholder));
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

        // Unsubscribe from collection change events
        IgnoreOptions.CollectionChanged -= _ignoreOptionsChangedHandler;
        Extensions.CollectionChanged -= _extensionsChangedHandler;
        RootFolders.CollectionChanged -= _rootFoldersChangedHandler;
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
        _previewText = string.Empty;
        _previewLineCount = 1;
    }
}
