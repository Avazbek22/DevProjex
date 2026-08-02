using Avalonia.Animation;
using Avalonia.Animation.Easings;
using DevProjex.Avalonia.Services;
using DevProjex.Avalonia.Views;

namespace DevProjex.Avalonia.Coordinators;

internal sealed record WorkspacePresentationControls(
    Grid WorkspaceGrid,
    Border TreePaneContainer,
    Border PreviewPaneContainer,
    ColumnDefinition TreePaneColumn,
    ColumnDefinition PreviewPaneColumn,
    Border TreePreviewSplitter,
    Border PreviewSettingsSplitter,
    Border TreeIsland,
    Border PreviewIsland,
    Border DropZoneContainer,
    ItemsControl ToastHost,
    Border PreviewBarContainer,
    Border PreviewBar,
    Grid PreviewSegmentGrid,
    Button PreviewTreeModeButton,
    Button PreviewContentModeButton,
    Button PreviewTreeAndContentModeButton,
    Border SettingsContainer,
    Border SettingsIsland,
    SettingsPanelView SettingsPanel);

internal sealed class WorkspacePresentationController : IDisposable
{
    internal const double SettingsPanelDefaultWidth = 285.0;
    internal const double SettingsPanelMinimumWidth = SettingsPanelDefaultWidth;
    internal const double SettingsPanelMaximumWidth = 320.0;
    internal const double SplitTreePaneMinimumWidth = 418.0;
    internal const double SplitPreviewPaneMinimumWidth = 320.0;
    internal const double TreePreviewSplitterWidth = 4.0;
    internal const double PreviewSettingsSplitterWidth = 4.0;
    internal const double WindowMinimumWidthSafetyPadding = 32.0;

    // Keep this baseline independent of current pane visibility. Opening preview or settings
    // must not force the native window to grow after it reached the supported minimum size.
    internal const double MinimumWindowWidth =
        SplitTreePaneMinimumWidth +
        TreePreviewSplitterWidth +
        SplitPreviewPaneMinimumWidth +
        PreviewSettingsSplitterWidth +
        SettingsPanelMinimumWidth +
        WindowMinimumWidthSafetyPadding;
    internal static readonly TimeSpan PanelAnimationDuration =
        UiTimingProfile.Scale(TimeSpan.FromMilliseconds(300));
    internal static readonly TimeSpan SettingsPanelAnimationDuration =
        UiTimingProfile.Scale(TimeSpan.FromMilliseconds(250));

    private const double PreviewToolbarWideThreshold = 380.0;
    private const double PreviewToolbarCompactThreshold = 320.0;
    private const double ToastHostBottomMargin = 38.0;
    private const double ToastHostHorizontalInset = 12.0;
    private const string SplitterDraggingClass = "splitter-dragging";

    private readonly Window _window;
    private readonly MainWindowViewModel _viewModel;
    private readonly WorkspacePresentationControls _controls;
    private GridLength _savedSplitTreeColumnWidth = new(5, GridUnitType.Star);
    private GridLength _savedSplitPreviewColumnWidth = new(6, GridUnitType.Star);
    private double _currentPreviewTreePaneWidth;
    private double _currentSettingsPanelWidth = SettingsPanelDefaultWidth;
    private double _savedNonSplitSettingsPanelWidth = SettingsPanelDefaultWidth;
    private double _effectiveSettingsPanelMinimumWidth = SettingsPanelMinimumWidth;
    private double _lastWindowBoundsWidth;
    private WorkspaceResizeTarget _activeResizeTarget;
    private IPointer? _activeResizePointer;
    private double _lastResizePointerX;
    private PreviewToolbarLayoutMode _previewToolbarLayoutMode = PreviewToolbarLayoutMode.Wide;
    private Task _settingsAnimationTask = Task.CompletedTask;
    private bool _workspaceChromeRefreshPending;
    private bool _disposed;

    public WorkspacePresentationController(
        Window window,
        MainWindowViewModel viewModel,
        WorkspacePresentationControls controls)
    {
        _window = window;
        _viewModel = viewModel;
        _controls = controls;

        controls.SettingsContainer.Width = 0;
        controls.SettingsIsland.Width = SettingsPanelDefaultWidth;
        controls.SettingsIsland.Opacity = 0;
        controls.PreviewPaneContainer.Width = 0;

        controls.SettingsPanel.MinimumWidthChanged += OnSettingsPanelMinimumWidthChanged;
        UpdateSettingsPanelMinimumWidth(controls.SettingsPanel.GetRequiredMinimumWidth());
    }

    public bool IsSettingsAnimating { get; private set; }

    public bool IsTreePaneAnimating { get; set; }

    public bool IsPreviewPaneAnimating { get; set; }

    public Task SettingsAnimationTask => _settingsAnimationTask;

    public GridLength SavedSplitTreeColumnWidth => _savedSplitTreeColumnWidth;

    public GridLength SavedSplitPreviewColumnWidth => _savedSplitPreviewColumnWidth;

    public void SetCurrentPreviewTreePaneWidth(double width)
        => _currentPreviewTreePaneWidth = width;

    public void ResetSettingsPanelWidthForPreview()
        => _currentSettingsPanelWidth = GetClampedSettingsPanelWidth(SettingsPanelDefaultWidth);

    public void HandleWindowBoundsChanged(double width)
    {
        var widthDelta = _lastWindowBoundsWidth > 0
            ? width - _lastWindowBoundsWidth
            : 0;
        _lastWindowBoundsWidth = width;

        if (_viewModel.IsPreviewTreeVisible)
            AdjustSplitPaneWidthsForWindowResize(widthDelta);

        ClampSettingsPanelWidthToAvailableSpace(ShouldApplySettingsPanelWidthToVisual());
        UpdatePreviewSettingsSplitterState();
        UpdateAdaptiveWorkspaceChrome();
    }

    public void HandleWindowScalingChanged()
        => UpdateWindowMinimumWidth();

    public void UpdateCompactModeVisualState()
    {
        if (_viewModel.IsCompactModeEffective)
            _window.Classes.Add("compact-mode");
        else
            _window.Classes.Remove("compact-mode");

        _controls.SettingsPanel.RequestMinimumWidthRefresh();
    }

    public void UpdateWorkspaceLayoutForCurrentMode()
    {
        var displayMode = GetCurrentDisplayMode();

        switch (displayMode)
        {
            case WorkspaceDisplayMode.PreviewOnly:
                SetWorkspacePaneState(
                    _controls.TreePaneColumn,
                    visible: false,
                    new GridLength(0),
                    minWidth: 0);
                SetWorkspacePaneState(
                    _controls.PreviewPaneColumn,
                    visible: true,
                    new GridLength(1, GridUnitType.Star),
                    SplitPreviewPaneMinimumWidth);
                SetTreePreviewSplitterState(isVisible: false);
                if (!IsPreviewPaneAnimating)
                    ApplyPreviewPaneWidth(double.NaN, animate: false);
                break;

            case WorkspaceDisplayMode.PreviewWithTree:
                SetWorkspacePaneState(
                    _controls.TreePaneColumn,
                    visible: true,
                    GridLength.Auto,
                    minWidth: 0);
                SetWorkspacePaneState(
                    _controls.PreviewPaneColumn,
                    visible: true,
                    new GridLength(1, GridUnitType.Star),
                    SplitPreviewPaneMinimumWidth + TreePreviewSplitterWidth);
                SetTreePreviewSplitterState(isVisible: true);
                ApplyPreviewTreePaneWidth(ResolveDesiredPreviewTreePaneWidth(), animate: false);
                if (!IsPreviewPaneAnimating)
                    ApplyPreviewPaneWidth(double.NaN, animate: false);
                break;

            default:
                SetWorkspacePaneState(
                    _controls.TreePaneColumn,
                    visible: true,
                    new GridLength(1, GridUnitType.Star),
                    SplitTreePaneMinimumWidth);
                SetWorkspacePaneState(
                    _controls.PreviewPaneColumn,
                    visible: false,
                    new GridLength(0),
                    minWidth: 0);
                SetTreePreviewSplitterState(isVisible: false);
                if (!IsTreePaneAnimating)
                    ApplyPreviewTreePaneWidth(double.NaN, animate: false);
                if (!IsPreviewPaneAnimating)
                    ApplyPreviewPaneWidth(0.0, animate: false);
                break;
        }

        ClampSettingsPanelWidthToAvailableSpace(ShouldApplySettingsPanelWidthToVisual());
        UpdatePreviewSettingsSplitterState();
        UpdateAdaptiveWorkspaceChrome();
    }

    public void UpdateAdaptiveWorkspaceChrome(bool forcePreviewLabels = false)
    {
        UpdateWindowMinimumWidth();
        UpdatePreviewToolbarPresentation(forcePreviewLabels);
        UpdateToastHostLayout();
    }

    public void UpdatePreviewToolbarPresentation(bool forceRefreshContent)
    {
        var nextLayoutMode = DeterminePreviewToolbarLayoutMode();
        if (nextLayoutMode != _previewToolbarLayoutMode)
        {
            _previewToolbarLayoutMode = nextLayoutMode;
            ApplyPreviewToolbarLayoutMode();
            forceRefreshContent = true;
        }

        if (forceRefreshContent)
            ApplyPreviewToolbarLabels();
    }

    public void UpdateToastHostLayout()
    {
        if (_controls.ToastHost.Parent is not Visual toastHostParent)
            return;

        var targetVisual = ResolveToastHostTarget();
        var translatedOrigin = targetVisual.TranslatePoint(default, toastHostParent);
        var targetWidth = targetVisual.Bounds.Width;
        if (translatedOrigin is null || targetWidth <= 1)
        {
            ResetToastHostLayout();
            return;
        }

        var horizontalInset = Math.Min(ToastHostHorizontalInset, targetWidth / 8);
        var hostWidth = Math.Max(0, targetWidth - (horizontalInset * 2));
        if (hostWidth <= 1)
        {
            ResetToastHostLayout();
            return;
        }

        _controls.ToastHost.HorizontalAlignment = HorizontalAlignment.Left;
        _controls.ToastHost.Width = hostWidth;
        _controls.ToastHost.MaxWidth = hostWidth;
        _controls.ToastHost.Margin = new Thickness(
            translatedOrigin.Value.X + horizontalInset,
            0,
            0,
            ToastHostBottomMargin);
    }

    public void CaptureSplitPaneLayout()
    {
        var treeWidth = ResolvePreviewTreePaneVisibleWidth();
        var previewWidth = Math.Max(
            0.0,
            _controls.PreviewPaneColumn.ActualWidth -
            GetRenderedTreePreviewSplitterWidth());
        var totalWidth = treeWidth + previewWidth;
        if (treeWidth <= 0 || previewWidth <= 0 || totalWidth <= 0)
            return;

        _currentPreviewTreePaneWidth = treeWidth;
        _savedSplitTreeColumnWidth = new GridLength(treeWidth / totalWidth, GridUnitType.Star);
        _savedSplitPreviewColumnWidth = new GridLength(previewWidth / totalWidth, GridUnitType.Star);
    }

    public void AdjustSplitPaneWidthsForWindowResize(double widthDelta)
    {
        if (!_viewModel.IsPreviewTreeVisible || Math.Abs(widthDelta) < 0.5)
            return;

        var clampedWidth = GetClampedPreviewTreePaneWidth(
            _currentPreviewTreePaneWidth > 0.5
                ? _currentPreviewTreePaneWidth
                : ResolveDesiredPreviewTreePaneWidth());
        if (Math.Abs(clampedWidth - ResolvePreviewTreePaneVisibleWidth()) < 0.5)
            return;

        _currentPreviewTreePaneWidth = clampedWidth;
        ApplyPreviewTreePaneWidth(clampedWidth, animate: false);
    }

    public void ApplyPreviewTreePaneWidth(double width, bool animate)
    {
        if (animate)
        {
            EnsureWidthTransition(_controls.TreePaneContainer, PanelAnimationDuration);
            _controls.TreePaneContainer.Width = width;
            return;
        }

        SetWidthWithoutTransition(_controls.TreePaneContainer, width);
    }

    public void ApplyPreviewPaneWidth(double width, bool animate)
    {
        if (animate)
        {
            EnsureWidthTransition(_controls.PreviewPaneContainer, PanelAnimationDuration);
            _controls.PreviewPaneContainer.Width = width;
            return;
        }

        SetWidthWithoutTransition(_controls.PreviewPaneContainer, width);
    }

    public void EnsurePreviewPaneTransitions()
        => EnsureWidthTransition(_controls.PreviewPaneContainer, PanelAnimationDuration);

    public double ResolvePreviewPaneVisibleWidth()
    {
        if (_controls.PreviewPaneContainer.Width > 0.5)
        {
            return Math.Max(
                0.0,
                _controls.PreviewPaneContainer.Width -
                GetRenderedTreePreviewSplitterWidth());
        }

        if (_controls.PreviewPaneContainer.Bounds.Width > 0.5)
        {
            return Math.Max(
                0.0,
                _controls.PreviewPaneContainer.Bounds.Width -
                GetRenderedTreePreviewSplitterWidth());
        }

        return _controls.PreviewPaneColumn.ActualWidth > 0.5
            ? Math.Max(
                0.0,
                _controls.PreviewPaneColumn.ActualWidth -
                GetRenderedTreePreviewSplitterWidth())
            : 0;
    }

    private double GetRenderedTreePreviewSplitterWidth()
    {
        if (!_controls.TreePreviewSplitter.IsVisible)
            return 0.0;

        if (_controls.TreePreviewSplitter.Bounds.Width > 0.0)
            return _controls.TreePreviewSplitter.Bounds.Width;

        return double.IsFinite(_controls.TreePreviewSplitter.Width)
            ? Math.Max(0.0, _controls.TreePreviewSplitter.Width)
            : 0.0;
    }

    public double ResolveDesiredPreviewTreePaneWidth()
    {
        if (_currentPreviewTreePaneWidth > 0.5)
            return GetClampedPreviewTreePaneWidth(_currentPreviewTreePaneWidth);

        return ResolvePreviewTreePaneProjectedWidth();
    }

    public double ResolveDesiredPreviewPaneWidth(double desiredTreeWidth)
    {
        var availableSplitWidth = GetAvailableSplitWorkspaceWidth();
        if (availableSplitWidth <= 0.5)
            return SplitPreviewPaneMinimumWidth;

        return Math.Max(SplitPreviewPaneMinimumWidth, availableSplitWidth - desiredTreeWidth);
    }

    public double GetClampedPreviewTreePaneWidth(double desiredWidth)
    {
        var maxWidth = GetMaximumPreviewTreePaneWidth();
        if (maxWidth <= 0.5)
            return SplitTreePaneMinimumWidth;

        var minWidth = Math.Min(SplitTreePaneMinimumWidth, maxWidth);
        return Math.Clamp(desiredWidth, minWidth, maxWidth);
    }

    public double GetAvailableTreeOnlyWorkspaceWidth()
    {
        var workspaceWidth = _controls.WorkspaceGrid.Bounds.Width;
        if (workspaceWidth <= 0.5)
            return SplitTreePaneMinimumWidth;

        var settingsWidth = ShouldShowPreviewSettingsSplitter()
            ? GetVisibleSettingsPanelWidth() + PreviewSettingsSplitterWidth
            : 0.0;
        return Math.Max(SplitTreePaneMinimumWidth, workspaceWidth - settingsWidth);
    }

    public double ResolvePreviewTreePaneWidthForCollapse()
    {
        var visibleWidth = ResolvePreviewTreePaneVisibleWidth();
        if (visibleWidth > 0.5)
            return visibleWidth;

        var workspaceWidth = _controls.WorkspaceGrid.Bounds.Width;
        if (workspaceWidth <= 0.5)
            return SplitTreePaneMinimumWidth;

        EnsureSavedSplitPaneWidths();

        var settingsWidth = ShouldShowPreviewSettingsSplitter()
            ? GetVisibleSettingsPanelWidth() + PreviewSettingsSplitterWidth
            : 0.0;
        var availableWorkspaceWidth = Math.Max(0, workspaceWidth - settingsWidth);
        var availableSplitWidth = Math.Max(
            0,
            availableWorkspaceWidth - TreePreviewSplitterWidth);
        if (availableSplitWidth <= 0.5)
            return SplitTreePaneMinimumWidth;

        var treeWeight = IsUsableSplitPaneWidth(_savedSplitTreeColumnWidth)
            ? _savedSplitTreeColumnWidth.Value
            : 5.0;
        var previewWeight = IsUsableSplitPaneWidth(_savedSplitPreviewColumnWidth)
            ? _savedSplitPreviewColumnWidth.Value
            : 6.0;
        var totalWeight = treeWeight + previewWeight;
        if (totalWeight <= 0.001)
            return SplitTreePaneMinimumWidth;

        var projectedTreeWidth = availableSplitWidth * (treeWeight / totalWeight);
        var maximumTreeWidth = Math.Max(
            SplitTreePaneMinimumWidth,
            availableSplitWidth - SplitPreviewPaneMinimumWidth);
        return Math.Clamp(
            projectedTreeWidth,
            SplitTreePaneMinimumWidth,
            maximumTreeWidth);
    }

    public void UpdatePreviewSettingsSplitterState()
        => SetPreviewSettingsSplitterVisibility(ShouldShowPreviewSettingsSplitter());

    public void SetPreviewSettingsSplitterVisibility(bool isVisible)
    {
        _controls.PreviewSettingsSplitter.IsVisible = isVisible;
        _controls.PreviewSettingsSplitter.IsHitTestVisible = isVisible;
    }

    public bool ShouldShowPreviewSettingsSplitter()
    {
        if (!_viewModel.IsProjectLoaded)
            return false;

        return IsSettingsAnimating || _viewModel.SettingsVisible || HasVisibleSettingsPanelWidth();
    }

    public bool ShouldApplySettingsPanelWidthToVisual()
        => !IsSettingsAnimating && HasVisibleSettingsPanelWidth();

    public void ClampSettingsPanelWidthToAvailableSpace(bool applyToVisual)
    {
        _currentSettingsPanelWidth = GetClampedSettingsPanelWidth(_currentSettingsPanelWidth);
        if (!applyToVisual || IsSettingsAnimating)
            return;

        if (!_viewModel.SettingsVisible &&
            _controls.SettingsContainer.Width <= 0.5 &&
            _controls.SettingsContainer.Bounds.Width <= 0.5)
        {
            return;
        }

        ApplySettingsPanelWidth(_currentSettingsPanelWidth, animate: false);
    }

    public double GetClampedSettingsPanelWidth(double desiredWidth)
    {
        var maxWidth = GetMaximumSettingsPanelWidth();
        if (maxWidth <= 0)
            return 0;

        var minWidth = Math.Min(_effectiveSettingsPanelMinimumWidth, maxWidth);
        return Math.Clamp(desiredWidth, minWidth, maxWidth);
    }

    public double GetVisibleSettingsPanelWidth()
    {
        if (_controls.SettingsContainer.Width > 0.5)
            return GetSettingsContentWidth(_controls.SettingsContainer.Width);

        if (_controls.SettingsContainer.Bounds.Width > 0.5)
            return GetSettingsContentWidth(_controls.SettingsContainer.Bounds.Width);

        return _currentSettingsPanelWidth;
    }

    public void OnTreePreviewSplitterPointerPressed(object? sender, PointerPressedEventArgs e)
        => BeginWorkspaceResize(sender as Border, e, WorkspaceResizeTarget.TreePreview);

    public void OnPreviewSettingsSplitterPointerPressed(object? sender, PointerPressedEventArgs e)
        => BeginWorkspaceResize(sender as Border, e, WorkspaceResizeTarget.PreviewSettings);

    public void OnWorkspaceSplitterPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_activeResizeTarget == WorkspaceResizeTarget.None ||
            !ReferenceEquals(e.Pointer, _activeResizePointer))
        {
            return;
        }

        var currentX = e.GetPosition(_controls.WorkspaceGrid).X;
        var deltaX = currentX - _lastResizePointerX;
        if (Math.Abs(deltaX) < 0.01)
            return;

        _lastResizePointerX = currentX;
        switch (_activeResizeTarget)
        {
            case WorkspaceResizeTarget.TreePreview:
                ResizeTreePreviewPanes(deltaX);
                break;

            case WorkspaceResizeTarget.PreviewSettings:
                ResizeSettingsPane(deltaX);
                break;
        }

        e.Handled = true;
    }

    public void OnWorkspaceSplitterPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer, _activeResizePointer))
            return;

        CompleteActiveWorkspaceResize(releasePointer: true);
        e.Handled = true;
    }

    public void OnWorkspaceSplitterPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => CompleteActiveWorkspaceResize(releasePointer: false);

    public void OnWorkspaceSplitterPointerExited(object? sender, PointerEventArgs e)
    {
        if (_activeResizeTarget == WorkspaceResizeTarget.None)
            ScheduleWorkspaceChromeRefresh();
    }

    public Task AnimateSettingsPanelAsync(bool show)
    {
        if (IsSettingsAnimating)
            return _settingsAnimationTask;

        _settingsAnimationTask = RunSettingsPanelAnimationAsync(show);
        return _settingsAnimationTask;
    }

    public void CaptureNonSplitSettingsPanelWidth()
    {
        if (_viewModel.IsPreviewMode)
            return;

        var currentWidth = GetVisibleSettingsPanelWidth();
        if (currentWidth > 0.5)
        {
            _savedNonSplitSettingsPanelWidth =
                Math.Max(_effectiveSettingsPanelMinimumWidth, currentWidth);
        }
    }

    public void RestoreNonSplitSettingsPanelWidth()
        => _currentSettingsPanelWidth =
            Math.Max(_effectiveSettingsPanelMinimumWidth, _savedNonSplitSettingsPanelWidth);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CompleteActiveWorkspaceResize(releasePointer: true);
        _controls.SettingsPanel.MinimumWidthChanged -= OnSettingsPanelMinimumWidthChanged;
    }

    internal static double AlignWindowConstraintToPhysicalPixels(double constraint, double renderScaling)
    {
        var effectiveScaling = double.IsFinite(renderScaling) && renderScaling > 0
            ? renderScaling
            : 1.0;

        // Win32 tracks constraints in physical pixels. DIP alignment prevents
        // Avalonia and WM_GETMINMAXINFO from rounding in opposite directions.
        return Math.Ceiling(constraint * effectiveScaling) / effectiveScaling;
    }

    private WorkspaceDisplayMode GetCurrentDisplayMode()
    {
        if (_viewModel.IsPreviewTreeVisible)
            return WorkspaceDisplayMode.PreviewWithTree;

        return _viewModel.IsPreviewMode
            ? WorkspaceDisplayMode.PreviewOnly
            : WorkspaceDisplayMode.Tree;
    }

    private void UpdateWindowMinimumWidth()
    {
        var computedMinimumWidth = Math.Max(
            MinimumWindowWidth,
            GetRequiredWindowWorkspaceWidth() + WindowMinimumWidthSafetyPadding);
        _window.MinWidth = AlignWindowConstraintToPhysicalPixels(
            computedMinimumWidth,
            _window.RenderScaling);
    }

    private double GetRequiredWindowWorkspaceWidth()
    {
        if (!_viewModel.IsProjectLoaded)
            return 0.0;

        var minimumWidth = GetMinimumLeadingWorkspaceWidth();
        if (ShouldReserveSettingsWidth())
            minimumWidth += _effectiveSettingsPanelMinimumWidth + PreviewSettingsSplitterWidth;

        return minimumWidth;
    }

    private bool ShouldReserveSettingsWidth()
        => IsSettingsAnimating || _viewModel.SettingsVisible || HasVisibleSettingsPanelWidth();

    private PreviewToolbarLayoutMode DeterminePreviewToolbarLayoutMode()
    {
        var previewBarWidth = _controls.PreviewSegmentGrid.Bounds.Width > 0
            ? _controls.PreviewSegmentGrid.Bounds.Width
            : _controls.PreviewBar.Bounds.Width > 0
                ? _controls.PreviewBar.Bounds.Width
                : _controls.PreviewBarContainer.Bounds.Width;
        if (previewBarWidth <= 0)
            return _previewToolbarLayoutMode;

        if (previewBarWidth < PreviewToolbarCompactThreshold)
            return PreviewToolbarLayoutMode.Narrow;

        return previewBarWidth < PreviewToolbarWideThreshold
            ? PreviewToolbarLayoutMode.Compact
            : PreviewToolbarLayoutMode.Wide;
    }

    private void ApplyPreviewToolbarLayoutMode()
    {
        _controls.PreviewBar.Classes.Remove("preview-toolbar-compact");
        _controls.PreviewBar.Classes.Remove("preview-toolbar-narrow");

        switch (_previewToolbarLayoutMode)
        {
            case PreviewToolbarLayoutMode.Compact:
                _controls.PreviewBar.Classes.Add("preview-toolbar-compact");
                break;

            case PreviewToolbarLayoutMode.Narrow:
                _controls.PreviewBar.Classes.Add("preview-toolbar-compact");
                _controls.PreviewBar.Classes.Add("preview-toolbar-narrow");
                break;
        }
    }

    private void ApplyPreviewToolbarLabels()
    {
        var useShortLabels = _previewToolbarLayoutMode != PreviewToolbarLayoutMode.Wide;
        _controls.PreviewTreeModeButton.Content =
            useShortLabels ? _viewModel.PreviewModeTreeShort : _viewModel.PreviewModeTree;
        _controls.PreviewContentModeButton.Content =
            useShortLabels ? _viewModel.PreviewModeContentShort : _viewModel.PreviewModeContent;
        _controls.PreviewTreeAndContentModeButton.Content =
            useShortLabels
                ? _viewModel.PreviewModeTreeAndContentShort
                : _viewModel.PreviewModeTreeAndContent;
        ToolTip.SetTip(_controls.PreviewTreeModeButton, null);
        ToolTip.SetTip(_controls.PreviewContentModeButton, null);
        ToolTip.SetTip(_controls.PreviewTreeAndContentModeButton, null);
    }

    private Control ResolveToastHostTarget()
    {
        if (!_viewModel.IsProjectLoaded)
            return _controls.DropZoneContainer;

        if (_viewModel.IsPreviewTreeVisible)
            return _controls.TreeIsland;

        return _viewModel.IsPreviewMode
            ? _controls.PreviewIsland
            : _controls.TreeIsland;
    }

    private void ResetToastHostLayout()
    {
        _controls.ToastHost.HorizontalAlignment = HorizontalAlignment.Center;
        _controls.ToastHost.Width = double.NaN;
        _controls.ToastHost.MaxWidth = double.PositiveInfinity;
        _controls.ToastHost.Margin = new Thickness(0, 0, 0, ToastHostBottomMargin);
    }

    private double ResolvePreviewTreePaneProjectedWidth()
    {
        EnsureSavedSplitPaneWidths();

        var workspaceWidth = _controls.WorkspaceGrid.Bounds.Width;
        if (workspaceWidth <= 0.5)
            return SplitTreePaneMinimumWidth;

        var settingsWidth = ShouldShowPreviewSettingsSplitter()
            ? GetVisibleSettingsPanelWidth() + PreviewSettingsSplitterWidth
            : 0.0;
        var availableWorkspaceWidth = Math.Max(0, workspaceWidth - settingsWidth);
        var availableSplitWidth = Math.Max(
            0,
            availableWorkspaceWidth - TreePreviewSplitterWidth);
        if (availableSplitWidth <= 0.5)
            return SplitTreePaneMinimumWidth;

        var treeWeight = IsUsableSplitPaneWidth(_savedSplitTreeColumnWidth)
            ? _savedSplitTreeColumnWidth.Value
            : 5.0;
        var previewWeight = IsUsableSplitPaneWidth(_savedSplitPreviewColumnWidth)
            ? _savedSplitPreviewColumnWidth.Value
            : 6.0;
        var totalWeight = treeWeight + previewWeight;
        if (totalWeight <= 0.001)
            return SplitTreePaneMinimumWidth;

        return GetClampedPreviewTreePaneWidth(availableSplitWidth * (treeWeight / totalWeight));
    }

    private double GetMaximumPreviewTreePaneWidth()
    {
        var availableSplitWidth = GetAvailableSplitWorkspaceWidth();
        return Math.Max(
            SplitTreePaneMinimumWidth,
            availableSplitWidth - SplitPreviewPaneMinimumWidth);
    }

    private double GetAvailableSplitWorkspaceWidth()
    {
        var workspaceWidth = _controls.WorkspaceGrid.Bounds.Width;
        if (workspaceWidth <= 0.5)
            return 0;

        var settingsWidth = ShouldShowPreviewSettingsSplitter()
            ? GetVisibleSettingsPanelWidth() + PreviewSettingsSplitterWidth
            : 0.0;
        var availableWorkspaceWidth = Math.Max(0, workspaceWidth - settingsWidth);
        return Math.Max(0, availableWorkspaceWidth - TreePreviewSplitterWidth);
    }

    public bool HasVisibleSettingsPanelWidth()
        => _controls.SettingsContainer.Width > 0.5 ||
           _controls.SettingsContainer.Bounds.Width > 0.5;

    private double GetMaximumSettingsPanelWidth()
    {
        var workspaceWidth = _controls.WorkspaceGrid.Bounds.Width;
        if (workspaceWidth <= 0)
            return SettingsPanelMaximumWidth;

        var reservedWidth = GetMinimumLeadingWorkspaceWidth() + PreviewSettingsSplitterWidth;
        var availableWidth = Math.Max(0, workspaceWidth - reservedWidth);
        var panelWidthCap = Math.Max(
            _effectiveSettingsPanelMinimumWidth,
            SettingsPanelMaximumWidth);
        return Math.Min(panelWidthCap, availableWidth);
    }

    private double GetMinimumLeadingWorkspaceWidth()
        => GetCurrentDisplayMode() switch
        {
            WorkspaceDisplayMode.PreviewWithTree =>
                SplitTreePaneMinimumWidth +
                SplitPreviewPaneMinimumWidth +
                TreePreviewSplitterWidth,
            WorkspaceDisplayMode.PreviewOnly => SplitPreviewPaneMinimumWidth,
            _ => SplitTreePaneMinimumWidth
        };

    private void ApplySettingsPanelWidth(double width, bool animate)
    {
        if (width > 0.5)
            SetWidthWithoutTransition(_controls.SettingsIsland, width);

        var carrierWidth = width > 0.5
            ? width + PreviewSettingsSplitterWidth
            : 0.0;

        if (animate)
        {
            EnsureSettingsPanelTransitions();
            _controls.SettingsContainer.Width = carrierWidth;
            return;
        }

        SetWidthWithoutTransition(_controls.SettingsContainer, carrierWidth);
    }

    private static double GetSettingsContentWidth(double carrierWidth)
        => Math.Max(0.0, carrierWidth - PreviewSettingsSplitterWidth);

    private void BeginWorkspaceResize(
        Border? splitter,
        PointerPressedEventArgs e,
        WorkspaceResizeTarget target)
    {
        if (splitter is null || !e.GetCurrentPoint(splitter).Properties.IsLeftButtonPressed)
            return;

        if (target == WorkspaceResizeTarget.TreePreview &&
            !_viewModel.IsPreviewTreeVisible)
        {
            return;
        }

        if (target == WorkspaceResizeTarget.PreviewSettings &&
            !ShouldShowPreviewSettingsSplitter())
        {
            return;
        }

        CompleteActiveWorkspaceResize(releasePointer: false);
        _activeResizeTarget = target;
        _activeResizePointer = e.Pointer;
        _lastResizePointerX = e.GetPosition(_controls.WorkspaceGrid).X;
        SetWorkspaceSplitterDraggingState(splitter, isDragging: true);
        e.Pointer.Capture(splitter);
        e.Handled = true;
    }

    private void ResizeTreePreviewPanes(double deltaX)
    {
        var currentWidth = ResolvePreviewTreePaneVisibleWidth();
        var newTreeWidth = GetClampedPreviewTreePaneWidth(currentWidth + deltaX);
        if (Math.Abs(newTreeWidth - currentWidth) < 0.01)
            return;

        _currentPreviewTreePaneWidth = newTreeWidth;
        ApplyPreviewTreePaneWidth(newTreeWidth, animate: false);
        UpdatePreviewToolbarPresentation(forceRefreshContent: false);
        UpdateToastHostLayout();
    }

    private void ResizeSettingsPane(double deltaX)
    {
        if (IsSettingsAnimating)
            return;

        var currentWidth = GetVisibleSettingsPanelWidth();
        var desiredWidth = currentWidth - deltaX;
        var clampedWidth = GetClampedSettingsPanelWidth(desiredWidth);
        if (Math.Abs(clampedWidth - currentWidth) < 0.01)
            return;

        _currentSettingsPanelWidth = clampedWidth;
        if (!_viewModel.IsPreviewMode)
            _savedNonSplitSettingsPanelWidth = clampedWidth;
        ApplySettingsPanelWidth(clampedWidth, animate: false);
        UpdatePreviewToolbarPresentation(forceRefreshContent: false);
        UpdateToastHostLayout();
    }

    private void CompleteActiveWorkspaceResize(bool releasePointer)
    {
        if (_activeResizeTarget == WorkspaceResizeTarget.None)
            return;

        var activeTarget = _activeResizeTarget;
        var activePointer = _activeResizePointer;
        _activeResizeTarget = WorkspaceResizeTarget.None;
        _activeResizePointer = null;
        _lastResizePointerX = 0;

        SetWorkspaceSplitterDraggingState(_controls.TreePreviewSplitter, isDragging: false);
        SetWorkspaceSplitterDraggingState(
            _controls.PreviewSettingsSplitter,
            isDragging: false);

        if (activeTarget == WorkspaceResizeTarget.TreePreview)
        {
            CaptureSplitPaneLayout();
            _controls.TreePaneColumn.Width = GridLength.Auto;
            _controls.PreviewPaneColumn.Width = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            ClampSettingsPanelWidthToAvailableSpace(
                ShouldApplySettingsPanelWidthToVisual());
        }

        if (releasePointer)
            activePointer?.Capture(null);

        UpdatePreviewSettingsSplitterState();
        UpdateAdaptiveWorkspaceChrome();
        ScheduleWorkspaceChromeRefresh();
    }

    private async Task RunSettingsPanelAnimationAsync(bool show)
    {
        IsSettingsAnimating = true;
        try
        {
            await DispatcherTaskSchedulerProvider.YieldAsync(DispatcherPriority.Render);

            EnsureSettingsPanelTransitions();
            _currentSettingsPanelWidth =
                GetClampedSettingsPanelWidth(_currentSettingsPanelWidth);
            var targetVisibleWidth = _currentSettingsPanelWidth;

            // Keep the divider inside the animated carrier until the close frame has
            // completed. Hiding it up front leaves a four-DIP visual gap moving on
            // its own even though the carrier geometry is still correct.
            SetPreviewSettingsSplitterVisibility(true);
            _controls.PreviewSettingsSplitter.IsHitTestVisible = show;

            ApplySettingsPanelWidth(show ? targetVisibleWidth : 0.0, animate: true);
            _controls.SettingsIsland.Opacity = show ? 1.0 : 0.0;
            await WaitForPanelAnimationAsync();
        }
        finally
        {
            IsSettingsAnimating = false;
            UpdatePreviewSettingsSplitterState();
            UpdateAdaptiveWorkspaceChrome();
        }
    }

    private void OnSettingsPanelMinimumWidthChanged(
        object? sender,
        SettingsPanelMinimumWidthChangedEventArgs e)
        => UpdateSettingsPanelMinimumWidth(e.MinimumWidth);

    private void UpdateSettingsPanelMinimumWidth(double minimumWidth)
    {
        var normalizedMinimumWidth = Math.Max(
            SettingsPanelMinimumWidth,
            Math.Ceiling(minimumWidth));
        if (Math.Abs(
                normalizedMinimumWidth - _effectiveSettingsPanelMinimumWidth) < 0.5)
        {
            return;
        }

        _effectiveSettingsPanelMinimumWidth = normalizedMinimumWidth;
        _currentSettingsPanelWidth = Math.Max(
            _currentSettingsPanelWidth,
            _effectiveSettingsPanelMinimumWidth);
        _savedNonSplitSettingsPanelWidth = Math.Max(
            _savedNonSplitSettingsPanelWidth,
            _effectiveSettingsPanelMinimumWidth);

        ClampSettingsPanelWidthToAvailableSpace(
            ShouldApplySettingsPanelWidthToVisual());
        UpdateAdaptiveWorkspaceChrome();
    }

    private void EnsureSettingsPanelTransitions()
    {
        if (_controls.SettingsContainer.Transitions is null)
        {
            _controls.SettingsContainer.Transitions =
            [
                new DoubleTransition
                {
                    Property = Layoutable.WidthProperty,
                    Duration = SettingsPanelAnimationDuration,
                    Easing = new CubicEaseInOut()
                }
            ];
        }

        if (_controls.SettingsIsland.Transitions is null)
        {
            _controls.SettingsIsland.Transitions =
            [
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = SettingsPanelAnimationDuration,
                    Easing = new CubicEaseOut()
                }
            ];
        }
    }

    private static void EnsureWidthTransition(Control control, TimeSpan duration)
    {
        if (control.Transitions is not null)
            return;

        control.Transitions =
        [
            new DoubleTransition
            {
                Property = Layoutable.WidthProperty,
                Duration = duration,
                Easing = new CubicEaseOut()
            }
        ];
    }

    private static void SetWidthWithoutTransition(Control control, double width)
    {
        var cachedTransitions = control.Transitions;
        control.Transitions = null;
        control.Width = width;
        control.Transitions = cachedTransitions;
    }

    private static void SetWorkspacePaneState(
        ColumnDefinition column,
        bool visible,
        GridLength width,
        double minWidth)
    {
        column.MinWidth = visible ? minWidth : 0;
        column.Width = visible ? width : new GridLength(0);
    }

    private void SetTreePreviewSplitterState(bool isVisible)
    {
        SetWidthWithoutTransition(
            _controls.TreePreviewSplitter,
            isVisible ? TreePreviewSplitterWidth : 0.0);
        _controls.TreePreviewSplitter.IsVisible = isVisible;
        _controls.TreePreviewSplitter.IsHitTestVisible = isVisible;
    }

    private static void SetWorkspaceSplitterDraggingState(
        Border splitter,
        bool isDragging)
    {
        if (isDragging)
            splitter.Classes.Add(SplitterDraggingClass);
        else
            splitter.Classes.Remove(SplitterDraggingClass);
    }

    private void ScheduleWorkspaceChromeRefresh()
    {
        if (_workspaceChromeRefreshPending)
            return;

        _workspaceChromeRefreshPending = true;
        _window.Dispatcher.Post(
            () =>
            {
                _workspaceChromeRefreshPending = false;
                _controls.WorkspaceGrid.InvalidateArrange();
                _controls.WorkspaceGrid.InvalidateVisual();
                _controls.TreeIsland.InvalidateVisual();
                _controls.PreviewIsland.InvalidateVisual();
                _controls.SettingsContainer.InvalidateVisual();
                _controls.TreePreviewSplitter.InvalidateVisual();
                _controls.PreviewSettingsSplitter.InvalidateVisual();
                _window.InvalidateVisual();
            },
            DispatcherPriority.Render);
    }

    private static bool IsUsableSplitPaneWidth(GridLength width)
    {
        if (width.IsAuto)
            return false;

        return width.GridUnitType switch
        {
            GridUnitType.Pixel => width.Value > 1,
            GridUnitType.Star => width.Value > 0,
            _ => false
        };
    }

    private void EnsureSavedSplitPaneWidths()
    {
        if (!IsUsableSplitPaneWidth(_savedSplitTreeColumnWidth))
            _savedSplitTreeColumnWidth = new GridLength(5, GridUnitType.Star);

        if (!IsUsableSplitPaneWidth(_savedSplitPreviewColumnWidth))
            _savedSplitPreviewColumnWidth = new GridLength(6, GridUnitType.Star);
    }

    public double ResolvePreviewTreePaneVisibleWidth()
    {
        if (_controls.TreePaneContainer.Width > 0.5)
            return _controls.TreePaneContainer.Width;

        if (_controls.TreePaneContainer.Bounds.Width > 0.5)
            return _controls.TreePaneContainer.Bounds.Width;

        return _controls.TreePaneColumn.ActualWidth > 0.5
            ? _controls.TreePaneColumn.ActualWidth
            : 0;
    }

    private static Task WaitForPanelAnimationAsync()
        => Task.Delay(
            SettingsPanelAnimationDuration +
            UiTimingProfile.AnimationSettleBuffer);

    private enum WorkspaceDisplayMode
    {
        Tree = 0,
        PreviewWithTree = 1,
        PreviewOnly = 2
    }

    private enum WorkspaceResizeTarget
    {
        None = 0,
        TreePreview = 1,
        PreviewSettings = 2
    }

    private enum PreviewToolbarLayoutMode
    {
        Wide = 0,
        Compact = 1,
        Narrow = 2
    }
}
