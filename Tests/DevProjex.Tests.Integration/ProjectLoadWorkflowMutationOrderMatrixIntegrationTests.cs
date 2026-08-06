using static DevProjex.Tests.Shared.ProjectLoadWorkflow.ProjectLoadWorkflowRefreshHarness;

namespace DevProjex.Tests.Integration;

public sealed class ProjectLoadWorkflowMutationOrderMatrixIntegrationTests
{
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ComputeFullRefreshSnapshot_SectionMutationOrdersConvergeWithoutLosingExplicitStates(
        string extensionScenarioName,
        string ignoreScenarioName,
        string[] mutationOrderNames)
    {
        var extensionScenario = Enum.Parse<WorkflowExtensionScenario>(extensionScenarioName);
        var ignoreScenario = Enum.Parse<WorkflowIgnoreScenario>(ignoreScenarioName);
        var mutationOrder = mutationOrderNames.Select(name => Enum.Parse<WorkflowMutationStep>(name)).ToArray();
        var rootPath = ProjectLoadWorkflowSharedWorkspace.RootPath;

        var services = CreateServices();
        var baselineSnapshot = services.Engine.ComputeFullRefreshSnapshot(
            CreateDefaultContext(rootPath) with { CaptureTreeInventory = true },
            CancellationToken.None);
        var targetScenario = CreateScenario(baselineSnapshot, extensionScenario, ignoreScenario);

        var directContext = CreateScenarioContext(rootPath, targetScenario) with { CaptureTreeInventory = true };
        var directSnapshot = services.Engine.ComputeFullRefreshSnapshot(directContext, CancellationToken.None);
        var directConverged = services.Engine.ComputeFullRefreshSnapshot(
            BuildConvergedContext(rootPath, directSnapshot, directContext) with { CaptureTreeInventory = true },
            CancellationToken.None);

        SelectionSnapshotContractAssertions.AssertAllSectionsConsistent(
            rootPath,
            services.IgnoreRulesService,
            directSnapshot);
        SelectionSnapshotContractAssertions.AssertAllSectionsConsistent(
            rootPath,
            services.IgnoreRulesService,
            directConverged);
        AssertEquivalentSnapshots(directSnapshot, directConverged);
        AssertScenarioSelectionContract(directConverged, targetScenario);

        var currentSnapshot = baselineSnapshot;
        var clientState = WorkflowClientSelectionState.FromSnapshot(baselineSnapshot);
        foreach (var step in mutationOrder)
        {
            var stepContext = clientState.CreateStepContext(rootPath, currentSnapshot, targetScenario, step);
            currentSnapshot = services.Engine.ComputeFullRefreshSnapshot(stepContext, CancellationToken.None);
            SelectionSnapshotContractAssertions.AssertAllSectionsConsistent(
                rootPath,
                services.IgnoreRulesService,
                currentSnapshot);
            clientState.MergeVisibleState(currentSnapshot);
        }

        var orderedConverged = services.Engine.ComputeFullRefreshSnapshot(
            clientState.CreateConvergedContext(rootPath, currentSnapshot),
            CancellationToken.None);
        clientState.MergeVisibleState(orderedConverged);

        var orderedSecondPass = services.Engine.ComputeFullRefreshSnapshot(
            clientState.CreateConvergedContext(rootPath, orderedConverged),
            CancellationToken.None);
        SelectionSnapshotContractAssertions.AssertAllSectionsConsistent(
            rootPath,
            services.IgnoreRulesService,
            orderedConverged);
        SelectionSnapshotContractAssertions.AssertAllSectionsConsistent(
            rootPath,
            services.IgnoreRulesService,
            orderedSecondPass);
        AssertEquivalentSnapshots(orderedConverged, orderedSecondPass);
        AssertVisibleAdvancedIgnoreOptionsCarryPositiveCounts(orderedConverged);
        AssertRequestedExtensionsStillChecked(orderedConverged, targetScenario);
        clientState.AssertExplicitlyUncheckedVisibleExtensionsRemainUnchecked(orderedConverged);
        AssertExplicitlyDisabledIgnoreOptionsRemainUnchecked(orderedConverged, targetScenario);

        var orderedMetrics = await ComputeMetricsFromSnapshotAsync(rootPath, orderedConverged);
        Assert.NotEqual(ExportOutputMetrics.Empty, orderedMetrics.TreeMetrics);
    }

    private static void AssertRequestedExtensionsStillChecked(
        SelectionRefreshSnapshot snapshot,
        SelectionRefreshScenario scenario)
    {
        foreach (var extensionName in scenario.RequestedExtensionNames)
        {
            var option = snapshot.ExtensionOptions.FirstOrDefault(option =>
                string.Equals(option.Name, extensionName, StringComparison.OrdinalIgnoreCase));
            if (option is not null)
                Assert.True(option.IsChecked, $"Requested extension '{extensionName}' must remain checked.");
        }
    }

    private static void AssertExplicitlyDisabledIgnoreOptionsRemainUnchecked(
        SelectionRefreshSnapshot snapshot,
        SelectionRefreshScenario scenario)
    {
        foreach (var optionId in scenario.ExplicitlyDisabledIgnoreOptions)
        {
            foreach (var option in snapshot.IgnoreOptions.Where(option => option.Id == optionId))
                Assert.False(option.IsChecked, $"Explicitly disabled ignore option '{optionId}' must stay unchecked.");
        }
    }

    public static IEnumerable<object[]> Cases()
    {
        var targetStates = new[]
        {
            (WorkflowExtensionScenario.AllVisible, WorkflowIgnoreScenario.Defaults),
            (WorkflowExtensionScenario.DocsOnly, WorkflowIgnoreScenario.Defaults),
            (WorkflowExtensionScenario.CodeOnly, WorkflowIgnoreScenario.Defaults),
            (WorkflowExtensionScenario.CodeAndDocs, WorkflowIgnoreScenario.AllOff),
            (WorkflowExtensionScenario.CodeAndDocs, WorkflowIgnoreScenario.GitIgnoreOff),
            (WorkflowExtensionScenario.MarkdownOnly, WorkflowIgnoreScenario.EmptyFoldersOff),
            (WorkflowExtensionScenario.CodeOnly, WorkflowIgnoreScenario.FileDynamicsOff),
            (WorkflowExtensionScenario.DocsOnly, WorkflowIgnoreScenario.DirectoryDynamicsOff),
            (WorkflowExtensionScenario.JsonOnly, WorkflowIgnoreScenario.MixedManual),
            (WorkflowExtensionScenario.AllVisible, WorkflowIgnoreScenario.MixedManual)
        };

        var orders = new[]
        {
            new[] { WorkflowMutationStep.Extensions, WorkflowMutationStep.Ignore },
            new[] { WorkflowMutationStep.Ignore, WorkflowMutationStep.Extensions }
        };

        foreach (var state in targetStates)
        {
            foreach (var order in orders)
                yield return [state.Item1.ToString(), state.Item2.ToString(), order.Select(step => step.ToString()).ToArray()];
        }
    }

    private sealed class WorkflowClientSelectionState
    {
        private readonly Dictionary<string, bool> _extensionStates;
        private readonly HashSet<string> _explicitlyUncheckedExtensions = new(StringComparer.OrdinalIgnoreCase);

        private WorkflowClientSelectionState(Dictionary<string, bool> extensionStates)
        {
            _extensionStates = extensionStates;
        }

        public static WorkflowClientSelectionState FromSnapshot(SelectionRefreshSnapshot snapshot) =>
            new(BuildExtensionOptionStateCache(snapshot));

        public SelectionRefreshContext CreateStepContext(
            string rootPath,
            SelectionRefreshSnapshot snapshot,
            SelectionRefreshScenario targetScenario,
            WorkflowMutationStep step)
        {
            var context = CreateConvergedContext(rootPath, snapshot);
            return step switch
            {
                WorkflowMutationStep.Extensions => ApplyExtensionScenario(context, snapshot, targetScenario),
                WorkflowMutationStep.Ignore => context with
                {
                    IgnoreSelectionInitialized = targetScenario.IgnoreSelectionInitialized,
                    IgnoreSelectionCache = new HashSet<IgnoreOptionId>(targetScenario.RequestedIgnoreOptions),
                    IgnoreOptionStateCache = BuildIgnoreStateCache(
                        targetScenario.RequestedIgnoreOptions,
                        targetScenario.ExplicitlyDisabledIgnoreOptions),
                    IgnoreAllPreference = targetScenario.IgnoreAllPreference
                },
                _ => throw new ArgumentOutOfRangeException(nameof(step), step, null)
            };
        }

        public SelectionRefreshContext CreateConvergedContext(
            string rootPath,
            SelectionRefreshSnapshot snapshot)
        {
            var context = CreateContextFromSnapshot(rootPath, snapshot);
            return context with
            {
                ExtensionsSelectionCache = CollectCheckedVisibleExtensionNames(snapshot),
                ExtensionOptionStateCache = new Dictionary<string, bool>(
                    _extensionStates,
                    StringComparer.OrdinalIgnoreCase),
                CaptureTreeInventory = true
            };
        }

        public void MergeVisibleState(SelectionRefreshSnapshot snapshot)
        {
            foreach (var option in snapshot.ExtensionOptions)
                _extensionStates[option.Name] = option.IsChecked;
        }

        public void AssertExplicitlyUncheckedVisibleExtensionsRemainUnchecked(SelectionRefreshSnapshot snapshot)
        {
            foreach (var option in snapshot.ExtensionOptions)
            {
                if (_explicitlyUncheckedExtensions.Contains(option.Name))
                    Assert.False(option.IsChecked, $"Explicitly unchecked extension '{option.Name}' must stay unchecked.");
            }
        }

        private SelectionRefreshContext ApplyExtensionScenario(
            SelectionRefreshContext context,
            SelectionRefreshSnapshot snapshot,
            SelectionRefreshScenario scenario)
        {
            var allVisible = scenario.ExtensionScenario == WorkflowExtensionScenario.AllVisible;
            foreach (var option in snapshot.ExtensionOptions)
            {
                var isChecked = allVisible || scenario.RequestedExtensionNames.Contains(option.Name);
                _extensionStates[option.Name] = isChecked;
                TrackExplicitUncheckedState(option.Name, isChecked);
            }

            return context with
            {
                AllExtensionsChecked = allVisible,
                ExtensionsSelectionInitialized = !allVisible,
                ExtensionsSelectionCache = CollectCheckedVisibleExtensionNames(snapshot),
                ExtensionOptionStateCache = new Dictionary<string, bool>(
                    _extensionStates,
                    StringComparer.OrdinalIgnoreCase)
            };
        }

        private HashSet<string> CollectCheckedVisibleExtensionNames(SelectionRefreshSnapshot snapshot)
        {
            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var option in snapshot.ExtensionOptions)
            {
                if (_extensionStates.TryGetValue(option.Name, out var isChecked) && isChecked)
                    selected.Add(option.Name);
            }

            return selected;
        }

        private void TrackExplicitUncheckedState(string name, bool isChecked)
        {
            if (isChecked)
                _explicitlyUncheckedExtensions.Remove(name);
            else
                _explicitlyUncheckedExtensions.Add(name);
        }
    }
}
