namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class AvaloniaHeadlessHarnessSmokeTests
{
    [AvaloniaFact]
    public async Task HeadlessHarness_CreatesAndClosesWindow()
    {
        Assert.Same(Dispatcher.UIThread, Dispatcher.CurrentDispatcher);

        var window = new Window();
        UiTestDriver.TrackTopLevelWindow(window);

        window.Show();
        Assert.True(window.IsVisible);

        await UiTestDriver.CloseTopLevelWindowAsync(window);
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public void HeadlessCleanup_ClosesTrackedWindowsAndClearsTransientState()
    {
        var firstWindow = new Window();
        var secondWindow = new Window();
        UiTestDriver.TrackTopLevelWindow(firstWindow);
        UiTestDriver.TrackTopLevelWindow(secondWindow);

        firstWindow.Show();
        secondWindow.Show();
        Assert.True(firstWindow.IsVisible);
        Assert.True(secondWindow.IsVisible);
        Assert.True(UiTestDriver.TrackedWindowCount >= 2);

        UiTestDriver.CleanupHeadlessState();

        Assert.False(firstWindow.IsVisible);
        Assert.False(secondWindow.IsVisible);
        Assert.Equal(0, UiTestDriver.TrackedWindowCount);
    }
}
