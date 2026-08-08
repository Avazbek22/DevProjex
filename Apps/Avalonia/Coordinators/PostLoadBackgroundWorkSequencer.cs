namespace DevProjex.Avalonia.Coordinators;

// Initial project loading has one ordered background boundary. Starting any of these phases before
// the settings reveal settles makes file IO, parser work and status updates compete with animation.
internal static class PostLoadBackgroundWorkSequencer
{
    public static async Task RunAsync(
        Task visualReadyTask,
        Func<CancellationToken, Task> prepareCompressionAsync,
        Func<CancellationToken, Task> initializeMetricsAsync,
        Action scheduleSecretAnalysis,
        CancellationToken cancellationToken)
    {
        await visualReadyTask.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        await prepareCompressionAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        await initializeMetricsAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        scheduleSecretAnalysis();
    }
}
