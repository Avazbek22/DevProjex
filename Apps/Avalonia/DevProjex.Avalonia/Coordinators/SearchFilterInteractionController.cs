using Avalonia.Animation;
using Avalonia.Animation.Easings;
using DevProjex.Avalonia.Services;
using DevProjex.Avalonia.Views;
using DevProjex.Kernel;

namespace DevProjex.Avalonia.Coordinators;

internal sealed class SearchFilterInteractionController : IDisposable
{
    private const double ToolBarHeight = 46.0;
    private const double PanelIslandSpacing = 4.0;
    private static readonly TimeSpan ToolBarAnimationDuration =
        UiTimingProfile.Scale(TimeSpan.FromMilliseconds(250));
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

    private HashSet<string>? _filterExpansionSnapshot;
    private SuspendedTextTool _suspendedTool;
    private int _filterApplyVersion;
    private int _realtimeSuppressionDepth;
    private long _lastSearchHotkeyTimestamp;
    private long _lastFilterHotkeyTimestamp;
    private int _pendingSearchHotkeyToggle;
    private int _pendingFilterHotkeyToggle;
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
        Action scheduleSearchMemoryCleanup,
        Func<string?> getCurrentPath,
        Func<BuildTreeResult?> getCurrentTree,
        Func<bool, CancellationToken, Task<TreeRefreshOutcome>> refreshTreeAsync,
        Action resetInteractiveFilterCache,
        Func<bool> wasLastInteractiveFilterInMemory,
        Func<Exception, Task> showErrorAsync,
        Action<MemoryCleanupReason> scheduleMemoryCleanup)
    {
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
            scheduleSearchMemoryCleanup,
            sessionMetrics);
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

    public void OnSearchQueryChanged()
    {
        if (!IsRealtimeSuppressed)
            _searchCoordinator.OnSearchQueryChanged();
    }

    public void OnNameFilterChanged()
    {
        if (!IsRealtimeSuppressed)
            _filterCoordinator.OnNameFilterChanged();
    }

    public void UpdateHighlights(string? query) => _searchCoordinator.UpdateHighlights(query);

    public void UpdateSearchMatches() => _searchCoordinator.UpdateSearchMatches();

    public void ClearSearchState() => _searchCoordinator.ClearSearchState();

    public void NavigateSearch(int step)
    {
        if (string.IsNullOrWhiteSpace(_viewModel.SearchQuery))
            return;

        if (!_searchCoordinator.TryNavigateForCurrentQuery(step))
            _toastService.Show(_localization["Toast.NoMatches"]);
    }

    public async Task ToggleSearchAsync()
    {
        if (!_viewModel.IsProjectLoaded || !_viewModel.IsSearchAvailable)
            return;

        if (_viewModel.SearchVisible)
        {
            await CloseSearchAsync();
            return;
        }

        if (IsEffectivelyVisible(_filter))
            await CloseFilterAsync(focusTree: false);

        ShowSearch();
    }

    public async Task ToggleFilterAsync()
    {
        if (!_viewModel.IsProjectLoaded || !_viewModel.IsSearchFilterAvailable)
            return;

        if (_viewModel.FilterVisible)
        {
            await CloseFilterAsync();
            return;
        }

        if (IsEffectivelyVisible(_search))
            await CloseSearchAsync(focusTree: false);

        ShowFilter();
    }

    public void ShowSearch(bool focusInput = true, bool selectAllOnFocus = true) =>
        Show(_search, focusInput, selectAllOnFocus);

    public void ShowFilter(bool focusInput = true, bool selectAllOnFocus = true) =>
        Show(_filter, focusInput, selectAllOnFocus);

    public async Task CloseSearchAsync(bool focusTree = true)
    {
        if (!IsEffectivelyVisible(_search))
            return;

        InvalidateFocusRequest(_search);
        PrepareForClose(_search, focusTree);
        await WaitForAnimationAsync(_search);

        if (_viewModel.SearchVisible)
            return;

        if (string.IsNullOrEmpty(_viewModel.SearchQuery))
        {
            _searchCoordinator.CancelPending();
            return;
        }

        _viewModel.SearchQuery = string.Empty;
        _searchCoordinator.CancelPending();
        if (_searchCoordinator.HasMatches)
            _searchCoordinator.UpdateSearchMatches();

        _scheduleMemoryCleanup(MemoryCleanupReason.SearchClose);
    }

    public async Task CloseFilterAsync(bool focusTree = true)
    {
        if (!IsEffectivelyVisible(_filter))
            return;

        InvalidateFocusRequest(_filter);
        PrepareForClose(_filter, focusTree);
        await WaitForAnimationAsync(_filter);

        if (_viewModel.FilterVisible)
            return;

        if (string.IsNullOrEmpty(_viewModel.NameFilter))
        {
            _filterCoordinator.CancelPending();
            return;
        }

        _viewModel.NameFilter = string.Empty;
        _filterCoordinator.CancelPending();
        _ = ApplyFilterRealtimeAsync(CancellationToken.None);
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
        var modifiers = e.KeyModifiers;
        if (modifiers == KeyModifiers.Control && e.Key == Key.F)
        {
            if (!IsHotkeyDebounced(ref _lastSearchHotkeyTimestamp))
                ScheduleHotkeyToggle(TextToolKind.Search);

            e.Handled = true;
            return true;
        }

        if (modifiers != (KeyModifiers.Control | KeyModifiers.Shift) || e.Key != Key.N)
            return false;

        if (!IsHotkeyDebounced(ref _lastFilterHotkeyTimestamp))
            ScheduleHotkeyToggle(TextToolKind.Filter);

        e.Handled = true;
        return true;
    }

    public bool TryHandleActiveToolKey(KeyEventArgs e)
    {
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
        var stopwatch = Stopwatch.StartNew();
        var version = 0;
        try
        {
            if (string.IsNullOrEmpty(_getCurrentPath()))
            {
                _viewModel.UpdateFilterMatchSummary(0);
                _viewModel.SetFilterInProgress(false);
                return;
            }

            var query = _viewModel.NameFilter?.Trim();
            var hasQuery = !string.IsNullOrWhiteSpace(query);
            version = Interlocked.Increment(ref _filterApplyVersion);

            if (hasQuery && _filterExpansionSnapshot is null)
                _filterExpansionSnapshot = CaptureExpandedNodes();

            cancellationToken.ThrowIfCancellationRequested();
            await _refreshTreeAsync(true, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (version != Volatile.Read(ref _filterApplyVersion))
                return;

            var matchCount = hasQuery ? ApplyNameFilterPresentation(query!) : 0;
            if (!hasQuery)
                _viewModel.UpdateFilterMatchSummary(0);

            if (!hasQuery && _filterExpansionSnapshot is not null)
            {
                RestoreExpandedNodes(_filterExpansionSnapshot);
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
            await _showErrorAsync(ex);
        }
        finally
        {
            if (version == 0 || version == Volatile.Read(ref _filterApplyVersion))
                _viewModel.SetFilterInProgress(false);
        }
    }

    public int ApplyNameFilterPresentation(string filterQuery)
    {
        var currentTree = _getCurrentTree();
        var matchCount = currentTree is null
            ? 0
            : NameFilterMatchCounter.CountMatchesUnderRoot(currentTree.Root, filterQuery);
        _viewModel.UpdateFilterMatchSummary(matchCount);
        _searchCoordinator.UpdateHighlights(filterQuery);

        using (TreeNodeViewModel.BeginPreserveDescendantExpansionStateScope())
        {
            TreeSearchEngine.ApplySmartExpandForFilter(
                _viewModel.TreeNodes,
                filterQuery,
                node => node.DisplayName,
                node => node.Children,
                (node, expanded) => node.IsExpanded = expanded);
        }

        return matchCount;
    }

    public void ReapplyActiveTreeQueryPresentation()
    {
        var filterQuery = _viewModel.NameFilter?.Trim();
        if (!string.IsNullOrWhiteSpace(filterQuery))
        {
            ApplyNameFilterPresentation(filterQuery);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_viewModel.SearchQuery))
            _searchCoordinator.UpdateSearchMatches();
    }

    public void SuspendForPreviewOnly()
    {
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
        ResetAnimationState();
        if (_viewModel.SearchVisible && _viewModel.FilterVisible)
            _viewModel.FilterVisible = false;

        SetForcedVisibility(_search, _viewModel.SearchVisible);
        SetForcedVisibility(_filter, _viewModel.FilterVisible);
    }

    public async Task PrepareForProjectLoadAsync()
    {
        var searchWasVisible = IsEffectivelyVisible(_search);
        var filterWasVisible = IsEffectivelyVisible(_filter);

        InvalidateFocusRequests();
        using (SuppressRealtimeUpdates())
        {
            _viewModel.SearchVisible = false;
            _viewModel.FilterVisible = false;
            _search.ClosePending = false;
            _filter.ClosePending = false;

            if (searchWasVisible && !_search.IsAnimating)
                _ = AnimateAsync(_search, show: false);
            if (filterWasVisible && !_filter.IsAnimating)
                _ = AnimateAsync(_filter, show: false);

            if (searchWasVisible || filterWasVisible)
                await WaitForAnimationAsync(_search);

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
        if (normalizedQuery.Length == 0 || !_viewModel.IsSearchFilterAvailable)
            return;

        if (IsEffectivelyVisible(_search))
            await CloseSearchAsync(focusTree: false);

        ShowFilter(focusInput: false, selectAllOnFocus: false);
        using (SuppressRealtimeUpdates())
        {
            _viewModel.SearchQuery = string.Empty;
            _viewModel.NameFilter = normalizedQuery;
        }

        _filterCoordinator.CancelPending();
        _viewModel.SetFilterInProgress(true);
        await ApplyFilterRealtimeAsync(CancellationToken.None);
    }

    public async Task ApplyStartupSearchAsync(string query)
    {
        var normalizedQuery = query.Trim();
        if (normalizedQuery.Length == 0 || !_viewModel.IsSearchFilterAvailable)
            return;

        if (IsEffectivelyVisible(_filter))
            await CloseFilterAsync(focusTree: false);

        ShowSearch(focusInput: false, selectAllOnFocus: false);
        using (SuppressRealtimeUpdates())
        {
            _viewModel.NameFilter = string.Empty;
            _viewModel.SearchQuery = normalizedQuery;
        }

        _searchCoordinator.CancelPending();
        _searchCoordinator.UpdateSearchMatches();
    }

    public void ClearProjectState()
    {
        _searchCoordinator.ClearSearchState();
        _filterCoordinator.CancelPending();
        _filterExpansionSnapshot = null;
        _suspendedTool = SuspendedTextTool.None;
        Interlocked.Increment(ref _filterApplyVersion);
        _resetInteractiveFilterCache();
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
        _searchCoordinator.Dispose();
        _filterCoordinator.Dispose();
        _filterExpansionSnapshot = null;
    }

    private void ApplyFilterRealtime(CancellationToken cancellationToken) =>
        _ = ApplyFilterRealtimeAsync(cancellationToken);

    private void Show(TextToolState tool, bool focusInput, bool selectAllOnFocus)
    {
        if (!_viewModel.IsProjectLoaded || !IsAvailable(tool) || tool.IsAnimating)
            return;

        SuppressAccent(tool);
        SetLogicalVisibility(tool, true);
        _ = AnimateAsync(tool, show: true);

        if (!focusInput)
            return;

        var focusVersion = Interlocked.Increment(ref tool.FocusVersion);
        _ = FocusAfterOpenAsync(tool, selectAllOnFocus, focusVersion);
    }

    private void PrepareForClose(TextToolState tool, bool focusTree)
    {
        if (tool.GetInput()?.IsFocused == true)
            _treeView.Focus();

        SuppressAccent(tool);
        SetLogicalVisibility(tool, false);
        if (tool.IsAnimating)
            tool.ClosePending = true;
        else
            _ = AnimateAsync(tool, show: false);

        if (focusTree)
            _treeView.Focus();
    }

    private async Task AnimateAsync(TextToolState tool, bool show)
    {
        if (tool.IsAnimating)
            return;

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
            tool.Transform.Y = 0.0;
            tool.Surface.Opacity = show ? 1.0 : 0.0;
            await WaitForAnimationAsync(tool);

            if (!show && !IsLogicallyVisible(tool))
                ForceHidden(tool);
            else if (show && IsLogicallyVisible(tool))
                _ = RestoreAccentAfterOpenAsync(tool);

            await RefreshVisualHostAsync();
        }
        finally
        {
            tool.IsAnimating = false;
            if (tool.ClosePending && !IsLogicallyVisible(tool))
            {
                tool.ClosePending = false;
                _ = AnimateAsync(tool, show: false);
            }
        }
    }

    private async Task FocusAfterOpenAsync(
        TextToolState tool,
        bool selectAllOnFocus,
        int focusVersion)
    {
        await WaitForAnimationAsync(tool);
        if (!IsLogicallyVisible(tool) || !IsAvailable(tool) || !IsFocusRequestCurrent(tool, focusVersion))
            return;

        const int maxAttempts = 4;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (!IsFocusRequestCurrent(tool, focusVersion))
                return;

            var focused = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var input = tool.GetInput();
                if (!IsInputReady(tool, input))
                    return false;

                FocusInput(input!, selectAllOnFocus);
                return input!.IsFocused;
            }, DispatcherPriority.Input);

            if (focused)
                return;

            await DispatcherTaskSchedulerProvider.YieldAsync(DispatcherPriority.Background);
        }
    }

    private static void FocusInput(TextBox input, bool selectAllOnFocus)
    {
        input.Focus();
        if (selectAllOnFocus)
        {
            input.SelectAll();
            return;
        }

        PlaceCaretAtEnd(input);
        _ = input.Dispatcher.InvokeAsync(() => PlaceCaretAtEnd(input), DispatcherPriority.Input);
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
        await DispatcherTaskSchedulerProvider.YieldAsync(DispatcherPriority.Render);
        if (IsLogicallyVisible(tool) && IsAvailable(tool))
            RestoreAccent(tool);
    }

    private async Task RefreshVisualHostAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            InvalidateTool(_search);
            InvalidateTool(_filter);
            _window.InvalidateVisual();
        }, DispatcherPriority.Render);

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
        ref var pending = ref kind == TextToolKind.Search
            ? ref _pendingSearchHotkeyToggle
            : ref _pendingFilterHotkeyToggle;
        if (Interlocked.CompareExchange(ref pending, 1, 0) != 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
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

    private HashSet<string> CaptureExpandedNodes()
    {
        var result = new HashSet<string>(PathComparer.Default);
        TreeNodeViewModel.ForEachDescendant(_viewModel.TreeNodes, node =>
        {
            if (node.IsExpanded)
                result.Add(node.FullPath);
        });
        return result;
    }

    private void RestoreExpandedNodes(HashSet<string> expandedPaths)
    {
        using (TreeNodeViewModel.BeginPreserveDescendantExpansionStateScope())
        {
            TreeNodeViewModel.ForEachDescendant(
                _viewModel.TreeNodes,
                node => node.IsExpanded = expandedPaths.Contains(node.FullPath));
        }

        if (_viewModel.TreeNodes.FirstOrDefault() is { } root && !root.IsExpanded)
            root.IsExpanded = true;
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
                Easing = new CubicEaseOut()
            },
            new ThicknessTransition
            {
                Property = Layoutable.MarginProperty,
                Duration = ToolBarAnimationDuration,
                Easing = new CubicEaseOut()
            }
        ];
        tool.Surface.Transitions ??=
        [
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = ToolBarAnimationDuration,
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
        tool.Transform.Y = 0;
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

    private static Task WaitForAnimationAsync(TextToolState _) =>
        Task.Delay(ToolBarAnimationDuration + UiTimingProfile.AnimationSettleBuffer);

    private void ResetAnimationState()
    {
        _search.IsAnimating = false;
        _search.ClosePending = false;
        _filter.IsAnimating = false;
        _filter.ClosePending = false;
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
        public bool ClosePending { get; set; }
        public int FocusVersion;
    }

    private sealed class RealtimeSuppressionLease(SearchFilterInteractionController owner) : IDisposable
    {
        private SearchFilterInteractionController? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.ReleaseRealtimeSuppression();
    }
}
