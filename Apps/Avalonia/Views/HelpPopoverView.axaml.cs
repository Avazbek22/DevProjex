using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls.Documents;

namespace DevProjex.Avalonia.Views;

public partial class HelpPopoverView : UserControl
{
    private const double SearchPanelWidth = 390;
    private const double SearchButtonWidth = 32;
    private const double SearchContentOffset = 16;
    private const int MinimumSearchQueryLength = 2;
    private static readonly TimeSpan SearchPanelAnimationDuration =
        UiTimingProfile.Scale(TimeSpan.FromMilliseconds(220));
    private static readonly TimeSpan SearchContentAnimationDuration =
        UiTimingProfile.Scale(TimeSpan.FromMilliseconds(180));
    private static readonly TimeSpan SearchFadeDuration =
        UiTimingProfile.Scale(TimeSpan.FromMilliseconds(150));
    private static readonly TimeSpan SearchDebounceInterval =
        UiTimingProfile.Scale(TimeSpan.FromMilliseconds(200));

    public event EventHandler<RoutedEventArgs>? CloseRequested;
    private readonly List<HelpTextEntry> _searchEntries = [];
    private readonly List<HelpSearchMatch> _searchMatches = [];
    private readonly Dictionary<int, int> _searchMarkerMatchIndices = [];
    private Border _searchPanel = null!;
    private Grid _searchContent = null!;
    private Grid _bodySurface = null!;
    private TextBox _searchBox = null!;
    private TextBlock _searchMatchSummary = null!;
    private Button _searchButton = null!;
    private ScrollViewer _bodyScrollViewer = null!;
    private PreviewMarkerBar _searchMarkerBar = null!;
    private ScrollBar? _bodyVerticalScrollBar;
    private PreviewMarkerSnapshot _searchMarkerSnapshot = PreviewMarkerSnapshot.Empty;
    private Cursor? _searchMarkerHandCursor;
    private Cursor? _searchMarkerDragCursor;
    private InputElement? _searchMarkerCursorTarget;
    private IPointer? _searchMarkerDragPointer;
    private double _searchMarkerDragStartY;
    private double _searchMarkerDragStartOffsetY;
    private bool _searchMarkerDragging;
    private bool _searchMarkerScrollBarAllowAutoHide;
    private bool _searchMarkerScrollBarInteractionActive;
    private bool _searchMarkerRailPointerOver;
    private DispatcherTimer? _searchDebounceTimer;
    private MainWindowViewModel? _boundViewModel;
    private int _currentSearchMatchIndex = -1;
    private long _searchAnimationVersion;
    private bool _isSearchOpen;
    private bool _themeHandlerAttached;

    public HelpPopoverView()
    {
        AvaloniaXamlLoader.Load(this);
        ResolveSearchControls();
        ConfigureSearchTransitions();
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        _bodyScrollViewer.LayoutUpdated += OnBodyScrollViewerLayoutUpdated;
        _bodySurface.AddHandler(
            InputElement.PointerPressedEvent,
            OnSearchMarkerPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        _bodySurface.AddHandler(
            InputElement.PointerMovedEvent,
            OnSearchMarkerPointerMoved,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        _bodySurface.AddHandler(
            InputElement.PointerReleasedEvent,
            OnSearchMarkerPointerReleased,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        _bodySurface.PointerExited += OnSearchMarkerPointerExited;
        _bodyScrollViewer.PointerCaptureLost += OnSearchMarkerPointerCaptureLost;
        KeyDown += OnHelpKeyDown;
    }

    internal bool IsSearchOpen => _isSearchOpen;
    internal int SearchMatchCount => _searchMatches.Count;
    internal int CurrentSearchMatchIndex => _currentSearchMatchIndex + 1;
    internal int SearchMarkerCount => _searchMarkerSnapshot.Markers.Length;
    internal TextBox SearchBoxControl => _searchBox;
    internal TextBlock? CurrentSearchMatchControl =>
        _currentSearchMatchIndex >= 0 && _currentSearchMatchIndex < _searchMatches.Count
            ? _searchMatches[_currentSearchMatchIndex].Entry.Control
            : null;

    internal int ResolveSearchMarkerMatchIndex(PreviewMarkerTarget target)
        => target.Category == PreviewMarkerCategory.Search &&
           _searchMarkerMatchIndices.TryGetValue(target.LineNumber, out var matchIndex)
            ? matchIndex + 1
            : 0;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        BindAndRenderCurrentViewModel();
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        AttachThemeHandler();
        BindAndRenderCurrentViewModel();
    }

    private void BindAndRenderCurrentViewModel()
    {
        var bodyPanel = GetBodyPanel();
        if (bodyPanel is null)
            return;

        var nextViewModel = DataContext as MainWindowViewModel;
        if (!ReferenceEquals(_boundViewModel, nextViewModel))
        {
            if (_boundViewModel is not null)
                _boundViewModel.PropertyChanged -= OnViewModelPropertyChanged;

            _boundViewModel = nextViewModel;

            if (_boundViewModel is not null)
                _boundViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        if (_boundViewModel is null)
        {
            CancelPendingSearch();
            bodyPanel.Children.Clear();
            _searchEntries.Clear();
            ClearSearchResults();
            return;
        }

        BuildBody(bodyPanel, _boundViewModel.HelpHelpBody);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.HelpHelpBody) && DataContext is MainWindowViewModel viewModel)
        {
            var bodyPanel = GetBodyPanel();
            if (bodyPanel is null)
                return;

            BuildBody(bodyPanel, viewModel.HelpHelpBody);
        }
    }

    private void BuildBody(StackPanel bodyPanel, string? rawText)
    {
        CancelPendingSearch();
        bodyPanel.Children.Clear();
        _searchEntries.Clear();
        ClearSearchResults();
        if (string.IsNullOrWhiteSpace(rawText))
            return;

        var lines = rawText.Replace("\r\n", "\n").Split('\n');
        var pendingSpacer = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                pendingSpacer = true;
                continue;
            }

            if (pendingSpacer)
            {
                bodyPanel.Children.Add(new Border { Height = 8 });
                pendingSpacer = false;
            }

            if (TryAddHeading(bodyPanel, trimmed)) continue;
            if (TryAddSubheading(bodyPanel, trimmed)) continue;
            if (TryAddBullet(bodyPanel, trimmed)) continue;
            if (TryAddNumbered(bodyPanel, trimmed)) continue;

            bodyPanel.Children.Add(CreateParagraph(trimmed));
        }

        if (_isSearchOpen)
            RefreshSearch(navigateToFirstMatch: true);
    }

    private bool TryAddHeading(StackPanel bodyPanel, string line)
    {
        if (!line.StartsWith("## ", StringComparison.Ordinal))
            return false;

        bodyPanel.Children.Add(CreateHeading(line[3..], 16));
        return true;
    }

    private bool TryAddSubheading(StackPanel bodyPanel, string line)
    {
        if (!line.StartsWith("### ", StringComparison.Ordinal))
            return false;

        bodyPanel.Children.Add(CreateHeading(line[4..], 14));
        return true;
    }

    private bool TryAddBullet(StackPanel bodyPanel, string line)
    {
        if (line.Length < 2)
            return false;

        if (!(line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)))
            return false;

        bodyPanel.Children.Add(CreateBullet(line[2..]));
        return true;
    }

    private bool TryAddNumbered(StackPanel bodyPanel, string line)
    {
        var dotIndex = line.IndexOf(')');
        if (dotIndex <= 0 || dotIndex > 4)
            return false;

        if (!char.IsDigit(line[0]))
            return false;

        if (dotIndex + 1 < line.Length && line[dotIndex + 1] == ' ')
        {
            bodyPanel.Children.Add(CreateBullet(line));
            return true;
        }

        return false;
    }

    private Control CreateHeading(string text, double size)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 2)
        };
        RegisterSearchEntry(textBlock, text);
        return textBlock;
    }

    private Control CreateParagraph(string text)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap
        };
        RegisterSearchEntry(textBlock, text);
        return textBlock;
    }

    private Control CreateBullet(string text)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*")
        };

        grid.Children.Add(new TextBlock
        {
            Text = "•",
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Top
        });

        var textBlock = new TextBlock
        {
            Text = text,
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(6, 0, 0, 0)
        };
        grid.Children.Add(textBlock);
        RegisterSearchEntry(textBlock, text);

        Grid.SetColumn(grid.Children[1], 1);
        return grid;
    }

    private void RegisterSearchEntry(TextBlock textBlock, string text)
        => _searchEntries.Add(new HelpTextEntry(textBlock, text));

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_boundViewModel is not null)
        {
            _boundViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _boundViewModel = null;
        }

        DetachThemeHandler();
        EndSearchMarkerInteraction();
        ForceSearchClosed(clearQuery: true);
    }

    private StackPanel? GetBodyPanel() => this.FindControl<StackPanel>("BodyPanel");

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        ForceSearchClosed(clearQuery: true);
        CloseRequested?.Invoke(this, e);
    }

    private void OnSearchOpen(object? sender, RoutedEventArgs e)
    {
        if (_isSearchOpen)
            return;

        _isSearchOpen = true;
        var version = ++_searchAnimationVersion;
        _searchPanel.IsHitTestVisible = true;
        _searchPanel.Width = SearchPanelWidth;
        _searchPanel.Margin = new Thickness(0, 0, 4, 0);
        _searchPanel.Opacity = 1;
        GetSearchContentTransform().X = 0;
        _searchButton.IsHitTestVisible = false;
        _searchButton.Width = 0;
        _searchButton.Margin = new Thickness(0);
        _searchButton.Opacity = 0;
        RefreshSearch(navigateToFirstMatch: true);
        _ = FocusSearchAfterOpenAsync(version);
        e.Handled = true;
    }

    private void OnSearchClose(object? sender, RoutedEventArgs e)
    {
        CloseSearch();
        e.Handled = true;
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isSearchOpen)
            ScheduleSearch();
    }

    private void OnSearchPrevious(object? sender, RoutedEventArgs e)
    {
        NavigateSearch(-1);
        e.Handled = true;
    }

    private void OnSearchNext(object? sender, RoutedEventArgs e)
    {
        NavigateSearch(1);
        e.Handled = true;
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseSearch();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter)
            return;

        NavigateSearch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
        e.Handled = true;
    }

    private void OnHelpKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isSearchOpen)
            return;

        if (e.Key == Key.Escape)
        {
            CloseSearch();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.F3)
            return;

        NavigateSearch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
        e.Handled = true;
    }

    private void RefreshSearch(bool navigateToFirstMatch)
    {
        _searchMatches.Clear();
        var query = GetEligibleSearchQuery();
        if (query is not null)
        {
            foreach (var entry in _searchEntries)
            {
                var startIndex = 0;
                while (startIndex <= entry.Text.Length - query.Length)
                {
                    var matchIndex = entry.Text.IndexOf(
                        query,
                        startIndex,
                        StringComparison.OrdinalIgnoreCase);
                    if (matchIndex < 0)
                        break;

                    _searchMatches.Add(new HelpSearchMatch(entry, matchIndex, query.Length));
                    startIndex = matchIndex + query.Length;
                }
            }
        }

        if (_searchMatches.Count == 0)
            _currentSearchMatchIndex = -1;
        else if (navigateToFirstMatch || _currentSearchMatchIndex < 0)
            _currentSearchMatchIndex = 0;
        else
            _currentSearchMatchIndex = Math.Min(_currentSearchMatchIndex, _searchMatches.Count - 1);

        ApplySearchHighlights();
        UpdateSearchControls();
        UpdateSearchMarkers();
        if (_currentSearchMatchIndex >= 0)
            ScrollCurrentMatchIntoView();
    }

    private void NavigateSearch(int step)
    {
        if (!_isSearchOpen || _searchMatches.Count == 0)
            return;

        _currentSearchMatchIndex =
            (_currentSearchMatchIndex + step + _searchMatches.Count) % _searchMatches.Count;
        ApplySearchHighlights();
        UpdateSearchControls();
        ScrollCurrentMatchIntoView();
    }

    private void ApplySearchHighlights()
    {
        var highlightBrush = ResolveBrush("TreeSearchHighlightBrush", "#FFEB3B");
        var currentBrush = ResolveBrush("TreeSearchCurrentBrush", "#F9A825");
        var highlightTextBrush = ResolveBrush("TreeSearchHighlightTextBrush", "#000000");
        var normalTextBrush = ResolveBrush("AppTextBrush", "#1A1A1A");
        var globalMatchIndex = 0;

        foreach (var entry in _searchEntries)
        {
            var firstEntryMatchIndex = globalMatchIndex;
            while (globalMatchIndex < _searchMatches.Count &&
                   ReferenceEquals(_searchMatches[globalMatchIndex].Entry, entry))
            {
                globalMatchIndex++;
            }

            if (firstEntryMatchIndex == globalMatchIndex)
            {
                RestorePlainText(entry);
                continue;
            }

            var inlines = new InlineCollection();
            var textIndex = 0;
            for (var matchIndex = firstEntryMatchIndex; matchIndex < globalMatchIndex; matchIndex++)
            {
                var match = _searchMatches[matchIndex];
                if (match.Start > textIndex)
                {
                    inlines.Add(new Run(entry.Text[textIndex..match.Start])
                    {
                        Foreground = normalTextBrush
                    });
                }

                inlines.Add(new Run(entry.Text.Substring(match.Start, match.Length))
                {
                    Background = matchIndex == _currentSearchMatchIndex ? currentBrush : highlightBrush,
                    Foreground = highlightTextBrush
                });
                textIndex = match.Start + match.Length;
            }

            if (textIndex < entry.Text.Length)
            {
                inlines.Add(new Run(entry.Text[textIndex..])
                {
                    Foreground = normalTextBrush
                });
            }

            entry.Control.Text = null;
            entry.Control.Inlines = inlines;
        }
    }

    private void ClearSearchResults()
    {
        _searchMatches.Clear();
        _currentSearchMatchIndex = -1;
        foreach (var entry in _searchEntries)
            RestorePlainText(entry);

        UpdateSearchControls();
        SetSearchMarkerSnapshot(PreviewMarkerSnapshot.Empty);
    }

    private static void RestorePlainText(HelpTextEntry entry)
    {
        entry.Control.Inlines = null;
        entry.Control.Text = entry.Text;
    }

    private void UpdateSearchControls()
    {
        var hasQuery = GetEligibleSearchQuery() is not null;
        var current = CurrentSearchMatchIndex.ToString("N0", CultureInfo.CurrentCulture);
        var total = SearchMatchCount.ToString("N0", CultureInfo.CurrentCulture);
        _searchMatchSummary.Text = $"({current} / {total})";
        _searchMatchSummary.IsVisible = hasQuery;
    }

    private void ScrollCurrentMatchIntoView(bool centerInViewport = false)
    {
        if (_currentSearchMatchIndex < 0 || _currentSearchMatchIndex >= _searchMatches.Count)
            return;

        var control = _searchMatches[_currentSearchMatchIndex].Entry.Control;
        if (centerInViewport && GetBodyPanel() is { } bodyPanel &&
            control.TranslatePoint(default, bodyPanel) is { } origin)
        {
            var maximumOffset = Math.Max(
                0,
                _bodyScrollViewer.Extent.Height - _bodyScrollViewer.Viewport.Height);
            var targetOffset = Math.Clamp(
                origin.Y + (control.Bounds.Height / 2) -
                (_bodyScrollViewer.Viewport.Height / 2),
                0,
                maximumOffset);
            _bodyScrollViewer.Offset = new Vector(
                _bodyScrollViewer.Offset.X,
                targetOffset);
            return;
        }

        Dispatcher.UIThread.Post(control.BringIntoView, DispatcherPriority.Background);
    }

    private void CloseSearch()
    {
        if (!_isSearchOpen)
            return;

        _isSearchOpen = false;
        ++_searchAnimationVersion;
        CancelPendingSearch();
        ClearSearchResults();
        _searchPanel.IsHitTestVisible = false;
        _searchPanel.Width = 0;
        _searchPanel.Margin = new Thickness(0);
        _searchPanel.Opacity = 0;
        GetSearchContentTransform().X = SearchContentOffset;
        _searchButton.IsHitTestVisible = true;
        _searchButton.Width = SearchButtonWidth;
        _searchButton.Margin = new Thickness(0, 0, 4, 0);
        _searchButton.Opacity = 1;
    }

    private void ForceSearchClosed(bool clearQuery)
    {
        _isSearchOpen = false;
        ++_searchAnimationVersion;
        CancelPendingSearch();
        if (clearQuery)
            _searchBox.Text = string.Empty;

        ClearSearchResults();
        _searchPanel.IsHitTestVisible = false;
        _searchPanel.Width = 0;
        _searchPanel.Margin = new Thickness(0);
        _searchPanel.Opacity = 0;
        GetSearchContentTransform().X = SearchContentOffset;
        _searchButton.IsHitTestVisible = true;
        _searchButton.Width = SearchButtonWidth;
        _searchButton.Margin = new Thickness(0, 0, 4, 0);
        _searchButton.Opacity = 1;
    }

    private void ScheduleSearch()
    {
        CancelPendingSearch();
        ClearSearchResults();
        if (GetEligibleSearchQuery() is null)
            return;

        _searchDebounceTimer ??= CreateSearchDebounceTimer();
        _searchDebounceTimer.Start();
    }

    private DispatcherTimer CreateSearchDebounceTimer()
    {
        var timer = new DispatcherTimer
        {
            Interval = SearchDebounceInterval
        };
        timer.Tick += OnSearchDebounceElapsed;
        return timer;
    }

    private void OnSearchDebounceElapsed(object? sender, EventArgs e)
    {
        CancelPendingSearch();
        if (_isSearchOpen)
            RefreshSearch(navigateToFirstMatch: true);
    }

    private void CancelPendingSearch() => _searchDebounceTimer?.Stop();

    private string? GetEligibleSearchQuery()
    {
        var query = _searchBox.Text?.Trim();
        return query is { Length: >= MinimumSearchQueryLength }
            ? query
            : null;
    }

    private async Task FocusSearchAfterOpenAsync(long version)
    {
        await Task.Delay(SearchPanelAnimationDuration + UiTimingProfile.AnimationSettleBuffer);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!_isSearchOpen || version != _searchAnimationVersion)
                return;

            if (_searchBox.Focus())
                _searchBox.SelectAll();
        });
    }

    private void ConfigureSearchTransitions()
    {
        _searchPanel.Transitions =
        [
            new DoubleTransition
            {
                Property = Layoutable.WidthProperty,
                Duration = SearchPanelAnimationDuration,
                Easing = new CubicEaseInOut()
            },
            new ThicknessTransition
            {
                Property = Layoutable.MarginProperty,
                Duration = SearchPanelAnimationDuration,
                Easing = new CubicEaseInOut()
            },
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = SearchFadeDuration,
                Easing = new CubicEaseOut()
            }
        ];
        GetSearchContentTransform().Transitions =
        [
            new DoubleTransition
            {
                Property = TranslateTransform.XProperty,
                Duration = SearchContentAnimationDuration,
                Easing = new CubicEaseOut()
            }
        ];
        _searchButton.Transitions =
        [
            new DoubleTransition
            {
                Property = Layoutable.WidthProperty,
                Duration = SearchPanelAnimationDuration,
                Easing = new CubicEaseInOut()
            },
            new ThicknessTransition
            {
                Property = Layoutable.MarginProperty,
                Duration = SearchPanelAnimationDuration,
                Easing = new CubicEaseInOut()
            },
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = SearchFadeDuration,
                Easing = new CubicEaseOut()
            }
        ];
    }

    private TranslateTransform GetSearchContentTransform()
        => _searchContent.RenderTransform as TranslateTransform ??
           throw new InvalidOperationException("Help search content transform was not found.");

    private void ResolveSearchControls()
    {
        _searchPanel = GetRequiredControl<Border>("HelpSearchPanel");
        _searchContent = GetRequiredControl<Grid>("HelpSearchContent");
        _searchBox = GetRequiredControl<TextBox>("HelpSearchBox");
        _searchMatchSummary = GetRequiredControl<TextBlock>("HelpSearchMatchSummary");
        _searchButton = GetRequiredControl<Button>("HelpSearchButton");
        _bodySurface = GetRequiredControl<Grid>("HelpBodySurface");
        _bodyScrollViewer = GetRequiredControl<ScrollViewer>("HelpBodyScrollViewer");
        _searchMarkerBar = GetRequiredControl<PreviewMarkerBar>("HelpSearchMarkerBar");
    }

    private void OnBodyScrollViewerLayoutUpdated(object? sender, EventArgs e)
    {
        UpdateSearchMarkers();
        UpdateSearchMarkerTrackGeometry();
    }

    private void UpdateSearchMarkers()
    {
        _searchMarkerMatchIndices.Clear();
        if (_searchMatches.Count == 0 || GetBodyPanel() is not { } bodyPanel)
        {
            SetSearchMarkerSnapshot(PreviewMarkerSnapshot.Empty);
            return;
        }

        var extentHeight = _bodyScrollViewer.Extent.Height;
        if (!double.IsFinite(extentHeight) || extentHeight <= 0)
        {
            SetSearchMarkerSnapshot(PreviewMarkerSnapshot.Empty);
            return;
        }

        var totalLineCount = (int)Math.Clamp(
            Math.Ceiling(extentHeight),
            1,
            int.MaxValue);
        var lineHeight = extentHeight / totalLineCount;
        var markers = new List<PreviewMarkerSource>(_searchMatches.Count);
        HelpTextEntry? previousEntry = null;
        for (var matchIndex = 0; matchIndex < _searchMatches.Count; matchIndex++)
        {
            var match = _searchMatches[matchIndex];
            if (ReferenceEquals(previousEntry, match.Entry))
                continue;

            previousEntry = match.Entry;
            if (match.Entry.Control.TranslatePoint(default, bodyPanel) is not { } origin)
                continue;

            var center = Math.Clamp(
                origin.Y + (match.Entry.Control.Bounds.Height / 2),
                0,
                Math.Max(0, extentHeight - double.Epsilon));
            var lineNumber = Math.Clamp(
                (int)Math.Floor(center / lineHeight) + 1,
                1,
                totalLineCount);
            if (_searchMarkerMatchIndices.TryAdd(lineNumber, matchIndex))
                markers.Add(new PreviewMarkerSource(lineNumber, PreviewMarkerCategory.Search));
        }

        SetSearchMarkerSnapshot(markers.Count == 0
            ? PreviewMarkerSnapshot.Empty
            : new PreviewMarkerSnapshot(totalLineCount, [.. markers]));
    }

    private void SetSearchMarkerSnapshot(PreviewMarkerSnapshot snapshot)
    {
        if (_searchMarkerSnapshot.TotalLineCount == snapshot.TotalLineCount &&
            _searchMarkerSnapshot.Markers.AsSpan().SequenceEqual(snapshot.Markers))
        {
            return;
        }

        _searchMarkerSnapshot = snapshot;
        _searchMarkerBar.SetMarkers(snapshot);
        if (snapshot.Markers.Length == 0)
            EndSearchMarkerInteraction();
    }

    private void UpdateSearchMarkerTrackGeometry()
    {
        _bodyVerticalScrollBar ??= _bodyScrollViewer
            .GetVisualDescendants()
            .OfType<ScrollBar>()
            .FirstOrDefault(static scrollBar => scrollBar.Orientation == Orientation.Vertical);

        var hasVerticalScrollBar = _bodyVerticalScrollBar is { IsVisible: true };
        _searchMarkerBar.IsMarkerDisplayEnabled = hasVerticalScrollBar;

        var margin = default(Thickness);
        if (_bodyVerticalScrollBar is { IsVisible: true } scrollBar &&
            scrollBar.GetVisualDescendants().OfType<Track>().FirstOrDefault() is { } track &&
            track.GetVisualDescendants().OfType<Thumb>().FirstOrDefault() is { } thumb &&
            track.TranslatePoint(default, _bodyScrollViewer) is { } origin)
        {
            var availableHeight = Math.Max(0, _bodyScrollViewer.Bounds.Height);
            var top = Math.Clamp(origin.Y, 0, availableHeight);
            var bottom = Math.Clamp(
                availableHeight - top - track.Bounds.Height,
                0,
                availableHeight);
            margin = new Thickness(0, top, 0, bottom);

            var totalLineCount = Math.Max(1, _searchMarkerSnapshot.TotalLineCount);
            var extentHeight = _bodyScrollViewer.Extent.Height;
            var lineHeight = double.IsFinite(extentHeight) && extentHeight > 0
                ? extentHeight / totalLineCount
                : 1;
            _searchMarkerBar.SetScrollMetrics(new PreviewMarkerScrollMetrics(
                extentHeight,
                _bodyScrollViewer.Viewport.Height,
                thumb.Bounds.Height,
                FirstLineTop: 0,
                lineHeight));
        }
        else
        {
            _searchMarkerBar.SetScrollMetrics(null);
        }

        if (_searchMarkerBar.Margin != margin)
            _searchMarkerBar.Margin = margin;
    }

    private void OnSearchMarkerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_bodyScrollViewer).Properties.IsLeftButtonPressed ||
            _searchMarkerBar.FindTargetAt(e.GetPosition(_searchMarkerBar)) is not { } target ||
            !TryNavigateToSearchMarker(target))
        {
            return;
        }

        _searchMarkerDragPointer = e.Pointer;
        _searchMarkerDragStartY = e.GetPosition(_searchMarkerBar).Y;
        _searchMarkerDragStartOffsetY = _bodyScrollViewer.Offset.Y;
        _searchMarkerDragging = false;
        e.Pointer.Capture(_bodyScrollViewer);
        BeginSearchMarkerScrollBarInteraction();
        SetSearchMarkerCursor(_bodyScrollViewer, isDragging: true);
        e.Handled = true;
    }

    private void OnSearchMarkerPointerMoved(object? sender, PointerEventArgs e)
    {
        UpdateSearchMarkerRailPointerState(IsPointerWithinSearchScrollBar(e));
        if (ReferenceEquals(_searchMarkerDragPointer, e.Pointer) &&
            e.GetCurrentPoint(_bodyScrollViewer).Properties.IsLeftButtonPressed)
        {
            var deltaY = e.GetPosition(_searchMarkerBar).Y - _searchMarkerDragStartY;
            _searchMarkerDragging |= Math.Abs(deltaY) >= 2;
            if (_searchMarkerDragging)
            {
                KeepSearchMarkerScrollBarActive();
                SetSearchMarkerCursor(_bodyScrollViewer, isDragging: true);
                ScrollFromSearchMarkerDrag(deltaY);
            }

            e.Handled = true;
            return;
        }

        var markerTarget = _searchMarkerBar.FindTargetAt(e.GetPosition(_searchMarkerBar));
        SetSearchMarkerCursor(
            markerTarget is not null || _searchMarkerRailPointerOver
                ? e.Source as InputElement
                : null,
            isDragging: markerTarget is null && _searchMarkerRailPointerOver);
    }

    private void OnSearchMarkerPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(_searchMarkerDragPointer, e.Pointer))
            return;

        UpdateSearchMarkerRailPointerState(IsPointerWithinSearchScrollBar(e));
        EndSearchMarkerDrag(releaseCapture: true);
        var markerTarget = _searchMarkerBar.FindTargetAt(e.GetPosition(_searchMarkerBar));
        SetSearchMarkerCursor(
            markerTarget is not null || _searchMarkerRailPointerOver
                ? e.Source as InputElement
                : null,
            isDragging: markerTarget is null && _searchMarkerRailPointerOver);
        e.Handled = true;
    }

    private void OnSearchMarkerPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!ReferenceEquals(_searchMarkerDragPointer, e.Pointer))
            return;

        EndSearchMarkerDrag(releaseCapture: false);
        SetSearchMarkerCursor(null);
    }

    private bool TryNavigateToSearchMarker(PreviewMarkerTarget target)
    {
        var matchNumber = ResolveSearchMarkerMatchIndex(target);
        if (matchNumber <= 0 || matchNumber > _searchMatches.Count)
            return false;

        _currentSearchMatchIndex = matchNumber - 1;
        ApplySearchHighlights();
        UpdateSearchControls();
        ScrollCurrentMatchIntoView(centerInViewport: true);
        return true;
    }

    private void ScrollFromSearchMarkerDrag(double deltaY)
    {
        if (_bodyVerticalScrollBar?.GetVisualDescendants().OfType<Track>().FirstOrDefault() is not { } track ||
            track.GetVisualDescendants().OfType<Thumb>().FirstOrDefault() is not { } thumb)
        {
            return;
        }

        var maximumOffset = Math.Max(
            0,
            _bodyScrollViewer.Extent.Height - _bodyScrollViewer.Viewport.Height);
        var thumbTravel = Math.Max(1, track.Bounds.Height - thumb.Bounds.Height);
        var targetY = Math.Clamp(
            _searchMarkerDragStartOffsetY + ((deltaY / thumbTravel) * maximumOffset),
            0,
            maximumOffset);
        _bodyScrollViewer.Offset = new Vector(_bodyScrollViewer.Offset.X, targetY);
    }

    private void EndSearchMarkerDrag(bool releaseCapture)
    {
        var pointer = _searchMarkerDragPointer;
        _searchMarkerDragPointer = null;
        _searchMarkerDragging = false;
        if (releaseCapture && ReferenceEquals(pointer?.Captured, _bodyScrollViewer))
            pointer.Capture(null);

        if (!_searchMarkerRailPointerOver)
            EndSearchMarkerScrollBarInteraction();
    }

    private bool IsPointerWithinSearchScrollBar(PointerEventArgs e)
        => _searchMarkerBar.IsVisible &&
           _bodyVerticalScrollBar is { IsVisible: true, Bounds.Width: > 0 } scrollBar &&
           new Rect(scrollBar.Bounds.Size).Contains(e.GetPosition(scrollBar));

    private void UpdateSearchMarkerRailPointerState(bool pointerOver)
    {
        if (_searchMarkerRailPointerOver == pointerOver)
            return;

        _searchMarkerRailPointerOver = pointerOver;
        if (pointerOver)
            BeginSearchMarkerScrollBarInteraction();
        else if (_searchMarkerDragPointer is null)
            EndSearchMarkerScrollBarInteraction();
    }

    private void BeginSearchMarkerScrollBarInteraction()
    {
        if (_searchMarkerScrollBarInteractionActive)
        {
            KeepSearchMarkerScrollBarActive();
            return;
        }

        _searchMarkerScrollBarAllowAutoHide = _bodyVerticalScrollBar?.AllowAutoHide ?? true;
        _searchMarkerScrollBarInteractionActive = true;
        KeepSearchMarkerScrollBarActive();
    }

    private void KeepSearchMarkerScrollBarActive()
        => _bodyVerticalScrollBar?.SetCurrentValue(ScrollBar.AllowAutoHideProperty, false);

    private void EndSearchMarkerScrollBarInteraction()
    {
        if (!_searchMarkerScrollBarInteractionActive)
            return;

        _searchMarkerScrollBarInteractionActive = false;
        _bodyVerticalScrollBar?.SetCurrentValue(
            ScrollBar.AllowAutoHideProperty,
            _searchMarkerScrollBarAllowAutoHide);
    }

    private void OnSearchMarkerPointerExited(object? sender, PointerEventArgs e)
    {
        UpdateSearchMarkerRailPointerState(pointerOver: false);
        SetSearchMarkerCursor(null);
    }

    private void EndSearchMarkerInteraction()
    {
        EndSearchMarkerDrag(releaseCapture: true);
        _searchMarkerRailPointerOver = false;
        EndSearchMarkerScrollBarInteraction();
        SetSearchMarkerCursor(null);
    }

    private void SetSearchMarkerCursor(InputElement? target, bool isDragging = false)
    {
        var cursor = isDragging
            ? _searchMarkerDragCursor ??= new Cursor(StandardCursorType.Arrow)
            : _searchMarkerHandCursor ??= new Cursor(StandardCursorType.Hand);
        if (ReferenceEquals(_searchMarkerCursorTarget, target) &&
            ReferenceEquals(target?.Cursor, cursor))
        {
            return;
        }

        _searchMarkerCursorTarget?.ClearValue(InputElement.CursorProperty);
        _searchMarkerCursorTarget = target;
        if (target is not null)
            target.Cursor = cursor;
    }

    private T GetRequiredControl<T>(string name)
        where T : Control
        => this.FindControl<T>(name) ??
           throw new InvalidOperationException($"Help control '{name}' was not found.");

    private void AttachThemeHandler()
    {
        if (_themeHandlerAttached || global::Avalonia.Application.Current is not { } application)
            return;

        application.ActualThemeVariantChanged += OnActualThemeVariantChanged;
        _themeHandlerAttached = true;
    }

    private void DetachThemeHandler()
    {
        if (!_themeHandlerAttached || global::Avalonia.Application.Current is not { } application)
            return;

        application.ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        _themeHandlerAttached = false;
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        if (_isSearchOpen && _searchMatches.Count > 0)
            ApplySearchHighlights();
    }

    private static IBrush ResolveBrush(string resourceKey, string fallbackColor)
    {
        var application = global::Avalonia.Application.Current;
        var theme = application?.ActualThemeVariant ?? ThemeVariant.Light;
        return application?.TryFindResource(resourceKey, theme, out var resource) == true &&
               resource is IBrush brush
            ? brush
            : new SolidColorBrush(Color.Parse(fallbackColor));
    }

    private async void OnCopyAll(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_boundViewModel?.HelpHelpBody))
            return;

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
                return;

            await clipboard.SetTextAsync(HelpContentProvider.ToPlainText(_boundViewModel.HelpHelpBody));
        }
        catch (Exception ex)
        {
            // Copy support must never break the help popup on platforms with limited clipboard providers.
            Debug.WriteLine($"Failed to copy help text: {ex}");
        }
    }

    private sealed record HelpTextEntry(TextBlock Control, string Text);

    private sealed record HelpSearchMatch(HelpTextEntry Entry, int Start, int Length);
}
