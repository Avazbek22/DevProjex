using DevProjex.Avalonia.Services;
using DevProjex.Application.Secrets;

namespace DevProjex.Avalonia.Coordinators;

internal sealed record PreviewSurfaceControls(
    ScrollViewer TextScrollViewer,
    VirtualizedPreviewTextControl TextControl,
    VirtualizedLineNumbersControl LineNumbersControl,
    Border StickyHeaderCap,
    Border StickyHeaderContainer,
    TextBlock StickyHeaderText);

internal sealed class PreviewSurfaceController : IDisposable
{
    private const int PreviewWarmupTreeNodeLimit = 192;
    private const int PreviewWarmupCandidateFileLimit = 24;
    private const int PreviewWarmupCandidateNodeVisitLimit = 512;
    private const int PreviewWarmupContentFileLimit = 6;
    private const int PreviewWarmupMaxFileBytes = 64 * 1024;
    private const int PreviewWarmupMaxCharacters = 96 * 1024;
    private static readonly IReadOnlySet<string> EmptySelectedPaths =
        new HashSet<string>(PathComparer.Default);
    private static readonly TimeSpan SelectionMetricsDebounceInterval =
        UiTimingProfile.Scale(TimeSpan.FromMilliseconds(80));

    private readonly Window _window;
    private readonly MainWindowViewModel _viewModel;
    private readonly PreviewSurfaceControls _controls;
    private readonly LocalizationService _localization;
    private readonly IToastService _toastService;
    private readonly PreviewDocumentBuilder _previewDocumentBuilder;
	private readonly SecretRedactionOutputPreparer _secretRedactionPreparer;
    private readonly SelectedContentExportService _contentExport;
    private readonly ProjectTextOutputPipeline _textOutputPipeline;
    private readonly TreeExportService _treeExport;
    private readonly MetricsPipeline _metrics;
    private readonly PreviewWorkspacePipeline _previewPipeline;
    private readonly Func<bool> _ensureClipboardOutputReady;
    private readonly Func<string, Task> _setClipboardTextAsync;
    private readonly Func<string, Task> _showErrorAsync;
	private readonly Func<SecretRedactionContext?> _redactionContextProvider;
	private readonly Action _requestRedactionRefresh;

    private CancellationTokenSource? _selectionMetricsCts;
    private DispatcherTimer? _selectionMetricsDebounceTimer;
    private int _selectionMetricsVersion;
    private ExportOutputMetrics _lastSelectionMetrics =
        ExportOutputMetrics.Empty;
    private bool _hasSelectionMetricsSnapshot;
    private bool _scrollSyncActive;
    private bool _disposed;

    public PreviewSurfaceController(
        Window window,
        MainWindowViewModel viewModel,
        PreviewSurfaceControls controls,
        LocalizationService localization,
        IToastService toastService,
        PreviewDocumentBuilder previewDocumentBuilder,
		SecretRedactionOutputPreparer secretRedactionPreparer,
        SelectedContentExportService contentExport,
        ProjectTextOutputPipeline textOutputPipeline,
        TreeExportService treeExport,
        MetricsPipeline metrics,
        PreviewWorkspacePipeline previewPipeline,
        Func<bool> ensureClipboardOutputReady,
        Func<string, Task> setClipboardTextAsync,
        Func<string, Task> showErrorAsync,
		Func<SecretRedactionContext?> redactionContextProvider,
		Action requestRedactionRefresh)
    {
        _window = window;
        _viewModel = viewModel;
        _controls = controls;
        _localization = localization;
        _toastService = toastService;
        _previewDocumentBuilder = previewDocumentBuilder;
		_secretRedactionPreparer = secretRedactionPreparer;
        _contentExport = contentExport;
        _textOutputPipeline = textOutputPipeline;
        _treeExport = treeExport;
        _metrics = metrics;
        _previewPipeline = previewPipeline;
        _ensureClipboardOutputReady = ensureClipboardOutputReady;
        _setClipboardTextAsync = setClipboardTextAsync;
        _showErrorAsync = showErrorAsync;
		_redactionContextProvider = redactionContextProvider;
		_requestRedactionRefresh = requestRedactionRefresh;

        controls.TextControl.VerticalOffset =
            Math.Max(0, controls.TextScrollViewer.Offset.Y);
        controls.TextControl.ViewportHeight =
            Math.Max(0, controls.TextScrollViewer.Viewport.Height);
        controls.TextControl.ViewportWidth =
            Math.Max(0, controls.TextScrollViewer.Viewport.Width);
        controls.TextControl.CopyingToClipboard += OnCopyingToClipboard;
        controls.TextControl.CopiedToClipboard += OnCopiedToClipboard;
        controls.TextControl.PreviewSelectionChanged +=
            OnSelectionChanged;
		controls.TextControl.RedactionToggleRequested += OnRedactionToggleRequested;
    }

	private void OnRedactionToggleRequested(
		object? sender,
		PreviewRedactionToggleRequestedEventArgs e)
	{
		var context = _redactionContextProvider();
		if (context is null)
			return;

		context.Session.ToggleKeepAsIs(e.OccurrenceId);
		_requestRedactionRefresh();
	}

    public bool HasSelectionMetricsSnapshot =>
        _hasSelectionMetricsSnapshot;

    public void HandleTextScrollChanged(
        object? sender,
        ScrollChangedEventArgs _)
    {
        if (_scrollSyncActive ||
            sender is not ScrollViewer textScrollViewer)
        {
            return;
        }

        _controls.TextControl.HorizontalOffset =
            Math.Max(0, textScrollViewer.Offset.X);
        _controls.TextControl.VerticalOffset =
            Math.Max(0, textScrollViewer.Offset.Y);
        _controls.TextControl.ViewportHeight =
            Math.Max(0, textScrollViewer.Viewport.Height);
        _controls.TextControl.ViewportWidth =
            Math.Max(0, textScrollViewer.Viewport.Width);

        _controls.LineNumbersControl.ExtentHeight =
            Math.Max(0, textScrollViewer.Extent.Height);
        _controls.LineNumbersControl.ViewportHeight =
            Math.Max(0, textScrollViewer.Viewport.Height);

        var targetY = textScrollViewer.Offset.Y;
        if (Math.Abs(
                _controls.LineNumbersControl.VerticalOffset -
                targetY) >= 0.1)
        {
            try
            {
                _scrollSyncActive = true;
                _controls.LineNumbersControl.VerticalOffset = targetY;
            }
            finally
            {
                _scrollSyncActive = false;
            }
        }

        UpdateStickyPath();
    }

    public void HandleToolTipLoaded(object? sender)
    {
        if (sender is not ToolTip toolTip)
            return;

        PopupBackdropConfigurator.TryApply(
            toolTip,
            TopLevel.GetTopLevel(_window),
            _viewModel.ActiveThemeEffect,
            PopupBackdropTransparencyFallback.Transparent);
    }

    public async Task CopyVisibleFilePathAsync()
    {
        if (!_ensureClipboardOutputReady() ||
            !await WaitForClipboardSourceReadyAsync()
                .ConfigureAwait(true) ||
            !TryBuildCurrentStickySectionCopyPayload(out var payload))
        {
            return;
        }

        try
        {
            await _setClipboardTextAsync(payload);
            _toastService.Show(
                _localization["Toast.Copy.Preview"]);
        }
        catch (Exception ex)
        {
            await _showErrorAsync(ex.Message);
        }
    }

    public async Task CopyCurrentPreviewAsync()
    {
        if (!_ensureClipboardOutputReady() ||
            !await WaitForClipboardSourceReadyAsync()
                .ConfigureAwait(true) ||
            !TryBuildCurrentPreviewCopyPayload(out var payload))
        {
            return;
        }

        try
        {
            await _setClipboardTextAsync(payload);
            var toastKey =
                _viewModel.SelectedPreviewContentMode switch
                {
                    PreviewContentMode.Tree => "Toast.Copy.Tree",
                    PreviewContentMode.Content =>
                        "Toast.Copy.Content",
                    _ => "Toast.Copy.TreeAndContent"
                };
            _toastService.Show(_localization[toastKey]);
        }
        catch (Exception ex)
        {
            await _showErrorAsync(ex.Message);
        }
    }

    public void RefreshStickyPath()
        => UpdateStickyPath();

    public void HandleScrollViewerPointerPressed(
        PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_controls.TextScrollViewer)
                .Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is Visual sourceVisual)
        {
            if (sourceVisual is VirtualizedPreviewTextControl ||
                sourceVisual.FindAncestorOfType<
                    VirtualizedPreviewTextControl>() is not null)
            {
                return;
            }

            if (sourceVisual is ScrollBar or Thumb or RepeatButton ||
                sourceVisual.FindAncestorOfType<ScrollBar>() is not null)
            {
                return;
            }
        }

        var viewportPoint =
            e.GetPosition(_controls.TextScrollViewer);
        var handledByPreview =
            _controls.TextControl.TryHandleViewportSelectionStart(
                e.Pointer,
                viewportPoint,
                e.KeyModifiers);

        if (!handledByPreview)
            _controls.TextControl.ClearSelection();

        e.Handled = true;
    }

    public bool TryBuildCurrentPreviewCopyPayload(
        out string previewPayload)
    {
        previewPayload = string.Empty;
        var document =
            _controls.TextControl.Document ??
            _viewModel.PreviewDocument;
        if (document is null)
            return false;

        previewPayload =
            PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(
                document);
        return !string.IsNullOrWhiteSpace(previewPayload);
    }

    public bool TryBuildCurrentStickySectionCopyPayload(
        out string sectionPayload)
    {
        sectionPayload = string.Empty;
        if (!TryGetCurrentStickySection(out var currentSection))
            return false;

        var document =
            _controls.TextControl.Document ??
            _viewModel.PreviewDocument;
        if (document is null)
            return false;

        sectionPayload =
            PreviewClipboardPayloadBuilder.BuildSectionPayload(
                document,
                currentSection);
        return !string.IsNullOrWhiteSpace(sectionPayload);
    }

    public bool TryGetCurrentStickySection(
        out PreviewDocumentSection currentSection)
    {
        currentSection = null!;
        if (!_viewModel.IsAnyPreviewVisible)
            return false;

        var document =
            _controls.TextControl.Document ??
            _viewModel.PreviewDocument;
        if (document?.Sections is not { Count: > 0 } sections)
            return false;

        var verticalOffset = _controls.TextScrollViewer.Offset.Y;
        var topLine =
            _controls.TextControl.GetLineNumberAtVerticalOffset(
                verticalOffset);
        if (topLine < sections[0].StartLine)
            return false;

        currentSection =
            PreviewDocumentSectionLookup.FindContainingSection(
                sections,
                topLine) ??
            PreviewDocumentSectionLookup.FindContainingOrNextSection(
                sections,
                topLine) ??
            sections[^1];
        return true;
    }

    public async Task<PreviewWarmupSnapshot?>
        TryBuildWarmupSnapshotAsync(
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
    {
		// A partial warmup cannot assign the same deterministic secret indexes as the full
		// selection. Skip it instead of briefly presenting an unredacted or inconsistent preview.
		if (_redactionContextProvider() is not null)
			return null;

        if (!PreviewWarmupPolicy.ShouldBuildPreviewWarmup(
                mode,
                hasSelection,
                selectedPaths,
                currentTreeRoot))
        {
            return null;
        }

        return await Task.Run<PreviewWarmupSnapshot?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selectionPlan = PreviewWarmupPolicy.CreateSelectionPlan(
                currentTreeRoot,
                hasSelection ? selectedPaths : EmptySelectedPaths);
            var files = mode == PreviewContentMode.Tree
                ? []
                : PreviewWarmupPolicy.CollectInitialPreviewFiles(
                    selectionPlan,
                    PreviewWarmupCandidateFileLimit,
                    PreviewWarmupCandidateNodeVisitLimit,
                    currentTreeOrderedFilePaths);

            if (mode == PreviewContentMode.Content)
            {
                if (files.Count == 0)
                {
                    var fallbackText = hasSelection
                        ? noCheckedFilesText
                        : noTextContentText;
                    return CreateWarmupSnapshot(fallbackText);
                }

                var contentText = _contentExport.BuildBoundedPreviewAsync(
                        files,
                        PreviewWarmupContentFileLimit,
                        PreviewWarmupMaxFileBytes,
                        PreviewWarmupMaxCharacters,
                        cancellationToken,
                        pathPresentation?.MapFilePath)
                    .GetAwaiter()
                    .GetResult();
                if (string.IsNullOrWhiteSpace(contentText))
                    contentText = noTextContentText;

                return CreateWarmupSnapshot(contentText);
            }

            if (string.IsNullOrWhiteSpace(currentPath) ||
                currentTreeRoot is null)
            {
                return null;
            }

            var projectedTree = PreviewWarmupPolicy.CreateBoundedTreeProjection(
                selectionPlan,
                PreviewWarmupTreeNodeLimit);
            if (projectedTree is null)
                return null;

            var treeText = _textOutputPipeline.BuildTree(
                new ProjectTextOutputSnapshot(
                    currentPath,
                    projectedTree,
                    EmptySelectedPaths,
                    OrderedFilePaths: null,
                    treeFormat,
                    pathPresentation),
                cancellationToken);
            if (string.IsNullOrWhiteSpace(treeText))
                return null;

            if (mode == PreviewContentMode.Tree)
                return CreateWarmupSnapshot(treeText);

            if (mode != PreviewContentMode.TreeAndContent)
                return null;

            if (files.Count == 0)
                return CreateWarmupSnapshot(treeText);

            var combinedContent = _contentExport.BuildBoundedPreviewAsync(
                    files,
                    PreviewWarmupContentFileLimit,
                    PreviewWarmupMaxFileBytes,
                    PreviewWarmupMaxCharacters,
                    cancellationToken,
                    TreeAndContentExportService
                        .CreateRelativeContentHeaderPathMapper(
                            currentPath))
                .GetAwaiter()
                .GetResult();
            if (string.IsNullOrWhiteSpace(combinedContent))
                return CreateWarmupSnapshot(treeText);

            var combinedBuilder =
                new StringBuilder(
                    treeText.Length +
                    combinedContent.Length +
                    16);
            combinedBuilder.Append(treeText.TrimEnd('\r', '\n'));
            combinedBuilder.AppendLine("\u00A0");
            combinedBuilder.AppendLine("\u00A0");
            combinedBuilder.Append(combinedContent);
            return CreateWarmupSnapshot(combinedBuilder.ToString());
        }, cancellationToken).ConfigureAwait(false);
    }

    public PreviewBuildResult BuildDocument(
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
			var redactionContext = _redactionContextProvider();
			if (redactionContext is not null && currentTreeRoot is not null)
			{
				var selectedFiles = ResolvePreviewFiles(
					selectedPaths,
					hasSelection,
					currentTreeRoot,
					currentTreeOrderedFilePaths);
				_secretRedactionPreparer
					.AnalyzeAsync(redactionContext, selectedFiles, cancellationToken)
					.GetAwaiter()
					.GetResult();
			}

            var treePreviewText =
                !string.IsNullOrWhiteSpace(currentPath) &&
                currentTreeRoot is not null
                    ? _textOutputPipeline.BuildTree(
                        new ProjectTextOutputSnapshot(
                            currentPath,
                            currentTreeRoot,
                            selectedPaths,
                            currentTreeOrderedFilePaths,
                            treeFormat,
                            pathPresentation),
                        cancellationToken)
                    : string.Empty;
            var effectiveTreeText =
                string.IsNullOrEmpty(treePreviewText)
                    ? noDataText
                    : treePreviewText;
            return new PreviewBuildResult(
                _previewDocumentBuilder.CreateInMemory(
                    effectiveTreeText));
        }

        var files = ResolvePreviewFiles(
            selectedPaths,
            hasSelection,
            currentTreeRoot,
            currentTreeOrderedFilePaths);

        if (selectedMode == PreviewContentMode.Content)
        {
            if (files.Count == 0)
            {
                var fallbackText = hasSelection
                    ? noCheckedFilesText
                    : noTextContentText;
                return new PreviewBuildResult(
                    _previewDocumentBuilder.CreateInMemory(
                        fallbackText));
            }

            var contentDocument =
                _previewDocumentBuilder.BuildContentDocumentAsync(
                        files,
                        cancellationToken,
						pathPresentation?.MapFilePath,
						redactionContext: _redactionContextProvider())
                    .GetAwaiter()
                    .GetResult();
            return new PreviewBuildResult(
                contentDocument ??
                _previewDocumentBuilder.CreateInMemory(
                    noTextContentText));
        }

        if (string.IsNullOrWhiteSpace(currentPath) ||
            currentTreeRoot is null)
        {
            return new PreviewBuildResult(
                _previewDocumentBuilder.CreateInMemory(
                    noTextContentText));
        }

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

        if (selectedPaths.Count > 0 &&
            string.IsNullOrWhiteSpace(treeText))
        {
            treeText = _treeExport.BuildFullTree(
                currentPath,
                currentTreeRoot,
                treeFormat,
                pathPresentation?.DisplayRootPath,
                pathPresentation?.DisplayRootName);
        }

        if (string.IsNullOrWhiteSpace(treeText))
        {
            return new PreviewBuildResult(
                _previewDocumentBuilder.CreateInMemory(noDataText));
        }

        if (files.Count == 0)
        {
            return new PreviewBuildResult(
                _previewDocumentBuilder.CreateInMemory(treeText));
        }

        var document =
            _previewDocumentBuilder
                .BuildTreeAndContentDocumentAsync(
                    treeText,
                    files,
                    cancellationToken,
                    TreeAndContentExportService
                        .CreateRelativeContentHeaderPathMapper(
							currentPath),
					redactionContext: _redactionContextProvider())
                .GetAwaiter()
                .GetResult();
        return new PreviewBuildResult(document);
    }

    public void ApplyText(string text)
    {
        var effectiveText = string.IsNullOrEmpty(text)
            ? _viewModel.PreviewNoDataText
            : text;
        ApplyText(
            effectiveText,
            PreviewFileCollectionPolicy.CountPreviewLines(
                effectiveText));
    }

    public void ApplyText(string text, int lineCount)
    {
        InvalidateCache();
        ApplyDocument(
            _previewDocumentBuilder.CreateInMemory(text),
            lineCount);
    }

    public void ApplyDocument(IPreviewTextDocument document)
        => ApplyDocument(document, document.LineCount);

    public void ClearDocument()
    {
        ClearSelectionMetrics();
        var previousDocument = _viewModel.PreviewDocument;
        _viewModel.PreviewDocument = null;
        _viewModel.PreviewText = string.Empty;
        _viewModel.PreviewLineCount = 1;
        previousDocument?.Dispose();
        HideStickyPath();
    }

    public bool IsCurrentCacheHit(PreviewCacheKeyData key)
        => _previewPipeline.IsCurrentCacheHit(
            key,
            _viewModel.PreviewDocument);

    public void Cache(PreviewCacheKeyData key)
        => _previewPipeline.CachePreview(key);

    public void InvalidateCache()
        => _previewPipeline.InvalidateCache();

    public void RefreshSelectionMetricsPresentation()
        => RenderSelectionMetrics();

    public void ScheduleSelectionMetricsUpdate(
        bool immediate = false)
    {
        if (!_viewModel.IsAnyPreviewVisible ||
            !_controls.TextControl.TryGetSelectionRange(out _))
        {
            ClearSelectionMetrics();
            return;
        }

        if (immediate)
        {
            _selectionMetricsDebounceTimer?.Stop();
            RecalculateSelectionMetrics();
            return;
        }

        if (_selectionMetricsDebounceTimer is null)
        {
            _selectionMetricsDebounceTimer =
                new DispatcherTimer(
                    DispatcherPriority.Background,
                    _window.Dispatcher)
                {
                    Interval = SelectionMetricsDebounceInterval
                };
            _selectionMetricsDebounceTimer.Tick +=
                OnSelectionMetricsDebounceTick;
        }

        _selectionMetricsDebounceTimer.Stop();
        _selectionMetricsDebounceTimer.Start();
    }

    public void ClearSelectionMetrics()
    {
        _selectionMetricsDebounceTimer?.Stop();
        var previousCts =
            Interlocked.Exchange(ref _selectionMetricsCts, null);
        previousCts?.Cancel();
        previousCts?.Dispose();
        Interlocked.Increment(ref _selectionMetricsVersion);

        _lastSelectionMetrics = ExportOutputMetrics.Empty;
        _hasSelectionMetricsSnapshot = false;
        _viewModel.StatusPreviewSelectionVisible = false;
        _viewModel.StatusPreviewSelectionStatsText = string.Empty;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _controls.TextControl.CopyingToClipboard -=
            OnCopyingToClipboard;
		_controls.TextControl.RedactionToggleRequested -= OnRedactionToggleRequested;
        _controls.TextControl.CopiedToClipboard -=
            OnCopiedToClipboard;
        _controls.TextControl.PreviewSelectionChanged -=
            OnSelectionChanged;

        if (_selectionMetricsDebounceTimer is not null)
        {
            _selectionMetricsDebounceTimer.Stop();
            _selectionMetricsDebounceTimer.Tick -=
                OnSelectionMetricsDebounceTick;
            _selectionMetricsDebounceTimer = null;
        }

        var metricsCts =
            Interlocked.Exchange(ref _selectionMetricsCts, null);
        metricsCts?.Cancel();
        metricsCts?.Dispose();
    }

    private void ApplyDocument(
        IPreviewTextDocument document,
        int lineCount)
    {
        ClearSelectionMetrics();
        var previousDocument = _viewModel.PreviewDocument;
        _viewModel.PreviewDocument = document;
        _viewModel.PreviewText = string.Empty;
        _viewModel.PreviewLineCount = Math.Max(1, lineCount);

        _controls.TextScrollViewer.Offset = default;
        _controls.LineNumbersControl.VerticalOffset = 0;
        _controls.LineNumbersControl.ExtentHeight =
            Math.Max(0, _controls.TextScrollViewer.Extent.Height);
        _controls.LineNumbersControl.ViewportHeight =
            Math.Max(0, _controls.TextScrollViewer.Viewport.Height);
        _controls.TextControl.VerticalOffset = 0;
        _controls.TextControl.ViewportHeight =
            Math.Max(0, _controls.TextScrollViewer.Viewport.Height);
        _controls.TextControl.ViewportWidth =
            Math.Max(0, _controls.TextScrollViewer.Viewport.Width);

        if (!ReferenceEquals(previousDocument, document))
            previousDocument?.Dispose();

        UpdateStickyPath();
        Dispatcher.UIThread.Post(
            UpdateStickyPath,
            DispatcherPriority.Render);
    }

    private IReadOnlyList<string> ResolvePreviewFiles(
        IReadOnlySet<string> selectedPaths,
        bool hasSelection,
        TreeNodeDescriptor? currentTreeRoot,
        IReadOnlyList<string>? currentTreeOrderedFilePaths)
    {
        if (currentTreeRoot is null)
            return [];

        var effectiveSelectedPaths =
            ProjectTreeSelectionProjection.NormalizeSelectedPaths(
                currentTreeRoot,
                selectedPaths);
        if (hasSelection && effectiveSelectedPaths.Count > 0)
        {
            return PreviewFileCollectionPolicy
                .BuildOrderedSelectedFilePaths(
                    effectiveSelectedPaths,
                    currentTreeRoot);
        }

        return currentTreeOrderedFilePaths ??
               _metrics.GetOrBuildAllOrderedFilePaths(
                   currentTreeRoot);
    }

    private static PreviewWarmupSnapshot CreateWarmupSnapshot(
        string text)
        => new(
            text,
            PreviewFileCollectionPolicy.CountPreviewLines(text));

    private void UpdateStickyPath()
    {
        if (!TryGetCurrentStickySection(out var currentSection))
        {
            HideStickyPath();
            return;
        }

        _controls.StickyHeaderText.Text =
            currentSection.DisplayPath;
        _controls.StickyHeaderContainer.IsVisible = true;
        _controls.StickyHeaderCap.IsVisible = true;
        SetStickyHeaderClipHeight(
            ResolveStickyHeaderOverlayHeight());

        _controls.TextControl.StickyHeaderReserved = false;
        _controls.TextControl.StickyHeaderVisible = false;
        _controls.TextControl.StickyHeaderText = string.Empty;
        _controls.LineNumbersControl.StickyHeaderReserved = false;
        _controls.LineNumbersControl.StickyHeaderVisible = false;
    }

    private void HideStickyPath()
    {
        _controls.StickyHeaderText.Text = string.Empty;
        _controls.StickyHeaderContainer.IsVisible = false;
        _controls.StickyHeaderCap.IsVisible = false;
        SetStickyHeaderClipHeight(0);

        _controls.TextControl.StickyHeaderReserved = false;
        _controls.TextControl.StickyHeaderVisible = false;
        _controls.TextControl.StickyHeaderText = string.Empty;
        _controls.LineNumbersControl.StickyHeaderReserved = false;
        _controls.LineNumbersControl.StickyHeaderVisible = false;
    }

    private void SetStickyHeaderClipHeight(double height)
    {
        var normalizedHeight = Math.Max(0, height);
        _controls.TextControl.TopOverlayClipHeight =
            normalizedHeight;
        _controls.LineNumbersControl.TopOverlayClipHeight =
            normalizedHeight;
    }

    private double ResolveStickyHeaderOverlayHeight()
        => Math.Max(
            24.0,
            Math.Ceiling(
                _controls.TextControl.TextFontSize + 12.0));

    private async Task<bool> WaitForClipboardSourceReadyAsync()
    {
        if (!_viewModel.IsAnyPreviewVisible)
            return false;

        if (!_viewModel.IsPreviewLoading)
        {
            return (_controls.TextControl.Document ??
                    _viewModel.PreviewDocument) is not null;
        }

        var timeout = TimeSpan.FromSeconds(10);
        var stopwatch = Stopwatch.StartNew();
        while (_viewModel.IsAnyPreviewVisible &&
               _viewModel.IsPreviewLoading &&
               stopwatch.Elapsed < timeout)
        {
            await DispatcherTaskSchedulerProvider.YieldAsync(
                DispatcherPriority.Background);
            await Task.Delay(15).ConfigureAwait(true);
        }

        return !_viewModel.IsPreviewLoading &&
               (_controls.TextControl.Document ??
                _viewModel.PreviewDocument) is not null;
    }

    private void OnCopyingToClipboard(object? sender, CancelEventArgs e)
        => e.Cancel = !_ensureClipboardOutputReady();

    private void OnCopiedToClipboard(object? sender, EventArgs e)
    {
        if (_viewModel.IsAnyPreviewVisible)
            _toastService.Show(_localization["Toast.Copy.Preview"]);
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
        => ScheduleSelectionMetricsUpdate();

    private void OnSelectionMetricsDebounceTick(
        object? sender,
        EventArgs e)
    {
        _selectionMetricsDebounceTimer?.Stop();
        RecalculateSelectionMetrics();
    }

    private void RecalculateSelectionMetrics()
    {
        if (!TryCaptureSelectionMetricsSnapshot(out var snapshot))
        {
            ClearSelectionMetrics();
            return;
        }

        if (_metrics.TryGetCachedPreviewSelectionMetrics(
                _viewModel.SelectedPreviewContentMode,
                snapshot.Document,
                snapshot.SelectionRange,
                out var cachedMetrics))
        {
            ReplaceSelectionMetricsWithCached(cachedMetrics);
            return;
        }

        var metricsCts = ReplaceSelectionMetricsCancellation();
        var version =
            Interlocked.Increment(ref _selectionMetricsVersion);
        _ = RecalculateSelectionMetricsCoreAsync(
            snapshot,
            metricsCts,
            metricsCts.Token,
            version);
    }

    private void ReplaceSelectionMetricsWithCached(
        ExportOutputMetrics cachedMetrics)
    {
        _selectionMetricsDebounceTimer?.Stop();
        var previousCts =
            Interlocked.Exchange(ref _selectionMetricsCts, null);
        previousCts?.Cancel();
        previousCts?.Dispose();
        Interlocked.Increment(ref _selectionMetricsVersion);
        _lastSelectionMetrics = cachedMetrics;
        _hasSelectionMetricsSnapshot = true;
        RenderSelectionMetrics();
    }

    private CancellationTokenSource
        ReplaceSelectionMetricsCancellation()
    {
        var replacement = new CancellationTokenSource();
        var previous = Interlocked.Exchange(
            ref _selectionMetricsCts,
            replacement);
        previous?.Cancel();
        previous?.Dispose();
        return replacement;
    }

    private async Task RecalculateSelectionMetricsCoreAsync(
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
                    version != Volatile.Read(
                        ref _selectionMetricsVersion))
                {
                    return;
                }

                if (!TryCaptureSelectionMetricsSnapshot(
                        out var currentSnapshot) ||
                    !ReferenceEquals(
                        currentSnapshot.Document,
                        snapshot.Document) ||
                    currentSnapshot.SelectionRange !=
                    snapshot.SelectionRange)
                {
                    return;
                }

                _lastSelectionMetrics = metrics;
                _hasSelectionMetricsSnapshot =
                    metrics != ExportOutputMetrics.Empty;
                RenderSelectionMetrics();
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
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _selectionMetricsCts,
                        null,
                        metricsCts),
                    metricsCts))
            {
                metricsCts.Dispose();
            }
        }
    }

    private bool TryCaptureSelectionMetricsSnapshot(
        out PreviewSelectionMetricsSnapshot snapshot)
    {
        snapshot = default;
        if (!_viewModel.IsAnyPreviewVisible)
            return false;

        var document =
            _controls.TextControl.Document ??
            _viewModel.PreviewDocument;
        if (document is null ||
            !_controls.TextControl.TryGetSelectionRange(
                out var selectionRange))
        {
            return false;
        }

        snapshot =
            new PreviewSelectionMetricsSnapshot(
                document,
                selectionRange);
        return true;
    }

    private void RenderSelectionMetrics()
    {
        if (!_hasSelectionMetricsSnapshot)
        {
            _viewModel.StatusPreviewSelectionVisible = false;
            _viewModel.StatusPreviewSelectionStatsText =
                string.Empty;
            return;
        }

        _viewModel.StatusPreviewSelectionStatsText =
            PreviewSelectionMetricsPolicy.FormatStatusMetricsText(
                _lastSelectionMetrics,
                BuildStatusMetricLabels(),
                useCompactMode: false);
        _viewModel.StatusPreviewSelectionVisible = true;
    }

    private StatusMetricLabels BuildStatusMetricLabels()
    {
        var linesLabel =
            _localization.Format("Status.Metric.Lines", "{0}");
        var charsLabel =
            _localization.Format("Status.Metric.Chars", "{0}");
        var tokensLabel =
            _localization.Format("Status.Metric.Tokens", "{0}");
        return new StatusMetricLabels(
            RemoveMetricPlaceholder(linesLabel),
            RemoveMetricPlaceholder(charsLabel),
            RemoveMetricPlaceholder(tokensLabel));
    }

    private static string RemoveMetricPlaceholder(string value)
        => value.Replace("{0}", string.Empty).Trim();

    private readonly record struct PreviewSelectionMetricsSnapshot(
        IPreviewTextDocument Document,
        PreviewSelectionRange SelectionRange);
}
