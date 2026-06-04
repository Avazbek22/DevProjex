using System.Collections.Concurrent;
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
        int PathMapperIdentity);

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

    private readonly object _metricsLock = new();
    private readonly object _computationCacheLock = new();
    private readonly Dictionary<string, FileMetricsData> _fileMetricsCache = new(PathComparer.Default);
    private readonly HashSet<string> _inspectedFileMetricsPaths = new(PathComparer.Default);

    private CancellationTokenSource? _metricsCalculationCts;
    private DispatcherTimer? _metricsDebounceTimer;
    private CancellationTokenSource? _recalculateMetricsCts;
    private volatile bool _isBackgroundMetricsActive;
    private int _metricsRecalcVersion;
    private int _lastStatusTreeLines;
    private int _lastStatusTreeChars;
    private int _lastStatusTreeTokens;
    private int _lastStatusContentLines;
    private int _lastStatusContentChars;
    private int _lastStatusContentTokens;
    private bool _hasStatusMetricsSnapshot;
    private bool _hasTreeMetricsCache;
    private TreeMetricsCacheKey _treeMetricsCacheKey;
    private ExportOutputMetrics _treeMetricsCacheValue = ExportOutputMetrics.Empty;
    private bool _hasContentMetricsCache;
    private ContentMetricsCacheKey _contentMetricsCacheKey;
    private ExportOutputMetrics _contentMetricsCacheValue = ExportOutputMetrics.Empty;
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
        TreeNodeDescriptor treeRoot,
        CancellationToken cancellationToken)
    {
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        cancellationToken.ThrowIfCancellationRequested();

        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render);
        cancellationToken.ThrowIfCancellationRequested();

        var warmupDelay = UiTimingProfile.Scale(
            MetricsCalculationPolicy.GetInitialWarmupStartDelay(viewModel.SettingsVisible));
        if (warmupDelay > TimeSpan.Zero)
            await Task.Delay(warmupDelay, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        await InitializeFileMetricsCacheAsync(treeRoot, cancellationToken);
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
            _inspectedFileMetricsPaths.Clear();
            if (trimCapacity)
            {
                _fileMetricsCache.TrimExcess();
                _inspectedFileMetricsPaths.TrimExcess();
            }
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
            _contentMetricsCacheValue = ExportOutputMetrics.Empty;
            _allOrderedFilePathsCache = null;
            _allOrderedFilePathsTreeIdentity = 0;
        }
    }

    public void UpdateStatusBarMetrics(
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

    public void RenderStatusBarMetrics()
    {
        var labels = BuildStatusMetricLabels();
        var useCompactMode = ShouldUseCompactStatusMetrics();
        viewModel.StatusTreeStatsText = PreviewSelectionMetricsPolicy.FormatStatusMetricsText(
            new ExportOutputMetrics(_lastStatusTreeLines, _lastStatusTreeChars, _lastStatusTreeTokens),
            labels,
            useCompactMode);
        viewModel.StatusContentStatsText = PreviewSelectionMetricsPolicy.FormatStatusMetricsText(
            new ExportOutputMetrics(_lastStatusContentLines, _lastStatusContentChars, _lastStatusContentTokens),
            labels,
            useCompactMode);
    }

    public bool TryGetCachedPreviewSelectionMetrics(
        PreviewContentMode selectedMode,
        IPreviewTextDocument document,
        PreviewSelectionRange selectionRange,
        out ExportOutputMetrics metrics)
    {
        return PreviewSelectionMetricsPolicy.TryGetCachedMetrics(
            _hasStatusMetricsSnapshot,
            selectedMode,
            document,
            selectionRange,
            new ExportOutputMetrics(_lastStatusTreeLines, _lastStatusTreeChars, _lastStatusTreeTokens),
            new ExportOutputMetrics(_lastStatusContentLines, _lastStatusContentChars, _lastStatusContentTokens),
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

    private async Task InitializeFileMetricsCacheAsync(TreeNodeDescriptor treeRoot, CancellationToken cancellationToken)
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
        var stagedMetrics = new ConcurrentDictionary<string, FileMetricsData>(PathComparer.Default);
        var stagedInspectedPaths = new ConcurrentDictionary<string, byte>(PathComparer.Default);
        try
        {
            if (statusOperations.IsActive(statusOperationId))
                viewModel.StatusProgressValue = 0;

            IReadOnlyList<string> filePaths;
            using (PerformanceMetrics.Measure("CollectMetricsWarmupFilePaths"))
            {
                filePaths = await Task.Run(
                    () => GetOrBuildAllOrderedFilePaths(treeRoot),
                    linkedCts.Token);
            }

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
                stagedMetrics,
                stagedInspectedPaths,
                linkedCts.Token,
                statusOperationId,
                MetricsCalculationPolicy.GetBaselineWarmupParallelism(Environment.ProcessorCount));

            MergeStagedMetricsIntoCache(stagedMetrics);
            MergeInspectedMetricsPaths(stagedInspectedPaths);

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
            MergeStagedMetricsIntoCache(stagedMetrics);
            MergeInspectedMetricsPaths(stagedInspectedPaths);
            bool hasCachedMetrics;
            lock (_metricsLock)
                hasCachedMetrics = _fileMetricsCache.Count > 0;
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

    private void MergeStagedMetricsIntoCache(ConcurrentDictionary<string, FileMetricsData> stagedMetrics)
    {
        if (stagedMetrics.IsEmpty)
            return;

        lock (_metricsLock)
        {
            foreach (var pair in stagedMetrics)
                _fileMetricsCache[pair.Key] = pair.Value;
        }

        stagedMetrics.Clear();
    }

    private void MergeInspectedMetricsPaths(ConcurrentDictionary<string, byte> inspectedPaths)
    {
        if (inspectedPaths.IsEmpty)
            return;

        lock (_metricsLock)
        {
            foreach (var pair in inspectedPaths)
                _inspectedFileMetricsPaths.Add(pair.Key);
        }

        inspectedPaths.Clear();
    }

    private async Task ScanFileMetricsAsync(
        IReadOnlyList<string> filePaths,
        ConcurrentDictionary<string, FileMetricsData> stagedMetrics,
        ConcurrentDictionary<string, byte> stagedInspectedPaths,
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

        await Parallel.ForEachAsync(filePaths, parallelOptions, async (filePath, ct) =>
        {
            try
            {
                var metrics = await fileContentAnalyzer.GetTextFileMetricsAsync(filePath, ct)
                    .ConfigureAwait(false);

                if (metrics is not null)
                {
                    stagedMetrics[filePath] = new FileMetricsData(
                        metrics.SizeBytes,
                        metrics.LineCount,
                        metrics.CharCount,
                        metrics.IsEmpty,
                        metrics.IsWhitespaceOnly,
                        metrics.IsEstimated,
                        metrics.CrLfPairCount,
                        metrics.TrailingNewlineChars,
                        metrics.TrailingNewlineLineBreaks);
                }

                // Null means "not exportable as text", not "still missing". Remembering the
                // completed probe prevents binary files from keeping selected content metrics
                // stuck behind a permanent "missing metrics" gate.
                stagedInspectedPaths[filePath] = 0;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                stagedInspectedPaths[filePath] = 0;
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
                    : GetOrBuildAllOrderedFilePaths(currentTree.Root),
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
        var contentMetricsTask = Task.Run(() => CalculateContentMetrics(hasAnyChecked, selectedPaths), token);

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
                contentMetrics.Lines, contentMetrics.Chars, contentMetrics.Tokens);
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
                if (!_inspectedFileMetricsPaths.Contains(path))
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
        var stagedMetrics = new ConcurrentDictionary<string, FileMetricsData>(PathComparer.Default);
        var stagedInspectedPaths = new ConcurrentDictionary<string, byte>(PathComparer.Default);

        try
        {
            if (statusOperations.IsActive(statusOperationId))
                viewModel.StatusProgressValue = 0;

            await ScanFileMetricsAsync(
                missingPaths,
                stagedMetrics,
                stagedInspectedPaths,
                linkedCts.Token,
                statusOperationId,
                MetricsCalculationPolicy.GetSelectionRecoveryParallelism(Environment.ProcessorCount));
            MergeStagedMetricsIntoCache(stagedMetrics);
            MergeInspectedMetricsPaths(stagedInspectedPaths);

            if (statusOperations.IsActive(statusOperationId))
                viewModel.StatusProgressValue = 100;
        }
        catch (OperationCanceledException)
        {
            MergeStagedMetricsIntoCache(stagedMetrics);
            MergeInspectedMetricsPaths(stagedInspectedPaths);
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

    private ExportOutputMetrics CalculateContentMetrics(bool hasSelection, IReadOnlySet<string> selectedPaths)
    {
        var currentTree = currentTreeProvider();
        if (currentTree is null)
            return ExportOutputMetrics.Empty;

        var pathMapper = exportPathPresentationProvider()?.MapFilePath;
        var isFullTreeSelection = hasSelection && IsFullTreeSelection(currentTree.Root, selectedPaths);
        var effectiveHasSelection = hasSelection && !isFullTreeSelection;
        var selectedCount = effectiveHasSelection ? selectedPaths.Count : 0;
        var selectedHash = effectiveHasSelection ? PreviewFileCollectionPolicy.BuildPathSetHash(selectedPaths) : 0;
        var cacheKey = new ContentMetricsCacheKey(
            TreeIdentity: RuntimeHelpers.GetHashCode(currentTree.Root),
            SelectedCount: selectedCount,
            SelectedHash: selectedHash,
            PathMapperIdentity: pathMapper is null ? 0 : RuntimeHelpers.GetHashCode(pathMapper));

        lock (_computationCacheLock)
        {
            if (_hasContentMetricsCache && _contentMetricsCacheKey == cacheKey)
                return _contentMetricsCacheValue;
        }

        var orderedPaths = effectiveHasSelection
            ? BuildOrderedSelectedFilePaths(currentTree.Root, selectedPaths, ensureExists: false)
            : GetOrBuildAllOrderedFilePaths(currentTree.Root);

        if (orderedPaths.Count == 0)
            return ExportOutputMetrics.Empty;

        var accumulator = new ExportOutputMetricsCalculator.OrderedContentMetricsAccumulator();
        lock (_metricsLock)
        {
            foreach (var path in orderedPaths)
            {
                if (!_fileMetricsCache.TryGetValue(path, out var metrics))
                    continue;

                var displayPath = MapExportDisplayPath(path, pathMapper);
                accumulator.AppendFile(new ContentFileMetrics(
                    Path: displayPath,
                    SizeBytes: metrics.Size,
                    LineCount: metrics.LineCount,
                    CharCount: metrics.CharCount,
                    IsEmpty: metrics.IsEmpty,
                    IsWhitespaceOnly: metrics.IsWhitespaceOnly,
                    IsEstimated: metrics.IsEstimated,
                    CrLfPairCount: metrics.CrLfPairCount,
                    TrailingNewlineChars: metrics.TrailingNewlineChars,
                    TrailingNewlineLineBreaks: metrics.TrailingNewlineLineBreaks));
            }
        }

        var computed = accumulator.ToMetrics();
        lock (_computationCacheLock)
        {
            _hasContentMetricsCache = true;
            _contentMetricsCacheKey = cacheKey;
            _contentMetricsCacheValue = computed;
        }

        return computed;
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

    private static List<string> BuildOrderedSelectedFilePaths(
        TreeNodeDescriptor treeRoot,
        IReadOnlySet<string> selectedPaths,
        bool ensureExists = true) =>
        PreviewFileCollectionPolicy.BuildOrderedSelectedFilePaths(selectedPaths, treeRoot, ensureExists);

    private static bool IsDefinitelyBinaryByExtensionForMetricsWarmup(string path)
    {
        var extension = Path.GetExtension(path);
        return !string.IsNullOrWhiteSpace(extension) && MetricsWarmupBinaryExtensions.Contains(extension);
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
