using DevProjex.Application.Presentation;
using System.Text.RegularExpressions;
using DevProjex.Application.Services;
using DevProjex.Kernel.Abstractions;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowProjectLoadWorkflowUiTests
{

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
    public async Task CancelledBaselineBinaryOnlyFolderSelection_PublishesRootOnlyContentMetrics()
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
            Assert.Equal(1, expected.ContentMetrics.Lines);
            Assert.True(expected.ContentMetrics.Chars > 0);

            await UiTestDriver.WaitForStatusMetricsAsync(window, expected.TreeMetrics, expected.ContentMetrics, waitForSelectionRefreshIdle: false);
        }
        finally
        {
            analyzer.Release();
            await UiTestDriver.CloseWindowAsync(window);
        }
    }

    [AvaloniaFact]
    public async Task CancelledBaselineBinaryOnlyFileSelection_PublishesRootOnlyContentMetrics()
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
            Assert.Equal(1, expected.ContentMetrics.Lines);
            Assert.True(expected.ContentMetrics.Chars > 0);

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
                [WorkflowUiMutationStep.Extensions, WorkflowUiMutationStep.Ignore]);
            var finalExpectedB = await ApplyCombinedScenarioAsync(
                windowB,
                [WorkflowUiMutationStep.Ignore, WorkflowUiMutationStep.Extensions]);

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

            await ApplyPendingCombinedScenarioAsync(windowA, [WorkflowUiMutationStep.Extensions, WorkflowUiMutationStep.Ignore]);
            await ApplyPendingCombinedScenarioAsync(windowB, [WorkflowUiMutationStep.Ignore, WorkflowUiMutationStep.Extensions]);

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
                [WorkflowUiMutationStep.Extensions, WorkflowUiMutationStep.Ignore]);
            AssertMetricsChanged(baseline, changed, "combined all-sections scenario");

            await ApplyPendingCombinedScenarioAsync(window, [WorkflowUiMutationStep.Ignore, WorkflowUiMutationStep.Extensions]);
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

    private static async Task<ProjectLoadWorkflowRuntime.ProjectLoadWorkflowMetrics> ComputeProjectedMetricsFromSettingsAsync(
        string rootPath,
        MainWindow window)
    {
        await UiTestDriver.WaitForSelectionRefreshIdleAsync(window);

        var viewModel = UiTestDriver.GetViewModel(window);
        var scanRoots = Directory.EnumerateDirectories(rootPath)
            .Select(Path.GetFileName)
            .Where(static name => !string.IsNullOrEmpty(name))
            .Select(static name => name!)
            .ToHashSet(PathComparer.Default);
        var allowedExtensions = CollectCheckedNames(viewModel.Extensions, StringComparer.OrdinalIgnoreCase);
        var selectedIgnoreOptions = UiTestDriver.GetSelectedIgnoreOptionIds(window);

		return await ProjectLoadWorkflowRuntime.ComputeMetricsAsync(
			rootPath,
			scanRoots,
			allowedExtensions,
			selectedIgnoreOptions,
			CancellationToken.None,
			useContentRootAndRelativeHeaders: true);
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

			// Content transformations are not path filters: their labels carry a processing result,
			// not a count of what would be removed from the tree.
			if (ProjectPresentationCatalog.ContentTransformationOptionIds.Contains(option.Id))
				continue;

            var match = Regex.Match(option.Label, @"\((\d+)\)$");
            Assert.True(match.Success, $"Advanced ignore option '{option.Id}' must render a positive count in its label. Actual label: '{option.Label}'.");
            Assert.True(int.TryParse(match.Groups[1].Value, out var count) && count > 0,
                $"Advanced ignore option '{option.Id}' must never stay visible with a zero/invalid count. Actual label: '{option.Label}'.");
		}
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
