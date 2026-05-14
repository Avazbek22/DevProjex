using DevProjex.Tests.Shared.ProjectLoadWorkflow;
using static DevProjex.Tests.Shared.ProjectLoadWorkflow.ProjectLoadWorkflowRefreshHarness;

namespace DevProjex.Tests.Integration;

public sealed class ProjectLoadWorkflowMutationOrderMatrixIntegrationTests
{
    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ComputeFullRefreshSnapshot_SectionMutationOrdersConvergeWithoutLosingExplicitStates(
        string rootScenarioName,
        string extensionScenarioName,
        string ignoreScenarioName,
        string[] mutationOrderNames)
    {
        var rootScenario = Enum.Parse<WorkflowRootScenario>(rootScenarioName);
        var extensionScenario = Enum.Parse<WorkflowExtensionScenario>(extensionScenarioName);
        var ignoreScenario = Enum.Parse<WorkflowIgnoreScenario>(ignoreScenarioName);
        var mutationOrder = mutationOrderNames.Select(name => Enum.Parse<WorkflowMutationStep>(name)).ToArray();

        var rootPath = ProjectLoadWorkflowSharedWorkspace.RootPath;

        var services = CreateServices();
        var baselineSnapshot = services.Engine.ComputeFullRefreshSnapshot(
            CreateDefaultContext(rootPath),
            CancellationToken.None);
        var targetScenario = CreateScenario(baselineSnapshot, rootScenario, extensionScenario, ignoreScenario);

        var directSnapshot = services.Engine.ComputeFullRefreshSnapshot(
            CreateScenarioContext(rootPath, targetScenario),
            CancellationToken.None);
        var directConverged = services.Engine.ComputeFullRefreshSnapshot(
            BuildConvergedContext(rootPath, directSnapshot),
            CancellationToken.None);

        AssertEquivalentSnapshots(directSnapshot, directConverged);
        AssertScenarioSelectionContract(directConverged, targetScenario);

        var currentSnapshot = baselineSnapshot;
        var clientState = WorkflowClientSelectionState.FromSnapshot(baselineSnapshot);
        foreach (var step in mutationOrder)
        {
            var stepContext = clientState.CreateStepContext(rootPath, currentSnapshot, targetScenario, step);
            currentSnapshot = services.Engine.ComputeFullRefreshSnapshot(stepContext, CancellationToken.None);
            clientState.MergeVisibleState(currentSnapshot);
        }

        var orderedConverged = services.Engine.ComputeFullRefreshSnapshot(
            clientState.CreateConvergedContext(rootPath, currentSnapshot),
            CancellationToken.None);
        clientState.MergeVisibleState(orderedConverged);

        var orderedSecondPass = services.Engine.ComputeFullRefreshSnapshot(
            clientState.CreateConvergedContext(rootPath, orderedConverged),
            CancellationToken.None);
        AssertEquivalentSnapshots(orderedConverged, orderedSecondPass);
        AssertVisibleAdvancedIgnoreOptionsCarryPositiveCounts(orderedConverged);
        AssertRequestedSelectionsStillChecked(orderedConverged, targetScenario);
        clientState.AssertExplicitlyUncheckedVisibleEntriesRemainUnchecked(orderedConverged);
        AssertExplicitlyDisabledIgnoreOptionsRemainUnchecked(orderedConverged, targetScenario);

        var orderedMetrics = await ComputeMetricsFromSnapshotAsync(rootPath, orderedConverged);
        Assert.NotEqual(ExportOutputMetrics.Empty, orderedMetrics.TreeMetrics);
    }

    private static void AssertRequestedSelectionsStillChecked(
        SelectionRefreshSnapshot snapshot,
        SelectionRefreshScenario scenario)
    {
        if (snapshot.RootOptions is not null)
        {
            foreach (var rootName in scenario.RequestedRootNames)
            {
                var option = snapshot.RootOptions.FirstOrDefault(option =>
                    string.Equals(option.Name, rootName, StringComparison.Ordinal));
                if (option is not null)
                    Assert.True(option.IsChecked, $"Requested root '{rootName}' must remain checked.");
            }
        }

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
            (WorkflowRootScenario.AllVisible, WorkflowExtensionScenario.AllVisible, WorkflowIgnoreScenario.Defaults),
            (WorkflowRootScenario.DocsOnly, WorkflowExtensionScenario.DocsOnly, WorkflowIgnoreScenario.Defaults),
            (WorkflowRootScenario.SrcOnly, WorkflowExtensionScenario.CodeOnly, WorkflowIgnoreScenario.Defaults),
            (WorkflowRootScenario.SamplesOnly, WorkflowExtensionScenario.CodeAndDocs, WorkflowIgnoreScenario.AllOff),
            (WorkflowRootScenario.DocsAndSrc, WorkflowExtensionScenario.CodeAndDocs, WorkflowIgnoreScenario.GitIgnoreOff),
            (WorkflowRootScenario.DocsAndSamples, WorkflowExtensionScenario.MarkdownOnly, WorkflowIgnoreScenario.EmptyFoldersOff),
            (WorkflowRootScenario.AllVisible, WorkflowExtensionScenario.CodeOnly, WorkflowIgnoreScenario.FileDynamicsOff),
            (WorkflowRootScenario.AllVisible, WorkflowExtensionScenario.DocsOnly, WorkflowIgnoreScenario.DirectoryDynamicsOff),
            (WorkflowRootScenario.DocsOnly, WorkflowExtensionScenario.JsonOnly, WorkflowIgnoreScenario.MixedManual),
            (WorkflowRootScenario.SrcOnly, WorkflowExtensionScenario.AllVisible, WorkflowIgnoreScenario.MixedManual),
            (WorkflowRootScenario.DocsAndSrc, WorkflowExtensionScenario.MarkdownOnly, WorkflowIgnoreScenario.AllOff),
            (WorkflowRootScenario.DocsAndSamples, WorkflowExtensionScenario.CodeOnly, WorkflowIgnoreScenario.GitIgnoreOff)
        };

        var orders = new[]
        {
            new[] { WorkflowMutationStep.Roots, WorkflowMutationStep.Extensions, WorkflowMutationStep.Ignore },
            new[] { WorkflowMutationStep.Roots, WorkflowMutationStep.Ignore, WorkflowMutationStep.Extensions },
            new[] { WorkflowMutationStep.Extensions, WorkflowMutationStep.Roots, WorkflowMutationStep.Ignore },
            new[] { WorkflowMutationStep.Extensions, WorkflowMutationStep.Ignore, WorkflowMutationStep.Roots },
            new[] { WorkflowMutationStep.Ignore, WorkflowMutationStep.Roots, WorkflowMutationStep.Extensions },
            new[] { WorkflowMutationStep.Ignore, WorkflowMutationStep.Extensions, WorkflowMutationStep.Roots }
        };

        foreach (var state in targetStates)
        {
            foreach (var order in orders)
                yield return [
                    state.Item1.ToString(),
                    state.Item2.ToString(),
                    state.Item3.ToString(),
                    order.Select(step => step.ToString()).ToArray()
                ];
        }
    }

    private sealed class WorkflowClientSelectionState
    {
        private readonly Dictionary<string, bool> _rootStates;
        private readonly Dictionary<string, bool> _extensionStates;
        private readonly HashSet<string> _explicitlyUncheckedRoots = new(PathComparer.Default);
        private readonly HashSet<string> _explicitlyUncheckedExtensions = new(StringComparer.OrdinalIgnoreCase);

        private WorkflowClientSelectionState(
            Dictionary<string, bool> rootStates,
            Dictionary<string, bool> extensionStates)
        {
            _rootStates = rootStates;
            _extensionStates = extensionStates;
        }

        public static WorkflowClientSelectionState FromSnapshot(SelectionRefreshSnapshot snapshot) =>
            new(
                BuildRootOptionStateCache(snapshot),
                BuildExtensionOptionStateCache(snapshot));

        public SelectionRefreshContext CreateStepContext(
            string rootPath,
            SelectionRefreshSnapshot snapshot,
            SelectionRefreshScenario targetScenario,
            WorkflowMutationStep step)
        {
            var context = CreateConvergedContext(rootPath, snapshot);

            return step switch
            {
                WorkflowMutationStep.Roots => ApplyRootScenario(context, snapshot, targetScenario),
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
            var context = ProjectLoadWorkflowRefreshHarness.CreateContextFromSnapshot(rootPath, snapshot);
            return context with
            {
                RootSelectionCache = CollectCheckedVisibleRootNames(snapshot),
                ExtensionsSelectionCache = CollectCheckedVisibleExtensionNames(snapshot),
                RootOptionStateCache = new Dictionary<string, bool>(_rootStates, PathComparer.Default),
                ExtensionOptionStateCache = new Dictionary<string, bool>(
                    _extensionStates,
                    StringComparer.OrdinalIgnoreCase)
            };
        }

        public void MergeVisibleState(SelectionRefreshSnapshot snapshot)
        {
            if (snapshot.RootOptions is not null)
            {
                foreach (var option in snapshot.RootOptions)
                    _rootStates[option.Name] = option.IsChecked;
            }

            foreach (var option in snapshot.ExtensionOptions)
                _extensionStates[option.Name] = option.IsChecked;
        }

        public void AssertExplicitlyUncheckedVisibleEntriesRemainUnchecked(SelectionRefreshSnapshot snapshot)
        {
            if (snapshot.RootOptions is not null)
            {
                foreach (var option in snapshot.RootOptions)
                {
                    if (_explicitlyUncheckedRoots.Contains(option.Name))
                        Assert.False(option.IsChecked, $"Explicitly unchecked root '{option.Name}' must stay unchecked.");
                }
            }

            foreach (var option in snapshot.ExtensionOptions)
            {
                if (_explicitlyUncheckedExtensions.Contains(option.Name))
                    Assert.False(option.IsChecked, $"Explicitly unchecked extension '{option.Name}' must stay unchecked.");
            }
        }

        private SelectionRefreshContext ApplyRootScenario(
            SelectionRefreshContext context,
            SelectionRefreshSnapshot snapshot,
            SelectionRefreshScenario scenario)
        {
            if (snapshot.RootOptions is not null)
            {
                var allVisible = scenario.RootScenario == WorkflowRootScenario.AllVisible;
                foreach (var option in snapshot.RootOptions)
                {
                    var isChecked = allVisible || scenario.RequestedRootNames.Contains(option.Name);
                    _rootStates[option.Name] = isChecked;
                    TrackExplicitUncheckedState(_explicitlyUncheckedRoots, option.Name, isChecked);
                }
            }

            return context with
            {
                AllRootFoldersChecked = scenario.RootScenario == WorkflowRootScenario.AllVisible,
                RootSelectionInitialized = scenario.RootScenario != WorkflowRootScenario.AllVisible,
                RootSelectionCache = CollectCheckedVisibleRootNames(snapshot),
                RootOptionStateCache = new Dictionary<string, bool>(_rootStates, PathComparer.Default)
            };
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
                TrackExplicitUncheckedState(_explicitlyUncheckedExtensions, option.Name, isChecked);
            }

            return context with
            {
                AllExtensionsChecked = scenario.ExtensionScenario == WorkflowExtensionScenario.AllVisible,
                ExtensionsSelectionInitialized = scenario.ExtensionScenario != WorkflowExtensionScenario.AllVisible,
                ExtensionsSelectionCache = CollectCheckedVisibleExtensionNames(snapshot),
                ExtensionOptionStateCache = new Dictionary<string, bool>(
                    _extensionStates,
                    StringComparer.OrdinalIgnoreCase)
            };
        }

        private HashSet<string> CollectCheckedVisibleRootNames(SelectionRefreshSnapshot snapshot)
        {
            var selected = new HashSet<string>(PathComparer.Default);
            if (snapshot.RootOptions is null)
                return selected;

            foreach (var option in snapshot.RootOptions)
            {
                if (_rootStates.TryGetValue(option.Name, out var isChecked) && isChecked)
                    selected.Add(option.Name);
            }

            return selected;
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

        private static void TrackExplicitUncheckedState(
            HashSet<string> explicitlyUncheckedNames,
            string name,
            bool isChecked)
        {
            if (isChecked)
            {
                explicitlyUncheckedNames.Remove(name);
                return;
            }

            explicitlyUncheckedNames.Add(name);
        }
    }
}
