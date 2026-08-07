using DevProjex.Avalonia.Coordinators;
using DevProjex.Avalonia.Services;
using DevProjex.Avalonia.Views;
using DevProjex.Application.DesktopControl;
using DevProjex.Application.Secrets;
using DevProjex.Infrastructure.RecentProjects;
using DevProjex.Terminal.DesktopControl;
using ThemeSettingsStore =
    DevProjex.Infrastructure.ThemePresets.ThemeSettingsStore;
using UserSettingsStore =
    DevProjex.Infrastructure.ThemePresets.UserSettingsStore;

namespace DevProjex.Avalonia;

public partial class MainWindow
{
	private void OnSecretRedactionSnapshotPublished(
		object? sender,
		SecretRedactionSnapshotPublishedEventArgs eventArgs)
	{
		Dispatcher.UIThread.Post(() =>
		{
			var snapshot = GetCachedSecretRedactionSnapshotForCurrentSelection();
			if (snapshot is null || snapshot.SelectionKey != eventArgs.Snapshot.SelectionKey)
				return;

			_secretRedactionMatchedCount = snapshot.DetectedCount;
			_secretRedactionCount = snapshot.RedactedCount;
			_secretRedactionScanState = SecretScanState.Completed;
			_viewModel.SetContentProcessingStatus(
				SecretScanState.Completed,
				snapshot.DetectedCount,
				snapshot.RedactedCount);
			RelabelIgnoreOptionsWithCurrentCounts();
		});
	}

	/// <summary>
	/// Reports what compression did to the selection that is on screen. A snapshot for a different
	/// selection is ignored rather than shown: stale counts next to a live preview read as a claim
	/// about the current files.
	/// </summary>
	private void OnCodeCompressionSnapshotPublished(object? sender, EventArgs eventArgs)
	{
		Dispatcher.UIThread.Post(() =>
		{
			if (_windowLifetimeCts is not { IsCancellationRequested: false })
				return;

			var enabled = CreateCodeCompressionContext() is not null;
			var snapshot = enabled ? GetCompressionSnapshotForCurrentSelection() : null;
			_codeCompressionSnapshot = snapshot;
			_viewModel.SetCompressionStatus(snapshot, enabled);
			RelabelIgnoreOptionsWithCurrentCounts();
		});
	}

	private CodeCompressionSnapshot? GetCompressionSnapshotForCurrentSelection()
	{
		if (_currentTree is null || string.IsNullOrWhiteSpace(_currentPath))
			return null;

		var selectedPaths = GetCheckedPaths();
		var files = selectedPaths.Count > 0
			? BuildOrderedSelectedFilePaths(_currentTree.Root, selectedPaths)
			: _currentTree.OrderedFilePaths ??
			  PreviewFileCollectionPolicy.BuildOrderedAllFilePaths(_currentTree.Root);
		var snapshot = _codeCompressionSession.Snapshot;
		return snapshot.SelectionKey == CodeCompressionSession.BuildSelectionKey(_currentPath, files)
			? snapshot
			: null;
	}

	/// <summary>Relabels every content transformation row from the counts published so far.</summary>
	private void RelabelIgnoreOptionsWithCurrentCounts()
	{
		// This runs on the UI thread whenever the transformation rows change - on toggle, on project
		// load, and after a snapshot - which makes it the right place to refresh the copy the
		// metrics worker reads.
		PublishTransformationContext();
		_selectionCoordinator.RelabelIgnoreOptions(
			AdvancedIgnoreCountsAlwaysEnabled,
			_secretRedactionCount,
			_secretRedactionScanState,
			_secretRedactionMatchedCount,
			_codeCompressionSnapshot?.CompressedFiles,
			_codeCompressionSnapshot?.UnchangedFiles);
	}

	private SecretRedactionSnapshot? GetCachedSecretRedactionSnapshotForCurrentSelection()
	{
		if (_windowLifetimeCts is not { IsCancellationRequested: false } ||
		    _currentTree is null ||
		    string.IsNullOrWhiteSpace(_currentPath) ||
		    !_selectionCoordinator.GetSelectedIgnoreOptionIds().Contains(IgnoreOptionId.HideSecrets))
		{
			return null;
		}

		var selectedPaths = GetCheckedPaths();
		var files = selectedPaths.Count > 0
			? BuildOrderedSelectedFilePaths(_currentTree.Root, selectedPaths)
			: _currentTree.OrderedFilePaths ??
			  PreviewFileCollectionPolicy.BuildOrderedAllFilePaths(_currentTree.Root);
		return _secretRedactionSession.GetSnapshot(_currentPath, files);
	}

	private CodeCompressionContext? CreateCodeCompressionContext()
	{
		if (string.IsNullOrWhiteSpace(_currentPath) ||
		    !_selectionCoordinator.GetSelectedIgnoreOptionIds().Contains(IgnoreOptionId.CompressCode))
		{
			return null;
		}

		return new CodeCompressionContext(_currentPath, _codeCompressionSession);
	}

	/// <summary>
	/// The transformation state as last seen on the UI thread, for callers that run on a worker.
	/// The metrics pipeline is one of them, and resolving the context there would read the option
	/// collection and the current path off-thread while the UI is free to be mutating both.
	/// </summary>
	private ContentTransformationContext? PublishedTransformationContext =>
		Volatile.Read(ref _publishedTransformationContext);

	private void PublishTransformationContext() =>
		Volatile.Write(ref _publishedTransformationContext, CreateContentTransformationContext());

	/// <summary>
	/// The enabled transformations as one ordered pipeline. Every output surface takes this, so
	/// preview, clipboard, exports and the project copy cannot disagree about what was applied.
	/// </summary>
	private ContentTransformationContext? CreateContentTransformationContext() =>
		ContentTransformationContext.For(CreateCodeCompressionContext(), CreateSecretRedactionContext());

	private SecretRedactionContext? CreateSecretRedactionContext()
	{
		if (string.IsNullOrWhiteSpace(_currentPath) ||
		    !_selectionCoordinator.GetSelectedIgnoreOptionIds().Contains(IgnoreOptionId.HideSecrets))
		{
			return null;
		}

		return new SecretRedactionContext(_currentPath, _secretRedactionSession);
	}

	private void ScheduleSecretRedactionCountRefresh()
	{
		var refreshVersion = Interlocked.Increment(ref _secretRedactionCountRefreshVersion);
		CancelAndDispose(ref _secretRedactionCountCts);

		if (_secretRedactionCount is not null ||
		    _viewModel.IsAnyPreviewVisible ||
		    _windowLifetimeCts is not { IsCancellationRequested: false } ||
		    _currentTree is null ||
		    string.IsNullOrWhiteSpace(_currentPath) ||
		    !_selectionCoordinator.GetSelectedIgnoreOptionIds().Contains(IgnoreOptionId.HideSecrets))
		{
			return;
		}

		_secretRedactionScanState = SecretScanState.Scanning;
		_viewModel.SetContentProcessingStatus(SecretScanState.Scanning);
		_selectionCoordinator.RelabelIgnoreOptions(
			AdvancedIgnoreCountsAlwaysEnabled,
			secretRedactionsCount: null,
			_secretRedactionScanState,
			secretMatchesCount: null);

		var selectedPaths = GetCheckedPaths();
		var files = selectedPaths.Count > 0
			? BuildOrderedSelectedFilePaths(_currentTree.Root, selectedPaths)
			: _currentTree.OrderedFilePaths ??
			  PreviewFileCollectionPolicy.BuildOrderedAllFilePaths(_currentTree.Root);
		var projectPath = _currentPath;
		var countCts = new CancellationTokenSource();
		_secretRedactionCountCts = countCts;
		_ = RefreshSecretRedactionCountAsync(
			new SecretRedactionContext(projectPath, _secretRedactionSession),
			files,
			refreshVersion,
			countCts);
	}

	private async Task RefreshSecretRedactionCountAsync(
		SecretRedactionContext context,
		IReadOnlyList<string> files,
		long refreshVersion,
		CancellationTokenSource countCts)
	{
		try
		{
			await _secretRedactionPreparer
				.AnalyzeAsync(context, files, countCts.Token)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (countCts.IsCancellationRequested)
		{
			// A newer selection or the real Preview owns the next scan.
		}
		catch (Exception exception)
		{
			if (refreshVersion != Volatile.Read(ref _secretRedactionCountRefreshVersion) ||
			    _windowLifetimeCts is not { IsCancellationRequested: false })
			{
				return;
			}

			var message = ResolveUserFacingOutputErrorMessage(exception);
			await Dispatcher.UIThread.InvokeAsync(() =>
			{
				_secretRedactionScanState = SecretScanState.Failed;
				_viewModel.SetContentProcessingStatus(SecretScanState.Failed);
				_selectionCoordinator.RelabelIgnoreOptions(
					AdvancedIgnoreCountsAlwaysEnabled,
					secretRedactionsCount: null,
					_secretRedactionScanState,
					secretMatchesCount: null);
				return ShowErrorAsync(message);
			});
		}
		finally
		{
			DisposeIfCurrent(ref _secretRedactionCountCts, countCts);
		}
	}

	private string ResolveUserFacingOutputErrorMessage(Exception exception) => exception switch
	{
		SecretScanLimitExceededException =>
			_localization["Error.ProjectCopy.SecretScanLimitExceeded"],
		SecretDetectionException =>
			_localization["Error.ProjectCopy.SecretDetectionFailed"],
		_ => exception.Message
	};

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
    private readonly ApplicationUpdateCoordinator _applicationUpdates;
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
    private Task<RecentProjectsDb>? _recentProjectsLoadTask;
    private bool _recentProjectsLoaded;
    private bool _recentMenuMaterialized;
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
    private Border? _startupBackdropCover;
    private static readonly TimeSpan StartupBackdropFallbackTimeout =
        UiTimingProfile.Scale(TimeSpan.FromMilliseconds(90));
    private static readonly TimeSpan StartupVisualReadyTimeout =
        UiTimingProfile.Scale(TimeSpan.FromMilliseconds(500));
    private static readonly TimeSpan StartupBackdropRevealDuration =
        UiTimingProfile.Scale(TimeSpan.FromMilliseconds(100));
    private bool _startupRevealGateActive;
    private bool _startupRevealCompleted;
    private bool _startupWindowCloaked;
    private bool _fontCatalogLoadScheduled;
    private bool _fontCatalogLoaded;
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
	private readonly SecretRedactionSession _secretRedactionSession;
	private readonly CodeCompressionSession _codeCompressionSession;
	private CodeCompressionSnapshot? _codeCompressionSnapshot;
	private ContentTransformationContext? _publishedTransformationContext;
	private readonly SecretRedactionOutputPreparer _secretRedactionPreparer;
	private CancellationTokenSource? _secretRedactionCountCts;
	private long _secretRedactionCountRefreshVersion;
	private int? _secretRedactionCount;
	private int? _secretRedactionMatchedCount;
	private SecretScanState _secretRedactionScanState = SecretScanState.Disabled;

    public MainWindow(
        DesktopStartupOptions startupOptions,
        AvaloniaAppServices services,
        IReadOnlyList<string>? startupErrors = null)
    {
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
		_secretRedactionSession = services.SecretRedactionSession;
		_codeCompressionSession = services.CodeCompressionSession;
		_secretRedactionPreparer = new SecretRedactionOutputPreparer(services.FileContentAnalyzer);
		_secretRedactionSession.SnapshotPublished += OnSecretRedactionSnapshotPublished;
		_codeCompressionSession.SnapshotPublished += OnCodeCompressionSnapshotPublished;
        _recentProjectsStore = services.RecentProjectsStore;
        _recentWorkspacesService = services.RecentWorkspacesService;
        _recentFolderAvailabilityService = services.RecentFolderAvailabilityService;

        _viewModel = new MainWindowViewModel(_localization, services.HelpContentProvider);
        _applicationUpdates = new ApplicationUpdateCoordinator(
            _viewModel,
            services.ApplicationUpdateService,
            _userSettingsStore,
            GetApplicationVersion());
        InitializeDefaultFont();
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
            ScheduleBackgroundMemoryCleanup,
            () => PublishedTransformationContext);
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
            _statusOperations,
            ScheduleContentTransformationRefresh,
            InvalidateSecretRedactionCount);
        _projectLoadPipeline = new ProjectLoadPipeline(this, _statusOperations);
        _projectLoadSnapshotPipeline = new ProjectLoadSnapshotPipeline(this);
        _projectProfiles = new ProjectProfilePersistenceCoordinator(
			_viewModel,
			_selectionCoordinator,
			services.ProjectProfileStore,
			_secretRedactionSession);
        _taskbarProgress = new TaskbarProgressCoordinator(
            _viewModel,
            services.TaskbarProgressService);
        _viewModel.SetToastItems(_toastService.Items);
        _sessionMetrics.SetIdleStateProvider(IsSessionMetricsIdle);
        _sessionMetrics.Start(_startupOptions.EffectiveSessionMetrics.ProjectPath, GetApplicationVersion());
        if (_desktopStartupRequest?.UseLastProject == true)
            LoadRecentProjectsSynchronously();
        DataContext = _viewModel;

        InitializeComponent();

        _startupBackdropCover = this.FindControl<Border>("StartupBackdropCover");

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
			_secretRedactionPreparer,
			_secretRedactionSession,
            _contentExport,
            _textOutputPipeline,
            _treeExport,
            _metrics,
            _previewPipeline,
            EnsureTrackedGitOutputReady,
            SetClipboardTextAsync,
			ShowErrorAsync,
			CreateContentTransformationContext,
			ScheduleContentTransformationRefresh,
			() => _selectionCoordinator.ApplyHideSecretsOverride(true),
			() => _projectProfiles.PersistIfNeeded(_currentPath));
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
        ConfigureStartupRevealGateForTheme();
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

        RefreshTreeFontMenu();
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
            else if (args.PropertyName == nameof(MainWindowViewModel.ActiveThemeEffect))
                _topMenuBar?.RefreshOpenPopupBackdrops();
            else if (args.PropertyName == nameof(MainWindowViewModel.ThemePopoverOpen))
                _appearanceSettings.HandleThemePopoverStateChange();
            else if (args.PropertyName == nameof(MainWindowViewModel.UpdatePopoverOpen) &&
                     _viewModel.UpdatePopoverOpen)
            {
                _viewModel.HelpPopoverOpen = false;
                _viewModel.HelpDocsPopoverOpen = false;
                _viewModel.ThemePopoverOpen = false;
            }
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
				if (_viewModel.IsAnyPreviewVisible)
					CancelAndDispose(ref _secretRedactionCountCts);
				else
					ScheduleSecretRedactionCountRefresh();
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
