using Avalonia.Threading;

namespace DevProjex.Tests.Unit.Avalonia;

[Collection("AvaloniaUI")]
public sealed class SelectionRefreshStatusLeaseTests
{
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

        await Task.Delay(280, TestContext.Current.CancellationToken);
        await Dispatcher.UIThread.InvokeAsync(() => { });

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
            await Task.Delay(280, TestContext.Current.CancellationToken);
            await Dispatcher.UIThread.InvokeAsync(() => { });

            Assert.True(viewModel.StatusBusy);
            Assert.True(viewModel.StatusProgressIsIndeterminate);
            Assert.Equal("Updating options", viewModel.StatusOperationText);
            Assert.Equal(StatusOperationType.SelectionRefresh, status.GetActiveSnapshot().OperationType);
        }

        await Dispatcher.UIThread.InvokeAsync(() => { });

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
            await Task.Delay(280, TestContext.Current.CancellationToken);
            await Dispatcher.UIThread.InvokeAsync(() => { });

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
            await Task.Delay(280, TestContext.Current.CancellationToken);
            await Dispatcher.UIThread.InvokeAsync(() => { });

            var snapshot = status.GetActiveSnapshot();
            Assert.Equal(StatusOperationType.SelectionRefresh, snapshot.OperationType);

            snapshot.CancelAction?.Invoke();

            Assert.True(canceled);
        }
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
