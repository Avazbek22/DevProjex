using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using DevProjex.Application;
using DevProjex.Avalonia.Controls;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Avalonia.Services;
using DevProjex.Avalonia.Views;
using DevProjex.Kernel;
using DevProjex.Infrastructure.RecentProjects;
using DevProjex.Infrastructure.Reports;
using DevProjex.Infrastructure.TerminalCommands;
using UserSettingsStore = DevProjex.Infrastructure.ThemePresets.UserSettingsStore;
using UserSettingsDb = DevProjex.Infrastructure.ThemePresets.UserSettingsDb;
using ThemeSettingsStore = DevProjex.Infrastructure.ThemePresets.ThemeSettingsStore;
using ThemeSettingsDocument = DevProjex.Infrastructure.ThemePresets.ThemeSettingsDocument;
using ThemePreset = DevProjex.Infrastructure.ThemePresets.ThemePreset;
using ThemePresetVariant = DevProjex.Infrastructure.ThemePresets.ThemeVariant;
using ThemePresetEffect = DevProjex.Infrastructure.ThemePresets.ThemeEffectMode;
using ThemePresetSession = DevProjex.Infrastructure.ThemePresets.ThemePresetSession;
using AppViewSettings = DevProjex.Infrastructure.ThemePresets.AppViewSettings;

namespace DevProjex.Avalonia;

public partial class MainWindow : Window
{
    private const double BranchMenuItemHeight = 32;
    private const double TreeFontMenuItemHeight = 32;

    private enum WorkspaceDisplayMode
    {
        Tree = 0,
        PreviewWithTree = 1,
        PreviewOnly = 2
    }

    internal enum TerminalCommandPostInstallUiAction
    {
        None,
        ShowError
    }

    internal enum AutomaticTerminalCommandStartupAction
    {
        None,
        ShowPrompt,
        RepairSilently
    }

    private enum ZoomSurfaceTarget
    {
        None = 0,
        Tree = 1,
        Preview = 2
    }

    private enum WorkspaceResizeTarget
    {
        None = 0,
        TreePreview = 1,
        PreviewSettings = 2
    }

    private enum PreviewToolbarLayoutMode
    {
        Wide = 0,
        Compact = 1,
        Narrow = 2
    }

    private enum SuspendedTreeToolMode
    {
        None = 0,
        Search = 1,
        Filter = 2
    }

    private readonly record struct PreviewSelectionMetricsSnapshot(
        IPreviewTextDocument Document,
        PreviewSelectionRange SelectionRange);

    private static async Task YieldUiAsync(DispatcherPriority priority)
        => await DispatcherTaskSchedulerProvider.YieldAsync(priority);

#if DEVPROJEX_PROJECT_LOAD_TIMING
    private sealed class ProjectLoadTiming
    {
        public Stopwatch LoadingStopwatch { get; } = Stopwatch.StartNew();
        public TimeSpan LoadingElapsed { get; set; }
        public bool HasLoadingElapsed { get; set; }
    }
#endif

    public MainWindow()
        : this(CommandLineOptions.Empty, AvaloniaCompositionRoot.CreateDefault(CommandLineOptions.Empty))
    {
    }

    private readonly CommandLineOptions _startupOptions;
    private const string TreeItemPaddingResourceKey = "TreeItemPaddingResource";
    private const string TreeItemSpacingResourceKey = "TreeItemSpacingResource";
    private const string TreeIconSizeResourceKey = "TreeIconSizeResource";
    private const string TreeTextMarginResourceKey = "TreeTextMarginResource";
    private readonly LocalizationService _localization;
    private readonly ScanOptionsUseCase _scanOptions;
    private readonly BuildTreeUseCase _buildTree;
    private readonly IgnoreOptionsService _ignoreOptionsService;
    private readonly IgnoreRulesService _ignoreRulesService;
    private readonly FilterOptionSelectionService _filterSelectionService;
    private readonly TreeExportService _treeExport;
    private readonly SelectedContentExportService _contentExport;
    private readonly TreeAndContentExportService _treeAndContentExport;
    private readonly ProjectCopyExportService _projectCopyExport;
    private readonly PreviewDocumentBuilder _previewDocumentBuilder;
    private readonly RepositoryWebPathPresentationService _repositoryWebPathPresentationService;
    private readonly TextFileExportService _textFileExport;
    private readonly IToastService _toastService;
    private readonly IconCache _iconCache;
    private readonly IElevationService _elevation;
    private readonly IAppInstanceLauncher _appInstanceLauncher;
    private readonly UserSettingsStore _userSettingsStore;
    private readonly ThemeSettingsStore _themeSettingsStore;
    private readonly IGitRepositoryService _gitService;
    private readonly IRepoCacheService _repoCacheService;
    private readonly IZipDownloadService _zipDownloadService;
    private readonly RecentProjectsStore _recentProjectsStore;
    private readonly RecentFolderAvailabilityService _recentFolderAvailabilityService;
    private readonly HashSet<string> _unavailableRecentFolderPaths = new(PathComparer.Default);

    private readonly MainWindowViewModel _viewModel;
    private readonly TreeSearchCoordinator _searchCoordinator;
    private readonly NameFilterCoordinator _filterCoordinator;
    private readonly ThemeBrushCoordinator _themeBrushCoordinator;
    private readonly bool _isMicaSupported = ThemeEffectPlatformSupport.IsMicaSupportedOnCurrentPlatform();
    private readonly SelectionSyncCoordinator _selectionCoordinator;
    private readonly StatusOperationCoordinator _statusOperations;
    private readonly MetricsPipeline _metrics;
    private readonly ProjectLoadPipeline _projectLoadPipeline;
    private readonly ProjectLoadSnapshotPipeline _projectLoadSnapshotPipeline;
    private readonly PreviewWorkspacePipeline _previewPipeline;
    private readonly RefreshTreePipeline _refreshPipeline;
    private readonly ProjectProfilePersistenceCoordinator _projectProfiles;
    private readonly ProjectLoadCancellationCoordinator _projectLoadCancellation = new();
    private readonly TaskbarProgressCoordinator _taskbarProgress;

    private BuildTreeResult? _currentTree;
    private BuildTreeResult? _filterBaseTree;
    private ProjectTreeInventoryState? _currentTreeInventory;
    private TreeNodeDescriptor? _lastInteractiveFilteredRoot;
    private TreeNodeDescriptor? _lastInteractiveFilterBaseRoot;
    private string? _lastInteractiveFilterQuery;
    private bool _lastInteractiveFilterUsedInMemory;
    private readonly Dictionary<string, TreeNodeDescriptor> _interactiveFilterQueryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _interactiveFilterQueryCacheLru = [];
    private readonly Dictionary<string, LinkedListNode<string>> _interactiveFilterQueryCacheNodes = new(StringComparer.OrdinalIgnoreCase);
    private const int InteractiveFilterQueryCacheLimit = 8;
    // Advanced ignore counts are always part of the ignore-options UX now. The old
    // persisted toggle is normalized to true so legacy settings cannot hide counts.
    private const bool AdvancedIgnoreCountsAlwaysEnabled = true;
    private string? _currentPath;
    private string? _currentProjectDisplayName;
    private string? _currentRepositoryUrl;
    private string? _cachedPathPresentationProjectPath;
    private string? _cachedPathPresentationRepositoryUrl;
    private ExportPathPresentation? _cachedPathPresentation;
    private bool _elevationAttempted;
    private bool _wasThemePopoverOpen;
    private bool _themeEffectRuntimeProbeReady;
    private int _applyingThemePresetDepth;
    private bool _awaitingSystemDialogActivation;
    private TaskCompletionSource<bool>? _systemDialogActivationTcs;
    private UserSettingsDb _userSettingsDb = new();
    private ThemeSettingsDocument _themeSettingsDocument = new();
    private ThemePresetSession? _themePresetSession;
    private ThemePresetVariant _currentThemeVariant = ThemePresetVariant.Dark;
    private ThemePresetEffect _currentEffectMode = ThemePresetEffect.Transparent;

    private TreeView? _treeView;
    private TopMenuBarView? _topMenuBar;
    private Grid? _workspaceGrid;
    private Grid? _treePaneRoot;
    private Border? _treePaneContainer;
    private Border? _treePaneSnapshotHost;
    private Image? _treePaneSnapshotImage;
    private TranslateTransform? _treePaneSnapshotTransform;
    private RenderTargetBitmap? _treePaneSnapshotBitmap;
    private Grid? _previewPaneRoot;
    private Border? _previewPaneContainer;
    private Border? _previewPaneSnapshotHost;
    private Image? _previewPaneSnapshotImage;
    private RenderTargetBitmap? _previewPaneSnapshotBitmap;
    private ColumnDefinition? _treePaneColumn;
    private ColumnDefinition? _treePreviewSplitterColumn;
    private ColumnDefinition? _previewPaneColumn;
    private ColumnDefinition? _previewSettingsSplitterColumn;
    private Border? _treePreviewSplitter;
    private Border? _previewSettingsSplitter;
    private Border? _treeIsland;
    private Border? _previewIsland;
    private Border? _previewLineNumbersBackground;
    private Border? _previewStickyHeaderCap;
    private Border? _previewStickyHeaderContainer;
    private TextBlock? _previewStickyHeaderText;
    private ItemsControl? _toastHost;
    private SearchBarView? _searchBar;
    private FilterBarView? _filterBar;
    private ScrollViewer? _previewTextScrollViewer;
    private VirtualizedPreviewTextControl? _previewTextControl;
    private VirtualizedLineNumbersControl? _previewLineNumbersControl;
    private HashSet<string>? _filterExpansionSnapshot;
    private int _filterApplyVersion;
    private SuspendedTreeToolMode _previewOnlySuspendedTreeToolMode;
    private CancellationTokenSource? _projectOperationCts;
    private CancellationTokenSource? _applySettingsCts;
    private CancellationTokenSource? _gitCloneCts;
    private CancellationTokenSource? _gitOperationCts;
    private CancellationTokenSource? _projectCopyExportCts;
    private TaskCompletionSource<bool>? _projectCopyExportCompletion;
    private bool _projectCopyExportClosePending;
    private bool _allowCloseAfterProjectCopyExportCleanup;
    private GitCloneWindow? _gitCloneWindow;
    private string? _currentCachedRepoPath;
    private RecentProjectsDb _recentProjectsDb = new();
    private Task? _recentFolderAvailabilityRefreshTask;
    private Border? _dropZoneContainer;
    private bool _dropZoneAcceptsCurrentDrag;
#if DEVPROJEX_PROJECT_LOAD_TIMING
    private ProjectLoadTiming? _projectLoadTiming;
#endif

    // Settings panel animation
    private Border? _settingsContainer;
    private Border? _settingsIsland;
    private SettingsPanelView? _settingsPanel;
    private TranslateTransform? _settingsTransform;
    private bool _settingsAnimating;
    private Task _settingsAnimationTask = Task.CompletedTask;
    private Task _postLoadVisualReadyTask = Task.CompletedTask;
    private const double SearchToolbarMinWidth = 418.0;
    private const double FilterToolbarMinWidth = 338.0;
    // Minimum/default aligns the settings island edge with the top tree-format switcher.
    // Manual splitter resize can still expand up to SettingsPanelMaxWidth.
    private const double SettingsPanelWidth = 285.0;
    private const double SettingsPanelMinWidth = SettingsPanelWidth;
    private const double SettingsPanelMaxWidth = 320.0;
    private static readonly TimeSpan SettingsPanelAnimationDuration = UiTimingProfile.Scale(TimeSpan.FromMilliseconds(300));
    private const double SplitTreePaneMinWidth = SearchToolbarMinWidth;
    private const double SplitPreviewPaneMinWidth = 320.0;
    private const double TreePreviewSplitterWidth = 4.0;
    private const double PreviewSettingsSplitterWidth = 4.0;
    private const string SplitterDraggingClass = "splitter-dragging";
    private GridLength _savedSplitTreeColumnWidth = new(5, GridUnitType.Star);
    private GridLength _savedSplitPreviewColumnWidth = new(6, GridUnitType.Star);
    private double _currentPreviewTreePaneWidth;
    private double _currentSettingsPanelWidth = SettingsPanelWidth;
    private double _savedNonSplitSettingsPanelWidth = SettingsPanelWidth;
    private double _effectiveSettingsPanelMinWidth = SettingsPanelMinWidth;
    private double _lastWindowBoundsWidth;
    private WorkspaceResizeTarget _activeWorkspaceResizeTarget;
    private IPointer? _activeWorkspaceResizePointer;
    private double _lastWorkspaceResizePointerX;
    private bool _workspaceChromeRefreshPending;

    // Search bar animation
    private Border? _searchBarContainer;
    private TranslateTransform? _searchBarTransform;
    private bool _searchBarAnimating;
    private bool _searchBarClosePending;
    private const double SearchBarHeight = 46.0;
    private static readonly TimeSpan SearchBarAnimationDuration = UiTimingProfile.Scale(TimeSpan.FromMilliseconds(250));
    private static readonly TimeSpan SearchFilterHotkeyDebounceWindow = UiTimingProfile.Scale(TimeSpan.FromMilliseconds(220));
    private long _lastSearchHotkeyTimestamp;
    private long _lastFilterHotkeyTimestamp;
    private int _pendingSearchHotkeyToggle;
    private int _pendingFilterHotkeyToggle;
    private int _searchFocusRequestVersion;
    private int _filterFocusRequestVersion;

    // Filter bar animation
    private Border? _filterBarContainer;
    private TranslateTransform? _filterBarTransform;
    private bool _filterBarAnimating;
    private bool _filterBarClosePending;
    private const double FilterBarHeight = 46.0;
    private static readonly TimeSpan FilterBarAnimationDuration = UiTimingProfile.Scale(TimeSpan.FromMilliseconds(250));

    // Tree pane animation inside preview workspace
    private bool _treePaneAnimating;
    private const double PreviewTreePaneSlideOffset = 32.0;
    private static readonly TimeSpan PreviewTreePaneAnimationDuration = SettingsPanelAnimationDuration;

    // Preview pane animation
    private bool _previewPaneAnimating;
    private static readonly TimeSpan PreviewPaneAnimationDuration = SettingsPanelAnimationDuration;

    // Preview bar chrome
    private Border? _previewBarContainer;
    private Border? _previewBar;
    private Grid? _previewSegmentGrid;
    private Border? _previewSegmentThumb;
    private TranslateTransform? _previewSegmentThumbTransform;
    private Button? _previewTreeModeButton;
    private Button? _previewContentModeButton;
    private Button? _previewTreeAndContentModeButton;
    private static readonly TimeSpan PreviewSegmentThumbAnimationDuration = UiTimingProfile.Scale(TimeSpan.FromMilliseconds(220));
    private const double PanelIslandSpacing = 4.0;
    private const int PreviewWarmupFileLimit = 24;
    private const double DefaultWindowMinWidth = 850.0;
    private const double WindowMinimumWidthSafetyPadding = 32.0;
    private const double PreviewToolbarWideThreshold = 380.0;
    private const double PreviewToolbarCompactThreshold = 320.0;
    private const double ToastHostBottomMargin = 38.0;
    private const double ToastHostHorizontalInset = 12.0;
    private const double StartupRevealHiddenOpacity = 0.0;
    private const double StartupRevealVisibleOpacity = 1.0;
    private static readonly TimeSpan StartupBackdropWarmupDelay = UiTimingProfile.Scale(TimeSpan.FromMilliseconds(90));
    private static readonly TimeSpan StartupStoreLockTimeout = TimeSpan.FromMilliseconds(100);
    private PreviewToolbarLayoutMode _previewToolbarLayoutMode = PreviewToolbarLayoutMode.Wide;
    private bool _startupRevealGateActive;
    private bool _startupRevealCompleted;
    private CancellationTokenSource? _windowLifetimeCts = new();
    private int _startupSequenceStarted;

    // Preview generation
    private bool _previewScrollSyncActive;
    private CancellationTokenSource? _previewMemoryCleanupCts;
    private int _previewMemoryCleanupVersion;
    private CancellationTokenSource? _searchMemoryCleanupCts;
    private int _searchMemoryCleanupVersion;
    private CancellationTokenSource? _backgroundMemoryCleanupCts;
    private CancellationTokenSource? _previewModeSwitchCts;
    private int _previewModeSwitchVersion;
    private bool _previewModeSwitchInProgress;
    private bool _previewFontInitialized;
    private int _suppressSearchFilterRealtimeDepth;
    private static readonly int TreeViewModelBuildParallelism =
        Math.Clamp(Environment.ProcessorCount, min: 2, max: 12);
    private const int TreeViewModelParallelChildrenThreshold = 24;

    // Real-time metrics calculation
    private CancellationTokenSource? _previewSelectionMetricsCts;
    private DispatcherTimer? _previewSelectionMetricsDebounceTimer;
    private readonly TreeSelectionSnapshotCache _treeSelectionSnapshotCache = new();

    private int _previewSelectionMetricsVersion;
    private static readonly TimeSpan PreviewSelectionMetricsDebounceInterval = UiTimingProfile.Scale(TimeSpan.FromMilliseconds(80));
    private ExportOutputMetrics _lastPreviewSelectionMetrics = ExportOutputMetrics.Empty;
    private bool _hasPreviewSelectionMetricsSnapshot;

    // Event handler delegates for proper unsubscription
    private EventHandler? _languageChangedHandler;
    private EventHandler? _themeChangedHandler;
    private PropertyChangedEventHandler? _viewModelPropertyChangedHandler;

    private readonly IReadOnlyList<CommandLineParseError> _startupCommandLineErrors;
    private readonly ProjectAnalysisService _projectAnalysisService;
    private readonly ReportPathResolver _reportPathResolver;
    private readonly ProjectAnalysisReportWriter _projectAnalysisReportWriter;
    private readonly ITerminalCommandSetupService _terminalCommandSetupService;
    private readonly SessionMetricsRecorder _sessionMetrics;

    public MainWindow(
        CommandLineOptions startupOptions,
        AvaloniaAppServices services,
        IReadOnlyList<CommandLineParseError>? startupCommandLineErrors = null)
    {
        PrepareStartupRevealGate();

        _startupOptions = startupOptions;
        _startupCommandLineErrors = startupCommandLineErrors ?? [];
        _localization = services.Localization;
        _scanOptions = services.ScanOptionsUseCase;
        _buildTree = services.BuildTreeUseCase;
        _ignoreOptionsService = services.IgnoreOptionsService;
        _ignoreRulesService = services.IgnoreRulesService;
        _filterSelectionService = services.FilterOptionSelectionService;
        _treeExport = services.TreeExportService;
        _contentExport = services.ContentExportService;
        _treeAndContentExport = services.TreeAndContentExportService;
        _projectCopyExport = services.ProjectCopyExportService;
        _previewDocumentBuilder = services.PreviewDocumentBuilder;
        _repositoryWebPathPresentationService = services.RepositoryWebPathPresentationService;
        _textFileExport = services.TextFileExportService;
        _toastService = services.ToastService;
        _iconCache = new IconCache(services.IconStore);
        _elevation = services.Elevation;
        _appInstanceLauncher = services.AppInstanceLauncher;
        _userSettingsStore = services.UserSettingsStore;
        _themeSettingsStore = services.ThemeSettingsStore;
        _gitService = services.GitRepositoryService;
        _repoCacheService = services.RepoCacheService;
        _zipDownloadService = services.ZipDownloadService;
        _projectAnalysisService = services.ProjectAnalysisService;
        _reportPathResolver = services.ReportPathResolver;
        _projectAnalysisReportWriter = services.ProjectAnalysisReportWriter;
        _terminalCommandSetupService = services.TerminalCommandSetupService;
        _sessionMetrics = services.SessionMetricsRecorder;
        _recentProjectsStore = services.RecentProjectsStore;
        _recentFolderAvailabilityService = services.RecentFolderAvailabilityService;

        _viewModel = new MainWindowViewModel(_localization, services.HelpContentProvider);
        _statusOperations = new StatusOperationCoordinator(
            _viewModel,
            IsBackgroundMetricsActive,
            () => _viewModel.StatusOperationCalculatingData);
        _metrics = new MetricsPipeline(
            _viewModel,
            _localization,
            services.FileContentAnalyzer,
            _treeExport,
            _statusOperations,
            () => _currentTree,
            () => _currentPath,
            GetCheckedPaths,
            GetCurrentTreeTextFormat,
            CreateExportPathPresentation,
            () => Bounds.Width);
        _previewPipeline = new PreviewWorkspacePipeline(
            this,
            // 350ms delay ensures thumb animation (250ms) completes fully before loading.
            UiTimingProfile.Scale(TimeSpan.FromMilliseconds(350)));
        _refreshPipeline = new RefreshTreePipeline(this);
        _selectionCoordinator = new SelectionSyncCoordinator(
            _viewModel,
            _scanOptions,
            _filterSelectionService,
            _ignoreOptionsService,
            BuildIgnoreRules,
            GetIgnoreOptionsAvailability,
            TryElevateAndRestart,
            () => _currentPath,
            _statusOperations);
        _projectLoadPipeline = new ProjectLoadPipeline(this, _statusOperations);
        _projectLoadSnapshotPipeline = new ProjectLoadSnapshotPipeline(this);
        _projectProfiles = new ProjectProfilePersistenceCoordinator(
            _viewModel,
            _selectionCoordinator,
            services.ProjectProfileStore);
        _taskbarProgress = new TaskbarProgressCoordinator(
            _viewModel,
            services.TaskbarProgressService);
        _viewModel.SetToastItems(_toastService.Items);
        _sessionMetrics.SetIdleStateProvider(IsSessionMetricsIdle);
        _sessionMetrics.Start(_startupOptions.SessionMetrics.Path, GetApplicationVersion());
        LoadRecentProjects();
        DataContext = _viewModel;

        InitializeComponent();

        // Setup drag & drop for the drop zone
        _dropZoneContainer = this.FindControl<Border>("DropZoneContainer");
        if (_dropZoneContainer is not null)
        {
            _dropZoneContainer.AddHandler(DragDrop.DragEnterEvent, OnDropZoneDragEnter);
            _dropZoneContainer.AddHandler(DragDrop.DragOverEvent, OnDropZoneDragOver);
            _dropZoneContainer.AddHandler(DragDrop.DragLeaveEvent, OnDropZoneDragLeave);
            _dropZoneContainer.AddHandler(DragDrop.DropEvent, OnDropZoneDrop);
            UpdateDropZoneAnimationState();
        }

        InitializeUserSettings();
        _viewModel.SetMicaAvailability(_isMicaSupported);

        _viewModel.UpdateHelpPopoverMaxSize(Bounds.Size);
        PropertyChanged += OnWindowPropertyChanged;

        _treeView = this.FindControl<TreeView>("ProjectTree");
        _topMenuBar = this.FindControl<TopMenuBarView>("TopMenuBar");
        _workspaceGrid = this.FindControl<Grid>("WorkspaceGrid");
        _treePaneContainer = this.FindControl<Border>("TreePaneContainer");
        _treePaneSnapshotHost = this.FindControl<Border>("TreePaneAnimationSnapshotHost");
        _treePaneSnapshotImage = this.FindControl<Image>("TreePaneAnimationSnapshotImage");
        _treePaneRoot = this.FindControl<Grid>("TreePaneRoot");
        _previewPaneContainer = this.FindControl<Border>("PreviewPaneContainer");
        _previewPaneRoot = this.FindControl<Grid>("PreviewPaneRoot");
        _previewPaneSnapshotHost = this.FindControl<Border>("PreviewPaneAnimationSnapshotHost");
        _previewPaneSnapshotImage = this.FindControl<Image>("PreviewPaneAnimationSnapshotImage");
        _treePreviewSplitter = this.FindControl<Border>("TreePreviewSplitter");
        _previewSettingsSplitter = this.FindControl<Border>("PreviewSettingsSplitter");
        _treeIsland = this.FindControl<Border>("TreeIsland");
        _previewIsland = this.FindControl<Border>("PreviewIsland");
        _toastHost = this.FindControl<ItemsControl>("ToastHost");
        if (_workspaceGrid is not null && _workspaceGrid.ColumnDefinitions.Count >= 5)
        {
            _treePaneColumn = _workspaceGrid.ColumnDefinitions[0];
            _treePreviewSplitterColumn = _workspaceGrid.ColumnDefinitions[1];
            _previewPaneColumn = _workspaceGrid.ColumnDefinitions[2];
            _previewSettingsSplitterColumn = _workspaceGrid.ColumnDefinitions[3];
        }
        _searchBar = this.FindControl<SearchBarView>("SearchBar");
        _filterBar = this.FindControl<FilterBarView>("FilterBar");
        _previewBarContainer = this.FindControl<Border>("PreviewBarContainer");
        _previewBar = this.FindControl<Border>("PreviewBar");
        _previewLineNumbersBackground = this.FindControl<Border>("PreviewLineNumbersBackground");
        _previewStickyHeaderCap = this.FindControl<Border>("PreviewStickyHeaderCap");
        _previewStickyHeaderContainer = this.FindControl<Border>("PreviewStickyHeaderContainer");
        _previewStickyHeaderText = this.FindControl<TextBlock>("PreviewStickyHeaderText");
        _previewSegmentGrid = this.FindControl<Grid>("PreviewSegmentGrid");
        _previewSegmentThumb = this.FindControl<Border>("PreviewSegmentThumb");
        _previewTreeModeButton = this.FindControl<Button>("PreviewTreeModeButton");
        _previewContentModeButton = this.FindControl<Button>("PreviewContentModeButton");
        _previewTreeAndContentModeButton = this.FindControl<Button>("PreviewTreeAndContentModeButton");
        _previewTextScrollViewer = this.FindControl<ScrollViewer>("PreviewTextScrollViewer");
        _previewTextControl = this.FindControl<VirtualizedPreviewTextControl>("PreviewTextControl");
        _previewLineNumbersControl = this.FindControl<VirtualizedLineNumbersControl>("PreviewLineNumbersControl");
        AttachRecentMenuHandlers();
        AttachTreeFontMenuHandlers();
        RefreshRecentFoldersMenu();
        RefreshLanguageMenuChecks();
        if (_treePaneSnapshotImage is not null)
        {
            _treePaneSnapshotTransform = _treePaneSnapshotImage.RenderTransform as TranslateTransform ?? new TranslateTransform();
            _treePaneSnapshotImage.RenderTransform = _treePaneSnapshotTransform;
        }
        if (_previewTextScrollViewer is not null && _previewTextControl is not null)
        {
            _previewTextScrollViewer.Cursor = new Cursor(StandardCursorType.Ibeam);
            _previewTextControl.VerticalOffset = Math.Max(0, _previewTextScrollViewer.Offset.Y);
            _previewTextControl.ViewportHeight = Math.Max(0, _previewTextScrollViewer.Viewport.Height);
            _previewTextControl.ViewportWidth = Math.Max(0, _previewTextScrollViewer.Viewport.Width);
            _previewTextControl.CopiedToClipboard += OnPreviewCopiedToClipboard;
            _previewTextControl.PreviewSelectionChanged += OnPreviewSelectionChanged;
        }
        _settingsContainer = this.FindControl<Border>("SettingsContainer");
        _settingsIsland = this.FindControl<Border>("SettingsIsland");
        _settingsPanel = this.FindControl<SettingsPanelView>("SettingsPanel");

        if (_settingsIsland is not null && _settingsContainer is not null)
        {
            _settingsTransform = new TranslateTransform();
            _settingsIsland.RenderTransform = _settingsTransform;
            // Start hidden (collapsed width, off-screen to the right)
            _settingsContainer.Width = 0;
            _settingsTransform.X = SettingsPanelWidth;
            _settingsIsland.Opacity = 0;
        }

        if (_settingsPanel is not null)
        {
            _settingsPanel.MinimumWidthChanged += OnSettingsPanelMinimumWidthChanged;
            UpdateSettingsPanelMinimumWidth(_settingsPanel.GetRequiredMinimumWidth());
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
            _searchBarTransform.Y = 0;
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
            _filterBarTransform.Y = 0;
            _filterBar.Opacity = 0;
        }

        if (_previewPaneContainer is not null)
            _previewPaneContainer.Width = 0;

        if (_previewSegmentThumb is not null)
        {
            _previewSegmentThumbTransform = new TranslateTransform();
            _previewSegmentThumb.RenderTransform = _previewSegmentThumbTransform;
            EnsurePreviewSegmentThumbTransitions();
        }

        if (_previewSegmentGrid is not null)
            _previewSegmentGrid.SizeChanged += OnPreviewSegmentGridSizeChanged;
        if (_previewBar is not null)
            _previewBar.SizeChanged += OnPreviewBarSizeChanged;

        if (_treeView is not null)
        {
            _treeView.PointerEntered += OnTreePointerEntered;
        }
        AddHandler(PointerWheelChangedEvent, OnWindowPointerWheelChanged, RoutingStrategies.Tunnel, true);

        _searchCoordinator = new TreeSearchCoordinator(
            _viewModel,
            _treeView ?? throw new InvalidOperationException(),
            ScheduleSearchMemoryCleanupAfterRender,
            _sessionMetrics);
        _filterCoordinator = new NameFilterCoordinator(
            ApplyFilterRealtimeWithToken,
            () => !string.IsNullOrWhiteSpace(_viewModel.NameFilter),
            _viewModel.SetFilterInProgress);
        _themeBrushCoordinator = new ThemeBrushCoordinator(this, _viewModel, () => _topMenuBar?.MainMenuControl);
        // Publish builds can reach the first native frame much faster than local debug runs.
        // Prime the saved material/backdrop before the startup reveal gate waits for the
        // first render frames, otherwise the gate would only hide the default XAML surface.
        ApplyStartupThemePreset();
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
        Activated += OnActivated;
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
        RefreshTreeFontMenu();
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
            else if (args.PropertyName is nameof(MainWindowViewModel.BackgroundTransparency)
                     or nameof(MainWindowViewModel.PanelContrast)
                     or nameof(MainWindowViewModel.BorderVisibility)
                     or nameof(MainWindowViewModel.MenuTransparency))
            {
                if (Volatile.Read(ref _applyingThemePresetDepth) == 0)
                    _themePresetSession?.MarkDirty();
                _themeBrushCoordinator.ScheduleDynamicThemeBrushUpdate();
            }
            else if (args.PropertyName == nameof(MainWindowViewModel.ThemePopoverOpen))
                HandleThemePopoverStateChange();
            else if (args.PropertyName == nameof(MainWindowViewModel.IsProjectLoaded))
                UpdateDropZoneAnimationState();
            else if (args.PropertyName is nameof(MainWindowViewModel.StatusBusy)
                     or nameof(MainWindowViewModel.StatusProgressIsIndeterminate)
                     or nameof(MainWindowViewModel.StatusProgressValue))
                _taskbarProgress.SyncWithStatusBar();
            else if (args.PropertyName == nameof(MainWindowViewModel.SelectedExportFormat))
            {
                _metrics.Recalculate(); // Update tree metrics when the selected tree format changes.
                InvalidatePreviewCache();
                SchedulePreviewRefresh();
                _sessionMetrics.RecordTreeFormatChanged(GetCurrentTreeTextFormat());
            }
            else if (args.PropertyName == nameof(MainWindowViewModel.SelectedPreviewContentMode))
            {
                if (!_previewModeSwitchInProgress)
                    UpdatePreviewSegmentThumbPosition(animate: false);
                if (_metrics.HasStatusMetricsSnapshot && _viewModel.StatusMetricsVisible)
                    _metrics.RenderStatusBarMetrics();
                _sessionMetrics.RecordPreviewModeChanged(
                    _viewModel.SelectedPreviewContentMode,
                    _viewModel.IsAnyPreviewVisible);
            }
            else if (args.PropertyName == nameof(MainWindowViewModel.IsAnyPreviewVisible))
            {
                if (_metrics.HasStatusMetricsSnapshot && _viewModel.StatusMetricsVisible)
                    _metrics.RenderStatusBarMetrics();
                _sessionMetrics.RecordPreviewModeChanged(
                    _viewModel.SelectedPreviewContentMode,
                    _viewModel.IsAnyPreviewVisible);
            }
            else if (args.PropertyName is nameof(MainWindowViewModel.PreviewFontSize)
                     or nameof(MainWindowViewModel.SelectedFontFamily))
            {
                Dispatcher.Post(UpdatePreviewStickyPath, DispatcherPriority.Render);
            }
            else if (args.PropertyName == nameof(MainWindowViewModel.PendingFontFamily))
                RefreshTreeFontMenu();
            else if (args.PropertyName is nameof(MainWindowViewModel.TreeItemSpacing)
                     or nameof(MainWindowViewModel.TreeItemPadding)
                     or nameof(MainWindowViewModel.TreeIconSize)
                     or nameof(MainWindowViewModel.TreeTextMargin))
            {
                UpdateTreeVisualResources();
            }
        };
        _viewModel.PropertyChanged += _viewModelPropertyChangedHandler;
        UpdatePreviewSegmentThumbPosition(animate: false);
        UpdateTreeVisualResources();
        UpdateWorkspaceLayoutForCurrentMode();
        UpdateAdaptiveWorkspaceChrome(forcePreviewLabels: true);

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        Opened += OnOpened;
        ScalingChanged += OnWindowScalingChanged;

        // Hook menu item submenu opening to apply brushes directly
        AddHandler(MenuItem.SubmenuOpenedEvent, _themeBrushCoordinator.HandleSubmenuOpened, RoutingStrategies.Bubble);
        AddHandler(MenuItem.SubmenuOpenedEvent, GitBranchMenuScrollBehavior.HandleSubmenuOpened, RoutingStrategies.Bubble);
    }

    private void EnsureAppStateStoresExist()
    {
        try
        {
            // Keep all user-facing state documents present from startup so the app can
            // recover from partial external cleanup without waiting for a later save path.
            _userSettingsStore.EnsureStorageExists();
            _themeSettingsStore.EnsureStorageExists();
            _recentProjectsStore.EnsureStorageExists();
            _projectProfiles.EnsureStorageExists();
        }
        catch
        {
            // Best effort only. Persistence bootstrap must never prevent startup.
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        CancelAndDispose(ref _windowLifetimeCts);
        CompleteSessionMetricsRecording();
        FlushPersistedStateOnWindowClose();

        // Unsubscribe from window events
        PropertyChanged -= OnWindowPropertyChanged;
        ScalingChanged -= OnWindowScalingChanged;

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

        // Unsubscribe from DragDrop events
        if (_dropZoneContainer is not null)
        {
            _dropZoneContainer.RemoveHandler(DragDrop.DragEnterEvent, OnDropZoneDragEnter);
            _dropZoneContainer.RemoveHandler(DragDrop.DragOverEvent, OnDropZoneDragOver);
            _dropZoneContainer.RemoveHandler(DragDrop.DragLeaveEvent, OnDropZoneDragLeave);
            _dropZoneContainer.RemoveHandler(DragDrop.DropEvent, OnDropZoneDrop);
        }

        // Unsubscribe from tree pointer events
        if (_treeView is not null)
            _treeView.PointerEntered -= OnTreePointerEntered;
        if (_previewTextControl is not null)
        {
            _previewTextControl.CopiedToClipboard -= OnPreviewCopiedToClipboard;
            _previewTextControl.PreviewSelectionChanged -= OnPreviewSelectionChanged;
        }
        if (_previewSegmentGrid is not null)
            _previewSegmentGrid.SizeChanged -= OnPreviewSegmentGridSizeChanged;
        if (_previewBar is not null)
            _previewBar.SizeChanged -= OnPreviewBarSizeChanged;
        if (_settingsPanel is not null)
            _settingsPanel.MinimumWidthChanged -= OnSettingsPanelMinimumWidthChanged;
        DetachRecentMenuHandlers();
        DetachTreeFontMenuHandlers();

        // Unsubscribe from tunneled/bubbled events
        RemoveHandler(PointerWheelChangedEvent, OnWindowPointerWheelChanged);
        RemoveHandler(KeyDownEvent, OnKeyDown);
        RemoveHandler(MenuItem.SubmenuOpenedEvent, _themeBrushCoordinator.HandleSubmenuOpened);
        RemoveHandler(MenuItem.SubmenuOpenedEvent, GitBranchMenuScrollBehavior.HandleSubmenuOpened);

        // Unsubscribe from window lifecycle events
        Opened -= OnOpened;
        Closing -= OnWindowClosing;
        Closed -= OnWindowClosed;
        Activated -= OnActivated;
        Deactivated -= OnDeactivated;

        CancelAndDisposeWindowOperations();

        // Dispose coordinators
        _searchCoordinator.Dispose();
        _filterCoordinator.Dispose();
        _selectionCoordinator.Dispose();
        _themeBrushCoordinator.Dispose();

        // Dispose ViewModel to clean up collection event handlers
        _viewModel.Dispose();

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
        _filterBaseTree = null;
        _currentTreeInventory = null;
        _filterExpansionSnapshot = null;
        _previewOnlySuspendedTreeToolMode = SuspendedTreeToolMode.None;
        ResetPreviewTreePaneVisualState();
        ResetInteractiveFilterCache();
        _metrics.InvalidateComputedCaches();

        // Clear file metrics cache
        _metrics.ClearFileMetricsCache(trimCapacity: true);

        // Clean up repository cache on exit
        _repoCacheService.ClearAllCache();

        _taskbarProgress.Dispose();

        // Dispose ZipDownloadService
        if (_zipDownloadService is IDisposable disposable)
            disposable.Dispose();

        _sessionMetrics.Dispose();
    }

    private bool IsSessionMetricsIdle()
        => IsVisible &&
           !_viewModel.StatusBusy &&
           !_viewModel.IsSearchInProgress &&
           !_viewModel.IsFilterInProgress &&
           !_viewModel.IsPreviewLoading &&
           !_settingsAnimating &&
           !_previewModeSwitchInProgress;

    private void CompleteSessionMetricsRecording()
    {
        var completion = _sessionMetrics.Complete();
        if (completion is null)
            return;

        if (completion.Success)
        {
            Console.Out.WriteLine($"DevProjex session metrics: {completion.NormalizedOutputPath}");
            return;
        }

        Console.Error.WriteLine($"DevProjex: failed to write session metrics to {completion.NormalizedOutputPath}: {completion.ErrorMessage}");
    }

    private static string GetApplicationVersion()
    {
        var assembly = typeof(MainWindow).Assembly;
        return assembly
                   .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                   ?.InformationalVersion
               ?? assembly.GetName().Version?.ToString()
               ?? "unknown";
    }

    private void UpdateDropZoneAnimationState()
    {
        if (_dropZoneContainer is null)
            return;

        // This is a render-lifecycle boundary, not merely a visual class toggle.
        // In v4.9 the class was made permanent in XAML and the method was removed.
        // The hidden drop zone then kept DefaultRenderLoop, Skia, and ANGLE active while
        // the tree or preview workspace was idle. IsVisible alone did not prevent that
        // regression on the affected Windows/Avalonia rendering path.
        //
        // Keep the explicit remove/add symmetry: removal guarantees true project idle,
        // while re-adding preserves the original animation after reset back to drop zone.
        // PlaybackBehavior=OnlyIfVisible in XAML remains an additional safety boundary.
        // Do not bind this state to Window.IsActive. During an external drag the source
        // application can retain activation while this window receives DragEnter/Drop,
        // which would freeze the drop-zone feedback exactly when the user needs it.
        if (_viewModel.IsProjectLoaded)
            _dropZoneContainer.Classes.Remove("drop-zone-animating");
        else
            _dropZoneContainer.Classes.Add("drop-zone-animating");
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _themeBrushCoordinator.ScheduleDynamicThemeBrushUpdate();

        // Defer update to let theme resources settle first.
        Dispatcher.Post(
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
        if (e.Property == ActualTransparencyLevelProperty)
        {
            if (_themeEffectRuntimeProbeReady)
                _themeBrushCoordinator.ScheduleActualEffectSynchronization();
            return;
        }

        if (e.Property != BoundsProperty)
            return;

        if (e.NewValue is Rect rect)
        {
            var widthDelta = _lastWindowBoundsWidth > 0
                ? rect.Width - _lastWindowBoundsWidth
                : 0;
            _lastWindowBoundsWidth = rect.Width;

            _viewModel.UpdateHelpPopoverMaxSize(rect.Size);
            if (_viewModel.IsPreviewTreeVisible)
                AdjustSplitPaneWidthsForWindowResize(widthDelta);
            ClampSettingsPanelWidthToAvailableSpace(applyToVisual: ShouldApplySettingsPanelWidthToVisual());
            UpdatePreviewSettingsSplitterState();
            UpdateAdaptiveWorkspaceChrome();
            if (_metrics.HasStatusMetricsSnapshot && _viewModel.StatusMetricsVisible)
                _metrics.RenderStatusBarMetrics();
            if (_hasPreviewSelectionMetricsSnapshot)
                RenderPreviewSelectionMetrics();
            if (_viewModel.IsAnyPreviewVisible && !_previewModeSwitchInProgress)
                UpdatePreviewSegmentThumbPosition(animate: false);
        }
    }

    private void OnPreviewSegmentGridSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!_viewModel.IsAnyPreviewVisible || _previewModeSwitchInProgress)
            return;

        UpdatePreviewSegmentThumbPosition(animate: false);
    }

    private void OnPreviewBarSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdatePreviewToolbarPresentation(forceRefreshContent: false);
        UpdateToastHostLayout();
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        CancelBackgroundMemoryCleanup();
        _systemDialogActivationTcs?.TrySetResult(true);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_awaitingSystemDialogActivation && _systemDialogActivationTcs is null)
        {
            _systemDialogActivationTcs =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        if (_viewModel.HelpPopoverOpen)
            _viewModel.HelpPopoverOpen = false;
        if (_viewModel.HelpDocsPopoverOpen)
            _viewModel.HelpDocsPopoverOpen = false;

        // Native pickers temporarily deactivate the main window too. Running aggressive cleanup
        // during that handoff makes the dialog open/close path feel heavier than it should.
        if (_awaitingSystemDialogActivation)
            return;

        // Do not use deactivation as a cleanup trigger. Alt-Tab, focus changes and native
        // window-manager handoffs are common interactive paths; forcing Gen2/working-set
        // trimming here saves little and creates avoidable page faults when the user returns.
    }

    private async Task WaitForWindowActivationAfterSystemDialogAsync(CancellationToken cancellationToken = default)
    {
        var activationTcs = _systemDialogActivationTcs;
        _systemDialogActivationTcs = null;
        _awaitingSystemDialogActivation = false;

        if (activationTcs is not null)
        {
            var activationTimeout = UiTimingProfile.Scale(TimeSpan.FromMilliseconds(700));
            await Task.WhenAny(
                activationTcs.Task,
                Task.Delay(activationTimeout, cancellationToken));
        }

        cancellationToken.ThrowIfCancellationRequested();
        await YieldUiAsync(DispatcherPriority.Background);
        cancellationToken.ThrowIfCancellationRequested();
        await YieldUiAsync(DispatcherPriority.Render);
        cancellationToken.ThrowIfCancellationRequested();

        // The first frame after a native picker closes is often the focus/activation handoff frame.
        // Give the window one short extra beat before project-load work starts on the same UI loop.
        await Task.Delay(UiTimingProfile.Scale(TimeSpan.FromMilliseconds(50)), cancellationToken);
    }

    private static async Task YieldProjectLoadStartupFrameAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await YieldUiAsync(DispatcherPriority.Background);
        cancellationToken.ThrowIfCancellationRequested();
        await YieldUiAsync(DispatcherPriority.Render);
    }

    private void PrepareStartupRevealGate()
    {
        if (!ShouldUseStartupRevealGate())
            return;

        // Published single-file Windows builds can show the first HWND frame before
        // DWM/WinUI composition has attached Acrylic/Mica. Keep the top-level invisible
        // until the first render cycles have completed; the final opacity is restored in
        // OnOpened before any user-visible startup dialogs or project-load work begin.
        Opacity = StartupRevealHiddenOpacity;
        _startupRevealGateActive = true;
    }

    internal static bool ShouldUseStartupRevealGate()
        => OperatingSystem.IsWindows();

    private async Task RevealStartupWindowAfterCompositionWarmupAsync(CancellationToken cancellationToken)
    {
        if (!_startupRevealGateActive || _startupRevealCompleted)
            return;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await YieldUiAsync(DispatcherPriority.Render);
            await WaitForNextAnimationFrameAsync(cancellationToken);
            await WaitForNextAnimationFrameAsync(cancellationToken);
            await YieldUiAsync(DispatcherPriority.Loaded);
            cancellationToken.ThrowIfCancellationRequested();

            // The Avalonia frame is ready at this point, but the native Windows backdrop
            // may still attach one beat later in published builds. This tiny pause hides
            // that platform-level transparent/square intermediate frame without changing
            // the steady-state UI.
            await Task.Delay(StartupBackdropWarmupDelay, cancellationToken);
        }
        finally
        {
            CompleteStartupRevealGate();
        }
    }

    private async Task WaitForNextAnimationFrameAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            RequestAnimationFrame(_ => completion.TrySetResult(true));
        }
        catch
        {
            // If a platform denies RAF during startup, never keep the main window hidden.
            return;
        }

        var completedTask = await Task.WhenAny(
            completion.Task,
            Task.Delay(UiTimingProfile.Scale(TimeSpan.FromMilliseconds(250)), cancellationToken));
        await completedTask;
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void CompleteStartupRevealGate()
    {
        if (_startupRevealCompleted)
            return;

        _startupRevealCompleted = true;
        _startupRevealGateActive = false;
        Opacity = StartupRevealVisibleOpacity;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _startupSequenceStarted, 1) != 0)
            return;

        Opened -= OnOpened;
        var lifetime = _windowLifetimeCts;
        if (lifetime is null)
            return;

        ObserveDetachedTask(RunStartupAsync(lifetime.Token), "MainWindowStartup");
    }

    private async Task RunStartupAsync(CancellationToken cancellationToken)
    {
        try
        {
            _taskbarProgress.Attach(this);

            UpdateAdaptiveWorkspaceChrome(forcePreviewLabels: true);

            await RevealStartupWindowAfterCompositionWarmupAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _themeEffectRuntimeProbeReady = true;
            _themeBrushCoordinator.ScheduleActualEffectSynchronization();
            StartDeferredAppStateBootstrap(cancellationToken);

            if (_startupCommandLineErrors.Count > 0)
            {
                await ShowErrorAsync(string.Join(Environment.NewLine, _startupCommandLineErrors.Select(static error => error.Message)));
                cancellationToken.ThrowIfCancellationRequested();
            }

            var startupProjectPath = ResolveStartupProjectPath();
            if (!string.IsNullOrWhiteSpace(startupProjectPath))
            {
                var startupLoadStopwatch = Stopwatch.StartNew();
                var opened = await TryOpenFolderAsync(startupProjectPath, fromDialog: false);
                startupLoadStopwatch.Stop();
                cancellationToken.ThrowIfCancellationRequested();

                if (opened)
                {
                    await TryApplyStartupSelectionOverridesAsync();
                    await TryWriteStartupReportAsync(startupLoadStopwatch.Elapsed);
                    await TryApplyStartupUiOptionsAsync();
                    if (await TryRunStartupUiBenchmarkScriptAsync())
                        return;
                }
            }
            else if (_startupOptions.Ui.OpenLastProject)
            {
                await ShowInfoAsync(_viewModel.MenuFileRecentEmpty);
            }
            else
            {
                await TryShowAutomaticTerminalCommandPromptAsync(cancellationToken);
            }

            ObserveDetachedTask(
                Task.Run(_repoCacheService.CleanupStaleCacheOnStartup, cancellationToken),
                "CleanupStaleRepositoryCache");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Closing the window owns cancellation of the complete startup sequence.
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested && IsVisible)
                await ShowErrorAsync(ex.Message);
        }
    }

    private void StartDeferredAppStateBootstrap(CancellationToken cancellationToken)
    {
        ObserveDetachedTask(
            Task.Run(EnsureAppStateStoresExist, cancellationToken),
            "EnsureAppStateStoresExist");
    }

    private string? ResolveStartupProjectPath()
    {
        if (_startupOptions.SessionMetrics.Enabled)
            return _startupOptions.SessionMetrics.Path;

        if (!string.IsNullOrWhiteSpace(_startupOptions.Path))
            return _startupOptions.Path;

        if (!_startupOptions.Ui.OpenLastProject)
            return null;

        foreach (var recentFolder in _recentProjectsDb.RecentFolders)
        {
            if (!string.IsNullOrWhiteSpace(recentFolder.Path) &&
                Directory.Exists(recentFolder.Path))
            {
                return recentFolder.Path;
            }
        }

        return null;
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
        string? folder = null;
        try
        {
            folder = ResolveDropFolderPath(
                e.DataTransfer.TryGetFiles()?.Select(item => item.TryGetLocalPath()) ?? []);
        }
        catch
        {
            // Some platform storage providers can fail while materializing a dragged item.
        }

        _dropZoneAcceptsCurrentDrag = !string.IsNullOrWhiteSpace(folder);
        e.DragEffects = ResolveDropEffect(_dropZoneAcceptsCurrentDrag);

        if (sender is Border border)
        {
            if (_dropZoneAcceptsCurrentDrag)
                border.Classes.Add("drag-over");
            else
                border.Classes.Remove("drag-over");
        }
    }

    private void OnDropZoneDragOver(object? sender, DragEventArgs e)
    {
        // The native no-drop cursor is updated from DragOver, which fires while the pointer moves.
        e.DragEffects = ResolveDropEffect(_dropZoneAcceptsCurrentDrag);
    }

    private void OnDropZoneDragLeave(object? sender, DragEventArgs e)
    {
        _dropZoneAcceptsCurrentDrag = false;

        // Remove visual feedback class
        if (sender is Border border)
        {
            border.Classes.Remove("drag-over");
        }
    }

    private async void OnDropZoneDrop(object? sender, DragEventArgs e)
    {
        _dropZoneAcceptsCurrentDrag = false;

        // Remove visual feedback class
        if (sender is Border border)
        {
            border.Classes.Remove("drag-over");
        }

        try
        {
            var files = e.DataTransfer.TryGetFiles();
            if (files is null) return;

            var localPaths = files
                .Select(f => f.TryGetLocalPath());
            var folder = ResolveDropFolderPath(localPaths);
            e.DragEffects = ResolveDropEffect(!string.IsNullOrWhiteSpace(folder));

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
        ApplyPresetValues(_themeSettingsStore.GetPreset(
            _themeSettingsDocument,
            _currentThemeVariant,
            _currentEffectMode));
        _themeBrushCoordinator.UpdateTransparencyEffect();
    }

    private void InitializeUserSettings()
    {
        _userSettingsDb = _userSettingsStore.LoadForStartup(StartupStoreLockTimeout);
        ApplySavedLanguagePreference(_userSettingsDb.ViewSettings);

        _themeSettingsDocument = _themeSettingsStore.LoadForStartup(StartupStoreLockTimeout);
        _themePresetSession = new ThemePresetSession(_themeSettingsStore, _themeSettingsDocument);
        var theme = _themePresetSession.CurrentTheme;
        var effect = ThemeEffectPlatformSupport.Normalize(_themePresetSession.CurrentEffect, _isMicaSupported);

        _currentThemeVariant = theme;
        _currentEffectMode = effect;
        _viewModel.IsDarkTheme = theme == ThemePresetVariant.Dark;
        ApplyEffectMode(effect);
        ApplyPresetValues(_themeSettingsStore.GetPreset(_themeSettingsDocument, theme, effect));
        ApplyViewSettings(_userSettingsDb.ViewSettings);
        _wasThemePopoverOpen = _viewModel.ThemePopoverOpen;
    }

    private void ApplyEffectMode(ThemePresetEffect effect)
    {
        switch (effect)
        {
            case ThemePresetEffect.Solid:
                _viewModel.SetThemeEffects(transparent: false, mica: false, acrylic: false);
                break;
            case ThemePresetEffect.Mica:
                _viewModel.SetThemeEffects(transparent: false, mica: true, acrylic: false);
                break;
            case ThemePresetEffect.Acrylic:
                _viewModel.SetThemeEffects(transparent: false, mica: false, acrylic: true);
                break;
            default:
                _viewModel.SetThemeEffects(transparent: true, mica: false, acrylic: false);
                break;
        }
    }

    private void ApplyPresetValues(ThemePreset preset)
    {
        Interlocked.Increment(ref _applyingThemePresetDepth);
        try
        {
            _viewModel.BackgroundTransparency = preset.BackgroundTransparency;
            _viewModel.PanelContrast = preset.PanelContrast;
            _viewModel.MenuTransparency = preset.MenuTransparency;
            _viewModel.BorderVisibility = preset.BorderVisibility;
        }
        finally
        {
            Interlocked.Decrement(ref _applyingThemePresetDepth);
        }
    }

    private void ApplyPresetForSelection(ThemePresetVariant theme, ThemePresetEffect effect)
    {
        if (_themePresetSession is null)
            return;

        var preset = _themePresetSession.Select(theme, effect, CreateCurrentThemePreset());
        _currentThemeVariant = theme;
        _currentEffectMode = effect;
        ApplyPresetValues(preset);
    }

    private void ApplyViewSettings(AppViewSettings settings)
    {
        _viewModel.IsCompactMode = settings.IsCompactMode;
        _viewModel.IsTreeAnimationEnabled = settings.IsTreeAnimationEnabled;

        UpdateCompactModeVisualState();

        if (_viewModel.IsTreeAnimationEnabled)
            Classes.Add("tree-animation");
        else
            Classes.Remove("tree-animation");
    }

    private void UpdateCompactModeVisualState()
    {
        if (_viewModel.IsCompactModeEffective)
            Classes.Add("compact-mode");
        else
            Classes.Remove("compact-mode");

        _settingsPanel?.RequestMinimumWidthRefresh();
    }

    private WorkspaceDisplayMode GetCurrentDisplayMode()
    {
        if (_viewModel.IsPreviewTreeVisible)
            return WorkspaceDisplayMode.PreviewWithTree;

        return _viewModel.IsPreviewMode
            ? WorkspaceDisplayMode.PreviewOnly
            : WorkspaceDisplayMode.Tree;
    }

    private void UpdateWorkspaceLayoutForCurrentMode()
    {
        if (_treePaneColumn is null || _treePreviewSplitterColumn is null || _previewPaneColumn is null)
            return;

        var displayMode = GetCurrentDisplayMode();

        switch (displayMode)
        {
            case WorkspaceDisplayMode.PreviewOnly:
                SetWorkspacePaneState(_treePaneColumn, visible: false, width: new GridLength(0), minWidth: 0);
                SetWorkspacePaneState(_previewPaneColumn, visible: true, width: new GridLength(1, GridUnitType.Star), minWidth: SplitPreviewPaneMinWidth);
                _treePreviewSplitterColumn.Width = new GridLength(0);
                if (_previewPaneContainer is not null && !_previewPaneAnimating)
                    ApplyPreviewPaneWidth(double.NaN, animate: false);
                break;

            case WorkspaceDisplayMode.PreviewWithTree:
                SetWorkspacePaneState(_treePaneColumn, visible: true, width: GridLength.Auto, minWidth: 0);
                SetWorkspacePaneState(_previewPaneColumn, visible: true, width: new GridLength(1, GridUnitType.Star), minWidth: SplitPreviewPaneMinWidth);
                _treePreviewSplitterColumn.Width = new GridLength(TreePreviewSplitterWidth);
                ApplyPreviewTreePaneWidth(ResolveDesiredPreviewTreePaneWidth(), animate: false);
                if (_previewPaneContainer is not null && !_previewPaneAnimating)
                    ApplyPreviewPaneWidth(double.NaN, animate: false);
                break;

            default:
                SetWorkspacePaneState(_treePaneColumn, visible: true, width: new GridLength(1, GridUnitType.Star), minWidth: SplitTreePaneMinWidth);
                SetWorkspacePaneState(_previewPaneColumn, visible: false, width: new GridLength(0), minWidth: 0);
                _treePreviewSplitterColumn.Width = new GridLength(0);
                if (_treePaneContainer is not null && !_treePaneAnimating)
                    ApplyPreviewTreePaneWidth(double.NaN, animate: false);
                if (_previewPaneContainer is not null && !_previewPaneAnimating)
                    ApplyPreviewPaneWidth(0.0, animate: false);
                break;
        }

        ClampSettingsPanelWidthToAvailableSpace(applyToVisual: ShouldApplySettingsPanelWidthToVisual());
        UpdatePreviewSettingsSplitterState();

        if (_treePreviewSplitter is not null)
            _treePreviewSplitter.IsVisible = _viewModel.IsPreviewTreeVisible;

        UpdateAdaptiveWorkspaceChrome();
    }

    private void UpdateAdaptiveWorkspaceChrome(bool forcePreviewLabels = false)
    {
        UpdateWindowMinimumWidth();
        UpdatePreviewToolbarPresentation(forcePreviewLabels);
        UpdateToastHostLayout();
    }

    private void UpdateWindowMinimumWidth()
    {
        var computedMinWidth = Math.Max(DefaultWindowMinWidth, GetRequiredWindowWorkspaceWidth() + WindowMinimumWidthSafetyPadding);
        MinWidth = AlignWindowConstraintToPhysicalPixels(computedMinWidth, RenderScaling);
    }

    internal static double AlignWindowConstraintToPhysicalPixels(double constraint, double renderScaling)
    {
        var effectiveScaling = double.IsFinite(renderScaling) && renderScaling > 0
            ? renderScaling
            : 1.0;

        // Win32 tracks window constraints in physical pixels. Keeping the DIP value aligned
        // prevents Avalonia and WM_GETMINMAXINFO from rounding it in opposite directions.
        return Math.Ceiling(constraint * effectiveScaling) / effectiveScaling;
    }

    private void OnWindowScalingChanged(object? sender, EventArgs e)
    {
        UpdateWindowMinimumWidth();
    }

    private double GetRequiredWindowWorkspaceWidth()
    {
        if (!_viewModel.IsProjectLoaded)
            return DefaultWindowMinWidth;

        var minimumWidth = GetMinimumLeadingWorkspaceWidth();
        if (ShouldReserveSettingsWidth())
            minimumWidth += _effectiveSettingsPanelMinWidth + PreviewSettingsSplitterWidth;

        return minimumWidth;
    }

    private bool ShouldReserveSettingsWidth()
    {
        if (_settingsAnimating || _viewModel.SettingsVisible)
            return true;

        return HasVisibleSettingsPanelWidth();
    }

    private void UpdatePreviewToolbarPresentation(bool forceRefreshContent)
    {
        var nextLayoutMode = DeterminePreviewToolbarLayoutMode();
        if (nextLayoutMode != _previewToolbarLayoutMode)
        {
            _previewToolbarLayoutMode = nextLayoutMode;
            ApplyPreviewToolbarLayoutMode();
            forceRefreshContent = true;
        }

        if (forceRefreshContent)
            ApplyPreviewToolbarLabels();
    }

    private PreviewToolbarLayoutMode DeterminePreviewToolbarLayoutMode()
    {
        var previewBarWidth = _previewSegmentGrid?.Bounds.Width > 0
            ? _previewSegmentGrid.Bounds.Width
            : _previewBar?.Bounds.Width ?? _previewBarContainer?.Bounds.Width ?? 0;
        if (previewBarWidth <= 0)
            return _previewToolbarLayoutMode;

        if (previewBarWidth < PreviewToolbarCompactThreshold)
            return PreviewToolbarLayoutMode.Narrow;

        if (previewBarWidth < PreviewToolbarWideThreshold)
            return PreviewToolbarLayoutMode.Compact;

        return PreviewToolbarLayoutMode.Wide;
    }

    private void ApplyPreviewToolbarLayoutMode()
    {
        if (_previewBar is null)
            return;

        _previewBar.Classes.Remove("preview-toolbar-compact");
        _previewBar.Classes.Remove("preview-toolbar-narrow");

        switch (_previewToolbarLayoutMode)
        {
            case PreviewToolbarLayoutMode.Compact:
                _previewBar.Classes.Add("preview-toolbar-compact");
                break;

            case PreviewToolbarLayoutMode.Narrow:
                _previewBar.Classes.Add("preview-toolbar-compact");
                _previewBar.Classes.Add("preview-toolbar-narrow");
                break;
        }
    }

    private void ApplyPreviewToolbarLabels()
    {
        if (_previewTreeModeButton is null || _previewContentModeButton is null || _previewTreeAndContentModeButton is null)
            return;

        var useShortLabels = _previewToolbarLayoutMode != PreviewToolbarLayoutMode.Wide;
        _previewTreeModeButton.Content = useShortLabels ? _viewModel.PreviewModeTreeShort : _viewModel.PreviewModeTree;
        _previewContentModeButton.Content = useShortLabels ? _viewModel.PreviewModeContentShort : _viewModel.PreviewModeContent;
        _previewTreeAndContentModeButton.Content = useShortLabels ? _viewModel.PreviewModeTreeAndContentShort : _viewModel.PreviewModeTreeAndContent;
        ToolTip.SetTip(_previewTreeModeButton, null);
        ToolTip.SetTip(_previewContentModeButton, null);
        ToolTip.SetTip(_previewTreeAndContentModeButton, null);
    }

    private void UpdateToastHostLayout()
    {
        if (_toastHost is null)
            return;

        if (_toastHost.Parent is not Visual toastHostParent)
            return;

        var targetVisual = ResolveToastHostTarget();
        if (targetVisual is null)
        {
            ResetToastHostLayout();
            return;
        }

        var translatedOrigin = targetVisual.TranslatePoint(default, toastHostParent);
        if (translatedOrigin is null)
        {
            ResetToastHostLayout();
            return;
        }

        var targetWidth = targetVisual.Bounds.Width;
        if (targetWidth <= 1)
        {
            ResetToastHostLayout();
            return;
        }

        var horizontalInset = Math.Min(ToastHostHorizontalInset, targetWidth / 8);
        var hostWidth = Math.Max(0, targetWidth - (horizontalInset * 2));
        if (hostWidth <= 1)
        {
            ResetToastHostLayout();
            return;
        }

        _toastHost.HorizontalAlignment = HorizontalAlignment.Left;
        _toastHost.Width = hostWidth;
        _toastHost.MaxWidth = hostWidth;
        _toastHost.Margin = new Thickness(
            translatedOrigin.Value.X + horizontalInset,
            0,
            0,
            ToastHostBottomMargin);
    }

    private Control? ResolveToastHostTarget()
    {
        if (_viewModel.IsProjectLoaded)
        {
            if (_viewModel.IsPreviewTreeVisible)
                return _treeIsland;

            return _viewModel.IsPreviewMode
                ? _previewIsland
                : _treeIsland;
        }

        return _dropZoneContainer;
    }

    private void ResetToastHostLayout()
    {
        if (_toastHost is null)
            return;

        _toastHost.HorizontalAlignment = HorizontalAlignment.Center;
        _toastHost.Width = double.NaN;
        _toastHost.MaxWidth = double.PositiveInfinity;
        _toastHost.Margin = new Thickness(0, 0, 0, ToastHostBottomMargin);
    }

    private void CaptureSplitPaneLayout()
    {
        if (_previewPaneColumn is null)
            return;

        var treeWidth = ResolvePreviewTreePaneVisibleWidth();
        var previewWidth = _previewPaneColumn.ActualWidth;
        var totalWidth = treeWidth + previewWidth;
        if (treeWidth <= 0 || previewWidth <= 0 || totalWidth <= 0)
            return;

        _currentPreviewTreePaneWidth = treeWidth;
        _savedSplitTreeColumnWidth = new GridLength(treeWidth / totalWidth, GridUnitType.Star);
        _savedSplitPreviewColumnWidth = new GridLength(previewWidth / totalWidth, GridUnitType.Star);
    }

    private void NormalizeSplitPaneWidthsToStar()
    {
        if (!_viewModel.IsPreviewTreeVisible)
            return;

        CaptureSplitPaneLayout();
        if (_treePaneColumn is null || _previewPaneColumn is null)
            return;

        _treePaneColumn.Width = GridLength.Auto;
        _previewPaneColumn.Width = new GridLength(1, GridUnitType.Star);
        ApplyPreviewTreePaneWidth(ResolveDesiredPreviewTreePaneWidth(), animate: false);
    }

    private void AdjustSplitPaneWidthsForWindowResize(double widthDelta)
    {
        if (!_viewModel.IsPreviewTreeVisible)
            return;

        if (Math.Abs(widthDelta) < 0.5)
            return;

        var clampedWidth = GetClampedPreviewTreePaneWidth(_currentPreviewTreePaneWidth > 0.5
            ? _currentPreviewTreePaneWidth
            : ResolveDesiredPreviewTreePaneWidth());
        if (Math.Abs(clampedWidth - ResolvePreviewTreePaneVisibleWidth()) < 0.5)
            return;

        _currentPreviewTreePaneWidth = clampedWidth;
        ApplyPreviewTreePaneWidth(clampedWidth, animate: false);
    }

    private void EnsureSavedSplitPaneWidths()
    {
        if (!IsUsableSplitPaneWidth(_savedSplitTreeColumnWidth))
            _savedSplitTreeColumnWidth = new GridLength(5, GridUnitType.Star);

        if (!IsUsableSplitPaneWidth(_savedSplitPreviewColumnWidth))
            _savedSplitPreviewColumnWidth = new GridLength(6, GridUnitType.Star);
    }

    private static void SetWorkspacePaneState(
        ColumnDefinition column,
        bool visible,
        GridLength width,
        double minWidth)
    {
        column.MinWidth = visible ? minWidth : 0;
        column.Width = visible ? width : new GridLength(0);
    }

    private void ApplyPreviewTreePaneWidth(double width, bool animate)
    {
        if (_treePaneContainer is null)
            return;

        if (animate)
        {
            EnsurePreviewTreePaneTransitions();
            _treePaneContainer.Width = width;
            return;
        }

        var cachedTransitions = _treePaneContainer.Transitions;
        _treePaneContainer.Transitions = null;
        _treePaneContainer.Width = width;
        _treePaneContainer.Transitions = cachedTransitions;
    }

    private void ApplyPreviewPaneWidth(double width, bool animate)
    {
        if (_previewPaneContainer is null)
            return;

        if (animate)
        {
            EnsurePreviewPaneTransitions();
            _previewPaneContainer.Width = width;
            return;
        }

        var cachedTransitions = _previewPaneContainer.Transitions;
        _previewPaneContainer.Transitions = null;
        _previewPaneContainer.Width = width;
        _previewPaneContainer.Transitions = cachedTransitions;
    }

    private void EnsurePreviewPaneTransitions()
    {
        if (_previewPaneContainer is null)
            return;

        if (_previewPaneContainer.Transitions is null)
        {
            _previewPaneContainer.Transitions =
            [
                new DoubleTransition
                {
                    Property = WidthProperty,
                    Duration = PreviewPaneAnimationDuration,
                    Easing = new CubicEaseOut()
                }
            ];
        }
    }

    private double ResolvePreviewPaneVisibleWidth()
    {
        if (_previewPaneContainer is null)
            return 0;

        if (_previewPaneContainer.Width > 0.5)
            return _previewPaneContainer.Width;

        if (_previewPaneContainer.Bounds.Width > 0.5)
            return _previewPaneContainer.Bounds.Width;

        if (_previewPaneColumn is not null && _previewPaneColumn.ActualWidth > 0.5)
            return _previewPaneColumn.ActualWidth;

        return 0;
    }

    private double ResolveDesiredPreviewTreePaneWidth()
    {
        if (_currentPreviewTreePaneWidth > 0.5)
            return GetClampedPreviewTreePaneWidth(_currentPreviewTreePaneWidth);

        return ResolvePreviewTreePaneProjectedWidth();
    }

    private double ResolveDesiredPreviewPaneWidth(double desiredTreeWidth)
    {
        var availableSplitWidth = GetAvailableSplitWorkspaceWidth();
        if (availableSplitWidth <= 0.5)
            return SplitPreviewPaneMinWidth;

        return Math.Max(SplitPreviewPaneMinWidth, availableSplitWidth - desiredTreeWidth);
    }

    private double ResolvePreviewTreePaneProjectedWidth()
    {
        if (_workspaceGrid is null)
            return SplitTreePaneMinWidth;

        EnsureSavedSplitPaneWidths();

        var workspaceWidth = _workspaceGrid.Bounds.Width;
        if (workspaceWidth <= 0.5)
            return SplitTreePaneMinWidth;

        var settingsWidth = ShouldShowPreviewSettingsSplitter()
            ? GetVisibleSettingsPanelWidth() + PreviewSettingsSplitterWidth
            : 0.0;
        var availableWorkspaceWidth = Math.Max(0, workspaceWidth - settingsWidth);
        var availableSplitWidth = Math.Max(0, availableWorkspaceWidth - TreePreviewSplitterWidth);
        if (availableSplitWidth <= 0.5)
            return SplitTreePaneMinWidth;

        var treeWeight = IsUsableSplitPaneWidth(_savedSplitTreeColumnWidth)
            ? _savedSplitTreeColumnWidth.Value
            : 5.0;
        var previewWeight = IsUsableSplitPaneWidth(_savedSplitPreviewColumnWidth)
            ? _savedSplitPreviewColumnWidth.Value
            : 6.0;
        var totalWeight = treeWeight + previewWeight;
        if (totalWeight <= 0.001)
            return SplitTreePaneMinWidth;

        return GetClampedPreviewTreePaneWidth(availableSplitWidth * (treeWeight / totalWeight));
    }

    private double GetClampedPreviewTreePaneWidth(double desiredWidth)
    {
        var maxWidth = GetMaximumPreviewTreePaneWidth();
        if (maxWidth <= 0.5)
            return SplitTreePaneMinWidth;

        var minWidth = Math.Min(SplitTreePaneMinWidth, maxWidth);
        return Math.Clamp(desiredWidth, minWidth, maxWidth);
    }

    private double GetMaximumPreviewTreePaneWidth()
    {
        var availableSplitWidth = GetAvailableSplitWorkspaceWidth();
        return Math.Max(SplitTreePaneMinWidth, availableSplitWidth - SplitPreviewPaneMinWidth);
    }

    private double GetAvailableSplitWorkspaceWidth()
    {
        if (_workspaceGrid is null)
            return 0;

        var workspaceWidth = _workspaceGrid.Bounds.Width;
        if (workspaceWidth <= 0.5)
            return 0;

        var settingsWidth = ShouldShowPreviewSettingsSplitter()
            ? GetVisibleSettingsPanelWidth() + PreviewSettingsSplitterWidth
            : 0.0;
        var availableWorkspaceWidth = Math.Max(0, workspaceWidth - settingsWidth);
        return Math.Max(0, availableWorkspaceWidth - TreePreviewSplitterWidth);
    }

    private double GetAvailableTreeOnlyWorkspaceWidth()
    {
        if (_workspaceGrid is null)
            return SplitTreePaneMinWidth;

        var workspaceWidth = _workspaceGrid.Bounds.Width;
        if (workspaceWidth <= 0.5)
            return SplitTreePaneMinWidth;

        var settingsWidth = ShouldShowPreviewSettingsSplitter()
            ? GetVisibleSettingsPanelWidth() + PreviewSettingsSplitterWidth
            : 0.0;
        return Math.Max(SplitTreePaneMinWidth, workspaceWidth - settingsWidth);
    }

    private double GetAvailableWorkspaceWidthForSettingsAnimation(double settingsPanelWidth, bool includeSplitter)
    {
        if (_workspaceGrid is null)
            return 0;

        var workspaceWidth = _workspaceGrid.Bounds.Width;
        if (workspaceWidth <= 0.5)
            return 0;

        var reservedSettingsWidth = settingsPanelWidth > 0.5
            ? settingsPanelWidth + (includeSplitter ? PreviewSettingsSplitterWidth : 0.0)
            : 0.0;
        return Math.Max(0, workspaceWidth - reservedSettingsWidth);
    }

    private double ResolveTreeModeTargetWidthForSettingsAnimation(double settingsPanelWidth, bool includeSplitter)
    {
        var availableWidth = GetAvailableWorkspaceWidthForSettingsAnimation(settingsPanelWidth, includeSplitter);
        return availableWidth <= 0.5
            ? SplitTreePaneMinWidth
            : Math.Max(SplitTreePaneMinWidth, availableWidth);
    }

    private double ResolvePreviewOnlyTargetWidthForSettingsAnimation(double settingsPanelWidth, bool includeSplitter)
    {
        var availableWidth = GetAvailableWorkspaceWidthForSettingsAnimation(settingsPanelWidth, includeSplitter);
        return availableWidth <= 0.5
            ? SplitPreviewPaneMinWidth
            : Math.Max(SplitPreviewPaneMinWidth, availableWidth);
    }

    private double ResolvePreviewPaneTargetWidthForSettingsAnimation(
        double treePaneWidth,
        double settingsPanelWidth,
        bool includeSplitter)
    {
        var availableWorkspaceWidth = GetAvailableWorkspaceWidthForSettingsAnimation(settingsPanelWidth, includeSplitter);
        var availableSplitWidth = Math.Max(0, availableWorkspaceWidth - TreePreviewSplitterWidth);
        return availableSplitWidth <= 0.5
            ? SplitPreviewPaneMinWidth
            : Math.Max(SplitPreviewPaneMinWidth, availableSplitWidth - treePaneWidth);
    }

    private void SetSettingsAnimationPaneAnchors(WorkspaceDisplayMode displayMode, bool anchoredToLeftEdge)
    {
        switch (displayMode)
        {
            case WorkspaceDisplayMode.Tree:
                SetSettingsAnimationPaneAnchor(_treePaneContainer, anchoredToLeftEdge);
                break;

            case WorkspaceDisplayMode.PreviewOnly:
            case WorkspaceDisplayMode.PreviewWithTree:
                SetSettingsAnimationPaneAnchor(_previewPaneContainer, anchoredToLeftEdge);
                break;
        }
    }

    private static void SetSettingsAnimationPaneAnchor(Border? pane, bool anchoredToLeftEdge)
    {
        if (pane is null)
            return;

        // Explicit Width stops Stretch from controlling layout. While the settings panel is
        // animating, anchor the active pane to the left edge so the grid cannot recenter it.
        pane.HorizontalAlignment = anchoredToLeftEdge
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Stretch;
    }

    private void UpdatePreviewSettingsSplitterState()
    {
        if (_previewSettingsSplitterColumn is null)
            return;

        SetPreviewSettingsSplitterVisibility(ShouldShowPreviewSettingsSplitter());
    }

    private void SetPreviewSettingsSplitterVisibility(bool isVisible)
    {
        if (_previewSettingsSplitterColumn is null)
            return;

        _previewSettingsSplitterColumn.Width = new GridLength(isVisible ? PreviewSettingsSplitterWidth : 0);

        if (_previewSettingsSplitter is not null)
        {
            _previewSettingsSplitter.IsVisible = isVisible;
            _previewSettingsSplitter.IsHitTestVisible = isVisible;
        }
    }

    private bool ShouldShowPreviewSettingsSplitter()
    {
        if (!_viewModel.IsProjectLoaded)
            return false;

        if (_settingsAnimating)
            return true;

        if (_viewModel.SettingsVisible)
            return true;

        return HasVisibleSettingsPanelWidth();
    }

    private bool ShouldApplySettingsPanelWidthToVisual()
    {
        if (_settingsAnimating)
            return false;

        return HasVisibleSettingsPanelWidth();
    }

    private bool HasVisibleSettingsPanelWidth()
    {
        var containerWidth = _settingsContainer?.Width ?? 0;
        if (containerWidth > 0.5)
            return true;

        var actualWidth = _settingsContainer?.Bounds.Width ?? 0;
        return actualWidth > 0.5;
    }

    private void ClampSettingsPanelWidthToAvailableSpace(bool applyToVisual)
    {
        _currentSettingsPanelWidth = GetClampedSettingsPanelWidth(_currentSettingsPanelWidth);
        if (!applyToVisual || _settingsAnimating || _settingsContainer is null)
            return;

        if (!_viewModel.SettingsVisible && _settingsContainer.Width <= 0.5 && _settingsContainer.Bounds.Width <= 0.5)
            return;

        ApplySettingsPanelWidth(_currentSettingsPanelWidth, animate: false);
    }

    private double GetClampedSettingsPanelWidth(double desiredWidth)
    {
        var maxWidth = GetMaximumSettingsPanelWidth();
        if (maxWidth <= 0)
            return 0;

        var minWidth = Math.Min(_effectiveSettingsPanelMinWidth, maxWidth);
        return Math.Clamp(desiredWidth, minWidth, maxWidth);
    }

    private double GetMaximumSettingsPanelWidth()
    {
        if (_workspaceGrid is null)
            return SettingsPanelMaxWidth;

        var workspaceWidth = _workspaceGrid.Bounds.Width;
        if (workspaceWidth <= 0)
            return SettingsPanelMaxWidth;

        var reservedWidth = GetMinimumLeadingWorkspaceWidth() + PreviewSettingsSplitterWidth;
        var availableWidth = Math.Max(0, workspaceWidth - reservedWidth);
        var panelWidthCap = Math.Max(_effectiveSettingsPanelMinWidth, SettingsPanelMaxWidth);
        return Math.Min(panelWidthCap, availableWidth);
    }

    private double GetMinimumLeadingWorkspaceWidth()
    {
        return GetCurrentDisplayMode() switch
        {
            WorkspaceDisplayMode.PreviewWithTree => SplitTreePaneMinWidth + SplitPreviewPaneMinWidth + TreePreviewSplitterWidth,
            WorkspaceDisplayMode.PreviewOnly => SplitPreviewPaneMinWidth,
            _ => SplitTreePaneMinWidth
        };
    }

    private void ApplySettingsPanelWidth(double width, bool animate)
    {
        if (_settingsContainer is null)
            return;

        if (animate)
        {
            EnsureSettingsPanelTransitions();
            _settingsContainer.Width = width;
            return;
        }

        var cachedTransitions = _settingsContainer.Transitions;
        _settingsContainer.Transitions = null;
        _settingsContainer.Width = width;
        _settingsContainer.Transitions = cachedTransitions;
    }

    private double GetVisibleSettingsPanelWidth()
    {
        if (_settingsContainer is null)
            return _currentSettingsPanelWidth;

        if (_settingsContainer.Width > 0.5)
            return _settingsContainer.Width;

        if (_settingsContainer.Bounds.Width > 0.5)
            return _settingsContainer.Bounds.Width;

        return _currentSettingsPanelWidth;
    }

    // Custom resize handles avoid stale hover artifacts on transparent window surfaces
    // and let us clamp the settings pane independently from the split preview/tree layout.
    private void OnTreePreviewSplitterPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        BeginWorkspaceResize(sender as Border, e, WorkspaceResizeTarget.TreePreview);
    }

    private void OnPreviewSettingsSplitterPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        BeginWorkspaceResize(sender as Border, e, WorkspaceResizeTarget.PreviewSettings);
    }

    private void BeginWorkspaceResize(Border? splitter, PointerPressedEventArgs e, WorkspaceResizeTarget target)
    {
        if (splitter is null || _workspaceGrid is null)
            return;

        if (!e.GetCurrentPoint(splitter).Properties.IsLeftButtonPressed)
            return;

        if (target == WorkspaceResizeTarget.TreePreview && !_viewModel.IsPreviewTreeVisible)
            return;

        if (target == WorkspaceResizeTarget.PreviewSettings && !ShouldShowPreviewSettingsSplitter())
            return;

        CompleteActiveWorkspaceResize(releasePointer: false);

        _activeWorkspaceResizeTarget = target;
        _activeWorkspaceResizePointer = e.Pointer;
        _lastWorkspaceResizePointerX = e.GetPosition(_workspaceGrid).X;
        SetWorkspaceSplitterDraggingState(splitter, isDragging: true);
        e.Pointer.Capture(splitter);
        e.Handled = true;
    }

    private void OnWorkspaceSplitterPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_workspaceGrid is null || _activeWorkspaceResizeTarget == WorkspaceResizeTarget.None)
            return;

        if (!ReferenceEquals(e.Pointer, _activeWorkspaceResizePointer))
            return;

        var currentX = e.GetPosition(_workspaceGrid).X;
        var deltaX = currentX - _lastWorkspaceResizePointerX;
        if (Math.Abs(deltaX) < 0.01)
            return;

        _lastWorkspaceResizePointerX = currentX;

        switch (_activeWorkspaceResizeTarget)
        {
            case WorkspaceResizeTarget.TreePreview:
                ResizeTreePreviewPanes(deltaX);
                break;

            case WorkspaceResizeTarget.PreviewSettings:
                ResizeSettingsPane(deltaX);
                break;
        }

        e.Handled = true;
    }

    private void OnWorkspaceSplitterPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer, _activeWorkspaceResizePointer))
            return;

        CompleteActiveWorkspaceResize(releasePointer: true);
        e.Handled = true;
    }

    private void OnWorkspaceSplitterPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        CompleteActiveWorkspaceResize(releasePointer: false);
    }

    private void OnWorkspaceSplitterPointerExited(object? sender, PointerEventArgs e)
    {
        if (_activeWorkspaceResizeTarget != WorkspaceResizeTarget.None)
            return;

        ScheduleWorkspaceChromeRefresh();
    }

    private void ResizeTreePreviewPanes(double deltaX)
    {
        if (_treePaneContainer is null)
            return;

        var currentWidth = ResolvePreviewTreePaneVisibleWidth();
        var newTreeWidth = GetClampedPreviewTreePaneWidth(currentWidth + deltaX);
        if (Math.Abs(newTreeWidth - currentWidth) < 0.01)
            return;

        _currentPreviewTreePaneWidth = newTreeWidth;
        ApplyPreviewTreePaneWidth(newTreeWidth, animate: false);
        UpdatePreviewToolbarPresentation(forceRefreshContent: false);
        UpdateToastHostLayout();
    }

    private void ResizeSettingsPane(double deltaX)
    {
        if (_settingsAnimating || _settingsContainer is null)
            return;

        var currentWidth = GetVisibleSettingsPanelWidth();
        var desiredWidth = currentWidth - deltaX;
        var clampedWidth = GetClampedSettingsPanelWidth(desiredWidth);
        if (Math.Abs(clampedWidth - currentWidth) < 0.01)
            return;

        _currentSettingsPanelWidth = clampedWidth;
        if (!_viewModel.IsPreviewMode)
            _savedNonSplitSettingsPanelWidth = clampedWidth;
        ApplySettingsPanelWidth(clampedWidth, animate: false);
        UpdatePreviewToolbarPresentation(forceRefreshContent: false);
        UpdateToastHostLayout();
    }

    private void CompleteActiveWorkspaceResize(bool releasePointer)
    {
        if (_activeWorkspaceResizeTarget == WorkspaceResizeTarget.None)
            return;

        var activeTarget = _activeWorkspaceResizeTarget;
        var activePointer = _activeWorkspaceResizePointer;

        _activeWorkspaceResizeTarget = WorkspaceResizeTarget.None;
        _activeWorkspaceResizePointer = null;
        _lastWorkspaceResizePointerX = 0;

        SetWorkspaceSplitterDraggingState(_treePreviewSplitter, isDragging: false);
        SetWorkspaceSplitterDraggingState(_previewSettingsSplitter, isDragging: false);

        if (activeTarget == WorkspaceResizeTarget.TreePreview)
        {
            CaptureSplitPaneLayout();
            if (_treePaneColumn is not null)
                _treePaneColumn.Width = GridLength.Auto;
            if (_previewPaneColumn is not null)
                _previewPaneColumn.Width = new GridLength(1, GridUnitType.Star);
        }
        else if (activeTarget == WorkspaceResizeTarget.PreviewSettings)
        {
            ClampSettingsPanelWidthToAvailableSpace(applyToVisual: ShouldApplySettingsPanelWidthToVisual());
        }

        if (releasePointer)
            activePointer?.Capture(null);

        UpdatePreviewSettingsSplitterState();
        UpdateAdaptiveWorkspaceChrome();
        ScheduleWorkspaceChromeRefresh();
    }

    private static void SetWorkspaceSplitterDraggingState(Border? splitter, bool isDragging)
    {
        if (splitter is null)
            return;

        if (isDragging)
            splitter.Classes.Add(SplitterDraggingClass);
        else
            splitter.Classes.Remove(SplitterDraggingClass);
    }

    private void ScheduleWorkspaceChromeRefresh()
    {
        if (_workspaceChromeRefreshPending)
            return;

        _workspaceChromeRefreshPending = true;
        Dispatcher.Post(
            () =>
            {
                _workspaceChromeRefreshPending = false;
                _workspaceGrid?.InvalidateArrange();
                _workspaceGrid?.InvalidateVisual();
                _treeIsland?.InvalidateVisual();
                _previewIsland?.InvalidateVisual();
                _settingsContainer?.InvalidateVisual();
                _treePreviewSplitter?.InvalidateVisual();
                _previewSettingsSplitter?.InvalidateVisual();
                InvalidateVisual();
            },
            DispatcherPriority.Render);
    }

    private static bool IsUsableSplitPaneWidth(GridLength width)
    {
        if (width.IsAuto)
            return false;

        return width.GridUnitType switch
        {
            GridUnitType.Pixel => width.Value > 1,
            GridUnitType.Star => width.Value > 0,
            _ => false
        };
    }

    private void ApplySavedLanguagePreference(AppViewSettings settings)
    {
        var startupLanguage = ResolveStartupLanguage(
            _localization.CurrentLanguage,
            _startupOptions.Language,
            settings.PreferredLanguage);
        if (startupLanguage == _localization.CurrentLanguage)
            return;

        _localization.SetLanguage(startupLanguage);
        _viewModel.UpdateLocalization();
    }

    internal static AppLanguage ResolveStartupLanguage(
        AppLanguage currentLanguage,
        AppLanguage? commandLineLanguage,
        AppLanguage? preferredLanguage) =>
        commandLineLanguage ?? preferredLanguage ?? currentLanguage;

    private void HandleThemePopoverStateChange()
    {
        if (_wasThemePopoverOpen && !_viewModel.ThemePopoverOpen)
            PersistCurrentThemePreset();

        _wasThemePopoverOpen = _viewModel.ThemePopoverOpen;
    }

    private ThemePreset CreateCurrentThemePreset()
    {
        return new ThemePreset
        {
            BackgroundTransparency = _viewModel.BackgroundTransparency,
            PanelContrast = _viewModel.PanelContrast,
            MenuTransparency = _viewModel.MenuTransparency,
            BorderVisibility = _viewModel.BorderVisibility
        };
    }

    private void PersistCurrentThemePreset()
    {
        if (_themePresetSession is not { IsDirty: true } session)
            return;

        session.Persist(CreateCurrentThemePreset());
    }

    private void SaveCurrentViewSettings()
    {
        _userSettingsDb.ViewSettings = new AppViewSettings
        {
            IsCompactMode = _viewModel.IsCompactMode,
            IsTreeAnimationEnabled = _viewModel.IsTreeAnimationEnabled,
            IsTerminalCommandPromptDismissed = _userSettingsDb.ViewSettings?.IsTerminalCommandPromptDismissed ?? false,
            PreferredLanguage = _userSettingsDb.ViewSettings?.PreferredLanguage
        };

        _userSettingsStore.TryPersistViewSettings(_userSettingsDb);
    }

    private void SaveCurrentLanguageSetting()
    {
        var currentViewSettings = _userSettingsDb.ViewSettings ?? new AppViewSettings();
        _userSettingsDb.ViewSettings = currentViewSettings with
        {
            PreferredLanguage = _localization.CurrentLanguage
        };

        _userSettingsStore.TryPersistViewSettings(_userSettingsDb);
    }

    private void SetLanguageAndPersist(AppLanguage language)
    {
        _localization.SetLanguage(language);
        SaveCurrentLanguageSetting();
    }

    private ThemePresetVariant GetSelectedThemeVariant()
        => _viewModel.IsDarkTheme ? ThemePresetVariant.Dark : ThemePresetVariant.Light;

    private ThemePresetEffect GetSelectedEffectMode()
    {
        if (_viewModel.IsMicaEnabled)
            return ThemePresetEffect.Mica;
        if (_viewModel.IsAcrylicEnabled)
            return ThemePresetEffect.Acrylic;
        return _viewModel.IsTransparentEnabled
            ? ThemePresetEffect.Transparent
            : ThemePresetEffect.Solid;
    }

    private void InitializeFonts()
    {
        // Only use predefined fonts like WinForms
        var predefinedFonts = new[]
            { "Consolas", "Courier New", "Fira Code", "Lucida Console", "Cascadia Code", "JetBrains Mono" };

        var systemFonts = FontManager.Current?.SystemFonts;
        var predefinedFontSet = new HashSet<string>(predefinedFonts, StringComparer.OrdinalIgnoreCase);
        var availablePredefinedFonts = new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);
        if (systemFonts is not null)
        {
            foreach (var font in systemFonts)
            {
                if (predefinedFontSet.Contains(font.Name))
                    availablePredefinedFonts.TryAdd(font.Name, font);

                if (availablePredefinedFonts.Count == predefinedFonts.Length)
                    break;
            }
        }

        _viewModel.FontFamilies.Add(FontFamily.Default);

        // Add only predefined fonts that exist on system
        foreach (var fontName in predefinedFonts)
        {
            if (availablePredefinedFonts.TryGetValue(fontName, out var font))
                _viewModel.FontFamilies.Add(font);
        }

        if (_viewModel.FontFamilies.Count == 1 && systemFonts is not null)
        {
            foreach (var font in systemFonts
                         .DistinctBy(static font => font.Name, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(static font => font.Name, StringComparer.OrdinalIgnoreCase))
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
        _settingsPanel?.RequestMinimumWidthRefresh();
        RefreshTreeFontMenu();
        RefreshLanguageMenuChecks();
        UpdatePreviewToolbarPresentation(forceRefreshContent: true);
        _metrics.Recalculate(); // Update metrics text with new localization
        if (_hasPreviewSelectionMetricsSnapshot)
            RenderPreviewSelectionMetrics();
        if (_viewModel.IsAnyPreviewVisible)
            SchedulePreviewRefresh(immediate: true);
        UpdateTitle();
        UpdateToastHostLayout();

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
        if (!_viewModel.CanChangeProjectTree)
            return;

        try
        {
            var options = new FolderPickerOpenOptions
            {
                AllowMultiple = false,
                Title = _viewModel.MenuFileOpen
            };

            CancelBackgroundMemoryCleanup();
            _awaitingSystemDialogActivation = true;
            _systemDialogActivationTcs = null;
            var folders = await StorageProvider.OpenFolderPickerAsync(options);
            var folder = folders.FirstOrDefault();
            var path = folder?.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                _awaitingSystemDialogActivation = false;
                _systemDialogActivationTcs = null;
                return;
            }

            await WaitForWindowActivationAfterSystemDialogAsync();
            await TryOpenFolderAsync(path, fromDialog: true);
        }
        catch (OperationCanceledException)
        {
            _awaitingSystemDialogActivation = false;
            _systemDialogActivationTcs = null;
            // Cancellation is handled by status operation fallback.
        }
        catch (Exception ex)
        {
            _awaitingSystemDialogActivation = false;
            _systemDialogActivationTcs = null;
            await ShowErrorAsync(ex.Message);
        }
    }

    private async void OnOpenNewWindow(object? sender, RoutedEventArgs e)
    {
        var launchResult = _appInstanceLauncher.LaunchNewInstance();
        if (launchResult.Succeeded)
            return;

        var details = string.IsNullOrWhiteSpace(launchResult.ErrorMessage)
            ? "No launch candidate was available."
            : launchResult.ErrorMessage;
        await ShowErrorAsync(_localization.Format("Msg.NewWindowLaunchFailed", details));
    }

    private async void OnRefresh(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanChangeProjectTree)
            return;

        await ProjectRefreshRoutingPolicy.ExecuteAsync(
            _viewModel.IsProjectLoaded,
            _viewModel.ProjectSourceType,
            ReloadCurrentProjectAsync,
            GetGitUpdatesAsync);
        e.Handled = true;
    }

    private async Task ReloadCurrentProjectAsync()
    {
        CancelBackgroundMemoryCleanup();
        CancelPreviewRefresh();
        var refreshCts = ReplaceCancellationSource(ref _projectOperationCts);
        var cancellationToken = refreshCts.Token;
        var statusOperationId = _statusOperations.Begin(
            _viewModel.StatusOperationRefreshingProject,
            indeterminate: true,
            operationType: StatusOperationType.RefreshProject,
            cancelAction: () => refreshCts.Cancel());
        try
        {
            await ReloadProjectAsync(
                cancellationToken,
                applyStoredProfile: true,
                reuseUnchangedDiscoveryCaches: true);
            _statusOperations.Complete(statusOperationId);
            ScheduleBackgroundMemoryCleanup(MemoryCleanupReason.RefreshProject);
            _toastService.Show(_localization["Toast.Refresh.Success"]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _statusOperations.Complete(statusOperationId);
            _toastService.Show(_localization["Toast.Operation.RefreshCanceled"]);
        }
        catch (Exception ex)
        {
            _statusOperations.Complete(statusOperationId);
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
            _sessionMetrics.RecordClipboard("tree", format, content.Length, success: true);
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
            _metrics.CancelBackgroundCalculation();

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
            statusOperationId = BeginOutputPreparationStatus();
            var pathPresentation = CreateExportPathPresentation();
            var content = await Task.Run(() => _contentExport.BuildAsync(
                files,
                CancellationToken.None,
                pathPresentation?.MapFilePath));
            if (string.IsNullOrWhiteSpace(content))
            {
                CompleteStatusOperation(ref statusOperationId);
                await ShowInfoAsync(_localization["Msg.NoTextContent"]);
                return;
            }

            await SetClipboardTextAsync(content);
            _sessionMetrics.RecordClipboard("content", format: null, content.Length, success: true);
            CompleteStatusOperation(ref statusOperationId);
            _toastService.Show(_localization["Toast.Copy.Content"]);
        }
        catch (Exception ex)
        {
            CompleteStatusOperation(ref statusOperationId);
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
            _metrics.CancelBackgroundCalculation();

            var selected = GetCheckedPaths();
            var format = GetCurrentTreeTextFormat();
            var pathPresentation = CreateExportPathPresentation();
            // Run file reading off UI thread
            statusOperationId = BeginOutputPreparationStatus();
            var content = await Task.Run(() =>
                _treeAndContentExport.BuildAsync(
                    _currentPath!,
                    _currentTree!.Root,
                    selected,
                    format,
                    CancellationToken.None,
                    pathPresentation));
            await SetClipboardTextAsync(content);
            _sessionMetrics.RecordClipboard("tree-content", format, content.Length, success: true);
            CompleteStatusOperation(ref statusOperationId);
            _toastService.Show(_localization["Toast.Copy.TreeAndContent"]);
        }
        catch (Exception ex)
        {
            CompleteStatusOperation(ref statusOperationId);
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

            var saved = await TryExportTextToFileAsync(
                content,
                BuildSuggestedExportFileName("tree", GetTreeExportFileExtension(format)),
                _viewModel.MenuFileExportTree,
                defaultExtension: GetTreeExportFileExtension(format),
                fileTypeChoices: CreateTreeExportFileTypeChoices(format));

            if (saved)
            {
                _sessionMetrics.RecordFileExport("tree", format, content.Length, success: true);
                _toastService.Show(_localization["Toast.Export.Tree"]);
            }
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

            _metrics.CancelBackgroundCalculation();

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

            statusOperationId = BeginOutputPreparationStatus();
            var pathPresentation = CreateExportPathPresentation();
            var content = await Task.Run(() => _contentExport.BuildAsync(
                files,
                CancellationToken.None,
                pathPresentation?.MapFilePath));
            if (string.IsNullOrWhiteSpace(content))
            {
                CompleteStatusOperation(ref statusOperationId);
                await ShowInfoAsync(_localization["Msg.NoTextContent"]);
                return;
            }

            // File preparation is complete. The native picker can remain open indefinitely
            // while the user chooses a name, so it must never keep the app status busy.
            CompleteStatusOperation(ref statusOperationId);
            var saved = await TryExportTextToFileAsync(
                content,
                BuildSuggestedExportFileName("content", "txt"),
                _viewModel.MenuFileExportContent,
                defaultExtension: "txt",
                fileTypeChoices: [CreateTextFileType()]);

            if (saved)
            {
                _sessionMetrics.RecordFileExport("content", format: null, content.Length, success: true);
                _toastService.Show(_localization["Toast.Export.Content"]);
            }
        }
        catch (Exception ex)
        {
            CompleteStatusOperation(ref statusOperationId);
            await ShowErrorAsync(ex.Message);
        }
    }

    private async void OnExportTreeAndContentToFile(object? sender, RoutedEventArgs e)
    {
        long? statusOperationId = null;
        try
        {
            if (!EnsureTreeReady()) return;

            _metrics.CancelBackgroundCalculation();

            var selected = GetCheckedPaths();
            var format = GetCurrentTreeTextFormat();
            var pathPresentation = CreateExportPathPresentation();

            statusOperationId = BeginOutputPreparationStatus();
            var content = await Task.Run(() =>
                _treeAndContentExport.BuildAsync(
                    _currentPath!,
                    _currentTree!.Root,
                    selected,
                    format,
                    CancellationToken.None,
                    pathPresentation));

            // A combined payload is plain text even when its leading tree is JSON/XML/MD.
            // Finish the operation before opening the picker and keep the .txt contract.
            CompleteStatusOperation(ref statusOperationId);
            var saved = await TryExportTextToFileAsync(
                content,
                BuildSuggestedExportFileName("tree_content", "txt"),
                _viewModel.MenuFileExportTreeAndContent,
                defaultExtension: "txt",
                fileTypeChoices: [CreateTextFileType()]);

            if (saved)
            {
                _sessionMetrics.RecordFileExport("tree-content", format, content.Length, success: true);
                _toastService.Show(_localization["Toast.Export.TreeAndContent"]);
            }
        }
        catch (Exception ex)
        {
            CompleteStatusOperation(ref statusOperationId);
            await ShowErrorAsync(ex.Message);
        }
    }

    private TreeTextFormat GetCurrentTreeTextFormat()
        => _viewModel.SelectedExportFormat switch
        {
            ExportFormat.Json => TreeTextFormat.Json,
            ExportFormat.Xml => TreeTextFormat.Xml,
            ExportFormat.Markdown => TreeTextFormat.Markdown,
            _ => TreeTextFormat.Ascii
        };

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

    private static string MapExportDisplayPath(string filePath, Func<string, string>? mapFilePath)
    {
        if (mapFilePath is null)
            return filePath;

        try
        {
            var mapped = mapFilePath(filePath);
            return string.IsNullOrWhiteSpace(mapped) ? filePath : mapped;
        }
        catch
        {
            return filePath;
        }
    }

    private void SchedulePreviewRefresh(bool immediate = false)
    {
        _previewPipeline.ScheduleRefresh(immediate);
    }

    private void CancelPreviewRefresh()
    {
        _previewPipeline.CancelRefresh();
    }

    private void OnPreviewTextScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_previewScrollSyncActive)
            return;

        if (sender is not ScrollViewer textScrollViewer)
            return;

        if (_previewTextControl is not null)
        {
            _previewTextControl.HorizontalOffset = Math.Max(0, textScrollViewer.Offset.X);
            _previewTextControl.VerticalOffset = Math.Max(0, textScrollViewer.Offset.Y);
            _previewTextControl.ViewportHeight = Math.Max(0, textScrollViewer.Viewport.Height);
            _previewTextControl.ViewportWidth = Math.Max(0, textScrollViewer.Viewport.Width);
        }

        if (_previewLineNumbersControl is null)
            return;

        _previewLineNumbersControl.ExtentHeight = Math.Max(0, textScrollViewer.Extent.Height);
        _previewLineNumbersControl.ViewportHeight = Math.Max(0, textScrollViewer.Viewport.Height);

        var targetY = textScrollViewer.Offset.Y;
        var currentY = _previewLineNumbersControl.VerticalOffset;
        if (Math.Abs(currentY - targetY) >= 0.1)
        {
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

        UpdatePreviewStickyPath();
    }

    private void UpdatePreviewStickyPath()
    {
        if (!TryGetCurrentPreviewStickySection(out var currentSection))
        {
            HidePreviewStickyPath();
            return;
        }

        if (_previewStickyHeaderText is not null)
            _previewStickyHeaderText.Text = currentSection.DisplayPath;

        if (_previewStickyHeaderContainer is not null)
            _previewStickyHeaderContainer.IsVisible = true;

        if (_previewStickyHeaderCap is not null)
            _previewStickyHeaderCap.IsVisible = true;

        SetPreviewStickyHeaderClipHeight(ResolvePreviewStickyHeaderOverlayHeight());

        if (_previewTextControl is not null)
        {
            _previewTextControl.StickyHeaderReserved = false;
            _previewTextControl.StickyHeaderVisible = false;
            _previewTextControl.StickyHeaderText = string.Empty;
        }

        if (_previewLineNumbersControl is not null)
        {
            _previewLineNumbersControl.StickyHeaderReserved = false;
            _previewLineNumbersControl.StickyHeaderVisible = false;
        }
    }

    private void OnPreviewToolTipLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToolTip toolTip)
            return;

        ApplyPreviewToolTipBackdrop(toolTip);
    }

    private async void OnPreviewCopyVisibleFilePath(object? sender, RoutedEventArgs e)
    {
        if (!await WaitForPreviewClipboardSourceReadyAsync().ConfigureAwait(true) ||
            !TryBuildCurrentPreviewStickySectionCopyPayload(out var sectionPayload))
        {
            return;
        }

        await CopyPreviewVisibleFilePathAsync(sectionPayload);
    }

    private async Task CopyPreviewVisibleFilePathAsync(string text)
    {
        try
        {
            await SetClipboardTextAsync(text);
            _toastService.Show(_localization["Toast.Copy.Preview"]);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    private bool TryGetCurrentPreviewStickySection(out PreviewDocumentSection currentSection)
    {
        currentSection = null!;
        if (_previewTextControl is null || !_viewModel.IsAnyPreviewVisible)
            return false;

        var document = _previewTextControl.Document ?? _viewModel.PreviewDocument;
        if (document?.Sections is not { Count: > 0 } sections)
            return false;

        var verticalOffset = _previewTextScrollViewer?.Offset.Y ?? _previewTextControl.VerticalOffset;
        var topLine = _previewTextControl.GetLineNumberAtVerticalOffset(verticalOffset);
        if (topLine < sections[0].StartLine)
            return false;

        currentSection = PreviewDocumentSectionLookup.FindContainingSection(sections, topLine) ??
                         PreviewDocumentSectionLookup.FindContainingOrNextSection(sections, topLine) ??
                         sections[^1];

        return currentSection is not null;
    }

    private bool TryBuildCurrentPreviewStickySectionCopyPayload(out string sectionPayload)
    {
        sectionPayload = string.Empty;

        if (!TryGetCurrentPreviewStickySection(out var currentSection) ||
            _previewTextControl is null)
        {
            return false;
        }

        var document = _previewTextControl.Document ?? _viewModel.PreviewDocument;
        if (document is null)
            return false;

        sectionPayload = PreviewClipboardPayloadBuilder.BuildSectionPayload(document, currentSection);
        return !string.IsNullOrWhiteSpace(sectionPayload);
    }

    private bool TryBuildCurrentPreviewCopyPayload(out string previewPayload)
    {
        previewPayload = string.Empty;

        var document = _previewTextControl?.Document ?? _viewModel.PreviewDocument;
        if (document is null)
            return false;

        previewPayload = PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(document);
        return !string.IsNullOrWhiteSpace(previewPayload);
    }

    private async Task<bool> WaitForPreviewClipboardSourceReadyAsync()
    {
        if (!_viewModel.IsAnyPreviewVisible)
            return false;

        if (!_viewModel.IsPreviewLoading)
            return (_previewTextControl?.Document ?? _viewModel.PreviewDocument) is not null;

        var timeout = TimeSpan.FromSeconds(10);
        var stopwatch = Stopwatch.StartNew();

        while (_viewModel.IsAnyPreviewVisible &&
               _viewModel.IsPreviewLoading &&
               stopwatch.Elapsed < timeout)
        {
            await YieldUiAsync(DispatcherPriority.Background);
            await Task.Delay(15).ConfigureAwait(true);
        }

        return !_viewModel.IsPreviewLoading &&
               (_previewTextControl?.Document ?? _viewModel.PreviewDocument) is not null;
    }

    private void ApplyPreviewToolTipBackdrop(ToolTip toolTip)
    {
        PopupBackdropConfigurator.TryApply(
            toolTip,
            GetTopLevel(this),
            _viewModel.ActiveThemeEffect,
            PopupBackdropTransparencyFallback.Transparent);
    }

    private void HidePreviewStickyPath()
    {
        if (_previewStickyHeaderText is not null)
            _previewStickyHeaderText.Text = string.Empty;

        if (_previewStickyHeaderContainer is not null)
            _previewStickyHeaderContainer.IsVisible = false;

        if (_previewStickyHeaderCap is not null)
            _previewStickyHeaderCap.IsVisible = false;

        SetPreviewStickyHeaderClipHeight(0);

        if (_previewTextControl is not null)
        {
            _previewTextControl.StickyHeaderReserved = false;
            _previewTextControl.StickyHeaderVisible = false;
            _previewTextControl.StickyHeaderText = string.Empty;
        }

        if (_previewLineNumbersControl is not null)
        {
            _previewLineNumbersControl.StickyHeaderReserved = false;
            _previewLineNumbersControl.StickyHeaderVisible = false;
        }
    }

    private void SetPreviewStickyHeaderClipHeight(double height)
    {
        var normalizedHeight = Math.Max(0, height);

        if (_previewTextControl is not null)
            _previewTextControl.TopOverlayClipHeight = normalizedHeight;

        if (_previewLineNumbersControl is not null)
            _previewLineNumbersControl.TopOverlayClipHeight = normalizedHeight;
    }

    private double ResolvePreviewStickyHeaderOverlayHeight()
    {
        var fontSize = _previewTextControl?.TextFontSize ?? _viewModel.PreviewFontSize;
        return Math.Max(24.0, Math.Ceiling(fontSize + 12.0));
    }

    private void OnPreviewScrollViewerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_previewTextScrollViewer is null ||
            !e.GetCurrentPoint(_previewTextScrollViewer).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is Visual sourceVisual)
        {
            if (sourceVisual is VirtualizedPreviewTextControl ||
                sourceVisual.FindAncestorOfType<VirtualizedPreviewTextControl>() is not null)
            {
                return;
            }

            if (sourceVisual is ScrollBar or Thumb or RepeatButton ||
                sourceVisual.FindAncestorOfType<ScrollBar>() is not null)
            {
                return;
            }
        }

        if (_previewTextControl is null)
            return;

        var viewportPoint = e.GetPosition(_previewTextScrollViewer);
        var handledByPreview = _previewTextControl.TryHandleViewportSelectionStart(
            e.Pointer,
            viewportPoint,
            e.KeyModifiers);

        if (!handledByPreview)
            _previewTextControl.ClearSelection();

        e.Handled = true;
    }

    private void OnPreviewCopiedToClipboard(object? sender, EventArgs e)
    {
        if (_viewModel.IsAnyPreviewVisible)
            _toastService.Show(_localization["Toast.Copy.Preview"]);
    }

    private void OnPreviewSelectionChanged(object? sender, EventArgs e)
    {
        SchedulePreviewSelectionMetricsUpdate();
    }

    private async Task<PreviewWarmupSnapshot?> TryBuildPreviewWarmupSnapshotAsync(
        PreviewContentMode mode,
        TreeTextFormat treeFormat,
        bool hasSelection,
        IReadOnlySet<string> selectedPaths,
        string? currentPath,
        TreeNodeDescriptor? currentTreeRoot,
        ExportPathPresentation? pathPresentation,
        string noTextContentText,
        string noCheckedFilesText,
        CancellationToken cancellationToken)
    {
        if (!PreviewWarmupPolicy.ShouldBuildPreviewWarmup(mode, hasSelection, selectedPaths, currentTreeRoot))
            return null;

        return await Task.Run<PreviewWarmupSnapshot?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var files = PreviewWarmupPolicy.CollectInitialPreviewFiles(
                selectedPaths: selectedPaths,
                hasSelection: hasSelection,
                treeRoot: currentTreeRoot,
                maxFileCount: PreviewWarmupFileLimit);

            if (mode == PreviewContentMode.Content)
            {
                if (files.Count == 0)
                {
                    var fallbackText = hasSelection ? noCheckedFilesText : noTextContentText;
                    return new PreviewWarmupSnapshot(
                        fallbackText,
                        PreviewFileCollectionPolicy.CountPreviewLines(fallbackText));
                }

                var contentText = _contentExport.BuildAsync(
                    files,
                    cancellationToken,
                    pathPresentation?.MapFilePath).GetAwaiter().GetResult();

                if (string.IsNullOrWhiteSpace(contentText))
                    contentText = noTextContentText;

                return new PreviewWarmupSnapshot(
                    contentText,
                    PreviewFileCollectionPolicy.CountPreviewLines(contentText));
            }

            if (mode == PreviewContentMode.TreeAndContent &&
                !string.IsNullOrWhiteSpace(currentPath) &&
                currentTreeRoot is not null)
            {
                var treeText = selectedPaths.Count > 0
                    ? _treeExport.BuildSelectedTree(
                        currentPath,
                        currentTreeRoot,
                        selectedPaths,
                        treeFormat,
                        pathPresentation?.DisplayRootPath,
                        pathPresentation?.DisplayRootName)
                    : _treeExport.BuildFullTree(
                        currentPath,
                        currentTreeRoot,
                        treeFormat,
                        pathPresentation?.DisplayRootPath,
                        pathPresentation?.DisplayRootName);

                if (selectedPaths.Count > 0 && string.IsNullOrWhiteSpace(treeText))
                {
                    treeText = _treeExport.BuildFullTree(
                        currentPath,
                        currentTreeRoot,
                        treeFormat,
                        pathPresentation?.DisplayRootPath,
                        pathPresentation?.DisplayRootName);
                }

                if (string.IsNullOrWhiteSpace(treeText))
                    return null;

                if (files.Count == 0)
                {
                    return new PreviewWarmupSnapshot(
                        treeText,
                        PreviewFileCollectionPolicy.CountPreviewLines(treeText));
                }

                var contentText = _contentExport.BuildAsync(
                    files,
                    cancellationToken,
                    TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(currentPath)).GetAwaiter().GetResult();

                if (string.IsNullOrWhiteSpace(contentText))
                {
                    return new PreviewWarmupSnapshot(
                        treeText,
                        PreviewFileCollectionPolicy.CountPreviewLines(treeText));
                }

                var combinedBuilder = new StringBuilder(treeText.Length + contentText.Length + 16);
                combinedBuilder.Append(treeText.TrimEnd('\r', '\n'));
                combinedBuilder.AppendLine("\u00A0");
                combinedBuilder.AppendLine("\u00A0");
                combinedBuilder.Append(contentText);
                var combinedText = combinedBuilder.ToString();

                return new PreviewWarmupSnapshot(
                    combinedText,
                    PreviewFileCollectionPolicy.CountPreviewLines(combinedText));
            }

            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static bool ShouldBuildPreviewWarmup(
        PreviewContentMode mode,
        bool hasSelection,
        IReadOnlySet<string> selectedPaths,
        TreeNodeDescriptor? treeRoot) =>
        PreviewWarmupPolicy.ShouldBuildPreviewWarmup(mode, hasSelection, selectedPaths, treeRoot);

    private void ApplyPreviewText(string text)
    {
        var effectiveText = string.IsNullOrEmpty(text)
            ? _viewModel.PreviewNoDataText
            : text;

        ApplyPreviewText(effectiveText, PreviewFileCollectionPolicy.CountPreviewLines(effectiveText));
    }

    private void ApplyPreviewText(string text, int lineCount)
    {
        InvalidatePreviewCache();
        ApplyPreviewDocument(_previewDocumentBuilder.CreateInMemory(text), lineCount);
    }

    private void ApplyPreviewDocument(IPreviewTextDocument document)
    {
        ApplyPreviewDocument(document, document.LineCount);
    }

    private void ApplyPreviewDocument(IPreviewTextDocument document, int lineCount)
    {
        ClearPreviewSelectionMetrics();
        var previousDocument = _viewModel.PreviewDocument;
        _viewModel.PreviewDocument = document;
        _viewModel.PreviewText = string.Empty;
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

        if (_previewTextControl is not null)
        {
            _previewTextControl.VerticalOffset = 0;
            if (_previewTextScrollViewer is not null)
            {
                _previewTextControl.ViewportHeight = Math.Max(0, _previewTextScrollViewer.Viewport.Height);
                _previewTextControl.ViewportWidth = Math.Max(0, _previewTextScrollViewer.Viewport.Width);
            }
        }

        if (!ReferenceEquals(previousDocument, document))
            previousDocument?.Dispose();

        UpdatePreviewStickyPath();
        Dispatcher.Post(UpdatePreviewStickyPath, DispatcherPriority.Render);
    }

    private void ClearPreviewDocument()
    {
        ClearPreviewSelectionMetrics();
        var previousDocument = _viewModel.PreviewDocument;
        _viewModel.PreviewDocument = null;
        _viewModel.PreviewText = string.Empty;
        _viewModel.PreviewLineCount = 1;
        previousDocument?.Dispose();
        HidePreviewStickyPath();
    }

    private static int CountPreviewLines(string text) => PreviewFileCollectionPolicy.CountPreviewLines(text);

    private PreviewBuildResult BuildPreviewDocument(
        PreviewContentMode selectedMode,
        IReadOnlySet<string> selectedPaths,
        bool hasSelection,
        TreeTextFormat treeFormat,
        string noCheckedFilesText,
        string noTextContentText,
        string noDataText,
        string? currentPath,
        TreeNodeDescriptor? currentTreeRoot,
        IReadOnlyList<string>? currentTreeOrderedFilePaths,
        ExportPathPresentation? pathPresentation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (selectedMode == PreviewContentMode.Tree)
        {
            var treePreviewText = BuildTreeTextForSelection(selectedPaths, treeFormat);
            var effectiveTreeText = string.IsNullOrEmpty(treePreviewText) ? noDataText : treePreviewText;
            return new PreviewBuildResult(_previewDocumentBuilder.CreateInMemory(effectiveTreeText));
        }

        var files = hasSelection
            ? currentTreeRoot is not null
                ? BuildOrderedSelectedFilePaths(currentTreeRoot, selectedPaths)
                : []
            : currentTreeRoot is not null
                ? currentTreeOrderedFilePaths ?? _metrics.GetOrBuildAllOrderedFilePaths(currentTreeRoot)
                : [];

        if (selectedMode == PreviewContentMode.Content)
        {
            if (files.Count == 0)
            {
                var fallbackText = hasSelection ? noCheckedFilesText : noTextContentText;
                return new PreviewBuildResult(_previewDocumentBuilder.CreateInMemory(fallbackText));
            }

            var contentDocument = _previewDocumentBuilder.BuildContentDocumentAsync(
                files,
                cancellationToken,
                pathPresentation?.MapFilePath).GetAwaiter().GetResult();

            return new PreviewBuildResult(contentDocument ?? _previewDocumentBuilder.CreateInMemory(noTextContentText));
        }

        if (string.IsNullOrWhiteSpace(currentPath) || currentTreeRoot is null)
            return new PreviewBuildResult(_previewDocumentBuilder.CreateInMemory(noTextContentText));

        var treeText = selectedPaths.Count > 0
            ? _treeExport.BuildSelectedTree(
                currentPath,
                currentTreeRoot,
                selectedPaths,
                treeFormat,
                pathPresentation?.DisplayRootPath,
                pathPresentation?.DisplayRootName)
            : _treeExport.BuildFullTree(
                currentPath,
                currentTreeRoot,
                treeFormat,
                pathPresentation?.DisplayRootPath,
                pathPresentation?.DisplayRootName);

        if (selectedPaths.Count > 0 && string.IsNullOrWhiteSpace(treeText))
        {
            treeText = _treeExport.BuildFullTree(
                currentPath,
                currentTreeRoot,
                treeFormat,
                pathPresentation?.DisplayRootPath,
                pathPresentation?.DisplayRootName);
        }

        if (string.IsNullOrWhiteSpace(treeText))
            return new PreviewBuildResult(_previewDocumentBuilder.CreateInMemory(noDataText));

        if (files.Count == 0)
            return new PreviewBuildResult(_previewDocumentBuilder.CreateInMemory(treeText));

        var document = _previewDocumentBuilder.BuildTreeAndContentDocumentAsync(
            treeText,
            files,
            cancellationToken,
            TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(currentPath)).GetAwaiter().GetResult();

        return new PreviewBuildResult(document);
    }

    private static List<string> CollectOrderedPreviewFiles(
        IReadOnlySet<string> selectedPaths,
        bool hasSelection,
        TreeNodeDescriptor? treeRoot) =>
        PreviewFileCollectionPolicy.CollectOrderedPreviewFiles(selectedPaths, hasSelection, treeRoot);

    private static PreviewCacheKeyData BuildPreviewCacheKey(
        string? projectPath,
        TreeNodeDescriptor? treeRoot,
        PreviewContentMode mode,
        TreeTextFormat treeFormat,
        IReadOnlySet<string> selectedPaths)
    {
        return new PreviewCacheKeyData(
            ProjectPath: projectPath,
            TreeIdentity: treeRoot is null ? 0 : RuntimeHelpers.GetHashCode(treeRoot),
            Mode: mode,
            TreeFormat: treeFormat,
            SelectedCount: selectedPaths.Count,
            SelectedHash: PreviewFileCollectionPolicy.BuildPathSetHash(selectedPaths));
    }

    private void UpdateTreeVisualResources()
    {
        if (_treeView is null)
            return;

        _treeView.Resources[TreeItemPaddingResourceKey] = _viewModel.TreeItemPadding;
        _treeView.Resources[TreeItemSpacingResourceKey] = _viewModel.TreeItemSpacing;
        _treeView.Resources[TreeIconSizeResourceKey] = _viewModel.TreeIconSize;
        _treeView.Resources[TreeTextMarginResourceKey] = _viewModel.TreeTextMargin;
    }

    private static int BuildPathSetHash(IReadOnlySet<string> selectedPaths) =>
        PreviewFileCollectionPolicy.BuildPathSetHash(selectedPaths);

    private bool IsCurrentPreviewCacheHit(PreviewCacheKeyData key)
        => _previewPipeline.IsCurrentCacheHit(key, _viewModel.PreviewDocument);

    private void CachePreview(PreviewCacheKeyData key) => _previewPipeline.CachePreview(key);

    private void InvalidatePreviewCache()
    {
        _previewPipeline.InvalidateCache();
    }

    private static bool ShouldForcePreviewMemoryCleanup(long textLength, int lineCount) =>
        PreviewFileCollectionPolicy.ShouldForcePreviewMemoryCleanup(textLength, lineCount);

    private async Task<bool> TryExportTextToFileAsync(
        string content,
        string suggestedFileName,
        string dialogTitle,
        string defaultExtension,
        IReadOnlyList<FilePickerFileType> fileTypeChoices)
    {
        if (StorageProvider is null || string.IsNullOrWhiteSpace(content))
            return false;

        var options = new FilePickerSaveOptions
        {
            Title = dialogTitle,
            SuggestedFileName = suggestedFileName,
            ShowOverwritePrompt = true,
            DefaultExtension = defaultExtension,
            FileTypeChoices = fileTypeChoices
        };

        var file = await StorageProvider.SaveFilePickerAsync(options);
        if (file is null)
            return false;

        await using var stream = await file.OpenWriteAsync();
        await _textFileExport.WriteAsync(stream, content);

        return true;
    }

    private static IReadOnlyList<FilePickerFileType> CreateTreeExportFileTypeChoices(TreeTextFormat format)
    {
        if (format == TreeTextFormat.Ascii)
            return [CreateTextFileType()];

        var nativeFileType = format switch
        {
            TreeTextFormat.Json => CreateJsonFileType(),
            TreeTextFormat.Xml => CreateXmlFileType(),
            TreeTextFormat.Markdown => CreateMarkdownFileType(),
            _ => CreateTextFileType()
        };

        // Structured tree text is also useful as a generic text artifact. Keep the native
        // extension first while offering TXT as an explicit, semantically honest fallback.
        return [nativeFileType, CreateTextFileType()];
    }

    private static FilePickerFileType CreateTextFileType()
        => new("TXT")
        {
            Patterns = ["*.txt"],
            MimeTypes = ["text/plain"]
        };

    private static FilePickerFileType CreateJsonFileType()
        => new("JSON")
        {
            Patterns = ["*.json"],
            MimeTypes = ["application/json"]
        };

    private static FilePickerFileType CreateXmlFileType()
        => new("XML")
        {
            Patterns = ["*.xml"],
            MimeTypes = ["application/xml", "text/xml"]
        };

    private static FilePickerFileType CreateMarkdownFileType()
        => new("Markdown")
        {
            Patterns = ["*.md"],
            MimeTypes = ["text/markdown", "text/plain"]
        };

    private static string GetTreeExportFileExtension(TreeTextFormat format)
        => format switch
        {
            TreeTextFormat.Json => "json",
            TreeTextFormat.Xml => "xml",
            TreeTextFormat.Markdown => "md",
            _ => "txt"
        };

    private void CompleteStatusOperation(ref long? operationId)
    {
        if (!operationId.HasValue)
            return;

        _statusOperations.Complete(operationId.Value);
        operationId = null;
    }

    private long? BeginOutputPreparationStatus()
    {
        // Clipboard and text-file exports are safe against the captured project tree, but
        // they must not replace the progress or cancellation action of a physical export.
        if (_viewModel.IsProjectCopyExportInProgress)
            return null;

        return _statusOperations.Begin(
            _localization["Status.Operation.PreparingOutput"],
            indeterminate: true);
    }

    private string BuildSuggestedExportFileName(string suffix, string extension)
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

        return $"{sanitized}_{suffix}.{extension}";
    }

    private void OnExpandAll(object? sender, RoutedEventArgs e)
    {
        // A pending compacting collection must not interrupt mass lazy-node realization.
        CancelBackgroundMemoryCleanup();
        ExpandCollapseTree(expand: true);
    }

    private void OnCollapseAll(object? sender, RoutedEventArgs e)
    {
        ExpandCollapseTree(expand: false);

        // Collapsing makes realized row containers recyclable. Wait for layout to detach them
        // before compacting the managed heap and trimming the native working set.
        ScheduleBackgroundMemoryCleanup(MemoryCleanupReason.TreeCollapseCompleted);
    }

    private void ExpandCollapseTree(bool expand)
    {
        if (!_viewModel.IsTreePaneVisible)
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
        if (_viewModel.IsPreviewTreeVisible)
        {
            ResetTreeZoom();
            ResetPreviewZoom();
            return;
        }

        if (_viewModel.IsAnyPreviewVisible)
        {
            ResetPreviewZoom();
            return;
        }

        ResetTreeZoom();
    }

    private void AdjustZoomFontSize(double delta, ZoomSurfaceTarget? target = null)
    {
        if (_viewModel.IsPreviewTreeVisible && target is null)
        {
            _viewModel.TreeFontSize = ClampZoomFontSize(_viewModel.TreeFontSize + delta);
            _viewModel.PreviewFontSize = ClampZoomFontSize(_viewModel.PreviewFontSize + delta);
            return;
        }

        var effectiveTarget = target ?? (_viewModel.IsAnyPreviewVisible ? ZoomSurfaceTarget.Preview : ZoomSurfaceTarget.Tree);
        if (effectiveTarget == ZoomSurfaceTarget.Preview)
        {
            _viewModel.PreviewFontSize = ClampZoomFontSize(_viewModel.PreviewFontSize + delta);
            return;
        }

        _viewModel.TreeFontSize = ClampZoomFontSize(_viewModel.TreeFontSize + delta);
    }

    private static double ClampZoomFontSize(double value) => Math.Clamp(value, 6, 28);

    private void ResetTreeZoom() => _viewModel.TreeFontSize = MainWindowViewModel.DefaultTreeFontSize;

    private void ResetPreviewZoom() => _viewModel.PreviewFontSize = MainWindowViewModel.DefaultPreviewFontSize;

    private void PreparePreviewPane()
    {
        if (_previewFontInitialized)
            return;

        _viewModel.PreviewFontSize = _viewModel.TreeFontSize;
        _previewFontInitialized = true;
    }

    private void OnToggleSettings(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsProjectLoaded) return;
        if (_settingsAnimating) return;

        var newVisible = !_viewModel.SettingsVisible;
        _viewModel.SettingsVisible = newVisible;
        ObserveDetachedTask(
            AnimateSettingsPanelAsync(newVisible),
            "AnimateSettingsPanel");
    }

    private void OnTogglePreview(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanUseProjectWorkspaceActions)
            return;

        if (_viewModel.IsPreviewMode)
            ClosePreviewMode();
        else
            OpenPreviewMode();
    }

    private void OnPreviewClose(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanUseProjectWorkspaceActions)
            return;

        ClosePreviewMode();
    }

    private async void OnPreviewCopyCurrentMode(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsProjectLoaded || !_viewModel.IsAnyPreviewVisible)
            return;

        if (!await WaitForPreviewClipboardSourceReadyAsync().ConfigureAwait(true) ||
            !TryBuildCurrentPreviewCopyPayload(out var previewPayload))
        {
            return;
        }

        try
        {
            await SetClipboardTextAsync(previewPayload);

            var toastKey = _viewModel.SelectedPreviewContentMode switch
            {
                PreviewContentMode.Tree => "Toast.Copy.Tree",
                PreviewContentMode.Content => "Toast.Copy.Content",
                _ => "Toast.Copy.TreeAndContent"
            };

            _toastService.Show(_localization[toastKey]);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    private void OnPreviewTreeHide(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanUseProjectWorkspaceActions || !_viewModel.IsPreviewTreeVisible)
            return;

        HidePreviewTreePane();
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
        if (!_viewModel.CanUseProjectWorkspaceActions ||
            _viewModel.SelectedPreviewContentMode == targetMode)
            return;

        var switchCts = ReplaceCancellationSource(ref _previewModeSwitchCts);
        var switchVersion = Interlocked.Increment(ref _previewModeSwitchVersion);
        _previewModeSwitchInProgress = true;

        try
        {
            // Cancel any in-flight preview work so stale content cannot render.
            _previewPipeline.CancelActiveBuildAndInvalidate();

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
            _previewPipeline.MarkClearBeforeNextRefresh();
            SchedulePreviewRefresh(immediate: true);
            // Restore keyboard shortcuts to the preview surface after the mode button steals focus.
            Dispatcher.Post(FocusPreviewSurface, DispatcherPriority.Background);
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

        _previewSegmentThumbTransform.Transitions =
        [
            new DoubleTransition
            {
                Property = TranslateTransform.XProperty,
                Duration = PreviewSegmentThumbAnimationDuration,
                Easing = new CubicEaseInOut()
            }
        ];
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
        await OpenPreviewModeAsync();
    }

    private async Task OpenPreviewModeAsync()
    {
        if (!_viewModel.IsProjectLoaded)
            return;
        if (_previewPaneAnimating || _treePaneAnimating)
            return;

        // Keep the tree live while the preview pane grows in from the right.
        // Compact mode is applied only after the animation so the tree does not rescale mid-flight.
        PreparePreviewPane();
        CaptureNonSplitSettingsPanelWidth();
        _currentSettingsPanelWidth = GetClampedSettingsPanelWidth(SettingsPanelWidth);
        ResetPreviewTreePaneVisualState();
        CollapsePreviewPaneVisualState();
        _viewModel.SetPreviewCompactModeActive(false);

        var initialTreeWidth = Math.Max(SplitTreePaneMinWidth, ResolvePreviewTreePaneVisibleWidth());
        // A newly opened preview starts from the smallest useful tree width. Once open,
        // splitter drags still update this value and window resizes preserve that manual choice.
        var targetTreeWidth = GetClampedPreviewTreePaneWidth(SplitTreePaneMinWidth);
        var targetPreviewWidth = ResolveDesiredPreviewPaneWidth(targetTreeWidth);
        _currentPreviewTreePaneWidth = targetTreeWidth;

        _viewModel.PreviewWorkspaceMode = PreviewWorkspaceMode.TreeAndPreview;
        PreparePreviewPaneOpenLayout(initialTreeWidth);
        UpdatePreviewSegmentThumbPosition(animate: false);

        await AnimatePreviewPaneOpenAsync(targetTreeWidth, targetPreviewWidth);
        _viewModel.SetPreviewCompactModeActive(true);
        UpdateCompactModeVisualState();
        await WaitForPreviewRenderPassesAsync();
        CaptureSplitPaneLayout();
        UpdateWorkspaceLayoutForCurrentMode();
        UpdatePreviewSegmentThumbPosition(animate: false);
        _treeView?.Focus();
        SchedulePreviewRefresh(immediate: true);
    }

    private async void ClosePreviewMode()
    {
        if (_previewPaneAnimating || _treePaneAnimating)
            return;

        SetPreviewToolbarInteractionSuspended(true);
        try
        {
            var startedFromPreviewOnly = _viewModel.IsPreviewOnlyMode;
            var currentPreviewWidth = Math.Max(SplitPreviewPaneMinWidth, ResolvePreviewPaneVisibleWidth());
            var currentTreeWidth = _viewModel.IsPreviewTreeVisible
                ? Math.Max(SplitTreePaneMinWidth, ResolvePreviewTreePaneVisibleWidth())
                : 0.0;

            if (_viewModel.IsPreviewTreeVisible)
                CaptureSplitPaneLayout();

            _previewModeSwitchCts?.Cancel();
            _previewModeSwitchInProgress = false;
            CancelPreviewRefresh();

            if (startedFromPreviewOnly)
            {
                // Rehydrate the hidden tree pane before the close animation starts so the tree
                // can expand back into the freed width exactly like the settings panel does.
                _viewModel.PreviewWorkspaceMode = PreviewWorkspaceMode.TreeAndPreview;
                UpdateWorkspaceLayoutForCurrentMode();
                UpdatePreviewSegmentThumbPosition(animate: false);
            }

            PreparePreviewPaneCloseLayout(currentTreeWidth, currentPreviewWidth);
            await AnimatePreviewPaneCloseAsync(startedFromPreviewOnly);

            _viewModel.PreviewWorkspaceMode = PreviewWorkspaceMode.Off;
            _viewModel.SetPreviewCompactModeActive(false);
            UpdateCompactModeVisualState();
            RestoreNonSplitSettingsPanelWidth();
            UpdateWorkspaceLayoutForCurrentMode();
            await WaitForPreviewRenderPassesAsync();
            ResetPreviewTreePaneVisualState();
            CollapsePreviewPaneVisualState();

            if (startedFromPreviewOnly)
                RestoreTreeToolStateAfterPreviewOnly();

            var previewDocument = _viewModel.PreviewDocument;
            var shouldForceMemoryCleanup =
                previewDocument is not null &&
                PreviewFileCollectionPolicy.ShouldForcePreviewMemoryCleanup(
                    previewDocument.CharacterCount,
                    previewDocument.LineCount);

            ClearPreviewSelectionMetrics();
            ClearPreviewMemory();
            // Small documents are reclaimed efficiently by normal generational GC. Preserve
            // explicit LOH compaction for previews large enough to retain material buffers.
            SchedulePreviewMemoryCleanup(force: shouldForceMemoryCleanup);
            _treeView?.Focus();
        }
        finally
        {
            SetPreviewToolbarInteractionSuspended(false);
        }
    }

    private async void HidePreviewTreePane()
    {
        if (!_viewModel.IsPreviewTreeVisible)
            return;
        if (_previewPaneAnimating || _treePaneAnimating)
            return;

        SetPreviewToolbarInteractionSuspended(true);
        try
        {
            // Pause refresh/build pressure before the tree collapse starts.
            // The animation itself should only touch width/transform state.
            var shouldResumePreviewRefresh = SuspendPreviewRefreshForTreeHide();
            SuspendTreeToolActivityForPreviewTreeHide();
            SuspendTreeToolStateForPreviewOnly();
            CaptureSplitPaneLayout();
            PreparePreviewTreePaneCollapseLayout();
            await Dispatcher.UIThread.InvokeAsync(
                static () => { },
                DispatcherPriority.Render);
            TryPreparePreviewTreePaneSnapshot();
            await AnimatePreviewTreePaneHideAsync();

            _viewModel.PreviewWorkspaceMode = PreviewWorkspaceMode.PreviewOnly;
            UpdateWorkspaceLayoutForCurrentMode();
            UpdatePreviewSegmentThumbPosition(animate: false);
            ResetPreviewTreePaneVisualState();
            await WaitForPreviewRenderPassesAsync();

            if (shouldResumePreviewRefresh && _viewModel.IsAnyPreviewVisible)
                SchedulePreviewRefresh(immediate: true);

            FocusPreviewSurface();
        }
        finally
        {
            SetPreviewToolbarInteractionSuspended(false);
        }
    }

    private void PreparePreviewPaneOpenLayout(double initialTreeWidth)
    {
        if (_treePaneColumn is null ||
            _previewPaneColumn is null ||
            _treePreviewSplitterColumn is null)
        {
            return;
        }

        _treePaneColumn.MinWidth = 0;
        _treePaneColumn.Width = GridLength.Auto;
        _previewPaneColumn.MinWidth = 0;
        _previewPaneColumn.Width = GridLength.Auto;
        _treePreviewSplitterColumn.Width = new GridLength(TreePreviewSplitterWidth);

        if (_treePreviewSplitter is not null)
        {
            _treePreviewSplitter.IsVisible = true;
            _treePreviewSplitter.IsHitTestVisible = false;
        }

        ApplyPreviewTreePaneWidth(initialTreeWidth, animate: false);
        ApplyPreviewPaneWidth(0.0, animate: false);
        ResetPreviewPaneSnapshotVisualState();
    }

    private void PreparePreviewPaneCloseLayout(double currentTreeWidth, double currentPreviewWidth)
    {
        if (_treePaneColumn is null ||
            _previewPaneColumn is null ||
            _treePreviewSplitterColumn is null)
        {
            return;
        }

        var showSplitter = currentTreeWidth > 0.5;

        _treePaneColumn.MinWidth = 0;
        _treePaneColumn.Width = GridLength.Auto;
        _previewPaneColumn.MinWidth = 0;
        _previewPaneColumn.Width = GridLength.Auto;
        _treePreviewSplitterColumn.Width = new GridLength(showSplitter ? TreePreviewSplitterWidth : 0.0);

        if (_treePreviewSplitter is not null)
        {
            _treePreviewSplitter.IsVisible = showSplitter;
            _treePreviewSplitter.IsHitTestVisible = false;
        }

        ApplyPreviewTreePaneWidth(currentTreeWidth, animate: false);
        ApplyPreviewPaneWidth(currentPreviewWidth, animate: false);
        ResetPreviewPaneSnapshotVisualState();
    }

    private async Task AnimatePreviewPaneOpenAsync(double targetTreeWidth, double targetPreviewWidth)
    {
        if (_treePaneContainer is null || _previewPaneContainer is null)
            return;
        if (_previewPaneAnimating)
            return;

        _previewPaneAnimating = true;
        try
        {
            await YieldUiAsync(DispatcherPriority.Render);

            // Both containers animate as plain width transitions. The live tree stays visible here,
            // which avoids bitmap resampling artifacts during preview open.
            EnsurePreviewTreePaneTransitions();
            EnsurePreviewPaneTransitions();
            _treePaneContainer.Width = targetTreeWidth;
            _previewPaneContainer.Width = targetPreviewWidth;
            await WaitForPanelAnimationAsync(PreviewPaneAnimationDuration);

            // Waiting for the nominal duration does not guarantee that every compositor
            // backend has presented the final transition sample. Snap the base values before
            // CaptureSplitPaneLayout reads them; otherwise a transient width can become the
            // persisted manual width on slower CI machines (and occasionally in production).
            ApplyPreviewTreePaneWidth(targetTreeWidth, animate: false);
            ApplyPreviewPaneWidth(targetPreviewWidth, animate: false);
            await YieldUiAsync(DispatcherPriority.Render);
        }
        finally
        {
            _previewPaneAnimating = false;
            if (_treePreviewSplitter is not null)
                _treePreviewSplitter.IsHitTestVisible = _viewModel.IsPreviewTreeVisible;
        }
    }

    private async Task AnimatePreviewPaneCloseAsync(bool startedFromPreviewOnly)
    {
        if (_treePaneContainer is null || _previewPaneContainer is null)
            return;
        if (_previewPaneAnimating)
            return;

        _previewPaneAnimating = true;
        try
        {
            await YieldUiAsync(DispatcherPriority.Render);

            // Keep the live tree visible during preview close. Preview compact is removed
            // only after the animation completes, so a bitmap snapshot here only adds
            // resampling artifacts and the "DPI zoom" effect on the expanding tree pane.
            ResetPreviewTreePaneSnapshotVisualState();
            TryPreparePreviewPaneSnapshot();

            EnsurePreviewTreePaneTransitions();
            EnsurePreviewPaneTransitions();
            var targetTreeWidth = GetAvailableTreeOnlyWorkspaceWidth();
            _treePaneContainer.Width = targetTreeWidth;
            _previewPaneContainer.Width = 0.0;
            await WaitForPanelAnimationAsync(PreviewPaneAnimationDuration);

            // Close uses the same explicit final-state boundary as open. ViewModel mode
            // changes and subsequent reopen calculations must never observe an in-flight width.
            ApplyPreviewTreePaneWidth(targetTreeWidth, animate: false);
            ApplyPreviewPaneWidth(0.0, animate: false);
            await YieldUiAsync(DispatcherPriority.Render);
        }
        finally
        {
            _previewPaneAnimating = false;
            if (_treePreviewSplitter is not null)
                _treePreviewSplitter.IsHitTestVisible = _viewModel.IsPreviewTreeVisible;
        }
    }

    private void ClearPreviewMemory()
    {
        InvalidatePreviewCache();
        ClearPreviewDocument();
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
    /// Schedules aggressive memory cleanup on a background thread.
    /// The reason-driven policy keeps heavy GC out of hot UI paths while still preserving the
    /// explicit "free memory back to the OS" behavior that large project sessions rely on.
    /// </summary>
    private void ScheduleBackgroundMemoryCleanup(MemoryCleanupReason reason)
    {
        var cleanupPlan = MemoryCleanupPolicy.CreateDeferredPlan(reason, SettingsPanelAnimationDuration);
        if (!MemoryCleanupPolicy.ShouldRun(cleanupPlan, GC.GetTotalMemory(forceFullCollection: false)))
            return;

        ScheduleBackgroundMemoryCleanupCore(reason, cleanupPlan);
    }

    private void ScheduleBackgroundMemoryCleanupCore(MemoryCleanupReason reason, MemoryCleanupPlan cleanupPlan)
    {
        _sessionMetrics.RecordMemoryCleanupScheduled(reason);
        var cleanupCts = ReplaceCancellationSource(ref _backgroundMemoryCleanupCts);

        _ = Task.Run(async () =>
        {
            try
            {
                if (cleanupPlan.WaitForUiSettled)
                    await WaitForUiReadyForMemoryCleanupAsync(cleanupCts.Token);

                var scaledDelay = UiTimingProfile.Scale(cleanupPlan.Delay);
                if (scaledDelay > TimeSpan.Zero)
                    await Task.Delay(scaledDelay, cleanupCts.Token);

                cleanupCts.Token.ThrowIfCancellationRequested();
                // A normal GC may have reclaimed the transient data during the delay. Recheck
                // before paying for a compacting Gen2 collection and native working-set trim.
                if (!MemoryCleanupPolicy.ShouldRun(
                        cleanupPlan,
                        GC.GetTotalMemory(forceFullCollection: false)))
                {
                    return;
                }

                var stopwatch = Stopwatch.StartNew();
                ForceMemoryCleanup();
                _sessionMetrics.RecordMemoryCleanupCompleted(reason, stopwatch.Elapsed);
            }
            catch (OperationCanceledException)
            {
                // Ignore canceled cleanup runs; a newer user interaction superseded them.
            }
            finally
            {
                DisposeIfCurrent(ref _backgroundMemoryCleanupCts, cleanupCts);
            }
        }, cleanupCts.Token);
    }

    private async Task WaitForUiReadyForMemoryCleanupAsync(CancellationToken cancellationToken)
    {
        var stableSamples = 0;

        while (stableSamples < 3)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var isUiReady = await Dispatcher.UIThread.InvokeAsync(
                () => IsVisible &&
                      !_viewModel.StatusBusy &&
                      !_viewModel.IsPreviewLoading &&
                      !_settingsAnimating &&
                      !_previewModeSwitchInProgress,
                DispatcherPriority.Background);

            stableSamples = isUiReady ? stableSamples + 1 : 0;
            await Task.Delay(UiTimingProfile.Scale(TimeSpan.FromMilliseconds(120)), cancellationToken);
        }
    }

    private void CancelBackgroundMemoryCleanup()
    {
        var previous = Interlocked.Exchange(ref _backgroundMemoryCleanupCts, null);
        previous?.Cancel();
        previous?.Dispose();
    }

    /// <summary>
    /// Schedules the same aggressive cleanup path used by search close,
    /// but only after the latest search result has been rendered.
    /// Rapid search updates are coalesced into a single cleanup request.
    /// </summary>
    private void ScheduleSearchMemoryCleanupAfterRender()
    {
        var cleanupCts = ReplaceCancellationSource(ref _searchMemoryCleanupCts);
        var cleanupVersion = Interlocked.Increment(ref _searchMemoryCleanupVersion);

        _ = Task.Run(async () =>
        {
            try
            {
                await Dispatcher.UIThread.InvokeAsync(
                    static () => { },
                    DispatcherPriority.Render);
                cleanupCts.Token.ThrowIfCancellationRequested();

                if (cleanupVersion != Volatile.Read(ref _searchMemoryCleanupVersion))
                    return;

                await Dispatcher.UIThread.InvokeAsync(
                    static () => { },
                    DispatcherPriority.Render);
                cleanupCts.Token.ThrowIfCancellationRequested();

                if (cleanupVersion != Volatile.Read(ref _searchMemoryCleanupVersion))
                    return;

                ScheduleBackgroundMemoryCleanup(MemoryCleanupReason.SearchClose);
            }
            catch (OperationCanceledException)
            {
                // Ignore canceled coalesced cleanup requests.
            }
            finally
            {
                DisposeIfCurrent(ref _searchMemoryCleanupCts, cleanupCts);
            }
        }, cleanupCts.Token);
    }

    /// <summary>
    /// Schedules aggressive cleanup specifically for preview rendering completion.
    /// Multiple rapid requests are coalesced into one cleanup run.
    /// </summary>
    private void SchedulePreviewMemoryCleanup(bool force)
        => SchedulePreviewMemoryCleanup(force, MemoryCleanupReason.PreviewClose);

    private void SchedulePreviewRebuildMemoryCleanup(bool force)
        => SchedulePreviewMemoryCleanup(force, MemoryCleanupReason.PreviewRebuildCompleted);

    private void SchedulePreviewMemoryCleanup(bool force, MemoryCleanupReason reason)
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

                ScheduleBackgroundMemoryCleanup(reason);
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

    private async Task WaitForTreeRenderStabilizationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await Dispatcher.UIThread.InvokeAsync(
            static () => { },
            DispatcherPriority.Render);
        cancellationToken.ThrowIfCancellationRequested();

        await Dispatcher.UIThread.InvokeAsync(
            static () => { },
            DispatcherPriority.Render);
        cancellationToken.ThrowIfCancellationRequested();

        if (_workspaceGrid is null || _treePaneContainer is null || _settingsContainer is null)
        {
            await Task.Delay(UiTimingProfile.Scale(TimeSpan.FromMilliseconds(140)), cancellationToken);
            return;
        }

        var readinessTimeout = UiTimingProfile.Scale(TimeSpan.FromMilliseconds(700));
        var frameDelay = UiTimingProfile.Scale(TimeSpan.FromMilliseconds(16));
        var stopwatch = Stopwatch.StartNew();
        var previousWorkspaceWidth = 0.0;
        var previousTreeWidth = 0.0;
        var stableSamples = 0;

        while (stopwatch.Elapsed < readinessTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Dispatcher.UIThread.InvokeAsync(
                static () => { },
                DispatcherPriority.Render);
            cancellationToken.ThrowIfCancellationRequested();

            var workspaceWidth = _workspaceGrid.Bounds.Width;
            var treeWidth = ResolvePreviewTreePaneVisibleWidth();
            var previewWidth = ResolvePreviewPaneVisibleWidth();
            var settingsWidth = _settingsContainer.Bounds.Width > 0.5
                ? _settingsContainer.Bounds.Width
                : _settingsContainer.Width;

            // The deferred settings animation must start only after the tree already owns
            // the full workspace width. Otherwise the animation begins from a fallback width
            // and the first reveal looks like a jump instead of a smooth shrink.
            var treeOccupiesWorkspace =
                workspaceWidth > 0.5 &&
                treeWidth > 0.5 &&
                previewWidth <= 0.5 &&
                settingsWidth <= 0.5 &&
                Math.Abs(treeWidth - workspaceWidth) <= 2.0;

            var widthStable =
                Math.Abs(workspaceWidth - previousWorkspaceWidth) <= 0.5 &&
                Math.Abs(treeWidth - previousTreeWidth) <= 0.5;

            stableSamples = treeOccupiesWorkspace && widthStable
                ? stableSamples + 1
                : 0;

            if (stableSamples >= 2)
                break;

            previousWorkspaceWidth = workspaceWidth;
            previousTreeWidth = treeWidth;
            await Task.Delay(frameDelay, cancellationToken);
        }

        await Task.Delay(UiTimingProfile.Scale(TimeSpan.FromMilliseconds(60)), cancellationToken);
    }

    private void EnsurePreviewTreePaneTransitions()
    {
        if (_treePaneContainer is null)
            return;

        if (_treePaneContainer.Transitions is null)
        {
            _treePaneContainer.Transitions =
            [
                new DoubleTransition
                {
                    Property = WidthProperty,
                    Duration = PreviewTreePaneAnimationDuration,
                    Easing = new CubicEaseOut()
                }
            ];
        }

        if (_treePaneSnapshotImage is not null && _treePaneSnapshotImage.Transitions is null)
        {
            _treePaneSnapshotImage.Transitions =
            [
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = PreviewTreePaneAnimationDuration,
                    Easing = new CubicEaseOut()
                }
            ];
        }

        if (_treePaneSnapshotTransform is not null && _treePaneSnapshotTransform.Transitions is null)
        {
            _treePaneSnapshotTransform.Transitions =
            [
                new DoubleTransition
                {
                    Property = TranslateTransform.XProperty,
                    Duration = PreviewTreePaneAnimationDuration,
                    Easing = new CubicEaseOut()
                }
            ];
        }
    }

    private void ResetPreviewTreePaneVisualState()
    {
        if (_treePaneContainer is null)
            return;

        var cachedContainerTransitions = _treePaneContainer.Transitions;
        _treePaneContainer.Transitions = null;
        _treePaneContainer.Transitions = cachedContainerTransitions;
        ResetPreviewTreePaneSnapshotVisualState();
    }

    private async Task AnimatePreviewTreePaneHideAsync()
    {
        if (_treePaneContainer is null)
            return;

        if (_treePaneAnimating)
            return;

        _treePaneAnimating = true;
        try
        {
            EnsurePreviewTreePaneTransitions();
            _treePaneContainer.Width = 0.0;

            if (_treePaneSnapshotImage is not null && _treePaneSnapshotTransform is not null && _treePaneSnapshotHost?.IsVisible == true)
            {
                _treePaneSnapshotImage.Opacity = 0.0;
                _treePaneSnapshotTransform.X = -ResolvePreviewTreePaneHiddenOffset();
            }

            await WaitForPanelAnimationAsync(PreviewTreePaneAnimationDuration);
        }
        finally
        {
            _treePaneAnimating = false;
        }
    }

    private void PreparePreviewTreePaneCollapseLayout()
    {
        if (_treePaneColumn is null || _previewPaneColumn is null || _treePaneContainer is null)
            return;

        // Freeze the current width before collapse starts. This keeps the tree anchored on the left
        // while the preview pane expands into the released space on the right.
        var visibleTreeWidth = ResolvePreviewTreePaneWidthForCollapse();

        _currentPreviewTreePaneWidth = visibleTreeWidth;
        ApplyPreviewTreePaneWidth(visibleTreeWidth, animate: false);
        _treePaneColumn.MinWidth = 0;
        _treePaneColumn.Width = GridLength.Auto;
        _previewPaneColumn.Width = new GridLength(1, GridUnitType.Star);
        _previewPaneColumn.MinWidth = SplitPreviewPaneMinWidth;
    }

    private double ResolvePreviewTreePaneWidthForCollapse()
    {
        var visibleWidth = ResolvePreviewTreePaneVisibleWidth();
        if (visibleWidth > 0.5)
            return visibleWidth;

        if (_workspaceGrid is null)
            return SplitTreePaneMinWidth;

        var workspaceWidth = _workspaceGrid.Bounds.Width;
        if (workspaceWidth <= 0.5)
            return SplitTreePaneMinWidth;

        EnsureSavedSplitPaneWidths();

        var settingsWidth = ShouldShowPreviewSettingsSplitter()
            ? GetVisibleSettingsPanelWidth() + PreviewSettingsSplitterWidth
            : 0.0;
        var availableWorkspaceWidth = Math.Max(0, workspaceWidth - settingsWidth);
        var availableSplitWidth = Math.Max(0, availableWorkspaceWidth - TreePreviewSplitterWidth);

        if (availableSplitWidth <= 0.5)
            return SplitTreePaneMinWidth;

        var treeWeight = IsUsableSplitPaneWidth(_savedSplitTreeColumnWidth)
            ? _savedSplitTreeColumnWidth.Value
            : 5.0;
        var previewWeight = IsUsableSplitPaneWidth(_savedSplitPreviewColumnWidth)
            ? _savedSplitPreviewColumnWidth.Value
            : 6.0;
        var totalWeight = treeWeight + previewWeight;
        if (totalWeight <= 0.001)
            return SplitTreePaneMinWidth;

        var projectedTreeWidth = availableSplitWidth * (treeWeight / totalWeight);
        var maximumTreeWidth = Math.Max(SplitTreePaneMinWidth, availableSplitWidth - SplitPreviewPaneMinWidth);
        return Math.Clamp(projectedTreeWidth, SplitTreePaneMinWidth, maximumTreeWidth);
    }

    private double ResolvePreviewTreePaneVisibleWidth()
    {
        if (_treePaneContainer is null)
            return 0;

        if (_treePaneContainer.Width > 0)
            return _treePaneContainer.Width;

        if (_treePaneContainer.Bounds.Width > 0)
            return _treePaneContainer.Bounds.Width;

        if (_treePaneColumn is not null && _treePaneColumn.ActualWidth > 0)
            return _treePaneColumn.ActualWidth;

        return 0;
    }

    private double ResolvePreviewTreePaneHiddenOffset()
    {
        if (_treePaneContainer is null)
            return PreviewTreePaneSlideOffset;

        var paneWidth = _treePaneSnapshotHost?.Width > 0.5
            ? _treePaneSnapshotHost.Width
            : ResolvePreviewTreePaneVisibleWidth();
        if (paneWidth <= 0)
            return PreviewTreePaneSlideOffset;

        return Math.Max(PreviewTreePaneSlideOffset, Math.Ceiling(Math.Min(paneWidth, 280.0)));
    }

    private void SuspendTreeToolActivityForPreviewTreeHide()
    {
        Interlocked.Increment(ref _searchFocusRequestVersion);
        Interlocked.Increment(ref _filterFocusRequestVersion);
        _searchCoordinator.CancelPending();
        _filterCoordinator.CancelPending();
    }

    private bool TryPreparePreviewTreePaneSnapshot()
    {
        if (_treePaneContainer is null ||
            _treePaneRoot is null ||
            _treePaneSnapshotHost is null ||
            _treePaneSnapshotImage is null ||
            _treePaneSnapshotTransform is null)
        {
            return false;
        }

        var size = _treePaneContainer.Bounds.Size;
        if (size.Width <= 0.5 || size.Height <= 0.5)
            return false;

        try
        {
            var topLevel = GetTopLevel(this);
            var renderScaling = topLevel?.RenderScaling ?? 1.0;
            var pixelWidth = Math.Max(1, (int)Math.Ceiling(size.Width * renderScaling));
            var pixelHeight = Math.Max(1, (int)Math.Ceiling(size.Height * renderScaling));
            var visualWidth = Math.Ceiling(size.Width);
            var visualHeight = Math.Ceiling(size.Height);

            ResetPreviewTreePaneSnapshotVisualState();

            // Render the already-laid-out pane into a bitmap so the collapse animation touches
            // only a static surface instead of a live virtualized TreeView.
            var bitmap = new RenderTargetBitmap(
                new PixelSize(pixelWidth, pixelHeight),
                new Vector(96 * renderScaling, 96 * renderScaling));
            bitmap.Render(_treePaneContainer);
            _treePaneSnapshotBitmap = bitmap;

            var cachedImageTransitions = _treePaneSnapshotImage.Transitions;
            var cachedTransformTransitions = _treePaneSnapshotTransform.Transitions;
            _treePaneSnapshotImage.Transitions = null;
            _treePaneSnapshotTransform.Transitions = null;

            _treePaneSnapshotHost.Width = visualWidth;
            _treePaneSnapshotHost.Height = visualHeight;
            _treePaneSnapshotHost.IsVisible = true;
            _treePaneSnapshotImage.Width = visualWidth;
            _treePaneSnapshotImage.Height = visualHeight;
            _treePaneSnapshotImage.Source = bitmap;
            _treePaneSnapshotImage.Opacity = 1.0;
            _treePaneSnapshotImage.IsVisible = true;
            _treePaneSnapshotTransform.X = 0.0;
            _treePaneRoot.IsVisible = false;

            _treePaneSnapshotImage.Transitions = cachedImageTransitions;
            _treePaneSnapshotTransform.Transitions = cachedTransformTransitions;
            return true;
        }
        catch
        {
            ResetPreviewTreePaneSnapshotVisualState();
            return false;
        }
    }

    // The tree pane hosts a virtualized TreeView. During collapse we replace it with a
    // snapshot inside the same clipped container so the layout shrinks like the settings
    // panel, but repeated animations do not pay the cost of re-rendering the live tree.
    private void ResetPreviewTreePaneSnapshotVisualState()
    {
        if (_treePaneRoot is not null)
            _treePaneRoot.IsVisible = true;

        if (_treePaneSnapshotHost is not null)
        {
            _treePaneSnapshotHost.IsVisible = false;
            _treePaneSnapshotHost.Width = double.NaN;
            _treePaneSnapshotHost.Height = double.NaN;
        }

        if (_treePaneSnapshotImage is not null)
        {
            var cachedTransitions = _treePaneSnapshotImage.Transitions;
            _treePaneSnapshotImage.Transitions = null;
            _treePaneSnapshotImage.IsVisible = false;
            _treePaneSnapshotImage.Width = 0.0;
            _treePaneSnapshotImage.Height = 0.0;
            _treePaneSnapshotImage.Opacity = 0.0;
            _treePaneSnapshotImage.Source = null;
            _treePaneSnapshotImage.Transitions = cachedTransitions;
        }

        if (_treePaneSnapshotTransform is not null)
        {
            var cachedTransitions = _treePaneSnapshotTransform.Transitions;
            _treePaneSnapshotTransform.Transitions = null;
            _treePaneSnapshotTransform.X = 0.0;
            _treePaneSnapshotTransform.Transitions = cachedTransitions;
        }

        _treePaneSnapshotBitmap?.Dispose();
        _treePaneSnapshotBitmap = null;
    }

    private void CollapsePreviewPaneVisualState()
    {
        if (_previewPaneContainer is not null)
        {
            var cachedTransitions = _previewPaneContainer.Transitions;
            _previewPaneContainer.Transitions = null;
            _previewPaneContainer.Width = 0.0;
            _previewPaneContainer.Transitions = cachedTransitions;
        }

        ResetPreviewPaneSnapshotVisualState();
    }

    private bool TryPreparePreviewPaneSnapshot()
    {
        if (_previewPaneContainer is null ||
            _previewPaneRoot is null ||
            _previewPaneSnapshotHost is null ||
            _previewPaneSnapshotImage is null)
        {
            return false;
        }

        var size = _previewPaneContainer.Bounds.Size;
        if (size.Width <= 0.5 || size.Height <= 0.5)
            return false;

        try
        {
            var topLevel = GetTopLevel(this);
            var renderScaling = topLevel?.RenderScaling ?? 1.0;
            var pixelWidth = Math.Max(1, (int)Math.Ceiling(size.Width * renderScaling));
            var pixelHeight = Math.Max(1, (int)Math.Ceiling(size.Height * renderScaling));
            var visualWidth = Math.Ceiling(size.Width);
            var visualHeight = Math.Ceiling(size.Height);

            ResetPreviewPaneSnapshotVisualState();

            // Freeze the preview surface for close animations so the expensive text surface does
            // not reflow every frame while the pane width shrinks.
            var bitmap = new RenderTargetBitmap(
                new PixelSize(pixelWidth, pixelHeight),
                new Vector(96 * renderScaling, 96 * renderScaling));
            bitmap.Render(_previewPaneContainer);
            _previewPaneSnapshotBitmap = bitmap;

            var cachedImageTransitions = _previewPaneSnapshotImage.Transitions;
            _previewPaneSnapshotImage.Transitions = null;

            _previewPaneSnapshotHost.Width = visualWidth;
            _previewPaneSnapshotHost.Height = visualHeight;
            _previewPaneSnapshotHost.IsVisible = true;
            _previewPaneSnapshotImage.Width = visualWidth;
            _previewPaneSnapshotImage.Height = visualHeight;
            _previewPaneSnapshotImage.Source = bitmap;
            _previewPaneSnapshotImage.Opacity = 1.0;
            _previewPaneSnapshotImage.IsVisible = true;
            _previewPaneRoot.IsVisible = false;

            _previewPaneSnapshotImage.Transitions = cachedImageTransitions;
            return true;
        }
        catch
        {
            ResetPreviewPaneSnapshotVisualState();
            return false;
        }
    }

    // The preview surface combines the mode-toolbar island and the content island.
    // During close animations we freeze that combined surface into a snapshot so the
    // pane shrinks horizontally without re-laying out the full preview document every frame.
    private void ResetPreviewPaneSnapshotVisualState()
    {
        if (_previewPaneRoot is not null)
            _previewPaneRoot.IsVisible = true;

        if (_previewPaneSnapshotHost is not null)
        {
            _previewPaneSnapshotHost.IsVisible = false;
            _previewPaneSnapshotHost.Width = double.NaN;
            _previewPaneSnapshotHost.Height = double.NaN;
        }

        if (_previewPaneSnapshotImage is not null)
        {
            var cachedTransitions = _previewPaneSnapshotImage.Transitions;
            _previewPaneSnapshotImage.Transitions = null;
            _previewPaneSnapshotImage.IsVisible = false;
            _previewPaneSnapshotImage.Width = 0.0;
            _previewPaneSnapshotImage.Height = 0.0;
            _previewPaneSnapshotImage.Opacity = 0.0;
            _previewPaneSnapshotImage.Source = null;
            _previewPaneSnapshotImage.Transitions = cachedTransitions;
        }

        _previewPaneSnapshotBitmap?.Dispose();
        _previewPaneSnapshotBitmap = null;
    }

    private bool SuspendPreviewRefreshForTreeHide()
    {
        return _previewPipeline.SuspendForTreeHide();
    }

    private void SetPreviewToolbarInteractionSuspended(bool suspended)
    {
        if (_previewBar is null)
            return;

        _previewBar.IsHitTestVisible = !suspended;

        if (suspended)
            _previewBar.Classes.Add("preview-toolbar-suspended");
        else
            _previewBar.Classes.Remove("preview-toolbar-suspended");
    }

    private Task AnimateSettingsPanelAsync(bool show)
    {
        var settingsIsland = _settingsIsland;
        var settingsTransform = _settingsTransform;
        if (settingsIsland is null || settingsTransform is null || _settingsContainer is null)
            return Task.CompletedTask;

        if (_settingsAnimating)
            return _settingsAnimationTask;

        _settingsAnimationTask = RunSettingsPanelAnimationAsync(show, settingsIsland, settingsTransform);
        return _settingsAnimationTask;
    }

    private async Task RunSettingsPanelAnimationAsync(
        bool show,
        Border settingsIsland,
        TranslateTransform settingsTransform)
    {
        var displayMode = GetCurrentDisplayMode();
        _settingsAnimating = true;
        try
        {
            await YieldUiAsync(DispatcherPriority.Render);

            EnsureSettingsPanelTransitions();
            _currentSettingsPanelWidth = GetClampedSettingsPanelWidth(_currentSettingsPanelWidth);
            var targetVisibleWidth = _currentSettingsPanelWidth;
            var currentTreeWidth = Math.Max(SplitTreePaneMinWidth, ResolvePreviewTreePaneVisibleWidth());
            var currentPreviewWidth = Math.Max(SplitPreviewPaneMinWidth, ResolvePreviewPaneVisibleWidth());

            SetSettingsAnimationPaneAnchors(displayMode, anchoredToLeftEdge: true);

            if (show)
            {
                SetPreviewSettingsSplitterVisibility(true);

                // Drive the neighboring pane with the same easing so the grid does not
                // have to "catch up" to an Auto-sized settings column on every frame.
                switch (displayMode)
                {
                    case WorkspaceDisplayMode.Tree:
                        ApplyPreviewTreePaneWidth(currentTreeWidth, animate: false);
                        ApplyPreviewTreePaneWidth(
                            ResolveTreeModeTargetWidthForSettingsAnimation(targetVisibleWidth, includeSplitter: true),
                            animate: true);
                        break;

                    case WorkspaceDisplayMode.PreviewOnly:
                        ApplyPreviewPaneWidth(currentPreviewWidth, animate: false);
                        ApplyPreviewPaneWidth(
                            ResolvePreviewOnlyTargetWidthForSettingsAnimation(targetVisibleWidth, includeSplitter: true),
                            animate: true);
                        break;

                    case WorkspaceDisplayMode.PreviewWithTree:
                        ApplyPreviewTreePaneWidth(currentTreeWidth, animate: false);
                        ApplyPreviewPaneWidth(currentPreviewWidth, animate: false);
                        ApplyPreviewPaneWidth(
                            ResolvePreviewPaneTargetWidthForSettingsAnimation(
                                currentTreeWidth,
                                targetVisibleWidth,
                                includeSplitter: true),
                            animate: true);
                        break;
                }
            }
            else
            {
                // Drop the splitter at close start so the neighboring pane can reclaim
                // the full width during the animation instead of jumping at the end.
                SetPreviewSettingsSplitterVisibility(false);

                switch (displayMode)
                {
                    case WorkspaceDisplayMode.Tree:
                        ApplyPreviewTreePaneWidth(currentTreeWidth, animate: false);
                        ApplyPreviewTreePaneWidth(
                            ResolveTreeModeTargetWidthForSettingsAnimation(0.0, includeSplitter: false),
                            animate: true);
                        break;

                    case WorkspaceDisplayMode.PreviewOnly:
                        ApplyPreviewPaneWidth(currentPreviewWidth, animate: false);
                        ApplyPreviewPaneWidth(
                            ResolvePreviewOnlyTargetWidthForSettingsAnimation(0.0, includeSplitter: false),
                            animate: true);
                        break;

                    case WorkspaceDisplayMode.PreviewWithTree:
                        ApplyPreviewTreePaneWidth(currentTreeWidth, animate: false);
                        ApplyPreviewPaneWidth(currentPreviewWidth, animate: false);
                        ApplyPreviewPaneWidth(
                            ResolvePreviewPaneTargetWidthForSettingsAnimation(
                                currentTreeWidth,
                                0.0,
                                includeSplitter: false),
                            animate: true);
                        break;
                }
            }

            ApplySettingsPanelWidth(show ? targetVisibleWidth : 0.0, animate: true);
            settingsTransform.X = show ? 0.0 : targetVisibleWidth;
            settingsIsland.Opacity = show ? 1.0 : 0.0;
            await WaitForPanelAnimationAsync(SettingsPanelAnimationDuration);

            switch (displayMode)
            {
                case WorkspaceDisplayMode.Tree:
                    ApplyPreviewTreePaneWidth(double.NaN, animate: false);
                    break;

                case WorkspaceDisplayMode.PreviewOnly:
                    ApplyPreviewPaneWidth(double.NaN, animate: false);
                    break;

                case WorkspaceDisplayMode.PreviewWithTree:
                    ApplyPreviewTreePaneWidth(ResolveDesiredPreviewTreePaneWidth(), animate: false);
                    ApplyPreviewPaneWidth(double.NaN, animate: false);
                    break;
            }
        }
        finally
        {
            SetSettingsAnimationPaneAnchors(displayMode, anchoredToLeftEdge: false);
            _settingsAnimating = false;
            UpdatePreviewSettingsSplitterState();
            UpdateAdaptiveWorkspaceChrome();
        }
    }

    private void OnSettingsPanelMinimumWidthChanged(object? sender, SettingsPanelMinimumWidthChangedEventArgs e)
    {
        UpdateSettingsPanelMinimumWidth(e.MinimumWidth);
    }

    private void UpdateSettingsPanelMinimumWidth(double minimumWidth)
    {
        var normalizedMinimumWidth = Math.Max(SettingsPanelMinWidth, Math.Ceiling(minimumWidth));
        if (Math.Abs(normalizedMinimumWidth - _effectiveSettingsPanelMinWidth) < 0.5)
            return;

        _effectiveSettingsPanelMinWidth = normalizedMinimumWidth;
        if (_currentSettingsPanelWidth < _effectiveSettingsPanelMinWidth)
            _currentSettingsPanelWidth = _effectiveSettingsPanelMinWidth;
        if (_savedNonSplitSettingsPanelWidth < _effectiveSettingsPanelMinWidth)
            _savedNonSplitSettingsPanelWidth = _effectiveSettingsPanelMinWidth;

        ClampSettingsPanelWidthToAvailableSpace(applyToVisual: ShouldApplySettingsPanelWidthToVisual());
        UpdateAdaptiveWorkspaceChrome();
    }

    // Preview workspace starts from the computed minimum width,
    // but the user's regular tree-only settings width should remain restorable.
    private void CaptureNonSplitSettingsPanelWidth()
    {
        if (_viewModel.IsPreviewMode)
            return;

        var currentWidth = GetVisibleSettingsPanelWidth();
        if (currentWidth > 0.5)
            _savedNonSplitSettingsPanelWidth = Math.Max(_effectiveSettingsPanelMinWidth, currentWidth);
    }

    private void RestoreNonSplitSettingsPanelWidth()
    {
        _currentSettingsPanelWidth = Math.Max(_effectiveSettingsPanelMinWidth, _savedNonSplitSettingsPanelWidth);
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
            else
            {
                // Ensure controls are interactive even if a previous force-hide left them disabled.
                _searchBar.IsHitTestVisible = true;
                _searchBar.IsEnabled = true;
            }
            if (show)
                _searchBarContainer.IsVisible = true;
            _searchBarContainer.Height = show ? SearchBarHeight : 0.0;
            _searchBarContainer.Margin = new Thickness(0, 0, 0, show ? PanelIslandSpacing : 0.0);
            _searchBarTransform.Y = 0.0;
            _searchBar.Opacity = show ? 1.0 : 0.0;
            await WaitForPanelAnimationAsync(SearchBarAnimationDuration);
            if (!show && !_viewModel.SearchVisible)
            {
                _searchBarContainer.IsVisible = false;
                _searchBar.IsHitTestVisible = false;
                _searchBar.IsEnabled = false;
            }
            if (show && _viewModel.SearchVisible)
            {
                _ = RestoreSearchBoxAccentAfterOpenAsync();
            }

            await RefreshSearchFilterHostAfterAnimationAsync();
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

    private static async Task WaitForPreviewRenderPassesAsync()
    {
        await YieldUiAsync(DispatcherPriority.Render);
        await YieldUiAsync(DispatcherPriority.Render);
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
            else
            {
                // Ensure controls are interactive even if a previous force-hide left them disabled.
                _filterBar.IsHitTestVisible = true;
                _filterBar.IsEnabled = true;
            }
            if (show)
                _filterBarContainer.IsVisible = true;
            _filterBarContainer.Height = show ? FilterBarHeight : 0.0;
            _filterBarContainer.Margin = new Thickness(0, 0, 0, show ? PanelIslandSpacing : 0.0);
            _filterBarTransform.Y = 0.0;
            _filterBar.Opacity = show ? 1.0 : 0.0;
            await WaitForPanelAnimationAsync(FilterBarAnimationDuration);
            if (!show && !_viewModel.FilterVisible)
            {
                _filterBarContainer.IsVisible = false;
                _filterBar.IsHitTestVisible = false;
                _filterBar.IsEnabled = false;
            }
            if (show && _viewModel.FilterVisible)
            {
                _ = RestoreFilterBoxAccentAfterOpenAsync();
            }

            await RefreshSearchFilterHostAfterAnimationAsync();
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

    }

    private static Task WaitForPanelAnimationAsync(TimeSpan duration)
    {
        // A tiny safety buffer ensures state flags reset after the transition settles.
        return Task.Delay(duration + UiTimingProfile.AnimationSettleBuffer);
    }

    private static Task WaitForPanelAnimationAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        // A tiny safety buffer ensures state flags reset after the transition settles.
        return Task.Delay(duration + UiTimingProfile.AnimationSettleBuffer, cancellationToken);
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

    private void OnToggleCompactMode(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanToggleCompactMode)
            return;

        _viewModel.IsCompactMode = !_viewModel.IsCompactMode;
        UpdateCompactModeVisualState();
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
        ApplyPresetForSelection(GetSelectedThemeVariant(), GetSelectedEffectMode());
        _themeBrushCoordinator.UpdateTransparencyEffect();
        e.Handled = true;
    }

    private void OnSetMicaMode(object? sender, RoutedEventArgs e)
    {
        _viewModel.ToggleMica();
        ApplyPresetForSelection(GetSelectedThemeVariant(), GetSelectedEffectMode());
        _themeBrushCoordinator.UpdateTransparencyEffect();
        e.Handled = true;
    }

    private void OnSetAcrylicMode(object? sender, RoutedEventArgs e)
    {
        _viewModel.ToggleAcrylic();
        ApplyPresetForSelection(GetSelectedThemeVariant(), GetSelectedEffectMode());
        _themeBrushCoordinator.UpdateTransparencyEffect();
        e.Handled = true;
    }


    private void OnLangRu(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.Ru);
    private void OnLangEn(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.En);
    private void OnLangUz(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.Uz);
    private void OnLangTg(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.Tg);
    private void OnLangKk(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.Kk);
    private void OnLangFr(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.Fr);
    private void OnLangDe(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.De);
    private void OnLangIt(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.It);
    private void OnLangEs(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.Es);
    private void OnLangPt(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.Pt);
    private void OnLangPtPt(object? sender, RoutedEventArgs e) => SetLanguageAndPersist(AppLanguage.PtPt);

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

    private async void OnTerminalCommandSetup(object? sender, RoutedEventArgs e)
    {
        try
        {
            await ShowTerminalCommandSetupAsync(_terminalCommandSetupService.Probe(), isAutomaticPrompt: false);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
            e.Handled = true;
        }
    }

    private void OnHelpClose(object? sender, RoutedEventArgs e)
    {
        _viewModel.HelpDocsPopoverOpen = false;
        e.Handled = true;
    }

    private async Task ShowTerminalCommandSetupAsync(
        TerminalCommandSetupSnapshot snapshot,
        bool isAutomaticPrompt)
    {
        while (true)
        {
            var dialogResult = await TerminalCommandSetupDialog.ShowAsync(
                this,
                _localization,
                snapshot,
                isAutomaticPrompt);
            if (ShouldPersistTerminalCommandPromptDismissal(dialogResult))
                SaveTerminalCommandPromptDismissed();

            if (dialogResult.Action == TerminalCommandDialogAction.ConfigurePath)
            {
                var pathResult = await Task.Run(_terminalCommandSetupService.ConfigurePath);
                if (pathResult.Success)
                    return;

                await ShowErrorAsync(pathResult.ErrorMessage ?? _localization["Dialog.TerminalCommand.InstallFailed"]);
                snapshot = pathResult.Snapshot;
                isAutomaticPrompt = false;
                continue;
            }

            if (dialogResult.Action is not (TerminalCommandDialogAction.InstallOrRepair or
                TerminalCommandDialogAction.Reinstall))
                return;

            var installResult = await Task.Run(() =>
                dialogResult.Action == TerminalCommandDialogAction.Reinstall
                    ? _terminalCommandSetupService.Reinstall()
                    : _terminalCommandSetupService.InstallOrRepair());
            if (ResolveTerminalCommandPostInstallUiAction(installResult) == TerminalCommandPostInstallUiAction.ShowError)
            {
                await ShowErrorAsync(installResult.ErrorMessage ?? _localization["Dialog.TerminalCommand.InstallFailed"]);
                return;
            }

            if (RequiresTerminalCommandPathConfiguration(installResult.Snapshot))
            {
                var pathResult = await Task.Run(_terminalCommandSetupService.ConfigurePath);
                if (pathResult.Success)
                    return;

                await ShowErrorAsync(pathResult.ErrorMessage ?? _localization["Dialog.TerminalCommand.InstallFailed"]);
                snapshot = pathResult.Snapshot;
                isAutomaticPrompt = false;
                continue;
            }

            if (dialogResult.Action == TerminalCommandDialogAction.Reinstall)
            {
                await MessageDialog.ShowAsync(
                    this,
                    _localization["Dialog.TerminalCommand.Title"],
                    _localization["Dialog.TerminalCommand.ReconfigureSucceeded"],
                    height: 120);
            }

            return;
        }
    }

    internal static TerminalCommandPostInstallUiAction ResolveTerminalCommandPostInstallUiAction(
        TerminalCommandInstallResult installResult) =>
        installResult.Success
            ? TerminalCommandPostInstallUiAction.None
            : TerminalCommandPostInstallUiAction.ShowError;

    internal static bool RequiresTerminalCommandPathConfiguration(TerminalCommandSetupSnapshot snapshot) =>
        snapshot.State is
            TerminalCommandSetupState.InstalledPathMissing or
            TerminalCommandSetupState.CommandShadowed;

    private void SaveTerminalCommandPromptDismissed()
    {
        var current = _userSettingsDb.ViewSettings ?? new AppViewSettings();
        _userSettingsDb.ViewSettings = current with
        {
            IsTerminalCommandPromptDismissed = true
        };
        _userSettingsStore.TryPersistViewSettings(_userSettingsDb);
    }

    internal static bool ShouldPersistTerminalCommandPromptDismissal(TerminalCommandDialogResult dialogResult)
    {
        // Choosing install, repair, or reinstall is not a dismissal. If the setup attempt fails,
        // the next startup should still be allowed to offer setup again.
        return dialogResult.Action is not (TerminalCommandDialogAction.InstallOrRepair or
                   TerminalCommandDialogAction.Reinstall or
                   TerminalCommandDialogAction.ConfigurePath) &&
               (dialogResult.DontShowAgain || dialogResult.Action == TerminalCommandDialogAction.DismissPrompt);
    }

    private async void OnResetSettings(object? sender, RoutedEventArgs e)
    {
        var confirmed = await MessageDialog.ShowConfirmationAsync(
            this,
            _localization["Dialog.ResetSettings.Title"],
            _localization["Dialog.ResetSettings.Message"],
            _localization["Dialog.ResetSettings.Confirm"],
            _localization["Dialog.Cancel"],
            height: 180);

        if (!confirmed)
        {
            e.Handled = true;
            return;
        }

        ResetThemeSettings();
        _toastService.Show(_localization["Toast.Settings.Reset"]);
        e.Handled = true;
    }

    private async void OnResetData(object? sender, RoutedEventArgs e)
    {
        var confirmed = await MessageDialog.ShowConfirmationAsync(
            this,
            _localization["Dialog.ResetData.Title"],
            _localization["Dialog.ResetData.Message"],
            _localization["Dialog.ResetData.Confirm"],
            _localization["Dialog.Cancel"]);

        if (!confirmed)
        {
            e.Handled = true;
            return;
        }

        _projectProfiles.ClearAllProfiles();
        _toastService.Show(_localization["Toast.Data.Reset"]);
        e.Handled = true;
    }

    /// <summary>
    /// Resets all theme presets to factory defaults and reapplies current selection.
    /// </summary>
    private void ResetThemeSettings()
    {
        var resetDocument = _themeSettingsStore.ResetToDefaults();
        var resetSession = new ThemePresetSession(_themeSettingsStore, resetDocument);
        var theme = resetSession.CurrentTheme;
        var effect = ThemeEffectPlatformSupport.Normalize(resetSession.CurrentEffect, _isMicaSupported);

        if (global::Avalonia.Application.Current is { } app)
            app.RequestedThemeVariant = theme == ThemePresetVariant.Dark
                ? ThemeVariant.Dark
                : ThemeVariant.Light;

        _currentThemeVariant = theme;
        _currentEffectMode = effect;
        _viewModel.IsDarkTheme = theme == ThemePresetVariant.Dark;
        ApplyEffectMode(effect);

        ApplyPresetValues(_themeSettingsStore.GetPreset(resetDocument, theme, effect));
        _themeSettingsDocument = resetDocument;
        _themePresetSession = resetSession;

        _themeBrushCoordinator.UpdateTransparencyEffect();
        _themeBrushCoordinator.UpdateDynamicThemeBrushes();
    }

    #region Recent Projects

    private void LoadRecentProjects()
    {
        _recentProjectsDb = _recentProjectsStore.LoadForStartup(StartupStoreLockTimeout);
        SyncRecentProjectsToViewModel();
    }

    private void SyncRecentProjectsToViewModel()
    {
        _viewModel.RecentFolders.Clear();
        foreach (var entry in _recentProjectsDb.RecentFolders)
        {
            _viewModel.RecentFolders.Add(new RecentProjectEntryViewModel(
                entry.Path,
                RecentProjectPresentationService.CreateFolderDisplayText(entry.Path),
                RecentProjectPresentationService.CreateFolderToolTip(entry.Path)));
        }

        _viewModel.RecentRepositories.Clear();
        foreach (var entry in _recentProjectsDb.RecentRepositories)
        {
            _viewModel.RecentRepositories.Add(new RecentProjectEntryViewModel(
                entry.Url,
                RecentProjectPresentationService.CreateRepositoryDisplayText(entry.Url),
                RecentProjectPresentationService.CreateRepositoryToolTip(entry.Url)));
        }
    }

    private void AttachRecentMenuHandlers()
    {
        if (_topMenuBar?.RecentMenuItemControl is { } recentMenuItem)
            recentMenuItem.SubmenuOpened += OnRecentMenuSubmenuOpened;
    }

    private void DetachRecentMenuHandlers()
    {
        if (_topMenuBar?.RecentMenuItemControl is { } recentMenuItem)
            recentMenuItem.SubmenuOpened -= OnRecentMenuSubmenuOpened;
    }

    private void OnRecentMenuSubmenuOpened(object? sender, RoutedEventArgs e)
    {
        RefreshRecentFoldersMenu();
        StartRecentFolderAvailabilityRefresh();
    }

    private void RefreshRecentFoldersMenu()
    {
        var recentMenuItem = _topMenuBar?.RecentMenuItemControl;
        if (recentMenuItem is null)
            return;

        recentMenuItem.Items.Clear();

        if (_viewModel.RecentFolders.Count == 0)
        {
            recentMenuItem.Items.Add(new MenuItem
            {
                Header = _viewModel.MenuFileRecentEmpty,
                IsEnabled = false
            });
            return;
        }

        foreach (var recentFolder in _viewModel.RecentFolders)
        {
            var item = new MenuItem
            {
                Header = recentFolder.DisplayText,
                Tag = recentFolder.Value
            };

            ToolTip.SetTip(item, null);
            SetRecentFolderMenuItemAvailability(
                item,
                !_unavailableRecentFolderPaths.Contains(recentFolder.Value));
            item.Click += OnRecentFolderMenuItemClick;
            recentMenuItem.Items.Add(item);
        }
    }

    private async void OnRecentFolderMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanChangeProjectTree || sender is not MenuItem { Tag: string path })
            return;

        var lifetimeToken = _windowLifetimeCts?.Token ?? CancellationToken.None;
        bool isAvailable;
        try
        {
            isAvailable = await _recentFolderAvailabilityService.IsAvailableAsync(path, lifetimeToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        UpdateRecentFolderAvailability(path, isAvailable);
        ApplyRecentFolderAvailabilityToMenu();
        if (!isAvailable)
        {
            var shouldRemove = await MessageDialog.ShowConfirmationAsync(
                this,
                _localization["Dialog.RecentFolderUnavailable.Title"],
                _localization.Format("Dialog.RecentFolderUnavailable.Message", path),
                _localization["Dialog.RecentFolderUnavailable.Remove"],
                _localization["Dialog.RecentFolderUnavailable.Keep"],
                width: 450,
                height: 180);

            if (shouldRemove)
                RemoveRecentFolder(path);
            return;
        }

        await TryOpenFolderAsync(path, fromDialog: true);
    }

    private void StartRecentFolderAvailabilityRefresh()
    {
        if (_recentFolderAvailabilityRefreshTask is { IsCompleted: false } ||
            _windowLifetimeCts is not { } lifetime)
        {
            return;
        }

        var refreshTask = RefreshRecentFolderAvailabilityAsync(lifetime.Token);
        _recentFolderAvailabilityRefreshTask = refreshTask;
        ObserveDetachedTask(refreshTask, "RefreshRecentFolderAvailability");
    }

    private async Task RefreshRecentFolderAvailabilityAsync(CancellationToken cancellationToken)
    {
        var paths = _viewModel.RecentFolders
            .Select(static folder => folder.Value)
            .ToArray();
        if (paths.Length == 0)
        {
            _unavailableRecentFolderPaths.Clear();
            return;
        }

        var availability = await _recentFolderAvailabilityService.CheckAsync(paths, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        _unavailableRecentFolderPaths.IntersectWith(paths);
        foreach (var (path, isAvailable) in availability)
            UpdateRecentFolderAvailability(path, isAvailable);

        ApplyRecentFolderAvailabilityToMenu();
    }

    private void ApplyRecentFolderAvailabilityToMenu()
    {
        if (_topMenuBar?.RecentMenuItemControl is not { } recentMenuItem)
            return;

        foreach (var item in recentMenuItem.Items.OfType<MenuItem>())
        {
            if (item.Tag is string path)
            {
                SetRecentFolderMenuItemAvailability(
                    item,
                    !_unavailableRecentFolderPaths.Contains(path));
            }
        }
    }

    private void UpdateRecentFolderAvailability(string path, bool isAvailable)
    {
        if (isAvailable)
            _unavailableRecentFolderPaths.Remove(path);
        else
            _unavailableRecentFolderPaths.Add(path);
    }

    private static void SetRecentFolderMenuItemAvailability(MenuItem item, bool isAvailable)
    {
        const string unavailableClass = "recent-folder-unavailable";
        if (isAvailable)
            item.Classes.Remove(unavailableClass);
        else if (!item.Classes.Contains(unavailableClass))
            item.Classes.Add(unavailableClass);
    }

    private void RemoveRecentFolder(string path)
    {
        _recentProjectsDb = _recentProjectsStore.RemoveFolder(_recentProjectsDb, path);
        _unavailableRecentFolderPaths.Remove(path);
        SyncRecentProjectsToViewModel();
        RefreshRecentFoldersMenu();
    }

    private void RecordRecentFolder(string path)
    {
        _recentProjectsDb = _recentProjectsStore.AddFolder(_recentProjectsDb, path);
        _unavailableRecentFolderPaths.Remove(path);
        SyncRecentProjectsToViewModel();
        RefreshRecentFoldersMenu();
    }

    private void RecordRecentRepository(string repositoryUrl)
    {
        _recentProjectsDb = _recentProjectsStore.AddRepository(_recentProjectsDb, repositoryUrl);
        SyncRecentProjectsToViewModel();
    }

    #endregion

    #region Language Menu

    private void RefreshLanguageMenuChecks()
    {
        foreach (var (item, language, label) in EnumerateLanguageMenuItems())
        {
            if (item is null)
                continue;

            item.Header = CreateCheckedMenuHeader(_localization.CurrentLanguage == language, label);
        }
    }

    private IEnumerable<(MenuItem? Item, AppLanguage Language, string Label)> EnumerateLanguageMenuItems()
    {
        var topMenuBar = _topMenuBar;
        if (topMenuBar is null)
            yield break;

        yield return (topMenuBar.LanguageEnMenuItemControl, AppLanguage.En, "English");
        yield return (topMenuBar.LanguageRuMenuItemControl, AppLanguage.Ru, "Русский");
        yield return (topMenuBar.LanguageEsMenuItemControl, AppLanguage.Es, "Español");
        yield return (topMenuBar.LanguagePtMenuItemControl, AppLanguage.Pt, "Português (Brasil)");
        yield return (topMenuBar.LanguagePtPtMenuItemControl, AppLanguage.PtPt, "Português (Portugal)");
        yield return (topMenuBar.LanguageDeMenuItemControl, AppLanguage.De, "Deutsch");
        yield return (topMenuBar.LanguageFrMenuItemControl, AppLanguage.Fr, "Français");
        yield return (topMenuBar.LanguageItMenuItemControl, AppLanguage.It, "Italiano");
        yield return (topMenuBar.LanguageTgMenuItemControl, AppLanguage.Tg, "Тоҷикӣ");
        yield return (topMenuBar.LanguageUzMenuItemControl, AppLanguage.Uz, "Oʻzbek");
        yield return (topMenuBar.LanguageKkMenuItemControl, AppLanguage.Kk, "Қазақ");
    }

    private static string CreateCheckedMenuHeader(bool isChecked, string label)
        => isChecked ? $"✓ {label}" : $"   {label}";

    #endregion

    #region Tree Font Menu

    private void AttachTreeFontMenuHandlers()
    {
        if (_topMenuBar?.TreeFontMenuItemControl is { } treeFontMenuItem)
            treeFontMenuItem.SubmenuOpened += OnTreeFontMenuSubmenuOpened;
    }

    private void DetachTreeFontMenuHandlers()
    {
        if (_topMenuBar?.TreeFontMenuItemControl is { } treeFontMenuItem)
            treeFontMenuItem.SubmenuOpened -= OnTreeFontMenuSubmenuOpened;
    }

    private void OnTreeFontMenuSubmenuOpened(object? sender, RoutedEventArgs e)
    {
        RefreshTreeFontMenu();
    }

    private void RefreshTreeFontMenu()
    {
        var treeFontMenuItem = _topMenuBar?.TreeFontMenuItemControl;
        if (treeFontMenuItem is null)
            return;

        treeFontMenuItem.Items.Clear();
        foreach (var fontFamily in EnumerateTreeFontMenuFamilies())
            treeFontMenuItem.Items.Add(CreateTreeFontMenuItem(fontFamily));
    }

    private IEnumerable<FontFamily> EnumerateTreeFontMenuFamilies()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fontFamily in _viewModel.FontFamilies)
        {
            if (seen.Add(GetTreeFontKey(fontFamily)))
                yield return fontFamily;
        }
    }

    private MenuItem CreateTreeFontMenuItem(FontFamily fontFamily)
    {
        var displayName = GetTreeFontDisplayName(fontFamily);
        var item = new MenuItem
        {
            Header = CreateCheckedMenuHeader(IsPendingTreeFont(fontFamily), displayName),
            Tag = fontFamily,
            MinHeight = TreeFontMenuItemHeight
        };

        item.Click += OnTreeFontMenuItemClick;
        return item;
    }

    private void OnTreeFontMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: FontFamily fontFamily })
            return;

        _viewModel.PendingFontFamily = fontFamily;
        e.Handled = true;
    }

    private bool IsPendingTreeFont(FontFamily fontFamily)
        => AreSameTreeFont(_viewModel.PendingFontFamily, fontFamily);

    private string GetTreeFontDisplayName(FontFamily fontFamily)
    {
        if (IsDefaultTreeFont(fontFamily))
            return _viewModel.SettingsFontDefault;

        var name = fontFamily.Name?.Trim();
        return string.IsNullOrWhiteSpace(name) ? _viewModel.SettingsFontDefault : name;
    }

    private static bool AreSameTreeFont(FontFamily? left, FontFamily? right)
    {
        if (IsDefaultTreeFont(left) && IsDefaultTreeFont(right))
            return true;

        return string.Equals(left?.Name, right?.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetTreeFontKey(FontFamily fontFamily)
        => IsDefaultTreeFont(fontFamily) ? string.Empty : fontFamily.Name ?? string.Empty;

    private static bool IsDefaultTreeFont(FontFamily? fontFamily)
    {
        var name = fontFamily?.Name;
        return string.IsNullOrWhiteSpace(name) || name.StartsWith("$", StringComparison.Ordinal);
    }

    #endregion

    #region Git Operations

    private void OnGitClone(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanChangeProjectTree)
            return;

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
        _taskbarProgress.BeginGitClone();

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
                _taskbarProgress.MarkGitCloneError();
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
                Dispatcher.Post(() =>
                {
                    _taskbarProgress.UpdateGitClone(status);

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
                _taskbarProgress.SetGitCloneIndeterminate();
                result = await _gitService.CloneAsync(url, targetPath, progress, cancellationToken);
            }
            else
            {
                // Fallback to ZIP download
                _viewModel.GitCloneStatus = _viewModel.GitErrorGitNotFound;
                await Task.Delay(1500, cancellationToken);

                currentOperation = _viewModel.GitCloneProgressDownloading;
                _viewModel.GitCloneStatus = currentOperation;
                _taskbarProgress.SetGitCloneIndeterminate();
                result = await _zipDownloadService.DownloadAndExtractAsync(url, targetPath, progress, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!result.Success)
            {
                _repoCacheService.DeleteRepositoryDirectory(targetPath);
                _gitCloneWindow?.Close();
                _gitCloneWindow = null;
                _viewModel.GitCloneInProgress = false;
                _taskbarProgress.MarkGitCloneError();
                await ShowErrorAsync(_localization.Format("Git.Error.CloneFailed", result.ErrorMessage ?? "Unknown error"));
                _toastService.Show(_localization["Toast.Git.CloneError"]);
                return;
            }

            await ApplySuccessfulGitCloneAsync(result, targetPath, url, cancellationToken);
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
            _taskbarProgress.MarkGitCloneError();
            await ShowErrorAsync(_localization.Format("Git.Error.CloneFailed", ex.Message));
            _toastService.Show(_localization["Toast.Git.CloneError"]);
        }
        finally
        {
            _viewModel.GitCloneInProgress = false;
            _taskbarProgress.CompleteGitClone();
            DisposeIfCurrent(ref _gitCloneCts, gitCloneCts);
        }

        e.Handled = true;
    }

    internal async Task ApplySuccessfulGitCloneAsync(
        GitCloneResult result,
        string cachePath,
        string requestedUrl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();

        _gitCloneWindow?.Close();
        _gitCloneWindow = null;
        _viewModel.GitCloneInProgress = false;
        _viewModel.ProjectSourceType = result.SourceType;
        _viewModel.CurrentBranch = result.DefaultBranch ?? "main";
        _currentProjectDisplayName = result.RepositoryName;
        _currentRepositoryUrl = result.RepositoryUrl;
        _currentCachedRepoPath = cachePath;

        var opened = await TryOpenFolderAsync(result.LocalPath, fromDialog: false, recordRecentFolder: false);
        if (!opened || !PathComparer.Default.Equals(_currentPath, result.LocalPath))
            return;

        RecordRecentRepository(string.IsNullOrWhiteSpace(result.RepositoryUrl) ? requestedUrl : result.RepositoryUrl);

        // Clone-only branch discovery stays behind the reveal barrier so it cannot compete
        // with the settings animation or make the clone completion appear frozen.
        if (result.SourceType == ProjectSourceType.GitClone)
        {
            var visualReadyTask = _postLoadVisualReadyTask;
            await MetricsCalculationPolicy.WaitForInitialVisualReadyAsync(
                visualReadyTask,
                MetricsCalculationPolicy.InitialVisualReadyTimeout,
                cancellationToken);

            if (PathComparer.Default.Equals(_currentPath, result.LocalPath))
                await RefreshGitBranchesAsync(result.LocalPath, cancellationToken);
        }

        if (PathComparer.Default.Equals(_currentPath, result.LocalPath))
            _toastService.Show(_localization["Toast.Git.CloneSuccess"]);
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
        _taskbarProgress.CompleteGitClone();
    }

    private async void OnGitGetUpdates(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanGetGitUpdates)
            return;

        await GetGitUpdatesAsync();
        e.Handled = true;
    }

    private async Task GetGitUpdatesAsync()
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
            statusOperationId = _statusOperations.Begin(
                statusText,
                indeterminate: true,
                operationType: StatusOperationType.GitPullUpdates,
                cancelAction: () => gitCts.Cancel());

            var progress = new Progress<string>(status =>
            {
                Dispatcher.Post(() =>
                {
                    if (GitProgressStatusParser.TryParseTrailingPercent(status, out var percent))
                        _statusOperations.UpdateProgress(percent, statusText, statusOperationId);
                    else
                        _statusOperations.UpdateText(statusText, statusOperationId);
                });
            });
            var beforeHash = await _gitService.GetHeadCommitAsync(_currentPath, cancellationToken);
            var success = await _gitService.PullUpdatesAsync(_currentPath, progress, cancellationToken);

            if (!success)
            {
                _statusOperations.Complete(statusOperationId);
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
                _statusOperations.Complete(statusOperationId);
            }
            else
            {
                _toastService.Show(_localization["Toast.Git.UpdatesApplied"]);
                _statusOperations.Complete(statusOperationId);
                // Clean up memory from old tree after successful update.
                ScheduleBackgroundMemoryCleanup(MemoryCleanupReason.GitPullUpdate);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _statusOperations.Complete(statusOperationId);
            _toastService.Show(_localization["Toast.Operation.GitCanceled"]);
        }
        catch (Exception ex)
        {
            _statusOperations.Complete(statusOperationId);
            await ShowErrorAsync(_localization.Format("Git.Error.UpdateFailed", ex.Message));
        }
        finally
        {
            DisposeIfCurrent(ref _gitOperationCts, gitCts);
        }
    }

    private async void OnGitBranchSwitch(object? sender, string branchName)
    {
        if (!_viewModel.CanGetGitUpdates || string.IsNullOrEmpty(_currentPath))
            return;

        var gitCts = ReplaceCancellationSource(ref _gitOperationCts);
        var cancellationToken = gitCts.Token;
        long? statusOperationId = null;
        try
        {
            var statusText = _localization.Format("Status.Operation.SwitchingBranch", branchName);
            statusOperationId = _statusOperations.Begin(
                statusText,
                indeterminate: true,
                operationType: StatusOperationType.GitSwitchBranch,
                cancelAction: () => gitCts.Cancel());

            var progress = new Progress<string>(status =>
            {
                Dispatcher.Post(() =>
                {
                    if (GitProgressStatusParser.TryParseTrailingPercent(status, out var percent))
                        _statusOperations.UpdateProgress(percent, statusText, statusOperationId);
                    else
                        _statusOperations.UpdateText(statusText, statusOperationId);
                });
            });
            var success = await _gitService.SwitchBranchAsync(_currentPath, branchName, progress, cancellationToken);

            // A lightweight retry helps recover from transient remote/network hiccups.
            if (!success)
                success = await _gitService.SwitchBranchAsync(_currentPath, branchName, progress: null, cancellationToken);

            if (!success)
            {
                _statusOperations.Complete(statusOperationId);
                await ShowErrorAsync(_localization.Format("Git.Error.BranchSwitchFailed", branchName));
                return;
            }

            // Reload tree first so branch/title state is only updated after full success.
            // This keeps UI stable if reload fails or gets cancelled mid-flight.
            await ReloadProjectAsync(cancellationToken);
            await RefreshGitBranchesAsync(_currentPath, cancellationToken);
            _statusOperations.Complete(statusOperationId);

            _viewModel.CurrentBranch = branchName;
            UpdateTitle();
            _toastService.Show(_localization.Format("Toast.Git.BranchSwitched", branchName));

            // Clean up memory from old branch tree.
            ScheduleBackgroundMemoryCleanup(MemoryCleanupReason.GitBranchSwitch);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _statusOperations.Complete(statusOperationId);
            _toastService.Show(_localization["Toast.Operation.GitCanceled"]);
        }
        catch (Exception ex)
        {
            _statusOperations.Complete(statusOperationId);
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
        GitBranchMenuScrollBehavior.SetScrollable(branchMenuItem, _viewModel.GitBranches.Count);

        foreach (var branch in _viewModel.GitBranches)
            branchMenuItem.Items.Add(CreateBranchMenuItem(branch));
    }

    private MenuItem CreateBranchMenuItem(GitBranch branch)
    {
        var item = new MenuItem
        {
            Header = CreateCheckedMenuHeader(branch.IsActive, branch.Name),
            Tag = branch.Name,
            MinHeight = BranchMenuItemHeight
        };

        // Use a named handler to avoid closure captures and keep menu rebuilds cheap.
        item.Click += OnBranchMenuItemClick;
        return item;
    }

    private void OnBranchMenuItemClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.CanChangeProjectTree && sender is MenuItem { Tag: string name })
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
        TryNavigateSearchMatches(1);
    }

    private void OnSearchPrev(object? sender, RoutedEventArgs e)
    {
        TryNavigateSearchMatches(-1);
    }

    private async void OnToggleSearch(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsProjectLoaded) return;
        if (!_viewModel.IsSearchAvailable) return;

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
        if (!_viewModel.IsSearchFilterAvailable) return;

        if (_viewModel.FilterVisible)
        {
            await CloseFilterAsync();
            return;
        }

        // Keep only one active text tool at a time: close search first, then open filter.
        if (IsSearchBarEffectivelyVisible())
            await CloseSearchAsync(focusTree: false);

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
        if (!_viewModel.IsSearchFilterAvailable) return;
        if (_filterBarAnimating) return;

        SuppressFilterBoxAccentVisual();
        _viewModel.FilterVisible = true;
        AnimateFilterBar(true);

        if (!focusInput)
            return;

        var focusRequestVersion = Interlocked.Increment(ref _filterFocusRequestVersion);
        _ = FocusFilterBoxAfterOpenAnimationAsync(selectAllOnFocus, focusRequestVersion);
    }

    private async Task CloseFilterAsync(bool focusTree = true)
    {
        if (!IsFilterBarEffectivelyVisible()) return;
        Interlocked.Increment(ref _filterFocusRequestVersion);

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
            ScheduleBackgroundMemoryCleanup(MemoryCleanupReason.FilterClose);
        }
        else
        {
            _filterCoordinator.CancelPending();
        }
    }

    private void SuspendTreeToolStateForPreviewOnly()
    {
        // Preview-only mode should temporarily hide tree tools without destroying the
        // current search/filter session. This keeps tree state intact when the user
        // closes preview-only or re-enters preview with the tree pane visible again.
        Interlocked.Increment(ref _searchFocusRequestVersion);
        Interlocked.Increment(ref _filterFocusRequestVersion);

        var searchWasVisible = _viewModel.SearchVisible || IsSearchBarEffectivelyVisible();
        var filterWasVisible = !searchWasVisible && (_viewModel.FilterVisible || IsFilterBarEffectivelyVisible());
        _previewOnlySuspendedTreeToolMode = searchWasVisible
            ? SuspendedTreeToolMode.Search
            : filterWasVisible
                ? SuspendedTreeToolMode.Filter
                : SuspendedTreeToolMode.None;

        _viewModel.SearchVisible = false;
        _viewModel.FilterVisible = false;

        _searchBarAnimating = false;
        _filterBarAnimating = false;
        _searchBarClosePending = false;
        _filterBarClosePending = false;

        ForceHideSearchBarVisualState();
        ForceHideFilterBarVisualState();

        _searchCoordinator.CancelPending();
        _filterCoordinator.CancelPending();
    }

    private void RestoreTreeToolStateAfterPreviewOnly()
    {
        var suspendedToolMode = _previewOnlySuspendedTreeToolMode;
        _previewOnlySuspendedTreeToolMode = SuspendedTreeToolMode.None;

        switch (suspendedToolMode)
        {
            case SuspendedTreeToolMode.Search:
                _viewModel.SearchVisible = true;
                _viewModel.FilterVisible = false;
                ForceShowSearchBarVisualState();
                break;

            case SuspendedTreeToolMode.Filter:
                _viewModel.FilterVisible = true;
                _viewModel.SearchVisible = false;
                ForceShowFilterBarVisualState();
                break;
        }
    }

    private void FocusPreviewSurface()
    {
        if (_previewTextControl is not null && _previewTextControl.Focusable)
        {
            _previewTextControl.Focus();
            return;
        }

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
        var stopwatch = Stopwatch.StartNew();
        var version = 0;
        try
        {
            if (string.IsNullOrEmpty(_currentPath))
            {
                _viewModel.UpdateFilterMatchSummary(0);
                _viewModel.SetFilterInProgress(false);
                return;
            }

            var query = _viewModel.NameFilter?.Trim();
            bool hasQuery = !string.IsNullOrWhiteSpace(query);
            version = Interlocked.Increment(ref _filterApplyVersion);

            if (hasQuery && _filterExpansionSnapshot is null)
                _filterExpansionSnapshot = CaptureExpandedNodes();

            cancellationToken.ThrowIfCancellationRequested();

            await RefreshTreeAsync(interactiveFilter: true);

            cancellationToken.ThrowIfCancellationRequested();

            if (version != _filterApplyVersion)
                return;

            var matchCount = hasQuery ? ApplyNameFilterPresentation(query!) : 0;
            if (!hasQuery)
                _viewModel.UpdateFilterMatchSummary(0);

            if (!hasQuery && _filterExpansionSnapshot is not null)
            {
                RestoreExpandedNodes(_filterExpansionSnapshot);
                _filterExpansionSnapshot = null;
                ResetInteractiveFilterCache();
            }

            _sessionMetrics.RecordTreeFilter(
                query,
                matchCount,
                stopwatch.Elapsed,
                _lastInteractiveFilterUsedInMemory);
        }
        catch (OperationCanceledException)
        {
            // Filter was superseded by a newer request - expected behavior
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
        finally
        {
            if (version == 0 || version == Volatile.Read(ref _filterApplyVersion))
                _viewModel.SetFilterInProgress(false);
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
            TryNavigateSearchMatches(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            e.Handled = true;
        }
    }

    private bool TryNavigateSearchMatches(int step)
    {
        if (string.IsNullOrWhiteSpace(_viewModel.SearchQuery))
            return false;

        if (_searchCoordinator.TryNavigateForCurrentQuery(step))
            return true;

        _toastService.Show(_localization["Toast.NoMatches"]);
        return false;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;

        // Ctrl+O (always available)
        if (mods == KeyModifiers.Control && e.Key == Key.O)
        {
            if (_viewModel.CanChangeProjectTree)
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

            if (_viewModel.IsSearchFilterAvailable)
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

        if (e.Key == Key.F3 && _viewModel.SearchVisible)
        {
            TryNavigateSearchMatches(mods.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            e.Handled = true;
            return;
        }

        // F5 refresh (same as WinForms)
        if (e.Key == Key.F5)
        {
            if (_viewModel.CanChangeProjectTree && _viewModel.IsProjectLoaded)
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
            if (_viewModel.IsTreePaneVisible)
                ExpandCollapseTree(expand: true);
            e.Handled = true;
            return;
        }

        // Ctrl+W Collapse All
        if (mods == KeyModifiers.Control && e.Key == Key.W)
        {
            if (_viewModel.IsTreePaneVisible)
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
        if (_viewModel.SearchVisible || _viewModel.FilterVisible || !_viewModel.IsTreePaneVisible)
            return;

        _treeView?.Focus();
    }

    private void OnWindowPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var zoomTarget = GetZoomSurfaceTarget(e.Source);
        if (!TreeZoomWheelHandler.TryGetZoomStep(e.KeyModifiers, e.Delta, zoomTarget != ZoomSurfaceTarget.None, out var step))
            return;

        AdjustZoomFontSize(step, zoomTarget);
        e.Handled = true;
    }

    private ZoomSurfaceTarget GetZoomSurfaceTarget(object? source)
    {
        if (_treeView is null)
            return ZoomSurfaceTarget.None;

        if (ReferenceEquals(source, _treeView))
            return ZoomSurfaceTarget.Tree;

        if (_treeIsland is not null && ReferenceEquals(source, _treeIsland))
            return ZoomSurfaceTarget.Tree;

        if (_viewModel.IsAnyPreviewVisible)
        {
            if (_previewIsland is not null && ReferenceEquals(source, _previewIsland))
                return ZoomSurfaceTarget.Preview;

            if (_previewLineNumbersBackground is not null && ReferenceEquals(source, _previewLineNumbersBackground))
                return ZoomSurfaceTarget.Preview;

            if (_previewTextScrollViewer is not null && ReferenceEquals(source, _previewTextScrollViewer))
                return ZoomSurfaceTarget.Preview;

            if (_previewLineNumbersControl is not null && ReferenceEquals(source, _previewLineNumbersControl))
                return ZoomSurfaceTarget.Preview;
        }

        if (source is not Visual visual)
            return ZoomSurfaceTarget.None;

        foreach (var ancestor in visual.GetVisualAncestors())
        {
            if (_treeIsland is not null && ReferenceEquals(ancestor, _treeIsland))
                return ZoomSurfaceTarget.Tree;

            if (ReferenceEquals(ancestor, _treeView))
                return ZoomSurfaceTarget.Tree;

            if (!_viewModel.IsAnyPreviewVisible)
                continue;

            if (_previewIsland is not null && ReferenceEquals(ancestor, _previewIsland))
                return ZoomSurfaceTarget.Preview;

            if (_previewLineNumbersBackground is not null && ReferenceEquals(ancestor, _previewLineNumbersBackground))
                return ZoomSurfaceTarget.Preview;

            if (_previewTextScrollViewer is not null && ReferenceEquals(ancestor, _previewTextScrollViewer))
                return ZoomSurfaceTarget.Preview;

            if (_previewLineNumbersControl is not null && ReferenceEquals(ancestor, _previewLineNumbersControl))
                return ZoomSurfaceTarget.Preview;
        }

        return ZoomSurfaceTarget.None;
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
        Dispatcher.Post(() =>
        {
            try
            {
                var isAvailable = isSearchToggle
                    ? _viewModel.IsSearchAvailable
                    : _viewModel.IsSearchFilterAvailable;
                if (!isAvailable)
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
        if (!_viewModel.IsSearchAvailable) return;
        if (_searchBarAnimating) return;

        SuppressSearchBoxAccentVisual();
        _viewModel.SearchVisible = true;
        AnimateSearchBar(true);

        if (!focusInput)
            return;

        var focusRequestVersion = Interlocked.Increment(ref _searchFocusRequestVersion);
        _ = FocusSearchBoxAfterOpenAnimationAsync(selectAllOnFocus, focusRequestVersion);
    }

    private async Task FocusSearchBoxAfterOpenAnimationAsync(bool selectAllOnFocus, int focusRequestVersion)
    {
        await WaitForPanelAnimationAsync(SearchBarAnimationDuration);
        if (!_viewModel.SearchVisible || !_viewModel.IsSearchAvailable || !IsSearchFocusRequestCurrent(focusRequestVersion))
            return;

        await TryFocusSearchBoxWithRetryAsync(selectAllOnFocus, focusRequestVersion);
    }

    private async Task FocusFilterBoxAfterOpenAnimationAsync(bool selectAllOnFocus, int focusRequestVersion)
    {
        await WaitForPanelAnimationAsync(FilterBarAnimationDuration);
        if (!_viewModel.FilterVisible || !_viewModel.IsSearchFilterAvailable || !IsFilterFocusRequestCurrent(focusRequestVersion))
            return;

        await TryFocusFilterBoxWithRetryAsync(selectAllOnFocus, focusRequestVersion);
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
        _ = textBox.Dispatcher.InvokeAsync(() => PlaceCaretAtTextEnd(textBox), DispatcherPriority.Input);
    }

    private static void PlaceCaretAtTextEnd(TextBox textBox)
    {
        var end = textBox.Text?.Length ?? 0;
        textBox.SelectionStart = end;
        textBox.SelectionEnd = end;
        textBox.CaretIndex = end;
    }

    private async Task TryFocusSearchBoxWithRetryAsync(bool selectAllOnFocus, int focusRequestVersion)
    {
        const int maxAttempts = 4;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (!IsSearchFocusRequestCurrent(focusRequestVersion))
                return;

            var focused = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var textBox = _searchBar?.SearchBoxControl;
                if (textBox is null || !IsSearchInputReady(textBox))
                    return false;

                FocusInputTextBox(textBox, selectAllOnFocus);
                return textBox.IsFocused;
            }, DispatcherPriority.Input);

            if (focused)
                return;

            await YieldUiAsync(DispatcherPriority.Background);
        }
    }

    private async Task TryFocusFilterBoxWithRetryAsync(bool selectAllOnFocus, int focusRequestVersion)
    {
        const int maxAttempts = 4;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (!IsFilterFocusRequestCurrent(focusRequestVersion))
                return;

            var focused = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var textBox = _filterBar?.FilterBoxControl;
                if (textBox is null || !IsFilterInputReady(textBox))
                    return false;

                FocusInputTextBox(textBox, selectAllOnFocus);
                return textBox.IsFocused;
            }, DispatcherPriority.Input);

            if (focused)
                return;

            await YieldUiAsync(DispatcherPriority.Background);
        }
    }

    private bool IsSearchFocusRequestCurrent(int requestVersion)
        => Volatile.Read(ref _searchFocusRequestVersion) == requestVersion && _viewModel.SearchVisible;

    private bool IsFilterFocusRequestCurrent(int requestVersion)
        => Volatile.Read(ref _filterFocusRequestVersion) == requestVersion && _viewModel.FilterVisible;

    private bool IsSearchInputReady(TextBox? textBox)
        => textBox is { IsVisible: true, IsEnabled: true }
           && _searchBar is { IsVisible: true, IsEnabled: true, IsHitTestVisible: true }
           && _searchBarContainer is { IsVisible: true };

    private bool IsFilterInputReady(TextBox? textBox)
        => textBox is { IsVisible: true, IsEnabled: true }
           && _filterBar is { IsVisible: true, IsEnabled: true, IsHitTestVisible: true }
           && _filterBarContainer is { IsVisible: true };

    private async Task CloseSearchAsync(bool focusTree = true)
    {
        if (!IsSearchBarEffectivelyVisible())
            return;
        Interlocked.Increment(ref _searchFocusRequestVersion);

        // Remove focus from the search textbox before close animation starts.
        // This avoids a transient focused-border artifact during panel collapse.
        if (_searchBar?.SearchBoxControl?.IsFocused == true)
            _treeView?.Focus();
        SuppressSearchBoxAccentVisual();

        _viewModel.SearchVisible = false;
        if (_searchBarAnimating)
            _searchBarClosePending = true;
        else
            AnimateSearchBar(false);
        if (focusTree)
            _treeView?.Focus();

        // Keep search close sequencing consistent with filter close:
        // finish panel animation first, then clear query and apply tree state changes.
        await WaitForPanelAnimationAsync(SearchBarAnimationDuration);

        // If search was reopened during animation, keep current query/state intact.
        if (_viewModel.SearchVisible)
            return;

        if (!string.IsNullOrEmpty(_viewModel.SearchQuery))
        {
            _viewModel.SearchQuery = string.Empty;
            _searchCoordinator.CancelPending();
            // Project load clears search state ahead of time. Skip the expensive tree-wide search
            // normalization when there is no active query or cached match state to restore.
            if (!string.IsNullOrWhiteSpace(_viewModel.SearchQuery) || _searchCoordinator.HasMatches)
                _searchCoordinator.UpdateSearchMatches();

            // Release stale highlight objects after search state is rebuilt.
            ScheduleBackgroundMemoryCleanup(MemoryCleanupReason.SearchClose);
        }
        else
        {
            _searchCoordinator.CancelPending();
        }
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
        var textBox = _searchBar?.SearchBoxControl;
        textBox?.Classes.Remove("suppress-accent");
        textBox?.InvalidateVisual();
        _searchBar?.InvalidateVisual();
        _searchBarContainer?.InvalidateVisual();
    }

    private void SuppressFilterBoxAccentVisual()
    {
        _filterBar?.FilterBoxControl?.Classes.Add("suppress-accent");
    }

    private void RestoreFilterBoxAccentVisual()
    {
        var textBox = _filterBar?.FilterBoxControl;
        textBox?.Classes.Remove("suppress-accent");
        textBox?.InvalidateVisual();
        _filterBar?.InvalidateVisual();
        _filterBarContainer?.InvalidateVisual();
    }

    private async Task RestoreSearchBoxAccentAfterOpenAsync()
    {
        await YieldUiAsync(DispatcherPriority.Render);
        await YieldUiAsync(DispatcherPriority.Render);

        if (!_viewModel.SearchVisible || !_viewModel.IsSearchAvailable)
            return;

        RestoreSearchBoxAccentVisual();
    }

    private async Task RestoreFilterBoxAccentAfterOpenAsync()
    {
        await YieldUiAsync(DispatcherPriority.Render);
        await YieldUiAsync(DispatcherPriority.Render);

        if (!_viewModel.FilterVisible || !_viewModel.IsSearchFilterAvailable)
            return;

        RestoreFilterBoxAccentVisual();
    }

    private async Task RefreshSearchFilterHostAfterAnimationAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _searchBar?.InvalidateVisual();
            _filterBar?.InvalidateVisual();

            _searchBarContainer?.InvalidateMeasure();
            _searchBarContainer?.InvalidateArrange();
            _searchBarContainer?.InvalidateVisual();

            _filterBarContainer?.InvalidateMeasure();
            _filterBarContainer?.InvalidateArrange();
            _filterBarContainer?.InvalidateVisual();

            if (_searchBarContainer?.Parent is Visual searchParentVisual)
                searchParentVisual.InvalidateVisual();

            if (_filterBarContainer?.Parent is Visual filterParentVisual)
                filterParentVisual.InvalidateVisual();

            InvalidateVisual();
        }, DispatcherPriority.Render);

        await YieldUiAsync(DispatcherPriority.Render);
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
            _searchBarTransform.Y = 0;

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
            _filterBarTransform.Y = 0;

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

        Interlocked.Increment(ref _searchFocusRequestVersion);
        Interlocked.Increment(ref _filterFocusRequestVersion);
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
            ResetInteractiveFilterCache();
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
        if (!_viewModel.CanApplySettings)
            return;

        var applyCts = ReplaceCancellationSource(ref _applySettingsCts);
        var cancellationToken = applyCts.Token;
        void CancelApply()
        {
            applyCts.Cancel();
            _selectionCoordinator.CancelPendingRefreshes();
            _refreshPipeline.CancelActiveRefresh();
        }

        try
        {
            await using var statusLease = SelectionRefreshStatusLease.StartApplyingSettings(
                _viewModel,
                _statusOperations,
                CancelApply,
                cancellationToken);

            try
            {
                // Font family follows WinForms behavior: applied only on Apply
                var pending = _viewModel.PendingFontFamily;
                if (pending is not null &&
                    !string.Equals(_viewModel.SelectedFontFamily?.Name, pending.Name, StringComparison.OrdinalIgnoreCase))
                {
                    _viewModel.SelectedFontFamily = pending;
                }

                // Apply must observe the latest converged section state. A user can click Apply
                // while an earlier ignore refresh is still finishing; rebuilding the tree first
                // would capture stale root-folder availability and keep newly revealed folders hidden.
                await _selectionCoordinator.WaitForPendingRefreshesAsync(cancellationToken);
                await RefreshTreeAsync(cancellationToken: cancellationToken);
                // Most checkbox changes already queue and apply a converged selection snapshot
                // before Apply rebuilds the tree. Running another live refresh unconditionally
                // doubles the expensive scan path on large projects, so only do it if a new
                // selection change landed while the tree was rebuilding.
                await _selectionCoordinator.UpdateLiveOptionsFromRootSelectionIfDirtyAsync(_currentPath, cancellationToken);
                await _selectionCoordinator.WaitForPendingRefreshesAsync(cancellationToken);
                _projectProfiles.PersistIfNeeded(_currentPath);
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
        finally
        {
            DisposeIfCurrent(ref _applySettingsCts, applyCts);
        }
    }

    private void FlushPersistedStateOnWindowClose()
    {
        // Give persistence one last synchronous chance before the process exits.
        // This protects against transient IO failures that would otherwise make the UI look correct
        // during the session but leave no durable snapshot for the next launch.
        PersistCurrentThemePreset();

        if (_recentProjectsDb.RecentFolders.Count > 0 ||
            _recentProjectsDb.RecentFolderRemovals.Count > 0 ||
            _recentProjectsDb.RecentRepositories.Count > 0)
        {
            _recentProjectsStore.TryPersist(_recentProjectsDb);
        }

        _projectProfiles.FlushPending();
    }

    private async Task<bool> TryOpenFolderAsync(string path, bool fromDialog, bool recordRecentFolder = true)
    {
        if (!_viewModel.CanChangeProjectTree)
            return false;

        var stopwatch = Stopwatch.StartNew();
        string normalizedPath;
        try
        {
            normalizedPath = PathUtility.Normalize(path);
        }
        catch
        {
            _sessionMetrics.RecordProjectLoad(stopwatch.Elapsed, success: false, errorCode: "invalid-path");
            await ShowErrorAsync(_localization.Format("Msg.PathNotFound", path));
            return false;
        }

        if (!Directory.Exists(normalizedPath))
        {
            _sessionMetrics.RecordProjectLoad(stopwatch.Elapsed, success: false, errorCode: "folder-not-found");
            await ShowErrorAsync(_localization.Format("Msg.PathNotFound", path));
            return false;
        }

        if (!_scanOptions.CanReadRoot(normalizedPath))
        {
            _sessionMetrics.RecordProjectLoad(stopwatch.Elapsed, success: false, errorCode: "access-denied");
            if (TryElevateAndRestart(normalizedPath))
                return false;

            if (BuildFlags.AllowElevation)
                await ShowErrorAsync(_localization["Msg.AccessDeniedRoot"]);
            return false;
        }

        try
        {
            await _projectLoadPipeline.OpenFolderAsync(normalizedPath, fromDialog, recordRecentFolder);
            _sessionMetrics.RecordProjectLoad(stopwatch.Elapsed, success: true);
            return true;
        }
        catch
        {
            _sessionMetrics.RecordProjectLoad(stopwatch.Elapsed, success: false, errorCode: "load-failed");
            throw;
        }
    }

    private async Task TryApplyStartupSelectionOverridesAsync()
    {
        if (!_startupOptions.HasSelectionOverrides || string.IsNullOrWhiteSpace(_currentPath))
            return;

        if (_startupOptions.HasRootFolderOverrides)
        {
            var selectedRoots = new HashSet<string>(_startupOptions.IncludeRootFolders, PathComparer.Default);
            foreach (var option in _viewModel.RootFolders)
                option.IsChecked = selectedRoots.Contains(option.Name);
        }

        if (_startupOptions.HasExtensionOverrides)
        {
            var selectedExtensions = new HashSet<string>(_startupOptions.IncludeExtensions, StringComparer.OrdinalIgnoreCase);
            foreach (var option in _viewModel.Extensions)
                option.IsChecked = selectedExtensions.Contains(option.Name);
        }

        if (_startupOptions.HasIgnoreOverrides)
        {
            var selectedIgnoreOptions = new HashSet<IgnoreOptionId>(_startupOptions.IgnoreOptions);
            foreach (var option in _viewModel.IgnoreOptions)
                option.IsChecked = selectedIgnoreOptions.Contains(option.Id);
        }

        await _selectionCoordinator.WaitForPendingRefreshesAsync();
        await RefreshTreeAsync();
        await _selectionCoordinator.UpdateLiveOptionsFromRootSelectionIfDirtyAsync(_currentPath);
        await _selectionCoordinator.WaitForPendingRefreshesAsync();
    }

    private async Task TryApplyStartupUiOptionsAsync()
    {
        var ui = _startupOptions.Ui;
        if (!ui.HasStartupActions || !_viewModel.IsProjectLoaded)
            return;

        if (ui.TreeFormat is { } treeFormat)
            _viewModel.SelectedExportFormat = MapStartupTreeFormat(treeFormat);

        if (ui.PreviewMode is { } previewMode)
            _viewModel.SelectedPreviewContentMode = MapStartupPreviewMode(previewMode);

        if (!string.IsNullOrWhiteSpace(ui.TreeFilter))
            await ApplyStartupTreeFilterAsync(ui.TreeFilter!);

        var shouldOpenPreview =
            ui.OpenPreview ||
            ui.PreviewMode is not null ||
            !string.IsNullOrWhiteSpace(ui.PreviewSearch);

        if (shouldOpenPreview && !_viewModel.IsPreviewMode)
            await OpenPreviewModeAsync();

        if (!string.IsNullOrWhiteSpace(ui.PreviewSearch))
            await ApplyStartupPreviewSearchAsync(ui.PreviewSearch!);
    }

    private async Task<bool> TryRunStartupUiBenchmarkScriptAsync()
    {
        if (!_startupOptions.UiBenchmarkScript.Enabled || !_viewModel.IsProjectLoaded)
            return false;

        try
        {
            switch (_startupOptions.UiBenchmarkScript.Script)
            {
                case StartupUiBenchmarkScript.Standard:
                    await RunStandardUiBenchmarkScriptAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            _sessionMetrics.RecordUiBenchmarkStep("scenario.failed", TimeSpan.Zero, success: false, ex.GetType().Name);
        }
        finally
        {
            await SettleUiBenchmarkStepAsync(TimeSpan.FromMilliseconds(250));
            Close();
        }

        return true;
    }

    private async Task RunStandardUiBenchmarkScriptAsync()
    {
        await RunUiBenchmarkStepAsync("startup.settle", async () =>
        {
            await _selectionCoordinator.WaitForPendingRefreshesAsync();
            await SettleUiBenchmarkStepAsync(TimeSpan.FromSeconds(1));
        });

        await RunUiBenchmarkStepAsync("preview.open", async () =>
        {
            await OpenPreviewModeAsync();
            await WaitForUiBenchmarkConditionAsync(
                () => _viewModel.IsPreviewMode && !_viewModel.IsPreviewLoading && !_previewPaneAnimating,
                TimeSpan.FromSeconds(30),
                "preview did not open");
            await SettleUiBenchmarkStepAsync(TimeSpan.FromSeconds(1));
        });

        await RunUiBenchmarkStepAsync("tree-format.json", () => ApplyUiBenchmarkTreeFormatAsync(ExportFormat.Json));
        await RunUiBenchmarkStepAsync("tree-format.xml", () => ApplyUiBenchmarkTreeFormatAsync(ExportFormat.Xml));
        await RunUiBenchmarkStepAsync("tree-format.md", () => ApplyUiBenchmarkTreeFormatAsync(ExportFormat.Markdown));
        await RunUiBenchmarkStepAsync("tree-format.ascii", () => ApplyUiBenchmarkTreeFormatAsync(ExportFormat.Ascii));

        await RunUiBenchmarkStepAsync("preview-mode.content", () => ApplyUiBenchmarkPreviewModeAsync(PreviewContentMode.Content));
        await RunUiBenchmarkStepAsync("preview-mode.tree-content", () => ApplyUiBenchmarkPreviewModeAsync(PreviewContentMode.TreeAndContent));
        await RunUiBenchmarkStepAsync("preview-mode.tree", () => ApplyUiBenchmarkPreviewModeAsync(PreviewContentMode.Tree));

        var searchQuery = ResolveUiBenchmarkQuery("service", "test", "src", "app");
        await RunUiBenchmarkStepAsync("search.apply", async () =>
        {
            await ApplyStartupPreviewSearchAsync(searchQuery);
            await WaitForUiBenchmarkConditionAsync(
                () => _viewModel.SearchVisible &&
                      string.Equals(_viewModel.SearchQuery, searchQuery, StringComparison.Ordinal) &&
                      !_viewModel.IsSearchInProgress,
                TimeSpan.FromSeconds(10),
                "search did not apply");
            await SettleUiBenchmarkStepAsync(TimeSpan.FromMilliseconds(500));
        });

        await RunUiBenchmarkStepAsync("search.navigate", async () =>
        {
            TryNavigateSearchMatches(1);
            await SettleUiBenchmarkStepAsync(TimeSpan.FromMilliseconds(500));
        });

        var filterQuery = ResolveUiBenchmarkQuery("test", "src", "service", "app");
        await RunUiBenchmarkStepAsync("filter.apply", async () =>
        {
            await ApplyStartupTreeFilterAsync(filterQuery);
            await WaitForUiBenchmarkConditionAsync(
                () => _viewModel.FilterVisible &&
                      string.Equals(_viewModel.NameFilter, filterQuery, StringComparison.Ordinal) &&
                      !_viewModel.IsFilterInProgress,
                TimeSpan.FromSeconds(20),
                "filter did not apply");
            await SettleUiBenchmarkStepAsync(TimeSpan.FromMilliseconds(500));
        });

        await RunUiBenchmarkStepAsync("filter.close", async () =>
        {
            await CloseFilterAsync(focusTree: false);
            await WaitForUiBenchmarkConditionAsync(
                () => !_viewModel.FilterVisible && string.IsNullOrEmpty(_viewModel.NameFilter),
                TimeSpan.FromSeconds(10),
                "filter did not close");
            await SettleUiBenchmarkStepAsync(TimeSpan.FromMilliseconds(500));
        });

        await RunUiBenchmarkStepAsync("preview.close", async () =>
        {
            ClosePreviewMode();
            await WaitForUiBenchmarkConditionAsync(
                () => !_viewModel.IsPreviewMode && !_previewPaneAnimating && !_treePaneAnimating,
                TimeSpan.FromSeconds(30),
                "preview did not close");
            await SettleUiBenchmarkStepAsync(TimeSpan.FromMilliseconds(500));
        });

        await RunUiBenchmarkStepAsync("idle.settle", () => SettleUiBenchmarkStepAsync(TimeSpan.FromMilliseconds(1500)));
    }

    private async Task RunUiBenchmarkStepAsync(string stepName, Func<Task> action)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await action();
            stopwatch.Stop();
            _sessionMetrics.RecordUiBenchmarkStep(stepName, stopwatch.Elapsed, success: true);
        }
        catch
        {
            stopwatch.Stop();
            _sessionMetrics.RecordUiBenchmarkStep(stepName, stopwatch.Elapsed, success: false);
            throw;
        }
    }

    private async Task ApplyUiBenchmarkTreeFormatAsync(ExportFormat format)
    {
        _viewModel.SelectedExportFormat = format;
        await WaitForUiBenchmarkConditionAsync(
            () => _viewModel.SelectedExportFormat == format && !_viewModel.IsPreviewLoading,
            TimeSpan.FromSeconds(20),
            $"tree format {format} did not settle");
        await SettleUiBenchmarkStepAsync(TimeSpan.FromMilliseconds(500));
    }

    private async Task ApplyUiBenchmarkPreviewModeAsync(PreviewContentMode mode)
    {
        await SwitchPreviewModeAsync(mode);
        await WaitForUiBenchmarkConditionAsync(
            () => _viewModel.SelectedPreviewContentMode == mode && !_viewModel.IsPreviewLoading && !_previewModeSwitchInProgress,
            TimeSpan.FromSeconds(20),
            $"preview mode {mode} did not settle");
        await SettleUiBenchmarkStepAsync(TimeSpan.FromMilliseconds(500));
    }

    private string ResolveUiBenchmarkQuery(params string[] candidates)
    {
        if (_currentTree?.Root is null)
            return candidates[0];

        foreach (var candidate in candidates)
        {
            if (NameFilterMatchCounter.CountMatchesUnderRoot(_currentTree.Root, candidate) > 0)
                return candidate;
        }

        return candidates[0];
    }

    private static async Task WaitForUiBenchmarkConditionAsync(
        Func<bool> condition,
        TimeSpan timeout,
        string timeoutMessage)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
                return;

            await YieldUiAsync(DispatcherPriority.Background);
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException(timeoutMessage);
    }

    private static async Task SettleUiBenchmarkStepAsync(TimeSpan minimumDelay)
    {
        await WaitForPreviewRenderPassesAsync();
        await Task.Delay(minimumDelay);
        await YieldUiAsync(DispatcherPriority.Background);
    }

    private async Task ApplyStartupTreeFilterAsync(string query)
    {
        var normalizedQuery = query.Trim();
        if (normalizedQuery.Length == 0 || !_viewModel.IsSearchFilterAvailable)
            return;

        if (IsSearchBarEffectivelyVisible())
            await CloseSearchAsync(focusTree: false);

        ShowFilter(focusInput: false, selectAllOnFocus: false);

        Interlocked.Increment(ref _suppressSearchFilterRealtimeDepth);
        try
        {
            _viewModel.SearchQuery = string.Empty;
            _viewModel.NameFilter = normalizedQuery;
        }
        finally
        {
            Interlocked.Decrement(ref _suppressSearchFilterRealtimeDepth);
        }

        _filterCoordinator.CancelPending();
        _viewModel.SetFilterInProgress(true);
        await ApplyFilterRealtimeAsync(CancellationToken.None);
    }

    private async Task ApplyStartupPreviewSearchAsync(string query)
    {
        var normalizedQuery = query.Trim();
        if (normalizedQuery.Length == 0 || !_viewModel.IsSearchFilterAvailable)
            return;

        if (IsFilterBarEffectivelyVisible())
            await CloseFilterAsync(focusTree: false);

        ShowSearch(focusInput: false, selectAllOnFocus: false);

        Interlocked.Increment(ref _suppressSearchFilterRealtimeDepth);
        try
        {
            _viewModel.NameFilter = string.Empty;
            _viewModel.SearchQuery = normalizedQuery;
        }
        finally
        {
            Interlocked.Decrement(ref _suppressSearchFilterRealtimeDepth);
        }

        _searchCoordinator.CancelPending();
        _searchCoordinator.UpdateSearchMatches();
    }

    private static ExportFormat MapStartupTreeFormat(TreeTextFormat format) => format switch
    {
        TreeTextFormat.Json => ExportFormat.Json,
        TreeTextFormat.Xml => ExportFormat.Xml,
        TreeTextFormat.Markdown => ExportFormat.Markdown,
        _ => ExportFormat.Ascii
    };

    private static PreviewContentMode MapStartupPreviewMode(StartupPreviewMode mode) => mode switch
    {
        StartupPreviewMode.Content => PreviewContentMode.Content,
        StartupPreviewMode.TreeContent => PreviewContentMode.TreeAndContent,
        _ => PreviewContentMode.Tree
    };

    private async Task TryWriteStartupReportAsync(TimeSpan loadingElapsed)
    {
        if (!_startupOptions.Report.Enabled ||
            string.IsNullOrWhiteSpace(_currentPath) ||
            _currentTree is null)
        {
            return;
        }

        var reportInput = new LoadedProjectAnalysisRequest(
            RootPath: _currentPath,
            Tree: _currentTree,
            AvailableRootFolders: _viewModel.RootFolders.Select(static option => option.Name).ToArray(),
            AvailableExtensions: _viewModel.Extensions.Select(static option => option.Name).ToArray(),
            SelectedRootFolders: _viewModel.RootFolders
                .Where(static option => option.IsChecked)
                .Select(static option => option.Name)
                .ToArray(),
            SelectedExtensions: _viewModel.Extensions
                .Where(static option => option.IsChecked)
                .Select(static option => option.Name)
                .ToArray(),
            SelectedIgnoreOptions: _selectionCoordinator.GetSelectedIgnoreOptionIds().ToArray(),
            RootAccessDenied: _currentTree.RootAccessDenied,
            HadAccessDenied: _currentTree.HadAccessDenied,
            KnownLoadingElapsed: loadingElapsed);

        try
        {
            var report = await Task.Run(
                () => _projectAnalysisService.BuildReportFromTreeAsync(reportInput),
                CancellationToken.None);
            var reportPath = _reportPathResolver.Resolve(_startupOptions.Report);
            await _projectAnalysisReportWriter.WriteAsync(report, reportPath);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    private async Task TryShowAutomaticTerminalCommandPromptAsync(CancellationToken cancellationToken)
    {
        try
        {
            await YieldUiAsync(DispatcherPriority.Background);
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await Task.Run(_terminalCommandSetupService.Probe, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var action = ResolveAutomaticTerminalCommandStartupAction(
                _userSettingsDb.ViewSettings,
                snapshot,
                !string.IsNullOrWhiteSpace(_startupOptions.Path));

            if (action == AutomaticTerminalCommandStartupAction.RepairSilently)
            {
                var repairResult = await Task.Run(
                    _terminalCommandSetupService.InstallOrRepair,
                    cancellationToken);
                if (repairResult.Success &&
                    TerminalCommandPromptPolicy.ShouldOfferAutomaticPrompt(
                        _userSettingsDb.ViewSettings,
                        repairResult.Snapshot,
                        !string.IsNullOrWhiteSpace(_startupOptions.Path)))
                {
                    await ShowTerminalCommandSetupAsync(
                        repairResult.Snapshot,
                        isAutomaticPrompt: true);
                }

                return;
            }

            if (action == AutomaticTerminalCommandStartupAction.ShowPrompt)
                await ShowTerminalCommandSetupAsync(snapshot, isAutomaticPrompt: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Terminal setup is optional; startup must stay resilient even if probing fails.
        }
    }

    internal static bool ShouldShowAutomaticTerminalCommandPrompt(
        AppViewSettings settings,
        TerminalCommandSetupSnapshot snapshot,
        bool startedWithProjectPath) =>
        ResolveAutomaticTerminalCommandStartupAction(settings, snapshot, startedWithProjectPath) ==
        AutomaticTerminalCommandStartupAction.ShowPrompt;

    internal static AutomaticTerminalCommandStartupAction ResolveAutomaticTerminalCommandStartupAction(
        AppViewSettings settings,
        TerminalCommandSetupSnapshot snapshot,
        bool startedWithProjectPath)
    {
        if (!LooksLikePublishedDevProjexExecutable(snapshot.TargetExecutablePath))
            return AutomaticTerminalCommandStartupAction.None;

        if (TerminalCommandPromptPolicy.ShouldRepairAutomatically(snapshot))
            return AutomaticTerminalCommandStartupAction.RepairSilently;

        return TerminalCommandPromptPolicy.ShouldOfferAutomaticPrompt(settings, snapshot, startedWithProjectPath)
            ? AutomaticTerminalCommandStartupAction.ShowPrompt
            : AutomaticTerminalCommandStartupAction.None;
    }

    private static bool LooksLikePublishedDevProjexExecutable(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return false;

        return CommandLineExecutableAliases.IsPublishedPortableFileName(
            GetFileNameCrossPlatform(executablePath));
    }

    private static string GetFileNameCrossPlatform(string path)
    {
        // Unit tests intentionally pass Windows-style paths on Linux runners.
        // Path.GetFileName* only recognizes the current OS separator, so keep
        // this prompt gate deterministic across CI platforms.
        var fileNameStart = Math.Max(
            path.LastIndexOf('/'),
            path.LastIndexOf('\\')) + 1;
        return path[fileNameStart..];
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

        var opts = _startupOptions with
        {
            Path = path,
            Language = _localization.CurrentLanguage,
            ElevationAttempted = true
        };

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
        bool applyStoredProfile = false,
        bool reuseUnchangedDiscoveryCaches = false)
    {
        if (string.IsNullOrEmpty(_currentPath)) return;
        cancellationToken.ThrowIfCancellationRequested();

        // A no-change F5 validates only directories previously inspected by scope discovery.
        // Structural changes, project switches and git operations still force a complete rebuild.
        var canReuseIgnoreRuleCaches = false;
        if (reuseUnchangedDiscoveryCaches)
        {
            canReuseIgnoreRuleCaches = await Task.Run(
                () => _ignoreRulesService.RevalidateCaches(_currentPath, cancellationToken),
                cancellationToken);
        }
        else
        {
            _ignoreRulesService.InvalidateCaches(_currentPath);
        }

        if (!canReuseIgnoreRuleCaches)
            _selectionCoordinator.InvalidateFileSystemCaches();

#if DEVPROJEX_PROJECT_LOAD_TIMING
        var timing = new ProjectLoadTiming();
        _projectLoadTiming = timing;
#endif

        if (applyStoredProfile)
        {
            var profileSnapshot = await Task.Run(
                () => _projectProfiles.LoadSnapshot(_currentPath),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (profileSnapshot is { HasProfile: true, Profile: not null })
                _selectionCoordinator.ApplyProjectProfileSelections(_currentPath, profileSnapshot.Profile);
            else
                _selectionCoordinator.ResetProjectProfileSelections(_currentPath);
        }

        await _projectLoadSnapshotPipeline.ReloadAsync(_currentPath, cancellationToken);
    }

    /// <summary>
    /// Clears state from previous project to release memory before loading a new one.
    /// </summary>
    private void ClearPreviousProjectState(bool forceCompactingGc = false)
    {
        _previewMemoryCleanupCts?.Cancel();
        _previewMemoryCleanupCts?.Dispose();
        _previewMemoryCleanupCts = null;

        // Background metrics become stale as soon as the visible tree is about to change.
        // Cancel them before tearing down the current project state to avoid wasted I/O.
        _metrics.CancelBackgroundCalculation();

        // Clear search state first (holds references to TreeNodeViewModel)
        _searchCoordinator.ClearSearchState();

        // Clear filter state
        _filterExpansionSnapshot = null;
        ResetInteractiveFilterCache();
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
        _metrics.ClearFileMetricsCache(trimCapacity: true);

        // Reconnect ItemsSource
        if (_treeView is not null)
            _treeView.ItemsSource = _viewModel.TreeNodes;

        // Clear current tree descriptor reference (this is the second copy of the tree)
        _currentTree = null;
        _filterBaseTree = null;
        _currentTreeInventory = null;
        _metrics.HasCompleteBaseline = false;
        _viewModel.StatusMetricsVisible = false;
        _viewModel.StatusTreeStatsText = string.Empty;
        _viewModel.StatusContentStatsText = string.Empty;
        ClearPreviewDocument();
        _viewModel.IsPreviewLoading = false;
        InvalidatePreviewCache();
        _metrics.InvalidateComputedCaches();

        // Clear icon cache to release bitmaps
        _iconCache.Clear();

        // A small project is cheaper to leave to generational GC. Forcing Gen2 here and then
        // again after the new load used to create two avoidable pauses on routine switches.
        // Large abandoned trees still cross this threshold and are reclaimed immediately.
        var shouldRunImmediateCleanup = MemoryCleanupPolicy.ShouldRunRoutineCleanup(
            GC.GetTotalMemory(forceFullCollection: false));
        if (forceCompactingGc && shouldRunImmediateCleanup)
        {
            // Full compacting collection — user is switching projects and expects memory
            // from the old tree (view models, icons, metrics cache) to be freed immediately.
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(1, GCCollectionMode.Forced, blocking: false);
            TrimNativeWorkingSet();
        }
        else if (shouldRunImmediateCleanup)
        {
            // A canceled/reset load can also leave a large generation behind, but does not
            // require LOH compaction unless this was an explicit project switch.
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        }
    }

    private bool TryBuildInteractiveFilteredTreeResult(
        string? nameFilter,
        CancellationToken cancellationToken,
        out BuildTreeResult result)
    {
        result = default!;
        var baseTree = _filterBaseTree;
        if (baseTree is null)
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(nameFilter))
        {
            ResetInteractiveFilterCache();
            result = baseTree;
            return true;
        }

        if (TryGetCachedInteractiveFilterRoot(nameFilter, out var cachedRoot))
        {
            _lastInteractiveFilterBaseRoot = baseTree.Root;
            _lastInteractiveFilteredRoot = cachedRoot;
            _lastInteractiveFilterQuery = nameFilter;
            result = new BuildTreeResult(
                Root: cachedRoot,
                RootAccessDenied: baseTree.RootAccessDenied,
                HadAccessDenied: baseTree.HadAccessDenied);
            return true;
        }

        // For incremental typing, prefer the narrowest known prefix source.
        // This reduces traversal work while preserving correctness.
        var filterSourceRoot = baseTree.Root;
        if (_lastInteractiveFilterBaseRoot is not null &&
            ReferenceEquals(_lastInteractiveFilterBaseRoot, baseTree.Root))
        {
            if (_lastInteractiveFilteredRoot is not null &&
                !string.IsNullOrWhiteSpace(_lastInteractiveFilterQuery) &&
                nameFilter.StartsWith(_lastInteractiveFilterQuery, StringComparison.OrdinalIgnoreCase))
            {
                filterSourceRoot = _lastInteractiveFilteredRoot;
            }
            else if (TryGetBestInteractiveFilterPrefixSource(nameFilter, out var prefixRoot))
            {
                filterSourceRoot = prefixRoot;
            }
        }

        var filteredRoot = FilterTreeForNameQuery(filterSourceRoot, nameFilter, cancellationToken);
        _lastInteractiveFilterBaseRoot = baseTree.Root;
        _lastInteractiveFilteredRoot = filteredRoot;
        _lastInteractiveFilterQuery = nameFilter;
        CacheInteractiveFilterRoot(nameFilter, filteredRoot);

        result = new BuildTreeResult(
            Root: filteredRoot,
            RootAccessDenied: baseTree.RootAccessDenied,
            HadAccessDenied: baseTree.HadAccessDenied);
        return true;
    }

    private static TreeNodeDescriptor FilterTreeForNameQuery(
        TreeNodeDescriptor root,
        string query,
        CancellationToken cancellationToken)
    {
        List<TreeNodeDescriptor>? filteredChildren = null;
        var originalChildren = root.Children;

        for (var index = 0; index < originalChildren.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var originalChild = originalChildren[index];
            var filteredChild = FilterTreeNodeByName(originalChild, query, cancellationToken);

            if (filteredChild is null)
            {
                if (filteredChildren is null)
                {
                    filteredChildren = new List<TreeNodeDescriptor>(Math.Min(originalChildren.Count, 16));
                    for (var j = 0; j < index; j++)
                        filteredChildren.Add(originalChildren[j]);
                }

                continue;
            }

            if (filteredChildren is not null)
            {
                filteredChildren.Add(filteredChild);
                continue;
            }

            if (!ReferenceEquals(filteredChild, originalChild))
            {
                filteredChildren = new List<TreeNodeDescriptor>(Math.Min(originalChildren.Count, 16));
                for (var j = 0; j < index; j++)
                    filteredChildren.Add(originalChildren[j]);
                filteredChildren.Add(filteredChild);
            }
        }

        if (filteredChildren is null)
            return root;

        return root with { Children = filteredChildren };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ResetInteractiveFilterCache()
    {
        _lastInteractiveFilteredRoot = null;
        _lastInteractiveFilterBaseRoot = null;
        _lastInteractiveFilterQuery = null;
        _interactiveFilterQueryCache.Clear();
        _interactiveFilterQueryCacheLru.Clear();
        _interactiveFilterQueryCacheNodes.Clear();
    }

    private bool TryGetCachedInteractiveFilterRoot(string query, out TreeNodeDescriptor root)
    {
        if (_interactiveFilterQueryCache.TryGetValue(query, out root!))
        {
            if (_interactiveFilterQueryCacheNodes.TryGetValue(query, out var node))
            {
                _interactiveFilterQueryCacheLru.Remove(node);
                _interactiveFilterQueryCacheLru.AddFirst(node);
            }

            return true;
        }

        root = null!;
        return false;
    }

    private bool TryGetBestInteractiveFilterPrefixSource(string query, out TreeNodeDescriptor root)
    {
        root = null!;
        string? bestPrefix = null;

        foreach (var cachedQuery in _interactiveFilterQueryCache.Keys)
        {
            if (string.IsNullOrWhiteSpace(cachedQuery))
                continue;

            if (!query.StartsWith(cachedQuery, StringComparison.OrdinalIgnoreCase))
                continue;

            if (bestPrefix is null || cachedQuery.Length > bestPrefix.Length)
                bestPrefix = cachedQuery;
        }

        if (bestPrefix is null)
            return false;

        return TryGetCachedInteractiveFilterRoot(bestPrefix, out root);
    }

    private void CacheInteractiveFilterRoot(string query, TreeNodeDescriptor root)
    {
        if (string.IsNullOrWhiteSpace(query))
            return;

        _interactiveFilterQueryCache[query] = root;

        if (_interactiveFilterQueryCacheNodes.TryGetValue(query, out var existingNode))
        {
            _interactiveFilterQueryCacheLru.Remove(existingNode);
            _interactiveFilterQueryCacheLru.AddFirst(existingNode);
            return;
        }

        var node = new LinkedListNode<string>(query);
        _interactiveFilterQueryCacheLru.AddFirst(node);
        _interactiveFilterQueryCacheNodes[query] = node;

        while (_interactiveFilterQueryCacheNodes.Count > InteractiveFilterQueryCacheLimit)
        {
            var last = _interactiveFilterQueryCacheLru.Last;
            if (last is null)
                break;

            _interactiveFilterQueryCacheLru.RemoveLast();
            _interactiveFilterQueryCacheNodes.Remove(last.Value);
            _interactiveFilterQueryCache.Remove(last.Value);
        }
    }

    private static TreeNodeDescriptor? FilterTreeNodeByName(
        TreeNodeDescriptor node,
        string query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var selfMatches = node.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase);
        if (node.Children.Count == 0)
            return selfMatches ? node : null;

        List<TreeNodeDescriptor>? filteredChildren = null;
        var originalChildren = node.Children;
        var matchedChildrenCount = 0;

        for (var index = 0; index < originalChildren.Count; index++)
        {
            var originalChild = originalChildren[index];
            var filteredChild = FilterTreeNodeByName(originalChild, query, cancellationToken);
            if (filteredChild is null)
            {
                if (filteredChildren is null)
                {
                    filteredChildren = new List<TreeNodeDescriptor>(Math.Min(originalChildren.Count, 8));
                    for (var j = 0; j < index; j++)
                        filteredChildren.Add(originalChildren[j]);
                }

                continue;
            }

            matchedChildrenCount++;
            if (filteredChildren is not null)
            {
                filteredChildren.Add(filteredChild);
                continue;
            }

            if (!ReferenceEquals(filteredChild, originalChild))
            {
                filteredChildren = new List<TreeNodeDescriptor>(Math.Min(originalChildren.Count, 8));
                for (var j = 0; j < index; j++)
                    filteredChildren.Add(originalChildren[j]);
                filteredChildren.Add(filteredChild);
            }
        }

        if (!selfMatches && matchedChildrenCount == 0)
            return null;

        if (filteredChildren is null)
            return node;

        return node with { Children = filteredChildren };
    }

    private async Task RefreshTreeAsync(bool interactiveFilter = false, CancellationToken cancellationToken = default)
    {
        await _refreshPipeline.RefreshTreeAsync(interactiveFilter, cancellationToken);
    }

    private TreeNodeViewModel BuildTreeViewModel(TreeNodeDescriptor descriptor, TreeNodeViewModel? parent)
    {
        return BuildTreeViewModelCore(
            descriptor,
            parent,
            materializeChildrenNow: parent is null,
            allowParallelAtThisLevel: parent is null);
    }

    private TreeNodeViewModel BuildTreeViewModelCore(
        TreeNodeDescriptor descriptor,
        TreeNodeViewModel? parent,
        bool materializeChildrenNow,
        bool allowParallelAtThisLevel)
    {
        var icon = _iconCache.GetIcon(descriptor.IconKey);
        // Eagerly building the entire view-model graph was one of the biggest remaining
        // startup costs on large projects. We now materialize only the root-visible level
        // during load and defer deeper branches until the UI actually needs them.
        var node = materializeChildrenNow || descriptor.Children.Count == 0
            ? new TreeNodeViewModel(descriptor, parent, icon, checkedChanged: OnTreeNodeCheckedChanged)
            : new TreeNodeViewModel(
                descriptor,
                parent,
                icon,
                BuildDeferredChildViewModels,
                OnTreeNodeCheckedChanged);

        if (!materializeChildrenNow || descriptor.Children.Count == 0)
            return node;

        foreach (var child in BuildImmediateChildViewModels(node, descriptor.Children, allowParallelAtThisLevel))
            node.Children.Add(child);

        return node;
    }

    private IReadOnlyList<TreeNodeViewModel> BuildDeferredChildViewModels(TreeNodeViewModel parent)
    {
        if (parent.Descriptor.Children.Count == 0)
            return [];

        return BuildImmediateChildViewModels(
            parent,
            parent.Descriptor.Children,
            allowParallelAtThisLevel: false);
    }

    private List<TreeNodeViewModel> BuildImmediateChildViewModels(
        TreeNodeViewModel parent,
        IReadOnlyList<TreeNodeDescriptor> children,
        bool allowParallelAtThisLevel)
    {
        if (children.Count == 0)
            return [];

        if (allowParallelAtThisLevel && children.Count >= TreeViewModelParallelChildrenThreshold)
        {
            // Only the first visible level is built eagerly. Deeper subtrees stay lazy until the
            // user expands them or a tree-wide operation explicitly traverses that branch.
            var childNodes = new TreeNodeViewModel[children.Count];
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(TreeViewModelBuildParallelism, children.Count)
            };

            Parallel.For(0, children.Count, parallelOptions, index =>
            {
                childNodes[index] = BuildTreeViewModelCore(
                    children[index],
                    parent,
                    materializeChildrenNow: false,
                    allowParallelAtThisLevel: false);
            });

            return [.. childNodes];
        }

        var realizedChildren = new List<TreeNodeViewModel>(children.Count);
        foreach (var child in children)
        {
            var childViewModel = BuildTreeViewModelCore(
                child,
                parent,
                materializeChildrenNow: false,
                allowParallelAtThisLevel: false);
            realizedChildren.Add(childViewModel);
        }

        return realizedChildren;
    }

    private void StartPostLoadBackgroundWork(BuildTreeResult currentTree, CancellationToken cancellationToken)
    {
        // The tree is already visible at this point. Keep any non-critical post-load work detached
        // so opening a project is no longer blocked by metrics warmup or cosmetic panel animation.
        var settingsRevealTask = StartDeferredSettingsPanelAnimationAsync(cancellationToken);
        _postLoadVisualReadyTask = settingsRevealTask;
        ObserveDetachedTask(settingsRevealTask, "AnimateSettingsPanelWhenTreeReady");
#if DEVPROJEX_PROJECT_LOAD_TIMING
        var timing = _projectLoadTiming;
        if (timing is not null && !timing.HasLoadingElapsed)
        {
            timing.LoadingElapsed = timing.LoadingStopwatch.Elapsed;
            timing.HasLoadingElapsed = true;
        }

        ObserveDetachedTask(
            TrackProjectAnalysisTimingAsync(
                _metrics.InitializeFileMetricsCacheSoonAfterFirstPaintMeasuredAsync(
                    currentTree,
                    settingsRevealTask,
                    cancellationToken),
                timing),
            "InitializeFileMetricsCache");
#else
        ObserveDetachedTask(
            _metrics.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
                currentTree,
                settingsRevealTask,
                cancellationToken),
            "InitializeFileMetricsCache");
#endif
    }

#if DEVPROJEX_PROJECT_LOAD_TIMING
    private async Task TrackProjectAnalysisTimingAsync(
        Task<TimeSpan> metricsWarmupTask,
        ProjectLoadTiming? timing)
    {
        var analysisElapsed = await metricsWarmupTask;

        if (timing is null ||
            !timing.HasLoadingElapsed ||
            !ReferenceEquals(_projectLoadTiming, timing))
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                if (!ReferenceEquals(_projectLoadTiming, timing))
                    return;

                ApplyProjectLoadTimingTitleSuffix(
                    timing.LoadingElapsed,
                    analysisElapsed);
                _projectLoadTiming = null;
            },
            DispatcherPriority.Background);
    }
#endif

    private Task StartDeferredSettingsPanelAnimationAsync(CancellationToken cancellationToken)
    {
        if (_settingsAnimating)
            return _settingsAnimationTask;

        // A visible-width island is already on screen (notably during F5). Treating it as a new
        // reveal would delay metrics and make existing status values disappear and jump again.
        if (!SettingsPanelRevealPolicy.ShouldRunInitialReveal(
                _viewModel.SettingsVisible,
                settingsAnimating: false,
                HasVisibleSettingsPanelWidth()))
        {
            return Task.CompletedTask;
        }

        return AnimateSettingsPanelWhenTreeReadyAsync(cancellationToken);
    }

    private async Task AnimateSettingsPanelWhenTreeReadyAsync(CancellationToken cancellationToken)
    {
        await WaitForTreeRenderStabilizationAsync(cancellationToken);
        if (_viewModel.SettingsVisible)
            await AnimateSettingsPanelAsync(true);
    }

    private static async void ObserveDetachedTask(Task task, string operationName)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Detached post-load tasks are routinely canceled by refresh/reload operations.
        }
        catch (ObjectDisposedException)
        {
            // Cancellation source disposal during shutdown/reload is expected.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WARN] Background task '{operationName}' failed: {ex}");
        }
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

    private static string? ResolveDropFolderPath(IEnumerable<string?> localPaths)
    {
        return localPaths.FirstOrDefault(path =>
            !string.IsNullOrWhiteSpace(path) && Directory.Exists(path));
    }

    private static DragDropEffects ResolveDropEffect(bool hasFolder)
    {
        return hasFolder ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private static string BuildWindowTitle(
        string? currentPath,
        bool isGitMode,
        string? currentRepositoryUrl,
        string? currentBranch,
        string? currentProjectDisplayName)
    {
        if (string.IsNullOrWhiteSpace(currentPath))
            return MainWindowViewModel.BaseTitleWithAuthor;

        if (isGitMode && !string.IsNullOrEmpty(currentRepositoryUrl))
        {
            var displayRepositoryUrl = RepositoryWebPathPresentationService.NormalizeForDisplay(currentRepositoryUrl);
            if (string.IsNullOrWhiteSpace(displayRepositoryUrl))
                displayRepositoryUrl = currentRepositoryUrl;

            var branchDisplay = !string.IsNullOrEmpty(currentBranch)
                ? $" [{currentBranch}]"
                : string.Empty;
            return $"{MainWindowViewModel.BaseTitle} - {displayRepositoryUrl}{branchDisplay}";
        }

        var displayPath = !string.IsNullOrEmpty(currentProjectDisplayName)
            ? currentProjectDisplayName
            : currentPath;

        return $"{MainWindowViewModel.BaseTitle} - {displayPath}";
    }

    private void UpdateTitle()
    {
        _viewModel.Title = BuildWindowTitle(
            _currentPath,
            _viewModel.IsGitMode,
            _currentRepositoryUrl,
            _viewModel.CurrentBranch,
            _currentProjectDisplayName);
    }

#if DEVPROJEX_PROJECT_LOAD_TIMING
    private void ApplyProjectLoadTimingTitleSuffix(TimeSpan loadingElapsed, TimeSpan analysisElapsed)
    {
        var baseTitle = BuildWindowTitle(
            _currentPath,
            _viewModel.IsGitMode,
            _currentRepositoryUrl,
            _viewModel.CurrentBranch,
            _currentProjectDisplayName);
        var totalElapsed = loadingElapsed + analysisElapsed;
        var timingSuffix =
            $"[{FormatSeconds(loadingElapsed)} + {FormatSeconds(analysisElapsed)} = {FormatSeconds(totalElapsed)}]";

        _viewModel.Title = $"{baseTitle} {timingSuffix}";

        static string FormatSeconds(TimeSpan elapsed) =>
            elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture);
    }
#endif

    private IgnoreRules BuildIgnoreRules(
        string rootPath,
        IReadOnlyCollection<IgnoreOptionId> selectedOptions,
        IReadOnlyCollection<string>? selectedRootFolders)
    {
        var rules = _ignoreRulesService.Build(rootPath, selectedOptions, selectedRootFolders);
        if (!_viewModel.IsGitMode ||
            string.IsNullOrWhiteSpace(_currentCachedRepoPath) ||
            !PathComparer.Default.Equals(rootPath, _currentCachedRepoPath))
        {
            return rules;
        }

        return rules with { ExcludedRootFolderName = ".git" };
    }

    private IgnoreOptionsAvailability GetIgnoreOptionsAvailability(
        string rootPath,
        IReadOnlyCollection<string> selectedRootFolders)
    {
        var availability = _ignoreRulesService.GetIgnoreOptionsAvailability(rootPath, selectedRootFolders);
        return availability with
        {
            ShowAdvancedCounts = AdvancedIgnoreCountsAlwaysEnabled
        };
    }

    private IgnoreRules BuildIgnoreRules(string rootPath)
    {
        var selected = _selectionCoordinator.GetSelectedIgnoreOptionIds();
        var selectedRoots = _selectionCoordinator.GetSelectedRootFolders();
        return BuildIgnoreRules(rootPath, selected, selectedRoots);
    }

    /// <summary>
    /// Cancels any active background metrics calculation.
    /// Call this before starting user-initiated operations that need the status bar.
    /// </summary>
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
            PreviewWorkspaceMode: _viewModel.PreviewWorkspaceMode,
            StatusMetricsVisible: _viewModel.StatusMetricsVisible,
            StatusTreeStatsText: _viewModel.StatusTreeStatsText,
            StatusContentStatsText: _viewModel.StatusContentStatsText,
            AllRootFoldersChecked: _viewModel.AllRootFoldersChecked,
            AllExtensionsChecked: _viewModel.AllExtensionsChecked,
            AllIgnoreChecked: _viewModel.AllIgnoreChecked,
            HasCompleteMetricsBaseline: _metrics.HasCompleteBaseline,
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
        return _projectLoadCancellation.TryApply(
            ResetToInitialProjectStateAfterCancellation,
            RestorePreviousProjectStateAfterCancellation);
    }

    private void RestorePreviousProjectStateAfterCancellation(ProjectLoadCancellationSnapshot snapshot)
    {
        _currentPath = snapshot.Path;
        _currentProjectDisplayName = snapshot.ProjectDisplayName;
        _currentRepositoryUrl = snapshot.RepositoryUrl;
        _currentTree = snapshot.Tree;
        _filterBaseTree = snapshot.Tree;
        _currentTreeInventory = null;
        _previewOnlySuspendedTreeToolMode = SuspendedTreeToolMode.None;
        ResetInteractiveFilterCache();
        _metrics.InvalidateComputedCaches();

        _viewModel.IsProjectLoaded = true;
        _viewModel.SettingsVisible = snapshot.SettingsVisible;
        _viewModel.SearchVisible = snapshot.SearchVisible;
        _viewModel.FilterVisible = snapshot.FilterVisible;
        _viewModel.SetPreviewCompactModeActive(snapshot.PreviewWorkspaceMode != PreviewWorkspaceMode.Off);
        _viewModel.PreviewWorkspaceMode = snapshot.PreviewWorkspaceMode;
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
        _metrics.HasCompleteBaseline = snapshot.HasCompleteMetricsBaseline;
        UpdateCompactModeVisualState();
        UpdateWorkspaceLayoutForCurrentMode();
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
        _projectLoadCancellation.Clear();
        _metrics.CancelBackgroundCalculation();
        CancelPreviewRefresh();
        ClearPreviousProjectState();

        _currentPath = null;
        _currentTree = null;
        _filterBaseTree = null;
        _currentTreeInventory = null;
        _currentProjectDisplayName = null;
        _currentRepositoryUrl = null;
        _filterExpansionSnapshot = null;
        _previewOnlySuspendedTreeToolMode = SuspendedTreeToolMode.None;
        ResetInteractiveFilterCache();

        _viewModel.IsProjectLoaded = false;
        _viewModel.SettingsVisible = false;
        _viewModel.SearchVisible = false;
        _viewModel.FilterVisible = false;
        _viewModel.SetPreviewCompactModeActive(false);
        _viewModel.PreviewWorkspaceMode = PreviewWorkspaceMode.Off;
        _viewModel.StatusMetricsVisible = false;
        _viewModel.ProjectSourceType = ProjectSourceType.LocalFolder;
        _viewModel.CurrentBranch = string.Empty;
        _viewModel.GitBranches.Clear();
        _viewModel.RootFolders.Clear();
        _viewModel.Extensions.Clear();
        _viewModel.IgnoreOptions.Clear();
        UpdateCompactModeVisualState();
        UpdateWorkspaceLayoutForCurrentMode();
        UpdateBranchMenu();

        _metrics.UpdateStatusBarMetrics(0, 0, 0, 0, 0, 0);
        UpdateTitle();
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

    private static HashSet<string> CollectCheckedOptionNames(
        IEnumerable<SelectionOptionViewModel> options,
        StringComparer comparer)
    {
        var selected = new HashSet<string>(comparer);
        foreach (var option in options)
        {
            if (option.IsChecked)
                selected.Add(option.Name);
        }

        return selected;
    }

    private HashSet<string> GetCheckedPaths()
        => _treeSelectionSnapshotCache.GetOrCreate(_viewModel.TreeNodes);

    private IReadOnlyList<string> BuildOrderedUniqueFilePaths(IReadOnlySet<string> selectedPaths)
    {
        if (selectedPaths.Count > 0)
        {
            if (_currentTree is null)
                return [];

            return BuildOrderedSelectedFilePaths(_currentTree.Root, selectedPaths);
        }

        return _currentTree is null
            ? []
            : _currentTree.OrderedFilePaths ?? _metrics.GetOrBuildAllOrderedFilePaths(_currentTree.Root);
    }

    private static List<string> BuildOrderedSelectedFilePaths(
        TreeNodeDescriptor treeRoot,
        IReadOnlySet<string> selectedPaths,
        bool ensureExists = true) =>
        PreviewFileCollectionPolicy.BuildOrderedSelectedFilePaths(selectedPaths, treeRoot, ensureExists);

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
        using (TreeNodeViewModel.BeginPreserveDescendantExpansionStateScope())
        {
            TreeNodeViewModel.ForEachDescendant(_viewModel.TreeNodes, node =>
                node.IsExpanded = expandedPaths.Contains(node.FullPath));
        }

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

    private bool IsBackgroundMetricsActive() => _metrics.IsBackgroundActive;

    private void OnTreeNodeCheckedChanged(TreeNodeViewModel _)
    {
        _treeSelectionSnapshotCache.Invalidate();
        _metrics.ScheduleRecalculate();
        SchedulePreviewRefresh();
    }

    private void RenderPreviewSelectionMetrics()
    {
        if (!_hasPreviewSelectionMetricsSnapshot)
        {
            _viewModel.StatusPreviewSelectionVisible = false;
            _viewModel.StatusPreviewSelectionStatsText = string.Empty;
            return;
        }

        _viewModel.StatusPreviewSelectionStatsText = PreviewSelectionMetricsPolicy.FormatStatusMetricsText(
            _lastPreviewSelectionMetrics,
            BuildStatusMetricLabels(),
            useCompactMode: false);
        _viewModel.StatusPreviewSelectionVisible = true;
    }

    private StatusMetricLabels BuildStatusMetricLabels()
    {
        var linesLabel = _localization.Format("Status.Metric.Lines", "{0}");
        var charsLabel = _localization.Format("Status.Metric.Chars", "{0}");
        var tokensLabel = _localization.Format("Status.Metric.Tokens", "{0}");

        return new StatusMetricLabels(
            linesLabel.Replace("{0}", string.Empty).Trim(),
            charsLabel.Replace("{0}", string.Empty).Trim(),
            tokensLabel.Replace("{0}", string.Empty).Trim());
    }

    private void SchedulePreviewSelectionMetricsUpdate(bool immediate = false)
    {
        if (!_viewModel.IsAnyPreviewVisible || _previewTextControl is null)
        {
            ClearPreviewSelectionMetrics();
            return;
        }

        if (!_previewTextControl.TryGetSelectionRange(out _))
        {
            ClearPreviewSelectionMetrics();
            return;
        }

        if (immediate)
        {
            _previewSelectionMetricsDebounceTimer?.Stop();
            RecalculatePreviewSelectionMetricsAsync();
            return;
        }

        if (_previewSelectionMetricsDebounceTimer is null)
        {
            _previewSelectionMetricsDebounceTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = PreviewSelectionMetricsDebounceInterval
            };
            _previewSelectionMetricsDebounceTimer.Tick += OnPreviewSelectionMetricsDebounceTick;
        }

        _previewSelectionMetricsDebounceTimer.Stop();
        _previewSelectionMetricsDebounceTimer.Start();
    }

    private void OnPreviewSelectionMetricsDebounceTick(object? sender, EventArgs e)
    {
        _previewSelectionMetricsDebounceTimer?.Stop();
        RecalculatePreviewSelectionMetricsAsync();
    }

    private void RecalculatePreviewSelectionMetricsAsync()
    {
        if (!TryCapturePreviewSelectionMetricsSnapshot(out var snapshot))
        {
            ClearPreviewSelectionMetrics();
            return;
        }

        if (TryGetCachedPreviewSelectionMetrics(snapshot, out var cachedMetrics))
        {
            _previewSelectionMetricsDebounceTimer?.Stop();
            var previousCts = Interlocked.Exchange(ref _previewSelectionMetricsCts, null);
            previousCts?.Cancel();
            previousCts?.Dispose();
            Interlocked.Increment(ref _previewSelectionMetricsVersion);
            _lastPreviewSelectionMetrics = cachedMetrics;
            _hasPreviewSelectionMetricsSnapshot = true;
            RenderPreviewSelectionMetrics();
            return;
        }

        var metricsCts = ReplaceCancellationSource(ref _previewSelectionMetricsCts);
        var token = metricsCts.Token;
        var version = Interlocked.Increment(ref _previewSelectionMetricsVersion);

        _ = RecalculatePreviewSelectionMetricsCoreAsync(snapshot, metricsCts, token, version);
    }

    private async Task RecalculatePreviewSelectionMetricsCoreAsync(
        PreviewSelectionMetricsSnapshot snapshot,
        CancellationTokenSource metricsCts,
        CancellationToken cancellationToken,
        int version)
    {
        try
        {
            var metrics = await Task.Run(
                () => PreviewSelectionMetricsCalculator.Calculate(
                    snapshot.Document,
                    snapshot.SelectionRange,
                    cancellationToken),
                cancellationToken);

            if (cancellationToken.IsCancellationRequested)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested ||
                    version != Volatile.Read(ref _previewSelectionMetricsVersion))
                {
                    return;
                }

                if (!TryCapturePreviewSelectionMetricsSnapshot(out var currentSnapshot) ||
                    !ReferenceEquals(currentSnapshot.Document, snapshot.Document) ||
                    currentSnapshot.SelectionRange != snapshot.SelectionRange)
                {
                    return;
                }

                _lastPreviewSelectionMetrics = metrics;
                _hasPreviewSelectionMetricsSnapshot = metrics != ExportOutputMetrics.Empty;
                RenderPreviewSelectionMetrics();
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            DisposeIfCurrent(ref _previewSelectionMetricsCts, metricsCts);
        }
    }

    private bool TryCapturePreviewSelectionMetricsSnapshot(out PreviewSelectionMetricsSnapshot snapshot)
    {
        snapshot = default;

        if (!_viewModel.IsAnyPreviewVisible || _previewTextControl is null)
            return false;

        var document = _previewTextControl.Document ?? _viewModel.PreviewDocument;
        if (document is null)
            return false;

        if (!_previewTextControl.TryGetSelectionRange(out var selectionRange))
            return false;

        snapshot = new PreviewSelectionMetricsSnapshot(document, selectionRange);
        return true;
    }

    private bool TryGetCachedPreviewSelectionMetrics(
        PreviewSelectionMetricsSnapshot snapshot,
        out ExportOutputMetrics metrics)
    {
        return _metrics.TryGetCachedPreviewSelectionMetrics(
            _viewModel.SelectedPreviewContentMode,
            snapshot.Document,
            snapshot.SelectionRange,
            out metrics);
    }

    private void ClearPreviewSelectionMetrics()
    {
        _previewSelectionMetricsDebounceTimer?.Stop();
        var previousCts = Interlocked.Exchange(ref _previewSelectionMetricsCts, null);
        previousCts?.Cancel();
        previousCts?.Dispose();
        Interlocked.Increment(ref _previewSelectionMetricsVersion);

        _lastPreviewSelectionMetrics = ExportOutputMetrics.Empty;
        _hasPreviewSelectionMetricsSnapshot = false;
        _viewModel.StatusPreviewSelectionVisible = false;
        _viewModel.StatusPreviewSelectionStatsText = string.Empty;
    }

    private void OnStatusOperationCancelRequested(object? sender, RoutedEventArgs e)
    {
        var activeOperation = _statusOperations.GetActiveSnapshot();
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
                _projectLoadPipeline.CancelActiveLoad();
                break;
            case StatusOperationType.RefreshProject:
                _projectOperationCts?.Cancel();
                _refreshPipeline.CancelActiveRefresh();
                break;
            case StatusOperationType.GitPullUpdates:
            case StatusOperationType.GitSwitchBranch:
                _gitOperationCts?.Cancel();
                break;
            case StatusOperationType.PreviewBuild:
                _previewPipeline.CancelActiveBuild();
                break;
            case StatusOperationType.SelectionRefresh:
                if (_selectionCoordinator.CancelPendingRefreshes())
                    _toastService.Show(_localization["Toast.Operation.RefreshCanceled"]);
                break;
            case StatusOperationType.ApplySettings:
                _applySettingsCts?.Cancel();
                _selectionCoordinator.CancelPendingRefreshes();
                _refreshPipeline.CancelActiveRefresh();
                break;
            case StatusOperationType.ProjectCopyExport:
                _projectCopyExportCts?.Cancel();
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
            _metrics.CancelByUser();
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
            _previewPipeline.CancelActiveBuild();
            _viewModel.IsPreviewLoading = false;
            _toastService.Show(_viewModel.ToastPreviewCanceled);
        }

        _statusOperations.Complete(activeOperationId);
    }

    #endregion

}
