using System.Runtime.CompilerServices;
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
    private readonly Dictionary<string, FileMetricsCacheEntry> _fileMetricsCache = new(PathComparer.Default);
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

    public bool IsBackgroundActive => _isBackgroundMetricsActive;

    public bool HasCompleteBaseline
    {
        get => _hasCompleteMetricsBaseline;
        set => _hasCompleteMetricsBaseline = value;
    }

    public bool HasStatusMetricsSnapshot => _hasStatusMetricsSnapshot;

    public void CancelCompressionPrewarm()
    {
        _compressionPrewarmCts?.Cancel();
		Volatile.Write(ref _postLoadReadFacts, null);
    }

    public Task PrewarmCompressionAsync(
        BuildTreeResult currentTree,
        CancellationToken cancellationToken,
        StatusOperationPresentation presentation =
            StatusOperationPresentation.ExtendedDelay)
    {
        var compression = transformationContextProvider?.Invoke()?.Compression;
        if (compression is null)
        {
            viewModel.SetCompressionPreparationStatus(isActive: false);
			viewModel.SetCommentStripPreparationStatus(isActive: false);
            return Task.CompletedTask;
        }

        var selectedPaths = selectedPathsProvider();
        var filePaths = selectedPaths.Count > 0
            ? BuildOrderedSelectedFilePaths(currentTree.Root, selectedPaths, ensureExists: false)
            : GetOrBuildAllOrderedFilePaths(currentTree);
        var prewarmCts = ReplaceCancellationSource(ref _compressionPrewarmCts);
		var compressBodies = (compression.Kinds & CodeTransformKinds.Bodies) != 0;
		var stripComments = (compression.Kinds & CodeTransformKinds.Comments) != 0;
        viewModel.SetCompressionPreparationStatus(compressBodies);
		viewModel.SetCommentStripPreparationStatus(stripComments);
		var progressText = compression.Kinds switch
		{
			CodeTransformKinds.Bodies => localization["Settings.Compression.Status.Scanning"],
			CodeTransformKinds.Comments => localization["Settings.Comments.Status.Scanning"],
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
            progress);
    }

    public void ScheduleRecalculate()
    {
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
        var cacheGeneration = Volatile.Read(ref _metricsCacheGeneration);
        await WaitForInitialMetricsWarmupSlotAsync(cancellationToken);

        // This task represents visual stability, not merely animation completion. Replacing it
        // with the raw reveal task lets file prewarming compete with the island's final layout and
        // causes visible stalls on large projects. F5 passes a completed task and stays immediate.
        await WaitForInitialVisualReadyAsync(initialVisualReadyTask, cancellationToken);
        if (cacheGeneration != Volatile.Read(ref _metricsCacheGeneration))
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
        transformationContextProvider?.Invoke()?.Compression?.Session.IsSupported(filePath) == true;

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
        var contentMetrics = selectedMode == PreviewContentMode.TreeAndContent
            ? new ExportOutputMetrics(
                _lastStatusTreeAndContentContentLines,
                _lastStatusTreeAndContentContentChars,
                _lastStatusTreeAndContentContentTokens)
            : new ExportOutputMetrics(_lastStatusContentLines, _lastStatusContentChars, _lastStatusContentTokens);

        return PreviewSelectionMetricsPolicy.TryGetCachedMetrics(
            _hasStatusMetricsSnapshot,
            selectedMode,
            document,
            selectionRange,
            new ExportOutputMetrics(_lastStatusTreeLines, _lastStatusTreeChars, _lastStatusTreeTokens),
            contentMetrics,
            out metrics);
    }

    public void Dispose()
    {
        CancelAndDispose(ref _metricsCalculationCts);
        CancelAndDispose(ref _recalculateMetricsCts);
        CancelAndDispose(ref _compressionPrewarmCts);

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
        IProgress<CodeCompressionWarmupProgress> progress)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            prewarmCts.Token,
            cancellationToken);
		CodeCompressionWarmupResult? warmupResult = null;
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
				Volatile.Write(ref _postLoadReadFacts, warmupResult?.ReadFacts);
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
                    }
                    viewModel.SetCompressionPreparationStatus(isActive: false);
					viewModel.SetCommentStripPreparationStatus(isActive: false);
                    statusOperations.Complete(statusOperationId);
                });
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
                    () => GetOrBuildAllOrderedFilePaths(currentTree),
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
				var selection = ContentSelectionSnapshot.Create(projectRoot, filePaths);
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

            await ScanFileMetricsAsync(
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
            _hasCompleteMetricsBaseline = true;
            if (statusOperations.IsActive(statusOperationId))
                viewModel.StatusProgressValue = 100;
            Recalculate();
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
            }
        }
    }

    private async Task ScanFileMetricsAsync(
        IReadOnlyList<string> filePaths,
        FileMetricsScanResult[] stagedResults,
		ContentReadFactSnapshot? readFacts,
        CancellationToken cancellationToken,
        long statusOperationId,
        int maxDegreeOfParallelism)
    {
        if (filePaths.Count == 0)
            return;

        var processedCount = 0;
        var lastProgressPercent = 0;
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
					var fact = retainedFact ?? await fileContentAnalyzer
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
            catch
            {
                stagedResults[index] = new FileMetricsScanResult(
                    Raw: new FileMetricsVariant(default, HasMetrics: false),
                    Effective: new FileMetricsVariant(default, HasMetrics: false),
                    TransformIdentity: transformIdentity,
                    WasInspected: true);
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

            var targetFilePaths = await Task.Run(
                () => hasAnyChecked
                    ? BuildOrderedSelectedFilePaths(currentTree.Root, selectedPaths, ensureExists: false)
                    : GetOrBuildAllOrderedFilePaths(currentTree),
                token);

            if (targetFilePaths.Count == 0)
            {
                await PublishTreeMetricsWhileContentPendingAsync(
                    token,
                    recalcVersion,
                    hasAnyChecked,
                    selectedPaths,
                    treeFormat,
                    currentTree,
                    currentPath);
                completed = true;
                return;
            }

            var missingPaths = await Task.Run(() => CollectMissingMetricsFilePaths(targetFilePaths), token);
            if (missingPaths.Count > 0)
            {
                if (_isBackgroundMetricsActive)
                {
                    _metricsCalculationCts?.Cancel();
                    await WaitForBackgroundMetricsIdleAsync(token);
                    if (token.IsCancellationRequested)
                        return;

                    missingPaths = await Task.Run(() => CollectMissingMetricsFilePaths(targetFilePaths), token);
                }

                if (missingPaths.Count > 0)
                {
                    await PublishTreeMetricsWhileContentPendingAsync(
                        token,
                        recalcVersion,
                        hasAnyChecked,
                        selectedPaths,
                        treeFormat,
                        currentTree,
                        currentPath);

                    await EnsureSelectedFileMetricsAsync(missingPaths, token);
                }
            }

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
            // Expected when a newer selection supersedes the current one.
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
        bool hasAnyChecked,
        IReadOnlySet<string> selectedPaths,
        TreeTextFormat treeFormat,
        BuildTreeResult currentTree,
        string currentPath)
    {
        if (token.IsCancellationRequested)
            return;

        var treeMetrics = await Task.Run(() => CalculateTreeMetrics(hasAnyChecked, selectedPaths, treeFormat), token);

        if (token.IsCancellationRequested)
            return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (token.IsCancellationRequested || recalcVersion != Volatile.Read(ref _metricsRecalcVersion))
                return;

            if (!string.Equals(currentPathProvider(), currentPath, StringComparison.Ordinal) ||
                !ReferenceEquals(currentTreeProvider(), currentTree))
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
        string? currentPath)
    {
        if (token.IsCancellationRequested)
            return;

        if (currentTree is null || string.IsNullOrWhiteSpace(currentPath))
        {
            await Dispatcher.UIThread.InvokeAsync(
                () => UpdateStatusBarMetrics(0, 0, 0, 0, 0, 0));
            return;
        }

        var treeMetricsTask = Task.Run(() => CalculateTreeMetrics(hasAnyChecked, selectedPaths, treeFormat), token);
        var contentMetricsTask = Task.Run(() => CalculateContentMetrics(hasAnyChecked, selectedPaths, currentPath), token);

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

    private List<string> CollectMissingMetricsFilePaths(IReadOnlyList<string> orderedPaths)
    {
        var missingPaths = new List<string>();
        lock (_metricsLock)
        {
            for (var index = 0; index < orderedPaths.Count; index++)
            {
                var path = orderedPaths[index];
                if (!_fileMetricsCache.TryGetValue(path, out var entry) ||
                    !entry.TryGet(_appliedTransformIdentity, out _))
                    missingPaths.Add(path);
            }
        }

        return missingPaths;
    }

    private async Task EnsureSelectedFileMetricsAsync(
        IReadOnlyList<string> missingPaths,
        CancellationToken cancellationToken)
    {
        if (missingPaths.Count == 0)
            return;

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

            await ScanFileMetricsAsync(
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

    private ExportOutputMetrics CalculateTreeMetrics(
        bool hasSelection,
        IReadOnlySet<string> selectedPaths,
        TreeTextFormat format)
    {
        var currentTree = currentTreeProvider();
        var currentPath = currentPathProvider();
        if (currentTree is null || string.IsNullOrWhiteSpace(currentPath))
            return ExportOutputMetrics.Empty;

        var isFullTreeSelection =
            ProjectTreeSelectionProjection.CoversWholeTree(
                currentTree.Root,
                selectedPaths);
        var effectiveHasSelection = hasSelection && !isFullTreeSelection;
        var pathPresentation = exportPathPresentationProvider();
        var pathPresentationIdentity = pathPresentation is null
            ? 0
            : HashCode.Combine(pathPresentation.DisplayRootPath, pathPresentation.DisplayRootName);
        var selectedCount = effectiveHasSelection ? selectedPaths.Count : 0;
        var selectedHash = effectiveHasSelection ? PreviewFileCollectionPolicy.BuildPathSetHash(selectedPaths) : 0;
        var cacheKey = new TreeMetricsCacheKey(
            TreeIdentity: RuntimeHelpers.GetHashCode(currentTree.Root),
            Format: format,
            SelectedCount: selectedCount,
            SelectedHash: selectedHash,
            PathPresentationIdentity: pathPresentationIdentity);

        lock (_computationCacheLock)
        {
            if (_hasTreeMetricsCache && _treeMetricsCacheKey == cacheKey)
                return _treeMetricsCacheValue;
        }

        var metrics = effectiveHasSelection
            ? treeExport.CalculateSelectedTreeMetrics(
                currentPath,
                currentTree.Root,
                selectedPaths,
                format,
                pathPresentation?.DisplayRootPath,
                pathPresentation?.DisplayRootName)
            : treeExport.CalculateFullTreeMetrics(
                currentPath,
                currentTree.Root,
                format,
                pathPresentation?.DisplayRootPath,
                pathPresentation?.DisplayRootName);

        lock (_computationCacheLock)
        {
            _hasTreeMetricsCache = true;
            _treeMetricsCacheKey = cacheKey;
            _treeMetricsCacheValue = metrics;
        }

        return metrics;
    }

    private ContentMetricsPair CalculateContentMetrics(
        bool hasSelection,
        IReadOnlySet<string> selectedPaths,
        string currentPath)
    {
        var currentTree = currentTreeProvider();
        if (currentTree is null)
            return new ContentMetricsPair(ExportOutputMetrics.Empty, ExportOutputMetrics.Empty);

        var pathPresentation = exportPathPresentationProvider();
        var contentOnlyPathMapper = pathPresentation?.MapFilePath;
        var treeAndContentPathMapper = TreeAndContentExportService.CreateRelativeContentHeaderPathMapper(currentPath);
        var isFullTreeSelection =
            ProjectTreeSelectionProjection.CoversWholeTree(
                currentTree.Root,
                selectedPaths);
        var effectiveHasSelection = hasSelection && !isFullTreeSelection;
        var selectedCount = effectiveHasSelection ? selectedPaths.Count : 0;
        var selectedHash = effectiveHasSelection ? PreviewFileCollectionPolicy.BuildPathSetHash(selectedPaths) : 0;
        // The transformation belongs in the key rather than being reconciled here: this method only
        // reads the per-file cache, and clearing it mid-read would publish an empty project.
        // Reconciliation happens where a pass can still refill what it drops.
        var cacheKey = new ContentMetricsCacheKey(
            TreeIdentity: RuntimeHelpers.GetHashCode(currentTree.Root),
            SelectedCount: selectedCount,
            SelectedHash: selectedHash,
            ContentPathPresentationIdentity: BuildPathPresentationIdentity(pathPresentation),
            TreeAndContentRootPathIdentity: BuildRootPathIdentity(currentPath),
            TransformIdentity: ResolveTransformIdentity());

        lock (_computationCacheLock)
        {
            if (_hasContentMetricsCache && _contentMetricsCacheKey == cacheKey)
                return _contentMetricsCacheValue;
        }

        var orderedPaths = effectiveHasSelection
            ? BuildOrderedSelectedFilePaths(currentTree.Root, selectedPaths, ensureExists: false)
            : GetOrBuildAllOrderedFilePaths(currentTree);

        if (orderedPaths.Count == 0)
            return new ContentMetricsPair(ExportOutputMetrics.Empty, ExportOutputMetrics.Empty);

        var contentOnlyAccumulator = new ExportOutputMetricsCalculator.OrderedContentMetricsAccumulator();
        var treeAndContentAccumulator = new ExportOutputMetricsCalculator.OrderedContentMetricsAccumulator();
        lock (_metricsLock)
        {
            foreach (var path in orderedPaths)
            {
                if (!_fileMetricsCache.TryGetValue(path, out var cacheEntry) ||
                    !cacheEntry.TryGet(_appliedTransformIdentity, out var variant) ||
                    !variant.HasMetrics)
                {
                    continue;
                }

                var metrics = variant.Metrics;
                var contentOnlyPath = MapExportDisplayPath(path, contentOnlyPathMapper);
                var treeAndContentPath = MapExportDisplayPath(path, treeAndContentPathMapper);
                var fileMetrics = new ContentFileMetrics(
                    Path: contentOnlyPath,
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
                treeAndContentAccumulator.AppendFile(fileMetrics with { Path = treeAndContentPath });
            }
        }

        var computed = new ContentMetricsPair(
            contentOnlyAccumulator.ToMetrics(),
            treeAndContentAccumulator.ToMetrics());
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
    {
        var treeIdentity = RuntimeHelpers.GetHashCode(treeRoot);
        lock (_computationCacheLock)
        {
            if (_allOrderedFilePathsCache is not null &&
                _allOrderedFilePathsTreeIdentity == treeIdentity)
            {
                return _allOrderedFilePathsCache;
            }
        }

        var orderedPaths = PreviewFileCollectionPolicy.BuildOrderedAllFilePaths(treeRoot);

        lock (_computationCacheLock)
        {
            _allOrderedFilePathsTreeIdentity = treeIdentity;
            _allOrderedFilePathsCache = orderedPaths;
            return _allOrderedFilePathsCache;
        }
    }

    public IReadOnlyList<string> GetOrBuildAllOrderedFilePaths(BuildTreeResult currentTree)
    {
        if (currentTree.OrderedFilePaths is not { } orderedFilePaths)
            return GetOrBuildAllOrderedFilePaths(currentTree.Root);

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

    private static int BuildPathPresentationIdentity(ExportPathPresentation? pathPresentation)
    {
        return pathPresentation is null
            ? 0
            : HashCode.Combine(
                pathPresentation.DisplayRootPath,
                pathPresentation.DisplayRootName,
                RuntimeHelpers.GetHashCode(pathPresentation.MapFilePath));
    }

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

    private static List<string> BuildOrderedSelectedFilePaths(
        TreeNodeDescriptor treeRoot,
        IReadOnlySet<string> selectedPaths,
        bool ensureExists = true) =>
        PreviewFileCollectionPolicy.BuildOrderedSelectedFilePaths(selectedPaths, treeRoot, ensureExists);

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
