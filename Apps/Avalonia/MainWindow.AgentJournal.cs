using DevProjex.Avalonia.Services;
using DevProjex.Infrastructure.AgentJournal;

namespace DevProjex.Avalonia;

public partial class MainWindow
{
    private readonly AgentActivityPreferenceStore _agentActivityPreferenceStore;
    private CancellationTokenSource? _agentActivityRefreshCts;
    private CancellationTokenSource? _agentActivityWatchCts;
    private string? _agentActivitySessionId;
    private long _agentActivityLatestSequence;
    private AgentJournalCall? _agentActivityLatestCall;
    private string? _agentDeliveryBaselineSessionId;
    private long _agentDeliveryBaselineSequence;
    private bool _captureAgentDeliveryBaseline;
    private int _agentActivityRefreshQueued;
    private IReadOnlyDictionary<string, int> _agentDeliveryCounts =
        new Dictionary<string, int>(PathComparer.Default);

    private void InitializeAgentActivity()
    {
        _viewModel.IsAgentActivityEnabled = _agentActivityPreferenceStore.Load();
        if (_viewModel.IsAgentActivityEnabled)
            RefreshAgentActivityPresentation();
    }

    private void OnToggleAgentActivity(object? sender, RoutedEventArgs e)
    {
        _viewModel.IsAgentActivityEnabled = !_viewModel.IsAgentActivityEnabled;
        _agentActivityPreferenceStore.TrySave(_viewModel.IsAgentActivityEnabled);
        if (_viewModel.IsAgentActivityEnabled)
            RefreshAgentActivityPresentation();
        else
            ClearAgentActivityPresentation();
        e.Handled = true;
    }

    private void RefreshAgentActivityPresentation()
    {
        if (!_viewModel.IsAgentActivityEnabled ||
            !_viewModel.IsProjectLoaded ||
            string.IsNullOrWhiteSpace(_currentPath))
        {
            ClearAgentActivityPresentation(preserveEnabledState: true);
            return;
        }

        var refreshCts = CancellationTokenSource.CreateLinkedTokenSource(
            _windowLifetimeCts?.Token ?? CancellationToken.None);
        var previous = Interlocked.Exchange(ref _agentActivityRefreshCts, refreshCts);
        previous?.Cancel();
        previous?.Dispose();
        _ = RefreshAgentActivityAsync(Path.GetFullPath(_currentPath), refreshCts);
    }

    private async Task RefreshAgentActivityAsync(
        string projectRoot,
        CancellationTokenSource refreshCts)
    {
        try
        {
            var sessions = await _agentJournalReader.ListSessionsAsync(
                projectRoot,
                cancellationToken: refreshCts.Token);
            var session = sessions
                .Where(static candidate =>
                    candidate.IsLive &&
                    candidate.Mode == AgentJournalMode.Live &&
                    candidate.EndedUtc is null)
                .OrderByDescending(static candidate => candidate.StartedUtc)
                .ThenBy(static candidate => candidate.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            if (session is null)
            {
                await Dispatcher.UIThread.InvokeAsync(
                    () => ClearAgentActivityPresentation(preserveEnabledState: true));
                return;
            }

            IReadOnlyList<AgentJournalCall> calls;
            AgentJournalCall? snapshotLatestCall = null;
            var sameSession = string.Equals(
                _agentActivitySessionId,
                session.Id,
                StringComparison.Ordinal);
            var cursorSequence = sameSession
                ? Volatile.Read(ref _agentActivityLatestSequence)
                : 0;
            var requiresReset = false;
            if (_agentJournalReader is IAgentJournalActivityReader activityReader)
            {
                var activity = await activityReader.ReadActivityAsync(
                    session.Id,
                    cursorSequence,
                    refreshCts.Token);
                if (activity is null)
                    return;
                session = activity.Session;
                calls = activity.AppendedCalls;
                snapshotLatestCall = activity.LatestCall;
                cursorSequence = Math.Max(
                    cursorSequence,
                    snapshotLatestCall?.Sequence ?? 0);
                if (calls.Count > 0)
                {
                    cursorSequence = Math.Max(
                        cursorSequence,
                        calls.Max(static call => call.Sequence));
                }
                requiresReset = activity.RequiresReset;
                if (requiresReset)
                {
                    calls = await _agentJournalReader.ReadCallsAsync(session.Id, refreshCts.Token);
                    cursorSequence = Math.Max(
                        cursorSequence,
                        calls.Count == 0 ? 0 : calls.Max(static call => call.Sequence));
                }
            }
            else
            {
                calls = await _agentJournalReader.ReadCallsAsync(session.Id, refreshCts.Token);
                cursorSequence = calls.Count == 0
                    ? 0
                    : calls.Max(static call => call.Sequence);
            }
            sameSession = string.Equals(
                _agentActivitySessionId,
                session.Id,
                StringComparison.Ordinal);
            var baselineSequence = string.Equals(
                _agentDeliveryBaselineSessionId,
                session.Id,
                StringComparison.Ordinal)
                ? Volatile.Read(ref _agentDeliveryBaselineSequence)
                : 0;
            if (_captureAgentDeliveryBaseline)
            {
                baselineSequence = cursorSequence;
                _agentDeliveryBaselineSessionId = session.Id;
                _agentDeliveryBaselineSequence = baselineSequence;
            }
            var counts = sameSession && !requiresReset
                ? new Dictionary<string, int>(_agentDeliveryCounts, PathComparer.Default)
                : new Dictionary<string, int>(PathComparer.Default);
            AddDeliveredPathCounts(counts, projectRoot, session, calls, baselineSequence);
            var rootIndex = ResolveAgentJournalRootIndex(projectRoot, session.Roots);
            var latestCall = calls
                .Where(call => MatchesAgentJournalRoot(call, rootIndex, session.Roots.Count))
                .OrderByDescending(static call => call.Sequence)
                .FirstOrDefault();
            if (snapshotLatestCall is not null &&
                MatchesAgentJournalRoot(snapshotLatestCall, rootIndex, session.Roots.Count) &&
                (latestCall is null || snapshotLatestCall.Sequence > latestCall.Sequence))
            {
                latestCall = snapshotLatestCall;
            }
            if (latestCall is null && sameSession && !requiresReset)
                latestCall = _agentActivityLatestCall;
            refreshCts.Token.ThrowIfCancellationRequested();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_viewModel.IsAgentActivityEnabled ||
                    !PathComparer.Default.Equals(projectRoot, _currentPath))
                {
                    return;
                }

                var changedSession = !string.Equals(
                    _agentActivitySessionId,
                    session.Id,
                    StringComparison.Ordinal);
                if (changedSession)
                {
                    ClearAgentDeliveryTrace();
                    if (!string.Equals(_agentDeliveryBaselineSessionId, session.Id, StringComparison.Ordinal))
                    {
                        _agentDeliveryBaselineSessionId = null;
                        _agentDeliveryBaselineSequence = 0;
                    }
                }
                _agentActivitySessionId = session.Id;
                _agentActivityLatestSequence = cursorSequence;
                _agentActivityLatestCall = latestCall;
                _captureAgentDeliveryBaseline = false;
                _agentDeliveryCounts = counts;
                ApplyAgentDeliveryTrace();
                _viewModel.SetAgentActivityText(FormatAgentActivity(session, latestCall));
                if (changedSession)
                    StartAgentActivityWatch(session.Id);
            });
        }
        catch (OperationCanceledException) when (refreshCts.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Agent activity could not be refreshed: {0}", exception.GetType().Name);
        }
        finally
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _agentActivityRefreshCts, null, refreshCts),
                    refreshCts))
            {
                refreshCts.Dispose();
            }
        }
    }

    private void StartAgentActivityWatch(string sessionId)
    {
        _agentActivityWatchCts?.Cancel();
        _agentActivityWatchCts?.Dispose();
        _agentActivityWatchCts = CancellationTokenSource.CreateLinkedTokenSource(
            _windowLifetimeCts?.Token ?? CancellationToken.None);
        _ = WatchAgentActivityAsync(sessionId, _agentActivityWatchCts.Token);
    }

    private async Task WatchAgentActivityAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var change in _agentJournalReader.WatchChangesAsync(sessionId, cancellationToken))
            {
                if (string.Equals(change.SessionId, sessionId, StringComparison.Ordinal))
                    ScheduleAgentActivityRefresh();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Agent activity change stream ended: {0}", exception.GetType().Name);
        }
    }

    private void ScheduleAgentActivityRefresh()
    {
        if (Interlocked.Exchange(ref _agentActivityRefreshQueued, 1) != 0)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _agentActivityRefreshQueued, 0);
            RefreshAgentActivityPresentation();
        }, DispatcherPriority.Background);
    }

    private void ClearAgentActivityPresentation(
        bool preserveEnabledState = false,
        bool preserveDeliveryBaseline = false)
    {
        _agentActivityRefreshCts?.Cancel();
        _agentActivityRefreshCts?.Dispose();
        _agentActivityRefreshCts = null;
        _agentActivityWatchCts?.Cancel();
        _agentActivityWatchCts?.Dispose();
        _agentActivityWatchCts = null;
        _agentActivitySessionId = null;
        _agentActivityLatestSequence = 0;
        _agentActivityLatestCall = null;
        if (!preserveDeliveryBaseline)
        {
            _agentDeliveryBaselineSessionId = null;
            _agentDeliveryBaselineSequence = 0;
        }
        _viewModel.SetAgentActivityText(null);
        ClearAgentDeliveryTrace();
        if (!preserveEnabledState)
            _viewModel.IsAgentActivityEnabled = false;
    }

    private void ResetAgentActivityForProjectOpen()
    {
        _captureAgentDeliveryBaseline = true;
        _agentDeliveryBaselineSessionId = null;
        _agentDeliveryBaselineSequence = 0;
        ClearAgentActivityPresentation(
            preserveEnabledState: true,
            preserveDeliveryBaseline: true);
    }

    private void ClearAgentDeliveryTrace()
    {
        _agentDeliveryCounts = new Dictionary<string, int>(PathComparer.Default);
        TreeNodeViewModel.ForEachRealizedDescendant(
            _viewModel.TreeNodes,
            static node => node.SetAgentDelivery(0, string.Empty));
    }

    private void ApplyAgentDeliveryTrace()
    {
        TreeNodeViewModel.ForEachRealizedDescendant(
            _viewModel.TreeNodes,
            ApplyAgentDeliveryToNode);
    }

    private void ApplyAgentDeliveryToNode(TreeNodeViewModel node)
    {
        var count = _agentDeliveryCounts.TryGetValue(node.FullPath, out var delivered)
            ? delivered
            : 0;
        node.SetAgentDelivery(
            count,
            count == 0
                ? string.Empty
                : _localization.Format("AgentActivity.Tree.ToolTip", count));
    }

    private string FormatAgentActivity(
        AgentJournalSession session,
        AgentJournalCall? latestCall)
    {
        if (latestCall is null)
            return string.Empty;
        var argument = TryResolveActivityArgument(latestCall.Arguments);
        var tool = string.IsNullOrEmpty(argument)
            ? latestCall.Tool
            : $"{latestCall.Tool} «{argument}»";
        return string.Join(
            " · ",
            NormalizeClientName(session.ClientName),
            tool,
            AgentActivityPresentation.FormatCount(
                _localization,
                "AgentActivity.Status.Files",
                latestCall.FilesDelivered),
            AgentActivityPresentation.FormatCount(
                _localization,
                "AgentActivity.Status.Tokens",
                session.Totals.EstimatedTokens),
            AgentActivityPresentation.FormatCount(
                _localization,
                "AgentActivity.Status.Calls",
                session.Totals.Calls));
    }

    private static string TryResolveActivityArgument(IReadOnlyDictionary<string, string> arguments)
    {
        foreach (var key in new[] { "pattern", "query", "symbol", "path", "focus" })
        {
            if (!arguments.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                continue;
            var collapsed = string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
            return collapsed.Length <= 40 ? collapsed : $"{collapsed[..39]}…";
        }
        return string.Empty;
    }

    private static string NormalizeClientName(string clientName)
    {
        if (string.IsNullOrWhiteSpace(clientName))
            return "MCP";
        return clientName.Equals("codex", StringComparison.OrdinalIgnoreCase)
            ? "Codex"
            : clientName.Equals("claude-code", StringComparison.OrdinalIgnoreCase)
                ? "Claude Code"
                : clientName;
    }

    private static void AddDeliveredPathCounts(
        Dictionary<string, int> counts,
        string projectRoot,
        AgentJournalSession session,
        IReadOnlyList<AgentJournalCall> calls,
        long baselineSequence)
    {
        var rootIndex = ResolveAgentJournalRootIndex(projectRoot, session.Roots);

        foreach (var call in calls)
        {
            if (call.Sequence <= baselineSequence ||
                !MatchesAgentJournalRoot(call, rootIndex, session.Roots.Count))
                continue;
            foreach (var path in call.DeliveredPaths)
                AddDeliveredPath(counts, projectRoot, path, 1);
        }
    }

    private static int ResolveAgentJournalRootIndex(
        string projectRoot,
        IReadOnlyList<AgentJournalRoot> roots)
    {
        for (var index = 0; index < roots.Count; index++)
            if (PathComparer.Default.Equals(PathUtility.Normalize(projectRoot), PathUtility.Normalize(roots[index].ConfiguredPath)))
                return index;
        return -1;
    }

    private static bool MatchesAgentJournalRoot(
        AgentJournalCall call,
        int rootIndex,
        int rootCount) =>
        rootIndex >= 0 &&
        (call.RootIndex == rootIndex || rootCount == 1 && call.RootIndex is null);

    private static void AddDeliveredPath(
        Dictionary<string, int> counts,
        string projectRoot,
        string path,
        long calls)
    {
        if (string.IsNullOrWhiteSpace(path) || calls <= 0)
            return;
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(projectRoot, path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }
        var relative = Path.GetRelativePath(projectRoot, fullPath);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            return;
        var increment = (int)Math.Min(int.MaxValue, calls);
        counts[fullPath] = counts.TryGetValue(fullPath, out var existing)
            ? (int)Math.Min(int.MaxValue, (long)existing + increment)
            : increment;
    }
}
