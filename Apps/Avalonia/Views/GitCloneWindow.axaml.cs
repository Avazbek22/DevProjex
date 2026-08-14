using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia.Views;

public partial class GitCloneWindow : Window
{
    public event EventHandler<RoutedEventArgs>? StartCloneRequested;
    public event EventHandler<RoutedEventArgs>? CancelRequested;
	internal event EventHandler<RepositoryCacheEntryEventArgs>? OpenCachedRepositoryRequested;
	internal event EventHandler<RepositoryCacheEntryEventArgs>? DeleteCachedRepositoryRequested;
    private readonly TextBox? _urlTextBox;
    private readonly ComboBox? _recentRepositoriesComboBox;
	private readonly ComboBox? _localCacheComboBox;
	private bool _updatingUrlFromRecentSelection;

    public GitCloneWindow()
    {
        // Keep the native WinUI acrylic layer aligned with CloneWindowCard.CornerRadius.
        // The acrylic/backdrop layer lives behind Avalonia visuals, so the XAML Border
        // cannot clip it. If this falls back to the generic 8px popup profile, Windows
        // shows a second, less rounded rectangle behind the 12px dialog card.
        CompositionBackdropCornerRadiusCoordinator.UseRoundedCornersForBorderlessDialogSurface();

        AvaloniaXamlLoader.Load(this);
        _urlTextBox = this.FindControl<TextBox>("UrlTextBox");
        _recentRepositoriesComboBox = this.FindControl<ComboBox>("RecentRepositoriesComboBox");
		_localCacheComboBox = this.FindControl<ComboBox>("LocalCacheComboBox");

        if (_recentRepositoriesComboBox is not null)
            _recentRepositoriesComboBox.DropDownOpened += OnRecentRepositoriesDropDownOpened;
		if (_localCacheComboBox is not null)
			_localCacheComboBox.DropDownOpened += OnRecentRepositoriesDropDownOpened;
		AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

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
		if (_localCacheComboBox is not null)
			_localCacheComboBox.DropDownOpened -= OnRecentRepositoriesDropDownOpened;
		RemoveHandler(KeyDownEvent, OnWindowKeyDown);

        Closed -= OnClosed;
    }

    private void OnStartClone(object? sender, RoutedEventArgs e)
    {
		ConfirmSelection(e);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        CancelRequested?.Invoke(this, e);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
			ConfirmSelection(new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelRequested?.Invoke(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

	private void OnUrlTextChanged(object? sender, TextChangedEventArgs e)
	{
		if (!_updatingUrlFromRecentSelection)
			ClearLocalCacheSelection();
	}

    private void OnRecentRepositorySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: RecentProjectEntryViewModel recent })
            return;

        if (DataContext is not MainWindowViewModel viewModel)
            return;

		ClearLocalCacheSelection();
		_updatingUrlFromRecentSelection = true;
		try
		{
			viewModel.GitCloneUrl = recent.Value;
			if (_urlTextBox is not null && !string.Equals(_urlTextBox.Text, recent.Value, StringComparison.Ordinal))
				_urlTextBox.Text = recent.Value;
		}
		finally
		{
			_updatingUrlFromRecentSelection = false;
		}
        Dispatcher.Post(() =>
        {
            _urlTextBox?.Focus();
            _urlTextBox?.SelectAll();
        }, DispatcherPriority.Input);
    }

	private void OnLocalCacheSelectionChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (sender is not ComboBox comboBox || DataContext is not MainWindowViewModel viewModel)
			return;
		viewModel.SelectedGitCloneCacheEntry = comboBox.SelectedItem as RepositoryCacheEntryViewModel;
	}

	private void OnDeleteLocalCacheEntry(object? sender, RoutedEventArgs e)
	{
		if (sender is Button { Tag: RepositoryCacheEntryViewModel entry } && entry.CanDelete)
		{
			if (_localCacheComboBox?.SelectedItem is RepositoryCacheEntryViewModel selected &&
			    PathComparer.Default.Equals(selected.LocalPath, entry.LocalPath))
			{
				ClearLocalCacheSelection();
			}
			DeleteCachedRepositoryRequested?.Invoke(this, new RepositoryCacheEntryEventArgs(entry));
		}
		e.Handled = true;
	}

	private void ConfirmSelection(RoutedEventArgs e)
	{
		if (DataContext is not MainWindowViewModel { CanStartGitClone: true })
		{
			e.Handled = true;
			return;
		}

		if (_localCacheComboBox?.SelectedItem is RepositoryCacheEntryViewModel entry)
			OpenCachedRepositoryRequested?.Invoke(this, new RepositoryCacheEntryEventArgs(entry));
		else
			StartCloneRequested?.Invoke(this, e);
		e.Handled = true;
	}

	private void ClearLocalCacheSelection()
	{
		if (_localCacheComboBox is not null)
			_localCacheComboBox.SelectedItem = null;
		if (DataContext is MainWindowViewModel viewModel)
			viewModel.SelectedGitCloneCacheEntry = null;
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
                viewModel.ActiveThemeEffect,
                PopupBackdropTransparencyFallback.Transparent);
        }, DispatcherPriority.Loaded);
    }
}

internal sealed class RepositoryCacheEntryEventArgs(RepositoryCacheEntryViewModel entry) : EventArgs
{
	public RepositoryCacheEntryViewModel Entry { get; } = entry;
}
