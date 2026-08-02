namespace DevProjex.Avalonia.Coordinators;

internal sealed class ProjectLoadPipeline(
    IProjectLoadPipelineHost host,
    StatusOperationCoordinator statusOperations) : IDisposable
{
    private CancellationTokenSource? _activeLoadCts;

    public async Task OpenFolderAsync(
        string path,
        bool fromDialog,
        bool recordRecentFolder)
    {
        host.CaptureProjectLoadCancellationSnapshot();
        await host.PrepareSearchAndFilterForProjectLoadAsync();
        host.CancelBackgroundMemoryCleanup();
        host.CancelPreviewRefresh();

        var hadLoadedProjectBefore = host.ViewModel.IsProjectLoaded;
        var cachedRepoPathToDeleteOnSuccess = fromDialog ? host.CurrentCachedRepoPath : null;
        var projectLoadCts = ReplaceCancellationSource(ref _activeLoadCts);
        var cancellationToken = projectLoadCts.Token;

        host.ViewModel.StatusMetricsVisible = false;
        var statusOperationId = statusOperations.Begin(
            host.ViewModel.StatusOperationLoadingProject,
            indeterminate: true,
            operationType: StatusOperationType.LoadProject,
            cancelAction: projectLoadCts.Cancel);

        try
        {
            if (hadLoadedProjectBefore)
            {
                // Publish the loading shell first; compacting old project memory can block long
                // enough that doing it before a visible transition feels like a frozen window.
                await host.YieldProjectLoadStartupFrameAsync(cancellationToken);
                host.ClearPreviousProjectState(forceCompactingGc: true);
            }

            host.SetProjectLoadIdentity(path, fromDialog);
            host.UpdateTitle();
            await host.YieldProjectLoadStartupFrameAsync(cancellationToken);

            await host.ReloadProjectAsync(cancellationToken, applyStoredProfile: true);
            if (recordRecentFolder)
                await host.RecordRecentFolderAsync(path, cancellationToken);

            if (fromDialog && !string.IsNullOrWhiteSpace(cachedRepoPathToDeleteOnSuccess))
            {
                await host.DeleteRepositoryDirectoryAsync(cachedRepoPathToDeleteOnSuccess, cancellationToken);
                host.ClearCurrentCachedRepoPath();
            }

            host.ClearProjectLoadCancellation();
            statusOperations.Complete(statusOperationId);
            host.ScheduleProjectLoadMemoryCleanup(hadLoadedProjectBefore);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (statusOperations.IsActive(statusOperationId) &&
                host.TryApplyActiveProjectLoadCancellationFallback())
            {
                host.ShowLoadCanceledToast();
            }

            statusOperations.Complete(statusOperationId);
        }
        catch
        {
            host.ClearProjectLoadCancellation();
            statusOperations.Complete(statusOperationId);
            throw;
        }
        finally
        {
            DisposeIfCurrent(ref _activeLoadCts, projectLoadCts);
        }
    }

    public void CancelActiveLoad()
    {
        _activeLoadCts?.Cancel();
    }

    public void Dispose()
    {
        CancelAndDispose(ref _activeLoadCts);
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
