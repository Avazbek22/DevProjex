using DebounceTimer = System.Timers.Timer;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class NameFilterCoordinatorTests
{
    [Fact]
    public void OnNameFilterChanged_StartsDebounceTimer()
    {
        using var coordinator = new NameFilterCoordinator(_ => { });
        var debounceTimer = GetDebounceTimer(coordinator);

        coordinator.OnNameFilterChanged();

        Assert.True(debounceTimer.Enabled);
    }

    [Fact]
    public void CancelPending_StopsDebounceTimer()
    {
        using var coordinator = new NameFilterCoordinator(_ => { });
        var debounceTimer = GetDebounceTimer(coordinator);
        coordinator.OnNameFilterChanged();

        coordinator.CancelPending();

        Assert.False(debounceTimer.Enabled);
    }

    [Fact]
    public void CancelPending_CancelsActiveFilterTokenSource()
    {
        using var coordinator = new NameFilterCoordinator(_ => { });
        var cts = new CancellationTokenSource();
        SetFilterCts(coordinator, cts);

        coordinator.CancelPending();

        Assert.True(cts.IsCancellationRequested);
    }

    private static DebounceTimer GetDebounceTimer(NameFilterCoordinator coordinator)
    {
        var field = typeof(NameFilterCoordinator).GetField(
            "_filterDebounceTimer",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);
        var timer = field!.GetValue(coordinator) as DebounceTimer;
        Assert.NotNull(timer);
        return timer!;
    }

    private static void SetFilterCts(NameFilterCoordinator coordinator, CancellationTokenSource cts)
    {
        var field = typeof(NameFilterCoordinator).GetField(
            "_filterCts",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);
        field!.SetValue(coordinator, cts);
    }
}
