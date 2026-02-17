using Avalonia.Markup.Xaml;

namespace DevProjex.Avalonia.Views;

public partial class AboutPopoverView : UserControl
{
    public event EventHandler<RoutedEventArgs>? CloseRequested;
    public event EventHandler<RoutedEventArgs>? OpenLinkRequested;
    public event EventHandler<RoutedEventArgs>? CopyLinkRequested;

    public AboutPopoverView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnClose(object? sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(sender, e);

    private void OnOpenLink(object? sender, RoutedEventArgs e)
        => OpenLinkRequested?.Invoke(sender, e);

    private void OnCopyLink(object? sender, RoutedEventArgs e)
        => CopyLinkRequested?.Invoke(sender, e);
}
