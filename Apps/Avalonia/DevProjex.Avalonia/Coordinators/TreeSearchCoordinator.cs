namespace DevProjex.Avalonia.Coordinators;

public sealed class TreeSearchCoordinator(
    MainWindowViewModel viewModel,
    TreeView treeView,
    Action? onSearchApplied = null,
    ITreeSearchMetricsSink? metricsSink = null)
    : IDisposable
{
    private enum BringIntoViewResult
    {
        NotFound = 0,
        Pending = 1,
        Visible = 2
    }

    private const int MaxBringIntoViewAttempts = 6;
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
    private readonly List<TreeNodeViewModel> _searchMatches = [];
    private readonly HashSet<TreeNodeViewModel> _activeHighlightNodes = [];
    private readonly HashSet<TreeNodeViewModel> _nextHighlightNodes = [];
    private readonly HashSet<TreeNodeViewModel> _searchExpandedNodes = [];
    private readonly HashSet<TreeNodeViewModel> _nextSearchExpandedNodes = [];
    private readonly HashSet<TreeNodeViewModel> _searchSelfMatchedNodes = [];
    private readonly List<TreeNodeViewModel> _highlightAddedNodes = [];
    private readonly List<TreeNodeViewModel> _highlightRemovedNodes = [];
    private readonly List<TreeNodeViewModel> _flatNodeIndex = [];
    private readonly List<TreeNodeViewModel> _lastComputedMatches = [];
    private readonly Dictionary<string, List<TreeNodeViewModel>> _queryMatchesCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _queryMatchesCacheLru = [];
    private readonly Dictionary<string, LinkedListNode<string>> _queryMatchesCacheNodes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _highlightCtsLock = new();
    private readonly object _expansionCtsLock = new();
    private CancellationTokenSource? _highlightApplyCts;
    private CancellationTokenSource? _expansionApplyCts;
    private int _searchMatchIndex = -1;
    private TreeNodeViewModel? _currentSearchMatch;
    private string? _activeHighlightQuery;
    private string? _lastComputedQuery;
    private TreeNodeViewModel? _indexedFirstRoot;
    private int _indexedRootCount;
    private int _searchVersion;
    private int _bringIntoViewVersion;
    private int _searchExpansionEpoch;
    private bool _searchExpansionStateInitialized;
    private const int SearchQueryCacheLimit = 8;
    private const int MaxCachedMatchCount = 4096;
    private const int HighlightBatchSize = 256;
    private const int ExpansionBatchSize = 192;
    private const int ExpansionBatchThreshold = 256;
    private const int SearchAutoExpandMatchCap = 2500;
    private const int SearchGlobalHighlightMatchCap = 3500;

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

        CancellationToken token;
        int version;
        lock (_searchCtsLock)
        {
            _searchDebounceCts?.Cancel();
            _searchDebounceCts?.Dispose();
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
        lock (_searchCtsLock)
        {
            _searchDebounceCts?.Cancel();
            _searchCts?.Cancel();
        }

        CancelPendingHighlightApply();
        CancelPendingExpansionApply();
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

    private void CancelPendingExpansionApply()
    {
        lock (_expansionCtsLock)
        {
            _expansionApplyCts?.Cancel();
            _expansionApplyCts?.Dispose();
            _expansionApplyCts = null;
        }
    }

    public void UpdateSearchMatches(bool normalizeTreeWhenEmptyQuery = true)
    {
        var stopwatch = Stopwatch.StartNew();
        viewModel.SetSearchInProgress(false);

        lock (_searchCtsLock)
        {
            _searchDebounceCts?.Cancel();
            _searchCts?.Cancel();
        }

        var query = viewModel.SearchQuery ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            CancelPendingExpansionApply();
            if (!normalizeTreeWhenEmptyQuery)
            {
                _searchMatches.Clear();
                _searchMatchIndex = -1;
                UpdateCurrentSearchMatch(null);
                UpdateSearchMatchSummary();
                ClearHighlightsIfNeeded();
                _searchExpandedNodes.Clear();
                _nextSearchExpandedNodes.Clear();
                _searchSelfMatchedNodes.Clear();
                _searchExpansionStateInitialized = false;
                _lastComputedQuery = null;
                _lastComputedMatches.Clear();
                return;
            }

            ApplySearchResultCore(query, Array.Empty<TreeNodeViewModel>());
            metricsSink?.RecordTreeSearch(new TreeSearchMetrics(
                query,
                stopwatch.Elapsed,
                TotalNodes: 0,
                MatchCount: 0,
                UsedCache: false));
            return;
        }

        EnsureSearchIndexCurrent();
        var source = CreateSearchSource(query);
        var usedCache = TryGetCachedMatches(query, out var cachedMatches);
        var matches = usedCache
            ? cachedMatches
            : CollectMatches(source, query);
        if (!ReferenceEquals(matches, cachedMatches))
            CacheMatches(query, matches);
        ApplySearchResultCore(query, matches);
        metricsSink?.RecordTreeSearch(new TreeSearchMetrics(
            query,
            stopwatch.Elapsed,
            source.Count,
            matches.Count,
            usedCache));
    }

    public bool HasMatches => _searchMatches.Count > 0;

    public void UpdateHighlights(string? query)
    {
        var (highlightBackground, highlightForeground, normalForeground, currentBackground) = GetSearchHighlightBrushes();
        TreeNodeViewModel.ForEachDescendant(viewModel.TreeNodes, node =>
            node.UpdateSearchHighlight(query, highlightBackground, highlightForeground, normalForeground, currentBackground));
    }

    public void ClearSearchState()
    {
        Interlocked.Increment(ref _bringIntoViewVersion);
        CancelPendingHighlightApply();
        CancelPendingExpansionApply();
        viewModel.SetSearchInProgress(false);

        // Clear current match reference first
        _currentSearchMatch = null;
        _searchMatchIndex = -1;
        ClearActiveHighlights();

        // Clear and trim the matches list
        _searchMatches.Clear();
        _searchMatches.TrimExcess();
        _nextHighlightNodes.Clear();
        _flatNodeIndex.Clear();
        _lastComputedMatches.Clear();
        _queryMatchesCache.Clear();
        _queryMatchesCacheLru.Clear();
        _queryMatchesCacheNodes.Clear();
        _searchExpandedNodes.Clear();
        _nextSearchExpandedNodes.Clear();
        _searchSelfMatchedNodes.Clear();
        _searchExpansionStateInitialized = false;
        _lastComputedQuery = null;
        _indexedFirstRoot = null;
        _indexedRootCount = 0;
        _searchExpansionEpoch = 0;
        UpdateSearchMatchSummary();

        // Note: Don't call UpdateHighlights here - nodes may already be cleared
    }

    public void Dispose()
    {
        Interlocked.Increment(ref _bringIntoViewVersion);
        CancelPendingHighlightApply();
        CancelPendingExpansionApply();
        viewModel.SetSearchInProgress(false);
        lock (_searchCtsLock)
        {
            _searchDebounceCts?.Cancel();
            _searchDebounceCts?.Dispose();
            _searchDebounceCts = null;
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = null;
        }

        // Clear search state to release references
        _searchMatches.Clear();
        _searchMatches.TrimExcess();
        _activeHighlightNodes.Clear();
        _nextHighlightNodes.Clear();
        _flatNodeIndex.Clear();
        _lastComputedMatches.Clear();
        _queryMatchesCache.Clear();
        _queryMatchesCacheLru.Clear();
        _queryMatchesCacheNodes.Clear();
        _searchExpandedNodes.Clear();
        _nextSearchExpandedNodes.Clear();
        _searchSelfMatchedNodes.Clear();
        _highlightAddedNodes.Clear();
        _highlightRemovedNodes.Clear();
        _currentSearchMatch = null;
        _activeHighlightQuery = null;
        _lastComputedQuery = null;
        _indexedFirstRoot = null;
        _indexedRootCount = 0;
        UpdateSearchMatchSummary();

        // Clear cached brushes
        _cachedHighlightBackground = null;
        _cachedHighlightForeground = null;
        _cachedNormalForeground = null;
        _cachedCurrentBackground = null;
    }

    public void Navigate(int step)
    {
        if (_searchMatches.Count == 0)
            return;

        _searchMatchIndex = (_searchMatchIndex + step + _searchMatches.Count) % _searchMatches.Count;
        SelectSearchMatch();
        metricsSink?.RecordTreeSearchNavigation(step, _searchMatchIndex + 1, _searchMatches.Count);
    }

    public bool TryNavigateForCurrentQuery(int step)
    {
        var query = viewModel.SearchQuery ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
            return false;

        var shouldRefreshMatches =
            viewModel.IsSearchInProgress ||
            _searchMatches.Count == 0 ||
            !string.Equals(_lastComputedQuery, query, StringComparison.OrdinalIgnoreCase);

        if (!shouldRefreshMatches)
        {
            Navigate(step);
            return true;
        }

        UpdateSearchMatches(normalizeTreeWhenEmptyQuery: false);
        if (_searchMatches.Count == 0)
            return false;

        // Forward activation should land on the first result after a query refresh
        // instead of skipping straight to the second match.
        if (step < 0)
            Navigate(step);

        return true;
    }

    public void RefreshThemeHighlights()
    {
        UpdateHighlights(viewModel.SearchQuery);
    }

    private void SelectSearchMatch()
    {
        if (_searchMatchIndex < 0 || _searchMatchIndex >= _searchMatches.Count)
        {
            UpdateSearchMatchSummary();
            return;
        }

        var node = _searchMatches[_searchMatchIndex];
        node.EnsureParentsExpanded();
        SelectTreeNode(node);
        UpdateCurrentSearchMatch(node);
        UpdateSearchMatchSummary();
        // Horizontal scrolling is disabled on the tree, so search navigation only needs to bring the
        // match into view vertically. The former horizontal offset capture/restore/re-clamp dance was
        // the source of the side-scroll jumpiness and is intentionally gone.
        BringNodeIntoView(node);
        treeView.Focus();
    }

    private async Task RunSearchAsync(int version, CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            string query = string.Empty;
            IReadOnlyList<TreeNodeViewModel>? sourceNodes = null;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || version != Volatile.Read(ref _searchVersion))
                    return;

                query = viewModel.SearchQuery ?? string.Empty;
                EnsureSearchIndexCurrent();
                sourceNodes = CreateSearchSource(query);
            }, DispatcherPriority.Background);

            if (token.IsCancellationRequested || version != Volatile.Read(ref _searchVersion))
                return;

            if (string.IsNullOrWhiteSpace(query))
            {
                await Dispatcher.UIThread.InvokeAsync(
                    () =>
                    {
                        ApplySearchResultCore(query, Array.Empty<TreeNodeViewModel>());
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

            List<TreeNodeViewModel> matches;
            var usedCache = false;
            if (TryGetCachedMatches(query, out var cachedMatches))
            {
                matches = cachedMatches;
                usedCache = true;
            }
            else
            {
                matches = CollectMatches(sourceNodes ?? Array.Empty<TreeNodeViewModel>(), query, token, version);
                CacheMatches(query, matches);
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || version != Volatile.Read(ref _searchVersion))
                    return;

                ApplySearchResultCore(query, matches);
                metricsSink?.RecordTreeSearch(new TreeSearchMetrics(
                    query,
                    stopwatch.Elapsed,
                    sourceNodes?.Count ?? 0,
                    matches.Count,
                    usedCache));
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
                    () => viewModel.SetSearchInProgress(false),
                    DispatcherPriority.Background);
            }
        }
    }

    private void ApplySearchResultCore(string query, IReadOnlyList<TreeNodeViewModel> matches)
    {
        _searchMatches.Clear();
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

            _searchExpandedNodes.Clear();
            _nextSearchExpandedNodes.Clear();
            _searchSelfMatchedNodes.Clear();
            _searchExpansionStateInitialized = false;
            _lastComputedQuery = null;
            _lastComputedMatches.Clear();
            UpdateSearchMatchSummary();
            return;
        }

        _searchMatches.AddRange(matches);
        if (_searchMatches.Count <= SearchAutoExpandMatchCap)
        {
            ApplySmartExpandFromMatches(_searchMatches);
        }
        else
        {
            // For massive match sets, skip global expand to prevent container explosion
            // and keep the tree responsive.
            _searchExpandedNodes.Clear();
            _nextSearchExpandedNodes.Clear();
            _searchSelfMatchedNodes.Clear();
            _searchExpansionStateInitialized = false;
        }

        if (_searchMatches.Count <= SearchGlobalHighlightMatchCap)
        {
            ApplySearchHighlightDiff(query);
        }
        else
        {
            // Avoid allocating and mutating highlights for thousands of nodes at once.
            // Current-match highlight remains active via SelectSearchMatch().
            ClearActiveHighlights();
        }

        if (_searchMatches.Count > 0)
        {
            _searchMatchIndex = 0;
            SelectSearchMatch();
        }
        else
        {
            UpdateSearchMatchSummary();
        }

        _lastComputedQuery = query;
        _lastComputedMatches.Clear();
        _lastComputedMatches.AddRange(_searchMatches);
        onSearchApplied?.Invoke();
    }

    private List<TreeNodeViewModel> CollectMatches(
        IReadOnlyList<TreeNodeViewModel> source,
        string query,
        CancellationToken cancellationToken,
        int version)
    {
        if (string.IsNullOrWhiteSpace(query) || source.Count == 0)
            return [];

        var matches = new List<TreeNodeViewModel>(capacity: Math.Min(source.Count, 1024));
        for (var i = 0; i < source.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested || version != Volatile.Read(ref _searchVersion))
                break;

            var node = source[i];
            if (node.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                matches.Add(node);
        }

        return matches;
    }

    private static List<TreeNodeViewModel> CollectMatches(
        IReadOnlyList<TreeNodeViewModel> source,
        string query)
    {
        if (string.IsNullOrWhiteSpace(query) || source.Count == 0)
            return [];

        var matches = new List<TreeNodeViewModel>(capacity: Math.Min(source.Count, 1024));
        for (var i = 0; i < source.Count; i++)
        {
            var node = source[i];
            if (node.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                matches.Add(node);
        }

        return matches;
    }

    private void ApplySmartExpandFromMatches(IReadOnlyList<TreeNodeViewModel> matches)
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
            addedNodes,
            matches.Count > 0 ? matches[0] : null);

        _searchExpandedNodes.Clear();
        foreach (var node in _nextSearchExpandedNodes)
            _searchExpandedNodes.Add(node);
    }

    private void ApplyExpansionDiff(
        List<TreeNodeViewModel>? removedNodes,
        List<TreeNodeViewModel>? addedNodes,
        TreeNodeViewModel? firstMatch)
    {
        var removedCount = removedNodes?.Count ?? 0;
        var addedCount = addedNodes?.Count ?? 0;
        if (removedCount == 0 && addedCount == 0)
        {
            CancelPendingExpansionApply();
            return;
        }

        if (removedCount + addedCount < ExpansionBatchThreshold)
        {
            CancelPendingExpansionApply();
            using var _ = TreeNodeViewModel.BeginPreserveDescendantExpansionStateScope();
            if (removedNodes is not null)
            {
                foreach (var node in removedNodes)
                    node.IsExpanded = false;
            }

            if (addedNodes is not null)
            {
                foreach (var node in addedNodes)
                    node.IsExpanded = true;
            }

            return;
        }

        if (firstMatch is not null)
        {
            // Keep the first selected match path expanded synchronously so selection and bring-into-view
            // stay responsive while the rest of a large expansion diff is applied in background batches.
            using var _ = TreeNodeViewModel.BeginPreserveDescendantExpansionStateScope();
            firstMatch.EnsureParentsExpanded();
            if (addedNodes is not null)
                RemoveAncestorPathNodes(addedNodes, firstMatch);
        }

        ScheduleExpansionDiffApplication(
            removedNodes?.ToArray() ?? Array.Empty<TreeNodeViewModel>(),
            addedNodes?.ToArray() ?? Array.Empty<TreeNodeViewModel>());
    }

    private static void RemoveAncestorPathNodes(List<TreeNodeViewModel> addedNodes, TreeNodeViewModel firstMatch)
    {
        var ancestor = firstMatch.Parent;
        while (ancestor is not null)
        {
            addedNodes.Remove(ancestor);
            ancestor = ancestor.Parent;
        }
    }

    private void ScheduleExpansionDiffApplication(
        TreeNodeViewModel[] removedNodes,
        TreeNodeViewModel[] addedNodes)
    {
        CancellationToken token;
        lock (_expansionCtsLock)
        {
            _expansionApplyCts?.Cancel();
            _expansionApplyCts?.Dispose();
            _expansionApplyCts = new CancellationTokenSource();
            token = _expansionApplyCts.Token;
        }

        void ApplyRemovedBatch(int startIndex)
        {
            if (token.IsCancellationRequested)
                return;

            var endIndex = Math.Min(startIndex + ExpansionBatchSize, removedNodes.Length);
            using (TreeNodeViewModel.BeginPreserveDescendantExpansionStateScope())
            {
                for (var i = startIndex; i < endIndex; i++)
                    removedNodes[i].IsExpanded = false;
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

            var endIndex = Math.Min(startIndex + ExpansionBatchSize, addedNodes.Length);
            using (TreeNodeViewModel.BeginPreserveDescendantExpansionStateScope())
            {
                for (var i = startIndex; i < endIndex; i++)
                    addedNodes[i].IsExpanded = true;
            }

            if (endIndex < addedNodes.Length)
                treeView.Dispatcher.Post(() => ApplyAddedBatch(endIndex), DispatcherPriority.Background);
        }

        ApplyRemovedBatch(0);
    }

    private void SeedExpandedNodesSnapshot()
    {
        _searchExpandedNodes.Clear();
        TreeNodeViewModel.ForEachDescendant(viewModel.TreeNodes, node =>
        {
            if (node.Children.Count > 0 && node.IsExpanded)
                _searchExpandedNodes.Add(node);
        });
    }

    private void EnsureSearchIndexCurrent()
    {
        var currentRootCount = viewModel.TreeNodes.Count;
        var currentFirstRoot = currentRootCount > 0 ? viewModel.TreeNodes[0] : null;

        if (_indexedRootCount == currentRootCount && ReferenceEquals(_indexedFirstRoot, currentFirstRoot))
            return;

        _flatNodeIndex.Clear();
        TreeNodeViewModel.ForEachDescendant(viewModel.TreeNodes, node => _flatNodeIndex.Add(node));

        _indexedRootCount = currentRootCount;
        _indexedFirstRoot = currentFirstRoot;
        _queryMatchesCache.Clear();
        _queryMatchesCacheLru.Clear();
        _queryMatchesCacheNodes.Clear();
        _lastComputedQuery = null;
        _lastComputedMatches.Clear();
    }

    private IReadOnlyList<TreeNodeViewModel> CreateSearchSource(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<TreeNodeViewModel>();

        if (!string.IsNullOrWhiteSpace(_lastComputedQuery) &&
            query.StartsWith(_lastComputedQuery, StringComparison.OrdinalIgnoreCase) &&
            _lastComputedMatches.Count > 0)
        {
            return _lastComputedMatches;
        }

        if (TryGetBestCachedPrefixMatches(query, out var cachedPrefixMatches))
            return cachedPrefixMatches;

        return _flatNodeIndex;
    }

    private bool TryGetCachedMatches(string query, out List<TreeNodeViewModel> matches)
    {
        if (_queryMatchesCache.TryGetValue(query, out matches!))
        {
            if (_queryMatchesCacheNodes.TryGetValue(query, out var node))
            {
                _queryMatchesCacheLru.Remove(node);
                _queryMatchesCacheLru.AddFirst(node);
            }

            return true;
        }

        matches = null!;
        return false;
    }

    private bool TryGetBestCachedPrefixMatches(string query, out List<TreeNodeViewModel> matches)
    {
        matches = null!;
        string? bestPrefix = null;

        foreach (var cachedQuery in _queryMatchesCache.Keys)
        {
            if (string.IsNullOrWhiteSpace(cachedQuery))
                continue;

            if (!query.StartsWith(cachedQuery, StringComparison.OrdinalIgnoreCase))
                continue;

            if (bestPrefix is null || cachedQuery.Length > bestPrefix.Length)
                bestPrefix = cachedQuery;
        }

        if (bestPrefix is null)
            return false;

        return TryGetCachedMatches(bestPrefix, out matches);
    }

    private void CacheMatches(string query, List<TreeNodeViewModel> matches)
    {
        if (string.IsNullOrWhiteSpace(query))
            return;

        if (matches.Count > MaxCachedMatchCount)
            return;

        if (_queryMatchesCache.ContainsKey(query))
        {
            if (_queryMatchesCacheNodes.TryGetValue(query, out var existingNode))
            {
                _queryMatchesCacheLru.Remove(existingNode);
                _queryMatchesCacheLru.AddFirst(existingNode);
            }

            return;
        }

        _queryMatchesCache[query] = matches;
        var node = new LinkedListNode<string>(query);
        _queryMatchesCacheLru.AddFirst(node);
        _queryMatchesCacheNodes[query] = node;

        while (_queryMatchesCacheLru.Count > SearchQueryCacheLimit)
        {
            var oldestNode = _queryMatchesCacheLru.Last;
            if (oldestNode is null)
                break;

            _queryMatchesCacheLru.RemoveLast();
            _queryMatchesCacheNodes.Remove(oldestNode.Value);
            _queryMatchesCache.Remove(oldestNode.Value);
        }
    }

    private void BringNodeIntoView(TreeNodeViewModel node)
    {
        var version = Interlocked.Increment(ref _bringIntoViewVersion);
        TryBringNodeIntoViewWithRetries(node, version, attempt: 0);
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
        var currentIndex = _searchMatchIndex >= 0 && _searchMatchIndex < _searchMatches.Count
            ? _searchMatchIndex + 1
            : 0;
        viewModel.UpdateSearchMatchSummary(currentIndex, _searchMatches.Count);
    }

    private void CollapseAllExceptRoot(TreeNodeViewModel node)
    {
        foreach (var child in node.Children)
        {
            child.IsExpanded = false;
            CollapseAllExceptRoot(child);
        }
    }

    private void ClearHighlightsIfNeeded()
    {
        if (_activeHighlightNodes.Count > 0)
        {
            ClearActiveHighlights();
            return;
        }

        var (highlightBackground, highlightForeground, normalForeground, currentBackground) = GetSearchHighlightBrushes();

        TreeNodeViewModel.ForEachDescendant(viewModel.TreeNodes, node =>
        {
            if (!node.HasHighlightedDisplay && !node.IsCurrentSearchMatch)
                return;

            node.UpdateSearchHighlight(null, highlightBackground, highlightForeground, normalForeground, currentBackground);
        });
    }

    private void ApplySearchHighlightDiff(string query)
    {
        var (highlightBackground, highlightForeground, normalForeground, currentBackground) = GetSearchHighlightBrushes();
        _nextHighlightNodes.Clear();
        for (var i = 0; i < _searchMatches.Count; i++)
            _nextHighlightNodes.Add(_searchMatches[i]);

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

    private void TryBringNodeIntoViewWithRetries(TreeNodeViewModel node, int version, int attempt)
    {
        if (version != Volatile.Read(ref _bringIntoViewVersion))
            return;

        var result = TryBringNodeIntoView(node);
        if (result == BringIntoViewResult.Visible || attempt >= MaxBringIntoViewAttempts - 1)
            return;

        var priority = BringIntoViewRetryPriorities[Math.Min(attempt, BringIntoViewRetryPriorities.Length - 1)];
        treeView.Dispatcher.Post(
            () => TryBringNodeIntoViewWithRetries(node, version, attempt + 1),
            priority);
    }

    private BringIntoViewResult TryBringNodeIntoView(TreeNodeViewModel node)
    {
        if (TryGetContainer(node, out var directContainer) && directContainer is not null)
        {
            BringContainerIntoViewForSearchNavigation(directContainer);
            if (IsContainerVisibleInViewport(directContainer))
                return BringIntoViewResult.Visible;

            return IsContainerVisibleInViewport(directContainer)
                ? BringIntoViewResult.Visible
                : BringIntoViewResult.Pending;
        }

        // The target container may not be materialized yet under virtualization.
        // Scrolling to the nearest realized ancestor progressively materializes descendants.
        if (TryGetNearestRealizedAncestorContainer(node, out var ancestorContainer) && ancestorContainer is not null)
        {
            if (!IsContainerVisibleInViewport(ancestorContainer))
                BringContainerIntoViewForSearchNavigation(ancestorContainer);

            return BringIntoViewResult.Pending;
        }

        return BringIntoViewResult.NotFound;
    }

    private void BringContainerIntoViewForSearchNavigation(TreeViewItem container)
    {
        var scrollViewer = GetTreeScrollViewer();
        if (scrollViewer is null)
        {
            container.BringIntoView();
            return;
        }

        var containerTopLeft = container.TranslatePoint(default, scrollViewer);
        if (containerTopLeft is null)
            return;

        // Vertical-only bring-into-view. Horizontal scrolling is disabled on the tree, so the
        // horizontal offset is left untouched (it stays at 0) and is never read or re-clamped.
        var currentOffset = scrollViewer.Offset;
        var targetY = ResolveVerticalOffsetForSearchNavigation(
            currentOffset.Y,
            containerTopLeft.Value.Y,
            containerTopLeft.Value.Y + container.Bounds.Height,
            scrollViewer.Viewport.Height,
            scrollViewer.Extent.Height);

        if (Math.Abs(targetY - currentOffset.Y) < 0.5)
            return;

        scrollViewer.Offset = new Vector(currentOffset.X, targetY);
    }

    private bool TryGetContainer(TreeNodeViewModel node, out TreeViewItem? container)
    {
        if (treeView.ContainerFromItem(node) is TreeViewItem directContainer)
        {
            container = directContainer;
            return true;
        }

        container = treeView.FindDescendantOfType<TreeViewItem>(
            includeSelf: false,
            visual => ReferenceEquals(visual.DataContext, node));
        return container is not null;
    }

    private bool TryGetNearestRealizedAncestorContainer(TreeNodeViewModel node, out TreeViewItem? ancestorContainer)
    {
        var current = node.Parent;
        while (current is not null)
        {
            if (TryGetContainer(current, out ancestorContainer))
                return true;

            current = current.Parent;
        }

        ancestorContainer = null;
        return false;
    }

    private bool IsContainerVisibleInViewport(TreeViewItem container)
    {
        var scrollViewer = GetTreeScrollViewer();
        if (scrollViewer is null)
            return true;

        var topLeft = container.TranslatePoint(default, scrollViewer);
        if (topLeft is null)
            return true;

        var top = topLeft.Value.Y;
        var bottom = top + container.Bounds.Height;
        var viewportHeight = scrollViewer.Viewport.Height;

        const double tolerance = 1.0;
        return bottom >= -tolerance && top <= viewportHeight + tolerance;
    }

    private ScrollViewer? GetTreeScrollViewer() =>
        treeView.FindDescendantOfType<ScrollViewer>(
            includeSelf: false,
            visual => visual is ScrollViewer);

    // Horizontal scrolling is disabled on the tree, so search navigation only adjusts the vertical
    // offset. The former horizontal offset helpers were removed along with that logic.
    internal static double ResolveVerticalOffsetForSearchNavigation(
        double currentOffsetY,
        double itemTop,
        double itemBottom,
        double viewportHeight,
        double extentHeight)
    {
        // Avalonia BringIntoView can adjust both axes too eagerly. Search navigation uses
        // explicit offsets so horizontal scrolling happens only when the target is clipped.
        if (viewportHeight <= 0 || itemBottom >= 0 && itemTop <= viewportHeight)
            return currentOffsetY;

        var targetY = itemTop < 0
            ? currentOffsetY + itemTop
            : currentOffsetY + itemBottom - viewportHeight;

        var maxY = Math.Max(0, extentHeight - viewportHeight);
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
        if (app.Resources.TryGetResource("TreeSearchHighlightBrush", theme, out var bg) &&
            bg is IBrush bgBrush)
            _cachedHighlightBackground = bgBrush;

        if (app.Resources.TryGetResource("TreeSearchHighlightTextBrush", theme, out var fg) &&
            fg is IBrush fgBrush)
            _cachedHighlightForeground = fgBrush;

        if (app.Resources.TryGetResource("TreeSearchCurrentBrush", theme, out var current) &&
            current is IBrush currentBrush)
            _cachedCurrentBackground = currentBrush;

        if (app.Resources.TryGetResource("AppTextBrush", theme, out var textFg) && textFg is IBrush textBrush)
            _cachedNormalForeground = textBrush;
    }
}
