using Avalonia.Controls.Primitives.PopupPositioning;
using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia.Views;

public partial class TopMenuBarView : UserControl
{
    private const double LargePopupViewportInset = 8;
    private bool _ownedControlHandlersAttached;

    public event EventHandler<RoutedEventArgs>? OpenFolderRequested;
    public event EventHandler<RoutedEventArgs>? OpenNewWindowRequested;
    public event EventHandler<RoutedEventArgs>? RefreshRequested;
    public event EventHandler<RoutedEventArgs>? ExportTreeToFileRequested;
    public event EventHandler<RoutedEventArgs>? ExportContentToFileRequested;
    public event EventHandler<RoutedEventArgs>? ExportTreeAndContentToFileRequested;
    public event EventHandler<RoutedEventArgs>? ExportProjectCopyToFolderRequested;
    public event EventHandler<RoutedEventArgs>? ExportProjectCopyToZipRequested;
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
    public event EventHandler<RoutedEventArgs>? ToggleTreeExpansionAnimationRequested;
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
    public event EventHandler<RoutedEventArgs>? LanguageEsRequested;
    public event EventHandler<RoutedEventArgs>? LanguagePtRequested;
    public event EventHandler<RoutedEventArgs>? LanguagePtPtRequested;
    public event EventHandler<RoutedEventArgs>? LanguageZhCnRequested;
    public event EventHandler<RoutedEventArgs>? LanguageZhTwRequested;
    public event EventHandler<RoutedEventArgs>? LanguageJaRequested;
    public event EventHandler<RoutedEventArgs>? LanguageKoRequested;
    public event EventHandler<RoutedEventArgs>? LanguageTrRequested;
    public event EventHandler<RoutedEventArgs>? LanguageUkRequested;
    public event EventHandler<RoutedEventArgs>? LanguagePlRequested;
    public event EventHandler<RoutedEventArgs>? LanguageViRequested;
    public event EventHandler<RoutedEventArgs>? LanguageIdRequested;
    public event EventHandler<RoutedEventArgs>? HelpRequested;
    public event EventHandler<RoutedEventArgs>? UpdateCheckMenuRequested;
    public event EventHandler<RoutedEventArgs>? UpdateCheckRequested;
    public event EventHandler<RoutedEventArgs>? UpdateCloseRequested;
    public event EventHandler<RoutedEventArgs>? UpdateOpenRepositoryRequested;
    public event EventHandler<AutomaticUpdateCheckChangedEventArgs>? AutomaticUpdateCheckChanged;
    public event EventHandler<RoutedEventArgs>? TerminalCommandSetupRequested;
    public event EventHandler<RoutedEventArgs>? HelpCloseRequested;
    public event EventHandler<RoutedEventArgs>? AboutRequested;
    public event EventHandler<RoutedEventArgs>? AboutCloseRequested;
    public event EventHandler<RoutedEventArgs>? AboutSupportRequested;
    public event EventHandler<RoutedEventArgs>? AboutOpenLinkRequested;
    public event EventHandler<RoutedEventArgs>? ResetSettingsRequested;
    public event EventHandler<RoutedEventArgs>? ResetDataRequested;
    public event EventHandler<RoutedEventArgs>? SetSystemThemeRequested;
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
        HelpPopup.CustomPopupPlacementCallback = ConfigureLargePopupPlacement;
        HelpDocsPopup.CustomPopupPlacementCallback = ConfigureLargePopupPlacement;
        UpdatePopup.CustomPopupPlacementCallback = ConfigureLargePopupPlacement;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    public Menu? MainMenuControl => MainMenu;
    public MenuItem? RecentMenuItemControl => RecentMenuItem;
    public MenuItem? TreeFontMenuItemControl => TreeFontMenuItem;
    public MenuItem? LanguageMenuItemControl => LanguageMenuItem;
    public MenuItem? LanguageRuMenuItemControl => LanguageRuMenuItem;
    public MenuItem? LanguageEnMenuItemControl => LanguageEnMenuItem;
    public MenuItem? LanguageUzMenuItemControl => LanguageUzMenuItem;
    public MenuItem? LanguageTgMenuItemControl => LanguageTgMenuItem;
    public MenuItem? LanguageKkMenuItemControl => LanguageKkMenuItem;
    public MenuItem? LanguageFrMenuItemControl => LanguageFrMenuItem;
    public MenuItem? LanguageDeMenuItemControl => LanguageDeMenuItem;
    public MenuItem? LanguageItMenuItemControl => LanguageItMenuItem;
    public MenuItem? LanguageEsMenuItemControl => LanguageEsMenuItem;
    public MenuItem? LanguagePtMenuItemControl => LanguagePtMenuItem;
    public MenuItem? LanguagePtPtMenuItemControl => LanguagePtPtMenuItem;
    public MenuItem? LanguageZhCnMenuItemControl => LanguageZhCnMenuItem;
    public MenuItem? LanguageZhTwMenuItemControl => LanguageZhTwMenuItem;
    public MenuItem? LanguageJaMenuItemControl => LanguageJaMenuItem;
    public MenuItem? LanguageKoMenuItemControl => LanguageKoMenuItem;
    public MenuItem? LanguageTrMenuItemControl => LanguageTrMenuItem;
    public MenuItem? LanguageUkMenuItemControl => LanguageUkMenuItem;
    public MenuItem? LanguagePlMenuItemControl => LanguagePlMenuItem;
    public MenuItem? LanguageViMenuItemControl => LanguageViMenuItem;
    public MenuItem? LanguageIdMenuItemControl => LanguageIdMenuItem;

    private void OnOpenFolder(object? sender, RoutedEventArgs e) => OpenFolderRequested?.Invoke(sender, e);

    private void OnOpenNewWindow(object? sender, RoutedEventArgs e) => OpenNewWindowRequested?.Invoke(sender, e);

    private void OnRefresh(object? sender, RoutedEventArgs e) => RefreshRequested?.Invoke(sender, e);

    private void OnExportTreeToFile(object? sender, RoutedEventArgs e) => ExportTreeToFileRequested?.Invoke(sender, e);

    private void OnExportContentToFile(object? sender, RoutedEventArgs e) => ExportContentToFileRequested?.Invoke(sender, e);

    private void OnExportTreeAndContentToFile(object? sender, RoutedEventArgs e)
        => ExportTreeAndContentToFileRequested?.Invoke(sender, e);

    private void OnExportProjectCopyToFolder(object? sender, RoutedEventArgs e)
        => ExportProjectCopyToFolderRequested?.Invoke(sender, e);

    private void OnExportProjectCopyToZip(object? sender, RoutedEventArgs e)
        => ExportProjectCopyToZipRequested?.Invoke(sender, e);

    private void OnProjectCopyHelpIndicatorPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // The indicator is informational; consuming pointer input keeps the submenu open and prevents export.
        e.Handled = true;
    }

    private void OnProjectCopyHelpIndicatorPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Control indicator)
            ToolTip.SetIsOpen(indicator, true);

        e.Handled = true;
    }

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

    private void OnToggleTreeExpansionAnimation(
        object? sender,
        RoutedEventArgs e)
        => ToggleTreeExpansionAnimationRequested?.Invoke(sender, e);

    private void OnToggleSettings(object? sender, RoutedEventArgs e) => ToggleSettingsRequested?.Invoke(sender, e);

    private void OnTogglePreview(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel { CanTogglePreview: false })
            return;

        TogglePreviewRequested?.Invoke(sender, e);
    }

    private void OnToggleSearch(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel { IsSearchAvailable: false })
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
        if (DataContext is MainWindowViewModel { CanUseProjectWorkspaceActions: true } vm)
            vm.SelectedExportFormat = ExportFormat.Ascii;
    }

    private void OnJsonFormatClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel { CanUseProjectWorkspaceActions: true } vm)
            vm.SelectedExportFormat = ExportFormat.Json;
    }

    private void OnXmlFormatClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel { CanUseProjectWorkspaceActions: true } vm)
            vm.SelectedExportFormat = ExportFormat.Xml;
    }

    private void OnMarkdownFormatClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel { CanUseProjectWorkspaceActions: true } vm)
            vm.SelectedExportFormat = ExportFormat.Markdown;
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

    private void OnLangEs(object? sender, RoutedEventArgs e) => LanguageEsRequested?.Invoke(sender, e);

    private void OnLangPt(object? sender, RoutedEventArgs e) => LanguagePtRequested?.Invoke(sender, e);

    private void OnLangPtPt(object? sender, RoutedEventArgs e) => LanguagePtPtRequested?.Invoke(sender, e);

    private void OnLangZhCn(object? sender, RoutedEventArgs e) => LanguageZhCnRequested?.Invoke(sender, e);

    private void OnLangZhTw(object? sender, RoutedEventArgs e) => LanguageZhTwRequested?.Invoke(sender, e);

    private void OnLangJa(object? sender, RoutedEventArgs e) => LanguageJaRequested?.Invoke(sender, e);

    private void OnLangKo(object? sender, RoutedEventArgs e) => LanguageKoRequested?.Invoke(sender, e);

    private void OnLangTr(object? sender, RoutedEventArgs e) => LanguageTrRequested?.Invoke(sender, e);

    private void OnLangUk(object? sender, RoutedEventArgs e) => LanguageUkRequested?.Invoke(sender, e);

    private void OnLangPl(object? sender, RoutedEventArgs e) => LanguagePlRequested?.Invoke(sender, e);

    private void OnLangVi(object? sender, RoutedEventArgs e) => LanguageViRequested?.Invoke(sender, e);

    private void OnLangId(object? sender, RoutedEventArgs e) => LanguageIdRequested?.Invoke(sender, e);

    private void OnHelp(object? sender, RoutedEventArgs e) => HelpRequested?.Invoke(sender, e);

    private void OnCheckForUpdates(object? sender, RoutedEventArgs e)
        => UpdateCheckMenuRequested?.Invoke(sender, e);

    private void OnTerminalCommandSetup(object? sender, RoutedEventArgs e)
        => TerminalCommandSetupRequested?.Invoke(sender, e);

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
            ThemePopover.SetSystemThemeRequested += OnThemePopoverSetSystemThemeRequested;
            ThemePopover.SetLightThemeRequested += OnThemePopoverSetLightThemeRequested;
            ThemePopover.SetDarkThemeRequested += OnThemePopoverSetDarkThemeRequested;
            ThemePopover.SetTransparentModeRequested += OnThemePopoverSetTransparentModeRequested;
            ThemePopover.SetMicaModeRequested += OnThemePopoverSetMicaModeRequested;
            ThemePopover.SetAcrylicModeRequested += OnThemePopoverSetAcrylicModeRequested;
        }

        if (HelpPopover is not null)
        {
            HelpPopover.CloseRequested += OnHelpPopoverCloseRequested;
            HelpPopover.SupportRequested += OnHelpPopoverSupportRequested;
            HelpPopover.OpenLinkRequested += OnHelpPopoverOpenLinkRequested;
        }

        if (ThemePopup is not null)
            ThemePopup.Opened += OnThemePopupOpened;

        if (HelpPopup is not null)
            HelpPopup.Opened += OnHelpPopupOpened;

        if (HelpDocsPopover is not null)
            HelpDocsPopover.CloseRequested += OnHelpDocsPopoverCloseRequested;

        if (UpdatePopover is not null)
        {
            UpdatePopover.CloseRequested += OnUpdatePopoverCloseRequested;
            UpdatePopover.CheckRequested += OnUpdatePopoverCheckRequested;
            UpdatePopover.OpenRepositoryRequested += OnUpdatePopoverOpenRepositoryRequested;
            UpdatePopover.AutomaticCheckChanged += OnAutomaticUpdateCheckChanged;
        }

        if (HelpDocsPopup is not null)
            HelpDocsPopup.Opened += OnHelpDocsPopupOpened;

        if (UpdatePopup is not null)
            UpdatePopup.Opened += OnUpdatePopupOpened;

        if (RootGrid is not null)
            RootGrid.SizeChanged += OnLargePopupPlacementBoundsChanged;

        if (HelpPopover is not null)
            HelpPopover.SizeChanged += OnLargePopupPlacementBoundsChanged;

        if (HelpDocsPopover is not null)
            HelpDocsPopover.SizeChanged += OnLargePopupPlacementBoundsChanged;

        if (UpdatePopover is not null)
            UpdatePopover.SizeChanged += OnLargePopupPlacementBoundsChanged;

        _ownedControlHandlersAttached = true;
    }

    private void DetachOwnedControlHandlers()
    {
        if (!_ownedControlHandlersAttached)
            return;

        if (ThemePopover is not null)
        {
            ThemePopover.SetSystemThemeRequested -= OnThemePopoverSetSystemThemeRequested;
            ThemePopover.SetLightThemeRequested -= OnThemePopoverSetLightThemeRequested;
            ThemePopover.SetDarkThemeRequested -= OnThemePopoverSetDarkThemeRequested;
            ThemePopover.SetTransparentModeRequested -= OnThemePopoverSetTransparentModeRequested;
            ThemePopover.SetMicaModeRequested -= OnThemePopoverSetMicaModeRequested;
            ThemePopover.SetAcrylicModeRequested -= OnThemePopoverSetAcrylicModeRequested;
        }

        if (HelpPopover is not null)
        {
            HelpPopover.CloseRequested -= OnHelpPopoverCloseRequested;
            HelpPopover.SupportRequested -= OnHelpPopoverSupportRequested;
            HelpPopover.OpenLinkRequested -= OnHelpPopoverOpenLinkRequested;
        }

        if (ThemePopup is not null)
            ThemePopup.Opened -= OnThemePopupOpened;

        if (HelpPopup is not null)
            HelpPopup.Opened -= OnHelpPopupOpened;

        if (HelpDocsPopover is not null)
            HelpDocsPopover.CloseRequested -= OnHelpDocsPopoverCloseRequested;

        if (UpdatePopover is not null)
        {
            UpdatePopover.CloseRequested -= OnUpdatePopoverCloseRequested;
            UpdatePopover.CheckRequested -= OnUpdatePopoverCheckRequested;
            UpdatePopover.OpenRepositoryRequested -= OnUpdatePopoverOpenRepositoryRequested;
            UpdatePopover.AutomaticCheckChanged -= OnAutomaticUpdateCheckChanged;
        }

        if (HelpDocsPopup is not null)
            HelpDocsPopup.Opened -= OnHelpDocsPopupOpened;

        if (UpdatePopup is not null)
            UpdatePopup.Opened -= OnUpdatePopupOpened;

        if (RootGrid is not null)
            RootGrid.SizeChanged -= OnLargePopupPlacementBoundsChanged;

        if (HelpPopover is not null)
            HelpPopover.SizeChanged -= OnLargePopupPlacementBoundsChanged;

        if (HelpDocsPopover is not null)
            HelpDocsPopover.SizeChanged -= OnLargePopupPlacementBoundsChanged;

        if (UpdatePopover is not null)
            UpdatePopover.SizeChanged -= OnLargePopupPlacementBoundsChanged;

        _ownedControlHandlersAttached = false;
    }

    private void OnThemePopoverSetSystemThemeRequested(object? sender, RoutedEventArgs e)
        => SetSystemThemeRequested?.Invoke(this, e);

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

    private void OnHelpPopoverSupportRequested(object? sender, RoutedEventArgs e)
        => AboutSupportRequested?.Invoke(this, e);

    private void OnHelpPopoverOpenLinkRequested(object? sender, RoutedEventArgs e)
        => AboutOpenLinkRequested?.Invoke(this, e);

    private void OnHelpDocsPopoverCloseRequested(object? sender, RoutedEventArgs e)
        => HelpCloseRequested?.Invoke(this, e);

    private void OnUpdatePopoverCloseRequested(object? sender, RoutedEventArgs e)
        => UpdateCloseRequested?.Invoke(this, e);

    private void OnUpdatePopoverCheckRequested(object? sender, RoutedEventArgs e)
        => UpdateCheckRequested?.Invoke(this, e);

    private void OnUpdatePopoverOpenRepositoryRequested(object? sender, RoutedEventArgs e)
        => UpdateOpenRepositoryRequested?.Invoke(this, e);

    private void OnAutomaticUpdateCheckChanged(
        object? sender,
        AutomaticUpdateCheckChangedEventArgs e)
        => AutomaticUpdateCheckChanged?.Invoke(this, e);

    private void OnThemePopupOpened(object? sender, EventArgs e)
    {
        ThemePopover?.Focus();
        ApplyPopupBackdrop(ThemePopup);
    }

    private void OnHelpPopupOpened(object? sender, EventArgs e)
    {
        SynchronizeLargePopupOffset(HelpPopup);
        HelpPopover?.Focus();
        ApplyPopupBackdrop(HelpPopup);
    }

    private void OnHelpDocsPopupOpened(object? sender, EventArgs e)
    {
        SynchronizeLargePopupOffset(HelpDocsPopup);
        HelpDocsPopover?.Focus();
        ApplyPopupBackdrop(HelpDocsPopup);
    }

    private void OnUpdatePopupOpened(object? sender, EventArgs e)
    {
        SynchronizeLargePopupOffset(UpdatePopup);
        UpdatePopover?.Focus();
        ApplyPopupBackdrop(UpdatePopup);
    }

    private void ConfigureLargePopupPlacement(CustomPopupPlacement placement)
    {
        // Preserve Avalonia's original Bottom placement (centered under Help) while
        // constraining the popup to this window rather than the monitor work area.
        placement.Anchor = PopupAnchor.Bottom;
        placement.Gravity = PopupGravity.Bottom;
        placement.Offset = new Point(
            CalculateLargePopupHorizontalOffset(
                placement.AnchorRectangle,
                placement.PopupSize.Width,
                GetLargePopupViewportBounds()),
            placement.Offset.Y);
    }

    private void OnLargePopupPlacementBoundsChanged(object? sender, SizeChangedEventArgs e)
    {
        SynchronizeLargePopupOffset(HelpPopup);
        SynchronizeLargePopupOffset(HelpDocsPopup);
        SynchronizeLargePopupOffset(UpdatePopup);
    }

    private void SynchronizeLargePopupOffset(Popup? popup)
    {
        if (popup?.IsOpen != true ||
            popup.Child is not { Bounds.Width: > 0 } child ||
            HelpMenuItem is null ||
            RootGrid is null ||
            HelpMenuItem.TranslatePoint(default, RootGrid) is not { } targetOrigin)
        {
            return;
        }

        var anchorRectangle = new Rect(targetOrigin, HelpMenuItem.Bounds.Size);
        var horizontalOffset = CalculateLargePopupHorizontalOffset(
            anchorRectangle,
            child.Bounds.Width,
            new Rect(default, RootGrid.Bounds.Size));
        if (Math.Abs(popup.HorizontalOffset - horizontalOffset) > 0.1)
            popup.HorizontalOffset = horizontalOffset;
    }

    private Rect GetLargePopupViewportBounds()
    {
        if (RootGrid is null)
            return default;

        var topLevel = TopLevel.GetTopLevel(RootGrid);
        var origin = topLevel is null
            ? default
            : RootGrid.TranslatePoint(default, topLevel) ?? default;
        return new Rect(origin, RootGrid.Bounds.Size);
    }

    internal static double CalculateLargePopupHorizontalOffset(
        Rect anchorRectangle,
        double popupWidth,
        Rect viewportBounds)
    {
        if (!double.IsFinite(popupWidth) ||
            popupWidth <= 0 ||
            !double.IsFinite(viewportBounds.Width) ||
            viewportBounds.Width <= 0)
        {
            return 0;
        }

        var preferredLeft = anchorRectangle.X + (anchorRectangle.Width - popupWidth) / 2;
        var minimumLeft = viewportBounds.Left + LargePopupViewportInset;
        var maximumLeft = Math.Max(
            minimumLeft,
            viewportBounds.Right - LargePopupViewportInset - popupWidth);
        var constrainedLeft = Math.Clamp(preferredLeft, minimumLeft, maximumLeft);
        return constrainedLeft - preferredLeft;
    }

    internal void RefreshOpenPopupBackdrops()
    {
        // Native popups are separate top-level windows. Dynamic brushes update in place,
        // but changing the selected material does not renegotiate their transparency hints.
        // Keep already-open surfaces synchronized instead of requiring a close/reopen cycle.
        ApplyPopupBackdropIfOpen(ThemePopup);
        ApplyPopupBackdropIfOpen(HelpPopup);
        ApplyPopupBackdropIfOpen(HelpDocsPopup);
        ApplyPopupBackdropIfOpen(UpdatePopup);
    }

    private void ApplyPopupBackdrop(Popup? popup)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        PopupBackdropConfigurator.TryApply(
            popup?.Child,
            TopLevel.GetTopLevel(this),
            viewModel.ActiveThemeEffect,
            PopupBackdropTransparencyFallback.None);
    }

    private void ApplyPopupBackdropIfOpen(Popup? popup)
    {
        if (popup?.IsOpen == true)
            ApplyPopupBackdrop(popup);
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        DetachOwnedControlHandlers();
    }
}
