namespace DevProjex.Avalonia.Coordinators;

public sealed class NameFilterCoordinator : IDisposable
{
    private static readonly TimeSpan DebounceDelay = UiTimingProfile.Scale(TimeSpan.FromMilliseconds(360));
    private readonly Action<CancellationToken> _applyFilterRealtime;
    private readonly Func<bool>? _hasActiveQuery;
    private readonly Action<bool>? _onFilterStateChanged;
    private readonly Func<CancellationToken, Task> _delayAsync;
    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _filterCts;
    private readonly object _ctsLock = new();
    private int _debounceVersion;
    private int _disposed;

    public NameFilterCoordinator(
        Action<CancellationToken> applyFilterRealtime,
        Func<bool>? hasActiveQuery = null,
        Action<bool>? onFilterStateChanged = null)
        : this(
            applyFilterRealtime,
            hasActiveQuery,
            onFilterStateChanged,
            static token => Task.Delay(DebounceDelay, token))
    {
    }

    internal NameFilterCoordinator(
        Action<CancellationToken> applyFilterRealtime,
        Func<bool>? hasActiveQuery,
        Action<bool>? onFilterStateChanged,
        Func<CancellationToken, Task> delayAsync)
    {
        _applyFilterRealtime = applyFilterRealtime;
        _hasActiveQuery = hasActiveQuery;
        _onFilterStateChanged = onFilterStateChanged;
        _delayAsync = delayAsync;
    }

    private async Task RunDebounceAsync(int version, CancellationToken token)
    {
        try
        {
            // Keep first keystrokes smooth while avoiding background timer wakeups.
            await _delayAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested ||
            version != Volatile.Read(ref _debounceVersion) ||
            Volatile.Read(ref _disposed) != 0)
            return;

        CancellationToken applyToken;
        lock (_ctsLock)
        {
            if (_disposed != 0)
                return;

            _filterCts?.Cancel();
            _filterCts?.Dispose();
            _filterCts = new CancellationTokenSource();
            applyToken = _filterCts.Token;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!applyToken.IsCancellationRequested)
                _applyFilterRealtime(applyToken);
        }, DispatcherPriority.Background);
    }

    public void OnNameFilterChanged()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        CancellationToken token;
        int version;

        lock (_ctsLock)
        {
            if (_disposed != 0)
                return;

            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            token = _debounceCts.Token;
            version = Interlocked.Increment(ref _debounceVersion);
        }

        _onFilterStateChanged?.Invoke(_hasActiveQuery?.Invoke() == true);
        _ = RunDebounceAsync(version, token);
    }

    /// <summary>
    /// Cancels any pending filter operation.
    /// </summary>
    public void CancelPending()
    {
        lock (_ctsLock)
        {
            _debounceCts?.Cancel();
            _filterCts?.Cancel();
        }

        _onFilterStateChanged?.Invoke(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        lock (_ctsLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
            _filterCts?.Cancel();
            _filterCts?.Dispose();
            _filterCts = null;
        }

        _onFilterStateChanged?.Invoke(false);
    }
}
