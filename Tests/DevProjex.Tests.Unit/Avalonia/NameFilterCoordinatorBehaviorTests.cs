namespace DevProjex.Tests.Unit.Avalonia;

public sealed class NameFilterCoordinatorBehaviorTests
{
    [Fact]
    public void OnNameFilterChanged_MultipleCalls_ReplacesAndCancelsPreviousDebounceToken()
    {
        using var coordinator = new NameFilterCoordinator(_ => { });
        coordinator.OnNameFilterChanged();
        var firstDebounceCts = GetDebounceCts(coordinator);
        Assert.NotNull(firstDebounceCts);

        coordinator.OnNameFilterChanged();
        var secondDebounceCts = GetDebounceCts(coordinator);
        Assert.NotNull(secondDebounceCts);

        coordinator.OnNameFilterChanged();
        var thirdDebounceCts = GetDebounceCts(coordinator);

        Assert.NotNull(thirdDebounceCts);
        Assert.True(firstDebounceCts!.IsCancellationRequested);
        Assert.True(secondDebounceCts!.IsCancellationRequested);
        Assert.False(thirdDebounceCts!.IsCancellationRequested);
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
        coordinator.OnNameFilterChanged();
        var debounceCts = GetDebounceCts(coordinator);

        coordinator.Dispose();

        Assert.NotNull(debounceCts);
        Assert.True(debounceCts!.IsCancellationRequested);
        Assert.True(IsDebounceCtsNull(coordinator));
    }

    [Fact]
    public void OnNameFilterChanged_WhenActiveQueryExists_SetsBusyStateTrue()
    {
        bool? busyState = null;
        using var coordinator = new NameFilterCoordinator(
            _ => { },
            () => true,
            isBusy => busyState = isBusy);

        coordinator.OnNameFilterChanged();

        Assert.True(busyState);
    }

    [Fact]
    public void CancelPending_ResetsBusyState()
    {
        bool? busyState = null;
        using var coordinator = new NameFilterCoordinator(
            _ => { },
            () => true,
            isBusy => busyState = isBusy);

        coordinator.OnNameFilterChanged();
        coordinator.CancelPending();

        Assert.False(busyState);
    }

    [AvaloniaFact]
    public async Task DebounceContinuationAfterDispose_DoesNotRecreateFilterOrApply()
    {
        var applyCount = 0;
        var coordinator = new NameFilterCoordinator(_ => applyCount++);
        var runDebounce = typeof(NameFilterCoordinator).GetMethod(
            "RunDebounceAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(runDebounce);
        var continuation = Assert.IsAssignableFrom<Task>(runDebounce!.Invoke(
            coordinator,
            [0, CancellationToken.None]));

        coordinator.Dispose();
        await continuation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, applyCount);
        Assert.True(IsFilterCtsNull(coordinator));
    }

    private static CancellationTokenSource? GetDebounceCts(NameFilterCoordinator coordinator)
    {
        var field = typeof(NameFilterCoordinator).GetField(
            "_debounceCts",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);
        return field!.GetValue(coordinator) as CancellationTokenSource;
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

    private static bool IsDebounceCtsNull(NameFilterCoordinator coordinator)
    {
        var field = typeof(NameFilterCoordinator).GetField(
            "_debounceCts",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);
        return field!.GetValue(coordinator) is null;
    }
}
