using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia.Coordinators;

internal static class PostLoadVisualStabilityGate
{
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

        // The transition task ends when its values reach their targets, but Avalonia and the
        // compositor still need a quiet frame to publish final layout before background work starts.
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
