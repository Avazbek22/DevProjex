namespace DevProjex.Avalonia.Coordinators;

// INITIAL PROJECT LOAD UX CONTRACT:
// 1. Publish and render the project tree at full workspace width.
// 2. Reveal the settings island and let its final layout settle.
// 3. Prepare enabled code compression.
// 4. Initialize file metrics.
// 5. Start secret analysis.
//
// Do not start steps 3-5 from tree publication callbacks, and do not parallelize or reorder them.
// File IO, parser work and competing status updates during steps 1-2 cause visible width jumps and
// dropped animation frames. This sequencer is the single release point for initial post-load work.
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
