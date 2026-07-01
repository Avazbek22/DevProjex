using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia.Views;

public partial class GitCloneWindow : Window
{
    public event EventHandler<RoutedEventArgs>? StartCloneRequested;
    public event EventHandler<RoutedEventArgs>? CancelRequested;
    private readonly TextBox? _urlTextBox;
    private readonly ComboBox? _recentRepositoriesComboBox;

    public GitCloneWindow()
    {
        CompositionBackdropCornerRadiusCoordinator.UseRoundedCornersForPopupSurface();

        AvaloniaXamlLoader.Load(this);
        _urlTextBox = this.FindControl<TextBox>("UrlTextBox");
        _recentRepositoriesComboBox = this.FindControl<ComboBox>("RecentRepositoriesComboBox");

        if (_recentRepositoriesComboBox is not null)
            _recentRepositoriesComboBox.DropDownOpened += OnRecentRepositoriesDropDownOpened;

        Closed += OnClosed;

        // Focus URL textbox when window opens
        Opened += (_, _) =>
        {
            Dispatcher.Post(() =>
            {
                _urlTextBox?.Focus();
                _urlTextBox?.SelectAll();
            }, DispatcherPriority.Input);
        };
    }

    public TextBox? UrlTextBoxControl => _urlTextBox;

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_recentRepositoriesComboBox is not null)
            _recentRepositoriesComboBox.DropDownOpened -= OnRecentRepositoriesDropDownOpened;

        Closed -= OnClosed;
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

    private void OnRecentRepositorySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: RecentProjectEntryViewModel recent })
            return;

        if (DataContext is not MainWindowViewModel viewModel)
            return;

        viewModel.GitCloneUrl = recent.Value;
        Dispatcher.Post(() =>
        {
            _urlTextBox?.Focus();
            _urlTextBox?.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OnRecentRepositoriesDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is not ComboBox comboBox ||
            DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        Dispatcher.Post(() =>
        {
            var popup = comboBox
                .GetVisualDescendants()
                .OfType<Popup>()
                .FirstOrDefault(static candidate => string.Equals(candidate.Name, "PART_Popup", StringComparison.Ordinal));

            PopupBackdropConfigurator.TryApply(
                popup?.Child,
                this,
                viewModel.HasAnyEffect,
                PopupBackdropTransparencyFallback.Transparent);
        }, DispatcherPriority.Loaded);
    }
}
