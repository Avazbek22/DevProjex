using System.Diagnostics;
using DevProjex.Infrastructure.ProjectProfiles;

namespace DevProjex.Avalonia.Coordinators;

internal enum SelectionPersistencePhase
{
    Idle,
    Pending,
    Saving,
    Deferred,
    Failed
}

internal readonly record struct SelectionPersistenceState(
    SelectionPersistencePhase Phase,
    string? FailureReason = null);

internal sealed class TreeSelectionProfilePersistenceCoordinator : IDisposable
{
    private static readonly TimeSpan PersistenceDelay =
        UiTimingProfile.Scale(TimeSpan.FromSeconds(2));

    private readonly Func<string, IReadOnlyCollection<string>?, CancellationToken,
        Task<ProjectProfilePersistenceResult>> _persistAsync;
    private readonly Func<CancellationToken, Task> _delayAsync;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _sync = new();
    private PendingSelectionWrite? _pending;
    private CancellationTokenSource? _delayCts;
    private SelectionPersistenceState _state = new(SelectionPersistencePhase.Idle);
    private long _version;
    private int _activeWriteCalls;
    private int _disposed;

    public event EventHandler? StateChanged;

    public SelectionPersistenceState State
    {
        get
        {
            lock (_sync)
                return _state;
        }
    }

    public TreeSelectionProfilePersistenceCoordinator(
        Func<string, IReadOnlyCollection<string>?, CancellationToken,
            Task<ProjectProfilePersistenceResult>> persistAsync)
        : this(
            persistAsync,
            static cancellationToken => Task.Delay(PersistenceDelay, cancellationToken))
    {
    }

    public TreeSelectionProfilePersistenceCoordinator(
        Func<string, IReadOnlyCollection<string>?, CancellationToken, Task> persistAsync)
        : this(
            (projectPath, selectedPaths, cancellationToken) => PersistAndReportSavedAsync(
                persistAsync,
                projectPath,
                selectedPaths,
                cancellationToken))
    {
    }

    internal TreeSelectionProfilePersistenceCoordinator(
        Func<string, IReadOnlyCollection<string>?, CancellationToken,
            Task<ProjectProfilePersistenceResult>> persistAsync,
        Func<CancellationToken, Task> delayAsync)
    {
        _persistAsync = persistAsync ?? throw new ArgumentNullException(nameof(persistAsync));
        _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
    }

    internal TreeSelectionProfilePersistenceCoordinator(
        Func<string, IReadOnlyCollection<string>?, CancellationToken, Task> persistAsync,
        Func<CancellationToken, Task> delayAsync)
        : this(
            (projectPath, selectedPaths, cancellationToken) => PersistAndReportSavedAsync(
                persistAsync,
                projectPath,
                selectedPaths,
                cancellationToken),
            delayAsync)
    {
    }

    private static async Task<ProjectProfilePersistenceResult> PersistAndReportSavedAsync(
        Func<string, IReadOnlyCollection<string>?, CancellationToken, Task> persistAsync,
        string projectPath,
        IReadOnlyCollection<string>? selectedPaths,
        CancellationToken cancellationToken)
    {
        await persistAsync(projectPath, selectedPaths, cancellationToken).ConfigureAwait(false);
        return ProjectProfilePersistenceResult.Saved();
    }

    public void Schedule(string projectPath, IReadOnlyCollection<string>? selectedPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        if (Volatile.Read(ref _disposed) != 0)
            return;

        CancellationToken token;
        long version;
        EventHandler? stateChanged;
        lock (_sync)
        {
            if (_disposed != 0)
                return;

            _delayCts?.Cancel();
            _delayCts?.Dispose();
            _delayCts = new CancellationTokenSource();
            token = _delayCts.Token;
            version = checked(++_version);
            _pending = new PendingSelectionWrite(
                Path.GetFullPath(projectPath),
                selectedPaths?.ToArray(),
                version);
            stateChanged = SetStateLocked(new SelectionPersistenceState(
                SelectionPersistencePhase.Pending));
        }

        stateChanged?.Invoke(this, EventArgs.Empty);
        _ = PersistAfterDelayAsync(version, token);
    }

    public async Task<bool> FlushAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            CancellationTokenSource? delayCancellation;
            lock (_sync)
            {
                delayCancellation = _delayCts;
                _delayCts = null;
                _version = checked(_version + 1);
            }

            try
            {
                delayCancellation?.Cancel();
            }
            finally
            {
                delayCancellation?.Dispose();
            }

            if (!await PersistPendingAsync(expectedVersion: null, cancellationToken).ConfigureAwait(false))
                return false;

            lock (_sync)
            {
                if (_disposed != 0 || _pending is null)
                    return true;
            }
        }
    }

    public bool Flush(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            return FlushAsync(cancellation.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return false;
        }
    }

    public void CancelPending()
    {
        EventHandler? stateChanged;
        lock (_sync)
        {
            _delayCts?.Cancel();
            _delayCts?.Dispose();
            _delayCts = null;
            _pending = null;
            _version = checked(_version + 1);
            stateChanged = SetStateLocked(new SelectionPersistenceState(
                SelectionPersistencePhase.Idle));
        }
        stateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task CancelPendingAndDrainAsync(CancellationToken cancellationToken = default)
    {
        CancelPending();
        if (await TryEnterWriteAsync(cancellationToken).ConfigureAwait(false))
            ReleaseWriteGate();
    }

    private async Task PersistAfterDelayAsync(long version, CancellationToken cancellationToken)
    {
        try
        {
            await _delayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await PersistPendingAsync(version, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> PersistPendingAsync(
        long? expectedVersion,
        CancellationToken cancellationToken)
    {
        if (!await TryEnterWriteAsync(cancellationToken).ConfigureAwait(false))
            return true;

        try
        {
            PendingSelectionWrite? pending;
            lock (_sync)
            {
                pending = _pending;
                if (_disposed != 0 ||
                    pending is null ||
                    (expectedVersion.HasValue && pending.Version != expectedVersion.Value))
                {
                    return true;
                }
            }

            PublishStateForVersion(
                pending.Version,
                new SelectionPersistenceState(SelectionPersistencePhase.Saving));

            try
            {
                var result = await _persistAsync(
                    pending.ProjectPath,
                    pending.SelectedPaths,
                    cancellationToken).ConfigureAwait(false);
                if (!result.Completed)
                {
                    PublishStateForVersion(
                        pending.Version,
                        new SelectionPersistenceState(
                            result.Disposition == ProjectProfilePersistenceDisposition.Deferred
                                ? SelectionPersistencePhase.Deferred
                                : SelectionPersistencePhase.Failed,
                            result.Reason));
                    return false;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Trace.TraceWarning(
                    "Project tree selection persistence failed: {0}",
                    exception.GetType().Name);
                PublishStateForVersion(
                    pending.Version,
                    new SelectionPersistenceState(
                        SelectionPersistencePhase.Failed,
                        exception.Message));
                return false;
            }

            EventHandler? stateChanged = null;
            lock (_sync)
            {
                if (_pending?.Version == pending.Version)
                {
                    _pending = null;
                    stateChanged = SetStateLocked(new SelectionPersistenceState(
                        SelectionPersistencePhase.Idle));
                }
            }
            stateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally
        {
            ReleaseWriteGate();
        }
    }

    private async Task<bool> TryEnterWriteAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_disposed != 0)
                return false;

            _activeWriteCalls++;
        }

        try
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            ExitWriteCall();
            throw;
        }
    }

    private void ReleaseWriteGate()
    {
        _writeGate.Release();
        ExitWriteCall();
    }

    private void ExitWriteCall()
    {
        bool disposeWriteGate;
        lock (_sync)
        {
            _activeWriteCalls--;
            disposeWriteGate = _disposed != 0 && _activeWriteCalls == 0;
        }

        if (disposeWriteGate)
            _writeGate.Dispose();
    }

    private void PublishStateForVersion(long version, SelectionPersistenceState state)
    {
        EventHandler? stateChanged;
        lock (_sync)
        {
            if (_disposed != 0 || _pending?.Version != version)
                return;
            stateChanged = SetStateLocked(state);
        }
        stateChanged?.Invoke(this, EventArgs.Empty);
    }

    private EventHandler? SetStateLocked(SelectionPersistenceState state)
    {
        if (_state == state)
            return null;
        _state = state;
        return StateChanged;
    }

    public void Dispose()
    {
        bool disposeWriteGate;
        lock (_sync)
        {
            if (_disposed != 0)
                return;

            _disposed = 1;
            disposeWriteGate = _activeWriteCalls == 0;
        }

        CancelPending();
        // Queued calls also retain the gate until their wait and release have completed.
        if (disposeWriteGate)
            _writeGate.Dispose();
    }

    private sealed record PendingSelectionWrite(
        string ProjectPath,
        IReadOnlyCollection<string>? SelectedPaths,
        long Version);
}
