using DevProjex.Application.Models;
using DevProjex.Avalonia.Coordinators;
using DevProjex.Tests.Shared.ProjectLoadWorkflow;

namespace DevProjex.Tests.Integration;

public sealed class SelectionRefreshEngineWorkflowMatrixIntegrationTests
{
    [Theory]
    [MemberData(nameof(WorkflowCases))]
    public async Task ComputeFullRefreshSnapshot_ConvergesToStableStateAcrossComplexWorkflowCases(
        string workflowCaseName)
    {
        var workflowCase = GetWorkflowCase(workflowCaseName);
        var rootPath = ProjectLoadWorkflowSharedWorkspace.RootPath;

        var services = CreateServices();
        var firstSnapshot = services.Engine.ComputeFullRefreshSnapshot(
            workflowCase.CreateContext(rootPath),
            CancellationToken.None);

        workflowCase.AssertSnapshot(firstSnapshot);
        AssertVisibleAdvancedIgnoreOptionsCarryPositiveCounts(firstSnapshot);

        var firstMetrics = await ComputeMetricsFromSnapshotAsync(rootPath, firstSnapshot, services);

        var secondSnapshot = services.Engine.ComputeFullRefreshSnapshot(
            BuildConvergedContext(rootPath, firstSnapshot, workflowCase.PreparedSelectionMode),
            CancellationToken.None);

        if (RequiresDeferredProfileReconciliation(workflowCaseName))
        {
            workflowCase.AssertSnapshot(secondSnapshot);
            AssertDeferredProfileReconciliation(firstSnapshot, secondSnapshot, workflowCaseName);
            AssertVisibleAdvancedIgnoreOptionsCarryPositiveCounts(secondSnapshot);

            var reconciledMetrics = await ComputeMetricsFromSnapshotAsync(rootPath, secondSnapshot, services);
            Assert.Equal(firstMetrics.TreeMetrics, reconciledMetrics.TreeMetrics);
            Assert.Equal(firstMetrics.ContentMetrics, reconciledMetrics.ContentMetrics);
            return;
        }

        AssertEquivalentSnapshots(firstSnapshot, secondSnapshot);
        AssertVisibleAdvancedIgnoreOptionsCarryPositiveCounts(secondSnapshot);

        var secondMetrics = await ComputeMetricsFromSnapshotAsync(rootPath, secondSnapshot, services);
        Assert.Equal(firstMetrics.TreeMetrics, secondMetrics.TreeMetrics);
        Assert.Equal(firstMetrics.ContentMetrics, secondMetrics.ContentMetrics);
    }

    [Fact]
    public void ComputeFullRefreshSnapshot_ProfileAllRoots_DoesNotProbeUnselectedDotRoot()
    {
        var rootPath = ProjectLoadWorkflowSharedWorkspace.RootPath;
        var services = CreateServices();

        var snapshot = services.Engine.ComputeFullRefreshSnapshot(
            CreateProfileWithAllVisibleRootsAndUnavailableDotFolderContext(rootPath),
            CancellationToken.None);

        Assert.All(snapshot.RootOptions!, option => Assert.True(option.IsChecked));
        Assert.DoesNotContain(snapshot.RootOptions!, option => string.Equals(option.Name, ".cache", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.DotFolders);
    }

    public static IEnumerable<object[]> WorkflowCases()
    {
        foreach (var workflowCase in BuildWorkflowCases())
            yield return [workflowCase.Name];
    }

    private static IReadOnlyList<WorkflowCase> BuildWorkflowCases() =>
    [
        new WorkflowCase(
            "defaults",
            PreparedSelectionMode.Defaults,
            CreateDefaultsContext,
            snapshot =>
            {
                Assert.Contains(snapshot.RootOptions!, option => string.Equals(option.Name, "docs", StringComparison.Ordinal));
                Assert.Contains(snapshot.RootOptions!, option => string.Equals(option.Name, "samples", StringComparison.Ordinal));
                Assert.Contains(snapshot.RootOptions!, option => string.Equals(option.Name, "src", StringComparison.Ordinal));
                Assert.All(snapshot.RootOptions!, option => Assert.True(option.IsChecked));
                Assert.Contains(snapshot.ExtensionOptions, option => string.Equals(option.Name, ".cs", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(snapshot.ExtensionOptions, option => string.Equals(option.Name, ".json", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(snapshot.ExtensionOptions, option => string.Equals(option.Name, ".md", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(snapshot.ExtensionOptions, option => string.Equals(option.Name, ".txt", StringComparison.OrdinalIgnoreCase));
                Assert.All(snapshot.ExtensionOptions, option => Assert.True(option.IsChecked));
                Assert.Contains(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.UseGitIgnore && option.IsChecked);
                Assert.Contains(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.EmptyFolders && option.IsChecked);
            }),
        new WorkflowCase(
            "ignore-all-off",
            PreparedSelectionMode.Defaults,
            CreateIgnoreAllOffContext,
            snapshot =>
            {
                Assert.Contains(snapshot.RootOptions!, option => string.Equals(option.Name, ".cache", StringComparison.Ordinal));
                Assert.Contains(snapshot.RootOptions!, option => string.Equals(option.Name, "generated", StringComparison.Ordinal));
                Assert.Contains(snapshot.RootOptions!, option => string.Equals(option.Name, "logs", StringComparison.Ordinal));
                Assert.Contains(snapshot.RootOptions!, option => string.Equals(option.Name, "node_modules", StringComparison.Ordinal));
                Assert.All(snapshot.IgnoreOptions, option => Assert.False(option.IsChecked));
            }),
        new WorkflowCase(
            "docs-only",
            PreparedSelectionMode.Defaults,
            CreateDocsOnlyContext,
            snapshot =>
            {
                Assert.Contains(snapshot.RootOptions!, option => string.Equals(option.Name, "docs", StringComparison.Ordinal) && option.IsChecked);
                Assert.Contains(snapshot.RootOptions!, option => string.Equals(option.Name, "samples", StringComparison.Ordinal) && !option.IsChecked);
                Assert.Contains(snapshot.RootOptions!, option => string.Equals(option.Name, "src", StringComparison.Ordinal) && !option.IsChecked);
            }),
        new WorkflowCase(
            "code-only-extensions",
            PreparedSelectionMode.Defaults,
            CreateCodeOnlyExtensionsContext,
            snapshot =>
            {
                Assert.Contains(snapshot.ExtensionOptions, option => string.Equals(option.Name, ".cs", StringComparison.OrdinalIgnoreCase) && option.IsChecked);
                Assert.Contains(snapshot.ExtensionOptions, option => string.Equals(option.Name, ".json", StringComparison.OrdinalIgnoreCase) && option.IsChecked);
                Assert.Contains(snapshot.ExtensionOptions, option => string.Equals(option.Name, ".md", StringComparison.OrdinalIgnoreCase) && !option.IsChecked);
                Assert.Contains(snapshot.ExtensionOptions, option => string.Equals(option.Name, ".txt", StringComparison.OrdinalIgnoreCase) && !option.IsChecked);
                Assert.Contains(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.EmptyFolders);
            }),
        new WorkflowCase(
            "mixed-manual-selection",
            PreparedSelectionMode.Defaults,
            CreateMixedManualSelectionContext,
            snapshot =>
            {
                Assert.Contains(snapshot.RootOptions!, option => string.Equals(option.Name, "docs", StringComparison.Ordinal) && option.IsChecked);
                Assert.Contains(snapshot.RootOptions!, option => string.Equals(option.Name, "src", StringComparison.Ordinal) && option.IsChecked);
                Assert.Contains(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.UseGitIgnore && option.IsChecked);
                Assert.Contains(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.DotFiles && option.IsChecked);
                Assert.Contains(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.EmptyFolders && option.IsChecked);
            }),
        new WorkflowCase(
            "profile-stale-hidden-roots",
            PreparedSelectionMode.Profile,
            CreateProfileWithUnavailableRootSelectionsContext,
            snapshot =>
            {
                Assert.Contains(snapshot.RootOptions!, option => string.Equals(option.Name, "node_modules", StringComparison.Ordinal) && option.IsChecked);
                Assert.Contains(snapshot.RootOptions!, option => string.Equals(option.Name, "docs", StringComparison.Ordinal) && !option.IsChecked);
                Assert.DoesNotContain(snapshot.RootOptions!, option => string.Equals(option.Name, ".cache", StringComparison.Ordinal));
                Assert.DoesNotContain(snapshot.RootOptions!, option => string.Equals(option.Name, "generated", StringComparison.Ordinal));
            }),
        new WorkflowCase(
            "profile-stale-unavailable-extension",
            PreparedSelectionMode.Profile,
            CreateProfileWithUnavailableExtensionSelectionsContext,
            snapshot =>
            {
                Assert.Contains(snapshot.ExtensionOptions, option => string.Equals(option.Name, ".js", StringComparison.OrdinalIgnoreCase) && option.IsChecked);
                Assert.Contains(snapshot.ExtensionOptions, option => string.Equals(option.Name, ".cs", StringComparison.OrdinalIgnoreCase) && !option.IsChecked);
            }),
        new WorkflowCase(
            "profile-stale-unavailable-ignore-option",
            PreparedSelectionMode.Profile,
            CreateProfileWithUnavailableIgnoreSelectionContext,
            snapshot =>
            {
                Assert.DoesNotContain(snapshot.IgnoreOptions, option => option.Id == IgnoreOptionId.DotFolders);
                Assert.All(
                    snapshot.IgnoreOptions.Where(option => option.Id is not IgnoreOptionId.UseGitIgnore and not IgnoreOptionId.SmartIgnore),
                    option => Assert.True(option.IsChecked));
            })
    ];

    private static WorkflowCase GetWorkflowCase(string workflowCaseName)
    {
        var workflowCase = BuildWorkflowCases()
            .FirstOrDefault(candidate => string.Equals(candidate.Name, workflowCaseName, StringComparison.Ordinal));

        return Assert.IsType<WorkflowCase>(workflowCase);
    }

    private static bool RequiresDeferredProfileReconciliation(string workflowCaseName) =>
        workflowCaseName is "profile-stale-hidden-roots" or "profile-stale-unavailable-ignore-option";

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
            CurrentSnapshotState: EmptySnapshotState);

    private static SelectionRefreshContext CreateIgnoreAllOffContext(string rootPath) =>
        CreateDefaultsContext(rootPath) with
        {
            IgnoreSelectionInitialized = true,
            IgnoreAllPreference = false
        };

    private static SelectionRefreshContext CreateDocsOnlyContext(string rootPath) =>
        CreateDefaultsContext(rootPath) with
        {
            AllRootFoldersChecked = false,
            RootSelectionInitialized = true,
            RootSelectionCache = new HashSet<string>(PathComparer.Default) { "docs" }
        };

    private static SelectionRefreshContext CreateCodeOnlyExtensionsContext(string rootPath) =>
        CreateDefaultsContext(rootPath) with
        {
            AllExtensionsChecked = false,
            ExtensionsSelectionInitialized = true,
            ExtensionsSelectionCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".json" }
        };

    private static SelectionRefreshContext CreateMixedManualSelectionContext(string rootPath)
    {
        var selectedIgnoreOptions = new[]
        {
            IgnoreOptionId.UseGitIgnore,
            IgnoreOptionId.SmartIgnore,
            IgnoreOptionId.DotFiles,
            IgnoreOptionId.EmptyFolders
        };

        return CreateDefaultsContext(rootPath) with
        {
            AllRootFoldersChecked = false,
            RootSelectionInitialized = true,
            RootSelectionCache = new HashSet<string>(PathComparer.Default) { "docs", "src" },
            AllExtensionsChecked = false,
            ExtensionsSelectionInitialized = true,
            ExtensionsSelectionCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".json", ".md", ".txt" },
            IgnoreSelectionInitialized = true,
            IgnoreSelectionCache = new HashSet<IgnoreOptionId>(selectedIgnoreOptions),
            IgnoreOptionStateCache = BuildIgnoreStateCache(selectedIgnoreOptions),
            IgnoreAllPreference = false
        };
    }

    private static SelectionRefreshContext CreateProfileWithUnavailableRootSelectionsContext(string rootPath) =>
        new(
            Path: rootPath,
            PreparedSelectionMode: PreparedSelectionMode.Profile,
            AllRootFoldersChecked: false,
            AllExtensionsChecked: true,
            RootSelectionInitialized: true,
            RootSelectionCache: new HashSet<string>(PathComparer.Default) { ".cache", "generated", "node_modules" },
            ExtensionsSelectionInitialized: false,
            ExtensionsSelectionCache: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            IgnoreSelectionInitialized: false,
            IgnoreSelectionCache: new HashSet<IgnoreOptionId>(),
            IgnoreOptionStateCache: new Dictionary<IgnoreOptionId, bool>(),
            IgnoreAllPreference: null,
            CurrentSnapshotState: EmptySnapshotState);

    private static SelectionRefreshContext CreateProfileWithUnavailableExtensionSelectionsContext(string rootPath) =>
        new(
            Path: rootPath,
            PreparedSelectionMode: PreparedSelectionMode.Profile,
            AllRootFoldersChecked: true,
            AllExtensionsChecked: false,
            RootSelectionInitialized: false,
            RootSelectionCache: new HashSet<string>(PathComparer.Default),
            ExtensionsSelectionInitialized: true,
            ExtensionsSelectionCache: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".js" },
            IgnoreSelectionInitialized: false,
            IgnoreSelectionCache: new HashSet<IgnoreOptionId>(),
            IgnoreOptionStateCache: new Dictionary<IgnoreOptionId, bool>(),
            IgnoreAllPreference: null,
            CurrentSnapshotState: EmptySnapshotState);

    private static SelectionRefreshContext CreateProfileWithUnavailableIgnoreSelectionContext(string rootPath)
    {
        var selectedIgnoreOptions = new[] { IgnoreOptionId.DotFolders };

        return new SelectionRefreshContext(
            Path: rootPath,
            PreparedSelectionMode: PreparedSelectionMode.Profile,
            AllRootFoldersChecked: false,
            AllExtensionsChecked: true,
            RootSelectionInitialized: true,
            RootSelectionCache: new HashSet<string>(PathComparer.Default) { "docs" },
            ExtensionsSelectionInitialized: false,
            ExtensionsSelectionCache: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            IgnoreSelectionInitialized: true,
            IgnoreSelectionCache: new HashSet<IgnoreOptionId>(selectedIgnoreOptions),
            IgnoreOptionStateCache: BuildIgnoreStateCache(selectedIgnoreOptions),
            IgnoreAllPreference: null,
            CurrentSnapshotState: EmptySnapshotState);
    }

    private static SelectionRefreshContext CreateProfileWithAllVisibleRootsAndUnavailableDotFolderContext(string rootPath)
    {
        var selectedIgnoreOptions = new[] { IgnoreOptionId.DotFolders };

        return new SelectionRefreshContext(
            Path: rootPath,
            PreparedSelectionMode: PreparedSelectionMode.Profile,
            AllRootFoldersChecked: true,
            AllExtensionsChecked: true,
            RootSelectionInitialized: true,
            RootSelectionCache: new HashSet<string>(PathComparer.Default)
            {
                "docs",
                "generated",
                "logs",
                "node_modules",
                "samples",
                "src",
                "stealth-root"
            },
            ExtensionsSelectionInitialized: false,
            ExtensionsSelectionCache: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            IgnoreSelectionInitialized: true,
            IgnoreSelectionCache: new HashSet<IgnoreOptionId>(selectedIgnoreOptions),
            IgnoreOptionStateCache: BuildIgnoreStateCache(selectedIgnoreOptions),
            IgnoreAllPreference: null,
            CurrentSnapshotState: EmptySnapshotState);
    }

    private static Dictionary<IgnoreOptionId, bool> BuildIgnoreStateCache(IEnumerable<IgnoreOptionId> selectedIgnoreOptions)
    {
        var cache = new Dictionary<IgnoreOptionId, bool>();
        foreach (var optionId in selectedIgnoreOptions)
            cache[optionId] = true;

        return cache;
    }

    private static SelectionRefreshContext BuildConvergedContext(
        string rootPath,
        SelectionRefreshSnapshot snapshot,
        PreparedSelectionMode preparedSelectionMode)
    {
        return new SelectionRefreshContext(
            Path: rootPath,
            PreparedSelectionMode: preparedSelectionMode,
            AllRootFoldersChecked: snapshot.RootOptions is { Count: > 0 } rootOptions &&
                                   rootOptions.All(option => option.IsChecked),
            AllExtensionsChecked: snapshot.ExtensionOptions.Count > 0 &&
                                  snapshot.ExtensionOptions.All(option => option.IsChecked),
            RootSelectionInitialized: true,
            RootSelectionCache: snapshot.RootOptions is null
                ? new HashSet<string>(PathComparer.Default)
                : new HashSet<string>(
                    snapshot.RootOptions.Where(option => option.IsChecked).Select(option => option.Name),
                    PathComparer.Default),
            ExtensionsSelectionInitialized: true,
            ExtensionsSelectionCache: new HashSet<string>(
                snapshot.ExtensionOptions.Where(option => option.IsChecked).Select(option => option.Name),
                StringComparer.OrdinalIgnoreCase),
            IgnoreSelectionInitialized: true,
            IgnoreSelectionCache: new HashSet<IgnoreOptionId>(
                snapshot.IgnoreOptionStateCache.Where(pair => pair.Value).Select(pair => pair.Key)),
            IgnoreOptionStateCache: new Dictionary<IgnoreOptionId, bool>(snapshot.IgnoreOptionStateCache),
            IgnoreAllPreference: DeriveIgnoreAllPreference(snapshot.IgnoreOptions),
            CurrentSnapshotState: new IgnoreSectionSnapshotState(
                snapshot.HasIgnoreOptionCounts,
                snapshot.IgnoreOptionCounts,
                snapshot.ExtensionlessEntriesCount > 0,
                snapshot.ExtensionlessEntriesCount));
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

    private static void AssertEquivalentSnapshots(
        SelectionRefreshSnapshot expected,
        SelectionRefreshSnapshot actual)
    {
        AssertEquivalentSelectionOptions(expected.RootOptions, actual.RootOptions);
        AssertEquivalentSelectionOptions(expected.ExtensionOptions, actual.ExtensionOptions);
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

    private static void AssertDeferredProfileReconciliation(
        SelectionRefreshSnapshot firstSnapshot,
        SelectionRefreshSnapshot secondSnapshot,
        string workflowCaseName)
    {
        Assert.NotEqual(firstSnapshot.IgnoreOptions, secondSnapshot.IgnoreOptions);

        // Deferred reconciliation is allowed to reshuffle which dynamic ignore options are
        // visible after the first pass, because the updated root/ignore state can expose a
        // different effective tree shape on the follow-up snapshot. What must stay stable is
        // the meaning of the already visible options: the same visible option cannot silently
        // flip its checked state across the deferred pass.
        var firstVisibleStates = firstSnapshot.IgnoreOptions.ToDictionary(option => option.Id, option => option.IsChecked);
        foreach (var option in secondSnapshot.IgnoreOptions)
        {
            if (firstVisibleStates.TryGetValue(option.Id, out var firstCheckedState))
            {
                Assert.Equal(
                    firstCheckedState,
                    option.IsChecked);
            }
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
            if (option.Id is IgnoreOptionId.UseGitIgnore or IgnoreOptionId.SmartIgnore)
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

    private static async Task<ProjectMetricsSnapshot> ComputeMetricsFromSnapshotAsync(
        string rootPath,
        SelectionRefreshSnapshot snapshot,
        WorkflowServices services)
    {
        var selectedRoots = snapshot.RootOptions is null
            ? new HashSet<string>(PathComparer.Default)
            : new HashSet<string>(
                snapshot.RootOptions.Where(option => option.IsChecked).Select(option => option.Name),
                PathComparer.Default);
        var selectedExtensions = new HashSet<string>(
            snapshot.ExtensionOptions.Where(option => option.IsChecked).Select(option => option.Name),
            StringComparer.OrdinalIgnoreCase);
        var selectedIgnoreOptions = snapshot.IgnoreOptionStateCache
            .Where(pair => pair.Value)
            .Select(pair => pair.Key)
            .ToArray();

        var metrics = await ProjectLoadWorkflowRuntime.ComputeMetricsAsync(
            rootPath,
            selectedRoots,
            selectedExtensions,
            selectedIgnoreOptions,
            CancellationToken.None);

        return new ProjectMetricsSnapshot(metrics.TreeMetrics, metrics.ContentMetrics);
    }

    private static WorkflowServices CreateServices()
    {
        var localization = ProjectLoadWorkflowRuntime.CreateLocalizationService();
        var scanOptions = new ScanOptionsUseCase(new FileSystemScanner());
        var filterSelectionService = new FilterOptionSelectionService();
        var ignoreOptionsService = new IgnoreOptionsService(localization);
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

        return new WorkflowServices(
            Engine: engine,
            IgnoreRulesService: ignoreRulesService);
    }

    private static readonly IgnoreSectionSnapshotState EmptySnapshotState =
        new(false, IgnoreOptionCounts.Empty, false, 0);

    private sealed record WorkflowCase(
        string Name,
        PreparedSelectionMode PreparedSelectionMode,
        Func<string, SelectionRefreshContext> CreateContext,
        Action<SelectionRefreshSnapshot> AssertSnapshot);

    private sealed record ProjectMetricsSnapshot(
        ExportOutputMetrics TreeMetrics,
        ExportOutputMetrics ContentMetrics);

    private sealed record WorkflowServices(
        SelectionRefreshEngine Engine,
        IgnoreRulesService IgnoreRulesService);
}
