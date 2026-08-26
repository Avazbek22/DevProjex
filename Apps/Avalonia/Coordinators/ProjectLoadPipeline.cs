namespace DevProjex.Avalonia.Coordinators;

internal sealed class ProjectLoadPipeline(
    IProjectLoadPipelineHost host,
    StatusOperationCoordinator statusOperations) : IDisposable
{
    private CancellationTokenSource? _activeLoadCts;
	private readonly SemaphoreSlim _loadGate = new(1, 1);
	private readonly object _lifetimeGate = new();
	private long _latestRequestId;
	private int _activeCalls;
	private bool _disposed;
	private bool _loadGateDisposed;

    public async Task OpenFolderAsync(
        string path,
        bool fromDialog,
        bool recordRecentFolder)
    {
		if (!TryEnterCall())
			return;

		var gateEntered = false;
		try
		{
			var requestId = Interlocked.Increment(ref _latestRequestId);
			CancelActiveLoad();
			await _loadGate.WaitAsync();
			gateEntered = true;
			if (IsDisposed())
				return;

			if (requestId != Volatile.Read(ref _latestRequestId))
				return;

			await OpenFolderCoreAsync(path, fromDialog, recordRecentFolder);
		}
		finally
		{
			if (gateEntered)
				_loadGate.Release();
			ExitCall();
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
		try
		{
			_activeLoadCts?.Cancel();
		}
		catch (ObjectDisposedException)
		{
			// The load completed while cancellation was being requested.
		}
    }

    public void Dispose()
    {
		var disposeLoadGate = false;
		lock (_lifetimeGate)
		{
			if (_disposed)
				return;

			_disposed = true;
			if (_activeCalls == 0)
			{
				_loadGateDisposed = true;
				disposeLoadGate = true;
			}
		}

		CancelActiveLoad();
		if (disposeLoadGate)
			_loadGate.Dispose();
    }

	private bool TryEnterCall()
	{
		lock (_lifetimeGate)
		{
			if (_disposed)
				return false;

			_activeCalls++;
			return true;
		}
	}

	private bool IsDisposed()
	{
		lock (_lifetimeGate)
			return _disposed;
	}

	private void ExitCall()
	{
		var disposeLoadGate = false;
		lock (_lifetimeGate)
		{
			_activeCalls--;
			if (_disposed && _activeCalls == 0 && !_loadGateDisposed)
			{
				_loadGateDisposed = true;
				disposeLoadGate = true;
			}
		}

		if (disposeLoadGate)
			_loadGate.Dispose();
	}

    private static void DisposeIfCurrent(ref CancellationTokenSource? target, CancellationTokenSource candidate)
    {
        var current = Interlocked.CompareExchange(ref target, null, candidate);
        if (ReferenceEquals(current, candidate))
            candidate.Dispose();
    }
}
