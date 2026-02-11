using System;
using System.Threading;
using Avalonia.Threading;

namespace DevProjex.Avalonia.Coordinators;

public sealed class NameFilterCoordinator : IDisposable
{
    private readonly Action<CancellationToken> _applyFilterRealtime;
    private readonly System.Timers.Timer _filterDebounceTimer;
    private CancellationTokenSource? _filterCts;
    private readonly object _ctsLock = new();

    public NameFilterCoordinator(Action<CancellationToken> applyFilterRealtime)
    {
        _applyFilterRealtime = applyFilterRealtime;
        _filterDebounceTimer = new System.Timers.Timer(280)
        {
            AutoReset = false
        };
        _filterDebounceTimer.Elapsed += (_, _) =>
        {
            CancellationToken token;
            lock (_ctsLock)
            {
                // Cancel previous operation
                _filterCts?.Cancel();
                _filterCts?.Dispose();
                _filterCts = new CancellationTokenSource();
                token = _filterCts.Token;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (!token.IsCancellationRequested)
                    _applyFilterRealtime(token);
            });
        };
    }

    public void OnNameFilterChanged()
    {
        _filterDebounceTimer.Stop();
        _filterDebounceTimer.Start();
    }

    /// <summary>
    /// Cancels any pending filter operation.
    /// </summary>
    public void CancelPending()
    {
        lock (_ctsLock)
        {
            _filterCts?.Cancel();
        }
    }

    public void Dispose()
    {
        _filterDebounceTimer.Stop();
        _filterDebounceTimer.Dispose();
        lock (_ctsLock)
        {
            _filterCts?.Cancel();
            _filterCts?.Dispose();
            _filterCts = null;
        }
    }
}
