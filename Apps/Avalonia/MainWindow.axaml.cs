using System.Runtime.CompilerServices;
using Avalonia.Platform.Storage;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Avalonia.Services;
using DevProjex.Infrastructure.TerminalCommands;
using AppViewSettings = DevProjex.Infrastructure.ThemePresets.AppViewSettings;

namespace DevProjex.Avalonia;

public partial class MainWindow : Window
{
    private const double BranchMenuItemHeight = 32;
    private const double TreeFontMenuItemHeight = 32;

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

    private void ApplyStartupThemePreset()
        => _appearanceSettings.ApplyStartupPreset();

    private void UpdateCompactModeVisualState()
        => _workspacePresentation.UpdateCompactModeVisualState();

    private void UpdateWorkspaceLayoutForCurrentMode()
        => _workspacePresentation.UpdateWorkspaceLayoutForCurrentMode();

    private void UpdateAdaptiveWorkspaceChrome(bool forcePreviewLabels = false)
        => _workspacePresentation.UpdateAdaptiveWorkspaceChrome(forcePreviewLabels);

    internal static double AlignWindowConstraintToPhysicalPixels(double constraint, double renderScaling)
        => WorkspacePresentationController.AlignWindowConstraintToPhysicalPixels(
            constraint,
            renderScaling);

    private void OnWindowScalingChanged(object? sender, EventArgs e)
        => _workspacePresentation.HandleWindowScalingChanged();

    private void UpdatePreviewToolbarPresentation(bool forceRefreshContent)
        => _workspacePresentation.UpdatePreviewToolbarPresentation(forceRefreshContent);

    private void UpdateToastHostLayout()
        => _workspacePresentation.UpdateToastHostLayout();

    private void CaptureSplitPaneLayout()
        => _workspacePresentation.CaptureSplitPaneLayout();

    private void AdjustSplitPaneWidthsForWindowResize(double widthDelta)
        => _workspacePresentation.AdjustSplitPaneWidthsForWindowResize(widthDelta);

    private void ApplyPreviewTreePaneWidth(double width, bool animate)
        => _workspacePresentation.ApplyPreviewTreePaneWidth(width, animate);

    private void ApplyPreviewPaneWidth(double width, bool animate)
        => _workspacePresentation.ApplyPreviewPaneWidth(width, animate);

    private void EnsurePreviewPaneTransitions()
        => _workspacePresentation.EnsurePreviewPaneTransitions();

    private double ResolvePreviewPaneVisibleWidth()
        => _workspacePresentation.ResolvePreviewPaneVisibleWidth();

    private double ResolveDesiredPreviewTreePaneWidth()
        => _workspacePresentation.ResolveDesiredPreviewTreePaneWidth();

    private double ResolveDesiredPreviewPaneWidth(double desiredTreeWidth)
        => _workspacePresentation.ResolveDesiredPreviewPaneWidth(desiredTreeWidth);

    private double GetClampedPreviewTreePaneWidth(double desiredWidth)
        => _workspacePresentation.GetClampedPreviewTreePaneWidth(desiredWidth);

    private double GetAvailableTreeOnlyWorkspaceWidth()
        => _workspacePresentation.GetAvailableTreeOnlyWorkspaceWidth();

    private void UpdatePreviewSettingsSplitterState()
        => _workspacePresentation.UpdatePreviewSettingsSplitterState();

    private void SetPreviewSettingsSplitterVisibility(bool isVisible)
        => _workspacePresentation.SetPreviewSettingsSplitterVisibility(isVisible);

    private bool ShouldShowPreviewSettingsSplitter()
        => _workspacePresentation.ShouldShowPreviewSettingsSplitter();

    private bool ShouldApplySettingsPanelWidthToVisual()
        => _workspacePresentation.ShouldApplySettingsPanelWidthToVisual();

    private void ClampSettingsPanelWidthToAvailableSpace(bool applyToVisual)
        => _workspacePresentation.ClampSettingsPanelWidthToAvailableSpace(applyToVisual);

    private double GetClampedSettingsPanelWidth(double desiredWidth)
        => _workspacePresentation.GetClampedSettingsPanelWidth(desiredWidth);

    private double GetVisibleSettingsPanelWidth()
        => _workspacePresentation.GetVisibleSettingsPanelWidth();

    // Custom resize handles avoid stale hover artifacts on transparent window surfaces
    // and let us clamp the settings pane independently from the split preview/tree layout.
    private void OnTreePreviewSplitterPointerPressed(object? sender, PointerPressedEventArgs e)
        => _workspacePresentation.OnTreePreviewSplitterPointerPressed(sender, e);

    private void OnPreviewSettingsSplitterPointerPressed(object? sender, PointerPressedEventArgs e)
        => _workspacePresentation.OnPreviewSettingsSplitterPointerPressed(sender, e);

    private void OnWorkspaceSplitterPointerMoved(object? sender, PointerEventArgs e)
        => _workspacePresentation.OnWorkspaceSplitterPointerMoved(sender, e);

    private void OnWorkspaceSplitterPointerReleased(object? sender, PointerReleasedEventArgs e)
        => _workspacePresentation.OnWorkspaceSplitterPointerReleased(sender, e);

    private void OnWorkspaceSplitterPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => _workspacePresentation.OnWorkspaceSplitterPointerCaptureLost(sender, e);

    private void OnWorkspaceSplitterPointerExited(object? sender, PointerEventArgs e)
        => _workspacePresentation.OnWorkspaceSplitterPointerExited(sender, e);

    internal static AppLanguage ResolveStartupLanguage(
        AppLanguage currentLanguage,
        AppLanguage? commandLineLanguage,
        AppLanguage? preferredLanguage)
        => AppearanceSettingsController.ResolveStartupLanguage(
            currentLanguage,
            commandLineLanguage,
            preferredLanguage);

    private void SetLanguageAndPersist(AppLanguage language)
        => _appearanceSettings.SetLanguage(language);

    private void SetLanguageForCurrentSession(AppLanguage language)
        => _appearanceSettings.SetLanguageForCurrentSession(language);

    private void InitializeDefaultFont()
    {
        _viewModel.FontFamilies.Add(FontFamily.Default);
        _viewModel.SelectedFontFamily = FontFamily.Default;
    }

    private void ScheduleOptionalFontCatalogLoad()
    {
        if (_fontCatalogLoadScheduled || _fontCatalogLoaded)
            return;

        _fontCatalogLoadScheduled = true;
        Dispatcher.Post(
            () =>
            {
                if (_windowLifetimeCts is not { IsCancellationRequested: false })
                    return;

                EnsureOptionalFontCatalogLoaded();
            },
            DispatcherPriority.ApplicationIdle);
    }

    private void EnsureOptionalFontCatalogLoaded()
    {
        if (_fontCatalogLoaded)
            return;

        _fontCatalogLoadScheduled = true;
        try
        {
            PopulateOptionalFontCatalog();
        }
        catch (Exception ex)
        {
            // Platform font discovery is optional; keep the default family usable if a
            // native provider fails instead of turning a settings menu into a startup fault.
            Debug.WriteLine($"Optional font catalog discovery failed: {ex.GetType().Name}");
        }
        finally
        {
            // Font discovery is optional. Even if a platform provider fails, the stable
            // FontFamily.Default entry remains usable and discovery is not retried on every menu open.
            _fontCatalogLoaded = true;
        }
    }

    private void PopulateOptionalFontCatalog()
    {
        string[] preferredFontNames =
            ["Consolas", "Courier New", "Fira Code", "Lucida Console", "Cascadia Code", "JetBrains Mono"];

        var systemFonts = FontManager.Current?.SystemFonts;
        var preferredFontSet = new HashSet<string>(preferredFontNames, StringComparer.OrdinalIgnoreCase);
        var availablePreferredFonts = new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);
        var discoveredFonts = new List<FontFamily>();
        if (systemFonts is not null)
        {
            foreach (var font in systemFonts)
            {
                discoveredFonts.Add(font);
                if (preferredFontSet.Contains(font.Name))
                    availablePreferredFonts.TryAdd(font.Name, font);

                if (availablePreferredFonts.Count == preferredFontNames.Length)
                    break;
            }
        }

        foreach (var fontName in preferredFontNames)
        {
            if (availablePreferredFonts.TryGetValue(fontName, out var font))
                _viewModel.FontFamilies.Add(font);
        }

        if (_viewModel.FontFamilies.Count == 1)
        {
            foreach (var font in discoveredFonts
                         .DistinctBy(static font => font.Name, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(static font => font.Name, StringComparer.OrdinalIgnoreCase))
                _viewModel.FontFamilies.Add(font);
        }
    }

    private void SyncThemeWithSystem()
        => _appearanceSettings.SyncThemeWithSystem();

    private void ApplyLocalization()
    {
        _viewModel.UpdateLocalization();
        _settingsPanel?.RequestMinimumWidthRefresh();
        RefreshTreeFontMenu();
        RefreshLanguageMenuChecks();
        UpdatePreviewToolbarPresentation(forceRefreshContent: true);
        _metrics.Recalculate(); // Update metrics text with new localization
        _previewSurfaceController.RefreshSelectionMetricsPresentation();
        if (_viewModel.IsAnyPreviewVisible)
            SchedulePreviewRefresh(immediate: true);
        UpdateTitle();
        UpdateToastHostLayout();

		RelabelIgnoreOptionsWithCurrentCounts();
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


    private void SchedulePreviewRefresh(bool immediate = false)
    {
        _previewPipeline.ScheduleRefresh(immediate);
    }

	private void ScheduleContentTransformationRefresh(IgnoreOptionId? changedOptionId)
	{
		// The regular cache key already tracks selection and presentation changes. Redaction
		// overrides are separate content state, so invalidate only when that state changes;
		// invalidating every preview refresh would rescan all selected text unnecessarily.
		InvalidatePreviewCache();
		PublishTransformationContext();
		if (RequiresCompressionRefresh(changedOptionId))
		{
			// A complete baseline only describes one transformation identity. Mark it incomplete so
			// MetricsPipeline fills the missing raw/compressed variants instead of republishing the
			// previous identity; already measured variants remain cached and are reused on later
			// toggles. The metrics identity contains only compression, so a Hide Secrets toggle
			// would recompute the exact numbers already on screen - it skips this block entirely.
			_metrics.HasCompleteBaseline = false;
			_metrics.ScheduleRecalculate();
			// Redaction consumes compressed text but does not change the compression plan. Restarting
			// Tree-sitter for a Hide Secrets toggle stalls the checkbox without changing its result.
			var compressionEnabled = CreateCodeCompressionContext() is not null;
			_metrics.CancelCompressionPrewarm();
			if (_currentTree is not null)
			{
				ObserveDetachedTask(
					_metrics.PrewarmCompressionAsync(
						_currentTree,
						CancellationToken.None,
						cleanupAfterCompletion:
							MemoryCleanupReason.ApplySettingsWorkCompleted),
					"PrewarmCodeCompression");
			}

			// Compression publishes counts only after it has produced output. Clear a disabled
			// transformation immediately instead of attaching the previous run to an inactive row.
			_codeCompressionSnapshot = compressionEnabled
				? GetCompressionSnapshotForCurrentSelection()
				: null;
		}
		if (!IsAnyContentRedactionEnabled)
		{
			// Redaction scanning is strictly opt-in: with both checkboxes off no discovery may run, not
			// even in the background. Cancel anything in flight and return the row to neutral.
			CancelSecretRedactionDiscovery();
			_secretRedactionMatchedCount = null;
			_secretRedactionCount = null;
			_privateDataRedactionMatchedCount = null;
			_privateDataRedactionCount = null;
			_secretRedactionScanState = SecretScanState.Disabled;
			ApplyRedactionStatus(SecretScanState.Disabled);
			RelabelIgnoreOptionsWithCurrentCounts();
			if (_viewModel.IsAnyPreviewVisible)
				_previewPipeline.ScheduleRefresh(immediate: true);
			return;
		}
		// Start detector-only initialization before Preview and count pipelines read any
		// selected content. This is engine warm-up; it never accesses the project tree.
		_ = _secretRedactionSession.BeginWarmUp(AppliedRedactionFeatures);
		// A canceled option refresh may restore the exact selection that was already scanned.
		// Reuse that snapshot synchronously so rollback also restores the measured label.
		var discoveryActive = IsSecretDiscoveryActiveForCurrentSelection();
		var cachedRedactionSnapshot = GetCachedSecretRedactionSnapshotForCurrentSelection();
		if (cachedRedactionSnapshot is not null)
		{
			_secretRedactionMatchedCount = cachedRedactionSnapshot.SecretDetectedCount;
			_secretRedactionCount = cachedRedactionSnapshot.SecretRedactedCount;
			_privateDataRedactionMatchedCount = cachedRedactionSnapshot.PrivateDataDetectedCount;
			_privateDataRedactionCount = cachedRedactionSnapshot.PrivateDataRedactedCount;
			_secretRedactionScanState = ResolveSecretScanState(cachedRedactionSnapshot);
			ApplyRedactionStatus(
				_secretRedactionScanState,
				cachedRedactionSnapshot.SkippedFileCount,
				cachedRedactionSnapshot.FailedFileCount,
				cachedRedactionSnapshot.UnscannableFiles);
		}
		// A visible preview is already a complete measurement. Keep it until its replacement
		// publishes so a session-to-durable mark transition cannot flash an empty status.
		else if (!_viewModel.IsAnyPreviewVisible || _secretRedactionCount is null)
		{
			_secretRedactionMatchedCount = null;
			_secretRedactionCount = null;
			_privateDataRedactionMatchedCount = null;
			_privateDataRedactionCount = null;
			_secretRedactionScanState = discoveryActive
				? SecretScanState.Scanning
				: SecretScanState.Pending;
			ApplyRedactionStatus(_secretRedactionScanState);
		}
		RelabelIgnoreOptionsWithCurrentCounts();
		if (_viewModel.IsAnyPreviewVisible)
			_previewPipeline.ScheduleRefresh(immediate: true);
		// Applying a redaction change is an explicit request: its scan starts without a debounce and
		// with visible progress. Selection-driven refreshes keep the delayed anti-flash presentation.
		ScheduleSecretRedactionCountRefresh(
			changedOptionId is IgnoreOptionId.HideSecrets or IgnoreOptionId.HidePrivateData
				? StatusOperationPresentation.Immediate
				: StatusOperationPresentation.ExtendedDelay);
	}

	private void ApplyProgrammaticContentTransformationSelectionChange(IgnoreOptionId? changedOptionId)
	{
		if (changedOptionId is IgnoreOptionId.HideSecrets or IgnoreOptionId.HidePrivateData)
		{
			TryApplySelectedContentRedactionState(changedOptionId.Value);
			return;
		}

		ScheduleContentTransformationRefresh(changedOptionId);
	}

	private bool EnsureManualRedactionClassEnabled(ManualRedactionClass classification)
	{
		var optionId = classification switch
		{
			ManualRedactionClass.Secret => IgnoreOptionId.HideSecrets,
			ManualRedactionClass.PrivateData => IgnoreOptionId.HidePrivateData,
			_ => throw new ArgumentOutOfRangeException(nameof(classification), classification, null)
		};
		var wasApplied = optionId == IgnoreOptionId.HideSecrets
			? _appliedHideSecretsEnabled
			: _appliedHidePrivateDataEnabled;
		_selectionCoordinator.ApplyContentRedactionOverrideAsApplied(
			_currentPath,
			optionId,
			enabled: true);
		var isApplied = optionId == IgnoreOptionId.HideSecrets
			? _appliedHideSecretsEnabled
			: _appliedHidePrivateDataEnabled;
		return !wasApplied && isApplied || TryApplySelectedContentRedactionState(optionId);
	}

	private void ApplySelectedContentRedactionStates()
	{
		var selected = _selectionCoordinator.GetSelectedIgnoreOptionIds();
		var hideSecrets = selected.Contains(IgnoreOptionId.HideSecrets);
		var hidePrivateData = selected.Contains(IgnoreOptionId.HidePrivateData);
		if (_appliedHideSecretsEnabled == hideSecrets &&
		    _appliedHidePrivateDataEnabled == hidePrivateData)
		{
			return;
		}

		_appliedHideSecretsEnabled = hideSecrets;
		_appliedHidePrivateDataEnabled = hidePrivateData;
		ScheduleContentTransformationRefresh(IgnoreOptionId.HideSecrets);
	}

	private bool TryApplySelectedContentRedactionState(IgnoreOptionId optionId)
	{
		var selected = _selectionCoordinator
			.GetSelectedIgnoreOptionIds()
			.Contains(optionId);
		var applied = optionId == IgnoreOptionId.HideSecrets
			? _appliedHideSecretsEnabled
			: _appliedHidePrivateDataEnabled;
		if (applied == selected)
			return false;

		if (optionId == IgnoreOptionId.HideSecrets)
			_appliedHideSecretsEnabled = selected;
		else
			_appliedHidePrivateDataEnabled = selected;
		ScheduleContentTransformationRefresh(optionId);
		return true;
	}

	internal static bool RequiresCompressionRefresh(IgnoreOptionId? changedOptionId) =>
		changedOptionId is null or IgnoreOptionId.CompressCode or IgnoreOptionId.StripComments or
			IgnoreOptionId.StripBlankLines;

	private static SecretScanState ResolveSecretScanState(SecretRedactionSnapshot? snapshot) =>
		snapshot switch
		{
			null => SecretScanState.Pending,
			{ HasFailures: true } => SecretScanState.Failed,
			{ HasLimitedCoverage: true } => SecretScanState.Limited,
			{ IsComplete: true } => SecretScanState.Completed,
			_ => SecretScanState.Failed
		};

	private void InvalidateSecretRedactionCount(bool scheduleRefreshImmediately = true)
	{
		CancelSecretRedactionDiscovery();
		_secretRedactionSession.InvalidateSnapshots();
		_secretRedactionScanState = IsAnyContentRedactionEnabled
			? SecretScanState.Pending
			: SecretScanState.Disabled;
		ApplyRedactionStatus(_secretRedactionScanState);
		if (_secretRedactionCount is not null)
			_secretRedactionCount = null;
		_secretRedactionMatchedCount = null;
		_privateDataRedactionCount = null;
		_privateDataRedactionMatchedCount = null;
		RelabelIgnoreOptionsWithCurrentCounts();

		if (scheduleRefreshImmediately)
			ScheduleSecretRedactionCountRefresh();
	}

    private void CancelPreviewRefresh()
    {
        _previewPipeline.CancelRefresh();
    }

    private void OnPreviewTextScrollChanged(
        object? sender,
        ScrollChangedEventArgs e)
        => _previewSurfaceController.HandleTextScrollChanged(sender, e);

    private async void OnPreviewCopyVisibleFilePath(
        object? sender,
        RoutedEventArgs e)
        => await _previewSurfaceController.CopyVisibleFilePathAsync();

    private void OnPreviewStickyHeaderNavigate(
        object? sender,
        RoutedEventArgs e)
        => _previewSurfaceController.ScrollCurrentStickySectionToStart();

    private bool TryGetCurrentPreviewStickySection(
        out PreviewDocumentSection currentSection)
        => _previewSurfaceController.TryGetCurrentStickySection(
            out currentSection);

    private bool TryBuildCurrentPreviewStickySectionCopyPayload(
        out string sectionPayload)
        => _previewSurfaceController
            .TryBuildCurrentStickySectionCopyPayload(
                out sectionPayload);

    private bool TryBuildCurrentPreviewCopyPayload(
        out string previewPayload)
        => _previewSurfaceController.TryBuildCurrentPreviewCopyPayload(
            out previewPayload);

    private void OnPreviewScrollViewerPointerPressed(
        object? sender,
        PointerPressedEventArgs e)
        => _previewSurfaceController.HandleScrollViewerPointerPressed(e);

    private Task<PreviewWarmupSnapshot?> TryBuildPreviewWarmupSnapshotAsync(
        PreviewContentMode mode,
        TreeTextFormat treeFormat,
        bool hasSelection,
        IReadOnlySet<string> selectedPaths,
        string? currentPath,
        TreeNodeDescriptor? currentTreeRoot,
        IReadOnlyList<string>? currentTreeOrderedFilePaths,
        ExportPathPresentation? pathPresentation,
        string noTextContentText,
        string noCheckedFilesText,
        CancellationToken cancellationToken)
        => _previewSurfaceController.TryBuildWarmupSnapshotAsync(
            mode,
            treeFormat,
            hasSelection,
            selectedPaths,
            currentPath,
            currentTreeRoot,
            currentTreeOrderedFilePaths,
            pathPresentation,
            noTextContentText,
            noCheckedFilesText,
            cancellationToken);

    private static bool ShouldBuildPreviewWarmup(
        PreviewContentMode mode,
        bool hasSelection,
        IReadOnlySet<string> selectedPaths,
        TreeNodeDescriptor? treeRoot) =>
        PreviewWarmupPolicy.ShouldBuildPreviewWarmup(mode, hasSelection, selectedPaths, treeRoot);

    private void ApplyPreviewText(string text)
        => _previewSurfaceController.ApplyText(text);

    private void ApplyPreviewText(string text, int lineCount)
        => _previewSurfaceController.ApplyText(text, lineCount);

    private void ApplyPreviewDocument(IPreviewTextDocument document)
    {
        _previewSurfaceController.ApplyDocument(document);
        CompleteDeferredRedactionDecisionPresentation();
    }

    private void ClearPreviewDocument()
    {
        _previewSurfaceController.ClearDocument();
        CompleteDeferredRedactionDecisionPresentation();
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
        => _previewSurfaceController.BuildDocument(
            selectedMode,
            selectedPaths,
            hasSelection,
            treeFormat,
            noCheckedFilesText,
            noTextContentText,
            noDataText,
            currentPath,
            currentTreeRoot,
            currentTreeOrderedFilePaths,
            pathPresentation,
            cancellationToken);

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
        => PreviewFileCollectionPolicy.BuildPreviewCacheKey(
            projectPath,
            treeRoot,
            mode,
            treeFormat,
            selectedPaths);

    private void UpdateTreeVisualResources()
        => _treeViewport.UpdateVisualResources();

    private static int BuildPathSetHash(IReadOnlySet<string> selectedPaths) =>
        PreviewFileCollectionPolicy.BuildPathSetHash(selectedPaths);

    private bool IsCurrentPreviewCacheHit(PreviewCacheKeyData key)
        => _previewSurfaceController.IsCurrentCacheHit(key);

    private void CachePreview(PreviewCacheKeyData key)
        => _previewSurfaceController.Cache(key);

    private void InvalidatePreviewCache()
        => _previewSurfaceController.InvalidateCache();


    private void OnExpandAll(object? sender, RoutedEventArgs e)
        => _treeViewport.ExpandAll();

    private void OnCollapseAll(object? sender, RoutedEventArgs e)
        => _treeViewport.CollapseAll();

    private void OnZoomIn(object? sender, RoutedEventArgs e)
        => _treeViewport.ZoomIn();

    private void OnZoomOut(object? sender, RoutedEventArgs e)
        => _treeViewport.ZoomOut();

    private void OnZoomReset(object? sender, RoutedEventArgs e)
        => _treeViewport.ResetZoom();

    private void OnToggleSettings(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsProjectLoaded) return;
        if (_workspacePresentation.IsSettingsAnimating) return;

        var newVisible = !_viewModel.SettingsVisible;
        _viewModel.SettingsVisible = newVisible;
        ObserveDetachedTask(
            AnimateSettingsPanelAsync(newVisible),
            "AnimateSettingsPanel");
    }

    private void OnTogglePreview(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanTogglePreview)
            return;

        if (_viewModel.IsPreviewMode)
            RequestPreviewClose();
        else
            RequestPreviewOpen();
    }

    private void OnPreviewClose(object? sender, RoutedEventArgs e)
        => RequestPreviewClose();

    private void OnWindowPointerPressedForPreviewNavigation(
        object? sender,
        PointerPressedEventArgs e)
    {
        switch (e.Properties.PointerUpdateKind)
        {
            case PointerUpdateKind.XButton1Pressed:
                e.Handled = true;
                RequestPreviewClose();
                break;
            case PointerUpdateKind.XButton2Pressed:
                e.Handled = true;
                RequestPreviewOpen();
                break;
        }
    }

    private void RequestPreviewOpen()
    {
        if (!_viewModel.CanTogglePreview || _viewModel.IsPreviewMode)
            return;

        ObserveDetachedTask(
            _previewWorkspaceController.OpenAsync(),
            "OpenPreviewMode");
    }

    private void RequestPreviewClose()
    {
        if (!_viewModel.CanTogglePreview || !_viewModel.IsPreviewMode)
            return;

        ObserveDetachedTask(
            _previewWorkspaceController.CloseAsync(),
            "ClosePreviewMode");
    }

    private async void OnPreviewCopyCurrentMode(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsProjectLoaded || !_viewModel.IsAnyPreviewVisible)
            return;

        await _previewSurfaceController.CopyCurrentPreviewAsync();
    }

    private void OnPreviewTreeHide(object? sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanUseProjectWorkspaceActions || !_viewModel.IsPreviewTreeVisible)
            return;

        ObserveDetachedTask(
            _previewWorkspaceController.HideTreePaneAsync(),
            "HidePreviewTreePane");
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
        => await _previewWorkspaceController.SwitchModeAsync(targetMode);

    private void UpdatePreviewSegmentThumbPosition(bool animate)
        => _previewWorkspaceController.UpdatePreviewSegmentThumbPosition(animate);

    private void ClearPreviewMemory()
    {
        InvalidatePreviewCache();
        ClearPreviewDocument();
    }

    private void ScheduleBackgroundMemoryCleanup(MemoryCleanupReason reason)
        => _memoryCleanup.Schedule(reason);

    private void ScheduleBackgroundMemoryCleanup(
        MemoryCleanupReason reason,
        Task visualReadyTask)
        => _memoryCleanup.Schedule(reason, visualReadyTask);

    private void CancelBackgroundMemoryCleanup()
        => _memoryCleanup.CancelBackground();

    private void CancelAllMemoryCleanup()
        => _memoryCleanup.CancelAll();

    private void OnWindowPointerPressedForMemoryCleanup(
        object? sender,
        PointerPressedEventArgs e)
        => CancelAllMemoryCleanup();

    private void SchedulePreviewMemoryCleanup()
        => _memoryCleanup.SchedulePreview(MemoryCleanupReason.PreviewClose);

    private void SchedulePreviewRebuildMemoryCleanup()
        => _memoryCleanup.SchedulePreview(
            MemoryCleanupReason.PreviewRebuildCompleted);
    private async Task WaitForTreeRenderStabilizationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_workspaceGrid is null || _treePaneContainer is null || _settingsContainer is null)
            return;

        var readinessTimeout = UiTimingProfile.Scale(TimeSpan.FromMilliseconds(700));
        var stopwatch = Stopwatch.StartNew();
        var previousWorkspaceWidth = 0.0;
        var previousTreeWidth = 0.0;
        var stableSamples = 0;

        while (stopwatch.Elapsed < readinessTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remaining = readinessTimeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero ||
                !await TryWaitForNextAnimationFrameAsync(
                    remaining,
                    cancellationToken))
            {
                break;
            }

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
            var rootNode = _viewModel.TreeNodes.FirstOrDefault();
            // Stable column widths do not imply that TreeView generated and laid out its first
            // container. Starting the island transition before that first materialization makes
            // template work compete with the animation on the initial project load only.
            var rootContainerReady =
                rootNode is null ||
                _treeView?.TreeContainerFromItem(rootNode) is TreeViewItem
                {
                    Bounds.Width: > 0.5,
                    Bounds.Height: > 0.5
                };

            var widthStable =
                Math.Abs(workspaceWidth - previousWorkspaceWidth) <= 0.5 &&
                Math.Abs(treeWidth - previousTreeWidth) <= 0.5;

            stableSamples = treeOccupiesWorkspace && rootContainerReady && widthStable
                ? stableSamples + 1
                : 0;

            if (stableSamples >= 2)
                break;

            previousWorkspaceWidth = workspaceWidth;
            previousTreeWidth = treeWidth;
        }
    }

    private void ResetPreviewTreePaneVisualState()
        => _previewWorkspaceController.ResetPreviewTreePaneVisualState();

    private double ResolvePreviewTreePaneVisibleWidth()
        => _workspacePresentation.ResolvePreviewTreePaneVisibleWidth();
    private Task AnimateSettingsPanelAsync(bool show)
        => _workspacePresentation.AnimateSettingsPanelAsync(show);

    // Preview workspace starts from the computed minimum width,
    // but the user's regular tree-only settings width should remain restorable.
    private void CaptureNonSplitSettingsPanelWidth()
        => _workspacePresentation.CaptureNonSplitSettingsPanelWidth();

    private void RestoreNonSplitSettingsPanelWidth()
        => _workspacePresentation.RestoreNonSplitSettingsPanelWidth();

    private static async Task WaitForPreviewRenderPassesAsync()
    {
        await YieldUiAsync(DispatcherPriority.Render);
        await YieldUiAsync(DispatcherPriority.Render);
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


    private void FlushPersistedStateOnWindowClose()
    {
        // Give persistence one last synchronous chance before the process exits.
        // This protects against transient IO failures that would otherwise make the UI look correct
        // during the session but leave no durable snapshot for the next launch.
        _appearanceSettings.PersistPendingChanges();

        if (_recentProjectsDb.RecentFolders.Count > 0 ||
            _recentProjectsDb.RecentFolderRemovals.Count > 0 ||
            _recentProjectsDb.RecentRepositories.Count > 0)
        {
            _recentProjectsStore.TryPersist(_recentProjectsDb);
        }

        _projectProfiles.FlushPending();
    }

    private async Task<bool> TryOpenFolderAsync(
        string path,
        bool fromDialog,
        bool recordRecentFolder = true,
        IRepositoryCacheSession? preparedSession = null)
    {
        if (!_viewModel.CanChangeProjectTree)
        {
            preparedSession?.Dispose();
            return false;
        }

        var stopwatch = Stopwatch.StartNew();
        var candidateSession = preparedSession;
        var ownsCandidateSession = candidateSession is not null;
        string normalizedPath;
        try
        {
            normalizedPath = PathUtility.Normalize(path);

            if (candidateSession is null && _repoCacheService.IsInCache(normalizedPath))
            {
                if (_currentRepositorySession is not null &&
                    PathComparer.Default.Equals(
                        _currentRepositorySession.RepositoryPath,
                        normalizedPath))
                {
                    candidateSession = _currentRepositorySession;
                }
                else
                {
                    candidateSession = await _repoCacheService.TryAcquireRepositorySessionByPathAsync(
                        normalizedPath,
                        _windowLifetimeCts?.Token ?? CancellationToken.None);
                    ownsCandidateSession = candidateSession is not null;
                }
            }

            if (candidateSession is not null)
                normalizedPath = PathUtility.Normalize(candidateSession.RepositoryPath);
        }
        catch (OperationCanceledException)
        {
            if (ownsCandidateSession)
                candidateSession?.Dispose();
            _sessionMetrics.RecordProjectLoad(stopwatch.Elapsed, success: false, errorCode: "load-canceled");
            return false;
        }
        catch (RepositoryBranchUnavailableException exception)
        {
            if (ownsCandidateSession)
                candidateSession?.Dispose();
            _sessionMetrics.RecordProjectLoad(stopwatch.Elapsed, success: false, errorCode: "branch-unavailable");
            await ShowErrorAsync(FormatRepositoryBranchUnavailableMessage(exception));
            return false;
        }
        catch
        {
            if (ownsCandidateSession)
                candidateSession?.Dispose();
            _sessionMetrics.RecordProjectLoad(stopwatch.Elapsed, success: false, errorCode: "invalid-path");
            await ShowErrorAsync(_localization.Format("Msg.PathNotFound", path));
            return false;
        }

        // Local folders are usually cheap to probe, but disconnected network mounts and
        // removable media can block enumeration. Keep both filesystem checks off the UI thread.
        var rootAccess = await Task.Run(() =>
        {
            var exists = Directory.Exists(normalizedPath);
            return (
                Exists: exists,
                CanRead: exists && _scanOptions.CanReadRoot(normalizedPath));
        });

        if (!rootAccess.Exists)
        {
            if (ownsCandidateSession)
                candidateSession?.Dispose();
            _sessionMetrics.RecordProjectLoad(stopwatch.Elapsed, success: false, errorCode: "folder-not-found");
            await ShowErrorAsync(_localization.Format("Msg.PathNotFound", path));
            return false;
        }

        if (!rootAccess.CanRead)
        {
            if (ownsCandidateSession)
                candidateSession?.Dispose();
            _sessionMetrics.RecordProjectLoad(stopwatch.Elapsed, success: false, errorCode: "access-denied");
            if (TryElevateAndRestart(normalizedPath))
                return false;

            if (BuildFlags.AllowElevation)
                await ShowErrorAsync(_localization["Msg.AccessDeniedRoot"]);
            return false;
        }

        var projectLoadFinalization = BeginProjectLoadFinalization();
        var previousSourceType = _viewModel.ProjectSourceType;
        var previousBranch = _viewModel.CurrentBranch;
		var previousBranches = _viewModel.GitBranches.ToArray();
        var previousRepositoryUrl = _currentRepositoryUrl;
        var previousProjectDisplayName = _currentProjectDisplayName;
        var candidateBranch = ReferenceEquals(candidateSession, _currentRepositorySession)
            ? previousBranch
            : candidateSession?.Branch ?? string.Empty;
        if (candidateSession is not null)
        {
            _viewModel.ProjectSourceType = candidateSession.ContentKind == RepositoryCacheContentKind.Git
                ? ProjectSourceType.GitClone
                : ProjectSourceType.ZipDownload;
            _viewModel.CurrentBranch = candidateBranch;
            _currentRepositoryUrl = candidateSession.RepositoryUrl;
            _currentProjectDisplayName = RepositoryUrlUtility.GetRepositoryName(
                candidateSession.RepositoryUrl);
        }
        try
        {
            await _projectLoadPipeline.OpenFolderAsync(
                normalizedPath,
                candidateSession is null && (fromDialog || _currentRepositorySession is not null),
                recordRecentFolder);
            if (!PathComparer.Default.Equals(_currentPath, normalizedPath) ||
                !_viewModel.IsProjectLoaded)
            {
                if (ownsCandidateSession)
                    candidateSession?.Dispose();
                _viewModel.ProjectSourceType = previousSourceType;
                _viewModel.CurrentBranch = previousBranch;
				RestoreGitBranches(previousBranches);
                _currentRepositoryUrl = previousRepositoryUrl;
                _currentProjectDisplayName = previousProjectDisplayName;
                _sessionMetrics.RecordProjectLoad(stopwatch.Elapsed, success: false, errorCode: "load-canceled");
                return false;
            }

            var previousSession = _currentRepositorySession;
            if (!ReferenceEquals(previousSession, candidateSession))
            {
                _currentRepositorySession = candidateSession;
                previousSession?.Dispose();
                if (previousSession is not null)
                    _ = Task.Run(_repoCacheService.CollectGarbage);
            }

            if (candidateSession is not null)
            {
                _currentCachedRepoPath = candidateSession.RepositoryPath;
                _currentRepositoryUrl = candidateSession.RepositoryUrl;
                _currentProjectDisplayName = RepositoryUrlUtility.GetRepositoryName(
                    candidateSession.RepositoryUrl);
                _viewModel.ProjectSourceType = candidateSession.ContentKind == RepositoryCacheContentKind.Git
                    ? ProjectSourceType.GitClone
                    : ProjectSourceType.ZipDownload;
                _viewModel.CurrentBranch = candidateBranch;
				_viewModel.GitBranches.Clear();
				if (candidateSession.ContentKind == RepositoryCacheContentKind.Git &&
				    !string.IsNullOrWhiteSpace(candidateBranch))
				{
					_viewModel.GitBranches.Add(
						new GitBranch(candidateBranch, IsActive: true, IsRemote: false));
				}
				UpdateBranchMenu();
				if (candidateSession.ContentKind == RepositoryCacheContentKind.Git)
				{
					ObserveDetachedTask(
						RefreshCachedRepositoryBranchesAfterLoadAsync(normalizedPath),
						"RefreshCachedRepositoryBranchesAfterLoad");
				}
                UpdateTitle();
            }
            else
            {
                _currentCachedRepoPath = null;
            }

            if (_desktopControlServer is not null)
                await _desktopControlServer.UpdateProjectAsync(normalizedPath);
            _sessionMetrics.RecordProjectLoad(stopwatch.Elapsed, success: true);
            return true;
        }
        catch
        {
            if (ownsCandidateSession && !ReferenceEquals(candidateSession, _currentRepositorySession))
                candidateSession?.Dispose();
            if (!ReferenceEquals(candidateSession, _currentRepositorySession))
            {
                _viewModel.ProjectSourceType = previousSourceType;
                _viewModel.CurrentBranch = previousBranch;
				RestoreGitBranches(previousBranches);
                _currentRepositoryUrl = previousRepositoryUrl;
                _currentProjectDisplayName = previousProjectDisplayName;
            }
            _sessionMetrics.RecordProjectLoad(stopwatch.Elapsed, success: false, errorCode: "load-failed");
            throw;
        }
        finally
        {
            projectLoadFinalization.TrySetResult();
        }
    }

    private TaskCompletionSource BeginProjectLoadFinalization()
    {
        // Tree publication happens inside the reload pipeline, before recent-workspace
        // persistence, status finalization and Desktop IPC have completed. The initial island
        // reveal captures this boundary so those operations cannot enqueue work into its frames.
        var finalization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _projectLoadFinalizationTask = finalization.Task;
        return finalization;
    }

    private Task TryApplyStartupSelectionOverridesAsync()
        => _startupInteractions.ApplySelectionOverridesAsync();

    private Task TryApplyStartupUiOptionsAsync()
        => _startupInteractions.ApplyUiOptionsAsync();

    private Task<bool> TryRunStartupUiBenchmarkScriptAsync()
        => _startupInteractions.RunBenchmarkScriptAsync();

    private async Task TryShowAutomaticTerminalCommandPromptAsync(CancellationToken cancellationToken)
    {
        try
        {
            await YieldUiAsync(DispatcherPriority.Background);
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await Task.Run(_terminalCommandSetupService.Probe, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var action = ResolveAutomaticTerminalCommandStartupAction(
                _appearanceSettings.ViewSettings,
                snapshot,
                !string.IsNullOrWhiteSpace(_desktopStartupRequest?.ProjectPath));

            if (action == AutomaticTerminalCommandStartupAction.RepairSilently)
            {
                var repairResult = await Task.Run(
                    _terminalCommandSetupService.InstallOrRepair,
                    cancellationToken);
                if (repairResult.Success &&
                    TerminalCommandPromptPolicy.ShouldOfferAutomaticPrompt(
                        _appearanceSettings.ViewSettings,
                        repairResult.Snapshot,
                        !string.IsNullOrWhiteSpace(_desktopStartupRequest?.ProjectPath)))
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

        var arguments = new[]
        {
            "open",
            path,
            "--new-window",
            "--language",
            AppLanguageUtility.ToCode(_localization.CurrentLanguage),
            "--internal-elevation-attempted"
        };

        bool started = _elevation.TryRelaunchAsAdministrator(arguments);
        if (started)
        {
            Close();
            return true;
        }

        _ = ShowInfoAsync(_localization["Msg.ElevationCanceled"]);
        return false;
    }

    private async Task<bool> ReloadProjectAsync(
        CancellationToken cancellationToken = default,
        bool applyStoredProfile = false,
        bool reuseUnchangedDiscoveryCaches = false,
        bool preserveTreeState = true)
    {
		if (string.IsNullOrEmpty(_currentPath)) return false;
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

		PersistentSecretMarksSnapshot? persistentMarks = null;
		if (applyStoredProfile)
		{
			var profileSnapshot = await LoadProjectProfileWithRetryAsync(
				_currentPath,
				cancellationToken);
			cancellationToken.ThrowIfCancellationRequested();

			if (profileSnapshot is { HasProfile: true, Profile: not null })
			{
				_selectionCoordinator.ApplyProjectProfileSelections(_currentPath, profileSnapshot.Profile);
			}
			else if (profileSnapshot.Status == ProjectProfileLookupStatus.Missing)
			{
				_selectionCoordinator.ResetProjectProfileSelections(_currentPath);
			}

			if (profileSnapshot is
			    {
				    Status: ProjectProfileLookupStatus.Found or ProjectProfileLookupStatus.Missing,
				    PersistentMarks: not null
			    })
			{
				persistentMarks = profileSnapshot.PersistentMarks;
			}
		}

		return await _projectLoadSnapshotPipeline.ReloadAsync(
			_currentPath,
			preserveTreeState,
			persistentMarks,
			cancellationToken);
	}

	private async Task<ProjectProfileLoadSnapshot> LoadProjectProfileWithRetryAsync(
		string projectPath,
		CancellationToken cancellationToken)
	{
		var retryDelay = TimeSpan.FromMilliseconds(100);
		while (true)
		{
			var snapshot = await _projectProfiles
				.LoadSnapshotAsync(projectPath, cancellationToken);
			if (snapshot.Status != ProjectProfileLookupStatus.TemporarilyUnavailable)
				return snapshot;

			await Task.Delay(retryDelay, cancellationToken);
			retryDelay = TimeSpan.FromMilliseconds(Math.Min(retryDelay.TotalMilliseconds * 2, 1000));
		}
	}

    /// <summary>
    /// Clears state from previous project to release memory before loading a new one.
    /// </summary>
    private void ClearPreviousProjectState(
		bool forceCompactingGc = false,
		bool preserveProjectSessions = false)
    {
        _memoryCleanup.CancelPreview();
		CancelSecretRedactionDiscovery();
		ApplyProjectRuntimeState(ProjectRuntimeStateSnapshot.Cleared);
		PublishTransformationContext();
		if (!preserveProjectSessions)
		{
			_secretRedactionSession.Reset();
			_codeCompressionSession.Reset();
			_contentSessionProjectPath = null;
		}
		_selectionCoordinator.ClearAppliedSelectionState();

        // Background metrics become stale as soon as the visible tree is about to change.
        // Cancel them before tearing down the current project state to avoid wasted I/O.
        _metrics.CancelBackgroundCalculation();

        // Clear search state first (holds references to TreeNodeViewModel)
        _searchFilterController.ClearProjectState();
        _interactiveFilterSelectionSnapshot = null;

        // Clear TreeView selection and temporarily disconnect ItemsSource
        // to force Avalonia to release all TreeViewItem containers
        if (_treeView is not null)
        {
            _treeView.SelectedItem = null;
            _treeView.ItemsSource = null;
            _treeView.InvalidateMeasure();
            _treeView.InvalidateArrange();
            _treeView.InvalidateVisual();
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
		_viewModel.SetAppliedContentTransformationState(
			compressCode: false,
			stripComments: false,
			stripBlankLines: false);
		_viewModel.SetCompressionPreparationStatus(isActive: false);
		_viewModel.SetCommentStripPreparationStatus(isActive: false);
		_viewModel.SetBlankLineStripPreparationStatus(isActive: false);
		_viewModel.SetCompressionStatus(null, null, null, null);
		_viewModel.SetCommentStripStatus(null, null);
		_viewModel.SetBlankLineStripStatus(null, null);
        _metrics.HasCompleteBaseline = false;
        _viewModel.StatusMetricsVisible = false;
        _viewModel.StatusTreeStatsText = string.Empty;
        _viewModel.StatusContentStatsText = string.Empty;
        ClearPreviewDocument();
        _viewModel.IsPreviewLoading = false;
        InvalidatePreviewCache();
        _metrics.InvalidateComputedCaches();
		_metrics.ResetStatusMetricsSnapshot();

        // Clear icon cache to release bitmaps
        _iconCache.Clear();

        // The detached graph is known garbage regardless of its size. Project switches publish
        // a loading frame before this call, so reclaim it now instead of retaining generations
        // from every previously opened project.
        _memoryCleanup.RunImmediate(
            compactLargeObjectHeap: forceCompactingGc);
    }

	private void RestoreGitBranches(IReadOnlyList<GitBranch> branches)
	{
		_viewModel.GitBranches.Clear();
		foreach (var branch in branches)
			_viewModel.GitBranches.Add(branch);
		UpdateBranchMenu();
	}

	private async Task RefreshCachedRepositoryBranchesAfterLoadAsync(string projectPath)
	{
		var cancellationToken = _windowLifetimeCts?.Token ?? CancellationToken.None;
		await MetricsCalculationPolicy.WaitForInitialVisualReadyAsync(
			_postLoadVisualReadyTask,
			MetricsCalculationPolicy.InitialVisualReadyTimeout,
			cancellationToken);
		if (PathComparer.Default.Equals(_currentPath, projectPath))
			await RefreshGitBranchesAsync(projectPath, cancellationToken);
	}

	private ProjectRuntimeStateSnapshot CaptureProjectRuntimeState() => new(
		_appliedHideSecretsEnabled,
		_appliedHidePrivateDataEnabled,
		_appliedCompressCodeEnabled,
		_appliedStripCommentsEnabled,
		_appliedStripBlankLinesEnabled,
		_secretRedactionCount,
		_secretRedactionMatchedCount,
		_privateDataRedactionCount,
		_privateDataRedactionMatchedCount,
		_secretRedactionScanState,
		_codeCompressionSnapshot);

	private void ApplyProjectRuntimeState(ProjectRuntimeStateSnapshot state)
	{
		_appliedHideSecretsEnabled = state.HideSecretsApplied;
		_appliedHidePrivateDataEnabled = state.HidePrivateDataApplied;
		_appliedCompressCodeEnabled = state.CompressCodeApplied;
		_appliedStripCommentsEnabled = state.StripCommentsApplied;
		_appliedStripBlankLinesEnabled = state.StripBlankLinesApplied;
		_secretRedactionCount = state.SecretRedactedCount;
		_secretRedactionMatchedCount = state.SecretDetectedCount;
		_privateDataRedactionCount = state.PrivateDataRedactedCount;
		_privateDataRedactionMatchedCount = state.PrivateDataDetectedCount;
		_secretRedactionScanState = state.SecretScanState;
		_codeCompressionSnapshot = state.CompressionSnapshot;
		_viewModel.SetAppliedContentTransformationState(
			state.CompressCodeApplied,
			state.StripCommentsApplied,
			state.StripBlankLinesApplied);
		ApplyRedactionStatus(state.SecretScanState);
		RelabelIgnoreOptionsWithCurrentCounts();
	}

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ResetInteractiveFilterCache() =>
        _refreshPipeline.InvalidateInteractiveFilterCache();

    private Task<TreeRefreshOutcome> RefreshTreeAsync(
        bool interactiveFilter = false,
        CancellationToken cancellationToken = default,
        MemoryCleanupReason? postLoadCleanupReason = null,
        bool preserveStatusMetrics = false) =>
        _refreshPipeline.RefreshTreeAsync(
            interactiveFilter,
            cancellationToken,
            postLoadCleanupReason,
            preserveStatusMetrics);

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

    private void StartPostLoadBackgroundWork(
        BuildTreeResult currentTree,
        CancellationToken cancellationToken,
        MemoryCleanupReason? cleanupAfterCompletion)
    {
        // Preserve the initial-load choreography defined by PostLoadBackgroundWorkSequencer:
        // tree first, settings reveal second, background content work only after visual settle.
        // Do not move any phase above this reveal or detach individual phases from the sequencer.
        var statusPresentation =
            PostLoadBackgroundWorkSequencer.ResolveStatusPresentation(
                _statusOperations.GetActiveSnapshot().OperationType);
        var settingsRevealTask = StartDeferredSettingsPanelAnimationAsync(
            _projectLoadFinalizationTask,
            cancellationToken);
        // Do not fan out the raw reveal task here. All heavy post-load consumers must share the
        // same settle gate or they can wake together on the final animation frame and stall the
        // settings island. Refreshes keep their immediate path because no reveal is pending.
        var postLoadVisualReadyTask = settingsRevealTask.IsCompletedSuccessfully
            ? Task.CompletedTask
            : PostLoadVisualStabilityGate.WaitAsync(
                settingsRevealTask,
                cancellationToken);
        _postLoadVisualReadyTask = postLoadVisualReadyTask;
        ObserveDetachedTask(settingsRevealTask, "AnimateSettingsPanelWhenTreeReady");
#if DEVPROJEX_PROJECT_LOAD_TIMING
        var timing = _projectLoadTiming;
        if (timing is not null && !timing.HasLoadingElapsed)
        {
            timing.LoadingElapsed = timing.LoadingStopwatch.Elapsed;
            timing.HasLoadingElapsed = true;
        }

        Func<CancellationToken, Task> initializeMetricsAsync = token =>
            TrackProjectAnalysisTimingAsync(
                _metrics.InitializeFileMetricsCacheSoonAfterFirstPaintMeasuredAsync(
                    currentTree,
                    Task.CompletedTask,
                    token,
                    statusPresentation),
                timing);
#else
        Func<CancellationToken, Task> initializeMetricsAsync = token =>
            _metrics.InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
                currentTree,
                Task.CompletedTask,
                token,
                statusPresentation);
#endif
        // Project lifecycle operations already committed to visible progress. Their compression
        // and metrics phases must continue that feedback immediately once the reveal gate opens.
        // Interactive option changes keep the delayed presentation to avoid flashing on fast work.
        ObserveDetachedTask(
            RunPostLoadBackgroundWorkAsync(
                postLoadVisualReadyTask,
                currentTree,
                statusPresentation,
                initializeMetricsAsync,
                cleanupAfterCompletion,
                cancellationToken),
            "RunPostLoadBackgroundWork");
    }

    private async Task RunPostLoadBackgroundWorkAsync(
        Task visualReadyTask,
        BuildTreeResult currentTree,
        StatusOperationPresentation statusPresentation,
        Func<CancellationToken, Task> initializeMetricsAsync,
        MemoryCleanupReason? cleanupAfterCompletion,
        CancellationToken cancellationToken)
    {
        try
        {
            await PostLoadBackgroundWorkSequencer.RunAsync(
                visualReadyTask,
                token => _metrics.PrewarmCompressionAsync(
                    currentTree,
                    token,
                    statusPresentation,
                    retainReadFactsForNextMetricsPass: true),
                initializeMetricsAsync,
				() => ScheduleSecretRedactionCountRefresh(statusPresentation),
                cleanupAfterCompletion,
                ScheduleBackgroundMemoryCleanup,
                cancellationToken);
        }
        finally
        {
            _metrics.ReleasePostLoadReadFacts();
        }
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

    private Task StartDeferredSettingsPanelAnimationAsync(
        Task projectLoadFinalizationTask,
        CancellationToken cancellationToken)
    {
        Task settingsRevealTask;
        if (_workspacePresentation.IsSettingsAnimating)
        {
            settingsRevealTask = _workspacePresentation.SettingsAnimationTask;
            return RevealProjectToolsAfterSettingsAsync(settingsRevealTask, cancellationToken);
        }

        // A visible-width island is already on screen (notably during F5). Treating it as a new
        // reveal would delay metrics and make existing status values disappear and jump again.
        if (!SettingsPanelRevealPolicy.ShouldRunInitialReveal(
                _viewModel.SettingsVisible,
                settingsAnimating: false,
                _workspacePresentation.HasVisibleSettingsPanelWidth()))
        {
            return RevealProjectToolsAfterSettingsAsync(Task.CompletedTask, cancellationToken);
        }

        settingsRevealTask = AnimateSettingsPanelWhenTreeReadyAsync(
            projectLoadFinalizationTask,
            cancellationToken);
        return RevealProjectToolsAfterSettingsAsync(settingsRevealTask, cancellationToken);
    }

    private async Task RevealProjectToolsAfterSettingsAsync(
        Task settingsRevealTask,
        CancellationToken cancellationToken)
    {
        await settingsRevealTask.WaitAsync(cancellationToken);
        if (_topMenuBar is not null)
        {
            await _topMenuBar.RevealProjectToolsAsync(
                _viewModel.IsToolAnimationEnabled,
                cancellationToken);
        }
    }

    private async Task AnimateSettingsPanelWhenTreeReadyAsync(
        Task projectLoadFinalizationTask,
        CancellationToken cancellationToken)
    {
        await projectLoadFinalizationTask.WaitAsync(cancellationToken);
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
            !string.IsNullOrWhiteSpace(path));
    }

    private static Task<string?> FindFirstExistingDirectoryAsync(
        IEnumerable<string?> candidatePaths,
        CancellationToken cancellationToken)
    {
        var paths = candidatePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray();

        return Task.Run(
            () =>
            {
                foreach (var path in paths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Directory.Exists(path))
                        return path;
                }

                return null;
            },
            cancellationToken);
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
            return MainWindowViewModel.BaseTitle;

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
			ShowAdvancedCounts = AdvancedIgnoreCountsAlwaysEnabled,
			SecretRedactionsCount = _secretRedactionCount,
			SecretMatchesCount = _secretRedactionMatchedCount,
			PrivateDataRedactionsCount = _privateDataRedactionCount,
			PrivateDataMatchesCount = _privateDataRedactionMatchedCount
		};
    }

    private IgnoreRules BuildIgnoreRules(string rootPath)
    {
        var selected = _selectionCoordinator.GetSelectedIgnoreOptionIds();
        var scanRoots = _selectionCoordinator.GetProjectScanRoots();
        return BuildIgnoreRules(rootPath, selected, scanRoots);
    }

    /// <summary>
    /// Cancels any active background metrics calculation.
    /// Call this before starting user-initiated operations that need the status bar.
    /// </summary>
    private ProjectLoadCancellationSnapshot CaptureProjectLoadCancellationSnapshot()
    {
        var hadLoadedProjectBefore = _viewModel.IsProjectLoaded && !string.IsNullOrWhiteSpace(_currentPath);
		var selectionCheckpoint = _selectionCoordinator.CaptureProjectCheckpoint();
		var treeSelection = hadLoadedProjectBefore
			? ProjectTreeSelectionSnapshot.Capture(
				_currentPath!,
				_viewModel.TreeNodes,
				_treeSelectionSnapshotCache)
			: null;
		var treeExpansion = hadLoadedProjectBefore
			? ProjectTreeUiState.CaptureExpansion(_currentPath!, _viewModel.TreeNodes)
			: null;

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
            AllExtensionsChecked: _viewModel.AllExtensionsChecked,
            AllIgnoreChecked: _viewModel.AllIgnoreChecked,
            HasCompleteMetricsBaseline: _metrics.HasCompleteBaseline,
            Extensions: _viewModel.Extensions
				.Select(static option => new SelectionOptionSnapshot(option.Name, option.IsChecked))
				.ToArray(),
            IgnoreOptions: _viewModel.IgnoreOptions
				.Select(static option => new IgnoreOptionSnapshot(option.Id, option.Label, option.IsChecked))
				.ToArray())
		{
			SelectionCheckpoint = selectionCheckpoint,
			RuntimeState = CaptureProjectRuntimeState(),
			TreeSelection = treeSelection,
			TreeExpansion = treeExpansion,
			SearchQuery = _viewModel.SearchQuery,
			NameFilter = _viewModel.NameFilter,
			PreviewSearchVisible = _viewModel.PreviewSearchVisible,
			PreviewSearchQuery = _viewModel.PreviewSearchQuery
		};
    }

    private bool TryApplyActiveProjectLoadCancellationFallback()
    {
        return _projectLoadCancellation.TryApply(
            ResetToInitialProjectStateAfterCancellation,
            RestorePreviousProjectStateAfterCancellation);
    }

    private void RestorePreviousProjectStateAfterCancellation(ProjectLoadCancellationSnapshot snapshot)
    {
        _topMenuBar?.CompleteProjectToolsReveal();
        _currentPath = snapshot.Path;
        _currentProjectDisplayName = snapshot.ProjectDisplayName;
        _currentRepositoryUrl = snapshot.RepositoryUrl;
        _currentTree = snapshot.Tree;
        _filterBaseTree = snapshot.Tree;
        _currentTreeInventory = null;
        _searchFilterController.ClearProjectState();
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
		_viewModel.SearchQuery = snapshot.SearchQuery;
		_viewModel.NameFilter = snapshot.NameFilter;
		ApplyProjectRuntimeState(snapshot.RuntimeState);

        _viewModel.ProjectSourceType = snapshot.ProjectSourceType;
        _viewModel.CurrentBranch = snapshot.CurrentBranch;
        _viewModel.GitBranches.Clear();
        foreach (var branch in snapshot.GitBranches)
            _viewModel.GitBranches.Add(branch);

		if (snapshot.SelectionCheckpoint is { } selectionCheckpoint)
			_selectionCoordinator.RestoreProjectCheckpoint(selectionCheckpoint);
		else
			RestoreLegacySelectionSnapshot(snapshot);
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
            _viewModel.TreeNodes.Add(rootNode);
			ProjectTreeUiState.RestoreExpansion(rootNode, snapshot.TreeExpansion);
			if (snapshot.TreeSelection is not null)
			{
				ApplyTreeSelectionWithoutPublishing(() => snapshot.TreeSelection.Restore(rootNode));
			}
        }
		_previewSearchController.RestoreProjectState(
			snapshot.PreviewSearchQuery,
			snapshot.PreviewSearchVisible);
		SyncSearchAndFilterVisualStateFromFlags();
		_searchFilterController.ReapplyActiveTreeQueryPresentation();
		_viewModel.IsProjectLoadInProgress = false;
		PublishTransformationContext();
		ScheduleRestoredPreviewRefresh(snapshot.Path);

        UpdateBranchMenu();
        UpdateTitle();
    }

	private void ScheduleRestoredPreviewRefresh(string? restoredProjectPath)
	{
		Dispatcher.UIThread.Post(
			() =>
			{
				if (string.IsNullOrWhiteSpace(restoredProjectPath) ||
				    !PathComparer.Default.Equals(_currentPath, restoredProjectPath) ||
				    !_viewModel.IsAnyPreviewVisible)
				{
					return;
				}

				var refresh = _previewPipeline.RefreshNowAsync(allowDuringModeSwitch: true);
				ObserveDetachedTask(
					refresh.Completion,
					"RestoreProjectPreviewAfterCancellation");
			},
			DispatcherPriority.Background);
	}

	private void RestoreLegacySelectionSnapshot(ProjectLoadCancellationSnapshot snapshot)
	{
		_viewModel.Extensions.Clear();
		foreach (var option in snapshot.Extensions)
			_viewModel.Extensions.Add(new SelectionOptionViewModel(option.Name, option.IsChecked));

		_viewModel.IgnoreOptions.Clear();
		var controllerGroupEndIndex = -1;
		for (var index = snapshot.IgnoreOptions.Count - 1; index >= 0; index--)
		{
			if (snapshot.IgnoreOptions[index].Id is IgnoreOptionId.UseGitIgnore
			    or IgnoreOptionId.TrackedGitFilesOnly
			    or IgnoreOptionId.SmartIgnore)
			{
				controllerGroupEndIndex = index;
				break;
			}
		}

		for (var index = 0; index < snapshot.IgnoreOptions.Count; index++)
		{
			var option = snapshot.IgnoreOptions[index];
			_viewModel.IgnoreOptions.Add(new IgnoreOptionViewModel(
				option.Id,
				option.Label,
				option.IsChecked,
				isControllerGroupEnd: index == controllerGroupEndIndex));
		}

		_viewModel.AllExtensionsChecked = snapshot.AllExtensionsChecked;
		_viewModel.AllIgnoreChecked = snapshot.AllIgnoreChecked;
		_selectionCoordinator.ReevaluatePendingApplyChanges();
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
        _topMenuBar?.CompleteProjectToolsReveal();
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
        _searchFilterController.ClearProjectState();

        _viewModel.IsProjectLoaded = false;
		_viewModel.IsProjectLoadInProgress = false;
        _viewModel.SettingsVisible = false;
        _viewModel.SearchVisible = false;
        _viewModel.FilterVisible = false;
        _viewModel.SetPreviewCompactModeActive(false);
        _viewModel.PreviewWorkspaceMode = PreviewWorkspaceMode.Off;
        _viewModel.StatusMetricsVisible = false;
        _viewModel.ProjectSourceType = ProjectSourceType.LocalFolder;
        _viewModel.CurrentBranch = string.Empty;
        _viewModel.GitBranches.Clear();
        _viewModel.Extensions.Clear();
        _viewModel.IgnoreOptions.Clear();
        _selectionCoordinator.ClearAppliedSelectionState();
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

    private static void OpenExternalLink(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
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

    private IReadOnlySet<string> GetCheckedPaths()
    {
		return _currentTree is null
			? _treeSelectionSnapshotCache.GetOrCreate(_viewModel.TreeNodes)
			: _treeSelectionSnapshotCache.GetOrCreateNormalized(
				_viewModel.TreeNodes,
				_currentTree.Root);
    }

	private IReadOnlyList<string> GetOrderedSelectedFilePaths() =>
		_currentTree is null
			? Array.Empty<string>()
			: _treeSelectionSnapshotCache.GetOrCreateOrderedFiles(
				_viewModel.TreeNodes,
				_currentTree.Root,
				_currentTree.OrderedFilePaths);

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


}
