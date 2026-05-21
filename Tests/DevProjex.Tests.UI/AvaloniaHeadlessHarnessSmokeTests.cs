namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class AvaloniaHeadlessHarnessSmokeTests
{
    [AvaloniaFact]
    public async Task HeadlessHarness_CreatesAndClosesWindow()
    {
        Assert.Same(Dispatcher.UIThread, Dispatcher.CurrentDispatcher);

        var window = new Window();

        window.Show();
        Assert.True(window.IsVisible);

        await UiTestDriver.CloseTopLevelWindowAsync(window);
        Assert.False(window.IsVisible);
    }
}
