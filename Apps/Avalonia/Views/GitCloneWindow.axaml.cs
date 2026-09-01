using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia.Views;

public partial class GitCloneWindow : Window
{
	internal const double MaximumRepositoryDropDownHeight = 320;
	internal const double MinimumRepositoryDropDownHeight = 120;
	private const double RepositoryDropDownEdgeMargin = 12;

    public event EventHandler<RoutedEventArgs>? StartCloneRequested;
    public event EventHandler<RoutedEventArgs>? CancelRequested;
	internal event EventHandler<RepositoryCacheEntryEventArgs>? OpenCachedRepositoryRequested;
	internal event EventHandler<RepositoryCacheEntryEventArgs>? DeleteCachedRepositoryRequested;
    private readonly TextBox? _urlTextBox;
	private readonly ComboBox? _recentRepositoriesComboBox;
	private readonly ComboBox? _localCacheComboBox;
	private bool _updatingUrlFromRecentSelection;
	private bool _updatingUrlFromLocalCacheSelection;

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
        {
            _recentRepositoriesComboBox.DropDownOpened += OnRecentRepositoriesDropDownOpened;
			_recentRepositoriesComboBox.DropDownClosed += OnRecentRepositoriesDropDownClosed;
		}
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
        {
            _recentRepositoriesComboBox.DropDownOpened -= OnRecentRepositoriesDropDownOpened;
			_recentRepositoriesComboBox.DropDownClosed -= OnRecentRepositoriesDropDownClosed;
		}
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
		if (TryMoveRecentRepositorySelection(e.Key))
		{
			e.Handled = true;
			return;
		}

        if (e.Key == Key.Enter)
        {
			ConfirmSelection(new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
			if (CloseOpenRepositoryDropDown())
			{
				e.Handled = true;
				return;
			}

            CancelRequested?.Invoke(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

	private void OnUrlTextChanged(object? sender, TextChangedEventArgs e)
	{
		if (_updatingUrlFromRecentSelection ||
		    _updatingUrlFromLocalCacheSelection ||
		    IsUrlTextAlignedWithArmedSelection())
			return;

		ClearLocalCacheSelection();
		ClearRecentRepositorySelection();
	}

	private bool IsUrlTextAlignedWithArmedSelection()
	{
		if (_recentRepositoriesComboBox?.SelectedItem is RecentProjectEntryViewModel recent)
			return string.Equals(_urlTextBox?.Text, recent.Value, StringComparison.Ordinal);

		return _localCacheComboBox?.SelectedItem is RepositoryCacheEntryViewModel &&
		       string.IsNullOrEmpty(_urlTextBox?.Text);
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
    }

	private bool TryMoveRecentRepositorySelection(Key key)
	{
		if (_recentRepositoriesComboBox?.IsDropDownOpen != true ||
		    key is not (Key.Down or Key.Up) ||
		    _recentRepositoriesComboBox.ItemCount == 0)
		{
			return false;
		}

		var currentIndex = _recentRepositoriesComboBox.SelectedIndex;
		var targetIndex = key == Key.Down
			? Math.Min(currentIndex + 1, _recentRepositoriesComboBox.ItemCount - 1)
			: currentIndex < 0
				? _recentRepositoriesComboBox.ItemCount - 1
				: Math.Max(0, currentIndex - 1);
		_recentRepositoriesComboBox.SelectedIndex = targetIndex;
		return true;
	}

	private void OnRecentRepositoriesDropDownClosed(object? sender, EventArgs e)
	{
		if (!ReferenceEquals(sender, _recentRepositoriesComboBox))
			return;

		Dispatcher.Post(() =>
		{
			if (_recentRepositoriesComboBox is null || _recentRepositoriesComboBox.IsDropDownOpen)
				return;

			if (!IsVisible)
				return;

			_urlTextBox?.Focus();
			_urlTextBox?.SelectAll();
		}, DispatcherPriority.Input);
	}

	private void OnLocalCacheSelectionChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (sender is not ComboBox comboBox || DataContext is not MainWindowViewModel viewModel)
			return;

		var entry = comboBox.SelectedItem as RepositoryCacheEntryViewModel;
		viewModel.SelectedGitCloneCacheEntry = entry;
		if (entry is null)
			return;

		ClearRecentRepositorySelection();
		_updatingUrlFromLocalCacheSelection = true;
		try
		{
			viewModel.GitCloneUrl = string.Empty;
			if (_urlTextBox is not null && _urlTextBox.Text?.Length > 0)
				_urlTextBox.Text = string.Empty;
		}
		finally
		{
			_updatingUrlFromLocalCacheSelection = false;
		}
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

	private void ClearRecentRepositorySelection()
	{
		if (_recentRepositoriesComboBox is not null)
			_recentRepositoriesComboBox.SelectedItem = null;
	}

    private void OnRecentRepositoriesDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is not ComboBox comboBox ||
            DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

		comboBox.MaxDropDownHeight = ResolveRepositoryDropDownHeight(comboBox);

        Dispatcher.Post(() =>
        {
            var popup = comboBox
                .GetVisualDescendants()
                .OfType<Popup>()
                .FirstOrDefault(static candidate => string.Equals(candidate.Name, "PART_Popup", StringComparison.Ordinal));

			if (popup?.Child is Border popupBorder)
				ConstrainRepositoryDropDownWidth(comboBox, popupBorder);

            PopupBackdropConfigurator.TryApply(
                popup?.Child,
                this,
                viewModel.ActiveThemeEffect,
                PopupBackdropTransparencyFallback.Transparent);
        }, DispatcherPriority.Loaded);
    }

	private static void ConstrainRepositoryDropDownWidth(ComboBox comboBox, Border popupBorder)
	{
		var width = comboBox.Bounds.Width;
		if (!double.IsFinite(width) || width <= 0)
			return;

		popupBorder.Width = width;
		popupBorder.MinWidth = width;
		popupBorder.MaxWidth = width;
	}

	private double ResolveRepositoryDropDownHeight(ComboBox comboBox)
	{
		if (Owner is not Window owner || !owner.IsVisible || comboBox.Bounds.Height <= 0)
			return MaximumRepositoryDropDownHeight;

		var renderScaling = Math.Max(0.1, TopLevel.GetTopLevel(comboBox)?.RenderScaling ?? RenderScaling);
		var ownerTop = owner.PointToScreen(default).Y;
		var ownerBottom = owner.PointToScreen(new Point(0, owner.ClientSize.Height)).Y;
		var comboTop = comboBox.PointToScreen(default).Y;
		var comboBottom = comboBox.PointToScreen(new Point(0, comboBox.Bounds.Height)).Y;
		var availableAbove = (comboTop - ownerTop) / renderScaling - RepositoryDropDownEdgeMargin;
		var availableBelow = (ownerBottom - comboBottom) / renderScaling - RepositoryDropDownEdgeMargin;
		var placementIndependentHeight = Math.Min(availableAbove, availableBelow);

		return Math.Clamp(
			placementIndependentHeight,
			MinimumRepositoryDropDownHeight,
			MaximumRepositoryDropDownHeight);
	}

	private bool CloseOpenRepositoryDropDown()
	{
		var closed = false;
		if (_recentRepositoriesComboBox?.IsDropDownOpen == true)
		{
			_recentRepositoriesComboBox.IsDropDownOpen = false;
			closed = true;
		}
		if (_localCacheComboBox?.IsDropDownOpen == true)
		{
			_localCacheComboBox.IsDropDownOpen = false;
			closed = true;
		}

		return closed;
	}
}

internal sealed class RepositoryCacheEntryEventArgs(RepositoryCacheEntryViewModel entry) : EventArgs
{
	public RepositoryCacheEntryViewModel Entry { get; } = entry;
}
