using Avalonia.Threading;

namespace DevProjex.Avalonia.Coordinators;

internal sealed class SelectionRefreshStatusLease : IAsyncDisposable
{
    private static readonly TimeSpan VisibleDelay = TimeSpan.FromMilliseconds(220);

    private readonly MainWindowViewModel _viewModel;
    private readonly StatusOperationCoordinator? _statusOperations;
    private readonly Action? _cancelAction;
    private readonly CancellationTokenSource _delayCts;
    private readonly object _sync = new();
    private readonly Task _startTask;
    private bool _disposed;
    private long? _operationId;

    private SelectionRefreshStatusLease(
        MainWindowViewModel viewModel,
        StatusOperationCoordinator? statusOperations,
        Action? cancelAction,
        CancellationToken cancellationToken)
    {
        _viewModel = viewModel;
        _statusOperations = statusOperations;
        _cancelAction = cancelAction;
        _delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _startTask = statusOperations is null ? Task.CompletedTask : StartAfterDelayAsync();
    }

    public static SelectionRefreshStatusLease Start(
        MainWindowViewModel viewModel,
        StatusOperationCoordinator? statusOperations,
        Action? cancelAction,
        CancellationToken cancellationToken) =>
        new(viewModel, statusOperations, cancelAction, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        long? operationId;
        lock (_sync)
        {
            _disposed = true;
            operationId = _operationId;
        }

        await CancelDelayAndWaitAsync().ConfigureAwait(false);

        if (operationId.HasValue && _statusOperations is not null)
        {
            await Dispatcher.UIThread.InvokeAsync(
                () => _statusOperations.Complete(operationId),
                DispatcherPriority.Background);
        }

        _delayCts.Dispose();
    }

    private async Task StartAfterDelayAsync()
    {
        try
        {
            await Task.Delay(VisibleDelay, _delayCts.Token).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(TryBeginOperation, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
            // Fast refreshes complete before the status bar is worth showing.
        }
    }

    private void TryBeginOperation()
    {
        if (_statusOperations is null)
            return;

        lock (_sync)
        {
            if (_disposed || _delayCts.IsCancellationRequested || _viewModel.StatusBusy)
                return;

            _operationId = _statusOperations.Begin(
                _viewModel.StatusOperationUpdatingOptions,
                indeterminate: true,
                operationType: StatusOperationType.SelectionRefresh,
                cancelAction: _cancelAction);
        }
    }

    private async Task CancelDelayAndWaitAsync()
    {
        try
        {
            _delayCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            await _startTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
