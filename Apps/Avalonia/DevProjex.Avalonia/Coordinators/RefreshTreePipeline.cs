using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia.Coordinators;

internal sealed class RefreshTreePipeline(IRefreshTreePipelineHost host) : IDisposable
{
    private CancellationTokenSource? _refreshCts;
    private readonly TreeNameFilterSession _nameFilterSession = new();

    public async Task<TreeRefreshOutcome> RefreshTreeAsync(
        bool interactiveFilter = false,
        CancellationToken cancellationToken = default)
    {
        var input = host.CaptureTreeRefreshInput();
        if (input is null)
            return TreeRefreshOutcome.Skipped;

        cancellationToken.ThrowIfCancellationRequested();

        using var _ = PerformanceMetrics.Measure("RefreshTreeAsync");

        var refreshCts = ReplaceCancellationSource(ref _refreshCts);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(refreshCts.Token, cancellationToken);
        var linkedToken = linkedCts.Token;

        if (!interactiveFilter)
            host.BeforeFullTreeRefresh();

        try
        {
            BuildTreeSnapshotResult result;
            var usedInMemoryFilter = false;

            if (interactiveFilter && input.InteractiveFilterBaseTree is { } baseTree)
            {
                var filteredResult = await Task.Run(
                    () => _nameFilterSession.Build(baseTree, input.NameFilter, linkedToken),
                    linkedToken);
                result = new BuildTreeSnapshotResult(filteredResult, Inventory: null);
                usedInMemoryFilter = true;
            }
            else
            {
                // Keep expensive filesystem traversal off the UI thread.
                using (PerformanceMetrics.Measure("BuildTree"))
                {
                    result = await Task.Run(
                        () => host.BuildTree(input, linkedToken),
                        linkedToken);
                }
            }

            linkedToken.ThrowIfCancellationRequested();

            if (host.TryHandleRootAccessDenied(input, result.Tree))
                return TreeRefreshOutcome.Skipped;

            TreeNodeViewModel root;
            using (PerformanceMetrics.Measure("BuildTreeViewModel"))
            {
                root = await Task.Run(
                    () => host.BuildTreeViewModel(input, result.Tree),
                    linkedToken);
            }

            linkedToken.ThrowIfCancellationRequested();
            // Selection changes are allowed while the filesystem walk is running. The
            // completed graph is immutable, but it is no longer authoritative when its
            // root/extension/ignore revision has changed, so never publish it.
            if (!host.IsTreeRefreshInputCurrent(input))
                return TreeRefreshOutcome.StaleInput;

            host.ApplyTreeRefreshResult(
                input,
                result,
                root,
                interactiveFilter,
                usedInMemoryFilter,
                cancellationToken);
            return TreeRefreshOutcome.Applied;
        }
        finally
        {
            DisposeIfCurrent(ref _refreshCts, refreshCts);
        }
    }

    public void CancelActiveRefresh()
    {
        _refreshCts?.Cancel();
    }

    public void InvalidateInteractiveFilterCache() => _nameFilterSession.Invalidate();

    public void Dispose()
    {
        CancelAndDispose(ref _refreshCts);
        _nameFilterSession.Invalidate();
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

internal enum TreeRefreshOutcome
{
    Applied,
    Skipped,
    StaleInput
}
