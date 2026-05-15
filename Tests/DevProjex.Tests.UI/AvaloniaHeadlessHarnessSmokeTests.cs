namespace DevProjex.Tests.UI;

public sealed class AvaloniaHeadlessHarnessSmokeTests
{
    [AvaloniaFact]
    public void HeadlessHarness_CreatesAndClosesWindow()
    {
        Assert.Same(Dispatcher.UIThread, Dispatcher.CurrentDispatcher);

        var window = new Window();

        window.Show();
        Assert.True(window.IsVisible);

        window.Close();
        Assert.False(window.IsVisible);
    }
}
