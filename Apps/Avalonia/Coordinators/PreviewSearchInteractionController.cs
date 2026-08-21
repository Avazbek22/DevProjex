using Avalonia.Animation;
using Avalonia.Animation.Easings;
using DevProjex.Avalonia.Services;
using DevProjex.Avalonia.Views;

namespace DevProjex.Avalonia.Coordinators;

internal sealed class PreviewSearchInteractionController : IDisposable
{
	private const double ToolBarHeight = 48.0;
	private const double PanelIslandSpacing = 4.0;
	private static readonly TimeSpan ToolBarAnimationDuration =
		UiTimingProfile.Scale(TimeSpan.FromMilliseconds(250));
	private static readonly TimeSpan SearchDebounceInterval =
		UiTimingProfile.Scale(TimeSpan.FromMilliseconds(200));
	private static readonly TimeSpan SearchButtonFadeDuration =
		UiTimingProfile.Scale(TimeSpan.FromMilliseconds(150));
	private static readonly TimeSpan HotkeyDebounceWindow =
		UiTimingProfile.Scale(TimeSpan.FromMilliseconds(220));

	private readonly MainWindowViewModel _viewModel;
	private readonly PreviewSearchBarView _searchBar;
	private readonly Border _container;
	private readonly Button _searchButton;
	private readonly VirtualizedPreviewTextControl _previewTextControl;
	private readonly DesktopShortcutModifiers _shortcutModifiers;
	private readonly CancellationTokenSource _lifetimeCts = new();
	private readonly TranslateTransform _transform;
	private CancellationTokenSource? _searchCts;
	private long _animationVersion;
	private long _searchVersion;
	private long _lastHotkeyTimestamp;
	private int _pendingHotkeyToggle;
	private bool _restoreVisibleWhenAvailable;
	private bool _disposed;

	public PreviewSearchInteractionController(
		MainWindowViewModel viewModel,
		PreviewSearchBarView searchBar,
		Border container,
		Button searchButton,
		VirtualizedPreviewTextControl previewTextControl,
		DesktopShortcutModifiers? shortcutModifiers = null)
	{
		_viewModel = viewModel;
		_searchBar = searchBar;
		_container = container;
		_searchButton = searchButton;
		_previewTextControl = previewTextControl;
		_shortcutModifiers = shortcutModifiers ?? DesktopShortcutModifiers.Current;
		_transform = searchBar.RenderTransform as TranslateTransform ?? new TranslateTransform();
		searchBar.RenderTransform = _transform;
		_previewTextControl.SearchDocumentChanged += OnPreviewDocumentChanged;
		EnsureTransitions();
		UpdateSearchButtonAvailability();
		ForceHidden();
	}

	public bool IsAnimating { get; private set; }

	public void OnQueryChanged()
	{
		if (_disposed)
			return;

		ScheduleSearch(navigateToNearest: true, debounce: true);
	}

	public void OnAvailabilityChanged()
	{
		if (_disposed)
			return;

		UpdateSearchButtonAvailability();
		if (_viewModel.IsPreviewSearchAvailable)
		{
			if (_restoreVisibleWhenAvailable)
			{
				_restoreVisibleWhenAvailable = false;
				Show();
			}
			return;
		}

		Close(focusPreview: false);
	}

	public void ClearProjectState()
	{
		if (_disposed)
			return;

		CancelPendingSearch();
		_restoreVisibleWhenAvailable = false;
		_viewModel.PreviewSearchQuery = string.Empty;
		_viewModel.SetPreviewSearchInProgress(false);
		_previewTextControl.ClearSearchMatches();
		_viewModel.UpdatePreviewSearchMatchSummary(0, 0, matchesCapped: false);

		if (_viewModel.PreviewSearchVisible || _container.IsVisible)
			Close(focusPreview: false);
	}

	public void RestoreProjectState(string query, bool visible)
	{
		if (_disposed)
			return;

		_viewModel.PreviewSearchQuery = query;
		_restoreVisibleWhenAvailable = visible && !_viewModel.IsPreviewSearchAvailable;
		if (visible && _viewModel.IsPreviewSearchAvailable)
		{
			_restoreVisibleWhenAvailable = false;
			if (!_viewModel.PreviewSearchVisible)
				Show();
			else
				ScheduleSearch(navigateToNearest: false, debounce: false);
			return;
		}

		Close(focusPreview: false);
	}

	public void Toggle()
	{
		if (_disposed || !_viewModel.IsPreviewSearchAvailable)
			return;

		if (_viewModel.PreviewSearchVisible)
		{
			Close();
			return;
		}

		Show();
	}

	public bool TryHandleToggleHotkey(KeyEventArgs e)
	{
		if (_disposed ||
		    e.Key != Key.F ||
		    !_shortcutModifiers.IsPrimaryWithShift(e.KeyModifiers))
		{
			return false;
		}

		if (!IsHotkeyDebounced() &&
		    Interlocked.CompareExchange(ref _pendingHotkeyToggle, 1, 0) == 0)
		{
			Dispatcher.UIThread.Post(
				() =>
				{
					try
					{
						Toggle();
					}
					finally
					{
						Interlocked.Exchange(ref _pendingHotkeyToggle, 0);
					}
				},
				DispatcherPriority.Background);
		}

		e.Handled = true;
		return true;
	}

	public bool TryHandleEscape(KeyEventArgs e)
	{
		if (_disposed || e.Key != Key.Escape || !_viewModel.PreviewSearchVisible)
			return false;

		Close();
		e.Handled = true;
		return true;
	}

	public bool TryHandleNavigationHotkey(KeyEventArgs e)
	{
		if (_disposed || e.Key != Key.F3 || !_viewModel.PreviewSearchVisible)
			return false;

		Navigate(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
		e.Handled = true;
		return true;
	}

	public void HandleInputKey(KeyEventArgs e)
	{
		if (e.Key == Key.Escape)
		{
			Close();
			e.Handled = true;
			return;
		}

		if (e.Key is Key.Up or Key.Down && e.KeyModifiers == KeyModifiers.Alt)
		{
			_previewTextControl.NavigateRedaction(forward: e.Key == Key.Down);
			e.Handled = true;
			return;
		}

		if (e.Key != Key.Enter)
			return;

		Navigate(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
		e.Handled = true;
	}

	public void Navigate(int step)
	{
		if (_disposed || !_viewModel.PreviewSearchVisible)
			return;

		var currentIndex = _previewTextControl.NavigateSearchMatch(step);
		_viewModel.UpdatePreviewSearchMatchSummary(
			currentIndex,
			_previewTextControl.SearchMatchCount,
			_viewModel.PreviewSearchMatchesCapped);
	}

	public void Close(bool focusPreview = true)
	{
		if (_disposed || !_viewModel.PreviewSearchVisible && !_container.IsVisible)
			return;

		_viewModel.PreviewSearchVisible = false;
		CancelPendingSearch();
		_viewModel.SetPreviewSearchInProgress(false);
		_previewTextControl.ClearSearchMatches();
		_viewModel.UpdatePreviewSearchMatchSummary(0, 0, matchesCapped: false);
		if (focusPreview)
			_previewTextControl.Focus();

		StartAnimation(show: false);
	}

	public void Dispose()
	{
		if (_disposed)
			return;

		_disposed = true;
		_lifetimeCts.Cancel();
		CancelPendingSearch();
		_previewTextControl.SearchDocumentChanged -= OnPreviewDocumentChanged;
		Interlocked.Increment(ref _animationVersion);
		Interlocked.Exchange(ref _pendingHotkeyToggle, 0);
		ForceHidden();
		_lifetimeCts.Dispose();
	}

	private void Show()
	{
		_viewModel.PreviewSearchVisible = true;
		StartAnimation(show: true);
		ScheduleSearch(navigateToNearest: true, debounce: false);
		var animationVersion = Interlocked.Read(ref _animationVersion);
		_ = FocusAfterOpenAsync(animationVersion);
	}

	private void OnPreviewDocumentChanged(object? sender, EventArgs e)
	{
		if (_disposed || !_viewModel.PreviewSearchVisible)
			return;

		ScheduleSearch(navigateToNearest: true, debounce: false, scrollIntoView: false);
	}

	private void ScheduleSearch(
		bool navigateToNearest,
		bool debounce,
		bool scrollIntoView = true)
	{
		CancelPendingSearch();
		_viewModel.SetPreviewSearchInProgress(false);
		_previewTextControl.ClearSearchMatches();
		_viewModel.UpdatePreviewSearchMatchSummary(0, 0, matchesCapped: false);

		var query = _viewModel.PreviewSearchQuery;
		var document = _previewTextControl.Document;
		if (!_viewModel.PreviewSearchVisible ||
		    string.IsNullOrWhiteSpace(query) ||
		    document is null)
		{
			return;
		}

		_viewModel.SetPreviewSearchInProgress(true);
		var version = Interlocked.Increment(ref _searchVersion);
		var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
		_searchCts = cts;
		_ = SearchAsync(
			document,
			query,
			version,
			navigateToNearest,
			scrollIntoView,
			debounce,
			cts);
	}

	private async Task SearchAsync(
		IPreviewTextDocument document,
		string query,
		long version,
		bool navigateToNearest,
		bool scrollIntoView,
		bool debounce,
		CancellationTokenSource searchCts)
	{
		var cancellationToken = searchCts.Token;
		try
		{
			if (debounce)
				await Task.Delay(SearchDebounceInterval, cancellationToken);

			var result = await Task.Run(
				() => PreviewSearchIndex.Find(document, query, cancellationToken),
				cancellationToken);
			await Dispatcher.UIThread.InvokeAsync(
				() => PublishSearchResult(
					document,
					query,
					version,
					result,
					navigateToNearest,
					scrollIntoView),
				DispatcherPriority.Background,
				cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (Exception exception)
			when (exception is ObjectDisposedException or IOException or UnauthorizedAccessException)
		{
			// A replaced or unavailable file-backed preview must not fault the UI event loop.
		}
		finally
		{
			if (Interlocked.CompareExchange(ref _searchCts, null, searchCts) == searchCts)
				searchCts.Dispose();

			Dispatcher.UIThread.Post(
				() =>
				{
					if (!_disposed && version == Interlocked.Read(ref _searchVersion))
						_viewModel.SetPreviewSearchInProgress(false);
				},
				DispatcherPriority.Background);
		}
	}

	private void PublishSearchResult(
		IPreviewTextDocument document,
		string query,
		long version,
		PreviewSearchResult result,
		bool navigateToNearest,
		bool scrollIntoView)
	{
		if (_disposed ||
		    version != Interlocked.Read(ref _searchVersion) ||
		    !_viewModel.PreviewSearchVisible ||
		    !ReferenceEquals(document, _previewTextControl.Document) ||
		    !string.Equals(query, _viewModel.PreviewSearchQuery, StringComparison.Ordinal))
		{
			return;
		}

		var currentIndex = _previewTextControl.SetSearchMatches(
			result.Matches,
			navigateToNearest,
			scrollIntoView);
		_viewModel.UpdatePreviewSearchMatchSummary(
			currentIndex,
			result.Matches.Length,
			result.IsCapped);
		_viewModel.SetPreviewSearchInProgress(false);
	}

	private void StartAnimation(bool show)
	{
		var version = Interlocked.Increment(ref _animationVersion);
		IsAnimating = true;
		if (show)
		{
			_container.IsVisible = true;
			_searchBar.IsEnabled = true;
			_searchBar.IsHitTestVisible = true;
		}

		_container.Height = show ? ToolBarHeight : 0;
		_container.Margin = new Thickness(0, 0, 0, show ? PanelIslandSpacing : 0);
		_transform.Y = 0;
		_searchBar.Opacity = show ? 1 : 0;
		_ = CompleteAnimationAsync(version, show);
	}

	private async Task CompleteAnimationAsync(long version, bool show)
	{
		try
		{
			await Task.Delay(
				ToolBarAnimationDuration + UiTimingProfile.AnimationSettleBuffer,
				_lifetimeCts.Token);
			if (_disposed || version != Interlocked.Read(ref _animationVersion))
				return;

			if (!show && !_viewModel.PreviewSearchVisible)
				ForceHidden();
		}
		catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
		{
		}
		finally
		{
			if (version == Interlocked.Read(ref _animationVersion))
				IsAnimating = false;
		}
	}

	private async Task FocusAfterOpenAsync(long version)
	{
		try
		{
			await Task.Delay(
				ToolBarAnimationDuration + UiTimingProfile.AnimationSettleBuffer,
				_lifetimeCts.Token);
			if (_disposed ||
			    version != Interlocked.Read(ref _animationVersion) ||
			    !_viewModel.PreviewSearchVisible)
			{
				return;
			}

			var input = _searchBar.SearchBoxControl;
			if (input?.Focus() == true)
				input.SelectAll();
		}
		catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
		{
		}
	}

	private void EnsureTransitions()
	{
		_container.Transitions =
		[
			new DoubleTransition
			{
				Property = Layoutable.HeightProperty,
				Duration = ToolBarAnimationDuration,
				Easing = new CubicEaseOut()
			},
			new ThicknessTransition
			{
				Property = Layoutable.MarginProperty,
				Duration = ToolBarAnimationDuration,
				Easing = new CubicEaseOut()
			}
		];
		_searchBar.Transitions =
		[
			new DoubleTransition
			{
				Property = Visual.OpacityProperty,
				Duration = ToolBarAnimationDuration,
				Easing = new CubicEaseOut()
			}
		];

		var buttonTransitions = new Transitions();
		if (_searchButton.Transitions is { } currentTransitions)
		{
			foreach (var transition in currentTransitions)
				buttonTransitions.Add(transition);
		}

		buttonTransitions.Add(
			new DoubleTransition
			{
				Property = Visual.OpacityProperty,
				Duration = SearchButtonFadeDuration,
				Easing = new CubicEaseOut()
			});
		_searchButton.Transitions = buttonTransitions;
	}

	private void UpdateSearchButtonAvailability()
	{
		var available = _viewModel.IsPreviewSearchAvailable;
		_searchButton.IsHitTestVisible = available;
		_searchButton.Opacity = available ? 1 : 0;
		if (!available)
			ToolTip.SetIsOpen(_searchButton, false);
	}

	private void ForceHidden()
	{
		_container.Height = 0;
		_container.Margin = new Thickness(0);
		_container.IsVisible = false;
		_transform.Y = 0;
		_searchBar.Opacity = 0;
		_searchBar.IsHitTestVisible = false;
		_searchBar.IsEnabled = false;
		IsAnimating = false;
	}

	private bool IsHotkeyDebounced()
	{
		var now = Stopwatch.GetTimestamp();
		var previous = Interlocked.Read(ref _lastHotkeyTimestamp);
		if (previous != 0)
		{
			var elapsed = TimeSpan.FromSeconds(
				(now - previous) / (double)Stopwatch.Frequency);
			if (elapsed < HotkeyDebounceWindow)
				return true;
		}

		Interlocked.Exchange(ref _lastHotkeyTimestamp, now);
		return false;
	}

	private void CancelPendingSearch()
	{
		Interlocked.Increment(ref _searchVersion);
		var cts = Interlocked.Exchange(ref _searchCts, null);
		if (cts is null)
			return;

		cts.Cancel();
		cts.Dispose();
	}
}
