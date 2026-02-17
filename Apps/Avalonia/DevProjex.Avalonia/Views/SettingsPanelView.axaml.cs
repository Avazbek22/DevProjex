namespace DevProjex.Avalonia.Views;

public partial class SettingsPanelView : UserControl
{
    public event EventHandler<RoutedEventArgs>? ApplySettingsRequested;
    public event EventHandler<RoutedEventArgs>? IgnoreAllChanged;
    public event EventHandler<RoutedEventArgs>? ExtensionsAllChanged;
    public event EventHandler<RoutedEventArgs>? RootAllChanged;

    public SettingsPanelView()
    {
        InitializeComponent();
    }

    private void OnApplySettings(object? sender, RoutedEventArgs e)
        => ApplySettingsRequested?.Invoke(sender, e);

    private void OnIgnoreAllChanged(object? sender, RoutedEventArgs e)
        => IgnoreAllChanged?.Invoke(sender, e);

    private void OnExtensionsAllChanged(object? sender, RoutedEventArgs e)
        => ExtensionsAllChanged?.Invoke(sender, e);

    private void OnRootAllChanged(object? sender, RoutedEventArgs e)
        => RootAllChanged?.Invoke(sender, e);
}
