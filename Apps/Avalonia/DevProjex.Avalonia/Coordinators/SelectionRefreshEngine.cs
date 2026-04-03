using DevProjex.Application.Models;
using DevProjex.Kernel;

namespace DevProjex.Avalonia.Coordinators;

internal sealed class SelectionRefreshEngine(
    ScanOptionsUseCase scanOptions,
    FilterOptionSelectionService filterSelectionService,
    IgnoreOptionsService ignoreOptionsService,
    Func<string, IReadOnlyCollection<IgnoreOptionId>, IReadOnlyCollection<string>?, IgnoreRules> buildIgnoreRules,
    Func<string, IReadOnlyCollection<string>, IgnoreOptionsAvailability> getIgnoreOptionsAvailability)
{
    private const int MaximumDynamicSnapshotPasses = 3;
    private static readonly HashSet<string> EmptyRootSelection = new(PathComparer.Default);
    private static readonly HashSet<string> EmptyExtensionSelection = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<IgnoreOptionId> EmptyIgnoreSelection = [];

    public SelectionRefreshSnapshot ComputeFullRefreshSnapshot(
        SelectionRefreshContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var warmIgnore = BuildIgnoreOptionState(
            context.Path,
            EmptyRootSelection,
            context,
            context.CurrentSnapshotState);

        var rootSection = BuildRootSection(
            context,
            warmIgnore.SelectedIgnoreOptions,
            cancellationToken);

        var dynamicSection = BuildDynamicSection(
            context,
            rootSection.SelectedRoots,
            warmIgnore.SelectedIgnoreOptions,
            warmIgnore.IgnoreOptionStateCache,
            context.CurrentSnapshotState,
            cancellationToken);

        return new SelectionRefreshSnapshot(
            RootOptions: dynamicSection.RootOptions ?? rootSection.Options,
            ExtensionOptions: dynamicSection.ExtensionOptions,
            IgnoreOptions: dynamicSection.IgnoreOptions,
            ExtensionlessEntriesCount: dynamicSection.ExtensionlessEntriesCount,
            HasIgnoreOptionCounts: dynamicSection.HasIgnoreOptionCounts,
            IgnoreOptionCounts: dynamicSection.IgnoreOptionCounts,
            IgnoreOptionStateCache: dynamicSection.IgnoreOptionStateCache,
            RootAccessDenied: rootSection.RootAccessDenied || dynamicSection.RootAccessDenied,
            HadAccessDenied: rootSection.HadAccessDenied || dynamicSection.HadAccessDenied);
    }

    public SelectionRefreshSnapshot ComputeLiveRefreshSnapshot(
        SelectionRefreshContext context,
        IReadOnlyCollection<string> selectedRoots,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dynamicSection = BuildDynamicSection(
            context,
            selectedRoots,
            context.IgnoreSelectionCache,
            context.IgnoreOptionStateCache,
            context.CurrentSnapshotState,
            cancellationToken);

        return new SelectionRefreshSnapshot(
            RootOptions: dynamicSection.RootOptions,
            ExtensionOptions: dynamicSection.ExtensionOptions,
            IgnoreOptions: dynamicSection.IgnoreOptions,
            ExtensionlessEntriesCount: dynamicSection.ExtensionlessEntriesCount,
            HasIgnoreOptionCounts: dynamicSection.HasIgnoreOptionCounts,
            IgnoreOptionCounts: dynamicSection.IgnoreOptionCounts,
            IgnoreOptionStateCache: dynamicSection.IgnoreOptionStateCache,
            RootAccessDenied: dynamicSection.RootAccessDenied,
            HadAccessDenied: dynamicSection.HadAccessDenied);
    }

    private RootSectionSnapshot BuildRootSection(
        SelectionRefreshContext context,
        IReadOnlySet<IgnoreOptionId> selectedIgnoreOptions,
        CancellationToken cancellationToken)
    {
        var ignoreRules = buildIgnoreRules(context.Path, selectedIgnoreOptions, null);
        var scan = scanOptions.GetRootFolders(context.Path, ignoreRules, cancellationToken);

        var previousSelections = context.RootSelectionInitialized
            ? new HashSet<string>(context.RootSelectionCache, PathComparer.Default)
            : EmptyRootSelection;

        var options = filterSelectionService.BuildRootFolderOptions(
            scan.Value,
            previousSelections,
            ignoreRules,
            context.RootSelectionInitialized);
        options = SelectionSyncCoordinatorPolicy.ApplyMissingProfileSelectionsFallbackToRootFolders(
            context.PreparedSelectionMode,
            context.RootSelectionCache,
            options,
            scan.Value,
            ignoreRules,
            filterSelectionService,
            EmptyRootSelection);

        if (!ShouldSuppressAllTogglesOverride(context) && context.AllRootFoldersChecked)
            options = ForceAllChecked(options);

        var selectedRoots = CollectCheckedSelectionNames(options, PathComparer.Default);
        return new RootSectionSnapshot(options, selectedRoots, scan.RootAccessDenied, scan.HadAccessDenied);
    }

    private DynamicSectionSnapshot BuildDynamicSection(
        SelectionRefreshContext context,
        IReadOnlyCollection<string> selectedRoots,
        IReadOnlySet<IgnoreOptionId> selectedIgnoreOptions,
        IReadOnlyDictionary<IgnoreOptionId, bool> ignoreStateCache,
        IgnoreSectionSnapshotState beforeSnapshot,
        CancellationToken cancellationToken)
    {
        var currentRoots = selectedRoots;
        var currentSelectedIgnoreOptions = selectedIgnoreOptions;
        var currentIgnoreStateCache = ignoreStateCache;
        var previousSnapshot = beforeSnapshot;
        IReadOnlyList<SelectionOption>? refreshedRootOptions = null;
        var rootAccessDenied = false;
        var hadAccessDenied = false;

        // Dynamic ignore availability can feed back into the selected ignore set, especially
        // when profile fallback revives default-checked options that immediately change the
        // tree shape. A bounded convergence loop keeps the load path deterministic without
        // requiring the user to trigger another refresh manually.
        for (var passIndex = 0; passIndex < MaximumDynamicSnapshotPasses; passIndex++)
        {
            var snapshot = BuildSingleDynamicSnapshot(
                context,
                currentRoots,
                currentSelectedIgnoreOptions,
                currentIgnoreStateCache,
                cancellationToken);

            rootAccessDenied |= snapshot.RootAccessDenied;
            hadAccessDenied |= snapshot.HadAccessDenied;

            var refreshPlan = IgnoreSectionRefreshPlanBuilder.Build(
                previousSnapshot,
                snapshot.SnapshotState,
                currentSelectedIgnoreOptions,
                snapshot.SelectedIgnoreOptions);
            if (!refreshPlan.RequiresSecondSnapshotPass)
            {
                return snapshot with
                {
                    RootOptions = refreshedRootOptions,
                    RootAccessDenied = rootAccessDenied,
                    HadAccessDenied = hadAccessDenied
                };
            }

            if (refreshPlan.RequiresRootFolderRefresh)
            {
                var rebuiltRootSection = BuildRootSection(
                    context,
                    snapshot.SelectedIgnoreOptions,
                    cancellationToken);
                currentRoots = rebuiltRootSection.SelectedRoots;
                refreshedRootOptions = rebuiltRootSection.Options;
                rootAccessDenied |= rebuiltRootSection.RootAccessDenied;
                hadAccessDenied |= rebuiltRootSection.HadAccessDenied;
            }

            currentSelectedIgnoreOptions = snapshot.SelectedIgnoreOptions;
            currentIgnoreStateCache = snapshot.IgnoreOptionStateCache;
            previousSnapshot = snapshot.SnapshotState;

            if (passIndex == MaximumDynamicSnapshotPasses - 1)
            {
                return snapshot with
                {
                    RootOptions = refreshedRootOptions,
                    RootAccessDenied = rootAccessDenied,
                    HadAccessDenied = hadAccessDenied
                };
            }
        }

        throw new InvalidOperationException("The dynamic selection refresh loop exited unexpectedly.");
    }

    private DynamicSectionSnapshot BuildSingleDynamicSnapshot(
        SelectionRefreshContext context,
        IReadOnlyCollection<string> selectedRoots,
        IReadOnlySet<IgnoreOptionId> selectedIgnoreOptions,
        IReadOnlyDictionary<IgnoreOptionId, bool> ignoreStateCache,
        CancellationToken cancellationToken)
    {
        var ignoreRules = buildIgnoreRules(context.Path, selectedIgnoreOptions, selectedRoots);
        var extensionScanRules = BuildExtensionAvailabilityScanRules(ignoreRules);
        var effectiveAllowedExtensions = BuildEffectiveAllowedExtensions(context);

        // Extension availability and effective ignore counts must come from the same snapshot.
        // Otherwise the UI can briefly show mismatched counts/options after dynamic toggles appear.
        var scan = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
            context.Path,
            selectedRoots,
            extensionScanRules,
            ignoreRules,
            effectiveAllowedExtensions,
            cancellationToken);

        var visibleExtensions = new List<string>(scan.Value.Extensions.Count);
        var extensionlessEntriesCount = SplitExtensions(scan.Value.Extensions, visibleExtensions);

        var extensionOptions = filterSelectionService.BuildExtensionOptions(
            visibleExtensions,
            context.ExtensionsSelectionInitialized
                ? new HashSet<string>(context.ExtensionsSelectionCache, StringComparer.OrdinalIgnoreCase)
                : EmptyExtensionSelection);
        extensionOptions = SelectionSyncCoordinatorPolicy.ApplyMissingProfileSelectionsFallbackToExtensions(
            context.PreparedSelectionMode,
            context.ExtensionsSelectionCache,
            extensionOptions);

        if (!ShouldSuppressAllTogglesOverride(context) && context.AllExtensionsChecked)
            extensionOptions = ForceAllChecked(extensionOptions);

        var snapshotState = CreateSnapshotState(scan.Value.EffectiveIgnoreOptionCounts);
        var ignoreState = BuildIgnoreOptionState(
            context.Path,
            selectedRoots,
            context,
            snapshotState,
            ignoreStateCache,
            context.IgnoreSelectionInitialized
                ? new HashSet<IgnoreOptionId>(context.IgnoreSelectionCache)
                : EmptyIgnoreSelection);

        return new DynamicSectionSnapshot(
            RootOptions: null,
            ExtensionOptions: extensionOptions,
            IgnoreOptions: ignoreState.VisibleOptions,
            ExtensionlessEntriesCount: extensionlessEntriesCount,
            HasIgnoreOptionCounts: true,
            IgnoreOptionCounts: scan.Value.EffectiveIgnoreOptionCounts,
            IgnoreOptionStateCache: ignoreState.IgnoreOptionStateCache,
            SelectedIgnoreOptions: ignoreState.SelectedIgnoreOptions,
            SnapshotState: snapshotState,
            RootAccessDenied: scan.RootAccessDenied,
            HadAccessDenied: scan.HadAccessDenied);
    }

    private IgnoreOptionResolutionResult BuildIgnoreOptionState(
        string path,
        IReadOnlyCollection<string> selectedRoots,
        SelectionRefreshContext context,
        IgnoreSectionSnapshotState snapshotState,
        IReadOnlyDictionary<IgnoreOptionId, bool>? stateCacheOverride = null,
        IReadOnlySet<IgnoreOptionId>? previousSelectionOverride = null)
    {
        var previousSelections = previousSelectionOverride ??
                                 (context.IgnoreSelectionInitialized
                                     ? new HashSet<IgnoreOptionId>(context.IgnoreSelectionCache)
                                     : EmptyIgnoreSelection);
        var stateCache = new Dictionary<IgnoreOptionId, bool>(
            stateCacheOverride ?? context.IgnoreOptionStateCache);
        var availability = ResolveIgnoreOptionsAvailability(path, selectedRoots, snapshotState);
        var descriptors = ignoreOptionsService.GetOptions(availability);
        var useDefaultCheckedFallback = SelectionSyncCoordinatorPolicy.ShouldUseIgnoreDefaultFallback(
            context.PreparedSelectionMode,
            descriptors,
            previousSelections);

        var visibleIds = new HashSet<IgnoreOptionId>();
        var resolved = new List<ResolvedIgnoreOptionState>(descriptors.Count);
        foreach (var option in descriptors)
        {
            visibleIds.Add(option.Id);
            var isChecked = ResolveIgnoreOptionCheckedState(
                option,
                previousSelections,
                context.IgnoreSelectionInitialized,
                stateCache,
                context,
                useDefaultCheckedFallback);
            stateCache[option.Id] = isChecked;
            resolved.Add(new ResolvedIgnoreOptionState(option.Id, option.Label, option.DefaultChecked, isChecked));
        }

        PreserveMissingIgnoreSelections(previousSelections, visibleIds, stateCache);
        return new IgnoreOptionResolutionResult(
            resolved,
            stateCache,
            BuildSelectedIgnoreOptionSet(stateCache));
    }

    private IgnoreOptionsAvailability ResolveIgnoreOptionsAvailability(
        string? path,
        IReadOnlyCollection<string> selectedRootFolders,
        IgnoreSectionSnapshotState snapshotState)
    {
        if (string.IsNullOrWhiteSpace(path))
            return CreateCountDrivenIgnoreAvailability(includeGitIgnore: false, includeSmartIgnore: false);

        try
        {
            var availability = CreateCountDrivenIgnoreAvailability(getIgnoreOptionsAvailability(path, selectedRootFolders));
            if (snapshotState.HasIgnoreOptionCounts)
            {
                return availability with
                {
                    IncludeHiddenFolders = snapshotState.IgnoreOptionCounts.HiddenFolders > 0,
                    HiddenFoldersCount = snapshotState.IgnoreOptionCounts.HiddenFolders,
                    IncludeHiddenFiles = snapshotState.IgnoreOptionCounts.HiddenFiles > 0,
                    HiddenFilesCount = snapshotState.IgnoreOptionCounts.HiddenFiles,
                    IncludeDotFolders = snapshotState.IgnoreOptionCounts.DotFolders > 0,
                    DotFoldersCount = snapshotState.IgnoreOptionCounts.DotFolders,
                    IncludeDotFiles = snapshotState.IgnoreOptionCounts.DotFiles > 0,
                    DotFilesCount = snapshotState.IgnoreOptionCounts.DotFiles,
                    IncludeEmptyFolders = snapshotState.IgnoreOptionCounts.EmptyFolders > 0,
                    EmptyFoldersCount = snapshotState.IgnoreOptionCounts.EmptyFolders,
                    IncludeEmptyFiles = snapshotState.IgnoreOptionCounts.EmptyFiles > 0,
                    EmptyFilesCount = snapshotState.IgnoreOptionCounts.EmptyFiles,
                    IncludeExtensionlessFiles = snapshotState.IgnoreOptionCounts.ExtensionlessFiles > 0,
                    ExtensionlessFilesCount = snapshotState.IgnoreOptionCounts.ExtensionlessFiles
                };
            }

            if (snapshotState.HasExtensionlessEntries)
            {
                return availability with
                {
                    IncludeExtensionlessFiles = true,
                    ExtensionlessFilesCount = snapshotState.ExtensionlessEntriesCount
                };
            }

            return availability;
        }
        catch
        {
            return CreateCountDrivenIgnoreAvailability(includeGitIgnore: false, includeSmartIgnore: false);
        }
    }

    private static IgnoreSectionSnapshotState CreateSnapshotState(IgnoreOptionCounts counts) =>
        new(
            HasIgnoreOptionCounts: true,
            IgnoreOptionCounts: counts,
            HasExtensionlessEntries: counts.ExtensionlessFiles > 0,
            ExtensionlessEntriesCount: counts.ExtensionlessFiles);

    private static IReadOnlyList<SelectionOption> ForceAllChecked(IReadOnlyList<SelectionOption> options)
    {
        if (options.Count == 0)
            return options;

        var updated = new List<SelectionOption>(options.Count);
        foreach (var option in options)
            updated.Add(option with { IsChecked = true });
        return updated;
    }

    private static HashSet<string>? BuildEffectiveAllowedExtensions(SelectionRefreshContext context)
    {
        if (!ShouldSuppressAllTogglesOverride(context) && context.AllExtensionsChecked)
            return null;

        if (context.ExtensionsSelectionInitialized)
            return new HashSet<string>(context.ExtensionsSelectionCache, StringComparer.OrdinalIgnoreCase);

        return null;
    }

    private static IgnoreRules BuildExtensionAvailabilityScanRules(IgnoreRules rules)
    {
        if (!rules.IgnoreHiddenFiles &&
            !rules.IgnoreDotFiles &&
            !rules.IgnoreEmptyFiles &&
            !rules.IgnoreExtensionlessFiles)
        {
            return rules;
        }

        return rules with
        {
            IgnoreHiddenFiles = false,
            IgnoreDotFiles = false,
            IgnoreEmptyFiles = false,
            IgnoreExtensionlessFiles = false
        };
    }

    private static bool ResolveIgnoreOptionCheckedState(
        IgnoreOptionDescriptor option,
        IReadOnlySet<IgnoreOptionId> previousSelections,
        bool hasPreviousSelections,
        IReadOnlyDictionary<IgnoreOptionId, bool> stateCache,
        SelectionRefreshContext context,
        bool useDefaultCheckedFallback)
    {
        if (stateCache.TryGetValue(option.Id, out var cachedState))
            return cachedState;

        if (useDefaultCheckedFallback)
            return option.DefaultChecked;

        if (!ShouldSuppressAllTogglesOverride(context) && context.IgnoreAllPreference.HasValue)
            return context.IgnoreAllPreference.Value;

        if (context.PreparedSelectionMode == PreparedSelectionMode.Profile && hasPreviousSelections)
            return previousSelections.Contains(option.Id);

        if (previousSelections.Contains(option.Id))
            return true;

        return option.DefaultChecked;
    }

    private static void PreserveMissingIgnoreSelections(
        IReadOnlySet<IgnoreOptionId> previousSelections,
        IReadOnlySet<IgnoreOptionId> visibleIds,
        IDictionary<IgnoreOptionId, bool> stateCache)
    {
        foreach (var id in previousSelections)
        {
            if (!visibleIds.Contains(id) && !stateCache.ContainsKey(id))
                stateCache[id] = true;
        }
    }

    private static HashSet<IgnoreOptionId> BuildSelectedIgnoreOptionSet(
        IReadOnlyDictionary<IgnoreOptionId, bool> stateCache)
    {
        var selected = new HashSet<IgnoreOptionId>();
        foreach (var (id, isChecked) in stateCache)
        {
            if (isChecked)
                selected.Add(id);
        }

        return selected;
    }

    private static HashSet<string> CollectCheckedSelectionNames(
        IEnumerable<SelectionOption> options,
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

    private static int SplitExtensions(IReadOnlyCollection<string> source, ICollection<string> visibleExtensions)
    {
        var extensionlessEntriesCount = 0;
        foreach (var entry in source)
        {
            if (IsExtensionlessEntry(entry))
            {
                extensionlessEntriesCount++;
                continue;
            }

            visibleExtensions.Add(entry);
        }

        return extensionlessEntriesCount;
    }

    private static bool IsExtensionlessEntry(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var extension = Path.GetExtension(value);
        return string.IsNullOrEmpty(extension) || extension == ".";
    }

    private static bool ShouldSuppressAllTogglesOverride(SelectionRefreshContext context)
        => context.PreparedSelectionMode == PreparedSelectionMode.Profile;

    private static IgnoreOptionsAvailability CreateCountDrivenIgnoreAvailability(
        bool includeGitIgnore,
        bool includeSmartIgnore)
    {
        return new IgnoreOptionsAvailability(
            IncludeGitIgnore: includeGitIgnore,
            IncludeSmartIgnore: includeSmartIgnore);
    }

    private static IgnoreOptionsAvailability CreateCountDrivenIgnoreAvailability(IgnoreOptionsAvailability availability)
    {
        return availability with
        {
            IncludeHiddenFolders = false,
            HiddenFoldersCount = 0,
            IncludeHiddenFiles = false,
            HiddenFilesCount = 0,
            IncludeDotFolders = false,
            DotFoldersCount = 0,
            IncludeDotFiles = false,
            DotFilesCount = 0,
            IncludeEmptyFolders = false,
            EmptyFoldersCount = 0,
            IncludeEmptyFiles = false,
            EmptyFilesCount = 0,
            IncludeExtensionlessFiles = false,
            ExtensionlessFilesCount = 0
        };
    }

    private sealed record RootSectionSnapshot(
        IReadOnlyList<SelectionOption> Options,
        IReadOnlySet<string> SelectedRoots,
        bool RootAccessDenied,
        bool HadAccessDenied);

    private sealed record DynamicSectionSnapshot(
        IReadOnlyList<SelectionOption>? RootOptions,
        IReadOnlyList<SelectionOption> ExtensionOptions,
        IReadOnlyList<ResolvedIgnoreOptionState> IgnoreOptions,
        int ExtensionlessEntriesCount,
        bool HasIgnoreOptionCounts,
        IgnoreOptionCounts IgnoreOptionCounts,
        IReadOnlyDictionary<IgnoreOptionId, bool> IgnoreOptionStateCache,
        IReadOnlySet<IgnoreOptionId> SelectedIgnoreOptions,
        IgnoreSectionSnapshotState SnapshotState,
        bool RootAccessDenied,
        bool HadAccessDenied);

    private sealed record IgnoreOptionResolutionResult(
        IReadOnlyList<ResolvedIgnoreOptionState> VisibleOptions,
        IReadOnlyDictionary<IgnoreOptionId, bool> IgnoreOptionStateCache,
        IReadOnlySet<IgnoreOptionId> SelectedIgnoreOptions);
}
