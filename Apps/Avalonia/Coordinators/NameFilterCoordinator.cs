namespace DevProjex.Avalonia.Coordinators;

public sealed class NameFilterCoordinator(
    Action<CancellationToken> applyFilterRealtime,
    Func<bool>? hasActiveQuery = null,
    Action<bool>? onFilterStateChanged = null) : IDisposable
{
    private static readonly TimeSpan DebounceDelay = UiTimingProfile.Scale(TimeSpan.FromMilliseconds(360));
    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _filterCts;
    private readonly object _ctsLock = new();
    private int _debounceVersion;

    private async Task RunDebounceAsync(int version, CancellationToken token)
    {
        try
        {
            // Keep first keystrokes smooth while avoiding background timer wakeups.
            await Task.Delay(DebounceDelay, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested || version != Volatile.Read(ref _debounceVersion))
            return;

        CancellationToken applyToken;
        lock (_ctsLock)
        {
            _filterCts?.Cancel();
            _filterCts?.Dispose();
            _filterCts = new CancellationTokenSource();
            applyToken = _filterCts.Token;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!applyToken.IsCancellationRequested)
                applyFilterRealtime(applyToken);
        }, DispatcherPriority.Background);
    }

    public void OnNameFilterChanged()
    {
        onFilterStateChanged?.Invoke(hasActiveQuery?.Invoke() == true);

        CancellationToken token;
        int version;

        lock (_ctsLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            token = _debounceCts.Token;
            version = Interlocked.Increment(ref _debounceVersion);
        }

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

        onFilterStateChanged?.Invoke(false);
    }

    public void Dispose()
    {
        lock (_ctsLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
            _filterCts?.Cancel();
            _filterCts?.Dispose();
            _filterCts = null;
        }

        onFilterStateChanged?.Invoke(false);
    }
}
