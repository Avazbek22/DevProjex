namespace DevProjex.Avalonia.Views;

public sealed class AutomaticUpdateCheckChangedEventArgs(bool enabled) : EventArgs
{
    public bool Enabled { get; } = enabled;
}

public partial class UpdatePopoverView : UserControl
{
    public event EventHandler<RoutedEventArgs>? CloseRequested;
    public event EventHandler<RoutedEventArgs>? CheckRequested;
    public event EventHandler<RoutedEventArgs>? OpenRepositoryRequested;
    public event EventHandler<AutomaticUpdateCheckChangedEventArgs>? AutomaticCheckChanged;

    public UpdatePopoverView()
    {
        InitializeComponent();
    }

    private void OnClose(object? sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(this, e);

    private void OnCheck(object? sender, RoutedEventArgs e)
        => CheckRequested?.Invoke(this, e);

    private void OnOpenRepository(object? sender, RoutedEventArgs e)
        => OpenRepositoryRequested?.Invoke(this, e);

    private void OnAutomaticCheckChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox)
        {
            AutomaticCheckChanged?.Invoke(
                this,
                new AutomaticUpdateCheckChangedEventArgs(checkBox.IsChecked == true));
        }
    }
}
