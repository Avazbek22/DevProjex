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
		Dispatcher.UIThread.Post(() => TryApplySecretRedactionSnapshot(eventArgs.Snapshot));
	}

	private void TryApplySecretRedactionSnapshot(SecretRedactionSnapshot publishedSnapshot)
	{
		// Scanning is opt-in: a snapshot finishing after the user switched the option off must
		// not repopulate counters that the disable path just cleared.
		var enabled = _selectionCoordinator
			.GetSelectedIgnoreOptionIds()
			.Contains(IgnoreOptionId.HideSecrets);
		if (!enabled)
			return;

		var snapshot = GetCachedSecretRedactionSnapshotForCurrentSelection();
		if (snapshot is null || snapshot.SelectionKey != publishedSnapshot.SelectionKey)
			return;

		_secretRedactionMatchedCount = snapshot.DetectedCount;
		_secretRedactionCount = snapshot.RedactedCount;
		_secretRedactionScanState = ResolveSecretScanState(snapshot);
		_viewModel.SetContentProcessingStatus(
			_secretRedactionScanState,
			snapshot.DetectedCount,
			_secretRedactionCount,
			snapshot.SkippedFileCount,
			snapshot.FailedFileCount);
		RelabelIgnoreOptionsWithCurrentCounts();
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
			RelabelIgnoreOptionsWithCurrentCounts();
		});
	}

	private CodeCompressionSnapshot? GetCompressionSnapshotForCurrentSelection()
	{
		if (_currentTree is null || string.IsNullOrWhiteSpace(_currentPath))
			return null;

		var files = GetOrderedSelectedFilePaths();
		var snapshot = _codeCompressionSession.Snapshot;
		var context = CreateCodeCompressionContext();
		return context is not null &&
		       snapshot.SelectionKey == CodeCompressionSession.BuildSelectionKey(_currentPath, files) &&
		       snapshot.TransformIdentity.Equals(context.TransformIdentity, StringComparison.Ordinal)
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
			_codeCompressionSnapshot?.BodyTransformedFiles,
			_codeCompressionSnapshot?.UnchangedFiles,
			_codeCompressionSnapshot?.CommentTransformedFiles,
			_codeCompressionSnapshot?.UnchangedFiles,
			_codeCompressionSnapshot?.BlankLineTransformedFiles,
			_codeCompressionSnapshot?.UnchangedFiles);
		_viewModel.SetCompressionStatus(
			_codeCompressionSnapshot?.BodyTransformedFiles,
			_codeCompressionSnapshot?.TotalFiles,
			_codeCompressionSnapshot?.SourceCharacters,
			_codeCompressionSnapshot?.TransformedCharacters);
		_viewModel.SetCommentStripStatus(
			_codeCompressionSnapshot?.CommentTransformedFiles,
			_codeCompressionSnapshot?.TotalFiles);
		_viewModel.SetBlankLineStripStatus(
			_codeCompressionSnapshot?.BlankLineTransformedFiles,
			_codeCompressionSnapshot?.TotalFiles);
	}

	private void ScheduleCompressionRefreshForSelectionChange()
	{
		var version = Interlocked.Increment(ref _compressionSelectionRefreshVersion);
		_metrics.CancelCompressionPrewarm();
		_codeCompressionSnapshot = null;
		RelabelIgnoreOptionsWithCurrentCounts();
		if (_currentTree is null)
			return;

		ObserveDetachedTask(
			PrewarmCompressionForSelectionChangeAsync(version, _currentTree),
			"PrewarmCodeCompressionAfterSelectionChange");
	}

	private async Task PrewarmCompressionForSelectionChangeAsync(
		long version,
		BuildTreeResult currentTree)
	{
		// Yield once so rapid consecutive user changes collapse onto the latest selection before
		// native compression work starts. A single parent checkbox already emits one tree event.
		await Task.Yield();
		if (version != Volatile.Read(ref _compressionSelectionRefreshVersion))
			return;

		await _metrics
			.PrewarmCompressionAsync(
				currentTree,
				CancellationToken.None,
				cleanupAfterCompletion:
					MemoryCleanupReason.ApplySettingsWorkCompleted)
			.ConfigureAwait(false);
	}

	private async Task ApplyContentTransformationSettingsAsync(
		BuildTreeResult currentTree,
		CancellationToken cancellationToken)
	{
		CaptureAppliedContentTransformationState();
		_metrics.CancelAndDiscardBackgroundCalculation();
		_viewModel.StatusMetricsVisible = false;
		_codeCompressionSnapshot = null;
		InvalidatePreviewCache();
		InvalidateSecretRedactionCount(scheduleRefreshImmediately: false);
		RelabelIgnoreOptionsWithCurrentCounts();
		SchedulePreviewRefresh(immediate: true);

		await RunPostLoadBackgroundWorkAsync(
			Task.CompletedTask,
			currentTree,
			StatusOperationPresentation.ExtendedDelay,
			token => _metrics.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
				currentTree,
				Task.CompletedTask,
				token,
				StatusOperationPresentation.ExtendedDelay),
			MemoryCleanupReason.ApplySettingsWorkCompleted,
			cancellationToken);
	}

	private SecretRedactionSnapshot? GetCachedSecretRedactionSnapshotForCurrentSelection()
	{
		if (_windowLifetimeCts is not { IsCancellationRequested: false } ||
		    _currentTree is null ||
		    string.IsNullOrWhiteSpace(_currentPath))
		{
			return null;
		}

		var files = GetOrderedSelectedFilePaths();
		return _secretRedactionSession.GetSnapshot(
			_currentPath,
			files,
			GetCurrentSecretTransformIdentity());
	}

	private string GetCurrentSecretTransformIdentity() =>
		CreateCodeCompressionContext()?.TransformIdentity ?? string.Empty;

	private CodeCompressionContext? CreateCodeCompressionContext()
	{
		// Syntax transformations are drafts until «Apply settings» publishes a tree. Only the
		// captured state drives output, previews and counters; uncommitted checkboxes do no work.
		if (string.IsNullOrWhiteSpace(_currentPath))
			return null;

		var kinds = CodeTransformIdentity.Resolve(
			_appliedCompressCodeEnabled,
			_appliedStripCommentsEnabled,
			_appliedStripBlankLinesEnabled);
		return kinds == CodeTransformKinds.None
			? null
			: new CodeCompressionContext(_currentPath, _codeCompressionSession, kinds);
	}

	private void CaptureAppliedContentTransformationState()
	{
		var selectedOptions = _selectionCoordinator.GetSelectedIgnoreOptionIds();
		_appliedCompressCodeEnabled = selectedOptions.Contains(IgnoreOptionId.CompressCode);
		_appliedStripCommentsEnabled = selectedOptions.Contains(IgnoreOptionId.StripComments);
		_appliedStripBlankLinesEnabled = selectedOptions.Contains(IgnoreOptionId.StripBlankLines);
		if (!_appliedCompressCodeEnabled &&
		    !_appliedStripCommentsEnabled &&
		    !_appliedStripBlankLinesEnabled)
		{
			_codeCompressionSnapshot = null;
		}

		_viewModel.SetAppliedContentTransformationState(
			_appliedCompressCodeEnabled,
			_appliedStripCommentsEnabled,
			_appliedStripBlankLinesEnabled);
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

	private void ScheduleSecretRedactionCountRefresh(
		StatusOperationPresentation presentation = StatusOperationPresentation.ExtendedDelay)
	{
		// Scanning is opt-in: nothing runs while Hide Secrets is off, and a visible preview owns
		// the strict analysis for the enabled state, so background discovery stands down there too.
		var hideSecretsEnabled = _selectionCoordinator
			.GetSelectedIgnoreOptionIds()
			.Contains(IgnoreOptionId.HideSecrets);
		if (!hideSecretsEnabled ||
		    _viewModel.IsAnyPreviewVisible ||
		    _windowLifetimeCts is not { IsCancellationRequested: false } ||
		    _currentTree is null ||
		    string.IsNullOrWhiteSpace(_currentPath))
		{
			return;
		}

		// After a selection change this scheduler is the first reader of the ordered file list,
		// and building it walks every tree descriptor. Only the view-model checkbox walk needs
		// the UI thread; the descriptor projection runs on a worker and reschedules this refresh.
		var files = _treeSelectionSnapshotCache.TryGetOrderedFiles(_currentTree.Root);
		if (files is null)
		{
			BeginOrderedSelectionProjectionBuild(presentation);
			return;
		}

		var compressionContext = CreateCodeCompressionContext();
		var request = new SecretDiscoveryRequest(
			_currentPath,
			_currentTree,
			files,
			compressionContext?.TransformIdentity ?? string.Empty,
			_secretRedactionSession.OutputRevision,
			new ContentTransformationContext(
				compressionContext,
				new SecretRedactionContext(_currentPath, _secretRedactionSession)),
			presentation == StatusOperationPresentation.Immediate
				? SecretDiscoveryCacheMode.RevalidateContent
				: SecretDiscoveryCacheMode.ReuseValidatedContent,
			presentation);

		var activeRequest = Volatile.Read(ref _activeSecretDiscoveryRequest);
		if (_secretRedactionCountCts is { IsCancellationRequested: false } &&
		    activeRequest is not null &&
		    activeRequest.HasSameInput(request) &&
		    SatisfiesCacheMode(activeRequest.CacheMode, request.CacheMode))
		{
			return;
		}

		if (request.CacheMode == SecretDiscoveryCacheMode.ReuseValidatedContent)
		{
			var cachedSnapshot = _secretRedactionSession.GetSnapshot(
				request.ProjectRoot,
				request.Files,
				request.TransformIdentity);
			// A failure snapshot is shown, never reused: read failures are usually transient (a
			// file locked by another program), and reusing one would make every later toggle of
			// the same selection replay the failure without ever reopening the file.
			if (cachedSnapshot is { HasFailures: false })
			{
				CancelSecretRedactionDiscovery();
				TryApplySecretRedactionSnapshot(cachedSnapshot);
				return;
			}
		}

		CancelAndDispose(ref _secretRedactionCountCts);
		var refreshVersion = Interlocked.Increment(ref _secretRedactionCountRefreshVersion);
		var countCts = new CancellationTokenSource();
		_secretRedactionCountCts = countCts;
		Volatile.Write(ref _activeSecretDiscoveryRequest, request);
		_ = RunSecretRedactionCountRefreshAsync(
			request,
			refreshVersion,
			countCts);
	}

	private async Task RunSecretRedactionCountRefreshAsync(
		SecretDiscoveryRequest request,
		long refreshVersion,
		CancellationTokenSource countCts)
	{
		long operationId = 0;
		var terminalCompletion = false;
		try
		{
			if (request.Presentation != StatusOperationPresentation.Immediate)
			{
				await Task.Delay(SecretDiscoveryInteractiveDebounce, countCts.Token)
					.ConfigureAwait(false);
			}

			countCts.Token.ThrowIfCancellationRequested();
			if (refreshVersion != Volatile.Read(ref _secretRedactionCountRefreshVersion))
				return;

			operationId = await Dispatcher.UIThread.InvokeAsync(() =>
			{
				if (refreshVersion != Volatile.Read(ref _secretRedactionCountRefreshVersion))
					return 0L;

				_secretRedactionScanState = SecretScanState.Scanning;
				_viewModel.SetContentProcessingStatus(SecretScanState.Scanning);
				RelabelIgnoreOptionsWithCurrentCounts();
				return _statusOperations.Begin(
					_localization["Settings.Ignore.HideSecrets.Scanning"],
					indeterminate: true,
					operationType: StatusOperationType.SecretAnalysis,
					cancelAction: () => countCts.Cancel(),
					presentation: request.Presentation);
			});
			if (operationId == 0)
				return;

			var snapshot = await _secretRedactionPreparer
				.DiscoverAsync(
					request.Context,
					request.Files,
					request.CacheMode,
					countCts.Token)
				.ConfigureAwait(false);
			if (refreshVersion != Volatile.Read(ref _secretRedactionCountRefreshVersion) ||
			    _windowLifetimeCts is not { IsCancellationRequested: false })
			{
				return;
			}

			terminalCompletion = await Dispatcher.UIThread.InvokeAsync(() =>
			{
				if (refreshVersion != Volatile.Read(ref _secretRedactionCountRefreshVersion))
					return false;

				TryApplySecretRedactionSnapshot(snapshot);
				return true;
			});
		}
		catch (OperationCanceledException) when (countCts.IsCancellationRequested)
		{
			if (refreshVersion == Volatile.Read(ref _secretRedactionCountRefreshVersion) &&
			    _windowLifetimeCts is { IsCancellationRequested: false })
			{
				await Dispatcher.UIThread.InvokeAsync(() =>
				{
					if (refreshVersion == Volatile.Read(ref _secretRedactionCountRefreshVersion))
					{
						_secretRedactionScanState = SecretScanState.Pending;
						_viewModel.SetContentProcessingStatus(SecretScanState.Pending);
						RelabelIgnoreOptionsWithCurrentCounts();
					}
				});
			}
		}
		catch (Exception)
		{
			if (refreshVersion != Volatile.Read(ref _secretRedactionCountRefreshVersion) ||
			    _windowLifetimeCts is not { IsCancellationRequested: false })
			{
				return;
			}

			terminalCompletion = await Dispatcher.UIThread.InvokeAsync(() =>
			{
				if (refreshVersion != Volatile.Read(ref _secretRedactionCountRefreshVersion))
					return false;

				_secretRedactionScanState = SecretScanState.Failed;
				_viewModel.SetContentProcessingStatus(SecretScanState.Failed);
				RelabelIgnoreOptionsWithCurrentCounts();
				return true;
			});
		}
		finally
		{
			if (operationId != 0)
				await Dispatcher.UIThread.InvokeAsync(() => _statusOperations.Complete(operationId));
			DisposeIfCurrent(ref _secretRedactionCountCts, countCts);
			if (refreshVersion == Volatile.Read(ref _secretRedactionCountRefreshVersion))
			{
				var removedRequest = Interlocked.CompareExchange(
					ref _activeSecretDiscoveryRequest,
					null,
					request);
				if (terminalCompletion && ReferenceEquals(removedRequest, request))
				{
					ScheduleBackgroundMemoryCleanup(
						MemoryCleanupReason.ApplySettingsWorkCompleted);
				}
			}
		}
	}

	private void BeginOrderedSelectionProjectionBuild(StatusOperationPresentation presentation)
	{
		var selectionVersion = _treeSelectionSnapshotCache.SelectionVersion;
		if (_orderedSelectionProjectionBuildVersion == selectionVersion)
		{
			// A build for this selection is already running; keep the strongest presentation so an
			// explicit request is not downgraded to a silently debounced one.
			if (presentation == StatusOperationPresentation.Immediate)
				_orderedSelectionProjectionPresentation = presentation;
			return;
		}

		_orderedSelectionProjectionBuildVersion = selectionVersion;
		_orderedSelectionProjectionPresentation = presentation;
		var tree = _currentTree!;
		var checkedPaths = _treeSelectionSnapshotCache.GetOrCreate(_viewModel.TreeNodes);
		ObserveDetachedTask(
			BuildOrderedSelectionProjectionAsync(tree, checkedPaths, selectionVersion),
			"BuildOrderedSelectionProjection");
	}

	private async Task BuildOrderedSelectionProjectionAsync(
		BuildTreeResult tree,
		IReadOnlySet<string> checkedPaths,
		long selectionVersion)
	{
		var projection = await Task.Run(() => TreeSelectionSnapshotCache.BuildProjection(
			tree.Root,
			checkedPaths,
			tree.OrderedFilePaths));
		if (_orderedSelectionProjectionBuildVersion == selectionVersion)
			_orderedSelectionProjectionBuildVersion = -1;
		if (_windowLifetimeCts is not { IsCancellationRequested: false } ||
		    !ReferenceEquals(_currentTree, tree) ||
		    selectionVersion != _treeSelectionSnapshotCache.SelectionVersion)
		{
			// A newer selection or tree superseded this build; its own change already scheduled a
			// refresh, so this stale projection is simply dropped.
			return;
		}

		_treeSelectionSnapshotCache.StoreProjection(
			selectionVersion,
			tree.Root,
			projection.NormalizedPaths,
			projection.OrderedFiles);
		ScheduleSecretRedactionCountRefresh(_orderedSelectionProjectionPresentation);
	}

	/// <summary>
	/// While a preview is visible with Hide Secrets enabled, the strict analysis inside the preview
	/// build is the only scan that runs - background discovery deliberately stands down. When that
	/// build fails on anything the scanner would also have failed on, the Hide Secrets row must say
	/// so, or it keeps reporting the scan as still running next to a preview showing an error.
	/// </summary>
	private void HandlePreviewSecretAnalysisFailure(Exception exception)
	{
		if (_windowLifetimeCts is not { IsCancellationRequested: false } ||
		    CreateSecretRedactionContext() is null)
		{
			return;
		}

		if (exception is not (SecretDetectionException
		    or IOException
		    or UnauthorizedAccessException
		    or DecoderFallbackException))
		{
			return;
		}

		_secretRedactionScanState = SecretScanState.Failed;
		_viewModel.SetContentProcessingStatus(SecretScanState.Failed);
		RelabelIgnoreOptionsWithCurrentCounts();
	}

	/// <summary>
	/// The warning indicator on the Hide Secrets row requests one more pass. When a visible preview
	/// owns the scan, rebuilding the preview reruns its strict analysis; otherwise discovery runs
	/// immediately with full content revalidation, so previously failed files are actually reopened.
	/// </summary>
	private void OnSecretScanRetryRequested(object? sender, EventArgs e)
	{
		if (_windowLifetimeCts is not { IsCancellationRequested: false })
			return;

		var hideSecretsEnabled = _selectionCoordinator
			.GetSelectedIgnoreOptionIds()
			.Contains(IgnoreOptionId.HideSecrets);
		if (_viewModel.IsAnyPreviewVisible && hideSecretsEnabled)
		{
			_previewPipeline.ScheduleRefresh(immediate: true);
			return;
		}

		ScheduleSecretRedactionCountRefresh(StatusOperationPresentation.Immediate);
	}

	private void CancelSecretRedactionDiscovery()
	{
		Interlocked.Increment(ref _secretRedactionCountRefreshVersion);
		CancelAndDispose(ref _secretRedactionCountCts);
		Volatile.Write(ref _activeSecretDiscoveryRequest, null);
	}

	private bool IsSecretDiscoveryActiveForCurrentSelection()
	{
		if (_secretRedactionCountCts is not { IsCancellationRequested: false } ||
		    _currentTree is null ||
		    string.IsNullOrWhiteSpace(_currentPath))
		{
			return false;
		}

		var activeRequest = Volatile.Read(ref _activeSecretDiscoveryRequest);
		return activeRequest is not null && activeRequest.HasSameInput(
			_currentPath,
			_currentTree,
			GetOrderedSelectedFilePaths(),
			GetCurrentSecretTransformIdentity(),
			_secretRedactionSession.OutputRevision);
	}

	private static bool SatisfiesCacheMode(
		SecretDiscoveryCacheMode active,
		SecretDiscoveryCacheMode requested) =>
		active == SecretDiscoveryCacheMode.RevalidateContent || active == requested;

	private sealed record SecretDiscoveryRequest(
		string ProjectRoot,
		BuildTreeResult Tree,
		IReadOnlyList<string> Files,
		string TransformIdentity,
		long RedactionRevision,
		ContentTransformationContext Context,
		SecretDiscoveryCacheMode CacheMode,
		StatusOperationPresentation Presentation)
	{
		public bool HasSameInput(SecretDiscoveryRequest other) => HasSameInput(
			other.ProjectRoot,
			other.Tree,
			other.Files,
			other.TransformIdentity,
			other.RedactionRevision);

		public bool HasSameInput(
			string projectRoot,
			BuildTreeResult tree,
			IReadOnlyList<string> files,
			string transformIdentity,
			long redactionRevision) =>
			PathComparer.Default.Equals(ProjectRoot, projectRoot) &&
			ReferenceEquals(Tree, tree) &&
			ReferenceEquals(Files, files) &&
			TransformIdentity.Equals(transformIdentity, StringComparison.Ordinal) &&
			RedactionRevision == redactionRevision;
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
	private bool _manualSecretMarkClosePending;
	private bool _allowCloseAfterManualSecretMarkPersistence;
    private GitCloneWindow? _gitCloneWindow;
    private string? _currentCachedRepoPath;
    private IRepositoryCacheSession? _currentRepositorySession;
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
	private long _compressionSelectionRefreshVersion;
	private int _treeSelectionChangeBatchDepth;
	private bool _treeSelectionChangedDuringBatch;
	private int _suppressTreeSelectionChanges;
	private ContentTransformationContext? _publishedTransformationContext;
	private readonly SecretRedactionOutputPreparer _secretRedactionPreparer;
	private static readonly TimeSpan SecretDiscoveryInteractiveDebounce =
		UiTimingProfile.Scale(TimeSpan.FromMilliseconds(150));
	private CancellationTokenSource? _secretRedactionCountCts;
	private SecretDiscoveryRequest? _activeSecretDiscoveryRequest;
	private long _secretRedactionCountRefreshVersion;
	private long _orderedSelectionProjectionBuildVersion = -1;
	private StatusOperationPresentation _orderedSelectionProjectionPresentation;
	private int? _secretRedactionCount;
	private int? _secretRedactionMatchedCount;
	private SecretScanState _secretRedactionScanState = SecretScanState.Disabled;
	private bool _appliedCompressCodeEnabled;
	private bool _appliedStripCommentsEnabled;
	private bool _appliedStripBlankLinesEnabled;

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
            ScheduleContentTransformationRefresh);
        // Parameter checkboxes edit a draft until Apply publishes a replacement tree. Starting secret
        // discovery here would repeatedly scan the still-active tree and then discard that work. Tree
        // publication is the single invalidation boundary for applied parameter changes.
        _projectLoadPipeline = new ProjectLoadPipeline(this, _statusOperations);
        _projectLoadSnapshotPipeline = new ProjectLoadSnapshotPipeline(this);
        _projectProfiles = new ProjectProfilePersistenceCoordinator(
			_viewModel,
			_selectionCoordinator,
			services.ProjectProfileStore,
			_secretRedactionSession,
			() => _currentPath);
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
			() => _currentPath,
			CreateContentTransformationContext,
			() => ScheduleContentTransformationRefresh(IgnoreOptionId.HideSecrets),
			() =>
			{
				var changed = _selectionCoordinator.ApplyHideSecretsOverride(true);
				_selectionCoordinator.AcceptHideSecretsOverrideAsApplied(_currentPath);
				return changed;
			},
			delta => _projectProfiles.ApplyMarkDeltaAsync(_currentPath, delta),
			cancellationToken => _projectProfiles.PersistIfNeededAsync(_currentPath, cancellationToken));
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
            SettingsPanelAnimationDuration,
            () => new MemoryCleanupRetentionSnapshot(
                _codeCompressionSession.Diagnostics.RetainedCacheBytes,
                _metrics.RetainedReadFactBytes));
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
				var hideSecretsEnabled = _selectionCoordinator
					.GetSelectedIgnoreOptionIds()
					.Contains(IgnoreOptionId.HideSecrets);
				if (_viewModel.IsAnyPreviewVisible && hideSecretsEnabled)
					CancelSecretRedactionDiscovery();
				else
					ScheduleSecretRedactionCountRefresh();
                if (_metrics.HasStatusMetricsSnapshot && _viewModel.StatusMetricsVisible)
                    _metrics.RenderStatusBarMetrics();
                _sessionMetrics.RecordPreviewModeChanged(
                    _viewModel.SelectedPreviewContentMode,
                    _viewModel.IsAnyPreviewVisible);
            }
            else if (args.PropertyName == nameof(MainWindowViewModel.PreviewFontSize))
            {
                Dispatcher.Post(
                    _previewSurfaceController.RefreshStickyPath,
                    DispatcherPriority.Render);
            }
            else if (args.PropertyName == nameof(MainWindowViewModel.SelectedFontFamily))
            {
                RefreshTreeFontMenu();
                Dispatcher.Post(
                    _previewSurfaceController.RefreshStickyPath,
                    DispatcherPriority.Render);
            }
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
			Close,
			ApplyTreeSelectionBatch);

}
