using DevProjex.Application.Models;
using System.Runtime.InteropServices;

namespace DevProjex.Application.Selection;

public sealed class SelectionRefreshEngine(
    ScanOptionsUseCase scanOptions,
    FilterOptionSelectionService filterSelectionService,
    IgnoreOptionsService ignoreOptionsService,
    Func<string, IReadOnlyCollection<IgnoreOptionId>, IReadOnlyCollection<string>?, IgnoreRules> buildIgnoreRules,
    Func<string, IReadOnlyCollection<string>, IgnoreOptionsAvailability> getIgnoreOptionsAvailability)
{
    // Some dynamic chains need more than one follow-up pass:
    // controller -> directory toggle -> empty/extensionless toggle -> final root shape.
    private const int MaximumDynamicSnapshotPasses = 6;
    private static readonly HashSet<string> EmptyRootSelection = new(PathComparer.Default);
    private static readonly HashSet<string> EmptyExtensionSelection = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<IgnoreOptionId> EmptyIgnoreSelection = [];
    private readonly object _ignoreRulesBuildCacheSync = new();
    private IgnoreRulesBuildCacheEntry? _ignoreRulesBuildCache;

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
        var initialSelectedIgnoreOptions = BuildInitialFullRefreshIgnoreSelection(
            context,
            warmIgnore.SelectedIgnoreOptions);

        var rootSection = BuildRootSection(
            context,
            initialSelectedIgnoreOptions,
            cancellationToken);

        var dynamicSection = BuildDynamicSection(
            context,
            rootSection.SelectedRoots,
            initialSelectedIgnoreOptions,
            warmIgnore.IgnoreOptionStateCache,
            context.CurrentSnapshotState,
            cancellationToken,
            preserveFirstPassActiveRuntimeOptions: ReferenceEquals(initialSelectedIgnoreOptions, warmIgnore.SelectedIgnoreOptions));

        return new SelectionRefreshSnapshot(
            RootOptions: dynamicSection.RootOptions ?? rootSection.Options,
            ExtensionOptions: dynamicSection.ExtensionOptions,
            IgnoreOptions: dynamicSection.IgnoreOptions,
            ExtensionlessEntriesCount: dynamicSection.ExtensionlessEntriesCount,
            HasIgnoreOptionCounts: dynamicSection.HasIgnoreOptionCounts,
            IgnoreOptionCounts: dynamicSection.IgnoreOptionCounts,
            ControllerImpactCounts: dynamicSection.ControllerImpactCounts,
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
            cancellationToken,
            preserveFirstPassActiveRuntimeOptions: false);

        return new SelectionRefreshSnapshot(
            RootOptions: dynamicSection.RootOptions,
            ExtensionOptions: dynamicSection.ExtensionOptions,
            IgnoreOptions: dynamicSection.IgnoreOptions,
            ExtensionlessEntriesCount: dynamicSection.ExtensionlessEntriesCount,
            HasIgnoreOptionCounts: dynamicSection.HasIgnoreOptionCounts,
            IgnoreOptionCounts: dynamicSection.IgnoreOptionCounts,
            ControllerImpactCounts: dynamicSection.ControllerImpactCounts,
            IgnoreOptionStateCache: dynamicSection.IgnoreOptionStateCache,
            RootAccessDenied: dynamicSection.RootAccessDenied,
            HadAccessDenied: dynamicSection.HadAccessDenied);
    }

    private RootSectionSnapshot BuildRootSection(
        SelectionRefreshContext context,
        IReadOnlySet<IgnoreOptionId> selectedIgnoreOptions,
        CancellationToken cancellationToken)
    {
        var ignoreRules = BuildIgnoreRules(context.Path, selectedIgnoreOptions, null);
        var scan = scanOptions.GetRootFolders(context.Path, ignoreRules, cancellationToken);

        var previousSelections = context.RootSelectionInitialized
            ? new HashSet<string>(context.RootSelectionCache, PathComparer.Default)
            : EmptyRootSelection;

        var options = filterSelectionService.BuildRootFolderOptions(
            scan.Value,
            previousSelections,
            ignoreRules,
            context.RootSelectionInitialized,
            context.RootOptionStateCache);
        options = SelectionRefreshPolicy.ApplyMissingProfileSelectionsFallbackToRootFolders(
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
        CancellationToken cancellationToken,
        bool preserveFirstPassActiveRuntimeOptions = true)
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
                previousSnapshot,
                cancellationToken,
                preserveActiveRuntimeOptions: passIndex > 0 || preserveFirstPassActiveRuntimeOptions);

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
        IgnoreSectionSnapshotState previousSnapshotState,
        CancellationToken cancellationToken,
        bool preserveActiveRuntimeOptions = true)
    {
        var ignoreRules = BuildIgnoreRules(context.Path, selectedIgnoreOptions, selectedRoots);
        var extensionScanRules = BuildExtensionAvailabilityScanRules(ignoreRules);
        var effectiveExtensionPolicy = BuildEffectiveExtensionPolicy(context);

        // Extension availability and effective ignore counts must come from the same snapshot.
        // Otherwise the UI can briefly show mismatched counts/options after dynamic toggles appear.
        var scan = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
            context.Path,
            selectedRoots,
            extensionScanRules,
            ignoreRules,
            effectiveExtensionPolicy,
            includeDirectoryToggleProbeRoots: ShouldIncludeDirectoryToggleProbeRoots(context, selectedRoots, selectedIgnoreOptions),
            cancellationToken,
            includeControllerImpactProbeRoots: ShouldIncludeControllerImpactProbeRoots(context, selectedIgnoreOptions));

        var snapshotState = CreateSnapshotState(
            scan.Value.EffectiveIgnoreOptionCounts,
            scan.Value.ControllerImpactCounts);
        if (preserveActiveRuntimeOptions)
        {
            snapshotState = PreserveActiveRuntimeSnapshotState(
                snapshotState,
                previousSnapshotState,
                selectedIgnoreOptions);
        }

        var visibleExtensions = new List<string>(scan.Value.Extensions.Count);
        var extensionlessEntriesCount = SplitExtensions(scan.Value.Extensions, visibleExtensions);
        extensionlessEntriesCount = Math.Max(
            extensionlessEntriesCount,
            snapshotState.IgnoreOptionCounts.ExtensionlessFiles);

        var extensionOptions = filterSelectionService.BuildExtensionOptions(
            visibleExtensions,
            context.ExtensionsSelectionInitialized
                ? new HashSet<string>(context.ExtensionsSelectionCache, StringComparer.OrdinalIgnoreCase)
                : EmptyExtensionSelection,
            context.ExtensionOptionStateCache);
        var usedProfileFallback = SelectionRefreshPolicy.ShouldApplyMissingProfileSelectionsFallback(
            context.PreparedSelectionMode,
            context.ExtensionsSelectionCache,
            extensionOptions);
        extensionOptions = SelectionRefreshPolicy.ApplyMissingProfileSelectionsFallbackToExtensions(
            context.PreparedSelectionMode,
            context.ExtensionsSelectionCache,
            extensionOptions);

        if (!ShouldSuppressAllTogglesOverride(context) && context.AllExtensionsChecked)
            extensionOptions = ForceAllChecked(extensionOptions);

        if (usedProfileFallback &&
            !ExtensionSnapshotReusePolicy.CanReuseSnapshot(effectiveExtensionPolicy, extensionOptions))
        {
            scan = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
                context.Path,
                selectedRoots,
                extensionScanRules,
                ignoreRules,
                BuildResolvedExtensionPolicy(extensionOptions),
                includeDirectoryToggleProbeRoots: ShouldIncludeDirectoryToggleProbeRoots(context, selectedRoots, selectedIgnoreOptions),
                cancellationToken,
                includeControllerImpactProbeRoots: ShouldIncludeControllerImpactProbeRoots(context, selectedIgnoreOptions));

            snapshotState = CreateSnapshotState(
                scan.Value.EffectiveIgnoreOptionCounts,
                scan.Value.ControllerImpactCounts);
            if (preserveActiveRuntimeOptions)
            {
                snapshotState = PreserveActiveRuntimeSnapshotState(
                    snapshotState,
                    previousSnapshotState,
                    selectedIgnoreOptions);
            }

            visibleExtensions = new List<string>(scan.Value.Extensions.Count);
            extensionlessEntriesCount = SplitExtensions(scan.Value.Extensions, visibleExtensions);
            extensionlessEntriesCount = Math.Max(
                extensionlessEntriesCount,
                snapshotState.IgnoreOptionCounts.ExtensionlessFiles);

            extensionOptions = filterSelectionService.BuildExtensionOptions(
                visibleExtensions,
                context.ExtensionsSelectionInitialized
                    ? new HashSet<string>(context.ExtensionsSelectionCache, StringComparer.OrdinalIgnoreCase)
                    : EmptyExtensionSelection,
                context.ExtensionOptionStateCache);
            extensionOptions = SelectionRefreshPolicy.ApplyMissingProfileSelectionsFallbackToExtensions(
                context.PreparedSelectionMode,
                context.ExtensionsSelectionCache,
                extensionOptions);

            if (!ShouldSuppressAllTogglesOverride(context) && context.AllExtensionsChecked)
                extensionOptions = ForceAllChecked(extensionOptions);
        }

        var ignoreState = BuildIgnoreOptionState(
            context.Path,
            selectedRoots,
            context,
            snapshotState,
            ignoreStateCache,
            selectedIgnoreOptions);

        return new DynamicSectionSnapshot(
            RootOptions: null,
            ExtensionOptions: extensionOptions,
            IgnoreOptions: ignoreState.VisibleOptions,
            ExtensionlessEntriesCount: extensionlessEntriesCount,
            HasIgnoreOptionCounts: true,
            IgnoreOptionCounts: snapshotState.IgnoreOptionCounts,
            ControllerImpactCounts: snapshotState.ControllerImpactCounts,
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
        var defaultFallbackReferenceSelections = GetIgnoreDefaultFallbackReferenceSelections(
            context,
            previousSelections);
        var useDefaultCheckedFallback = SelectionRefreshPolicy.ShouldUseIgnoreDefaultFallback(
            context.PreparedSelectionMode,
            descriptors,
            defaultFallbackReferenceSelections);

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
                context.IgnoreOptionStateCacheIsComplete,
                useDefaultCheckedFallback);
            stateCache[option.Id] = isChecked;
            resolved.Add(new ResolvedIgnoreOptionState(option.Id, option.Label, option.DefaultChecked, isChecked));
        }

		PreserveMissingIgnoreSelections(previousSelections, visibleIds, stateCache);
		return new IgnoreOptionResolutionResult(
			resolved,
			stateCache,
			BuildSelectedIgnoreOptionSet(stateCache, visibleIds));
    }

    private static IReadOnlySet<IgnoreOptionId> BuildInitialFullRefreshIgnoreSelection(
        SelectionRefreshContext context,
        IReadOnlySet<IgnoreOptionId> warmSelection)
    {
        if (context.PreparedSelectionMode == PreparedSelectionMode.Profile ||
            context.IgnoreSelectionInitialized ||
            context.IgnoreAllPreference == false)
        {
            return warmSelection;
        }

        var selected = new HashSet<IgnoreOptionId>(warmSelection);
        AddDefaultDynamicIgnoreOptions(selected);
        return selected;
    }

    private static void AddDefaultDynamicIgnoreOptions(HashSet<IgnoreOptionId> selected)
    {
        // File-level defaults are cheap and safe to apply optimistically on the first
        // full refresh. Directory-level defaults are discovered from the first snapshot
        // because they can change the root-folder shape and hide their own evidence.
        selected.Add(IgnoreOptionId.HiddenFiles);
        selected.Add(IgnoreOptionId.DotFiles);
        selected.Add(IgnoreOptionId.EmptyFiles);
        selected.Add(IgnoreOptionId.ExtensionlessFiles);
    }

    private static IReadOnlySet<IgnoreOptionId> GetIgnoreDefaultFallbackReferenceSelections(
        SelectionRefreshContext context,
        IReadOnlySet<IgnoreOptionId> runtimeSelections)
    {
        if (context.PreparedSelectionMode == PreparedSelectionMode.Profile &&
            context.IgnoreSelectionInitialized &&
            !context.IgnoreOptionStateCacheIsComplete)
        {
            return context.IgnoreSelectionCache;
        }

        return runtimeSelections;
    }

    private static IgnoreSectionSnapshotState PreserveActiveRuntimeSnapshotState(
        IgnoreSectionSnapshotState current,
        IgnoreSectionSnapshotState previous,
        IReadOnlySet<IgnoreOptionId> activeOptions)
    {
        if (activeOptions.Count == 0 || !previous.HasIgnoreOptionCounts)
            return current;

        var counts = current.IgnoreOptionCounts;
        var controllerImpactCounts = current.ControllerImpactCounts;
        var dotFoldersOwnHiddenDotFolders = activeOptions.Contains(IgnoreOptionId.DotFolders);
        var dotFilesOwnHiddenDotFiles = activeOptions.Contains(IgnoreOptionId.DotFiles);
        foreach (var id in activeOptions)
        {
            // Active options can hide their own evidence on the next convergence pass.
            // Carry the last positive evidence forward inside this refresh so the option
            // stays visible with a real count instead of oscillating or rendering as zero.
            counts = id switch
            {
                IgnoreOptionId.HiddenFolders when !dotFoldersOwnHiddenDotFolders => counts with
                {
                    HiddenFolders = PreserveActiveCount(
                        counts.HiddenFolders,
                        previous.IgnoreOptionCounts.HiddenFolders)
                },
                IgnoreOptionId.HiddenFiles when !dotFilesOwnHiddenDotFiles => counts with
                {
                    HiddenFiles = PreserveActiveCount(
                        counts.HiddenFiles,
                        previous.IgnoreOptionCounts.HiddenFiles)
                },
                IgnoreOptionId.DotFolders => counts with
                {
                    DotFolders = PreserveActiveCount(
                        counts.DotFolders,
                        previous.IgnoreOptionCounts.DotFolders)
                },
                IgnoreOptionId.DotFiles => counts with
                {
                    DotFiles = PreserveActiveCount(
                        counts.DotFiles,
                        previous.IgnoreOptionCounts.DotFiles)
                },
                IgnoreOptionId.EmptyFolders => counts with
                {
                    EmptyFolders = PreserveActiveCount(
                        counts.EmptyFolders,
                        previous.IgnoreOptionCounts.EmptyFolders)
                },
                IgnoreOptionId.EmptyFiles => counts with
                {
                    EmptyFiles = PreserveActiveCount(
                        counts.EmptyFiles,
                        previous.IgnoreOptionCounts.EmptyFiles)
                },
                IgnoreOptionId.ExtensionlessFiles => counts with
                {
                    ExtensionlessFiles = PreserveActiveCount(
                        counts.ExtensionlessFiles,
                        previous.IgnoreOptionCounts.ExtensionlessFiles)
                },
                _ => counts
            };

            controllerImpactCounts = id switch
            {
                IgnoreOptionId.SmartIgnore => controllerImpactCounts with
                {
                    SmartIgnore = PreserveActiveCount(
                        controllerImpactCounts.SmartIgnore,
                        previous.ControllerImpactCounts.SmartIgnore)
                },
                IgnoreOptionId.UseGitIgnore => controllerImpactCounts with
                {
                    GitIgnore = PreserveActiveCount(
                        controllerImpactCounts.GitIgnore,
                        previous.ControllerImpactCounts.GitIgnore)
                },
                _ => controllerImpactCounts
            };
        }

        if (counts == current.IgnoreOptionCounts &&
            controllerImpactCounts == current.ControllerImpactCounts)
        {
            return current;
        }

        return current with
        {
            IgnoreOptionCounts = counts,
            ControllerImpactCounts = controllerImpactCounts,
            HasExtensionlessEntries = counts.ExtensionlessFiles > 0,
            ExtensionlessEntriesCount = counts.ExtensionlessFiles
        };
    }

    private static int PreserveActiveCount(int currentCount, int previousCount) =>
        currentCount > 0 ? currentCount : previousCount;

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
                    IncludeGitIgnore = availability.IncludeGitIgnore &&
                                       snapshotState.ControllerImpactCounts.GitIgnore > 0,
                    IncludeSmartIgnore = availability.IncludeSmartIgnore &&
                                         snapshotState.ControllerImpactCounts.SmartIgnore > 0,
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

    private static IgnoreSectionSnapshotState CreateSnapshotState(
        IgnoreOptionCounts counts,
        IgnoreControllerImpactCounts controllerImpactCounts) =>
        new(
            HasIgnoreOptionCounts: true,
            IgnoreOptionCounts: counts,
            ControllerImpactCounts: controllerImpactCounts,
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

    private static IExtensionInclusionPolicy? BuildEffectiveExtensionPolicy(SelectionRefreshContext context)
    {
        if (!ShouldSuppressAllTogglesOverride(context) && context.AllExtensionsChecked)
            return null;

		if (!context.ExtensionsSelectionInitialized)
			return null;

		var previousSelections = new HashSet<string>(
			context.ExtensionsSelectionCache,
			StringComparer.OrdinalIgnoreCase);
		var stateCache = context.ExtensionOptionStateCache;

		return new ExtensionSelectionInclusionPolicy(
            new SelectionStateResolver(previousSelections, stateCache),
            defaultForNewExtension: stateCache is not null);
    }

    private static IExtensionInclusionPolicy BuildResolvedExtensionPolicy(
        IReadOnlyList<SelectionOption> extensionOptions)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in extensionOptions)
        {
            if (option.IsChecked)
                selected.Add(option.Name);
        }

        return new ExtensionSetInclusionPolicy(selected);
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
        bool stateCacheIsComplete,
        bool useDefaultCheckedFallback)
    {
        if (stateCache.TryGetValue(option.Id, out var cachedState))
        {
            if (!cachedState &&
                useDefaultCheckedFallback &&
                !stateCacheIsComplete &&
                !context.IgnoreOptionStateCache.ContainsKey(option.Id) &&
                SelectionRefreshPolicy.CanUseIgnoreDefaultFallback(option.Id))
            {
                // Legacy selected-only profiles can expose new options after a convergence
                // pass. A transient false from an earlier pass must not block the fallback
                // that makes newly available non-controller options checked by default.
                return option.DefaultChecked;
            }

            return cachedState;
        }

        if (context.IgnoreAllPreference.HasValue)
            return context.IgnoreAllPreference.Value;

        if (stateCacheIsComplete)
            return option.DefaultChecked;

        if (useDefaultCheckedFallback && SelectionRefreshPolicy.CanUseIgnoreDefaultFallback(option.Id))
            return option.DefaultChecked;

        if (context.PreparedSelectionMode == PreparedSelectionMode.Profile && hasPreviousSelections)
            return previousSelections.Contains(option.Id);

        if (previousSelections.Contains(option.Id))
            return true;

        return option.DefaultChecked;
    }

    private static void PreserveMissingIgnoreSelections(
        IReadOnlySet<IgnoreOptionId> previousSelections,
        IReadOnlySet<IgnoreOptionId> visibleIds,
        Dictionary<IgnoreOptionId, bool> stateCache)
    {
        foreach (var id in previousSelections)
        {
            if (visibleIds.Contains(id))
                continue;

            ref var cachedState = ref CollectionsMarshal.GetValueRefOrAddDefault(
                stateCache,
                id,
                out var exists);
            if (!exists)
                cachedState = true;
        }
    }

    private IgnoreRules BuildIgnoreRules(
        string path,
        IReadOnlyCollection<IgnoreOptionId> selectedIgnoreOptions,
        IReadOnlyCollection<string>? selectedRootFolders)
    {
        var cacheKey = IgnoreRulesBuildCacheKeyBuilder.Build(path, selectedIgnoreOptions, selectedRootFolders);

        lock (_ignoreRulesBuildCacheSync)
        {
            if (_ignoreRulesBuildCache is not null &&
                string.Equals(_ignoreRulesBuildCache.Key, cacheKey, StringComparison.Ordinal))
            {
                return _ignoreRulesBuildCache.Rules;
            }
        }

        var rules = buildIgnoreRules(path, selectedIgnoreOptions, selectedRootFolders);

        lock (_ignoreRulesBuildCacheSync)
            _ignoreRulesBuildCache = new IgnoreRulesBuildCacheEntry(cacheKey, rules);

        return rules;
    }

	private static HashSet<IgnoreOptionId> BuildSelectedIgnoreOptionSet(
		IReadOnlyDictionary<IgnoreOptionId, bool> stateCache,
		IReadOnlySet<IgnoreOptionId> visibleIds)
	{
		var selected = new HashSet<IgnoreOptionId>();
		foreach (var (id, isChecked) in stateCache)
		{
			// Hidden states are kept for profile roundtrip and transient availability churn,
			// but invisible options must never silently affect the active tree/export rules.
			if (isChecked && visibleIds.Contains(id))
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

    private static bool ShouldIncludeControllerImpactProbeRoots(
        SelectionRefreshContext context,
        IReadOnlySet<IgnoreOptionId> selectedIgnoreOptions)
    {
        var hasActiveController =
            selectedIgnoreOptions.Contains(IgnoreOptionId.UseGitIgnore) ||
            selectedIgnoreOptions.Contains(IgnoreOptionId.SmartIgnore);

        if (hasActiveController)
        {
            // Controller options also shape the root-folder list. If a selected controller
            // hides root-level generated/log/artifact folders, its impact must stay visible
            // even when the current content scope is a manually selected root subset.
            return true;
        }

        return context.AllRootFoldersChecked ||
               !ShouldSuppressAllTogglesOverride(context) ||
               HasCompleteSelectionStateForNewRootLevelToggles(context);
    }

    private static bool ShouldIncludeDirectoryToggleProbeRoots(
        SelectionRefreshContext context,
        IReadOnlyCollection<string> selectedRoots,
        IReadOnlySet<IgnoreOptionId> selectedIgnoreOptions)
    {
        var hasDirectoryToggle =
            selectedIgnoreOptions.Contains(IgnoreOptionId.DotFolders) ||
            selectedIgnoreOptions.Contains(IgnoreOptionId.HiddenFolders);
        var canDiscoverNewRootLevelToggle =
            context.AllRootFoldersChecked ||
            HasCompleteSelectionStateForNewRootLevelToggles(context);

        if (!hasDirectoryToggle)
        {
            // Root-level directory toggles are different from nested candidates: a newly
            // discovered .idea/.git/hidden root can be selected before DotFolders/HiddenFolders
            // exists in the ignore section. The probe must still run so the new ignore option
            // can appear checked by default instead of forcing the user to discover it manually.
            return canDiscoverNewRootLevelToggle ||
                   ContainsDotDirectoryName(context.RootSelectionCache) ||
                   ContainsDotDirectoryName(selectedRoots);
        }

        // Once a directory-level toggle is active, the probe must remain enabled even when
        // that toggle hides its own root-level evidence. Without this, DotFolders can appear
        // checked for one pass and then disappear on the convergence pass that hides .idea.
        if (canDiscoverNewRootLevelToggle || !ShouldSuppressAllTogglesOverride(context))
            return true;

        // Profile/default restoration has the same requirement: preserve the active toggle
        // until the user explicitly changes it, but legacy selected-only profiles cannot
        // promote an unselected .cache/.idea root into the ignore section.
        return ContainsDotDirectoryName(context.RootSelectionCache) ||
               ContainsDotDirectoryName(selectedRoots) ||
               selectedRoots.Count < context.RootSelectionCache.Count;
    }

    private static bool HasCompleteSelectionStateForNewRootLevelToggles(SelectionRefreshContext context) =>
        context.PreparedSelectionMode != PreparedSelectionMode.Profile ||
        context.RootOptionStateCache is not null ||
        context.IgnoreOptionStateCacheIsComplete;

    private static bool ContainsDotDirectoryName(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            if (IgnoreRuleSemantics.IsDotName(name))
                return true;
        }

        return false;
    }

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
        IgnoreControllerImpactCounts ControllerImpactCounts,
        IReadOnlyDictionary<IgnoreOptionId, bool> IgnoreOptionStateCache,
        IReadOnlySet<IgnoreOptionId> SelectedIgnoreOptions,
        IgnoreSectionSnapshotState SnapshotState,
        bool RootAccessDenied,
        bool HadAccessDenied);

    private sealed record IgnoreOptionResolutionResult(
        IReadOnlyList<ResolvedIgnoreOptionState> VisibleOptions,
        IReadOnlyDictionary<IgnoreOptionId, bool> IgnoreOptionStateCache,
        IReadOnlySet<IgnoreOptionId> SelectedIgnoreOptions);

    private sealed record IgnoreRulesBuildCacheEntry(string Key, IgnoreRules Rules);
}
