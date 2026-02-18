using System.Timers;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DevProjex.Application.Services;
using Timer = System.Timers.Timer;

namespace DevProjex.Avalonia.Coordinators;

public sealed class TreeSearchCoordinator : IDisposable
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

    private readonly MainWindowViewModel _viewModel;
    private readonly TreeView _treeView;
    private readonly Timer _searchDebounceTimer;
    private readonly object _searchCtsLock = new();
    private CancellationTokenSource? _searchCts;
    private readonly List<TreeNodeViewModel> _searchMatches = new();
    private readonly HashSet<TreeNodeViewModel> _activeHighlightNodes = new();
    private int _searchMatchIndex = -1;
    private TreeNodeViewModel? _currentSearchMatch;
    private string? _activeHighlightQuery;
    private int _bringIntoViewVersion;

    // Cached brushes to avoid creating new objects for each node
    private IBrush? _cachedHighlightBackground;
    private IBrush? _cachedHighlightForeground;
    private IBrush? _cachedNormalForeground;
    private IBrush? _cachedCurrentBackground;
    private ThemeVariant? _cachedTheme;

    public TreeSearchCoordinator(MainWindowViewModel viewModel, TreeView treeView)
    {
        _viewModel = viewModel;
        _treeView = treeView;
        _searchDebounceTimer = new Timer(120)
        {
            AutoReset = false
        };
        _searchDebounceTimer.Elapsed += OnSearchDebounceTimerElapsed;
    }

    private void OnSearchDebounceTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        CancellationToken token;
        lock (_searchCtsLock)
        {
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = new CancellationTokenSource();
            token = _searchCts.Token;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!token.IsCancellationRequested)
                UpdateSearchMatches();
        }, DispatcherPriority.Background);
    }

    public void OnSearchQueryChanged()
    {
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    /// <summary>
    /// Cancels any pending debounced search update.
    /// </summary>
    public void CancelPending()
    {
        _searchDebounceTimer.Stop();
        lock (_searchCtsLock)
        {
            _searchCts?.Cancel();
        }
    }

    public void UpdateSearchMatches()
    {
        _searchMatches.Clear();
        _searchMatchIndex = -1;
        UpdateCurrentSearchMatch(null);

        var query = _viewModel.SearchQuery;
        if (string.IsNullOrWhiteSpace(query))
        {
            ClearHighlightsIfNeeded();
            foreach (var node in _viewModel.TreeNodes)
                CollapseAllExceptRoot(node);
            return;
        }

        _searchMatches.AddRange(TreeSearchEngine.CollectMatches(
            _viewModel.TreeNodes,
            query,
            node => node.DisplayName,
            node => node.Children,
            StringComparison.OrdinalIgnoreCase));
        ApplySearchHighlightDiff(query);

        TreeSearchEngine.ApplySmartExpandForSearch(
            _viewModel.TreeNodes,
            query,
            node => node.DisplayName,
            node => node.Children,
            node => node.Children.Count > 0,
            (node, expanded) => node.IsExpanded = expanded);

        if (_searchMatches.Count > 0)
        {
            _searchMatchIndex = 0;
            SelectSearchMatch();
        }
    }

    public bool HasMatches => _searchMatches.Count > 0;

    public void UpdateHighlights(string? query)
    {
        var (highlightBackground, highlightForeground, normalForeground, currentBackground) = GetSearchHighlightBrushes();
        TreeNodeViewModel.ForEachDescendant(_viewModel.TreeNodes, node =>
            node.UpdateSearchHighlight(query, highlightBackground, highlightForeground, normalForeground, currentBackground));
    }

    public void ClearSearchState()
    {
        Interlocked.Increment(ref _bringIntoViewVersion);

        // Clear current match reference first
        _currentSearchMatch = null;
        _searchMatchIndex = -1;
        ClearActiveHighlights();

        // Clear and trim the matches list
        _searchMatches.Clear();
        _searchMatches.TrimExcess();

        // Note: Don't call UpdateHighlights here - nodes may already be cleared
    }

    public void Dispose()
    {
        Interlocked.Increment(ref _bringIntoViewVersion);
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Elapsed -= OnSearchDebounceTimerElapsed;
        _searchDebounceTimer.Dispose();
        lock (_searchCtsLock)
        {
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = null;
        }

        // Clear search state to release references
        _searchMatches.Clear();
        _searchMatches.TrimExcess();
        _activeHighlightNodes.Clear();
        _currentSearchMatch = null;
        _activeHighlightQuery = null;

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
    }

    public void RefreshThemeHighlights()
    {
        UpdateHighlights(_viewModel.SearchQuery);
    }

    private void SelectSearchMatch()
    {
        if (_searchMatchIndex < 0 || _searchMatchIndex >= _searchMatches.Count)
            return;

        var node = _searchMatches[_searchMatchIndex];
        node.EnsureParentsExpanded();
        SelectTreeNode(node);
        UpdateCurrentSearchMatch(node);
        BringNodeIntoView(node);
        _treeView.Focus();
    }

    private void BringNodeIntoView(TreeNodeViewModel node)
    {
        var version = Interlocked.Increment(ref _bringIntoViewVersion);
        TryBringNodeIntoViewWithRetries(node, version, attempt: 0);
    }

    private void SelectTreeNode(TreeNodeViewModel node)
    {
        _treeView.SelectedItem = node;
        node.IsSelected = true;
    }

    private void UpdateCurrentSearchMatch(TreeNodeViewModel? node)
    {
        if (ReferenceEquals(_currentSearchMatch, node))
            return;

        var query = _viewModel.SearchQuery;
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

        TreeNodeViewModel.ForEachDescendant(_viewModel.TreeNodes, node =>
        {
            if (!node.HasHighlightedDisplay && !node.IsCurrentSearchMatch)
                return;

            node.UpdateSearchHighlight(null, highlightBackground, highlightForeground, normalForeground, currentBackground);
        });
    }

    private void ApplySearchHighlightDiff(string query)
    {
        var (highlightBackground, highlightForeground, normalForeground, currentBackground) = GetSearchHighlightBrushes();
        var nextHighlights = new HashSet<TreeNodeViewModel>(_searchMatches);
        var queryChanged = !string.Equals(_activeHighlightQuery, query, StringComparison.Ordinal);

        foreach (var node in _activeHighlightNodes)
        {
            if (nextHighlights.Contains(node))
                continue;

            node.IsCurrentSearchMatch = false;
            node.UpdateSearchHighlight(null, highlightBackground, highlightForeground, normalForeground, currentBackground);
        }

        foreach (var node in nextHighlights)
        {
            if (queryChanged || !_activeHighlightNodes.Contains(node))
            {
                node.UpdateSearchHighlight(query, highlightBackground, highlightForeground, normalForeground, currentBackground);
            }
        }

        _activeHighlightNodes.Clear();
        foreach (var node in nextHighlights)
            _activeHighlightNodes.Add(node);

        _activeHighlightQuery = query;
    }

    private void ClearActiveHighlights()
    {
        if (_activeHighlightNodes.Count == 0)
            return;

        var (highlightBackground, highlightForeground, normalForeground, currentBackground) = GetSearchHighlightBrushes();
        foreach (var node in _activeHighlightNodes)
        {
            node.IsCurrentSearchMatch = false;
            node.UpdateSearchHighlight(null, highlightBackground, highlightForeground, normalForeground, currentBackground);
        }

        _activeHighlightNodes.Clear();
        _activeHighlightQuery = null;
    }

    private void TryBringNodeIntoViewWithRetries(TreeNodeViewModel node, int version, int attempt)
    {
        if (version != Volatile.Read(ref _bringIntoViewVersion))
            return;

        var result = TryBringNodeIntoView(node);
        if (result == BringIntoViewResult.Visible || attempt >= MaxBringIntoViewAttempts - 1)
            return;

        var priority = BringIntoViewRetryPriorities[Math.Min(attempt, BringIntoViewRetryPriorities.Length - 1)];
        Dispatcher.UIThread.Post(
            () => TryBringNodeIntoViewWithRetries(node, version, attempt + 1),
            priority);
    }

    private BringIntoViewResult TryBringNodeIntoView(TreeNodeViewModel node)
    {
        if (TryGetContainer(node, out var directContainer) && directContainer is not null)
        {
            if (IsContainerVisibleInViewport(directContainer))
                return BringIntoViewResult.Visible;

            directContainer.BringIntoView();
            return IsContainerVisibleInViewport(directContainer)
                ? BringIntoViewResult.Visible
                : BringIntoViewResult.Pending;
        }

        // The target container may not be materialized yet under virtualization.
        // Scrolling to the nearest realized ancestor progressively materializes descendants.
        if (TryGetNearestRealizedAncestorContainer(node, out var ancestorContainer) && ancestorContainer is not null)
        {
            if (!IsContainerVisibleInViewport(ancestorContainer))
                ancestorContainer.BringIntoView();

            return BringIntoViewResult.Pending;
        }

        return BringIntoViewResult.NotFound;
    }

    private bool TryGetContainer(TreeNodeViewModel node, out TreeViewItem? container)
    {
        if (_treeView.ContainerFromItem(node) is TreeViewItem directContainer)
        {
            container = directContainer;
            return true;
        }

        container = _treeView.FindDescendantOfType<TreeViewItem>(
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
        var scrollViewer = _treeView.FindDescendantOfType<ScrollViewer>(
            includeSelf: false,
            visual => visual is ScrollViewer);
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

    private (IBrush highlightBackground, IBrush highlightForeground, IBrush normalForeground, IBrush currentBackground)
        GetSearchHighlightBrushes()
    {
        var app = global::Avalonia.Application.Current;
        var theme = app?.ActualThemeVariant ?? ThemeVariant.Light;

        // Return cached brushes if theme hasn't changed
        if (_cachedTheme == theme &&
            _cachedHighlightBackground is not null &&
            _cachedHighlightForeground is not null &&
            _cachedNormalForeground is not null &&
            _cachedCurrentBackground is not null)
        {
            return (_cachedHighlightBackground, _cachedHighlightForeground, _cachedNormalForeground, _cachedCurrentBackground);
        }

        // Create new brushes only when theme changes
        _cachedTheme = theme;

        _cachedHighlightBackground = new SolidColorBrush(Color.Parse("#FFEB3B"));
        _cachedHighlightForeground = new SolidColorBrush(Color.Parse("#000000"));
        _cachedNormalForeground = theme == ThemeVariant.Dark
            ? new SolidColorBrush(Color.Parse("#E7E9EF"))
            : new SolidColorBrush(Color.Parse("#1A1A1A"));
        _cachedCurrentBackground = new SolidColorBrush(Color.Parse("#F9A825"));

        if (app?.Resources.TryGetResource("TreeSearchHighlightBrush", theme, out var bg) == true &&
            bg is IBrush bgBrush)
            _cachedHighlightBackground = bgBrush;

        if (app?.Resources.TryGetResource("TreeSearchHighlightTextBrush", theme, out var fg) == true &&
            fg is IBrush fgBrush)
            _cachedHighlightForeground = fgBrush;

        if (app?.Resources.TryGetResource("TreeSearchCurrentBrush", theme, out var current) == true &&
            current is IBrush currentBrush)
            _cachedCurrentBackground = currentBrush;

        if (app?.Resources.TryGetResource("AppTextBrush", theme, out var textFg) == true && textFg is IBrush textBrush)
            _cachedNormalForeground = textBrush;

        return (_cachedHighlightBackground, _cachedHighlightForeground, _cachedNormalForeground, _cachedCurrentBackground);
    }
}
