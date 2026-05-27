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
