using System.Reflection;
using DevProjex.Avalonia.Coordinators;

namespace DevProjex.Tests.UI;

public sealed class MainWindowTaskbarProgressUiTests
{
    [AvaloniaFact]
    public async Task StatusBarState_IsMirroredToTaskbarProgressService()
    {
        using var project = UiTestProject.CreateDefault();
        var taskbarProgress = new RecordingTaskbarProgressService();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(
            project,
            configureServices: services => services with { TaskbarProgressService = taskbarProgress });

        try
        {
            await UiTestDriver.WaitForInitialMetricsBaselineAsync(window);
            Assert.Equal(1, taskbarProgress.AttachCount);

            taskbarProgress.Calls.Clear();
            var viewModel = UiTestDriver.GetViewModel(window);

            viewModel.StatusProgressIsIndeterminate = true;
            viewModel.StatusBusy = true;

            viewModel.StatusProgressIsIndeterminate = false;
            viewModel.StatusProgressValue = 37.5;

            viewModel.StatusBusy = false;

            Assert.Contains(TaskbarProgressCall.Indeterminate(), taskbarProgress.Calls);
            Assert.Contains(TaskbarProgressCall.Progress(37.5), taskbarProgress.Calls);
            Assert.Equal(TaskbarProgressCall.Clear(), taskbarProgress.Calls[^1]);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task DelayedStatusPresentation_DoesNotFlashForFastOperation()
    {
        using var project = UiTestProject.CreateDefault();
        var taskbarProgress = new RecordingTaskbarProgressService();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(
            project,
            configureServices: services => services with { TaskbarProgressService = taskbarProgress });

        try
        {
            await UiTestDriver.WaitForInitialMetricsBaselineAsync(window);
            taskbarProgress.Calls.Clear();
            var viewModel = UiTestDriver.GetViewModel(window);
            var statusOperations = GetStatusOperationCoordinator(window);

            var operationId = statusOperations.Begin(
                "Preparing preview",
                operationType: StatusOperationType.PreviewBuild,
                presentation: StatusOperationPresentation.ExtendedDelay);

            Assert.True(viewModel.StatusBusy);
            Assert.False(viewModel.StatusOperationVisible);

            statusOperations.Complete(operationId);
            await Task.Delay(
                StatusOperationCoordinator.ExtendedDelayedPresentationThreshold +
                TimeSpan.FromMilliseconds(100));

            Assert.False(viewModel.StatusBusy);
            Assert.False(viewModel.StatusOperationVisible);
            Assert.DoesNotContain(
                taskbarProgress.Calls,
                call => call.Kind is
                    TaskbarProgressCallKind.Indeterminate or
                    TaskbarProgressCallKind.Progress);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task DelayedStatusPresentation_ShowsStatusBarAndTaskbarTogetherForLongOperation()
    {
        using var project = UiTestProject.CreateDefault();
        var taskbarProgress = new RecordingTaskbarProgressService();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(
            project,
            configureServices: services => services with { TaskbarProgressService = taskbarProgress });

        try
        {
            await UiTestDriver.WaitForInitialMetricsBaselineAsync(window);
            taskbarProgress.Calls.Clear();
            var viewModel = UiTestDriver.GetViewModel(window);
            var statusOperations = GetStatusOperationCoordinator(window);

            var operationId = statusOperations.Begin(
                "Calculating data",
                indeterminate: false,
                operationType: StatusOperationType.MetricsCalculation,
                presentation: StatusOperationPresentation.ExtendedDelay);

            Assert.True(viewModel.StatusBusy);
            Assert.False(viewModel.StatusOperationVisible);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => viewModel.StatusOperationVisible,
                "delayed progress presentation to become visible");

            Assert.Equal(TaskbarProgressCall.Progress(0), taskbarProgress.Calls[^1]);

            statusOperations.UpdateProgress(48, operationId: operationId);

            Assert.True(viewModel.StatusProgressVisible);
            Assert.Equal(TaskbarProgressCall.Progress(48), taskbarProgress.Calls[^1]);

            var replacementOperationId = statusOperations.Begin(
                "Preparing preview",
                operationType: StatusOperationType.PreviewBuild,
                presentation: StatusOperationPresentation.ExtendedDelay);

            Assert.True(viewModel.StatusOperationVisible);
            Assert.Equal(TaskbarProgressCall.Indeterminate(), taskbarProgress.Calls[^1]);

            statusOperations.Complete(replacementOperationId);

            Assert.False(viewModel.StatusOperationVisible);
            Assert.Equal(TaskbarProgressCall.Clear(), taskbarProgress.Calls[^1]);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task GitCloneDialogProgress_UsesMainTaskbarIcon()
    {
        using var project = UiTestProject.CreateDefault();
        var taskbarProgress = new RecordingTaskbarProgressService();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(
            project,
            configureServices: services => services with { TaskbarProgressService = taskbarProgress });

        try
        {
            await UiTestDriver.WaitForInitialMetricsBaselineAsync(window);
            taskbarProgress.Calls.Clear();
            var initialAttachCount = taskbarProgress.AttachCount;

            InvokeGitCloneTaskbarMethod(window, "BeginGitCloneTaskbarProgress");
            InvokeGitCloneTaskbarMethod(window, "UpdateGitCloneTaskbarProgress", "Receiving objects: 42%");
            InvokeGitCloneTaskbarMethod(window, "SetGitCloneIndeterminate");
            InvokeGitCloneTaskbarMethod(window, "MarkGitCloneTaskbarProgressError");
            InvokeGitCloneTaskbarMethod(window, "CompleteGitCloneTaskbarProgress");

            Assert.Equal(initialAttachCount, taskbarProgress.AttachCount);
            Assert.Equal(
            [
                TaskbarProgressCall.Indeterminate(),
                TaskbarProgressCall.Progress(42),
                TaskbarProgressCall.Indeterminate(),
                TaskbarProgressCall.Error(),
                TaskbarProgressCall.Clear()
            ], taskbarProgress.Calls);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task GitCloneDialogProgress_RestoresPreviousTaskbarStateOnCompletion()
    {
        using var project = UiTestProject.CreateDefault();
        var taskbarProgress = new RecordingTaskbarProgressService();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(
            project,
            configureServices: services => services with { TaskbarProgressService = taskbarProgress });

        try
        {
            await UiTestDriver.WaitForInitialMetricsBaselineAsync(window);
            var viewModel = UiTestDriver.GetViewModel(window);

            viewModel.StatusProgressIsIndeterminate = false;
            viewModel.StatusProgressValue = 64;
            viewModel.StatusBusy = true;
            taskbarProgress.Calls.Clear();

            InvokeGitCloneTaskbarMethod(window, "BeginGitCloneTaskbarProgress");
            InvokeGitCloneTaskbarMethod(window, "UpdateGitCloneTaskbarProgress", "Receiving objects: 25%");
            InvokeGitCloneTaskbarMethod(window, "CompleteGitCloneTaskbarProgress");

            Assert.Equal(TaskbarProgressCall.Progress(64), taskbarProgress.Calls[^1]);
            Assert.Equal(
            [
                TaskbarProgressCall.Indeterminate(),
                TaskbarProgressCall.Progress(25),
                TaskbarProgressCall.Progress(64)
            ], taskbarProgress.Calls);

            viewModel.StatusProgressIsIndeterminate = true;
            taskbarProgress.Calls.Clear();

            InvokeGitCloneTaskbarMethod(window, "BeginGitCloneTaskbarProgress");
            InvokeGitCloneTaskbarMethod(window, "UpdateGitCloneTaskbarProgress", "Resolving deltas: 75%");
            InvokeGitCloneTaskbarMethod(window, "CompleteGitCloneTaskbarProgress");

            Assert.Equal(TaskbarProgressCall.Indeterminate(), taskbarProgress.Calls[^1]);
            Assert.Equal(
            [
                TaskbarProgressCall.Indeterminate(),
                TaskbarProgressCall.Progress(75),
                TaskbarProgressCall.Indeterminate()
            ], taskbarProgress.Calls);

            viewModel.StatusBusy = false;
            taskbarProgress.Calls.Clear();

            InvokeGitCloneTaskbarMethod(window, "BeginGitCloneTaskbarProgress");
            InvokeGitCloneTaskbarMethod(window, "CompleteGitCloneTaskbarProgress");

            Assert.Equal(
            [
                TaskbarProgressCall.Indeterminate(),
                TaskbarProgressCall.Clear()
            ], taskbarProgress.Calls);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task GitCloneDialogProgress_HandlesInactiveAndPercentEdgeCases()
    {
        using var project = UiTestProject.CreateDefault();
        var taskbarProgress = new RecordingTaskbarProgressService();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(
            project,
            configureServices: services => services with { TaskbarProgressService = taskbarProgress });

        try
        {
            await UiTestDriver.WaitForInitialMetricsBaselineAsync(window);
            taskbarProgress.Calls.Clear();

            InvokeGitCloneTaskbarMethod(window, "UpdateGitCloneTaskbarProgress", "Receiving objects: 42%");
            InvokeGitCloneTaskbarMethod(window, "MarkGitCloneTaskbarProgressError");
            InvokeGitCloneTaskbarMethod(window, "CompleteGitCloneTaskbarProgress");

            Assert.Empty(taskbarProgress.Calls);

            InvokeGitCloneTaskbarMethod(window, "BeginGitCloneTaskbarProgress");
            InvokeGitCloneTaskbarMethod(window, "UpdateGitCloneTaskbarProgress", "42%");
            InvokeGitCloneTaskbarMethod(
                window,
                "UpdateGitCloneTaskbarProgress",
                "Receiving objects: 42% (42/100), 1.00 MiB");
            InvokeGitCloneTaskbarMethod(window, "UpdateGitCloneTaskbarProgress", "Receiving objects: 87%");
            InvokeGitCloneTaskbarMethod(window, "UpdateGitCloneTaskbarProgress", "Resolving deltas: 99.5%");
            InvokeGitCloneTaskbarMethod(window, "UpdateGitCloneTaskbarProgress", "Cloning into repository...");
            InvokeGitCloneTaskbarMethod(window, "CompleteGitCloneTaskbarProgress");
            InvokeGitCloneTaskbarMethod(window, "CompleteGitCloneTaskbarProgress");

            Assert.Equal(
            [
                TaskbarProgressCall.Indeterminate(),
                TaskbarProgressCall.Progress(42),
                TaskbarProgressCall.Progress(87),
                TaskbarProgressCall.Progress(99.5),
                TaskbarProgressCall.Clear()
            ], taskbarProgress.Calls);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    private static void InvokeGitCloneTaskbarMethod(MainWindow window, string methodName, params object[] args)
    {
        var coordinator = GetTaskbarProgressCoordinator(window);
        var mappedName = methodName switch
        {
            "BeginGitCloneTaskbarProgress" => nameof(TaskbarProgressCoordinator.BeginGitClone),
            "UpdateGitCloneTaskbarProgress" => nameof(TaskbarProgressCoordinator.UpdateGitClone),
            "SetGitCloneIndeterminate" => nameof(TaskbarProgressCoordinator.SetGitCloneIndeterminate),
            "MarkGitCloneTaskbarProgressError" => nameof(TaskbarProgressCoordinator.MarkGitCloneError),
            "CompleteGitCloneTaskbarProgress" => nameof(TaskbarProgressCoordinator.CompleteGitClone),
            _ => methodName
        };
        var method = typeof(TaskbarProgressCoordinator).GetMethod(
            mappedName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        method ??= typeof(TaskbarProgressCoordinator).GetMethod(
            mappedName,
            BindingFlags.Instance | BindingFlags.Public);

        Assert.NotNull(method);
        method.Invoke(coordinator, args);
    }

    private static TaskbarProgressCoordinator GetTaskbarProgressCoordinator(MainWindow window)
    {
        var field = typeof(MainWindow).GetField(
            "_taskbarProgress",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (TaskbarProgressCoordinator)field.GetValue(window)!;
    }

    private static StatusOperationCoordinator GetStatusOperationCoordinator(MainWindow window)
    {
        var field = typeof(MainWindow).GetField(
            "_statusOperations",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (StatusOperationCoordinator)field.GetValue(window)!;
    }

    private sealed class RecordingTaskbarProgressService : ITaskbarProgressService
    {
        public int AttachCount { get; private set; }

        public List<TaskbarProgressCall> Calls { get; } = [];

        public bool IsSupported => true;

        public void Attach(Window window) => AttachCount++;

        public void SetIndeterminate() => Calls.Add(TaskbarProgressCall.Indeterminate());

        public void SetProgress(double percent) => Calls.Add(TaskbarProgressCall.Progress(percent));

        public void SetPaused() => Calls.Add(TaskbarProgressCall.Paused());

        public void SetError() => Calls.Add(TaskbarProgressCall.Error());

        public void Clear() => Calls.Add(TaskbarProgressCall.Clear());

        public void Dispose()
        {
        }
    }

    private sealed record TaskbarProgressCall(TaskbarProgressCallKind Kind, double? Percent = null)
    {
        public static TaskbarProgressCall Indeterminate() => new(TaskbarProgressCallKind.Indeterminate);

        public static TaskbarProgressCall Progress(double percent) => new(TaskbarProgressCallKind.Progress, percent);

        public static TaskbarProgressCall Paused() => new(TaskbarProgressCallKind.Paused);

        public static TaskbarProgressCall Error() => new(TaskbarProgressCallKind.Error);

        public static TaskbarProgressCall Clear() => new(TaskbarProgressCallKind.Clear);
    }

    private enum TaskbarProgressCallKind
    {
        Indeterminate,
        Progress,
        Paused,
        Error,
        Clear
    }
}
