using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia;

public partial class MainWindow
{
    private readonly AgentActivityPreferenceStore _agentActivityPreferenceStore;
    private CancellationTokenSource? _agentActivityRefreshCts;
    private CancellationTokenSource? _agentActivityWatchCts;
    private string? _agentActivitySessionId;
    private long _agentActivityLatestSequence;
    private string? _agentDeliveryBaselineSessionId;
    private long _agentDeliveryBaselineSequence;
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

            var calls = await _agentJournalReader.ReadCallsAsync(session.Id, refreshCts.Token);
            var receipt = await _agentJournalReader.ReadReceiptAsync(session.Id, refreshCts.Token);
            var baselineSequence = string.Equals(
                _agentDeliveryBaselineSessionId,
                session.Id,
                StringComparison.Ordinal)
                ? Volatile.Read(ref _agentDeliveryBaselineSequence)
                : 0;
            var counts = BuildDeliveredPathCounts(projectRoot, receipt, calls, baselineSequence);
            var latestCall = calls
                .OrderByDescending(static call => call.Sequence)
                .FirstOrDefault();
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
                _agentActivityLatestSequence = latestCall?.Sequence ?? 0;
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
                    Dispatcher.UIThread.Post(RefreshAgentActivityPresentation, DispatcherPriority.Background);
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
        _agentDeliveryBaselineSessionId = _agentActivitySessionId;
        _agentDeliveryBaselineSequence = _agentActivityLatestSequence;
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
            _localization.Format(
                "AgentActivity.Status.Files",
                AgentJournalPresentation.FormatNumber(latestCall.FilesDelivered)),
            _localization.Format(
                "AgentActivity.Status.Tokens",
                AgentJournalPresentation.FormatNumber(session.Totals.EstimatedTokens)),
            _localization.Format(
                "AgentActivity.Status.Calls",
                AgentJournalPresentation.FormatNumber(session.Totals.Calls)));
    }

    private static string TryResolveActivityArgument(IReadOnlyDictionary<string, string> arguments)
    {
        foreach (var key in new[] { "query", "symbol", "path", "focus" })
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

    private static IReadOnlyDictionary<string, int> BuildDeliveredPathCounts(
        string projectRoot,
        AgentJournalReceipt? receipt,
        IReadOnlyList<AgentJournalCall> calls,
        long baselineSequence)
    {
        var counts = new Dictionary<string, int>(PathComparer.Default);
        if (baselineSequence <= 0 && receipt?.DeliveredPaths.Count > 0)
        {
            foreach (var delivered in receipt.DeliveredPaths)
                AddDeliveredPath(counts, projectRoot, delivered.Path, delivered.Calls);
            return counts;
        }

        foreach (var call in calls)
        {
            if (call.Sequence <= baselineSequence)
                continue;
            foreach (var path in call.DeliveredPaths)
                AddDeliveredPath(counts, projectRoot, path, 1);
        }
        return counts;
    }

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
