namespace DevProjex.Avalonia.Views;

public partial class PreviewSearchBarView : UserControl
{
	public event EventHandler<KeyEventArgs>? SearchKeyDown;
	public event EventHandler<RoutedEventArgs>? SearchPrevRequested;
	public event EventHandler<RoutedEventArgs>? SearchNextRequested;
	public event EventHandler<RoutedEventArgs>? SearchCloseRequested;

	public PreviewSearchBarView()
	{
		InitializeComponent();
	}

	public TextBox? SearchBoxControl => PreviewSearchBox;

	private void OnSearchKeyDown(object? sender, KeyEventArgs e) =>
		SearchKeyDown?.Invoke(sender, e);

	private void OnSearchPrev(object? sender, RoutedEventArgs e) =>
		SearchPrevRequested?.Invoke(sender, e);

	private void OnSearchNext(object? sender, RoutedEventArgs e) =>
		SearchNextRequested?.Invoke(sender, e);

	private void OnSearchClose(object? sender, RoutedEventArgs e) =>
		SearchCloseRequested?.Invoke(sender, e);
}
