using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Platform.Storage;
using Avalonia.Rendering.Composition;
using DevProjex.Application.Context;
using DevProjex.Avalonia.Services;
using ThemeEffectMode = DevProjex.Infrastructure.ThemePresets.ThemeEffectMode;

namespace DevProjex.Avalonia;

public partial class MainWindow
{
    private void EnsureAppStateStoresExist()
    {
        try
        {
            // Keep all user-facing state documents present from startup so the app can
            // recover from partial external cleanup without waiting for a later save path.
            _userSettingsStore.EnsureStorageExists();
            _themeSettingsStore.EnsureStorageExists();
            _recentProjectsStore.EnsureStorageExists();
            _projectProfiles.EnsureStorageExists();
        }
        catch
        {
            // Best effort only. Persistence bootstrap must never prevent startup.
        }
    }

    private bool IsSessionMetricsIdle()
        => Volatile.Read(ref _startupSequenceStarted) != 0 &&
           !_viewModel.StatusBusy &&
           !_viewModel.IsSearchInProgress &&
           !_viewModel.IsFilterInProgress &&
           !_viewModel.IsPreviewLoading &&
           !_workspacePresentation.IsSettingsAnimating &&
           !_workspacePresentation.IsPreviewPaneAnimating &&
           !_workspacePresentation.IsTreePaneAnimating &&
           !_searchFilterController.IsAnimating &&
           !_previewWorkspaceController.IsModeSwitchInProgress;

    private void CompleteSessionMetricsRecording()
    {
        var completion = _sessionMetrics.Complete();
        if (completion is null)
            return;

        if (completion.Success)
        {
            Console.Out.WriteLine($"DevProjex session metrics: {completion.NormalizedOutputPath}");
            return;
        }

        Console.Error.WriteLine($"DevProjex: failed to write session metrics to {completion.NormalizedOutputPath}: {completion.ErrorMessage}");
    }

    private static string GetApplicationVersion()
    {
        var assembly = typeof(MainWindow).Assembly;
        return assembly
                   .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                   ?.InformationalVersion
               ?? assembly.GetName().Version?.ToString()
               ?? "unknown";
    }

    private void UpdateDropZoneAnimationState()
    {
        if (_dropZoneContainer is null)
            return;

        // This is a render-lifecycle boundary, not merely a visual class toggle.
        // In v4.9 the class was made permanent in XAML and the method was removed.
        // The hidden drop zone then kept DefaultRenderLoop, Skia, and ANGLE active while
        // the tree or preview workspace was idle. IsVisible alone did not prevent that
        // regression on the affected Windows/Avalonia rendering path.
        //
        // Keep the explicit remove/add symmetry: removal guarantees true project idle,
        // while re-adding preserves the original animation after reset back to drop zone.
        // PlaybackBehavior=OnlyIfVisible in XAML remains an additional safety boundary.
        // Do not bind this state to Window.IsActive. During an external drag the source
        // application can retain activation while this window receives DragEnter/Drop,
        // which would freeze the drop-zone feedback exactly when the user needs it.
        if (_viewModel.IsProjectLoaded)
            _dropZoneContainer.Classes.Remove("drop-zone-animating");
        else
            _dropZoneContainer.Classes.Add("drop-zone-animating");
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        SyncThemeWithSystem();
        _themeBrushCoordinator.ScheduleDynamicThemeBrushUpdate();

        // Defer update to let theme resources settle first.
        Dispatcher.Post(
            RefreshThemeHighlightsForActiveQuery,
            DispatcherPriority.Background);
    }

    private void RefreshThemeHighlightsForActiveQuery()
    {
        // Preserve current highlight precedence: active name filter overrides search query.
        var effectiveQuery = !string.IsNullOrWhiteSpace(_viewModel.NameFilter)
            ? _viewModel.NameFilter
            : _viewModel.SearchQuery;
        _searchFilterController.UpdateHighlights(effectiveQuery);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ActualTransparencyLevelProperty)
        {
            if (_themeEffectRuntimeProbeReady)
                _themeBrushCoordinator.ScheduleActualEffectSynchronization();
            return;
        }

        if (e.Property != BoundsProperty)
            return;

        if (e.NewValue is Rect rect)
            ScheduleWindowBoundsUpdate(rect);
    }

    private void ScheduleWindowBoundsUpdate(Rect bounds)
    {
        _pendingWindowBounds = bounds;
        if (_windowBoundsFramePending)
            return;

        _windowBoundsFramePending = true;
        try
        {
            RequestAnimationFrame(
                _ =>
                {
                    _windowBoundsFramePending = false;
                    if (_windowLifetimeCts is not
                        {
                            IsCancellationRequested: false
                        })
                    {
                        return;
                    }

                    ApplyWindowBoundsUpdate(_pendingWindowBounds);
                });
        }
        catch (InvalidOperationException)
        {
            _windowBoundsFramePending = false;
            ApplyWindowBoundsUpdate(_pendingWindowBounds);
        }
    }

    private void ApplyWindowBoundsUpdate(Rect bounds)
    {
        _viewModel.UpdateHelpPopoverMaxSize(bounds.Size);
        _workspacePresentation.HandleWindowBoundsChanged(bounds.Width);
        if (_metrics.HasStatusMetricsSnapshot && _viewModel.StatusMetricsVisible)
            _metrics.RenderStatusBarMetrics();
        _previewSurfaceController.RefreshSelectionMetricsPresentation();
        if (_viewModel.IsAnyPreviewVisible &&
            !_previewWorkspaceController.IsModeSwitchInProgress)
        {
            UpdatePreviewSegmentThumbPosition(animate: false);
        }
    }

    private void OnPreviewSegmentGridSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!_viewModel.IsAnyPreviewVisible ||
            _previewWorkspaceController.IsModeSwitchInProgress)
            return;

        UpdatePreviewSegmentThumbPosition(animate: false);
    }

    private void OnPreviewBarSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdatePreviewToolbarPresentation(forceRefreshContent: false);
        UpdateToastHostLayout();
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        CancelAllMemoryCleanup();
        _systemDialogActivationTcs?.TrySetResult(true);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_awaitingSystemDialogActivation && _systemDialogActivationTcs is null)
        {
            _systemDialogActivationTcs =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        if (_viewModel.HelpPopoverOpen)
            _viewModel.HelpPopoverOpen = false;
        if (_viewModel.HelpDocsPopoverOpen)
            _viewModel.HelpDocsPopoverOpen = false;

        // Native pickers temporarily deactivate the main window too. Running aggressive cleanup
        // during that handoff makes the dialog open/close path feel heavier than it should.
        if (_awaitingSystemDialogActivation)
            return;

        // Do not use deactivation as a cleanup trigger. Alt-Tab, focus changes and native
        // window-manager handoffs are common interactive paths; forcing Gen2/working-set
        // trimming here saves little and creates avoidable page faults when the user returns.
    }

    private async Task WaitForWindowActivationAfterSystemDialogAsync(CancellationToken cancellationToken = default)
    {
        var activationTcs = _systemDialogActivationTcs;
        _systemDialogActivationTcs = null;
        _awaitingSystemDialogActivation = false;

        if (activationTcs is not null)
        {
            var activationTimeout = UiTimingProfile.Scale(TimeSpan.FromMilliseconds(700));
            await Task.WhenAny(
                activationTcs.Task,
                Task.Delay(activationTimeout, cancellationToken));
        }

        cancellationToken.ThrowIfCancellationRequested();
        await YieldUiAsync(DispatcherPriority.Background);
        cancellationToken.ThrowIfCancellationRequested();
        await YieldUiAsync(DispatcherPriority.Render);
        cancellationToken.ThrowIfCancellationRequested();

        // The first frame after a native picker closes is often the focus/activation handoff frame.
        // Give the window one short extra beat before project-load work starts on the same UI loop.
        await Task.Delay(UiTimingProfile.Scale(TimeSpan.FromMilliseconds(50)), cancellationToken);
    }

    private static async Task YieldProjectLoadStartupFrameAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await YieldUiAsync(DispatcherPriority.Background);
        cancellationToken.ThrowIfCancellationRequested();
        await YieldUiAsync(DispatcherPriority.Render);
    }

    private void ConfigureStartupRevealGateForTheme()
    {
        _startupRevealGateActive =
            _startupBackdropCover is not null &&
            ShouldUseStartupRevealGate(
                OperatingSystem.IsWindows(),
                _viewModel.ActiveThemeEffect) &&
            WindowsDwmWindowCloak.TrySet(this, cloaked: true);
        _startupWindowCloaked = _startupRevealGateActive;
        _startupRevealCompleted = !_startupRevealGateActive;

        if (_startupBackdropCover is null)
            return;

        _startupBackdropCover.Transitions = null;
        _startupBackdropCover.Opacity = _startupRevealGateActive ? 1 : 0;
        _startupBackdropCover.IsVisible = _startupRevealGateActive;
    }

    internal static bool ShouldUseStartupRevealGate(
        bool isWindows,
        ThemeEffectMode effect)
        => isWindows && effect is ThemeEffectMode.Acrylic or ThemeEffectMode.Mica;

    private async Task RevealStartupWindowAfterCompositionWarmupAsync(CancellationToken cancellationToken)
    {
        if (!_startupRevealGateActive || _startupRevealCompleted)
            return;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await YieldUiAsync(DispatcherPriority.Render);
            await WaitForNextAnimationFrameAsync(cancellationToken);
            var visualReadyTask = WaitForStartupVisualTreeReadyAsync(cancellationToken);
            var backdropReadyTask = WaitForStartupBackdropReadinessAsync(cancellationToken);
            await Task.WhenAll(visualReadyTask, backdropReadyTask);
            var firstCompositionRendered =
                await TryWaitForRenderedCompositionBatchAsync(cancellationToken);

            if (await visualReadyTask &&
                await backdropReadyTask &&
                firstCompositionRendered)
            {
                // Both the native material and the complete Avalonia frame are already
                // composed. Remove the opaque fallback while still cloaked, commit that
                // state, then publish the final window atomically without a solid-to-blur
                // transition. Drivers that cannot prove readiness use the safe fade below.
                CompleteStartupRevealGate(animate: false);
                await TryWaitForRenderedCompositionBatchAsync(cancellationToken);
            }
        }
        finally
        {
            ReleaseStartupWindowCloak();
            CompleteStartupRevealGate();
        }
    }

    private void ReleaseStartupWindowCloak()
    {
        if (!_startupWindowCloaked)
            return;

        _startupWindowCloaked = false;
        WindowsDwmWindowCloak.TrySet(this, cloaked: false);
    }

    private async Task<bool> WaitForStartupBackdropReadinessAsync(
        CancellationToken cancellationToken)
    {
        if (IsStartupBackdropReady())
            return true;

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void HandlePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == ActualTransparencyLevelProperty && IsStartupBackdropReady())
                completion.TrySetResult(true);
        }

        PropertyChanged += HandlePropertyChanged;
        try
        {
            if (IsStartupBackdropReady())
                return true;

            // Native backdrop attachment has no synchronous readiness API. Prefer the
            // observable transparency transition and retain 90 ms only as a bounded escape
            // hatch for drivers that never report a usable level.
            var completedTask = await Task.WhenAny(
                completion.Task,
                Task.Delay(StartupBackdropFallbackTimeout, cancellationToken));
            await completedTask;
            cancellationToken.ThrowIfCancellationRequested();
            return ReferenceEquals(completedTask, completion.Task);
        }
        finally
        {
            PropertyChanged -= HandlePropertyChanged;
        }
    }

    private bool IsStartupBackdropReady()
        => ThemeEffectPlatformSupport.ResolveActual(
               _viewModel.ActiveThemeEffect,
               ActualTransparencyLevel) is ThemeEffectMode.Acrylic or ThemeEffectMode.Mica;

    private async Task<bool> WaitForStartupVisualTreeReadyAsync(
        CancellationToken cancellationToken)
    {
        if (IsStartupVisualTreeReady())
            return true;

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void HandleLayoutUpdated(object? sender, EventArgs args)
        {
            if (IsStartupVisualTreeReady())
                completion.TrySetResult(true);
        }

        LayoutUpdated += HandleLayoutUpdated;
        try
        {
            if (IsStartupVisualTreeReady())
                return true;

            var completedTask = await Task.WhenAny(
                completion.Task,
                Task.Delay(StartupVisualReadyTimeout, cancellationToken));
            await completedTask;
            cancellationToken.ThrowIfCancellationRequested();
            return ReferenceEquals(completedTask, completion.Task);
        }
        finally
        {
            LayoutUpdated -= HandleLayoutUpdated;
        }
    }

    private bool IsStartupVisualTreeReady()
        => Bounds.Width > 1 &&
           Bounds.Height > 1 &&
           _topMenuBar is { Bounds.Width: > 1, Bounds.Height: > 1 } &&
           _dropZoneContainer is { Bounds.Width: > 1, Bounds.Height: > 1 };

    private async Task<bool> TryWaitForRenderedCompositionBatchAsync(
        CancellationToken cancellationToken)
    {
        var compositionVisual = ElementComposition.GetElementVisual(this);
        if (compositionVisual is null)
            return false;

        try
        {
            // RequestAnimationFrame is a UI animation tick, not a presentation boundary.
            // CompositionBatch.Rendered is the public Avalonia contract that confirms the
            // fully laid-out visual changes reached the render thread before native reveal.
            var batch = compositionVisual.Compositor.RequestCompositionBatchCommitAsync();
            await batch.Rendered.WaitAsync(
                UiTimingProfile.Scale(TimeSpan.FromMilliseconds(250)),
                cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            // Never strand a startup-cloaked window if a driver stalls rendering.
            return false;
        }
    }

    private async Task WaitForNextAnimationFrameAsync(CancellationToken cancellationToken)
        => await TryWaitForNextAnimationFrameAsync(
            UiTimingProfile.Scale(TimeSpan.FromMilliseconds(250)),
            cancellationToken);

    private async Task<bool> TryWaitForNextAnimationFrameAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            RequestAnimationFrame(_ => completion.TrySetResult(true));
        }
        catch
        {
            // If a platform denies RAF during startup, never keep the main window hidden.
            return false;
        }

        var completedTask = await Task.WhenAny(
            completion.Task,
            Task.Delay(timeout, cancellationToken));
        await completedTask;
        cancellationToken.ThrowIfCancellationRequested();
        return ReferenceEquals(completedTask, completion.Task);
    }

    private void CompleteStartupRevealGate(bool animate = true)
    {
        if (_startupRevealCompleted)
            return;

        _startupRevealCompleted = true;
        _startupRevealGateActive = false;

        if (_startupBackdropCover is null)
            return;

        _startupBackdropCover.Transitions = animate
            ?
            [
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = StartupBackdropRevealDuration,
                    Easing = new CubicEaseOut()
                }
            ]
            : null;
        _startupBackdropCover.Opacity = 0;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _startupSequenceStarted, 1) != 0)
            return;

        Opened -= OnOpened;
        var lifetime = _windowLifetimeCts;
        if (lifetime is null)
            return;

        ObserveDetachedTask(RunStartupAsync(lifetime.Token), "MainWindowStartup");
    }

    private async Task RunStartupAsync(CancellationToken cancellationToken)
    {
        try
        {
            _taskbarProgress.Attach(this);
            UpdateAdaptiveWorkspaceChrome(forcePreviewLabels: true);

            // The IPC registration and compositor warmup are independent. Running them
            // together removes registry/socket IO from the first-visible-frame critical path.
            await Task.WhenAll(
                EnsureDesktopControlServerAsync(cancellationToken),
                RevealStartupWindowAfterCompositionWarmupAsync(cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            _themeEffectRuntimeProbeReady = true;
            _themeBrushCoordinator.ScheduleActualEffectSynchronization();
            StartDeferredAppStateBootstrap(cancellationToken);
            StartDeferredRecentProjectsLoad(cancellationToken);
            ScheduleOptionalFontCatalogLoad();

            if (_startupOptions.StoreScreenshotCapture is { } storeCaptureRequest)
            {
                await RunStoreScreenshotCaptureAsync(
                    storeCaptureRequest,
                    cancellationToken);
                return;
            }

            if (_startupErrors.Count > 0)
            {
                await ShowErrorAsync(string.Join(Environment.NewLine, _startupErrors));
                cancellationToken.ThrowIfCancellationRequested();
            }

            var startupProjectPath =
                await ResolveStartupProjectPathAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(startupProjectPath))
            {
                var startupLoadStopwatch = Stopwatch.StartNew();
                var opened = await TryOpenFolderAsync(startupProjectPath, fromDialog: false);
                startupLoadStopwatch.Stop();
                cancellationToken.ThrowIfCancellationRequested();

                if (opened)
                {
                    await TryApplyStartupSelectionOverridesAsync();
                    if (_desktopStartupRequest is not null &&
                        GetDesktopGitReadinessDiagnostic(_desktopStartupRequest) is
                            { Severity: ContextDiagnosticSeverity.Error } diagnostic)
                    {
                        _desktopStartupErrorCode = diagnostic.Code;
                    }
                    await TryApplyStartupUiOptionsAsync();
                    if (await TryRunStartupUiBenchmarkScriptAsync())
                        return;
                }
                else if (_desktopStartupRequest is not null)
                {
                    _desktopStartupErrorCode = "DPX-DESKTOP-PROJECT-OPEN-FAILED";
                }
            }
            else if (_desktopStartupRequest?.UseLastProject == true)
            {
                _desktopStartupErrorCode = "DPX-DESKTOP-NO-RECENT-PROJECT";
                await ShowInfoAsync(_viewModel.MenuFileRecentEmpty);
            }
            else
            {
                await TryShowAutomaticTerminalCommandPromptAsync(cancellationToken);
            }

            ObserveDetachedTask(
                Task.Run(_repoCacheService.CleanupStaleCacheOnStartup, cancellationToken),
                "CleanupStaleRepositoryCache");
            ObserveDetachedTask(
                _applicationUpdates.RunAutomaticCheckIfDueAsync(cancellationToken),
                "AutomaticUpdateCheck");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Closing the window owns cancellation of the complete startup sequence.
        }
        catch (Exception ex)
        {
            if (_desktopStartupRequest is not null)
                _desktopStartupErrorCode = "DPX-DESKTOP-STARTUP-FAILED";
            if (!cancellationToken.IsCancellationRequested && IsVisible)
                await ShowErrorAsync(ex.Message);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
                _desktopStartupReady = true;
        }
    }

    private void StartDeferredAppStateBootstrap(CancellationToken cancellationToken)
    {
        ObserveDetachedTask(
            Task.Run(EnsureAppStateStoresExist, cancellationToken),
            "EnsureAppStateStoresExist");
    }

    private async Task<string?> ResolveStartupProjectPathAsync(
        CancellationToken cancellationToken)
    {
        if (_startupOptions.EffectiveSessionMetrics.Enabled)
            return _startupOptions.EffectiveSessionMetrics.ProjectPath;

        if (!string.IsNullOrWhiteSpace(_desktopStartupRequest?.ProjectPath))
            return _desktopStartupRequest.ProjectPath;

        if (_desktopStartupRequest?.UseLastProject != true)
            return null;

        await EnsureRecentProjectsLoadedAsync(cancellationToken);

        return await FindFirstExistingDirectoryAsync(
            _recentProjectsDb.RecentFolders.Select(static folder => folder.Path),
            cancellationToken);
    }

    #region Drop Zone Handlers

    private void OnDropZoneClick(object? sender, PointerPressedEventArgs e)
    {
        // Ignore if clicked on the button (button has its own handler)
        if (e.Source is Button) return;

        OnOpenFolder(sender, new RoutedEventArgs());
    }

    private void OnDropZoneDragEnter(object? sender, DragEventArgs e)
    {
        string? folder = null;
        try
        {
            folder = ResolveDropFolderPath(
                e.DataTransfer.TryGetFiles()?
                    .OfType<IStorageFolder>()
                    .Select(static folder => folder.TryGetLocalPath()) ??
                []);
        }
        catch
        {
            // Some platform storage providers can fail while materializing a dragged item.
        }

        _dropZoneAcceptsCurrentDrag = !string.IsNullOrWhiteSpace(folder);
        e.DragEffects = ResolveDropEffect(_dropZoneAcceptsCurrentDrag);

        if (sender is Border border)
        {
            if (_dropZoneAcceptsCurrentDrag)
                border.Classes.Add("drag-over");
            else
                border.Classes.Remove("drag-over");
        }
    }

    private void OnDropZoneDragOver(object? sender, DragEventArgs e)
    {
        // The native no-drop cursor is updated from DragOver, which fires while the pointer moves.
        e.DragEffects = ResolveDropEffect(_dropZoneAcceptsCurrentDrag);
    }

    private void OnDropZoneDragLeave(object? sender, DragEventArgs e)
    {
        _dropZoneAcceptsCurrentDrag = false;

        // Remove visual feedback class
        if (sender is Border border)
        {
            border.Classes.Remove("drag-over");
        }
    }

    private async void OnDropZoneDrop(object? sender, DragEventArgs e)
    {
        _dropZoneAcceptsCurrentDrag = false;

        // Remove visual feedback class
        if (sender is Border border)
        {
            border.Classes.Remove("drag-over");
        }

        try
        {
            var files = e.DataTransfer.TryGetFiles();
            if (files is null) return;

            var folder = ResolveDropFolderPath(
                files
                    .OfType<IStorageFolder>()
                    .Select(static item => item.TryGetLocalPath()));
            e.DragEffects = ResolveDropEffect(!string.IsNullOrWhiteSpace(folder));

            if (!string.IsNullOrWhiteSpace(folder))
            {
                await TryOpenFolderAsync(folder, fromDialog: true);
            }
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    #endregion
}
