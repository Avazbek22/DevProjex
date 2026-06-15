namespace DevProjex.Avalonia.Coordinators;

public sealed class StatusOperationCoordinator(
    MainWindowViewModel viewModel,
    Func<bool> isBackgroundMetricsActive,
    Func<string> metricsOperationTextProvider)
{
    private readonly object _sync = new();
    private long _operationSequence;
    private long _activeOperationId;
    private StatusOperationType _activeOperationType;
    private Action? _activeCancelAction;

    public long Begin(
        string text,
        bool indeterminate = true,
        StatusOperationType operationType = StatusOperationType.None,
        Action? cancelAction = null)
    {
        var operationId = Interlocked.Increment(ref _operationSequence);

        lock (_sync)
        {
            _activeOperationId = operationId;
            _activeOperationType = operationType;
            _activeCancelAction = cancelAction;
        }

        viewModel.StatusOperationText = text;
        viewModel.StatusBusy = true;
        viewModel.StatusProgressIsIndeterminate = indeterminate;
        viewModel.StatusProgressValue = 0;

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

        viewModel.StatusOperationText = string.Empty;
        viewModel.StatusBusy = false;
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
}
