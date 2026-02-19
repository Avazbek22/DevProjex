using System.Collections.Frozen;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DevProjex.Application;
using DevProjex.Application.Services;
using DevProjex.Application.UseCases;
using DevProjex.Avalonia.Controls;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Avalonia.Services;
using DevProjex.Avalonia.Views;
using DevProjex.Kernel;
using DevProjex.Kernel.Abstractions;
using DevProjex.Kernel.Contracts;
using DevProjex.Kernel.Models;
using ThemePresetStore = DevProjex.Infrastructure.ThemePresets.ThemePresetStore;
using ThemePresetDb = DevProjex.Infrastructure.ThemePresets.ThemePresetDb;
using ThemePreset = DevProjex.Infrastructure.ThemePresets.ThemePreset;
using ThemePresetVariant = DevProjex.Infrastructure.ThemePresets.ThemeVariant;
using ThemePresetEffect = DevProjex.Infrastructure.ThemePresets.ThemeEffectMode;
using AppViewSettings = DevProjex.Infrastructure.ThemePresets.AppViewSettings;

namespace DevProjex.Avalonia;

public partial class MainWindow : Window
{
    private enum StatusOperationType
    {
        None = 0,
        LoadProject = 1,
        RefreshProject = 2,
        MetricsCalculation = 3,
        GitPullUpdates = 4,
        GitSwitchBranch = 5,
        PreviewBuild = 6
    }

    private sealed record SelectionOptionSnapshot(string Name, bool IsChecked);

    private sealed record IgnoreOptionSnapshot(IgnoreOptionId Id, string Label, bool IsChecked);

    private sealed record PreviewCacheKey(
        string? ProjectPath,
        int TreeIdentity,
        PreviewContentMode Mode,
        TreeTextFormat TreeFormat,
        int SelectedCount,
        int SelectedHash);

    private sealed record PreviewCacheEntry(
        PreviewCacheKey Key,
        string Text,
        int LineCount);

    private readonly record struct StatusOperationSnapshot(
        long OperationId,
        StatusOperationType OperationType,
        Action? CancelAction);

    private sealed record ProjectLoadCancellationSnapshot(
        bool HadLoadedProjectBefore,
        string? Path,
        string? ProjectDisplayName,
        string? RepositoryUrl,
        BuildTreeResult? Tree,
        ProjectSourceType ProjectSourceType,
        string CurrentBranch,
        IReadOnlyList<GitBranch> GitBranches,
        bool SettingsVisible,
        bool SearchVisible,
        bool FilterVisible,
        bool IsPreviewMode,
        bool StatusMetricsVisible,
        string StatusTreeStatsText,
        string StatusContentStatsText,
        bool AllRootFoldersChecked,
        bool AllExtensionsChecked,
        bool AllIgnoreChecked,
        bool HasCompleteMetricsBaseline,
        IReadOnlyList<SelectionOptionSnapshot> RootFolders,
        IReadOnlyList<SelectionOptionSnapshot> Extensions,
        IReadOnlyList<IgnoreOptionSnapshot> IgnoreOptions);

    public MainWindow()
        : this(CommandLineOptions.Empty, AvaloniaCompositionRoot.CreateDefault(CommandLineOptions.Empty))
    {
    }

    private readonly CommandLineOptions _startupOptions;
    private readonly LocalizationService _localization;
    private readonly ScanOptionsUseCase _scanOptions;
    private readonly BuildTreeUseCase _buildTree;
    private readonly IgnoreOptionsService _ignoreOptionsService;
    private readonly IgnoreRulesService _ignoreRulesService;
    private readonly FilterOptionSelectionService _filterSelectionService;
    private readonly TreeExportService _treeExport;
    private readonly SelectedContentExportService _contentExport;
    private readonly TreeAndContentExportService _treeAndContentExport;
    private readonly RepositoryWebPathPresentationService _repositoryWebPathPresentationService;
    private readonly TextFileExportService _textFileExport;
    private readonly IToastService _toastService;
    private readonly IconCache _iconCache;
    private readonly IElevationService _elevation;
    private readonly ThemePresetStore _themePresetStore;
    private readonly IProjectProfileStore _projectProfileStore;
    private readonly IGitRepositoryService _gitService;
    private readonly IRepoCacheService _repoCacheService;
    private readonly IZipDownloadService _zipDownloadService;
    private readonly IFileContentAnalyzer _fileContentAnalyzer;

    private readonly MainWindowViewModel _viewModel;
    private readonly TreeSearchCoordinator _searchCoordinator;
    private readonly NameFilterCoordinator _filterCoordinator;
    private readonly ThemeBrushCoordinator _themeBrushCoordinator;
    private readonly SelectionSyncCoordinator _selectionCoordinator;

    private BuildTreeResult? _currentTree;
    private string? _currentPath;
    private string? _currentProjectDisplayName;
    private string? _currentRepositoryUrl;
    private string? _cachedPathPresentationProjectPath;
    private string? _cachedPathPresentationRepositoryUrl;
    private ExportPathPresentation? _cachedPathPresentation;
    private bool _elevationAttempted;
    private bool _wasThemePopoverOpen;
    private ThemePresetDb _themePresetDb = new();
    private ThemePresetVariant _currentThemeVariant = ThemePresetVariant.Dark;
    private ThemePresetEffect _currentEffectMode = ThemePresetEffect.Transparent;

    private TreeView? _treeView;
    private TopMenuBarView? _topMenuBar;
    private SearchBarView? _searchBar;
    private FilterBarView? _filterBar;
    private ScrollViewer? _previewTextScrollViewer;
    private VirtualizedLineNumbersControl? _previewLineNumbersControl;
    private HashSet<string>? _filterExpansionSnapshot;
    private int _filterApplyVersion;
    private CancellationTokenSource? _projectOperationCts;
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _gitCloneCts;
    private CancellationTokenSource? _gitOperationCts;
    private GitCloneWindow? _gitCloneWindow;
    private string? _currentCachedRepoPath;
    private Border? _dropZoneContainer;

    // Settings panel animation
    private Border? _settingsContainer;
    private Border? _settingsIsland;
    private TranslateTransform? _settingsTransform;
    private bool _settingsAnimating;
    private const double SettingsPanelWidth = 328.0; // 320 content + 8 margin
    private static readonly TimeSpan SettingsPanelAnimationDuration = TimeSpan.FromMilliseconds(300);

    // Search bar animation
    private Border? _searchBarContainer;
    private TranslateTransform? _searchBarTransform;
    private bool _searchBarAnimating;
    private bool _searchBarClosePending;
    private const double SearchBarHeight = 46.0;
    private static readonly TimeSpan SearchBarAnimationDuration = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SearchFilterHotkeyDebounceWindow = TimeSpan.FromMilliseconds(220);
    private long _lastSearchHotkeyTimestamp;
    private long _lastFilterHotkeyTimestamp;
    private int _pendingSearchHotkeyToggle;
    private int _pendingFilterHotkeyToggle;

    // Filter bar animation
    private Border? _filterBarContainer;
    private TranslateTransform? _filterBarTransform;
    private bool _filterBarAnimating;
    private bool _filterBarClosePending;
    private const double FilterBarHeight = 46.0;
    private static readonly TimeSpan FilterBarAnimationDuration = TimeSpan.FromMilliseconds(250);

    // Preview bar animation
    private Border? _previewBarContainer;
    private Border? _previewBar;
    private TranslateTransform? _previewBarTransform;
    private Grid? _previewSegmentGrid;
    private Border? _previewSegmentThumb;
    private TranslateTransform? _previewSegmentThumbTransform;
    private Button? _previewTreeModeButton;
    private Button? _previewContentModeButton;
    private Button? _previewTreeAndContentModeButton;
    private bool _previewBarAnimating;
    private const double PreviewBarHeight = 46.0;
    private static readonly TimeSpan PreviewBarAnimationDuration = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PreviewSegmentThumbAnimationDuration = TimeSpan.FromMilliseconds(220);
    private const double PanelIslandSpacing = 4.0;

    // Preview generation
    private CancellationTokenSource? _previewBuildCts;
    private DispatcherTimer? _previewDebounceTimer;
    private int _previewBuildVersion;
    private volatile bool _previewRefreshRequested;
    private bool _previewScrollSyncActive;
    private CancellationTokenSource? _previewMemoryCleanupCts;
    private int _previewMemoryCleanupVersion;
    private PreviewCacheEntry? _previewCacheEntry;
    private CancellationTokenSource? _previewModeSwitchCts;
    private int _previewModeSwitchVersion;
    private bool _previewModeSwitchInProgress;
    private bool _clearPreviewBeforeNextRefresh;
    private bool _restoreSearchAfterPreview;
    private bool _restoreFilterAfterPreview;
    private bool _previewFontInitialized;
    private int _suppressSearchFilterRealtimeDepth;

    // Real-time metrics calculation
    private readonly object _metricsLock = new();
    private CancellationTokenSource? _metricsCalculationCts;
    private DispatcherTimer? _metricsDebounceTimer;

    private readonly Dictionary<string, FileMetricsData> _fileMetricsCache = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _isBackgroundMetricsActive;
    private int _metricsRecalcVersion;
    private const double CompactStatusMetricsThresholdWidth = 1050;
    private int _lastStatusTreeLines;
    private int _lastStatusTreeChars;
    private int _lastStatusTreeTokens;
    private int _lastStatusContentLines;
    private int _lastStatusContentChars;
    private int _lastStatusContentTokens;
    private bool _hasStatusMetricsSnapshot;
    private CancellationTokenSource? _recalculateMetricsCts;
    private long _statusOperationSequence;
    private readonly object _statusOperationLock = new();
    private long _activeStatusOperationId;
    private StatusOperationType _activeStatusOperationType;
    private Action? _activeStatusCancelAction;
    private bool _metricsCancellationRequestedByUser;
    private volatile bool _hasCompleteMetricsBaseline;
    private ProjectLoadCancellationSnapshot? _activeProjectLoadCancellationSnapshot;
    private static readonly FrozenSet<string> MetricsWarmupBinaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".svg", ".tiff", ".tif",
        ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm",
        ".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma", ".m4a",
        ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz",
        ".exe", ".dll", ".so", ".dylib", ".pdb", ".ilk",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".ttf", ".otf", ".woff", ".woff2", ".eot",
        ".bin", ".dat", ".db", ".sqlite", ".mdb"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // Event handler delegates for proper unsubscription
    private EventHandler? _languageChangedHandler;
    private EventHandler? _themeChangedHandler;
    private PropertyChangedEventHandler? _viewModelPropertyChangedHandler;

    public MainWindow(CommandLineOptions startupOptions, AvaloniaAppServices services)
    {
        _startupOptions = startupOptions;
        _localization = services.Localization;
        _scanOptions = services.ScanOptionsUseCase;
        _buildTree = services.BuildTreeUseCase;
        _ignoreOptionsService = services.IgnoreOptionsService;
        _ignoreRulesService = services.IgnoreRulesService;
        _filterSelectionService = services.FilterOptionSelectionService;
        _treeExport = services.TreeExportService;
        _contentExport = services.ContentExportService;
        _treeAndContentExport = services.TreeAndContentExportService;
        _repositoryWebPathPresentationService = services.RepositoryWebPathPresentationService;
        _textFileExport = services.TextFileExportService;
        _toastService = services.ToastService;
        _iconCache = new IconCache(services.IconStore);
        _elevation = services.Elevation;
        _themePresetStore = services.ThemePresetStore;
        _projectProfileStore = services.ProjectProfileStore;
        _gitService = services.GitRepositoryService;
        _repoCacheService = services.RepoCacheService;
        _zipDownloadService = services.ZipDownloadService;
        _fileContentAnalyzer = services.FileContentAnalyzer;

        _viewModel = new MainWindowViewModel(_localization, services.HelpContentProvider);
        _viewModel.SetToastItems(_toastService.Items);
        DataContext = _viewModel;
        SubscribeToMetricsUpdates();

        InitializeComponent();

        // Setup drag & drop for the drop zone
        _dropZoneContainer = this.FindControl<Border>("DropZoneContainer");
        if (_dropZoneContainer is not null)
        {
            _dropZoneContainer.AddHandler(DragDrop.DragEnterEvent, OnDropZoneDragEnter);
            _dropZoneContainer.AddHandler(DragDrop.DragLeaveEvent, OnDropZoneDragLeave);
            _dropZoneContainer.AddHandler(DragDrop.DropEvent, OnDropZoneDrop);
            // Start with animation class since no project is loaded initially
            _dropZoneContainer.Classes.Add("drop-zone-animating");
        }

        InitializeThemePresets();

        _viewModel.UpdateHelpPopoverMaxSize(Bounds.Size);
        PropertyChanged += OnWindowPropertyChanged;

        _treeView = this.FindControl<TreeView>("ProjectTree");
        _topMenuBar = this.FindControl<TopMenuBarView>("TopMenuBar");
        _searchBar = this.FindControl<SearchBarView>("SearchBar");
        _filterBar = this.FindControl<FilterBarView>("FilterBar");
        _previewBarContainer = this.FindControl<Border>("PreviewBarContainer");
        _previewBar = this.FindControl<Border>("PreviewBar");
        _previewSegmentGrid = this.FindControl<Grid>("PreviewSegmentGrid");
        _previewSegmentThumb = this.FindControl<Border>("PreviewSegmentThumb");
        _previewTreeModeButton = this.FindControl<Button>("PreviewTreeModeButton");
        _previewContentModeButton = this.FindControl<Button>("PreviewContentModeButton");
        _previewTreeAndContentModeButton = this.FindControl<Button>("PreviewTreeAndContentModeButton");
        _previewTextScrollViewer = this.FindControl<ScrollViewer>("PreviewTextScrollViewer");
        _previewLineNumbersControl = this.FindControl<VirtualizedLineNumbersControl>("PreviewLineNumbersControl");
        _settingsContainer = this.FindControl<Border>("SettingsContainer");
        _settingsIsland = this.FindControl<Border>("SettingsIsland");

        if (_settingsIsland is not null && _settingsContainer is not null)
        {
            _settingsTransform = new TranslateTransform();
            _settingsIsland.RenderTransform = _settingsTransform;
            // Start hidden (collapsed width, off-screen to the right)
            _settingsContainer.Width = 0;
            _settingsTransform.X = SettingsPanelWidth;
            _settingsIsland.Opacity = 0;
        }

        // Initialize search bar animation
        _searchBarContainer = this.FindControl<Border>("SearchBarContainer");
        if (_searchBarContainer is not null && _searchBar is not null)
        {
            _searchBarTransform = _searchBar.RenderTransform as TranslateTransform ?? new TranslateTransform();
            _searchBar.RenderTransform = _searchBarTransform;
            // Start hidden (collapsed height, off-screen to the top)
            _searchBarContainer.Height = 0;
            _searchBarContainer.IsVisible = false;
            _searchBarTransform.Y = -SearchBarHeight;
            _searchBar.Opacity = 0;
        }

        // Initialize filter bar animation
        _filterBarContainer = this.FindControl<Border>("FilterBarContainer");
        if (_filterBarContainer is not null && _filterBar is not null)
        {
            _filterBarTransform = _filterBar.RenderTransform as TranslateTransform ?? new TranslateTransform();
            _filterBar.RenderTransform = _filterBarTransform;
            // Start hidden (collapsed height, off-screen to the top)
            _filterBarContainer.Height = 0;
            _filterBarContainer.IsVisible = false;
            _filterBarTransform.Y = -FilterBarHeight;
            _filterBar.Opacity = 0;
        }

        if (_previewBarContainer is not null && _previewBar is not null)
        {
            _previewBarTransform = _previewBar.RenderTransform as TranslateTransform ?? new TranslateTransform();
            _previewBar.RenderTransform = _previewBarTransform;
            _previewBarContainer.Height = 0;
            _previewBarTransform.Y = -PreviewBarHeight;
            _previewBar.Opacity = 0;
        }

        if (_previewSegmentThumb is not null)
        {
            _previewSegmentThumbTransform = new TranslateTransform();
            _previewSegmentThumb.RenderTransform = _previewSegmentThumbTransform;
            EnsurePreviewSegmentThumbTransitions();
        }

        if (_treeView is not null)
        {
            _treeView.PointerEntered += OnTreePointerEntered;
        }
        AddHandler(PointerWheelChangedEvent, OnWindowPointerWheelChanged, RoutingStrategies.Tunnel, true);

        _searchCoordinator = new TreeSearchCoordinator(_viewModel, _treeView ?? throw new InvalidOperationException());
        _filterCoordinator = new NameFilterCoordinator(ApplyFilterRealtimeWithToken);
        _themeBrushCoordinator = new ThemeBrushCoordinator(this, _viewModel, () => _topMenuBar?.MainMenuControl);
        _selectionCoordinator = new SelectionSyncCoordinator(
            _viewModel,
            _scanOptions,
            _filterSelectionService,
            _ignoreOptionsService,
            BuildIgnoreRules,
            GetIgnoreOptionsAvailability,
            TryElevateAndRestart,
            () => _currentPath);

        Closed += OnWindowClosed;
        Deactivated += OnDeactivated;

        _elevationAttempted = startupOptions.ElevationAttempted;

        // Store event handlers for proper unsubscription
        _languageChangedHandler = (_, _) => ApplyLocalization();
        _localization.LanguageChanged += _languageChangedHandler;

        var app = global::Avalonia.Application.Current;
        if (app is not null)
        {
            _themeChangedHandler = OnThemeChanged;
            app.ActualThemeVariantChanged += _themeChangedHandler;
        }

        InitializeFonts();
        _selectionCoordinator.HookOptionListeners(_viewModel.RootFolders);
        _selectionCoordinator.HookOptionListeners(_viewModel.Extensions);
        _selectionCoordinator.HookIgnoreListeners(_viewModel.IgnoreOptions);

        _viewModelPropertyChangedHandler = (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.SearchQuery))
            {
                if (Volatile.Read(ref _suppressSearchFilterRealtimeDepth) > 0)
                    return;
                _searchCoordinator.OnSearchQueryChanged();
            }
            else if (args.PropertyName == nameof(MainWindowViewModel.NameFilter))
            {
                if (Volatile.Read(ref _suppressSearchFilterRealtimeDepth) > 0)
                    return;
                _filterCoordinator.OnNameFilterChanged();
            }
            else if (args.PropertyName is nameof(MainWindowViewModel.MaterialIntensity)
                     or nameof(MainWindowViewModel.PanelContrast)
                     or nameof(MainWindowViewModel.BorderStrength)
                     or nameof(MainWindowViewModel.MenuChildIntensity))
                _themeBrushCoordinator.UpdateDynamicThemeBrushes();
            else if (args.PropertyName == nameof(MainWindowViewModel.BlurRadius))
                _themeBrushCoordinator.UpdateTransparencyEffect();
            else if (args.PropertyName == nameof(MainWindowViewModel.ThemePopoverOpen))
                HandleThemePopoverStateChange();
            else if (args.PropertyName == nameof(MainWindowViewModel.IsProjectLoaded))
                UpdateDropZoneFloatAnimationState();
            else if (args.PropertyName == nameof(MainWindowViewModel.SelectedExportFormat))
            {
                RecalculateMetricsAsync(); // Update tree metrics when format changes (ASCII vs JSON)
                InvalidatePreviewCache();
                SchedulePreviewRefresh();
            }
            else if (args.PropertyName == nameof(MainWindowViewModel.SelectedPreviewContentMode))
            {
                if (!_previewModeSwitchInProgress)
                    UpdatePreviewSegmentThumbPosition(animate: false);
            }
        };
        _viewModel.PropertyChanged += _viewModelPropertyChangedHandler;
        UpdatePreviewSegmentThumbPosition(animate: false);

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        Opened += OnOpened;

        // Hook menu item submenu opening to apply brushes directly
        AddHandler(MenuItem.SubmenuOpenedEvent, _themeBrushCoordinator.HandleSubmenuOpened, RoutingStrategies.Bubble);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        // Unsubscribe from window events
        PropertyChanged -= OnWindowPropertyChanged;

        // Unsubscribe from localization service
        if (_languageChangedHandler is not null)
            _localization.LanguageChanged -= _languageChangedHandler;

        // Unsubscribe from application theme changes
        var app = global::Avalonia.Application.Current;
        if (app is not null && _themeChangedHandler is not null)
            app.ActualThemeVariantChanged -= _themeChangedHandler;

        // Unsubscribe from ViewModel
        if (_viewModelPropertyChangedHandler is not null)
            _viewModel.PropertyChanged -= _viewModelPropertyChangedHandler;

        // Unsubscribe from tree checkbox changes for metrics
        UnsubscribeFromMetricsUpdates();

        // Unsubscribe from DragDrop events
        if (_dropZoneContainer is not null)
        {
            _dropZoneContainer.RemoveHandler(DragDrop.DragEnterEvent, OnDropZoneDragEnter);
            _dropZoneContainer.RemoveHandler(DragDrop.DragLeaveEvent, OnDropZoneDragLeave);
            _dropZoneContainer.RemoveHandler(DragDrop.DropEvent, OnDropZoneDrop);
        }

        // Unsubscribe from tree pointer events
        if (_treeView is not null)
            _treeView.PointerEntered -= OnTreePointerEntered;

        // Unsubscribe from tunneled/bubbled events
        RemoveHandler(PointerWheelChangedEvent, OnWindowPointerWheelChanged);
        RemoveHandler(KeyDownEvent, OnKeyDown);
        RemoveHandler(MenuItem.SubmenuOpenedEvent, _themeBrushCoordinator.HandleSubmenuOpened);

        // Unsubscribe from window lifecycle events
        Opened -= OnOpened;
        Closed -= OnWindowClosed;
        Deactivated -= OnDeactivated;

        // Cancel metrics calculation
        _metricsCalculationCts?.Cancel();
        _metricsCalculationCts?.Dispose();
        _recalculateMetricsCts?.Cancel();
        _recalculateMetricsCts?.Dispose();
        // Properly clean up debounce timers
        if (_metricsDebounceTimer is not null)
        {
            _metricsDebounceTimer.Stop();
            _metricsDebounceTimer.Tick -= OnMetricsDebounceTimerTick;
        }

        _previewBuildCts?.Cancel();
        _previewBuildCts?.Dispose();
        if (_previewDebounceTimer is not null)
        {
            _previewDebounceTimer.Stop();
            _previewDebounceTimer.Tick -= OnPreviewDebounceTick;
        }
        _previewMemoryCleanupCts?.Cancel();
        _previewMemoryCleanupCts?.Dispose();
        _previewModeSwitchCts?.Cancel();
        _previewModeSwitchCts?.Dispose();

        // Dispose coordinators
        _searchCoordinator.Dispose();
        _filterCoordinator.Dispose();
        _selectionCoordinator.Dispose();
        _themeBrushCoordinator.Dispose();

        // Dispose ViewModel to clean up collection event handlers
        _viewModel.Dispose();

        // Cancel and dispose refresh token
        _projectOperationCts?.Cancel();
        _projectOperationCts?.Dispose();
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();

        // Cancel and dispose git clone token
        _gitCloneCts?.Cancel();
        _gitCloneCts?.Dispose();
        _gitOperationCts?.Cancel();
        _gitOperationCts?.Dispose();

        // Dispose icon cache to release bitmap resources
        _iconCache.Dispose();

        // Dispose toast service to cancel pending dismiss timers
        if (_toastService is IDisposable toastDisposable)
            toastDisposable.Dispose();

        // Clear tree references and release memory
        foreach (var node in _viewModel.TreeNodes)
            node.ClearRecursive();
        _viewModel.TreeNodes.Clear();
        _currentTree = null;
        _filterExpansionSnapshot = null;

        // Clear file metrics cache
        ClearFileMetricsCache(trimCapacity: true);

        // Clean up repository cache on exit
        _repoCacheService.ClearAllCache();

        // Dispose ZipDownloadService
        if (_zipDownloadService is IDisposable disposable)
            disposable.Dispose();
    }

    private void UpdateDropZoneFloatAnimationState()
    {
        if (_viewModel.IsProjectLoaded)
        {
            // Remove animation class to stop drop-zone animations when project is loaded.
            _dropZoneContainer?.Classes.Remove("drop-zone-animating");
            return;
        }

        // Add animation class to enable drop-zone animations.
        _dropZoneContainer?.Classes.Add("drop-zone-animating");
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        // Defer update to let theme resources settle first
        Dispatcher.UIThread.Post(
            RefreshThemeHighlightsForActiveQuery,
            DispatcherPriority.Background);
    }

    private void RefreshThemeHighlightsForActiveQuery()
    {
        // Preserve current highlight precedence: active name filter overrides search query.
        var effectiveQuery = !string.IsNullOrWhiteSpace(_viewModel.NameFilter)
            ? _viewModel.NameFilter
            : _viewModel.SearchQuery;
        _searchCoordinator.UpdateHighlights(effectiveQuery);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != BoundsProperty)
            return;

        if (e.NewValue is Rect rect)
        {
            _viewModel.UpdateHelpPopoverMaxSize(rect.Size);
            if (_hasStatusMetricsSnapshot && _viewModel.StatusMetricsVisible)
                RenderStatusBarMetrics();
            if (_viewModel.IsPreviewMode && !_previewModeSwitchInProgress)
                UpdatePreviewSegmentThumbPosition(animate: false);
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_viewModel.HelpPopoverOpen)
            _viewModel.HelpPopoverOpen = false;
        if (_viewModel.HelpDocsPopoverOpen)
            _viewModel.HelpDocsPopoverOpen = false;

        // App lost focus — ideal time to compact the heap and return pages to the OS.
        // Runs on a background thread so the deactivation handler returns instantly.
        ScheduleBackgroundMemoryCleanup();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            ApplyStartupThemePreset();

            if (!string.IsNullOrWhiteSpace(_startupOptions.Path))
                await TryOpenFolderAsync(_startupOptions.Path!, fromDialog: false);

            // Clean up stale cache from previous sessions (non-blocking background task)
            _ = Task.Run(() =>
            {
                try
                {
                    _repoCacheService.CleanupStaleCacheOnStartup();
                }
                catch
                {
                    // Best effort - ignore errors
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Cancellation is handled by status operation fallback.
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    #region Drop Zone Handlers

    private void OnDropZoneClick(object? sender, PointerPressedEventArgs e)
    {
        // Ignore if clicked on the button (button has its own handler)
        if (e.Source is Button) return;

        OnOpenFolder(sender, new RoutedEventArgs());
    }

    private void OnDropZoneDragEnter(object? sender, DragEventArgs e)
    {
        var hasFolder = e.DataTransfer.Contains(DataFormat.File);

        e.DragEffects = hasFolder ? DragDropEffects.Copy : DragDropEffects.None;

        // Add visual feedback class
        if (sender is Border border)
        {
            border.Classes.Add("drag-over");
        }
    }

    private void OnDropZoneDragLeave(object? sender, DragEventArgs e)
    {
        // Remove visual feedback class
        if (sender is Border border)
        {
            border.Classes.Remove("drag-over");
        }
    }

    private async void OnDropZoneDrop(object? sender, DragEventArgs e)
    {
        // Remove visual feedback class
        if (sender is Border border)
        {
            border.Classes.Remove("drag-over");
        }

        try
        {
            var files = e.DataTransfer.TryGetFiles();
            if (files is null) return;

            var folder = files
                .Select(f => f.TryGetLocalPath())
                .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(folder))
            {
                // Maybe it's a file - try to get its directory
                var file = files
                    .Select(f => f.TryGetLocalPath())
                    .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                    .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(file))
                {
                    folder = Path.GetDirectoryName(file);
                }
            }

            if (!string.IsNullOrWhiteSpace(folder))
            {
                await TryOpenFolderAsync(folder, fromDialog: true);
            }
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    #endregion

    private void ApplyStartupThemePreset()
    {
        var app = global::Avalonia.Application.Current;
        if (app is null) return;

        app.RequestedThemeVariant = _currentThemeVariant == ThemePresetVariant.Dark
            ? ThemeVariant.Dark
            : ThemeVariant.Light;

        _viewModel.IsDarkTheme = _currentThemeVariant == ThemePresetVariant.Dark;
        ApplyEffectMode(_currentEffectMode);
        ApplyPresetValues(_themePresetStore.GetPreset(_themePresetDb, _currentThemeVariant, _currentEffectMode));
        _themeBrushCoordinator.UpdateTransparencyEffect();
    }

    private void InitializeThemePresets()
    {
        _themePresetDb = _themePresetStore.Load();

        if (!_themePresetStore.TryParseKey(_themePresetDb.LastSelected, out var theme, out var effect))
        {
            theme = ThemePresetVariant.Dark;
            effect = ThemePresetEffect.Transparent;
        }

        _currentThemeVariant = theme;
        _currentEffectMode = effect;
        _viewModel.IsDarkTheme = theme == ThemePresetVariant.Dark;
        ApplyEffectMode(effect);
        ApplyPresetValues(_themePresetStore.GetPreset(_themePresetDb, theme, effect));
        ApplyViewSettings(_themePresetDb.ViewSettings);
        _wasThemePopoverOpen = _viewModel.ThemePopoverOpen;
    }

    private void ApplyEffectMode(ThemePresetEffect effect)
    {
        switch (effect)
        {
            case ThemePresetEffect.Mica:
                _viewModel.IsMicaEnabled = true;
                break;
            case ThemePresetEffect.Acrylic:
                _viewModel.IsAcrylicEnabled = true;
                break;
            default:
                _viewModel.IsTransparentEnabled = true;
                break;
        }
    }

    private void ApplyPresetValues(ThemePreset preset)
    {
        _viewModel.MaterialIntensity = preset.MaterialIntensity;
        _viewModel.BlurRadius = preset.BlurRadius;
        _viewModel.PanelContrast = preset.PanelContrast;
        _viewModel.MenuChildIntensity = preset.MenuChildIntensity;
        _viewModel.BorderStrength = preset.BorderStrength;
    }

    private void ApplyPresetForSelection(ThemePresetVariant theme, ThemePresetEffect effect)
    {
        _currentThemeVariant = theme;
        _currentEffectMode = effect;
        ApplyPresetValues(_themePresetStore.GetPreset(_themePresetDb, theme, effect));
    }

    private void ApplyViewSettings(AppViewSettings settings)
    {
        _viewModel.IsCompactMode = settings.IsCompactMode;
        _viewModel.IsTreeAnimationEnabled = settings.IsTreeAnimationEnabled;

        if (_viewModel.IsCompactMode)
            Classes.Add("compact-mode");
        else
            Classes.Remove("compact-mode");

        if (_viewModel.IsTreeAnimationEnabled)
            Classes.Add("tree-animation");
        else
            Classes.Remove("tree-animation");
    }

    private void HandleThemePopoverStateChange()
    {
        if (_wasThemePopoverOpen && !_viewModel.ThemePopoverOpen)
            SaveCurrentThemePreset();

        _wasThemePopoverOpen = _viewModel.ThemePopoverOpen;
    }

    private void SaveCurrentThemePreset()
    {
        var theme = GetSelectedThemeVariant();
        var effect = GetEffectModeForSave();

        _currentThemeVariant = theme;
        _currentEffectMode = effect;

        var preset = new ThemePreset
        {
            Theme = theme,
            Effect = effect,
            MaterialIntensity = _viewModel.MaterialIntensity,
            BlurRadius = _viewModel.BlurRadius,
            PanelContrast = _viewModel.PanelContrast,
            MenuChildIntensity = _viewModel.MenuChildIntensity,
            BorderStrength = _viewModel.BorderStrength
        };

        _themePresetStore.SetPreset(_themePresetDb, theme, effect, preset);
        _themePresetDb.LastSelected = $"{theme}.{effect}";
        _themePresetStore.Save(_themePresetDb);
    }

    private void SaveCurrentViewSettings()
    {
        _themePresetDb.ViewSettings = new AppViewSettings
        {
            IsCompactMode = _viewModel.IsCompactMode,
            IsTreeAnimationEnabled = _viewModel.IsTreeAnimationEnabled
        };

        _themePresetStore.Save(_themePresetDb);
    }

    private ThemePresetVariant GetSelectedThemeVariant()
        => _viewModel.IsDarkTheme ? ThemePresetVariant.Dark : ThemePresetVariant.Light;

    private ThemePresetEffect GetSelectedEffectMode()
    {
        if (_viewModel.IsMicaEnabled)
            return ThemePresetEffect.Mica;
        if (_viewModel.IsAcrylicEnabled)
            return ThemePresetEffect.Acrylic;
        return ThemePresetEffect.Transparent;
    }

    private ThemePresetEffect GetEffectModeForSave()
    {
        if (_viewModel.HasAnyEffect)
            return GetSelectedEffectMode();

        return _currentEffectMode;
    }

    private void InitializeFonts()
    {
        // Only use predefined fonts like WinForms
        var predefinedFonts = new[]
            { "Consolas", "Courier New", "Fira Code", "Lucida Console", "Cascadia Code", "JetBrains Mono" };

        var systemFonts = FontManager.Current?.SystemFonts?
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);

        _viewModel.FontFamilies.Add(FontFamily.Default);

        // Add only predefined fonts that exist on system
        foreach (var fontName in predefinedFonts)
        {
            if (systemFonts.TryGetValue(fontName, out var font))
                _viewModel.FontFamilies.Add(font);
        }

        if (_viewModel.FontFamilies.Count == 1)
        {
            foreach (var font in systemFonts.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                _viewModel.FontFamilies.Add(font);
        }

        var selected = _viewModel.FontFamilies.FirstOrDefault();
        _viewModel.SelectedFontFamily = selected;
        _viewModel.PendingFontFamily = selected;
    }

    private void SyncThemeWithSystem()
    {
        var app = global::Avalonia.Application.Current;
        if (app is null) return;

        var isDark = app.ActualThemeVariant == ThemeVariant.Dark;
        _viewModel.IsDarkTheme = isDark;
    }

    private void ApplyLocalization()
    {
        _viewModel.UpdateLocalization();
        RecalculateMetricsAsync(); // Update metrics text with new localization
        if (_viewModel.IsPreviewMode)
            SchedulePreviewRefresh(immediate: true);
        UpdateTitle();

        if (_currentPath is not null)
        {
            _ = _selectionCoordinator.PopulateIgnoreOptionsForRootSelectionAsync(
                _selectionCoordinator.GetSelectedRootFolders(),
                _currentPath);
        }
    }

    private async Task ShowErrorAsync(string message)
    {
        // Show error relative to Git Clone window if it's open, otherwise relative to main window
        var owner = _gitCloneWindow ?? (Window)this;
        await MessageDialog.ShowAsync(owner, _localization["Msg.ErrorTitle"], message);
    }

    private async Task ShowInfoAsync(string message) =>
        await MessageDialog.ShowAsync(this, _localization["Msg.InfoTitle"], message);

    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            var options = new FolderPickerOpenOptions
            {
                AllowMultiple = false,
                Title = _viewModel.MenuFileOpen
            };

            var folders = await StorageProvider.OpenFolderPickerAsync(options);
            var folder = folders.FirstOrDefault();
            var path = folder?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
                return;

            await TryOpenFolderAsync(path, fromDialog: true);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is handled by status operation fallback.
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    private async void OnRefresh(object? sender, RoutedEventArgs e)
    {
        CancelPreviewRefresh();
        var refreshCts = ReplaceCancellationSource(ref _projectOperationCts);
        var cancellationToken = refreshCts.Token;
        var statusOperationId = BeginStatusOperation(
            _viewModel.StatusOperationRefreshingProject,
            indeterminate: true,
            operationType: StatusOperationType.RefreshProject,
            cancelAction: () => refreshCts.Cancel());
        try
        {
            await ReloadProjectAsync(cancellationToken, applyStoredProfile: true);
            CompleteStatusOperation(statusOperationId);
            _toastService.Show(_localization["Toast.Refresh.Success"]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CompleteStatusOperation(statusOperationId);
            _toastService.Show(_localization["Toast.Operation.RefreshCanceled"]);
        }
        catch (Exception ex)
        {
            CompleteStatusOperation(statusOperationId);
            await ShowErrorAsync(ex.Message);
        }
        finally
        {
            DisposeIfCurrent(ref _projectOperationCts, refreshCts);
        }
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private async void OnCopyTree(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!EnsureTreeReady()) return;

            var selected = GetCheckedPaths();
            var format = GetCurrentTreeTextFormat();
            var content = BuildTreeTextForSelection(selected, format);

            await SetClipboardTextAsync(content);
            _toastService.Show(_localization["Toast.Copy.Tree"]);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    private async void OnCopyContent(object? sender, RoutedEventArgs e)
    {
        long? statusOperationId = null;
        try
        {
            if (!EnsureTreeReady()) return;

            // Cancel background metrics calculation - user wants immediate action
            CancelBackgroundMetricsCalculation();

            var selected = GetCheckedPaths();
            var files = BuildOrderedUniqueFilePaths(selected);

            if (files.Count == 0)
            {
                if (selected.Count > 0)
                    await ShowInfoAsync(_localization["Msg.NoCheckedFiles"]);
                else
                    await ShowInfoAsync(_localization["Msg.NoTextContent"]);
                return;
            }

            // Run file reading off UI thread
            statusOperationId = BeginStatusOperation("Preparing content...", indeterminate: true);
            var pathPresentation = CreateExportPathPresentation();
            var content = await Task.Run(() => _contentExport.BuildAsync(
                files,
                CancellationToken.None,
                pathPresentation?.MapFilePath));
            if (string.IsNullOrWhiteSpace(content))
            {
                CompleteStatusOperation(statusOperationId);
                await ShowInfoAsync(_localization["Msg.NoTextContent"]);
                return;
            }

            await SetClipboardTextAsync(content);
            CompleteStatusOperation(statusOperationId);
            _toastService.Show(_localization["Toast.Copy.Content"]);
        }
        catch (Exception ex)
        {
            CompleteStatusOperation(statusOperationId);
            await ShowErrorAsync(ex.Message);
        }
    }

    private async void OnCopyTreeAndContent(object? sender, RoutedEventArgs e)
    {
        long? statusOperationId = null;
        try
        {
            if (!EnsureTreeReady()) return;

            // Cancel background metrics calculation - user wants immediate action
            CancelBackgroundMetricsCalculation();

            var selected = GetCheckedPaths();
            var format = GetCurrentTreeTextFormat();
            var pathPresentation = CreateExportPathPresentation();
            // Run file reading off UI thread
            statusOperationId = BeginStatusOperation("Building export...", indeterminate: true);
            var content = await Task.Run(() =>
                _treeAndContentExport.BuildAsync(
                    _currentPath!,
                    _currentTree!.Root,
                    selected,
                    format,
                    CancellationToken.None,
                    pathPresentation));
            await SetClipboardTextAsync(content);
            CompleteStatusOperation(statusOperationId);
            _toastService.Show(_localization["Toast.Copy.TreeAndContent"]);
        }
        catch (Exception ex)
        {
            CompleteStatusOperation(statusOperationId);
            await ShowErrorAsync(ex.Message);
        }
    }

    private async void OnExportTreeToFile(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!EnsureTreeReady()) return;

            var selected = GetCheckedPaths();
            var format = GetCurrentTreeTextFormat();
            var content = BuildTreeTextForSelection(selected, format);
            var saveAsJson = format == TreeTextFormat.Json;

            var saved = await TryExportTextToFileAsync(
                content,
                BuildSuggestedExportFileName("tree", saveAsJson),
                _viewModel.MenuFileExportTree,
                useJsonDefaultExtension: saveAsJson,
                allowBothExtensions: saveAsJson);

            if (saved)
                _toastService.Show(_localization["Toast.Export.Tree"]);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    private async void OnExportContentToFile(object? sender, RoutedEventArgs e)
    {
        long? statusOperationId = null;
        try
        {
            if (!EnsureTreeReady()) return;

            CancelBackgroundMetricsCalculation();

            var selected = GetCheckedPaths();
            var files = BuildOrderedUniqueFilePaths(selected);

            if (files.Count == 0)
            {
                if (selected.Count > 0)
                    await ShowInfoAsync(_localization["Msg.NoCheckedFiles"]);
                else
                    await ShowInfoAsync(_localization["Msg.NoTextContent"]);
                return;
            }

            statusOperationId = BeginStatusOperation("Preparing content...", indeterminate: true);
            var pathPresentation = CreateExportPathPresentation();
            var content = await Task.Run(() => _contentExport.BuildAsync(
                files,
                CancellationToken.None,
                pathPresentation?.MapFilePath));
            if (string.IsNullOrWhiteSpace(content))
            {
                CompleteStatusOperation(statusOperationId);
                await ShowInfoAsync(_localization["Msg.NoTextContent"]);
                return;
            }

            var saved = await TryExportTextToFileAsync(
                content,
                BuildSuggestedExportFileName("content", saveAsJson: false),
                _viewModel.MenuFileExportContent,
                useJsonDefaultExtension: false,
                allowBothExtensions: false);

            CompleteStatusOperation(statusOperationId);
            if (saved)
                _toastService.Show(_localization["Toast.Export.Content"]);
        }
        catch (Exception ex)
        {
            CompleteStatusOperation(statusOperationId);
            await ShowErrorAsync(ex.Message);
        }
    }

    private async void OnExportTreeAndContentToFile(object? sender, RoutedEventArgs e)
    {
        long? statusOperationId = null;
        try
        {
            if (!EnsureTreeReady()) return;

            CancelBackgroundMetricsCalculation();

            var selected = GetCheckedPaths();
            var format = GetCurrentTreeTextFormat();
            var saveAsJson = false;
            var pathPresentation = CreateExportPathPresentation();

            statusOperationId = BeginStatusOperation("Building export...", indeterminate: true);
            var content = await Task.Run(() =>
                _treeAndContentExport.BuildAsync(
                    _currentPath!,
                    _currentTree!.Root,
                    selected,
                    format,
                    CancellationToken.None,
                    pathPresentation));

            var saved = await TryExportTextToFileAsync(
                content,
                BuildSuggestedExportFileName("tree_content", saveAsJson),
                _viewModel.MenuFileExportTreeAndContent,
                useJsonDefaultExtension: false,
                allowBothExtensions: false);

            CompleteStatusOperation(statusOperationId);
            if (saved)
                _toastService.Show(_localization["Toast.Export.TreeAndContent"]);
        }
        catch (Exception ex)
        {
            CompleteStatusOperation(statusOperationId);
            await ShowErrorAsync(ex.Message);
        }
    }

    private TreeTextFormat GetCurrentTreeTextFormat()
        => _viewModel.SelectedExportFormat == ExportFormat.Json
            ? TreeTextFormat.Json
            : TreeTextFormat.Ascii;

    private string BuildTreeTextForSelection(IReadOnlySet<string> selectedPaths, TreeTextFormat format)
    {
        if (_currentTree is null || string.IsNullOrWhiteSpace(_currentPath))
            return string.Empty;

        var pathPresentation = CreateExportPathPresentation();
        var displayRootPath = pathPresentation?.DisplayRootPath;
        var displayRootName = pathPresentation?.DisplayRootName;
        var hasSelection = selectedPaths.Count > 0;
        var treeText = hasSelection
            ? _treeExport.BuildSelectedTree(
                _currentPath,
                _currentTree.Root,
                selectedPaths,
                format,
                displayRootPath,
                displayRootName)
            : _treeExport.BuildFullTree(
                _currentPath,
                _currentTree.Root,
                format,
                displayRootPath,
                displayRootName);

        if (hasSelection && string.IsNullOrWhiteSpace(treeText))
            treeText = _treeExport.BuildFullTree(
                _currentPath,
                _currentTree.Root,
                format,
                displayRootPath,
                displayRootName);

        return treeText;
    }

    private ExportPathPresentation? CreateExportPathPresentation()
    {
        if (!_viewModel.IsGitMode)
        {
            _cachedPathPresentation = null;
            _cachedPathPresentationProjectPath = null;
            _cachedPathPresentationRepositoryUrl = null;
            return null;
        }

        if (string.IsNullOrWhiteSpace(_currentPath) || string.IsNullOrWhiteSpace(_currentRepositoryUrl))
        {
            _cachedPathPresentation = null;
            _cachedPathPresentationProjectPath = null;
            _cachedPathPresentationRepositoryUrl = null;
            return null;
        }

        if (_cachedPathPresentation is not null &&
            string.Equals(_cachedPathPresentationProjectPath, _currentPath, StringComparison.Ordinal) &&
            string.Equals(_cachedPathPresentationRepositoryUrl, _currentRepositoryUrl, StringComparison.Ordinal))
        {
            return _cachedPathPresentation;
        }

        _cachedPathPresentation = _repositoryWebPathPresentationService.TryCreate(_currentPath, _currentRepositoryUrl);
        _cachedPathPresentationProjectPath = _currentPath;
        _cachedPathPresentationRepositoryUrl = _currentRepositoryUrl;

        return _cachedPathPresentation;
    }

    private void SchedulePreviewRefresh(bool immediate = false)
    {
        _previewRefreshRequested = true;

        if (!_viewModel.IsProjectLoaded || !_viewModel.IsPreviewMode)
            return;

        if (immediate)
        {
            _previewDebounceTimer?.Stop();
            _ = RefreshPreviewAsync();
            return;
        }

        if (_previewDebounceTimer is null)
        {
            _previewDebounceTimer = new DispatcherTimer
            {
                // 350ms delay ensures thumb animation (250ms) completes fully before loading
                Interval = TimeSpan.FromMilliseconds(350)
            };
            _previewDebounceTimer.Tick += OnPreviewDebounceTick;
        }

        _previewDebounceTimer.Stop();
        _previewDebounceTimer.Start();
    }

    private void CancelPreviewRefresh()
    {
        _previewRefreshRequested = false;
        _previewDebounceTimer?.Stop();
        _previewBuildCts?.Cancel();
        _clearPreviewBeforeNextRefresh = false;
        _viewModel.IsPreviewLoading = false;
    }

    private void OnPreviewDebounceTick(object? sender, EventArgs e)
    {
        _previewDebounceTimer?.Stop();
        _ = RefreshPreviewAsync();
    }

    private void OnPreviewTextScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_previewScrollSyncActive)
            return;

        if (sender is not ScrollViewer textScrollViewer || _previewLineNumbersControl is null)
            return;

        _previewLineNumbersControl.ExtentHeight = Math.Max(0, textScrollViewer.Extent.Height);
        _previewLineNumbersControl.ViewportHeight = Math.Max(0, textScrollViewer.Viewport.Height);

        var targetY = textScrollViewer.Offset.Y;
        var currentY = _previewLineNumbersControl.VerticalOffset;
        if (Math.Abs(currentY - targetY) < 0.1)
            return;

        try
        {
            _previewScrollSyncActive = true;
            _previewLineNumbersControl.VerticalOffset = targetY;
        }
        finally
        {
            _previewScrollSyncActive = false;
        }
    }

    private async Task RefreshPreviewAsync()
    {
        if (!_previewRefreshRequested || !_viewModel.IsProjectLoaded || !_viewModel.IsPreviewMode)
            return;
        if (_previewModeSwitchInProgress)
            return;

        if (!EnsureTreeReady())
        {
            ApplyPreviewText(_viewModel.PreviewNoDataText);
            _previewRefreshRequested = false;
            SchedulePreviewMemoryCleanup(force: false);
            return;
        }

        var previewCts = ReplaceCancellationSource(ref _previewBuildCts);
        var cancellationToken = previewCts.Token;
        var buildVersion = Interlocked.Increment(ref _previewBuildVersion);
        _viewModel.IsPreviewLoading = true;

        // Show progress bar immediately with cancel support
        var operationId = BeginStatusOperation(
            _viewModel.StatusOperationPreparingPreview,
            indeterminate: true,
            operationType: StatusOperationType.PreviewBuild,
            cancelAction: () =>
            {
                previewCts.Cancel();
                _toastService.Show(_viewModel.ToastPreviewCanceled);
            });

        try
        {
            if (_clearPreviewBeforeNextRefresh)
            {
                // Keep old content visible during thumb animation and clear it only
                // after preview progress becomes visible for a smoother sequence.
                _viewModel.PreviewText = string.Empty;
                _viewModel.PreviewLineCount = 1;
                _clearPreviewBeforeNextRefresh = false;
            }

            // Capture state on UI thread before background work
            var selectedPaths = GetCheckedPaths();
            var selectedMode = _viewModel.SelectedPreviewContentMode;
            var treeFormat = GetCurrentTreeTextFormat();
            var hasSelection = selectedPaths.Count > 0;
            var noCheckedFilesText = _localization["Msg.NoCheckedFilesShort"];
            var noTextContentText = _localization["Msg.NoTextContent"];
            var currentPath = _currentPath;
            var currentTreeRoot = _currentTree?.Root;
            var noDataText = _viewModel.PreviewNoDataText;
            var pathPresentation = CreateExportPathPresentation();
            var previewKey = BuildPreviewCacheKey(
                projectPath: currentPath,
                treeRoot: currentTreeRoot,
                mode: selectedMode,
                treeFormat: treeFormat,
                selectedPaths: selectedPaths);

            if (TryGetCachedPreview(previewKey, out var cachedPreview))
            {
                if (buildVersion == Volatile.Read(ref _previewBuildVersion))
                {
                    ApplyPreviewText(cachedPreview.Text, cachedPreview.LineCount);
                    _previewRefreshRequested = false;
                    SchedulePreviewMemoryCleanup(
                        force: ShouldForcePreviewMemoryCleanup(cachedPreview.Text.Length, cachedPreview.LineCount));
                }

                return;
            }

            // Run all heavy work in background thread
            var (previewText, lineCount) = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                string text;
                if (selectedMode == PreviewContentMode.Tree)
                {
                    text = BuildTreeTextForSelection(selectedPaths, treeFormat);
                }
                else if (selectedMode == PreviewContentMode.Content)
                {
                    var files = CollectOrderedPreviewFiles(
                        selectedPaths: selectedPaths,
                        hasSelection: hasSelection,
                        treeRoot: currentTreeRoot);

                    if (files.Count == 0)
                        text = hasSelection ? noCheckedFilesText : noTextContentText;
                    else
                        text = _contentExport.BuildAsync(
                            files,
                            cancellationToken,
                            pathPresentation?.MapFilePath).GetAwaiter().GetResult();

                    if (string.IsNullOrWhiteSpace(text))
                        text = noTextContentText;
                }
                else
                {
                    text = currentPath != null && currentTreeRoot != null
                        ? _treeAndContentExport.BuildAsync(
                            currentPath,
                            currentTreeRoot,
                            selectedPaths,
                            treeFormat,
                            cancellationToken,
                            pathPresentation).GetAwaiter().GetResult()
                        : noTextContentText;
                }

                cancellationToken.ThrowIfCancellationRequested();

                var effectiveText = string.IsNullOrEmpty(text) ? noDataText : text;
                var lines = CountPreviewLines(effectiveText);

                return (effectiveText, lines);
            }, cancellationToken);

            if (buildVersion != Volatile.Read(ref _previewBuildVersion))
                return;

            CachePreview(previewKey, previewText, lineCount);
            ApplyPreviewText(previewText, lineCount);
            _previewRefreshRequested = false;
            SchedulePreviewMemoryCleanup(force: ShouldForcePreviewMemoryCleanup(previewText.Length, lineCount));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ignore stale preview builds.
        }
        catch (Exception ex)
        {
            if (buildVersion == Volatile.Read(ref _previewBuildVersion))
            {
                ApplyPreviewText(ex.Message);
                SchedulePreviewMemoryCleanup(force: false);
            }
        }
        finally
        {
            if (buildVersion == Volatile.Read(ref _previewBuildVersion))
                _viewModel.IsPreviewLoading = false;

            // Always hide progress bar
            CompleteStatusOperation(operationId);

            DisposeIfCurrent(ref _previewBuildCts, previewCts);
        }
    }

    private void ApplyPreviewText(string text)
    {
        var effectiveText = string.IsNullOrEmpty(text)
            ? _viewModel.PreviewNoDataText
            : text;

        ApplyPreviewText(effectiveText, CountPreviewLines(effectiveText));
    }

    private void ApplyPreviewText(string text, int lineCount)
    {
        _viewModel.PreviewText = text;
        _viewModel.PreviewLineCount = Math.Max(1, lineCount);

        // Reset both preview scroll viewers to top-left when content changes.
        if (_previewTextScrollViewer is not null)
            _previewTextScrollViewer.Offset = default;
        if (_previewLineNumbersControl is not null)
        {
            _previewLineNumbersControl.VerticalOffset = 0;
            if (_previewTextScrollViewer is not null)
            {
                _previewLineNumbersControl.ExtentHeight = Math.Max(0, _previewTextScrollViewer.Extent.Height);
                _previewLineNumbersControl.ViewportHeight = Math.Max(0, _previewTextScrollViewer.Viewport.Height);
            }
        }
    }

    private static int CountPreviewLines(string text)
    {
        var lineCount = 1;
        foreach (var ch in text)
        {
            if (ch == '\n')
                lineCount++;
        }

        return lineCount;
    }

    private static List<string> CollectOrderedPreviewFiles(
        IReadOnlySet<string> selectedPaths,
        bool hasSelection,
        TreeNodeDescriptor? treeRoot)
    {
        var uniqueFiles = new HashSet<string>(PathComparer.Default);

        if (hasSelection)
        {
            foreach (var path in selectedPaths)
            {
                if (File.Exists(path))
                    uniqueFiles.Add(path);
            }
        }
        else if (treeRoot is not null)
        {
            foreach (var path in EnumerateFilePaths(treeRoot))
                uniqueFiles.Add(path);
        }

        var files = new List<string>(uniqueFiles.Count);
        files.AddRange(uniqueFiles);
        files.Sort(PathComparer.Default);
        return files;
    }

    private static PreviewCacheKey BuildPreviewCacheKey(
        string? projectPath,
        TreeNodeDescriptor? treeRoot,
        PreviewContentMode mode,
        TreeTextFormat treeFormat,
        IReadOnlySet<string> selectedPaths)
    {
        return new PreviewCacheKey(
            ProjectPath: projectPath,
            TreeIdentity: treeRoot is null ? 0 : RuntimeHelpers.GetHashCode(treeRoot),
            Mode: mode,
            TreeFormat: treeFormat,
            SelectedCount: selectedPaths.Count,
            SelectedHash: BuildPathSetHash(selectedPaths));
    }

    private static int BuildPathSetHash(IReadOnlySet<string> selectedPaths)
    {
        if (selectedPaths.Count == 0)
            return 0;

        var ordered = new List<string>(selectedPaths.Count);
        ordered.AddRange(selectedPaths);
        ordered.Sort(PathComparer.Default);

        var hash = new HashCode();
        foreach (var path in ordered)
            hash.Add(path, StringComparer.OrdinalIgnoreCase);

        return hash.ToHashCode();
    }

    private bool TryGetCachedPreview(PreviewCacheKey key, out PreviewCacheEntry cached)
    {
        if (_previewCacheEntry is not null && _previewCacheEntry.Key == key)
        {
            cached = _previewCacheEntry;
            return true;
        }

        cached = null!;
        return false;
    }

    private void CachePreview(PreviewCacheKey key, string text, int lineCount)
    {
        _previewCacheEntry = new PreviewCacheEntry(
            Key: key,
            Text: text,
            LineCount: Math.Max(1, lineCount));
    }

    private void InvalidatePreviewCache()
    {
        _previewCacheEntry = null;
    }

    private static bool ShouldForcePreviewMemoryCleanup(int textLength, int lineCount)
    {
        const int heavyTextThreshold = 1_500_000;
        const int heavyLineThreshold = 35_000;
        return textLength >= heavyTextThreshold || lineCount >= heavyLineThreshold;
    }

    private async Task<bool> TryExportTextToFileAsync(
        string content,
        string suggestedFileName,
        string dialogTitle,
        bool useJsonDefaultExtension,
        bool allowBothExtensions)
    {
        if (StorageProvider is null || string.IsNullOrWhiteSpace(content))
            return false;

        var jsonFileType = new FilePickerFileType("JSON")
        {
            Patterns = new[] { "*.json" },
            MimeTypes = new[] { "application/json" }
        };

        var textFileType = new FilePickerFileType("Text")
        {
            Patterns = new[] { "*.txt" },
            MimeTypes = new[] { "text/plain" }
        };

        var options = new FilePickerSaveOptions
        {
            Title = dialogTitle,
            SuggestedFileName = suggestedFileName,
            ShowOverwritePrompt = true,
            // Tree export allows choosing both .json and .txt.
            // Other export modes stay text-only for predictable output format.
            DefaultExtension = useJsonDefaultExtension ? "json" : "txt",
            FileTypeChoices = allowBothExtensions
                ? new[] { jsonFileType, textFileType }
                : useJsonDefaultExtension
                    ? new[] { jsonFileType }
                    : new[] { textFileType }
        };

        var file = await StorageProvider.SaveFilePickerAsync(options);
        if (file is null)
            return false;

        await using var stream = await file.OpenWriteAsync();
        await _textFileExport.WriteAsync(stream, content);

        return true;
    }

    private string BuildSuggestedExportFileName(string suffix, bool saveAsJson)
    {
        var baseName = _currentProjectDisplayName;
        if (string.IsNullOrWhiteSpace(baseName) && !string.IsNullOrWhiteSpace(_currentPath))
            baseName = Path.GetFileName(_currentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "devprojex";

        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new StringBuilder(baseName.Length);
        foreach (var ch in baseName)
            sanitized.Append(invalidChars.Contains(ch) ? '_' : ch);

        var extension = saveAsJson ? "json" : "txt";
        return $"{sanitized}_{suffix}.{extension}";
    }

    private void OnExpandAll(object? sender, RoutedEventArgs e) => ExpandCollapseTree(expand: true);

    private void OnCollapseAll(object? sender, RoutedEventArgs e) => ExpandCollapseTree(expand: false);

    private void ExpandCollapseTree(bool expand)
    {
        if (_viewModel.IsPreviewMode)
            return;

        foreach (var node in _viewModel.TreeNodes)
        {
            node.SetExpandedRecursive(expand);
            if (!expand)
                node.IsExpanded = true;
        }
    }

    private void OnZoomIn(object? sender, RoutedEventArgs e) => AdjustZoomFontSize(1);

    private void OnZoomOut(object? sender, RoutedEventArgs e) => AdjustZoomFontSize(-1);

    private void OnZoomReset(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.IsPreviewMode)
        {
            _viewModel.PreviewFontSize = MainWindowViewModel.DefaultPreviewFontSize;
            return;
        }

        _viewModel.TreeFontSize = MainWindowViewModel.DefaultTreeFontSize;
    }

    private void AdjustZoomFontSize(double delta)
    {
        const double min = 6;
        const double max = 28;

        if (_viewModel.IsPreviewMode)
        {
            var nextPreview = Math.Clamp(_viewModel.PreviewFontSize + delta, min, max);
            _viewModel.PreviewFontSize = nextPreview;
            return;
        }

        var nextTree = Math.Clamp(_viewModel.TreeFontSize + delta, min, max);
        _viewModel.TreeFontSize = nextTree;
    }

    private void OnToggleSettings(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsProjectLoaded) return;
        if (_settingsAnimating) return;

        var newVisible = !_viewModel.SettingsVisible;
        _viewModel.SettingsVisible = newVisible;
        AnimateSettingsPanel(newVisible);
    }

    private void OnTogglePreview(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsProjectLoaded)
            return;

        if (_viewModel.IsPreviewMode)
            ClosePreviewMode();
        else
            OpenPreviewMode();
    }

    private void OnPreviewClose(object? sender, RoutedEventArgs e)
    {
        ClosePreviewMode();
    }

    private async void OnPreviewTreeModeClick(object? sender, RoutedEventArgs e)
    {
        await SwitchPreviewModeAsync(PreviewContentMode.Tree);
    }

    private async void OnPreviewContentModeClick(object? sender, RoutedEventArgs e)
    {
        await SwitchPreviewModeAsync(PreviewContentMode.Content);
    }

    private async void OnPreviewTreeAndContentModeClick(object? sender, RoutedEventArgs e)
    {
        await SwitchPreviewModeAsync(PreviewContentMode.TreeAndContent);
    }

    private async Task SwitchPreviewModeAsync(PreviewContentMode targetMode)
    {
        if (_viewModel.SelectedPreviewContentMode == targetMode)
            return;

        var switchCts = ReplaceCancellationSource(ref _previewModeSwitchCts);
        var switchVersion = Interlocked.Increment(ref _previewModeSwitchVersion);
        _previewModeSwitchInProgress = true;

        try
        {
            // Cancel any in-flight preview work so stale content cannot render.
            CancelPreviewRefresh();
            Interlocked.Increment(ref _previewBuildVersion);

            _viewModel.SelectedPreviewContentMode = targetMode;
            UpdatePreviewSegmentThumbPosition(animate: true);

            // Wait for thumb transition completion before rebuilding preview.
            await WaitForPanelAnimationAsync(PreviewSegmentThumbAnimationDuration, switchCts.Token);

            if (switchVersion != Volatile.Read(ref _previewModeSwitchVersion))
                return;

            // Mark completion before scheduling refresh to avoid a race where
            // RefreshPreviewAsync exits early while switch is still in-progress.
            _previewModeSwitchInProgress = false;
            // Clear preview only when refresh actually starts (after progress is shown).
            _clearPreviewBeforeNextRefresh = true;
            SchedulePreviewRefresh(immediate: true);
        }
        catch (OperationCanceledException)
        {
            // Ignore canceled stale switch operations.
        }
        finally
        {
            if (switchVersion == Volatile.Read(ref _previewModeSwitchVersion))
                _previewModeSwitchInProgress = false;

            DisposeIfCurrent(ref _previewModeSwitchCts, switchCts);
        }
    }

    private void EnsurePreviewSegmentThumbTransitions()
    {
        if (_previewSegmentThumbTransform is null || _previewSegmentThumbTransform.Transitions is not null)
            return;

        _previewSegmentThumbTransform.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = TranslateTransform.XProperty,
                Duration = PreviewSegmentThumbAnimationDuration,
                Easing = new CubicEaseInOut()
            }
        };
    }

    private void UpdatePreviewSegmentThumbPosition(bool animate)
    {
        if (_previewSegmentThumb is null || _previewSegmentThumbTransform is null)
            return;
        if (!TryGetPreviewSegmentTarget(out var targetX, out var targetWidth))
            return;

        _previewSegmentThumb.Width = targetWidth;

        if (!animate)
        {
            var cachedTransitions = _previewSegmentThumbTransform.Transitions;
            _previewSegmentThumbTransform.Transitions = null;
            _previewSegmentThumbTransform.X = targetX;
            _previewSegmentThumbTransform.Transitions = cachedTransitions;
            EnsurePreviewSegmentThumbTransitions();
            return;
        }

        EnsurePreviewSegmentThumbTransitions();
        _previewSegmentThumbTransform.X = targetX;
    }

    private bool TryGetPreviewSegmentTarget(out double targetX, out double targetWidth)
    {
        targetX = 0;
        targetWidth = 0;

        var selectedButton = GetSelectedPreviewModeButton();
        if (selectedButton is null)
            return false;

        targetWidth = selectedButton.Bounds.Width;
        targetX = selectedButton.Bounds.X;
        return targetWidth > 0;
    }

    private Button? GetSelectedPreviewModeButton()
    {
        return _viewModel.SelectedPreviewContentMode switch
        {
            PreviewContentMode.Tree => _previewTreeModeButton,
            PreviewContentMode.Content => _previewContentModeButton,
            _ => _previewTreeAndContentModeButton
        };
    }

    private async void OpenPreviewMode()
    {
        if (!_viewModel.IsProjectLoaded)
            return;
        if (_previewBarAnimating)
            return;

        _restoreSearchAfterPreview = _viewModel.SearchVisible;
        _restoreFilterAfterPreview = _viewModel.FilterVisible;
        if (_restoreSearchAfterPreview && _restoreFilterAfterPreview)
            _restoreFilterAfterPreview = false;
        if (!_previewFontInitialized)
        {
            _viewModel.PreviewFontSize = _viewModel.TreeFontSize;
            _previewFontInitialized = true;
        }
        ForceCloseSearchAndFilterForPreview();
        UpdatePreviewSegmentThumbPosition(animate: false);

        // Animate preview panel open and wait until transition settles.
        await AnimatePreviewBarAsync(show: true);
        UpdatePreviewSegmentThumbPosition(animate: false);

        // Switch from tree view to preview host only after panel is fully opened.
        _viewModel.IsPreviewMode = true;

        // Wait for render passes instead of hard-coded delays to keep animation
        // smooth across different refresh rates and GPU speeds.
        await WaitForPreviewRenderPassesAsync();

        // Move keyboard focus from hidden search/filter text boxes to preview surface.
        FocusPreviewSurface();

        // Start loading preview content after preview host is painted.
        SchedulePreviewRefresh(immediate: true);
    }

    private async void ClosePreviewMode()
    {
        if (_previewBarAnimating)
            return;

        _previewModeSwitchCts?.Cancel();
        _previewModeSwitchInProgress = false;
        CancelPreviewRefresh();
        await AnimatePreviewBarAsync(show: false);

        _viewModel.IsPreviewMode = false;
        RestoreSearchAndFilterAfterPreview();
        ClearPreviewMemory();
        SchedulePreviewMemoryCleanup(force: true);
        _treeView?.Focus();
    }

    private void RestoreSearchAndFilterAfterPreview()
    {
        // Restore only one tool to keep search/filter behavior predictable.
        if (_restoreSearchAfterPreview && !_viewModel.SearchVisible)
            ShowSearch(focusInput: true, selectAllOnFocus: false);
        else if (_restoreFilterAfterPreview && !_viewModel.FilterVisible)
            ShowFilter(focusInput: true, selectAllOnFocus: false);

        _restoreSearchAfterPreview = false;
        _restoreFilterAfterPreview = false;
    }

    private void ClearPreviewMemory()
    {
        _viewModel.PreviewText = string.Empty;
        _viewModel.PreviewLineCount = 1;
        InvalidatePreviewCache();
    }

    /// <summary>
    /// Aggressive memory cleanup for user-triggered operations (project switch, git ops,
    /// preview close, search/filter close, window deactivation).
    /// Compacts LOH and returns physical pages to the OS.
    /// NOTE: Call only from explicit user actions — never from background timers.
    /// </summary>
    private static void ForceMemoryCleanup()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(1, GCCollectionMode.Forced, blocking: false);
        TrimNativeWorkingSet();
    }

    /// <summary>
    /// Schedules aggressive memory cleanup on a background thread after heavy operations
    /// (project load, git branch switch, git pull, search/filter close, deactivation).
    /// The delay lets finalizers and UI thread finish releasing references before sweep.
    /// </summary>
    private static void ScheduleBackgroundMemoryCleanup()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(400);
            ForceMemoryCleanup();
        });
    }

    /// <summary>
    /// Schedules aggressive cleanup specifically for preview rendering completion.
    /// Multiple rapid requests are coalesced into one cleanup run.
    /// </summary>
    private void SchedulePreviewMemoryCleanup(bool force)
    {
        if (!force)
            return;

        var cleanupCts = ReplaceCancellationSource(ref _previewMemoryCleanupCts);
        var cleanupVersion = Interlocked.Increment(ref _previewMemoryCleanupVersion);

        _ = Task.Run(async () =>
        {
            try
            {
                // Wait for text updates to be painted before forcing collection.
                await Dispatcher.UIThread.InvokeAsync(
                    static () => { },
                    DispatcherPriority.Render);
                cleanupCts.Token.ThrowIfCancellationRequested();

                if (cleanupVersion != Volatile.Read(ref _previewMemoryCleanupVersion))
                    return;

                await Dispatcher.UIThread.InvokeAsync(
                    static () => { },
                    DispatcherPriority.Render);
                cleanupCts.Token.ThrowIfCancellationRequested();

                if (cleanupVersion != Volatile.Read(ref _previewMemoryCleanupVersion))
                    return;

                // Keep a tiny buffer so panel/tree transitions settle first.
                await Task.Delay(140, cleanupCts.Token);
                if (cleanupVersion != Volatile.Read(ref _previewMemoryCleanupVersion))
                    return;

                ForceMemoryCleanup();
            }
            catch (OperationCanceledException)
            {
                // Ignore canceled coalesced cleanup requests.
            }
            finally
            {
                DisposeIfCurrent(ref _previewMemoryCleanupCts, cleanupCts);
            }
        }, cleanupCts.Token);
    }

    /// <summary>
    /// Returns unused physical memory pages to the OS.
    /// On Windows calls SetProcessWorkingSetSize; other platforms are a no-op
    /// because their kernels reclaim pages more aggressively by default.
    /// </summary>
    private static void TrimNativeWorkingSet()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var proc = Process.GetCurrentProcess();
            SetProcessWorkingSetSize(proc.Handle, -1, -1);
        }
        catch
        {
            // Ignore — not critical, may fail in sandboxed / store environments.
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minWorkingSetSize, nint maxWorkingSetSize);

    private static async Task WaitForTreeRenderStabilizationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Wait for two render passes so the tree has time to materialize and paint.
        await Dispatcher.UIThread.InvokeAsync(
            static () => { },
            DispatcherPriority.Render);
        cancellationToken.ThrowIfCancellationRequested();

        await Dispatcher.UIThread.InvokeAsync(
            static () => { },
            DispatcherPriority.Render);
        cancellationToken.ThrowIfCancellationRequested();

        // Small buffer helps avoid visual contention with immediate post-load updates.
        await Task.Delay(140, cancellationToken);
    }

    private async void AnimateSettingsPanel(bool show)
    {
        if (_settingsIsland is null || _settingsTransform is null || _settingsContainer is null) return;
        if (_settingsAnimating) return;

        _settingsAnimating = true;
        try
        {
            EnsureSettingsPanelTransitions();
            _settingsContainer.Width = show ? SettingsPanelWidth : 0.0;
            _settingsTransform.X = show ? 0.0 : SettingsPanelWidth;
            _settingsIsland.Opacity = show ? 1.0 : 0.0;
            await WaitForPanelAnimationAsync(SettingsPanelAnimationDuration);
        }
        finally
        {
            _settingsAnimating = false;
        }
    }

    private async void AnimateSearchBar(bool show)
    {
        if (_searchBar is null || _searchBarTransform is null || _searchBarContainer is null) return;
        if (_searchBarAnimating) return;

        _searchBarAnimating = true;
        try
        {
            EnsureSearchBarTransitions();
            if (!show)
                SuppressSearchBoxAccentVisual();
            _searchBar.IsHitTestVisible = false;
            _searchBar.IsEnabled = false;
            if (show)
                _searchBarContainer.IsVisible = true;
            _searchBarContainer.Height = show ? SearchBarHeight : 0.0;
            _searchBarContainer.Margin = new Thickness(0, 0, 0, show ? PanelIslandSpacing : 0.0);
            _searchBarTransform.Y = show ? 0.0 : -SearchBarHeight;
            _searchBar.Opacity = show ? 1.0 : 0.0;
            await WaitForPanelAnimationAsync(SearchBarAnimationDuration);
            if (!show && !_viewModel.SearchVisible)
                _searchBarContainer.IsVisible = false;
            if (show && _viewModel.SearchVisible)
            {
                RestoreSearchBoxAccentVisual();
                _searchBar.IsHitTestVisible = true;
                _searchBar.IsEnabled = true;
            }
        }
        finally
        {
            _searchBarAnimating = false;

            if (_searchBarClosePending && !_viewModel.SearchVisible)
            {
                _searchBarClosePending = false;
                AnimateSearchBar(false);
            }
        }
    }

    private async Task AnimatePreviewBarAsync(bool show)
    {
        if (_previewBar is null || _previewBarTransform is null || _previewBarContainer is null) return;
        if (_previewBarAnimating) return;

        _previewBarAnimating = true;
        try
        {
            EnsurePreviewBarTransitions();
            _previewBarContainer.Height = show ? PreviewBarHeight : 0.0;
            _previewBarContainer.Margin = new Thickness(0, 0, 0, show ? PanelIslandSpacing : 0.0);
            _previewBarTransform.Y = show ? 0.0 : -PreviewBarHeight;
            _previewBar.Opacity = show ? 1.0 : 0.0;
            await WaitForPanelAnimationAsync(PreviewBarAnimationDuration);
        }
        finally
        {
            _previewBarAnimating = false;
        }
    }

    private static async Task WaitForPreviewRenderPassesAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(
            static () => { },
            DispatcherPriority.Render);

        await Dispatcher.UIThread.InvokeAsync(
            static () => { },
            DispatcherPriority.Render);
    }

    private async void AnimateFilterBar(bool show)
    {
        if (_filterBar is null || _filterBarTransform is null || _filterBarContainer is null) return;
        if (_filterBarAnimating) return;

        _filterBarAnimating = true;
        try
        {
            EnsureFilterBarTransitions();
            if (!show)
                SuppressFilterBoxAccentVisual();
            _filterBar.IsHitTestVisible = false;
            _filterBar.IsEnabled = false;
            if (show)
                _filterBarContainer.IsVisible = true;
            _filterBarContainer.Height = show ? FilterBarHeight : 0.0;
            _filterBarContainer.Margin = new Thickness(0, 0, 0, show ? PanelIslandSpacing : 0.0);
            _filterBarTransform.Y = show ? 0.0 : -FilterBarHeight;
            _filterBar.Opacity = show ? 1.0 : 0.0;
            await WaitForPanelAnimationAsync(FilterBarAnimationDuration);
            if (!show && !_viewModel.FilterVisible)
                _filterBarContainer.IsVisible = false;
            if (show && _viewModel.FilterVisible)
            {
                RestoreFilterBoxAccentVisual();
                _filterBar.IsHitTestVisible = true;
                _filterBar.IsEnabled = true;
            }
        }
        finally
        {
            _filterBarAnimating = false;

            if (_filterBarClosePending && !_viewModel.FilterVisible)
            {
                _filterBarClosePending = false;
                AnimateFilterBar(false);
            }
        }
    }

    private void EnsureSettingsPanelTransitions()
    {
        if (_settingsContainer is { } settingsContainer && settingsContainer.Transitions is null)
        {
            settingsContainer.Transitions =
            [
                new DoubleTransition
                {
                    Property = WidthProperty,
                    Duration = SettingsPanelAnimationDuration,
                    Easing = new CubicEaseOut()
                }
            ];
        }

        if (_settingsIsland is { } settingsIsland && settingsIsland.Transitions is null)
        {
            settingsIsland.Transitions =
            [
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = SettingsPanelAnimationDuration,
                    Easing = new CubicEaseOut()
                }
            ];
        }

        if (_settingsTransform is { } settingsTransform && settingsTransform.Transitions is null)
        {
            settingsTransform.Transitions =
            [
                new DoubleTransition
                {
                    Property = TranslateTransform.XProperty,
                    Duration = SettingsPanelAnimationDuration,
                    Easing = new CubicEaseOut()
                }
            ];
        }
    }

    private void EnsureSearchBarTransitions()
    {
        if (_searchBarContainer is { } searchBarContainer && searchBarContainer.Transitions is null)
        {
            searchBarContainer.Transitions =
            [
                new DoubleTransition
                {
                    Property = HeightProperty,
                    Duration = SearchBarAnimationDuration,
                    Easing = new CubicEaseOut()
                },
                new ThicknessTransition
                {
                    Property = MarginProperty,
                    Duration = SearchBarAnimationDuration,
                    Easing = new CubicEaseOut()
                }
            ];
        }

        if (_searchBar is { } searchBar && searchBar.Transitions is null)
        {
            searchBar.Transitions =
            [
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = SearchBarAnimationDuration,
                    Easing = new CubicEaseOut()
                }
            ];
        }

        if (_searchBarTransform is { } searchBarTransform && searchBarTransform.Transitions is null)
        {
            searchBarTransform.Transitions =
            [
                new DoubleTransition
                {
                    Property = TranslateTransform.YProperty,
                    Duration = SearchBarAnimationDuration,
                    Easing = new CubicEaseOut()
                }
            ];
        }
    }

    private void EnsurePreviewBarTransitions()
    {
        if (_previewBarContainer is { } previewBarContainer && previewBarContainer.Transitions is null)
        {
            previewBarContainer.Transitions =
            [
                new DoubleTransition
                {
                    Property = HeightProperty,
                    Duration = PreviewBarAnimationDuration,
                    Easing = new CubicEaseOut()
                },
                new ThicknessTransition
                {
                    Property = MarginProperty,
                    Duration = PreviewBarAnimationDuration,
                    Easing = new CubicEaseOut()
                }
            ];
        }

        if (_previewBar is { } previewBar && previewBar.Transitions is null)
        {
            previewBar.Transitions =
            [
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = PreviewBarAnimationDuration,
                    Easing = new CubicEaseOut()
                }
            ];
        }

        if (_previewBarTransform is { } previewBarTransform && previewBarTransform.Transitions is null)
        {
            previewBarTransform.Transitions =
            [
                new DoubleTransition
                {
                    Property = TranslateTransform.YProperty,
                    Duration = PreviewBarAnimationDuration,
                    Easing = new CubicEaseOut()
                }
            ];
        }
    }

    private void EnsureFilterBarTransitions()
    {
        if (_filterBarContainer is { } filterBarContainer && filterBarContainer.Transitions is null)
        {
            filterBarContainer.Transitions =
            [
                new DoubleTransition
                {
                    Property = HeightProperty,
                    Duration = FilterBarAnimationDuration,
                    Easing = new CubicEaseOut()
                },
                new ThicknessTransition
                {
                    Property = MarginProperty,
                    Duration = FilterBarAnimationDuration,
                    Easing = new CubicEaseOut()
                }
            ];
        }

        if (_filterBar is { } filterBar && filterBar.Transitions is null)
        {
            filterBar.Transitions =
            [
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = FilterBarAnimationDuration,
                    Easing = new CubicEaseOut()
                }
            ];
        }

        if (_filterBarTransform is { } filterBarTransform && filterBarTransform.Transitions is null)
        {
            filterBarTransform.Transitions =
            [
                new DoubleTransition
                {
                    Property = TranslateTransform.YProperty,
                    Duration = FilterBarAnimationDuration,
                    Easing = new CubicEaseOut()
                }
            ];
        }
    }

    private static Task WaitForPanelAnimationAsync(TimeSpan duration)
    {
        // A tiny safety buffer ensures state flags reset after the transition settles.
        return Task.Delay(duration + TimeSpan.FromMilliseconds(24));
    }

    private static Task WaitForPanelAnimationAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        // A tiny safety buffer ensures state flags reset after the transition settles.
        return Task.Delay(duration + TimeSpan.FromMilliseconds(24), cancellationToken);
    }

    private void OnSetLightTheme(object? sender, RoutedEventArgs e)
    {
        var app = global::Avalonia.Application.Current;
        if (app is null) return;

        app.RequestedThemeVariant = ThemeVariant.Light;
        _viewModel.IsDarkTheme = false;
        ApplyPresetForSelection(ThemePresetVariant.Light, GetSelectedEffectMode());
        RefreshThemeHighlightsForActiveQuery();
        _themeBrushCoordinator.UpdateDynamicThemeBrushes();
    }

    private void OnSetDarkTheme(object? sender, RoutedEventArgs e)
    {
        var app = global::Avalonia.Application.Current;
        if (app is null) return;

        app.RequestedThemeVariant = ThemeVariant.Dark;
        _viewModel.IsDarkTheme = true;
        ApplyPresetForSelection(ThemePresetVariant.Dark, GetSelectedEffectMode());
        RefreshThemeHighlightsForActiveQuery();
        _themeBrushCoordinator.UpdateDynamicThemeBrushes();
    }

    private void OnToggleMica(object? sender, RoutedEventArgs e)
    {
        _viewModel.IsMicaEnabled = !_viewModel.IsMicaEnabled;
        _themeBrushCoordinator.UpdateTransparencyEffect();
    }

    private void OnToggleAcrylic(object? sender, RoutedEventArgs e)
    {
        _viewModel.IsAcrylicEnabled = !_viewModel.IsAcrylicEnabled;
        _themeBrushCoordinator.UpdateTransparencyEffect();
    }

    private void OnToggleCompactMode(object? sender, RoutedEventArgs e)
    {
        _viewModel.IsCompactMode = !_viewModel.IsCompactMode;

        if (_viewModel.IsCompactMode)
            Classes.Add("compact-mode");
        else
            Classes.Remove("compact-mode");

        SaveCurrentViewSettings();
    }

    private void OnToggleTreeAnimation(object? sender, RoutedEventArgs e)
    {
        _viewModel.IsTreeAnimationEnabled = !_viewModel.IsTreeAnimationEnabled;

        if (_viewModel.IsTreeAnimationEnabled)
            Classes.Add("tree-animation");
        else
            Classes.Remove("tree-animation");

        SaveCurrentViewSettings();
    }

    private void OnThemeMenuClick(object? sender, RoutedEventArgs e)
    {
        _viewModel.ThemePopoverOpen = !_viewModel.ThemePopoverOpen;
        e.Handled = true;
    }

    private void OnSetLightThemeCheckbox(object? sender, RoutedEventArgs e)
    {
        // Always set light theme when clicked (even if already light - just refresh)
        OnSetLightTheme(sender, e);
        e.Handled = true;
    }

    private void OnSetDarkThemeCheckbox(object? sender, RoutedEventArgs e)
    {
        // Always set dark theme when clicked
        OnSetDarkTheme(sender, e);
        e.Handled = true;
    }

    private void OnSetTransparentMode(object? sender, RoutedEventArgs e)
    {
        _viewModel.ToggleTransparent();
        _themeBrushCoordinator.UpdateTransparencyEffect();
        if (_viewModel.IsTransparentEnabled)
            ApplyPresetForSelection(GetSelectedThemeVariant(), ThemePresetEffect.Transparent);
        e.Handled = true;
    }

    private void OnSetMicaMode(object? sender, RoutedEventArgs e)
    {
        _viewModel.ToggleMica();
        _themeBrushCoordinator.UpdateTransparencyEffect();
        if (_viewModel.IsMicaEnabled)
            ApplyPresetForSelection(GetSelectedThemeVariant(), ThemePresetEffect.Mica);
        e.Handled = true;
    }

    private void OnSetAcrylicMode(object? sender, RoutedEventArgs e)
    {
        _viewModel.ToggleAcrylic();
        _themeBrushCoordinator.UpdateTransparencyEffect();
        if (_viewModel.IsAcrylicEnabled)
            ApplyPresetForSelection(GetSelectedThemeVariant(), ThemePresetEffect.Acrylic);
        e.Handled = true;
    }


    private void OnLangRu(object? sender, RoutedEventArgs e) => _localization.SetLanguage(AppLanguage.Ru);
    private void OnLangEn(object? sender, RoutedEventArgs e) => _localization.SetLanguage(AppLanguage.En);
    private void OnLangUz(object? sender, RoutedEventArgs e) => _localization.SetLanguage(AppLanguage.Uz);
    private void OnLangTg(object? sender, RoutedEventArgs e) => _localization.SetLanguage(AppLanguage.Tg);
    private void OnLangKk(object? sender, RoutedEventArgs e) => _localization.SetLanguage(AppLanguage.Kk);
    private void OnLangFr(object? sender, RoutedEventArgs e) => _localization.SetLanguage(AppLanguage.Fr);
    private void OnLangDe(object? sender, RoutedEventArgs e) => _localization.SetLanguage(AppLanguage.De);
    private void OnLangIt(object? sender, RoutedEventArgs e) => _localization.SetLanguage(AppLanguage.It);

    private void OnAbout(object? sender, RoutedEventArgs e)
    {
        _viewModel.HelpPopoverOpen = true;
        _viewModel.HelpDocsPopoverOpen = false;
        _viewModel.ThemePopoverOpen = false;
        e.Handled = true;
    }

    private void OnAboutClose(object? sender, RoutedEventArgs e)
    {
        _viewModel.HelpPopoverOpen = false;
        e.Handled = true;
    }

    private void OnHelp(object? sender, RoutedEventArgs e)
    {
        _viewModel.HelpDocsPopoverOpen = true;
        _viewModel.HelpPopoverOpen = false;
        _viewModel.ThemePopoverOpen = false;
        e.Handled = true;
    }

    private void OnHelpClose(object? sender, RoutedEventArgs e)
    {
        _viewModel.HelpDocsPopoverOpen = false;
        e.Handled = true;
    }

    private void OnResetSettings(object? sender, RoutedEventArgs e)
    {
        ResetThemeSettings();
        _toastService.Show(_localization["Toast.Settings.Reset"]);
        e.Handled = true;
    }

    private void OnResetData(object? sender, RoutedEventArgs e)
    {
        _projectProfileStore.ClearAllProfiles();
        _toastService.Show(_localization["Toast.Data.Reset"]);
        e.Handled = true;
    }

    /// <summary>
    /// Resets all theme presets to factory defaults and reapplies current selection.
    /// </summary>
    private void ResetThemeSettings()
    {
        _themePresetDb = _themePresetStore.ResetToDefaults();

        // Reparse last selected to get current theme variant and effect
        if (!_themePresetStore.TryParseKey(_themePresetDb.LastSelected, out var theme, out var effect))
        {
            theme = ThemePresetVariant.Dark;
            effect = ThemePresetEffect.Transparent;
        }

        _currentThemeVariant = theme;
        _currentEffectMode = effect;

        // Apply default preset values to ViewModel
        ApplyPresetValues(_themePresetStore.GetPreset(_themePresetDb, theme, effect));
        ApplyViewSettings(_themePresetDb.ViewSettings);

        // Refresh visual effects
        _themeBrushCoordinator.UpdateTransparencyEffect();
        _themeBrushCoordinator.UpdateDynamicThemeBrushes();
    }

    #region Git Operations

    private void OnGitClone(object? sender, RoutedEventArgs e)
    {
        _viewModel.GitCloneUrl = string.Empty;
        _viewModel.GitCloneStatus = string.Empty;
        _viewModel.GitCloneInProgress = false;

        // Create and show Git Clone window
        _gitCloneWindow = new GitCloneWindow
        {
            DataContext = _viewModel
        };

        _gitCloneWindow.StartCloneRequested += OnGitCloneStart;
        _gitCloneWindow.CancelRequested += OnGitCloneCancel;

        _gitCloneWindow.ShowDialog(this);
        e.Handled = true;
    }

    private void OnGitCloneClose(object? sender, RoutedEventArgs e)
    {
        CancelGitCloneOperation();
        _gitCloneWindow?.Close();
        _gitCloneWindow = null;
        e.Handled = true;
    }

    private async void OnGitCloneStart(object? sender, RoutedEventArgs e)
    {
        var url = _viewModel.GitCloneUrl?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            await ShowErrorAsync(_viewModel.GitErrorInvalidUrl);
            return;
        }

        // Validate URL format before attempting to clone
        if (!IsValidGitRepositoryUrl(url))
        {
            await ShowErrorAsync(_viewModel.GitErrorInvalidUrl);
            return;
        }

        var gitCloneCts = ReplaceCancellationSource(ref _gitCloneCts);
        var cancellationToken = gitCloneCts.Token;

        _viewModel.GitCloneInProgress = true;
        _viewModel.GitCloneStatus = _viewModel.GitCloneProgressCheckingGit;

        string? targetPath = null;

        try
        {
            // Check internet connection before starting
            var hasInternet = await CheckInternetConnectionAsync(cancellationToken);
            if (!hasInternet)
            {
                _viewModel.GitCloneInProgress = false;
                _gitCloneWindow?.Close();
                _gitCloneWindow = null;
                await ShowErrorAsync(_viewModel.GitErrorNoInternetConnection);
                return;
            }

            // Clean up previous cached repository before cloning a new one
            if (_currentCachedRepoPath is not null)
            {
                _repoCacheService.DeleteRepositoryDirectory(_currentCachedRepoPath);
                _currentCachedRepoPath = null;
            }

            targetPath = _repoCacheService.CreateRepositoryDirectory(url);

            // Track current operation for progress reporting
            string currentOperation = string.Empty;

            var progress = new Progress<string>(status =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    // Handle phase transition markers
                    if (status == "::EXTRACTING::")
                    {
                        currentOperation = _viewModel.GitCloneProgressExtracting;
                        _viewModel.GitCloneStatus = currentOperation;
                        return;
                    }

                    // Keep localized phase labels and append numeric progress only.
                    // Raw git stderr lines (e.g. "Cloning into ...") are not shown in UI.
                    if (status.EndsWith('%') && status.Length <= 4 && !string.IsNullOrEmpty(currentOperation))
                    {
                        _viewModel.GitCloneStatus = $"{currentOperation} {status}";
                    }
                    else if (!string.IsNullOrEmpty(currentOperation))
                    {
                        _viewModel.GitCloneStatus = currentOperation;
                    }
                });
            });

            GitCloneResult result;

            // Check if Git is available
            var gitAvailable = await _gitService.IsGitAvailableAsync(cancellationToken);

            if (gitAvailable)
            {
                currentOperation = _viewModel.GitCloneProgressCloning;
                _viewModel.GitCloneStatus = currentOperation;
                result = await _gitService.CloneAsync(url, targetPath, progress, cancellationToken);
            }
            else
            {
                // Fallback to ZIP download
                _viewModel.GitCloneStatus = _viewModel.GitErrorGitNotFound;
                await Task.Delay(1500, cancellationToken);

                currentOperation = _viewModel.GitCloneProgressDownloading;
                _viewModel.GitCloneStatus = currentOperation;
                result = await _zipDownloadService.DownloadAndExtractAsync(url, targetPath, progress, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!result.Success)
            {
                _repoCacheService.DeleteRepositoryDirectory(targetPath);
                _gitCloneWindow?.Close();
                _gitCloneWindow = null;
                _viewModel.GitCloneInProgress = false;
                await ShowErrorAsync(_localization.Format("Git.Error.CloneFailed", result.ErrorMessage ?? "Unknown error"));
                _toastService.Show(_localization["Toast.Git.CloneError"]);
                return;
            }

            // Successfully cloned - open the project
            _gitCloneWindow?.Close();
            _gitCloneWindow = null;
            _viewModel.GitCloneInProgress = false;
            _viewModel.ProjectSourceType = result.SourceType;
            _viewModel.CurrentBranch = result.DefaultBranch ?? "main";

            // Save repository name and URL for display
            _currentProjectDisplayName = result.RepositoryName;
            _currentRepositoryUrl = result.RepositoryUrl;

            // Save cache path for cleanup when project is closed or replaced
            _currentCachedRepoPath = targetPath;

            await TryOpenFolderAsync(result.LocalPath, fromDialog: false);

            // Load branches if Git mode
            if (result.SourceType == ProjectSourceType.GitClone)
                await RefreshGitBranchesAsync(result.LocalPath);

            if (_currentPath == result.LocalPath)
            {
                _toastService.Show(_localization["Toast.Git.CloneSuccess"]);
            }
        }
        catch (OperationCanceledException)
        {
            if (targetPath is not null)
            {
                // Use default cancellation token since operation was cancelled
                _repoCacheService.DeleteRepositoryDirectory(targetPath);
            }
        }
        catch (Exception ex)
        {
            if (targetPath is not null)
            {
                _repoCacheService.DeleteRepositoryDirectory(targetPath);
            }

            _gitCloneWindow?.Close();
            _gitCloneWindow = null;
            await ShowErrorAsync(_localization.Format("Git.Error.CloneFailed", ex.Message));
            _toastService.Show(_localization["Toast.Git.CloneError"]);
        }
        finally
        {
            _viewModel.GitCloneInProgress = false;
            DisposeIfCurrent(ref _gitCloneCts, gitCloneCts);
        }

        e.Handled = true;
    }

    private void OnGitCloneCancel(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.GitCloneInProgress)
        {
            CancelGitCloneOperation();
        }
        else
        {
            _gitCloneWindow?.Close();
            _gitCloneWindow = null;
        }
        e.Handled = true;
    }

    private void CancelGitCloneOperation()
    {
        _gitCloneCts?.Cancel();
        _viewModel.GitCloneInProgress = false;
    }

    private async void OnGitGetUpdates(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsGitMode || string.IsNullOrEmpty(_currentPath))
            return;

        var gitCts = ReplaceCancellationSource(ref _gitOperationCts);
        var cancellationToken = gitCts.Token;
        long? statusOperationId = null;
        try
        {
            var statusText = string.IsNullOrWhiteSpace(_viewModel.CurrentBranch)
                ? _viewModel.StatusOperationGettingUpdates
                : _localization.Format("Status.Operation.GettingUpdatesBranch", _viewModel.CurrentBranch);
            statusOperationId = BeginStatusOperation(
                statusText,
                indeterminate: true,
                operationType: StatusOperationType.GitPullUpdates,
                cancelAction: () => gitCts.Cancel());

            var progress = new Progress<string>(status =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (TryParseTrailingPercent(status, out var percent))
                        UpdateStatusOperationProgress(percent, statusText, statusOperationId);
                    else
                        UpdateStatusOperationText(statusText, statusOperationId);
                });
            });
            var beforeHash = await _gitService.GetHeadCommitAsync(_currentPath, cancellationToken);
            var success = await _gitService.PullUpdatesAsync(_currentPath, progress, cancellationToken);

            if (!success)
            {
                CompleteStatusOperation(statusOperationId);
                await ShowErrorAsync(_localization.Format("Git.Error.UpdateFailed", "Pull failed"));
                return;
            }

            // Refresh branches and tree
            await RefreshGitBranchesAsync(_currentPath, cancellationToken);
            await ReloadProjectAsync(cancellationToken);

            var afterHash = await _gitService.GetHeadCommitAsync(_currentPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(beforeHash) && !string.IsNullOrWhiteSpace(afterHash) && beforeHash == afterHash)
            {
                _toastService.Show(_localization["Toast.Git.NoUpdates"]);
                CompleteStatusOperation(statusOperationId);
            }
            else
            {
                _toastService.Show(_localization["Toast.Git.UpdatesApplied"]);
                CompleteStatusOperation(statusOperationId);
                // Clean up memory from old tree after successful update
                ScheduleBackgroundMemoryCleanup();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CompleteStatusOperation(statusOperationId);
            _toastService.Show(_localization["Toast.Operation.GitCanceled"]);
        }
        catch (Exception ex)
        {
            CompleteStatusOperation(statusOperationId);
            await ShowErrorAsync(_localization.Format("Git.Error.UpdateFailed", ex.Message));
        }
        finally
        {
            DisposeIfCurrent(ref _gitOperationCts, gitCts);
        }

        e.Handled = true;
    }

    private async void OnGitBranchSwitch(object? sender, string branchName)
    {
        if (!_viewModel.IsGitMode || string.IsNullOrEmpty(_currentPath))
            return;

        var gitCts = ReplaceCancellationSource(ref _gitOperationCts);
        var cancellationToken = gitCts.Token;
        long? statusOperationId = null;
        try
        {
            var statusText = _localization.Format("Status.Operation.SwitchingBranch", branchName);
            statusOperationId = BeginStatusOperation(
                statusText,
                indeterminate: true,
                operationType: StatusOperationType.GitSwitchBranch,
                cancelAction: () => gitCts.Cancel());

            var progress = new Progress<string>(status =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (TryParseTrailingPercent(status, out var percent))
                        UpdateStatusOperationProgress(percent, statusText, statusOperationId);
                    else
                        UpdateStatusOperationText(statusText, statusOperationId);
                });
            });
            var success = await _gitService.SwitchBranchAsync(_currentPath, branchName, progress, cancellationToken);

            // A lightweight retry helps recover from transient remote/network hiccups.
            if (!success)
                success = await _gitService.SwitchBranchAsync(_currentPath, branchName, progress: null, cancellationToken);

            if (!success)
            {
                CompleteStatusOperation(statusOperationId);
                await ShowErrorAsync(_localization.Format("Git.Error.BranchSwitchFailed", branchName));
                return;
            }

            // Reload tree first so branch/title state is only updated after full success.
            // This keeps UI stable if reload fails or gets cancelled mid-flight.
            await ReloadProjectAsync(cancellationToken);
            await RefreshGitBranchesAsync(_currentPath, cancellationToken);
            CompleteStatusOperation(statusOperationId);

            _viewModel.CurrentBranch = branchName;
            UpdateTitle();
            _toastService.Show(_localization.Format("Toast.Git.BranchSwitched", branchName));

            // Clean up memory from old branch tree
            ScheduleBackgroundMemoryCleanup();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CompleteStatusOperation(statusOperationId);
            _toastService.Show(_localization["Toast.Operation.GitCanceled"]);
        }
        catch (Exception ex)
        {
            CompleteStatusOperation(statusOperationId);
            await ShowErrorAsync(_localization.Format("Git.Error.BranchSwitchFailed", ex.Message));
        }
        finally
        {
            DisposeIfCurrent(ref _gitOperationCts, gitCts);
        }
    }

    private async Task RefreshGitBranchesAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var branches = await _gitService.GetBranchesAsync(repositoryPath, cancellationToken);

            _viewModel.GitBranches.Clear();
            foreach (var branch in branches)
                _viewModel.GitBranches.Add(branch);

            // Update branch menu
            UpdateBranchMenu();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Ignore branch loading errors
        }
    }

    private void UpdateBranchMenu()
    {
        var branchMenuItem = _topMenuBar?.GitBranchMenuItemControl;
        if (branchMenuItem is null)
            return;

        // Clear old items - they will be garbage collected since they have no external references
        // and we're using a named handler method instead of lambda captures
        branchMenuItem.Items.Clear();

        foreach (var branch in _viewModel.GitBranches)
        {
            var item = new MenuItem
            {
                Header = branch.IsActive ? $"✓ {branch.Name}" : $"   {branch.Name}",
                Tag = branch.Name
            };

            // Use named handler to avoid closure capture memory leaks
            item.Click += OnBranchMenuItemClick;

            branchMenuItem.Items.Add(item);
        }
    }

    private void OnBranchMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string name })
            _topMenuBar?.OnGitBranchSwitch(name);
    }

    #endregion

    private void OnAboutOpenLink(object? sender, RoutedEventArgs e)
    {
        OpenRepositoryLink();
        e.Handled = true;
    }

    private async void OnAboutCopyLink(object? sender, RoutedEventArgs e)
    {
        try
        {
            await SetClipboardTextAsync(ProjectLinks.RepositoryUrl);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
        e.Handled = true;
    }

    private void OnSearchNext(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_viewModel.SearchQuery))
            return;

        if (!_searchCoordinator.HasMatches)
        {
            _toastService.Show(_localization["Toast.NoMatches"]);
            return;
        }

        _searchCoordinator.Navigate(1);
    }

    private void OnSearchPrev(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_viewModel.SearchQuery))
            return;

        if (!_searchCoordinator.HasMatches)
        {
            _toastService.Show(_localization["Toast.NoMatches"]);
            return;
        }

        _searchCoordinator.Navigate(-1);
    }

    private async void OnToggleSearch(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsProjectLoaded) return;
        if (_viewModel.IsPreviewMode) return;

        if (_viewModel.SearchVisible)
        {
            await CloseSearchAsync();
            return;
        }

        // Keep only one active text tool at a time: close filter first, then open search.
        if (IsFilterBarEffectivelyVisible())
            await CloseFilterAsync(focusTree: false);

        ShowSearch();
    }

    private void OnSearchClose(object? sender, RoutedEventArgs e) => _ = CloseSearchAsync();

    private async void OnToggleFilter(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsProjectLoaded) return;
        if (_viewModel.IsPreviewMode) return;

        if (_viewModel.FilterVisible)
        {
            await CloseFilterAsync();
            return;
        }

        // Keep only one active text tool at a time: close search first, then open filter.
        if (IsSearchBarEffectivelyVisible())
            await CloseSearchAsync(focusTree: false, waitForAnimation: true);

        ShowFilter();
    }

    private void OnFilterClose(object? sender, RoutedEventArgs e) => _ = CloseFilterAsync();

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _ = CloseFilterAsync();
            e.Handled = true;
        }
    }

    private void ShowFilter(bool focusInput = true, bool selectAllOnFocus = true)
    {
        if (!_viewModel.IsProjectLoaded) return;
        if (_viewModel.IsPreviewMode) return;
        if (_filterBarAnimating) return;

        _viewModel.FilterVisible = true;
        AnimateFilterBar(true);

        if (!focusInput)
            return;

        _ = FocusFilterBoxAfterOpenAnimationAsync(selectAllOnFocus);
    }

    private async Task CloseFilterAsync(bool focusTree = true)
    {
        if (!IsFilterBarEffectivelyVisible()) return;

        // Remove focus from the filter textbox before close animation starts.
        // This avoids a transient focused-border artifact during panel collapse.
        if (_filterBar?.FilterBoxControl?.IsFocused == true)
            _treeView?.Focus();
        SuppressFilterBoxAccentVisual();

        _viewModel.FilterVisible = false;

        if (_filterBarAnimating)
            _filterBarClosePending = true;
        else
            AnimateFilterBar(false);
        if (focusTree)
            _treeView?.Focus();

        // Let close animation complete first to avoid concurrent UI + tree rebuild pressure.
        await WaitForPanelAnimationAsync(FilterBarAnimationDuration);

        // If filter was reopened during animation, keep current query/state intact.
        if (_viewModel.FilterVisible)
            return;

        if (!string.IsNullOrEmpty(_viewModel.NameFilter))
        {
            _viewModel.NameFilter = string.Empty;
            _filterCoordinator.CancelPending();
            _ = ApplyFilterRealtimeAsync(CancellationToken.None);

            // Release stale filtered snapshots after rebuild is queued.
            ScheduleBackgroundMemoryCleanup();
        }
        else
        {
            _filterCoordinator.CancelPending();
        }
    }

    private void ForceCloseSearchAndFilterForPreview()
    {
        // Only hide search/filter islands visually for preview mode.
        // Do not clear queries or re-apply filters here, otherwise tree selection state
        // can be rebuilt and preview will diverge from copy/export behavior.
        _viewModel.SearchVisible = false;
        _viewModel.FilterVisible = false;

        _searchBarAnimating = false;
        _filterBarAnimating = false;

        // Cancel any pending debounce operations to avoid wasted background work
        _searchCoordinator.CancelPending();
        _filterCoordinator.CancelPending();

        if (_searchBarContainer is not null)
        {
            _searchBarContainer.Height = 0;
            _searchBarContainer.Margin = new Thickness(0);
            _searchBarContainer.IsVisible = false;
        }

        if (_searchBarTransform is not null)
            _searchBarTransform.Y = -SearchBarHeight;

        if (_searchBar is not null)
            _searchBar.Opacity = 0;

        if (_filterBarContainer is not null)
        {
            _filterBarContainer.Height = 0;
            _filterBarContainer.Margin = new Thickness(0);
            _filterBarContainer.IsVisible = false;
        }

        if (_filterBarTransform is not null)
            _filterBarTransform.Y = -FilterBarHeight;

        if (_filterBar is not null)
            _filterBar.Opacity = 0;
    }

    private void FocusPreviewSurface()
    {
        if (_previewTextScrollViewer is not null && _previewTextScrollViewer.Focusable)
        {
            _previewTextScrollViewer.Focus();
            return;
        }

        _treeView?.Focus();
    }

    private void ApplyFilterRealtimeWithToken(CancellationToken cancellationToken)
    {
        // Fire-and-forget with cancellation support
        _ = ApplyFilterRealtimeAsync(cancellationToken);
    }

    private async Task ApplyFilterRealtimeAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrEmpty(_currentPath)) return;

            var query = _viewModel.NameFilter?.Trim();
            bool hasQuery = !string.IsNullOrWhiteSpace(query);
            var version = Interlocked.Increment(ref _filterApplyVersion);

            if (hasQuery && _filterExpansionSnapshot is null)
                _filterExpansionSnapshot = CaptureExpandedNodes();

            cancellationToken.ThrowIfCancellationRequested();

            await RefreshTreeAsync(interactiveFilter: true);

            cancellationToken.ThrowIfCancellationRequested();

            if (version != _filterApplyVersion)
                return;
            _searchCoordinator.UpdateHighlights(query);

            if (hasQuery)
            {
                TreeSearchEngine.ApplySmartExpandForFilter(
                    _viewModel.TreeNodes,
                    query!,
                    node => node.DisplayName,
                    node => node.Children,
                    (node, expanded) => node.IsExpanded = expanded);
            }
            else if (_filterExpansionSnapshot is not null)
            {
                RestoreExpandedNodes(_filterExpansionSnapshot);
                _filterExpansionSnapshot = null;
            }
        }
        catch (OperationCanceledException)
        {
            // Filter was superseded by a newer request - expected behavior
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    private void ApplyFilterRealtime()
    {
        _ = ApplyFilterRealtimeAsync(CancellationToken.None);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _ = CloseSearchAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            _searchCoordinator.UpdateSearchMatches();
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                _searchCoordinator.Navigate(-1);
            else
                _searchCoordinator.Navigate(1);

            e.Handled = true;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;

        // Ctrl+O (always available)
        if (mods == KeyModifiers.Control && e.Key == Key.O)
        {
            OnOpenFolder(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Ctrl+F (available only when a project is loaded, same as WinForms miSearch.Enabled)
        if (mods == KeyModifiers.Control && e.Key == Key.F)
        {
            if (IsSearchFilterHotkeyDebounced(ref _lastSearchHotkeyTimestamp))
            {
                e.Handled = true;
                return;
            }

            ScheduleSearchOrFilterHotkeyToggle(
                isSearchToggle: true,
                static (window) => window.OnToggleSearch(window, new RoutedEventArgs()));
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+N - Filter by name
        if (mods == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.N)
        {
            if (IsSearchFilterHotkeyDebounced(ref _lastFilterHotkeyTimestamp))
            {
                e.Handled = true;
                return;
            }

            if (_viewModel.IsProjectLoaded)
            {
                ScheduleSearchOrFilterHotkeyToggle(
                    isSearchToggle: false,
                    static (window) => window.OnToggleFilter(window, new RoutedEventArgs()));
            }
            e.Handled = true;
            return;
        }

        // Esc closes the help popover
        if (e.Key == Key.Escape && _viewModel.HelpPopoverOpen)
        {
            _viewModel.HelpPopoverOpen = false;
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _viewModel.HelpDocsPopoverOpen)
        {
            _viewModel.HelpDocsPopoverOpen = false;
            e.Handled = true;
            return;
        }

        // Esc closes the currently active text tool.
        if (e.Key == Key.Escape && _viewModel.SearchVisible)
        {
            _ = CloseSearchAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _viewModel.FilterVisible)
        {
            _ = CloseFilterAsync();
            e.Handled = true;
            return;
        }

        // F5 refresh (same as WinForms)
        if (e.Key == Key.F5)
        {
            if (_viewModel.IsProjectLoaded)
                OnRefresh(this, new RoutedEventArgs());

            e.Handled = true;
            return;
        }

        // Zoom hotkeys (in WinForms they work even without a loaded project)
        if (mods == KeyModifiers.Control && (e.Key == Key.OemPlus || e.Key == Key.Add))
        {
            AdjustZoomFontSize(1);
            e.Handled = true;
            return;
        }

        if (mods == KeyModifiers.Control && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
        {
            AdjustZoomFontSize(-1);
            e.Handled = true;
            return;
        }

        if (mods == KeyModifiers.Control && (e.Key == Key.D0 || e.Key == Key.NumPad0))
        {
            OnZoomReset(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (!_viewModel.IsProjectLoaded)
            return;

        // Ctrl+B Preview mode toggle
        if (mods == KeyModifiers.Control && e.Key == Key.B)
        {
            OnTogglePreview(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Ctrl+P Options panel toggle
        if (mods == KeyModifiers.Control && e.Key == Key.P)
        {
            OnToggleSettings(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Ctrl+E Expand All
        if (mods == KeyModifiers.Control && e.Key == Key.E)
        {
            if (!_viewModel.IsPreviewMode)
                ExpandCollapseTree(expand: true);
            e.Handled = true;
            return;
        }

        // Ctrl+W Collapse All
        if (mods == KeyModifiers.Control && e.Key == Key.W)
        {
            if (!_viewModel.IsPreviewMode)
                ExpandCollapseTree(expand: false);
            e.Handled = true;
            return;
        }

        // Copy hotkeys (same as WinForms)
        if (mods == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.C)
        {
            OnCopyTree(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (mods == (KeyModifiers.Control | KeyModifiers.Alt) && e.Key == Key.C)
        {
            OnCopyTree(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (mods == (KeyModifiers.Control | KeyModifiers.Alt) && e.Key == Key.V)
        {
            OnCopyContent(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (mods == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.V)
        {
            OnCopyTreeAndContent(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
    }

    private void OnTreePointerEntered(object? sender, PointerEventArgs e)
    {
        _treeView?.Focus();
    }

    private void OnWindowPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!TreeZoomWheelHandler.TryGetZoomStep(e.KeyModifiers, e.Delta, IsPointerOverZoomSurface(e.Source), out var step))
            return;

        AdjustZoomFontSize(step);
        e.Handled = true;
    }

    private bool IsPointerOverZoomSurface(object? source)
    {
        if (_treeView is null)
            return false;

        if (ReferenceEquals(source, _treeView))
            return true;

        if (_viewModel.IsPreviewMode)
        {
            if (_previewTextScrollViewer is not null && ReferenceEquals(source, _previewTextScrollViewer))
                return true;

            if (_previewLineNumbersControl is not null && ReferenceEquals(source, _previewLineNumbersControl))
                return true;
        }

        if (source is not Visual visual)
            return false;

        foreach (var ancestor in visual.GetVisualAncestors())
        {
            if (ReferenceEquals(ancestor, _treeView))
                return true;

            if (!_viewModel.IsPreviewMode)
                continue;

            if (_previewTextScrollViewer is not null && ReferenceEquals(ancestor, _previewTextScrollViewer))
                return true;

            if (_previewLineNumbersControl is not null && ReferenceEquals(ancestor, _previewLineNumbersControl))
                return true;
        }

        return false;
    }

    private static bool IsSearchFilterHotkeyDebounced(ref long lastTimestamp)
    {
        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Read(ref lastTimestamp);

        if (previous != 0)
        {
            var elapsed = TimeSpan.FromSeconds((now - previous) / (double)Stopwatch.Frequency);
            if (elapsed < SearchFilterHotkeyDebounceWindow)
                return true;
        }

        Interlocked.Exchange(ref lastTimestamp, now);
        return false;
    }

    private void ScheduleSearchOrFilterHotkeyToggle(bool isSearchToggle, Action<MainWindow> toggleAction)
    {
        ref var pendingFlag = ref isSearchToggle ? ref _pendingSearchHotkeyToggle : ref _pendingFilterHotkeyToggle;
        if (Interlocked.CompareExchange(ref pendingFlag, 1, 0) != 0)
            return;

        // Execute toggle after the current keyboard input dispatch completes.
        // This prevents visual artifacts caused by state changes during tunnel key handling.
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (!_viewModel.IsProjectLoaded || _viewModel.IsPreviewMode)
                    return;

                toggleAction(this);
            }
            finally
            {
                if (isSearchToggle)
                    Interlocked.Exchange(ref _pendingSearchHotkeyToggle, 0);
                else
                    Interlocked.Exchange(ref _pendingFilterHotkeyToggle, 0);
            }
        }, DispatcherPriority.Background);
    }

    private void ShowSearch(bool focusInput = true, bool selectAllOnFocus = true)
    {
        if (!_viewModel.IsProjectLoaded) return;
        if (_viewModel.IsPreviewMode) return;
        if (_searchBarAnimating) return;

        _viewModel.SearchVisible = true;
        AnimateSearchBar(true);

        if (!focusInput)
            return;

        _ = FocusSearchBoxAfterOpenAnimationAsync(selectAllOnFocus);
    }

    private async Task FocusSearchBoxAfterOpenAnimationAsync(bool selectAllOnFocus)
    {
        await WaitForPanelAnimationAsync(SearchBarAnimationDuration);
        if (!_viewModel.SearchVisible || _viewModel.IsPreviewMode)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            FocusInputTextBox(_searchBar?.SearchBoxControl, selectAllOnFocus);
        }, DispatcherPriority.Background);
    }

    private async Task FocusFilterBoxAfterOpenAnimationAsync(bool selectAllOnFocus)
    {
        await WaitForPanelAnimationAsync(FilterBarAnimationDuration);
        if (!_viewModel.FilterVisible || _viewModel.IsPreviewMode)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            FocusInputTextBox(_filterBar?.FilterBoxControl, selectAllOnFocus);
        }, DispatcherPriority.Background);
    }

    private void FocusInputTextBox(TextBox? textBox, bool selectAllOnFocus)
    {
        if (textBox is null)
            return;

        textBox.Focus();
        if (selectAllOnFocus)
        {
            textBox.SelectAll();
            return;
        }

        // Keep text editable after preview restore: place caret to the end without selecting text.
        PlaceCaretAtTextEnd(textBox);
        _ = Dispatcher.UIThread.InvokeAsync(() => PlaceCaretAtTextEnd(textBox), DispatcherPriority.Input);
    }

    private static void PlaceCaretAtTextEnd(TextBox textBox)
    {
        var end = textBox.Text?.Length ?? 0;
        textBox.SelectionStart = end;
        textBox.SelectionEnd = end;
        textBox.CaretIndex = end;
    }

    private async Task CloseSearchAsync(bool focusTree = true, bool waitForAnimation = false)
    {
        if (!IsSearchBarEffectivelyVisible())
            return;

        // Remove focus from the search textbox before close animation starts.
        // This avoids a transient focused-border artifact during panel collapse.
        if (_searchBar?.SearchBoxControl?.IsFocused == true)
            _treeView?.Focus();
        SuppressSearchBoxAccentVisual();

        _viewModel.SearchVisible = false;
        _viewModel.SearchQuery = string.Empty;

        // Clear search and collapse tree immediately (same behavior as before),
        // without waiting for debounce.
        _searchCoordinator.CancelPending();
        _searchCoordinator.UpdateSearchMatches();
        if (_searchBarAnimating)
            _searchBarClosePending = true;
        else
            AnimateSearchBar(false);
        if (focusTree)
            _treeView?.Focus();

        // Aggressively release search highlight objects (InlineCollections, Run instances)
        // and trim working set on a background thread.
        ScheduleBackgroundMemoryCleanup();

        if (waitForAnimation)
            await WaitForPanelAnimationAsync(SearchBarAnimationDuration);
    }

    private bool IsSearchBarEffectivelyVisible()
    {
        if (_viewModel.SearchVisible)
            return true;

        if (_searchBarContainer?.IsVisible == true)
            return true;

        return _searchBarContainer?.Bounds.Height > 0.5;
    }

    private bool IsFilterBarEffectivelyVisible()
    {
        if (_viewModel.FilterVisible)
            return true;

        if (_filterBarContainer?.IsVisible == true)
            return true;

        return _filterBarContainer?.Bounds.Height > 0.5;
    }

    private void SuppressSearchBoxAccentVisual()
    {
        _searchBar?.SearchBoxControl?.Classes.Add("suppress-accent");
    }

    private void RestoreSearchBoxAccentVisual()
    {
        _searchBar?.SearchBoxControl?.Classes.Remove("suppress-accent");
    }

    private void SuppressFilterBoxAccentVisual()
    {
        _filterBar?.FilterBoxControl?.Classes.Add("suppress-accent");
    }

    private void RestoreFilterBoxAccentVisual()
    {
        _filterBar?.FilterBoxControl?.Classes.Remove("suppress-accent");
    }

    private void ForceHideSearchBarVisualState()
    {
        SuppressSearchBoxAccentVisual();

        if (_searchBarContainer is not null)
        {
            _searchBarContainer.Height = 0;
            _searchBarContainer.Margin = new Thickness(0);
            _searchBarContainer.IsVisible = false;
        }

        if (_searchBarTransform is not null)
            _searchBarTransform.Y = -SearchBarHeight;

        if (_searchBar is not null)
        {
            _searchBar.Opacity = 0;
            _searchBar.IsHitTestVisible = false;
            _searchBar.IsEnabled = false;
        }
    }

    private void ForceShowSearchBarVisualState()
    {
        RestoreSearchBoxAccentVisual();

        if (_searchBarContainer is not null)
        {
            _searchBarContainer.Height = SearchBarHeight;
            _searchBarContainer.Margin = new Thickness(0, 0, 0, PanelIslandSpacing);
            _searchBarContainer.IsVisible = true;
        }

        if (_searchBarTransform is not null)
            _searchBarTransform.Y = 0;

        if (_searchBar is not null)
        {
            _searchBar.Opacity = 1;
            _searchBar.IsHitTestVisible = true;
            _searchBar.IsEnabled = true;
        }
    }

    private void ForceHideFilterBarVisualState()
    {
        SuppressFilterBoxAccentVisual();

        if (_filterBarContainer is not null)
        {
            _filterBarContainer.Height = 0;
            _filterBarContainer.Margin = new Thickness(0);
            _filterBarContainer.IsVisible = false;
        }

        if (_filterBarTransform is not null)
            _filterBarTransform.Y = -FilterBarHeight;

        if (_filterBar is not null)
        {
            _filterBar.Opacity = 0;
            _filterBar.IsHitTestVisible = false;
            _filterBar.IsEnabled = false;
        }
    }

    private void ForceShowFilterBarVisualState()
    {
        RestoreFilterBoxAccentVisual();

        if (_filterBarContainer is not null)
        {
            _filterBarContainer.Height = FilterBarHeight;
            _filterBarContainer.Margin = new Thickness(0, 0, 0, PanelIslandSpacing);
            _filterBarContainer.IsVisible = true;
        }

        if (_filterBarTransform is not null)
            _filterBarTransform.Y = 0;

        if (_filterBar is not null)
        {
            _filterBar.Opacity = 1;
            _filterBar.IsHitTestVisible = true;
            _filterBar.IsEnabled = true;
        }
    }

    private void SyncSearchAndFilterVisualStateFromFlags()
    {
        // Load-cancel fallback restores logical visibility flags first.
        // Apply matching visual state immediately to avoid stale hidden containers.
        _searchBarAnimating = false;
        _filterBarAnimating = false;
        _searchBarClosePending = false;
        _filterBarClosePending = false;

        if (_viewModel.SearchVisible && _viewModel.FilterVisible)
        {
            // Keep one active text tool if an old snapshot ever contains both flags.
            _viewModel.FilterVisible = false;
        }

        if (_viewModel.SearchVisible)
            ForceShowSearchBarVisualState();
        else
            ForceHideSearchBarVisualState();

        if (_viewModel.FilterVisible)
            ForceShowFilterBarVisualState();
        else
            ForceHideFilterBarVisualState();
    }

    private async Task PrepareSearchAndFilterForProjectLoadAsync()
    {
        var hadVisibleSearch = IsSearchBarEffectivelyVisible();
        var hadVisibleFilter = IsFilterBarEffectivelyVisible();

        Interlocked.Increment(ref _suppressSearchFilterRealtimeDepth);
        try
        {
            _viewModel.SearchVisible = false;
            _viewModel.FilterVisible = false;

            _searchBarClosePending = false;
            _filterBarClosePending = false;

            if (hadVisibleSearch && !_searchBarAnimating)
                AnimateSearchBar(false);

            if (hadVisibleFilter && !_filterBarAnimating)
                AnimateFilterBar(false);

            if (hadVisibleSearch || hadVisibleFilter)
                await WaitForPanelAnimationAsync(SearchBarAnimationDuration > FilterBarAnimationDuration
                    ? SearchBarAnimationDuration
                    : FilterBarAnimationDuration);

            _searchCoordinator.CancelPending();
            _filterCoordinator.CancelPending();

            if (!string.IsNullOrEmpty(_viewModel.SearchQuery))
                _viewModel.SearchQuery = string.Empty;
            if (!string.IsNullOrEmpty(_viewModel.NameFilter))
                _viewModel.NameFilter = string.Empty;

            // Cancel once more after resetting queries to eliminate any stale queued work.
            _searchCoordinator.CancelPending();
            _filterCoordinator.CancelPending();

            _searchCoordinator.UpdateHighlights(null);
            _searchCoordinator.ClearSearchState();
            _filterExpansionSnapshot = null;
            Interlocked.Increment(ref _filterApplyVersion);

            ForceHideSearchBarVisualState();
            ForceHideFilterBarVisualState();
        }
        finally
        {
            Interlocked.Decrement(ref _suppressSearchFilterRealtimeDepth);
        }
    }

    private void OnRootAllChanged(object? sender, RoutedEventArgs e)
    {
        // Get value directly from control - event fires BEFORE binding updates ViewModel
        var check = (sender as CheckBox)?.IsChecked == true;
        _selectionCoordinator.HandleRootAllChanged(check, _currentPath);
    }

    private void OnExtensionsAllChanged(object? sender, RoutedEventArgs e)
    {
        // Get value directly from control - event fires BEFORE binding updates ViewModel
        var check = (sender as CheckBox)?.IsChecked == true;
        _selectionCoordinator.HandleExtensionsAllChanged(check);
    }

    private void OnIgnoreAllChanged(object? sender, RoutedEventArgs e)
    {
        // Get value directly from control - event fires BEFORE binding updates ViewModel
        var check = (sender as CheckBox)?.IsChecked == true;
        _selectionCoordinator.HandleIgnoreAllChanged(check, _currentPath);
    }

    private async void OnApplySettings(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Font family follows WinForms behavior: applied only on Apply
            var pending = _viewModel.PendingFontFamily;
            if (pending is not null &&
                !string.Equals(_viewModel.SelectedFontFamily?.Name, pending.Name, StringComparison.OrdinalIgnoreCase))
            {
                _viewModel.SelectedFontFamily = pending;
            }

            await RefreshTreeAsync();
            await _selectionCoordinator.WaitForPendingRefreshesAsync();
            PersistLocalProjectProfileIfNeeded();
        }
        catch (OperationCanceledException)
        {
            // Cancellation is handled by status operation fallback.
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    private void PersistLocalProjectProfileIfNeeded()
    {
        if (!IsLocalProjectProfilePersistenceApplicable())
            return;

        if (string.IsNullOrWhiteSpace(_currentPath))
            return;

        var profile = new ProjectSelectionProfile(
            SelectedRootFolders: CollectCheckedOptionNames(_viewModel.RootFolders),
            SelectedExtensions: CollectCheckedOptionNames(_viewModel.Extensions),
            SelectedIgnoreOptions: _selectionCoordinator.GetSelectedIgnoreOptionIds().ToArray());

        _projectProfileStore.SaveProfile(_currentPath, profile);
    }

    private bool TryGetLocalProjectProfile(out ProjectSelectionProfile profile)
    {
        profile = new ProjectSelectionProfile(
            SelectedRootFolders: Array.Empty<string>(),
            SelectedExtensions: Array.Empty<string>(),
            SelectedIgnoreOptions: Array.Empty<IgnoreOptionId>());

        if (!IsLocalProjectProfilePersistenceApplicable() || string.IsNullOrWhiteSpace(_currentPath))
            return false;

        return _projectProfileStore.TryLoadProfile(_currentPath, out profile);
    }

    private bool IsLocalProjectProfilePersistenceApplicable()
        => _viewModel.ProjectSourceType == ProjectSourceType.LocalFolder;

    private async Task TryOpenFolderAsync(string path, bool fromDialog)
    {
        if (!Directory.Exists(path))
        {
            await ShowErrorAsync(_localization.Format("Msg.PathNotFound", path));
            return;
        }

        if (!_scanOptions.CanReadRoot(path))
        {
            if (TryElevateAndRestart(path))
                return;

            if (BuildFlags.AllowElevation)
                await ShowErrorAsync(_localization["Msg.AccessDeniedRoot"]);
            return;
        }

        _activeProjectLoadCancellationSnapshot = CaptureProjectLoadCancellationSnapshot();
        await PrepareSearchAndFilterForProjectLoadAsync();
        CancelPreviewRefresh();

        // Clear previous project state BEFORE loading new one to release memory early
        // This is critical when switching between large projects
        if (_viewModel.IsProjectLoaded)
            ClearPreviousProjectState(forceCompactingGc: true);

        var cachedRepoPathToDeleteOnSuccess = fromDialog ? _currentCachedRepoPath : null;
        var projectLoadCts = ReplaceCancellationSource(ref _projectOperationCts);
        var cancellationToken = projectLoadCts.Token;
        _viewModel.StatusMetricsVisible = false;
        var statusOperationId = BeginStatusOperation(
            _viewModel.StatusOperationLoadingProject,
            indeterminate: true,
            operationType: StatusOperationType.LoadProject,
            cancelAction: () => projectLoadCts.Cancel());
        try
        {
            _currentPath = path;
            _viewModel.IsProjectLoaded = true;
            _viewModel.SettingsVisible = true;
            _viewModel.SearchVisible = false;

            // Set project source type based on how it was opened
            // If opened from dialog (File → Open), it's LocalFolder
            // If opened from Git clone, the source type is already set
            if (fromDialog)
            {
                _viewModel.ProjectSourceType = ProjectSourceType.LocalFolder;
                _viewModel.CurrentBranch = string.Empty;
                _viewModel.GitBranches.Clear();
                _currentProjectDisplayName = null;
                _currentRepositoryUrl = null;
            }

            UpdateTitle();

            await ReloadProjectAsync(cancellationToken, applyStoredProfile: true);

            // Clear cached repo path only after the new local project load has completed successfully.
            if (fromDialog && !string.IsNullOrWhiteSpace(cachedRepoPathToDeleteOnSuccess))
            {
                _repoCacheService.DeleteRepositoryDirectory(cachedRepoPathToDeleteOnSuccess);
                _currentCachedRepoPath = null;
            }

            _activeProjectLoadCancellationSnapshot = null;
            CompleteStatusOperation(statusOperationId);

            // Clean up memory from previous project (old tree, strings, etc.)
            // Must be AFTER loading new project so old references are replaced
            ScheduleBackgroundMemoryCleanup();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (IsStatusOperationActive(statusOperationId) && TryApplyActiveProjectLoadCancellationFallback())
            {
                _toastService.Show(_localization["Toast.Operation.LoadCanceled"]);
            }

            CompleteStatusOperation(statusOperationId);
        }
        catch
        {
            _activeProjectLoadCancellationSnapshot = null;
            CompleteStatusOperation(statusOperationId);
            throw;
        }
        finally
        {
            DisposeIfCurrent(ref _projectOperationCts, projectLoadCts);
        }
    }

    private bool TryElevateAndRestart(string path)
    {
        if (!BuildFlags.AllowElevation)
        {
            // Store builds: never attempt elevation, just show a clear message.
            _ = ShowErrorAsync(_localization["Msg.AccessDeniedElevationRequired"]);
            return false;
        }

        if (_elevation.IsAdministrator) return false;
        if (_elevationAttempted) return false;

        _elevationAttempted = true;

        var opts = new CommandLineOptions(
            Path: path,
            Language: _localization.CurrentLanguage,
            ElevationAttempted: true);

        bool started = _elevation.TryRelaunchAsAdministrator(opts);
        if (started)
        {
            Close();
            return true;
        }

        _ = ShowInfoAsync(_localization["Msg.ElevationCanceled"]);
        return false;
    }

    private async Task ReloadProjectAsync(
        CancellationToken cancellationToken = default,
        bool applyStoredProfile = false)
    {
        if (string.IsNullOrEmpty(_currentPath)) return;
        cancellationToken.ThrowIfCancellationRequested();

        if (applyStoredProfile)
        {
            if (TryGetLocalProjectProfile(out var profile))
                _selectionCoordinator.ApplyProjectProfileSelections(_currentPath, profile);
            else
                _selectionCoordinator.ResetProjectProfileSelections(_currentPath);
        }

        // Keep root/extension scans sequenced to avoid inconsistent UI states.
        await _selectionCoordinator.RefreshRootAndDependentsAsync(_currentPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await RefreshTreeAsync(cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Clears state from previous project to release memory before loading a new one.
    /// </summary>
    private void ClearPreviousProjectState(bool forceCompactingGc = false)
    {
        _restoreSearchAfterPreview = false;
        _restoreFilterAfterPreview = false;
        _previewMemoryCleanupCts?.Cancel();
        _previewMemoryCleanupCts?.Dispose();
        _previewMemoryCleanupCts = null;

        // Clear search state first (holds references to TreeNodeViewModel)
        _searchCoordinator.ClearSearchState();

        // Clear filter state
        _filterExpansionSnapshot = null;
        _filterCoordinator.CancelPending();

        // Clear TreeView selection and temporarily disconnect ItemsSource
        // to force Avalonia to release all TreeViewItem containers
        if (_treeView is not null)
        {
            _treeView.SelectedItem = null;
            var savedItemTemplate = _treeView.ItemTemplate;
            _treeView.ItemTemplate = null;
            _treeView.ItemsSource = null;
            _treeView.InvalidateMeasure();
            _treeView.InvalidateArrange();
            _treeView.InvalidateVisual();
            _treeView.ItemTemplate = savedItemTemplate;
        }

        // Recursively clear all tree nodes to break circular references and release memory
        foreach (var node in _viewModel.TreeNodes)
            node.ClearRecursive();
        _viewModel.ResetTreeNodes();
        ClearFileMetricsCache(trimCapacity: true);

        // Reconnect ItemsSource
        if (_treeView is not null)
            _treeView.ItemsSource = _viewModel.TreeNodes;

        // Clear current tree descriptor reference (this is the second copy of the tree)
        _currentTree = null;
        _hasCompleteMetricsBaseline = false;
        _viewModel.StatusMetricsVisible = false;
        _viewModel.StatusTreeStatsText = string.Empty;
        _viewModel.StatusContentStatsText = string.Empty;
        _viewModel.PreviewText = string.Empty;
        _viewModel.PreviewLineCount = 1;
        _viewModel.IsPreviewLoading = false;
        InvalidatePreviewCache();

        // Clear icon cache to release bitmaps
        _iconCache.Clear();

        if (forceCompactingGc)
        {
            // Full compacting collection — user is switching projects and expects memory
            // from the old tree (view models, icons, metrics cache) to be freed immediately.
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(1, GCCollectionMode.Forced, blocking: false);
            TrimNativeWorkingSet();
        }
        else
        {
            // Non-switching state reset (e.g. reload) — still force collection but skip compaction.
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        }
    }

    private async Task RefreshTreeAsync(bool interactiveFilter = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_currentPath)) return;
        cancellationToken.ThrowIfCancellationRequested();

        using var _ = PerformanceMetrics.Measure("RefreshTreeAsync");

        // Cancel any previous refresh operation to avoid race conditions
        var refreshCts = new CancellationTokenSource();
        var previousRefreshCts = Interlocked.Exchange(ref _refreshCts, refreshCts);
        previousRefreshCts?.Cancel();
        previousRefreshCts?.Dispose();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(refreshCts.Token, cancellationToken);
        var linkedToken = linkedCts.Token;

        var allowedExt = CollectCheckedOptionNames(_viewModel.Extensions);
        var allowedRoot = CollectCheckedOptionNames(_viewModel.RootFolders);

        var selectedIgnoreOptions = _selectionCoordinator.GetSelectedIgnoreOptionIds();
        var ignoreRules = BuildIgnoreRules(_currentPath, selectedIgnoreOptions, allowedRoot);

        var nameFilter = string.IsNullOrWhiteSpace(_viewModel.NameFilter) ? null : _viewModel.NameFilter.Trim();

        var options = new TreeFilterOptions(
            AllowedExtensions: allowedExt,
            AllowedRootFolders: allowedRoot,
            IgnoreRules: ignoreRules,
            NameFilter: nameFilter);

        if (!interactiveFilter)
            _viewModel.StatusMetricsVisible = false;

        try
        {
            BuildTreeResult result;

            // Build the tree off the UI thread to keep the window responsive on large folders.
            using (PerformanceMetrics.Measure("BuildTree"))
            {
                result = await Task.Run(
                    () => _buildTree.Execute(new BuildTreeRequest(_currentPath, options), linkedToken),
                    linkedToken);
            }

            // Check if this operation was superseded by a newer one
            linkedToken.ThrowIfCancellationRequested();

            if (result.RootAccessDenied && TryElevateAndRestart(_currentPath))
                return;

            // Build ViewModel tree off the UI thread for better responsiveness.
            // IconCache is now thread-safe, enabling parallel icon loading.
            var displayName = !string.IsNullOrEmpty(_currentProjectDisplayName)
                ? _currentProjectDisplayName
                : GetDirectoryNameSafe(_currentPath!);

            var root = await Task.Run(() =>
            {
                var node = BuildTreeViewModel(result.Root, null);
                node.DisplayName = displayName;
                return node;
            }, linkedToken);

            linkedToken.ThrowIfCancellationRequested();

            // Swap trees only after the new root is fully materialized.
            // This prevents losing the previously visible project on cancellation.
            _searchCoordinator.ClearSearchState();
            if (_treeView is not null)
                _treeView.SelectedItem = null;

            foreach (var node in _viewModel.TreeNodes)
                node.ClearRecursive();
            _viewModel.TreeNodes.Clear();

            _currentTree = result;
            _viewModel.TreeNodes.Add(root);
            root.IsExpanded = true;

            if (!interactiveFilter && !string.IsNullOrWhiteSpace(nameFilter) && root.Children.Count == 0)
                _toastService.Show(_localization["Toast.NoMatches"]);

            _searchCoordinator.UpdateSearchMatches();

            // Initialize file metrics cache in background for real-time status bar updates
            // Only do full scan on initial load, not on interactive filter changes
            if (!interactiveFilter)
            {
                // Animate settings panel BEFORE metrics calculation starts
                // so user sees the panel immediately after tree renders
                if (_viewModel.SettingsVisible && !_settingsAnimating)
                {
                    await WaitForTreeRenderStabilizationAsync(linkedToken);
                    if (_viewModel.SettingsVisible && !_settingsAnimating)
                        AnimateSettingsPanel(true);
                }

                UpdateStatusOperationText(_viewModel.StatusOperationCalculatingData);
                await InitializeFileMetricsCacheAsync(linkedToken);
            }
            else
            {
                // For filter changes, just recalculate from existing cache
                RecalculateMetricsAsync();
            }

            SchedulePreviewRefresh(immediate: true);

            // Collect old tree objects after building the new one.
            // Full-load refreshes warrant a forced sweep; interactive filter changes skip GC entirely.
            if (!interactiveFilter)
                GC.Collect(2, GCCollectionMode.Forced, blocking: false);
        }
        finally
        {
            DisposeIfCurrent(ref _refreshCts, refreshCts);
        }
    }

    private TreeNodeViewModel BuildTreeViewModel(TreeNodeDescriptor descriptor, TreeNodeViewModel? parent)
    {
        var icon = _iconCache.GetIcon(descriptor.IconKey);
        var node = new TreeNodeViewModel(descriptor, parent, icon);

        foreach (var child in descriptor.Children)
        {
            var childViewModel = BuildTreeViewModel(child, node);
            node.Children.Add(childViewModel);
        }

        return node;
    }

    /// <summary>
    /// Safely gets directory name without throwing on invalid paths.
    /// </summary>
    private static string GetDirectoryNameSafe(string path)
    {
        try
        {
            return new DirectoryInfo(path).Name;
        }
        catch
        {
            return Path.GetFileName(path) ?? path;
        }
    }

    private void UpdateTitle()
    {
        if (string.IsNullOrWhiteSpace(_currentPath))
        {
            _viewModel.Title = MainWindowViewModel.BaseTitleWithAuthor;
            return;
        }

        // For Git clones: show full URL + branch in square brackets
        // For local folders: show full path
        if (_viewModel.IsGitMode && !string.IsNullOrEmpty(_currentRepositoryUrl))
        {
            var branchDisplay = !string.IsNullOrEmpty(_viewModel.CurrentBranch)
                ? $" [{_viewModel.CurrentBranch}]"
                : string.Empty;
            _viewModel.Title = $"{MainWindowViewModel.BaseTitle} - {_currentRepositoryUrl}{branchDisplay}";
        }
        else
        {
            var displayPath = !string.IsNullOrEmpty(_currentProjectDisplayName)
                ? _currentProjectDisplayName
                : _currentPath;
            _viewModel.Title = $"{MainWindowViewModel.BaseTitle} - {displayPath}";
        }
    }

    private IgnoreRules BuildIgnoreRules(
        string rootPath,
        IReadOnlyCollection<IgnoreOptionId> selectedOptions,
        IReadOnlyCollection<string>? selectedRootFolders)
    {
        return _ignoreRulesService.Build(rootPath, selectedOptions, selectedRootFolders);
    }

    private IgnoreOptionsAvailability GetIgnoreOptionsAvailability(
        string rootPath,
        IReadOnlyCollection<string> selectedRootFolders)
    {
        return _ignoreRulesService.GetIgnoreOptionsAvailability(rootPath, selectedRootFolders);
    }

    private IgnoreRules BuildIgnoreRules(string rootPath)
    {
        var selected = _selectionCoordinator.GetSelectedIgnoreOptionIds();
        var selectedRoots = _selectionCoordinator.GetSelectedRootFolders();
        return BuildIgnoreRules(rootPath, selected, selectedRoots);
    }

    private long BeginStatusOperation(
        string text,
        bool indeterminate = true,
        StatusOperationType operationType = StatusOperationType.None,
        Action? cancelAction = null)
    {
        var operationId = Interlocked.Increment(ref _statusOperationSequence);

        lock (_statusOperationLock)
        {
            _activeStatusOperationId = operationId;
            _activeStatusOperationType = operationType;
            _activeStatusCancelAction = cancelAction;
        }

        _viewModel.StatusOperationText = text;
        _viewModel.StatusBusy = true;
        _viewModel.StatusProgressIsIndeterminate = indeterminate;
        _viewModel.StatusProgressValue = 0;

        return operationId;
    }

    private void UpdateStatusOperationText(string text, long? operationId = null)
    {
        if (operationId.HasValue && !IsStatusOperationActive(operationId.Value))
            return;

        _viewModel.StatusOperationText = text;
    }

    private void UpdateStatusOperationProgress(double percent, string? text = null, long? operationId = null)
    {
        if (operationId.HasValue && !IsStatusOperationActive(operationId.Value))
            return;

        if (!string.IsNullOrWhiteSpace(text))
            _viewModel.StatusOperationText = text;

        _viewModel.StatusBusy = true;
        _viewModel.StatusProgressIsIndeterminate = false;
        _viewModel.StatusProgressValue = Math.Clamp(percent, 0, 100);
    }

    private void CompleteStatusOperation(long? operationId = null)
    {
        if (operationId.HasValue && !IsStatusOperationActive(operationId.Value))
            return;

        StatusOperationType activeOperationType;
        lock (_statusOperationLock)
            activeOperationType = _activeStatusOperationType;

        // Keep progress visible only for the active metrics operation.
        if (_isBackgroundMetricsActive && activeOperationType == StatusOperationType.MetricsCalculation)
        {
            UpdateStatusOperationText(_viewModel.StatusOperationCalculatingData);
            return;
        }

        lock (_statusOperationLock)
        {
            if (operationId.HasValue && _activeStatusOperationId != operationId.Value)
                return;

            _activeStatusOperationId = 0;
            _activeStatusOperationType = StatusOperationType.None;
            _activeStatusCancelAction = null;
        }

        _viewModel.StatusOperationText = string.Empty;
        _viewModel.StatusBusy = false;
        _viewModel.StatusProgressIsIndeterminate = true;
        _viewModel.StatusProgressValue = 0;
    }

    private bool IsStatusOperationActive(long operationId)
    {
        lock (_statusOperationLock)
            return _activeStatusOperationId == operationId;
    }

    private StatusOperationSnapshot GetActiveStatusOperationSnapshot()
    {
        lock (_statusOperationLock)
        {
            return new StatusOperationSnapshot(
                _activeStatusOperationId,
                _activeStatusOperationType,
                _activeStatusCancelAction);
        }
    }

    /// <summary>
    /// Cancels any active background metrics calculation.
    /// Call this before starting user-initiated operations that need the status bar.
    /// </summary>
    private void CancelBackgroundMetricsCalculation()
    {
        if (_isBackgroundMetricsActive)
            _hasCompleteMetricsBaseline = false;

        _isBackgroundMetricsActive = false;
        _metricsCalculationCts?.Cancel();
        _recalculateMetricsCts?.Cancel();
    }

    private ProjectLoadCancellationSnapshot CaptureProjectLoadCancellationSnapshot()
    {
        var hadLoadedProjectBefore = _viewModel.IsProjectLoaded && !string.IsNullOrWhiteSpace(_currentPath);

        return new ProjectLoadCancellationSnapshot(
            HadLoadedProjectBefore: hadLoadedProjectBefore,
            Path: _currentPath,
            ProjectDisplayName: _currentProjectDisplayName,
            RepositoryUrl: _currentRepositoryUrl,
            Tree: _currentTree,
            ProjectSourceType: _viewModel.ProjectSourceType,
            CurrentBranch: _viewModel.CurrentBranch,
            GitBranches: _viewModel.GitBranches.ToArray(),
            SettingsVisible: _viewModel.SettingsVisible,
            SearchVisible: _viewModel.SearchVisible,
            FilterVisible: _viewModel.FilterVisible,
            IsPreviewMode: _viewModel.IsPreviewMode,
            StatusMetricsVisible: _viewModel.StatusMetricsVisible,
            StatusTreeStatsText: _viewModel.StatusTreeStatsText,
            StatusContentStatsText: _viewModel.StatusContentStatsText,
            AllRootFoldersChecked: _viewModel.AllRootFoldersChecked,
            AllExtensionsChecked: _viewModel.AllExtensionsChecked,
            AllIgnoreChecked: _viewModel.AllIgnoreChecked,
            HasCompleteMetricsBaseline: _hasCompleteMetricsBaseline,
            RootFolders: _viewModel.RootFolders
                .Select(option => new SelectionOptionSnapshot(option.Name, option.IsChecked))
                .ToArray(),
            Extensions: _viewModel.Extensions
                .Select(option => new SelectionOptionSnapshot(option.Name, option.IsChecked))
                .ToArray(),
            IgnoreOptions: _viewModel.IgnoreOptions
                .Select(option => new IgnoreOptionSnapshot(option.Id, option.Label, option.IsChecked))
                .ToArray());
    }

    private bool TryApplyActiveProjectLoadCancellationFallback()
    {
        var snapshot = _activeProjectLoadCancellationSnapshot;
        if (snapshot is null)
            return false;

        _activeProjectLoadCancellationSnapshot = null;
        ApplyProjectLoadCancellationFallback(snapshot);
        return true;
    }

    private void ApplyProjectLoadCancellationFallback(ProjectLoadCancellationSnapshot snapshot)
    {
        var fallback = ProjectLoadCancellationFallbackResolver.Resolve(snapshot.HadLoadedProjectBefore);
        if (fallback == ProjectLoadCancellationFallback.ResetToInitialState)
        {
            ResetToInitialProjectStateAfterCancellation();
            return;
        }

        RestorePreviousProjectStateAfterCancellation(snapshot);
    }

    private void RestorePreviousProjectStateAfterCancellation(ProjectLoadCancellationSnapshot snapshot)
    {
        _currentPath = snapshot.Path;
        _currentProjectDisplayName = snapshot.ProjectDisplayName;
        _currentRepositoryUrl = snapshot.RepositoryUrl;
        _currentTree = snapshot.Tree;

        _viewModel.IsProjectLoaded = true;
        _viewModel.SettingsVisible = snapshot.SettingsVisible;
        _viewModel.SearchVisible = snapshot.SearchVisible;
        _viewModel.FilterVisible = snapshot.FilterVisible;
        _viewModel.IsPreviewMode = snapshot.IsPreviewMode;
        _viewModel.StatusMetricsVisible = snapshot.StatusMetricsVisible;
        _viewModel.StatusTreeStatsText = snapshot.StatusTreeStatsText;
        _viewModel.StatusContentStatsText = snapshot.StatusContentStatsText;

        _viewModel.ProjectSourceType = snapshot.ProjectSourceType;
        _viewModel.CurrentBranch = snapshot.CurrentBranch;
        _viewModel.GitBranches.Clear();
        foreach (var branch in snapshot.GitBranches)
            _viewModel.GitBranches.Add(branch);

        _viewModel.RootFolders.Clear();
        foreach (var option in snapshot.RootFolders)
            _viewModel.RootFolders.Add(new SelectionOptionViewModel(option.Name, option.IsChecked));

        _viewModel.Extensions.Clear();
        foreach (var option in snapshot.Extensions)
            _viewModel.Extensions.Add(new SelectionOptionViewModel(option.Name, option.IsChecked));

        _viewModel.IgnoreOptions.Clear();
        foreach (var option in snapshot.IgnoreOptions)
            _viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(option.Id, option.Label, option.IsChecked));

        _viewModel.AllRootFoldersChecked = snapshot.AllRootFoldersChecked;
        _viewModel.AllExtensionsChecked = snapshot.AllExtensionsChecked;
        _viewModel.AllIgnoreChecked = snapshot.AllIgnoreChecked;
        _hasCompleteMetricsBaseline = snapshot.HasCompleteMetricsBaseline;
        SyncSearchAndFilterVisualStateFromFlags();

        if (_viewModel.TreeNodes.Count == 0 && snapshot.Tree is not null && !string.IsNullOrWhiteSpace(snapshot.Path))
        {
            var displayName = !string.IsNullOrEmpty(snapshot.ProjectDisplayName)
                ? snapshot.ProjectDisplayName
                : GetDirectoryNameSafe(snapshot.Path);

            var rootNode = BuildTreeViewModel(snapshot.Tree.Root, null);
            rootNode.DisplayName = displayName;
            rootNode.IsExpanded = true;
            _viewModel.TreeNodes.Add(rootNode);
        }

        UpdateBranchMenu();
        UpdateTitle();
    }

    private static CancellationTokenSource ReplaceCancellationSource(ref CancellationTokenSource? target)
    {
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref target, cts);
        previous?.Cancel();
        previous?.Dispose();
        return cts;
    }

    private static void DisposeIfCurrent(ref CancellationTokenSource? target, CancellationTokenSource candidate)
    {
        var observed = Interlocked.CompareExchange(ref target, null, candidate);
        if (ReferenceEquals(observed, candidate))
        {
            candidate.Dispose();
        }
    }

    private void ResetToInitialProjectStateAfterCancellation()
    {
        _activeProjectLoadCancellationSnapshot = null;
        CancelBackgroundMetricsCalculation();
        CancelPreviewRefresh();
        ClearPreviousProjectState();

        _currentPath = null;
        _currentTree = null;
        _currentProjectDisplayName = null;
        _currentRepositoryUrl = null;
        _filterExpansionSnapshot = null;

        _viewModel.IsProjectLoaded = false;
        _viewModel.SettingsVisible = false;
        _viewModel.SearchVisible = false;
        _viewModel.FilterVisible = false;
        _viewModel.IsPreviewMode = false;
        _viewModel.StatusMetricsVisible = false;
        _viewModel.ProjectSourceType = ProjectSourceType.LocalFolder;
        _viewModel.CurrentBranch = string.Empty;
        _viewModel.GitBranches.Clear();
        _viewModel.RootFolders.Clear();
        _viewModel.Extensions.Clear();
        _viewModel.IgnoreOptions.Clear();
        UpdateBranchMenu();

        UpdateStatusBarMetrics(0, 0, 0, 0, 0, 0);
        UpdateTitle();
    }


    private static bool TryParseTrailingPercent(string status, out double percent)
    {
        percent = 0;
        if (string.IsNullOrWhiteSpace(status))
            return false;

        var trimmed = status.Trim();
        if (!trimmed.EndsWith('%'))
            return false;

        var lastSpace = trimmed.LastIndexOf(' ');
        var token = lastSpace >= 0 ? trimmed[(lastSpace + 1)..] : trimmed;
        token = token.TrimEnd('%');

        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out percent) ||
               double.TryParse(token, NumberStyles.Float, CultureInfo.CurrentCulture, out percent);
    }

    private async Task SetClipboardTextAsync(string content)
    {
        var clipboard = GetTopLevel(this)?.Clipboard;

        if (clipboard != null)
            await clipboard.SetTextAsync(content);
    }

    private static void OpenRepositoryLink()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = ProjectLinks.RepositoryUrl,
            UseShellExecute = true
        });
    }

    private bool EnsureTreeReady() => _currentTree is not null && !string.IsNullOrWhiteSpace(_currentPath);

    private static HashSet<string> CollectCheckedOptionNames(IEnumerable<SelectionOptionViewModel> options)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in options)
        {
            if (option.IsChecked)
                selected.Add(option.Name);
        }

        return selected;
    }

    private HashSet<string> GetCheckedPaths()
    {
        var selected = new HashSet<string>(PathComparer.Default);
        foreach (var node in _viewModel.TreeNodes)
            CollectChecked(node, selected);
        return selected;
    }

    private List<string> BuildOrderedUniqueFilePaths(IReadOnlySet<string> selectedPaths)
    {
        var uniquePaths = new HashSet<string>(PathComparer.Default);
        if (selectedPaths.Count > 0)
        {
            foreach (var path in selectedPaths)
            {
                if (File.Exists(path))
                    uniquePaths.Add(path);
            }
        }
        else if (_currentTree is not null)
        {
            foreach (var path in EnumerateFilePaths(_currentTree.Root))
                uniquePaths.Add(path);
        }

        if (uniquePaths.Count == 0)
            return [];

        var ordered = new List<string>(uniquePaths.Count);
        ordered.AddRange(uniquePaths);
        ordered.Sort(PathComparer.Default);
        return ordered;
    }

    private static IEnumerable<string> EnumerateFilePaths(TreeNodeDescriptor node)
    {
        if (!node.IsDirectory)
        {
            yield return node.FullPath;
            yield break;
        }

        foreach (var child in node.Children)
        {
            foreach (var path in EnumerateFilePaths(child))
                yield return path;
        }
    }

    private static void CollectChecked(TreeNodeViewModel node, HashSet<string> selected)
    {
        if (node.IsChecked == true)
            selected.Add(node.FullPath);

        foreach (var child in node.Children)
            CollectChecked(child, selected);
    }

    private HashSet<string> CaptureExpandedNodes()
    {
        var result = new HashSet<string>(PathComparer.Default);
        TreeNodeViewModel.ForEachDescendant(_viewModel.TreeNodes, node =>
        {
            if (node.IsExpanded)
                result.Add(node.FullPath);
        });
        return result;
    }

    private void RestoreExpandedNodes(HashSet<string> expandedPaths)
    {
        TreeNodeViewModel.ForEachDescendant(_viewModel.TreeNodes, node =>
            node.IsExpanded = expandedPaths.Contains(node.FullPath));

        if (_viewModel.TreeNodes.FirstOrDefault() is { } root && !root.IsExpanded)
            root.IsExpanded = true;
    }

    /// <summary>
    /// Validates that URL looks like a valid Git repository URL.
    /// Accepts URLs from common Git hosting services (GitHub, GitLab, Bitbucket, etc.)
    /// or any URL ending with .git
    /// </summary>
    private static bool IsValidGitRepositoryUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        try
        {
            // Try to parse as URI
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;

            // Must be HTTP or HTTPS
            if (uri.Scheme != "http" && uri.Scheme != "https")
                return false;

            var host = uri.Host.ToLowerInvariant();
            var path = uri.AbsolutePath.ToLowerInvariant();

            // Check for common Git hosting services
            var validHosts = new[]
            {
                "github.com",
                "gitlab.com",
                "bitbucket.org",
                "gitea.com",
                "codeberg.org",
                "sourceforge.net",
                "git.sr.ht"
            };

            // Allow subdomains (e.g., gitlab.mycompany.com)
            var isKnownHost = validHosts.Any(h => host == h || host.EndsWith("." + h));

            // Or URL ends with .git extension
            var hasGitExtension = path.EndsWith(".git");

            // Or contains /git/ in path (common for self-hosted instances)
            var hasGitInPath = path.Contains("/git/");

            return isKnownHost || hasGitExtension || hasGitInPath;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if internet connection is available by attempting to connect to reliable hosts.
    /// Returns true if connection successful, false otherwise.
    /// This is a simple check - we try to resolve DNS and connect to well-known hosts.
    /// </summary>
    private static async Task<bool> CheckInternetConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Try to connect to multiple reliable hosts to avoid false negatives
            // Use different providers to increase reliability
            var hosts = new[]
            {
                "https://www.github.com",
                "https://www.google.com",
                "https://www.cloudflare.com"
            };

            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(5)
            };

            // Try each host - if any succeeds, we have internet
            foreach (var host in hosts)
            {
                try
                {
                    using var response = await httpClient.GetAsync(host, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    // If we get any response (even error status codes), it means we have connectivity
                    return true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Try next host
                    continue;
                }
            }

            // All hosts failed
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // If exception occurs, assume no internet
            return false;
        }
    }

    #region Real-time Status Metrics

    /// <summary>
    /// Cached file metrics for efficient real-time updates.
    /// </summary>
    private readonly record struct FileMetricsData(
        long Size,
        int LineCount,
        int CharCount,
        bool IsEmpty,
        bool IsWhitespaceOnly,
        int TrailingNewlineChars,
        int TrailingNewlineLineBreaks);

    /// <summary>
    /// Subscribe to checkbox change events for real-time metrics updates.
    /// </summary>
    private void SubscribeToMetricsUpdates()
    {
        TreeNodeViewModel.GlobalCheckedChanged += OnTreeNodeCheckedChanged;
    }

    /// <summary>
    /// Unsubscribe from checkbox change events.
    /// </summary>
    private void UnsubscribeFromMetricsUpdates()
    {
        TreeNodeViewModel.GlobalCheckedChanged -= OnTreeNodeCheckedChanged;
    }

    /// <summary>
    /// Handle checkbox change with debouncing to avoid excessive recalculations.
    /// </summary>
    private void OnTreeNodeCheckedChanged(object? sender, EventArgs e)
    {
        // Debounce rapid checkbox changes (e.g., when selecting parent node)
        // Reuse existing timer to prevent memory leaks from accumulating timer instances
        if (_metricsDebounceTimer is null)
        {
            _metricsDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _metricsDebounceTimer.Tick += OnMetricsDebounceTimerTick;
        }

        _metricsDebounceTimer.Stop();
        _metricsDebounceTimer.Start();

        InvalidatePreviewCache();
        SchedulePreviewRefresh();
    }

    /// <summary>
    /// Handler for metrics debounce timer tick. Separated to avoid lambda capture leaks.
    /// </summary>
    private void OnMetricsDebounceTimerTick(object? sender, EventArgs e)
    {
        _metricsDebounceTimer?.Stop();
        RecalculateMetricsAsync();
    }

    /// <summary>
    /// Initialize file metrics cache after tree is built.
    /// Scans all files in parallel using IFileContentAnalyzer as single source of truth.
    /// Binary files are skipped via extension check (fast) or null-byte detection.
    /// </summary>
    private async Task InitializeFileMetricsCacheAsync(CancellationToken cancellationToken)
    {
        // Cancel any previous calculation
        var metricsCts = ReplaceCancellationSource(ref _metricsCalculationCts);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, metricsCts.Token);

        _metricsCancellationRequestedByUser = false;
        _hasCompleteMetricsBaseline = false;
        _isBackgroundMetricsActive = true;
        var statusOperationId = BeginStatusOperation(
            _viewModel.StatusOperationCalculatingData,
            indeterminate: false,
            operationType: StatusOperationType.MetricsCalculation,
            cancelAction: CancelBackgroundMetricsCalculation);
        try
        {
            if (IsStatusOperationActive(statusOperationId))
                _viewModel.StatusProgressValue = 0;

            // Collect all file paths from tree
            var filePaths = new List<string>();
            TreeNodeViewModel.ForEachDescendant(_viewModel.TreeNodes, node =>
            {
                if (!node.Descriptor.IsDirectory && !node.Descriptor.IsAccessDenied)
                    filePaths.Add(node.FullPath);
            });

            // Clear cache before scanning
            ClearFileMetricsCache(trimCapacity: true);

            var totalFiles = filePaths.Count;
            if (totalFiles == 0)
            {
                _isBackgroundMetricsActive = false;
                _hasCompleteMetricsBaseline = true;
                RecalculateMetricsAsync();
                _viewModel.StatusMetricsVisible = true;
                CompleteStatusOperation(statusOperationId);
                return;
            }

            // Skip warmup scan for obvious binary-only datasets (e.g. screenshot folders).
            if (filePaths.TrueForAll(IsDefinitelyBinaryByExtensionForMetricsWarmup))
            {
                _isBackgroundMetricsActive = false;
                _hasCompleteMetricsBaseline = true;
                if (IsStatusOperationActive(statusOperationId))
                    _viewModel.StatusProgressValue = 100;
                RecalculateMetricsAsync();
                _viewModel.StatusMetricsVisible = true;
                CompleteStatusOperation(statusOperationId);
                return;
            }

            var processedCount = 0;
            var lastProgressPercent = 0;

            // Process files in parallel for better performance on modern multi-core CPUs with NVMe SSDs.
            // Using full processor count as modern storage can handle high parallelism.
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount),
                CancellationToken = linkedCts.Token
            };

            await Parallel.ForEachAsync(filePaths, parallelOptions, async (filePath, ct) =>
            {
                try
                {
                    // GetTextFileMetricsAsync uses streaming - no full content in memory:
                    // 1. Known binary extensions - instant skip (no I/O)
                    // 2. Null-byte detection in first 512 bytes (fast binary check)
                    // 3. Streams through file counting lines/chars without storing content
                    // 4. Large files (>10MB) get estimated metrics
                    var metrics = await _fileContentAnalyzer.GetTextFileMetricsAsync(filePath, ct)
                        .ConfigureAwait(false);

                    // Skip binary files - they won't be exported
                    if (metrics is not null)
                    {
                        lock (_metricsLock)
                        {
                            _fileMetricsCache[filePath] = new FileMetricsData(
                                metrics.SizeBytes,
                                metrics.LineCount,
                                metrics.CharCount,
                                metrics.IsEmpty,
                                metrics.IsWhitespaceOnly,
                                metrics.TrailingNewlineChars,
                                metrics.TrailingNewlineLineBreaks);
                        }
                    }

                    // Update progress periodically (every 5%) to reduce UI dispatch pressure.
                    var current = Interlocked.Increment(ref processedCount);
                    var progressPercent = (int)(current * 100.0 / totalFiles);
                    var observed = Volatile.Read(ref lastProgressPercent);
                    if (progressPercent >= observed + 5 &&
                        Interlocked.CompareExchange(ref lastProgressPercent, progressPercent, observed) == observed)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (_isBackgroundMetricsActive && IsStatusOperationActive(statusOperationId))
                                _viewModel.StatusProgressValue = progressPercent;
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Skip files that can't be read
                    Interlocked.Increment(ref processedCount);
                }
            });

            // Calculation completed successfully
            _isBackgroundMetricsActive = false;
            _hasCompleteMetricsBaseline = true;
            if (IsStatusOperationActive(statusOperationId))
                _viewModel.StatusProgressValue = 100;
            RecalculateMetricsAsync();
            _viewModel.StatusMetricsVisible = true;
            CompleteStatusOperation(statusOperationId);
        }
        catch (OperationCanceledException)
        {
            // Show explicit fallback for user-initiated cancellation.
            _isBackgroundMetricsActive = false;
            _hasCompleteMetricsBaseline = false;
            var hasCachedMetrics = false;
            lock (_metricsLock)
                hasCachedMetrics = _fileMetricsCache.Count > 0;
            if (_metricsCancellationRequestedByUser)
            {
                _metricsCancellationRequestedByUser = false;
                UpdateStatusBarMetrics(0, 0, 0, 0, 0, 0);
                _viewModel.StatusMetricsVisible = true;
            }
            else if (hasCachedMetrics)
            {
                RecalculateMetricsAsync();
                _viewModel.StatusMetricsVisible = true;
            }
            CompleteStatusOperation(statusOperationId);
        }
        finally
        {
            DisposeIfCurrent(ref _metricsCalculationCts, metricsCts);
        }
    }

    /// <summary>
    /// Recalculate both tree and content metrics based on current selection.
    /// Calculations run in parallel on background threads for better performance.
    /// Cancels any previous calculation to avoid stale updates and wasted CPU.
    /// </summary>
    private void RecalculateMetricsAsync()
    {
        if (!_viewModel.IsProjectLoaded || _viewModel.TreeNodes.Count == 0)
        {
            UpdateStatusBarMetrics(0, 0, 0, 0, 0, 0);
            return;
        }

        // Cancel previous calculation to avoid wasted CPU and stale updates
        var recalcCts = ReplaceCancellationSource(ref _recalculateMetricsCts);
        var token = recalcCts.Token;

        var recalcVersion = Interlocked.Increment(ref _metricsRecalcVersion);
        var treeRoot = _viewModel.TreeNodes.FirstOrDefault();
        if (treeRoot == null)
        {
            DisposeIfCurrent(ref _recalculateMetricsCts, recalcCts);
            return;
        }

        // Capture state for background calculation
        var hasAnyChecked = HasAnyCheckedNodes(treeRoot);
        var hasCompleteMetricsBaseline = _hasCompleteMetricsBaseline;
        if (!ShouldProceedWithMetricsCalculation(hasAnyChecked, hasCompleteMetricsBaseline))
        {
            UpdateStatusBarMetrics(0, 0, 0, 0, 0, 0);
            DisposeIfCurrent(ref _recalculateMetricsCts, recalcCts);
            return;
        }

        var selectedPaths = hasAnyChecked
            ? GetCheckedPaths()
            : new HashSet<string>(PathComparer.Default);
        var treeFormat = GetCurrentTreeTextFormat();
        var currentTree = _currentTree;
        var currentPath = _currentPath;

        // Run calculations in parallel on background threads without blocking waits.
        _ = RecalculateMetricsCoreAsync(
            recalcCts,
            token,
            recalcVersion,
            hasAnyChecked,
            selectedPaths,
            treeFormat,
            currentTree,
            currentPath);
    }

    private async Task RecalculateMetricsCoreAsync(
        CancellationTokenSource recalcCts,
        CancellationToken token,
        int recalcVersion,
        bool hasAnyChecked,
        IReadOnlySet<string> selectedPaths,
        TreeTextFormat treeFormat,
        BuildTreeResult? currentTree,
        string? currentPath)
    {
        try
        {
            // Early exit if cancelled before starting.
            if (token.IsCancellationRequested)
                return;

            if (currentTree is null || string.IsNullOrWhiteSpace(currentPath))
            {
                await Dispatcher.UIThread.InvokeAsync(
                    () => UpdateStatusBarMetrics(0, 0, 0, 0, 0, 0));
                return;
            }

            // Check cancellation before starting heavy calculations.
            if (token.IsCancellationRequested)
                return;

            // Calculate tree and content metrics in parallel.
            var treeMetricsTask = Task.Run(() => CalculateTreeMetrics(hasAnyChecked, selectedPaths, treeFormat), token);
            var contentMetricsTask = Task.Run(() => CalculateContentMetrics(hasAnyChecked, selectedPaths), token);

            try
            {
                await Task.WhenAll(treeMetricsTask, contentMetricsTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Check cancellation after calculations complete.
            if (token.IsCancellationRequested)
                return;

            var treeMetrics = treeMetricsTask.Result;
            var contentMetrics = contentMetricsTask.Result;

            // Update UI on dispatcher thread.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Double-check: version must match AND not cancelled.
                if (token.IsCancellationRequested || recalcVersion != Volatile.Read(ref _metricsRecalcVersion))
                    return;

                UpdateStatusBarMetrics(
                    treeMetrics.Lines, treeMetrics.Chars, treeMetrics.Tokens,
                    contentMetrics.Lines, contentMetrics.Chars, contentMetrics.Tokens);
            });
        }
        catch (OperationCanceledException)
        {
            // Expected when a newer recalculation supersedes the current one.
        }
        finally
        {
            DisposeIfCurrent(ref _recalculateMetricsCts, recalcCts);
        }
    }

    private void ClearFileMetricsCache(bool trimCapacity)
    {
        lock (_metricsLock)
        {
            _fileMetricsCache.Clear();
            if (trimCapacity)
                _fileMetricsCache.TrimExcess();
        }
    }

    /// <summary>
    /// Check if any node in the tree is explicitly checked.
    /// </summary>
    private static bool HasAnyCheckedNodes(TreeNodeViewModel root)
    {
        if (root.IsChecked == true)
            return true;

        foreach (var child in root.Children)
        {
            if (HasAnyCheckedNodes(child))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Full metrics require a completed baseline calculation.
    /// Selected metrics can be calculated independently.
    /// </summary>
    private static bool ShouldProceedWithMetricsCalculation(bool hasAnyCheckedNodes, bool hasCompleteMetricsBaseline) =>
        hasAnyCheckedNodes || hasCompleteMetricsBaseline;

    /// <summary>
    /// Calculates tree metrics from actual tree export output in the currently selected format.
    /// This keeps status metrics aligned with copy/export output.
    /// </summary>
    private ExportOutputMetrics CalculateTreeMetrics(
        bool hasSelection,
        IReadOnlySet<string> selectedPaths,
        TreeTextFormat format)
    {
        if (_currentTree is null || string.IsNullOrWhiteSpace(_currentPath))
            return ExportOutputMetrics.Empty;

        var treeText = BuildTreeTextForSelection(selectedPaths, format);

        return ExportOutputMetricsCalculator.FromText(treeText);
    }

    /// <summary>
    /// Calculates content metrics using the same text shaping rules as content export.
    /// File I/O is avoided by using cached metrics prepared in background.
    /// </summary>
    private ExportOutputMetrics CalculateContentMetrics(bool hasSelection, IReadOnlySet<string> selectedPaths)
    {
        if (_currentTree is null)
            return ExportOutputMetrics.Empty;

        var uniquePaths = new HashSet<string>(PathComparer.Default);
        if (hasSelection)
        {
            foreach (var path in selectedPaths)
            {
                if (File.Exists(path))
                    uniquePaths.Add(path);
            }
        }
        else
        {
            foreach (var path in EnumerateFilePaths(_currentTree.Root))
                uniquePaths.Add(path);
        }

        if (uniquePaths.Count == 0)
            return ExportOutputMetrics.Empty;

        var orderedPaths = new List<string>(uniquePaths.Count);
        orderedPaths.AddRange(uniquePaths);
        orderedPaths.Sort(PathComparer.Default);

        var metricsInputs = new List<ContentFileMetrics>(orderedPaths.Count);
        lock (_metricsLock)
        {
            foreach (var path in orderedPaths)
            {
                if (!_fileMetricsCache.TryGetValue(path, out var metrics))
                    continue;

                metricsInputs.Add(new ContentFileMetrics(
                    Path: path,
                    SizeBytes: metrics.Size,
                    LineCount: metrics.LineCount,
                    CharCount: metrics.CharCount,
                    IsEmpty: metrics.IsEmpty,
                    IsWhitespaceOnly: metrics.IsWhitespaceOnly,
                    TrailingNewlineChars: metrics.TrailingNewlineChars,
                    TrailingNewlineLineBreaks: metrics.TrailingNewlineLineBreaks));
            }
        }

        return ExportOutputMetricsCalculator.FromContentFiles(metricsInputs);
    }

    /// <summary>
    /// Update status bar with calculated metrics.
    /// </summary>
    private void UpdateStatusBarMetrics(
        int treeLines, int treeChars, int treeTokens,
        int contentLines, int contentChars, int contentTokens)
    {
        _lastStatusTreeLines = treeLines;
        _lastStatusTreeChars = treeChars;
        _lastStatusTreeTokens = treeTokens;
        _lastStatusContentLines = contentLines;
        _lastStatusContentChars = contentChars;
        _lastStatusContentTokens = contentTokens;
        _hasStatusMetricsSnapshot = true;
        RenderStatusBarMetrics();
    }

    private void RenderStatusBarMetrics()
    {
        // Format: [Lines: X | Chars: X | ~Tokens: X]
        var linesLabel = _localization.Format("Status.Metric.Lines", "{0}");
        var charsLabel = _localization.Format("Status.Metric.Chars", "{0}");
        var tokensLabel = _localization.Format("Status.Metric.Tokens", "{0}");

        // Extract format pattern (e.g., "Lines: {0}" -> "Lines:")
        var linesPrefix = linesLabel.Replace("{0}", "").Trim();
        var charsPrefix = charsLabel.Replace("{0}", "").Trim();
        var tokensPrefix = tokensLabel.Replace("{0}", "").Trim();

        var useCompactMetrics = Bounds.Width > 0 && Bounds.Width <= CompactStatusMetricsThresholdWidth;
        if (useCompactMetrics)
        {
            _viewModel.StatusTreeStatsText = $"[{linesPrefix} {FormatNumber(_lastStatusTreeLines)}]";
            _viewModel.StatusContentStatsText = $"[{linesPrefix} {FormatNumber(_lastStatusContentLines)}]";
            return;
        }

        _viewModel.StatusTreeStatsText = $"[{linesPrefix} {FormatNumber(_lastStatusTreeLines)} | {charsPrefix} {FormatNumber(_lastStatusTreeChars)} | {tokensPrefix} {FormatNumber(_lastStatusTreeTokens)}]";
        _viewModel.StatusContentStatsText = $"[{linesPrefix} {FormatNumber(_lastStatusContentLines)} | {charsPrefix} {FormatNumber(_lastStatusContentChars)} | {tokensPrefix} {FormatNumber(_lastStatusContentTokens)}]";
    }

    /// <summary>
    /// Format large numbers with K/M suffixes for readability.
    /// </summary>
    private static string FormatNumber(int value)
    {
        return value switch
        {
            >= 1_000_000 => $"{value / 1_000_000.0:F1}M",
            >= 10_000 => $"{value / 1_000.0:F1}K",
            _ => value.ToString("N0")
        };
    }

    private static bool IsDefinitelyBinaryByExtensionForMetricsWarmup(string path)
    {
        var extension = Path.GetExtension(path);
        return !string.IsNullOrWhiteSpace(extension) && MetricsWarmupBinaryExtensions.Contains(extension);
    }

    private void OnStatusOperationCancelRequested(object? sender, RoutedEventArgs e)
    {
        var activeOperation = GetActiveStatusOperationSnapshot();
        var activeOperationId = activeOperation.OperationId;
        var activeOperationType = activeOperation.OperationType;

        // Primary cancellation path for the currently visible status operation.
        try
        {
            activeOperation.CancelAction?.Invoke();
        }
        catch
        {
            // Ignore cancellation callback errors and continue with fallback logic.
        }

        // Scoped fallback path: cancel only the currently active operation family.
        switch (activeOperationType)
        {
            case StatusOperationType.LoadProject:
            case StatusOperationType.RefreshProject:
                _projectOperationCts?.Cancel();
                _refreshCts?.Cancel();
                break;
            case StatusOperationType.GitPullUpdates:
            case StatusOperationType.GitSwitchBranch:
                _gitOperationCts?.Cancel();
                break;
            case StatusOperationType.PreviewBuild:
                _previewBuildCts?.Cancel();
                break;
            case StatusOperationType.MetricsCalculation:
                // Metrics cancellation is handled below by dedicated fallback logic.
                break;
            case StatusOperationType.None:
            default:
                break;
        }

        if (activeOperationType == StatusOperationType.MetricsCalculation)
        {
            _metricsCancellationRequestedByUser = true;
            _hasCompleteMetricsBaseline = false;
            CancelBackgroundMetricsCalculation();
            UpdateStatusBarMetrics(0, 0, 0, 0, 0, 0);
            _viewModel.StatusMetricsVisible = _viewModel.IsProjectLoaded;
            _toastService.Show(_localization["Toast.Operation.MetricsCanceled"]);
        }

        if (activeOperationType == StatusOperationType.LoadProject)
        {
            if (TryApplyActiveProjectLoadCancellationFallback())
                _toastService.Show(_localization["Toast.Operation.LoadCanceled"]);
        }

        // Cancel preview build if in progress
        if (_viewModel.IsPreviewLoading || activeOperationType == StatusOperationType.PreviewBuild)
        {
            _previewBuildCts?.Cancel();
            _viewModel.IsPreviewLoading = false;
            _toastService.Show(_viewModel.ToastPreviewCanceled);
        }

        CompleteStatusOperation(activeOperationId);
    }

    #endregion

}
