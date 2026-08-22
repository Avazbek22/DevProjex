namespace DevProjex.Avalonia.Coordinators;

public sealed class TreeSearchCoordinator(
    MainWindowViewModel viewModel,
    TreeView treeView,
    ITreeSearchMetricsSink? metricsSink = null)
    : IDisposable
{
    internal enum NavigationResult
    {
        Canceled = 0,
        Navigated = 1,
        NoMatches = 2
    }

    internal sealed class BringIntoViewPathProgress(int segmentCount)
    {
        internal const int MaxNoProgressAttempts = 4;

        public int SegmentCount { get; } = segmentCount;
        public int DeepestRealizedSegment { get; private set; } = -1;
        public int NoProgressAttempts { get; private set; }
        public int TotalAttempts { get; private set; }

        public bool Observe(int deepestRealizedSegment)
        {
            TotalAttempts++;
            if (deepestRealizedSegment > DeepestRealizedSegment)
            {
                DeepestRealizedSegment = deepestRealizedSegment;
                NoProgressAttempts = 0;
                return true;
            }

            NoProgressAttempts++;
            return NoProgressAttempts < MaxNoProgressAttempts;
        }
    }

    private sealed class BringIntoViewRequest(
        TreeNodeViewModel node,
        TreeNodeViewModel[] path,
        int version,
        bool adjustHorizontalOffset,
        double? originalHorizontalOffset)
    {
        public TreeNodeViewModel Node { get; } = node;
        public TreeNodeViewModel[] Path { get; } = path;
        public int Version { get; } = version;
        public bool AdjustHorizontalOffset { get; } = adjustHorizontalOffset;
        public double? OriginalHorizontalOffset { get; } = originalHorizontalOffset;
        public BringIntoViewPathProgress Progress { get; } = new(path.Length);
        public bool HorizontalAdjustmentApplied { get; set; }
    }

    private enum BringIntoViewResult
    {
        NotFound = 0,
        Pending = 1,
        Visible = 2
    }

    private static readonly DispatcherPriority[] BringIntoViewRetryPriorities =
    [
        DispatcherPriority.Render,
        DispatcherPriority.Loaded,
        DispatcherPriority.Background,
        DispatcherPriority.Background
    ];

    private static readonly TimeSpan SearchDebounceDelay = UiTimingProfile.Scale(TimeSpan.FromMilliseconds(500));
    private readonly object _searchCtsLock = new();
    private CancellationTokenSource? _searchDebounceCts;
    private CancellationTokenSource? _searchCts;
    private readonly List<int> _pendingImmediateNavigationSteps = [];
    private TaskCompletionSource<NavigationResult>? _immediateNavigationCompletion;
    private string? _immediateNavigationQuery;
    private TreeNodeViewModel? _immediateNavigationRoot;
    private int _immediateNavigationVersion;
    private bool _immediateNavigationActive;
    private readonly TreeDescriptorSearchSession _descriptorSearch = new();
    private int[] _searchMatches = [];
    private readonly Dictionary<int, TreeNodeViewModel> _resolvedSearchNodes = [];
    private readonly HashSet<TreeNodeViewModel> _activeHighlightNodes = [];
    private readonly HashSet<TreeNodeViewModel> _nextHighlightNodes = [];
    private readonly HashSet<TreeNodeViewModel> _searchExpandedNodes = [];
    private readonly HashSet<TreeNodeViewModel> _nextSearchExpandedNodes = [];
    private readonly HashSet<TreeNodeViewModel> _searchSelfMatchedNodes = [];
    private readonly HashSet<TreeNodeViewModel> _searchLazyChildrenSnapshots = [];
    private readonly Dictionary<int, int> _searchMarkerMatchIndices = [];
    private readonly List<TreeNodeViewModel> _highlightAddedNodes = [];
    private readonly List<TreeNodeViewModel> _highlightRemovedNodes = [];
    private readonly object _highlightCtsLock = new();
    private CancellationTokenSource? _highlightApplyCts;
    private int _searchMatchIndex = -1;
    private TreeNodeViewModel? _currentSearchMatch;
    private TreeNodeViewModel? _searchRetainedSelectionNode;
    private TreeNodeViewModel? _searchRoot;
    private TreeDescriptorSearchIndex? _currentSearchIndex;
    private string? _activeHighlightQuery;
    private string? _lastComputedQuery;
    private int _searchVersion;
    private int _bringIntoViewVersion;
    private int _searchExpansionEpoch;
    private int _searchBranchReleaseVersion;
    private bool _searchExpansionStateInitialized;
    private bool _searchLazyChildrenSnapshotInitialized;
    private bool _searchBranchReleasePending;
    private bool _autoExpandAllMatches;
    private bool _treeAutoScrollSuppressed;
    private bool _restoreTreeAutoScroll;
    internal int LastBringIntoViewAttemptCount { get; private set; }
    private const int HighlightBatchSize = 256;
    private const int ProgressiveMaterializationMatchThreshold = 48;
    private const int MaterializationBatchSize = 32;
    private const int SearchGlobalHighlightMatchCap = 3500;
    private static readonly TimeSpan DispatcherWorkSlice =
        TimeSpan.FromMilliseconds(6);

    internal event EventHandler<PreviewMarkersChangedEventArgs>? SearchMarkersChanged;

    internal PreviewMarkerSnapshot SearchMarkerSnapshot { get; private set; } =
        PreviewMarkerSnapshot.Empty;

    // Cached brushes to avoid creating new objects for each node
    private IBrush? _cachedHighlightBackground;
    private IBrush? _cachedHighlightForeground;
    private IBrush? _cachedNormalForeground;
    private IBrush? _cachedCurrentBackground;
    private ThemeVariant? _cachedTheme;

    private async Task RunSearchDebounceAsync(int version, CancellationToken debounceToken)
    {
        try
        {
            await Task.Delay(SearchDebounceDelay, debounceToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        CancellationToken applyToken;
        lock (_searchCtsLock)
        {
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = new CancellationTokenSource();
            applyToken = _searchCts.Token;
        }

        await RunSearchAsync(version, applyToken).ConfigureAwait(false);
    }

    public void OnSearchQueryChanged()
    {
        viewModel.SetSearchInProgress(!string.IsNullOrWhiteSpace(viewModel.SearchQuery));
        Interlocked.Increment(ref _bringIntoViewVersion);

        CancellationToken token;
        int version;
        lock (_searchCtsLock)
        {
            _searchDebounceCts?.Cancel();
            _searchDebounceCts?.Dispose();
            _searchCts?.Cancel();
            CompleteImmediateNavigationLocked();
            _searchDebounceCts = new CancellationTokenSource();
            token = _searchDebounceCts.Token;
            version = Interlocked.Increment(ref _searchVersion);
        }

        _ = RunSearchDebounceAsync(version, token);
    }

    /// <summary>
    /// Cancels any pending debounced search update.
    /// </summary>
    public void CancelPending()
    {
        Interlocked.Increment(ref _searchVersion);
        Interlocked.Increment(ref _bringIntoViewVersion);
        lock (_searchCtsLock)
        {
            _searchDebounceCts?.Cancel();
            _searchCts?.Cancel();
            CompleteImmediateNavigationLocked();
        }

        CancelPendingHighlightApply();
    }

    private void CancelPendingHighlightApply()
    {
        lock (_highlightCtsLock)
        {
            _highlightApplyCts?.Cancel();
            _highlightApplyCts?.Dispose();
            _highlightApplyCts = null;
        }
    }

    public void UpdateSearchMatches(bool normalizeTreeWhenEmptyQuery = true)
    {
        var stopwatch = Stopwatch.StartNew();
        viewModel.SetSearchInProgress(false);
        Interlocked.Increment(ref _bringIntoViewVersion);

        lock (_searchCtsLock)
        {
            _searchDebounceCts?.Cancel();
            _searchCts?.Cancel();
            CompleteImmediateNavigationLocked();
        }

        var query = viewModel.SearchQuery ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            if (!normalizeTreeWhenEmptyQuery)
            {
                _searchMatches = [];
                _searchMatchIndex = -1;
                UpdateCurrentSearchMatch(null);
                PublishSearchMarkers();
                UpdateSearchMatchSummary();
                ClearHighlightsIfNeeded();
                _searchExpandedNodes.Clear();
                _nextSearchExpandedNodes.Clear();
                _searchSelfMatchedNodes.Clear();
                _searchExpansionStateInitialized = false;
                _lastComputedQuery = null;
                return;
            }

            ApplySearchResultCore(query, searchResult: null);
            metricsSink?.RecordTreeSearch(new TreeSearchMetrics(
                query,
                stopwatch.Elapsed,
                TotalNodes: 0,
                MatchCount: 0,
                UsedCache: false));
            return;
        }

        var root = viewModel.TreeNodes.FirstOrDefault();
        if (root is null)
        {
            ApplySearchResultCore(query, searchResult: null);
            return;
        }

        var searchResult = _descriptorSearch.Search(
            root.Descriptor,
            root.DisplayName,
            query,
            CancellationToken.None);
        ApplySearchResultCore(query, searchResult);
        metricsSink?.RecordTreeSearch(new TreeSearchMetrics(
            query,
            stopwatch.Elapsed,
            searchResult.Index.Count,
            searchResult.MatchIndices.Length,
            searchResult.UsedCache));
    }

    public Task UpdateSearchMatchesAsync(bool normalizeTreeWhenEmptyQuery = true)
    {
        if (string.IsNullOrWhiteSpace(viewModel.SearchQuery))
        {
            UpdateSearchMatches(normalizeTreeWhenEmptyQuery);
            return Task.CompletedTask;
        }

        CancellationToken token;
        int version;
        lock (_searchCtsLock)
        {
            _searchDebounceCts?.Cancel();
            _searchDebounceCts?.Dispose();
            _searchDebounceCts = null;
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            CompleteImmediateNavigationLocked();

            _searchCts = new CancellationTokenSource();
            token = _searchCts.Token;
            version = Interlocked.Increment(ref _searchVersion);
            Interlocked.Increment(ref _bringIntoViewVersion);
        }

        viewModel.SetSearchInProgress(true);
        return RunSearchAsync(version, token);
    }

    public bool HasMatches => _searchMatches.Length > 0;

    public bool HasAppliedSearchState =>
        _lastComputedQuery is not null ||
        _currentSearchIndex is not null ||
        _searchExpansionStateInitialized ||
        _activeHighlightNodes.Count > 0;

    public void UpdateHighlights(string? query)
    {
        var (highlightBackground, highlightForeground, normalForeground, currentBackground) = GetSearchHighlightBrushes();
        TreeNodeViewModel.ForEachRealizedDescendant(viewModel.TreeNodes, node =>
            node.UpdateSearchHighlight(query, highlightBackground, highlightForeground, normalForeground, currentBackground));
    }

    public int ApplyFilterPresentation(string query)
    {
        var (highlightBackground, highlightForeground, normalForeground, currentBackground) = GetSearchHighlightBrushes();
        using var _ = TreeNodeViewModel.BeginPreserveDescendantExpansionStateScope();
        var result = TreeSearchEngine.ApplyFilterPresentation(
            viewModel.TreeNodes,
            query,
            node => node.DisplayName,
            node => node.Children,
            (node, isMatch) => node.UpdateSearchHighlight(
                isMatch ? query : null,
                highlightBackground,
                highlightForeground,
                normalForeground,
                currentBackground),
            (node, expanded) => node.IsExpanded = expanded);

        return result.MatchCount;
    }

    public void ClearSearchState(bool preservePendingHighlightCleanup = false)
    {
        Interlocked.Increment(ref _bringIntoViewVersion);
        if (!preservePendingHighlightCleanup)
            CancelPendingHighlightApply();
        viewModel.SetSearchInProgress(false);

        // Clear current match reference first
        _currentSearchMatch = null;
        _searchMatchIndex = -1;
        ClearActiveHighlights();
        _activeHighlightNodes.TrimExcess();

        // Clear and trim the matches list
        _searchMatches = [];
        _nextHighlightNodes.Clear();
        _nextHighlightNodes.TrimExcess();
        _resolvedSearchNodes.Clear();
        _resolvedSearchNodes.TrimExcess();
        if (!_searchBranchReleasePending)
            ReleaseSearchMaterializedBranches(finalize: true);
        _descriptorSearch.Clear();
        _currentSearchIndex = null;
        _searchRoot = null;
        _searchExpandedNodes.Clear();
        _searchExpandedNodes.TrimExcess();
        _nextSearchExpandedNodes.Clear();
        _nextSearchExpandedNodes.TrimExcess();
        _searchSelfMatchedNodes.Clear();
        _searchSelfMatchedNodes.TrimExcess();
        _highlightAddedNodes.Clear();
        _highlightAddedNodes.TrimExcess();
        _highlightRemovedNodes.Clear();
        _highlightRemovedNodes.TrimExcess();
        _searchExpansionStateInitialized = false;
        _lastComputedQuery = null;
        _searchExpansionEpoch = 0;
        PublishSearchMarkers();
        UpdateSearchMatchSummary();

        // Note: Don't call UpdateHighlights here - nodes may already be cleared
    }

    public async Task CompleteSearchCloseAsync()
    {
        if (!_searchBranchReleasePending)
            return;

        var releaseVersion = Volatile.Read(ref _searchBranchReleaseVersion);
        await treeView.Dispatcher.InvokeAsync(
            static () => { },
            DispatcherPriority.Render);
        await treeView.Dispatcher.InvokeAsync(
            static () => { },
            DispatcherPriority.Background);

        if (!_searchBranchReleasePending ||
            releaseVersion != Volatile.Read(ref _searchBranchReleaseVersion))
        {
            return;
        }

        ReleaseSearchMaterializedBranches(finalize: true);
        _searchBranchReleasePending = false;
    }

    public void Dispose()
    {
        Interlocked.Increment(ref _bringIntoViewVersion);
        RestoreTreeAutoScroll();
        CancelPendingHighlightApply();
        viewModel.SetSearchInProgress(false);
        lock (_searchCtsLock)
        {
            _searchDebounceCts?.Cancel();
            _searchDebounceCts?.Dispose();
            _searchDebounceCts = null;
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = null;
            CompleteImmediateNavigationLocked();
        }

        // Clear search state to release references
        _searchMatches = [];
        _activeHighlightNodes.Clear();
        _nextHighlightNodes.Clear();
        _resolvedSearchNodes.Clear();
        Interlocked.Increment(ref _searchBranchReleaseVersion);
        _searchBranchReleasePending = false;
        ReleaseSearchMaterializedBranches(finalize: true);
        _descriptorSearch.Clear();
        _searchExpandedNodes.Clear();
        _nextSearchExpandedNodes.Clear();
        _searchSelfMatchedNodes.Clear();
        _highlightAddedNodes.Clear();
        _highlightRemovedNodes.Clear();
        _currentSearchMatch = null;
        _searchRetainedSelectionNode = null;
        _currentSearchIndex = null;
        _searchRoot = null;
        _activeHighlightQuery = null;
        _lastComputedQuery = null;
        PublishSearchMarkers();
        UpdateSearchMatchSummary();

        // Clear cached brushes
        _cachedHighlightBackground = null;
        _cachedHighlightForeground = null;
        _cachedNormalForeground = null;
        _cachedCurrentBackground = null;
    }

    public void Navigate(int step)
    {
        if (_searchMatches.Length == 0)
            return;

        _searchMatchIndex = (_searchMatchIndex + step + _searchMatches.Length) % _searchMatches.Length;
        SelectSearchMatch(adjustHorizontalOffset: true);
        metricsSink?.RecordTreeSearchNavigation(step, _searchMatchIndex + 1, _searchMatches.Length);
    }

    internal bool TryNavigateToSearchMarker(PreviewMarkerTarget target)
    {
        if (target.Category != PreviewMarkerCategory.Search ||
            !_searchMarkerMatchIndices.TryGetValue(target.LineNumber, out var matchIndex) ||
            matchIndex < 0 ||
            matchIndex >= _searchMatches.Length)
        {
            return false;
        }

        _searchMatchIndex = matchIndex;
        SelectSearchMatch(adjustHorizontalOffset: true);
        return true;
    }

    public bool TryNavigateForCurrentQuery(int step)
    {
        var query = viewModel.SearchQuery ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var hasComputedCurrentQuery =
            !viewModel.IsSearchInProgress &&
            string.Equals(_lastComputedQuery, query, StringComparison.OrdinalIgnoreCase);
        if (hasComputedCurrentQuery)
        {
            if (_searchMatches.Length == 0)
                return false;

            Navigate(step);
            return true;
        }

        var root = viewModel.TreeNodes.FirstOrDefault();
        if (root is null)
            return false;

        StartOrJoinImmediateNavigationSearch(query, root, step);
        return true;
    }

    internal Task<NavigationResult> TryNavigateForCurrentQueryAsync(int step)
    {
        var query = viewModel.SearchQuery ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult(NavigationResult.Canceled);

        var hasComputedCurrentQuery =
            !viewModel.IsSearchInProgress &&
            string.Equals(_lastComputedQuery, query, StringComparison.OrdinalIgnoreCase);
        if (hasComputedCurrentQuery)
        {
            if (_searchMatches.Length == 0)
                return Task.FromResult(NavigationResult.NoMatches);

            Navigate(step);
            return Task.FromResult(NavigationResult.Navigated);
        }

        var root = viewModel.TreeNodes.FirstOrDefault();
        return root is null
            ? Task.FromResult(NavigationResult.NoMatches)
            : StartOrJoinImmediateNavigationSearch(query, root, step);
    }

    internal Task WaitForImmediateNavigationAsync()
    {
        lock (_searchCtsLock)
            return _immediateNavigationCompletion?.Task ?? Task.CompletedTask;
    }

    private Task<NavigationResult> StartOrJoinImmediateNavigationSearch(
        string query,
        TreeNodeViewModel root,
        int step)
    {
        CancellationToken token;
        int version;
        Task<NavigationResult> completion;
        lock (_searchCtsLock)
        {
            if (_immediateNavigationActive &&
                !_searchCts!.IsCancellationRequested &&
                string.Equals(_immediateNavigationQuery, query, StringComparison.OrdinalIgnoreCase) &&
                ReferenceEquals(_immediateNavigationRoot, root))
            {
                _pendingImmediateNavigationSteps.Add(step);
                return _immediateNavigationCompletion!.Task;
            }

            _searchDebounceCts?.Cancel();
            _searchDebounceCts?.Dispose();
            _searchDebounceCts = null;
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            CompleteImmediateNavigationLocked();

            _searchCts = new CancellationTokenSource();
            token = _searchCts.Token;
            version = Interlocked.Increment(ref _searchVersion);
            Interlocked.Increment(ref _bringIntoViewVersion);
            _immediateNavigationVersion = version;
            _immediateNavigationQuery = query;
            _immediateNavigationRoot = root;
            _pendingImmediateNavigationSteps.Add(step);
            _immediateNavigationCompletion = new TaskCompletionSource<NavigationResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _immediateNavigationActive = true;
            completion = _immediateNavigationCompletion.Task;
        }

        viewModel.SetSearchInProgress(true);
        _ = RunSearchAsync(version, token);
        return completion;
    }

    private NavigationResult ApplyPendingImmediateNavigation(
        int version,
        string query,
        TreeNodeViewModel? root)
    {
        int[] steps;
        lock (_searchCtsLock)
        {
            if (!_immediateNavigationActive ||
                version != _immediateNavigationVersion ||
                !string.Equals(_immediateNavigationQuery, query, StringComparison.OrdinalIgnoreCase) ||
                !ReferenceEquals(_immediateNavigationRoot, root))
            {
                return NavigationResult.Canceled;
            }

            steps = [.. _pendingImmediateNavigationSteps];
            _pendingImmediateNavigationSteps.Clear();
            _immediateNavigationActive = false;
            _immediateNavigationQuery = null;
            _immediateNavigationRoot = null;
        }

        if (_searchMatches.Length == 0 || steps.Length == 0)
        {
            return steps.Length == 0
                ? NavigationResult.Canceled
                : NavigationResult.NoMatches;
        }

        for (var index = 0; index < steps.Length; index++)
        {
            var step = steps[index];
            if (index > 0 || step < 0)
            {
                Navigate(step);
                continue;
            }

            // A fresh forward request lands on the first result selected by result
            // application; subsequent queued requests advance normally.
            if (_currentSearchMatch is not null)
                BringNodeIntoView(_currentSearchMatch, adjustHorizontalOffset: true);
        }

        return NavigationResult.Navigated;
    }

    private void CompleteImmediateNavigationLocked(
        NavigationResult result = NavigationResult.Canceled)
    {
        _immediateNavigationActive = false;
        _immediateNavigationQuery = null;
        _immediateNavigationRoot = null;
        _pendingImmediateNavigationSteps.Clear();
        _immediateNavigationCompletion?.TrySetResult(result);
        _immediateNavigationCompletion = null;
    }

    public void RefreshThemeHighlights()
    {
        UpdateHighlights(viewModel.SearchQuery);
    }

    private void SelectSearchMatch(bool adjustHorizontalOffset)
    {
        if (_searchMatchIndex < 0 || _searchMatchIndex >= _searchMatches.Length)
        {
            UpdateSearchMatchSummary();
            return;
        }

        var node = ResolveSearchNode(_searchMatches[_searchMatchIndex]);
        if (node is null)
        {
            UpdateSearchMatchSummary();
            return;
        }

        SuppressTreeAutoScroll();
        try
        {
            if (!_autoExpandAllMatches)
                ApplySmartExpandFromMatches([node]);

            node.EnsureParentsExpanded();
            SelectTreeNode(node);
            UpdateCurrentSearchMatch(node);
            UpdateSearchMatchSummary();
            BringNodeIntoView(node, adjustHorizontalOffset);
            treeView.Focus();
        }
        catch
        {
            RestoreTreeAutoScroll();
            throw;
        }
    }

    private async Task RunSearchAsync(int version, CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        TreeNodeViewModel? root = null;
        var immediateNavigationResult = NavigationResult.Canceled;
        try
        {
            string query = string.Empty;
            TreeNodeDescriptor? rootDescriptor = null;
            string rootDisplayName = string.Empty;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || version != Volatile.Read(ref _searchVersion))
                    return;

                query = viewModel.SearchQuery ?? string.Empty;
                root = viewModel.TreeNodes.FirstOrDefault();
                rootDescriptor = root?.Descriptor;
                rootDisplayName = root?.DisplayName ?? string.Empty;
            }, DispatcherPriority.Background);

            if (token.IsCancellationRequested || version != Volatile.Read(ref _searchVersion))
                return;

            if (string.IsNullOrWhiteSpace(query))
            {
                await Dispatcher.UIThread.InvokeAsync(
                    () =>
                    {
                        if (!CanApplySearchResult(
                                token,
                                version,
                                Volatile.Read(ref _searchVersion),
                                root,
                                viewModel.TreeNodes.FirstOrDefault()))
                        {
                            return;
                        }

                        ApplySearchResultCore(query, searchResult: null);
                        immediateNavigationResult =
                            ApplyPendingImmediateNavigation(version, query, root);
                        metricsSink?.RecordTreeSearch(new TreeSearchMetrics(
                            query,
                            stopwatch.Elapsed,
                            TotalNodes: 0,
                            MatchCount: 0,
                            UsedCache: false));
                    },
                    DispatcherPriority.Background);
                return;
            }

            if (root is null || rootDescriptor is null)
                return;

            var searchResult = await Task.Run(
                () => _descriptorSearch.Search(
                    rootDescriptor,
                    rootDisplayName,
                    query,
                    token),
                token).ConfigureAwait(false);
            if (searchResult.MatchIndices.Length >=
                ProgressiveMaterializationMatchThreshold)
            {
                await ApplySearchResultProgressivelyAsync(
                    query,
                    searchResult,
                    version,
                    root,
                    token);
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!CanApplySearchResult(
                            token,
                            version,
                            Volatile.Read(ref _searchVersion),
                            root,
                            viewModel.TreeNodes.FirstOrDefault()))
                    {
                        return;
                    }

                    ApplySearchResultCore(query, searchResult);
                }, DispatcherPriority.Background);
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!CanApplySearchResult(
                        token,
                        version,
                        Volatile.Read(ref _searchVersion),
                        root,
                        viewModel.TreeNodes.FirstOrDefault()))
                {
                    return;
                }

                immediateNavigationResult =
                    ApplyPendingImmediateNavigation(version, query, root);
                metricsSink?.RecordTreeSearch(new TreeSearchMetrics(
                    query,
                    stopwatch.Elapsed,
                    searchResult.Index.Count,
                    searchResult.MatchIndices.Length,
                    searchResult.UsedCache));
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
            // Debounced/canceled search updates are expected.
        }
        finally
        {
            if (!token.IsCancellationRequested && version == Volatile.Read(ref _searchVersion))
            {
                await Dispatcher.UIThread.InvokeAsync(
                    () =>
                    {
                        if (!CanApplySearchResult(
                                token,
                                version,
                                Volatile.Read(ref _searchVersion),
                                root,
                                viewModel.TreeNodes.FirstOrDefault()))
                        {
                            return;
                        }

                        viewModel.SetSearchInProgress(false);
                    },
                    DispatcherPriority.Background);
            }

            lock (_searchCtsLock)
            {
                if (_immediateNavigationVersion == version)
                    CompleteImmediateNavigationLocked(immediateNavigationResult);
            }
        }
    }

    internal static bool CanApplySearchResult(
        CancellationToken token,
        int requestVersion,
        int currentVersion,
        TreeNodeViewModel? capturedRoot,
        TreeNodeViewModel? currentRoot) =>
        !token.IsCancellationRequested &&
        requestVersion == currentVersion &&
        ReferenceEquals(capturedRoot, currentRoot);

    private void ApplySearchResultCore(
        string query,
        TreeDescriptorSearchResult? searchResult,
        bool resolveAllMatches = true)
    {
        Interlocked.Increment(ref _bringIntoViewVersion);
        RestoreTreeAutoScroll();
        if (string.IsNullOrWhiteSpace(query))
        {
            var selectedNode = treeView.SelectedItem as TreeNodeViewModel;
            if (!IsAttachedToCurrentTree(selectedNode))
                selectedNode = null;

            _searchRetainedSelectionNode = selectedNode ??
                (IsAttachedToCurrentTree(_currentSearchMatch)
                    ? _currentSearchMatch
                    : null);
            if (!ReferenceEquals(
                    treeView.SelectedItem,
                    _searchRetainedSelectionNode))
            {
                treeView.SelectedItem = _searchRetainedSelectionNode;
            }
        }

        _searchMatches = [];
        _searchMatchIndex = -1;
        UpdateCurrentSearchMatch(null);

        if (string.IsNullOrWhiteSpace(query))
        {
            CancelPendingHighlightApply();
            ClearHighlightsIfNeeded();
            foreach (var node in viewModel.TreeNodes)
            {
                // When search is cleared, restore root visibility and collapse descendants.
                node.IsExpanded = true;
                CollapseAllExceptRoot(node);
            }
            PrepareSearchMaterializedBranchRelease();
            _searchExpandedNodes.Clear();
            _nextSearchExpandedNodes.Clear();
            _searchSelfMatchedNodes.Clear();
            _searchExpansionStateInitialized = false;
            _lastComputedQuery = null;
            _currentSearchIndex = null;
            _searchRoot = null;
            _resolvedSearchNodes.Clear();
            _descriptorSearch.Clear();
            _autoExpandAllMatches = false;
            PublishSearchMarkers();
            UpdateSearchMatchSummary();
            return;
        }

        if (searchResult is null || viewModel.TreeNodes.FirstOrDefault() is not { } root)
        {
            _autoExpandAllMatches = false;
            PublishSearchMarkers();
            UpdateSearchMatchSummary();
            return;
        }

        _currentSearchIndex = searchResult.Value.Index;
        _searchRoot = root;
        _resolvedSearchNodes.Clear();
        _resolvedSearchNodes[0] = root;
        CancelPendingSearchMaterializedBranchRelease();
        _searchRetainedSelectionNode = null;
        CaptureLazyChildrenSnapshot();
        _searchMatches = searchResult.Value.MatchIndices;
        _autoExpandAllMatches = resolveAllMatches;

        List<TreeNodeViewModel>? resolvedMatches = null;
        TreeNodeViewModel? firstResolvedMatch = null;
        if (resolveAllMatches)
        {
            resolvedMatches = ResolveSearchNodes(_searchMatches);
            firstResolvedMatch = resolvedMatches.FirstOrDefault();
            ApplySmartExpandFromMatches(resolvedMatches);
        }
        else
        {
            // Progressive searches publish one navigable path while the remaining
            // matches are materialized in dispatcher-sized batches.
            firstResolvedMatch = _searchMatches.Length > 0
                ? ResolveSearchNode(_searchMatches[0])
                : null;
            ApplySmartExpandFromMatches(
                firstResolvedMatch is null
                    ? Array.Empty<TreeNodeViewModel>()
                    : [firstResolvedMatch]);
        }

        if (_searchMatches.Length <= SearchGlobalHighlightMatchCap)
        {
            ApplySearchHighlightDiff(
                query,
                CollectRealizedSearchMatches(_searchMatches));
        }
        else
        {
            ApplySearchHighlightDiff(
                query,
                firstResolvedMatch is null
                    ? Array.Empty<TreeNodeViewModel>()
                    : [firstResolvedMatch]);
        }

        if (_searchMatches.Length > 0)
        {
            _searchMatchIndex = 0;
            SelectSearchMatch(adjustHorizontalOffset: false);
        }
        else
        {
            UpdateSearchMatchSummary();
        }

        _lastComputedQuery = query;
        PublishSearchMarkers();
    }

    private void ApplySmartExpandFromMatches(
        IReadOnlyList<TreeNodeViewModel> matches)
    {
        if (!_searchExpansionStateInitialized)
        {
            SeedExpandedNodesSnapshot();
            _searchExpansionStateInitialized = true;
        }

        _nextSearchExpandedNodes.Clear();
        _searchSelfMatchedNodes.Clear();

        var epoch = unchecked(++_searchExpansionEpoch);
        if (epoch == 0)
        {
            // Epoch overflow is practically unreachable, but keep semantics stable.
            _searchExpansionEpoch = 1;
            epoch = 1;
        }

        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            _searchSelfMatchedNodes.Add(match);
            match.MarkSearchSelfMatch(epoch);

            var ancestor = match.Parent;
            while (ancestor is not null)
            {
                ancestor.MarkSearchDescendantMatch(epoch);
                _nextSearchExpandedNodes.Add(ancestor);
                ancestor = ancestor.Parent;
            }
        }

        List<TreeNodeViewModel>? removedNodes = null;
        foreach (var node in _searchExpandedNodes)
        {
            if (_nextSearchExpandedNodes.Contains(node))
                continue;

            if (!_searchSelfMatchedNodes.Contains(node))
                (removedNodes ??= []).Add(node);
        }

        List<TreeNodeViewModel>? addedNodes = null;
        foreach (var node in _nextSearchExpandedNodes)
        {
            if (_searchExpandedNodes.Contains(node))
                continue;

            (addedNodes ??= []).Add(node);
        }

        ApplyExpansionDiff(
            removedNodes,
            addedNodes);
    }

    private void ApplyExpansionDiff(
        List<TreeNodeViewModel>? removedNodes,
        List<TreeNodeViewModel>? addedNodes)
    {
        var removedCount = removedNodes?.Count ?? 0;
        var addedCount = addedNodes?.Count ?? 0;
        if (removedCount == 0 && addedCount == 0)
            return;

        // Navigation must observe a settled hierarchy. Delayed expansion makes every
        // Next action reveal more rows and causes the scroll position to move underneath
        // the user, so apply the model diff before search is reported as complete.
        using (TreeNodeViewModel.BeginPreserveDescendantExpansionStateScope())
        {
            if (removedNodes is not null)
            {
                foreach (var node in removedNodes)
                {
                    node.IsExpanded = false;
                    _searchExpandedNodes.Remove(node);
                }
            }

            if (addedNodes is not null)
            {
                foreach (var node in addedNodes)
                {
                    node.IsExpanded = true;
                    _searchExpandedNodes.Add(node);
                }
            }
        }

    }

    private async Task ApplySearchResultProgressivelyAsync(
        string query,
        TreeDescriptorSearchResult searchResult,
        int version,
        TreeNodeViewModel root,
        CancellationToken token)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!CanApplySearchResult(
                    token,
                    version,
                    Volatile.Read(ref _searchVersion),
                    root,
                    viewModel.TreeNodes.FirstOrDefault()))
            {
                return;
            }

            ApplySearchResultCore(
                query,
                searchResult,
                resolveAllMatches: false);
        }, DispatcherPriority.Background);

        var resolvedMatches = new List<TreeNodeViewModel>(
            searchResult.MatchIndices.Length);
        var nextMatchIndex = 0;
        while (nextMatchIndex < searchResult.MatchIndices.Length)
        {
            token.ThrowIfCancellationRequested();
            var processedThrough = nextMatchIndex;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!CanApplySearchResult(
                        token,
                        version,
                        Volatile.Read(ref _searchVersion),
                        root,
                        viewModel.TreeNodes.FirstOrDefault()))
                {
                    return;
                }

                var sliceStarted = Stopwatch.GetTimestamp();
                var maximumIndex = Math.Min(
                    processedThrough + MaterializationBatchSize,
                    searchResult.MatchIndices.Length);
                while (processedThrough < maximumIndex)
                {
                    if (ResolveSearchNode(
                            searchResult.MatchIndices[processedThrough]) is { } node)
                    {
                        resolvedMatches.Add(node);
                    }

                    processedThrough++;
                    if (processedThrough < maximumIndex &&
                        Stopwatch.GetElapsedTime(sliceStarted) >= DispatcherWorkSlice)
                    {
                        break;
                    }
                }
            }, DispatcherPriority.Background);

            if (processedThrough == nextMatchIndex)
                return;

            nextMatchIndex = processedThrough;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!CanApplySearchResult(
                    token,
                    version,
                    Volatile.Read(ref _searchVersion),
                    root,
                    viewModel.TreeNodes.FirstOrDefault()))
            {
                return;
            }

            _autoExpandAllMatches = true;
            ApplySmartExpandFromMatches(resolvedMatches);
            var highlightedMatches =
                _searchMatches.Length <= SearchGlobalHighlightMatchCap
                    ? CollectRealizedSearchMatches(_searchMatches)
                    : resolvedMatches.Count == 0
                        ? []
                        : [resolvedMatches[0]];
            ApplySearchHighlightDiff(
                query,
                highlightedMatches);
            PublishSearchMarkers();
        }, DispatcherPriority.Background);
    }

    private void PublishSearchMarkers()
    {
        _searchMarkerMatchIndices.Clear();
        if (_searchMatches.Length == 0 || _currentSearchIndex is null)
        {
            SetSearchMarkerSnapshot(PreviewMarkerSnapshot.Empty);
            return;
        }

        var matchesByDescriptor = new Dictionary<TreeNodeDescriptor, int>(
            _searchMatches.Length,
            ReferenceEqualityComparer.Instance);
        for (var matchIndex = 0; matchIndex < _searchMatches.Length; matchIndex++)
        {
            var entryIndex = _searchMatches[matchIndex];
            if ((uint)entryIndex < (uint)_currentSearchIndex.Count)
            {
                matchesByDescriptor[_currentSearchIndex[entryIndex].Descriptor] =
                    matchIndex;
            }
        }

        var markers = new List<PreviewMarkerSource>(
            Math.Min(_searchMatches.Length, 4096));
        var visibleLineNumber = 0;
        var stack = new Stack<TreeNodeViewModel>();
        for (var rootIndex = viewModel.TreeNodes.Count - 1; rootIndex >= 0; rootIndex--)
            stack.Push(viewModel.TreeNodes[rootIndex]);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            visibleLineNumber++;
            if (matchesByDescriptor.TryGetValue(node.Descriptor, out var matchIndex))
            {
                markers.Add(new PreviewMarkerSource(
                    visibleLineNumber,
                    PreviewMarkerCategory.Search));
                _searchMarkerMatchIndices[visibleLineNumber] = matchIndex;
            }

            if (!node.IsExpanded || !node.AreChildrenRealized)
                continue;

            var children = node.Children;
            for (var childIndex = children.Count - 1; childIndex >= 0; childIndex--)
                stack.Push(children[childIndex]);
        }

        SetSearchMarkerSnapshot(markers.Count == 0
            ? PreviewMarkerSnapshot.Empty
            : new PreviewMarkerSnapshot(visibleLineNumber, [.. markers]));
    }

    private void SetSearchMarkerSnapshot(PreviewMarkerSnapshot snapshot)
    {
        SearchMarkerSnapshot = snapshot;
        SearchMarkersChanged?.Invoke(
            this,
            new PreviewMarkersChangedEventArgs(snapshot));
    }

    private void SeedExpandedNodesSnapshot()
    {
        _searchExpandedNodes.Clear();
        TreeNodeViewModel.ForEachRealizedDescendant(viewModel.TreeNodes, node =>
        {
            if (node.HasChildren && node.IsExpanded)
                _searchExpandedNodes.Add(node);
        });
    }

    private List<TreeNodeViewModel> ResolveSearchNodes(IReadOnlyList<int> entryIndices)
    {
        var resolved = new List<TreeNodeViewModel>(entryIndices.Count);
        for (var index = 0; index < entryIndices.Count; index++)
        {
            if (ResolveSearchNode(entryIndices[index]) is { } node)
                resolved.Add(node);
        }

        return resolved;
    }

    private void CaptureLazyChildrenSnapshot()
    {
        if (_searchLazyChildrenSnapshotInitialized)
            return;

        _searchLazyChildrenSnapshots.Clear();
        TreeNodeViewModel.ForEachRealizedDescendant(
            viewModel.TreeNodes,
            node =>
            {
                if (node.HasChildren && !node.AreChildrenRealized)
                    _searchLazyChildrenSnapshots.Add(node);
            });
        _searchLazyChildrenSnapshotInitialized = true;
    }

    private void PrepareSearchMaterializedBranchRelease()
    {
        _searchBranchReleasePending = true;
        Interlocked.Increment(ref _searchBranchReleaseVersion);
        ReleaseSearchMaterializedBranches(finalize: false);
    }

    private void CancelPendingSearchMaterializedBranchRelease()
    {
        if (!_searchBranchReleasePending)
            return;

        Interlocked.Increment(ref _searchBranchReleaseVersion);
        _searchBranchReleasePending = false;
        ReleaseSearchMaterializedBranches(finalize: true);
    }

    private void ReleaseSearchMaterializedBranches(bool finalize)
    {
        if (!_searchLazyChildrenSnapshotInitialized)
            return;

        var preservedSelection =
            IsAttachedToCurrentTree(_searchRetainedSelectionNode)
                ? _searchRetainedSelectionNode
                : treeView.SelectedItem as TreeNodeViewModel;
        if (!IsAttachedToCurrentTree(preservedSelection))
            preservedSelection = null;

        foreach (var node in _searchLazyChildrenSnapshots)
            node.TryReleaseChildrenToLazyState(preservedSelection);

        if (finalize)
        {
            _searchLazyChildrenSnapshots.Clear();
            _searchLazyChildrenSnapshots.TrimExcess();
            _searchLazyChildrenSnapshotInitialized = false;
            _searchRetainedSelectionNode = null;
        }
    }

    private List<TreeNodeViewModel> CollectRealizedSearchMatches(
        IReadOnlyList<int> entryIndices)
    {
        if (_currentSearchIndex is null || entryIndices.Count == 0)
            return [];

        var matchingDescriptors = new HashSet<TreeNodeDescriptor>(
            ReferenceEqualityComparer.Instance);
        for (var index = 0; index < entryIndices.Count; index++)
        {
            var entryIndex = entryIndices[index];
            if (entryIndex >= 0 && entryIndex < _currentSearchIndex.Count)
                matchingDescriptors.Add(_currentSearchIndex[entryIndex].Descriptor);
        }

        var realizedMatches = new List<TreeNodeViewModel>(
            Math.Min(entryIndices.Count, 256));
        TreeNodeViewModel.ForEachRealizedDescendant(
            viewModel.TreeNodes,
            node =>
            {
                if (matchingDescriptors.Contains(node.Descriptor))
                    realizedMatches.Add(node);
            });
        return realizedMatches;
    }

    private TreeNodeViewModel? ResolveSearchNode(int entryIndex)
    {
        if (_currentSearchIndex is null ||
            _searchRoot is null ||
            entryIndex < 0 ||
            entryIndex >= _currentSearchIndex.Count)
        {
            return null;
        }

        if (_resolvedSearchNodes.TryGetValue(entryIndex, out var resolved))
            return resolved;

        var unresolvedPath = new Stack<int>();
        var currentIndex = entryIndex;
        while (!_resolvedSearchNodes.TryGetValue(currentIndex, out resolved))
        {
            unresolvedPath.Push(currentIndex);
            currentIndex = _currentSearchIndex[currentIndex].ParentIndex;
            if (currentIndex < 0)
                return null;
        }

        while (unresolvedPath.Count > 0)
        {
            var childIndex = unresolvedPath.Pop();
            var childEntry = _currentSearchIndex[childIndex];
            var child = FindChild(
                resolved,
                childEntry.Descriptor,
                childEntry.ChildIndex);
            if (child is null)
                return null;

            resolved = child;
            _resolvedSearchNodes[childIndex] = child;
        }

        return resolved;
    }

    private TreeNodeViewModel? FindChild(
        TreeNodeViewModel parent,
        TreeNodeDescriptor childDescriptor,
        int childIndex)
    {
        TrackSearchMaterializedParent(parent);
        var children = parent.Children;
        if ((uint)childIndex < (uint)children.Count)
        {
            var indexedChild = children[childIndex];
            if (ReferenceEquals(indexedChild.Descriptor, childDescriptor) ||
                PathComparer.Default.Equals(
                    indexedChild.FullPath,
                    childDescriptor.FullPath))
            {
                return indexedChild;
            }
        }

        // A fallback keeps search correct if a future projection no longer preserves
        // descriptor order in its view-model child collection.
        foreach (var child in children)
        {
            if (ReferenceEquals(child.Descriptor, childDescriptor) ||
                PathComparer.Default.Equals(child.FullPath, childDescriptor.FullPath))
            {
                return child;
            }
        }

        return null;
    }

    private void TrackSearchMaterializedParent(TreeNodeViewModel parent)
    {
        if (_searchLazyChildrenSnapshotInitialized &&
            parent.HasChildren &&
            !parent.AreChildrenRealized)
        {
            _searchLazyChildrenSnapshots.Add(parent);
        }
    }

    private bool IsAttachedToCurrentTree(TreeNodeViewModel? node)
    {
        if (node is null)
            return false;

        while (node.Parent is not null)
            node = node.Parent;

        for (var index = 0; index < viewModel.TreeNodes.Count; index++)
        {
            if (ReferenceEquals(viewModel.TreeNodes[index], node))
                return true;
        }

        return false;
    }

    private void BringNodeIntoView(
        TreeNodeViewModel node,
        bool adjustHorizontalOffset)
    {
        var version = Interlocked.Increment(ref _bringIntoViewVersion);
        var request = new BringIntoViewRequest(
            node,
            BuildNavigationPath(node),
            version,
            adjustHorizontalOffset,
            GetTreeScrollViewer()?.Offset.X);
        LastBringIntoViewAttemptCount = 0;
        TryBringNodeIntoViewWithRetries(request);
    }

    private void SelectTreeNode(TreeNodeViewModel node)
    {
        treeView.SelectedItem = node;
        node.IsSelected = true;
    }

    private void UpdateCurrentSearchMatch(TreeNodeViewModel? node)
    {
        if (ReferenceEquals(_currentSearchMatch, node))
            return;

        var query = viewModel.SearchQuery;
        var (highlightBackground, highlightForeground, normalForeground, currentBackground) = GetSearchHighlightBrushes();

        if (_currentSearchMatch is not null)
        {
            _currentSearchMatch.IsCurrentSearchMatch = false;
            _currentSearchMatch.UpdateSearchHighlight(
                query,
                highlightBackground,
                highlightForeground,
                normalForeground,
                currentBackground);
        }

        _currentSearchMatch = node;

        if (_currentSearchMatch is not null)
        {
            _currentSearchMatch.IsCurrentSearchMatch = true;
            _currentSearchMatch.UpdateSearchHighlight(
                query,
                highlightBackground,
                highlightForeground,
                normalForeground,
                currentBackground);
        }
    }

    private void UpdateSearchMatchSummary()
    {
        var currentIndex = _searchMatchIndex >= 0 && _searchMatchIndex < _searchMatches.Length
            ? _searchMatchIndex + 1
            : 0;
        viewModel.UpdateSearchMatchSummary(currentIndex, _searchMatches.Length);
    }

    private void CollapseAllExceptRoot(TreeNodeViewModel node)
        => node.CollapseRealizedDescendants();

    private void ClearHighlightsIfNeeded()
    {
        if (_activeHighlightNodes.Count > 0)
        {
            ClearActiveHighlights();
            return;
        }

        var (highlightBackground, highlightForeground, normalForeground, currentBackground) = GetSearchHighlightBrushes();

        TreeNodeViewModel.ForEachRealizedDescendant(viewModel.TreeNodes, node =>
        {
            if (!node.HasHighlightedDisplay && !node.IsCurrentSearchMatch)
                return;

            node.UpdateSearchHighlight(null, highlightBackground, highlightForeground, normalForeground, currentBackground);
        });
    }

    private void ApplySearchHighlightDiff(
        string query,
        IReadOnlyList<TreeNodeViewModel> matches)
    {
        var (highlightBackground, highlightForeground, normalForeground, currentBackground) = GetSearchHighlightBrushes();
        _nextHighlightNodes.Clear();
        for (var index = 0; index < matches.Count; index++)
            _nextHighlightNodes.Add(matches[index]);

        var queryChanged = !string.Equals(_activeHighlightQuery, query, StringComparison.Ordinal);
        _highlightRemovedNodes.Clear();
        _highlightAddedNodes.Clear();

        foreach (var node in _activeHighlightNodes)
        {
            if (_nextHighlightNodes.Contains(node))
                continue;

            _highlightRemovedNodes.Add(node);
        }

        foreach (var node in _nextHighlightNodes)
        {
            if (queryChanged || !_activeHighlightNodes.Contains(node))
                _highlightAddedNodes.Add(node);
        }

        _activeHighlightNodes.Clear();
        foreach (var node in _nextHighlightNodes)
            _activeHighlightNodes.Add(node);

        _activeHighlightQuery = query;

        ScheduleHighlightDiffApplication(
            query,
            _highlightRemovedNodes.ToArray(),
            _highlightAddedNodes.ToArray(),
            highlightBackground,
            highlightForeground,
            normalForeground,
            currentBackground);
    }

    private void ClearActiveHighlights()
    {
        if (_activeHighlightNodes.Count == 0)
            return;

        var (highlightBackground, highlightForeground, normalForeground, currentBackground) = GetSearchHighlightBrushes();
        var nodes = _activeHighlightNodes.ToArray();

        _activeHighlightNodes.Clear();
        _activeHighlightQuery = null;

        ScheduleHighlightDiffApplication(
            query: null,
            removedNodes: nodes,
            addedNodes: Array.Empty<TreeNodeViewModel>(),
            highlightBackground,
            highlightForeground,
            normalForeground,
            currentBackground);
    }

    private void ScheduleHighlightDiffApplication(
        string? query,
        TreeNodeViewModel[] removedNodes,
        TreeNodeViewModel[] addedNodes,
        IBrush? highlightBackground,
        IBrush? highlightForeground,
        IBrush? normalForeground,
        IBrush? currentBackground)
    {
        // Apply highlight mutations in small UI batches so very large trees stay responsive
        // while keeping the same final visual state.
        CancellationToken token;
        lock (_highlightCtsLock)
        {
            _highlightApplyCts?.Cancel();
            _highlightApplyCts?.Dispose();
            _highlightApplyCts = new CancellationTokenSource();
            token = _highlightApplyCts.Token;
        }

        void ApplyRemovedBatch(int startIndex)
        {
            if (token.IsCancellationRequested)
                return;

            var endIndex = Math.Min(startIndex + HighlightBatchSize, removedNodes.Length);
            for (var i = startIndex; i < endIndex; i++)
            {
                var node = removedNodes[i];
                node.IsCurrentSearchMatch = false;
                node.UpdateSearchHighlight(null, highlightBackground, highlightForeground, normalForeground, currentBackground);
            }

            if (endIndex < removedNodes.Length)
            {
                treeView.Dispatcher.Post(() => ApplyRemovedBatch(endIndex), DispatcherPriority.Background);
                return;
            }

            ApplyAddedBatch(0);
        }

        void ApplyAddedBatch(int startIndex)
        {
            if (token.IsCancellationRequested)
                return;

            var endIndex = Math.Min(startIndex + HighlightBatchSize, addedNodes.Length);
            for (var i = startIndex; i < endIndex; i++)
                addedNodes[i].UpdateSearchHighlight(query, highlightBackground, highlightForeground, normalForeground, currentBackground);

            if (endIndex < addedNodes.Length)
                treeView.Dispatcher.Post(() => ApplyAddedBatch(endIndex), DispatcherPriority.Background);
        }

        ApplyRemovedBatch(0);
    }

    private void TryBringNodeIntoViewWithRetries(BringIntoViewRequest request)
    {
        if (request.Version != Volatile.Read(ref _bringIntoViewVersion))
            return;

        var result = TryBringNodeIntoView(request, out var deepestRealizedSegment);
        var shouldRetry = request.Progress.Observe(deepestRealizedSegment);
        LastBringIntoViewAttemptCount = request.Progress.TotalAttempts;
        if (result == BringIntoViewResult.Visible || !shouldRetry)
        {
            RestoreTreeAutoScroll();
            return;
        }

        ScheduleCapturedHorizontalOffsetRestore(request);
        var priority = BringIntoViewRetryPriorities[
            Math.Min(
                request.Progress.NoProgressAttempts,
                BringIntoViewRetryPriorities.Length - 1)];
        treeView.Dispatcher.Post(
            () =>
            {
                if (request.Version != Volatile.Read(ref _bringIntoViewVersion))
                    return;

                RestoreCapturedHorizontalOffset(request);
                TryBringNodeIntoViewWithRetries(request);
            },
            priority);
    }

    private BringIntoViewResult TryBringNodeIntoView(
        BringIntoViewRequest request,
        out int deepestRealizedSegment)
    {
        if (TryGetContainer(request.Node, out var directContainer) &&
            directContainer is not null)
        {
            deepestRealizedSegment = request.Path.Length - 1;
            if (!BringContainerIntoViewForSearchNavigation(
                    directContainer,
                    request.AdjustHorizontalOffset,
                    request.OriginalHorizontalOffset))
            {
                RestoreCapturedHorizontalOffset(request);
                return BringIntoViewResult.Pending;
            }

            request.HorizontalAdjustmentApplied =
                request.AdjustHorizontalOffset;
            if (!IsContainerPositionedForSearchNavigation(
                    directContainer,
                    request.AdjustHorizontalOffset))
                return BringIntoViewResult.Pending;

            return !request.AdjustHorizontalOffset ||
                   IsHorizontalTargetVisibleInViewport(directContainer)
                ? BringIntoViewResult.Visible
                : BringIntoViewResult.Pending;
        }

        deepestRealizedSegment = FindDeepestRealizedPathSegment(
            request.Path,
            request.Progress.DeepestRealizedSegment);
        if (deepestRealizedSegment >= 0 &&
            deepestRealizedSegment < request.Path.Length - 1 &&
            TryGetContainer(
                request.Path[deepestRealizedSegment],
                out var ancestorContainer) &&
            ancestorContainer is not null)
        {
            ancestorContainer.ScrollIntoView(
                request.Path[deepestRealizedSegment + 1]);
            RestoreCapturedHorizontalOffset(request);
            return BringIntoViewResult.Pending;
        }

        if (request.Path.Length > 0)
        {
            treeView.ScrollIntoView(request.Path[0]);
            RestoreCapturedHorizontalOffset(request);
            return BringIntoViewResult.Pending;
        }

        return BringIntoViewResult.NotFound;
    }

    private int FindDeepestRealizedPathSegment(
        IReadOnlyList<TreeNodeViewModel> path,
        int previouslyRealizedSegment)
    {
        var deepest = Math.Max(-1, previouslyRealizedSegment);
        for (var index = Math.Max(0, deepest); index < path.Count; index++)
        {
            if (!TryGetContainer(path[index], out _))
                break;

            deepest = index;
        }

        return deepest;
    }

    private void ScheduleCapturedHorizontalOffsetRestore(
        BringIntoViewRequest request)
    {
        if (request.OriginalHorizontalOffset is null)
            return;

        treeView.Dispatcher.Post(
            () =>
            {
                if (request.Version != Volatile.Read(ref _bringIntoViewVersion) ||
                    request.HorizontalAdjustmentApplied)
                {
                    return;
                }

                RestoreCapturedHorizontalOffset(request);
            },
            DispatcherPriority.Render);
    }

    private void RestoreCapturedHorizontalOffset(BringIntoViewRequest request)
    {
        if (request.OriginalHorizontalOffset is not { } originalOffset ||
            request.HorizontalAdjustmentApplied ||
            GetTreeScrollViewer() is not { } scrollViewer)
        {
            return;
        }

        var restoredOffsetX = ResolveClampedTreeHorizontalOffset(
            originalOffset,
            scrollViewer.Extent.Width,
            scrollViewer.Viewport.Width);
        var currentOffset = scrollViewer.Offset;
        if (Math.Abs(currentOffset.X - restoredOffsetX) >= 0.5)
            scrollViewer.Offset = new Vector(restoredOffsetX, currentOffset.Y);
    }

    private void SuppressTreeAutoScroll()
    {
        if (_treeAutoScrollSuppressed)
            return;

        _treeAutoScrollSuppressed = true;
        _restoreTreeAutoScroll = treeView.AutoScrollToSelectedItem;
        if (_restoreTreeAutoScroll)
            treeView.AutoScrollToSelectedItem = false;
    }

    private void RestoreTreeAutoScroll()
    {
        if (!_treeAutoScrollSuppressed)
            return;

        if (_restoreTreeAutoScroll)
            treeView.AutoScrollToSelectedItem = true;

        _treeAutoScrollSuppressed = false;
        _restoreTreeAutoScroll = false;
    }

    internal static TreeNodeViewModel[] BuildNavigationPath(
        TreeNodeViewModel node)
    {
        var depth = 1;
        var ancestor = node.Parent;
        while (ancestor is not null)
        {
            depth++;
            ancestor = ancestor.Parent;
        }

        var path = new TreeNodeViewModel[depth];
        var current = node;
        for (var index = depth - 1; index >= 0; index--)
        {
            path[index] = current;
            current = current.Parent!;
        }

        return path;
    }

    private bool BringContainerIntoViewForSearchNavigation(
        TreeViewItem container,
        bool adjustHorizontalOffset,
        double? originalHorizontalOffset)
    {
        var scrollViewer = GetTreeScrollViewer();
        if (scrollViewer is null)
        {
            container.BringIntoView();
            return true;
        }

        var navigationTarget =
            ResolveTreeItemNavigationTarget(container);
        var navigationTopLeft =
            navigationTarget.TranslatePoint(default, scrollViewer);
        if (navigationTopLeft is null ||
            navigationTarget.Bounds.Height <= 0)
        {
            return false;
        }

        var currentOffset = scrollViewer.Offset;
        var targetY = adjustHorizontalOffset
            ? ResolveComfortableVerticalOffsetForSearchNavigation(
                currentOffset.Y,
                navigationTopLeft.Value.Y,
                navigationTopLeft.Value.Y +
                navigationTarget.Bounds.Height,
                scrollViewer.Viewport.Height,
                scrollViewer.Extent.Height)
            : ResolveVerticalOffsetForSearchNavigation(
                currentOffset.Y,
                navigationTopLeft.Value.Y,
                navigationTopLeft.Value.Y +
                navigationTarget.Bounds.Height,
                scrollViewer.Viewport.Height,
                scrollViewer.Extent.Height);
        var baselineOffsetX = ResolveClampedTreeHorizontalOffset(
            originalHorizontalOffset ?? currentOffset.X,
            scrollViewer.Extent.Width,
            scrollViewer.Viewport.Width);
        var targetX = baselineOffsetX;
        if (adjustHorizontalOffset)
        {
            // TranslatePoint observes the current scroll position, which may have been
            // changed by TreeView while realizing a lazy path. Convert it back to the
            // captured navigation baseline before deciding whether X must move.
            var itemLeftAtBaselineOffset =
                currentOffset.X +
                navigationTopLeft.Value.X -
                baselineOffsetX;
            targetX = ResolveHorizontalOffsetForSearchNavigation(
                baselineOffsetX,
                itemLeftAtBaselineOffset,
                itemLeftAtBaselineOffset +
                navigationTarget.Bounds.Width,
                scrollViewer.Viewport.Width,
                scrollViewer.Extent.Width,
                navigationTarget.Bounds.Width);
        }

        if (Math.Abs(targetX - currentOffset.X) < 0.5 && Math.Abs(targetY - currentOffset.Y) < 0.5)
            return true;

        scrollViewer.Offset = new Vector(targetX, targetY);
        return true;
    }

    private static Control ResolveTreeItemNavigationTarget(
        TreeViewItem container)
    {
        // An expanded TreeViewItem includes its descendants in Bounds. Measuring the
        // data-template content keeps both axes anchored to the node's own header row.
        return container.FindDescendantOfType<Control>(
            includeSelf: false,
            visual =>
                visual is Control
                {
                    Name: "TreeItemContent"
                } content &&
                ReferenceEquals(
                    content.FindAncestorOfType<TreeViewItem>(),
                    container)) ??
               container;
    }

    private bool TryGetContainer(TreeNodeViewModel node, out TreeViewItem? container)
    {
        if (treeView.TreeContainerFromItem(node) is TreeViewItem directContainer)
        {
            container = directContainer;
            return true;
        }

        container = null;
        return false;
    }

    private bool IsContainerPositionedForSearchNavigation(
        TreeViewItem container,
        bool preferComfortZone)
    {
        var scrollViewer = GetTreeScrollViewer();
        if (scrollViewer is null)
            return true;

        var target = ResolveTreeItemNavigationTarget(container);
        var topLeft = target.TranslatePoint(default, scrollViewer);
        if (topLeft is null || target.Bounds.Height <= 0)
            return false;

        var top = topLeft.Value.Y;
        var bottom = top + target.Bounds.Height;
        var viewportHeight = scrollViewer.Viewport.Height;
        if (viewportHeight <= 0)
            return false;

        const double tolerance = 1.0;
        var fullyVisible = target.Bounds.Height > viewportHeight
            ? top <= tolerance && bottom >= viewportHeight - tolerance
            : top >= -tolerance && bottom <= viewportHeight + tolerance;
        if (!fullyVisible)
            return false;

        var currentOffsetY = scrollViewer.Offset.Y;
        var settledOffsetY = preferComfortZone
            ? ResolveComfortableVerticalOffsetForSearchNavigation(
                currentOffsetY,
                top,
                bottom,
                viewportHeight,
                scrollViewer.Extent.Height)
            : ResolveVerticalOffsetForSearchNavigation(
                currentOffsetY,
                top,
                bottom,
                viewportHeight,
                scrollViewer.Extent.Height);
        return Math.Abs(settledOffsetY - currentOffsetY) < 0.5;
    }

    private bool IsHorizontalTargetVisibleInViewport(TreeViewItem container)
    {
        var scrollViewer = GetTreeScrollViewer();
        if (scrollViewer is null)
            return true;

        var target = ResolveTreeItemNavigationTarget(container);
        var topLeft = target.TranslatePoint(default, scrollViewer);
        var viewportWidth = scrollViewer.Viewport.Width;
        if (topLeft is null || viewportWidth <= 0)
            return false;

        const double tolerance = 1.0;
        var left = topLeft.Value.X;
        if (target.Bounds.Width > viewportWidth)
            return left >= -tolerance && left <= tolerance;

        return left >= -tolerance &&
               left + target.Bounds.Width <= viewportWidth + tolerance;
    }

    private ScrollViewer? GetTreeScrollViewer() =>
        treeView.FindDescendantOfType<ScrollViewer>(
            includeSelf: false,
            visual => visual is ScrollViewer);

    internal static double ResolveClampedTreeHorizontalOffset(
        double preservedOffsetX,
        double extentWidth,
        double viewportWidth)
    {
        var maxX = Math.Max(0, extentWidth - viewportWidth);
        return Math.Clamp(preservedOffsetX, 0, maxX);
    }

    internal static double ResolveHorizontalOffsetForSearchNavigation(
        double baselineOffsetX,
        double itemLeft,
        double itemRight,
        double viewportWidth,
        double extentWidth,
        double itemWidth)
    {
        const double tolerance = 1.0;
        if (viewportWidth <= 0 ||
            itemLeft >= -tolerance &&
            itemRight <= viewportWidth + tolerance)
        {
            return ResolveClampedTreeHorizontalOffset(
                baselineOffsetX,
                extentWidth,
                viewportWidth);
        }

        // When a label itself is wider than the viewport, aligning its start is more
        // predictable than jumping to an arbitrary suffix on every navigation step.
        var comfortPadding = itemWidth >= viewportWidth
            ? 0
            : Math.Min(12, Math.Max(0, (viewportWidth - itemWidth) / 2));
        var targetX = itemWidth > viewportWidth
            ? baselineOffsetX + itemLeft
            : itemLeft < 0
                ? baselineOffsetX + itemLeft - comfortPadding
                : baselineOffsetX + itemRight - viewportWidth + comfortPadding;

        return ResolveClampedTreeHorizontalOffset(
            targetX,
            extentWidth,
            viewportWidth);
    }

    internal static double ResolveVerticalOffsetForSearchNavigation(
        double currentOffsetY,
        double itemTop,
        double itemBottom,
        double viewportHeight,
        double extentHeight)
    {
        // Avalonia BringIntoView can adjust both axes too eagerly. Search navigation uses
        // explicit offsets so horizontal scrolling happens only when the target is clipped.
        if (viewportHeight <= 0)
            return currentOffsetY;

        var maxY = Math.Max(0, extentHeight - viewportHeight);
        var itemHeight = Math.Max(0, itemBottom - itemTop);
        if (itemHeight >= viewportHeight)
            return Math.Clamp(currentOffsetY + itemTop, 0, maxY);

        if (itemTop >= 0 && itemBottom <= viewportHeight)
            return currentOffsetY;

        var targetY = itemTop < 0
            ? currentOffsetY + itemTop
            : currentOffsetY + itemBottom - viewportHeight;
        return Math.Clamp(targetY, 0, maxY);
    }

    internal static double ResolveComfortableVerticalOffsetForSearchNavigation(
        double currentOffsetY,
        double itemTop,
        double itemBottom,
        double viewportHeight,
        double extentHeight)
    {
        if (viewportHeight <= 0)
            return currentOffsetY;

        var itemHeight = Math.Max(0, itemBottom - itemTop);
        var maxY = Math.Max(0, extentHeight - viewportHeight);
        if (itemHeight >= viewportHeight)
            return Math.Clamp(currentOffsetY + itemTop, 0, maxY);

        const double comfortInsetRatio = 0.2;
        var maximumInset = Math.Max(0, (viewportHeight - itemHeight) / 2);
        var comfortInset = Math.Min(
            viewportHeight * comfortInsetRatio,
            maximumInset);
        var comfortTop = comfortInset;
        var comfortBottom = viewportHeight - comfortInset;
        if (itemTop >= comfortTop && itemBottom <= comfortBottom)
            return currentOffsetY;

        // A directional anchor near the center provides hysteresis: adjacent results
        // can move inside the comfort band without scrolling on every navigation step.
        var preferredCenter = viewportHeight *
                              (itemTop < comfortTop ? 0.45 : 0.55);
        var halfItemHeight = itemHeight / 2;
        var minimumCenter = comfortTop + halfItemHeight;
        var maximumCenter = comfortBottom - halfItemHeight;
        var anchoredCenter = Math.Clamp(
            preferredCenter,
            minimumCenter,
            maximumCenter);
        var itemCenter = (itemTop + itemBottom) / 2;
        var targetY = currentOffsetY + itemCenter - anchoredCenter;
        return Math.Clamp(targetY, 0, maxY);
    }


    private (IBrush highlightBackground, IBrush highlightForeground, IBrush normalForeground, IBrush currentBackground)
        GetSearchHighlightBrushes()
    {
        var canReadAvaloniaResources = Dispatcher.UIThread.CheckAccess();
        var app = canReadAvaloniaResources
            ? global::Avalonia.Application.Current
            : null;
        var theme = ResolveSearchHighlightTheme(app, canReadAvaloniaResources);

        if (_cachedTheme == theme &&
            _cachedHighlightBackground is not null &&
            _cachedHighlightForeground is not null &&
            _cachedNormalForeground is not null &&
            _cachedCurrentBackground is not null)
        {
            return (_cachedHighlightBackground, _cachedHighlightForeground, _cachedNormalForeground, _cachedCurrentBackground);
        }

        _cachedTheme = theme;

        _cachedHighlightBackground = new SolidColorBrush(Color.Parse("#FFEB3B"));
        _cachedHighlightForeground = new SolidColorBrush(Color.Parse("#000000"));
        _cachedNormalForeground = theme == ThemeVariant.Dark
            ? new SolidColorBrush(Color.Parse("#E7E9EF"))
            : new SolidColorBrush(Color.Parse("#1A1A1A"));
        _cachedCurrentBackground = new SolidColorBrush(Color.Parse("#F9A825"));

        if (app is not null)
            ApplySearchHighlightResourceOverrides(app, theme);

        return (_cachedHighlightBackground, _cachedHighlightForeground, _cachedNormalForeground, _cachedCurrentBackground);
    }

    private ThemeVariant ResolveSearchHighlightTheme(
        global::Avalonia.Application? app,
        bool canReadAvaloniaResources)
    {
        // Avalonia Application and resource dictionaries are dispatcher-owned. Worker-thread
        // searches must not touch them, otherwise test order and debounce timing make this flaky.
        if (!canReadAvaloniaResources)
            return _cachedTheme ?? ThemeVariant.Light;

        return app?.ActualThemeVariant ?? ThemeVariant.Light;
    }

    private void ApplySearchHighlightResourceOverrides(global::Avalonia.Application app, ThemeVariant theme)
    {
        if (app.TryFindResource("TreeSearchHighlightBrush", theme, out var bg) &&
            bg is IBrush bgBrush)
            _cachedHighlightBackground = bgBrush;

        if (app.TryFindResource("TreeSearchHighlightTextBrush", theme, out var fg) &&
            fg is IBrush fgBrush)
            _cachedHighlightForeground = fgBrush;

        if (app.TryFindResource("TreeSearchCurrentBrush", theme, out var current) &&
            current is IBrush currentBrush)
            _cachedCurrentBackground = currentBrush;

        if (app.TryFindResource("AppTextBrush", theme, out var textFg) && textFg is IBrush textBrush)
            _cachedNormalForeground = textBrush;
    }
}
