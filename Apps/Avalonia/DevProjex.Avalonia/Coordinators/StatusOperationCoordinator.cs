namespace DevProjex.Avalonia.Coordinators;

public sealed class StatusOperationCoordinator(
    MainWindowViewModel viewModel,
    Func<bool> isBackgroundMetricsActive,
    Func<string> metricsOperationTextProvider,
    TimeSpan? delayedPresentationThreshold = null,
    TimeSpan? extendedDelayedPresentationThreshold = null)
    : IDisposable
{
    internal static readonly TimeSpan DefaultDelayedPresentationThreshold =
        UiTimingProfile.Scale(TimeSpan.FromMilliseconds(250));
    internal static readonly TimeSpan ExtendedDelayedPresentationThreshold =
        UiTimingProfile.Scale(TimeSpan.FromMilliseconds(500));

    private readonly object _sync = new();
    private readonly TimeSpan _delayedPresentationThreshold =
        delayedPresentationThreshold ?? DefaultDelayedPresentationThreshold;
    private readonly TimeSpan _extendedDelayedPresentationThreshold =
        extendedDelayedPresentationThreshold ?? ExtendedDelayedPresentationThreshold;
    private long _operationSequence;
    private long _activeOperationId;
    private StatusOperationType _activeOperationType;
    private Action? _activeCancelAction;
    private CancellationTokenSource? _presentationDelayCts;
    private bool _disposed;

    public long Begin(
        string text,
        bool indeterminate = true,
        StatusOperationType operationType = StatusOperationType.None,
        Action? cancelAction = null,
        StatusOperationPresentation presentation = StatusOperationPresentation.Immediate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var operationId = Interlocked.Increment(ref _operationSequence);

        lock (_sync)
        {
            _activeOperationId = operationId;
            _activeOperationType = operationType;
            _activeCancelAction = cancelAction;
        }

        // A delayed successor must not create a visual gap in an already visible operation
        // chain, such as project loading handing off to metrics calculation.
        var keepExistingPresentation =
            presentation != StatusOperationPresentation.Immediate &&
            viewModel.StatusOperationVisible;
        CancelPresentationDelay();
        viewModel.StatusPresentationReady =
            presentation == StatusOperationPresentation.Immediate ||
            keepExistingPresentation;
        viewModel.StatusOperationText = text;
        viewModel.StatusBusy = true;
        viewModel.StatusProgressIsIndeterminate = indeterminate;
        viewModel.StatusProgressValue = 0;

        if (presentation != StatusOperationPresentation.Immediate &&
            !keepExistingPresentation)
        {
            StartPresentationDelay(
                operationId,
                ResolvePresentationDelay(presentation));
        }

        return operationId;
    }

    public void UpdateText(string text, long? operationId = null)
    {
        if (operationId.HasValue && !IsActive(operationId.Value))
            return;

        viewModel.StatusOperationText = text;
    }

    public void UpdateProgress(double percent, string? text = null, long? operationId = null)
    {
        if (operationId.HasValue && !IsActive(operationId.Value))
            return;

        if (!string.IsNullOrWhiteSpace(text))
            viewModel.StatusOperationText = text;

        viewModel.StatusBusy = true;
        viewModel.StatusProgressIsIndeterminate = false;
        viewModel.StatusProgressValue = Math.Clamp(percent, 0, 100);
    }

    public void Complete(long? operationId = null)
    {
        if (operationId.HasValue && !IsActive(operationId.Value))
            return;

        StatusOperationType activeOperationType;
        lock (_sync)
            activeOperationType = _activeOperationType;

        // Metrics can keep running in the background after user operations finish. In that
        // case the status bar should continue showing the metrics operation instead of
        // flickering to idle and immediately back to busy.
        if (isBackgroundMetricsActive() && activeOperationType == StatusOperationType.MetricsCalculation)
        {
            UpdateText(metricsOperationTextProvider());
            return;
        }

        lock (_sync)
        {
            if (operationId.HasValue && _activeOperationId != operationId.Value)
                return;

            _activeOperationId = 0;
            _activeOperationType = StatusOperationType.None;
            _activeCancelAction = null;
        }

        CancelPresentationDelay();
        viewModel.StatusOperationText = string.Empty;
        viewModel.StatusBusy = false;
        viewModel.StatusPresentationReady = true;
        viewModel.StatusProgressIsIndeterminate = true;
        viewModel.StatusProgressValue = 0;
    }

    public bool IsActive(long operationId)
    {
        lock (_sync)
            return _activeOperationId == operationId;
    }

    public StatusOperationSnapshot GetActiveSnapshot()
    {
        lock (_sync)
        {
            return new StatusOperationSnapshot(
                _activeOperationId,
                _activeOperationType,
                _activeCancelAction);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CancelPresentationDelay();
    }

    private TimeSpan ResolvePresentationDelay(StatusOperationPresentation presentation)
        => presentation == StatusOperationPresentation.ExtendedDelay
            ? _extendedDelayedPresentationThreshold
            : _delayedPresentationThreshold;

    private void StartPresentationDelay(long operationId, TimeSpan delay)
    {
        var delayCts = new CancellationTokenSource();
        _presentationDelayCts = delayCts;
        _ = ShowPresentationAfterDelayAsync(operationId, delay, delayCts);
    }

    private async Task ShowPresentationAfterDelayAsync(
        long operationId,
        TimeSpan delay,
        CancellationTokenSource delayCts)
    {
        try
        {
            await Task.Delay(
                    delay,
                    delayCts.Token)
                .ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(
                () =>
                {
                    if (!_disposed &&
                        !delayCts.IsCancellationRequested &&
                        ReferenceEquals(
                            Volatile.Read(ref _presentationDelayCts),
                            delayCts) &&
                        IsActive(operationId) &&
                        viewModel.StatusBusy)
                    {
                        viewModel.StatusPresentationReady = true;
                    }
                },
                DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _presentationDelayCts,
                        null,
                        delayCts),
                    delayCts))
            {
                delayCts.Dispose();
            }
        }
    }

    private void CancelPresentationDelay()
    {
        var delayCts = Interlocked.Exchange(
            ref _presentationDelayCts,
            null);
        if (delayCts is null)
            return;

        delayCts.Cancel();
        delayCts.Dispose();
    }
}
