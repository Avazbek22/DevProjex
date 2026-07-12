using System.Diagnostics;
using Avalonia.Threading;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
public sealed class SelectionRefreshStatusLeaseTests
{
    private static readonly TimeSpan SelectionRefreshVisibleTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FastRefreshGuardWindow = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    [AvaloniaFact]
    public async Task FastRefresh_DoesNotShowStatusOperation()
    {
        var viewModel = CreateViewModel();
        var status = CreateStatusCoordinator(viewModel);

        await using (SelectionRefreshStatusLease.Start(
                         viewModel,
                         status,
                         cancelAction: null,
                         TestContext.Current.CancellationToken))
        {
        }

        await AssertOperationTypeRemainsAsync(
            status,
            StatusOperationType.None,
            FastRefreshGuardWindow,
            TestContext.Current.CancellationToken);

        Assert.False(viewModel.StatusBusy);
        Assert.Equal(StatusOperationType.None, status.GetActiveSnapshot().OperationType);
    }

    [AvaloniaFact]
    public async Task LongRefresh_ShowsSelectionRefreshStatusAndClearsOnDispose()
    {
        var viewModel = CreateViewModel();
        var status = CreateStatusCoordinator(viewModel);

        await using (SelectionRefreshStatusLease.Start(
                         viewModel,
                         status,
                         cancelAction: null,
                         TestContext.Current.CancellationToken))
        {
            await WaitForOperationTypeAsync(
                status,
                StatusOperationType.SelectionRefresh,
                SelectionRefreshVisibleTimeout,
                TestContext.Current.CancellationToken);

            Assert.True(viewModel.StatusBusy);
            Assert.True(viewModel.StatusProgressIsIndeterminate);
            Assert.Equal("Updating options", viewModel.StatusOperationText);
            Assert.Equal(StatusOperationType.SelectionRefresh, status.GetActiveSnapshot().OperationType);
        }

        await FlushDispatcherAsync();

        Assert.False(viewModel.StatusBusy);
        Assert.Equal(StatusOperationType.None, status.GetActiveSnapshot().OperationType);
    }

    [AvaloniaFact]
    public async Task LongRefresh_DoesNotStealExistingStatusOperation()
    {
        var viewModel = CreateViewModel();
        var status = CreateStatusCoordinator(viewModel);
        var metricsOperation = status.Begin("Calculating data", operationType: StatusOperationType.MetricsCalculation);

        await using (SelectionRefreshStatusLease.Start(
                         viewModel,
                         status,
                         cancelAction: null,
                         TestContext.Current.CancellationToken))
        {
            await AssertOperationTypeRemainsAsync(
                status,
                StatusOperationType.MetricsCalculation,
                FastRefreshGuardWindow,
                TestContext.Current.CancellationToken);

            Assert.True(viewModel.StatusBusy);
            Assert.Equal("Calculating data", viewModel.StatusOperationText);
            Assert.True(status.IsActive(metricsOperation));
        }

        status.Complete(metricsOperation);
    }

    [AvaloniaFact]
    public async Task LongRefresh_ExposesCancelActionOnActiveStatusSnapshot()
    {
        var viewModel = CreateViewModel();
        var status = CreateStatusCoordinator(viewModel);
        var canceled = false;

        await using (SelectionRefreshStatusLease.Start(
                         viewModel,
                         status,
                         cancelAction: () => canceled = true,
                         TestContext.Current.CancellationToken))
        {
            var snapshot = await WaitForOperationTypeAsync(
                status,
                StatusOperationType.SelectionRefresh,
                SelectionRefreshVisibleTimeout,
                TestContext.Current.CancellationToken);
            Assert.Equal(StatusOperationType.SelectionRefresh, snapshot.OperationType);
            Assert.True(snapshot.CancelAction is not null, "Expected an active selection refresh status to expose a cancel action.");

            snapshot.CancelAction.Invoke();

            Assert.True(canceled);
        }
    }

    private static async Task<StatusOperationSnapshot> WaitForOperationTypeAsync(
        StatusOperationCoordinator status,
        StatusOperationType expectedOperationType,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            await FlushDispatcherAsync();

            var snapshot = status.GetActiveSnapshot();
            if (snapshot.OperationType == expectedOperationType)
                return snapshot;

            await Task.Delay(PollInterval, cancellationToken);
        }

        var actualSnapshot = status.GetActiveSnapshot();
        Assert.Fail(
            $"Expected active status operation '{expectedOperationType}' within {timeout}, " +
            $"but current operation is '{actualSnapshot.OperationType}'.");
        return default;
    }

    private static async Task AssertOperationTypeRemainsAsync(
        StatusOperationCoordinator status,
        StatusOperationType expectedOperationType,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < duration)
        {
            await FlushDispatcherAsync();

            var snapshot = status.GetActiveSnapshot();
            Assert.Equal(expectedOperationType, snapshot.OperationType);

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private static async Task FlushDispatcherAsync()
    {
        // SelectionRefreshStatusLease schedules status transitions at Background priority.
        // Tests must drain that queue before reading the snapshot, otherwise slow CI runners
        // can observe the old state and report a false regression.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static StatusOperationCoordinator CreateStatusCoordinator(MainWindowViewModel viewModel) =>
        new(
            viewModel,
            isBackgroundMetricsActive: () => false,
            metricsOperationTextProvider: () => viewModel.StatusOperationCalculatingData);

    private static MainWindowViewModel CreateViewModel()
    {
        var catalog = new StubLocalizationCatalog(new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
        {
            [AppLanguage.En] = new Dictionary<string, string>
            {
                ["Status.Operation.CalculatingData"] = "Calculating data",
                ["Status.Operation.UpdatingOptions"] = "Updating options"
            }
        });

        return new MainWindowViewModel(
            new LocalizationService(catalog, AppLanguage.En),
            new HelpContentProvider());
    }
}
