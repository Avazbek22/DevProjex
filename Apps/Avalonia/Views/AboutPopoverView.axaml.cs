namespace DevProjex.Avalonia.Views;

public partial class AboutPopoverView : UserControl
{
    public event EventHandler<RoutedEventArgs>? CloseRequested;
    public event EventHandler<RoutedEventArgs>? SupportRequested;
    public event EventHandler<RoutedEventArgs>? OpenLinkRequested;

    public AboutPopoverView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnClose(object? sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(sender, e);

    private void OnOpenLink(object? sender, RoutedEventArgs e)
        => OpenLinkRequested?.Invoke(sender, e);

    private void OnSupport(object? sender, RoutedEventArgs e)
        => SupportRequested?.Invoke(sender, e);
}
