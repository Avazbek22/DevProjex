namespace DevProjex.Avalonia.Views;

public partial class GitClonePopoverView : UserControl
{
    public event EventHandler<RoutedEventArgs>? CloseRequested;
    public event EventHandler<RoutedEventArgs>? StartCloneRequested;
    public event EventHandler<RoutedEventArgs>? CancelRequested;

    public GitClonePopoverView()
    {
        AvaloniaXamlLoader.Load(this);
        UrlTextBoxControl = this.FindControl<TextBox>("UrlTextBox");
    }

    public TextBox? UrlTextBoxControl { get; }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, e);
    }

    private void OnStartClone(object? sender, RoutedEventArgs e)
    {
        StartCloneRequested?.Invoke(this, e);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        CancelRequested?.Invoke(this, e);
    }

    private void OnUrlKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            StartCloneRequested?.Invoke(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelRequested?.Invoke(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }
}
