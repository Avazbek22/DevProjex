using System.Text.RegularExpressions;
using DevProjex.Application.Services;
using DevProjex.Kernel.Abstractions;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowProjectLoadWorkflowUiTests
{
    [AvaloniaFact]
    public async Task CancelledBaselineRootSelection_PublishesTreeMetricsBeforeContentMetricsFinish()
    {
        using var project = UiTestProject.CreateWithProjectLoadWorkflowWorkspace();
        var analyzer = new BlockingFileContentAnalyzer(new FileContentAnalyzer());
        var window = await CreateWindowDuringInitialMetricsWarmupAsync(project, analyzer);

        try
        {
            await UiTestDriver.RaiseButtonClickAsync(UiTestDriver.GetRequiredStatusCancelButton(window));
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => !UiTestDriver.GetViewModel(window).StatusBusy,
                "initial baseline metrics cancellation to settle");

            var rootNode = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
            var checkBox = await UiTestDriver.WaitForTreeNodeCheckBoxAsync(window, rootNode.DisplayName);
            await UiTestDriver.ClickAsync(window, checkBox);

            ExportOutputMetrics pendingTreeMetrics = default;
            ExportOutputMetrics pendingContentMetrics = default;
            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var viewModel = UiTestDriver.GetViewModel(window);
                    if (!viewModel.StatusBusy)
                        return false;

                    if (!UiTestDriver.TryGetCurrentStatusMetrics(window, out var actualTreeMetrics, out var actualContentMetrics))
                        return false;

                    pendingTreeMetrics = actualTreeMetrics;
                    pendingContentMetrics = actualContentMetrics;
                    return actualTreeMetrics != ExportOutputMetrics.Empty &&
                           actualContentMetrics == ExportOutputMetrics.Empty;
                },
                "tree metrics to appear while selected content metrics are still warming up");

            analyzer.Release();
            var expected = await ComputeExpectedAppliedMetricsAsync(window);

            Assert.Equal(expected.TreeMetrics, pendingTreeMetrics);
            Assert.Equal(ExportOutputMetrics.Empty, pendingContentMetrics);
            Assert.NotEqual(ExportOutputMetrics.Empty, expected.ContentMetrics);

            await UiTestDriver.WaitForStatusMetricsAsync(
                window,
                expected.TreeMetrics,
                expected.ContentMetrics,
                waitForSelectionRefreshIdle: false);
        }
        finally
        {
            analyzer.Release();
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task SelectingRootWhileBaselineMetricsAreStillRunning_AutoTransitionsToSelectionMetrics()
    {
        using var project = UiTestProject.CreateWithProjectLoadWorkflowWorkspace();
        var analyzer = new BlockingFileContentAnalyzer(new FileContentAnalyzer());
        var window = await CreateWindowDuringInitialMetricsWarmupAsync(project, analyzer);

        try
        {
            var rootNode = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
            var checkBox = await UiTestDriver.WaitForTreeNodeCheckBoxAsync(window, rootNode.DisplayName);
            await UiTestDriver.ClickAsync(window, checkBox);

            ExportOutputMetrics pendingTreeMetrics = default;
            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var viewModel = UiTestDriver.GetViewModel(window);
                    if (!viewModel.StatusBusy)
                        return false;

                    if (!UiTestDriver.TryGetCurrentStatusMetrics(window, out var actualTreeMetrics, out var actualContentMetrics))
                        return false;

                    pendingTreeMetrics = actualTreeMetrics;
                    return actualTreeMetrics != ExportOutputMetrics.Empty &&
                           actualContentMetrics == ExportOutputMetrics.Empty;
                },
                "selection metrics to replace the in-flight baseline without a second click");

            analyzer.Release();
            var expected = await ComputeExpectedAppliedMetricsAsync(window);

            Assert.Equal(expected.TreeMetrics, pendingTreeMetrics);
            Assert.True(rootNode.IsChecked);
            Assert.NotEqual(ExportOutputMetrics.Empty, expected.ContentMetrics);

            await UiTestDriver.WaitForStatusMetricsAsync(
                window,
                expected.TreeMetrics,
                expected.ContentMetrics,
                waitForSelectionRefreshIdle: false);
        }
        finally
        {
            analyzer.Release();
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task CancelledBaselineFolderSelection_PublishesSubtreeTreeMetricsBeforeContentMetricsFinish()
    {
        using var project = UiTestProject.CreateWithProjectLoadWorkflowWorkspace();
        var analyzer = new BlockingFileContentAnalyzer(new FileContentAnalyzer());
        var window = await CreateWindowDuringInitialMetricsWarmupAsync(project, analyzer);

        try
        {
            await UiTestDriver.RaiseButtonClickAsync(UiTestDriver.GetRequiredStatusCancelButton(window));
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => !UiTestDriver.GetViewModel(window).StatusBusy,
                "initial baseline metrics cancellation to settle");

            var rootNode = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
            rootNode.IsExpanded = true;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);

            var checkBox = await UiTestDriver.WaitForTreeNodeCheckBoxAsync(window, "src");
            await UiTestDriver.ClickAsync(window, checkBox);

            ExportOutputMetrics pendingTreeMetrics = default;
            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var viewModel = UiTestDriver.GetViewModel(window);
                    if (!viewModel.StatusBusy)
                        return false;

                    if (!UiTestDriver.TryGetCurrentStatusMetrics(window, out var actualTreeMetrics, out var actualContentMetrics))
                        return false;

                    pendingTreeMetrics = actualTreeMetrics;
                    return actualTreeMetrics != ExportOutputMetrics.Empty &&
                           actualContentMetrics == ExportOutputMetrics.Empty;
                },
                "folder subtree tree metrics to appear while selected content metrics are still warming up");

            analyzer.Release();
            var expected = await ComputeExpectedAppliedMetricsAsync(window);

            Assert.Equal(expected.TreeMetrics, pendingTreeMetrics);
            Assert.NotEqual(ExportOutputMetrics.Empty, expected.ContentMetrics);
            await UiTestDriver.WaitForStatusMetricsAsync(
                window,
                expected.TreeMetrics,
                expected.ContentMetrics,
                waitForSelectionRefreshIdle: false);
        }
        finally
        {
            analyzer.Release();
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task CancelledBaselineClearingRootSelection_RestoresWholeWorkspaceMetricsInsteadOfZeroingStatusBar()
    {
        using var project = UiTestProject.CreateWithProjectLoadWorkflowWorkspace();
        var analyzer = new BlockingFileContentAnalyzer(new FileContentAnalyzer());
        var window = await CreateWindowDuringInitialMetricsWarmupAsync(project, analyzer);

        try
        {
            await UiTestDriver.RaiseButtonClickAsync(UiTestDriver.GetRequiredStatusCancelButton(window));
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => !UiTestDriver.GetViewModel(window).StatusBusy,
                "initial baseline metrics cancellation to settle");

            var rootNode = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
            var rootCheckBox = await UiTestDriver.WaitForTreeNodeCheckBoxAsync(window, rootNode.DisplayName);

            await UiTestDriver.ClickAsync(window, rootCheckBox);
            analyzer.Release();

            var selectedExpected = await ComputeExpectedAppliedMetricsAsync(window);
            Assert.NotEqual(ExportOutputMetrics.Empty, selectedExpected.ContentMetrics);
            await UiTestDriver.WaitForStatusMetricsAsync(
                window,
                selectedExpected.TreeMetrics,
                selectedExpected.ContentMetrics,
                waitForSelectionRefreshIdle: false);

            await UiTestDriver.ClickAsync(window, rootCheckBox);

            var fullWorkspaceExpected = await ComputeExpectedAppliedMetricsAsync(window);
            Assert.NotEqual(ExportOutputMetrics.Empty, fullWorkspaceExpected.TreeMetrics);
            Assert.NotEqual(ExportOutputMetrics.Empty, fullWorkspaceExpected.ContentMetrics);
            await UiTestDriver.WaitForStatusMetricsAsync(
                window,
                fullWorkspaceExpected.TreeMetrics,
                fullWorkspaceExpected.ContentMetrics,
                waitForSelectionRefreshIdle: false);
        }
        finally
        {
            analyzer.Release();
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task CancelledBaselineFolderSelection_WithBinaryFiles_StillPublishesFinalContentMetrics()
    {
        using var project = UiTestProject.CreateWithMixedTextAndBinaryMetricsWorkspace();
        var analyzer = new BlockingFileContentAnalyzer(new FileContentAnalyzer());
        var window = await CreateWindowDuringInitialMetricsWarmupAsync(project, analyzer);

        try
        {
            await UiTestDriver.RaiseButtonClickAsync(UiTestDriver.GetRequiredStatusCancelButton(window));
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => !UiTestDriver.GetViewModel(window).StatusBusy,
                "initial baseline metrics cancellation to settle");

            var rootNode = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
            rootNode.IsExpanded = true;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);

            var checkBox = await UiTestDriver.WaitForTreeNodeCheckBoxAsync(window, "src");
            await UiTestDriver.ClickAsync(window, checkBox);

            analyzer.Release();

            var expected = await ComputeExpectedAppliedMetricsAsync(window);
            Assert.NotEqual(ExportOutputMetrics.Empty, expected.TreeMetrics);
            Assert.NotEqual(ExportOutputMetrics.Empty, expected.ContentMetrics);

            await UiTestDriver.WaitForStatusMetricsAsync(
                window,
                expected.TreeMetrics,
                expected.ContentMetrics,
                waitForSelectionRefreshIdle: false);
        }
        finally
        {
            analyzer.Release();
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task CancelledBaselineRootSelection_WithBinaryFiles_StillPublishesFinalContentMetrics()
    {
        using var project = UiTestProject.CreateWithMixedTextAndBinaryMetricsWorkspace();
        var analyzer = new BlockingFileContentAnalyzer(new FileContentAnalyzer());
        var window = await CreateWindowDuringInitialMetricsWarmupAsync(project, analyzer);

        try
        {
            await UiTestDriver.RaiseButtonClickAsync(UiTestDriver.GetRequiredStatusCancelButton(window));
            await UiTestDriver.WaitForConditionAsync(
                window,
                () => !UiTestDriver.GetViewModel(window).StatusBusy,
                "initial baseline metrics cancellation to settle");

            var rootNode = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
            var rootCheckBox = await UiTestDriver.WaitForTreeNodeCheckBoxAsync(window, rootNode.DisplayName);
            await UiTestDriver.ClickAsync(window, rootCheckBox);

            analyzer.Release();

            var expected = await ComputeExpectedAppliedMetricsAsync(window);
            Assert.NotEqual(ExportOutputMetrics.Empty, expected.TreeMetrics);
            Assert.NotEqual(ExportOutputMetrics.Empty, expected.ContentMetrics);

            await UiTestDriver.WaitForStatusMetricsAsync(
                window,
                expected.TreeMetrics,
                expected.ContentMetrics,
                waitForSelectionRefreshIdle: false);
        }
        finally
        {
            analyzer.Release();
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task CancelledBaselineBinaryOnlyFolderSelection_PublishesTreeMetricsWithZeroContentMetrics()
    {
        using var project = UiTestProject.CreateWithMixedTextAndBinaryMetricsWorkspace();
        var analyzer = new BlockingFileContentAnalyzer(new FileContentAnalyzer());
        var window = await CreateWindowDuringInitialMetricsWarmupAsync(project, analyzer);

        try
        {
            await UiTestDriver.RaiseButtonClickAsync(UiTestDriver.GetRequiredStatusCancelButton(window));
            await UiTestDriver.WaitForConditionAsync(window, () => !UiTestDriver.GetViewModel(window).StatusBusy, "baseline cancellation to settle");

            await ExpandChildPathAsync(window, "src", "assets", "raw");
            var checkBox = await UiTestDriver.WaitForTreeNodeCheckBoxAsync(window, "raw");
            await UiTestDriver.ClickAsync(window, checkBox);

            analyzer.Release();

            var expected = await ComputeExpectedAppliedMetricsAsync(window);
            Assert.NotEqual(ExportOutputMetrics.Empty, expected.TreeMetrics);
            Assert.Equal(ExportOutputMetrics.Empty, expected.ContentMetrics);

            await UiTestDriver.WaitForStatusMetricsAsync(window, expected.TreeMetrics, expected.ContentMetrics, waitForSelectionRefreshIdle: false);
        }
        finally
        {
            analyzer.Release();
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task CancelledBaselineBinaryOnlyFileSelection_PublishesZeroContentMetrics()
    {
        using var project = UiTestProject.CreateWithMixedTextAndBinaryMetricsWorkspace();
        var analyzer = new BlockingFileContentAnalyzer(new FileContentAnalyzer());
        var window = await CreateWindowDuringInitialMetricsWarmupAsync(project, analyzer);

        try
        {
            await UiTestDriver.RaiseButtonClickAsync(UiTestDriver.GetRequiredStatusCancelButton(window));
            await UiTestDriver.WaitForConditionAsync(window, () => !UiTestDriver.GetViewModel(window).StatusBusy, "baseline cancellation to settle");

            await ExpandChildPathAsync(window, "src", "assets");
            var checkBox = await UiTestDriver.WaitForTreeNodeCheckBoxAsync(window, "image.bin");
            await UiTestDriver.ClickAsync(window, checkBox);

            analyzer.Release();

            var expected = await ComputeExpectedAppliedMetricsAsync(window);
            Assert.NotEqual(ExportOutputMetrics.Empty, expected.TreeMetrics);
            Assert.Equal(ExportOutputMetrics.Empty, expected.ContentMetrics);

            await UiTestDriver.WaitForStatusMetricsAsync(window, expected.TreeMetrics, expected.ContentMetrics, waitForSelectionRefreshIdle: false);
        }
        finally
        {
            analyzer.Release();
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task CancelledBaselineTextOnlyFileSelection_PublishesFinalContentMetrics()
    {
        using var project = UiTestProject.CreateWithMixedTextAndBinaryMetricsWorkspace();
        var analyzer = new BlockingFileContentAnalyzer(new FileContentAnalyzer());
        var window = await CreateWindowDuringInitialMetricsWarmupAsync(project, analyzer);

        try
        {
            await UiTestDriver.RaiseButtonClickAsync(UiTestDriver.GetRequiredStatusCancelButton(window));
            await UiTestDriver.WaitForConditionAsync(window, () => !UiTestDriver.GetViewModel(window).StatusBusy, "baseline cancellation to settle");

            await ExpandChildPathAsync(window, "docs");
            var checkBox = await UiTestDriver.WaitForTreeNodeCheckBoxAsync(window, "guide.md");
            await UiTestDriver.ClickAsync(window, checkBox);

            analyzer.Release();

            var expected = await ComputeExpectedAppliedMetricsAsync(window);
            Assert.NotEqual(ExportOutputMetrics.Empty, expected.TreeMetrics);
            Assert.NotEqual(ExportOutputMetrics.Empty, expected.ContentMetrics);

            await UiTestDriver.WaitForStatusMetricsAsync(window, expected.TreeMetrics, expected.ContentMetrics, waitForSelectionRefreshIdle: false);
        }
        finally
        {
            analyzer.Release();
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task SelectingLatestFolderWhileSelectionMetricsAreInFlight_PublishesLatestMetricsOnly()
    {
        using var project = UiTestProject.CreateWithMixedTextAndBinaryMetricsWorkspace();
        var analyzer = new BlockingFileContentAnalyzer(new FileContentAnalyzer());
        var window = await CreateWindowDuringInitialMetricsWarmupAsync(project, analyzer);

        try
        {
            await UiTestDriver.RaiseButtonClickAsync(UiTestDriver.GetRequiredStatusCancelButton(window));
            await UiTestDriver.WaitForConditionAsync(window, () => !UiTestDriver.GetViewModel(window).StatusBusy, "baseline cancellation to settle");

            await ExpandChildPathAsync(window, "src");
            var srcCheckBox = await UiTestDriver.WaitForTreeNodeCheckBoxAsync(window, "src");
            var docsCheckBox = await UiTestDriver.WaitForTreeNodeCheckBoxAsync(window, "docs");

            await UiTestDriver.ClickAsync(window, srcCheckBox);
            await UiTestDriver.WaitForConditionAsync(window, () => UiTestDriver.GetViewModel(window).StatusBusy, "selection metrics to start for src");
            await UiTestDriver.ClickAsync(window, srcCheckBox);
            await UiTestDriver.ClickAsync(window, docsCheckBox);

            analyzer.Release();

            var expected = await ComputeExpectedAppliedMetricsAsync(window);
            Assert.NotEqual(ExportOutputMetrics.Empty, expected.TreeMetrics);
            Assert.NotEqual(ExportOutputMetrics.Empty, expected.ContentMetrics);

            await UiTestDriver.WaitForStatusMetricsAsync(window, expected.TreeMetrics, expected.ContentMetrics, waitForSelectionRefreshIdle: false);
        }
        finally
        {
            analyzer.Release();
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task CancelledBaselineClearingFolderSelection_RestoresWholeWorkspaceMetrics()
    {
        using var project = UiTestProject.CreateWithMixedTextAndBinaryMetricsWorkspace();
        var analyzer = new BlockingFileContentAnalyzer(new FileContentAnalyzer());
        var window = await CreateWindowDuringInitialMetricsWarmupAsync(project, analyzer);

        try
        {
            await UiTestDriver.RaiseButtonClickAsync(UiTestDriver.GetRequiredStatusCancelButton(window));
            await UiTestDriver.WaitForConditionAsync(window, () => !UiTestDriver.GetViewModel(window).StatusBusy, "baseline cancellation to settle");

            var docsCheckBox = await UiTestDriver.WaitForTreeNodeCheckBoxAsync(window, "docs");
            await UiTestDriver.ClickAsync(window, docsCheckBox);

            analyzer.Release();

            var selectedExpected = await ComputeExpectedAppliedMetricsAsync(window);
            Assert.NotEqual(ExportOutputMetrics.Empty, selectedExpected.ContentMetrics);
            await UiTestDriver.WaitForStatusMetricsAsync(window, selectedExpected.TreeMetrics, selectedExpected.ContentMetrics, waitForSelectionRefreshIdle: false);

            await UiTestDriver.ClickAsync(window, docsCheckBox);

            var fullWorkspaceExpected = await ComputeExpectedAppliedMetricsAsync(window);
            Assert.NotEqual(ExportOutputMetrics.Empty, fullWorkspaceExpected.TreeMetrics);
            Assert.NotEqual(ExportOutputMetrics.Empty, fullWorkspaceExpected.ContentMetrics);
            await UiTestDriver.WaitForStatusMetricsAsync(window, fullWorkspaceExpected.TreeMetrics, fullWorkspaceExpected.ContentMetrics, waitForSelectionRefreshIdle: false);
        }
        finally
        {
            analyzer.Release();
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ActiveBaselineFolderSelection_WithBinaryFiles_AutoTransitionsToFinalContentMetrics()
    {
        using var project = UiTestProject.CreateWithMixedTextAndBinaryMetricsWorkspace();
        var analyzer = new BlockingFileContentAnalyzer(new FileContentAnalyzer());
        var window = await CreateWindowDuringInitialMetricsWarmupAsync(project, analyzer);

        try
        {
            var srcCheckBox = await UiTestDriver.WaitForTreeNodeCheckBoxAsync(window, "src");
            await UiTestDriver.ClickAsync(window, srcCheckBox);

            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    if (!UiTestDriver.GetViewModel(window).StatusBusy)
                        return false;

                    return UiTestDriver.TryGetCurrentStatusMetrics(window, out var actualTreeMetrics, out var actualContentMetrics) &&
                           actualTreeMetrics != ExportOutputMetrics.Empty &&
                           actualContentMetrics == ExportOutputMetrics.Empty;
                },
                "tree metrics to appear while active baseline hands off to selected content metrics");

            analyzer.Release();

            var expected = await ComputeExpectedAppliedMetricsAsync(window);
            Assert.NotEqual(ExportOutputMetrics.Empty, expected.ContentMetrics);
            await UiTestDriver.WaitForStatusMetricsAsync(window, expected.TreeMetrics, expected.ContentMetrics, waitForSelectionRefreshIdle: false);
        }
        finally
        {
            analyzer.Release();
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task CancelledBaselineRapidRootToggle_DoesNotLeaveBusyStateAndRestoresFullMetrics()
    {
        using var project = UiTestProject.CreateWithMixedTextAndBinaryMetricsWorkspace();
        var analyzer = new BlockingFileContentAnalyzer(new FileContentAnalyzer());
        var window = await CreateWindowDuringInitialMetricsWarmupAsync(project, analyzer);

        try
        {
            await UiTestDriver.RaiseButtonClickAsync(UiTestDriver.GetRequiredStatusCancelButton(window));
            await UiTestDriver.WaitForConditionAsync(window, () => !UiTestDriver.GetViewModel(window).StatusBusy, "baseline cancellation to settle");

            var rootNode = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
            var rootCheckBox = await UiTestDriver.WaitForTreeNodeCheckBoxAsync(window, rootNode.DisplayName);
            await UiTestDriver.ClickAsync(window, rootCheckBox);
            await UiTestDriver.ClickAsync(window, rootCheckBox);

            analyzer.Release();

            var expected = await ComputeExpectedAppliedMetricsAsync(window);
            Assert.NotEqual(ExportOutputMetrics.Empty, expected.TreeMetrics);
            Assert.NotEqual(ExportOutputMetrics.Empty, expected.ContentMetrics);
            await UiTestDriver.WaitForStatusMetricsAsync(window, expected.TreeMetrics, expected.ContentMetrics, waitForSelectionRefreshIdle: false);
            Assert.False(UiTestDriver.GetViewModel(window).StatusBusy);
        }
        finally
        {
            analyzer.Release();
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task InitialLoad_ProjectWorkflowWorkspace_StatusBarMatchesExpectedExportMetrics()
    {
        using var project = UiTestProject.CreateWithProjectLoadWorkflowWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            var expected = await ComputeExpectedAppliedMetricsAsync(window);
            await UiTestDriver.WaitForStatusMetricsAsync(window, expected.TreeMetrics, expected.ContentMetrics);

            var viewModel = UiTestDriver.GetViewModel(window);
            Assert.Contains(viewModel.RootFolders, option => string.Equals(option.Name, "docs", StringComparison.Ordinal));
            Assert.Contains(viewModel.RootFolders, option => string.Equals(option.Name, "samples", StringComparison.Ordinal));
            Assert.Contains(viewModel.RootFolders, option => string.Equals(option.Name, "src", StringComparison.Ordinal));
            Assert.Contains(viewModel.IgnoreOptions, option => option.Id == IgnoreOptionId.UseGitIgnore && option.IsChecked);
            AssertVisibleAdvancedIgnoreOptionsCarryPositiveCounts(viewModel.IgnoreOptions);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PendingCombinedSectionChanges_DoNotAffectAppliedStatusMetricsUntilApply()
    {
        using var project = UiTestProject.CreateWithProjectLoadWorkflowWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            var initialExpected = await ComputeExpectedAppliedMetricsAsync(window);
            await UiTestDriver.WaitForStatusMetricsAsync(window, initialExpected.TreeMetrics, initialExpected.ContentMetrics);

            await UiTestDriver.ClickRootFolderCheckBoxAsync(window, "docs");
            await UiTestDriver.ClickExtensionCheckBoxAsync(window, ".md");
            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.EmptyFiles);
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 10);

            var pendingExpected = await ComputeProjectedMetricsFromSettingsAsync(project.RootPath, window);
            Assert.NotEqual(initialExpected.TreeMetrics, pendingExpected.TreeMetrics);

            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var viewModel = UiTestDriver.GetViewModel(window);
                    return UiTestDriver.TryParseStatusMetrics(viewModel.StatusTreeStatsText, out var actualTreeMetrics) &&
                           UiTestDriver.TryParseStatusMetrics(viewModel.StatusContentStatsText, out var actualContentMetrics) &&
                           actualTreeMetrics == initialExpected.TreeMetrics &&
                           actualContentMetrics == initialExpected.ContentMetrics;
                },
                "pending settings to leave the applied status metrics unchanged");

            var appliedExpected = await ApplySettingsAndWaitForExpectedMetricsAsync(window);
            Assert.Equal(pendingExpected.TreeMetrics, appliedExpected.TreeMetrics);
            Assert.Equal(pendingExpected.ContentMetrics, appliedExpected.ContentMetrics);
            AssertVisibleAdvancedIgnoreOptionsCarryPositiveCounts(UiTestDriver.GetViewModel(window).IgnoreOptions);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ApplySettings_DisablingAllIgnoreRules_RebuildsStatusMetricsForFullWorkspace()
    {
        using var project = UiTestProject.CreateWithProjectLoadWorkflowWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            var ignoreAllCheckBox = UiTestDriver.GetRequiredControl<CheckBox>(window, "IgnoreAllCheckBox");
            if (!UiTestDriver.GetViewModel(window).AllIgnoreChecked)
            {
                await UiTestDriver.ClickAsync(window, ignoreAllCheckBox);
                await UiTestDriver.WaitForConditionAsync(
                    window,
                    () => UiTestDriver.GetViewModel(window).AllIgnoreChecked,
                    "all ignore rules to become selected before testing the all-off transition");
                await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
            }

            await UiTestDriver.ClickAsync(window, ignoreAllCheckBox);

            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var rootNames = UiTestDriver.GetViewModel(window).RootFolders.Select(option => option.Name).ToArray();
                    return rootNames.Contains(".cache", StringComparer.Ordinal) &&
                           rootNames.Contains("generated", StringComparer.Ordinal) &&
                           rootNames.Contains("logs", StringComparer.Ordinal) &&
                           rootNames.Contains("node_modules", StringComparer.Ordinal);
                },
                "all root folders hidden by ignore rules to reappear after disabling all ignore rules");

            await ApplySettingsAndWaitForExpectedMetricsAsync(window);

            var viewModel = UiTestDriver.GetViewModel(window);
            Assert.Contains(viewModel.Extensions, option => string.Equals(option.Name, ".js", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(viewModel.Extensions, option => string.Equals(option.Name, ".log", StringComparison.OrdinalIgnoreCase));
            AssertVisibleAdvancedIgnoreOptionsCarryPositiveCounts(viewModel.IgnoreOptions);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task RapidMixedMutations_ApplyUsesLatestSelectionsInsteadOfIntermediateState()
    {
        using var project = UiTestProject.CreateWithProjectLoadWorkflowWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            await UiTestDriver.ClickRootFolderCheckBoxAsync(window, "docs");
            await UiTestDriver.ClickExtensionCheckBoxAsync(window, ".md");
            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.EmptyFiles);
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 6);
            var intermediateExpected = await ComputeProjectedMetricsFromSettingsAsync(project.RootPath, window);

            await UiTestDriver.ClickRootFolderCheckBoxAsync(window, "docs");
            await UiTestDriver.ClickRootFolderCheckBoxAsync(window, "samples");
            await UiTestDriver.ClickExtensionCheckBoxAsync(window, ".json");
            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.ExtensionlessFiles);
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 10);

            var pendingFinalExpected = await ComputeProjectedMetricsFromSettingsAsync(project.RootPath, window);
            Assert.NotEqual(intermediateExpected.TreeMetrics, pendingFinalExpected.TreeMetrics);

            var finalExpected = await ApplySettingsAndWaitForExpectedMetricsAsync(window);
            Assert.Equal(pendingFinalExpected.TreeMetrics, finalExpected.TreeMetrics);
            Assert.Equal(pendingFinalExpected.ContentMetrics, finalExpected.ContentMetrics);

            var viewModel = UiTestDriver.GetViewModel(window);
            Assert.True(UiTestDriver.TryParseStatusMetrics(viewModel.StatusTreeStatsText, out var finalTreeMetrics));
            Assert.NotEqual(intermediateExpected.TreeMetrics, finalTreeMetrics);
            AssertVisibleAdvancedIgnoreOptionsCarryPositiveCounts(viewModel.IgnoreOptions);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task BurstMixedMutations_QueuedRefreshesConvergeBeforeApply()
    {
        using var project = UiTestProject.CreateWithProjectLoadWorkflowWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            var baseline = await ComputeExpectedAppliedMetricsAsync(window);
            await UiTestDriver.WaitForStatusMetricsAsync(window, baseline.TreeMetrics, baseline.ContentMetrics);

            for (var cycle = 0; cycle < 3; cycle++)
            {
                await UiTestDriver.ClickRootFolderCheckBoxAsync(window, "docs");
                await UiTestDriver.ClickExtensionCheckBoxAsync(window, ".md");
                await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.EmptyFiles);
                await UiTestDriver.ClickRootFolderCheckBoxAsync(window, "docs");
                await UiTestDriver.ClickRootFolderCheckBoxAsync(window, "samples");
                await UiTestDriver.ClickExtensionCheckBoxAsync(window, ".json");
                await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.ExtensionlessFiles);
            }

            await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);
            AssertVisibleAdvancedIgnoreOptionsCarryPositiveCounts(UiTestDriver.GetViewModel(window).IgnoreOptions);

            var pendingExpected = await ComputeProjectedMetricsFromSettingsAsync(project.RootPath, window);
            AssertMetricsChanged(baseline, pendingExpected, "burst mixed root/extension/ignore changes");

            var appliedExpected = await ApplySettingsAndWaitForExpectedMetricsAsync(window);
            Assert.Equal(pendingExpected.TreeMetrics, appliedExpected.TreeMetrics);
            Assert.Equal(pendingExpected.ContentMetrics, appliedExpected.ContentMetrics);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task LiveSectionRefresh_NeverShowsAdvancedIgnoreOptionWithoutPositiveCount()
    {
        using var project = UiTestProject.CreateWithProjectLoadWorkflowWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            AssertVisibleAdvancedIgnoreOptionsCarryPositiveCounts(UiTestDriver.GetViewModel(window).IgnoreOptions);

            await UiTestDriver.ClickRootFolderCheckBoxAsync(window, "docs");
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);
            AssertVisibleAdvancedIgnoreOptionsCarryPositiveCounts(UiTestDriver.GetViewModel(window).IgnoreOptions);

            await UiTestDriver.ClickExtensionCheckBoxAsync(window, ".md");
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);
            AssertVisibleAdvancedIgnoreOptionsCarryPositiveCounts(UiTestDriver.GetViewModel(window).IgnoreOptions);

			await UiTestDriver.ClickAsync(window, UiTestDriver.GetRequiredControl<CheckBox>(window, "IgnoreAllCheckBox"));
			await UiTestDriver.WaitForSettledFramesAsync(frameCount: 10);
			await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.HideSecrets);
			await UiTestDriver.WaitForConditionAsync(
				window,
				() =>
				{
					var hideSecrets = UiTestDriver.GetViewModel(window).IgnoreOptions.First(
						static option => option.Id == IgnoreOptionId.HideSecrets);
					return hideSecrets.IsChecked && IsCompletedHideSecretsLabel(hideSecrets.Label);
				},
				"enabled Hide Secrets to publish an honest completed state");
            AssertVisibleAdvancedIgnoreOptionsCarryPositiveCounts(UiTestDriver.GetViewModel(window).IgnoreOptions);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task PendingGitIgnoreToggle_RevealsAdditionalRootFoldersWithoutChangingAppliedStatusBarUntilApply()
    {
        using var project = UiTestProject.CreateWithProjectLoadWorkflowWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            var baseline = await ComputeExpectedAppliedMetricsAsync(window);
            await UiTestDriver.WaitForStatusMetricsAsync(window, baseline.TreeMetrics, baseline.ContentMetrics);

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.UseGitIgnore);
            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var rootNames = UiTestDriver.GetViewModel(window).RootFolders.Select(option => option.Name).ToArray();
                    return rootNames.Contains("generated", StringComparer.Ordinal) &&
                           rootNames.Contains("logs", StringComparer.Ordinal);
                },
                "gitignored root folders to appear in the pending selection state");

            await UiTestDriver.WaitForConditionAsync(
                window,
                () =>
                {
                    var viewModel = UiTestDriver.GetViewModel(window);
                    return UiTestDriver.TryParseStatusMetrics(viewModel.StatusTreeStatsText, out var actualTreeMetrics) &&
                           UiTestDriver.TryParseStatusMetrics(viewModel.StatusContentStatsText, out var actualContentMetrics) &&
                           actualTreeMetrics == baseline.TreeMetrics &&
                           actualContentMetrics == baseline.ContentMetrics;
                },
                "pending gitignore change to leave the applied status bar unchanged");

            await ApplySettingsAndWaitForExpectedMetricsAsync(window);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ApplySettings_GitIgnoreRoundTrip_RestoresBaselineMetrics()
    {
        using var project = UiTestProject.CreateWithProjectLoadWorkflowWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            var baseline = await ComputeExpectedAppliedMetricsAsync(window);
            await UiTestDriver.WaitForStatusMetricsAsync(window, baseline.TreeMetrics, baseline.ContentMetrics);

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.UseGitIgnore);
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 10);
            var disabledExpected = await ApplySettingsAndWaitForExpectedMetricsAsync(window);
            AssertMetricsChanged(baseline, disabledExpected, "gitignore round-trip disable");

            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.UseGitIgnore);
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 10);
            var restoredExpected = await ApplySettingsAndWaitForExpectedMetricsAsync(window);
            Assert.Equal(baseline.TreeMetrics, restoredExpected.TreeMetrics);
            Assert.Equal(baseline.ContentMetrics, restoredExpected.ContentMetrics);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ApplySettings_EquivalentFinalSelections_ProduceSameMetricsAcrossDifferentMutationOrders()
    {
        using var project = UiTestProject.CreateWithProjectLoadWorkflowWorkspace();

        var windowA = await UiTestDriver.CreateLoadedMainWindowAsync(project);
        var windowB = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            var finalExpectedA = await ApplyCombinedScenarioAsync(
                windowA,
                [WorkflowUiMutationStep.Roots, WorkflowUiMutationStep.Extensions, WorkflowUiMutationStep.Ignore]);
            var finalExpectedB = await ApplyCombinedScenarioAsync(
                windowB,
                [WorkflowUiMutationStep.Ignore, WorkflowUiMutationStep.Extensions, WorkflowUiMutationStep.Roots]);

            Assert.Equal(finalExpectedA.TreeMetrics, finalExpectedB.TreeMetrics);
            Assert.Equal(finalExpectedA.ContentMetrics, finalExpectedB.ContentMetrics);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(windowA);
            await UiTestDriver.CloseWindowAsync(windowB);
        }
    }

    [AvaloniaFact]
    public async Task PendingEquivalentFinalSelections_LeaveAppliedMetricsStableAcrossDifferentMutationOrders()
    {
        using var project = UiTestProject.CreateWithProjectLoadWorkflowWorkspace();

        var windowA = await UiTestDriver.CreateLoadedMainWindowAsync(project);
        var windowB = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            var baselineA = await ComputeExpectedAppliedMetricsAsync(windowA);
            await UiTestDriver.WaitForStatusMetricsAsync(windowA, baselineA.TreeMetrics, baselineA.ContentMetrics);
            var baselineB = await ComputeExpectedAppliedMetricsAsync(windowB);
            await UiTestDriver.WaitForStatusMetricsAsync(windowB, baselineB.TreeMetrics, baselineB.ContentMetrics);

            await ApplyPendingCombinedScenarioAsync(windowA, [WorkflowUiMutationStep.Roots, WorkflowUiMutationStep.Extensions, WorkflowUiMutationStep.Ignore]);
            await ApplyPendingCombinedScenarioAsync(windowB, [WorkflowUiMutationStep.Ignore, WorkflowUiMutationStep.Extensions, WorkflowUiMutationStep.Roots]);

            await UiTestDriver.WaitForStatusMetricsAsync(windowA, baselineA.TreeMetrics, baselineA.ContentMetrics);
            await UiTestDriver.WaitForStatusMetricsAsync(windowB, baselineB.TreeMetrics, baselineB.ContentMetrics);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(windowA);
            await UiTestDriver.CloseWindowAsync(windowB);
        }
    }

    [AvaloniaFact]
    public async Task ApplySettings_CombinedRoundTripAcrossAllSections_RestoresBaseline()
    {
        using var project = UiTestProject.CreateWithProjectLoadWorkflowWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            var baseline = await ComputeExpectedAppliedMetricsAsync(window);
            await UiTestDriver.WaitForStatusMetricsAsync(window, baseline.TreeMetrics, baseline.ContentMetrics);

            var changed = await ApplyCombinedScenarioAsync(
                window,
                [WorkflowUiMutationStep.Roots, WorkflowUiMutationStep.Extensions, WorkflowUiMutationStep.Ignore]);
            AssertMetricsChanged(baseline, changed, "combined all-sections scenario");

            await ApplyPendingCombinedScenarioAsync(window, [WorkflowUiMutationStep.Ignore, WorkflowUiMutationStep.Extensions, WorkflowUiMutationStep.Roots]);
            var restored = await ApplySettingsAndWaitForExpectedMetricsAsync(window);
            Assert.Equal(baseline.TreeMetrics, restored.TreeMetrics);
            Assert.Equal(baseline.ContentMetrics, restored.ContentMetrics);
            AssertVisibleAdvancedIgnoreOptionsCarryPositiveCounts(UiTestDriver.GetViewModel(window).IgnoreOptions);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ApplySettings_EachCheckedRootFolderToggle_RebuildsMetricsAndRestoresBaseline()
    {
        using var project = UiTestProject.CreateWithProjectLoadWorkflowWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            var baseline = await ComputeExpectedAppliedMetricsAsync(window);
            await UiTestDriver.WaitForStatusMetricsAsync(window, baseline.TreeMetrics, baseline.ContentMetrics);

            var checkedRoots = UiTestDriver.GetViewModel(window).RootFolders
                .Where(option => option.IsChecked)
                // Some checked root folders can be neutralized by active ignore/file filters.
                // The round-trip metric assertion targets roots that contain applied content
                // in the seeded workflow workspace.
                .Where(option => option.Name is "docs" or "samples" or "src" or "stealth-root")
                .Select(option => option.Name)
                .ToArray();
            Assert.NotEmpty(checkedRoots);

            foreach (var rootName in checkedRoots)
            {
                await UiTestDriver.ClickRootFolderCheckBoxAsync(window, rootName);
                await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);

                var expectedWithoutRoot = await ComputeProjectedMetricsFromSettingsAsync(project.RootPath, window);
                AssertMetricsChanged(baseline, expectedWithoutRoot, $"root folder '{rootName}'");

                var appliedWithoutRoot = await ApplySettingsAndWaitForExpectedMetricsAsync(window);
                Assert.False(UiTestDriver.GetViewModel(window).RootFolders.First(option => option.Name == rootName).IsChecked);
                Assert.Equal(expectedWithoutRoot.TreeMetrics, appliedWithoutRoot.TreeMetrics);
                Assert.Equal(expectedWithoutRoot.ContentMetrics, appliedWithoutRoot.ContentMetrics);

                await UiTestDriver.ClickRootFolderCheckBoxAsync(window, rootName);
                await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);

                var restoredExpected = await ComputeProjectedMetricsFromSettingsAsync(project.RootPath, window);
                Assert.Equal(baseline.TreeMetrics, restoredExpected.TreeMetrics);
                Assert.Equal(baseline.ContentMetrics, restoredExpected.ContentMetrics);

                var appliedRestored = await ApplySettingsAndWaitForExpectedMetricsAsync(window);
                Assert.True(UiTestDriver.GetViewModel(window).RootFolders.First(option => option.Name == rootName).IsChecked);
                Assert.Equal(baseline.TreeMetrics, appliedRestored.TreeMetrics);
                Assert.Equal(baseline.ContentMetrics, appliedRestored.ContentMetrics);
            }
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ApplySettings_EachCheckedExtensionToggle_RebuildsMetricsAndRestoresBaseline()
    {
        using var project = UiTestProject.CreateWithProjectLoadWorkflowWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            var baseline = await ComputeExpectedAppliedMetricsAsync(window);
            await UiTestDriver.WaitForStatusMetricsAsync(window, baseline.TreeMetrics, baseline.ContentMetrics);

            var checkedExtensions = UiTestDriver.GetViewModel(window).Extensions
                .Where(option => option.IsChecked)
                // Some visible extensions can still be fully neutralized by other active
                // filters (for example .gitignore itself while UseGitIgnore is checked).
                // This round-trip test intentionally targets extensions that must affect
                // the applied export snapshot on the seeded workflow workspace.
                .Where(option => option.Name is ".cs" or ".json" or ".md" or ".txt")
                .Select(option => option.Name)
                .ToArray();
            Assert.NotEmpty(checkedExtensions);

            foreach (var extension in checkedExtensions)
            {
                await UiTestDriver.ClickExtensionCheckBoxAsync(window, extension);
                await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);

                var expectedWithoutExtension = await ComputeProjectedMetricsFromSettingsAsync(project.RootPath, window);
                AssertMetricsChanged(baseline, expectedWithoutExtension, $"extension '{extension}'");

                var appliedWithoutExtension = await ApplySettingsAndWaitForExpectedMetricsAsync(window);
                Assert.False(UiTestDriver.GetViewModel(window).Extensions.First(option => option.Name == extension).IsChecked);
                Assert.Equal(expectedWithoutExtension.TreeMetrics, appliedWithoutExtension.TreeMetrics);
                Assert.Equal(expectedWithoutExtension.ContentMetrics, appliedWithoutExtension.ContentMetrics);

                await UiTestDriver.ClickExtensionCheckBoxAsync(window, extension);
                await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);

                var restoredExpected = await ComputeProjectedMetricsFromSettingsAsync(project.RootPath, window);
                Assert.Equal(baseline.TreeMetrics, restoredExpected.TreeMetrics);
                Assert.Equal(baseline.ContentMetrics, restoredExpected.ContentMetrics);

                var appliedRestored = await ApplySettingsAndWaitForExpectedMetricsAsync(window);
                Assert.True(UiTestDriver.GetViewModel(window).Extensions.First(option => option.Name == extension).IsChecked);
                Assert.Equal(baseline.TreeMetrics, appliedRestored.TreeMetrics);
                Assert.Equal(baseline.ContentMetrics, appliedRestored.ContentMetrics);
            }
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task ApplySettings_EachCheckedIgnoreToggle_RebuildsMetricsAgainstFreshWindow()
    {
        using var project = UiTestProject.CreateWithProjectLoadWorkflowWorkspace();
        var candidateWindow = await UiTestDriver.CreateLoadedMainWindowAsync(project);
        IgnoreOptionId[] candidateIgnoreIds;

        try
        {
            candidateIgnoreIds = UiTestDriver.GetViewModel(candidateWindow).IgnoreOptions
                .Where(option => option.IsChecked)
                // Not every checked ignore rule changes the currently applied tree.
                // Some only widen availability in other sections by revealing extra roots.
                // This UI test focuses on the options that must change status-bar metrics.
                .Where(option => option.Id is IgnoreOptionId.HiddenFiles
                    or IgnoreOptionId.DotFiles
                    or IgnoreOptionId.EmptyFolders
                    or IgnoreOptionId.EmptyFiles
                    or IgnoreOptionId.ExtensionlessFiles)
                .Select(option => option.Id)
                .ToArray();
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(candidateWindow);
        }

        Assert.NotEmpty(candidateIgnoreIds);

        foreach (var optionId in candidateIgnoreIds)
        {
            var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

            try
            {
                var baseline = await ComputeExpectedAppliedMetricsAsync(window);
                await UiTestDriver.WaitForStatusMetricsAsync(window, baseline.TreeMetrics, baseline.ContentMetrics);

                await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, optionId);
                await UiTestDriver.WaitForSettledFramesAsync(frameCount: 10);

                var selectedAfterToggle = UiTestDriver.GetSelectedIgnoreOptionIds(window);
                Assert.DoesNotContain(optionId, selectedAfterToggle);

                var expectedWithoutOption = await ComputeProjectedMetricsFromSettingsAsync(project.RootPath, window);
                AssertMetricsChanged(baseline, expectedWithoutOption, $"ignore option '{optionId}'");

                var appliedWithoutOption = await ApplySettingsAndWaitForExpectedMetricsAsync(window);
                Assert.False(UiTestDriver.GetViewModel(window).IgnoreOptions.First(option => option.Id == optionId).IsChecked);
                Assert.Equal(expectedWithoutOption.TreeMetrics, appliedWithoutOption.TreeMetrics);
                Assert.Equal(expectedWithoutOption.ContentMetrics, appliedWithoutOption.ContentMetrics);
                AssertVisibleAdvancedIgnoreOptionsCarryPositiveCounts(UiTestDriver.GetViewModel(window).IgnoreOptions);
            }
            finally
            {
                await UiTestDriver.CloseWindowAsync(window);
            }
        }
    }

    [AvaloniaFact]
    public async Task RevertingPendingChangesAcrossSections_KeepsAppliedMetricsStable()
    {
        using var project = UiTestProject.CreateWithProjectLoadWorkflowWorkspace();
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(project);

        try
        {
            var baseline = await ComputeExpectedAppliedMetricsAsync(window);
            await UiTestDriver.WaitForStatusMetricsAsync(window, baseline.TreeMetrics, baseline.ContentMetrics);

            await UiTestDriver.ClickRootFolderCheckBoxAsync(window, "docs");
            await UiTestDriver.ClickRootFolderCheckBoxAsync(window, "docs");

            var extension = UiTestDriver.GetViewModel(window).Extensions.First(option => option.IsChecked).Name;
            await UiTestDriver.ClickExtensionCheckBoxAsync(window, extension);
            await UiTestDriver.ClickExtensionCheckBoxAsync(window, extension);

            var reversibleIgnore = UiTestDriver.GetViewModel(window).IgnoreOptions
                .Where(option => option.IsChecked)
                .Select(option => option.Id)
                .First(id => id is IgnoreOptionId.EmptyFiles
                    or IgnoreOptionId.EmptyFolders
                    or IgnoreOptionId.ExtensionlessFiles
                    or IgnoreOptionId.DotFiles
                    or IgnoreOptionId.HiddenFiles);
            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, reversibleIgnore);
            await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, reversibleIgnore);

            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 10);

            var revertedExpected = await ComputeProjectedMetricsFromSettingsAsync(project.RootPath, window);
            Assert.Equal(baseline.TreeMetrics, revertedExpected.TreeMetrics);
            Assert.Equal(baseline.ContentMetrics, revertedExpected.ContentMetrics);

            var appliedRestored = await ApplySettingsAndWaitForExpectedMetricsAsync(window);
            Assert.Equal(baseline.TreeMetrics, appliedRestored.TreeMetrics);
            Assert.Equal(baseline.ContentMetrics, appliedRestored.ContentMetrics);
            AssertVisibleAdvancedIgnoreOptionsCarryPositiveCounts(UiTestDriver.GetViewModel(window).IgnoreOptions);
        }
        finally
        {
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    private static async Task<ProjectLoadWorkflowRuntime.ProjectLoadWorkflowMetrics> ComputeExpectedAppliedMetricsAsync(
        MainWindow window)
    {
        return await UiTestDriver.ComputeAppliedExportMetricsAsync(window, CancellationToken.None);
    }

    private static async Task<TreeNodeViewModel> ExpandChildPathAsync(MainWindow window, params string[] childPath)
    {
        var current = Assert.Single(UiTestDriver.GetViewModel(window).TreeNodes);
        foreach (var segment in childPath)
        {
            current.IsExpanded = true;
            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);
            current = Assert.Single(current.Children, child => string.Equals(child.DisplayName, segment, StringComparison.Ordinal));
        }

        current.IsExpanded = true;
        await UiTestDriver.WaitForSettledFramesAsync(frameCount: 4);

        return current;
    }

    private static async Task<MainWindow> CreateWindowDuringInitialMetricsWarmupAsync(
        UiTestProject project,
        BlockingFileContentAnalyzer analyzer)
    {
        var window = await UiTestDriver.CreateLoadedMainWindowAsync(
            project,
            waitForInitialSettingsPane: false,
            configureServices: services => ReplaceFileContentServices(services, analyzer),
            waitForStatusIdle: false);

        await UiTestDriver.WaitForConditionAsync(
            window,
            () => analyzer.MetricsRequestCount > 0 && UiTestDriver.GetViewModel(window).StatusBusy,
            "initial background metrics calculation to start");

        return window;
    }

    private static AvaloniaAppServices ReplaceFileContentServices(
        AvaloniaAppServices services,
        IFileContentAnalyzer analyzer)
    {
        var contentExportService = new SelectedContentExportService(analyzer);
        return services with
        {
            FileContentAnalyzer = analyzer,
            ContentExportService = contentExportService,
            TreeAndContentExportService = new TreeAndContentExportService(services.TreeExportService, contentExportService),
            PreviewDocumentBuilder = new PreviewDocumentBuilder(analyzer)
        };
    }

    private static async Task<ProjectLoadWorkflowRuntime.ProjectLoadWorkflowMetrics> ComputeProjectedMetricsFromSettingsAsync(
        string rootPath,
        MainWindow window)
    {
        await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);

        var viewModel = UiTestDriver.GetViewModel(window);
        var selectedRoots = CollectCheckedNames(viewModel.RootFolders, PathComparer.Default);
        var allowedExtensions = CollectCheckedNames(viewModel.Extensions, StringComparer.OrdinalIgnoreCase);
        var selectedIgnoreOptions = UiTestDriver.GetSelectedIgnoreOptionIds(window);

        return await ProjectLoadWorkflowRuntime.ComputeMetricsAsync(
            rootPath,
            selectedRoots,
            allowedExtensions,
            selectedIgnoreOptions,
            CancellationToken.None);
    }

    private static HashSet<string> CollectCheckedNames(
        IEnumerable<SelectionOptionViewModel> options,
        IEqualityComparer<string> comparer)
    {
        var selected = new HashSet<string>(comparer);
        foreach (var option in options)
        {
            if (option.IsChecked)
                selected.Add(option.Name);
        }

        return selected;
    }

    private static void AssertMetricsChanged(
        ProjectLoadWorkflowRuntime.ProjectLoadWorkflowMetrics baseline,
        ProjectLoadWorkflowRuntime.ProjectLoadWorkflowMetrics candidate,
        string mutationName)
    {
        Assert.True(
            baseline.TreeMetrics != candidate.TreeMetrics || baseline.ContentMetrics != candidate.ContentMetrics,
            $"Mutation '{mutationName}' must change either tree or content metrics.");
    }

    private static async Task<ProjectLoadWorkflowRuntime.ProjectLoadWorkflowMetrics> ApplyCombinedScenarioAsync(
        MainWindow window,
        IReadOnlyList<WorkflowUiMutationStep> order)
    {
        await ApplyPendingCombinedScenarioAsync(window, order);
        return await ApplySettingsAndWaitForExpectedMetricsAsync(window);
    }

    private static async Task<ProjectLoadWorkflowRuntime.ProjectLoadWorkflowMetrics> ApplySettingsAndWaitForExpectedMetricsAsync(
        MainWindow window)
    {
        await UiTestDriver.ClickApplySettingsAsync(window);

        var expected = await ComputeExpectedAppliedMetricsAsync(window);
        await UiTestDriver.WaitForStatusMetricsAsync(
            window,
            expected.TreeMetrics,
            expected.ContentMetrics,
            waitForSelectionRefreshIdle: false);
        return expected;
    }

    private static async Task ApplyPendingCombinedScenarioAsync(
        MainWindow window,
        IReadOnlyList<WorkflowUiMutationStep> order)
    {
        foreach (var step in order)
        {
            switch (step)
            {
                case WorkflowUiMutationStep.Roots:
                    await UiTestDriver.ClickRootFolderCheckBoxAsync(window, "samples");
                    break;
                case WorkflowUiMutationStep.Extensions:
                    await UiTestDriver.ClickExtensionCheckBoxAsync(window, ".json");
                    break;
                case WorkflowUiMutationStep.Ignore:
                    await UiTestDriver.ClickIgnoreOptionCheckBoxAsync(window, IgnoreOptionId.EmptyFiles);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(step), step, null);
            }

            await UiTestDriver.WaitForSettledFramesAsync(frameCount: 8);
        }
    }

    private enum WorkflowUiMutationStep
    {
        Roots,
        Extensions,
        Ignore
    }

    private static void AssertVisibleAdvancedIgnoreOptionsCarryPositiveCounts(
        IEnumerable<IgnoreOptionViewModel> options)
    {
        foreach (var option in options)
        {
            if (option.Id is IgnoreOptionId.UseGitIgnore or IgnoreOptionId.SmartIgnore)
                continue;

			if (option.Id == IgnoreOptionId.HideSecrets)
			{
				if (option.IsChecked)
				{
					Assert.True(
						IsCompletedHideSecretsLabel(option.Label),
						$"Enabled Hide Secrets must render an honest completed state. Actual label: '{option.Label}'.");
					Assert.DoesNotMatch(@"\(0\)$", option.Label);
				}
				else
				{
					Assert.False(
						Regex.IsMatch(option.Label, @"\(\d+\)$"),
						$"Disabled Hide Secrets must not imply that a content scan ran. Actual label: '{option.Label}'.");
                }

                continue;
            }

            var match = Regex.Match(option.Label, @"\((\d+)\)$");
            Assert.True(match.Success, $"Advanced ignore option '{option.Id}' must render a positive count in its label. Actual label: '{option.Label}'.");
            Assert.True(int.TryParse(match.Groups[1].Value, out var count) && count > 0,
                $"Advanced ignore option '{option.Id}' must never stay visible with a zero/invalid count. Actual label: '{option.Label}'.");
		}
	}

	private static bool IsCompletedHideSecretsLabel(string label)
	{
		var positiveCount = Regex.Match(label, @"\(([1-9]\d*)\)$");
		return positiveCount.Success ||
		       label.EndsWith(" — no detected values", StringComparison.Ordinal);
	}

    private sealed class BlockingFileContentAnalyzer(IFileContentAnalyzer innerAnalyzer) : IFileContentAnalyzer
    {
        private readonly TaskCompletionSource<bool> _releaseMetrics = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _metricsRequestCount;

        public int MetricsRequestCount => Volatile.Read(ref _metricsRequestCount);

        public void Release() => _releaseMetrics.TrySetResult(true);

        public ValueTask<bool> IsTextFileAsync(string path, CancellationToken cancellationToken = default) =>
            innerAnalyzer.IsTextFileAsync(path, cancellationToken);

        public async ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _metricsRequestCount);
            await _releaseMetrics.Task.WaitAsync(cancellationToken);
            return await innerAnalyzer.GetTextFileMetricsAsync(path, cancellationToken);
        }

        public ValueTask<TextFileContent?> TryReadAsTextAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            innerAnalyzer.TryReadAsTextAsync(path, cancellationToken);

        public ValueTask<TextFileContent?> TryReadAsTextAsync(
            string path,
            long maxSizeForFullRead,
            CancellationToken cancellationToken = default) =>
            innerAnalyzer.TryReadAsTextAsync(path, maxSizeForFullRead, cancellationToken);
    }

}
