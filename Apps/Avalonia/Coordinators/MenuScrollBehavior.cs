namespace DevProjex.Avalonia.Coordinators;

internal static class MenuScrollBehavior
{
    public const int VisibleItemLimit = 15;
    public const string ScrollableClass = "menu-scrollable";
    public const string ExternalScrollBarClass = "menu-external-scrollbar";
    public const string ArrowButtonClass = "menu-scroll-arrow-button";

    private const double PopupMaxHeight = 530;
    private const double ScrollViewerMaxHeight = 520;
    private const double PopupChromeHeight = 10;
    private const double IndicatorWidth = 12;
    private const double ArrowButtonHeight = 14;
    private const double IndicatorTrackWidth = 3;
    private const double IndicatorThumbWidth = 5;
    private const double IndicatorThumbMinHeight = 32;
    private const double IndicatorVerticalInset = 4;
    private const string ScrollWrapperClass = "menu-scroll-wrapper";
    private const string IndicatorTrackClass = "menu-scroll-indicator-track";
    private const string IndicatorThumbClass = "menu-scroll-indicator-thumb";

    public static void SetScrollable(MenuItem menuItem, int itemCount)
    {
        if (itemCount > VisibleItemLimit)
        {
            if (!menuItem.Classes.Contains(ScrollableClass))
                menuItem.Classes.Add(ScrollableClass);

            if (menuItem.IsSubMenuOpen)
                ScheduleApply(menuItem);

            return;
        }

        menuItem.Classes.Remove(ScrollableClass);
        ResetExternalScrollIndicator(menuItem);
    }

    public static void HandleSubmenuOpened(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not MenuItem menuItem ||
            !menuItem.Classes.Contains(ScrollableClass))
        {
            return;
        }

        ScheduleApply(menuItem);
    }

    private static void ScheduleApply(MenuItem menuItem) =>
        menuItem.Dispatcher.Post(
            () =>
            {
                Apply(menuItem);
                menuItem.Dispatcher.Post(() => Apply(menuItem), DispatcherPriority.Render);
            },
            DispatcherPriority.Loaded);

    private static void Apply(MenuItem menuItem)
    {
        if (!menuItem.Classes.Contains(ScrollableClass))
            return;

        var popup = menuItem.GetVisualDescendants().OfType<Popup>().FirstOrDefault();
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

        DisableHoverScrollButtons(popupRoot);
        var rowStep = FitWholeMenuRows(popupRoot, popupBorder, scrollViewer);
        EnsureExternalScrollIndicator(popupBorder, scrollViewer, rowStep);
    }

    private static void DisableHoverScrollButtons(Visual popupRoot)
    {
        foreach (var button in popupRoot.GetVisualDescendants().OfType<RepeatButton>())
        {
            if (button.Classes.Contains(ArrowButtonClass))
                continue;

            button.IsVisible = false;
            button.IsHitTestVisible = false;
        }
    }

    private static double FitWholeMenuRows(
        Visual popupRoot,
        Border popupBorder,
        ScrollViewer scrollViewer)
    {
        var menuItems = popupRoot
            .GetVisualDescendants()
            .OfType<MenuItem>()
            .Take(VisibleItemLimit + 1)
            .ToArray();
        if (menuItems.Length <= VisibleItemLimit)
            return 0;

        var origin = menuItems[VisibleItemLimit].TranslatePoint(default, scrollViewer);
        if (origin is not { } point)
            return 0;

        var rowBoundary = point.Y + scrollViewer.Offset.Y;
        if (!double.IsFinite(rowBoundary) || rowBoundary <= 0)
            return 0;

        var viewportHeight = Math.Min(ScrollViewerMaxHeight, Math.Ceiling(rowBoundary));
        scrollViewer.MaxHeight = viewportHeight;
        popupBorder.MaxHeight = viewportHeight + PopupChromeHeight;

        if (menuItems[0].TranslatePoint(default, scrollViewer) is not { } firstOrigin ||
            menuItems[1].TranslatePoint(default, scrollViewer) is not { } secondOrigin)
        {
            return 0;
        }

        var rowStep = secondOrigin.Y - firstOrigin.Y;
        return double.IsFinite(rowStep) && rowStep > 0 ? rowStep : 0;
    }

    private static void EnsureExternalScrollIndicator(
        Border popupBorder,
        ScrollViewer scrollViewer,
        double rowStep)
    {
        if (popupBorder.Child is Grid existingGrid &&
            existingGrid.Classes.Contains(ScrollWrapperClass))
        {
            if (existingGrid.Children
                    .OfType<Control>()
                    .FirstOrDefault(control => control.Classes.Contains(ExternalScrollBarClass))?
                    .Tag is ScrollIndicatorBinding binding)
            {
                binding.UpdateRowStep(rowStep);
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

        var indicator = CreateScrollIndicator(scrollViewer, rowStep);
        Grid.SetColumn(indicator, 1);
        wrapper.Children.Add(indicator);

        popupBorder.Child = wrapper;
    }

    private static Grid CreateScrollIndicator(ScrollViewer scrollViewer, double rowStep)
    {
        var trackBrush = TryGetThemeBrush("AppBorderBrush") ?? new SolidColorBrush(Color.FromArgb(96, 128, 128, 128));
        var thumbBrush = TryGetThemeBrush("AppMutedTextBrush") ?? new SolidColorBrush(Color.FromArgb(220, 160, 160, 160));

        var indicator = new Grid
        {
            Width = IndicatorWidth,
            MinWidth = IndicatorWidth,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto)
            },
            Background = Brushes.Transparent,
            ClipToBounds = true,
            IsHitTestVisible = true,
            Opacity = 1
        };
        indicator.Classes.Add(ExternalScrollBarClass);

        var upButton = CreateArrowButton(isUp: true);
        Grid.SetRow(upButton, 0);

        var trackHost = new Grid
        {
            ClipToBounds = true
        };
        Grid.SetRow(trackHost, 1);

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

        trackHost.Children.Add(track);
        trackHost.Children.Add(thumb);

        var downButton = CreateArrowButton(isUp: false);
        Grid.SetRow(downButton, 2);

        indicator.Children.Add(upButton);
        indicator.Children.Add(trackHost);
        indicator.Children.Add(downButton);
        indicator.Tag = new ScrollIndicatorBinding(
            scrollViewer,
            indicator,
            trackHost,
            thumb,
            upButton,
            downButton,
            rowStep);
        return indicator;
    }

    private static RepeatButton CreateArrowButton(bool isUp)
    {
        var button = new RepeatButton
        {
            Width = IndicatorWidth,
            Height = ArrowButtonHeight,
            Padding = default,
            BorderThickness = default,
            Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new PathIcon
            {
                Width = 7,
                Height = 5,
                Data = StreamGeometry.Parse(isUp
                    ? "M 0,4 L 3.5,0.5 L 7,4 L 6,5 L 3.5,2.5 L 1,5 Z"
                    : "M 0,1 L 1,0 L 3.5,2.5 L 6,0 L 7,1 L 3.5,4.5 Z")
            }
        };
        button.Classes.Add(ArrowButtonClass);
        return button;
    }

    private static void ResetExternalScrollIndicator(MenuItem menuItem)
    {
        foreach (var popup in menuItem.GetVisualDescendants().OfType<Popup>())
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

        foreach (var button in scrollViewer.GetVisualDescendants().OfType<RepeatButton>())
        {
            if (button.Classes.Contains(ArrowButtonClass))
                continue;

            button.ClearValue(Visual.IsVisibleProperty);
            button.ClearValue(InputElement.IsHitTestVisibleProperty);
        }
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
        private readonly RepeatButton _upButton;
        private readonly RepeatButton _downButton;
        private readonly TranslateTransform _thumbTransform = new();
        private IPointer? _capturedPointer;
        private double _rowStep;
        private bool _isSnappingOffset;

        public ScrollIndicatorBinding(
            ScrollViewer scrollViewer,
            Control indicator,
            Control track,
            Border thumb,
            RepeatButton upButton,
            RepeatButton downButton,
            double rowStep)
        {
            _scrollViewer = scrollViewer;
            _indicator = indicator;
            _track = track;
            _thumb = thumb;
            _upButton = upButton;
            _downButton = downButton;
            _rowStep = rowStep;
            _thumb.RenderTransform = _thumbTransform;

            _scrollViewer.PropertyChanged += OnScrollViewerPropertyChanged;
            _indicator.PropertyChanged += OnIndicatorPropertyChanged;
            _track.PropertyChanged += OnIndicatorPropertyChanged;
            _track.PointerPressed += OnTrackPointerPressed;
            _track.PointerMoved += OnTrackPointerMoved;
            _track.PointerReleased += OnTrackPointerReleased;
            _upButton.Click += OnUpButtonClick;
            _downButton.Click += OnDownButtonClick;

            SyncFromViewer();
            _scrollViewer.Dispatcher.Post(SyncFromViewer, DispatcherPriority.Loaded);
            _scrollViewer.Dispatcher.Post(SyncFromViewer, DispatcherPriority.Render);
        }

        public void UpdateRowStep(double rowStep)
        {
            if (double.IsFinite(rowStep) && rowStep > 0)
                _rowStep = rowStep;
        }

        public void SyncFromViewer()
        {
            var maxOffset = ResolveMaximumOffset();
            var trackHeight = ResolveTrackHeight();
            var hasOverflow = maxOffset > 0.5 && trackHeight > 1;

            _indicator.IsVisible = hasOverflow;
            _upButton.IsEnabled = hasOverflow && _scrollViewer.Offset.Y > 0.5;
            _downButton.IsEnabled = hasOverflow && _scrollViewer.Offset.Y < maxOffset - 0.5;
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
            var maxOffset = ResolveMaximumOffset();
            var trackHeight = ResolveTrackHeight();
            if (maxOffset <= 0.5 || trackHeight <= 1)
                return;

            var thumbHeight = Math.Clamp(
                trackHeight * (_scrollViewer.Viewport.Height / Math.Max(_scrollViewer.Extent.Height, _scrollViewer.Viewport.Height)),
                IndicatorThumbMinHeight,
                trackHeight);
            var availableTravel = Math.Max(1, trackHeight - thumbHeight);
            var pointerY = e.GetPosition(_track).Y - IndicatorVerticalInset - thumbHeight / 2;
            var offsetRatio = Math.Clamp(pointerY / availableTravel, 0, 1);
            SetVerticalOffset(maxOffset * offsetRatio);
        }

        private double ResolveMaximumOffset()
        {
            var rawMaximum = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
            if (_rowStep <= 0.5 || rawMaximum < _rowStep)
                return rawMaximum;

            return Math.Floor(rawMaximum / _rowStep) * _rowStep;
        }

        private void SetVerticalOffset(double requestedOffset)
        {
            var maximumOffset = ResolveMaximumOffset();
            var clampedOffset = Math.Clamp(requestedOffset, 0, maximumOffset);
            var snappedOffset = _rowStep > 0.5
                ? Math.Round(clampedOffset / _rowStep) * _rowStep
                : clampedOffset;
            snappedOffset = Math.Clamp(snappedOffset, 0, maximumOffset);
            if (Math.Abs(_scrollViewer.Offset.Y - snappedOffset) <= 0.5)
                return;

            _isSnappingOffset = true;
            try
            {
                _scrollViewer.Offset = new Vector(_scrollViewer.Offset.X, snappedOffset);
            }
            finally
            {
                _isSnappingOffset = false;
            }
        }

        private void SnapOffsetToRows()
        {
            if (!_isSnappingOffset)
                SetVerticalOffset(_scrollViewer.Offset.Y);
        }

        private void OnScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ScrollViewer.OffsetProperty ||
                e.Property == ScrollViewer.ExtentProperty ||
                e.Property == ScrollViewer.ViewportProperty ||
                e.Property == Layoutable.BoundsProperty)
            {
                if (e.Property == ScrollViewer.OffsetProperty)
                    SnapOffsetToRows();

                SyncFromViewer();
            }
        }

        private void OnIndicatorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Layoutable.BoundsProperty)
                SyncFromViewer();
        }

        private void OnTrackPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(_indicator).Properties.IsLeftButtonPressed)
                return;

            _capturedPointer = e.Pointer;
            e.Pointer.Capture(_track);
            ScrollToPointer(e);
            e.Handled = true;
        }

        private void OnTrackPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_capturedPointer is null || !ReferenceEquals(_capturedPointer, e.Pointer))
                return;

            ScrollToPointer(e);
            e.Handled = true;
        }

        private void OnTrackPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_capturedPointer is null || !ReferenceEquals(_capturedPointer, e.Pointer))
                return;

            _capturedPointer.Capture(null);
            _capturedPointer = null;
            e.Handled = true;
        }

        private void OnUpButtonClick(object? sender, RoutedEventArgs e)
        {
            SetVerticalOffset(_scrollViewer.Offset.Y - Math.Max(1, _rowStep));
            SyncFromViewer();
            e.Handled = true;
        }

        private void OnDownButtonClick(object? sender, RoutedEventArgs e)
        {
            SetVerticalOffset(_scrollViewer.Offset.Y + Math.Max(1, _rowStep));
            SyncFromViewer();
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
            _track.PointerPressed -= OnTrackPointerPressed;
            _track.PointerMoved -= OnTrackPointerMoved;
            _track.PointerReleased -= OnTrackPointerReleased;
            _upButton.Click -= OnUpButtonClick;
            _downButton.Click -= OnDownButtonClick;
        }
    }
}
