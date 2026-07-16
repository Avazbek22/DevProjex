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
    private static readonly IgnoreSectionSnapshotState EmptySnapshotState = new(
        HasIgnoreOptionCounts: false,
        IgnoreOptionCounts: IgnoreOptionCounts.Empty,
        ControllerImpactCounts: IgnoreControllerImpactCounts.Empty,
        HasExtensionlessEntries: false,
        ExtensionlessEntriesCount: 0);
    private readonly object _ignoreRulesBuildCacheSync = new();
    private IgnoreRulesBuildCacheEntry? _ignoreRulesBuildCache;

	public void InvalidateCaches()
	{
		lock (_ignoreRulesBuildCacheSync)
			_ignoreRulesBuildCache = null;
	}

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
        var dynamicContext = EnsureRuntimeRootStateCache(context, rootSection.Options);

        var dynamicSection = BuildDynamicSection(
            dynamicContext,
            rootSection.Options,
            rootSection.SelectedRoots,
            initialSelectedIgnoreOptions,
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
            ControllerImpactCounts: dynamicSection.ControllerImpactCounts,
            IgnoreOptionStateCache: dynamicSection.IgnoreOptionStateCache,
            RootAccessDenied: rootSection.RootAccessDenied || dynamicSection.RootAccessDenied,
            HadAccessDenied: rootSection.HadAccessDenied || dynamicSection.HadAccessDenied,
            TreeInventory: dynamicSection.TreeInventory,
            VisibleExtensionOptions: dynamicSection.VisibleExtensionOptions);
    }

    public SelectionRefreshSnapshot ComputeLiveRefreshSnapshot(
        SelectionRefreshContext context,
        IReadOnlyCollection<string> selectedRoots,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var selectedIgnoreOptions = BuildInitialLiveRefreshIgnoreSelection(context);
        var dynamicSection = BuildDynamicSection(
            context,
            rootOptions: null,
            selectedRoots,
            selectedIgnoreOptions,
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
            ControllerImpactCounts: dynamicSection.ControllerImpactCounts,
            IgnoreOptionStateCache: dynamicSection.IgnoreOptionStateCache,
            RootAccessDenied: dynamicSection.RootAccessDenied,
            HadAccessDenied: dynamicSection.HadAccessDenied,
            TreeInventory: dynamicSection.TreeInventory,
            VisibleExtensionOptions: dynamicSection.VisibleExtensionOptions);
    }

    private RootSectionSnapshot BuildRootSection(
        SelectionRefreshContext context,
        IReadOnlySet<IgnoreOptionId> selectedIgnoreOptions,
        CancellationToken cancellationToken)
    {
        var discoveryRules = BuildIgnoreRules(context.Path, selectedIgnoreOptions, null);
        var scan = scanOptions.GetRootFolders(context.Path, discoveryRules, cancellationToken);
        var ignoreRules = BuildIgnoreRules(context.Path, selectedIgnoreOptions, scan.Value);
        var visibleRootFolders = RootFolderVisibilityProjection.ApplyScopedControllerRules(
            context.Path,
            scan.Value,
            ignoreRules,
            cancellationToken);

        var previousSelections = context.RootSelectionInitialized
            ? new HashSet<string>(context.RootSelectionCache, PathComparer.Default)
            : EmptyRootSelection;

        var options = filterSelectionService.BuildRootFolderOptions(
            visibleRootFolders,
            previousSelections,
            ignoreRules,
            context.RootSelectionInitialized,
            context.RootOptionStateCache);
        options = SelectionRefreshPolicy.ApplyMissingProfileSelectionsFallbackToRootFolders(
            context.PreparedSelectionMode,
            context.RootSelectionCache,
            options,
            visibleRootFolders,
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
        IReadOnlyList<SelectionOption>? rootOptions,
        IReadOnlyCollection<string> selectedRoots,
        IReadOnlySet<IgnoreOptionId> selectedIgnoreOptions,
        IReadOnlyDictionary<IgnoreOptionId, bool> ignoreStateCache,
        IgnoreSectionSnapshotState beforeSnapshot,
        CancellationToken cancellationToken)
    {
        var currentRoots = selectedRoots;
        var currentRootOptions = rootOptions;
        var currentSelectedIgnoreOptions = selectedIgnoreOptions;
        var currentIgnoreStateCache = ignoreStateCache;
        var previousSnapshot = beforeSnapshot;
        var previousRuntimeSnapshot = EmptySnapshotState;
        IReadOnlyList<SelectionOption>? refreshedRootOptions = null;
        ProjectWorkspaceScanSnapshot? reusableWorkspaceScan = null;
        HashSet<string>? reusableRemovedRootEmptyFolderImpactRoots = null;
        var rootAccessDenied = false;
        var hadAccessDenied = false;

        // Dynamic ignore availability can feed back into the selected ignore set, especially
        // when profile fallback revives default-checked options that immediately change the
        // tree shape. A bounded convergence loop keeps the load path deterministic without
        // requiring the user to trigger another refresh manually.
        for (var passIndex = 0; passIndex < MaximumDynamicSnapshotPasses; passIndex++)
        {
            var workspaceScanReuse = reusableWorkspaceScan;
            var removedRootEmptyFolderImpactRoots = reusableRemovedRootEmptyFolderImpactRoots;
            reusableWorkspaceScan = null;
            reusableRemovedRootEmptyFolderImpactRoots = null;
            var snapshot = BuildSingleDynamicSnapshot(
                context,
                currentRoots,
                currentSelectedIgnoreOptions,
                currentIgnoreStateCache,
                previousRuntimeSnapshot,
                workspaceScanReuse,
                removedRootEmptyFolderImpactRoots,
                cancellationToken);

            rootAccessDenied |= snapshot.RootAccessDenied;
            hadAccessDenied |= snapshot.HadAccessDenied;
            var rootProjectionChanged = false;

            if (currentRootOptions is not null && snapshot.TreeInventory is not null)
            {
                var projectedRootOptions = ProjectTreeInventoryRootFolderProjection
                    .RemoveCheckedRootsWithoutVisibleStructure(
                        snapshot.TreeInventory,
                        currentRootOptions,
                        CollectCheckedSelectionNames(
                            snapshot.VisibleExtensionOptions,
                            StringComparer.OrdinalIgnoreCase),
                        snapshot.EffectiveRules,
                        out var emptyFolderOwnedRemovedRoots,
                        cancellationToken);
                if (!ReferenceEquals(projectedRootOptions, currentRootOptions))
                {
                    currentRootOptions = projectedRootOptions;
                    refreshedRootOptions = projectedRootOptions;
                    currentRoots = CollectCheckedSelectionNames(projectedRootOptions, PathComparer.Default);
                    rootProjectionChanged = true;
                    if (emptyFolderOwnedRemovedRoots is not null)
                    {
                        removedRootEmptyFolderImpactRoots ??= new HashSet<string>(PathComparer.Default);
                        removedRootEmptyFolderImpactRoots.UnionWith(emptyFolderOwnedRemovedRoots);
                    }
                }
            }

            var refreshPlan = IgnoreSectionRefreshPlanBuilder.Build(
                previousSnapshot,
                snapshot.SnapshotState,
                BuildMeasuredSelectionForRefreshPlanning(currentSelectedIgnoreOptions, snapshot.SnapshotState),
                snapshot.SelectedIgnoreOptions);
            var canReuseWorkspaceScan = rootProjectionChanged &&
                                       !refreshPlan.RequiresSecondSnapshotPass;
            if (!refreshPlan.RequiresSecondSnapshotPass && !rootProjectionChanged)
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
                currentRootOptions = rebuiltRootSection.Options;
                refreshedRootOptions = rebuiltRootSection.Options;
                rootAccessDenied |= rebuiltRootSection.RootAccessDenied;
                hadAccessDenied |= rebuiltRootSection.HadAccessDenied;
            }

            currentSelectedIgnoreOptions = snapshot.SelectedIgnoreOptions;
            currentIgnoreStateCache = snapshot.IgnoreOptionStateCache;
            previousSnapshot = snapshot.SnapshotState;
            previousRuntimeSnapshot = snapshot.SnapshotState;
            if (canReuseWorkspaceScan)
            {
                reusableWorkspaceScan = snapshot.WorkspaceScan;
                reusableRemovedRootEmptyFolderImpactRoots = removedRootEmptyFolderImpactRoots;
            }

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
        IgnoreSectionSnapshotState previousRuntimeSnapshotState,
        ProjectWorkspaceScanSnapshot? reusableWorkspaceScan,
        IReadOnlySet<string>? retainedRemovedRootEmptyFolderImpactRoots,
        CancellationToken cancellationToken)
    {
        var ignoreRules = BuildIgnoreRules(context.Path, selectedIgnoreOptions, selectedRoots);
        var extensionScanRules = BuildExtensionAvailabilityScanRules(ignoreRules);
        var effectiveExtensionPolicy = BuildEffectiveExtensionPolicy(context);

        // Extension availability, effective ignore counts, and optional tree inventory must
        // come from the same filesystem observation. This prevents mismatched UI sections and
        // lets project load reuse the scan instead of enumerating the tree again.
        var scan = GetDynamicSectionScan(
            context,
            selectedRoots,
            selectedIgnoreOptions,
            extensionScanRules,
            ignoreRules,
            effectiveExtensionPolicy,
            reusableWorkspaceScan,
            retainedRemovedRootEmptyFolderImpactRoots,
            cancellationToken);
        var scanData = scan.Value.IgnoreSection;

        var snapshotState = CreateSnapshotState(
            scanData.EffectiveIgnoreOptionCounts,
            scanData.ControllerImpactCounts);
        snapshotState = PreserveActiveRuntimeSnapshotState(
            snapshotState,
            previousRuntimeSnapshotState,
            selectedIgnoreOptions);

        var visibleExtensions = new List<string>(scanData.Extensions.Count);
        var extensionlessEntriesCount = SplitExtensions(scanData.Extensions, visibleExtensions);
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

        var visibleExtensionOptions = FilterVisibleExtensionOptions(
            extensionOptions,
            scanData.VisibleExtensions);

        if (usedProfileFallback &&
            !ExtensionSnapshotReusePolicy.CanReuseSnapshot(effectiveExtensionPolicy, extensionOptions))
        {
            scan = GetDynamicSectionScan(
                context,
                selectedRoots,
                selectedIgnoreOptions,
                extensionScanRules,
                ignoreRules,
                BuildResolvedExtensionPolicy(extensionOptions),
                reusableWorkspaceScan: null,
                retainedRemovedRootEmptyFolderImpactRoots: null,
                cancellationToken);
            scanData = scan.Value.IgnoreSection;

            snapshotState = CreateSnapshotState(
                scanData.EffectiveIgnoreOptionCounts,
                scanData.ControllerImpactCounts);
            snapshotState = PreserveActiveRuntimeSnapshotState(
                snapshotState,
                previousRuntimeSnapshotState,
                selectedIgnoreOptions);

            visibleExtensions = new List<string>(scanData.Extensions.Count);
            extensionlessEntriesCount = SplitExtensions(scanData.Extensions, visibleExtensions);
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

            visibleExtensionOptions = FilterVisibleExtensionOptions(
                extensionOptions,
                scanData.VisibleExtensions);
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
            VisibleExtensionOptions: visibleExtensionOptions,
            IgnoreOptions: ignoreState.VisibleOptions,
            ExtensionlessEntriesCount: extensionlessEntriesCount,
            HasIgnoreOptionCounts: true,
            IgnoreOptionCounts: snapshotState.IgnoreOptionCounts,
            ControllerImpactCounts: snapshotState.ControllerImpactCounts,
            IgnoreOptionStateCache: ignoreState.IgnoreOptionStateCache,
            SelectedIgnoreOptions: ignoreState.SelectedIgnoreOptions,
            SnapshotState: snapshotState,
            RootAccessDenied: scan.RootAccessDenied,
            HadAccessDenied: scan.HadAccessDenied,
            TreeInventory: scan.Value.TreeInventory,
            EffectiveRules: ignoreRules,
            WorkspaceScan: scan.Value);
    }

    private ScanResult<ProjectWorkspaceScanSnapshot> GetDynamicSectionScan(
        SelectionRefreshContext context,
        IReadOnlyCollection<string> selectedRoots,
        IReadOnlySet<IgnoreOptionId> selectedIgnoreOptions,
        IgnoreRules extensionScanRules,
        IgnoreRules ignoreRules,
        IExtensionInclusionPolicy? effectiveExtensionPolicy,
        ProjectWorkspaceScanSnapshot? reusableWorkspaceScan,
        IReadOnlySet<string>? retainedRemovedRootEmptyFolderImpactRoots,
        CancellationToken cancellationToken)
    {
        var includeDirectoryToggleProbeRoots = ShouldIncludeDirectoryToggleProbeRoots(
            context,
            selectedRoots,
            selectedIgnoreOptions);
        var includeControllerImpactProbeRoots = ShouldIncludeControllerImpactProbeRoots(
            context,
            selectedIgnoreOptions);
        if (context.CaptureTreeInventory &&
            reusableWorkspaceScan is not null &&
            ProjectWorkspaceScanProjection.TryProjectSelectedRoots(
                reusableWorkspaceScan,
                selectedRoots,
                includeDirectoryToggleProbeRoots,
                includeControllerImpactProbeRoots,
                retainedRemovedRootEmptyFolderImpactRoots,
                out var projectedScan))
        {
            return projectedScan;
        }

        return context.CaptureTreeInventory
            ? scanOptions.GetProjectWorkspaceSnapshotForRootFolders(
                context.Path,
                selectedRoots,
                extensionScanRules,
                ignoreRules,
                effectiveExtensionPolicy,
                includeDirectoryToggleProbeRoots,
                cancellationToken,
                includeControllerImpactProbeRoots,
                captureRootScanBreakdown: true)
            : WrapIgnoreSectionScan(scanOptions.GetIgnoreSectionSnapshotForRootFolders(
                context.Path,
                selectedRoots,
                extensionScanRules,
                ignoreRules,
                effectiveExtensionPolicy,
                includeDirectoryToggleProbeRoots,
                cancellationToken,
                includeControllerImpactProbeRoots));
    }

    private static ScanResult<ProjectWorkspaceScanSnapshot> WrapIgnoreSectionScan(ScanResult<IgnoreSectionScanData> scan)
    {
        return new ScanResult<ProjectWorkspaceScanSnapshot>(
            new ProjectWorkspaceScanSnapshot(scan.Value, TreeInventory: null),
            scan.RootAccessDenied,
            scan.HadAccessDenied);
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
        var availability = ResolveIgnoreOptionsAvailability(
            path,
            selectedRoots,
            snapshotState,
            stateCache,
            context.IgnoreOptionStateCacheIsComplete);

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
		RemoveTransientControllerDefaults(context, visibleIds, stateCache);
		return new IgnoreOptionResolutionResult(
			resolved,
			stateCache,
			BuildSelectedIgnoreOptionSet(stateCache, visibleIds));
	}

	private static void RemoveTransientControllerDefaults(
		SelectionRefreshContext context,
		IReadOnlySet<IgnoreOptionId> visibleIds,
		Dictionary<IgnoreOptionId, bool> stateCache)
	{
		if (context.IgnoreSelectionInitialized || context.IgnoreOptionStateCacheIsComplete)
			return;

		// Warm discovery exposes structurally available controllers before the scanner has
		// measured whether they affect anything. Do not persist those optimistic defaults
		// when the measured UI intentionally hides them; otherwise Ignore All later turns
		// never-visible controllers into explicit unchecked options.
		if (!visibleIds.Contains(IgnoreOptionId.UseGitIgnore))
			stateCache.Remove(IgnoreOptionId.UseGitIgnore);
		if (!visibleIds.Contains(IgnoreOptionId.SmartIgnore))
			stateCache.Remove(IgnoreOptionId.SmartIgnore);
	}

    private static IReadOnlySet<IgnoreOptionId> BuildInitialFullRefreshIgnoreSelection(
        SelectionRefreshContext context,
        IReadOnlySet<IgnoreOptionId> warmSelection)
    {
        var selected = new HashSet<IgnoreOptionId>(warmSelection);
        if (context.IgnoreOptionStateCacheIsComplete)
            AddCheckedIgnoreStateCacheSelections(selected, context.IgnoreOptionStateCache);
        else if (context.PreparedSelectionMode == PreparedSelectionMode.Profile &&
                 context.IgnoreSelectionInitialized)
        {
            // Legacy selected-only profiles do not have a complete state cache. Their
            // selected ids still need one optimistic rule-build pass so self-hidden
            // options such as DotFolders can prove whether they affect the current tree.
            selected.UnionWith(context.IgnoreSelectionCache);
        }

        if (context.PreparedSelectionMode == PreparedSelectionMode.Profile ||
            context.IgnoreSelectionInitialized ||
            context.IgnoreAllPreference == false)
        {
            return selected;
        }

        AddDefaultDynamicIgnoreOptions(selected);
        return selected;
    }

    private static IReadOnlySet<IgnoreOptionId> BuildInitialLiveRefreshIgnoreSelection(SelectionRefreshContext context)
    {
        // Live refresh must use the same active-rule source as full refresh. Visible
        // options alone are not enough because self-hidden toggles stay active through
        // the complete state cache after they remove their own visible evidence.
        var selected = context.IgnoreSelectionInitialized
            ? new HashSet<IgnoreOptionId>(context.IgnoreSelectionCache)
            : new HashSet<IgnoreOptionId>();

        if (context.IgnoreOptionStateCacheIsComplete)
            AddCheckedIgnoreStateCacheSelections(selected, context.IgnoreOptionStateCache);

        return selected;
    }

    private static void AddCheckedIgnoreStateCacheSelections(
        HashSet<IgnoreOptionId> selected,
        IReadOnlyDictionary<IgnoreOptionId, bool> stateCache)
    {
        foreach (var (optionId, isChecked) in stateCache)
        {
            if (isChecked)
                selected.Add(optionId);
        }
    }

    private static void AddDefaultDynamicIgnoreOptions(HashSet<IgnoreOptionId> selected)
    {
        // Defaults are applied optimistically before the first expensive snapshot pass.
        // The scanner reports direct impact for self-hidden root-level directories and
        // controller-owned artifacts, so toggles can prove their own root-level evidence
        // before they filter it out of the visible root list. Removing controller defaults
        // here makes top-level obj/bin/log/cache roots disappear first and then prevents the
        // UI from discovering which checkbox should bring them back.
        selected.Add(IgnoreOptionId.UseGitIgnore);
        selected.Add(IgnoreOptionId.SmartIgnore);
        selected.Add(IgnoreOptionId.HiddenFolders);
        selected.Add(IgnoreOptionId.HiddenFiles);
        selected.Add(IgnoreOptionId.DotFolders);
        selected.Add(IgnoreOptionId.DotFiles);
        selected.Add(IgnoreOptionId.EmptyFolders);
        selected.Add(IgnoreOptionId.EmptyFiles);
        selected.Add(IgnoreOptionId.ExtensionlessFiles);
    }

    private static SelectionRefreshContext EnsureRuntimeRootStateCache(
        SelectionRefreshContext context,
        IReadOnlyList<SelectionOption> rootOptions)
    {
        if (context.RootOptionStateCache is not null || rootOptions.Count == 0)
            return context;

        var stateCache = new Dictionary<string, bool>(rootOptions.Count, PathComparer.Default);
        foreach (var option in rootOptions)
            stateCache[option.Name] = option.IsChecked;

        // The UI marks root state as complete immediately after applying root options.
        // Full refresh must use the same runtime contract inside one call, otherwise the
        // second F5 can discover different root-level ignore toggles than the first one.
        return context with { RootOptionStateCache = stateCache };
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
            // Counts are preserved only inside the current convergence loop. This keeps
            // self-hidden active toggles stable without leaking stale evidence from an old
            // root/extension scope into a new refresh.
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

    private static IReadOnlySet<IgnoreOptionId> BuildMeasuredSelectionForRefreshPlanning(
        IReadOnlySet<IgnoreOptionId> selectedOptions,
        IgnoreSectionSnapshotState snapshotState)
    {
        if (selectedOptions.Count == 0 || !snapshotState.HasIgnoreOptionCounts)
            return selectedOptions;

        // Optimistic defaults can be active during the first scan even when the project
        // has no matching entries. Treat only measured-impact options as planning inputs;
        // otherwise a zero-impact default disappearing from the visible section would
        // look like a user-visible mutation and force a redundant convergence pass.
        HashSet<IgnoreOptionId>? measured = null;
        foreach (var optionId in selectedOptions)
        {
            if (!HasMeasuredIgnoreImpact(optionId, snapshotState))
                continue;

            measured ??= new HashSet<IgnoreOptionId>();
            measured.Add(optionId);
        }

        return measured ?? EmptyIgnoreSelection;
    }

    private static bool HasMeasuredIgnoreImpact(
        IgnoreOptionId optionId,
        IgnoreSectionSnapshotState snapshotState)
    {
        var counts = snapshotState.IgnoreOptionCounts;
        var controllerImpactCounts = snapshotState.ControllerImpactCounts;
        return optionId switch
        {
            IgnoreOptionId.UseGitIgnore => controllerImpactCounts.GitIgnore > 0,
            IgnoreOptionId.SmartIgnore => controllerImpactCounts.SmartIgnore > 0,
            IgnoreOptionId.HiddenFolders => counts.HiddenFolders > 0,
            IgnoreOptionId.HiddenFiles => counts.HiddenFiles > 0,
            IgnoreOptionId.DotFolders => counts.DotFolders > 0,
            IgnoreOptionId.DotFiles => counts.DotFiles > 0,
            IgnoreOptionId.EmptyFolders => counts.EmptyFolders > 0,
            IgnoreOptionId.EmptyFiles => counts.EmptyFiles > 0,
            IgnoreOptionId.ExtensionlessFiles => counts.ExtensionlessFiles > 0,
            _ => true
        };
    }

    private IgnoreOptionsAvailability ResolveIgnoreOptionsAvailability(
        string? path,
        IReadOnlyCollection<string> selectedRootFolders,
        IgnoreSectionSnapshotState snapshotState,
        IReadOnlyDictionary<IgnoreOptionId, bool> stateCache,
        bool stateCacheIsComplete)
    {
        if (string.IsNullOrWhiteSpace(path))
            return CreateCountDrivenIgnoreAvailability(includeGitIgnore: false, includeSmartIgnore: false);

        try
        {
            var availability = CreateCountDrivenIgnoreAvailability(getIgnoreOptionsAvailability(path, selectedRootFolders));
            if (snapshotState.HasIgnoreOptionCounts)
            {
                var hasMeasuredGitIgnoreImpact = snapshotState.ControllerImpactCounts.GitIgnore > 0;
                var hasMeasuredSmartIgnoreImpact = snapshotState.ControllerImpactCounts.SmartIgnore > 0;
                var canPromoteSmartIgnoreFromMeasuredImpact =
                    hasMeasuredSmartIgnoreImpact && !availability.SmartIgnoreFollowsGitIgnore;
                return availability with
                {
                    // Scoped availability can become false after a controller hides its own
                    // top-level root option. A measured impact count is stronger evidence:
                    // keep the controller visible so the user can reverse that filtering.
                    // Smart Ignore is the exception when it follows Use .gitignore; then the
                    // measured smart impact belongs to the gitignore controller in the UI.
                    IncludeGitIgnore = (availability.IncludeGitIgnore || hasMeasuredGitIgnoreImpact) &&
                                       ShouldKeepControllerVisible(
                                           IgnoreOptionId.UseGitIgnore,
                                           snapshotState.ControllerImpactCounts.GitIgnore,
                                           stateCache,
                                           stateCacheIsComplete),
                    IncludeSmartIgnore = (availability.IncludeSmartIgnore || canPromoteSmartIgnoreFromMeasuredImpact) &&
                                          ShouldKeepControllerVisible(
                                              IgnoreOptionId.SmartIgnore,
                                              snapshotState.ControllerImpactCounts.SmartIgnore,
                                             stateCache,
                                             stateCacheIsComplete),
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

    private static bool ShouldKeepControllerVisible(
        IgnoreOptionId optionId,
        int controllerImpactCount,
        IReadOnlyDictionary<IgnoreOptionId, bool> stateCache,
        bool stateCacheIsComplete)
    {
        if (controllerImpactCount > 0)
            return true;

        // Controller toggles are reversible UI controls. An explicit unchecked state must
        // stay visible even when its own current impact drops to zero; otherwise turning
        // .gitignore off can make it impossible to turn back on. Checked zero-impact
        // controllers remain hidden so restored profiles do not promote no-op rules.
        return stateCacheIsComplete &&
               stateCache.TryGetValue(optionId, out var isChecked) &&
               !isChecked;
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

    private static IReadOnlyList<SelectionOption> FilterVisibleExtensionOptions(
        IReadOnlyList<SelectionOption> options,
        IReadOnlySet<string> visibleExtensionEntries)
    {
        if (options.Count == 0 || visibleExtensionEntries.Count == 0)
            return [];

        var visibleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in visibleExtensionEntries)
        {
            var extension = Path.GetExtension(entry);
            if (!string.IsNullOrWhiteSpace(extension))
                visibleNames.Add(extension);
        }

        if (visibleNames.Count == options.Count &&
            options.All(option => visibleNames.Contains(option.Name)))
        {
            return options;
        }

        var filtered = new List<SelectionOption>(Math.Min(options.Count, visibleNames.Count));
        foreach (var option in options)
        {
            if (visibleNames.Contains(option.Name))
                filtered.Add(option);
        }

        return filtered;
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

			// Controllers are activated optimistically on the first pass so their candidate
			// rules can measure real impact. A zero-impact controller was never presented to
			// the user and must not become a "known unchecked" option after Ignore All is
			// toggled. Explicit/profile states are already present in the cache and survive.
			if (id is IgnoreOptionId.UseGitIgnore or IgnoreOptionId.SmartIgnore)
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
        var canDiscoverNewRootLevelToggle = CanDiscoverNewRootLevelDirectoryToggle(context);

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

    private static bool CanDiscoverNewRootLevelDirectoryToggle(SelectionRefreshContext context)
    {
        if (context.AllRootFoldersChecked)
            return HasCompleteSelectionStateForNewRootLevelToggles(context);

        if (!context.RootSelectionInitialized)
            return false;

        // With a manual root subset, probing every root-level directory leaks counts from
        // unchecked roots into the Ignore section. Only complete root state can distinguish
        // between "unchecked before" and "new since last refresh, checked by default".
        return context.RootOptionStateCache is not null &&
               HasCompleteSelectionStateForNewRootLevelToggles(context);
    }

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
        IReadOnlyList<SelectionOption> VisibleExtensionOptions,
        IReadOnlyList<ResolvedIgnoreOptionState> IgnoreOptions,
        int ExtensionlessEntriesCount,
        bool HasIgnoreOptionCounts,
        IgnoreOptionCounts IgnoreOptionCounts,
        IgnoreControllerImpactCounts ControllerImpactCounts,
        IReadOnlyDictionary<IgnoreOptionId, bool> IgnoreOptionStateCache,
        IReadOnlySet<IgnoreOptionId> SelectedIgnoreOptions,
        IgnoreSectionSnapshotState SnapshotState,
        bool RootAccessDenied,
        bool HadAccessDenied,
        ProjectTreeInventorySnapshot? TreeInventory,
        IgnoreRules EffectiveRules,
        ProjectWorkspaceScanSnapshot WorkspaceScan);

    private sealed record IgnoreOptionResolutionResult(
        IReadOnlyList<ResolvedIgnoreOptionState> VisibleOptions,
        IReadOnlyDictionary<IgnoreOptionId, bool> IgnoreOptionStateCache,
        IReadOnlySet<IgnoreOptionId> SelectedIgnoreOptions);

    private sealed record IgnoreRulesBuildCacheEntry(string Key, IgnoreRules Rules);
}
