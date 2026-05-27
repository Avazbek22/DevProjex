namespace DevProjex.Avalonia.Coordinators;

internal static class GitBranchMenuScrollBehavior
{
    public const int VisibleItemLimit = 15;
    public const string ScrollableClass = "git-branch-menu-scrollable";
    public const string ExternalScrollBarClass = "git-branch-external-scrollbar";

    private const double PopupMaxHeight = 518;
    private const double ScrollViewerMaxHeight = 510;
    private const double IndicatorWidth = 12;
    private const double IndicatorTrackWidth = 4;
    private const double IndicatorThumbWidth = 6;
    private const double IndicatorThumbMinHeight = 32;
    private const double IndicatorVerticalInset = 4;
    private const string ScrollWrapperClass = "git-branch-menu-scroll-wrapper";
    private const string IndicatorTrackClass = "git-branch-scroll-indicator-track";
    private const string IndicatorThumbClass = "git-branch-scroll-indicator-thumb";

    public static void SetScrollable(MenuItem branchMenuItem, int branchCount)
    {
        if (branchCount > VisibleItemLimit)
        {
            if (!branchMenuItem.Classes.Contains(ScrollableClass))
                branchMenuItem.Classes.Add(ScrollableClass);

            return;
        }

        branchMenuItem.Classes.Remove(ScrollableClass);
        ResetExternalScrollIndicator(branchMenuItem);
    }

    public static void HandleSubmenuOpened(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not MenuItem menuItem ||
            !menuItem.Classes.Contains(ScrollableClass))
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                Apply(menuItem);
                Dispatcher.UIThread.Post(() => Apply(menuItem), DispatcherPriority.Render);
            },
            DispatcherPriority.Loaded);
    }

    private static void Apply(MenuItem branchMenuItem)
    {
        if (!branchMenuItem.Classes.Contains(ScrollableClass))
            return;

        var popup = branchMenuItem.GetVisualDescendants().OfType<Popup>().FirstOrDefault();
        if (popup?.Child is not Visual popupRoot ||
            popup.Child is not Border popupBorder)
        {
            return;
        }

        popupBorder.MaxHeight = PopupMaxHeight;

        var scrollViewer = popupRoot.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scrollViewer is null)
            return;

        scrollViewer.MaxHeight = ScrollViewerMaxHeight;
        scrollViewer.AllowAutoHide = false;
        scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;

        EnsureExternalScrollIndicator(popupBorder, scrollViewer);
    }

    private static void EnsureExternalScrollIndicator(Border popupBorder, ScrollViewer scrollViewer)
    {
        if (popupBorder.Child is Grid existingGrid &&
            existingGrid.Classes.Contains(ScrollWrapperClass))
        {
            if (existingGrid.Children
                    .OfType<Control>()
                    .FirstOrDefault(control => control.Classes.Contains(ExternalScrollBarClass))?
                    .Tag is ScrollIndicatorBinding binding)
            {
                binding.SyncFromViewer();
            }

            return;
        }

        if (popupBorder.Child is not Control popupContent)
            return;

        popupBorder.Child = null;

        var wrapper = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            }
        };
        wrapper.Classes.Add(ScrollWrapperClass);

        Grid.SetColumn(popupContent, 0);
        wrapper.Children.Add(popupContent);

        var indicator = CreateScrollIndicator(scrollViewer);
        Grid.SetColumn(indicator, 1);
        wrapper.Children.Add(indicator);

        popupBorder.Child = wrapper;
    }

    private static Grid CreateScrollIndicator(ScrollViewer scrollViewer)
    {
        var trackBrush = TryGetThemeBrush("AppBorderBrush") ?? new SolidColorBrush(Color.FromArgb(96, 128, 128, 128));
        var thumbBrush = TryGetThemeBrush("AppMutedTextBrush") ?? new SolidColorBrush(Color.FromArgb(220, 160, 160, 160));

        var indicator = new Grid
        {
            Width = IndicatorWidth,
            MinWidth = IndicatorWidth,
            Background = Brushes.Transparent,
            ClipToBounds = true,
            IsHitTestVisible = true,
            Opacity = 1
        };
        indicator.Classes.Add(ExternalScrollBarClass);

        var track = new Border
        {
            Width = IndicatorTrackWidth,
            Margin = new Thickness(0, IndicatorVerticalInset),
            CornerRadius = new CornerRadius(IndicatorTrackWidth / 2),
            Background = trackBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Opacity = 0.65
        };
        track.Classes.Add(IndicatorTrackClass);

        var thumb = new Border
        {
            Width = IndicatorThumbWidth,
            MinHeight = IndicatorThumbMinHeight,
            CornerRadius = new CornerRadius(IndicatorThumbWidth / 2),
            Background = thumbBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = 0.95
        };
        thumb.Classes.Add(IndicatorThumbClass);

        indicator.Children.Add(track);
        indicator.Children.Add(thumb);
        indicator.Tag = new ScrollIndicatorBinding(scrollViewer, indicator, track, thumb);
        return indicator;
    }

    private static void ResetExternalScrollIndicator(MenuItem branchMenuItem)
    {
        foreach (var popup in branchMenuItem.GetVisualDescendants().OfType<Popup>())
        {
            if (popup.Child is not Border popupBorder)
                continue;

            popupBorder.ClearValue(Layoutable.MaxHeightProperty);

            if (popupBorder.Child is not Grid grid ||
                !grid.Classes.Contains(ScrollWrapperClass))
            {
                continue;
            }

            foreach (var indicator in grid.Children
                         .OfType<Control>()
                         .Where(control => control.Classes.Contains(ExternalScrollBarClass)))
            {
                if (indicator.Tag is IDisposable disposable)
                    disposable.Dispose();

                indicator.Tag = null;
            }

            var content = grid.Children.FirstOrDefault(child => !child.Classes.Contains(ExternalScrollBarClass));
            if (content is null)
                continue;

            grid.Children.Remove(content);
            ResetPopupScrollViewer(content);
            popupBorder.Child = content;
        }
    }

    private static void ResetPopupScrollViewer(Control content)
    {
        if (content is ScrollViewer scrollViewer)
            ResetPopupScrollViewer(scrollViewer);

        if (content is Visual visual)
        {
            foreach (var descendantScrollViewer in visual.GetVisualDescendants().OfType<ScrollViewer>())
                ResetPopupScrollViewer(descendantScrollViewer);
        }
    }

    private static void ResetPopupScrollViewer(ScrollViewer scrollViewer)
    {
        scrollViewer.ClearValue(Layoutable.MaxHeightProperty);
        scrollViewer.ClearValue(ScrollViewer.AllowAutoHideProperty);
        scrollViewer.ClearValue(ScrollViewer.HorizontalScrollBarVisibilityProperty);
        scrollViewer.ClearValue(ScrollViewer.VerticalScrollBarVisibilityProperty);
    }

    private static IBrush? TryGetThemeBrush(string key)
    {
        var app = global::Avalonia.Application.Current;
        var themeVariant = app?.ActualThemeVariant ?? ThemeVariant.Light;
        return app?.TryFindResource(key, themeVariant, out var resource) == true
            ? resource as IBrush
            : null;
    }

    private sealed class ScrollIndicatorBinding : IDisposable
    {
        private readonly ScrollViewer _scrollViewer;
        private readonly Control _indicator;
        private readonly Control _track;
        private readonly Border _thumb;
        private readonly TranslateTransform _thumbTransform = new();
        private global::Avalonia.Input.IPointer? _capturedPointer;

        public ScrollIndicatorBinding(ScrollViewer scrollViewer, Control indicator, Control track, Border thumb)
        {
            _scrollViewer = scrollViewer;
            _indicator = indicator;
            _track = track;
            _thumb = thumb;
            _thumb.RenderTransform = _thumbTransform;

            _scrollViewer.PropertyChanged += OnScrollViewerPropertyChanged;
            _indicator.PropertyChanged += OnIndicatorPropertyChanged;
            _track.PropertyChanged += OnIndicatorPropertyChanged;
            _indicator.PointerPressed += OnIndicatorPointerPressed;
            _indicator.PointerMoved += OnIndicatorPointerMoved;
            _indicator.PointerReleased += OnIndicatorPointerReleased;

            SyncFromViewer();
            Dispatcher.UIThread.Post(SyncFromViewer, DispatcherPriority.Loaded);
            Dispatcher.UIThread.Post(SyncFromViewer, DispatcherPriority.Render);
        }

        public void SyncFromViewer()
        {
            var maxOffset = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
            var trackHeight = ResolveTrackHeight();
            var hasOverflow = maxOffset > 0.5 && trackHeight > 1;

            _indicator.IsVisible = hasOverflow;
            if (!hasOverflow)
                return;

            var extentHeight = Math.Max(_scrollViewer.Extent.Height, _scrollViewer.Viewport.Height);
            var viewportRatio = extentHeight <= 0 ? 1 : _scrollViewer.Viewport.Height / extentHeight;
            var thumbHeight = Math.Clamp(trackHeight * viewportRatio, IndicatorThumbMinHeight, trackHeight);
            var availableTravel = Math.Max(0, trackHeight - thumbHeight);
            var offsetRatio = maxOffset <= 0 ? 0 : Math.Clamp(_scrollViewer.Offset.Y / maxOffset, 0, 1);

            _thumb.Height = thumbHeight;
            _thumbTransform.Y = IndicatorVerticalInset + availableTravel * offsetRatio;
        }

        private double ResolveTrackHeight()
        {
            var trackHeight = _track.Bounds.Height - IndicatorVerticalInset * 2;
            if (trackHeight > 1)
                return trackHeight;

            var scrollViewerHeight = _scrollViewer.Bounds.Height - IndicatorVerticalInset * 2;
            return Math.Max(0, scrollViewerHeight);
        }

        private void ScrollToPointer(PointerEventArgs e)
        {
            var maxOffset = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
            var trackHeight = ResolveTrackHeight();
            if (maxOffset <= 0.5 || trackHeight <= 1)
                return;

            var thumbHeight = Math.Clamp(
                trackHeight * (_scrollViewer.Viewport.Height / Math.Max(_scrollViewer.Extent.Height, _scrollViewer.Viewport.Height)),
                IndicatorThumbMinHeight,
                trackHeight);
            var availableTravel = Math.Max(1, trackHeight - thumbHeight);
            var pointerY = e.GetPosition(_indicator).Y - IndicatorVerticalInset - thumbHeight / 2;
            var offsetRatio = Math.Clamp(pointerY / availableTravel, 0, 1);
            _scrollViewer.Offset = new Vector(_scrollViewer.Offset.X, maxOffset * offsetRatio);
        }

        private void OnScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ScrollViewer.OffsetProperty ||
                e.Property == ScrollViewer.ExtentProperty ||
                e.Property == ScrollViewer.ViewportProperty ||
                e.Property == Layoutable.BoundsProperty)
            {
                SyncFromViewer();
            }
        }

        private void OnIndicatorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Layoutable.BoundsProperty)
                SyncFromViewer();
        }

        private void OnIndicatorPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(_indicator).Properties.IsLeftButtonPressed)
                return;

            _capturedPointer = e.Pointer;
            e.Pointer.Capture(_indicator);
            ScrollToPointer(e);
            e.Handled = true;
        }

        private void OnIndicatorPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_capturedPointer is null || !ReferenceEquals(_capturedPointer, e.Pointer))
                return;

            ScrollToPointer(e);
            e.Handled = true;
        }

        private void OnIndicatorPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_capturedPointer is null || !ReferenceEquals(_capturedPointer, e.Pointer))
                return;

            _capturedPointer.Capture(null);
            _capturedPointer = null;
            e.Handled = true;
        }

        public void Dispose()
        {
            if (_capturedPointer is not null)
            {
                _capturedPointer.Capture(null);
                _capturedPointer = null;
            }

            _scrollViewer.PropertyChanged -= OnScrollViewerPropertyChanged;
            _indicator.PropertyChanged -= OnIndicatorPropertyChanged;
            _track.PropertyChanged -= OnIndicatorPropertyChanged;
            _indicator.PointerPressed -= OnIndicatorPointerPressed;
            _indicator.PointerMoved -= OnIndicatorPointerMoved;
            _indicator.PointerReleased -= OnIndicatorPointerReleased;
        }
    }
}
