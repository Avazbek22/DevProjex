using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using DevProjex.Avalonia.Services;
using DevProjex.Kernel;

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
    Func<double> boundsWidthProvider) : IDisposable
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
        int TreeAndContentRootPathIdentity);

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

    private readonly record struct FileMetricsScanResult(
        FileMetricsData Metrics,
        bool HasMetrics,
        bool WasInspected);

    private readonly record struct FileMetricsCacheEntry(
        FileMetricsData Metrics,
        bool HasMetrics);

    private const double CompactStatusMetricsThresholdWidth = 1050;

    private static readonly FrozenSet<string> MetricsWarmupBinaryExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
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

    private static async Task YieldUiAsync(DispatcherPriority priority)
        => await DispatcherTaskSchedulerProvider.YieldAsync(priority);

    private readonly object _metricsLock = new();
    private readonly object _computationCacheLock = new();
    private readonly Dictionary<string, FileMetricsCacheEntry> _fileMetricsCache = new(PathComparer.Default);

    private CancellationTokenSource? _metricsCalculationCts;
    private DispatcherTimer? _metricsDebounceTimer;
    private CancellationTokenSource? _recalculateMetricsCts;
    private volatile bool _isBackgroundMetricsActive;
    private int _metricsRecalcVersion;
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

    public void Recalculate()
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
                currentPath);
            return;
        }

        if (!MetricsCalculationPolicy.ShouldProceedWithMetricsCalculation(hasAnyChecked, hasCompleteMetricsBaseline))
        {
            UpdateStatusBarMetrics(0, 0, 0, 0, 0, 0);
            DisposeIfCurrent(ref _recalculateMetricsCts, recalcCts);
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
            currentPath);
    }

    public async Task InitializeFileMetricsCacheSoonAfterFirstPaintAsync(
        BuildTreeResult currentTree,
        CancellationToken cancellationToken)
    {
        await WaitForInitialMetricsWarmupSlotAsync(cancellationToken);
        await InitializeFileMetricsCacheAsync(currentTree, cancellationToken);
    }

#if DEVPROJEX_PROJECT_LOAD_TIMING
    public async Task<TimeSpan> InitializeFileMetricsCacheSoonAfterFirstPaintMeasuredAsync(
        BuildTreeResult currentTree,
        CancellationToken cancellationToken)
    {
        await WaitForInitialMetricsWarmupSlotAsync(cancellationToken);

        var stopwatch = Stopwatch.StartNew();
        await InitializeFileMetricsCacheAsync(currentTree, cancellationToken);
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

        var warmupDelay = UiTimingProfile.Scale(
            MetricsCalculationPolicy.GetInitialWarmupStartDelay(viewModel.SettingsVisible));
        if (warmupDelay > TimeSpan.Zero)
            await Task.Delay(warmupDelay, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
    }

    public void CancelBackgroundCalculation()
    {
        if (_isBackgroundMetricsActive)
            _hasCompleteMetricsBaseline = false;

        _isBackgroundMetricsActive = false;
        _metricsCalculationCts?.Cancel();
        _recalculateMetricsCts?.Cancel();
    }

    public void CancelByUser()
    {
        _metricsCancellationRequestedByUser = true;
        _hasCompleteMetricsBaseline = false;
        CancelBackgroundCalculation();
        UpdateStatusBarMetrics(0, 0, 0, 0, 0, 0);
        viewModel.StatusMetricsVisible = viewModel.IsProjectLoaded;
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

        if (_metricsDebounceTimer is not null)
        {
            _metricsDebounceTimer.Stop();
            _metricsDebounceTimer.Tick -= OnMetricsDebounceTimerTick;
            _metricsDebounceTimer = null;
        }
    }

    private void OnMetricsDebounceTimerTick(object? sender, EventArgs e)
    {
        _metricsDebounceTimer?.Stop();
        Recalculate();
    }

    private async Task InitializeFileMetricsCacheAsync(BuildTreeResult currentTree, CancellationToken cancellationToken)
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
            cancelAction: CancelBackgroundCalculation);
        IReadOnlyList<string> stagedFilePaths = Array.Empty<string>();
        FileMetricsScanResult[] stagedResults = [];
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
            stagedFilePaths = filePaths;
            stagedResults = new FileMetricsScanResult[filePaths.Count];

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

            if (AreAllFilesDefinitelyBinaryForMetricsWarmup(filePaths))
            {
                _isBackgroundMetricsActive = false;
                _hasCompleteMetricsBaseline = true;
                if (statusOperations.IsActive(statusOperationId))
                    viewModel.StatusProgressValue = 100;
                Recalculate();
                viewModel.StatusMetricsVisible = true;
                statusOperations.Complete(statusOperationId);
                return;
            }

            await ScanFileMetricsAsync(
                filePaths,
                stagedResults,
                linkedCts.Token,
                statusOperationId,
                MetricsCalculationPolicy.GetBaselineWarmupParallelism(Environment.ProcessorCount));

            MergeStagedMetricsIntoCache(stagedFilePaths, stagedResults);

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
            _isBackgroundMetricsActive = false;
            _hasCompleteMetricsBaseline = false;
            MergeStagedMetricsIntoCache(stagedFilePaths, stagedResults);
            bool hasCachedMetrics;
            lock (_metricsLock)
            {
                hasCachedMetrics = false;
                foreach (var entry in _fileMetricsCache.Values)
                {
                    if (!entry.HasMetrics)
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
            DisposeIfCurrent(ref _metricsCalculationCts, metricsCts);
        }
    }

    private static bool AreAllFilesDefinitelyBinaryForMetricsWarmup(IReadOnlyList<string> filePaths)
    {
        for (var index = 0; index < filePaths.Count; index++)
        {
            if (!IsDefinitelyBinaryByExtensionForMetricsWarmup(filePaths[index]))
                return false;
        }

        return true;
    }

    private void MergeStagedMetricsIntoCache(
        IReadOnlyList<string> filePaths,
        IReadOnlyList<FileMetricsScanResult> results)
    {
        if (filePaths.Count == 0 || results.Count == 0)
            return;

        lock (_metricsLock)
        {
            var count = Math.Min(filePaths.Count, results.Count);
            for (var index = 0; index < count; index++)
            {
                var result = results[index];
                if (!result.WasInspected)
                    continue;

                var filePath = filePaths[index];
                _fileMetricsCache[filePath] = new FileMetricsCacheEntry(
                    result.Metrics,
                    result.HasMetrics);
            }
        }
    }

    private async Task ScanFileMetricsAsync(
        IReadOnlyList<string> filePaths,
        FileMetricsScanResult[] stagedResults,
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

        await Parallel.ForAsync(0, filePaths.Count, parallelOptions, async (index, ct) =>
        {
            var filePath = filePaths[index];
            try
            {
                if (IsDefinitelyBinaryByExtensionForMetricsWarmup(filePath))
                {
                    stagedResults[index] = new FileMetricsScanResult(
                        Metrics: default,
                        HasMetrics: false,
                        WasInspected: true);
                    return;
                }

                var metrics = await fileContentAnalyzer.GetTextFileMetricsAsync(filePath, ct)
                    .ConfigureAwait(false);

                if (metrics is not null)
                {
                    stagedResults[index] = new FileMetricsScanResult(
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
                        HasMetrics: true,
                        WasInspected: true);
                    return;
                }

                // Null means "not exportable as text", not "still missing". Remembering the
                // completed probe prevents binary files from keeping selected content metrics
                // stuck behind a permanent "missing metrics" gate.
                stagedResults[index] = new FileMetricsScanResult(
                    Metrics: default,
                    HasMetrics: false,
                    WasInspected: true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                stagedResults[index] = new FileMetricsScanResult(
                    Metrics: default,
                    HasMetrics: false,
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

    private async Task RecalculateIncompleteBaselineMetricsAsync(
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
            if (token.IsCancellationRequested)
                return;

            if (currentTree is null || string.IsNullOrWhiteSpace(currentPath))
            {
                await Dispatcher.UIThread.InvokeAsync(() => UpdateStatusBarMetrics(0, 0, 0, 0, 0, 0));
                return;
            }

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
        }
        catch (OperationCanceledException)
        {
            // Expected when a newer selection supersedes the current one.
        }
        finally
        {
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
        string? currentPath)
    {
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
                if (!_fileMetricsCache.ContainsKey(path))
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

        _metricsCancellationRequestedByUser = false;
        _isBackgroundMetricsActive = true;
        var statusOperationId = statusOperations.Begin(
            viewModel.StatusOperationCalculatingData,
            indeterminate: false,
            operationType: StatusOperationType.MetricsCalculation,
            cancelAction: CancelBackgroundCalculation);
        var stagedResults = new FileMetricsScanResult[missingPaths.Count];

        try
        {
            if (statusOperations.IsActive(statusOperationId))
                viewModel.StatusProgressValue = 0;

            await ScanFileMetricsAsync(
                missingPaths,
                stagedResults,
                linkedCts.Token,
                statusOperationId,
                MetricsCalculationPolicy.GetSelectionRecoveryParallelism(Environment.ProcessorCount));
            MergeStagedMetricsIntoCache(missingPaths, stagedResults);

            if (statusOperations.IsActive(statusOperationId))
                viewModel.StatusProgressValue = 100;
        }
        catch (OperationCanceledException)
        {
            MergeStagedMetricsIntoCache(missingPaths, stagedResults);
            throw;
        }
        finally
        {
            _isBackgroundMetricsActive = false;
            statusOperations.Complete(statusOperationId);
            DisposeIfCurrent(ref _metricsCalculationCts, metricsCts);
        }
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

        var isFullTreeSelection = hasSelection && IsFullTreeSelection(currentTree.Root, selectedPaths);
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
        var isFullTreeSelection = hasSelection && IsFullTreeSelection(currentTree.Root, selectedPaths);
        var effectiveHasSelection = hasSelection && !isFullTreeSelection;
        var selectedCount = effectiveHasSelection ? selectedPaths.Count : 0;
        var selectedHash = effectiveHasSelection ? PreviewFileCollectionPolicy.BuildPathSetHash(selectedPaths) : 0;
        var cacheKey = new ContentMetricsCacheKey(
            TreeIdentity: RuntimeHelpers.GetHashCode(currentTree.Root),
            SelectedCount: selectedCount,
            SelectedHash: selectedHash,
            ContentPathPresentationIdentity: BuildPathPresentationIdentity(pathPresentation),
            TreeAndContentRootPathIdentity: BuildRootPathIdentity(currentPath));

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
                    !cacheEntry.HasMetrics)
                {
                    continue;
                }

                var metrics = cacheEntry.Metrics;
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

    private static bool IsFullTreeSelection(TreeNodeDescriptor treeRoot, IReadOnlySet<string> selectedPaths) =>
        selectedPaths.Contains(treeRoot.FullPath);

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

    private static bool IsDefinitelyBinaryByExtensionForMetricsWarmup(string path)
    {
        var extension = Path.GetExtension(path.AsSpan());
        if (extension.IsWhiteSpace())
            return false;

        if (MetricsWarmupBinaryExtensions.TryGetAlternateLookup<ReadOnlySpan<char>>(out var lookup))
            return lookup.Contains(extension);

        return MetricsWarmupBinaryExtensions.Contains(extension.ToString());
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
