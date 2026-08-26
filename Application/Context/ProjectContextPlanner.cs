using System.Buffers;
using System.Security.Cryptography;
using DevProjex.Application.Selection;

namespace DevProjex.Application.Context;

public sealed class ProjectContextPlanner(ProjectAnalysisService analysisService)
{
	private const int FingerprintStackBufferBytes = 512;
	private const string InvalidSelectedPathCode = ProjectSelectionPath.InvalidPathCode;
	private const string MissingSelectedPathCode = "DPX-SELECTION-PATH-MISSING";
	private static readonly byte[] FingerprintValueSeparator = [0];

	public Task<ProjectContextPlan> BuildWithIgnoreImpactCountsAsync(
		ProjectContextRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		return BuildAsync(
			request with { CaptureIgnoreImpactCounts = true },
			cancellationToken);
	}

	public async Task<ProjectContextPlan> BuildAsync(
		ProjectContextRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentNullException.ThrowIfNull(request.Selection);

		var sourceRoot = NormalizeSourceRoot(request.ProjectPath);
		var selection = ResolveLocalOverrideIntent(EnsureResolved(request.Selection));
		var selectedIgnoreOptions = ProjectSelectionAdapter.ToIgnoreOptions(selection);
		var loaded = analysisService.Load(
			new ProjectAnalysisRequest(
				sourceRoot,
				selection.Roots,
				selection.Extensions,
				selectedIgnoreOptions)
			{
				LocalProfileState = selection.LocalProfileState,
				CaptureIgnoreImpactCounts = request.CaptureIgnoreImpactCounts
			},
			cancellationToken);
		var sourceIdentity = ResolveSourceIdentity(sourceRoot, request.SourceIdentity);
		var effectiveRoot = loaded.Tree.Root with
		{
			DisplayName = sourceIdentity.DisplayName
		};

		var diagnostics = new List<ContextDiagnostic>();
		var selectedFullPaths = ResolveSelectedPaths(
			effectiveRoot,
			sourceRoot,
			selection.SelectedPaths,
			diagnostics,
			cancellationToken,
			out var explicitSelectionHadMatch);
		var selectsNoEffectivePaths =
			selection.SelectedPaths is { Count: > 0 } &&
			!explicitSelectionHadMatch;
		var includedNodes = selectsNoEffectivePaths
			? []
			: ProjectTreeSelectionProjection.BuildIncludedNodesWithCancellation(
				effectiveRoot,
				selectedFullPaths,
				cancellationToken);
		var includedPathSet = BuildIncludedPathSet(includedNodes, cancellationToken);
		var projectedTree = selectsNoEffectivePaths
			? loaded.Tree.Root with { Children = [] }
			: ProjectTreeSelectionProjection.BuildProjectedTreeWithCancellation(
				effectiveRoot,
				includedPathSet,
				cancellationToken) ??
			  effectiveRoot with { Children = [] };
		var includedFiles = selectsNoEffectivePaths
			? []
			: ProjectTreeSelectionProjection.BuildOrderedSelectedFilePathsWithCancellation(
				effectiveRoot,
				selectedFullPaths,
				ensureExists: false,
				cancellationToken);
		var effectiveFileSizes = BuildEffectiveFileSizes(
			effectiveRoot,
			loaded.TreeInventory,
			cancellationToken);
		var includedBytes = CalculateIncludedBytes(
			includedFiles,
			effectiveFileSizes,
			cancellationToken);
		var includedFolders = BuildOrderedIncludedFolders(includedNodes, cancellationToken);

		var projectedLoaded = loaded with
		{
			Tree = new BuildTreeResult(
				projectedTree,
				loaded.Tree.RootAccessDenied,
				loaded.Tree.HadAccessDenied,
				includedFiles)
		};
		var analysis = await analysisService
			.BuildReportFromTreeAsync(projectedLoaded, cancellationToken)
			.ConfigureAwait(false);
		AddAnalysisDiagnostics(analysis, diagnostics);

		var gitReadiness = ProjectContextGitReadiness.Evaluate(
			selection.GitMode!.Value,
			loaded.DiscoveredGitTrackedIndexCount,
			loaded.UnavailableGitTrackedIndexCount);
		if (gitReadiness.CreateDiagnostic(sourceRoot) is { } gitDiagnostic)
			diagnostics.Add(gitDiagnostic);

		var refreshedLocalState = RefreshLocalProfileState(selection.LocalProfileState, loaded);
		var preserveRequestedSelectionIntent =
			request.CaptureIgnoreImpactCounts && refreshedLocalState is null;
		var effectiveSelection = selection with
		{
			// Selection is durable user intent; SelectedRoots/SelectedExtensions below are
			// the effective rows that exist now. Keeping them separate preserves stale-profile
			// diagnostics and hidden checkbox state without feeding phantom names to the tree.
			Roots = preserveRequestedSelectionIntent && selection.Roots is not null
				? selection.Roots
				: ResolveRootSelectionIntent(selection, refreshedLocalState, loaded),
			Extensions = preserveRequestedSelectionIntent && selection.Extensions is not null
				? selection.Extensions
				: ResolveExtensionSelectionIntent(selection, refreshedLocalState, loaded),
			GitMode = preserveRequestedSelectionIntent
				? selection.GitMode
				: ResolveGitModeIntent(selection, refreshedLocalState, loaded),
			Exclusions = preserveRequestedSelectionIntent
				? selection.Exclusions
				: ResolveExclusionIntent(selection, refreshedLocalState, loaded),
			HideSecrets = preserveRequestedSelectionIntent
				? selection.HideSecrets
				: ResolveHideSecretsIntent(selection, refreshedLocalState, loaded),
			HidePrivateData = preserveRequestedSelectionIntent
				? selection.HidePrivateData
				: ResolveHidePrivateDataIntent(selection, refreshedLocalState, loaded),
			CompressCode = preserveRequestedSelectionIntent
				? selection.CompressCode
				: ResolveCompressCodeIntent(selection, refreshedLocalState, loaded),
			StripComments = preserveRequestedSelectionIntent
				? selection.StripComments
				: ResolveStripCommentsIntent(selection, refreshedLocalState, loaded),
			StripBlankLines = preserveRequestedSelectionIntent
				? selection.StripBlankLines
				: ResolveStripBlankLinesIntent(selection, refreshedLocalState, loaded),
			SelectedPaths = NormalizeRelativeSelectionForOutput(
				sourceRoot,
				selectedFullPaths,
				cancellationToken),
			LocalProfileState = refreshedLocalState
		};

		return new ProjectContextPlan(
			SourceRoot: sourceRoot,
			Selection: effectiveSelection,
			AvailableRoots: loaded.AvailableRootFolders.OrderBy(static value => value, PathComparer.Default).ToArray(),
			SelectedRoots: loaded.SelectedRootFolders.OrderBy(static value => value, PathComparer.Default).ToArray(),
			AvailableExtensions: loaded.AvailableExtensions.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
			SelectedExtensions: loaded.SelectedExtensions.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
			EffectiveTree: effectiveRoot,
			ProjectedTree: projectedTree,
			SelectedFullPaths: selectedFullPaths,
			IncludedFiles: includedFiles,
			IncludedFolders: includedFolders,
			Analysis: analysis,
			Diagnostics: diagnostics,
			GitReadiness: gitReadiness,
			Fingerprint: BuildFingerprint(sourceRoot, effectiveSelection, includedNodes, cancellationToken),
			IncludedBytes: includedBytes,
			EffectiveFileSizes: effectiveFileSizes,
			SourceIdentity: sourceIdentity,
			HasIgnoreOptionCounts: loaded.HasIgnoreOptionCounts,
			IgnoreOptionCounts: loaded.IgnoreOptionCounts,
			IgnoreControllerImpactCounts: loaded.IgnoreControllerImpactCounts);
	}

	public async Task<ProjectContextPlan> ReprojectSelectionAsync(
		ProjectContextPlan baseline,
		IReadOnlyCollection<string>? selectedPaths,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(baseline);
		var diagnostics = baseline.Diagnostics
			.Where(static diagnostic =>
				diagnostic.Code is not MissingSelectedPathCode and not InvalidSelectedPathCode)
			.ToList();
		var selectedFullPaths = ResolveSelectedPaths(
			baseline.EffectiveTree,
			baseline.SourceRoot,
			selectedPaths,
			diagnostics,
			cancellationToken,
			out var explicitSelectionHadMatch);
		var selectsNoEffectivePaths =
			selectedPaths is { Count: > 0 } &&
			!explicitSelectionHadMatch;
		var includedNodes = selectsNoEffectivePaths
			? []
			: ProjectTreeSelectionProjection.BuildIncludedNodesWithCancellation(
				baseline.EffectiveTree,
				selectedFullPaths,
				cancellationToken);
		var includedPathSet = BuildIncludedPathSet(includedNodes, cancellationToken);
		var projectedTree = selectsNoEffectivePaths
			? baseline.EffectiveTree with { Children = [] }
			: ProjectTreeSelectionProjection.BuildProjectedTreeWithCancellation(
				baseline.EffectiveTree,
				includedPathSet,
				cancellationToken) ??
			  baseline.EffectiveTree with { Children = [] };
		var includedFiles = selectsNoEffectivePaths
			? []
			: ProjectTreeSelectionProjection.BuildOrderedSelectedFilePathsWithCancellation(
				baseline.EffectiveTree,
				selectedFullPaths,
				ensureExists: false,
				cancellationToken);
		var includedBytes = CalculateIncludedBytes(
			includedFiles,
			baseline.EffectiveFileSizes,
			cancellationToken);
		var includedFolders = BuildOrderedIncludedFolders(includedNodes, cancellationToken);
		var selection = baseline.Selection with
		{
			SelectedPaths = NormalizeRelativeSelectionForOutput(
				baseline.SourceRoot,
				selectedFullPaths,
				cancellationToken)
		};
		var reportInput = new LoadedProjectAnalysisRequest(
			RootPath: baseline.SourceRoot,
			Tree: new BuildTreeResult(
				projectedTree,
				baseline.Analysis.Diagnostics.RootAccessDenied,
				baseline.Analysis.Diagnostics.HadAccessDenied,
				includedFiles),
			AvailableRootFolders: baseline.AvailableRoots,
			AvailableExtensions: baseline.AvailableExtensions,
			SelectedRootFolders: baseline.SelectedRoots,
			SelectedExtensions: baseline.SelectedExtensions,
			SelectedIgnoreOptions: ProjectSelectionAdapter.ToIgnoreOptions(selection),
			RootAccessDenied: baseline.Analysis.Diagnostics.RootAccessDenied,
			HadAccessDenied: baseline.Analysis.Diagnostics.HadAccessDenied,
			KnownLoadingElapsed: TimeSpan.Zero,
			DiscoveredGitTrackedIndexCount:
				baseline.GitReadiness.LoadedTrackedIndexCount +
				baseline.GitReadiness.UnavailableTrackedIndexCount,
			UnavailableGitTrackedIndexCount:
				baseline.GitReadiness.UnavailableTrackedIndexCount);
		var analysis = await analysisService
			.BuildReportFromTreeAsync(reportInput, cancellationToken)
			.ConfigureAwait(false);

		return baseline with
		{
			Selection = selection,
			ProjectedTree = projectedTree,
			SelectedFullPaths = selectedFullPaths,
			IncludedFiles = includedFiles,
			IncludedFolders = includedFolders,
			Analysis = analysis,
			Diagnostics = diagnostics,
			Fingerprint = BuildFingerprint(
				baseline.SourceRoot,
				selection,
				includedNodes,
				cancellationToken),
			IncludedBytes = includedBytes
		};
	}

	/// <summary>
	/// Applies a content transformation without touching discovery, ignore rules, or the tree.
	/// </summary>
	public ProjectContextPlan ApplyContentTransformationSelection(
		ProjectContextPlan baseline,
		bool hideSecrets,
		bool? compressCode = null,
		bool? stripComments = null,
		bool? stripBlankLines = null,
		bool? hidePrivateData = null) =>
		ApplyContentTransformationSelectionWithCancellation(
			baseline,
			hideSecrets,
			compressCode,
			stripComments,
			stripBlankLines,
			hidePrivateData,
			CancellationToken.None);

	public ProjectContextPlan ApplyContentTransformationSelectionWithCancellation(
		ProjectContextPlan baseline,
		bool hideSecrets,
		bool? compressCode,
		bool? stripComments,
		bool? stripBlankLines,
		bool? hidePrivateData,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(baseline);
		cancellationToken.ThrowIfCancellationRequested();
		var selection = baseline.Selection with
		{
			HideSecrets = hideSecrets,
			HidePrivateData = hidePrivateData ?? baseline.Selection.HidePrivateData,
			CompressCode = compressCode ?? baseline.Selection.CompressCode,
			StripComments = stripComments ?? baseline.Selection.StripComments,
			StripBlankLines = stripBlankLines ?? baseline.Selection.StripBlankLines
		};
		var includedNodes = ProjectTreeSelectionProjection.BuildIncludedNodesWithCancellation(
			baseline.EffectiveTree,
			baseline.SelectedFullPaths,
			cancellationToken);
		return baseline with
		{
			Selection = selection,
			Fingerprint = BuildFingerprint(
				baseline.SourceRoot,
				selection,
				includedNodes,
				cancellationToken),
			Redaction = null,
			Privacy = null
		};
	}

	private static IReadOnlyDictionary<string, long> BuildEffectiveFileSizes(
		TreeNodeDescriptor root,
		ProjectTreeInventorySnapshot? inventory,
		CancellationToken cancellationToken)
	{
		var sizes = new Dictionary<string, long>(PathComparer.Default);
		var stack = new Stack<TreeNodeDescriptor>();
		stack.Push(root);
		while (stack.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var node = stack.Pop();
			if (!node.IsDirectory)
			{
				sizes[node.FullPath] = -1;
				continue;
			}

			for (var index = node.Children.Count - 1; index >= 0; index--)
				stack.Push(node.Children[index]);
		}

		if (inventory is not null)
		{
			foreach (var entry in inventory.Entries)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!entry.IsDirectory && sizes.ContainsKey(entry.FullPath))
					sizes[entry.FullPath] = Math.Max(0, entry.Length);
			}
		}

		foreach (var path in sizes.Keys.ToArray())
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (sizes[path] >= 0)
				continue;
			try
			{
				sizes[path] = Math.Max(0, new FileInfo(path).Length);
			}
			catch
			{
				sizes[path] = 0;
			}
		}

		return sizes;
	}

	private static long CalculateIncludedBytes(
		IReadOnlyList<string> includedFiles,
		IReadOnlyDictionary<string, long>? effectiveFileSizes,
		CancellationToken cancellationToken)
	{
		if (effectiveFileSizes is null || effectiveFileSizes.Count == 0)
			return 0;

		long total = 0;
		foreach (var path in includedFiles)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!effectiveFileSizes.TryGetValue(path, out var length) || length <= 0)
				continue;
			total = total > long.MaxValue - length
				? long.MaxValue
				: total + length;
		}
		return total;
	}

	private static string NormalizeSourceRoot(string path)
	{
		if (PathUtility.IsMissingPath(path))
			throw new ProjectContextValidationException("DPX-PROJECT-PATH-REQUIRED", "Project path is required.");

		var normalized = PathUtility.Normalize(path);
		if (!Directory.Exists(normalized))
		{
			throw new ProjectContextValidationException(
				"DPX-PROJECT-NOT-FOUND",
				"Project directory was not found.");
		}

		return normalized;
	}

	private static ProjectSourceIdentity ResolveSourceIdentity(
		string sourceRoot,
		ProjectSourceIdentity? sourceIdentity)
	{
		var fallbackName = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceRoot));
		if (string.IsNullOrEmpty(fallbackName))
			fallbackName = sourceRoot;

		if (sourceIdentity is null)
		{
			return new ProjectSourceIdentity(
				fallbackName,
				ProjectSourceType.LocalFolder,
				sourceRoot);
		}

		var normalizedIdentity = sourceIdentity with
		{
			DisplayName = sourceIdentity.SourceType == ProjectSourceType.LocalFolder
				? fallbackName
				: string.IsNullOrWhiteSpace(sourceIdentity.DisplayName)
					? fallbackName
					: sourceIdentity.DisplayName.Trim(),
			SourceReference = string.IsNullOrWhiteSpace(sourceIdentity.SourceReference)
				? sourceRoot
				: sourceIdentity.SourceReference
		};
		if (normalizedIdentity.SourceType != ProjectSourceType.GitClone)
			return normalizedIdentity;

		var safeRepositoryUrl = RepositoryUrlUtility.ToSafeDisplay(
			normalizedIdentity.RepositoryUrl ?? normalizedIdentity.SourceReference);
		return normalizedIdentity with
		{
			SourceReference = safeRepositoryUrl.Length > 0 ? safeRepositoryUrl : fallbackName,
			RepositoryUrl = safeRepositoryUrl.Length > 0 ? safeRepositoryUrl : null
		};
	}

	private static ProjectSelectionSpec EnsureResolved(ProjectSelectionSpec selection)
	{
		if (selection.GitMode is null || selection.Exclusions is null)
		{
			throw new ProjectContextValidationException(
				"DPX-CLI-PROFILE-UNRESOLVED",
				"Selection profile was not fully resolved.");
		}

		var legacyHideSecrets = selection.Exclusions.Contains(ProjectExclusion.HideSecrets);
		return selection with
		{
			Exclusions = selection.Exclusions
				.Where(static exclusion => exclusion != ProjectExclusion.HideSecrets)
				.OrderBy(static exclusion => (int)exclusion)
				.ToArray(),
			HideSecrets = selection.HideSecrets ?? legacyHideSecrets,
			HidePrivateData = selection.HidePrivateData ?? false,
			// Compression never had a v5 --exclude token, so there is nothing legacy to fall back to.
			CompressCode = selection.CompressCode ?? false,
			StripComments = selection.StripComments ?? false,
			StripBlankLines = selection.StripBlankLines ?? false
		};
	}

	private static ProjectSelectionSpec ResolveLocalOverrideIntent(ProjectSelectionSpec selection)
	{
		if (selection.LocalProfileState is not { } state)
			return selection;

		var profile = state.Profile;
		var profileRoots = ResolveStoredSelection(profile.SelectedRootFolders, profile.RootFolderStates);
		var profileExtensions = ResolveStoredSelection(profile.SelectedExtensions, profile.ExtensionStates);
		var profileIgnoreOptions = ResolveStoredIgnoreSelection(profile);
		var inferredState = state with
		{
			RootsOverridden = state.RootsOverridden ||
			                  !SetEquals(selection.Roots, profileRoots, PathComparer.Default),
			ExtensionsOverridden = state.ExtensionsOverridden ||
			                       !SetEquals(
				                       selection.Extensions,
				                       profileExtensions,
				                       StringComparer.OrdinalIgnoreCase),
			IgnoreOptionsOverridden = state.IgnoreOptionsOverridden ||
			                          selection.GitMode != GitFilteringModeResolver.Resolve(profileIgnoreOptions) ||
			                          !SetEquals(
				                          selection.Exclusions,
				                          ProjectSelectionAdapter.ToExclusions(profileIgnoreOptions),
				                          EqualityComparer<ProjectExclusion>.Default) ||
				                          selection.HideSecrets != profileIgnoreOptions.Contains(IgnoreOptionId.HideSecrets) ||
				                          selection.HidePrivateData != profileIgnoreOptions.Contains(IgnoreOptionId.HidePrivateData) ||
				                          selection.CompressCode != profileIgnoreOptions.Contains(IgnoreOptionId.CompressCode) ||
				                          selection.StripComments != profileIgnoreOptions.Contains(IgnoreOptionId.StripComments) ||
				                          selection.StripBlankLines != profileIgnoreOptions.Contains(IgnoreOptionId.StripBlankLines)
		};

		return selection with { LocalProfileState = inferredState };
	}

	private static LocalProjectSelectionState? RefreshLocalProfileState(
		LocalProjectSelectionState? state,
		LoadedProjectAnalysisRequest loaded)
	{
		if (state is null)
			return null;

		var profile = state.Profile;
		var rootStates = state.RootsOverridden
			? profile.RootFolderStates
			: RefreshStringStates(
				profile.RootFolderStates,
				loaded.AvailableRootFolders,
				loaded.SelectedRootFolders,
				PathComparer.Default);
		var extensionStates = state.ExtensionsOverridden
			? profile.ExtensionStates
			: RefreshStringStates(
				profile.ExtensionStates,
				loaded.AvailableExtensions,
				loaded.SelectedExtensions,
				StringComparer.OrdinalIgnoreCase);
		var ignoreStates = state.IgnoreOptionsOverridden
			? profile.IgnoreOptionStates
			: RefreshIgnoreStates(
				profile.IgnoreOptionStates,
				loaded.ResolvedIgnoreOptionStates,
				loaded.SelectedIgnoreOptions);
		var selectedRoots = rootStates is null
			? profile.SelectedRootFolders.ToArray()
			: SelectCheckedNames(rootStates, PathComparer.Default);
		var selectedExtensions = extensionStates is null
			? profile.SelectedExtensions.ToArray()
			: SelectCheckedNames(extensionStates, StringComparer.OrdinalIgnoreCase);
		var selectedIgnoreOptions = ignoreStates is null
			? profile.SelectedIgnoreOptions.ToArray()
			: ignoreStates
				.Where(static pair => pair.Value)
				.Select(static pair => pair.Key)
				.OrderBy(static option => (int)option)
				.ToArray();

		return state with
		{
			Profile = new ProjectSelectionProfile(
				selectedRoots,
				selectedExtensions,
				selectedIgnoreOptions,
				rootStates,
				extensionStates,
				ignoreStates,
				profile.SelectedPaths?.ToArray())
		};
	}

	private static IReadOnlyDictionary<string, bool>? RefreshStringStates(
		IReadOnlyDictionary<string, bool>? previousStates,
		IReadOnlyCollection<string> available,
		IReadOnlyCollection<string> selected,
		StringComparer comparer)
	{
		if (previousStates is null)
			return null;

		var selectedSet = selected.ToHashSet(comparer);
		var refreshed = new Dictionary<string, bool>(previousStates, comparer);
		foreach (var name in available)
			refreshed[name] = selectedSet.Contains(name);
		return refreshed;
	}

	private static IReadOnlyDictionary<IgnoreOptionId, bool>? RefreshIgnoreStates(
		IReadOnlyDictionary<IgnoreOptionId, bool>? previousStates,
		IReadOnlyDictionary<IgnoreOptionId, bool>? resolvedStates,
		IReadOnlyCollection<IgnoreOptionId> selected)
	{
		if (previousStates is null)
			return null;

		var refreshed = new Dictionary<IgnoreOptionId, bool>(previousStates);
		if (resolvedStates is not null)
		{
			// The engine owns visibility churn and preserves hidden known rows. Rebuilding
			// from the visible selected set would silently clear a checked option precisely
			// when that option hid its own evidence.
			foreach (var (option, isChecked) in resolvedStates)
				refreshed[option] = isChecked;
		}
		else
		{
			var selectedSet = selected.ToHashSet();
			foreach (var option in previousStates.Keys)
				refreshed[option] = selectedSet.Contains(option);
			foreach (var option in selectedSet)
				refreshed[option] = true;
		}
		GitFilteringModeResolver.Normalize(refreshed);
		return refreshed;
	}

	private static string[] SelectCheckedNames(
		IReadOnlyDictionary<string, bool> states,
		StringComparer comparer) =>
		states
			.Where(static pair => pair.Value)
			.Select(static pair => pair.Key)
			.Distinct(comparer)
			.OrderBy(static name => name, comparer)
			.ToArray();

	private static IReadOnlyCollection<string>? ResolveRootSelectionIntent(
		ProjectSelectionSpec selection,
		LocalProjectSelectionState? state,
		LoadedProjectAnalysisRequest loaded) =>
		state is null
			? loaded.SelectedRootFolders.ToArray()
			: state.RootsOverridden
				? selection.Roots
				: ResolveStoredSelection(state.Profile.SelectedRootFolders, state.Profile.RootFolderStates);

	private static IReadOnlyCollection<string>? ResolveExtensionSelectionIntent(
		ProjectSelectionSpec selection,
		LocalProjectSelectionState? state,
		LoadedProjectAnalysisRequest loaded) =>
		state is null
			? loaded.SelectedExtensions.ToArray()
			: state.ExtensionsOverridden
				? selection.Extensions
				: ResolveStoredSelection(state.Profile.SelectedExtensions, state.Profile.ExtensionStates);

	private static GitFilteringMode ResolveGitModeIntent(
		ProjectSelectionSpec selection,
		LocalProjectSelectionState? state,
		LoadedProjectAnalysisRequest loaded) =>
		state is null || state.IgnoreOptionsOverridden
			? state is null
				? GitFilteringModeResolver.Resolve(loaded.SelectedIgnoreOptions)
				: selection.GitMode!.Value
			: GitFilteringModeResolver.Resolve(ResolveStoredIgnoreSelection(state.Profile));

	private static IReadOnlyCollection<ProjectExclusion> ResolveExclusionIntent(
		ProjectSelectionSpec selection,
		LocalProjectSelectionState? state,
		LoadedProjectAnalysisRequest loaded) =>
		state is null || state.IgnoreOptionsOverridden
			? state is null
				? ProjectSelectionAdapter.ToExclusions(loaded.SelectedIgnoreOptions)
				: selection.Exclusions ?? []
			: ProjectSelectionAdapter.ToExclusions(ResolveStoredIgnoreSelection(state.Profile));

	private static bool ResolveHideSecretsIntent(
		ProjectSelectionSpec selection,
		LocalProjectSelectionState? state,
		LoadedProjectAnalysisRequest loaded) =>
		state is null || state.IgnoreOptionsOverridden
			? state is null
				? loaded.SelectedIgnoreOptions.Contains(IgnoreOptionId.HideSecrets)
				: selection.HideSecrets == true
			: ResolveStoredIgnoreSelection(state.Profile).Contains(IgnoreOptionId.HideSecrets);

	private static bool ResolveHidePrivateDataIntent(
		ProjectSelectionSpec selection,
		LocalProjectSelectionState? state,
		LoadedProjectAnalysisRequest loaded) =>
		state is null || state.IgnoreOptionsOverridden
			? state is null
				? loaded.SelectedIgnoreOptions.Contains(IgnoreOptionId.HidePrivateData)
				: selection.HidePrivateData == true
			: ResolveStoredIgnoreSelection(state.Profile).Contains(IgnoreOptionId.HidePrivateData);

	private static bool ResolveCompressCodeIntent(
		ProjectSelectionSpec selection,
		LocalProjectSelectionState? state,
		LoadedProjectAnalysisRequest loaded) =>
		state is null || state.IgnoreOptionsOverridden
			? state is null
				? loaded.SelectedIgnoreOptions.Contains(IgnoreOptionId.CompressCode)
				: selection.CompressCode == true
			: ResolveStoredIgnoreSelection(state.Profile).Contains(IgnoreOptionId.CompressCode);

	private static bool ResolveStripCommentsIntent(
		ProjectSelectionSpec selection,
		LocalProjectSelectionState? state,
		LoadedProjectAnalysisRequest loaded) =>
		state is null || state.IgnoreOptionsOverridden
			? state is null
				? loaded.SelectedIgnoreOptions.Contains(IgnoreOptionId.StripComments)
				: selection.StripComments == true
			: ResolveStoredIgnoreSelection(state.Profile).Contains(IgnoreOptionId.StripComments);

	private static bool ResolveStripBlankLinesIntent(
		ProjectSelectionSpec selection,
		LocalProjectSelectionState? state,
		LoadedProjectAnalysisRequest loaded) =>
		state is null || state.IgnoreOptionsOverridden
			? state is null
				? loaded.SelectedIgnoreOptions.Contains(IgnoreOptionId.StripBlankLines)
				: selection.StripBlankLines == true
			: ResolveStoredIgnoreSelection(state.Profile).Contains(IgnoreOptionId.StripBlankLines);

	private static IReadOnlyCollection<string> ResolveStoredSelection(
		IReadOnlyCollection<string> selected,
		IReadOnlyDictionary<string, bool>? states) =>
		states is null
			? selected
			: states.Where(static pair => pair.Value).Select(static pair => pair.Key).ToArray();

	private static IReadOnlyCollection<IgnoreOptionId> ResolveStoredIgnoreSelection(
		ProjectSelectionProfile profile) =>
		profile.IgnoreOptionStates is null
			? profile.SelectedIgnoreOptions
			: profile.IgnoreOptionStates
				.Where(static pair => pair.Value)
				.Select(static pair => pair.Key)
				.ToArray();

	private static bool SetEquals<T>(
		IReadOnlyCollection<T>? left,
		IReadOnlyCollection<T> right,
		IEqualityComparer<T> comparer) =>
		new HashSet<T>(left ?? [], comparer).SetEquals(right);

	private static IReadOnlySet<string> ResolveSelectedPaths(
		TreeNodeDescriptor root,
		string sourceRoot,
		IReadOnlyCollection<string>? selectedPaths,
		List<ContextDiagnostic> diagnostics,
		CancellationToken cancellationToken,
		out bool explicitSelectionHadMatch)
	{
		explicitSelectionHadMatch = false;
		if (selectedPaths is null || selectedPaths.Count == 0)
			return new HashSet<string>(PathComparer.Default);

		var effectivePathMap = BuildEffectivePathMap(root, sourceRoot, cancellationToken);
		var resolved = new HashSet<string>(PathComparer.Default);
		foreach (var input in selectedPaths)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var relativePath = ProjectSelectionPath.NormalizeRelative(input);
			if (relativePath.Length == 0)
			{
				explicitSelectionHadMatch = true;
				resolved.Add(root.FullPath);
				continue;
			}

			if (effectivePathMap.TryGetValue(relativePath, out var fullPath))
			{
				explicitSelectionHadMatch = true;
				resolved.Add(fullPath);
				continue;
			}

			diagnostics.Add(new ContextDiagnostic(
				MissingSelectedPathCode,
				ContextDiagnosticSeverity.Warning,
				"Selected path is not present in the effective project tree.",
				relativePath));
		}

		return NormalizeSelectionFrontier(root, resolved, cancellationToken);
	}

	private static IReadOnlySet<string> NormalizeSelectionFrontier(
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedPaths,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (selectedPaths.Contains(root.FullPath))
			return new HashSet<string>(PathComparer.Default);

		if (selectedPaths.Count < 2)
			return selectedPaths;

		var frontier = new HashSet<string>(selectedPaths, PathComparer.Default);
		foreach (var selectedPath in selectedPaths)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var parent = Directory.GetParent(selectedPath);
			while (parent is not null)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (frontier.Contains(parent.FullName))
				{
					frontier.Remove(selectedPath);
					break;
				}

				parent = parent.Parent;
			}
		}

		return frontier;
	}

	private static Dictionary<string, string> BuildEffectivePathMap(
		TreeNodeDescriptor root,
		string sourceRoot,
		CancellationToken cancellationToken)
	{
		var paths = new Dictionary<string, string>(PathComparer.Default);
		var stack = new Stack<TreeNodeDescriptor>();
		stack.Push(root);
		while (stack.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var node = stack.Pop();
			var relativePath = Path.GetRelativePath(sourceRoot, node.FullPath);
			var key = relativePath == "." ? string.Empty : NormalizePathSeparators(relativePath);
			paths[key] = node.FullPath;

			for (var index = node.Children.Count - 1; index >= 0; index--)
				stack.Push(node.Children[index]);
		}

		return paths;
	}

	private static string NormalizePathSeparators(string path) =>
		PathUtility.NormalizeSeparators(path);

	private static void AddAnalysisDiagnostics(
		ProjectAnalysisReport analysis,
		List<ContextDiagnostic> diagnostics)
	{
		if (analysis.Diagnostics.RootAccessDenied)
		{
			diagnostics.Add(new ContextDiagnostic(
				"DPX-PROJECT-ROOT-ACCESS-DENIED",
				ContextDiagnosticSeverity.Error,
				"The project root cannot be read.",
				analysis.RootPath));
		}
		else if (analysis.Diagnostics.HadAccessDenied)
		{
			diagnostics.Add(new ContextDiagnostic(
				"DPX-PROJECT-PARTIAL-ACCESS",
				ContextDiagnosticSeverity.Warning,
				"Some project paths could not be read."));
		}

		foreach (var warning in analysis.Diagnostics.Warnings)
		{
			diagnostics.Add(new ContextDiagnostic(
				"DPX-PROJECT-SELECTION-WARNING",
				ContextDiagnosticSeverity.Warning,
				warning,
				ExtractSelectionWarningValue(warning)));
		}
	}

	private static string? ExtractSelectionWarningValue(string warning)
	{
		var separator = warning.LastIndexOf(": ", StringComparison.Ordinal);
		return separator >= 0 && separator + 2 < warning.Length
			? warning[(separator + 2)..]
			: null;
	}

	private static IReadOnlyList<string> NormalizeRelativeSelectionForOutput(
		string sourceRoot,
		IReadOnlySet<string> selectedFullPaths,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (selectedFullPaths.Count == 0)
			return [];

		var normalizedPaths = new List<string>(selectedFullPaths.Count);
		foreach (var selectedPath in selectedFullPaths)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var relativePath = Path.GetRelativePath(sourceRoot, selectedPath);
			normalizedPaths.Add(relativePath == "." ? "." : NormalizePathSeparators(relativePath));
		}

		CancellationAwareSort.Sort(normalizedPaths, StringComparer.Ordinal, cancellationToken);
		return normalizedPaths;
	}

	private static string BuildFingerprint(
		string sourceRoot,
		ProjectSelectionSpec selection,
		IReadOnlyList<TreeNodeDescriptor> includedNodes,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		Append(selection.GitMode!.Value.ToString());
		foreach (var exclusion in selection.Exclusions!.OrderBy(static value => value))
		{
			cancellationToken.ThrowIfCancellationRequested();
			Append(exclusion.ToString());
		}
		Append($"hide-secrets:{selection.HideSecrets == true}");
		Append($"hide-private-data:{selection.HidePrivateData == true}");
		Append($"compress-code:{selection.CompressCode == true}");
		Append($"strip-comments:{selection.StripComments == true}");
		Append($"strip-blank-lines:{selection.StripBlankLines == true}");
		foreach (var root in selection.Roots ?? [])
		{
			cancellationToken.ThrowIfCancellationRequested();
			Append("r:" + root);
		}
		foreach (var extension in selection.Extensions ?? [])
		{
			cancellationToken.ThrowIfCancellationRequested();
			Append("e:" + extension);
		}
		var orderedNodes = new List<TreeNodeDescriptor>(includedNodes.Count);
		foreach (var node in includedNodes)
		{
			cancellationToken.ThrowIfCancellationRequested();
			orderedNodes.Add(node);
		}
		CancellationAwareSort.Sort(
			orderedNodes,
			(left, right) => PathComparer.Default.Compare(left.FullPath, right.FullPath),
			cancellationToken);
		foreach (var node in orderedNodes)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var relativePath = Path.GetRelativePath(sourceRoot, node.FullPath);
			Append((node.IsDirectory ? "d:" : "f:") + NormalizePathSeparators(relativePath));
		}

		return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

		void Append(string value)
		{
			var byteCount = Encoding.UTF8.GetByteCount(value);
			if (byteCount <= FingerprintStackBufferBytes)
			{
				Span<byte> bytes = stackalloc byte[byteCount];
				Encoding.UTF8.GetBytes(value, bytes);
				hash.AppendData(bytes);
			}
			else
			{
				var rented = ArrayPool<byte>.Shared.Rent(byteCount);
				try
				{
					var written = Encoding.UTF8.GetBytes(value, rented);
					hash.AppendData(rented.AsSpan(0, written));
				}
				finally
				{
					ArrayPool<byte>.Shared.Return(rented, clearArray: true);
				}
			}

			hash.AppendData(FingerprintValueSeparator);
		}
	}

	private static HashSet<string> BuildIncludedPathSet(
		IReadOnlyList<TreeNodeDescriptor> includedNodes,
		CancellationToken cancellationToken)
	{
		var includedPaths = new HashSet<string>(PathComparer.Default);
		foreach (var node in includedNodes)
		{
			cancellationToken.ThrowIfCancellationRequested();
			includedPaths.Add(node.FullPath);
		}

		return includedPaths;
	}

	private static string[] BuildOrderedIncludedFolders(
		IReadOnlyList<TreeNodeDescriptor> includedNodes,
		CancellationToken cancellationToken)
	{
		var includedFolders = new List<string>();
		foreach (var node in includedNodes)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (node.IsDirectory)
				includedFolders.Add(node.FullPath);
		}

		CancellationAwareSort.Sort(includedFolders, PathComparer.Default, cancellationToken);
		return includedFolders.ToArray();
	}
}
