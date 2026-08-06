using DevProjex.Application.Models;

namespace DevProjex.Tests.Integration;

public sealed class ProjectLoadWorkflowSectionMutationMatrixIntegrationTests
{
    [Theory]
    [MemberData(nameof(MutationCases))]
    public async Task ComputeFullRefreshSnapshot_SingleSectionMutations_StayStableAndChangeAppliedMetrics(
        string mutationCaseName)
    {
        var mutationCase = GetMutationCase(mutationCaseName);
        var rootPath = ProjectLoadWorkflowSharedWorkspace.RootPath;

        var services = CreateServices();
        var baselineSnapshot = services.Engine.ComputeFullRefreshSnapshot(
            CreateDefaultsContext(rootPath),
            CancellationToken.None);
        SelectionSnapshotContractAssertions.AssertAllSectionsConsistent(
            rootPath,
            services.IgnoreRulesService,
            baselineSnapshot);
        var baselineMetrics = await ComputeMetricsFromSnapshotAsync(rootPath, baselineSnapshot);

        var mutatedSnapshot = services.Engine.ComputeFullRefreshSnapshot(
            mutationCase.CreateContext(rootPath, baselineSnapshot),
            CancellationToken.None);

        SelectionSnapshotContractAssertions.AssertAllSectionsConsistent(
            rootPath,
            services.IgnoreRulesService,
            mutatedSnapshot);
        mutationCase.AssertSnapshot(mutatedSnapshot);
        AssertVisibleAdvancedIgnoreOptionsCarryPositiveCounts(mutatedSnapshot);

        var convergedSnapshot = services.Engine.ComputeFullRefreshSnapshot(
            BuildConvergedContext(rootPath, mutatedSnapshot),
            CancellationToken.None);

        SelectionSnapshotContractAssertions.AssertAllSectionsConsistent(
            rootPath,
            services.IgnoreRulesService,
            convergedSnapshot);
        // A real refresh bug often looks "fine" on the first snapshot and only leaks out
        // after the coordinator feeds the updated caches back into the next pass.
        AssertEquivalentSnapshots(mutatedSnapshot, convergedSnapshot);

        if (mutationCase.RequiresAppliedMetricsChange)
        {
            var mutatedMetrics = await ComputeMetricsFromSnapshotAsync(rootPath, mutatedSnapshot);
            Assert.True(
                mutatedMetrics.TreeMetrics != baselineMetrics.TreeMetrics ||
                mutatedMetrics.ContentMetrics != baselineMetrics.ContentMetrics,
                $"Mutation '{mutationCaseName}' must change at least one applied metrics block compared to defaults.");
        }
    }

    public static IEnumerable<object[]> MutationCases()
    {
        foreach (var mutationCase in BuildMutationCases())
            yield return [mutationCase.Name];
    }

    private static IReadOnlyList<MutationCase> BuildMutationCases()
    {
        var cases = new List<MutationCase>
        {
            CreateExtensionMutation("extension-cs-off", ".cs"),
            CreateExtensionMutation("extension-json-off", ".json"),
            CreateExtensionMutation("extension-md-off", ".md"),
            CreateExtensionMutation("extension-txt-off", ".txt"),
            CreateIgnoreMutation(
                "gitignore-off",
                IgnoreOptionId.UseGitIgnore,
                requiresAppliedMetricsChange: false),
            CreateIgnoreMutation(
                "dot-folders-off",
                IgnoreOptionId.DotFolders,
                requiresAppliedMetricsChange: false),
            CreateIgnoreMutation(
                "dot-files-off",
                IgnoreOptionId.DotFiles,
                requiresAppliedMetricsChange: !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()),
            CreateIgnoreMutation("empty-folders-off", IgnoreOptionId.EmptyFolders, requiresAppliedMetricsChange: true),
            CreateIgnoreMutation("empty-files-off", IgnoreOptionId.EmptyFiles, requiresAppliedMetricsChange: true),
            CreateIgnoreMutation("extensionless-off", IgnoreOptionId.ExtensionlessFiles, requiresAppliedMetricsChange: true)
        };

        if (OperatingSystem.IsWindows())
        {
            cases.Add(CreateIgnoreMutation(
                "hidden-folders-off",
                IgnoreOptionId.HiddenFolders,
                requiresAppliedMetricsChange: false));
            cases.Add(CreateIgnoreMutation("hidden-files-off", IgnoreOptionId.HiddenFiles, requiresAppliedMetricsChange: true));
        }

        return cases;
    }

    private static MutationCase GetMutationCase(string mutationCaseName)
    {
        var mutationCase = BuildMutationCases()
            .FirstOrDefault(candidate => string.Equals(candidate.Name, mutationCaseName, StringComparison.Ordinal));

        return Assert.IsType<MutationCase>(mutationCase);
    }

    private static MutationCase CreateExtensionMutation(string name, string extension) =>
        new(
            name,
            (rootPath, baselineSnapshot) =>
            {
                var selectedExtensions = CollectCheckedExtensionNames(baselineSnapshot);
                selectedExtensions.Remove(extension);
                var extensionStates = BuildExtensionOptionStateCache(baselineSnapshot);
                extensionStates[extension] = false;

                return CreateEditableContext(rootPath, baselineSnapshot) with
                {
                    AllExtensionsChecked = false,
                    ExtensionsSelectionInitialized = true,
                    ExtensionsSelectionCache = selectedExtensions,
                    ExtensionOptionStateCache = extensionStates
                };
            },
            snapshot =>
            {
                Assert.Contains(snapshot.ExtensionOptions, option => string.Equals(option.Name, extension, StringComparison.OrdinalIgnoreCase) && !option.IsChecked);
            });

    private static MutationCase CreateIgnoreMutation(
        string name,
        IgnoreOptionId optionId,
        bool requiresAppliedMetricsChange,
        Action<SelectionRefreshSnapshot>? extraAssertion = null) =>
        new(
            name,
            (rootPath, baselineSnapshot) =>
            {
                var selectedIgnoreOptions = CollectCheckedIgnoreOptionIds(baselineSnapshot);
                selectedIgnoreOptions.Remove(optionId);

                return CreateEditableContext(rootPath, baselineSnapshot) with
                {
                    IgnoreSelectionInitialized = true,
                    IgnoreSelectionCache = selectedIgnoreOptions,
                    IgnoreOptionStateCache = BuildIgnoreStateCache(selectedIgnoreOptions, [optionId]),
                    IgnoreAllPreference = false
                };
            },
            snapshot =>
            {
                var isExplicitlyUnchecked = snapshot.IgnoreOptionStateCache.TryGetValue(optionId, out var isChecked) && !isChecked;
                var isNoLongerVisible = snapshot.IgnoreOptions.All(option => option.Id != optionId);
                var isVisibleButUnchecked = snapshot.IgnoreOptions.Any(option => option.Id == optionId && !option.IsChecked);

                // Dynamic ignore options are allowed to disappear when the current tree
                // no longer contains anything they can affect. For this contract we only
                // care that the option is no longer effectively selected.
                Assert.True(
                    isExplicitlyUnchecked || isNoLongerVisible || isVisibleButUnchecked,
                    $"Mutation must leave ignore option '{optionId}' unchecked or unavailable.");

                extraAssertion?.Invoke(snapshot);
            },
            requiresAppliedMetricsChange);

    private static SelectionRefreshContext CreateDefaultsContext(string rootPath) =>
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
            CurrentSnapshotState: EmptySnapshotState,
            CaptureTreeInventory: true);

    private static SelectionRefreshContext CreateEditableContext(
        string rootPath,
        SelectionRefreshSnapshot snapshot)
    {
        return new SelectionRefreshContext(
            Path: rootPath,
            PreparedSelectionMode: PreparedSelectionMode.Defaults,
            AllRootFoldersChecked: snapshot.RootOptions is null ||
                                   snapshot.RootOptions.Count == 0 ||
                                   snapshot.RootOptions.All(option => option.IsChecked),
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
                snapshot.ControllerImpactCounts,
                snapshot.ExtensionlessEntriesCount > 0,
                snapshot.ExtensionlessEntriesCount,
                snapshot.GitEvidence),
            RootOptionStateCache: snapshot.RootOptions?.ToDictionary(
                option => option.Name,
                option => option.IsChecked,
                PathComparer.Default),
            ExtensionOptionStateCache: snapshot.ExtensionOptions.ToDictionary(
                option => option.Name,
                option => option.IsChecked,
                StringComparer.OrdinalIgnoreCase),
            IgnoreOptionStateCacheIsComplete: true,
            CaptureTreeInventory: true);
    }

    private static SelectionRefreshContext BuildConvergedContext(
        string rootPath,
        SelectionRefreshSnapshot snapshot) =>
        CreateEditableContext(rootPath, snapshot);

    private static HashSet<string> CollectCheckedRootNames(SelectionRefreshSnapshot snapshot) =>
        snapshot.RootOptions is null
            ? new HashSet<string>(PathComparer.Default)
            : new HashSet<string>(
                snapshot.RootOptions.Where(option => option.IsChecked).Select(option => option.Name),
                PathComparer.Default);

    private static HashSet<string> CollectCheckedExtensionNames(SelectionRefreshSnapshot snapshot) =>
        new(
            snapshot.ExtensionOptions.Where(option => option.IsChecked).Select(option => option.Name),
            StringComparer.OrdinalIgnoreCase);

    private static HashSet<IgnoreOptionId> CollectCheckedIgnoreOptionIds(SelectionRefreshSnapshot snapshot) =>
        new(
            snapshot.IgnoreOptions.Where(option => option.IsChecked).Select(option => option.Id));

    private static Dictionary<string, bool> BuildExtensionOptionStateCache(SelectionRefreshSnapshot snapshot)
    {
        var cache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in snapshot.ExtensionOptions)
            cache[option.Name] = option.IsChecked;

        return cache;
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

    private static async Task<ProjectLoadWorkflowRuntime.ProjectLoadWorkflowMetrics> ComputeMetricsFromSnapshotAsync(
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

    private static void AssertEquivalentSnapshots(
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

    private static void AssertVisibleAdvancedIgnoreOptionsCarryPositiveCounts(SelectionRefreshSnapshot snapshot)
    {
        foreach (var option in snapshot.IgnoreOptions)
        {
            if (option.Id is IgnoreOptionId.UseGitIgnore or IgnoreOptionId.SmartIgnore or IgnoreOptionId.HideSecrets)
                continue;

            var expectedCount = GetIgnoreCount(snapshot.IgnoreOptionCounts, option.Id);
            Assert.True(expectedCount > 0, $"Visible advanced ignore option '{option.Id}' must have a positive effective count.");

            var match = Regex.Match(option.Label, @"\((\d+)\)$");
            Assert.True(match.Success, $"Advanced ignore option '{option.Id}' must render its live count. Actual label: '{option.Label}'.");
            Assert.Equal(expectedCount, int.Parse(match.Groups[1].Value));
        }
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

    private static WorkflowServices CreateServices()
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

    private static readonly IgnoreSectionSnapshotState EmptySnapshotState =
        new(false, IgnoreOptionCounts.Empty, IgnoreControllerImpactCounts.Empty, false, 0);

    private sealed record MutationCase(
        string Name,
        Func<string, SelectionRefreshSnapshot, SelectionRefreshContext> CreateContext,
        Action<SelectionRefreshSnapshot> AssertSnapshot,
        bool RequiresAppliedMetricsChange = true);

    private sealed record WorkflowServices(
        SelectionRefreshEngine Engine,
        IgnoreRulesService IgnoreRulesService);
}
