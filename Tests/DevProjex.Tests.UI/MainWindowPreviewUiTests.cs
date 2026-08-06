using Avalonia.Layout;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Application.Services;
using System.ComponentModel;
using System.Text.Json;
using System.Xml.Linq;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowPreviewUiTests(UiWorkspaceFixture workspace)
{
    [AvaloniaFact]
    public async Task JsonTree_UserVisibleCopyAndPreviewRoutesKeepUnicodeNamesReadable()
    {
        using var project = UiTestProject.CreateWithUnicodeJsonWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);
            viewModel.SelectedExportFormat = ExportFormat.Json;

            var treePayload = await UiTestDriver.ComputeAppliedPreviewCopyPayloadAsync(
                window,
                PreviewContentMode.Tree);
            AssertReadableUnicodeJsonTree(treePayload, project.RootPath);

            await UiTestDriver.CopyTreeToClipboardAsync(window, treePayload);
            Assert.Equal(treePayload, await UiTestDriver.GetClipboardTextAsync(window));

            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Tree);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => UiTestDriver.ComputeCurrentPreviewCopyPayload(window)
                    .Contains("\"Документы\"", StringComparison.Ordinal),
                "readable Unicode JSON tree preview to be rendered");

            var previewTreePayload = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
            Assert.Equal(NormalizeLineEndings(treePayload), NormalizeLineEndings(previewTreePayload));
            AssertReadableUnicodeJsonTree(previewTreePayload, project.RootPath);

            await UiTestDriver.SetClipboardTextAsync(window, $"unicode-preview-pending-{Guid.NewGuid():N}");
            await UiTestDriver.ClickPreviewCopyButtonAsync(window);
            await UiTestDriver.WaitForClipboardTextAsync(window, previewTreePayload);

            var treeAndContentPayload = await UiTestDriver.ComputeAppliedPreviewCopyPayloadAsync(
                window,
                PreviewContentMode.TreeAndContent);
            await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.TreeAndContent);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => UiTestDriver.ComputeCurrentPreviewCopyPayload(window)
                    .Contains("Содержимое отчёта", StringComparison.Ordinal),
                "Unicode JSON tree and content preview to be rendered");

            var previewTreeAndContentPayload = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
            Assert.Equal(
                NormalizeLineEndings(treeAndContentPayload),
                NormalizeLineEndings(previewTreeAndContentPayload));
            var (jsonTreePart, contentPart) = SplitTreeAndContentPayload(previewTreeAndContentPayload);
            AssertReadableUnicodeJsonTree(jsonTreePart, project.RootPath);
            Assert.Contains("Содержимое отчёта", contentPart, StringComparison.Ordinal);

            await UiTestDriver.CopyTreeAndContentToClipboardAsync(window, treeAndContentPayload);
            Assert.Equal(treeAndContentPayload, await UiTestDriver.GetClipboardTextAsync(window));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task TreePreview_UsesSelectedXmlAndMarkdownFormats()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Tree);
            var viewModel = UiTestDriver.GetViewModel(window);

            viewModel.SelectedExportFormat = ExportFormat.Xml;
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => UiTestDriver.ComputeCurrentPreviewCopyPayload(window).StartsWith("<t ", StringComparison.Ordinal),
                "XML tree preview to be rendered");

            var xmlPayload = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
            var xmlDocument = XDocument.Parse(xmlPayload);
            Assert.Equal("t", xmlDocument.Root!.Name.LocalName);
            Assert.NotEmpty(xmlDocument.Descendants("f"));

            viewModel.SelectedExportFormat = ExportFormat.Markdown;
            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var payload = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
                    return payload.StartsWith("Root: ", StringComparison.Ordinal) &&
                           payload.Contains("\n- ", StringComparison.Ordinal);
                },
                "Markdown tree preview to be rendered");

            var markdownPayload = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
            Assert.StartsWith("Root: ", markdownPayload, StringComparison.Ordinal);
            Assert.Contains("\n- ", markdownPayload, StringComparison.Ordinal);
            Assert.DoesNotContain("<t ", markdownPayload, StringComparison.Ordinal);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task TreeAndContentPreview_UsesSelectedTreeFormatWithRelativeContentHeaders()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.TreeAndContent);
            var viewModel = UiTestDriver.GetViewModel(window);

            viewModel.SelectedExportFormat = ExportFormat.Ascii;
            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var payload = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
                    return payload.Contains("\u00A0", StringComparison.Ordinal) &&
                           payload.Contains("\n├── ", StringComparison.Ordinal);
                },
                "ASCII tree and content preview to be rendered");

            var asciiPayload = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
            var (_, asciiContentPart) = SplitTreeAndContentPayload(asciiPayload);
            Assert.Contains(":", asciiContentPart, StringComparison.Ordinal);
            Assert.DoesNotContain(workspace.Project.RootPath.Replace('\\', '/'), asciiContentPart.Replace('\\', '/'), StringComparison.Ordinal);

            viewModel.SelectedExportFormat = ExportFormat.Xml;
            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var payload = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
                    return payload.StartsWith("<t ", StringComparison.Ordinal) &&
                           payload.Contains("\u00A0", StringComparison.Ordinal);
                },
                "XML tree and content preview to be rendered");

            var xmlPayload = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
            var (xmlTreePart, xmlContentPart) = SplitTreeAndContentPayload(xmlPayload);
            var xmlDocument = XDocument.Parse(xmlTreePart);
            Assert.Equal("t", xmlDocument.Root!.Name.LocalName);
            Assert.NotEmpty(xmlDocument.Descendants("f"));
            Assert.Contains(":", xmlContentPart, StringComparison.Ordinal);
            Assert.DoesNotContain(workspace.Project.RootPath.Replace('\\', '/'), xmlContentPart.Replace('\\', '/'), StringComparison.Ordinal);

            viewModel.SelectedExportFormat = ExportFormat.Markdown;
            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var payload = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
                    return payload.StartsWith("Root: ", StringComparison.Ordinal) &&
                           payload.Contains("\n- ", StringComparison.Ordinal) &&
                           payload.Contains("\u00A0", StringComparison.Ordinal);
                },
                "Markdown tree and content preview to be rendered");

            var markdownPayload = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
            var (markdownTreePart, markdownContentPart) = SplitTreeAndContentPayload(markdownPayload);
            Assert.StartsWith("Root: ", markdownTreePart, StringComparison.Ordinal);
            Assert.Contains("\n- ", markdownTreePart, StringComparison.Ordinal);
            Assert.DoesNotContain("<t ", markdownTreePart, StringComparison.Ordinal);
            Assert.Contains(":", markdownContentPart, StringComparison.Ordinal);
            Assert.DoesNotContain(workspace.Project.RootPath.Replace('\\', '/'), markdownContentPart.Replace('\\', '/'), StringComparison.Ordinal);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task TreeAndContentPreview_StatusContentMetricsMatchRelativeContentHeaders()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.TreeAndContent);
            var viewModel = UiTestDriver.GetViewModel(window);
            viewModel.SelectedExportFormat = ExportFormat.Ascii;

            await UiTestDriver.WaitForConditionAsync(
                window,
                () => UiTestDriver.ComputeCurrentPreviewCopyPayload(window).Contains("\u00A0", StringComparison.Ordinal),
                "tree and content preview payload to be rendered");
            await UiTestDriver.WaitForStatusMetricsReadyAsync(window);

            var payload = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
            var contentBody = ExtractContentBodyFromTreeAndContentPayload(payload);
            var expectedTreeAndContentMetrics = ExportOutputMetricsCalculator.FromText(contentBody);
            Assert.True(UiTestDriver.TryGetCurrentStatusMetrics(window, out _, out var actualTreeAndContentMetrics));
            Assert.Equal(ToRenderedStatusMetrics(expectedTreeAndContentMetrics), actualTreeAndContentMetrics);

            var rootPath = workspace.Project.RootPath.Replace('\\', '/');
            Assert.DoesNotContain(rootPath, contentBody.Replace('\\', '/'), StringComparison.Ordinal);

            foreach (var format in new[] { ExportFormat.Json, ExportFormat.Xml, ExportFormat.Markdown })
            {
                viewModel.SelectedExportFormat = format;
                await UiTestDriver.WaitForConditionAsync(
                    window,
                    () =>
                    {
                        var candidatePayload = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
                        return IsExpectedTreeFormat(candidatePayload, format) &&
                               string.Equals(
                                   contentBody,
                                   ExtractContentBodyFromTreeAndContentPayload(candidatePayload),
                                   StringComparison.Ordinal);
                    },
                    $"{format} tree and complete content preview payload to be rendered");
                await UiTestDriver.WaitForStatusMetricsReadyAsync(window);

                var formatPayload = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
                var formatContentBody = ExtractContentBodyFromTreeAndContentPayload(formatPayload);
                Assert.Equal(contentBody, formatContentBody);
                Assert.True(UiTestDriver.TryGetCurrentStatusMetrics(window, out _, out var formatContentMetrics));
                Assert.Equal(actualTreeAndContentMetrics, formatContentMetrics);
            }

            await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);
            await UiTestDriver.WaitForStatusMetricsReadyAsync(window);
            var contentOnlyPayload = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
            var expectedContentOnlyMetrics = ExportOutputMetricsCalculator.FromText(contentOnlyPayload);
            Assert.True(UiTestDriver.TryGetCurrentStatusMetrics(window, out _, out var actualContentOnlyMetrics));
            Assert.Equal(ToRenderedStatusMetrics(expectedContentOnlyMetrics), actualContentOnlyMetrics);
            Assert.True(
                actualTreeAndContentMetrics.Chars < actualContentOnlyMetrics.Chars,
                "Tree + Content content metrics should benefit from shorter relative headers while Content-only keeps full display paths.");

            await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.TreeAndContent);
            await UiTestDriver.WaitForStatusMetricsReadyAsync(window);
            Assert.True(UiTestDriver.TryGetCurrentStatusMetrics(window, out _, out var reopenedTreeAndContentMetrics));
            Assert.Equal(actualTreeAndContentMetrics, reopenedTreeAndContentMetrics);

            await UiTestDriver.ClosePreviewAsync(window);
            await UiTestDriver.WaitForStatusMetricsReadyAsync(window);
            Assert.True(UiTestDriver.TryGetCurrentStatusMetrics(window, out _, out var closedPreviewContentMetrics));
            Assert.Equal(actualContentOnlyMetrics, closedPreviewContentMetrics);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task LoadedProject_ShowsTreeAndSettingsBeforePreviewOpens()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);
            var treeIsland = UiTestDriver.GetRequiredControl<Border>(window, "TreeIsland");
            var previewIsland = UiTestDriver.GetRequiredControl<Border>(window, "PreviewIsland");
            var settingsContainer = UiTestDriver.GetRequiredControl<Border>(window, "SettingsContainer");

            Assert.False(viewModel.IsPreviewMode);
            Assert.True(treeIsland.IsVisible);
            Assert.False(previewIsland.IsVisible);
            Assert.True(UiTestDriver.IsActuallyVisibleHorizontally(settingsContainer));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewToggleButton_OpensPreviewBetweenTreeAndSettings()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);

            var viewModel = UiTestDriver.GetViewModel(window);
            var treeIsland = UiTestDriver.GetRequiredControl<Border>(window, "TreeIsland");
            var previewIsland = UiTestDriver.GetRequiredControl<Border>(window, "PreviewIsland");
            var settingsContainer = UiTestDriver.GetRequiredControl<Border>(window, "SettingsContainer");

            var treeBounds = UiTestDriver.GetBoundsInWindow(treeIsland, window);
            var previewBounds = UiTestDriver.GetBoundsInWindow(previewIsland, window);
            var settingsBounds = UiTestDriver.GetBoundsInWindow(settingsContainer, window);

            Assert.True(viewModel.IsPreviewTreeVisible);
            Assert.True(previewIsland.IsVisible);
            Assert.InRange(previewBounds.Left - treeBounds.Right, 0, 12);
            Assert.InRange(settingsBounds.Left - previewBounds.Right, 0, 12);
            Assert.True(previewBounds.Width >= 320);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewToggleButton_OpensPreviewNextToTreeWhenSettingsAreCollapsed()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.PressKeyAsync(window, Key.P, RawInputModifiers.Control);
            await UiTestDriver.WaitForSettingsVisibilityAsync(window, visible: false);
            await UiTestDriver.OpenPreviewAsync(window);

            var treeIsland = UiTestDriver.GetRequiredControl<Border>(window, "TreeIsland");
            var previewIsland = UiTestDriver.GetRequiredControl<Border>(window, "PreviewIsland");
            var settingsContainer = UiTestDriver.GetRequiredControl<Border>(window, "SettingsContainer");
            var treeBounds = UiTestDriver.GetBoundsInWindow(treeIsland, window);
            var previewBounds = UiTestDriver.GetBoundsInWindow(previewIsland, window);

            Assert.False(UiTestDriver.GetViewModel(window).SettingsVisible);
            Assert.False(UiTestDriver.IsActuallyVisibleHorizontally(settingsContainer));
            Assert.InRange(previewBounds.Left - treeBounds.Right, 0, 12);
            Assert.True(previewBounds.Width >= 320);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewCloseButton_ClosesPreviewAndRestoresTreeWorkspace()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.ClosePreviewAsync(window);

            var viewModel = UiTestDriver.GetViewModel(window);
            var treeIsland = UiTestDriver.GetRequiredControl<Border>(window, "TreeIsland");
            var previewIsland = UiTestDriver.GetRequiredControl<Border>(window, "PreviewIsland");

            Assert.False(viewModel.IsPreviewMode);
            Assert.True(treeIsland.IsVisible);
            Assert.False(previewIsland.IsVisible);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaTheory]
    [InlineData(PreviewContentMode.Tree)]
    [InlineData(PreviewContentMode.Content)]
    [InlineData(PreviewContentMode.TreeAndContent)]
    public async Task PreviewOpen_AnimatesPaneBeforePublishingContent(
        PreviewContentMode mode)
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);
            var previewController = GetPreviewWorkspaceController(window);
            var treePaneContainer =
                UiTestDriver.GetRequiredControl<Border>(
                    window,
                    "TreePaneContainer");
            viewModel.SelectedPreviewContentMode = mode;
            Assert.Null(viewModel.PreviewDocument);
            var animationStarted =
                new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var contentWasDeferred = false;

            void OnTreePanePropertyChanged(
                object? sender,
                AvaloniaPropertyChangedEventArgs args)
            {
                if (args.Property != Layoutable.WidthProperty ||
                    treePaneContainer.Width >
                    WorkspacePresentationController
                        .SplitTreePaneMinimumWidth + 1.0)
                {
                    return;
                }

                treePaneContainer.PropertyChanged -=
                    OnTreePanePropertyChanged;
                contentWasDeferred =
                    viewModel.PreviewDocument is null &&
                    !viewModel.IsPreviewLoading;
                animationStarted.TrySetResult();
            }

            treePaneContainer.PropertyChanged +=
                OnTreePanePropertyChanged;
            try
            {
                var openTask = previewController.OpenAsync();
                await animationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(contentWasDeferred);
                await openTask;
            }
            finally
            {
                treePaneContainer.PropertyChanged -=
                    OnTreePanePropertyChanged;
            }

            Assert.NotNull(viewModel.PreviewDocument);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewCloseRequestedDuringOpen_IsQueuedAndAllowsCleanReopen()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);
            var previewController = GetPreviewWorkspaceController(window);
            viewModel.SelectedPreviewContentMode = PreviewContentMode.Content;

            var openTask = previewController.OpenAsync();
            Assert.True(viewModel.IsPreviewMode);
            Assert.False(openTask.IsCompleted);

            await previewController.CloseAsync();
            await openTask;

            Assert.False(viewModel.IsPreviewMode);
            Assert.Null(viewModel.PreviewDocument);

            await previewController.OpenAsync();
            await UiTestDriver.WaitForPreviewReadyAsync(window);

            Assert.True(viewModel.IsPreviewMode);
            Assert.NotNull(viewModel.PreviewDocument);
            Assert.False(viewModel.IsPreviewLoading);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewCloseDuringOpenAnimation_InterruptsTransitionAndCleansVisualState()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var viewModel = UiTestDriver.GetViewModel(window);
            var previewController = GetPreviewWorkspaceController(window);
            var previewModeEntered =
                new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            Task? closeRequestTask = null;

            void OnViewModelPropertyChanged(
                object? sender,
                PropertyChangedEventArgs args)
            {
                if (args.PropertyName !=
                        nameof(MainWindowViewModel.PreviewWorkspaceMode) ||
                    !viewModel.IsPreviewMode)
                {
                    return;
                }

                viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                closeRequestTask = previewController.CloseAsync();
                previewModeEntered.TrySetResult();
            }

            viewModel.SelectedPreviewContentMode = PreviewContentMode.Content;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;

            try
            {
                var openTask = previewController.OpenAsync();
                await previewModeEntered.Task.WaitAsync(
                    TimeSpan.FromSeconds(5));
                await Assert.IsAssignableFrom<Task>(closeRequestTask);
                await openTask;
            }
            finally
            {
                viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }

            var treeSnapshotHost =
                UiTestDriver.GetRequiredControl<Border>(
                    window,
                    "TreePaneAnimationSnapshotHost");
            var treeSnapshotImage =
                UiTestDriver.GetRequiredControl<Image>(
                    window,
                    "TreePaneAnimationSnapshotImage");
            var previewSurface =
                UiTestDriver.GetRequiredControl<Grid>(window, "PreviewPaneSurface");

            Assert.True(previewController.WasLastOpenAnimationInterruptedByClose);
            Assert.False(viewModel.IsPreviewMode);
            Assert.False(treeSnapshotHost.IsVisible);
            Assert.Null(treeSnapshotImage.Source);
            Assert.True(double.IsNaN(previewSurface.Width));
            Assert.Equal(HorizontalAlignment.Stretch, previewSurface.HorizontalAlignment);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewCopyButton_IsPlacedBeforeModeSelector_AndMatchesCloseButtonSize()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);

            var copyButton = UiTestDriver.GetRequiredControl<Button>(window, "PreviewCopyButton");
            var segmentedControl = UiTestDriver.GetRequiredControl<Border>(window, "PreviewSegmentedControl");
            var closeButton = UiTestDriver.GetRequiredControl<Button>(window, "PreviewCloseButton");

            var copyBounds = UiTestDriver.GetBoundsInWindow(copyButton, window);
            var segmentedBounds = UiTestDriver.GetBoundsInWindow(segmentedControl, window);
            var closeBounds = UiTestDriver.GetBoundsInWindow(closeButton, window);

            Assert.True(
                copyBounds.Right <= segmentedBounds.Left,
                $"Expected preview copy button before mode selector without overlap. " +
                $"Copy={copyBounds}, Selector={segmentedBounds}");
            Assert.True(
                segmentedBounds.Right <= closeBounds.Left,
                $"Expected preview mode selector before close button without overlap. " +
                $"Selector={segmentedBounds}, Close={closeBounds}");
            Assert.InRange(Math.Abs(copyBounds.Width - closeBounds.Width), 0, 1.5);
            Assert.InRange(Math.Abs(copyBounds.Height - closeBounds.Height), 0, 1.5);
            Assert.Equal(0, copyButton.Padding.Left);
            Assert.Equal(0, copyButton.Padding.Right);
            Assert.Equal(0, copyButton.Padding.Top);
            Assert.Equal(0, copyButton.Padding.Bottom);
            Assert.Equal(HorizontalAlignment.Center, copyButton.HorizontalContentAlignment);
            Assert.Equal(VerticalAlignment.Center, copyButton.VerticalContentAlignment);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewCopyButton_CopiesPayloadForActivePreviewMode()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);

            foreach (var mode in new[]
                     {
                         PreviewContentMode.Tree,
                         PreviewContentMode.Content,
                         PreviewContentMode.TreeAndContent
                     })
            {
                await UiTestDriver.SwitchPreviewModeAsync(window, mode);
                var expectedText = UiTestDriver.ComputeCurrentPreviewCopyPayload(window);
                Assert.False(string.IsNullOrWhiteSpace(expectedText));

                await UiTestDriver.SetClipboardTextAsync(window, $"preview-copy-sentinel-{mode}-{Guid.NewGuid():N}");
                await UiTestDriver.ClickPreviewCopyButtonAsync(window);
                await UiTestDriver.WaitForClipboardTextAsync(window, expectedText);
            }
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewCopyButton_DoesNotReplacePreviewDocument_OrTogglePreviewLoading()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);

            foreach (var mode in new[]
                     {
                         PreviewContentMode.Tree,
                         PreviewContentMode.Content,
                         PreviewContentMode.TreeAndContent
                     })
            {
                await UiTestDriver.SwitchPreviewModeAsync(window, mode);

                var viewModel = UiTestDriver.GetViewModel(window);
                var previewDocumentBeforeCopy = viewModel.PreviewDocument;
                var previewLineCountBeforeCopy = viewModel.PreviewLineCount;
                var selectedModeBeforeCopy = viewModel.SelectedPreviewContentMode;

                Assert.NotNull(previewDocumentBeforeCopy);
                Assert.False(viewModel.IsPreviewLoading);

                await UiTestDriver.SetClipboardTextAsync(window, $"preview-copy-stability-{mode}-{Guid.NewGuid():N}");
                await UiTestDriver.ClickPreviewCopyButtonAsync(window);
                await UiTestDriver.WaitForClipboardTextAsync(
                    window,
                    UiTestDriver.ComputeCurrentPreviewCopyPayload(window));

                await UiTestDriver.WaitForSettledFramesAsync(frameCount: 10);

                Assert.Same(previewDocumentBeforeCopy, viewModel.PreviewDocument);
                Assert.Equal(previewLineCountBeforeCopy, viewModel.PreviewLineCount);
                Assert.Equal(selectedModeBeforeCopy, viewModel.SelectedPreviewContentMode);
                Assert.False(viewModel.IsPreviewLoading);
            }
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task TreeHideButton_SwitchesPreviewToPreviewOnly()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.HidePreviewTreeAsync(window);

            var viewModel = UiTestDriver.GetViewModel(window);
            var treeIsland = UiTestDriver.GetRequiredControl<Border>(window, "TreeIsland");
            var previewIsland = UiTestDriver.GetRequiredControl<Border>(window, "PreviewIsland");

            Assert.True(viewModel.IsPreviewOnlyMode);
            Assert.False(treeIsland.IsVisible);
            Assert.True(previewIsland.IsVisible);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task FilterState_SurvivesPreviewOnlyCloseAndReopenCycle()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.OpenFilterAsync(window);

            var filterBar = UiTestDriver.GetRequiredControl<FilterBarView>(window, "FilterBar");
            await UiTestDriver.EnterTextAsync(window, Assert.IsType<TextBox>(filterBar.FilterBoxControl), "app");
            await UiTestDriver.WaitForFilterAppliedAsync(window, "app");

            await UiTestDriver.HidePreviewTreeAsync(window);

            var viewModel = UiTestDriver.GetViewModel(window);
            var filterBarContainer = UiTestDriver.GetRequiredControl<Border>(window, "FilterBarContainer");
            Assert.Equal("app", viewModel.NameFilter);
            Assert.False(filterBarContainer.IsVisible);

            await UiTestDriver.ClosePreviewAsync(window);

            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var currentViewModel = UiTestDriver.GetViewModel(window);
                    return currentViewModel.FilterVisible &&
                           currentViewModel.NameFilter == "app" &&
                           UiTestDriver.GetRequiredControl<Border>(window, "FilterBarContainer").IsVisible;
                },
                "filter state to be restored after leaving preview-only");

            await UiTestDriver.OpenPreviewAsync(window);

            var restoredViewModel = UiTestDriver.GetViewModel(window);
            Assert.True(restoredViewModel.FilterVisible);
            Assert.Equal("app", restoredViewModel.NameFilter);
            Assert.True(UiTestDriver.GetRequiredControl<Border>(window, "FilterBarContainer").IsVisible);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task SearchState_SurvivesPreviewOnlyCloseAndReopenCycle()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.OpenSearchAsync(window);

            var searchBar = UiTestDriver.GetRequiredControl<SearchBarView>(window, "SearchBar");
            await UiTestDriver.EnterTextAsync(window, Assert.IsType<TextBox>(searchBar.SearchBoxControl), "app");
            await UiTestDriver.WaitForSearchAppliedAsync(window, "app");

            await UiTestDriver.HidePreviewTreeAsync(window);

            var viewModel = UiTestDriver.GetViewModel(window);
            var searchBarContainer = UiTestDriver.GetRequiredControl<Border>(window, "SearchBarContainer");
            Assert.Equal("app", viewModel.SearchQuery);
            Assert.False(searchBarContainer.IsVisible);

            await UiTestDriver.ClosePreviewAsync(window);

            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var currentViewModel = UiTestDriver.GetViewModel(window);
                    return currentViewModel.SearchVisible &&
                           currentViewModel.SearchQuery == "app" &&
                           UiTestDriver.GetRequiredControl<Border>(window, "SearchBarContainer").IsVisible;
                },
                "search state to be restored after leaving preview-only");

            await UiTestDriver.OpenPreviewAsync(window);

            var restoredViewModel = UiTestDriver.GetViewModel(window);
            Assert.True(restoredViewModel.SearchVisible);
            Assert.Equal("app", restoredViewModel.SearchQuery);
            Assert.True(UiTestDriver.GetRequiredControl<Border>(window, "SearchBarContainer").IsVisible);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task StickyPath_RemainsHiddenUntilPreviewScrollReachesFirstFileSection()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.TreeAndContent);

            var stickyHeader = UiTestDriver.GetRequiredControl<Border>(window, "PreviewStickyHeaderContainer");
            var stickyHeaderText = UiTestDriver.GetRequiredControl<TextBlock>(window, "PreviewStickyHeaderText");
            Assert.False(stickyHeader.IsVisible);

            await UiTestDriver.ScrollPreviewUntilStickyHeaderVisibleAsync(window);

            Assert.True(stickyHeader.IsVisible);
            Assert.False(string.IsNullOrWhiteSpace(stickyHeaderText.Text));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task StickyPath_DoesNotResizePreviewViewportWhenItAppears()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.TreeAndContent);

            var stickyHeader = UiTestDriver.GetRequiredControl<Border>(window, "PreviewStickyHeaderContainer");
            var scrollViewer = UiTestDriver.GetRequiredPreviewScrollViewer(window);
            var previewTextControl = UiTestDriver.GetRequiredControl<DevProjex.Avalonia.Controls.VirtualizedPreviewTextControl>(window, "PreviewTextControl");
            var lineNumbersControl = UiTestDriver.GetRequiredControl<DevProjex.Avalonia.Controls.VirtualizedLineNumbersControl>(window, "PreviewLineNumbersControl");

            await UiTestDriver.WaitForConditionAsync(
                window,
                () => scrollViewer.Viewport.Height > 0 &&
                      scrollViewer.Extent.Height > scrollViewer.Viewport.Height,
                "preview scroll range to become measurable before sticky header appears");
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);

            Assert.False(stickyHeader.IsVisible);
            Assert.Equal(0, previewTextControl.TopOverlayClipHeight);
            Assert.Equal(0, lineNumbersControl.TopOverlayClipHeight);
            var viewportHeightBeforeStickyHeader = scrollViewer.Viewport.Height;

            await UiTestDriver.ScrollPreviewUntilStickyHeaderVisibleAsync(window);
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);

            Assert.True(stickyHeader.IsVisible);
            Assert.True(previewTextControl.TopOverlayClipHeight > 0);
            Assert.Equal(previewTextControl.TopOverlayClipHeight, lineNumbersControl.TopOverlayClipHeight);
            Assert.InRange(
                Math.Abs(scrollViewer.Viewport.Height - viewportHeightBeforeStickyHeader),
                0,
                1);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task StickyPathCopyButton_IsVisibleInsideLineNumberCap_AndUsesCompactSize()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.TreeAndContent);
            await UiTestDriver.ScrollPreviewUntilStickyHeaderVisibleAsync(window);

            var stickyHeaderCap = UiTestDriver.GetRequiredControl<Border>(window, "PreviewStickyHeaderCap");
            var stickyHeaderContainer = UiTestDriver.GetRequiredControl<Border>(window, "PreviewStickyHeaderContainer");
            var stickyHeaderCopyButton = UiTestDriver.GetRequiredControl<Button>(window, "PreviewStickyHeaderCopyButton");
            var stickyHeaderText = UiTestDriver.GetRequiredControl<TextBlock>(window, "PreviewStickyHeaderText");
            var lineNumbersBackground = UiTestDriver.GetRequiredControl<Border>(window, "PreviewLineNumbersBackground");
            var previewIsland = UiTestDriver.GetRequiredControl<Border>(window, "PreviewIsland");

            var capBounds = UiTestDriver.GetBoundsInWindow(stickyHeaderCap, window);
            var headerBounds = UiTestDriver.GetBoundsInWindow(stickyHeaderContainer, window);
            var buttonBounds = UiTestDriver.GetBoundsInWindow(stickyHeaderCopyButton, window);
            var textBounds = UiTestDriver.GetBoundsInWindow(stickyHeaderText, window);
            var lineNumbersBounds = UiTestDriver.GetBoundsInWindow(lineNumbersBackground, window);

            Assert.True(stickyHeaderCap.IsVisible);
            Assert.True(stickyHeaderContainer.IsVisible);
            Assert.Equal(1, stickyHeaderContainer.Opacity);
            Assert.Same(previewIsland.Background, stickyHeaderCap.Background);
            Assert.Same(previewIsland.Background, stickyHeaderContainer.Background);
            Assert.InRange(Math.Abs(capBounds.Width - lineNumbersBounds.Width), 0, 1.5);
            Assert.InRange(Math.Abs(buttonBounds.Center.X - capBounds.Center.X), 0, 1.5);
            Assert.InRange(Math.Abs(buttonBounds.Width - 24), 0, 1.5);
            Assert.InRange(Math.Abs(buttonBounds.Height - 24), 0, 1.5);
            Assert.True(stickyHeaderCopyButton.BorderThickness.Left >= 0.9);
            Assert.True(stickyHeaderCopyButton.BorderThickness.Top >= 0.9);
            Assert.NotNull(stickyHeaderCopyButton.Background);
            Assert.NotNull(stickyHeaderCopyButton.BorderBrush);
            Assert.True(buttonBounds.Left >= capBounds.Left - 1);
            Assert.True(buttonBounds.Right <= capBounds.Right + 1);
            Assert.True(buttonBounds.Right <= headerBounds.Left + 2);
            Assert.True(capBounds.Right <= textBounds.Left + 2);
            Assert.Equal(PlacementMode.Right, ToolTip.GetPlacement(stickyHeaderCopyButton));
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task StickyPathCopyButton_CopiesVisibleSectionPath()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.TreeAndContent);
            await UiTestDriver.ScrollPreviewUntilStickyHeaderVisibleAsync(window);

            var stickyHeaderText = UiTestDriver.GetRequiredControl<TextBlock>(window, "PreviewStickyHeaderText");
            var expectedPayload = UiTestDriver.ComputeVisibleStickyHeaderCopyPayload(window);

            Assert.False(string.IsNullOrWhiteSpace(stickyHeaderText.Text));
            Assert.StartsWith($"{stickyHeaderText.Text}:", expectedPayload, StringComparison.Ordinal);

            await UiTestDriver.SetClipboardTextAsync(window, $"sticky-header-sentinel-{Guid.NewGuid():N}");
            await UiTestDriver.ClickPreviewStickyHeaderCopyButtonAsync(window);
            await UiTestDriver.WaitForClipboardTextAsync(window, expectedPayload);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task StickyPathCopyButton_DoesNotReplacePreviewDocument_OrChangeVisibleSection()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.TreeAndContent);
            await UiTestDriver.ScrollPreviewUntilStickyHeaderVisibleAsync(window);

            var viewModel = UiTestDriver.GetViewModel(window);
            var stickyHeaderText = UiTestDriver.GetRequiredControl<TextBlock>(window, "PreviewStickyHeaderText");
            var stickyHeaderTextBeforeCopy = stickyHeaderText.Text;
            var previewDocumentBeforeCopy = viewModel.PreviewDocument;
            var previewLineCountBeforeCopy = viewModel.PreviewLineCount;
            var expectedPayload = UiTestDriver.ComputeVisibleStickyHeaderCopyPayload(window);

            Assert.NotNull(previewDocumentBeforeCopy);
            Assert.False(string.IsNullOrWhiteSpace(stickyHeaderTextBeforeCopy));
            Assert.False(viewModel.IsPreviewLoading);
            Assert.False(viewModel.StatusBusy);

            await UiTestDriver.SetClipboardTextAsync(window, $"sticky-copy-stability-{Guid.NewGuid():N}");
            await UiTestDriver.ClickPreviewStickyHeaderCopyButtonAsync(window);
            await UiTestDriver.WaitForClipboardTextAsync(window, expectedPayload);
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);

            Assert.Same(previewDocumentBeforeCopy, viewModel.PreviewDocument);
            Assert.Equal(previewLineCountBeforeCopy, viewModel.PreviewLineCount);
            Assert.Equal(stickyHeaderTextBeforeCopy, stickyHeaderText.Text);
            Assert.False(viewModel.IsPreviewLoading);
            Assert.False(viewModel.StatusBusy);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task SpaceKey_DoesNotReinvokeLastToolbarButtonAfterPreviewButtonClick()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            var previewToggleButton = UiTestDriver.GetRequiredTopMenuControl<Button>(window, "PreviewToggleButton");
            await UiTestDriver.ClickAsync(window, previewToggleButton);
            await UiTestDriver.WaitForPreviewReadyAsync(window);

            await UiTestDriver.PressKeyAsync(window, Key.Space);
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 12);

            Assert.True(UiTestDriver.GetViewModel(window).IsPreviewMode);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewToggleButton_WhenClickedTwice_ClosesPreviewWorkspace()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.TogglePreviewViaToolbarAsync(window);
            await UiTestDriver.WaitForPreviewClosedAsync(window);

            var viewModel = UiTestDriver.GetViewModel(window);
            Assert.False(viewModel.IsPreviewMode);
            Assert.True(UiTestDriver.GetRequiredControl<Border>(window, "TreeIsland").IsVisible);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewModeButtons_SwitchBetweenTreeContentAndCombinedModes()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);

            await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Content);
            Assert.True(UiTestDriver.GetViewModel(window).IsPreviewContentSelected);

            await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.TreeAndContent);
            Assert.True(UiTestDriver.GetViewModel(window).IsPreviewTreeAndContentSelected);

            await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.Tree);
            Assert.True(UiTestDriver.GetViewModel(window).IsPreviewTreeSelected);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewModeSwitch_PreparesContentDuringAnimationAndPublishesAfterIt()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.SwitchPreviewModeAsync(
                window,
                PreviewContentMode.TreeAndContent);

            var viewModel = UiTestDriver.GetViewModel(window);
            var previewController = GetPreviewWorkspaceController(window);
            var previousDocument = viewModel.PreviewDocument;
            Assert.NotNull(previousDocument);
            var publicationObserved = false;
            var publicationObservedDuringAnimation = false;

            void OnViewModelPropertyChanged(
                object? sender,
                PropertyChangedEventArgs args)
            {
                if (args.PropertyName !=
                        nameof(MainWindowViewModel.PreviewDocument) ||
                    ReferenceEquals(previousDocument, viewModel.PreviewDocument))
                {
                    return;
                }

                publicationObserved = true;
                publicationObservedDuringAnimation |=
                    previewController.IsModeSwitchInProgress;
            }

            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            try
            {
                var switchTask =
                    previewController.SwitchModeAsync(PreviewContentMode.Tree);

                Assert.True(previewController.IsModeSwitchInProgress);
                Assert.True(viewModel.IsPreviewLoading);
                Assert.Same(previousDocument, viewModel.PreviewDocument);

                await switchTask;
            }
            finally
            {
                viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }

            Assert.True(publicationObserved);
            Assert.False(publicationObservedDuringAnimation);
            Assert.NotSame(previousDocument, viewModel.PreviewDocument);
            Assert.True(viewModel.IsPreviewTreeSelected);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewOnlyCloseButton_RestoresPlainTreeWorkspaceWhenNoSuspendedTools()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.HidePreviewTreeAsync(window);
            await UiTestDriver.ClosePreviewAsync(window);

            var viewModel = UiTestDriver.GetViewModel(window);
            Assert.False(viewModel.IsPreviewMode);
            Assert.False(viewModel.SearchVisible);
            Assert.False(viewModel.FilterVisible);
            Assert.True(UiTestDriver.GetRequiredControl<Border>(window, "TreeIsland").IsVisible);
            Assert.False(UiTestDriver.GetRequiredControl<Border>(window, "PreviewIsland").IsVisible);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewOpen_PreservesExistingFilterState()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenFilterAsync(window);

            var filterBar = UiTestDriver.GetRequiredControl<FilterBarView>(window, "FilterBar");
            await UiTestDriver.EnterTextAsync(window, Assert.IsType<TextBox>(filterBar.FilterBoxControl), "app");
            await UiTestDriver.WaitForFilterAppliedAsync(window, "app");

            await UiTestDriver.OpenPreviewAsync(window);

            var viewModel = UiTestDriver.GetViewModel(window);
            Assert.True(viewModel.FilterVisible);
            Assert.Equal("app", viewModel.NameFilter);
            Assert.True(UiTestDriver.GetRequiredControl<Border>(window, "FilterBarContainer").IsVisible);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PreviewOpen_PreservesExistingSearchState()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenSearchAsync(window);

            var searchBar = UiTestDriver.GetRequiredControl<SearchBarView>(window, "SearchBar");
            await UiTestDriver.EnterTextAsync(window, Assert.IsType<TextBox>(searchBar.SearchBoxControl), "app");
            await UiTestDriver.WaitForSearchAppliedAsync(window, "app");

            await UiTestDriver.OpenPreviewAsync(window);

            var viewModel = UiTestDriver.GetViewModel(window);
            Assert.True(viewModel.SearchVisible);
            Assert.Equal("app", viewModel.SearchQuery);
            Assert.True(UiTestDriver.GetRequiredControl<Border>(window, "SearchBarContainer").IsVisible);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task StickyPath_TextChangesWhenScrollingAcrossDifferentSections()
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(workspace.Project);

        try
        {
            await UiTestDriver.OpenPreviewAsync(window);
            await UiTestDriver.SwitchPreviewModeAsync(window, PreviewContentMode.TreeAndContent);

            await UiTestDriver.WaitForConditionAsync(
                window,
                () => UiTestDriver.GetViewModel(window).PreviewDocument is { Sections.Count: > 1 },
                "preview document with multiple file sections");

            await UiTestDriver.ScrollPreviewUntilStickyHeaderVisibleAsync(window);

            var stickyHeaderText = UiTestDriver.GetRequiredControl<TextBlock>(window, "PreviewStickyHeaderText");
            var firstStickyText = stickyHeaderText.Text;
            Assert.False(string.IsNullOrWhiteSpace(firstStickyText));

            await UiTestDriver.ScrollPreviewUntilStickyHeaderTextChangesAsync(window, firstStickyText!);

            Assert.False(string.IsNullOrWhiteSpace(stickyHeaderText.Text));
            Assert.NotEqual(firstStickyText, stickyHeaderText.Text);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    private static (string TreePart, string ContentPart) SplitTreeAndContentPayload(string payload)
    {
        var separatorIndex = payload.IndexOf('\u00A0');
        Assert.True(separatorIndex > 0, "Tree and content preview payload must contain the NBSP separator.");
        return (payload[..separatorIndex].TrimEnd('\r', '\n'), payload[separatorIndex..]);
    }

    private static string ExtractContentBodyFromTreeAndContentPayload(string payload)
    {
        var (_, contentPart) = SplitTreeAndContentPayload(payload);
        return contentPart.TrimStart('\u00A0', '\r', '\n');
    }

    private static ExportOutputMetrics ToRenderedStatusMetrics(ExportOutputMetrics metrics)
    {
        var rendered = PreviewSelectionMetricsPolicy.FormatStatusMetricsText(
            metrics,
            new StatusMetricLabels("Lines", "Chars", "Tokens"),
            useCompactMode: false);

        Assert.True(UiTestDriver.TryParseStatusMetrics(rendered, out var parsed));
        return parsed;
    }

    private static void AssertCloneTextContent(string payload)
    {
        Assert.Contains("CLONE-CONTENT-SENTINEL", payload, StringComparison.Ordinal);
        Assert.Contains("CLONE-DOCUMENTATION-SENTINEL", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("clone-image.bin:", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("No text content to copy", payload, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGitMetadataPath(string rootPath, string candidatePath)
    {
        var relativePath = Path.GetRelativePath(rootPath, candidatePath);
        return string.Equals(relativePath, ".git", StringComparison.OrdinalIgnoreCase) ||
               relativePath.StartsWith($".git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static void AssertReadableUnicodeJsonTree(string json, string expectedRootPath)
    {
        Assert.Contains("рабочая папка", json, StringComparison.Ordinal);
        Assert.Contains("\"Документы\"", json, StringComparison.Ordinal);
        Assert.Contains("Отчёт", json, StringComparison.Ordinal);
        Assert.Contains("Сводка.txt", json, StringComparison.Ordinal);
        Assert.Contains("Корень.txt", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u04", json, StringComparison.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            Path.GetFullPath(expectedRootPath).Replace('\\', '/'),
            document.RootElement.GetProperty("rootPath").GetString());
        var tree = document.RootElement.GetProperty("tree");
        Assert.Equal(
            ["Отчёт [финал].txt", "Сводка.txt"],
            tree.GetProperty("Документы").EnumerateArray().Select(static item => item.GetString()!).ToArray());
        Assert.Equal(
            ["Корень.txt"],
            tree.GetProperty("/").EnumerateArray().Select(static item => item.GetString()!).ToArray());
    }

    private static bool IsExpectedTreeFormat(string payload, ExportFormat format)
    {
        if (!payload.Contains("\u00A0", StringComparison.Ordinal))
            return false;

        return format switch
        {
            ExportFormat.Json => payload.TrimStart().StartsWith("{", StringComparison.Ordinal),
            ExportFormat.Xml => payload.StartsWith("<t ", StringComparison.Ordinal),
            ExportFormat.Markdown => payload.StartsWith("Root: ", StringComparison.Ordinal),
            _ => payload.Contains("\n├── ", StringComparison.Ordinal)
        };
    }

    private static DevProjex.Avalonia.Coordinators.PreviewWorkspaceController
        GetPreviewWorkspaceController(MainWindow window)
    {
        var field = typeof(MainWindow).GetField(
            "_previewWorkspaceController",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic);
        return Assert.IsType<
            DevProjex.Avalonia.Coordinators.PreviewWorkspaceController>(
            field?.GetValue(window));
    }

}
