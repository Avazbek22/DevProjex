using System.Diagnostics;

namespace DevProjex.Avalonia.Coordinators;

internal enum SelectionPersistencePhase
{
    Idle,
    Pending,
    Saving,
    Failed
}

internal readonly record struct SelectionPersistenceState(
    SelectionPersistencePhase Phase,
    string? FailureReason = null);

internal sealed class TreeSelectionProfilePersistenceCoordinator : IDisposable
{
    private static readonly TimeSpan PersistenceDelay =
        UiTimingProfile.Scale(TimeSpan.FromSeconds(2));

    private readonly Func<string, IReadOnlyCollection<string>?, CancellationToken, Task> _persistAsync;
    private readonly Func<CancellationToken, Task> _delayAsync;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _sync = new();
    private PendingSelectionWrite? _pending;
    private CancellationTokenSource? _delayCts;
    private SelectionPersistenceState _state = new(SelectionPersistencePhase.Idle);
    private long _version;
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
        Func<string, IReadOnlyCollection<string>?, CancellationToken, Task> persistAsync)
        : this(
            persistAsync,
            static cancellationToken => Task.Delay(PersistenceDelay, cancellationToken))
    {
    }

    internal TreeSelectionProfilePersistenceCoordinator(
        Func<string, IReadOnlyCollection<string>?, CancellationToken, Task> persistAsync,
        Func<CancellationToken, Task> delayAsync)
    {
        _persistAsync = persistAsync ?? throw new ArgumentNullException(nameof(persistAsync));
        _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
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

        return await PersistPendingAsync(expectedVersion: null, cancellationToken).ConfigureAwait(false);
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
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _writeGate.Release();
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
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                await _persistAsync(
                    pending.ProjectPath,
                    pending.SelectedPaths,
                    cancellationToken).ConfigureAwait(false);
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
            _writeGate.Release();
        }
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        CancelPending();
        _writeGate.Dispose();
    }

    private sealed record PendingSelectionWrite(
        string ProjectPath,
        IReadOnlyCollection<string>? SelectedPaths,
        long Version);
}
