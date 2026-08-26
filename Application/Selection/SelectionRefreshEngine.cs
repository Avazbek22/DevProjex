using DevProjex.Application.Models;
using DevProjex.Application.Diagnostics;
using System.Runtime.InteropServices;

namespace DevProjex.Application.Selection;

public sealed class SelectionRefreshEngine(
    ScanOptionsUseCase scanOptions,
    FilterOptionSelectionService filterSelectionService,
    IgnoreOptionsService ignoreOptionsService,
    Func<string, IReadOnlyCollection<IgnoreOptionId>, IReadOnlyCollection<string>?, IgnoreRules> buildIgnoreRules,
    Func<string, IReadOnlyCollection<string>, IgnoreOptionsAvailability> getIgnoreOptionsAvailability,
    Func<string, IReadOnlyCollection<IgnoreOptionId>, IReadOnlyCollection<string>?, CancellationToken, IgnoreRules>?
        buildIgnoreRulesWithCancellation = null,
    Func<string, IReadOnlyCollection<string>, CancellationToken, IgnoreOptionsAvailability>?
        getIgnoreOptionsAvailabilityWithCancellation = null)
{
    // Some dynamic chains need more than one follow-up pass:
    // controller -> directory toggle -> empty/extensionless toggle -> final scan scope.
    private const int MaximumDynamicSnapshotPasses = 6;
    private static readonly HashSet<string> EmptyScanRoots = new(PathComparer.Default);
    private static readonly HashSet<string> EmptyExtensionSelection = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<IgnoreOptionId> EmptyIgnoreSelection = [];
    private static readonly IgnoreSectionSnapshotState EmptySnapshotState = new(
        HasIgnoreOptionCounts: false,
        IgnoreOptionCounts: IgnoreOptionCounts.Empty,
        ControllerImpactCounts: IgnoreControllerImpactCounts.Empty,
        HasExtensionlessEntries: false,
        ExtensionlessEntriesCount: 0);
    private readonly IgnoreRulesBuildCache _ignoreRulesBuildCache = new(
        buildIgnoreRulesWithCancellation ??
        ((path, options, roots, _) => buildIgnoreRules(path, options, roots)));

	public void InvalidateCaches()
	{
		_ignoreRulesBuildCache.Invalidate();
	}

    public SelectionRefreshSnapshot ComputeFullRefreshSnapshot(
        SelectionRefreshContext context,
        CancellationToken cancellationToken)
    {
        IgnorePipelineDiagnostics.RecordFullSelectionRefresh();
        cancellationToken.ThrowIfCancellationRequested();

        var warmIgnore = BuildIgnoreOptionState(
            context.Path,
            EmptyScanRoots,
            context,
            context.CurrentSnapshotState,
            cancellationToken: cancellationToken);
        var initialSelectedIgnoreOptions = BuildInitialFullRefreshIgnoreSelection(
            context,
            warmIgnore.SelectedIgnoreOptions);

        var scanRootSection = BuildScanRootSection(
            context,
            initialSelectedIgnoreOptions,
            cancellationToken);

        var dynamicSection = BuildDynamicSection(
            context,
            scanRootSection.RootFolders,
            initialSelectedIgnoreOptions,
            warmIgnore.IgnoreOptionStateCache,
            context.CurrentSnapshotState,
            cancellationToken);

        return new SelectionRefreshSnapshot(
            RootOptions: dynamicSection.RootOptions ?? scanRootSection.RootOptions,
            ExtensionOptions: dynamicSection.ExtensionOptions,
            IgnoreOptions: dynamicSection.IgnoreOptions,
            ExtensionlessEntriesCount: dynamicSection.ExtensionlessEntriesCount,
            HasIgnoreOptionCounts: dynamicSection.HasIgnoreOptionCounts,
            IgnoreOptionCounts: dynamicSection.IgnoreOptionCounts,
            ControllerImpactCounts: dynamicSection.ControllerImpactCounts,
            IgnoreOptionStateCache: dynamicSection.IgnoreOptionStateCache,
            RootAccessDenied: scanRootSection.RootAccessDenied || dynamicSection.RootAccessDenied,
            HadAccessDenied: scanRootSection.HadAccessDenied || dynamicSection.HadAccessDenied,
            HadScanFailure: scanRootSection.HadScanFailure || dynamicSection.HadScanFailure,
            TreeInventory: dynamicSection.TreeInventory,
            VisibleExtensionOptions: dynamicSection.VisibleExtensionOptions,
            GitEvidence: dynamicSection.SnapshotState.GitEvidence,
            SelectedIgnoreOptions: dynamicSection.SelectedIgnoreOptions,
            EffectiveRules: dynamicSection.EffectiveRules);
    }

    public SelectionRefreshSnapshot ComputeLiveRefreshSnapshot(
        SelectionRefreshContext context,
        CancellationToken cancellationToken)
    {
        IgnorePipelineDiagnostics.RecordLiveSelectionRefresh();
        cancellationToken.ThrowIfCancellationRequested();

        var selectedIgnoreOptions = BuildInitialLiveRefreshIgnoreSelection(context);
        var scanRootSection = BuildKnownScanRootSection(context) ??
                              BuildScanRootSection(context, selectedIgnoreOptions, cancellationToken);
        var dynamicSection = BuildDynamicSection(
            context,
            scanRootSection.RootFolders,
            selectedIgnoreOptions,
            context.IgnoreOptionStateCache,
            context.CurrentSnapshotState,
            cancellationToken);

        return new SelectionRefreshSnapshot(
            RootOptions: dynamicSection.RootOptions ?? scanRootSection.RootOptions,
            ExtensionOptions: dynamicSection.ExtensionOptions,
            IgnoreOptions: dynamicSection.IgnoreOptions,
            ExtensionlessEntriesCount: dynamicSection.ExtensionlessEntriesCount,
            HasIgnoreOptionCounts: dynamicSection.HasIgnoreOptionCounts,
            IgnoreOptionCounts: dynamicSection.IgnoreOptionCounts,
            ControllerImpactCounts: dynamicSection.ControllerImpactCounts,
            IgnoreOptionStateCache: dynamicSection.IgnoreOptionStateCache,
            RootAccessDenied: scanRootSection.RootAccessDenied || dynamicSection.RootAccessDenied,
            HadAccessDenied: scanRootSection.HadAccessDenied || dynamicSection.HadAccessDenied,
            HadScanFailure: scanRootSection.HadScanFailure || dynamicSection.HadScanFailure,
            TreeInventory: dynamicSection.TreeInventory,
            VisibleExtensionOptions: dynamicSection.VisibleExtensionOptions,
            GitEvidence: dynamicSection.SnapshotState.GitEvidence,
            SelectedIgnoreOptions: dynamicSection.SelectedIgnoreOptions,
            EffectiveRules: dynamicSection.EffectiveRules);
    }

    private ScanRootSectionSnapshot BuildScanRootSection(
        SelectionRefreshContext context,
        IReadOnlySet<IgnoreOptionId> selectedIgnoreOptions,
        CancellationToken cancellationToken)
    {
        var discoveryRules = BuildIgnoreRules(context.Path, selectedIgnoreOptions, null, cancellationToken);
        var scan = scanOptions.GetRootFolders(context.Path, discoveryRules, cancellationToken);
        var ignoreRules = BuildIgnoreRules(context.Path, selectedIgnoreOptions, scan.Value, cancellationToken);
        var visibleRootFolders = RootFolderVisibilityProjection.ApplyScopedControllerRules(
            context.Path,
            scan.Value,
            ignoreRules,
            cancellationToken);

        var previousSelections = context.RootSelectionInitialized
            ? new HashSet<string>(context.RootSelectionCache, PathComparer.Default)
            : EmptyScanRoots;
        var options = filterSelectionService.BuildRootFolderOptions(
            visibleRootFolders,
            previousSelections,
            ignoreRules,
            context.RootSelectionInitialized,
            context.RootOptionStateCache);
        if (!context.RootSelectionIsExplicit)
        {
            options = SelectionRefreshPolicy.ApplyLegacyCliRootFallback(
                context.PreparedSelectionMode,
                context.RootSelectionCache,
                options,
                visibleRootFolders,
                ignoreRules,
                filterSelectionService,
                EmptyScanRoots);
        }

        if (!ShouldSuppressAllTogglesOverride(context) && context.AllRootFoldersChecked)
            options = ForceAllChecked(options);

        return new ScanRootSectionSnapshot(
            options,
            CollectCheckedSelectionNames(options, PathComparer.Default),
            scan.RootAccessDenied,
            scan.HadAccessDenied,
            scan.HadScanFailure);
    }

    private static ScanRootSectionSnapshot? BuildKnownScanRootSection(SelectionRefreshContext context)
    {
        if (context.CurrentRootOptions is null)
            return null;

        return new ScanRootSectionSnapshot(
            context.CurrentRootOptions,
            CollectCheckedSelectionNames(context.CurrentRootOptions, PathComparer.Default),
            RootAccessDenied: false,
            HadAccessDenied: false,
            HadScanFailure: false);
    }

    private DynamicSectionSnapshot BuildDynamicSection(
        SelectionRefreshContext context,
        IReadOnlyCollection<string> scanRoots,
        IReadOnlySet<IgnoreOptionId> selectedIgnoreOptions,
        IReadOnlyDictionary<IgnoreOptionId, bool> ignoreStateCache,
        IgnoreSectionSnapshotState beforeSnapshot,
        CancellationToken cancellationToken)
    {
        var currentRoots = scanRoots;
        var currentSelectedIgnoreOptions = selectedIgnoreOptions;
        var currentIgnoreStateCache = ignoreStateCache;
        var previousSnapshot = beforeSnapshot;
        var previousRuntimeSnapshot = EmptySnapshotState;
        IReadOnlyList<SelectionOption>? currentRootOptions = null;
        var rootAccessDenied = false;
        var hadAccessDenied = false;
        var hadScanFailure = false;
        var visitedStates = new List<DynamicConvergenceState>(MaximumDynamicSnapshotPasses);

        // Dynamic ignore availability can feed back into the selected ignore set, especially
        // when profile fallback revives default-checked options that immediately change the
        // tree shape. A bounded convergence loop keeps the load path deterministic without
        // requiring the user to trigger another refresh manually.
        for (var passIndex = 0; passIndex < MaximumDynamicSnapshotPasses; passIndex++)
        {
            IgnorePipelineDiagnostics.RecordDynamicSelectionPass();
            var snapshot = BuildSingleDynamicSnapshot(
                context,
                currentRoots,
                currentSelectedIgnoreOptions,
                currentIgnoreStateCache,
                previousRuntimeSnapshot,
                cancellationToken);

            rootAccessDenied |= snapshot.RootAccessDenied;
            hadAccessDenied |= snapshot.HadAccessDenied;
            hadScanFailure |= snapshot.HadScanFailure;
			if (hadScanFailure)
			{
				return snapshot with
				{
					RootOptions = currentRootOptions,
					RootAccessDenied = rootAccessDenied,
					HadAccessDenied = hadAccessDenied,
					HadScanFailure = true
				};
			}
            var refreshPlan = IgnoreSectionRefreshPlanBuilder.Build(
                previousSnapshot,
                snapshot.SnapshotState,
                BuildMeasuredSelectionForRefreshPlanning(
                    currentSelectedIgnoreOptions,
                    snapshot.SelectedIgnoreOptions,
                    snapshot.SnapshotState),
                snapshot.SelectedIgnoreOptions);
            if (!refreshPlan.RequiresSecondSnapshotPass)
            {
                return snapshot with
                {
                    RootOptions = currentRootOptions,
                    RootAccessDenied = rootAccessDenied,
                    HadAccessDenied = hadAccessDenied,
                    HadScanFailure = hadScanFailure
                };
            }

            if (refreshPlan.RequiresScanRootRefresh)
            {
                var rebuiltRootSection = BuildScanRootSection(
                    context,
                    snapshot.SelectedIgnoreOptions,
                    cancellationToken);
                currentRoots = rebuiltRootSection.RootFolders;
                currentRootOptions = rebuiltRootSection.RootOptions;
                rootAccessDenied |= rebuiltRootSection.RootAccessDenied;
                hadAccessDenied |= rebuiltRootSection.HadAccessDenied;
                hadScanFailure |= rebuiltRootSection.HadScanFailure;
            }

            currentSelectedIgnoreOptions = snapshot.SelectedIgnoreOptions;
            currentIgnoreStateCache = snapshot.IgnoreOptionStateCache;
            previousSnapshot = snapshot.SnapshotState;
            previousRuntimeSnapshot = snapshot.SnapshotState;
            var nextState = DynamicConvergenceState.Capture(
                currentRoots,
                currentSelectedIgnoreOptions,
                previousSnapshot);
            if (visitedStates.Any(state => state.IsEquivalentTo(nextState)))
            {
                throw new SelectionRefreshConvergenceException(
                    SelectionRefreshConvergenceFailure.CycleDetected,
                    passIndex + 1);
            }
            visitedStates.Add(nextState);

            if (passIndex == MaximumDynamicSnapshotPasses - 1)
            {
                // Never publish a snapshot assembled from different convergence passes.
                // The coordinator keeps the previous stable state and reports the failure.
                throw new SelectionRefreshConvergenceException(
                    SelectionRefreshConvergenceFailure.PassLimitExceeded,
                    MaximumDynamicSnapshotPasses);
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
        CancellationToken cancellationToken)
    {
        var ignoreRules = BuildIgnoreRules(context.Path, selectedIgnoreOptions, selectedRoots, cancellationToken);
        var extensionScanRules = IgnoreRulesProjection.ForExtensionAvailability(ignoreRules);
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
            cancellationToken);
        var scanData = scan.Value.IgnoreSection;
        var rootAccessDenied = scan.RootAccessDenied;
        var hadAccessDenied = scan.HadAccessDenied;
        var hadScanFailure = scan.HadScanFailure;

        var snapshotState = CreateSnapshotState(scanData);
        snapshotState = PreserveActiveRuntimeSnapshotState(
            snapshotState,
            previousRuntimeSnapshotState,
            selectedIgnoreOptions);

        var visibleExtensions = new List<string>(scanData.Extensions.Count);
        var extensionlessEntriesCount =
            ExtensionOptionProjection.SplitAvailableEntries(scanData.Extensions, visibleExtensions);
        extensionlessEntriesCount = Math.Max(
            extensionlessEntriesCount,
            snapshotState.IgnoreOptionCounts.ExtensionlessFiles);

        var extensionOptions = filterSelectionService.BuildExtensionOptions(
            visibleExtensions,
            context.ExtensionsSelectionInitialized
                ? new HashSet<string>(context.ExtensionsSelectionCache, StringComparer.OrdinalIgnoreCase)
                : EmptyExtensionSelection,
            context.ExtensionOptionStateCache);
		extensionOptions = ApplyExplicitExtensionSelection(context, extensionOptions);
		var usedProfileFallback = !context.ExtensionSelectionIsExplicit &&
			SelectionRefreshPolicy.ShouldApplyMissingProfileSelectionsFallback(
            context.PreparedSelectionMode,
            context.ExtensionsSelectionCache,
            extensionOptions);
		if (!context.ExtensionSelectionIsExplicit)
		{
			extensionOptions = SelectionRefreshPolicy.ApplyMissingProfileSelectionsFallbackToExtensions(
				context.PreparedSelectionMode,
				context.ExtensionsSelectionCache,
				extensionOptions);
		}

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
                ExtensionOptionProjection.BuildResolvedPolicy(extensionOptions),
                cancellationToken);
            scanData = scan.Value.IgnoreSection;
            rootAccessDenied |= scan.RootAccessDenied;
            hadAccessDenied |= scan.HadAccessDenied;
            hadScanFailure |= scan.HadScanFailure;

            snapshotState = CreateSnapshotState(scanData);
            snapshotState = PreserveActiveRuntimeSnapshotState(
                snapshotState,
                previousRuntimeSnapshotState,
                selectedIgnoreOptions);

            visibleExtensions = new List<string>(scanData.Extensions.Count);
            extensionlessEntriesCount =
                ExtensionOptionProjection.SplitAvailableEntries(scanData.Extensions, visibleExtensions);
            extensionlessEntriesCount = Math.Max(
                extensionlessEntriesCount,
                snapshotState.IgnoreOptionCounts.ExtensionlessFiles);

            extensionOptions = filterSelectionService.BuildExtensionOptions(
                visibleExtensions,
                context.ExtensionsSelectionInitialized
                    ? new HashSet<string>(context.ExtensionsSelectionCache, StringComparer.OrdinalIgnoreCase)
                    : EmptyExtensionSelection,
                context.ExtensionOptionStateCache);
			extensionOptions = ApplyExplicitExtensionSelection(context, extensionOptions);
			if (!context.ExtensionSelectionIsExplicit)
			{
				extensionOptions = SelectionRefreshPolicy.ApplyMissingProfileSelectionsFallbackToExtensions(
					context.PreparedSelectionMode,
					context.ExtensionsSelectionCache,
					extensionOptions);
			}

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
            selectedIgnoreOptions,
            cancellationToken);

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
            RootAccessDenied: rootAccessDenied,
            HadAccessDenied: hadAccessDenied,
            HadScanFailure: hadScanFailure,
            TreeInventory: scan.Value.TreeInventory,
            EffectiveRules: ignoreRules);
    }

	private static IReadOnlyList<SelectionOption> ApplyExplicitExtensionSelection(
		SelectionRefreshContext context,
		IReadOnlyList<SelectionOption> options)
	{
		if (!context.ExtensionSelectionIsExplicit)
			return options;

		// CLI extension overrides are exact sets. Unlike a persisted settings island,
		// newly discovered rows must not opt themselves into an explicit invocation.
		var selected = context.ExtensionsSelectionCache;
		return options
			.Select(option => option with { IsChecked = selected.Contains(option.Name) })
			.ToArray();
	}

    private ScanResult<ProjectWorkspaceScanSnapshot> GetDynamicSectionScan(
        SelectionRefreshContext context,
        IReadOnlyCollection<string> selectedRoots,
        IReadOnlySet<IgnoreOptionId> selectedIgnoreOptions,
        IgnoreRules extensionScanRules,
        IgnoreRules ignoreRules,
        IExtensionInclusionPolicy? effectiveExtensionPolicy,
        CancellationToken cancellationToken)
    {
        const bool includeDirectoryToggleProbeRoots = true;
        const bool includeControllerImpactProbeRoots = true;
        return scanOptions.GetProjectWorkspaceSnapshotForRootFolders(
                context.Path,
                selectedRoots,
                extensionScanRules,
                ignoreRules,
                effectiveExtensionPolicy,
                includeDirectoryToggleProbeRoots,
                cancellationToken,
                includeControllerImpactProbeRoots,
                captureTreeInventory: context.CaptureTreeInventory);
    }

    private IgnoreOptionResolutionResult BuildIgnoreOptionState(
        string path,
        IReadOnlyCollection<string> selectedRoots,
        SelectionRefreshContext context,
        IgnoreSectionSnapshotState snapshotState,
        IReadOnlyDictionary<IgnoreOptionId, bool>? stateCacheOverride = null,
        IReadOnlySet<IgnoreOptionId>? previousSelectionOverride = null,
        CancellationToken cancellationToken = default)
    {
        var previousSelections = previousSelectionOverride ??
                                 (context.IgnoreSelectionInitialized
                                     ? new HashSet<IgnoreOptionId>(context.IgnoreSelectionCache)
                                     : EmptyIgnoreSelection);
        var stateCache = new Dictionary<IgnoreOptionId, bool>(
            stateCacheOverride ?? context.IgnoreOptionStateCache);
        var preferredGitMode = ResolvePreferredGitFilteringMode(
            context,
            previousSelections,
            stateCache);
        var availability = ResolveIgnoreOptionsAvailability(
            path,
            selectedRoots,
            snapshotState,
            stateCache,
            context.IgnoreOptionStateCacheIsComplete,
            cancellationToken);

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
		GitFilteringModeResolver.Normalize(stateCache, preferredGitMode);
		for (var index = 0; index < resolved.Count; index++)
		{
			var option = resolved[index];
			if (stateCache.TryGetValue(option.Id, out var isChecked) &&
			    option.IsChecked != isChecked)
			{
				resolved[index] = option with { IsChecked = isChecked };
			}
		}

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
            GitFilteringModeResolver.Normalize(
                selected,
                ResolvePreferredGitFilteringMode(
                    context,
                    selected,
                    context.IgnoreOptionStateCache));
            return selected;
        }

        AddDefaultDynamicIgnoreOptions(selected);
        GitFilteringModeResolver.Normalize(selected, GitFilteringMode.RespectGitIgnore);
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

        GitFilteringModeResolver.Normalize(
            selected,
            ResolvePreferredGitFilteringMode(
                context,
                selected,
                context.IgnoreOptionStateCache));
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
        IReadOnlySet<IgnoreOptionId> resolvedSelectedOptions,
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
            if (!resolvedSelectedOptions.Contains(optionId) &&
                !HasMeasuredIgnoreImpact(optionId, snapshotState))
            {
                continue;
            }

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
            IgnoreOptionId.TrackedGitFilesOnly => true,
            IgnoreOptionId.SmartIgnore => controllerImpactCounts.SmartIgnore > 0,
			IgnoreOptionId.HideSecrets => true,
			IgnoreOptionId.HidePrivateData => true,
			IgnoreOptionId.CompressCode => true,
			IgnoreOptionId.StripComments => true,
			IgnoreOptionId.StripBlankLines => true,
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
        bool stateCacheIsComplete,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
            return IgnoreOptionsAvailabilityResolver.CreateUnmeasured(
                includeGitIgnore: false,
                includeSmartIgnore: false);

        try
        {
            return IgnoreOptionsAvailabilityResolver.Resolve(
                getIgnoreOptionsAvailabilityWithCancellation is null
                    ? getIgnoreOptionsAvailability(path, selectedRootFolders)
                    : getIgnoreOptionsAvailabilityWithCancellation(
                        path,
                        selectedRootFolders,
                        cancellationToken),
                snapshotState,
                stateCache,
                stateCacheIsComplete);
        }
        catch (Exception exception) when (exception is
               IOException or
               UnauthorizedAccessException or
               System.Security.SecurityException)
        {
            return IgnoreOptionsAvailabilityResolver.CreateUnmeasured(
                includeGitIgnore: false,
                includeSmartIgnore: false);
        }
    }

    private static IgnoreSectionSnapshotState CreateSnapshotState(IgnoreSectionScanData scanData) =>
        new(
            HasIgnoreOptionCounts: true,
            IgnoreOptionCounts: scanData.EffectiveIgnoreOptionCounts,
            ControllerImpactCounts: scanData.ControllerImpactCounts,
            HasExtensionlessEntries: scanData.EffectiveIgnoreOptionCounts.ExtensionlessFiles > 0,
            ExtensionlessEntriesCount: scanData.EffectiveIgnoreOptionCounts.ExtensionlessFiles,
            GitEvidence: scanData.GitEvidence);

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

		if (!ProjectPresentationCatalog.ContentTransformationOptionIds.Contains(option.Id) &&
		    context.IgnoreAllPreference.HasValue)
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
			if (id is IgnoreOptionId.UseGitIgnore
			    or IgnoreOptionId.TrackedGitFilesOnly
			    or IgnoreOptionId.SmartIgnore)
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
        IReadOnlyCollection<string>? selectedRootFolders,
        CancellationToken cancellationToken)
    {
        return _ignoreRulesBuildCache.GetOrBuildWithCancellation(
            path,
            selectedIgnoreOptions,
            selectedRootFolders,
            cancellationToken);
    }

	private static HashSet<IgnoreOptionId> BuildSelectedIgnoreOptionSet(
		IReadOnlyDictionary<IgnoreOptionId, bool> stateCache,
		IReadOnlySet<IgnoreOptionId> visibleIds)
	{
		var selected = new HashSet<IgnoreOptionId>();
		foreach (var (id, isChecked) in stateCache)
		{
			// Hidden states are kept for profile roundtrip and transient availability churn.
			// A selected Git mode is also active policy: no pattern file means an empty
			// gitignore rule set, while unavailable tracked evidence must remain fail-closed.
			if (isChecked &&
			    (visibleIds.Contains(id) ||
			     id is IgnoreOptionId.UseGitIgnore or IgnoreOptionId.TrackedGitFilesOnly))
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

    private static bool ShouldSuppressAllTogglesOverride(SelectionRefreshContext context)
        => context.PreparedSelectionMode == PreparedSelectionMode.Profile;

    private static GitFilteringMode ResolvePreferredGitFilteringMode(
        SelectionRefreshContext context,
        IReadOnlySet<IgnoreOptionId> previousSelections,
        IReadOnlyDictionary<IgnoreOptionId, bool> stateCache)
    {
        var stateContainsBothModes =
            stateCache.TryGetValue(IgnoreOptionId.UseGitIgnore, out var useGitIgnore) &&
            useGitIgnore &&
            stateCache.TryGetValue(IgnoreOptionId.TrackedGitFilesOnly, out var trackedOnly) &&
            trackedOnly;
        var selectionContainsBothModes =
            previousSelections.Contains(IgnoreOptionId.UseGitIgnore) &&
            previousSelections.Contains(IgnoreOptionId.TrackedGitFilesOnly);
        if (context.IgnoreAllPreference == true &&
            (stateContainsBothModes || selectionContainsBothModes))
        {
            // Legacy bulk toggles set every visible option to true. Treat the Git pair as
            // one logical slot and use the regular default instead of entering strict mode.
            return GitFilteringMode.RespectGitIgnore;
        }

        var mode = GitFilteringModeResolver.Resolve(stateCache);
        if (mode != GitFilteringMode.None)
            return mode;

        mode = GitFilteringModeResolver.Resolve(previousSelections);
        if (mode != GitFilteringMode.None)
            return mode;

        return context.IgnoreAllPreference == true
            ? GitFilteringMode.RespectGitIgnore
            : GitFilteringMode.None;
    }

    private sealed record ScanRootSectionSnapshot(
        IReadOnlyList<SelectionOption> RootOptions,
        IReadOnlySet<string> RootFolders,
        bool RootAccessDenied,
        bool HadAccessDenied,
        bool HadScanFailure);

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
        bool HadScanFailure,
        ProjectTreeInventorySnapshot? TreeInventory,
        IgnoreRules EffectiveRules);

    private sealed record IgnoreOptionResolutionResult(
        IReadOnlyList<ResolvedIgnoreOptionState> VisibleOptions,
        IReadOnlyDictionary<IgnoreOptionId, bool> IgnoreOptionStateCache,
        IReadOnlySet<IgnoreOptionId> SelectedIgnoreOptions);

    private sealed record DynamicConvergenceState(
        string[] SelectedRoots,
        IgnoreOptionId[] SelectedIgnoreOptions,
        IgnoreSectionSnapshotState SnapshotState)
    {
        public static DynamicConvergenceState Capture(
            IReadOnlyCollection<string> selectedRoots,
            IReadOnlySet<IgnoreOptionId> selectedIgnoreOptions,
            IgnoreSectionSnapshotState snapshotState) =>
            new(
                selectedRoots.OrderBy(static value => value, PathComparer.Default).ToArray(),
                selectedIgnoreOptions.OrderBy(static value => (int)value).ToArray(),
                snapshotState);

        public bool IsEquivalentTo(DynamicConvergenceState other)
        {
            if (SnapshotState != other.SnapshotState ||
                SelectedRoots.Length != other.SelectedRoots.Length ||
                SelectedIgnoreOptions.Length != other.SelectedIgnoreOptions.Length)
            {
                return false;
            }

            for (var index = 0; index < SelectedRoots.Length; index++)
            {
                if (!PathComparer.Default.Equals(SelectedRoots[index], other.SelectedRoots[index]))
                    return false;
            }

            if (!SelectedIgnoreOptions.AsSpan().SequenceEqual(other.SelectedIgnoreOptions))
                return false;

            return true;
        }
    }

}
