namespace DevProjex.Avalonia.Coordinators;

/// <summary>
/// Delays option-related status operations so quick refreshes stay visually quiet while long
/// selection or explicit Apply workflows still provide progress and cancellation feedback.
/// </summary>
internal sealed class SelectionRefreshStatusLease : IAsyncDisposable
{
    private static readonly TimeSpan VisibleDelay =
        StatusOperationCoordinator.DefaultDelayedPresentationThreshold;

    private readonly MainWindowViewModel _viewModel;
    private readonly StatusOperationCoordinator? _statusOperations;
    private readonly Action? _cancelAction;
    private readonly string _operationText;
    private readonly StatusOperationType _operationType;
    private readonly bool _canReplaceActiveOperation;
    private readonly CancellationTokenSource _delayCts;
    private readonly object _sync = new();
    private readonly Task _startTask;
    private bool _disposed;
    private long? _operationId;

    private SelectionRefreshStatusLease(
        MainWindowViewModel viewModel,
        StatusOperationCoordinator? statusOperations,
        Action? cancelAction,
        CancellationToken cancellationToken,
        string operationText,
        StatusOperationType operationType,
        bool canReplaceActiveOperation)
    {
        _viewModel = viewModel;
        _statusOperations = statusOperations;
        _cancelAction = cancelAction;
        _operationText = operationText;
        _operationType = operationType;
        _canReplaceActiveOperation = canReplaceActiveOperation;
        _delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _startTask = statusOperations is null ? Task.CompletedTask : StartAfterDelayAsync();
    }

    public static SelectionRefreshStatusLease Start(
        MainWindowViewModel viewModel,
        StatusOperationCoordinator? statusOperations,
        Action? cancelAction,
        CancellationToken cancellationToken) =>
        new(
            viewModel,
            statusOperations,
            cancelAction,
            cancellationToken,
            viewModel.StatusOperationUpdatingOptions,
            StatusOperationType.SelectionRefresh,
            canReplaceActiveOperation: false);

    public static SelectionRefreshStatusLease StartApplyingSettings(
        MainWindowViewModel viewModel,
        StatusOperationCoordinator statusOperations,
        Action cancelAction,
        CancellationToken cancellationToken) =>
        new(
            viewModel,
            statusOperations,
            cancelAction,
            cancellationToken,
            viewModel.StatusOperationApplyingSettings,
            StatusOperationType.ApplySettings,
            canReplaceActiveOperation: true);

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
            if (_disposed || _delayCts.IsCancellationRequested)
                return;

            // Applying settings owns the complete user-requested operation and may replace the
            // narrower background work that it supersedes. It must never steal another explicit
            // user operation such as project loading, Git, or preview generation.
            if (_viewModel.StatusBusy && !CanReplaceActiveOperation())
                return;

            _operationId = _statusOperations.Begin(
                _operationText,
                indeterminate: true,
                operationType: _operationType,
                cancelAction: _cancelAction);
        }
    }

    private bool CanReplaceActiveOperation()
    {
        if (!_canReplaceActiveOperation || _statusOperations is null)
            return false;

        return _statusOperations.GetActiveSnapshot().OperationType is
            StatusOperationType.None or
            StatusOperationType.MetricsCalculation or
            StatusOperationType.SelectionRefresh;
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
