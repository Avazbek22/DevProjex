using DevProjex.Avalonia.Controls;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Avalonia.Services;
using DevProjex.Avalonia.Views;
using DevProjex.Application.Context;
using DevProjex.Application.DesktopControl;
using DevProjex.Infrastructure.RecentProjects;
using DevProjex.Infrastructure.Reports;
using DevProjex.Kernel;
using DevProjex.Terminal.DesktopControl;
using ThemeSettingsStore =
    DevProjex.Infrastructure.ThemePresets.ThemeSettingsStore;
using UserSettingsStore =
    DevProjex.Infrastructure.ThemePresets.UserSettingsStore;

namespace DevProjex.Avalonia;

public partial class MainWindow
{
    public MainWindow()
        : this(DesktopStartupOptions.Default, AvaloniaCompositionRoot.CreateDefault(DesktopStartupOptions.Default))
    {
    }

    private readonly DesktopStartupOptions _startupOptions;
    private readonly DesktopOpenRequest? _desktopStartupRequest;
    private readonly LocalizationService _localization;
    private readonly ScanOptionsUseCase _scanOptions;
    private readonly BuildTreeUseCase _buildTree;
    private readonly IgnoreOptionsService _ignoreOptionsService;
    private readonly IgnoreRulesService _ignoreRulesService;
    private readonly FilterOptionSelectionService _filterSelectionService;
    private readonly TreeExportService _treeExport;
    private readonly SelectedContentExportService _contentExport;
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
    private readonly RecentWorkspacesService _recentWorkspacesService;
    private readonly RecentFolderAvailabilityService _recentFolderAvailabilityService;
    private readonly HashSet<string> _unavailableRecentFolderPaths = new(PathComparer.Default);

    private readonly MainWindowViewModel _viewModel;
    private readonly SearchFilterInteractionController _searchFilterController;
    private readonly WorkspacePresentationController _workspacePresentation;
    private readonly PreviewSurfaceController _previewSurfaceController;
    private readonly PreviewWorkspaceController _previewWorkspaceController;
    private readonly StartupInteractionController _startupInteractions;
    private readonly MemoryCleanupCoordinator _memoryCleanup;
    private readonly TreeViewportController _treeViewport;
    private readonly AppearanceSettingsController _appearanceSettings;
    private readonly ThemeBrushCoordinator _themeBrushCoordinator;
    private readonly bool _isMicaSupported = ThemeEffectPlatformSupport.IsMicaSupportedOnCurrentPlatform();
    private readonly SelectionSyncCoordinator _selectionCoordinator;
    private readonly StatusOperationCoordinator _statusOperations;
    private readonly MetricsPipeline _metrics;
    private readonly ProjectLoadPipeline _projectLoadPipeline;
    private readonly ProjectLoadSnapshotPipeline _projectLoadSnapshotPipeline;
    private readonly PreviewWorkspacePipeline _previewPipeline;
    private readonly RefreshTreePipeline _refreshPipeline;
    private readonly ProjectTextOutputPipeline _textOutputPipeline;
    private readonly ProjectProfilePersistenceCoordinator _projectProfiles;
    private readonly ProjectLoadCancellationCoordinator _projectLoadCancellation = new();
    private readonly TaskbarProgressCoordinator _taskbarProgress;
    private readonly SemaphoreSlim _desktopInteractionGate = new(1, 1);
    private readonly TaskCompletionSource<bool> _shutdownCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DesktopControlServer? _desktopControlServer;
    private bool _desktopStartupReady;
    private string? _desktopStartupErrorCode;

    private BuildTreeResult? _currentTree;
    private BuildTreeResult? _filterBaseTree;
    private ProjectTreeInventoryState? _currentTreeInventory;
    private bool _lastInteractiveFilterUsedInMemory;
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
    private bool _themeEffectRuntimeProbeReady;
    private bool _awaitingSystemDialogActivation;
    private TaskCompletionSource<bool>? _systemDialogActivationTcs;

    private TreeView? _treeView;
    private TopMenuBarView? _topMenuBar;
    private Grid? _workspaceGrid;
    private Grid? _treePaneRoot;
    private Border? _treePaneContainer;
    private Border? _treePaneSnapshotHost;
    private Image? _treePaneSnapshotImage;
    private Border? _previewPaneContainer;
    private Grid? _previewPaneSurface;
    private ColumnDefinition? _treePaneColumn;
    private ColumnDefinition? _previewPaneColumn;
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
    private CancellationTokenSource? _projectOperationCts;
    private CancellationTokenSource? _applySettingsCts;
    private Task _latestApplySettingsTask = Task.CompletedTask;
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

    private Border? _settingsContainer;
    private Border? _settingsIsland;
    private SettingsPanelView? _settingsPanel;
    private Task _projectLoadFinalizationTask = Task.CompletedTask;
    private Task _postLoadVisualReadyTask = Task.CompletedTask;
    private static readonly TimeSpan SettingsPanelAnimationDuration =
        WorkspacePresentationController.SettingsPanelAnimationDuration;

    private Border? _previewBarContainer;
    private Border? _previewBar;
    private Grid? _previewSegmentGrid;
    private Border? _previewSegmentThumb;
    private Button? _previewTreeModeButton;
    private Button? _previewContentModeButton;
    private Button? _previewTreeAndContentModeButton;
    private const double StartupRevealHiddenOpacity = 0.0;
    private const double StartupRevealVisibleOpacity = 1.0;
    private static readonly TimeSpan StartupBackdropWarmupDelay = UiTimingProfile.Scale(TimeSpan.FromMilliseconds(90));
    private bool _startupRevealGateActive;
    private bool _startupRevealCompleted;
    private CancellationTokenSource? _windowLifetimeCts = new();
    private int _startupSequenceStarted;
    private Rect _pendingWindowBounds;
    private bool _windowBoundsFramePending;

    private static readonly int TreeViewModelBuildParallelism =
        Math.Clamp(Environment.ProcessorCount, min: 2, max: 12);
    private const int TreeViewModelParallelChildrenThreshold = 24;

    private readonly TreeSelectionSnapshotCache _treeSelectionSnapshotCache = new();

    // Event handler delegates for proper unsubscription
    private EventHandler? _languageChangedHandler;
    private EventHandler? _themeChangedHandler;
    private PropertyChangedEventHandler? _viewModelPropertyChangedHandler;

    private readonly IReadOnlyList<string> _startupErrors;
    private readonly ITerminalCommandSetupService _terminalCommandSetupService;
    private readonly SessionMetricsRecorder _sessionMetrics;

    public MainWindow(
        DesktopStartupOptions startupOptions,
        AvaloniaAppServices services,
        IReadOnlyList<string>? startupErrors = null)
    {
        PrepareStartupRevealGate();

        _startupOptions = startupOptions;
        _desktopStartupRequest = startupOptions.OpenRequest;
        _startupErrors = startupErrors ?? [];
        _localization = services.Localization;
        _scanOptions = services.ScanOptionsUseCase;
        _buildTree = services.BuildTreeUseCase;
        _ignoreOptionsService = services.IgnoreOptionsService;
        _ignoreRulesService = services.IgnoreRulesService;
        _filterSelectionService = services.FilterOptionSelectionService;
        _treeExport = services.TreeExportService;
        _contentExport = services.ContentExportService;
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
        _terminalCommandSetupService = services.TerminalCommandSetupService;
        _sessionMetrics = services.SessionMetricsRecorder;
        _recentProjectsStore = services.RecentProjectsStore;
        _recentWorkspacesService = services.RecentWorkspacesService;
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
            () => Bounds.Width,
            ScheduleBackgroundMemoryCleanup);
        _previewPipeline = new PreviewWorkspacePipeline(
            this,
            // 350ms delay ensures thumb animation (250ms) completes fully before loading.
            UiTimingProfile.Scale(TimeSpan.FromMilliseconds(350)));
        _refreshPipeline = new RefreshTreePipeline(this);
        _textOutputPipeline = new ProjectTextOutputPipeline(
            _treeExport,
            _contentExport,
            services.TreeAndContentExportService);
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
        _sessionMetrics.Start(_startupOptions.EffectiveSessionMetrics.ProjectPath, GetApplicationVersion());
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
        _previewPaneSurface = this.FindControl<Grid>("PreviewPaneSurface");
        _treePreviewSplitter = this.FindControl<Border>("TreePreviewSplitter");
        _previewSettingsSplitter = this.FindControl<Border>("PreviewSettingsSplitter");
        _treeIsland = this.FindControl<Border>("TreeIsland");
        _previewIsland = this.FindControl<Border>("PreviewIsland");
        _toastHost = this.FindControl<ItemsControl>("ToastHost");
        if (_workspaceGrid is not null && _workspaceGrid.ColumnDefinitions.Count >= 3)
        {
            _treePaneColumn = _workspaceGrid.ColumnDefinitions[0];
            _previewPaneColumn = _workspaceGrid.ColumnDefinitions[1];
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
        _settingsContainer = this.FindControl<Border>("SettingsContainer");
        _settingsIsland = this.FindControl<Border>("SettingsIsland");
        _settingsPanel = this.FindControl<SettingsPanelView>("SettingsPanel");
        _workspacePresentation = new WorkspacePresentationController(
            this,
            _viewModel,
            new WorkspacePresentationControls(
                _workspaceGrid ?? throw new InvalidOperationException("Workspace grid was not found."),
                _treePaneContainer ?? throw new InvalidOperationException("Tree pane container was not found."),
                _previewPaneContainer ?? throw new InvalidOperationException("Preview pane container was not found."),
                _treePaneColumn ?? throw new InvalidOperationException("Tree pane column was not found."),
                _previewPaneColumn ?? throw new InvalidOperationException("Preview pane column was not found."),
                _treePreviewSplitter ?? throw new InvalidOperationException("Tree splitter was not found."),
                _previewSettingsSplitter ?? throw new InvalidOperationException("Settings splitter was not found."),
                _treeIsland ?? throw new InvalidOperationException("Tree island was not found."),
                _previewIsland ?? throw new InvalidOperationException("Preview island was not found."),
                _dropZoneContainer ?? throw new InvalidOperationException("Drop zone was not found."),
                _toastHost ?? throw new InvalidOperationException("Toast host was not found."),
                _previewBarContainer ?? throw new InvalidOperationException("Preview bar container was not found."),
                _previewBar ?? throw new InvalidOperationException("Preview bar was not found."),
                _previewSegmentGrid ?? throw new InvalidOperationException("Preview segment grid was not found."),
                _previewTreeModeButton ?? throw new InvalidOperationException("Preview tree button was not found."),
                _previewContentModeButton ?? throw new InvalidOperationException("Preview content button was not found."),
                _previewTreeAndContentModeButton ??
                throw new InvalidOperationException("Combined preview button was not found."),
                _settingsContainer ?? throw new InvalidOperationException("Settings container was not found."),
                _settingsIsland ?? throw new InvalidOperationException("Settings island was not found."),
                _settingsPanel ?? throw new InvalidOperationException("Settings panel was not found.")));

        if (_previewSegmentGrid is not null)
            _previewSegmentGrid.SizeChanged += OnPreviewSegmentGridSizeChanged;
        if (_previewBar is not null)
            _previewBar.SizeChanged += OnPreviewBarSizeChanged;

        if (_treeView is not null)
        {
            _treeView.PointerEntered += OnTreePointerEntered;
        }
        AddHandler(
            PointerPressedEvent,
            OnWindowPointerPressedForMemoryCleanup,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, OnWindowPointerWheelChanged, RoutingStrategies.Tunnel, true);

        _searchFilterController = new SearchFilterInteractionController(
            this,
            _viewModel,
            _treeView ?? throw new InvalidOperationException("Project tree control was not found."),
            _searchBar ?? throw new InvalidOperationException("Search bar control was not found."),
            this.FindControl<Border>("SearchBarContainer")
                ?? throw new InvalidOperationException("Search bar container was not found."),
            _filterBar ?? throw new InvalidOperationException("Filter bar control was not found."),
            this.FindControl<Border>("FilterBarContainer")
                ?? throw new InvalidOperationException("Filter bar container was not found."),
            _sessionMetrics,
            _toastService,
            _localization,
            () => _currentPath,
            () => _currentTree,
            (interactive, token) => RefreshTreeAsync(interactive, token),
            ResetInteractiveFilterCache,
            () => _lastInteractiveFilterUsedInMemory,
            ex => ShowErrorAsync(ex.Message),
            ScheduleBackgroundMemoryCleanup,
            CancelAllMemoryCleanup);
        _previewSurfaceController = new PreviewSurfaceController(
            this,
            _viewModel,
            new PreviewSurfaceControls(
                _previewTextScrollViewer ??
                throw new InvalidOperationException(
                    "Preview scroll viewer was not found."),
                _previewTextControl ??
                throw new InvalidOperationException(
                    "Preview text control was not found."),
                _previewLineNumbersControl ??
                throw new InvalidOperationException(
                    "Preview line numbers control was not found."),
                _previewStickyHeaderCap ??
                throw new InvalidOperationException(
                    "Preview sticky header cap was not found."),
                _previewStickyHeaderContainer ??
                throw new InvalidOperationException(
                    "Preview sticky header container was not found."),
                _previewStickyHeaderText ??
                throw new InvalidOperationException(
                    "Preview sticky header text was not found.")),
            _localization,
            _toastService,
            _previewDocumentBuilder,
            _contentExport,
            _textOutputPipeline,
            _treeExport,
            _metrics,
            _previewPipeline,
            EnsureTrackedGitOutputReady,
            SetClipboardTextAsync,
            ShowErrorAsync);
        _previewWorkspaceController = new PreviewWorkspaceController(
            this,
            _viewModel,
            new PreviewWorkspaceControls(
                _treeView ?? throw new InvalidOperationException("Project tree control was not found."),
                _treePaneRoot ?? throw new InvalidOperationException("Tree pane root was not found."),
                _treePaneContainer ?? throw new InvalidOperationException("Tree pane container was not found."),
                _treePaneSnapshotHost ?? throw new InvalidOperationException("Tree snapshot host was not found."),
                _treePaneSnapshotImage ?? throw new InvalidOperationException("Tree snapshot image was not found."),
                _previewPaneContainer ?? throw new InvalidOperationException("Preview pane container was not found."),
                _previewPaneSurface ?? throw new InvalidOperationException("Preview pane surface was not found."),
                _treePaneColumn ?? throw new InvalidOperationException("Tree pane column was not found."),
                _previewPaneColumn ?? throw new InvalidOperationException("Preview pane column was not found."),
                _treePreviewSplitter ?? throw new InvalidOperationException("Tree splitter was not found."),
                _previewBar ?? throw new InvalidOperationException("Preview bar was not found."),
                _previewSegmentGrid ?? throw new InvalidOperationException("Preview segment grid was not found."),
                _previewSegmentThumb ?? throw new InvalidOperationException("Preview segment thumb was not found."),
                _previewTreeModeButton ?? throw new InvalidOperationException("Preview tree button was not found."),
                _previewContentModeButton ?? throw new InvalidOperationException("Preview content button was not found."),
                _previewTreeAndContentModeButton ??
                throw new InvalidOperationException("Combined preview button was not found."),
                _previewTextScrollViewer ?? throw new InvalidOperationException("Preview scroll viewer was not found."),
                _previewTextControl ?? throw new InvalidOperationException("Preview text control was not found.")),
            _workspacePresentation,
            _searchFilterController,
            _previewPipeline,
            immediate => SchedulePreviewRefresh(immediate),
            _previewSurfaceController.ClearSelectionMetrics,
            ClearPreviewMemory,
            SchedulePreviewMemoryCleanup,
            CancelAllMemoryCleanup,
            UpdateCompactModeVisualState);
        _startupInteractions = CreateStartupInteractionController(
            _desktopStartupRequest,
            _startupOptions.DiagnosticScenario);
        _memoryCleanup = new MemoryCleanupCoordinator(
            _sessionMetrics,
            () => IsVisible &&
                  !_viewModel.StatusBusy &&
                  !_viewModel.IsPreviewLoading &&
                  !_workspacePresentation.IsSettingsAnimating &&
                  !_workspacePresentation.IsPreviewPaneAnimating &&
                  !_workspacePresentation.IsTreePaneAnimating &&
                  !_searchFilterController.IsAnimating &&
                  !_previewWorkspaceController.IsModeSwitchInProgress,
            SettingsPanelAnimationDuration);
        _treeViewport = new TreeViewportController(
            _viewModel,
            new TreeViewportControls(
                _treeView ??
                throw new InvalidOperationException(
                    "Project tree control was not found."),
                _treeIsland ??
                throw new InvalidOperationException(
                    "Tree island was not found."),
                _previewIsland ??
                throw new InvalidOperationException(
                    "Preview island was not found."),
                _previewLineNumbersBackground ??
                throw new InvalidOperationException(
                    "Preview line numbers background was not found."),
                _previewTextScrollViewer ??
                throw new InvalidOperationException(
                    "Preview scroll viewer was not found."),
            _previewLineNumbersControl ??
                throw new InvalidOperationException(
                    "Preview line numbers control was not found.")),
            CancelAllMemoryCleanup,
            ScheduleBackgroundMemoryCleanup);
        _themeBrushCoordinator = new ThemeBrushCoordinator(this, _viewModel, () => _topMenuBar?.MainMenuControl);
        _appearanceSettings = new AppearanceSettingsController(
            this,
            _viewModel,
            _localization,
            _userSettingsStore,
            _themeSettingsStore,
            _themeBrushCoordinator,
            _workspacePresentation,
            RefreshThemeHighlightsForActiveQuery,
            _isMicaSupported,
            _desktopStartupRequest?.Language);
        _appearanceSettings.Initialize();
        RefreshLanguageMenuChecks();
        // Publish builds can reach the first native frame much faster than local debug runs.
        // Prime the saved material/backdrop before the startup reveal gate waits for the
        // first render frames, otherwise the gate would only hide the default XAML surface.
        ApplyStartupThemePreset();
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
        Activated += OnActivated;
        Deactivated += OnDeactivated;

        _elevationAttempted = startupOptions.ElevationAttempted ||
                              _desktopStartupRequest?.ElevationAttempted == true;

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
                _searchFilterController.OnSearchQueryChanged();
            }
            else if (args.PropertyName == nameof(MainWindowViewModel.NameFilter))
            {
                _searchFilterController.OnNameFilterChanged();
            }
            else if (args.PropertyName is nameof(MainWindowViewModel.BackgroundTransparency)
                     or nameof(MainWindowViewModel.PanelContrast)
                     or nameof(MainWindowViewModel.BorderVisibility)
                     or nameof(MainWindowViewModel.MenuTransparency))
            {
                _appearanceSettings.MarkPresetDirty();
                _themeBrushCoordinator.ScheduleDynamicThemeBrushUpdate();
            }
            else if (args.PropertyName == nameof(MainWindowViewModel.ThemePopoverOpen))
                _appearanceSettings.HandleThemePopoverStateChange();
            else if (args.PropertyName == nameof(MainWindowViewModel.IsProjectLoaded))
                UpdateDropZoneAnimationState();
            else if (args.PropertyName is nameof(MainWindowViewModel.StatusBusy)
                     or nameof(MainWindowViewModel.StatusOperationVisible)
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
                if (!_previewWorkspaceController.IsModeSwitchInProgress)
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
                Dispatcher.Post(
                    _previewSurfaceController.RefreshStickyPath,
                    DispatcherPriority.Render);
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

    private StartupInteractionController CreateStartupInteractionController(
        DesktopOpenRequest? request,
        DesktopDiagnosticScenario? diagnosticScenario) =>
        new(
            request,
            diagnosticScenario,
            _viewModel,
            _selectionCoordinator,
            _searchFilterController,
            _previewWorkspaceController,
            _workspacePresentation,
            _sessionMetrics,
            () => _currentPath,
            () => _currentTree?.Root,
            () => RefreshTreeAsync(),
            path => TryOpenFolderAsync(
                path,
                fromDialog: false,
                recordRecentFolder: false),
            Close);

}
