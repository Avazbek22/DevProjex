using Avalonia.Animation;
using Avalonia.Animation.Easings;
using DevProjex.Avalonia.Services;
using DevProjex.Avalonia.Views;

namespace DevProjex.Avalonia.Coordinators;

internal sealed class SearchFilterInteractionController : IDisposable
{
    private const double ToolBarHeight = 48.0;
    private const double PanelIslandSpacing = 4.0;
    private const double ToolBarContentOffset = 5.0;
    private static readonly TimeSpan ToolBarAnimationDuration =
        UiTimingProfile.Scale(TimeSpan.FromMilliseconds(220));
    private static readonly TimeSpan ToolBarContentAnimationDuration =
        UiTimingProfile.Scale(TimeSpan.FromMilliseconds(180));
    private static readonly TimeSpan ToolBarFadeDuration =
        UiTimingProfile.Scale(TimeSpan.FromMilliseconds(160));
    private static readonly TimeSpan HotkeyDebounceWindow =
        UiTimingProfile.Scale(TimeSpan.FromMilliseconds(220));

    private readonly Window _window;
    private readonly MainWindowViewModel _viewModel;
    private readonly TreeView _treeView;
    private readonly TextToolState _search;
    private readonly TextToolState _filter;
    private readonly TreeSearchCoordinator _searchCoordinator;
    private readonly NameFilterCoordinator _filterCoordinator;
    private readonly SessionMetricsRecorder _sessionMetrics;
    private readonly IToastService _toastService;
    private readonly LocalizationService _localization;
    private readonly Func<string?> _getCurrentPath;
    private readonly Func<BuildTreeResult?> _getCurrentTree;
    private readonly Func<bool, CancellationToken, Task<TreeRefreshOutcome>> _refreshTreeAsync;
    private readonly Action _resetInteractiveFilterCache;
    private readonly Func<bool> _wasLastInteractiveFilterInMemory;
    private readonly Func<Exception, Task> _showErrorAsync;
    private readonly Action<MemoryCleanupReason> _scheduleMemoryCleanup;
    private readonly Action _cancelMemoryCleanup;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly DesktopShortcutModifiers _shortcutModifiers;
    private readonly PreviewMarkerBar? _treeSearchMarkerBar;

    private ProjectTreeExpansionSnapshot? _filterExpansionSnapshot;
    private ScrollViewer? _treeScrollViewer;
    private ScrollBar? _treeVerticalScrollBar;
    private Cursor? _searchMarkerCursor;
    private InputElement? _searchMarkerCursorTarget;
    private SuspendedTextTool _suspendedTool;
    private int _filterApplyVersion;
    private int _realtimeSuppressionDepth;
    private long _lastSearchHotkeyTimestamp;
    private long _lastFilterHotkeyTimestamp;
    private int _pendingSearchHotkeyToggle;
    private int _pendingFilterHotkeyToggle;
    private Task? _searchCloseTask;
    private Task? _filterCloseTask;
    private bool _disposed;

    public SearchFilterInteractionController(
        Window window,
        MainWindowViewModel viewModel,
        TreeView treeView,
        SearchBarView searchBar,
        Border searchBarContainer,
        FilterBarView filterBar,
        Border filterBarContainer,
        SessionMetricsRecorder sessionMetrics,
        IToastService toastService,
        LocalizationService localization,
        Func<string?> getCurrentPath,
        Func<BuildTreeResult?> getCurrentTree,
        Func<bool, CancellationToken, Task<TreeRefreshOutcome>> refreshTreeAsync,
        Action resetInteractiveFilterCache,
        Func<bool> wasLastInteractiveFilterInMemory,
        Func<Exception, Task> showErrorAsync,
        Action<MemoryCleanupReason> scheduleMemoryCleanup,
        Action cancelMemoryCleanup,
        DesktopShortcutModifiers? shortcutModifiers = null,
        PreviewMarkerBar? treeSearchMarkerBar = null)
    {
        _lifetimeToken = _lifetimeCts.Token;
        _window = window;
        _viewModel = viewModel;
        _treeView = treeView;
        _sessionMetrics = sessionMetrics;
        _toastService = toastService;
        _localization = localization;
        _getCurrentPath = getCurrentPath;
        _getCurrentTree = getCurrentTree;
        _refreshTreeAsync = refreshTreeAsync;
        _resetInteractiveFilterCache = resetInteractiveFilterCache;
        _wasLastInteractiveFilterInMemory = wasLastInteractiveFilterInMemory;
        _showErrorAsync = showErrorAsync;
        _scheduleMemoryCleanup = scheduleMemoryCleanup;
        _cancelMemoryCleanup = cancelMemoryCleanup;
        _shortcutModifiers = shortcutModifiers ?? DesktopShortcutModifiers.Current;
        _treeSearchMarkerBar = treeSearchMarkerBar;

        _search = CreateToolState(
            TextToolKind.Search,
            searchBar,
            searchBarContainer,
            () => searchBar.SearchBoxControl);
        _filter = CreateToolState(
            TextToolKind.Filter,
            filterBar,
            filterBarContainer,
            () => filterBar.FilterBoxControl);

        _searchCoordinator = new TreeSearchCoordinator(
            viewModel,
            treeView,
            sessionMetrics);
        if (_treeSearchMarkerBar is not null)
        {
            _searchCoordinator.SearchMarkersChanged += OnSearchMarkersChanged;
            _treeSearchMarkerBar.SetMarkers(_searchCoordinator.SearchMarkerSnapshot);
            _treeView.AddHandler(
                InputElement.PointerPressedEvent,
                OnSearchMarkerPointerPressed,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
            _treeView.AddHandler(
                InputElement.PointerMovedEvent,
                OnSearchMarkerPointerMoved,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
            _treeView.PointerExited += OnSearchMarkerPointerExited;
            _treeView.LayoutUpdated += OnTreeLayoutUpdated;
        }
        _filterCoordinator = new NameFilterCoordinator(
            ApplyFilterRealtime,
            () => !string.IsNullOrWhiteSpace(viewModel.NameFilter),
            viewModel.SetFilterInProgress);

        ForceHidden(_search);
        ForceHidden(_filter);
    }

    public bool IsAnimating => _search.IsAnimating || _filter.IsAnimating;

    public bool IsRealtimeSuppressed => Volatile.Read(ref _realtimeSuppressionDepth) > 0;

    public bool IsSearchEffectivelyVisible => IsEffectivelyVisible(_search);

    public bool IsFilterEffectivelyVisible => IsEffectivelyVisible(_filter);

    private void OnSearchMarkersChanged(
        object? sender,
        PreviewMarkersChangedEventArgs e)
    {
        _treeSearchMarkerBar?.SetMarkers(e.Snapshot);
        UpdateSearchMarkerTrackGeometry();
    }

    private void OnTreeLayoutUpdated(object? sender, EventArgs e) =>
        UpdateSearchMarkerTrackGeometry();

    private void UpdateSearchMarkerTrackGeometry()
    {
        if (_treeSearchMarkerBar is null)
            return;

        _treeScrollViewer ??= _treeView.FindDescendantOfType<ScrollViewer>();
        _treeVerticalScrollBar ??= _treeScrollViewer?
            .GetVisualDescendants()
            .OfType<ScrollBar>()
            .FirstOrDefault(static scrollBar =>
                scrollBar.Orientation == Orientation.Vertical);

        var hasVerticalScrollBar = _treeVerticalScrollBar is { IsVisible: true };
        _treeSearchMarkerBar.IsMarkerDisplayEnabled = hasVerticalScrollBar;

        var margin = default(Thickness);
        if (_treeScrollViewer is { } scrollViewer &&
            _treeVerticalScrollBar is { IsVisible: true } scrollBar &&
            scrollBar.GetVisualDescendants()
                .OfType<Track>()
                .FirstOrDefault() is { } track &&
            track.GetVisualDescendants()
                .OfType<Thumb>()
                .FirstOrDefault() is { } thumb &&
            track.TranslatePoint(default, _treeView) is { } origin)
        {
            var availableHeight = Math.Max(0, _treeView.Bounds.Height);
            var top = Math.Clamp(origin.Y, 0, availableHeight);
            var bottom = Math.Clamp(
                availableHeight - top - track.Bounds.Height,
                0,
                availableHeight);
            margin = new Thickness(0, top, 0, bottom);

            var visibleLineCount =
                _searchCoordinator.SearchMarkerSnapshot.TotalLineCount;
            var lineHeight = visibleLineCount > 0
                ? Math.Max(1, scrollViewer.Extent.Height / visibleLineCount)
                : 1;
            _treeSearchMarkerBar.SetScrollMetrics(new PreviewMarkerScrollMetrics(
                scrollViewer.Extent.Height,
                scrollViewer.Viewport.Height,
                thumb.Bounds.Height,
                FirstLineTop: 0,
                lineHeight));
        }
        else
        {
            _treeSearchMarkerBar.SetScrollMetrics(null);
        }

        if (_treeSearchMarkerBar.Margin != margin)
            _treeSearchMarkerBar.Margin = margin;
    }

    private void OnSearchMarkerPointerPressed(
        object? sender,
        PointerPressedEventArgs e)
    {
        if (_treeSearchMarkerBar is null ||
            !_treeSearchMarkerBar.IsVisible ||
            !e.GetCurrentPoint(_treeView).Properties.IsLeftButtonPressed ||
            _treeSearchMarkerBar.FindTargetAt(
                e.GetPosition(_treeSearchMarkerBar)) is not { } target ||
            !_searchCoordinator.TryNavigateToSearchMarker(target))
        {
            return;
        }

        e.Handled = true;
    }

    private void OnSearchMarkerPointerMoved(
        object? sender,
        PointerEventArgs e)
    {
        if (_treeSearchMarkerBar is not { IsVisible: true })
        {
            SetSearchMarkerCursor(null);
            return;
        }

        SetSearchMarkerCursor(
            _treeSearchMarkerBar.FindTargetAt(
                e.GetPosition(_treeSearchMarkerBar)) is not null
                ? e.Source as InputElement
                : null);
    }

    private void OnSearchMarkerPointerExited(
        object? sender,
        PointerEventArgs e) =>
        SetSearchMarkerCursor(null);

    private void SetSearchMarkerCursor(InputElement? target)
    {
        var cursor = _searchMarkerCursor ??=
            new Cursor(StandardCursorType.Hand);
        if (ReferenceEquals(_searchMarkerCursorTarget, target) &&
            ReferenceEquals(target?.Cursor, cursor))
        {
            return;
        }

        _searchMarkerCursorTarget?.ClearValue(InputElement.CursorProperty);
        _searchMarkerCursorTarget = target;
        if (target is not null)
            target.Cursor = cursor;
    }

    public void OnSearchQueryChanged()
    {
        if (!_disposed && !IsRealtimeSuppressed)
        {
            _cancelMemoryCleanup();
            _searchCoordinator.OnSearchQueryChanged();
        }
    }

    public void OnNameFilterChanged()
    {
        if (!_disposed && !IsRealtimeSuppressed)
        {
            _cancelMemoryCleanup();
            _filterCoordinator.OnNameFilterChanged();
        }
    }

    public void UpdateHighlights(string? query) => _searchCoordinator.UpdateHighlights(query);

    public void UpdateSearchMatches() => _searchCoordinator.UpdateSearchMatches();

    public void ClearSearchState() => _searchCoordinator.ClearSearchState();

    public void NavigateSearch(int step)
    {
        _ = NavigateSearchAsync(step);
    }

    internal async Task NavigateSearchAsync(int step)
    {
        if (_disposed)
            return;

        var query = _viewModel.SearchQuery;
        if (string.IsNullOrWhiteSpace(query))
            return;

        var result = await _searchCoordinator.TryNavigateForCurrentQueryAsync(step);
        if (!_disposed &&
            result == TreeSearchCoordinator.NavigationResult.NoMatches &&
            _viewModel.SearchVisible &&
            string.Equals(query, _viewModel.SearchQuery, StringComparison.Ordinal))
        {
            _toastService.Show(_localization["Toast.NoMatches"]);
        }
    }

    public async Task ToggleSearchAsync()
    {
        if (_disposed ||
            !_viewModel.IsProjectLoaded ||
            !_viewModel.IsSearchAvailable)
            return;

        if (_viewModel.SearchVisible)
        {
            await CloseSearchAsync();
            return;
        }

        if (IsEffectivelyVisible(_filter))
            await CloseFilterAsync(focusTree: false);

        if (_disposed)
            return;

        ShowSearch();
    }

    public async Task ToggleFilterAsync()
    {
        if (_disposed ||
            !_viewModel.IsProjectLoaded ||
            !_viewModel.IsSearchFilterAvailable)
            return;

        if (_viewModel.FilterVisible)
        {
            await CloseFilterAsync();
            return;
        }

        if (IsEffectivelyVisible(_search))
            await CloseSearchAsync(focusTree: false);

        if (_disposed)
            return;

        ShowFilter();
    }

    public void ShowSearch(bool focusInput = true, bool selectAllOnFocus = true) =>
        Show(_search, focusInput, selectAllOnFocus);

    public void ShowFilter(bool focusInput = true, bool selectAllOnFocus = true) =>
        Show(_filter, focusInput, selectAllOnFocus);

    public Task CloseSearchAsync(bool focusTree = true)
    {
        if (_disposed)
            return Task.CompletedTask;

        if (_searchCloseTask is { IsCompleted: false } pendingClose)
            return pendingClose;

        if (!IsEffectivelyVisible(_search))
            return Task.CompletedTask;

        var closeTask = CloseSearchCoreAsync(focusTree);
        _searchCloseTask = closeTask;
        return closeTask;
    }

    private async Task CloseSearchCoreAsync(bool focusTree)
    {
        InvalidateFocusRequest(_search);
        await PrepareForCloseAsync(_search, focusTree);

        if (_disposed || _viewModel.SearchVisible)
            return;

        var shouldNormalizeTree = _searchCoordinator.HasAppliedSearchState;
        using (SuppressRealtimeUpdates())
            _viewModel.SearchQuery = string.Empty;
        _searchCoordinator.CancelPending();
        if (shouldNormalizeTree)
        {
            _searchCoordinator.UpdateSearchMatches();
            _searchCoordinator.ClearSearchState(preservePendingHighlightCleanup: true);
        }
        else
        {
            _searchCoordinator.ClearSearchState();
        }

        await _searchCoordinator.CompleteSearchCloseAsync();
        if (!_disposed && shouldNormalizeTree)
            _scheduleMemoryCleanup(MemoryCleanupReason.SearchClose);
    }

    public Task CloseFilterAsync(bool focusTree = true)
    {
        if (_disposed)
            return Task.CompletedTask;

        if (_filterCloseTask is { IsCompleted: false } pendingClose)
            return pendingClose;

        if (!IsEffectivelyVisible(_filter))
            return Task.CompletedTask;

        var closeTask = CloseFilterCoreAsync(focusTree);
        _filterCloseTask = closeTask;
        return closeTask;
    }

    private async Task CloseFilterCoreAsync(bool focusTree)
    {
        InvalidateFocusRequest(_filter);
        await PrepareForCloseAsync(_filter, focusTree);

        if (_disposed || _viewModel.FilterVisible)
            return;

        if (string.IsNullOrEmpty(_viewModel.NameFilter) &&
            _filterExpansionSnapshot is null)
        {
            _filterCoordinator.CancelPending();
            return;
        }

        _viewModel.NameFilter = string.Empty;
        _filterCoordinator.CancelPending();
        await ApplyFilterRealtimeAsync(_lifetimeToken);
        if (!_disposed)
            _scheduleMemoryCleanup(MemoryCleanupReason.FilterClose);
    }

    public void HandleSearchInputKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _ = CloseSearchAsync();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter)
            return;

        NavigateSearch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
        e.Handled = true;
    }

    public void HandleFilterInputKey(KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        _ = CloseFilterAsync();
        e.Handled = true;
    }

    public bool TryHandleToggleHotkey(KeyEventArgs e)
    {
        if (_disposed)
            return false;

        var modifiers = e.KeyModifiers;
        if (_shortcutModifiers.IsPrimary(modifiers) && e.Key == Key.F)
        {
            if (!IsHotkeyDebounced(ref _lastSearchHotkeyTimestamp))
                ScheduleHotkeyToggle(TextToolKind.Search);

            e.Handled = true;
            return true;
        }

        if (!_shortcutModifiers.IsPrimaryWithShift(modifiers) || e.Key != Key.N)
            return false;

        if (!IsHotkeyDebounced(ref _lastFilterHotkeyTimestamp))
            ScheduleHotkeyToggle(TextToolKind.Filter);

        e.Handled = true;
        return true;
    }

    public bool TryHandleActiveToolKey(KeyEventArgs e)
    {
        if (_disposed)
            return false;

        if (e.Key == Key.Escape && _viewModel.SearchVisible)
        {
            _ = CloseSearchAsync();
            e.Handled = true;
            return true;
        }

        if (e.Key == Key.Escape && _viewModel.FilterVisible)
        {
            _ = CloseFilterAsync();
            e.Handled = true;
            return true;
        }

        if (e.Key != Key.F3 || !_viewModel.SearchVisible)
            return false;

        NavigateSearch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
        e.Handled = true;
        return true;
    }

    public async Task ApplyFilterRealtimeAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
            return;

        var stopwatch = Stopwatch.StartNew();
        var version = 0;
        try
        {
            var currentPath = _getCurrentPath();
            if (string.IsNullOrEmpty(currentPath))
            {
                _viewModel.UpdateFilterMatchSummary(0);
                _viewModel.SetFilterInProgress(false);
                return;
            }

            var query = _viewModel.NameFilter?.Trim();
            var hasQuery = !string.IsNullOrWhiteSpace(query);
            version = Interlocked.Increment(ref _filterApplyVersion);

            cancellationToken.ThrowIfCancellationRequested();
            _lifetimeToken.ThrowIfCancellationRequested();
            await _refreshTreeAsync(true, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _lifetimeToken.ThrowIfCancellationRequested();

            if (_disposed ||
                version != Volatile.Read(ref _filterApplyVersion))
                return;

            var matchCount = hasQuery ? ApplyNameFilterPresentation(query!) : 0;
            if (!hasQuery)
                _viewModel.UpdateFilterMatchSummary(0);

            if (!hasQuery && _filterExpansionSnapshot is not null)
            {
                if (_viewModel.TreeNodes.Count == 1)
                {
                    ProjectTreeUiState.RestoreExpansion(
                        _viewModel.TreeNodes[0],
                        _filterExpansionSnapshot);
                }
                _filterExpansionSnapshot = null;
                _resetInteractiveFilterCache();
            }

            _sessionMetrics.RecordTreeFilter(
                query,
                matchCount,
                stopwatch.Elapsed,
                _wasLastInteractiveFilterInMemory());
        }
        catch (OperationCanceledException)
        {
            // Superseding input owns the next projection.
        }
        catch (Exception ex)
        {
            if (!_disposed)
                await _showErrorAsync(ex);
        }
        finally
        {
            if (!_disposed &&
                (version == 0 ||
                 version == Volatile.Read(ref _filterApplyVersion)))
            {
                _viewModel.SetFilterInProgress(false);
            }
        }
    }

    public int ApplyNameFilterPresentation(string filterQuery)
    {
        if (_disposed)
            return 0;

        var matchCount = _getCurrentTree() is null
            ? 0
            : _searchCoordinator.ApplyFilterPresentation(filterQuery);
        _viewModel.UpdateFilterMatchSummary(matchCount);

        return matchCount;
    }

    public void ReapplyActiveTreeQueryPresentation()
    {
        if (_disposed)
            return;

        var filterQuery = _viewModel.NameFilter?.Trim();
        if (!string.IsNullOrWhiteSpace(filterQuery))
        {
            ApplyNameFilterPresentation(filterQuery);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_viewModel.SearchQuery))
            _ = _searchCoordinator.UpdateSearchMatchesAsync();
    }

    public void SuspendForPreviewOnly()
    {
        if (_disposed)
            return;

        InvalidateFocusRequests();

        var searchWasVisible = _viewModel.SearchVisible || IsEffectivelyVisible(_search);
        var filterWasVisible = !searchWasVisible &&
                               (_viewModel.FilterVisible || IsEffectivelyVisible(_filter));
        _suspendedTool = searchWasVisible
            ? SuspendedTextTool.Search
            : filterWasVisible
                ? SuspendedTextTool.Filter
                : SuspendedTextTool.None;

        _viewModel.SearchVisible = false;
        _viewModel.FilterVisible = false;
        ResetAnimationState();
        ForceHidden(_search);
        ForceHidden(_filter);
        CancelPending();
    }

    public void RestoreAfterPreviewOnly()
    {
        if (_disposed)
            return;

        var suspendedTool = _suspendedTool;
        _suspendedTool = SuspendedTextTool.None;

        if (suspendedTool == SuspendedTextTool.Search)
        {
            _viewModel.SearchVisible = true;
            _viewModel.FilterVisible = false;
            ForceVisible(_search);
        }
        else if (suspendedTool == SuspendedTextTool.Filter)
        {
            _viewModel.FilterVisible = true;
            _viewModel.SearchVisible = false;
            ForceVisible(_filter);
        }
    }

    public void InvalidateFocusRequests()
    {
        InvalidateFocusRequest(_search);
        InvalidateFocusRequest(_filter);
    }

    public void SyncVisualState()
    {
        if (_disposed)
            return;

        ResetAnimationState();
        if (_viewModel.SearchVisible && _viewModel.FilterVisible)
            _viewModel.FilterVisible = false;

        SetForcedVisibility(_search, _viewModel.SearchVisible);
        SetForcedVisibility(_filter, _viewModel.FilterVisible);
    }

    public async Task PrepareForProjectLoadAsync()
    {
        if (_disposed)
            return;

        var searchWasVisible = IsEffectivelyVisible(_search);
        var filterWasVisible = IsEffectivelyVisible(_filter);

        InvalidateFocusRequests();
        using (SuppressRealtimeUpdates())
        {
            _viewModel.SearchVisible = false;
            _viewModel.FilterVisible = false;
            var searchCloseTask = searchWasVisible
                ? AnimateAsync(_search, show: false)
                : Task.CompletedTask;
            var filterCloseTask = filterWasVisible
                ? AnimateAsync(_filter, show: false)
                : Task.CompletedTask;

            if (searchWasVisible || filterWasVisible)
            {
                await Task.WhenAll(searchCloseTask, filterCloseTask);
            }

            if (_disposed)
                return;

            CancelPending();
            _viewModel.SearchQuery = string.Empty;
            _viewModel.NameFilter = string.Empty;
            CancelPending();

            _searchCoordinator.UpdateHighlights(null);
            _searchCoordinator.ClearSearchState();
            _filterExpansionSnapshot = null;
            _resetInteractiveFilterCache();
            Interlocked.Increment(ref _filterApplyVersion);
            ForceHidden(_search);
            ForceHidden(_filter);
        }
    }

    public async Task ApplyStartupFilterAsync(string query)
    {
        var normalizedQuery = query.Trim();
        if (_disposed ||
            normalizedQuery.Length == 0 ||
            !_viewModel.IsSearchFilterAvailable)
            return;

        if (IsEffectivelyVisible(_search))
            await CloseSearchAsync(focusTree: false);

        if (_disposed)
            return;

        ShowFilter(focusInput: false, selectAllOnFocus: false);
        using (SuppressRealtimeUpdates())
        {
            _viewModel.SearchQuery = string.Empty;
            _viewModel.NameFilter = normalizedQuery;
        }

        _filterCoordinator.CancelPending();
        _viewModel.SetFilterInProgress(true);
        await ApplyFilterRealtimeAsync(_lifetimeToken);
    }

    public async Task ApplyStartupSearchAsync(string query)
    {
        var normalizedQuery = query.Trim();
        if (_disposed ||
            normalizedQuery.Length == 0 ||
            !_viewModel.IsSearchFilterAvailable)
            return;

        if (IsEffectivelyVisible(_filter))
            await CloseFilterAsync(focusTree: false);

        if (_disposed)
            return;

        ShowSearch(focusInput: false, selectAllOnFocus: false);
        using (SuppressRealtimeUpdates())
        {
            _viewModel.NameFilter = string.Empty;
            _viewModel.SearchQuery = normalizedQuery;
        }

        _searchCoordinator.CancelPending();
        await _searchCoordinator.UpdateSearchMatchesAsync();
    }

    public void ClearProjectState()
    {
        _searchCoordinator.CancelPending();
        _searchCoordinator.ClearSearchState();
        _filterCoordinator.CancelPending();
        _filterExpansionSnapshot = null;
        _suspendedTool = SuspendedTextTool.None;
        Interlocked.Increment(ref _filterApplyVersion);
        _resetInteractiveFilterCache();
    }

    public void CaptureFilterExpansionForTreeReplacement(string projectPath)
    {
        if (_filterExpansionSnapshot is not null)
            return;

        _filterExpansionSnapshot = ProjectTreeUiState.CaptureExpansion(
            projectPath,
            _viewModel.TreeNodes);
    }

    public IDisposable SuppressRealtimeUpdates()
    {
        Interlocked.Increment(ref _realtimeSuppressionDepth);
        return new RealtimeSuppressionLease(this);
    }

    public void CancelPending()
    {
        _searchCoordinator.CancelPending();
        _filterCoordinator.CancelPending();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _lifetimeCts.Cancel();
        InvalidateFocusRequests();
        Interlocked.Increment(ref _filterApplyVersion);
        Interlocked.Exchange(ref _pendingSearchHotkeyToggle, 0);
        Interlocked.Exchange(ref _pendingFilterHotkeyToggle, 0);
        ResetAnimationState();
        if (_treeSearchMarkerBar is not null)
        {
            _searchCoordinator.SearchMarkersChanged -= OnSearchMarkersChanged;
            _treeView.RemoveHandler(
                InputElement.PointerPressedEvent,
                OnSearchMarkerPointerPressed);
            _treeView.RemoveHandler(
                InputElement.PointerMovedEvent,
                OnSearchMarkerPointerMoved);
            _treeView.PointerExited -= OnSearchMarkerPointerExited;
            _treeView.LayoutUpdated -= OnTreeLayoutUpdated;
            SetSearchMarkerCursor(null);
        }

        _searchCoordinator.Dispose();
        _filterCoordinator.Dispose();
        _filterExpansionSnapshot = null;
        _lifetimeCts.Dispose();
    }

    private void ApplyFilterRealtime(CancellationToken cancellationToken) =>
        _ = ApplyFilterRealtimeAsync(cancellationToken);

    private void Show(TextToolState tool, bool focusInput, bool selectAllOnFocus)
    {
        if (_disposed ||
            !_viewModel.IsProjectLoaded ||
            !IsAvailable(tool))
            return;

        _cancelMemoryCleanup();
        SuppressAccent(tool);
        SetLogicalVisibility(tool, true);
        _ = AnimateAsync(tool, show: true);

        if (!focusInput)
            return;

        var focusVersion = Interlocked.Increment(ref tool.FocusVersion);
        _ = FocusAfterOpenAsync(tool, selectAllOnFocus, focusVersion);
    }

    private async Task PrepareForCloseAsync(TextToolState tool, bool focusTree)
    {
        if (_disposed)
            return;

        if (tool.GetInput()?.IsFocused == true)
            _treeView.Focus();

        SuppressAccent(tool);
        SetLogicalVisibility(tool, false);
        var animationTask = AnimateAsync(tool, show: false);

        if (focusTree)
            _treeView.Focus();

        await animationTask;
    }

    private async Task AnimateAsync(TextToolState tool, bool show)
    {
        if (_disposed)
            return;

        var version = Interlocked.Increment(ref tool.AnimationVersion);
        tool.IsAnimating = true;
        try
        {
            EnsureTransitions(tool);
            if (show)
            {
                tool.Surface.IsHitTestVisible = true;
                tool.Surface.IsEnabled = true;
                tool.Container.IsVisible = true;
            }
            else
            {
                SuppressAccent(tool);
            }

            tool.Container.Height = show ? ToolBarHeight : 0.0;
            tool.Container.Margin = new Thickness(0, 0, 0, show ? PanelIslandSpacing : 0.0);
            tool.Transform.Y = show ? 0.0 : ToolBarContentOffset;
            tool.Surface.Opacity = show ? 1.0 : 0.0;
            if (!await WaitForAnimationAsync(tool, _lifetimeToken))
                return;

            if (_disposed || version != Interlocked.Read(ref tool.AnimationVersion))
                return;

            if (!show && !IsLogicallyVisible(tool))
                ForceHidden(tool);
            else if (show && IsLogicallyVisible(tool))
                _ = RestoreAccentAfterOpenAsync(tool);

            await RefreshVisualHostAsync();
        }
        catch (OperationCanceledException)
            when (_lifetimeToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (version == Interlocked.Read(ref tool.AnimationVersion))
                tool.IsAnimating = false;
        }
    }

    private async Task FocusAfterOpenAsync(
        TextToolState tool,
        bool selectAllOnFocus,
        int focusVersion)
    {
        if (!await WaitForAnimationAsync(tool, _lifetimeToken))
            return;

        if (_disposed ||
            !IsLogicallyVisible(tool) ||
            !IsAvailable(tool) ||
            !IsFocusRequestCurrent(tool, focusVersion))
            return;

        const int maxAttempts = 4;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (_disposed ||
                !IsFocusRequestCurrent(tool, focusVersion))
                return;

            bool focused;
            try
            {
                focused = await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_disposed)
                        return false;

                    var input = tool.GetInput();
                    if (!IsInputReady(tool, input))
                        return false;

                    FocusInput(
                        input!,
                        selectAllOnFocus,
                        _lifetimeToken);
                    return input!.IsFocused;
                }, DispatcherPriority.Input, _lifetimeToken);
            }
            catch (OperationCanceledException)
                when (_lifetimeToken.IsCancellationRequested)
            {
                return;
            }

            if (focused)
                return;

            await DispatcherTaskSchedulerProvider.YieldAsync(DispatcherPriority.Background);
        }
    }

    private static void FocusInput(
        TextBox input,
        bool selectAllOnFocus,
        CancellationToken cancellationToken)
    {
        input.Focus();
        if (selectAllOnFocus)
        {
            input.SelectAll();
            return;
        }

        PlaceCaretAtEnd(input);
        _ = input.Dispatcher.InvokeAsync(
            () =>
            {
                if (!cancellationToken.IsCancellationRequested)
                    PlaceCaretAtEnd(input);
            },
            DispatcherPriority.Input);
    }

    private static void PlaceCaretAtEnd(TextBox input)
    {
        var end = input.Text?.Length ?? 0;
        input.SelectionStart = end;
        input.SelectionEnd = end;
        input.CaretIndex = end;
    }

    private async Task RestoreAccentAfterOpenAsync(TextToolState tool)
    {
        await DispatcherTaskSchedulerProvider.YieldAsync(DispatcherPriority.Render);
        if (_disposed || _lifetimeToken.IsCancellationRequested)
            return;

        await DispatcherTaskSchedulerProvider.YieldAsync(DispatcherPriority.Render);
        if (!_disposed &&
            !_lifetimeToken.IsCancellationRequested &&
            IsLogicallyVisible(tool) &&
            IsAvailable(tool))
            RestoreAccent(tool);
    }

    private async Task RefreshVisualHostAsync()
    {
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed)
                    return;

                InvalidateTool(_search);
                InvalidateTool(_filter);
                _window.InvalidateVisual();
            }, DispatcherPriority.Render, _lifetimeToken);
        }
        catch (OperationCanceledException)
            when (_lifetimeToken.IsCancellationRequested)
        {
            return;
        }

        if (_disposed || _lifetimeToken.IsCancellationRequested)
            return;
        await DispatcherTaskSchedulerProvider.YieldAsync(DispatcherPriority.Render);
    }

    private static void InvalidateTool(TextToolState tool)
    {
        tool.Surface.InvalidateVisual();
        tool.Container.InvalidateMeasure();
        tool.Container.InvalidateArrange();
        tool.Container.InvalidateVisual();
        if (tool.Container.Parent is Visual parent)
            parent.InvalidateVisual();
    }

    private void ScheduleHotkeyToggle(TextToolKind kind)
    {
        if (_disposed)
            return;

        ref var pending = ref kind == TextToolKind.Search
            ? ref _pendingSearchHotkeyToggle
            : ref _pendingFilterHotkeyToggle;
        if (Interlocked.CompareExchange(ref pending, 1, 0) != 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_disposed)
                    return;

                if (kind == TextToolKind.Search)
                {
                    if (_viewModel.IsSearchAvailable)
                        _ = ToggleSearchAsync();
                }
                else if (_viewModel.IsSearchFilterAvailable)
                {
                    _ = ToggleFilterAsync();
                }
            }
            finally
            {
                if (kind == TextToolKind.Search)
                    Interlocked.Exchange(ref _pendingSearchHotkeyToggle, 0);
                else
                    Interlocked.Exchange(ref _pendingFilterHotkeyToggle, 0);
            }
        }, DispatcherPriority.Background);
    }

    private static bool IsHotkeyDebounced(ref long lastTimestamp)
    {
        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Read(ref lastTimestamp);
        if (previous != 0)
        {
            var elapsed = TimeSpan.FromSeconds((now - previous) / (double)Stopwatch.Frequency);
            if (elapsed < HotkeyDebounceWindow)
                return true;
        }

        Interlocked.Exchange(ref lastTimestamp, now);
        return false;
    }

    private static TextToolState CreateToolState(
        TextToolKind kind,
        Control surface,
        Border container,
        Func<TextBox?> getInput)
    {
        var transform = surface.RenderTransform as TranslateTransform ?? new TranslateTransform();
        surface.RenderTransform = transform;
        return new TextToolState(kind, surface, container, transform, getInput);
    }

    private static void EnsureTransitions(TextToolState tool)
    {
        tool.Container.Transitions ??=
        [
            new DoubleTransition
            {
                Property = Layoutable.HeightProperty,
                Duration = ToolBarAnimationDuration,
                Easing = new CubicEaseInOut()
            },
            new ThicknessTransition
            {
                Property = Layoutable.MarginProperty,
                Duration = ToolBarAnimationDuration,
                Easing = new CubicEaseInOut()
            }
        ];
        tool.Surface.Transitions ??=
        [
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = ToolBarFadeDuration,
                Easing = new CubicEaseOut()
            }
        ];
        tool.Transform.Transitions ??=
        [
            new DoubleTransition
            {
                Property = TranslateTransform.YProperty,
                Duration = ToolBarContentAnimationDuration,
                Easing = new CubicEaseOut()
            }
        ];
    }

    private static void ForceHidden(TextToolState tool)
    {
        SuppressAccent(tool);
        tool.Container.Height = 0;
        tool.Container.Margin = new Thickness(0);
        tool.Container.IsVisible = false;
        tool.Transform.Y = ToolBarContentOffset;
        tool.Surface.Opacity = 0;
        tool.Surface.IsHitTestVisible = false;
        tool.Surface.IsEnabled = false;
    }

    private static void ForceVisible(TextToolState tool)
    {
        RestoreAccent(tool);
        tool.Container.Height = ToolBarHeight;
        tool.Container.Margin = new Thickness(0, 0, 0, PanelIslandSpacing);
        tool.Container.IsVisible = true;
        tool.Transform.Y = 0;
        tool.Surface.Opacity = 1;
        tool.Surface.IsHitTestVisible = true;
        tool.Surface.IsEnabled = true;
    }

    private static void SetForcedVisibility(TextToolState tool, bool visible)
    {
        if (visible)
            ForceVisible(tool);
        else
            ForceHidden(tool);
    }

    private static void SuppressAccent(TextToolState tool) =>
        tool.GetInput()?.Classes.Add("suppress-accent");

    private static void RestoreAccent(TextToolState tool)
    {
        var input = tool.GetInput();
        input?.Classes.Remove("suppress-accent");
        input?.InvalidateVisual();
        tool.Surface.InvalidateVisual();
        tool.Container.InvalidateVisual();
    }

    private bool IsAvailable(TextToolState tool) =>
        tool.Kind == TextToolKind.Search
            ? _viewModel.IsSearchAvailable
            : _viewModel.IsSearchFilterAvailable;

    private bool IsLogicallyVisible(TextToolState tool) =>
        tool.Kind == TextToolKind.Search
            ? _viewModel.SearchVisible
            : _viewModel.FilterVisible;

    private void SetLogicalVisibility(TextToolState tool, bool visible)
    {
        if (tool.Kind == TextToolKind.Search)
            _viewModel.SearchVisible = visible;
        else
            _viewModel.FilterVisible = visible;
    }

    private bool IsEffectivelyVisible(TextToolState tool) =>
        IsLogicallyVisible(tool) || tool.Container.IsVisible || tool.Container.Bounds.Height > 0.5;

    private bool IsFocusRequestCurrent(TextToolState tool, int version) =>
        Volatile.Read(ref tool.FocusVersion) == version && IsLogicallyVisible(tool);

    private static bool IsInputReady(TextToolState tool, TextBox? input) =>
        input is { IsVisible: true, IsEnabled: true } &&
        tool.Surface is { IsVisible: true, IsEnabled: true, IsHitTestVisible: true } &&
        tool.Container.IsVisible;

    private static void InvalidateFocusRequest(TextToolState tool) =>
        Interlocked.Increment(ref tool.FocusVersion);

    private static async Task<bool> WaitForAnimationAsync(
        TextToolState _,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(
                ToolBarAnimationDuration +
                UiTimingProfile.AnimationSettleBuffer,
                cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private void ResetAnimationState()
    {
        Interlocked.Increment(ref _search.AnimationVersion);
        _search.IsAnimating = false;
        Interlocked.Increment(ref _filter.AnimationVersion);
        _filter.IsAnimating = false;
    }

    private void ReleaseRealtimeSuppression()
    {
        var remaining = Interlocked.Decrement(ref _realtimeSuppressionDepth);
        Debug.Assert(remaining >= 0, "Realtime suppression leases must be balanced.");
    }

    private enum TextToolKind
    {
        Search,
        Filter
    }

    private enum SuspendedTextTool
    {
        None,
        Search,
        Filter
    }

    private sealed class TextToolState(
        TextToolKind kind,
        Control surface,
        Border container,
        TranslateTransform transform,
        Func<TextBox?> getInput)
    {
        public TextToolKind Kind { get; } = kind;
        public Control Surface { get; } = surface;
        public Border Container { get; } = container;
        public TranslateTransform Transform { get; } = transform;
        public Func<TextBox?> GetInput { get; } = getInput;
        public bool IsAnimating { get; set; }
        public long AnimationVersion;
        public int FocusVersion;
    }

    private sealed class RealtimeSuppressionLease(SearchFilterInteractionController owner) : IDisposable
    {
        private SearchFilterInteractionController? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.ReleaseRealtimeSuppression();
    }
}
