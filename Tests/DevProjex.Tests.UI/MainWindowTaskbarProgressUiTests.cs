using System.Reflection;

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
    public async Task GitCloneDialogProgress_UsesMainTaskbarIcon()
    {
        using var project = UiTestProject.CreateDefault();
        var taskbarProgress = new RecordingTaskbarProgressService();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(
            project,
            configureServices: services => services with { TaskbarProgressService = taskbarProgress });

        try
        {
            taskbarProgress.Calls.Clear();
            var initialAttachCount = taskbarProgress.AttachCount;

            InvokeGitCloneTaskbarMethod(window, "BeginGitCloneTaskbarProgress");
            InvokeGitCloneTaskbarMethod(window, "UpdateGitCloneTaskbarProgress", "Receiving objects: 42%");
            InvokeGitCloneTaskbarMethod(window, "UpdateGitCloneTaskbarProgress", "::EXTRACTING::");
            InvokeGitCloneTaskbarMethod(window, "MarkGitCloneTaskbarProgressError");
            InvokeGitCloneTaskbarMethod(window, "CompleteGitCloneTaskbarProgress");

            Assert.Equal(initialAttachCount + 1, taskbarProgress.AttachCount);
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
            taskbarProgress.Calls.Clear();

            InvokeGitCloneTaskbarMethod(window, "UpdateGitCloneTaskbarProgress", "Receiving objects: 42%");
            InvokeGitCloneTaskbarMethod(window, "MarkGitCloneTaskbarProgressError");
            InvokeGitCloneTaskbarMethod(window, "CompleteGitCloneTaskbarProgress");

            Assert.Empty(taskbarProgress.Calls);

            InvokeGitCloneTaskbarMethod(window, "BeginGitCloneTaskbarProgress");
            InvokeGitCloneTaskbarMethod(window, "UpdateGitCloneTaskbarProgress", "42%");
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
                TaskbarProgressCall.Indeterminate(),
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
        var method = typeof(MainWindow).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        method.Invoke(window, args);
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

    private sealed record TaskbarProgressCall(string Kind, double? Percent = null)
    {
        public static TaskbarProgressCall Indeterminate() => new("Indeterminate");

        public static TaskbarProgressCall Progress(double percent) => new("Progress", percent);

        public static TaskbarProgressCall Paused() => new("Paused");

        public static TaskbarProgressCall Error() => new("Error");

        public static TaskbarProgressCall Clear() => new("Clear");
    }
}
