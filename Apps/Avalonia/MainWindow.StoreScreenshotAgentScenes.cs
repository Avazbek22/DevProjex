using System.Text.Json;
using Avalonia.Rendering.Composition;
using DevProjex.Avalonia.Views;
using DevProjex.Terminal.DesktopControl;

namespace DevProjex.Avalonia;

// Store capture scenes that show a real Live context session. The capture controller runs the
// MCP server and its scripted client against the same isolated data root as this window; these
// steps only arrange the window around that session and wait until it reflects every call.
public partial class MainWindow
{
    private static readonly TimeSpan StoreCaptureAgentTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StoreCaptureAgentRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StoreCaptureFrameTimeout = TimeSpan.FromSeconds(2);
    private const double StoreCaptureJournalOwnerRatio = 0.86;

    private async Task<Control> PrepareStoreLiveContextSceneAsync(
        StoreScreenshotCaptureRequest request,
        CancellationToken cancellationToken)
    {
        if (request.LiveContextSelection is not { Count: > 0 } selection)
            throw new InvalidOperationException("The Store capture request has no Live context selection.");

        // Agent activity is shown only by the delivery markers in the tree, so these scenes keep
        // the tree beside the settings panel with the preview closed.
        await _previewWorkspaceController.CloseAsync();
        _viewModel.SettingsVisible = true;
        await AnimateSettingsPanelAsync(show: true);
        if (!_viewModel.IsAgentActivityEnabled)
        {
            _viewModel.IsAgentActivityEnabled = true;
            _agentActivityPreferenceStore.TrySave(true);
        }

        var selectedNodes = selection.Select(ResolveStoreCaptureNode).ToArray();
        ApplyTreeSelectionBatch(() =>
        {
            foreach (var node in selectedNodes)
                node.IsChecked = true;
        });
        foreach (var node in selectedNodes)
            node.EnsureParentsExpanded();
        await _selectionCoordinator.WaitForPendingRefreshesAsync(cancellationToken);
        if (!await _treeSelectionProfiles.FlushAsync(cancellationToken))
            throw new InvalidOperationException("The Live context selection could not be saved.");

        // The server follows the saved selection, and the window marks only calls made after it
        // first observed the session. The controller therefore connects first and sends tool
        // calls only after this window reports that it sees the live session.
        WriteStoreCaptureState(request, "live-session-request.json", new
        {
            selectionCount = selection.Count
        });
        await WaitForStoreCaptureMarkerAsync(request, "live-session-connected", cancellationToken);
        RefreshLiveSessionPresentation();
        await WaitForStoreCaptureConditionAsync(
            () => _liveSessions.Count == 1 &&
                  !string.IsNullOrWhiteSpace(_liveSessions[0].ClientName) &&
                  _agentActivitySessionId is not null,
            "The live session did not appear in the window.",
            RefreshLiveSessionPresentation,
            cancellationToken);
        WriteStoreCaptureState(request, "live-session-observed.json", new
        {
            observed = true
        });

        var expectedCalls = await ReadStoreCaptureLiveCallCountAsync(request, cancellationToken);
        await WaitForStoreCaptureAgentActivityAsync(expectedCalls, cancellationToken);
        foreach (var deliveredPath in _agentDeliveryCounts.Keys.ToArray())
            ResolveStoreCaptureNode(Path.GetRelativePath(_currentPath!, deliveredPath)).EnsureParentsExpanded();
        await WaitForStoreCaptureVisualsAsync(cancellationToken);
        return await OpenStoreDeliveryToolTipAsync(cancellationToken);
    }

    // The delivery marker is small, so the scene opens its own tooltip, the product's localized
    // "received N times" text, on the file the agent received most often.
    private async Task<Control> OpenStoreDeliveryToolTipAsync(CancellationToken cancellationToken)
    {
        var deliveredPath = _agentDeliveryCounts
            .OrderByDescending(static delivery => delivery.Value)
            .ThenBy(static delivery => delivery.Key, StringComparer.Ordinal)
            .Select(static delivery => delivery.Key)
            .FirstOrDefault() ??
            throw new InvalidOperationException("The live session delivered no files.");
        var node = ResolveStoreCaptureNode(Path.GetRelativePath(_currentPath!, deliveredPath));
        Control? marker = null;
        await WaitForStoreCaptureConditionAsync(
            () => (marker = FindStoreCaptureDeliveryMarker(node)) is not null,
            "The delivery marker was not realized in the tree.",
            retry: null,
            cancellationToken);

        ToolTip? openedToolTip = null;
        using var loadedSubscription = Control.LoadedEvent.AddClassHandler<ToolTip>(
            (toolTip, _) => openedToolTip = toolTip,
            RoutingStrategies.Direct,
            handledEventsToo: true);
        // A hovered tooltip follows the pointer; the capture has no pointer over the tree, so the
        // tooltip is anchored beside the marker instead.
        ToolTip.SetPlacement(marker!, PlacementMode.Right);
        ToolTip.SetIsOpen(marker!, true);
        await WaitForStoreCaptureConditionAsync(
            () => ToolTip.GetIsOpen(marker!) &&
                  openedToolTip is not null &&
                  TopLevel.GetTopLevel(openedToolTip) is not null,
            "The delivery marker tooltip did not open.",
            retry: null,
            cancellationToken);
        await WaitForStoreCaptureVisualsAsync(cancellationToken);
        await WaitForStoreCaptureTopLevelFrameAsync(TopLevel.GetTopLevel(openedToolTip!)!, cancellationToken);
        return marker!;
    }

    private Control? FindStoreCaptureDeliveryMarker(TreeNodeViewModel node) =>
        ProjectTree
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(textBlock =>
                ReferenceEquals(textBlock.DataContext, node) &&
                textBlock.IsEffectivelyVisible &&
                ToolTip.GetTip(textBlock) is string { Length: > 0 });

    private async Task OpenStoreMcpMenuAsync(
        Control deliveryMarker,
        CancellationToken cancellationToken)
    {
        ToolTip.SetIsOpen(deliveryMarker, false);
        deliveryMarker.ClearValue(ToolTip.PlacementProperty);
        // The menu opens over the Live context layout, the tree beside the settings panel with
        // the preview closed, so the open menu stays the focal point of the scene.
        await _previewWorkspaceController.CloseAsync();
        if (!_viewModel.SettingsVisible)
        {
            _viewModel.SettingsVisible = true;
            await AnimateSettingsPanelAsync(show: true);
        }
        await WaitForStoreCaptureVisualsAsync(cancellationToken);

        var mainMenu = _topMenuBar?.MainMenuControl;
        var mcpMenuItem = _topMenuBar?.McpMenuItemControl;
        var liveContextMenuItem = _topMenuBar?.McpLiveContextMenuItemControl;
        if (mainMenu is null || mcpMenuItem is null || liveContextMenuItem is null)
            throw new InvalidOperationException("The MCP menu is unavailable.");

        mainMenu.Open();
        mcpMenuItem.Open();
        await WaitForStoreCaptureConditionAsync(
            () => mcpMenuItem.IsSubMenuOpen && TopLevel.GetTopLevel(liveContextMenuItem) is not null,
            "The MCP menu did not open.",
            retry: null,
            cancellationToken);
        liveContextMenuItem.Open();
        var liveContextItems = liveContextMenuItem.Items.OfType<MenuItem>().ToArray();
        await WaitForStoreCaptureConditionAsync(
            () => liveContextMenuItem.IsSubMenuOpen &&
                  liveContextItems.Length > 0 &&
                  liveContextItems.All(static item => TopLevel.GetTopLevel(item) is not null),
            "The Live context submenu did not open.",
            retry: null,
            cancellationToken);
        // Highlight the first client the way keyboard navigation does, so the one-step action
        // reads as the point of the scene. Nothing is invoked.
        liveContextItems[0].IsSelected = true;

        await WaitForStoreCaptureVisualsAsync(cancellationToken);
        // Menus are separate native popups; each one must present its own frame.
        await WaitForStoreCaptureTopLevelFrameAsync(TopLevel.GetTopLevel(liveContextMenuItem)!, cancellationToken);
        await WaitForStoreCaptureTopLevelFrameAsync(TopLevel.GetTopLevel(liveContextItems[0])!, cancellationToken);
    }

    private async Task<AgentJournalWindow> OpenStoreAgentJournalAsync(CancellationToken cancellationToken)
    {
        _topMenuBar?.MainMenuControl?.Close();
        // Behind the journal the tree workspace returns with the delivery markers the journal
        // itemizes; the settings panel stays hidden to keep its edges clean.
        await _previewWorkspaceController.CloseAsync();
        _viewModel.SettingsVisible = false;
        await AnimateSettingsPanelAsync(show: false);
        await WaitForStoreCaptureVisualsAsync(cancellationToken);

        var sessions = await _agentJournalReader.ListSessionsAsync(
            Path.GetFullPath(_currentPath!),
            cancellationToken: cancellationToken);
        var liveSession = sessions.SingleOrDefault(session =>
                string.Equals(session.Id, _agentActivitySessionId, StringComparison.Ordinal)) ??
            throw new InvalidOperationException("The live session is missing from the journal.");

        // The journal is created through the same path as MCP -> Journal..., including its
        // opaque dialog surface; only its initial size is larger so both tables stay readable.
        var window = CreateAgentJournalWindow();
        window.Width = Math.Max(
            AgentJournalWindow.MinimumWindowWidth,
            ClientSize.Width * StoreCaptureJournalOwnerRatio);
        window.Height = Math.Max(
            AgentJournalWindow.MinimumWindowHeight,
            ClientSize.Height * StoreCaptureJournalOwnerRatio);
        window.Show(this);
        await WaitForStoreCaptureConditionAsync(
            () => window.IsVisible &&
                  !window.ViewModel.IsLoading &&
                  window.ViewModel.Sessions.Count == sessions.Count &&
                  string.Equals(
                      window.ViewModel.SelectedSession?.Session.Id,
                      liveSession.Id,
                      StringComparison.Ordinal) &&
                  window.ViewModel.Calls.Count == liveSession.Totals.Calls,
            "The agent journal did not load the capture sessions.",
            retry: null,
            cancellationToken);
        window.Activate();
        await WaitForStoreCaptureConditionAsync(
            () => window.IsActive,
            "The agent journal did not become active.",
            window.Activate,
            cancellationToken);
        return window;
    }

    private TreeNodeViewModel ResolveStoreCaptureNode(string relativePath)
    {
        var node = _viewModel.TreeNodes.FirstOrDefault() ??
            throw new InvalidOperationException("The Store capture project tree is empty.");
        foreach (var segment in relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            var childPath = Path.Combine(node.FullPath, segment);
            node = node.Children.FirstOrDefault(child => PathComparer.Default.Equals(child.FullPath, childPath)) ??
                throw new InvalidOperationException("A Store capture path is not part of the project tree.");
        }

        return node;
    }

    private static async Task<int> ReadStoreCaptureLiveCallCountAsync(
        StoreScreenshotCaptureRequest request,
        CancellationToken cancellationToken)
    {
        const string markerName = "live-session-calls-complete.json";
        await WaitForStoreCaptureMarkerAsync(request, markerName, cancellationToken);
        await using var stream = File.OpenRead(Path.Combine(request.SessionDirectory, markerName));
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var calls = document.RootElement.GetProperty("calls").GetInt32();
        return calls > 0
            ? calls
            : throw new InvalidOperationException("The Store capture live session made no calls.");
    }

    private async Task WaitForStoreCaptureAgentActivityAsync(
        int expectedCalls,
        CancellationToken cancellationToken)
    {
        var timeoutAt = DateTimeOffset.UtcNow + StoreCaptureAgentTimeout;
        var refreshAt = DateTimeOffset.UtcNow;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_agentActivitySessionId is { } sessionId)
            {
                var calls = await _agentJournalReader.ReadCallsAsync(sessionId, cancellationToken);
                // The window has applied every call once its activity cursor reaches the latest
                // journal sequence; the delivery counts then drive the markers in the tree.
                if (calls.Count >= expectedCalls &&
                    Volatile.Read(ref _agentActivityLatestSequence) == calls.Max(static call => call.Sequence) &&
                    _agentDeliveryCounts.Count > 0)
                {
                    return;
                }
            }

            var now = DateTimeOffset.UtcNow;
            if (now >= timeoutAt)
                throw new TimeoutException("Agent activity did not reflect the live session calls.");
            // Each refresh cancels the previous one, so it is requested at a slow cadence and the
            // file watcher remains the primary trigger.
            if (now >= refreshAt)
            {
                RefreshAgentActivityPresentation();
                refreshAt = now + StoreCaptureAgentRefreshInterval;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }
    }

    private static async Task WaitForStoreCaptureConditionAsync(
        Func<bool> condition,
        string timeoutMessage,
        Action? retry,
        CancellationToken cancellationToken)
    {
        var timeoutAt = DateTimeOffset.UtcNow + StoreCaptureAgentTimeout;
        var retryAt = DateTimeOffset.UtcNow + StoreCaptureAgentRefreshInterval;
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            if (now >= timeoutAt)
                throw new TimeoutException(timeoutMessage);
            if (retry is not null && now >= retryAt)
            {
                retry();
                retryAt = now + StoreCaptureAgentRefreshInterval;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(40), cancellationToken);
        }
    }

    private static async Task WaitForStoreCaptureTopLevelFrameAsync(
        TopLevel topLevel,
        CancellationToken cancellationToken)
    {
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        topLevel.RequestAnimationFrame(_ => frame.TrySetResult());
        try
        {
            await frame.Task.WaitAsync(StoreCaptureFrameTimeout, cancellationToken);
            if (ElementComposition.GetElementVisual(topLevel) is { } visual)
            {
                await visual.Compositor.RequestCompositionBatchCommitAsync().Rendered.WaitAsync(
                    StoreCaptureFrameTimeout,
                    cancellationToken);
            }
        }
        catch (TimeoutException)
        {
            // The controller keeps its own presentation guard before reading desktop pixels.
        }
    }
}
