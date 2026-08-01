using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media.Imaging;
using DevProjex.Avalonia.Controls;
using DevProjex.Avalonia.Services;

namespace DevProjex.Avalonia.Coordinators;

internal sealed record PreviewWorkspaceControls(
    TreeView TreeView,
    Grid TreePaneRoot,
    Border TreePaneContainer,
    Border TreePaneSnapshotHost,
    Image TreePaneSnapshotImage,
    Border PreviewPaneContainer,
    Grid PreviewPaneSurface,
    ColumnDefinition TreePaneColumn,
    ColumnDefinition PreviewPaneColumn,
    Border TreePreviewSplitter,
    Border PreviewBar,
    Grid PreviewSegmentGrid,
    Border PreviewSegmentThumb,
    Button PreviewTreeModeButton,
    Button PreviewContentModeButton,
    Button PreviewTreeAndContentModeButton,
    ScrollViewer PreviewTextScrollViewer,
    VirtualizedPreviewTextControl PreviewTextControl);

internal sealed class PreviewWorkspaceController : IDisposable
{
    private const double PreviewTreePaneSlideOffset = 32.0;
    private static readonly TimeSpan PreviewSegmentThumbAnimationDuration =
        UiTimingProfile.Scale(TimeSpan.FromMilliseconds(220));
    private static readonly TimeSpan PaneAnimationDuration =
        WorkspacePresentationController.PanelAnimationDuration;

    private readonly Window _window;
    private readonly MainWindowViewModel _viewModel;
    private readonly PreviewWorkspaceControls _controls;
    private readonly WorkspacePresentationController _workspace;
    private readonly SearchFilterInteractionController _searchFilter;
    private readonly PreviewWorkspacePipeline _previewPipeline;
    private readonly Action<bool> _schedulePreviewRefresh;
    private readonly Action _clearPreviewSelectionMetrics;
    private readonly Action _clearPreviewMemory;
    private readonly Action _schedulePreviewMemoryCleanup;
    private readonly Action _cancelPendingMemoryCleanup;
    private readonly Action _updateCompactModeVisualState;
    private readonly TranslateTransform _treePaneSnapshotTransform;
    private readonly TranslateTransform _previewSegmentThumbTransform;

    private CancellationTokenSource? _modeSwitchCts;
    private CancellationTokenSource? _paneAnimationCts;
    private RenderTargetBitmap? _treePaneSnapshotBitmap;
    private int _modeSwitchVersion;
    private bool _previewFontInitialized;
    private bool _isOpeningPreview;
    private bool _closeRequestedDuringOpen;
    private bool _disposed;

    public PreviewWorkspaceController(
        Window window,
        MainWindowViewModel viewModel,
        PreviewWorkspaceControls controls,
        WorkspacePresentationController workspace,
        SearchFilterInteractionController searchFilter,
        PreviewWorkspacePipeline previewPipeline,
        Action<bool> schedulePreviewRefresh,
        Action clearPreviewSelectionMetrics,
        Action clearPreviewMemory,
        Action schedulePreviewMemoryCleanup,
        Action cancelPendingMemoryCleanup,
        Action updateCompactModeVisualState)
    {
        _window = window;
        _viewModel = viewModel;
        _controls = controls;
        _workspace = workspace;
        _searchFilter = searchFilter;
        _previewPipeline = previewPipeline;
        _schedulePreviewRefresh = schedulePreviewRefresh;
        _clearPreviewSelectionMetrics = clearPreviewSelectionMetrics;
        _clearPreviewMemory = clearPreviewMemory;
        _schedulePreviewMemoryCleanup = schedulePreviewMemoryCleanup;
        _cancelPendingMemoryCleanup = cancelPendingMemoryCleanup;
        _updateCompactModeVisualState = updateCompactModeVisualState;

        _treePaneSnapshotTransform =
            controls.TreePaneSnapshotImage.RenderTransform as TranslateTransform
            ?? new TranslateTransform();
        controls.TreePaneSnapshotImage.RenderTransform = _treePaneSnapshotTransform;

        _previewSegmentThumbTransform =
            controls.PreviewSegmentThumb.RenderTransform as TranslateTransform
            ?? new TranslateTransform();
        controls.PreviewSegmentThumb.RenderTransform = _previewSegmentThumbTransform;
        EnsurePreviewSegmentThumbTransitions();
    }

    public bool IsModeSwitchInProgress { get; private set; }

    internal bool WasLastOpenAnimationInterruptedByClose { get; private set; }

    public void UpdatePreviewSegmentThumbPosition(bool animate)
    {
        if (!TryGetPreviewSegmentTarget(out var targetX, out var targetWidth))
            return;

        _controls.PreviewSegmentThumb.Width = targetWidth;
        if (!animate)
        {
            var cachedTransitions = _previewSegmentThumbTransform.Transitions;
            _previewSegmentThumbTransform.Transitions = null;
            _previewSegmentThumbTransform.X = targetX;
            _previewSegmentThumbTransform.Transitions = cachedTransitions;
            EnsurePreviewSegmentThumbTransitions();
            return;
        }

        EnsurePreviewSegmentThumbTransitions();
        _previewSegmentThumbTransform.X = targetX;
    }

    public async Task SwitchModeAsync(PreviewContentMode targetMode)
    {
        if (!_viewModel.CanUseProjectWorkspaceActions ||
            _viewModel.SelectedPreviewContentMode == targetMode)
        {
            return;
        }

        _cancelPendingMemoryCleanup();
        var switchCts = ReplaceModeSwitchCancellation();
        var switchVersion = Interlocked.Increment(ref _modeSwitchVersion);
        var publicationReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        IsModeSwitchInProgress = true;

        try
        {
            _previewPipeline.CancelActiveBuildAndInvalidate();
            _viewModel.SelectedPreviewContentMode = targetMode;
            UpdatePreviewSegmentThumbPosition(animate: true);
            var previewRefreshOperation =
                _previewPipeline.RefreshNowAsync(
                    allowDuringModeSwitch: true,
                    publicationReady: publicationReady.Task);

            await WaitForPanelAnimationAsync(
                PreviewSegmentThumbAnimationDuration,
                switchCts.Token);

            if (switchVersion != Volatile.Read(ref _modeSwitchVersion))
                return;

            IsModeSwitchInProgress = false;
            publicationReady.TrySetResult();
            await previewRefreshOperation.Completion;
            _window.Dispatcher.Post(FocusPreviewSurface, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
            // A newer mode selection owns the preview surface now.
        }
        finally
        {
            publicationReady.TrySetResult();

            if (switchVersion == Volatile.Read(ref _modeSwitchVersion))
                IsModeSwitchInProgress = false;

            if (ReferenceEquals(Interlocked.CompareExchange(
                    ref _modeSwitchCts,
                    null,
                    switchCts), switchCts))
            {
                switchCts.Dispose();
            }
        }
    }

    public async Task OpenAsync()
    {
        if (!_viewModel.IsProjectLoaded ||
            _workspace.IsPreviewPaneAnimating ||
            _workspace.IsTreePaneAnimating)
        {
            return;
        }

        _cancelPendingMemoryCleanup();
        _closeRequestedDuringOpen = false;
        WasLastOpenAnimationInterruptedByClose = false;
        PreparePreviewPane();
        _workspace.CaptureNonSplitSettingsPanelWidth();
        _workspace.ResetSettingsPanelWidthForPreview();
        ResetPreviewTreePaneVisualState();
        CollapsePreviewPaneVisualState();
        _viewModel.SetPreviewCompactModeActive(false);

        var initialTreeWidth = Math.Max(
            WorkspacePresentationController.SplitTreePaneMinimumWidth,
            ResolvePreviewTreePaneVisibleWidth());
        var targetTreeWidth = _workspace.GetClampedPreviewTreePaneWidth(
            WorkspacePresentationController.SplitTreePaneMinimumWidth);
        _workspace.SetCurrentPreviewTreePaneWidth(targetTreeWidth);

        var openCanceled = false;
        var openAnimationStarted = false;
        var openAnimationInterrupted = false;
        var animationCts = ReplacePaneAnimationCancellation();
        var cancellationToken = animationCts.Token;
        _isOpeningPreview = true;
        try
        {
            try
            {
                _viewModel.PreviewWorkspaceMode = PreviewWorkspaceMode.TreeAndPreview;
                cancellationToken.ThrowIfCancellationRequested();
                PreparePreviewPaneOpenLayout(initialTreeWidth);
                UpdatePreviewSegmentThumbPosition(animate: false);
                cancellationToken.ThrowIfCancellationRequested();
                openAnimationStarted = true;
                await AnimatePreviewPaneOpenAsync(
                    targetTreeWidth,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (_closeRequestedDuringOpen || _disposed)
            {
                openAnimationInterrupted = true;
                WasLastOpenAnimationInterruptedByClose =
                    _closeRequestedDuringOpen;
            }

            if (!openAnimationInterrupted && !_closeRequestedDuringOpen)
            {
                _viewModel.SetPreviewCompactModeActive(true);
                _updateCompactModeVisualState();
                await WaitForPreviewRenderPassesAsync();
                _workspace.CaptureSplitPaneLayout();
                _workspace.UpdateWorkspaceLayoutForCurrentMode();
                UpdatePreviewSegmentThumbPosition(animate: false);
            }

            if (!openAnimationInterrupted && !_closeRequestedDuringOpen)
            {
                var previewRefreshOperation = _previewPipeline.RefreshNowAsync();
                try
                {
                    await previewRefreshOperation.FirstContentReady;
                }
                catch (OperationCanceledException)
                {
                    openCanceled = true;
                }
            }
        }
        finally
        {
            CompletePaneAnimationCancellation(animationCts);
            _isOpeningPreview = false;
        }

        if (_disposed)
            return;

        if (openCanceled ||
            openAnimationInterrupted ||
            _closeRequestedDuringOpen)
        {
            _closeRequestedDuringOpen = false;
            if (openAnimationStarted &&
                ResolveRenderedPaneWidth(
                    _controls.PreviewPaneContainer,
                    _workspace.ResolvePreviewPaneVisibleWidth()) > 0.5)
            {
                await CloseAsync();
            }
            else
            {
                await AbortPreviewOpenAsync();
            }
            return;
        }

        _controls.TreeView.Focus();
    }

    public async Task CloseAsync()
    {
        if (_isOpeningPreview)
        {
            _closeRequestedDuringOpen = _viewModel.IsPreviewMode;
            if (_closeRequestedDuringOpen)
            {
                _previewPipeline.CancelRefresh();
                CancelPaneAnimation();
            }
            return;
        }

        if (_workspace.IsPreviewPaneAnimating)
            return;

        if (_workspace.IsTreePaneAnimating)
        {
            return;
        }

        _cancelPendingMemoryCleanup();
        _closeRequestedDuringOpen = false;
        SetPreviewToolbarInteractionSuspended(true);
        try
        {
            var startedFromPreviewOnly = _viewModel.IsPreviewOnlyMode;
            var currentTreeWidth = _viewModel.IsPreviewTreeVisible
                ? ResolveRenderedPaneWidth(
                    _controls.TreePaneContainer,
                    ResolvePreviewTreePaneVisibleWidth())
                : 0.0;

            if (_viewModel.IsPreviewTreeVisible)
                _workspace.CaptureSplitPaneLayout();

            CancelModeSwitch();
            _previewPipeline.CancelRefresh();

            if (startedFromPreviewOnly)
            {
                _viewModel.PreviewWorkspaceMode =
                    PreviewWorkspaceMode.TreeAndPreview;
                _workspace.UpdateWorkspaceLayoutForCurrentMode();
                UpdatePreviewSegmentThumbPosition(animate: false);
            }

            PreparePreviewPaneCloseLayout(currentTreeWidth);
            await AnimatePreviewPaneCloseAsync();

            _viewModel.PreviewWorkspaceMode = PreviewWorkspaceMode.Off;
            _viewModel.SetPreviewCompactModeActive(false);
            _updateCompactModeVisualState();
            _workspace.RestoreNonSplitSettingsPanelWidth();
            _workspace.UpdateWorkspaceLayoutForCurrentMode();
            await WaitForPreviewRenderPassesAsync();
            ResetPreviewTreePaneVisualState();
            CollapsePreviewPaneVisualState();

            if (startedFromPreviewOnly)
                _searchFilter.RestoreAfterPreviewOnly();

            ReleasePreviewDocumentAndScheduleCleanup();
            _controls.TreeView.Focus();
        }
        catch (OperationCanceledException) when (_disposed)
        {
            // Window teardown owns the visual state from this point.
        }
        finally
        {
            if (!_disposed)
                SetPreviewToolbarInteractionSuspended(false);
        }
    }

    public async Task HideTreePaneAsync()
    {
        if (!_viewModel.IsPreviewTreeVisible ||
            _workspace.IsPreviewPaneAnimating ||
            _workspace.IsTreePaneAnimating)
        {
            return;
        }

        SetPreviewToolbarInteractionSuspended(true);
        try
        {
            var shouldResumePreviewRefresh =
                _previewPipeline.SuspendForTreeHide();
            _searchFilter.InvalidateFocusRequests();
            _searchFilter.CancelPending();
            _searchFilter.SuspendForPreviewOnly();
            _workspace.CaptureSplitPaneLayout();
            PreparePreviewTreePaneCollapseLayout();

            await _window.Dispatcher.InvokeAsync(
                static () => { },
                DispatcherPriority.Render);
            TryPreparePreviewTreePaneSnapshot();
            await AnimatePreviewTreePaneHideAsync();

            _viewModel.PreviewWorkspaceMode = PreviewWorkspaceMode.PreviewOnly;
            _workspace.UpdateWorkspaceLayoutForCurrentMode();
            UpdatePreviewSegmentThumbPosition(animate: false);
            ResetPreviewTreePaneVisualState();
            await WaitForPreviewRenderPassesAsync();

            if (shouldResumePreviewRefresh && _viewModel.IsAnyPreviewVisible)
                _schedulePreviewRefresh(true);

            FocusPreviewSurface();
        }
        finally
        {
            SetPreviewToolbarInteractionSuspended(false);
        }
    }

    public void ResetPreviewTreePaneVisualState()
    {
        var cachedContainerTransitions =
            _controls.TreePaneContainer.Transitions;
        _controls.TreePaneContainer.Transitions = null;
        _controls.TreePaneContainer.Transitions = cachedContainerTransitions;
        ResetPreviewTreePaneSnapshotVisualState();
    }

    public void CollapsePreviewPaneVisualState()
    {
        var cachedTransitions = _controls.PreviewPaneContainer.Transitions;
        _controls.PreviewPaneContainer.Transitions = null;
        _controls.PreviewPaneContainer.Width = 0.0;
        _controls.PreviewPaneContainer.Transitions = cachedTransitions;
        ResetPreviewPaneSurfaceWidth();
    }

    public void CancelModeSwitch()
    {
        Interlocked.Increment(ref _modeSwitchVersion);
        var cts = Interlocked.Exchange(ref _modeSwitchCts, null);
        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }

        IsModeSwitchInProgress = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CancelModeSwitch();
        CancelPaneAnimation();
        ResetPreviewTreePaneSnapshotVisualState();
        ResetPreviewPaneSurfaceWidth();
    }

    private async Task AbortPreviewOpenAsync()
    {
        CancelModeSwitch();
        _previewPipeline.CancelRefresh();
        _viewModel.PreviewWorkspaceMode = PreviewWorkspaceMode.Off;
        _viewModel.SetPreviewCompactModeActive(false);
        _updateCompactModeVisualState();
        _workspace.RestoreNonSplitSettingsPanelWidth();
        _workspace.UpdateWorkspaceLayoutForCurrentMode();
        await WaitForPreviewRenderPassesAsync();
        ResetPreviewTreePaneVisualState();
        CollapsePreviewPaneVisualState();
        ReleasePreviewDocumentAndScheduleCleanup();
        _controls.TreeView.Focus();
    }

    private void ReleasePreviewDocumentAndScheduleCleanup()
    {
        _clearPreviewSelectionMetrics();
        _clearPreviewMemory();
        _schedulePreviewMemoryCleanup();
    }

    private void PreparePreviewPane()
    {
        if (_previewFontInitialized)
            return;

        _viewModel.PreviewFontSize = _viewModel.TreeFontSize;
        _previewFontInitialized = true;
    }

    private void EnsurePreviewSegmentThumbTransitions()
    {
        if (_previewSegmentThumbTransform.Transitions is not null)
            return;

        _previewSegmentThumbTransform.Transitions =
        [
            new DoubleTransition
            {
                Property = TranslateTransform.XProperty,
                Duration = PreviewSegmentThumbAnimationDuration,
                Easing = new CubicEaseInOut()
            }
        ];
    }

    private bool TryGetPreviewSegmentTarget(
        out double targetX,
        out double targetWidth)
    {
        var selectedButton = _viewModel.SelectedPreviewContentMode switch
        {
            PreviewContentMode.Tree => _controls.PreviewTreeModeButton,
            PreviewContentMode.Content => _controls.PreviewContentModeButton,
            _ => _controls.PreviewTreeAndContentModeButton
        };

        targetX = selectedButton.Bounds.X;
        targetWidth = selectedButton.Bounds.Width;
        return targetWidth > 0;
    }

    private void PreparePreviewPaneOpenLayout(double initialTreeWidth)
    {
        _controls.TreePaneColumn.MinWidth = 0;
        _controls.TreePaneColumn.Width = GridLength.Auto;
        _controls.PreviewPaneColumn.MinWidth = 0;
        _controls.PreviewPaneColumn.Width =
            new GridLength(1, GridUnitType.Star);
        SetTreePreviewSplitterWidthWithoutTransition(0.0);
        _controls.TreePreviewSplitter.IsVisible = true;
        _controls.TreePreviewSplitter.IsHitTestVisible = false;

        _workspace.ApplyPreviewTreePaneWidth(
            initialTreeWidth,
            animate: false);
        _workspace.ApplyPreviewPaneWidth(double.NaN, animate: false);
        ResetPreviewPaneSurfaceWidth();
    }

    private void PreparePreviewPaneCloseLayout(double currentTreeWidth)
    {
        var previewSurfaceWidth = ResolveRenderedPaneWidth(
            _controls.PreviewPaneContainer,
            _controls.PreviewPaneColumn.ActualWidth);
        var showSplitter = currentTreeWidth > 0.5;
        _controls.TreePaneColumn.MinWidth = 0;
        _controls.TreePaneColumn.Width = GridLength.Auto;
        _controls.PreviewPaneColumn.MinWidth = 0;
        _controls.PreviewPaneColumn.Width =
            new GridLength(1, GridUnitType.Star);
        var renderedSplitterWidth = showSplitter
            ? ResolveRenderedPaneWidth(
                _controls.TreePreviewSplitter,
                WorkspacePresentationController.TreePreviewSplitterWidth)
            : 0.0;
        SetTreePreviewSplitterWidthWithoutTransition(renderedSplitterWidth);
        _controls.TreePreviewSplitter.IsVisible = showSplitter;
        _controls.TreePreviewSplitter.IsHitTestVisible = false;

        _workspace.ApplyPreviewTreePaneWidth(
            currentTreeWidth,
            animate: false);
        _workspace.ApplyPreviewPaneWidth(double.NaN, animate: false);
        FreezePreviewPaneSurfaceWidth(previewSurfaceWidth);
    }

    private async Task AnimatePreviewPaneOpenAsync(
        double targetTreeWidth,
        CancellationToken cancellationToken)
    {
        if (_workspace.IsPreviewPaneAnimating)
            return;

        _workspace.IsPreviewPaneAnimating = true;
        try
        {
            await DispatcherTaskSchedulerProvider.YieldAsync(
                DispatcherPriority.Render);
            cancellationToken.ThrowIfCancellationRequested();

            EnsurePreviewTreePaneTransitions();
            // The tree boundary and its divider must share the same animation clock.
            // Updating either one in a separate layout pass recreates the four-DIP
            // kick that this carrier layout is designed to avoid.
            _controls.TreePaneContainer.Width = targetTreeWidth;
            _controls.TreePreviewSplitter.Width =
                WorkspacePresentationController.TreePreviewSplitterWidth;
            await WaitForPanelAnimationAsync(
                PaneAnimationDuration,
                cancellationToken);

            _workspace.ApplyPreviewTreePaneWidth(
                targetTreeWidth,
                animate: false);
            _workspace.ApplyPreviewPaneWidth(double.NaN, animate: false);
            await DispatcherTaskSchedulerProvider.YieldAsync(
                DispatcherPriority.Render);
        }
        finally
        {
            _workspace.IsPreviewPaneAnimating = false;
            _controls.TreePreviewSplitter.IsHitTestVisible =
                _viewModel.IsPreviewTreeVisible;
        }
    }

    private async Task AnimatePreviewPaneCloseAsync()
    {
        if (_workspace.IsPreviewPaneAnimating)
            return;

        var animationCts = ReplacePaneAnimationCancellation();
        var cancellationToken = animationCts.Token;
        _workspace.IsPreviewPaneAnimating = true;
        try
        {
            await DispatcherTaskSchedulerProvider.YieldAsync(
                DispatcherPriority.Render);
            cancellationToken.ThrowIfCancellationRequested();

            ResetPreviewTreePaneSnapshotVisualState();
            EnsurePreviewTreePaneTransitions();

            var targetTreeWidth = ResolvePreviewCloseTreeWidth();
            _controls.TreePaneContainer.Width = targetTreeWidth;
            await WaitForPanelAnimationAsync(
                PaneAnimationDuration,
                cancellationToken);

            _workspace.ApplyPreviewTreePaneWidth(
                targetTreeWidth,
                animate: false);
            _workspace.ApplyPreviewPaneWidth(double.NaN, animate: false);
            await DispatcherTaskSchedulerProvider.YieldAsync(
                DispatcherPriority.Render);
        }
        finally
        {
            CompletePaneAnimationCancellation(animationCts);
            _workspace.IsPreviewPaneAnimating = false;
            _controls.TreePreviewSplitter.IsHitTestVisible =
                _viewModel.IsPreviewTreeVisible;
        }
    }

    private double ResolvePreviewCloseTreeWidth()
    {
        var allocatedLeadingWidth =
            _controls.TreePaneColumn.ActualWidth +
            _controls.PreviewPaneColumn.ActualWidth;
        if (allocatedLeadingWidth > 0.5)
        {
            return Math.Max(
                WorkspacePresentationController.SplitTreePaneMinimumWidth,
                allocatedLeadingWidth);
        }

        return Math.Max(
            WorkspacePresentationController.SplitTreePaneMinimumWidth,
            _workspace.GetAvailableTreeOnlyWorkspaceWidth());
    }

    private void EnsurePreviewTreePaneTransitions()
    {
        if (_controls.TreePaneContainer.Transitions is null)
        {
            _controls.TreePaneContainer.Transitions =
            [
                new DoubleTransition
                {
                    Property = Layoutable.WidthProperty,
                    Duration = PaneAnimationDuration,
                    Easing = new CubicEaseInOut()
                }
            ];
        }

        if (_controls.TreePreviewSplitter.Transitions is null)
        {
            _controls.TreePreviewSplitter.Transitions =
            [
                new DoubleTransition
                {
                    Property = Layoutable.WidthProperty,
                    Duration = PaneAnimationDuration,
                    Easing = new CubicEaseInOut()
                }
            ];
        }

        if (_controls.TreePaneSnapshotImage.Transitions is null)
        {
            _controls.TreePaneSnapshotImage.Transitions =
            [
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = PaneAnimationDuration,
                    Easing = new CubicEaseInOut()
                }
            ];
        }

        if (_treePaneSnapshotTransform.Transitions is null)
        {
            _treePaneSnapshotTransform.Transitions =
            [
                new DoubleTransition
                {
                    Property = TranslateTransform.XProperty,
                    Duration = PaneAnimationDuration,
                    Easing = new CubicEaseInOut()
                }
            ];
        }
    }

    private async Task AnimatePreviewTreePaneHideAsync()
    {
        if (_workspace.IsTreePaneAnimating)
            return;

        _workspace.IsTreePaneAnimating = true;
        try
        {
            EnsurePreviewTreePaneTransitions();
            _controls.TreePaneContainer.Width = 0;
            _controls.TreePreviewSplitter.Width = 0;

            if (_controls.TreePaneSnapshotHost.IsVisible)
            {
                _controls.TreePaneSnapshotImage.Opacity = 0;
                _treePaneSnapshotTransform.X =
                    -ResolvePreviewTreePaneHiddenOffset();
            }

            await WaitForPanelAnimationAsync(PaneAnimationDuration);
        }
        finally
        {
            _workspace.IsTreePaneAnimating = false;
        }
    }

    private void PreparePreviewTreePaneCollapseLayout()
    {
        var visibleTreeWidth =
            _workspace.ResolvePreviewTreePaneWidthForCollapse();
        _workspace.SetCurrentPreviewTreePaneWidth(visibleTreeWidth);
        _workspace.ApplyPreviewTreePaneWidth(
            visibleTreeWidth,
            animate: false);
        _controls.TreePaneColumn.MinWidth = 0;
        _controls.TreePaneColumn.Width = GridLength.Auto;
        _controls.PreviewPaneColumn.Width =
            new GridLength(1, GridUnitType.Star);
        _controls.PreviewPaneColumn.MinWidth =
            WorkspacePresentationController.SplitPreviewPaneMinimumWidth;
    }

    private double ResolvePreviewTreePaneVisibleWidth()
    {
        if (_controls.TreePaneContainer.Width > 0)
            return _controls.TreePaneContainer.Width;

        if (_controls.TreePaneContainer.Bounds.Width > 0)
            return _controls.TreePaneContainer.Bounds.Width;

        return _controls.TreePaneColumn.ActualWidth > 0
            ? _controls.TreePaneColumn.ActualWidth
            : 0;
    }

    private static double ResolveRenderedPaneWidth(
        Control pane,
        double fallbackWidth)
        => pane.Bounds.Width > 0.5
            ? pane.Bounds.Width
            : Math.Max(0, fallbackWidth);

    private void SetTreePreviewSplitterWidthWithoutTransition(double width)
    {
        var cachedTransitions = _controls.TreePreviewSplitter.Transitions;
        _controls.TreePreviewSplitter.Transitions = null;
        _controls.TreePreviewSplitter.Width = width;
        _controls.TreePreviewSplitter.Transitions = cachedTransitions;
    }

    private void FreezePreviewPaneSurfaceWidth(double width)
    {
        if (width <= 0.5)
            return;

        // Keep live preview content at its pre-close width while the carrier is
        // clipped by the tree expansion. This avoids both repeated reflow and the
        // raster swap that can look like a one-frame zoom on scaled displays.
        _controls.PreviewPaneSurface.HorizontalAlignment = HorizontalAlignment.Left;
        _controls.PreviewPaneSurface.Width = width;
    }

    private void ResetPreviewPaneSurfaceWidth()
    {
        _controls.PreviewPaneSurface.Width = double.NaN;
        _controls.PreviewPaneSurface.HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    private double ResolvePreviewTreePaneHiddenOffset()
    {
        var paneWidth = _controls.TreePaneSnapshotHost.Width > 0.5
            ? _controls.TreePaneSnapshotHost.Width
            : ResolvePreviewTreePaneVisibleWidth();
        if (paneWidth <= 0)
            return PreviewTreePaneSlideOffset;

        return Math.Max(
            PreviewTreePaneSlideOffset,
            Math.Ceiling(Math.Min(paneWidth, 280)));
    }

    private bool TryPreparePreviewTreePaneSnapshot()
    {
        var size = _controls.TreePaneContainer.Bounds.Size;
        if (size.Width <= 0.5 || size.Height <= 0.5)
            return false;

        try
        {
            var renderScaling =
                TopLevel.GetTopLevel(_window)?.RenderScaling ?? 1.0;
            var pixelWidth = Math.Max(
                1,
                (int)Math.Ceiling(size.Width * renderScaling));
            var pixelHeight = Math.Max(
                1,
                (int)Math.Ceiling(size.Height * renderScaling));
            var visualWidth = Math.Ceiling(size.Width);
            var visualHeight = Math.Ceiling(size.Height);

            ResetPreviewTreePaneSnapshotVisualState();
            var bitmap = new RenderTargetBitmap(
                new PixelSize(pixelWidth, pixelHeight),
                new Vector(96 * renderScaling, 96 * renderScaling));
            bitmap.Render(_controls.TreePaneContainer);
            _treePaneSnapshotBitmap = bitmap;

            var cachedImageTransitions =
                _controls.TreePaneSnapshotImage.Transitions;
            var cachedTransformTransitions =
                _treePaneSnapshotTransform.Transitions;
            _controls.TreePaneSnapshotImage.Transitions = null;
            _treePaneSnapshotTransform.Transitions = null;

            _controls.TreePaneSnapshotHost.Width = visualWidth;
            _controls.TreePaneSnapshotHost.Height = visualHeight;
            _controls.TreePaneSnapshotHost.IsVisible = true;
            _controls.TreePaneSnapshotImage.Width = visualWidth;
            _controls.TreePaneSnapshotImage.Height = visualHeight;
            _controls.TreePaneSnapshotImage.Source = bitmap;
            _controls.TreePaneSnapshotImage.Opacity = 1;
            _controls.TreePaneSnapshotImage.IsVisible = true;
            _treePaneSnapshotTransform.X = 0;
            _controls.TreePaneRoot.IsVisible = false;

            _controls.TreePaneSnapshotImage.Transitions =
                cachedImageTransitions;
            _treePaneSnapshotTransform.Transitions =
                cachedTransformTransitions;
            return true;
        }
        catch
        {
            ResetPreviewTreePaneSnapshotVisualState();
            return false;
        }
    }

    private void ResetPreviewTreePaneSnapshotVisualState()
    {
        _controls.TreePaneRoot.IsVisible = true;
        _controls.TreePaneSnapshotHost.IsVisible = false;
        _controls.TreePaneSnapshotHost.Width = double.NaN;
        _controls.TreePaneSnapshotHost.Height = double.NaN;

        var cachedImageTransitions =
            _controls.TreePaneSnapshotImage.Transitions;
        _controls.TreePaneSnapshotImage.Transitions = null;
        _controls.TreePaneSnapshotImage.IsVisible = false;
        _controls.TreePaneSnapshotImage.Width = 0;
        _controls.TreePaneSnapshotImage.Height = 0;
        _controls.TreePaneSnapshotImage.Opacity = 0;
        _controls.TreePaneSnapshotImage.Source = null;
        _controls.TreePaneSnapshotImage.Transitions =
            cachedImageTransitions;

        var cachedTransformTransitions =
            _treePaneSnapshotTransform.Transitions;
        _treePaneSnapshotTransform.Transitions = null;
        _treePaneSnapshotTransform.X = 0;
        _treePaneSnapshotTransform.Transitions =
            cachedTransformTransitions;

        _treePaneSnapshotBitmap?.Dispose();
        _treePaneSnapshotBitmap = null;
    }

    private void SetPreviewToolbarInteractionSuspended(bool suspended)
    {
        _controls.PreviewBar.IsHitTestVisible = !suspended;
        if (suspended)
            _controls.PreviewBar.Classes.Add("preview-toolbar-suspended");
        else
            _controls.PreviewBar.Classes.Remove("preview-toolbar-suspended");
    }

    private void FocusPreviewSurface()
    {
        if (_controls.PreviewTextControl.Focusable)
        {
            _controls.PreviewTextControl.Focus();
            return;
        }

        if (_controls.PreviewTextScrollViewer.Focusable)
        {
            _controls.PreviewTextScrollViewer.Focus();
            return;
        }

        _controls.TreeView.Focus();
    }

    private CancellationTokenSource ReplaceModeSwitchCancellation()
    {
        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _modeSwitchCts, next);
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }

        return next;
    }

    private CancellationTokenSource ReplacePaneAnimationCancellation()
    {
        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _paneAnimationCts, next);
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }

        return next;
    }

    private void CancelPaneAnimation()
    {
        var animationCts = Interlocked.Exchange(ref _paneAnimationCts, null);
        if (animationCts is null)
            return;

        animationCts.Cancel();
        animationCts.Dispose();
    }

    private void CompletePaneAnimationCancellation(
        CancellationTokenSource animationCts)
    {
        if (ReferenceEquals(
                Interlocked.CompareExchange(
                    ref _paneAnimationCts,
                    null,
                    animationCts),
                animationCts))
        {
            animationCts.Dispose();
        }
    }

    private static async Task WaitForPreviewRenderPassesAsync()
    {
        await DispatcherTaskSchedulerProvider.YieldAsync(
            DispatcherPriority.Render);
        await DispatcherTaskSchedulerProvider.YieldAsync(
            DispatcherPriority.Render);
    }

    private static Task WaitForPanelAnimationAsync(TimeSpan duration)
        => Task.Delay(duration + UiTimingProfile.AnimationSettleBuffer);

    private static Task WaitForPanelAnimationAsync(
        TimeSpan duration,
        CancellationToken cancellationToken)
        => Task.Delay(
            duration + UiTimingProfile.AnimationSettleBuffer,
            cancellationToken);
}
