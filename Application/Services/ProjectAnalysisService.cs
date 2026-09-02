using System.Diagnostics;
using DevProjex.Application.Selection;

namespace DevProjex.Application.Services;

public sealed class ProjectAnalysisService(
	ScanOptionsUseCase scanOptions,
	BuildTreeUseCase buildTree,
	FilterOptionSelectionService filterSelectionService,
	IgnoreOptionsService ignoreOptions,
	IgnoreRulesService ignoreRules,
	TreeExportService treeExport,
	IFileContentAnalyzer fileContentAnalyzer,
	Func<DateTimeOffset>? utcNowProvider = null)
{
	private static readonly IReadOnlySet<string> EmptyRootSelection =
		new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer);
	private static readonly IReadOnlySet<string> EmptyExtensionSelection =
		new HashSet<string>(StringComparer.OrdinalIgnoreCase);
	private static readonly IReadOnlySet<IgnoreOptionId> EmptyIgnoreSelection = new HashSet<IgnoreOptionId>();
	private static readonly IReadOnlyDictionary<IgnoreOptionId, bool> EmptyIgnoreState =
		new Dictionary<IgnoreOptionId, bool>();
	private readonly Func<DateTimeOffset> _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);

	public Task<ExportOutputMetrics> CalculateContentMetricsAsync(
		IReadOnlyList<string>? orderedFilePaths,
		Action<ContentFileMetrics>? fileMetricsObserver,
		CancellationToken cancellationToken = default) =>
		ProjectContentMetricsCalculator.CalculateAsync(
			fileContentAnalyzer,
			orderedFilePaths,
			fileMetricsObserver,
			progress: null,
			cancellationToken);

	public async Task<ProjectAnalysisReport> AnalyzeAsync(
		ProjectAnalysisRequest request,
		CancellationToken cancellationToken = default)
	{
		var loadedProject = Load(request, cancellationToken);
		return await BuildReportFromTreeAsync(loadedProject, cancellationToken).ConfigureAwait(false);
	}

	public LoadedProjectAnalysisRequest Load(
		ProjectAnalysisRequest request,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (PathUtility.IsMissingPath(request.RootPath))
			throw new ArgumentException("Root path is required.", nameof(request));

		var rootPath = PathUtility.Normalize(request.RootPath);
		if (!Directory.Exists(rootPath))
			throw new DirectoryNotFoundException($"Project path was not found: {request.RootPath}");

		var loadingStopwatch = Stopwatch.StartNew();
		if (CanUseUnifiedSelectionPipeline(request) &&
		    buildTree.SupportsCompositeInventory)
		{
			return LoadWithUnifiedSelection(
				rootPath,
				request,
				loadingStopwatch,
				cancellationToken);
		}

		var selectedRootFolders = NormalizeRootFolders(request.SelectedRootFolders);
		var selectedIgnoreOptions = ResolveSelectedIgnoreOptions(
			rootPath,
			selectedRootFolders,
			useAllRootFoldersForDefaults: request.SelectedRootFolders is null,
			request.SelectedIgnoreOptions,
			cancellationToken);
		var discoveryRules = ignoreRules.BuildWithCancellation(
			rootPath,
			selectedIgnoreOptions,
			selectedRootFolders,
			cancellationToken);
		ScanOptionsResult scan;
		ProjectTreeInventorySnapshot? treeInventory = null;
		IReadOnlyList<string> allowedRootFolders;
		IgnoreRules rules;
		if (request.SelectedRootFolders is null &&
		    buildTree.SupportsCompositeInventory)
		{
			var rootFolders = scanOptions.GetRootFolders(rootPath, discoveryRules, cancellationToken);
			var rootProjectionRules = ignoreRules.BuildWithCancellation(
				rootPath,
				selectedIgnoreOptions,
				rootFolders.Value,
				cancellationToken);
			allowedRootFolders = RootFolderVisibilityProjection.ApplyScopedControllerRules(
				rootPath,
				rootFolders.Value,
				rootProjectionRules,
				cancellationToken);
			rules = ignoreRules.BuildWithCancellation(
				rootPath,
				selectedIgnoreOptions,
				allowedRootFolders,
				cancellationToken);
			var allowedRootFolderSet = allowedRootFolders.ToHashSet(ProjectTreePathIdentity.CanonicalComparer);
			treeInventory = buildTree.ReadCompositeInventory(
				rootPath,
				allowedRootFolderSet,
				discoveryRules,
				rules,
				cancellationToken);

			var availableExtensions = new List<string>(
				ProjectTreeInventoryExtensionDiscovery.GetVisibleExtensions(
					treeInventory,
					discoveryRules,
					cancellationToken));
			CancellationAwareSort.Sort(
				availableExtensions,
				StringComparer.OrdinalIgnoreCase,
				cancellationToken);
			scan = new ScanOptionsResult(
				Extensions: availableExtensions,
				RootFolders: allowedRootFolders,
				RootAccessDenied: rootFolders.RootAccessDenied || treeInventory.RootAccessDenied,
				HadAccessDenied: rootFolders.HadAccessDenied || treeInventory.HadAccessDenied,
				HadScanFailure: rootFolders.HadScanFailure || treeInventory.HadScanFailure);
		}
		else
		{
			if (request.SelectedRootFolders is null)
			{
				scan = scanOptions.Execute(
					new ScanOptionsRequest(rootPath, discoveryRules),
					cancellationToken);
				var rootProjectionRules = ignoreRules.BuildWithCancellation(
					rootPath,
					selectedIgnoreOptions,
					scan.RootFolders,
					cancellationToken);
				allowedRootFolders = RootFolderVisibilityProjection.ApplyScopedControllerRules(
					rootPath,
					scan.RootFolders,
					rootProjectionRules,
					cancellationToken);
				scan = scan with { RootFolders = allowedRootFolders };
				rules = ignoreRules.BuildWithCancellation(
					rootPath,
					selectedIgnoreOptions,
					allowedRootFolders,
					cancellationToken);
			}
			else
			{
				var rootFolders = scanOptions.GetRootFolders(
					rootPath,
					discoveryRules,
					cancellationToken);
				allowedRootFolders = ResolveRequestedRootFolders(rootFolders.Value, selectedRootFolders);
				rules = ignoreRules.BuildWithCancellation(
					rootPath,
					selectedIgnoreOptions,
					allowedRootFolders,
					cancellationToken);
				if (buildTree.SupportsCompositeInventory)
				{
					// Explicit CLI/TUI selections do not need the interactive convergence loop.
					// Reuse one composite inventory for extension discovery and tree projection
					// so the optimized path remains exact without scanning every file twice.
					treeInventory = buildTree.ReadCompositeInventory(
						rootPath,
						allowedRootFolders.ToHashSet(ProjectTreePathIdentity.CanonicalComparer),
						discoveryRules,
						rules,
						cancellationToken);
					var extensions = ProjectTreeInventoryExtensionDiscovery.GetVisibleExtensions(
						treeInventory,
						discoveryRules,
						cancellationToken);
					scan = new ScanOptionsResult(
						Extensions: extensions
							.OrderBy(static extension => extension, StringComparer.OrdinalIgnoreCase)
							.ToArray(),
						RootFolders: rootFolders.Value,
						RootAccessDenied: rootFolders.RootAccessDenied || treeInventory.RootAccessDenied,
						HadAccessDenied: rootFolders.HadAccessDenied || treeInventory.HadAccessDenied,
						HadScanFailure: rootFolders.HadScanFailure || treeInventory.HadScanFailure);
				}
				else
				{
					var extensions = scanOptions.GetExtensionsForRootFolders(
						rootPath,
						allowedRootFolders,
						discoveryRules,
						cancellationToken);
					scan = new ScanOptionsResult(
						Extensions: extensions.Value
							.OrderBy(static extension => extension, StringComparer.OrdinalIgnoreCase)
							.ToArray(),
						RootFolders: rootFolders.Value,
						RootAccessDenied: rootFolders.RootAccessDenied || extensions.RootAccessDenied,
						HadAccessDenied: rootFolders.HadAccessDenied || extensions.HadAccessDenied,
						HadScanFailure: rootFolders.HadScanFailure || extensions.HadScanFailure);
				}
			}
		}

		var allowedExtensions = request.SelectedExtensions is null
			? scan.Extensions.ToArray()
			: NormalizeExtensions(request.SelectedExtensions).ToArray();

		var treeRequest = new BuildTreeRequest(
			rootPath,
			new TreeFilterOptions(
				AllowedExtensions: allowedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase),
				AllowedRootFolders: allowedRootFolders.ToHashSet(ProjectTreePathIdentity.CanonicalComparer),
				IgnoreRules: rules));
		BuildTreeSnapshotResult treeResult;
		if (treeInventory is not null)
		{
			treeResult = buildTree.ExecuteWithInventory(treeRequest, treeInventory, cancellationToken);
		}
		else if (selectedIgnoreOptions.Contains(IgnoreOptionId.TrackedGitFilesOnly))
		{
			// Tracked mode needs inventory evidence to distinguish a valid empty index
			// from an unreadable repository. Other explicit selections retain the
			// bounded direct-build path and avoid an unnecessary inventory projection.
			treeResult = buildTree.ExecuteWithInventory(treeRequest, cancellationToken);
			treeInventory = treeResult.Inventory;
		}
		else
		{
			treeResult = new BuildTreeSnapshotResult(
				buildTree.Execute(treeRequest, cancellationToken),
				Inventory: null);
		}
		if (request.SelectedRootFolders is null)
		{
			allowedRootFolders = GetVisibleRootFolderNames(treeResult.Tree.Root);
			scan = scan with { RootFolders = allowedRootFolders };
		}
		loadingStopwatch.Stop();

		return new LoadedProjectAnalysisRequest(
			RootPath: rootPath,
			Tree: treeResult.Tree,
			AvailableRootFolders: scan.RootFolders,
			AvailableExtensions: scan.Extensions,
			SelectedRootFolders: allowedRootFolders,
			SelectedExtensions: allowedExtensions,
			SelectedIgnoreOptions: selectedIgnoreOptions,
			RootAccessDenied: scan.RootAccessDenied || treeResult.Tree.RootAccessDenied,
			HadAccessDenied: scan.HadAccessDenied || treeResult.Tree.HadAccessDenied,
			KnownLoadingElapsed: loadingStopwatch.Elapsed,
			DiscoveredGitTrackedIndexCount: treeInventory?.DiscoveredGitTrackedPathIndexes.Count ?? 0,
			UnavailableGitTrackedIndexCount: treeInventory?.DiscoveredGitTrackedPathIndexes.Count(
				static index => !index.IsAvailable) ?? 0,
			TreeInventory: treeInventory,
			GitEvidence: treeInventory?.GitEvidence ?? scan.GitEvidence)
		{
			RequestedRootFoldersForDiagnostics = request.SelectedRootFolders is null
				? null
				: selectedRootFolders,
			EffectiveRules = rules,
			RootSelectionIsExplicit = request.SelectedRootFolders is not null,
			EffectiveExtensionPolicy = request.SelectedExtensions is null
				? null
				: new ExtensionSetInclusionPolicy(
					allowedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase))
		};
	}

	private static bool CanUseUnifiedSelectionPipeline(ProjectAnalysisRequest request) =>
		request.LocalProfileState is not null ||
		request.CaptureIgnoreImpactCounts ||
		(request.SelectedRootFolders is null &&
		 request.SelectedExtensions is null &&
		 request.SelectedIgnoreOptions is null);

	private LoadedProjectAnalysisRequest LoadWithUnifiedSelection(
		string rootPath,
		ProjectAnalysisRequest request,
		Stopwatch loadingStopwatch,
		CancellationToken cancellationToken)
	{
		var selectionRefreshEngine = new SelectionRefreshEngine(
			scanOptions,
			filterSelectionService,
			ignoreOptions,
			(candidateRootPath, selectedOptions, selectedRootFolders) =>
				ignoreRules.Build(candidateRootPath, selectedOptions, selectedRootFolders),
			(candidateRootPath, selectedRootFolders) =>
				ignoreRules.GetIgnoreOptionsAvailability(candidateRootPath, selectedRootFolders),
			buildIgnoreRulesWithCancellation:
				(candidateRootPath, selectedOptions, selectedRootFolders, token) =>
					ignoreRules.BuildWithCancellation(
						candidateRootPath,
						selectedOptions,
						selectedRootFolders,
						token),
			getIgnoreOptionsAvailabilityWithCancellation:
				(candidateRootPath, selectedRootFolders, token) =>
					ignoreRules.GetIgnoreOptionsAvailabilityWithCancellation(
						candidateRootPath,
						selectedRootFolders,
						token));
		var selectionContext = BuildUnifiedSelectionContext(rootPath, request);
		var snapshot = selectionRefreshEngine.ComputeFullRefreshSnapshot(
			selectionContext,
			cancellationToken);
		var inventory = snapshot.TreeInventory ??
		                throw new InvalidOperationException(
			                "The unified project selection scan did not produce a tree inventory.");
		var rootOptions = snapshot.RootOptions ?? [];
		var selectedRootFolders = rootOptions
			.Where(static option => option.IsChecked)
			.Select(static option => option.Name)
			.ToArray();
		var extensionOptions = snapshot.EffectiveExtensionOptions;
		var selectedExtensions = extensionOptions
			.Where(static option => option.IsChecked)
			.Select(static option => option.Name)
			.ToArray();
		var selectedIgnoreOptions = snapshot.EffectiveIgnoreOptions.ToArray();
		var rules = snapshot.EffectiveRules ??
		            ignoreRules.BuildWithCancellation(
			            rootPath,
			            selectedIgnoreOptions,
			            selectedRootFolders,
			            cancellationToken);
		var treeRequest = new BuildTreeRequest(
			rootPath,
			new TreeFilterOptions(
				AllowedExtensions: selectedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase),
				AllowedRootFolders: selectedRootFolders.ToHashSet(ProjectTreePathIdentity.CanonicalComparer),
				IgnoreRules: rules));
		var treeResult = buildTree.ExecuteWithInventory(
			treeRequest,
			inventory,
			cancellationToken).Tree;
		loadingStopwatch.Stop();

		return new LoadedProjectAnalysisRequest(
			RootPath: rootPath,
			Tree: treeResult,
			AvailableRootFolders: rootOptions.Select(static option => option.Name).ToArray(),
			AvailableExtensions: extensionOptions.Select(static option => option.Name).ToArray(),
			SelectedRootFolders: selectedRootFolders,
			SelectedExtensions: selectedExtensions,
			SelectedIgnoreOptions: selectedIgnoreOptions,
			RootAccessDenied: snapshot.RootAccessDenied || treeResult.RootAccessDenied,
			HadAccessDenied: snapshot.HadAccessDenied || treeResult.HadAccessDenied,
			KnownLoadingElapsed: loadingStopwatch.Elapsed,
			DiscoveredGitTrackedIndexCount: inventory.DiscoveredGitTrackedPathIndexes.Count,
			UnavailableGitTrackedIndexCount: inventory.DiscoveredGitTrackedPathIndexes.Count(
				static index => !index.IsAvailable),
			TreeInventory: inventory,
			GitEvidence: snapshot.GitEvidence)
		{
			// The refresh snapshot intentionally contains only effective rows. Keep the
			// requested selection separately so stale profile/CLI values still produce the
			// same actionable diagnostics without leaking into the tree filter or output plan.
			RequestedRootFoldersForDiagnostics = selectionContext.RootSelectionInitialized
				? selectionContext.RootSelectionCache
				: selectedRootFolders,
			RequestedExtensionsForDiagnostics = selectionContext.ExtensionsSelectionInitialized
				? selectionContext.ExtensionsSelectionCache
				: selectedExtensions,
			ResolvedIgnoreOptionStates = snapshot.IgnoreOptionStateCache,
			EffectiveRules = rules,
			RootSelectionIsExplicit = selectionContext.RootSelectionIsExplicit,
			EffectiveExtensionPolicy = ExtensionInclusionPolicyFactory.Create(selectionContext),
			HasIgnoreOptionCounts = snapshot.HasIgnoreOptionCounts,
			IgnoreOptionCounts = snapshot.IgnoreOptionCounts,
			IgnoreControllerImpactCounts = snapshot.ControllerImpactCounts
		};
	}

	private static SelectionRefreshContext BuildUnifiedSelectionContext(
		string rootPath,
		ProjectAnalysisRequest request)
	{
		if (request.LocalProfileState is not { } localState)
		{
			var rootsAreExplicit = request.SelectedRootFolders is not null;
			var hasKnownExtensionStates = request.KnownExtensionStates is not null;
			var extensionsAreExplicit = request.SelectedExtensions is not null &&
			                            !hasKnownExtensionStates;
			var ignoreOptionsAreExplicit = request.SelectedIgnoreOptions is not null;
			var requestedIgnoreOptions = request.SelectedIgnoreOptions ?? EmptyIgnoreSelection;
			return new SelectionRefreshContext(
				Path: rootPath,
				PreparedSelectionMode: PreparedSelectionMode.Defaults,
				AllRootFoldersChecked: !rootsAreExplicit,
				AllExtensionsChecked: !extensionsAreExplicit &&
				                      (request.KnownExtensionStates is null ||
				                       !request.KnownExtensionStates.Values.Contains(false)),
				RootSelectionInitialized: rootsAreExplicit,
				RootSelectionCache: request.SelectedRootFolders?
					.ToHashSet(ProjectTreePathIdentity.CanonicalComparer) ??
				                    EmptyRootSelection,
				ExtensionsSelectionInitialized:
					request.SelectedExtensions is not null || hasKnownExtensionStates,
				ExtensionsSelectionCache: request.SelectedExtensions?
					.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? EmptyExtensionSelection,
				IgnoreSelectionInitialized: ignoreOptionsAreExplicit,
				IgnoreSelectionCache: requestedIgnoreOptions.ToHashSet(),
				IgnoreOptionStateCache: ignoreOptionsAreExplicit
					? BuildExplicitIgnoreState(requestedIgnoreOptions)
					: EmptyIgnoreState,
				IgnoreAllPreference: null,
				CurrentSnapshotState: default,
				ExtensionOptionStateCache: request.KnownExtensionStates,
				IgnoreOptionStateCacheIsComplete: ignoreOptionsAreExplicit,
				CaptureTreeInventory: true,
				RootSelectionIsExplicit: rootsAreExplicit,
				ExtensionSelectionIsExplicit: extensionsAreExplicit);
		}

		var profile = localState.Profile;
		var selectedRoots = request.SelectedRootFolders ?? profile.SelectedRootFolders;
		var selectedExtensions = request.SelectedExtensions ?? profile.SelectedExtensions;
		var selectedIgnoreOptions = request.SelectedIgnoreOptions ?? profile.SelectedIgnoreOptions;
		var ignoreState = localState.IgnoreOptionsOverridden
			? BuildExplicitIgnoreState(selectedIgnoreOptions)
			: profile.IgnoreOptionStates ?? EmptyIgnoreState;

		// The three settings islands share one snapshot contract. Complete modern maps
		// preserve every known checkbox and let genuinely new rows use current defaults;
		// explicit CLI overrides remain exact and never mutate the persisted profile.
		return new SelectionRefreshContext(
			Path: rootPath,
			PreparedSelectionMode: PreparedSelectionMode.Profile,
			AllRootFoldersChecked: false,
			AllExtensionsChecked: false,
			RootSelectionInitialized: true,
			RootSelectionCache: selectedRoots.ToHashSet(ProjectTreePathIdentity.CanonicalComparer),
			ExtensionsSelectionInitialized: true,
			ExtensionsSelectionCache: selectedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase),
			IgnoreSelectionInitialized: true,
			IgnoreSelectionCache: selectedIgnoreOptions.ToHashSet(),
			IgnoreOptionStateCache: ignoreState,
			IgnoreAllPreference: null,
			CurrentSnapshotState: default,
			RootOptionStateCache: localState.RootsOverridden ? null : profile.RootFolderStates,
			ExtensionOptionStateCache: request.KnownExtensionStates ??
			                           (localState.ExtensionsOverridden ? null : profile.ExtensionStates),
			IgnoreOptionStateCacheIsComplete:
				localState.IgnoreOptionsOverridden || profile.IgnoreOptionStates is not null,
			CaptureTreeInventory: true,
			RootSelectionIsExplicit: localState.RootsOverridden,
			ExtensionSelectionIsExplicit: localState.ExtensionsOverridden);
	}

	private static IReadOnlyDictionary<IgnoreOptionId, bool> BuildExplicitIgnoreState(
		IReadOnlyCollection<IgnoreOptionId> selectedOptions)
	{
		var selected = selectedOptions.ToHashSet();
		return Enum.GetValues<IgnoreOptionId>()
			.ToDictionary(static option => option, selected.Contains);
	}

	private static IReadOnlyList<string> GetVisibleRootFolderNames(TreeNodeDescriptor root)
	{
		var names = new List<string>();
		foreach (var child in root.Children)
		{
			if (child.IsDirectory)
				names.Add(child.DisplayName);
		}

		return names;
	}

	public Task<ProjectAnalysisReport> BuildReportFromTreeAsync(
		LoadedProjectAnalysisRequest request,
		CancellationToken cancellationToken = default)
		=> BuildReportFromTreeAsync(
			request,
			includeOutputMetrics: true,
			cancellationToken);

	internal Task<ProjectAnalysisReport> BuildReportFromTreeAsync(
		LoadedProjectAnalysisRequest request,
		bool includeOutputMetrics,
		CancellationToken cancellationToken) =>
		BuildReportFromTreeAsync(
			request,
			includeTreeOutputMetrics: includeOutputMetrics,
			includeContentOutputMetrics: includeOutputMetrics,
			cancellationToken);

	internal async Task<ProjectAnalysisReport> BuildReportFromTreeAsync(
		LoadedProjectAnalysisRequest request,
		bool includeTreeOutputMetrics,
		bool includeContentOutputMetrics,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var analysisStopwatch = Stopwatch.StartNew();
		var treeMetrics = includeTreeOutputMetrics
			? treeExport.CalculateFullTreeMetricsWithCancellation(
				request.RootPath,
				request.Tree.Root,
				TreeTextFormat.Ascii,
				displayRootPath: null,
				displayRootName: null,
				cancellationToken)
			: ExportOutputMetrics.Empty;
		var contentMetrics = includeContentOutputMetrics
			? await CalculateContentMetricsAsync(request.Tree.OrderedFilePaths, cancellationToken)
				.ConfigureAwait(false)
			: ExportOutputMetrics.Empty;
		cancellationToken.ThrowIfCancellationRequested();
		analysisStopwatch.Stop();

		var loadingElapsed = request.KnownLoadingElapsed ?? TimeSpan.Zero;
		var analysisElapsed = analysisStopwatch.Elapsed;
		var totalElapsed = loadingElapsed + analysisElapsed;
		var treeSummary = CountTree(request.Tree.Root, cancellationToken);

		return new ProjectAnalysisReport(
			SchemaVersion: ProjectAnalysisReport.CurrentSchemaVersion,
			GeneratedUtc: _utcNowProvider(),
			RootPath: request.RootPath,
			Selection: new ProjectAnalysisSelectionReport(
				SelectedRootFolders: NormalizeRootFolders(request.SelectedRootFolders).ToArray(),
				SelectedExtensions: NormalizeResolvedExtensionNames(request.SelectedExtensions).ToArray(),
				SelectedIgnoreOptions: request.SelectedIgnoreOptions.OrderBy(static option => (int)option).ToArray()),
			Inventory: new ProjectAnalysisInventoryReport(
				AvailableRootFolders: request.AvailableRootFolders
					.OrderBy(static value => value, ProjectTreePathIdentity.CanonicalComparer)
					.ToArray(),
				AvailableExtensions: request.AvailableExtensions.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
				Tree: treeSummary),
			Metrics: new ProjectAnalysisOutputMetricsReport(
				Tree: ToReportMetrics(treeMetrics),
				Content: ToReportMetrics(contentMetrics)),
			Timing: new ProjectAnalysisTimingReport(
				LoadingMilliseconds: ToMilliseconds(loadingElapsed),
				AnalysisMilliseconds: ToMilliseconds(analysisElapsed),
				TotalMilliseconds: ToMilliseconds(totalElapsed)),
			Diagnostics: BuildDiagnostics(request, cancellationToken));
	}

	public static ProjectAnalysisDiagnosticsReport BuildDiagnostics(LoadedProjectAnalysisRequest request) =>
		BuildDiagnostics(request, CancellationToken.None);

	private static ProjectAnalysisDiagnosticsReport BuildDiagnostics(
		LoadedProjectAnalysisRequest request,
		CancellationToken cancellationToken) =>
		new(
			RootAccessDenied: request.RootAccessDenied,
			HadAccessDenied: request.HadAccessDenied,
			Warnings: BuildWarnings(request, cancellationToken));

	private IReadOnlyCollection<IgnoreOptionId> ResolveSelectedIgnoreOptions(
		string rootPath,
		IReadOnlyCollection<string> selectedRootFolders,
		bool useAllRootFoldersForDefaults,
		IReadOnlyCollection<IgnoreOptionId>? overrideOptions,
		CancellationToken cancellationToken)
	{
		if (overrideOptions is not null)
			return overrideOptions.Distinct().ToArray();

		var availability = ignoreRules.GetIgnoreOptionsAvailabilityWithCancellation(
			rootPath,
			selectedRootFolders,
			cancellationToken);
		var discoveryOptions = ignoreOptions.GetOptions(availability)
			.Where(static option => option.DefaultChecked)
			.Select(static option => option.Id)
			.ToArray();
		var discoveryRules = ignoreRules.BuildWithCancellation(
			rootPath,
			discoveryOptions,
			selectedRootFolders,
			cancellationToken);
		var discoveryRootFolders = useAllRootFoldersForDefaults
			? scanOptions.GetRootFolders(rootPath, discoveryRules, cancellationToken).Value
			: selectedRootFolders;
		var extensionDiscoveryRules = IgnoreRulesProjection.ForExtensionAvailability(discoveryRules);
		var scan = scanOptions.GetIgnoreSectionSnapshotForRootFolders(
			rootPath,
			discoveryRootFolders,
			extensionDiscoveryRules,
			effectiveRules: discoveryRules,
			effectiveAllowedExtensions: (IReadOnlySet<string>?)null,
			includeDirectoryToggleProbeRoots: true,
			cancellationToken: cancellationToken,
			includeControllerImpactProbeRoots: true);
		var counts = scan.Value.EffectiveIgnoreOptionCounts;
		var controllerImpactCounts = scan.Value.ControllerImpactCounts;
		var snapshotState = new IgnoreSectionSnapshotState(
			HasIgnoreOptionCounts: true,
			IgnoreOptionCounts: counts,
			ControllerImpactCounts: controllerImpactCounts,
			HasExtensionlessEntries: counts.ExtensionlessFiles > 0,
			ExtensionlessEntriesCount: counts.ExtensionlessFiles,
			GitEvidence: scan.Value.GitEvidence);
		var dynamicAvailability = IgnoreOptionsAvailabilityResolver.Resolve(
			availability,
			snapshotState,
			new Dictionary<IgnoreOptionId, bool>(),
			stateCacheIsComplete: false);

		return ignoreOptions.GetOptions(dynamicAvailability)
			.Where(static option => option.DefaultChecked)
			.Select(static option => option.Id)
			.Distinct()
			.ToArray();
	}

	private async Task<ExportOutputMetrics> CalculateContentMetricsAsync(
		IReadOnlyList<string>? orderedFilePaths,
		CancellationToken cancellationToken) =>
		await ProjectContentMetricsCalculator
			.CalculateAsync(fileContentAnalyzer, orderedFilePaths, cancellationToken)
			.ConfigureAwait(false);

	private static ProjectTreeSummaryReport CountTree(
		TreeNodeDescriptor root,
		CancellationToken cancellationToken)
	{
		var directories = 0;
		var files = 0;
		var accessDeniedDirectories = 0;
		var stack = new Stack<TreeNodeDescriptor>();
		stack.Push(root);

		while (stack.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var node = stack.Pop();
			if (node.IsDirectory)
			{
				directories++;
				if (node.IsAccessDenied)
					accessDeniedDirectories++;
			}
			else
			{
				files++;
			}

			for (var i = node.Children.Count - 1; i >= 0; i--)
				stack.Push(node.Children[i]);
		}

		return new ProjectTreeSummaryReport(directories, files, accessDeniedDirectories);
	}

	private static IReadOnlyList<string> BuildWarnings(
		LoadedProjectAnalysisRequest request,
		CancellationToken cancellationToken)
	{
		var warnings = new List<string>();
		var availableRoots = request.AvailableRootFolders.ToArray();
		var requestedRoots = request.RequestedRootFoldersForDiagnostics ?? request.SelectedRootFolders;
		foreach (var root in requestedRoots)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!ProjectTreePathIdentity.TryResolveAvailableName(availableRoots, root, out _))
				warnings.Add($"Selected root folder was not found in the current project: {root}");
		}

		var availableExtensions = request.AvailableExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var requestedExtensions = request.RequestedExtensionsForDiagnostics ?? request.SelectedExtensions;
		foreach (var extension in requestedExtensions)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!availableExtensions.Contains(extension))
				warnings.Add($"Selected extension was not found in the current project: {extension}");
		}

		return warnings;
	}

	private static IReadOnlyCollection<string> NormalizeRootFolders(IReadOnlyCollection<string>? rootFolders)
	{
		if (rootFolders is null || rootFolders.Count == 0)
			return [];

		return rootFolders
			// Root folder names come from the filesystem. Do not trim legal POSIX names.
			.Where(static value => !string.IsNullOrEmpty(value))
			.Distinct(ProjectTreePathIdentity.CanonicalComparer)
			.OrderBy(static value => value, ProjectTreePathIdentity.CanonicalComparer)
			.ToArray();
	}

	private static IReadOnlyList<string> ResolveRequestedRootFolders(
		IReadOnlyList<string> availableRootFolders,
		IReadOnlyCollection<string> requestedRootFolders)
	{
		var resolved = new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer);
		foreach (var requestedRoot in requestedRootFolders)
		{
			if (ProjectTreePathIdentity.TryResolveAvailableName(
				    availableRootFolders,
				    requestedRoot,
				    out var availableRoot))
			{
				resolved.Add(availableRoot);
			}
		}

		return resolved
			.OrderBy(static value => value, ProjectTreePathIdentity.CanonicalComparer)
			.ToArray();
	}

	private static IReadOnlyCollection<string> NormalizeExtensions(IReadOnlyCollection<string>? extensions)
	{
		if (extensions is null || extensions.Count == 0)
			return [];

		return extensions
			.Where(static value => !string.IsNullOrEmpty(value))
			.Select(NormalizeExtension)
			.Where(static value => value.Length > 0)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static IReadOnlyCollection<string> NormalizeResolvedExtensionNames(
		IReadOnlyCollection<string>? extensions)
	{
		if (extensions is null || extensions.Count == 0)
			return [];

		return extensions
			.Where(static value => !string.IsNullOrEmpty(value))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static string NormalizeExtension(string value)
	{
		if (value.Length == 0 || value == ".")
			return string.Empty;

		return value[0] == '.' ? value : "." + value;
	}

	private static ProjectOutputMetricsReport ToReportMetrics(ExportOutputMetrics metrics) =>
		new(metrics.Lines, metrics.Chars, metrics.Tokens);

	private static double ToMilliseconds(TimeSpan elapsed) =>
		Math.Round(elapsed.TotalMilliseconds, 3, MidpointRounding.AwayFromZero);
}

public sealed record ProjectAnalysisRequest(
	string RootPath,
	IReadOnlyCollection<string>? SelectedRootFolders = null,
	IReadOnlyCollection<string>? SelectedExtensions = null,
	IReadOnlyCollection<IgnoreOptionId>? SelectedIgnoreOptions = null)
{
	internal LocalProjectSelectionState? LocalProfileState { get; init; }
	internal IReadOnlyDictionary<string, bool>? KnownExtensionStates { get; init; }
	internal bool CaptureIgnoreImpactCounts { get; init; }
}

public sealed record LoadedProjectAnalysisRequest(
	string RootPath,
	BuildTreeResult Tree,
	IReadOnlyCollection<string> AvailableRootFolders,
	IReadOnlyCollection<string> AvailableExtensions,
	IReadOnlyCollection<string> SelectedRootFolders,
	IReadOnlyCollection<string> SelectedExtensions,
	IReadOnlyCollection<IgnoreOptionId> SelectedIgnoreOptions,
	bool RootAccessDenied,
	bool HadAccessDenied,
	TimeSpan? KnownLoadingElapsed = null,
	int DiscoveredGitTrackedIndexCount = 0,
	int UnavailableGitTrackedIndexCount = 0,
	ProjectTreeInventorySnapshot? TreeInventory = null,
	GitWorkspaceEvidence GitEvidence = default)
{
	internal IReadOnlyCollection<string>? RequestedRootFoldersForDiagnostics { get; init; }
	internal IReadOnlyCollection<string>? RequestedExtensionsForDiagnostics { get; init; }
	internal IReadOnlyDictionary<IgnoreOptionId, bool>? ResolvedIgnoreOptionStates { get; init; }
	internal IgnoreRules? EffectiveRules { get; init; }
	internal bool RootSelectionIsExplicit { get; init; }
	internal IExtensionInclusionPolicy? EffectiveExtensionPolicy { get; init; }
	internal bool HasIgnoreOptionCounts { get; init; }
	internal IgnoreOptionCounts IgnoreOptionCounts { get; init; }
	internal IgnoreControllerImpactCounts IgnoreControllerImpactCounts { get; init; }
}
