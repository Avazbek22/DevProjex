namespace DevProjex.Avalonia.Views;

public partial class TopMenuBarView : UserControl
{
    private TopLevel? _helpPopupTopLevel;
    private bool _helpPopupHandlersAttached;
    private bool _helpPopupBoundsHandlerAttached;
    private TopLevel? _helpDocsPopupTopLevel;
    private bool _helpDocsPopupHandlersAttached;
    private bool _helpDocsPopupBoundsHandlerAttached;
    private bool _ownedControlHandlersAttached;

    public event EventHandler<RoutedEventArgs>? OpenFolderRequested;
    public event EventHandler<RoutedEventArgs>? OpenNewWindowRequested;
    public event EventHandler<RoutedEventArgs>? RefreshRequested;
    public event EventHandler<RoutedEventArgs>? ExportTreeToFileRequested;
    public event EventHandler<RoutedEventArgs>? ExportContentToFileRequested;
    public event EventHandler<RoutedEventArgs>? ExportTreeAndContentToFileRequested;
    public event EventHandler<RoutedEventArgs>? ExitRequested;
    public event EventHandler<RoutedEventArgs>? CopyTreeRequested;
    public event EventHandler<RoutedEventArgs>? CopyContentRequested;
    public event EventHandler<RoutedEventArgs>? CopyTreeAndContentRequested;
    public event EventHandler<RoutedEventArgs>? ExpandAllRequested;
    public event EventHandler<RoutedEventArgs>? CollapseAllRequested;
    public event EventHandler<RoutedEventArgs>? ZoomInRequested;
    public event EventHandler<RoutedEventArgs>? ZoomOutRequested;
    public event EventHandler<RoutedEventArgs>? ZoomResetRequested;
    public event EventHandler<RoutedEventArgs>? ToggleCompactModeRequested;
    public event EventHandler<RoutedEventArgs>? ToggleTreeAnimationRequested;
    public event EventHandler<RoutedEventArgs>? ToggleAdvancedCountsRequested;
    public event EventHandler<RoutedEventArgs>? ToggleSearchRequested;
    public event EventHandler<RoutedEventArgs>? ToggleSettingsRequested;
    public event EventHandler<RoutedEventArgs>? TogglePreviewRequested;
    public event EventHandler<RoutedEventArgs>? ToggleFilterRequested;
    public event EventHandler<RoutedEventArgs>? ThemeMenuClickRequested;
    public event EventHandler<RoutedEventArgs>? LanguageRuRequested;
    public event EventHandler<RoutedEventArgs>? LanguageEnRequested;
    public event EventHandler<RoutedEventArgs>? LanguageUzRequested;
    public event EventHandler<RoutedEventArgs>? LanguageTgRequested;
    public event EventHandler<RoutedEventArgs>? LanguageKkRequested;
    public event EventHandler<RoutedEventArgs>? LanguageFrRequested;
    public event EventHandler<RoutedEventArgs>? LanguageDeRequested;
    public event EventHandler<RoutedEventArgs>? LanguageItRequested;
    public event EventHandler<RoutedEventArgs>? HelpRequested;
    public event EventHandler<RoutedEventArgs>? HelpCloseRequested;
    public event EventHandler<RoutedEventArgs>? AboutRequested;
    public event EventHandler<RoutedEventArgs>? AboutCloseRequested;
    public event EventHandler<RoutedEventArgs>? AboutOpenLinkRequested;
    public event EventHandler<RoutedEventArgs>? AboutCopyLinkRequested;
    public event EventHandler<RoutedEventArgs>? ResetSettingsRequested;
    public event EventHandler<RoutedEventArgs>? ResetDataRequested;
    public event EventHandler<RoutedEventArgs>? SetLightThemeRequested;
    public event EventHandler<RoutedEventArgs>? SetDarkThemeRequested;
    public event EventHandler<RoutedEventArgs>? SetTransparentModeRequested;
    public event EventHandler<RoutedEventArgs>? SetMicaModeRequested;
    public event EventHandler<RoutedEventArgs>? SetAcrylicModeRequested;

    // Git events
    public event EventHandler<RoutedEventArgs>? GitCloneRequested;
    public event EventHandler<RoutedEventArgs>? GitGetUpdatesRequested;
    public event EventHandler<string>? GitBranchSwitchRequested;

    public TopMenuBarView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    public Menu? MainMenuControl => MainMenu;
    public MenuItem? RecentMenuItemControl => RecentMenuItem;

    private void OnOpenFolder(object? sender, RoutedEventArgs e) => OpenFolderRequested?.Invoke(sender, e);

    private void OnOpenNewWindow(object? sender, RoutedEventArgs e) => OpenNewWindowRequested?.Invoke(sender, e);

    private void OnRefresh(object? sender, RoutedEventArgs e) => RefreshRequested?.Invoke(sender, e);

    private void OnExportTreeToFile(object? sender, RoutedEventArgs e) => ExportTreeToFileRequested?.Invoke(sender, e);

    private void OnExportContentToFile(object? sender, RoutedEventArgs e) => ExportContentToFileRequested?.Invoke(sender, e);

    private void OnExportTreeAndContentToFile(object? sender, RoutedEventArgs e)
        => ExportTreeAndContentToFileRequested?.Invoke(sender, e);

    private void OnExit(object? sender, RoutedEventArgs e) => ExitRequested?.Invoke(sender, e);

    private void OnCopyTree(object? sender, RoutedEventArgs e) => CopyTreeRequested?.Invoke(sender, e);

    private void OnCopyContent(object? sender, RoutedEventArgs e) => CopyContentRequested?.Invoke(sender, e);

    private void OnCopyTreeAndContent(object? sender, RoutedEventArgs e)
        => CopyTreeAndContentRequested?.Invoke(sender, e);

    private void OnExpandAll(object? sender, RoutedEventArgs e) => ExpandAllRequested?.Invoke(sender, e);

    private void OnCollapseAll(object? sender, RoutedEventArgs e) => CollapseAllRequested?.Invoke(sender, e);

    private void OnZoomIn(object? sender, RoutedEventArgs e) => ZoomInRequested?.Invoke(sender, e);

    private void OnZoomOut(object? sender, RoutedEventArgs e) => ZoomOutRequested?.Invoke(sender, e);

    private void OnZoomReset(object? sender, RoutedEventArgs e) => ZoomResetRequested?.Invoke(sender, e);

    private void OnToggleCompactMode(object? sender, RoutedEventArgs e)
        => ToggleCompactModeRequested?.Invoke(sender, e);

    private void OnToggleTreeAnimation(object? sender, RoutedEventArgs e)
        => ToggleTreeAnimationRequested?.Invoke(sender, e);

    private void OnToggleAdvancedCounts(object? sender, RoutedEventArgs e)
        => ToggleAdvancedCountsRequested?.Invoke(sender, e);

    private void OnToggleSettings(object? sender, RoutedEventArgs e) => ToggleSettingsRequested?.Invoke(sender, e);

    private void OnTogglePreview(object? sender, RoutedEventArgs e) => TogglePreviewRequested?.Invoke(sender, e);

    private void OnToggleSearch(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel { IsSearchFilterAvailable: false })
            return;

        ToggleSearchRequested?.Invoke(sender, e);
    }

    private void OnToggleFilter(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel { IsSearchFilterAvailable: false })
            return;

        ToggleFilterRequested?.Invoke(sender, e);
    }

    private void OnAsciiFormatClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.SelectedExportFormat = ExportFormat.Ascii;
    }

    private void OnJsonFormatClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.SelectedExportFormat = ExportFormat.Json;
    }

    private void OnThemeMenuClick(object? sender, RoutedEventArgs e)
        => ThemeMenuClickRequested?.Invoke(sender, e);

    private void OnLangRu(object? sender, RoutedEventArgs e) => LanguageRuRequested?.Invoke(sender, e);

    private void OnLangEn(object? sender, RoutedEventArgs e) => LanguageEnRequested?.Invoke(sender, e);

    private void OnLangUz(object? sender, RoutedEventArgs e) => LanguageUzRequested?.Invoke(sender, e);

    private void OnLangTg(object? sender, RoutedEventArgs e) => LanguageTgRequested?.Invoke(sender, e);

    private void OnLangKk(object? sender, RoutedEventArgs e) => LanguageKkRequested?.Invoke(sender, e);

    private void OnLangFr(object? sender, RoutedEventArgs e) => LanguageFrRequested?.Invoke(sender, e);

    private void OnLangDe(object? sender, RoutedEventArgs e) => LanguageDeRequested?.Invoke(sender, e);

    private void OnLangIt(object? sender, RoutedEventArgs e) => LanguageItRequested?.Invoke(sender, e);

    private void OnHelp(object? sender, RoutedEventArgs e) => HelpRequested?.Invoke(sender, e);

    private void OnAbout(object? sender, RoutedEventArgs e) => AboutRequested?.Invoke(sender, e);

    private void OnResetSettings(object? sender, RoutedEventArgs e) => ResetSettingsRequested?.Invoke(sender, e);

    private void OnResetData(object? sender, RoutedEventArgs e) => ResetDataRequested?.Invoke(sender, e);

    private void OnGitClone(object? sender, RoutedEventArgs e) => GitCloneRequested?.Invoke(sender, e);

    private void OnGitGetUpdates(object? sender, RoutedEventArgs e) => GitGetUpdatesRequested?.Invoke(sender, e);

    public void OnGitBranchSwitch(string branchName) => GitBranchSwitchRequested?.Invoke(this, branchName);

    public MenuItem? GitBranchMenuItemControl => GitBranchMenuItem;

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        AttachOwnedControlHandlers();
    }

    private void AttachOwnedControlHandlers()
    {
        if (_ownedControlHandlersAttached)
            return;

        if (ThemePopover is not null)
        {
            ThemePopover.SetLightThemeRequested += OnThemePopoverSetLightThemeRequested;
            ThemePopover.SetDarkThemeRequested += OnThemePopoverSetDarkThemeRequested;
            ThemePopover.SetTransparentModeRequested += OnThemePopoverSetTransparentModeRequested;
            ThemePopover.SetMicaModeRequested += OnThemePopoverSetMicaModeRequested;
            ThemePopover.SetAcrylicModeRequested += OnThemePopoverSetAcrylicModeRequested;
        }

        if (HelpPopover is not null)
        {
            HelpPopover.CloseRequested += OnHelpPopoverCloseRequested;
            HelpPopover.OpenLinkRequested += OnHelpPopoverOpenLinkRequested;
            HelpPopover.CopyLinkRequested += OnHelpPopoverCopyLinkRequested;
        }

        if (ThemePopup is not null)
            ThemePopup.Opened += OnThemePopupOpened;

        if (HelpPopup is not null)
        {
            HelpPopup.Opened += OnHelpPopupOpened;
            HelpPopup.Closed += OnHelpPopupClosed;
        }

        if (HelpDocsPopover is not null)
            HelpDocsPopover.CloseRequested += OnHelpDocsPopoverCloseRequested;

        if (HelpDocsPopup is not null)
        {
            HelpDocsPopup.Opened += OnHelpDocsPopupOpened;
            HelpDocsPopup.Closed += OnHelpDocsPopupClosed;
        }

        _ownedControlHandlersAttached = true;
    }

    private void DetachOwnedControlHandlers()
    {
        if (!_ownedControlHandlersAttached)
            return;

        if (ThemePopover is not null)
        {
            ThemePopover.SetLightThemeRequested -= OnThemePopoverSetLightThemeRequested;
            ThemePopover.SetDarkThemeRequested -= OnThemePopoverSetDarkThemeRequested;
            ThemePopover.SetTransparentModeRequested -= OnThemePopoverSetTransparentModeRequested;
            ThemePopover.SetMicaModeRequested -= OnThemePopoverSetMicaModeRequested;
            ThemePopover.SetAcrylicModeRequested -= OnThemePopoverSetAcrylicModeRequested;
        }

        if (HelpPopover is not null)
        {
            HelpPopover.CloseRequested -= OnHelpPopoverCloseRequested;
            HelpPopover.OpenLinkRequested -= OnHelpPopoverOpenLinkRequested;
            HelpPopover.CopyLinkRequested -= OnHelpPopoverCopyLinkRequested;
        }

        if (ThemePopup is not null)
            ThemePopup.Opened -= OnThemePopupOpened;

        if (HelpPopup is not null)
        {
            HelpPopup.Opened -= OnHelpPopupOpened;
            HelpPopup.Closed -= OnHelpPopupClosed;
        }

        if (HelpDocsPopover is not null)
            HelpDocsPopover.CloseRequested -= OnHelpDocsPopoverCloseRequested;

        if (HelpDocsPopup is not null)
        {
            HelpDocsPopup.Opened -= OnHelpDocsPopupOpened;
            HelpDocsPopup.Closed -= OnHelpDocsPopupClosed;
        }

        _ownedControlHandlersAttached = false;
    }

    private void OnThemePopoverSetLightThemeRequested(object? sender, RoutedEventArgs e)
        => SetLightThemeRequested?.Invoke(this, e);

    private void OnThemePopoverSetDarkThemeRequested(object? sender, RoutedEventArgs e)
        => SetDarkThemeRequested?.Invoke(this, e);

    private void OnThemePopoverSetTransparentModeRequested(object? sender, RoutedEventArgs e)
        => SetTransparentModeRequested?.Invoke(this, e);

    private void OnThemePopoverSetMicaModeRequested(object? sender, RoutedEventArgs e)
        => SetMicaModeRequested?.Invoke(this, e);

    private void OnThemePopoverSetAcrylicModeRequested(object? sender, RoutedEventArgs e)
        => SetAcrylicModeRequested?.Invoke(this, e);

    private void OnHelpPopoverCloseRequested(object? sender, RoutedEventArgs e)
        => AboutCloseRequested?.Invoke(this, e);

    private void OnHelpPopoverOpenLinkRequested(object? sender, RoutedEventArgs e)
        => AboutOpenLinkRequested?.Invoke(this, e);

    private void OnHelpPopoverCopyLinkRequested(object? sender, RoutedEventArgs e)
        => AboutCopyLinkRequested?.Invoke(this, e);

    private void OnHelpDocsPopoverCloseRequested(object? sender, RoutedEventArgs e)
        => HelpCloseRequested?.Invoke(this, e);

    private void OnThemePopupOpened(object? sender, EventArgs e)
    {
        ThemePopover?.Focus();
        ApplyPopupBackdrop(ThemePopup);
    }

    private void OnHelpPopupOpened(object? sender, EventArgs e)
    {
        HelpPopover?.Focus();
        ApplyPopupBackdrop(HelpPopup);

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        if (_helpPopupHandlersAttached && _helpPopupTopLevel == topLevel)
            return;

        DetachHelpPopupHandlers();
        _helpPopupTopLevel = topLevel;
        topLevel.AddHandler(GotFocusEvent, OnTopLevelGotFocus, RoutingStrategies.Tunnel);
        topLevel.AddHandler(PointerPressedEvent, OnTopLevelPointerPressed, RoutingStrategies.Tunnel);
        _helpPopupHandlersAttached = true;
        topLevel.PropertyChanged += OnHelpPopupTopLevelPropertyChanged;
        _helpPopupBoundsHandlerAttached = true;

        SchedulePopupClamp(HelpPopup, HelpPopover);
    }

    private void OnHelpPopupClosed(object? sender, EventArgs e)
    {
        DetachHelpPopupHandlers();
    }

    private void OnTopLevelGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (HelpPopup?.IsOpen != true)
            return;

        if (!IsInsidePopup(HelpPopup, e.Source as Visual))
            HelpPopup.IsOpen = false;
    }

    private void OnTopLevelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (HelpPopup?.IsOpen != true)
            return;

        if (!IsInsidePopup(HelpPopup, e.Source as Visual))
            HelpPopup.IsOpen = false;
    }

    private bool IsInsidePopup(Popup? popup, Visual? source)
    {
        var popupRoot = popup?.Child as Visual;
        if (popupRoot is null || source is null)
            return false;

        return popupRoot == source || popupRoot.IsVisualAncestorOf(source);
    }

    private void DetachHelpPopupHandlers()
    {
        if (!_helpPopupHandlersAttached || _helpPopupTopLevel is null)
            return;

        _helpPopupTopLevel.RemoveHandler(GotFocusEvent, OnTopLevelGotFocus);
        _helpPopupTopLevel.RemoveHandler(PointerPressedEvent, OnTopLevelPointerPressed);
        if (_helpPopupBoundsHandlerAttached)
        {
            _helpPopupTopLevel.PropertyChanged -= OnHelpPopupTopLevelPropertyChanged;
            _helpPopupBoundsHandlerAttached = false;
        }
        _helpPopupTopLevel = null;
        _helpPopupHandlersAttached = false;
    }

    private void OnHelpDocsPopupOpened(object? sender, EventArgs e)
    {
        HelpDocsPopover?.Focus();
        ApplyPopupBackdrop(HelpDocsPopup);

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        if (_helpDocsPopupHandlersAttached && _helpDocsPopupTopLevel == topLevel)
            return;

        DetachHelpDocsPopupHandlers();
        _helpDocsPopupTopLevel = topLevel;
        topLevel.AddHandler(GotFocusEvent, OnTopLevelHelpDocsGotFocus, RoutingStrategies.Tunnel);
        topLevel.AddHandler(PointerPressedEvent, OnTopLevelHelpDocsPointerPressed, RoutingStrategies.Tunnel);
        _helpDocsPopupHandlersAttached = true;
        topLevel.PropertyChanged += OnHelpDocsPopupTopLevelPropertyChanged;
        _helpDocsPopupBoundsHandlerAttached = true;

        SchedulePopupClamp(HelpDocsPopup, HelpDocsPopover);
    }

    private void OnHelpDocsPopupClosed(object? sender, EventArgs e)
    {
        DetachHelpDocsPopupHandlers();
    }

    private void OnTopLevelHelpDocsGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (HelpDocsPopup?.IsOpen != true)
            return;

        if (!IsInsidePopup(HelpDocsPopup, e.Source as Visual))
            HelpDocsPopup.IsOpen = false;
    }

    private void OnTopLevelHelpDocsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (HelpDocsPopup?.IsOpen != true)
            return;

        if (!IsInsidePopup(HelpDocsPopup, e.Source as Visual))
            HelpDocsPopup.IsOpen = false;
    }

    private void DetachHelpDocsPopupHandlers()
    {
        if (!_helpDocsPopupHandlersAttached || _helpDocsPopupTopLevel is null)
            return;

        _helpDocsPopupTopLevel.RemoveHandler(GotFocusEvent, OnTopLevelHelpDocsGotFocus);
        _helpDocsPopupTopLevel.RemoveHandler(PointerPressedEvent, OnTopLevelHelpDocsPointerPressed);
        if (_helpDocsPopupBoundsHandlerAttached)
        {
            _helpDocsPopupTopLevel.PropertyChanged -= OnHelpDocsPopupTopLevelPropertyChanged;
            _helpDocsPopupBoundsHandlerAttached = false;
        }
        _helpDocsPopupTopLevel = null;
        _helpDocsPopupHandlersAttached = false;
    }

    private void OnHelpPopupTopLevelPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != BoundsProperty)
            return;
        if (HelpPopup?.IsOpen == true)
            SchedulePopupClamp(HelpPopup, HelpPopover);
    }

    private void OnHelpDocsPopupTopLevelPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != BoundsProperty)
            return;
        if (HelpDocsPopup?.IsOpen == true)
            SchedulePopupClamp(HelpDocsPopup, HelpDocsPopover);
    }

    private void OnToolTipLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToolTip toolTip)
            return;

        ApplyToolTipBackdrop(toolTip);
    }

    private void SchedulePopupClamp(Popup? popup, Control? popover)
    {
        if (popup is null || popover is null)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        const double margin = 8;
        var bounds = topLevel.Bounds;
        var availableWidth = Math.Max(0, bounds.Width - margin * 2);
        var availableHeight = Math.Max(0, bounds.Height - margin * 2);

        if (availableWidth <= 0 || availableHeight <= 0)
            return;

        popover.Measure(new Size(availableWidth, availableHeight));
        var desired = popover.DesiredSize;

        if (desired.Width > availableWidth + 0.5)
            popover.Width = availableWidth;
        else if (!double.IsNaN(popover.Width))
            popover.Width = double.NaN;

        if (desired.Height > availableHeight + 0.5)
            popover.Height = availableHeight;
        else if (!double.IsNaN(popover.Height))
            popover.Height = double.NaN;

        Dispatcher.UIThread.Post(
            () => ApplyPopupOffsets(popup, topLevel, margin),
            DispatcherPriority.Render);
    }

    private static void ApplyPopupOffsets(Popup popup, TopLevel topLevel, double margin)
    {
        if (popup.Child is not Visual popupRoot)
            return;

        var origin = popupRoot.TranslatePoint(new Point(0, 0), topLevel);
        if (origin is null)
            return;

        var bounds = topLevel.Bounds;
        var size = popupRoot.Bounds.Size;
        var left = origin.Value.X;
        var top = origin.Value.Y;
        var right = left + size.Width;
        var bottom = top + size.Height;

        var offsetX = 0.0;
        var offsetY = 0.0;

        if (left < margin)
            offsetX = margin - left;
        else if (right > bounds.Width - margin)
            offsetX = bounds.Width - margin - right;

        if (top < margin)
            offsetY = margin - top;
        else if (bottom > bounds.Height - margin)
            offsetY = bounds.Height - margin - bottom;

        var fitsHorizontally = size.Width <= bounds.Width - margin * 2;
        if (fitsHorizontally)
        {
            var candidateLeft = left + offsetX;
            var desiredLeft = (bounds.Width - size.Width) / 2;
            var nudge = (desiredLeft - candidateLeft) * 0.25;
            offsetX += nudge;
        }

        var finalLeft = left + offsetX;
        var finalRight = finalLeft + size.Width;
        if (finalLeft < margin)
            offsetX += margin - finalLeft;
        else if (finalRight > bounds.Width - margin)
            offsetX += bounds.Width - margin - finalRight;

        if (Math.Abs(offsetX) > 0.1)
            popup.HorizontalOffset += offsetX;
        if (Math.Abs(offsetY) > 0.1)
            popup.VerticalOffset += offsetY;
    }

    private void ApplyPopupBackdrop(Popup? popup)
    {
        if (popup?.Child is null)
            return;

        if (TopLevel.GetTopLevel(popup.Child) is null)
            return;

        if (TopLevel.GetTopLevel(popup.Child) is not TopLevel popupLevel)
            return;

        var host = TopLevel.GetTopLevel(this);
        if (host is not null && ReferenceEquals(popupLevel, host))
            return;

        var viewModel = DataContext as MainWindowViewModel;
        if (viewModel is null)
            return;

        try
        {
            if (viewModel.HasAnyEffect)
            {
                popupLevel.TransparencyLevelHint =
                [
                    WindowTransparencyLevel.AcrylicBlur,
                    WindowTransparencyLevel.Blur,
                    WindowTransparencyLevel.None
                ];

                popupLevel.Background = Brushes.Transparent;
            }
            else
            {
                popupLevel.TransparencyLevelHint =
                [
                    WindowTransparencyLevel.None
                ];
            }
        }
        catch
        {
            // Ignore: popup could have closed.
        }
    }

    private void ApplyToolTipBackdrop(ToolTip toolTip)
    {
        if (TopLevel.GetTopLevel(toolTip) is null)
            return;

        if (TopLevel.GetTopLevel(toolTip) is not TopLevel tooltipLevel)
            return;

        var host = TopLevel.GetTopLevel(this);
        if (host is not null && ReferenceEquals(tooltipLevel, host))
            return;

        if (DataContext is not MainWindowViewModel viewModel)
            return;

        try
        {
            if (viewModel.HasAnyEffect)
            {
                tooltipLevel.TransparencyLevelHint =
                [
                    WindowTransparencyLevel.AcrylicBlur,
                    WindowTransparencyLevel.Blur,
                    WindowTransparencyLevel.Transparent,
                    WindowTransparencyLevel.None
                ];

                tooltipLevel.Background = Brushes.Transparent;
            }
            else
            {
                tooltipLevel.TransparencyLevelHint =
                [
                    WindowTransparencyLevel.None
                ];
            }
        }
        catch
        {
            // Ignore: tooltip could have closed.
        }
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        DetachHelpPopupHandlers();
        DetachHelpDocsPopupHandlers();
        DetachOwnedControlHandlers();
    }
}
