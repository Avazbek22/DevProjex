using System.Reflection;
using DevProjex.Avalonia.Coordinators;
using DebounceTimer = System.Timers.Timer;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class NameFilterCoordinatorBehaviorTests
{
    [Fact]
    public void Constructor_ConfiguresExpectedDebounceInterval()
    {
        using var coordinator = new NameFilterCoordinator(_ => { });
        var timer = GetDebounceTimer(coordinator);

        Assert.Equal(360d, timer.Interval);
        Assert.False(timer.AutoReset);
    }

    [Fact]
    public void OnNameFilterChanged_MultipleCalls_LeavesDebounceTimerEnabled()
    {
        using var coordinator = new NameFilterCoordinator(_ => { });
        var timer = GetDebounceTimer(coordinator);

        coordinator.OnNameFilterChanged();
        coordinator.OnNameFilterChanged();
        coordinator.OnNameFilterChanged();

        Assert.True(timer.Enabled);
    }

    [Fact]
    public void CancelPending_WhenDebounceNotStarted_DoesNotThrow()
    {
        using var coordinator = new NameFilterCoordinator(_ => { });

        var ex = Record.Exception(() => coordinator.CancelPending());

        Assert.Null(ex);
    }

    [Fact]
    public void CancelPending_MultipleCalls_DoesNotThrow()
    {
        using var coordinator = new NameFilterCoordinator(_ => { });
        coordinator.OnNameFilterChanged();

        var ex = Record.Exception(() =>
        {
            coordinator.CancelPending();
            coordinator.CancelPending();
            coordinator.CancelPending();
        });

        Assert.Null(ex);
    }

    [Fact]
    public void CancelPending_CancelsButDoesNotDisposeActiveCts()
    {
        using var coordinator = new NameFilterCoordinator(_ => { });
        var cts = new CancellationTokenSource();
        SetFilterCts(coordinator, cts);

        coordinator.CancelPending();

        Assert.True(cts.IsCancellationRequested);
        Assert.False(IsFilterCtsNull(coordinator));
    }

    [Fact]
    public void Dispose_ClearsFilterCtsReference()
    {
        var coordinator = new NameFilterCoordinator(_ => { });
        SetFilterCts(coordinator, new CancellationTokenSource());

        coordinator.Dispose();

        Assert.True(IsFilterCtsNull(coordinator));
    }

    [Fact]
    public void Dispose_StopsDebounceTimer()
    {
        var coordinator = new NameFilterCoordinator(_ => { });
        var timer = GetDebounceTimer(coordinator);
        coordinator.OnNameFilterChanged();

        coordinator.Dispose();

        Assert.False(timer.Enabled);
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

    private static bool IsFilterCtsNull(NameFilterCoordinator coordinator)
    {
        var field = typeof(NameFilterCoordinator).GetField(
            "_filterCts",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);
        return field!.GetValue(coordinator) is null;
    }
}
