using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia.Coordinators;

// Initial project loading has one shared visual boundary. Transition completion only means that
// target values were reached; it does not guarantee that Avalonia published the final layout.
// Keep metrics, Git discovery and compacting GC behind this gate instead of giving each consumer
// the raw animation task or an independent delay.
internal static class PostLoadVisualStabilityGate
{
    // This is compositor breathing room after the transition, not part of its duration.
    internal static readonly TimeSpan QuietPeriod =
        TimeSpan.FromMilliseconds(120);

    public static Task WaitAsync(
        Task visualTransitionTask,
        CancellationToken cancellationToken) =>
        WaitAsync(
            visualTransitionTask,
            YieldUiAsync,
            Task.Delay,
            cancellationToken);

    internal static async Task WaitAsync(
        Task visualTransitionTask,
        Func<DispatcherPriority, CancellationToken, Task> yieldUiAsync,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        CancellationToken cancellationToken)
    {
        await MetricsCalculationPolicy.WaitForInitialVisualReadyAsync(
            visualTransitionTask,
            MetricsCalculationPolicy.InitialVisualReadyTimeout,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // Render once before and after the quiet period so both the final layout mutation and its
        // presentation are drained before CPU, IO or stop-the-world GC work becomes eligible.
        await yieldUiAsync(DispatcherPriority.Render, cancellationToken);

        var quietPeriod = UiTimingProfile.Scale(QuietPeriod);
        if (quietPeriod > TimeSpan.Zero)
            await delayAsync(quietPeriod, cancellationToken);

        await yieldUiAsync(DispatcherPriority.Render, cancellationToken);
        await yieldUiAsync(DispatcherPriority.Background, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task YieldUiAsync(
        DispatcherPriority priority,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await DispatcherTaskSchedulerProvider.YieldAsync(priority);
        cancellationToken.ThrowIfCancellationRequested();
    }
}
