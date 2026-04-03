using System.Text.RegularExpressions;
using DevProjex.Application.Services;
using DevProjex.Application.Models;
using DevProjex.Application.UseCases;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Infrastructure.FileSystem;

namespace DevProjex.Tests.Shared.ProjectLoadWorkflow;

internal static class ProjectLoadWorkflowRefreshHarness
{
    public static WorkflowServices CreateServices()
    {
        var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
        var filterSelectionService = new FilterOptionSelectionService();
        var ignoreOptionsService = ProjectLoadWorkflowRuntime.CreateIgnoreOptionsService();
        var ignoreRulesService = ProjectLoadWorkflowRuntime.CreateIgnoreRulesService();

        var engine = new SelectionRefreshEngine(
            scanOptions,
            filterSelectionService,
            ignoreOptionsService,
            (path, selectedIgnoreOptions, selectedRoots) => ignoreRulesService.Build(path, selectedIgnoreOptions, selectedRoots),
            (path, selectedRoots) => ignoreRulesService.GetIgnoreOptionsAvailability(path, selectedRoots) with
            {
                ShowAdvancedCounts = true
            });

        return new WorkflowServices(engine, ignoreRulesService);
    }

    public static SelectionRefreshContext CreateDefaultContext(string rootPath) =>
        new(
            Path: rootPath,
            PreparedSelectionMode: PreparedSelectionMode.Defaults,
            AllRootFoldersChecked: true,
            AllExtensionsChecked: true,
            RootSelectionInitialized: false,
            RootSelectionCache: new HashSet<string>(PathComparer.Default),
            ExtensionsSelectionInitialized: false,
            ExtensionsSelectionCache: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            IgnoreSelectionInitialized: false,
            IgnoreSelectionCache: new HashSet<IgnoreOptionId>(),
            IgnoreOptionStateCache: new Dictionary<IgnoreOptionId, bool>(),
            IgnoreAllPreference: null,
            CurrentSnapshotState: EmptySnapshotState);

    public static SelectionRefreshScenario CreateScenario(
        SelectionRefreshSnapshot baselineSnapshot,
        WorkflowRootScenario rootScenario,
        WorkflowExtensionScenario extensionScenario,
        WorkflowIgnoreScenario ignoreScenario)
    {
        var baselineSelectedIgnoreOptions = CollectCheckedIgnoreOptionIds(baselineSnapshot);
        var selectedIgnoreOptions = BuildRequestedIgnoreOptions(baselineSelectedIgnoreOptions, ignoreScenario);

        return new SelectionRefreshScenario(
            RootScenario: rootScenario,
            RequestedRootNames: BuildRequestedRootNames(rootScenario),
            ExtensionScenario: extensionScenario,
            RequestedExtensionNames: BuildRequestedExtensionNames(extensionScenario),
            IgnoreScenario: ignoreScenario,
            IgnoreSelectionInitialized: ignoreScenario != WorkflowIgnoreScenario.Defaults,
            RequestedIgnoreOptions: ignoreScenario == WorkflowIgnoreScenario.Defaults
                ? baselineSelectedIgnoreOptions
                : selectedIgnoreOptions,
            ExplicitlyDisabledIgnoreOptions: ignoreScenario == WorkflowIgnoreScenario.Defaults
                ? new HashSet<IgnoreOptionId>()
                : baselineSelectedIgnoreOptions.Except(selectedIgnoreOptions).ToHashSet(),
            IgnoreAllPreference: ignoreScenario == WorkflowIgnoreScenario.AllOff ? false : null);
    }

    public static SelectionRefreshContext CreateScenarioContext(
        string rootPath,
        SelectionRefreshScenario scenario)
    {
        return new SelectionRefreshContext(
            Path: rootPath,
            PreparedSelectionMode: PreparedSelectionMode.Defaults,
            AllRootFoldersChecked: scenario.RootScenario == WorkflowRootScenario.AllVisible,
            AllExtensionsChecked: scenario.ExtensionScenario == WorkflowExtensionScenario.AllVisible,
            RootSelectionInitialized: scenario.RootScenario != WorkflowRootScenario.AllVisible,
            RootSelectionCache: new HashSet<string>(scenario.RequestedRootNames, PathComparer.Default),
            ExtensionsSelectionInitialized: scenario.ExtensionScenario != WorkflowExtensionScenario.AllVisible,
            ExtensionsSelectionCache: new HashSet<string>(scenario.RequestedExtensionNames, StringComparer.OrdinalIgnoreCase),
            IgnoreSelectionInitialized: scenario.IgnoreSelectionInitialized,
            IgnoreSelectionCache: new HashSet<IgnoreOptionId>(scenario.RequestedIgnoreOptions),
            IgnoreOptionStateCache: BuildIgnoreStateCache(
                scenario.RequestedIgnoreOptions,
                scenario.ExplicitlyDisabledIgnoreOptions),
            IgnoreAllPreference: scenario.IgnoreAllPreference,
            CurrentSnapshotState: EmptySnapshotState);
    }

    public static SelectionRefreshContext BuildConvergedContext(
        string rootPath,
        SelectionRefreshSnapshot snapshot)
    {
        return CreateContextFromSnapshot(rootPath, snapshot);
    }

    public static SelectionRefreshContext CreateContextFromSnapshot(
        string rootPath,
        SelectionRefreshSnapshot snapshot)
    {
        return new SelectionRefreshContext(
            Path: rootPath,
            PreparedSelectionMode: PreparedSelectionMode.Defaults,
            AllRootFoldersChecked: snapshot.RootOptions is { Count: > 0 } rootOptions &&
                                   rootOptions.All(option => option.IsChecked),
            AllExtensionsChecked: snapshot.ExtensionOptions.Count > 0 &&
                                  snapshot.ExtensionOptions.All(option => option.IsChecked),
            RootSelectionInitialized: true,
            RootSelectionCache: CollectCheckedRootNames(snapshot),
            ExtensionsSelectionInitialized: true,
            ExtensionsSelectionCache: CollectCheckedExtensionNames(snapshot),
            IgnoreSelectionInitialized: true,
            IgnoreSelectionCache: CollectCheckedIgnoreOptionIds(snapshot),
            IgnoreOptionStateCache: new Dictionary<IgnoreOptionId, bool>(snapshot.IgnoreOptionStateCache),
            IgnoreAllPreference: DeriveIgnoreAllPreference(snapshot.IgnoreOptions),
            CurrentSnapshotState: new IgnoreSectionSnapshotState(
                snapshot.HasIgnoreOptionCounts,
                snapshot.IgnoreOptionCounts,
                snapshot.ExtensionlessEntriesCount > 0,
                snapshot.ExtensionlessEntriesCount));
    }

    public static SelectionRefreshContext ApplyScenarioStep(
        string rootPath,
        SelectionRefreshSnapshot snapshot,
        SelectionRefreshScenario targetScenario,
        WorkflowMutationStep step)
    {
        var context = CreateContextFromSnapshot(rootPath, snapshot);

        return step switch
        {
            WorkflowMutationStep.Roots => context with
            {
                AllRootFoldersChecked = targetScenario.RootScenario == WorkflowRootScenario.AllVisible,
                RootSelectionInitialized = targetScenario.RootScenario != WorkflowRootScenario.AllVisible,
                RootSelectionCache = new HashSet<string>(targetScenario.RequestedRootNames, PathComparer.Default)
            },
            WorkflowMutationStep.Extensions => context with
            {
                AllExtensionsChecked = targetScenario.ExtensionScenario == WorkflowExtensionScenario.AllVisible,
                ExtensionsSelectionInitialized = targetScenario.ExtensionScenario != WorkflowExtensionScenario.AllVisible,
                ExtensionsSelectionCache = new HashSet<string>(targetScenario.RequestedExtensionNames, StringComparer.OrdinalIgnoreCase)
            },
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

    public static async Task<ProjectLoadWorkflowRuntime.ProjectLoadWorkflowMetrics> ComputeMetricsFromSnapshotAsync(
        string rootPath,
        SelectionRefreshSnapshot snapshot)
    {
        return await ProjectLoadWorkflowRuntime.ComputeMetricsAsync(
            rootPath,
            CollectCheckedRootNames(snapshot),
            CollectCheckedExtensionNames(snapshot),
            CollectCheckedIgnoreOptionIds(snapshot),
            CancellationToken.None);
    }

    public static void AssertVisibleAdvancedIgnoreOptionsCarryPositiveCounts(SelectionRefreshSnapshot snapshot)
    {
        foreach (var option in snapshot.IgnoreOptions)
        {
            if (option.Id is IgnoreOptionId.UseGitIgnore or IgnoreOptionId.SmartIgnore)
                continue;

            var expectedCount = GetIgnoreCount(snapshot.IgnoreOptionCounts, option.Id);
            Assert.True(expectedCount > 0, $"Visible advanced ignore option '{option.Id}' must have a positive effective count.");

            var match = Regex.Match(option.Label, @"\((\d+)\)$");
            Assert.True(match.Success, $"Advanced ignore option '{option.Id}' must render its live count. Actual label: '{option.Label}'.");
            Assert.Equal(expectedCount, int.Parse(match.Groups[1].Value));
        }
    }

    public static void AssertScenarioSelectionContract(
        SelectionRefreshSnapshot snapshot,
        SelectionRefreshScenario scenario)
    {
        AssertRootSelectionContract(snapshot, scenario);
        AssertExtensionSelectionContract(snapshot, scenario);
        AssertIgnoreSelectionContract(snapshot, scenario);
    }

    public static void AssertEquivalentSnapshots(
        SelectionRefreshSnapshot expected,
        SelectionRefreshSnapshot actual)
    {
        AssertEquivalentSelectionOptions(expected.RootOptions, actual.RootOptions);
        Assert.Equal(expected.ExtensionOptions, actual.ExtensionOptions);
        Assert.Equal(expected.IgnoreOptions, actual.IgnoreOptions);
        Assert.Equal(expected.ExtensionlessEntriesCount, actual.ExtensionlessEntriesCount);
        Assert.Equal(expected.HasIgnoreOptionCounts, actual.HasIgnoreOptionCounts);
        Assert.Equal(expected.IgnoreOptionCounts, actual.IgnoreOptionCounts);
        Assert.Equal(expected.IgnoreOptionStateCache.Count, actual.IgnoreOptionStateCache.Count);
        foreach (var pair in expected.IgnoreOptionStateCache.OrderBy(pair => pair.Key))
        {
            Assert.True(actual.IgnoreOptionStateCache.TryGetValue(pair.Key, out var actualValue));
            Assert.Equal(pair.Value, actualValue);
        }
        Assert.Equal(expected.RootAccessDenied, actual.RootAccessDenied);
        Assert.Equal(expected.HadAccessDenied, actual.HadAccessDenied);
    }

    public static void AssertEquivalentVisibleSnapshots(
        SelectionRefreshSnapshot expected,
        SelectionRefreshSnapshot actual)
    {
        AssertEquivalentSelectionOptions(expected.RootOptions, actual.RootOptions);
        Assert.Equal(expected.ExtensionOptions, actual.ExtensionOptions);
        Assert.Equal(expected.IgnoreOptions, actual.IgnoreOptions);
        Assert.Equal(expected.ExtensionlessEntriesCount, actual.ExtensionlessEntriesCount);
        Assert.Equal(expected.HasIgnoreOptionCounts, actual.HasIgnoreOptionCounts);
        Assert.Equal(expected.IgnoreOptionCounts, actual.IgnoreOptionCounts);
        Assert.Equal(expected.RootAccessDenied, actual.RootAccessDenied);
        Assert.Equal(expected.HadAccessDenied, actual.HadAccessDenied);
    }

    public static HashSet<string> CollectCheckedRootNames(SelectionRefreshSnapshot snapshot) =>
        snapshot.RootOptions is null
            ? new HashSet<string>(PathComparer.Default)
            : new HashSet<string>(
                snapshot.RootOptions.Where(option => option.IsChecked).Select(option => option.Name),
                PathComparer.Default);

    public static HashSet<string> CollectCheckedExtensionNames(SelectionRefreshSnapshot snapshot) =>
        new(
            snapshot.ExtensionOptions.Where(option => option.IsChecked).Select(option => option.Name),
            StringComparer.OrdinalIgnoreCase);

    public static HashSet<IgnoreOptionId> CollectCheckedIgnoreOptionIds(SelectionRefreshSnapshot snapshot) =>
        new(
            snapshot.IgnoreOptionStateCache.Where(pair => pair.Value).Select(pair => pair.Key));

    private static IReadOnlySet<string> BuildRequestedRootNames(WorkflowRootScenario scenario)
    {
        return scenario switch
        {
            WorkflowRootScenario.AllVisible => new HashSet<string>(PathComparer.Default),
            WorkflowRootScenario.DocsOnly => new HashSet<string>(PathComparer.Default) { "docs" },
            WorkflowRootScenario.SrcOnly => new HashSet<string>(PathComparer.Default) { "src" },
            WorkflowRootScenario.SamplesOnly => new HashSet<string>(PathComparer.Default) { "samples" },
            WorkflowRootScenario.DocsAndSrc => new HashSet<string>(PathComparer.Default) { "docs", "src" },
            WorkflowRootScenario.DocsAndSamples => new HashSet<string>(PathComparer.Default) { "docs", "samples" },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        };
    }

    private static IReadOnlySet<string> BuildRequestedExtensionNames(WorkflowExtensionScenario scenario)
    {
        return scenario switch
        {
            WorkflowExtensionScenario.AllVisible => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            WorkflowExtensionScenario.CodeOnly => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".json" },
            WorkflowExtensionScenario.DocsOnly => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".md", ".txt" },
            WorkflowExtensionScenario.MarkdownOnly => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".md" },
            WorkflowExtensionScenario.JsonOnly => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".json" },
            WorkflowExtensionScenario.CodeAndDocs => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".md", ".txt" },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        };
    }

    private static IReadOnlySet<IgnoreOptionId> BuildRequestedIgnoreOptions(
        IReadOnlySet<IgnoreOptionId> baselineSelectedIgnoreOptions,
        WorkflowIgnoreScenario scenario)
    {
        switch (scenario)
        {
            case WorkflowIgnoreScenario.Defaults:
                return baselineSelectedIgnoreOptions;
            case WorkflowIgnoreScenario.AllOff:
                return new HashSet<IgnoreOptionId>();
            case WorkflowIgnoreScenario.GitIgnoreOff:
                return baselineSelectedIgnoreOptions
                    .Where(id => id != IgnoreOptionId.UseGitIgnore)
                    .ToHashSet();
            case WorkflowIgnoreScenario.EmptyFoldersOff:
                return baselineSelectedIgnoreOptions
                    .Where(id => id != IgnoreOptionId.EmptyFolders)
                    .ToHashSet();
            case WorkflowIgnoreScenario.FileDynamicsOff:
                return baselineSelectedIgnoreOptions
                    .Where(id => id is not IgnoreOptionId.HiddenFiles
                        and not IgnoreOptionId.DotFiles
                        and not IgnoreOptionId.EmptyFiles
                        and not IgnoreOptionId.ExtensionlessFiles)
                    .ToHashSet();
            case WorkflowIgnoreScenario.DirectoryDynamicsOff:
                return baselineSelectedIgnoreOptions
                    .Where(id => id is not IgnoreOptionId.HiddenFolders
                        and not IgnoreOptionId.DotFolders
                        and not IgnoreOptionId.EmptyFolders)
                    .ToHashSet();
            case WorkflowIgnoreScenario.MixedManual:
                return baselineSelectedIgnoreOptions
                    .Where(id => id is IgnoreOptionId.UseGitIgnore
                        or IgnoreOptionId.SmartIgnore
                        or IgnoreOptionId.DotFiles
                        or IgnoreOptionId.EmptyFolders
                        or IgnoreOptionId.ExtensionlessFiles)
                    .ToHashSet();
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }
    }

    private static Dictionary<IgnoreOptionId, bool> BuildIgnoreStateCache(
        IEnumerable<IgnoreOptionId> selectedIgnoreOptions,
        IEnumerable<IgnoreOptionId>? explicitlyDisabledIgnoreOptions = null)
    {
        var cache = new Dictionary<IgnoreOptionId, bool>();
        foreach (var optionId in selectedIgnoreOptions)
            cache[optionId] = true;

        if (explicitlyDisabledIgnoreOptions is not null)
        {
            foreach (var optionId in explicitlyDisabledIgnoreOptions)
                cache[optionId] = false;
        }

        return cache;
    }

    private static void AssertRootSelectionContract(
        SelectionRefreshSnapshot snapshot,
        SelectionRefreshScenario scenario)
    {
        if (snapshot.RootOptions is null)
            return;

        var actualChecked = snapshot.RootOptions
            .Where(option => option.IsChecked)
            .Select(option => option.Name)
            .ToHashSet(PathComparer.Default);

        if (scenario.RootScenario == WorkflowRootScenario.AllVisible)
        {
            Assert.All(snapshot.RootOptions, option => Assert.True(option.IsChecked));
            return;
        }

        var visibleNames = snapshot.RootOptions.Select(option => option.Name).ToHashSet(PathComparer.Default);
        var expectedChecked = scenario.RequestedRootNames
            .Where(visibleNames.Contains)
            .ToHashSet(PathComparer.Default);

        Assert.Equal(expectedChecked.Count, actualChecked.Count);
        foreach (var name in expectedChecked)
            Assert.Contains(name, actualChecked);
    }

    private static void AssertExtensionSelectionContract(
        SelectionRefreshSnapshot snapshot,
        SelectionRefreshScenario scenario)
    {
        var actualChecked = snapshot.ExtensionOptions
            .Where(option => option.IsChecked)
            .Select(option => option.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (scenario.ExtensionScenario == WorkflowExtensionScenario.AllVisible)
        {
            Assert.All(snapshot.ExtensionOptions, option => Assert.True(option.IsChecked));
            return;
        }

        var visibleNames = snapshot.ExtensionOptions.Select(option => option.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedChecked = scenario.RequestedExtensionNames
            .Where(visibleNames.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(expectedChecked.Count, actualChecked.Count);
        foreach (var name in expectedChecked)
            Assert.Contains(name, actualChecked);
    }

    private static void AssertIgnoreSelectionContract(
        SelectionRefreshSnapshot snapshot,
        SelectionRefreshScenario scenario)
    {
        var actualChecked = snapshot.IgnoreOptions
            .Where(option => option.IsChecked)
            .Select(option => option.Id)
            .ToHashSet();
        var visibleIds = snapshot.IgnoreOptions.Select(option => option.Id).ToHashSet();

        if (scenario.IgnoreScenario == WorkflowIgnoreScenario.AllOff)
        {
            Assert.Empty(actualChecked);
            return;
        }

        var expectedChecked = scenario.RequestedIgnoreOptions
            .Where(visibleIds.Contains)
            .ToHashSet();

        foreach (var id in expectedChecked)
            Assert.Contains(id, actualChecked);

        foreach (var id in scenario.ExplicitlyDisabledIgnoreOptions.Where(visibleIds.Contains))
            Assert.DoesNotContain(id, actualChecked);
    }

    private static void AssertEquivalentSelectionOptions(
        IReadOnlyList<SelectionOption>? expected,
        IReadOnlyList<SelectionOption>? actual)
    {
        if (expected is null || actual is null)
        {
            Assert.Equal(expected, actual);
            return;
        }

        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
            Assert.Equal(expected[index], actual[index]);
    }

    private static int GetIgnoreCount(IgnoreOptionCounts counts, IgnoreOptionId optionId)
    {
        return optionId switch
        {
            IgnoreOptionId.HiddenFolders => counts.HiddenFolders,
            IgnoreOptionId.HiddenFiles => counts.HiddenFiles,
            IgnoreOptionId.DotFolders => counts.DotFolders,
            IgnoreOptionId.DotFiles => counts.DotFiles,
            IgnoreOptionId.EmptyFolders => counts.EmptyFolders,
            IgnoreOptionId.EmptyFiles => counts.EmptyFiles,
            IgnoreOptionId.ExtensionlessFiles => counts.ExtensionlessFiles,
            _ => 0
        };
    }

    private static bool? DeriveIgnoreAllPreference(IReadOnlyList<ResolvedIgnoreOptionState> ignoreOptions)
    {
        if (ignoreOptions.Count == 0)
            return null;

        if (ignoreOptions.All(option => option.IsChecked))
            return true;
        if (ignoreOptions.All(option => !option.IsChecked))
            return false;

        return null;
    }

    internal static readonly IgnoreSectionSnapshotState EmptySnapshotState =
        new(false, IgnoreOptionCounts.Empty, false, 0);

    internal enum WorkflowRootScenario
    {
        AllVisible,
        DocsOnly,
        SrcOnly,
        SamplesOnly,
        DocsAndSrc,
        DocsAndSamples
    }

    internal enum WorkflowExtensionScenario
    {
        AllVisible,
        CodeOnly,
        DocsOnly,
        MarkdownOnly,
        JsonOnly,
        CodeAndDocs
    }

    internal enum WorkflowIgnoreScenario
    {
        Defaults,
        AllOff,
        GitIgnoreOff,
        EmptyFoldersOff,
        FileDynamicsOff,
        DirectoryDynamicsOff,
        MixedManual
    }

    internal enum WorkflowMutationStep
    {
        Roots,
        Extensions,
        Ignore
    }

    internal sealed record SelectionRefreshScenario(
        WorkflowRootScenario RootScenario,
        IReadOnlySet<string> RequestedRootNames,
        WorkflowExtensionScenario ExtensionScenario,
        IReadOnlySet<string> RequestedExtensionNames,
        WorkflowIgnoreScenario IgnoreScenario,
        bool IgnoreSelectionInitialized,
        IReadOnlySet<IgnoreOptionId> RequestedIgnoreOptions,
        IReadOnlySet<IgnoreOptionId> ExplicitlyDisabledIgnoreOptions,
        bool? IgnoreAllPreference);

    internal sealed record WorkflowServices(
        SelectionRefreshEngine Engine,
        IgnoreRulesService IgnoreRulesService);
}
