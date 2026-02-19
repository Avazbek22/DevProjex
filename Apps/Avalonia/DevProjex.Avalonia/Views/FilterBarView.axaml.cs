namespace DevProjex.Avalonia.Views;

public partial class FilterBarView : UserControl
{
    public event EventHandler<KeyEventArgs>? FilterKeyDown;
    public event EventHandler<RoutedEventArgs>? FilterCloseRequested;
    private readonly TextBox? _filterBox;

    public FilterBarView()
    {
        InitializeComponent();
        _filterBox = this.FindControl<TextBox>("FilterBox");
    }

    public TextBox? FilterBoxControl => _filterBox;

    private void OnFilterKeyDown(object? sender, KeyEventArgs e) => FilterKeyDown?.Invoke(sender, e);

    private void OnFilterClose(object? sender, RoutedEventArgs e) => FilterCloseRequested?.Invoke(sender, e);
}
