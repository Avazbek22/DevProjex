using System.Collections.Concurrent;

namespace DevProjex.Avalonia.Services;

internal static class DispatcherTaskSchedulerProvider
{
    private static readonly ConcurrentDictionary<DispatcherPriority, TaskScheduler> Schedulers = new();

    public static async Task YieldAsync(DispatcherPriority priority)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.Yield(priority);
            return;
        }

        // Avalonia 12.1 exposes Dispatcher.ToTaskScheduler, so background callers can
        // express a priority-aware UI scheduling point without allocating empty
        // DispatcherOperation wrappers at every call site.
        var scheduler = Schedulers.GetOrAdd(
            priority,
            static capturedPriority => Dispatcher.UIThread.ToTaskScheduler(capturedPriority));

        await Task.Factory.StartNew(
                static () => { },
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                scheduler)
            .ConfigureAwait(false);
    }
}
