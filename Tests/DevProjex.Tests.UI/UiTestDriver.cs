using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Media;
using DevProjex.Avalonia.Controls;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Application.Context;
using DevProjex.Application.Compression;
using DevProjex.Application.Preview;
using DevProjex.Application.Services;
using DevProjex.Application.Secrets;
using DevProjex.Kernel.Contracts;
using DevProjex.Infrastructure.Git;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace DevProjex.Tests.UI;

internal static class UiTestDriver
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
    private static readonly ConcurrentDictionary<Window, byte> TrackedWindows = new();
    private static readonly ConcurrentDictionary<MainWindow, string> WindowAppDataPaths = new();
    private static readonly bool FastTimingsEnabled =
        string.Equals(Environment.GetEnvironmentVariable("DEVPROJEX_FAST_UI_TESTS"), "1", StringComparison.Ordinal);
    private static readonly TimeSpan PollDelay = FastTimingsEnabled ? TimeSpan.FromMilliseconds(1) : TimeSpan.FromMilliseconds(15);
    private static readonly TimeSpan FrameDelay = FastTimingsEnabled ? TimeSpan.FromMilliseconds(1) : TimeSpan.FromMilliseconds(6);
    private const double FastSettledFrameScale = 0.25;

    public static async Task<MainWindow> CreateLoadedMainWindowAsync(
        UiTestProject project,
        bool waitForInitialSettingsPane = true,
        string? appDataPathOverride = null,
        Func<AvaloniaAppServices, AvaloniaAppServices>? configureServices = null,
        bool waitForStatusIdle = true,
        ProjectSourceType projectSourceType = ProjectSourceType.LocalFolder,
        string? managedClonePath = null,
        string? repositoryUrl = null,
        SessionMetricsOptions? sessionMetrics = null)
    {
        var options = new DesktopStartupOptions(
            new DesktopOpenRequest(project.RootPath, Language: AppLanguage.En),
            sessionMetrics);
        var appDataPath = appDataPathOverride ?? Path.Combine(project.AppDataPath, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appDataPath);

        var services = AvaloniaCompositionRoot.CreateDefault(options, () => appDataPath);
		services = services with
		{
			RepoCacheService = new RepoCacheService(Path.Combine(appDataPath, "RepoCache"))
		};
        if (configureServices is not null)
            services = configureServices(services);
        var window = new MainWindow(options, services)
        {
            Width = 1500,
            Height = 920
        };

        if (projectSourceType == ProjectSourceType.GitClone)
        {
            var clonePath = managedClonePath ?? project.RootPath;
            var viewModel = GetViewModel(window);
            viewModel.ProjectSourceType = ProjectSourceType.GitClone;
            viewModel.CurrentBranch = "main";
            SetRequiredPrivateField(window, "_currentCachedRepoPath", clonePath);
            SetRequiredPrivateField(window, "_currentRepositoryUrl", repositoryUrl);
        }

        TrackTopLevelWindow(window);
        WindowAppDataPaths[window] = appDataPath;

        window.Show();

        await WaitForConditionAsync(
            window,
            () => window.IsVisible,
            "main window to become visible");

        await WaitForConditionAsync(
            window,
            () =>
            {
                var viewModel = GetViewModel(window);
                return viewModel.IsProjectLoaded &&
                       viewModel.TreeNodes.Count > 0 &&
                       (!waitForStatusIdle || !viewModel.StatusBusy);
            },
            waitForStatusIdle
                ? "project to finish loading"
                : "project tree to become available before background metrics finish");

        if (waitForStatusIdle)
            await WaitForSelectionRefreshIdleAsync(window);

        if (waitForInitialSettingsPane)
        {
            await WaitForConditionAsync(
                window,
                () =>
                {
                    var viewModel = GetViewModel(window);
                    if (!viewModel.SettingsVisible)
                        return true;

                    var settingsContainer = GetRequiredControl<Border>(window, "SettingsContainer");
                    return GetActualWidth(settingsContainer) >= 200 &&
                           !GetWorkspacePresentationController(window).IsSettingsAnimating;
                },
                "initial settings pane to become visually available");
        }

        await WaitForSettledFramesAsync(frameCount: 24);
        return window;
    }

    public static async Task CloseWindowAsync(MainWindow window, bool cleanupAppData = true)
    {
        if (!window.IsVisible)
        {
            UntrackTopLevelWindow(window);
            if (cleanupAppData)
                CleanupWindowAppData(window);
            else
                WindowAppDataPaths.TryRemove(window, out _);
            return;
        }

        // Closing immediately after section mutations can leave coalesced refresh tasks
        // queued on the dispatcher. Drain the public idle contract first so headless test
        // teardown does not race app work that would still be running for a real user.
        await WaitForSelectionRefreshIdleAsync(window, TimeSpan.FromSeconds(10));
        window.Close();
        await window.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForSettledFramesAsync(frameCount: 2);
        UntrackTopLevelWindow(window);
        if (cleanupAppData)
            CleanupWindowAppData(window);
        else
            WindowAppDataPaths.TryRemove(window, out _);
    }

    public static async Task CloseTopLevelWindowAsync(Window window)
    {
        if (window.IsVisible)
            window.Close();

        await WaitForSettledFramesAsync(frameCount: 6);
        UntrackTopLevelWindow(window);
    }

    public static void TrackTopLevelWindow(Window window)
    {
        if (TrackedWindows.TryAdd(window, 0))
            window.Closed += OnTrackedWindowClosed;
    }

    public static void CleanupHeadlessState()
    {
        try
        {
            var dispatcher = Dispatcher.UIThread;
            if (dispatcher.CheckAccess())
                CleanupHeadlessStateOnUiThread();
            else
                dispatcher.InvokeAsync(CleanupHeadlessStateOnUiThread, DispatcherPriority.Send).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("idle test context", StringComparison.OrdinalIgnoreCase))
        {
            // Avalonia may already be tearing down the current headless context.
            // In that case there is no UI state left for our safety cleanup to own.
        }
        finally
        {
            CleanupTrackedAppData();
            TrackedWindows.Clear();
        }
    }

    public static int TrackedWindowCount => TrackedWindows.Count;

    private static void CleanupHeadlessStateOnUiThread()
    {
        foreach (var window in TrackedWindows.Keys.ToArray())
        {
            if (window.IsVisible)
                window.Close();

            UntrackTopLevelWindow(window);
        }

        for (var frame = 0; frame < 4; frame++)
            PumpHeadlessFrame(Dispatcher.UIThread);
    }

    private static void OnTrackedWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not Window window)
            return;

        UntrackTopLevelWindow(window);
    }

    private static void UntrackTopLevelWindow(Window window)
    {
        if (TrackedWindows.TryRemove(window, out _))
            window.Closed -= OnTrackedWindowClosed;
    }

    public static async Task OpenFolderAsync(
        MainWindow window,
        string path,
        bool fromDialog = true,
        bool recordRecentFolder = true)
    {
        var method = typeof(MainWindow).GetMethod("TryOpenFolderAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = await window.Dispatcher.InvokeAsync<Task>(() =>
        {
            var result = method!.Invoke(window, [path, fromDialog, recordRecentFolder, null]);
            return Assert.IsAssignableFrom<Task>(result);
        }, DispatcherPriority.Normal);
        await task;
        await WaitForSelectionRefreshIdleAsync(window);
    }

    public static async Task RefreshProjectAsync(MainWindow window)
    {
        var method = typeof(MainWindow).GetMethod("OnRefresh", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        await window.Dispatcher.InvokeAsync(() =>
        {
            method!.Invoke(window, [window, new RoutedEventArgs()]);
        }, DispatcherPriority.Normal);

        // OnRefresh is an async-void UI event handler. Waiting through the same public
        // idle contract used by real interactions makes the test exercise the full refresh
        // pipeline without relying on implementation-specific task handles.
        await WaitForSelectionRefreshIdleAsync(window, TimeSpan.FromSeconds(40));
    }

    public static MainWindowViewModel GetViewModel(MainWindow window)
        => Assert.IsType<MainWindowViewModel>(window.DataContext);

    public static T GetRequiredControl<T>(MainWindow window, string name)
        where T : Control
    {
        var control = window.FindControl<T>(name) ??
                      window.GetVisualDescendants()
                          .OfType<T>()
                          .FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
        return Assert.IsType<T>(control);
    }

    public static T GetRequiredTopMenuControl<T>(MainWindow window, string name)
        where T : Control
    {
        var topMenuBar = GetRequiredControl<TopMenuBarView>(window, "TopMenuBar");
        var control = topMenuBar.FindControl<T>(name);
        return Assert.IsType<T>(control);
    }

    public static CheckBox GetRequiredIgnoreOptionCheckBox(MainWindow window, IgnoreOptionId optionId)
    {
        var checkBox = FindIgnoreOptionCheckBox(window, optionId);

        return Assert.IsType<CheckBox>(checkBox);
    }

    public static CheckBox GetRequiredExtensionCheckBox(MainWindow window, string extensionName)
    {
        var checkBox = FindExtensionCheckBox(window, extensionName);

        return Assert.IsType<CheckBox>(checkBox);
    }

    public static async Task ClickExtensionCheckBoxAsync(MainWindow window, string extensionName)
    {
        await ScrollSettingsItemIntoViewAsync(
            window,
            "ExtensionsList",
            GetViewModel(window).Extensions.FirstOrDefault(option =>
                string.Equals(option.Name, extensionName, StringComparison.Ordinal)));
        await ClickResolvedControlAsync(
            window,
            () => FindExtensionCheckBox(window, extensionName),
            $"extension checkbox '{extensionName}'");
    }

    public static async Task ClickIgnoreOptionCheckBoxAsync(MainWindow window, IgnoreOptionId optionId)
    {
        if (optionId != IgnoreOptionId.HideSecrets)
        {
            await ScrollSettingsItemIntoViewAsync(
                window,
                "IgnoreOptionsList",
                GetViewModel(window).IgnoreOptions.FirstOrDefault(option => option.Id == optionId));
        }
        await ClickResolvedControlAsync(
            window,
            () => FindIgnoreOptionCheckBox(window, optionId),
            $"ignore checkbox '{optionId}'");
    }

    public static async Task ClickApplySettingsAsync(MainWindow window)
    {
        var previousApplyTask = window.LatestApplySettingsTask;
        // Selection workflow tests own the routed Apply contract; pointer hit-testing is
        // covered separately. Raising Button.Click avoids coupling every semantic matrix
        // to stale headless pointer capture from an unrelated test window.
        await RaiseButtonClickAsync(GetRequiredApplySettingsButton(window));
        await WaitForConditionAsync(
            window,
            () => !ReferenceEquals(window.LatestApplySettingsTask, previousApplyTask),
            "the routed Apply command to publish its owned operation");
        await window.LatestApplySettingsTask.WaitAsync(TimeSpan.FromSeconds(30));
        await WaitForSelectionRefreshIdleAsync(window);
    }

    public static async Task RaiseButtonClickAsync(Button button)
    {
        Assert.True(button.IsEnabled);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForSettledFramesAsync(frameCount: 4);
    }

    public static async Task RaiseMenuItemClickAsync(MenuItem menuItem)
    {
        menuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await WaitForSettledFramesAsync(frameCount: 4);
    }

    public static async Task<GitCloneWindow> OpenGitCloneWindowAsync(MainWindow window)
    {
        var method = typeof(MainWindow).GetMethod(
            "OnGitClone",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        await window.Dispatcher.InvokeAsync(() =>
            method!.Invoke(window, [window, new RoutedEventArgs()]));

        GitCloneWindow? cloneWindow = null;
        await WaitForConditionAsync(
            window,
            () =>
            {
                var field = typeof(MainWindow).GetField(
                    "_gitCloneWindow",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                cloneWindow = field?.GetValue(window) as GitCloneWindow;
                return cloneWindow is not null;
            },
            "git clone window to be created");
        TrackTopLevelWindow(cloneWindow!);

        await WaitForConditionAsync(
            window,
            () => cloneWindow!.IsVisible,
            "git clone window to open");

        await WaitForSettledFramesAsync(frameCount: 4);
        return cloneWindow!;
    }

    public static Button GetRequiredApplySettingsButton(MainWindow window)
    {
        var viewModel = GetViewModel(window);
        var button = window
            .GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(control => control.IsVisible &&
                                       string.Equals(control.Content?.ToString(), viewModel.SettingsApply, StringComparison.Ordinal));

        return Assert.IsType<Button>(button);
    }

    public static Button GetRequiredStatusCancelButton(MainWindow window)
    {
        var button = window
            .GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(control => control.IsVisible && control.Classes.Contains("status-cancel"));

        return Assert.IsType<Button>(button);
    }

    public static async Task ClickAsync(MainWindow window, Control control)
    {
        var inputRoot = TopLevel.GetTopLevel(control) ?? window;
        await EnsureControlVisibleAsync(window, control);
        await WaitForControlReadyForPointerAsync(window, inputRoot, control);

        await ClickReadyControlAsync(inputRoot, control);
	}

    public static async Task OpenToolTipThroughClickAsync(MainWindow window, Control control)
    {
        var inputRoot = TopLevel.GetTopLevel(control) ?? window;
        await EnsureControlVisibleAsync(window, control);
        await WaitForControlReadyForPointerAsync(window, inputRoot, control);
        var clickPoint = FindPointerHitPoint(inputRoot, control);

        inputRoot.MouseMove(clickPoint, RawInputModifiers.None);
        await WaitForSettledFramesAsync(frameCount: 2);
        inputRoot.MouseDown(clickPoint, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        inputRoot.MouseUp(clickPoint, MouseButton.Left, RawInputModifiers.None);
        await WaitForConditionAsync(
            window,
            () => ToolTip.GetIsOpen(control),
            $"the tooltip for '{GetControlDebugName(control)}' to open through pointer input");
    }

    public static async Task OpenToolTipThroughPointerAsync(MainWindow window, Control control)
    {
        var inputRoot = TopLevel.GetTopLevel(control) ?? window;
        await EnsureControlVisibleAsync(window, control);
        await WaitForControlReadyForPointerAsync(window, inputRoot, control);
        inputRoot.MouseMove(FindPointerHitPoint(inputRoot, control), RawInputModifiers.None);
        await WaitForConditionAsync(
            window,
            () => ToolTip.GetIsOpen(control),
            $"the tooltip for '{GetControlDebugName(control)}' to open through pointer hover");
    }

    private static Point FindPointerHitPoint(TopLevel inputRoot, Control control)
    {
        ReadOnlySpan<double> relativeCoordinates = [0.5, 0.25, 0.75, 0.125, 0.875];
        foreach (var relativeY in relativeCoordinates)
        {
            foreach (var relativeX in relativeCoordinates)
            {
                var point = control.TranslatePoint(
                    new Point(control.Bounds.Width * relativeX, control.Bounds.Height * relativeY),
                    inputRoot);
                if (point is not { } candidate)
                    continue;

                var hit = inputRoot.InputHitTest(candidate);
                if (hit is Visual visual &&
                    (ReferenceEquals(visual, control) || visual.GetVisualAncestors().Contains(control)))
                {
                    return candidate;
                }
            }
        }

        var center = GetControlCenter(control, inputRoot);
        var centerHit = inputRoot.InputHitTest(center);
        throw new XunitException(
            $"No pointer-hit location was found for '{GetControlDebugName(control)}'. " +
            $"Center hit: '{centerHit?.GetType().FullName ?? "none"}'.");
    }

    private static async Task ClickReadyControlAsync(TopLevel inputRoot, Control control)
    {
        var clickPoint = GetControlCenter(control, inputRoot);
        inputRoot.MouseMove(clickPoint, RawInputModifiers.None);
        await WaitForSettledFramesAsync(frameCount: 2);
        inputRoot.MouseDown(clickPoint, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        inputRoot.MouseUp(clickPoint, MouseButton.Left, RawInputModifiers.None);
        await WaitForSettledFramesAsync(frameCount: 4);
    }

    public static async Task PressExtendedMouseButtonAsync(
        MainWindow window,
        Control control,
        MouseButton button)
    {
        var pressedModifier = button switch
        {
            MouseButton.XButton1 => RawInputModifiers.XButton1MouseButton,
            MouseButton.XButton2 => RawInputModifiers.XButton2MouseButton,
            _ => throw new ArgumentOutOfRangeException(
                nameof(button),
                button,
                "An extended mouse button is required.")
        };

        await EnsureControlVisibleAsync(window, control);
        await WaitForControlReadyForPointerAsync(window, control);
        var point = FindPointerHitPoint(window, control);
        window.MouseMove(point, RawInputModifiers.None);
        await WaitForSettledFramesAsync(frameCount: 1);
        window.MouseDown(point, button, pressedModifier);
        window.MouseUp(point, button, RawInputModifiers.None);
        await WaitForSettledFramesAsync(frameCount: 4);
    }

    public static async Task DoubleClickAsync(MainWindow window, Control control)
    {
        var clickPoint = GetControlCenter(control, window);
        window.MouseMove(clickPoint, RawInputModifiers.None);
        await WaitForSettledFramesAsync(frameCount: 1);

        window.MouseDown(clickPoint, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseUp(clickPoint, MouseButton.Left, RawInputModifiers.None);
        await WaitForSettledFramesAsync(frameCount: 1);

        window.MouseDown(clickPoint, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseUp(clickPoint, MouseButton.Left, RawInputModifiers.None);
        await WaitForSettledFramesAsync(frameCount: 4);
    }

    public static async Task DragAsync(MainWindow window, Control control, double deltaX)
    {
        var startPoint = GetControlCenter(control, window);
        var endPoint = new Point(startPoint.X + deltaX, startPoint.Y);
        window.MouseMove(startPoint, RawInputModifiers.None);
        await WaitForSettledFramesAsync(frameCount: 1);
        window.MouseDown(startPoint, MouseButton.Left, RawInputModifiers.LeftMouseButton);

        const int steps = 6;
        for (var step = 1; step <= steps; step++)
        {
            var t = step / (double)steps;
            var intermediatePoint = new Point(
                startPoint.X + ((endPoint.X - startPoint.X) * t),
                startPoint.Y);
            window.MouseMove(intermediatePoint, RawInputModifiers.LeftMouseButton);
            await WaitForSettledFramesAsync(frameCount: 1);
        }

        window.MouseUp(endPoint, MouseButton.Left, RawInputModifiers.None);
        await WaitForSettledFramesAsync(frameCount: 12);
    }

    public static async Task PressKeyAsync(TopLevel inputRoot, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var physicalKey = key switch
        {
            Key.F => PhysicalKey.F,
            Key.B => PhysicalKey.B,
            Key.P => PhysicalKey.P,
            Key.N => PhysicalKey.N,
			Key.Enter => PhysicalKey.Enter,
			Key.Down => PhysicalKey.ArrowDown,
			Key.Up => PhysicalKey.ArrowUp,
            Key.Escape => PhysicalKey.Escape,
            Key.Space => PhysicalKey.Space,
            Key.D0 => PhysicalKey.Digit0,
            _ => PhysicalKey.None
        };
        var keySymbol = physicalKey.ToQwertyKeySymbol(modifiers.HasFlag(RawInputModifiers.Shift));
        var inputWindow = inputRoot as Window;
        var windowClosed = false;
        EventHandler? closedHandler = null;
        if (inputWindow is not null)
        {
            closedHandler = (_, _) => windowClosed = true;
            inputWindow.Closed += closedHandler;
        }

        try
        {
            inputRoot.KeyPress(key, modifiers, physicalKey, keySymbol);
            await WaitForSettledFramesAsync(frameCount: 1);
            if (!windowClosed && inputRoot.IsVisible)
            {
                try
                {
                    inputRoot.KeyRelease(key, modifiers, physicalKey, keySymbol);
                }
                catch (ObjectDisposedException) when (inputWindow is not null)
                {
                    // Enter may synchronously destroy a dialog before Avalonia raises Closed.
                    // A real input source cannot deliver the matching release to that top-level.
                }
            }
        }
        finally
        {
            if (inputWindow is not null && closedHandler is not null)
                inputWindow.Closed -= closedHandler;
        }

        await WaitForSettledFramesAsync(frameCount: 3);
    }

    public static async Task EnterTextAsync(MainWindow window, TextBox textBox, string text)
    {
        await ClickAsync(window, textBox);
		var inputRoot = TopLevel.GetTopLevel(textBox) ?? window;
		inputRoot.KeyTextInput(text);
        await WaitForSettledFramesAsync(frameCount: 4);
    }

    public static async Task OpenPreviewAsync(MainWindow window)
    {
        var previewToggleButton = GetRequiredTopMenuControl<Button>(window, "PreviewToggleButton");
        await ClickAsync(window, previewToggleButton);
        await WaitForPreviewReadyAsync(window);
    }

    public static async Task TogglePreviewViaToolbarAsync(MainWindow window)
    {
        var previewToggleButton = GetRequiredTopMenuControl<Button>(window, "PreviewToggleButton");
        await ClickAsync(window, previewToggleButton);
    }

    public static async Task ClosePreviewAsync(MainWindow window)
    {
        var previewCloseButton = GetRequiredControl<Button>(window, "PreviewCloseButton");
        await ClickAsync(window, previewCloseButton);
        await WaitForPreviewClosedAsync(window);
    }

    public static async Task ClickPreviewCopyButtonAsync(MainWindow window)
    {
        var previewCopyButton = GetRequiredControl<Button>(window, "PreviewCopyButton");
        await ClickAsync(window, previewCopyButton);
    }

    public static async Task CopyContentToClipboardAsync(MainWindow window, string expectedContent)
        => await InvokeClipboardActionAsync(window, "OnCopyContent", expectedContent);

    public static async Task CopyTreeToClipboardAsync(MainWindow window, string expectedContent)
        => await InvokeClipboardActionAsync(window, "OnCopyTree", expectedContent);

    public static async Task CopyTreeAndContentToClipboardAsync(MainWindow window, string expectedContent)
        => await InvokeClipboardActionAsync(window, "OnCopyTreeAndContent", expectedContent);

    private static async Task InvokeClipboardActionAsync(
        MainWindow window,
        string methodName,
        string expectedContent)
    {
        var method = typeof(MainWindow).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        await SetClipboardTextAsync(window, $"copy-content-pending-{Guid.NewGuid():N}");
        await window.Dispatcher.InvokeAsync(
            () => method!.Invoke(window, [window, new RoutedEventArgs()]),
            DispatcherPriority.Normal);

        await WaitForClipboardTextAsync(window, expectedContent, TimeSpan.FromSeconds(10));
        await WaitForConditionAsync(
            window,
            () => !GetViewModel(window).StatusBusy,
            $"{methodName} operation to finish",
            timeout: TimeSpan.FromSeconds(10));
    }

    public static async Task ClickPreviewStickyHeaderCopyButtonAsync(MainWindow window)
    {
        var stickyHeaderCopyButton = GetRequiredControl<Button>(window, "PreviewStickyHeaderCopyButton");
        await ClickAsync(window, stickyHeaderCopyButton);
    }

    public static async Task WaitForPreviewClosedAsync(MainWindow window)
    {
        await WaitForConditionAsync(
            window,
            () =>
            {
                var viewModel = GetViewModel(window);
                var previewIsland = GetRequiredControl<Border>(window, "PreviewIsland");
                return !viewModel.IsPreviewMode && !previewIsland.IsVisible;
            },
            "preview workspace to close");
        await WaitForSettledFramesAsync(frameCount: 18);
    }

    public static async Task HidePreviewTreeAsync(MainWindow window)
    {
        var treeHideButton = GetRequiredControl<Button>(window, "PreviewTreeHideButton");
        await ClickAsync(window, treeHideButton);
        await WaitForConditionAsync(
            window,
            () =>
            {
                var viewModel = GetViewModel(window);
                var treeIsland = GetRequiredControl<Border>(window, "TreeIsland");
                return viewModel.IsPreviewOnlyMode && !treeIsland.IsVisible;
            },
            "preview tree pane to collapse");
        await WaitForSettledFramesAsync(frameCount: 18);
    }

    public static async Task OpenFilterAsync(MainWindow window)
    {
        var filterToggleButton = GetRequiredTopMenuControl<Button>(window, "FilterToggleButton");
        await ClickAsync(window, filterToggleButton);
        await WaitForConditionAsync(
            window,
            () =>
            {
                var viewModel = GetViewModel(window);
                var filterBarContainer = GetRequiredControl<Border>(window, "FilterBarContainer");
                return viewModel.FilterVisible && filterBarContainer.IsVisible;
            },
            "filter bar to open");
        await WaitForSettledFramesAsync(frameCount: 10);
    }

    public static async Task OpenSearchAsync(MainWindow window)
    {
        await PressKeyAsync(window, Key.F, RawInputModifiers.Control);
        await WaitForConditionAsync(
            window,
            () =>
            {
                var viewModel = GetViewModel(window);
                var searchBarContainer = GetRequiredControl<Border>(window, "SearchBarContainer");
                return viewModel.SearchVisible && searchBarContainer.IsVisible;
            },
            "search bar to open");
        await WaitForSettledFramesAsync(frameCount: 10);
    }

    public static async Task WaitForSettingsVisibilityAsync(MainWindow window, bool visible)
    {
        await WaitForConditionAsync(
            window,
            () =>
            {
                var viewModel = GetViewModel(window);
                var settingsContainer = GetRequiredControl<Border>(window, "SettingsContainer");
                var isEffectivelyVisible = IsActuallyVisibleHorizontally(settingsContainer);
                var settingsAnimating =
                    GetWorkspacePresentationController(window).IsSettingsAnimating;

                return viewModel.SettingsVisible == visible &&
                       isEffectivelyVisible == visible &&
                       !settingsAnimating;
            },
            $"settings visibility to become {visible}");

        await WaitForSettledFramesAsync(frameCount: 12);
    }

    public static async Task WaitForIgnoreOptionStateAsync(
        MainWindow window,
        IgnoreOptionId optionId,
        bool visible,
        bool? isChecked = null)
    {
        await WaitForConditionAsync(
            window,
            () =>
            {
                var option = GetViewModel(window).IgnoreOptions.FirstOrDefault(item => item.Id == optionId);
                if (!visible)
                    return option is null;

                if (option is null)
                    return false;

                return isChecked is null || option.IsChecked == isChecked.Value;
            },
            $"ignore option {optionId} to become visible={visible} checked={isChecked?.ToString() ?? "<any>"}");

        await WaitForSettledFramesAsync(frameCount: 8);
    }

    public static async Task WaitForIgnoreOptionLabelAsync(
        MainWindow window,
        IgnoreOptionId optionId,
        string expectedLabel)
    {
        await WaitForConditionAsync(
            window,
            () =>
            {
                var option = GetViewModel(window).IgnoreOptions.FirstOrDefault(item => item.Id == optionId);
                return option is not null &&
                       string.Equals(option.Label, expectedLabel, StringComparison.Ordinal);
            },
            $"ignore option {optionId} label to become '{expectedLabel}'");

        await WaitForSettledFramesAsync(frameCount: 8);
    }

    public static async Task WaitForStatusMetricsAsync(
        MainWindow window,
        ExportOutputMetrics expectedTreeMetrics,
        ExportOutputMetrics expectedContentMetrics,
        bool waitForSelectionRefreshIdle = true)
    {
        if (waitForSelectionRefreshIdle)
            await WaitForSelectionRefreshIdleAsync(window, TimeSpan.FromSeconds(30));

        await WaitForStatusMetricsReadyAsync(window);

        await WaitForConditionAsync(
            window,
            () =>
            {
                var viewModel = GetViewModel(window);
                if (!viewModel.StatusMetricsVisible ||
                    string.IsNullOrWhiteSpace(viewModel.StatusTreeStatsText) ||
                    string.IsNullOrWhiteSpace(viewModel.StatusContentStatsText))
                {
                    return false;
                }

                return TryParseStatusMetrics(viewModel.StatusTreeStatsText, out var actualTreeMetrics) &&
                       TryParseStatusMetrics(viewModel.StatusContentStatsText, out var actualContentMetrics) &&
                       actualTreeMetrics == expectedTreeMetrics &&
                       actualContentMetrics == expectedContentMetrics;
            },
            $"status metrics to match the expected applied export snapshot (expected tree={expectedTreeMetrics}, expected content={expectedContentMetrics})",
            timeout: TimeSpan.FromSeconds(30));

        await WaitForSettledFramesAsync(frameCount: 12);
    }

    public static async Task<ProjectLoadWorkflowRuntime.ProjectLoadWorkflowMetrics> ComputeAppliedExportMetricsAsync(
        MainWindow window,
        CancellationToken cancellationToken = default)
    {
		await WaitForSelectionRefreshIdleAsync(window);

		var pipeline = GetRequiredPrivateField<ProjectTextOutputPipeline>(window, "_textOutputPipeline");
		var snapshot = InvokePrivateMethod<ProjectTextOutputSnapshot>(
			window,
			"CaptureProjectTextOutputSnapshot");
		var tree = await pipeline.BuildAsync(
			ProjectTextOutputMode.Tree,
			snapshot,
			cancellationToken);
		var content = await pipeline.BuildAsync(
			ProjectTextOutputMode.Content,
			snapshot,
			cancellationToken);
		var treeMetrics = ExportOutputMetricsCalculator.FromText(tree.Content);
		var contentMetrics = ExportOutputMetricsCalculator.FromText(content.Content);

        return new ProjectLoadWorkflowRuntime.ProjectLoadWorkflowMetrics(treeMetrics, contentMetrics);
    }

    public static async Task<string> ComputeAppliedPreviewCopyPayloadAsync(
        MainWindow window,
        PreviewContentMode mode,
        CancellationToken cancellationToken = default)
    {
        await WaitForSelectionRefreshIdleAsync(window);

        var pipeline = GetRequiredPrivateField<ProjectTextOutputPipeline>(window, "_textOutputPipeline");
        var snapshot = InvokePrivateMethod<ProjectTextOutputSnapshot>(
            window,
            "CaptureProjectTextOutputSnapshot");
        var outputMode = mode switch
        {
            PreviewContentMode.Tree => ProjectTextOutputMode.Tree,
            PreviewContentMode.Content => ProjectTextOutputMode.Content,
            PreviewContentMode.TreeAndContent => ProjectTextOutputMode.TreeAndContent,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };

        var result = await pipeline.BuildAsync(outputMode, snapshot, cancellationToken);
        return result.Content;
    }

    public static string ComputeCurrentPreviewCopyPayload(MainWindow window)
    {
        var viewModel = GetViewModel(window);
        var document = GetRequiredControl<DevProjex.Avalonia.Controls.VirtualizedPreviewTextControl>(window, "PreviewTextControl").Document ??
                       viewModel.PreviewDocument;
        Assert.NotNull(document);
        return PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(document);
    }

	public static async Task<(string RelativePath, int SourceOffset)> RequestPersistentSecretMarkAsync(
		MainWindow window,
		string value,
		bool persistent = true,
		ManualRedactionClass classification = ManualRedactionClass.Secret)
	{
		var viewModel = GetViewModel(window);
		var textControl = GetRequiredControl<DevProjex.Avalonia.Controls.VirtualizedPreviewTextControl>(
			window,
			"PreviewTextControl");
		var document = textControl.Document ?? viewModel.PreviewDocument;
		Assert.NotNull(document);
		Assert.True(MarkedSecretValueNormalizer.TryCreate(value, out var markedValue, out _));
		var selection = default(PreviewSelectionRange);
		var found = false;
		for (var lineNumber = 1; lineNumber <= document!.LineCount; lineNumber++)
		{
			var column = document.GetLineText(lineNumber).IndexOf(value, StringComparison.Ordinal);
			if (column < 0)
				continue;
			selection = new PreviewSelectionRange(
				lineNumber,
				column,
				lineNumber,
				column + value.Length);
			found = true;
			break;
		}
		Assert.True(found, "The value to mark was not found in the current preview document.");

		var controller = GetRequiredPrivateField<PreviewSurfaceController>(window, "_previewSurfaceController");
		var request = new DevProjex.Avalonia.Controls.PreviewManualSecretMarkRequestedEventArgs(
			markedValue,
			selection,
			classification,
			persistent);
		var resolve = typeof(PreviewSurfaceController).GetMethod(
			"TryResolveManualMarkLocation",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(resolve);
		object?[] resolveArguments = [document, request, null];
		Assert.True(Assert.IsType<bool>(resolve!.Invoke(controller, resolveArguments)));
		Assert.NotNull(resolveArguments[2]);
		var location = resolveArguments[2]!;
		var relativePath = Assert.IsType<string>(location.GetType().GetProperty("RelativePath")?.GetValue(location));
		var sourceOffset = Assert.IsType<int>(location.GetType().GetProperty("SourceOffset")?.GetValue(location));
		var handler = typeof(PreviewSurfaceController).GetMethod(
			"OnManualSecretMarkRequested",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(handler);
		await window.Dispatcher.InvokeAsync(
			() => handler!.Invoke(
				controller,
				[
					textControl,
					request
				]),
			DispatcherPriority.Normal);
		return (relativePath, sourceOffset);
	}

	public static async Task RequestSecretMarkThroughContextMenuAsync(
		MainWindow window,
		string value,
		bool persistent = false,
		int clickCount = 1,
		ManualRedactionClass classification = ManualRedactionClass.Secret)
	{
		Assert.True(clickCount > 0);
		await WaitForPreviewReadyAsync(window);
		var textControl = GetRequiredControl<DevProjex.Avalonia.Controls.VirtualizedPreviewTextControl>(
			window,
			"PreviewTextControl");
		var scrollViewer = GetRequiredPreviewScrollViewer(window);
		var document = textControl.Document ?? GetViewModel(window).PreviewDocument;
		Assert.NotNull(document);
		var lineNumber = 0;
		var startColumn = -1;
		for (var candidateLine = 1; candidateLine <= document!.LineCount; candidateLine++)
		{
			var candidateColumn = document.GetLineText(candidateLine).IndexOf(value, StringComparison.Ordinal);
			if (candidateColumn < 0)
				continue;
			lineNumber = candidateLine;
			startColumn = candidateColumn;
			break;
		}
		Assert.True(lineNumber > 0, "The value to mark was not found in the current preview document.");

		var lineHeight = InvokeRequiredPrivateMethod<double>(textControl, "ResolveLineHeight");
		var contentTopPadding = InvokeRequiredPrivateMethod<double>(textControl, "ResolveContentTopPadding");
		var localY = contentTopPadding + ((lineNumber - 1) * lineHeight) + (lineHeight / 2);
		var startTarget = await FindHorizontalTargetForColumnAsync(
			window,
			textControl,
			scrollViewer,
			localY,
			lineNumber,
			startColumn);
		var endTarget = await FindHorizontalTargetForColumnAsync(
			window,
			textControl,
			scrollViewer,
			localY,
			lineNumber,
			startColumn + value.Length);

		textControl.ClearSelection();
		await SetHorizontalOffsetAsync(scrollViewer, startTarget.HorizontalOffset);
		var start = ResolveViewportPoint(window, textControl, startTarget.ContentX, localY);
		textControl.Focus();
		window.MouseMove(start, RawInputModifiers.None);
		window.MouseDown(start, MouseButton.Left, RawInputModifiers.LeftMouseButton);
		window.MouseUp(start, MouseButton.Left, RawInputModifiers.None);
		await SetHorizontalOffsetAsync(scrollViewer, endTarget.HorizontalOffset);
		var end = ResolveViewportPoint(window, textControl, endTarget.ContentX, localY);
		window.MouseMove(end, RawInputModifiers.Shift);
		window.MouseDown(
			end,
			MouseButton.Left,
			RawInputModifiers.LeftMouseButton | RawInputModifiers.Shift);
		window.MouseUp(end, MouseButton.Left, RawInputModifiers.Shift);
		var selected = string.Equals(value, textControl.GetSelectedText(), StringComparison.Ordinal);
		Assert.True(
			selected,
			$"Expected exact pointer selection '{value}', actual '{textControl.GetSelectedText()}'. " +
			$"target={lineNumber}:{startColumn}-{startColumn + value.Length}, " +
			$"offsets={startTarget.HorizontalOffset:0.###}/{endTarget.HorizontalOffset:0.###}.");

		await WaitForSettledFramesAsync(frameCount: 2);
		var currentEnd = ResolveViewportPoint(window, textControl, endTarget.ContentX, localY);
		var contextPoint = new Point(currentEnd.X - 12, currentEnd.Y);
		window.MouseDown(contextPoint, MouseButton.Right, RawInputModifiers.RightMouseButton);
		window.MouseUp(contextPoint, MouseButton.Right, RawInputModifiers.None);
		await WaitForSettledFramesAsync(frameCount: 2);

		var flyout = Assert.IsType<MenuFlyout>(textControl.ContextFlyout);
		Assert.True(flyout.IsOpen);
		var menuItem = GetRequiredPrivateField<MenuItem>(
			textControl,
			(classification, persistent) switch
			{
				(ManualRedactionClass.Secret, false) => "_secretHideHereMenuItem",
				(ManualRedactionClass.Secret, true) => "_secretAlwaysHideMenuItem",
				(ManualRedactionClass.PrivateData, true) => "_privateDataAlwaysHideMenuItem",
				_ => throw new ArgumentOutOfRangeException(nameof(classification), classification, null)
			});
		Assert.True(menuItem.IsVisible);
		Assert.True(menuItem.IsEnabled);
		for (var click = 0; click < clickCount; click++)
			menuItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
		await WaitForSettledFramesAsync(frameCount: 4);
		flyout.Hide();
		await WaitForSettledFramesAsync(frameCount: 2);
	}

	private static async Task<PointerViewportTarget> FindHorizontalTargetForColumnAsync(
		MainWindow window,
		DevProjex.Avalonia.Controls.VirtualizedPreviewTextControl textControl,
		ScrollViewer scrollViewer,
		double localY,
		int targetLine,
		int targetColumn)
	{
		var maximumX = Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Viewport.Width);
		var low = 8.0;
		var high = Math.Max(low, scrollViewer.Extent.Width - 32);
		var preferredViewportX = Math.Max(8, Math.Min(72, scrollViewer.Viewport.Width - 32));
		var diagnostics = new List<string>();
		for (var attempt = 0; attempt < 24 && low <= high; attempt++)
		{
			var contentX = (low + high) / 2;
			var horizontalOffset = Math.Clamp(contentX - preferredViewportX, 0, maximumX);
			await SetHorizontalOffsetAsync(scrollViewer, horizontalOffset);
			var point = ResolveViewportPoint(window, textControl, contentX, localY);
			var probeEnd = new Point(point.X + 24, point.Y);
			textControl.ClearSelection();
			window.MouseMove(point, RawInputModifiers.None);
			window.MouseDown(point, MouseButton.Left, RawInputModifiers.LeftMouseButton);
			window.MouseMove(probeEnd, RawInputModifiers.LeftMouseButton);
			window.MouseUp(probeEnd, MouseButton.Left, RawInputModifiers.None);
			if (!textControl.TryGetSelectionRange(out var actual) || actual.StartLine != targetLine)
			{
				diagnostics.Add($"{contentX:0.###}:none");
				high = contentX - 0.25;
				continue;
			}

			diagnostics.Add($"{contentX:0.###}:{actual.StartLine}:{actual.StartColumn}");
			if (actual.StartColumn == targetColumn)
				return new PointerViewportTarget(horizontalOffset, contentX);
			if (actual.StartColumn < targetColumn)
				low = contentX + 0.25;
			else
				high = contentX - 0.25;
		}

		throw new XunitException(
			$"Could not position preview column {targetLine}:{targetColumn} inside the pointer viewport. " +
			string.Join(", ", diagnostics));
	}

	private static Point ResolveViewportPoint(
		MainWindow window,
		DevProjex.Avalonia.Controls.VirtualizedPreviewTextControl textControl,
		double contentX,
		double localY)
	{
		return Assert.IsType<Point>(textControl.TranslatePoint(
			new Point(contentX, localY),
			window));
	}

	private static async Task SetHorizontalOffsetAsync(ScrollViewer scrollViewer, double offset)
	{
		scrollViewer.Offset = new Vector(offset, scrollViewer.Offset.Y);
		await WaitForSettledFramesAsync(frameCount: 2);
	}

	private readonly record struct PointerViewportTarget(double HorizontalOffset, double ContentX);

	public static async Task RequestManualSecretUnmarkThroughContextMenuAsync(MainWindow window)
	{
		var textControl = GetRequiredControl<DevProjex.Avalonia.Controls.VirtualizedPreviewTextControl>(
			window,
			"PreviewTextControl");
		var redaction = Assert.Single(
			(textControl.Document ?? GetViewModel(window).PreviewDocument)!.Redactions,
			static span => span.PersistentMarkId is not null || !string.IsNullOrWhiteSpace(span.SessionMarkId));
		InvokeRequiredPrivateMethod(textControl, "EnsureContextMenu");
		var contextField = textControl.GetType().GetField(
			"_contextManualRedaction",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(contextField);
		contextField!.SetValue(textControl, redaction);
		InvokeRequiredPrivateMethod(textControl, "PrepareManualSecretMenuItems");
		await WaitForSettledFramesAsync(frameCount: 2);
		var remove = GetRequiredPrivateField<MenuItem>(textControl, "_removeSecretMarkMenuItem");
		Assert.True(remove.IsVisible);
		Assert.True(remove.IsEnabled);
		await RaiseMenuItemClickAsync(remove);
	}

	public static async Task RequestBulkRedactionToggleThroughContextMenuAsync(
		MainWindow window,
		string occurrenceId,
		bool ruleScope)
	{
		var textControl = GetRequiredControl<DevProjex.Avalonia.Controls.VirtualizedPreviewTextControl>(
			window,
			"PreviewTextControl");
		var document = textControl.Document ?? GetViewModel(window).PreviewDocument;
		Assert.NotNull(document);
		var redaction = document!.Redactions.First(span =>
			string.Equals(span.OccurrenceId, occurrenceId, StringComparison.Ordinal));
		InvokeRequiredPrivateMethod(textControl, "EnsureContextMenu");
		var contextField = textControl.GetType().GetField(
			"_contextDetectorRedaction",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(contextField);
		contextField!.SetValue(textControl, redaction);
		InvokeRequiredPrivateMethod(textControl, "PrepareBulkSecretMenuItems");
		await WaitForSettledFramesAsync(frameCount: 2);
		var item = GetRequiredPrivateField<MenuItem>(
			textControl,
			ruleScope ? "_bulkRuleRedactionMenuItem" : "_bulkFileRedactionMenuItem");
		Assert.True(item.IsVisible);
		Assert.True(item.IsEnabled);
		await RaiseMenuItemClickAsync(item);
	}

	public static async Task RequestRedactionToggleAsync(MainWindow window, string occurrenceId)
	{
		var textControl = GetRequiredControl<VirtualizedPreviewTextControl>(window, "PreviewTextControl");
		var controller = GetRequiredPrivateField<PreviewSurfaceController>(window, "_previewSurfaceController");
		var handler = typeof(PreviewSurfaceController).GetMethod(
			"OnRedactionToggleRequested",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(handler);
		await window.Dispatcher.InvokeAsync(
			() => handler!.Invoke(
				controller,
				[textControl, new PreviewRedactionToggleRequestedEventArgs(occurrenceId)]),
			DispatcherPriority.Normal);
		await WaitForSettledFramesAsync(frameCount: 4);
	}

	public static DevProjex.Avalonia.Services.ToastService GetToastService(MainWindow window) =>
		GetRequiredPrivateField<DevProjex.Avalonia.Services.ToastService>(window, "_toastService");

	public static string GetWindowAppDataPath(MainWindow window) =>
		WindowAppDataPaths.TryGetValue(window, out var path)
			? path
			: throw new XunitException("The window app-data path is not tracked.");

	public static void OverridePreviewErrorHandler(
		MainWindow window,
		Func<string, Task> handler)
	{
		ArgumentNullException.ThrowIfNull(handler);
		var controller = GetRequiredPrivateField<PreviewSurfaceController>(
			window,
			"_previewSurfaceController");
		var field = typeof(PreviewSurfaceController).GetField(
			"_showErrorAsync",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(field);
		field!.SetValue(controller, handler);
	}

	public static void OverridePersistentSecretMarkDeltaHandler(
		MainWindow window,
		Func<PersistentSecretMarkDelta, Task<PersistentSecretMarkWriteResult>> handler)
	{
		ArgumentNullException.ThrowIfNull(handler);
		var controller = GetRequiredPrivateField<PreviewSurfaceController>(
			window,
			"_previewSurfaceController");
		var field = typeof(PreviewSurfaceController).GetField(
			"_applyPersistentMarkDelta",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(field);
		field!.SetValue(controller, handler);
	}

	public static void SetCurrentProjectPath(MainWindow window, string projectPath) =>
		SetRequiredPrivateField(window, "_currentPath", projectPath);

	public static string RedactFileWithCurrentSession(MainWindow window, string filePath)
	{
		var session = GetRequiredPrivateField<SecretRedactionSession>(window, "_secretRedactionSession");
		var projectRoot = GetRequiredPrivateField<string>(window, "_currentPath");
		var scope = session.BeginOutput(projectRoot, [filePath]);
		var result = scope.Redact(filePath, File.ReadAllText(filePath));
		scope.Complete();
		return result.Text;
	}

	public static SecretRedactionSession GetSecretRedactionSession(MainWindow window) =>
		GetRequiredPrivateField<SecretRedactionSession>(window, "_secretRedactionSession");

	public static int GetPendingPersistentMarkCount(SecretRedactionSession session)
	{
		var property = typeof(SecretRedactionSession).GetProperty(
			"PendingPersistentMarkCount",
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(property);
		return Assert.IsType<int>(property!.GetValue(session));
	}

	public static CodeCompressionSession GetCodeCompressionSession(MainWindow window) =>
		GetRequiredPrivateField<CodeCompressionSession>(window, "_codeCompressionSession");

    public static string ComputeVisibleStickyHeaderCopyPayload(MainWindow window)
    {
        var viewModel = GetViewModel(window);
        var previewTextControl = GetRequiredControl<DevProjex.Avalonia.Controls.VirtualizedPreviewTextControl>(window, "PreviewTextControl");
        var previewScrollViewer = GetRequiredPreviewScrollViewer(window);
        var document = previewTextControl.Document ?? viewModel.PreviewDocument;
        if (document?.Sections is not { Count: > 0 } sections)
            throw new XunitException("Preview document sections are not available for sticky-header copy.");

        var topLine = previewTextControl.GetLineNumberAtVerticalOffset(previewScrollViewer.Offset.Y);
        if (topLine < sections[0].StartLine)
            throw new XunitException("Sticky-header copy payload was requested before the first file section became visible.");

        var currentSection = PreviewDocumentSectionLookup.FindContainingSection(sections, topLine) ??
                             PreviewDocumentSectionLookup.FindContainingOrNextSection(sections, topLine) ??
                             sections[^1];

        return PreviewClipboardPayloadBuilder.BuildSectionPayload(document, currentSection);
    }

    public static async Task SetClipboardTextAsync(MainWindow window, string content)
    {
        var clipboard = TopLevel.GetTopLevel(window)?.Clipboard;
        Assert.NotNull(clipboard);
        await clipboard.SetTextAsync(content);
        await WaitForSettledFramesAsync(frameCount: 2);
    }

    public static async Task<string?> GetClipboardTextAsync(MainWindow window)
    {
        var clipboard = TopLevel.GetTopLevel(window)?.Clipboard;
        Assert.NotNull(clipboard);
        return await ClipboardExtensions.TryGetTextAsync(clipboard);
    }

    public static async Task WaitForClipboardTextAsync(
        MainWindow window,
        string expectedText,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        string? lastClipboardText = null;

        while (DateTime.UtcNow < deadline)
        {
            lastClipboardText = await GetClipboardTextAsync(window);
            if (string.Equals(lastClipboardText, expectedText, StringComparison.Ordinal))
            {
                await WaitForSettledFramesAsync(frameCount: 4);
                return;
            }

            await Task.Delay(PollDelay);
            await WaitForSettledFramesAsync(frameCount: 2);
        }

        throw new XunitException(
            $"Timed out waiting for clipboard text to match the expected preview copy payload. Expected length={expectedText.Length}, actual length={lastClipboardText?.Length ?? 0}.");
    }

    public static async Task WaitForStatusMetricsReadyAsync(MainWindow window, TimeSpan? timeout = null)
    {
        await WaitForConditionAsync(
            window,
            () => TryGetCurrentStatusMetrics(window, out _, out _),
            "status metrics to become visible and parsable",
            timeout ?? TimeSpan.FromSeconds(30));

        await WaitForSettledFramesAsync(frameCount: 6);
    }

    public static bool TryParseStatusMetrics(string text, out ExportOutputMetrics metrics)
    {
        metrics = ExportOutputMetrics.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalizedText = text.Replace('\u00A0', ' ');
        var segments = normalizedText.Trim('[', ']').Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3)
            return false;

        var tokens = new string[3];
        for (var index = 0; index < 3; index++)
        {
            // CI runners can format status-bar numbers with culture-specific thousands
            // separators (for example 4,698 on en-US Windows or 4 698 on other locales).
            // The parser must accept the rendered token exactly as the user sees it.
            var match = Regex.Match(segments[index], @"([0-9][0-9\s,\.\u00A0]*[KM]?)");
            if (!match.Success)
                return false;

            tokens[index] = match.Groups[1].Value;
        }

        if (!TryParseMetricNumber(tokens[0], out var lines) ||
            !TryParseMetricNumber(tokens[1], out var chars) ||
            !TryParseMetricNumber(tokens[2], out var tokenCount))
        {
            return false;
        }

        metrics = new ExportOutputMetrics(lines, chars, tokenCount);
        return true;
    }

    public static IReadOnlyCollection<IgnoreOptionId> GetSelectedIgnoreOptionIds(MainWindow window)
    {
        return GetSelectionCoordinator(window).GetSelectedIgnoreOptionIds();
    }

	public static long GetSelectionRevision(MainWindow window) =>
		GetSelectionCoordinator(window).CurrentSelectionRevision;

	public static object GetCurrentTreeIdentity(MainWindow window) =>
		GetRequiredPrivateFieldValue(window, "_currentTree");

	public static object GetCurrentTreeInventoryIdentity(MainWindow window) =>
		GetRequiredPrivateFieldValue(window, "_currentTreeInventory");

	public static long GetRetainedReadFactBytes(MainWindow window) =>
		GetRequiredPrivateField<MetricsPipeline>(window, "_metrics").RetainedReadFactBytes;

	public static (bool CompressCode, bool StripComments, bool StripBlankLines)
		GetAppliedContentTransformationState(MainWindow window) =>
		(
			GetRequiredPrivateField<bool>(window, "_appliedCompressCodeEnabled"),
			GetRequiredPrivateField<bool>(window, "_appliedStripCommentsEnabled"),
			GetRequiredPrivateField<bool>(window, "_appliedStripBlankLinesEnabled")
		);

	public static (bool HideSecrets, bool HidePrivateData) GetAppliedContentRedactionState(
		MainWindow window) =>
		(
			GetRequiredPrivateField<bool>(window, "_appliedHideSecretsEnabled"),
			GetRequiredPrivateField<bool>(window, "_appliedHidePrivateDataEnabled")
		);

	public static (long Requested, long Completed, int Build) GetPreviewRefreshVersions(
		MainWindow window)
	{
		var pipeline = GetRequiredPrivateField<PreviewWorkspacePipeline>(window, "_previewPipeline");
		return (
			GetRequiredPrivateField<long>(pipeline, "_requestedRefreshVersion"),
			GetRequiredPrivateField<long>(pipeline, "_completedRefreshVersion"),
			GetRequiredPrivateField<int>(pipeline, "_buildVersion"));
	}

	public static HashSet<string> GetCheckedTreePaths(MainWindow window)
	{
		var selected = new HashSet<string>(PathComparer.Default);
		foreach (var root in GetViewModel(window).TreeNodes)
			root.CollectCheckedPaths(selected);
		return selected;
	}

    public static ContextDiagnostic? GetAppliedGitReadinessDiagnostic(
        MainWindow window,
        string projectPath) =>
        GetSelectionCoordinator(window).GetAppliedGitReadinessDiagnostic(projectPath);

    public static async Task WaitForSelectionRefreshIdleAsync(MainWindow window, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(effectiveTimeout);
        await GetSelectionCoordinator(window).WaitForPendingRefreshesAsync(cts.Token);

        await WaitForConditionAsync(
            window,
            () =>
            {
                var viewModel = GetViewModel(window);
                return !viewModel.StatusBusy;
            },
            "selection refresh pipeline to become idle",
            effectiveTimeout);

        await WaitForSettledFramesAsync(frameCount: 6);
    }

    public static async Task WaitForInitialMetricsBaselineAsync(
        MainWindow window,
        TimeSpan? timeout = null)
    {
        var metrics = GetRequiredPrivateField<MetricsPipeline>(
            window,
            "_metrics");

        // Project load can be idle briefly before deferred post-load metrics become eligible.
        // Tests that take ownership of StatusBusy must wait for the baseline itself, otherwise a
        // later metrics completion can overwrite their synthetic status operation.
        await WaitForConditionAsync(
            window,
            () => metrics.HasCompleteBaseline &&
                  !metrics.IsBackgroundActive &&
                  !GetViewModel(window).StatusBusy,
            "initial metrics baseline to finish",
            timeout);
    }

    public static async Task WaitForMemoryCleanupIdleAsync(
        MainWindow window,
        TimeSpan? timeout = null)
    {
        var coordinator = GetRequiredPrivateField<MemoryCleanupCoordinator>(
            window,
            "_memoryCleanup");
        await WaitForConditionAsync(
            window,
            () => !coordinator.IsCleanupPendingOrRunning,
            "deferred memory cleanup to become idle",
            timeout);
    }

    public static bool TryGetCurrentStatusMetrics(
        MainWindow window,
        out ExportOutputMetrics treeMetrics,
        out ExportOutputMetrics contentMetrics)
    {
        treeMetrics = ExportOutputMetrics.Empty;
        contentMetrics = ExportOutputMetrics.Empty;

        var viewModel = GetViewModel(window);
        if (!viewModel.StatusMetricsVisible ||
            string.IsNullOrWhiteSpace(viewModel.StatusTreeStatsText) ||
            string.IsNullOrWhiteSpace(viewModel.StatusContentStatsText))
        {
            return false;
        }

        return TryParseStatusMetrics(viewModel.StatusTreeStatsText, out treeMetrics) &&
               TryParseStatusMetrics(viewModel.StatusContentStatsText, out contentMetrics);
    }

    public static async Task SwitchPreviewModeAsync(MainWindow window, PreviewContentMode mode)
    {
        var buttonName = mode switch
        {
            PreviewContentMode.Tree => "PreviewTreeModeButton",
            PreviewContentMode.Content => "PreviewContentModeButton",
            PreviewContentMode.TreeAndContent => "PreviewTreeAndContentModeButton",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };

        var button = GetRequiredControl<Button>(window, buttonName);
        await ClickAsync(window, button);

        await WaitForConditionAsync(
            window,
            () =>
            {
                var viewModel = GetViewModel(window);
                var previewTextControl = window.FindControl<DevProjex.Avalonia.Controls.VirtualizedPreviewTextControl>("PreviewTextControl");
                var previewDocument = previewTextControl?.Document ?? viewModel.PreviewDocument;

                return viewModel.SelectedPreviewContentMode == mode &&
                       !viewModel.IsPreviewLoading &&
                       previewDocument is not null &&
                       IsPreviewPipelineIdle(window);
            },
            $"preview mode {mode} to become active");

        await WaitForSettledFramesAsync(frameCount: 12);
    }

    public static CodeCompressionDiagnosticsSnapshot GetCodeCompressionDiagnostics(MainWindow window) =>
        GetRequiredPrivateField<CodeCompressionSession>(window, "_codeCompressionSession").Diagnostics;

    public static async Task WaitForPreviewReadyAsync(MainWindow window)
    {
        await WaitForConditionAsync(
            window,
            () =>
            {
                var viewModel = GetViewModel(window);
                var previewIsland = GetRequiredControl<Border>(window, "PreviewIsland");
                var previewTextControl = window.FindControl<DevProjex.Avalonia.Controls.VirtualizedPreviewTextControl>("PreviewTextControl");
                var previewDocument = previewTextControl?.Document ?? viewModel.PreviewDocument;

                return viewModel.IsPreviewMode &&
                       previewIsland.IsVisible &&
                       !IsPreviewPaneTransitionInProgress(window) &&
                       !viewModel.IsPreviewLoading &&
                       previewDocument is not null &&
                       IsPreviewPipelineIdle(window);
            },
            "preview workspace to become ready");
        await WaitForSettledFramesAsync(frameCount: 18);
    }

    private static bool IsPreviewPaneTransitionInProgress(MainWindow window) =>
        GetWorkspacePresentationController(window).IsPreviewPaneAnimating ||
        GetWorkspacePresentationController(window).IsTreePaneAnimating;

    public static async Task WaitForFilterAppliedAsync(MainWindow window, string query)
    {
        await WaitForConditionAsync(
            window,
            () =>
            {
                var viewModel = GetViewModel(window);
                return viewModel.FilterVisible &&
                       !viewModel.IsFilterInProgress &&
                       string.Equals(viewModel.NameFilter, query, StringComparison.Ordinal) &&
                       viewModel.FilterMatchCount > 0;
            },
            $"filter query '{query}' to finish");
        await WaitForSettledFramesAsync(frameCount: 6);
    }

    public static async Task WaitForSearchAppliedAsync(MainWindow window, string query)
    {
        await WaitForConditionAsync(
            window,
            () =>
            {
                var viewModel = GetViewModel(window);
                return viewModel.SearchVisible &&
                       !viewModel.IsSearchInProgress &&
                       string.Equals(viewModel.SearchQuery, query, StringComparison.Ordinal) &&
                       viewModel.SearchTotalMatches > 0;
            },
            $"search query '{query}' to finish");
        await WaitForSettledFramesAsync(frameCount: 6);
    }

    public static async Task ScrollPreviewUntilStickyHeaderVisibleAsync(MainWindow window)
    {
        var stickyHeaderContainer = GetRequiredControl<Border>(window, "PreviewStickyHeaderContainer");
        var stickyHeaderText = GetRequiredControl<TextBlock>(window, "PreviewStickyHeaderText");
        var scrollViewer = GetRequiredPreviewScrollViewer(window);
        var previewTextControl = GetRequiredControl<DevProjex.Avalonia.Controls.VirtualizedPreviewTextControl>(window, "PreviewTextControl");

        var firstSectionStartLine = await WaitForPreviewScrollRangeAsync(window, scrollViewer);

        var maxOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var step = ResolvePreviewScrollStep(scrollViewer);
        var reachedTopLine = previewTextControl.GetLineNumberAtVerticalOffset(scrollViewer.Offset.Y);
        var reachedOffset = scrollViewer.Offset.Y;

        for (var offset = step; offset <= maxOffset + step; offset += step)
        {
            reachedOffset = Math.Min(maxOffset, offset);
            scrollViewer.Offset = new Vector(scrollViewer.Offset.X, reachedOffset);

            // Linux headless can lag one render behind when the ScrollViewer offset is
            // applied to the virtualized preview control. We wait for the propagated
            // VerticalOffset instead of waiting for the sticky threshold itself, because
            // the first section may require several scroll steps before it becomes reachable.
            await WaitForConditionAsync(
                window,
                () => stickyHeaderContainer.IsVisible ||
                      Math.Abs(previewTextControl.VerticalOffset - reachedOffset) <= 0.5,
                "preview scroll position to propagate into the virtualized preview control",
                timeout: TimeSpan.FromSeconds(2));

            await WaitForSettledFramesAsync(frameCount: 3);
            reachedTopLine = previewTextControl.GetLineNumberAtVerticalOffset(previewTextControl.VerticalOffset);
            if (stickyHeaderContainer.IsVisible && !string.IsNullOrWhiteSpace(stickyHeaderText.Text))
                return;

            if (reachedTopLine >= firstSectionStartLine)
                break;
        }

        await WaitForConditionAsync(
            window,
            () => stickyHeaderContainer.IsVisible && !string.IsNullOrWhiteSpace(stickyHeaderText.Text),
            "sticky preview path to become visible after the viewport reaches the first file section",
            timeout: TimeSpan.FromSeconds(5));

        if (stickyHeaderContainer.IsVisible && !string.IsNullOrWhiteSpace(stickyHeaderText.Text))
            return;

        throw new XunitException(
            "Sticky preview path never became visible after scrolling through the preview content. " +
            $"firstSectionStartLine={firstSectionStartLine}, reachedTopLine={reachedTopLine}, " +
            $"offset={reachedOffset:0.##}, controlOffset={previewTextControl.VerticalOffset:0.##}, maxOffset={maxOffset:0.##}, " +
            $"extent={scrollViewer.Extent.Height:0.##}, viewport={scrollViewer.Viewport.Height:0.##}");
    }

    public static async Task ScrollPreviewUntilStickyHeaderTextChangesAsync(MainWindow window, string previousText)
    {
        var stickyHeaderText = GetRequiredControl<TextBlock>(window, "PreviewStickyHeaderText");
        var scrollViewer = GetRequiredPreviewScrollViewer(window);
        var previewTextControl = GetRequiredControl<DevProjex.Avalonia.Controls.VirtualizedPreviewTextControl>(window, "PreviewTextControl");
        var firstSectionStartLine = await WaitForPreviewScrollRangeAsync(window, scrollViewer);
        var maxOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var step = ResolvePreviewScrollStep(scrollViewer);

        for (var offset = scrollViewer.Offset.Y + step; offset <= maxOffset + step; offset += step)
        {
            var targetOffset = Math.Min(maxOffset, offset);
            scrollViewer.Offset = new Vector(scrollViewer.Offset.X, targetOffset);
            await WaitForConditionAsync(
                window,
                () => Math.Abs(previewTextControl.VerticalOffset - targetOffset) <= 0.5,
                "preview scroll position to propagate into the virtualized preview control",
                timeout: TimeSpan.FromSeconds(2));
            await WaitForSettledFramesAsync(frameCount: 3);

            if (previewTextControl.GetLineNumberAtVerticalOffset(previewTextControl.VerticalOffset) < firstSectionStartLine)
                continue;

            if (!string.IsNullOrWhiteSpace(stickyHeaderText.Text) &&
                !string.Equals(stickyHeaderText.Text, previousText, StringComparison.Ordinal))
            {
                return;
            }
        }

        throw new XunitException("Sticky preview path text never changed after scrolling through multiple preview sections.");
    }

    private static async Task<int> WaitForPreviewScrollRangeAsync(MainWindow window, ScrollViewer scrollViewer)
    {
        var firstSectionStartLine = 1;

        // The sticky header depends on the virtualized preview document sections and on
        // the ScrollViewer extent being fully measured. If we start scrolling before both
        // are ready, CI can observe a zero or undersized scroll range and falsely report
        // that the sticky header never appears.
        await WaitForConditionAsync(
            window,
            () =>
            {
                var document = GetViewModel(window).PreviewDocument;
                if (document?.Sections is not { Count: > 0 })
                    return false;

                firstSectionStartLine = document.Sections[0].StartLine;
                return scrollViewer.Viewport.Height > 0 &&
                       scrollViewer.Extent.Height > scrollViewer.Viewport.Height;
            },
            "preview document sections and scroll range to become measurable");

        return firstSectionStartLine;
    }

    private static double ResolvePreviewScrollStep(ScrollViewer scrollViewer)
        => Math.Max(48, Math.Min(144, scrollViewer.Viewport.Height / 4));

    public static ScrollViewer GetRequiredPreviewScrollViewer(MainWindow window)
        => GetRequiredControl<ScrollViewer>(window, "PreviewTextScrollViewer");

    public static double GetActualWidth(Control control)
        => control.Bounds.Width;

    public static bool IsActuallyVisibleHorizontally(Control control)
        => control.IsVisible && GetActualWidth(control) > 0.5;

    public static Rect GetBoundsInWindow(Control control, TopLevel topLevel)
    {
        var origin = control.TranslatePoint(default, topLevel);
        if (!origin.HasValue)
            throw new XunitException($"Unable to translate control '{GetControlDebugName(control)}' into top-level coordinates.");

        return new Rect(origin.Value, control.Bounds.Size);
    }

    public static Point GetControlCenter(Control control, TopLevel topLevel)
    {
        var bounds = GetBoundsInWindow(control, topLevel);
        return bounds.Center;
    }

    private static async Task EnsureControlVisibleAsync(MainWindow window, Control control)
    {
        var scrollViewer = control.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (scrollViewer is null)
            return;

        var scrolled = false;
        await window.Dispatcher.InvokeAsync(() =>
        {
            var origin = control.TranslatePoint(default, scrollViewer);
            if (!origin.HasValue)
                return;

            var viewport = scrollViewer.Viewport;
            if (viewport.Width <= 0 || viewport.Height <= 0)
                return;

            const double Padding = 4;
            var bounds = new Rect(origin.Value, control.Bounds.Size);
            var offset = scrollViewer.Offset;
            var maxX = Math.Max(0, scrollViewer.Extent.Width - viewport.Width);
            var maxY = Math.Max(0, scrollViewer.Extent.Height - viewport.Height);

            var targetX = offset.X;
            if (bounds.Left < Padding)
                targetX = Math.Max(0, offset.X + bounds.Left - Padding);
            else if (bounds.Right > viewport.Width - Padding)
                targetX = Math.Min(maxX, offset.X + bounds.Right - viewport.Width + Padding);

            var targetY = offset.Y;
            if (bounds.Top < Padding)
                targetY = Math.Max(0, offset.Y + bounds.Top - Padding);
            else if (bounds.Bottom > viewport.Height - Padding)
                targetY = Math.Min(maxY, offset.Y + bounds.Bottom - viewport.Height + Padding);

            if (Math.Abs(targetX - offset.X) < 0.5 && Math.Abs(targetY - offset.Y) < 0.5)
                return;

            scrollViewer.Offset = new Vector(targetX, targetY);
            scrolled = true;
        });

        if (scrolled)
            await WaitForSettledFramesAsync(frameCount: 6);
    }

    private static async Task ScrollSettingsItemIntoViewAsync(
        MainWindow window,
        string listName,
        object? item)
    {
        if (item is null)
            return;

        var list = GetRequiredControl<ListBox>(window, listName);
        await window.Dispatcher.InvokeAsync(() => list.ScrollIntoView(item));
        await WaitForSettledFramesAsync(frameCount: 4);
    }

    private static async Task WaitForControlReadyForPointerAsync(MainWindow window, Control control)
        => await WaitForControlReadyForPointerAsync(window, window, control);

    private static async Task WaitForControlReadyForPointerAsync(
        MainWindow window,
        TopLevel inputRoot,
        Control control)
    {
        await WaitForConditionAsync(
            window,
            () => IsControlReadyForPointer(control, inputRoot),
            $"control '{GetControlDebugName(control)}' to be ready for pointer input");
    }

    private static async Task ClickResolvedControlAsync(
        MainWindow window,
        Func<Control?> resolveControl,
        string description)
    {
        var stopwatch = Stopwatch.StartNew();
        Exception? lastFailure = null;

        while (stopwatch.Elapsed < DefaultTimeout)
        {
            var control = resolveControl();
            if (control is null || !IsControlReadyForPointer(control, window))
            {
                await WaitForSettledFramesAsync(frameCount: 2);
                await Task.Delay(PollDelay);
                continue;
            }

            try
            {
                await EnsureControlVisibleAsync(window, control);
                if (!IsControlReadyForPointer(control, window))
                    continue;

                await ClickReadyControlAsync(window, control);
                return;
            }
            catch (XunitException exception)
            {
                lastFailure = exception;
                await WaitForSettledFramesAsync(frameCount: 2);
            }
        }

        var lastFailureText = lastFailure is null ? string.Empty : $" Last failure: {lastFailure.Message}";
        throw new XunitException(
            $"Timed out waiting for {description} to stay ready for pointer input.{lastFailureText} Current state: {DescribeState(window)}");
    }

    private static bool IsControlReadyForPointer(Control control, TopLevel inputRoot)
        => control.IsVisible
           && control.Bounds.Width > 0.5
           && control.Bounds.Height > 0.5
           && control.TranslatePoint(default, inputRoot).HasValue;

    private static string GetControlDebugName(Control control)
        => string.IsNullOrWhiteSpace(control.Name) ? control.GetType().Name : control.Name!;

    public static async Task WaitForConditionAsync(
        MainWindow window,
        Func<bool> predicate,
        string description,
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < effectiveTimeout)
        {
            await WaitForSettledFramesAsync(frameCount: 2);
            if (predicate())
                return;

            await Task.Delay(PollDelay);
        }

        throw new XunitException($"Timed out waiting for {description}. Current state: {DescribeState(window)}");
    }

    public static async Task WaitForSettledFramesAsync(int frameCount)
    {
        var effectiveFrameCount = FastTimingsEnabled
            ? Math.Max(1, (int)Math.Ceiling(frameCount * FastSettledFrameScale))
            : frameCount;
        var dispatcher = Dispatcher.UIThread;

        for (var index = 0; index < effectiveFrameCount; index++)
        {
            if (dispatcher.CheckAccess())
                PumpHeadlessFrame(dispatcher);
            else
                await dispatcher.InvokeAsync(static () => PumpHeadlessFrame(Dispatcher.UIThread), DispatcherPriority.Background);

            await Task.Delay(FrameDelay);
        }
    }

    private static void PumpHeadlessFrame(Dispatcher dispatcher)
    {
        // RunJobs drains Avalonia's queued dispatcher work immediately. Pairing it with
        // the headless render timer keeps layout/animation assertions deterministic
        // without adding real-time sleeps to every UI test frame.
        dispatcher.RunJobs(DispatcherPriority.Background);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        dispatcher.RunJobs(DispatcherPriority.Background);
    }

    private static string DescribeState(MainWindow window)
    {
        var viewModel = GetViewModel(window);
        var settingsContainer = window.FindControl<Border>("SettingsContainer");
        var settingsWidth = settingsContainer is null
            ? 0.0
            : GetActualWidth(settingsContainer);
        var ignoreOptions = string.Join(
            ";",
            viewModel.IgnoreOptions.Select(static option => $"{option.Id}:{option.IsChecked}"));
        return string.Join(
            ", ",
            [
                $"Visible={window.IsVisible}",
                $"ProjectLoaded={viewModel.IsProjectLoaded}",
                $"TreeNodes={viewModel.TreeNodes.Count}",
                $"PreviewMode={viewModel.PreviewWorkspaceMode}",
                $"PreviewLoading={viewModel.IsPreviewLoading}",
                $"PreviewTransitioning={IsPreviewPaneTransitionInProgress(window)}",
                $"SettingsVisible={viewModel.SettingsVisible}",
                $"SettingsWidth={settingsWidth:F2}",
                $"SearchVisible={viewModel.SearchVisible}",
                $"FilterVisible={viewModel.FilterVisible}",
                $"SearchBusy={viewModel.IsSearchInProgress}",
                $"FilterBusy={viewModel.IsFilterInProgress}",
                $"StatusBusy={viewModel.StatusBusy}",
                $"IgnoreOptions=[{ignoreOptions}]",
                $"StatusTree={viewModel.StatusTreeStatsText}",
                $"StatusContent={viewModel.StatusContentStatsText}"
            ]);
    }

    private static bool IsInteractableWithinWindow(Control control, MainWindow window)
        => control.IsVisible && control.TranslatePoint(default, window).HasValue;

    private static CheckBox? FindIgnoreOptionCheckBox(MainWindow window, IgnoreOptionId optionId)
    {
        return window
            .GetVisualDescendants()
            .OfType<CheckBox>()
            .FirstOrDefault(control => control.DataContext is IgnoreOptionViewModel option &&
                                       option.Id == optionId &&
                                       IsInteractableWithinWindow(control, window));
    }

    private static CheckBox? FindExtensionCheckBox(MainWindow window, string extensionName)
    {
        return window
            .GetVisualDescendants()
            .OfType<CheckBox>()
            .FirstOrDefault(control => control.DataContext is SelectionOptionViewModel option &&
                                       string.Equals(option.Name, extensionName, StringComparison.Ordinal) &&
                                       GetViewModel(window).Extensions.Contains(option) &&
                                       IsInteractableWithinWindow(control, window));
    }

    private static CheckBox? FindTreeNodeCheckBox(MainWindow window, string displayName)
    {
        return window
            .GetVisualDescendants()
            .OfType<CheckBox>()
            .FirstOrDefault(control => control.DataContext is TreeNodeViewModel node &&
                                       string.Equals(node.DisplayName, displayName, StringComparison.Ordinal) &&
                                       IsInteractableWithinWindow(control, window));
    }

    private static async Task<CheckBox> WaitForExtensionCheckBoxAsync(MainWindow window, string extensionName)
    {
        await WaitForConditionAsync(
            window,
            () => FindExtensionCheckBox(window, extensionName) is not null,
            $"extension checkbox '{extensionName}' to become interactable");

        return GetRequiredExtensionCheckBox(window, extensionName);
    }

    private static async Task<CheckBox> WaitForIgnoreOptionCheckBoxAsync(MainWindow window, IgnoreOptionId optionId)
    {
        await WaitForConditionAsync(
            window,
            () => FindIgnoreOptionCheckBox(window, optionId) is not null,
            $"ignore checkbox '{optionId}' to become interactable");

        return GetRequiredIgnoreOptionCheckBox(window, optionId);
    }

    public static async Task<CheckBox> WaitForTreeNodeCheckBoxAsync(MainWindow window, string displayName)
    {
        await WaitForConditionAsync(
            window,
            () => FindTreeNodeCheckBox(window, displayName) is not null,
            $"tree node checkbox '{displayName}' to become interactable");

        return Assert.IsType<CheckBox>(FindTreeNodeCheckBox(window, displayName));
    }

    private static Avalonia.Coordinators.SelectionSyncCoordinator GetSelectionCoordinator(MainWindow window)
    {
        var field = typeof(MainWindow).GetField("_selectionCoordinator", BindingFlags.Instance | BindingFlags.NonPublic);
        return Assert.IsType<Avalonia.Coordinators.SelectionSyncCoordinator>(field?.GetValue(window));
    }

    private static Avalonia.Coordinators.WorkspacePresentationController
        GetWorkspacePresentationController(MainWindow window)
    {
        var field = typeof(MainWindow).GetField(
            "_workspacePresentation",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return Assert.IsType<Avalonia.Coordinators.WorkspacePresentationController>(
            field?.GetValue(window));
    }

    private static bool IsPreviewPipelineIdle(MainWindow window)
    {
        // Preview mode switching is a two-step pipeline: the selected mode changes first,
        // then the actual preview refresh is scheduled and can temporarily clear the document.
        // UI tests must wait for both steps to settle before reading the live preview payload.
        return GetPreviewPipeline(window).IsIdle;
    }

    private static Avalonia.Coordinators.PreviewWorkspacePipeline GetPreviewPipeline(MainWindow window)
    {
        var field = typeof(MainWindow).GetField("_previewPipeline", BindingFlags.Instance | BindingFlags.NonPublic);
        return Assert.IsType<Avalonia.Coordinators.PreviewWorkspacePipeline>(field?.GetValue(window));
    }

    private static T GetRequiredPrivateField<T>(MainWindow window, string fieldName)
    {
        var field = typeof(MainWindow).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        return Assert.IsType<T>(field?.GetValue(window));
    }

	private static T GetRequiredPrivateField<T>(object instance, string fieldName)
	{
		var field = instance.GetType().GetField(
			fieldName,
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(field);
		return Assert.IsType<T>(field!.GetValue(instance));
	}

	private static T InvokeRequiredPrivateMethod<T>(
		object instance,
		string methodName,
		params object?[] arguments)
	{
		var method = instance.GetType().GetMethod(
			methodName,
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		return Assert.IsType<T>(method!.Invoke(instance, arguments));
	}

	private static void InvokeRequiredPrivateMethod(
		object instance,
		string methodName,
		params object?[] arguments)
	{
		var method = instance.GetType().GetMethod(
			methodName,
			BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(method);
		method!.Invoke(instance, arguments);
	}

	private static object GetRequiredPrivateFieldValue(MainWindow window, string fieldName)
	{
		var field = typeof(MainWindow).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
		Assert.NotNull(field);
		var value = field!.GetValue(window);
		Assert.NotNull(value);
		return value!;
	}

    private static void SetRequiredPrivateField(MainWindow window, string fieldName, object? value)
    {
        var field = typeof(MainWindow).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(window, value);
    }

    private static T InvokePrivateMethod<T>(MainWindow window, string methodName)
    {
        var method = typeof(MainWindow).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        return Assert.IsType<T>(method?.Invoke(window, null));
    }

    private static T InvokePrivateMethodAssignable<T>(MainWindow window, string methodName)
    {
        var method = typeof(MainWindow).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        return Assert.IsAssignableFrom<T>(method?.Invoke(window, null));
    }

    private static T? InvokePrivateMethodAllowNull<T>(MainWindow window, string methodName)
        where T : class
    {
        var method = typeof(MainWindow).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        return method?.Invoke(window, null) as T;
    }

    private static List<string> BuildOrderedSelectedFilePaths(
        TreeNodeDescriptor treeRoot,
        IReadOnlySet<string> selectedPaths)
    {
        return PreviewFileCollectionPolicy.BuildOrderedSelectedFilePaths(
            selectedPaths,
            treeRoot,
            ensureExists: true);
    }

    private static List<string> BuildOrderedAllFilePaths(TreeNodeDescriptor root)
    {
        var uniquePaths = new HashSet<string>(PathComparer.Default);
        var stack = new Stack<TreeNodeDescriptor>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!current.IsDirectory)
            {
                uniquePaths.Add(current.FullPath);
                continue;
            }

            for (var index = current.Children.Count - 1; index >= 0; index--)
                stack.Push(current.Children[index]);
        }

        var ordered = new List<string>(uniquePaths.Count);
        ordered.AddRange(uniquePaths);
        ordered.Sort(PathComparer.Default);
        return ordered;
    }

    private static void CleanupWindowAppData(MainWindow window)
    {
        if (!WindowAppDataPaths.TryRemove(window, out var appDataPath))
            return;

        DeleteAppDataDirectory(appDataPath);
    }

    private static void CleanupTrackedAppData()
    {
        foreach (var window in WindowAppDataPaths.Keys.ToArray())
        {
            if (WindowAppDataPaths.TryRemove(window, out var appDataPath))
                DeleteAppDataDirectory(appDataPath);
        }
    }

    private static void DeleteAppDataDirectory(string appDataPath)
    {
        try
        {
            if (Directory.Exists(appDataPath))
                Directory.Delete(appDataPath, recursive: true);
        }
        catch
        {
            // Best effort cleanup only. CI can keep temporary handles alive for a short time.
        }
    }

    private static bool TryParseMetricNumber(string text, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = text
            .Replace('\u00A0', ' ')
            .Trim();
        var multiplier = 1.0;
        if (normalized.EndsWith('K'))
        {
            multiplier = 1_000.0;
            normalized = normalized[..^1];
        }
        else if (normalized.EndsWith('M'))
        {
            multiplier = 1_000_000.0;
            normalized = normalized[..^1];
        }

        if (multiplier == 1.0)
        {
            // Integer status values use localized group separators only. We deliberately
            // keep digits and drop every separator so 4,698 / 4 698 / 4.698 all become 4698.
            normalized = string.Concat(normalized.Where(char.IsDigit));
            if (normalized.Length == 0 || !long.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out value))
                return false;

            return true;
        }

        normalized = normalized.Replace(" ", string.Empty, StringComparison.Ordinal);

        // Compact K/M labels can use either '.' or ',' as the decimal separator depending
        // on the current culture. Normalizing to invariant form keeps the comparison stable.
        var lastComma = normalized.LastIndexOf(',');
        var lastDot = normalized.LastIndexOf('.');
        var decimalSeparatorIndex = Math.Max(lastComma, lastDot);
        if (decimalSeparatorIndex >= 0)
        {
            var integerPart = new string(normalized[..decimalSeparatorIndex].Where(char.IsDigit).ToArray());
            var fractionalPart = new string(normalized[(decimalSeparatorIndex + 1)..].Where(char.IsDigit).ToArray());
            normalized = fractionalPart.Length == 0
                ? integerPart
                : $"{integerPart}.{fractionalPart}";
        }
        else
        {
            normalized = string.Concat(normalized.Where(char.IsDigit));
        }

        if (normalized.Length == 0 ||
            !double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        var scaled = Math.Round(parsed * multiplier, MidpointRounding.AwayFromZero);
        if (scaled < 0 || scaled > long.MaxValue)
            return false;

        value = (long)scaled;
        return true;
    }
}
