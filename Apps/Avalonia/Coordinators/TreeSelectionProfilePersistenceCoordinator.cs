using System.Diagnostics;

namespace DevProjex.Avalonia.Coordinators;

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
    private long _version;
    private int _disposed;

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
        }

        _ = PersistAfterDelayAsync(version, token);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
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

        await PersistPendingAsync(expectedVersion: null, cancellationToken).ConfigureAwait(false);
    }

    public bool Flush(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            FlushAsync(cancellation.Token).GetAwaiter().GetResult();
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return false;
        }
    }

    public void CancelPending()
    {
        lock (_sync)
        {
            _delayCts?.Cancel();
            _delayCts?.Dispose();
            _delayCts = null;
            _pending = null;
            _version = checked(_version + 1);
        }
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
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "Project tree selection persistence failed: {0}",
                exception.GetType().Name);
        }
    }

    private async Task PersistPendingAsync(
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
                    return;
                }
            }

            await _persistAsync(
                pending.ProjectPath,
                pending.SelectedPaths,
                cancellationToken).ConfigureAwait(false);

            lock (_sync)
            {
                if (_pending?.Version == pending.Version)
                    _pending = null;
            }
        }
        finally
        {
            _writeGate.Release();
        }
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
