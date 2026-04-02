using System.Text.RegularExpressions;
using DevProjex.Tests.Shared.ProjectLoadWorkflow;

namespace DevProjex.Tests.UI;

[Collection(UiWorkspaceCollection.Name)]
public sealed class MainWindowProjectLoadWorkflowUiTests
{
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

            var match = Regex.Match(option.Label, @"\((\d+)\)$");
            Assert.True(match.Success, $"Advanced ignore option '{option.Id}' must render a positive count in its label. Actual label: '{option.Label}'.");
            Assert.True(int.TryParse(match.Groups[1].Value, out var count) && count > 0,
                $"Advanced ignore option '{option.Id}' must never stay visible with a zero/invalid count. Actual label: '{option.Label}'.");
        }
    }

}
