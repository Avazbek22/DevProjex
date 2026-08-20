namespace DevProjex.Avalonia.Coordinators;

internal sealed class ProjectLoadPipeline(
    IProjectLoadPipelineHost host,
    StatusOperationCoordinator statusOperations) : IDisposable
{
    private CancellationTokenSource? _activeLoadCts;
	private readonly SemaphoreSlim _loadGate = new(1, 1);
	private long _latestRequestId;

    public async Task OpenFolderAsync(
        string path,
        bool fromDialog,
        bool recordRecentFolder)
    {
		var requestId = Interlocked.Increment(ref _latestRequestId);
		CancelActiveLoad();
		await _loadGate.WaitAsync();
		try
		{
			if (requestId != Volatile.Read(ref _latestRequestId))
				return;

			await OpenFolderCoreAsync(path, fromDialog, recordRecentFolder);
		}
		finally
		{
			_loadGate.Release();
		}
	}

	private async Task OpenFolderCoreAsync(
		string path,
		bool fromDialog,
		bool recordRecentFolder)
	{
        host.CaptureProjectLoadCancellationSnapshot();
        var hadLoadedProjectBefore = host.ViewModel.IsProjectLoaded;
        var releaseCachedRepositoryOnSuccess =
            fromDialog && !string.IsNullOrWhiteSpace(host.CurrentCachedRepoPath);
		var projectLoadCts = new CancellationTokenSource();
		Interlocked.Exchange(ref _activeLoadCts, projectLoadCts)?.Dispose();
        var cancellationToken = projectLoadCts.Token;
		var published = false;

        host.ViewModel.StatusMetricsVisible = false;
        var statusOperationId = statusOperations.Begin(
            host.ViewModel.StatusOperationLoadingProject,
            indeterminate: true,
            operationType: StatusOperationType.LoadProject,
            cancelAction: projectLoadCts.Cancel);

        try
        {
			await host.PrepareSearchAndFilterForProjectLoadAsync();
			host.CancelBackgroundMemoryCleanup();
			host.CancelPreviewRefresh();

            if (hadLoadedProjectBefore)
            {
                // Publish the loading shell first; compacting old project memory can block long
                // enough that doing it before a visible transition feels like a frozen window.
                await host.YieldProjectLoadStartupFrameAsync(cancellationToken);
				host.ClearPreviousProjectState(
					forceCompactingGc: true,
					preserveProjectSessions: true);
            }

            host.SetProjectLoadIdentity(path, fromDialog);
            host.UpdateTitle();
            await host.YieldProjectLoadStartupFrameAsync(cancellationToken);

			published = await host.ReloadProjectAsync(cancellationToken, applyStoredProfile: true);
			if (!published)
			{
				host.TryApplyActiveProjectLoadCancellationFallback();
				statusOperations.Complete(statusOperationId);
				return;
			}

			host.ClearProjectLoadCancellation();
			host.ScheduleProjectLoadMemoryCleanup(hadLoadedProjectBefore);
            if (recordRecentFolder)
                await host.RecordRecentFolderAsync(path, cancellationToken);

            if (releaseCachedRepositoryOnSuccess)
                host.ReleaseCurrentRepositorySession();

            statusOperations.Complete(statusOperationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
			var restored = !published &&
				host.TryApplyActiveProjectLoadCancellationFallback();
			if (restored && statusOperations.IsActive(statusOperationId))
			{
				host.ShowLoadCanceledToast();
            }

            statusOperations.Complete(statusOperationId);
        }
        catch
        {
			if (!published)
				host.TryApplyActiveProjectLoadCancellationFallback();
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
		_loadGate.Dispose();
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
