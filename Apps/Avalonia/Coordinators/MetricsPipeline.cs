using System.Runtime.CompilerServices;
using System.Security;
using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia.Coordinators;

internal sealed class MetricsPipeline(
    MainWindowViewModel viewModel,
    LocalizationService localization,
    IFileContentAnalyzer fileContentAnalyzer,
    TreeExportService treeExport,
    StatusOperationCoordinator statusOperations,
    Func<BuildTreeResult?> currentTreeProvider,
    Func<string?> currentPathProvider,
    Func<IReadOnlySet<string>> selectedPathsProvider,
    Func<TreeTextFormat> treeFormatProvider,
    Func<ExportPathPresentation?> exportPathPresentationProvider,
    Func<double> boundsWidthProvider,
    Action<MemoryCleanupReason>? scheduleMemoryCleanup = null,
    Func<ContentTransformationContext?>? transformationContextProvider = null) : IDisposable
{
    private readonly record struct TreeMetricsCacheKey(
        int TreeIdentity,
        TreeTextFormat Format,
        int SelectedCount,
        int SelectedHash,
        int PathPresentationIdentity);

    private readonly record struct ContentMetricsCacheKey(
        int TreeIdentity,
        int SelectedCount,
        int SelectedHash,
        int ContentPathPresentationIdentity,
        int TreeAndContentRootPathIdentity,
        string TransformIdentity);

    private readonly record struct ContentMetricsPair(
        ExportOutputMetrics ContentOnly,
        ExportOutputMetrics TreeAndContentContent);

    private readonly record struct MetricsSelectionProjection(
        BuildTreeResult Tree,
        string RootPath,
        IReadOnlySet<string> SelectedPaths,
        bool HasEffectiveSelection,
        int SelectedCount,
        int SelectedHash,
        IReadOnlyList<string>? OrderedFilePaths);

    private readonly record struct FileMetricsData(
        long Size,
        int LineCount,
        int CharCount,
        bool IsEmpty,
        bool IsWhitespaceOnly,
        bool IsEstimated,
        int CrLfPairCount,
        int TrailingNewlineChars,
        int TrailingNewlineLineBreaks);

    private readonly record struct FileMetricsVariant(
        FileMetricsData Metrics,
        bool HasMetrics);

    private readonly record struct FileMetricsScanResult(
        FileMetricsVariant Raw,
        FileMetricsVariant Effective,
        string TransformIdentity,
        bool WasInspected);

    private sealed class FileMetricsCacheEntry(FileMetricsVariant raw)
    {
        private readonly Dictionary<string, FileMetricsVariant> _transformed = new(StringComparer.Ordinal);

        public FileMetricsVariant Raw { get; set; } = raw;

        public void SetTransformed(string identity, FileMetricsVariant metrics) =>
            _transformed[identity] = metrics;

        public bool TryGet(string identity, out FileMetricsVariant metrics)
        {
            if (identity.Length == 0)
            {
                metrics = Raw;
                return true;
            }

            return _transformed.TryGetValue(identity, out metrics);
        }
    }

    private const double CompactStatusMetricsThresholdWidth = 1050;
    private const long MaximumMetricsMaterializationBytes = 10L * 1024 * 1024;

    private static async Task YieldUiAsync(DispatcherPriority priority)
        => await DispatcherTaskSchedulerProvider.YieldAsync(priority);

    private readonly object _metricsLock = new();
    private readonly object _computationCacheLock = new();
    private readonly Dictionary<string, FileMetricsCacheEntry> _fileMetricsCache =
        new(ProjectTreePathIdentity.CanonicalComparer);
    private readonly CodeCompressionPrewarmer _compressionPrewarmer = new(fileContentAnalyzer);

    private CancellationTokenSource? _metricsCalculationCts;
    private CancellationTokenSource? _compressionPrewarmCts;
	private ContentReadFactSnapshot? _postLoadReadFacts;
    private DispatcherTimer? _metricsDebounceTimer;
    private CancellationTokenSource? _recalculateMetricsCts;
    private volatile bool _isBackgroundMetricsActive;
    private int _metricsRecalcVersion;
    private int _metricsCacheGeneration;
    private long _lastStatusTreeLines;
    private long _lastStatusTreeChars;
    private long _lastStatusTreeTokens;
    private long _lastStatusContentLines;
    private long _lastStatusContentChars;
    private long _lastStatusContentTokens;
    private long _lastStatusTreeAndContentContentLines;
    private long _lastStatusTreeAndContentContentChars;
    private long _lastStatusTreeAndContentContentTokens;
    private bool _hasStatusMetricsSnapshot;
	private string? _statusMetricsProjectPath;
    private bool _hasTreeMetricsCache;
    private TreeMetricsCacheKey _treeMetricsCacheKey;
    private ExportOutputMetrics _treeMetricsCacheValue = ExportOutputMetrics.Empty;
    private bool _hasContentMetricsCache;
    private ContentMetricsCacheKey _contentMetricsCacheKey;
    private ContentMetricsPair _contentMetricsCacheValue = new(ExportOutputMetrics.Empty, ExportOutputMetrics.Empty);
    private int _allOrderedFilePathsTreeIdentity;
    private IReadOnlyList<string>? _allOrderedFilePathsCache;
    private bool _metricsCancellationRequestedByUser;
    private volatile bool _hasCompleteMetricsBaseline;
    private int _disposed;

    public bool IsBackgroundActive => _isBackgroundMetricsActive;

	internal bool IsCompressionPrewarmActive =>
		Volatile.Read(ref _compressionPrewarmCts) is not null;

    public bool HasCompleteBaseline
    {
        get => _hasCompleteMetricsBaseline;
        set => _hasCompleteMetricsBaseline = value;
    }

    public bool HasStatusMetricsSnapshot => _hasStatusMetricsSnapshot;

	internal long RetainedReadFactBytes =>
		Volatile.Read(ref _postLoadReadFacts)?.RetainedBytes ?? 0;

    public void CancelCompressionPrewarm()
    {
        _compressionPrewarmCts?.Cancel();
		ReleasePostLoadReadFacts();
    }

	internal void ReleasePostLoadReadFacts() =>
		Interlocked.Exchange(ref _postLoadReadFacts, null);

    public Task PrewarmCompressionAsync(
        BuildTreeResult currentTree,
        CancellationToken cancellationToken,
        StatusOperationPresentation presentation =
            StatusOperationPresentation.ExtendedDelay,
        MemoryCleanupReason? cleanupAfterCompletion = null,
        bool retainReadFactsForNextMetricsPass = false)
    {
		if (Volatile.Read(ref _disposed) != 0)
			return Task.CompletedTask;

		// Only the explicitly sequenced post-load path guarantees a following metrics consumer.
		// Standalone refreshes release their decoded content when prewarm completes.
		ReleasePostLoadReadFacts();
        var compression = transformationContextProvider?.Invoke()?.Compression;
        if (compression is null)
        {
			viewModel.SetCompressionPreparationStatus(isActive: false);
			viewModel.SetCommentStripPreparationStatus(isActive: false);
			viewModel.SetBlankLineStripPreparationStatus(isActive: false);
            ScheduleMemoryCleanup(cleanupAfterCompletion);
            return Task.CompletedTask;
        }

        var selectedPaths = selectedPathsProvider();
        var filePaths = selectedPaths.Count > 0
			? PreviewFileCollectionPolicy.BuildOrderedSelectedFilePathsWithCancellation(
				selectedPaths,
				currentTree.Root,
				ensureExists: false,
				cancellationToken)
			: GetOrBuildAllOrderedFilePathsWithCancellation(currentTree, cancellationToken);
        var prewarmCts = ReplaceCancellationSource(ref _compressionPrewarmCts);
		var compressBodies = (compression.Kinds & CodeTransformKinds.Bodies) != 0;
		var stripComments = (compression.Kinds & CodeTransformKinds.Comments) != 0;
		var stripBlankLines = (compression.Kinds & CodeTransformKinds.BlankLines) != 0;
		viewModel.SetCompressionPreparationStatus(compressBodies);
		viewModel.SetCommentStripPreparationStatus(stripComments);
		viewModel.SetBlankLineStripPreparationStatus(stripBlankLines);
		var progressText = compression.Kinds switch
		{
			CodeTransformKinds.Bodies => localization["Settings.Compression.Status.Scanning"],
			CodeTransformKinds.Comments => localization["Settings.Comments.Status.Scanning"],
			CodeTransformKinds.BlankLines => localization["Settings.BlankLines.Status.Scanning"],
			_ => localization["Settings.ContentTransform.Status.Scanning"]
		};
        var statusOperationId = statusOperations.Begin(
			progressText,
            indeterminate: false,
            operationType: StatusOperationType.CompressionPreparation,
            cancelAction: CancelCompressionPrewarm,
            presentation: presentation);
        var lastReportedPercent = -1;
        var progress = new Progress<CodeCompressionWarmupProgress>(value =>
        {
            if (!statusOperations.IsActive(statusOperationId) || value.TotalFiles <= 0)
                return;

            var percent = (int)(value.ProcessedFiles * 100.0 / value.TotalFiles);
            if (percent <= lastReportedPercent)
                return;

            lastReportedPercent = percent;
            statusOperations.UpdateProgress(percent, operationId: statusOperationId);
        });
        return PrewarmCompressionCoreAsync(
            compression,
            filePaths,
            prewarmCts,
            cancellationToken,
            statusOperationId,
            progress,
            cleanupAfterCompletion,
            retainReadFactsForNextMetricsPass);
    }

    public void ScheduleRecalculate()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        // A parent checkbox can fire hundreds of child change notifications. Keep the
        // debounce timer inside the metrics pipeline so UI code does not own recalc state.
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
    }

    public void Recalculate(
        MemoryCleanupReason? cleanupAfterCompletion = null)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        if (!viewModel.IsProjectLoaded || viewModel.TreeNodes.Count == 0)
        {
            UpdateStatusBarMetrics(0, 0, 0, 0, 0, 0);
            return;
        }

        var recalcCts = ReplaceCancellationSource(ref _recalculateMetricsCts);
        var token = recalcCts.Token;
        var recalcVersion = Interlocked.Increment(ref _metricsRecalcVersion);

        if (viewModel.TreeNodes.FirstOrDefault() is null)
        {
            DisposeIfCurrent(ref _recalculateMetricsCts, recalcCts);
            return;
        }

        var selectedPaths = selectedPathsProvider();
        var hasAnyChecked = selectedPaths.Count > 0;
        var hasCompleteMetricsBaseline = _hasCompleteMetricsBaseline;
        var treeFormat = treeFormatProvider();
        var currentTree = currentTreeProvider();
        var currentPath = currentPathProvider();

        if (!hasCompleteMetricsBaseline)
        {
            _ = RecalculateIncompleteBaselineMetricsAsync(
                recalcCts,
                token,
                recalcVersion,
                hasAnyChecked,
                selectedPaths,
                treeFormat,
                currentTree,
                currentPath,
                cleanupAfterCompletion);
            return;
        }

        if (!MetricsCalculationPolicy.ShouldProceedWithMetricsCalculation(hasAnyChecked, hasCompleteMetricsBaseline))
        {
            UpdateStatusBarMetrics(0, 0, 0, 0, 0, 0);
            DisposeIfCurrent(ref _recalculateMetricsCts, recalcCts);
            ScheduleMemoryCleanup(cleanupAfterCompletion);
            return;
        }

        _ = RecalculateMetricsCoreAsync(
            recalcCts,
            token,
            recalcVersion,
            hasAnyChecked,
            selectedPaths,
            treeFormat,
            currentTree,
            currentPath,
            cleanupAfterCompletion);
    }

    public async Task InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
        BuildTreeResult currentTree,
        CancellationToken cancellationToken,
        StatusOperationPresentation presentation =
            StatusOperationPresentation.ExtendedDelay)
    {
        await InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
            currentTree,
            Task.CompletedTask,
            cancellationToken,
            presentation);
    }

    public async Task InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
        BuildTreeResult currentTree,
        Task initialVisualReadyTask,
        CancellationToken cancellationToken,
        StatusOperationPresentation presentation =
            StatusOperationPresentation.ExtendedDelay)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var cacheGeneration = Volatile.Read(ref _metricsCacheGeneration);
        await WaitForInitialMetricsWarmupSlotAsync(cancellationToken);

        // This task represents visual stability, not merely animation completion. Replacing it
        // with the raw reveal task lets file prewarming compete with the island's final layout and
        // causes visible stalls on large projects. F5 passes a completed task and stays immediate.
        await WaitForInitialVisualReadyAsync(initialVisualReadyTask, cancellationToken);
        if (Volatile.Read(ref _disposed) != 0 ||
            cacheGeneration != Volatile.Read(ref _metricsCacheGeneration))
            return;

        await InitializeFileMetricsCacheAsync(
            currentTree,
            cacheGeneration,
            cancellationToken,
            presentation);
    }

#if DEVPROJEX_PROJECT_LOAD_TIMING
    public async Task<TimeSpan> InitializeFileMetricsCacheSoonAfterFirstPaintMeasuredAsync(
        BuildTreeResult currentTree,
        Task initialVisualReadyTask,
        CancellationToken cancellationToken,
        StatusOperationPresentation presentation =
            StatusOperationPresentation.ExtendedDelay)
    {
        var cacheGeneration = Volatile.Read(ref _metricsCacheGeneration);
        await WaitForInitialMetricsWarmupSlotAsync(cancellationToken);

        // Keep measured builds behaviorally identical to production: visual-settle time is excluded
        // from the metric, and file-system warmup cannot overlap the initial reveal's final frame.
        await WaitForInitialVisualReadyAsync(initialVisualReadyTask, cancellationToken);
        if (cacheGeneration != Volatile.Read(ref _metricsCacheGeneration))
            return TimeSpan.Zero;

        var stopwatch = Stopwatch.StartNew();
        await InitializeFileMetricsCacheAsync(
            currentTree,
            cacheGeneration,
            cancellationToken,
            presentation);
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }
#endif

    private async Task WaitForInitialMetricsWarmupSlotAsync(CancellationToken cancellationToken)
    {
        await YieldUiAsync(DispatcherPriority.Background);
        cancellationToken.ThrowIfCancellationRequested();

        await YieldUiAsync(DispatcherPriority.Render);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public void CancelBackgroundCalculation()
    {
        if (_isBackgroundMetricsActive)
            _hasCompleteMetricsBaseline = false;

        _isBackgroundMetricsActive = false;
        _metricsCalculationCts?.Cancel();
        _recalculateMetricsCts?.Cancel();
        _compressionPrewarmCts?.Cancel();
		ReleasePostLoadReadFacts();
    }

    public void CancelAndDiscardBackgroundCalculation()
    {
        Interlocked.Increment(ref _metricsCacheGeneration);
        _hasCompleteMetricsBaseline = false;
        CancelBackgroundCalculation();
        ClearFileMetricsCache(trimCapacity: true);
    }

    public void CancelByUser()
    {
        _metricsCancellationRequestedByUser = true;
        _hasCompleteMetricsBaseline = false;
        CancelBackgroundCalculation();
        UpdateStatusBarMetrics(0, 0, 0, 0, 0, 0);
        viewModel.StatusMetricsVisible = viewModel.IsProjectLoaded;
    }

    private string _appliedTransformIdentity = string.Empty;

    private string ResolveTransformIdentity()
    {
        var context = transformationContextProvider?.Invoke();
		return context?.Compression is { } compression ? compression.TransformIdentity : string.Empty;
    }

    /// <summary>Switches the effective variant while retaining already measured raw/compressed data.</summary>
    public void SynchronizeTransformIdentity()
    {
        var identity = ResolveTransformIdentity();
        if (string.Equals(_appliedTransformIdentity, identity, StringComparison.Ordinal))
            return;

        _appliedTransformIdentity = identity;
        InvalidateComputedCaches();
    }

    /// <summary>
    /// One compression scope for a whole metrics pass, or null when compression is off.
    ///
    /// Never one per file: a scope carries the coherent operation key and aggregate counters while
    /// borrowing bounded process-lifetime parser workers. It is never Completed here - metrics must
    /// not publish a snapshot, because the files it measured are not necessarily the ordered
    /// selection an output would have produced.
    /// </summary>
	private CodeCompressionScope? BeginTransformationScope(IReadOnlyList<string> filePaths) =>
		transformationContextProvider?.Invoke()?.Compression?.BeginMeasurement();

    /// <summary>
    /// Re-measures one file through the enabled transformations. A file the compressor leaves alone
    /// keeps its original metrics, so unsupported languages cost only the supported-extension check.
    /// </summary>
    private TextFileMetrics MeasureTransformed(
        CodeCompressionScope? scope,
        string projectRoot,
        string filePath,
        string content,
        TextFileMetrics rawMetrics,
		ContentFingerprint? fingerprint,
        CancellationToken cancellationToken)
    {
        if (scope is null || !IsCompressible(filePath))
            return rawMetrics;
        if (content.Length == 0)
            return rawMetrics;

        var relativePath = BuildRelativePath(projectRoot, filePath);
		var plan = fingerprint is { } knownFingerprint
			? scope.ResolvePlan(
				filePath,
				relativePath,
				content,
				knownFingerprint,
				cancellationToken)
			: scope.ResolvePlan(filePath, relativePath, content, cancellationToken);
		if (!plan.HasEdits)
			return rawMetrics;

		return FileContentAnalyzer.ComputeTransformedMetrics(content, plan);
    }

    private static TextFileMetrics ToMetrics(TextFileContent content) =>
        content.IsEstimated
            ? new TextFileMetrics(
                content.SizeBytes,
                content.LineCount,
                content.CharCount,
                content.IsEmpty,
                content.IsWhitespaceOnly,
                IsEstimated: true,
                TrailingNewlineChars: content.TrailingNewlineChars,
                TrailingNewlineLineBreaks: content.TrailingNewlineLineBreaks)
            : FileContentAnalyzer.ComputeMetrics(content.Content, content.SizeBytes);

    private static FileMetricsVariant ToVariant(TextFileMetrics? metrics) =>
        metrics is null
            ? new FileMetricsVariant(default, HasMetrics: false)
            : new FileMetricsVariant(
                new FileMetricsData(
                    metrics.SizeBytes,
                    metrics.LineCount,
                    metrics.CharCount,
                    metrics.IsEmpty,
                    metrics.IsWhitespaceOnly,
                    metrics.IsEstimated,
                    metrics.CrLfPairCount,
                    metrics.TrailingNewlineChars,
                    metrics.TrailingNewlineLineBreaks),
                HasMetrics: true);

	private bool IsCompressible(string filePath) =>
		transformationContextProvider?.Invoke()?.Compression?.IsSupported(filePath) == true;

    private string ResolveTransformationProjectRoot() =>
        transformationContextProvider?.Invoke()?.Compression?.ProjectRoot ?? string.Empty;

    private static string BuildRelativePath(string projectRoot, string fullPath)
    {
        try
        {
            return Path.GetRelativePath(projectRoot, fullPath);
        }
        catch (ArgumentException)
        {
            return fullPath;
        }
    }

    public void ClearFileMetricsCache(bool trimCapacity)
    {
        lock (_metricsLock)
        {
            _fileMetricsCache.Clear();
            if (trimCapacity)
                _fileMetricsCache.TrimExcess();
        }

        InvalidateComputedCaches();
    }

    public void InvalidateComputedCaches()
    {
        lock (_computationCacheLock)
        {
            _hasTreeMetricsCache = false;
            _treeMetricsCacheValue = ExportOutputMetrics.Empty;
            _hasContentMetricsCache = false;
            _contentMetricsCacheValue = new ContentMetricsPair(ExportOutputMetrics.Empty, ExportOutputMetrics.Empty);
            _allOrderedFilePathsCache = null;
            _allOrderedFilePathsTreeIdentity = 0;
        }
    }

    public void UpdateStatusBarMetrics(
        long treeLines, long treeChars, long treeTokens,
        long contentLines, long contentChars, long contentTokens,
        ExportOutputMetrics? treeAndContentContentMetrics = null)
    {
        _lastStatusTreeLines = treeLines;
        _lastStatusTreeChars = treeChars;
        _lastStatusTreeTokens = treeTokens;
        _lastStatusContentLines = contentLines;
        _lastStatusContentChars = contentChars;
        _lastStatusContentTokens = contentTokens;
        var combinedContentMetrics = treeAndContentContentMetrics ?? new ExportOutputMetrics(contentLines, contentChars, contentTokens);
        _lastStatusTreeAndContentContentLines = combinedContentMetrics.Lines;
        _lastStatusTreeAndContentContentChars = combinedContentMetrics.Chars;
        _lastStatusTreeAndContentContentTokens = combinedContentMetrics.Tokens;
		_statusMetricsProjectPath = currentPathProvider();
        _hasStatusMetricsSnapshot = true;
        RenderStatusBarMetrics();
    }

    public void RenderStatusBarMetrics()
    {
        var labels = BuildStatusMetricLabels();
        var useCompactMode = ShouldUseCompactStatusMetrics();
        viewModel.StatusTreeStatsText = PreviewSelectionMetricsPolicy.FormatStatusMetricsText(
            new ExportOutputMetrics(_lastStatusTreeLines, _lastStatusTreeChars, _lastStatusTreeTokens),
            labels,
            useCompactMode);
        var contentMetrics = GetRenderedStatusContentMetrics();
        viewModel.StatusContentStatsText = PreviewSelectionMetricsPolicy.FormatStatusMetricsText(
            contentMetrics,
            labels,
            useCompactMode);
    }

    public bool TryGetCachedPreviewSelectionMetrics(
        PreviewContentMode selectedMode,
        IPreviewTextDocument document,
        PreviewSelectionRange selectionRange,
        out ExportOutputMetrics metrics)
    {
		var currentProjectPath = currentPathProvider();
		var projectMatches = string.IsNullOrWhiteSpace(_statusMetricsProjectPath)
			? string.IsNullOrWhiteSpace(currentProjectPath)
			: !string.IsNullOrWhiteSpace(currentProjectPath) &&
			  PathComparer.Default.Equals(_statusMetricsProjectPath, currentProjectPath);
		var currentDocument = viewModel.PreviewDocument;
		if (!_hasStatusMetricsSnapshot ||
		    !projectMatches ||
		    currentDocument is not null && currentDocument.CharacterCount != document.CharacterCount)
		{
			metrics = ExportOutputMetrics.Empty;
			return false;
		}

        var contentMetrics = selectedMode == PreviewContentMode.TreeAndContent
            ? new ExportOutputMetrics(
                _lastStatusTreeAndContentContentLines,
                _lastStatusTreeAndContentContentChars,
                _lastStatusTreeAndContentContentTokens)
            : new ExportOutputMetrics(_lastStatusContentLines, _lastStatusContentChars, _lastStatusContentTokens);

        return PreviewSelectionMetricsPolicy.TryGetCachedMetrics(
			true,
            selectedMode,
            document,
            selectionRange,
            new ExportOutputMetrics(_lastStatusTreeLines, _lastStatusTreeChars, _lastStatusTreeTokens),
            contentMetrics,
            out metrics);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        CancelAndDispose(ref _metricsCalculationCts);
        CancelAndDispose(ref _recalculateMetricsCts);
        CancelAndDispose(ref _compressionPrewarmCts);
		ReleasePostLoadReadFacts();

        if (_metricsDebounceTimer is not null)
        {
            _metricsDebounceTimer.Stop();
            _metricsDebounceTimer.Tick -= OnMetricsDebounceTimerTick;
            _metricsDebounceTimer = null;
        }
    }

    private async Task PrewarmCompressionCoreAsync(
        CodeCompressionContext compression,
        IReadOnlyList<string> filePaths,
        CancellationTokenSource prewarmCts,
        CancellationToken cancellationToken,
        long statusOperationId,
        IProgress<CodeCompressionWarmupProgress> progress,
        MemoryCleanupReason? cleanupAfterCompletion,
        bool retainReadFactsForNextMetricsPass)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            prewarmCts.Token,
            cancellationToken);
		CodeCompressionWarmupResult? warmupResult = null;
        var completed = false;
        try
        {
            // WarmAsync performs candidate indexing and file-length probes before its first
            // asynchronous parser lease. Run the whole bootstrap on a worker so large or remote
            // projects cannot stall the UI immediately after the settings reveal.
            warmupResult = await Task.Run(
                    () => _compressionPrewarmer.WarmAsync(
                        compression,
                        filePaths,
                        linkedCts.Token,
                        progress),
                    linkedCts.Token)
                .ConfigureAwait(false);
            linkedCts.Token.ThrowIfCancellationRequested();
            completed = true;
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(
                    Volatile.Read(ref _compressionPrewarmCts),
                    prewarmCts))
            {
				Volatile.Write(
					ref _postLoadReadFacts,
					completed && retainReadFactsForNextMetricsPass
						? warmupResult?.ReadFacts
						: null);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var snapshot = compression.Session.Snapshot;
                    var expectedSelectionKey = CodeCompressionSession.BuildSelectionKey(
                        compression.ProjectRoot,
                        filePaths);
                    if (string.Equals(
                            snapshot.SelectionKey,
                            expectedSelectionKey,
							StringComparison.Ordinal) &&
					    snapshot.TransformIdentity.Equals(
						    compression.TransformIdentity,
						    StringComparison.Ordinal))
                    {
						if ((compression.Kinds & CodeTransformKinds.Bodies) != 0)
						{
							viewModel.SetCompressionStatus(
								snapshot.BodyTransformedFiles,
								snapshot.TotalFiles,
								snapshot.SourceCharacters,
								snapshot.TransformedCharacters);
						}
						if ((compression.Kinds & CodeTransformKinds.Comments) != 0)
						{
							viewModel.SetCommentStripStatus(
								snapshot.CommentTransformedFiles,
								snapshot.TotalFiles);
						}
						if ((compression.Kinds & CodeTransformKinds.BlankLines) != 0)
						{
							viewModel.SetBlankLineStripStatus(
								snapshot.BlankLineTransformedFiles,
								snapshot.TotalFiles);
						}
                    }
                    viewModel.SetCompressionPreparationStatus(isActive: false);
					viewModel.SetCommentStripPreparationStatus(isActive: false);
					viewModel.SetBlankLineStripPreparationStatus(isActive: false);
                    statusOperations.Complete(statusOperationId);
                });

                if (completed)
                    ScheduleMemoryCleanup(cleanupAfterCompletion);
            }
            DisposeIfCurrent(ref _compressionPrewarmCts, prewarmCts);
        }
    }

    private void OnMetricsDebounceTimerTick(object? sender, EventArgs e)
    {
        _metricsDebounceTimer?.Stop();
        Recalculate();
    }

    private async Task InitializeFileMetricsCacheAsync(
        BuildTreeResult currentTree,
        int cacheGeneration,
        CancellationToken cancellationToken,
        StatusOperationPresentation presentation)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        using var _ = PerformanceMetrics.Measure("InitializeFileMetricsCacheAsync");

        var metricsCts = ReplaceCancellationSource(ref _metricsCalculationCts);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, metricsCts.Token);

        _metricsCancellationRequestedByUser = false;
        _hasCompleteMetricsBaseline = false;
        _isBackgroundMetricsActive = true;
        var statusOperationId = statusOperations.Begin(
            viewModel.StatusOperationCalculatingData,
            indeterminate: false,
            operationType: StatusOperationType.MetricsCalculation,
            cancelAction: CancelBackgroundCalculation,
            presentation: presentation);
        IReadOnlyList<string> stagedFilePaths = Array.Empty<string>();
        FileMetricsScanResult[] stagedResults = [];
		ContentReadFactSnapshot? readFacts = null;
        try
        {
            if (statusOperations.IsActive(statusOperationId))
                viewModel.StatusProgressValue = 0;

            IReadOnlyList<string> filePaths;
            using (PerformanceMetrics.Measure("CollectMetricsWarmupFilePaths"))
            {
                filePaths = await Task.Run(
					() => GetOrBuildAllOrderedFilePathsWithCancellation(
						currentTree,
						linkedCts.Token),
                    linkedCts.Token);
            }
            linkedCts.Token.ThrowIfCancellationRequested();
            if (cacheGeneration != Volatile.Read(ref _metricsCacheGeneration))
                throw new OperationCanceledException(linkedCts.Token);

            stagedFilePaths = filePaths;
            stagedResults = new FileMetricsScanResult[filePaths.Count];
			var candidateReadFacts = Volatile.Read(ref _postLoadReadFacts);
			var projectRoot = ResolveTransformationProjectRoot();
			if (candidateReadFacts is not null && projectRoot.Length > 0)
			{
				var selection = ContentSelectionSnapshot.CreateWithCancellation(
					projectRoot,
					filePaths,
					linkedCts.Token);
				if (string.Equals(
						candidateReadFacts.Selection.SelectionFingerprint,
						selection.SelectionFingerprint,
						StringComparison.Ordinal))
				{
					readFacts = candidateReadFacts;
				}
				Interlocked.CompareExchange(ref _postLoadReadFacts, null, candidateReadFacts);
			}

            // Recorded against the pass that is about to fill the cache, so the reconciliation on
            // the next recalculation sees its own measurements and does not discard them.
            SynchronizeTransformIdentity();
            ClearFileMetricsCache(trimCapacity: true);

            var totalFiles = filePaths.Count;
            if (totalFiles == 0)
            {
                _isBackgroundMetricsActive = false;
                _hasCompleteMetricsBaseline = true;
                Recalculate();
                viewModel.StatusMetricsVisible = true;
                statusOperations.Complete(statusOperationId);
                return;
            }

            var hadReadFailures = await ScanFileMetricsAsync(
                filePaths,
                stagedResults,
				readFacts,
                linkedCts.Token,
                statusOperationId,
                MetricsCalculationPolicy.GetBaselineWarmupParallelism(Environment.ProcessorCount));
            ThrowIfMetricsRunIsStale(
                metricsCts,
                cacheGeneration,
                linkedCts.Token);

            MergeStagedMetricsIntoCache(
                stagedFilePaths,
                stagedResults,
                cacheGeneration);

            _isBackgroundMetricsActive = false;
            _hasCompleteMetricsBaseline = !hadReadFailures;
            if (statusOperations.IsActive(statusOperationId))
                viewModel.StatusProgressValue = 100;
            if (hadReadFailures)
            {
                await PublishAvailableMetricsWithoutRecoveryAsync(
                    currentTree,
                    linkedCts.Token);
            }
            else
            {
                Recalculate();
            }
            viewModel.StatusMetricsVisible = true;
            statusOperations.Complete(statusOperationId);
        }
        catch (OperationCanceledException)
        {
            if (!IsCurrentMetricsRun(metricsCts, cacheGeneration))
            {
                statusOperations.Complete(statusOperationId);
                return;
            }

            _isBackgroundMetricsActive = false;
            _hasCompleteMetricsBaseline = false;
            MergeStagedMetricsIntoCache(
                stagedFilePaths,
                stagedResults,
                cacheGeneration);
            bool hasCachedMetrics;
            lock (_metricsLock)
            {
                hasCachedMetrics = false;
                foreach (var entry in _fileMetricsCache.Values)
                {
                    if (!entry.TryGet(_appliedTransformIdentity, out var variant) ||
                        !variant.HasMetrics)
                        continue;

                    hasCachedMetrics = true;
                    break;
                }
            }
            if (_metricsCancellationRequestedByUser)
            {
                _metricsCancellationRequestedByUser = false;
                UpdateStatusBarMetrics(0, 0, 0, 0, 0, 0);
                viewModel.StatusMetricsVisible = true;
            }
            else if (hasCachedMetrics)
            {
                Recalculate();
                viewModel.StatusMetricsVisible = true;
            }
            statusOperations.Complete(statusOperationId);
        }
        catch (Exception exception)
        {
            if (!IsCurrentMetricsRun(metricsCts, cacheGeneration))
            {
                statusOperations.Complete(statusOperationId);
                return;
            }

            _isBackgroundMetricsActive = false;
            _hasCompleteMetricsBaseline = false;
            MergeStagedMetricsIntoCache(
                stagedFilePaths,
                stagedResults,
                cacheGeneration);
            Trace.TraceError(
                "File metrics baseline failed: {0}",
                exception);
            await PublishAvailableMetricsWithoutRecoveryAsync(
                currentTree,
                CancellationToken.None);
            viewModel.StatusMetricsVisible = true;
            statusOperations.Complete(statusOperationId);
        }
        finally
        {
			if (readFacts is not null)
				Interlocked.CompareExchange(ref _postLoadReadFacts, null, readFacts);
            DisposeIfCurrent(ref _metricsCalculationCts, metricsCts);
        }
    }

    private void MergeStagedMetricsIntoCache(
        IReadOnlyList<string> filePaths,
        IReadOnlyList<FileMetricsScanResult> results,
        int expectedCacheGeneration)
    {
        if (filePaths.Count == 0 || results.Count == 0)
            return;

        var mergedAny = false;
        lock (_metricsLock)
        {
            if (expectedCacheGeneration !=
                Volatile.Read(ref _metricsCacheGeneration))
            {
                return;
            }

            var count = Math.Min(filePaths.Count, results.Count);
            for (var index = 0; index < count; index++)
            {
                var result = results[index];
                if (!result.WasInspected)
                    continue;

                var filePath = filePaths[index];
                if (!_fileMetricsCache.TryGetValue(filePath, out var entry))
                {
                    entry = new FileMetricsCacheEntry(result.Raw);
                    _fileMetricsCache.Add(filePath, entry);
                }
                else
                {
                    entry.Raw = result.Raw;
                }

                if (result.TransformIdentity.Length > 0)
                    entry.SetTransformed(result.TransformIdentity, result.Effective);
                mergedAny = true;
            }
        }

		if (mergedAny)
			InvalidateContentMetricsCache();
	}

	private void InvalidateContentMetricsCache()
	{
		lock (_computationCacheLock)
		{
			_hasContentMetricsCache = false;
			_contentMetricsCacheValue = new ContentMetricsPair(
				ExportOutputMetrics.Empty,
				ExportOutputMetrics.Empty);
		}
	}

	private async Task<bool> ScanFileMetricsAsync(
        IReadOnlyList<string> filePaths,
        FileMetricsScanResult[] stagedResults,
		ContentReadFactSnapshot? readFacts,
        CancellationToken cancellationToken,
        long statusOperationId,
        int maxDegreeOfParallelism)
    {
        if (filePaths.Count == 0)
            return false;

        var processedCount = 0;
        var lastProgressPercent = 0;
        var hadReadFailures = 0;
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
            CancellationToken = cancellationToken
        };
        using var transformationScope = BeginTransformationScope(filePaths);
        var transformationProjectRoot = ResolveTransformationProjectRoot();
        var transformIdentity = transformationScope is null
            ? string.Empty
            : _appliedTransformIdentity;

        await Parallel.ForAsync(0, filePaths.Count, parallelOptions, async (index, ct) =>
        {
            var filePath = filePaths[index];
            try
            {
                if (fileContentAnalyzer.ClassifyWithoutReading(filePath) ==
                    FileContentClassification.Binary)
                {
                    stagedResults[index] = new FileMetricsScanResult(
                        Raw: new FileMetricsVariant(default, HasMetrics: false),
                        Effective: new FileMetricsVariant(default, HasMetrics: false),
                        TransformIdentity: transformIdentity,
                        WasInspected: true);
                    return;
                }

                TextFileMetrics? rawMetrics;
                TextFileMetrics? effectiveMetrics;
				ContentReadFact? retainedFact = null;
				if (readFacts is not null && readFacts.TryGet(filePath, out var retained))
					retainedFact = retained;
                if (transformationScope is not null && IsCompressible(filePath))
                {
					var fact = retainedFact is { IsMaterializedText: true }
						? retainedFact
						: await fileContentAnalyzer
							.ReadFactAsync(filePath, MaximumMetricsMaterializationBytes, ct)
							.ConfigureAwait(false);
					rawMetrics = fact.RawMetrics;
					effectiveMetrics = rawMetrics is { IsEstimated: false } && fact.IsMaterializedText
						? MeasureTransformed(
							transformationScope,
							transformationProjectRoot,
							filePath,
							fact.Content!,
							rawMetrics,
							fact.Fingerprint,
							ct)
						: rawMetrics;
                }
                else
                {
					if (retainedFact is not null)
					{
						rawMetrics = retainedFact.RawMetrics;
					}
					else
					{
						var result = await fileContentAnalyzer
							.GetClassifiedMetricsAsync(filePath, ct)
							.ConfigureAwait(false);
						rawMetrics = result.IsText ? result.Metrics : null;
					}
                    effectiveMetrics = rawMetrics;
                }

                stagedResults[index] = new FileMetricsScanResult(
                    Raw: ToVariant(rawMetrics),
                    Effective: ToVariant(effectiveMetrics),
                    TransformIdentity: transformIdentity,
                    WasInspected: true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsExpectedMetricsReadFailure(exception))
            {
                Interlocked.Exchange(ref hadReadFailures, 1);
            }
            finally
            {
                var current = Interlocked.Increment(ref processedCount);
                var progressPercent = (int)(current * 100.0 / filePaths.Count);
                var observed = Volatile.Read(ref lastProgressPercent);
                if (progressPercent >= observed + 5 &&
                    Interlocked.CompareExchange(ref lastProgressPercent, progressPercent, observed) == observed)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (_isBackgroundMetricsActive && statusOperations.IsActive(statusOperationId))
                            viewModel.StatusProgressValue = progressPercent;
                    });
                }
            }
        });

        return Volatile.Read(ref hadReadFailures) != 0;
    }

    private static bool IsExpectedMetricsReadFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException;

    private async Task PublishAvailableMetricsWithoutRecoveryAsync(
        BuildTreeResult currentTree,
        CancellationToken cancellationToken)
    {
        var currentPath = currentPathProvider();
        if (string.IsNullOrWhiteSpace(currentPath))
            return;

        var selectedPaths = selectedPathsProvider();
        var recalcVersion = Interlocked.Increment(ref _metricsRecalcVersion);
        try
        {
            await PublishMetricsAsync(
                cancellationToken,
                recalcVersion,
                selectedPaths.Count > 0,
                selectedPaths,
                treeFormatProvider(),
                currentTree,
                currentPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "Publishing incomplete file metrics failed: {0}",
                exception);
        }
    }

	public void ResetStatusMetricsSnapshot()
	{
		_hasStatusMetricsSnapshot = false;
		_lastStatusTreeLines = 0;
		_lastStatusTreeChars = 0;
		_lastStatusTreeTokens = 0;
		_lastStatusContentLines = 0;
		_lastStatusContentChars = 0;
		_lastStatusContentTokens = 0;
		_lastStatusTreeAndContentContentLines = 0;
		_lastStatusTreeAndContentContentChars = 0;
		_lastStatusTreeAndContentContentTokens = 0;
		_statusMetricsProjectPath = null;
	}

    private static Task WaitForInitialVisualReadyAsync(
        Task initialVisualReadyTask,
        CancellationToken cancellationToken) =>
        MetricsCalculationPolicy.WaitForInitialVisualReadyAsync(
            initialVisualReadyTask,
            // This is a safety deadline, not animation pacing. Scaling it for fast UI tests can
            // expire the gate under runner load and start file IO while the reveal is still active.
            MetricsCalculationPolicy.InitialVisualReadyTimeout,
            cancellationToken);

    private async Task RecalculateIncompleteBaselineMetricsAsync(
        CancellationTokenSource recalcCts,
        CancellationToken token,
        int recalcVersion,
        bool hasAnyChecked,
        IReadOnlySet<string> selectedPaths,
        TreeTextFormat treeFormat,
        BuildTreeResult? currentTree,
        string? currentPath,
        MemoryCleanupReason? cleanupAfterCompletion)
    {
        var completed = false;
        try
        {
            if (token.IsCancellationRequested)
                return;

            if (currentTree is null || string.IsNullOrWhiteSpace(currentPath))
            {
                await Dispatcher.UIThread.InvokeAsync(() => UpdateStatusBarMetrics(0, 0, 0, 0, 0, 0));
                return;
            }

            // Before the missing-file sweep, never after: dropping measurements taken under a
            // different transformation is only safe while there is still a pass left to refill
            // them. Doing it while reading the cache would publish the empty result instead.
            SynchronizeTransformIdentity();

            var selection = await Task.Run(
                () => BuildMetricsSelectionProjection(
                    currentTree,
                    currentPath,
                    hasAnyChecked,
                    selectedPaths,
                    includeOrderedFilePaths: true,
                    token),
                token);
            var targetFilePaths = selection.OrderedFilePaths!;

            if (targetFilePaths.Count == 0)
            {
                await PublishTreeMetricsWhileContentPendingAsync(
                    token,
                    recalcVersion,
                    treeFormat,
                    selection);
                completed = true;
                return;
            }

            var missingPaths = await Task.Run(
	            () => CollectMissingMetricsFilePaths(targetFilePaths, token),
	            token);
            if (missingPaths.Count > 0)
            {
                if (_isBackgroundMetricsActive)
                {
                    _metricsCalculationCts?.Cancel();
                    await WaitForBackgroundMetricsIdleAsync(token);
                    if (token.IsCancellationRequested)
                        return;

					missingPaths = await Task.Run(
						() => CollectMissingMetricsFilePaths(targetFilePaths, token),
						token);
                }

                if (missingPaths.Count > 0)
                {
                    await PublishTreeMetricsWhileContentPendingAsync(
                        token,
                        recalcVersion,
                        treeFormat,
                        selection);

                    var hadReadFailures = await EnsureSelectedFileMetricsAsync(missingPaths, token);
                    if (!hasAnyChecked &&
                        !hadReadFailures &&
						CollectMissingMetricsFilePaths(targetFilePaths, token).Count == 0)
                    {
                        _hasCompleteMetricsBaseline = true;
                    }
                }
            }

            await PublishMetricsAsync(
                token,
                recalcVersion,
                hasAnyChecked,
                selectedPaths,
                treeFormat,
                currentTree,
                currentPath,
                selection);
            completed = true;
        }
        catch (OperationCanceledException)
        {
            // Expected when a newer selection supersedes the current one.
        }
        catch (Exception exception)
        {
            _hasCompleteMetricsBaseline = false;
            Trace.TraceError(
                "File metrics recovery failed: {0}",
                exception);
        }
        finally
        {
            if (completed &&
                recalcVersion == Volatile.Read(ref _metricsRecalcVersion))
            {
                ScheduleMemoryCleanup(cleanupAfterCompletion);
            }

            DisposeIfCurrent(ref _recalculateMetricsCts, recalcCts);
        }
    }

    private async Task WaitForBackgroundMetricsIdleAsync(CancellationToken cancellationToken)
    {
        while (_isBackgroundMetricsActive)
            await Task.Delay(10, cancellationToken);
    }

    private async Task PublishTreeMetricsWhileContentPendingAsync(
        CancellationToken token,
        int recalcVersion,
        TreeTextFormat treeFormat,
        MetricsSelectionProjection selection)
    {
        if (token.IsCancellationRequested)
            return;

		var treeMetrics = await Task.Run(
			() => CalculateTreeMetrics(selection, treeFormat, token),
			token);

        if (token.IsCancellationRequested)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (token.IsCancellationRequested || recalcVersion != Volatile.Read(ref _metricsRecalcVersion))
                return;

            if (!string.Equals(currentPathProvider(), selection.RootPath, StringComparison.Ordinal) ||
                !ReferenceEquals(currentTreeProvider(), selection.Tree))
            {
                return;
            }

            UpdateStatusBarMetrics(treeMetrics.Lines, treeMetrics.Chars, treeMetrics.Tokens, 0, 0, 0);
            viewModel.StatusMetricsVisible = true;
        });
    }

    private async Task RecalculateMetricsCoreAsync(
        CancellationTokenSource recalcCts,
        CancellationToken token,
        int recalcVersion,
        bool hasAnyChecked,
        IReadOnlySet<string> selectedPaths,
        TreeTextFormat treeFormat,
        BuildTreeResult? currentTree,
        string? currentPath,
        MemoryCleanupReason? cleanupAfterCompletion)
    {
        var completed = false;
        try
        {
            await PublishMetricsAsync(
                token,
                recalcVersion,
                hasAnyChecked,
                selectedPaths,
                treeFormat,
                currentTree,
                currentPath);
            completed = true;
        }
        catch (OperationCanceledException)
        {
            // Expected when a newer recalculation supersedes the current one.
        }
        finally
        {
            if (completed &&
                recalcVersion == Volatile.Read(ref _metricsRecalcVersion))
            {
                ScheduleMemoryCleanup(cleanupAfterCompletion);
            }

            DisposeIfCurrent(ref _recalculateMetricsCts, recalcCts);
        }
    }

    private void ScheduleMemoryCleanup(
        MemoryCleanupReason? cleanupReason)
    {
        if (cleanupReason is { } reason)
            scheduleMemoryCleanup?.Invoke(reason);
    }

    private async Task PublishMetricsAsync(
        CancellationToken token,
        int recalcVersion,
        bool hasAnyChecked,
        IReadOnlySet<string> selectedPaths,
        TreeTextFormat treeFormat,
        BuildTreeResult? currentTree,
        string? currentPath,
        MetricsSelectionProjection? preparedSelection = null)
    {
        if (token.IsCancellationRequested)
            return;

        if (currentTree is null || string.IsNullOrWhiteSpace(currentPath))
        {
            await Dispatcher.UIThread.InvokeAsync(
                () => UpdateStatusBarMetrics(0, 0, 0, 0, 0, 0));
            return;
        }

        var selection = preparedSelection ?? await Task.Run(
            () => BuildMetricsSelectionProjection(
                currentTree,
                currentPath,
                hasAnyChecked,
                selectedPaths,
                includeOrderedFilePaths: false,
                token),
            token);
        var treeMetricsTask = Task.Run(
			() => CalculateTreeMetrics(selection, treeFormat, token),
			token);
		var contentMetricsTask = Task.Run(
			() => CalculateContentMetrics(selection, token),
			token);

        await Task.WhenAll(treeMetricsTask, contentMetricsTask).ConfigureAwait(false);

        if (token.IsCancellationRequested)
            return;

        var treeMetrics = treeMetricsTask.Result;
        var contentMetrics = contentMetricsTask.Result;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (token.IsCancellationRequested || recalcVersion != Volatile.Read(ref _metricsRecalcVersion))
                return;

            UpdateStatusBarMetrics(
                treeMetrics.Lines, treeMetrics.Chars, treeMetrics.Tokens,
                contentMetrics.ContentOnly.Lines, contentMetrics.ContentOnly.Chars, contentMetrics.ContentOnly.Tokens,
                contentMetrics.TreeAndContentContent);
        });
    }

	private List<string> CollectMissingMetricsFilePaths(
		IReadOnlyList<string> orderedPaths,
		CancellationToken cancellationToken)
    {
		cancellationToken.ThrowIfCancellationRequested();
        var missingPaths = new List<string>();
        lock (_metricsLock)
        {
            for (var index = 0; index < orderedPaths.Count; index++)
            {
				cancellationToken.ThrowIfCancellationRequested();
                var path = orderedPaths[index];
                if (!_fileMetricsCache.TryGetValue(path, out var entry) ||
                    !entry.TryGet(_appliedTransformIdentity, out _))
                    missingPaths.Add(path);
            }
        }

        return missingPaths;
    }

    private async Task<bool> EnsureSelectedFileMetricsAsync(
        IReadOnlyList<string> missingPaths,
        CancellationToken cancellationToken)
    {
        if (missingPaths.Count == 0)
            return false;

        var metricsCts = ReplaceCancellationSource(ref _metricsCalculationCts);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, metricsCts.Token);
        var cacheGeneration = Volatile.Read(ref _metricsCacheGeneration);

        _metricsCancellationRequestedByUser = false;
        _isBackgroundMetricsActive = true;
        var statusOperationId = statusOperations.Begin(
            viewModel.StatusOperationCalculatingData,
            indeterminate: false,
            operationType: StatusOperationType.MetricsCalculation,
            cancelAction: CancelBackgroundCalculation,
            presentation: StatusOperationPresentation.ExtendedDelay);
        var stagedResults = new FileMetricsScanResult[missingPaths.Count];

        try
        {
            if (statusOperations.IsActive(statusOperationId))
                viewModel.StatusProgressValue = 0;

            var hadReadFailures = await ScanFileMetricsAsync(
                missingPaths,
                stagedResults,
				null,
                linkedCts.Token,
                statusOperationId,
                MetricsCalculationPolicy.GetSelectionRecoveryParallelism(Environment.ProcessorCount));
            ThrowIfMetricsRunIsStale(
                metricsCts,
                cacheGeneration,
                linkedCts.Token);
            MergeStagedMetricsIntoCache(
                missingPaths,
                stagedResults,
                cacheGeneration);

            if (statusOperations.IsActive(statusOperationId))
                viewModel.StatusProgressValue = 100;
            return hadReadFailures;
        }
        catch (OperationCanceledException)
        {
            MergeStagedMetricsIntoCache(
                missingPaths,
                stagedResults,
                cacheGeneration);
            throw;
        }
        finally
        {
            if (ReferenceEquals(
                    Volatile.Read(ref _metricsCalculationCts),
                    metricsCts))
            {
                _isBackgroundMetricsActive = false;
            }

            statusOperations.Complete(statusOperationId);
            DisposeIfCurrent(ref _metricsCalculationCts, metricsCts);
        }
    }

    private bool IsCurrentMetricsRun(
        CancellationTokenSource metricsCts,
        int cacheGeneration) =>
        ReferenceEquals(
            Volatile.Read(ref _metricsCalculationCts),
            metricsCts) &&
        cacheGeneration == Volatile.Read(ref _metricsCacheGeneration);

    private void ThrowIfMetricsRunIsStale(
        CancellationTokenSource metricsCts,
        int cacheGeneration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentMetricsRun(metricsCts, cacheGeneration))
            throw new OperationCanceledException(cancellationToken);
    }

    private MetricsSelectionProjection BuildMetricsSelectionProjection(
        BuildTreeResult currentTree,
        string currentPath,
        bool hasSelection,
        IReadOnlySet<string> selectedPaths,
        bool includeOrderedFilePaths,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hasEffectiveSelection = hasSelection &&
            !ProjectTreeSelectionProjection.CoversWholeTree(currentTree.Root, selectedPaths);
        var selectedCount = hasEffectiveSelection ? selectedPaths.Count : 0;
        var selectedHash = hasEffectiveSelection
            ? PreviewFileCollectionPolicy.BuildPathSetHashWithCancellation(
                selectedPaths,
                cancellationToken)
            : 0;
        var projection = new MetricsSelectionProjection(
            currentTree,
            currentPath,
            selectedPaths,
            hasEffectiveSelection,
            selectedCount,
            selectedHash,
            OrderedFilePaths: null);
        return includeOrderedFilePaths
            ? projection with
            {
                OrderedFilePaths = BuildOrderedMetricsFilePaths(projection, cancellationToken)
            }
            : projection;
    }

    private IReadOnlyList<string> BuildOrderedMetricsFilePaths(
        MetricsSelectionProjection selection,
        CancellationToken cancellationToken) =>
        selection.HasEffectiveSelection
            ? PreviewFileCollectionPolicy.BuildOrderedSelectedFilePathsWithCancellation(
                selection.SelectedPaths,
                selection.Tree.Root,
                ensureExists: false,
                cancellationToken)
            : GetOrBuildAllOrderedFilePathsWithCancellation(selection.Tree, cancellationToken);

    private ExportOutputMetrics CalculateTreeMetrics(
        MetricsSelectionProjection selection,
		TreeTextFormat format,
		CancellationToken cancellationToken)
    {
		cancellationToken.ThrowIfCancellationRequested();
		var pathPresentation = exportPathPresentationProvider();
		var transformationContext = transformationContextProvider?.Invoke();
		var displayRootPath = OutputRootPathPresentation.Resolve(
			selection.RootPath,
			pathPresentation,
			transformationContext);
		var pathPresentationIdentity = HashCode.Combine(
			displayRootPath,
			pathPresentation?.DisplayRootName);
        var cacheKey = new TreeMetricsCacheKey(
            TreeIdentity: RuntimeHelpers.GetHashCode(selection.Tree.Root),
            Format: format,
            SelectedCount: selection.SelectedCount,
            SelectedHash: selection.SelectedHash,
            PathPresentationIdentity: pathPresentationIdentity);

        lock (_computationCacheLock)
        {
            if (_hasTreeMetricsCache && _treeMetricsCacheKey == cacheKey)
                return _treeMetricsCacheValue;
        }

		var metrics = selection.HasEffectiveSelection
			? treeExport.CalculateSelectedTreeMetricsWithCancellation(
				selection.RootPath,
				selection.Tree.Root,
				selection.SelectedPaths,
				format,
				displayRootPath,
				pathPresentation?.DisplayRootName,
				cancellationToken)
			: treeExport.CalculateFullTreeMetricsWithCancellation(
				selection.RootPath,
				selection.Tree.Root,
				format,
				displayRootPath,
				pathPresentation?.DisplayRootName,
				cancellationToken);

		cancellationToken.ThrowIfCancellationRequested();
        lock (_computationCacheLock)
        {
            _hasTreeMetricsCache = true;
            _treeMetricsCacheKey = cacheKey;
            _treeMetricsCacheValue = metrics;
        }

        return metrics;
    }

    private ContentMetricsPair CalculateContentMetrics(
        MetricsSelectionProjection selection,
		CancellationToken cancellationToken)
    {
		cancellationToken.ThrowIfCancellationRequested();
		var pathPresentation = exportPathPresentationProvider();
		var transformationContext = transformationContextProvider?.Invoke();
		var outputPathRedaction = OutputRootPathPresentation.CaptureRedactionDecision(
			transformationContext);
		var contentPathMapper = TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(
			selection.RootPath);
		var contentOnlyRootPath = OutputRootPathPresentation.Resolve(
			selection.RootPath,
			pathPresentation,
			outputPathRedaction);
        // The transformation belongs in the key rather than being reconciled here: this method only
        // reads the per-file cache, and clearing it mid-read would publish an empty project.
        // Reconciliation happens where a pass can still refill what it drops.
        var cacheKey = new ContentMetricsCacheKey(
			TreeIdentity: RuntimeHelpers.GetHashCode(selection.Tree.Root),
			SelectedCount: selection.SelectedCount,
			SelectedHash: selection.SelectedHash,
			ContentPathPresentationIdentity: HashCode.Combine(
				contentOnlyRootPath,
				BuildRootPathIdentity(selection.RootPath),
				outputPathRedaction?.OccurrenceId,
				outputPathRedaction?.Keep),
			TreeAndContentRootPathIdentity: BuildRootPathIdentity(selection.RootPath),
            TransformIdentity: ResolveTransformIdentity());

        lock (_computationCacheLock)
        {
            if (_hasContentMetricsCache && _contentMetricsCacheKey == cacheKey)
                return _contentMetricsCacheValue;
        }

		var orderedPaths = selection.OrderedFilePaths ??
			BuildOrderedMetricsFilePaths(selection, cancellationToken);
		var contentOnlyAccumulator = new ExportOutputMetricsCalculator.OrderedContentMetricsAccumulator();
		var treeAndContentAccumulator = new ExportOutputMetricsCalculator.OrderedContentMetricsAccumulator();
		if (orderedPaths.Count > 0)
			contentOnlyAccumulator.AppendRootHeader(contentOnlyRootPath);
		lock (_metricsLock)
        {
			for (var index = 0; index < orderedPaths.Count; index++)
            {
				cancellationToken.ThrowIfCancellationRequested();
				var path = orderedPaths[index];
                if (!_fileMetricsCache.TryGetValue(path, out var cacheEntry) ||
                    !cacheEntry.TryGet(_appliedTransformIdentity, out var variant) ||
                    !variant.HasMetrics)
                {
                    continue;
                }

                var metrics = variant.Metrics;
				var contentPath = OutputRootPathPresentation.ResolvePath(
					MapExportDisplayPath(path, contentPathMapper),
					outputPathRedaction).Text;
				var fileMetrics = new ContentFileMetrics(
					Path: contentPath,
                    SizeBytes: metrics.Size,
                    LineCount: metrics.LineCount,
                    CharCount: metrics.CharCount,
                    IsEmpty: metrics.IsEmpty,
                    IsWhitespaceOnly: metrics.IsWhitespaceOnly,
                    IsEstimated: metrics.IsEstimated,
                    CrLfPairCount: metrics.CrLfPairCount,
					TrailingNewlineChars: metrics.TrailingNewlineChars,
					TrailingNewlineLineBreaks: metrics.TrailingNewlineLineBreaks);

				contentOnlyAccumulator.AppendFile(fileMetrics);
				treeAndContentAccumulator.AppendFile(fileMetrics);
            }
        }

		var computed = new ContentMetricsPair(
            contentOnlyAccumulator.ToMetrics(),
            treeAndContentAccumulator.ToMetrics());
		cancellationToken.ThrowIfCancellationRequested();
        lock (_computationCacheLock)
        {
            _hasContentMetricsCache = true;
            _contentMetricsCacheKey = cacheKey;
            _contentMetricsCacheValue = computed;
        }

        return computed;
    }

    private ExportOutputMetrics GetRenderedStatusContentMetrics()
    {
        if (viewModel.IsAnyPreviewVisible && viewModel.SelectedPreviewContentMode == PreviewContentMode.TreeAndContent)
        {
            return new ExportOutputMetrics(
                _lastStatusTreeAndContentContentLines,
                _lastStatusTreeAndContentContentChars,
                _lastStatusTreeAndContentContentTokens);
        }

        return new ExportOutputMetrics(_lastStatusContentLines, _lastStatusContentChars, _lastStatusContentTokens);
    }

    public IReadOnlyList<string> GetOrBuildAllOrderedFilePaths(TreeNodeDescriptor treeRoot)
		=> GetOrBuildAllOrderedFilePathsWithCancellation(treeRoot, CancellationToken.None);

	private IReadOnlyList<string> GetOrBuildAllOrderedFilePathsWithCancellation(
		TreeNodeDescriptor treeRoot,
		CancellationToken cancellationToken)
    {
		cancellationToken.ThrowIfCancellationRequested();
        var treeIdentity = RuntimeHelpers.GetHashCode(treeRoot);
        lock (_computationCacheLock)
        {
            if (_allOrderedFilePathsCache is not null &&
                _allOrderedFilePathsTreeIdentity == treeIdentity)
            {
                return _allOrderedFilePathsCache;
            }
        }

		var orderedPaths = PreviewFileCollectionPolicy.BuildOrderedAllFilePathsWithCancellation(
			treeRoot,
			cancellationToken);

		cancellationToken.ThrowIfCancellationRequested();
        lock (_computationCacheLock)
        {
            _allOrderedFilePathsTreeIdentity = treeIdentity;
            _allOrderedFilePathsCache = orderedPaths;
            return _allOrderedFilePathsCache;
        }
    }

    public IReadOnlyList<string> GetOrBuildAllOrderedFilePaths(BuildTreeResult currentTree)
		=> GetOrBuildAllOrderedFilePathsWithCancellation(currentTree, CancellationToken.None);

	private IReadOnlyList<string> GetOrBuildAllOrderedFilePathsWithCancellation(
		BuildTreeResult currentTree,
		CancellationToken cancellationToken)
    {
		cancellationToken.ThrowIfCancellationRequested();
        if (currentTree.OrderedFilePaths is not { } orderedFilePaths)
			return GetOrBuildAllOrderedFilePathsWithCancellation(currentTree.Root, cancellationToken);

        var treeIdentity = RuntimeHelpers.GetHashCode(currentTree.Root);
        lock (_computationCacheLock)
        {
            _allOrderedFilePathsTreeIdentity = treeIdentity;
            _allOrderedFilePathsCache = orderedFilePaths;
            return _allOrderedFilePathsCache;
        }
    }

    private StatusMetricLabels BuildStatusMetricLabels()
    {
        var linesLabel = localization.Format("Status.Metric.Lines", "{0}");
        var charsLabel = localization.Format("Status.Metric.Chars", "{0}");
        var tokensLabel = localization.Format("Status.Metric.Tokens", "{0}");

        return new StatusMetricLabels(
            linesLabel.Replace("{0}", string.Empty).Trim(),
            charsLabel.Replace("{0}", string.Empty).Trim(),
            tokensLabel.Replace("{0}", string.Empty).Trim());
    }

    private bool ShouldUseCompactStatusMetrics() =>
        boundsWidthProvider() > 0 && boundsWidthProvider() <= CompactStatusMetricsThresholdWidth;

    private static int BuildRootPathIdentity(string rootPath)
    {
        try
        {
            return StringComparer.Ordinal.GetHashCode(Path.GetFullPath(rootPath));
        }
        catch
        {
            return StringComparer.Ordinal.GetHashCode(rootPath);
        }
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
        var current = Interlocked.CompareExchange(ref target, null, candidate);
        if (ReferenceEquals(current, candidate))
            candidate.Dispose();
    }

    private static void CancelAndDispose(ref CancellationTokenSource? source)
    {
        var current = Interlocked.Exchange(ref source, null);
        if (current is null)
            return;

        try
        {
            current.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        current.Dispose();
    }
}
