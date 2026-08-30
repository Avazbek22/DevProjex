using System.Buffers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using DevProjex.Application.Selection;

namespace DevProjex.Application.Context;

public sealed class ProjectContextPlanner(ProjectAnalysisService analysisService)
{
	private const int FingerprintStackBufferBytes = 512;
	private const string InvalidSelectedPathCode = ProjectSelectionPath.InvalidPathCode;
	private const string MissingSelectedPathCode = "DPX-SELECTION-PATH-MISSING";
	private static readonly byte[] FingerprintValueSeparator = [0];
	private readonly ConditionalWeakTable<ProjectContextPlan, GitScopeProjectionContext> _gitScopeProjectionContexts = new();

	public Task<ProjectContextPlan> BuildWithIgnoreImpactCountsAsync(
		ProjectContextRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);
		return BuildCoreAsync(
			request with { CaptureIgnoreImpactCounts = true },
			includeTreeOutputMetrics: true,
			includeContentOutputMetrics: true,
			cancellationToken);
	}

	public Task<ProjectContextPlan> BuildAsync(
		ProjectContextRequest request,
		CancellationToken cancellationToken = default)
		=> BuildCoreAsync(
			request,
			includeTreeOutputMetrics: true,
			includeContentOutputMetrics: true,
			cancellationToken);

	public Task<ProjectContextPlan> BuildWithTreeMetricsAsync(
		ProjectContextRequest request,
		CancellationToken cancellationToken = default)
		=> BuildCoreAsync(
			request,
			includeTreeOutputMetrics: true,
			includeContentOutputMetrics: false,
			cancellationToken);

	public Task<ProjectContextPlan> BuildStructureAsync(
		ProjectContextRequest request,
		CancellationToken cancellationToken = default)
		=> BuildCoreAsync(
			request,
			includeTreeOutputMetrics: false,
			includeContentOutputMetrics: false,
			cancellationToken);

	private async Task<ProjectContextPlan> BuildCoreAsync(
		ProjectContextRequest request,
		bool includeTreeOutputMetrics,
		bool includeContentOutputMetrics,
		CancellationToken cancellationToken)
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
				KnownExtensionStates = request.KnownExtensionStates,
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
		var projection = ResolveSelectionProjection(
			effectiveRoot,
			selectedFullPaths,
			selectsNoEffectivePaths,
			loaded.Tree.OrderedFilePaths,
			cancellationToken);
		var projectedTree = projection.ProjectedTree;
		var includedFiles = projection.IncludedFiles;
		var includedFolders = projection.IncludedFolders;
		var effectiveFileSizes = BuildEffectiveFileSizes(
			effectiveRoot,
			loaded.Tree.OrderedFilePaths,
			loaded.TreeInventory,
			cancellationToken);
		var includedBytes = CalculateIncludedBytes(
			includedFiles,
			effectiveFileSizes,
			cancellationToken);

		var projectedLoaded = loaded with
		{
			Tree = new BuildTreeResult(
				projectedTree,
				loaded.Tree.RootAccessDenied,
				loaded.Tree.HadAccessDenied,
				includedFiles)
		};
		var analysis = await analysisService
			.BuildReportFromTreeAsync(
				projectedLoaded,
				includeTreeOutputMetrics,
				includeContentOutputMetrics,
				cancellationToken)
			.ConfigureAwait(false);
		AddAnalysisDiagnostics(analysis, diagnostics);

		var gitReadiness = ProjectContextGitReadiness.Evaluate(
			selection.GitMode!.Value,
			loaded.DiscoveredGitTrackedIndexCount,
			loaded.UnavailableGitTrackedIndexCount,
			loaded.GitEvidence.HasRepositoryBoundary);
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
			GitMode = preserveRequestedSelectionIntent ||
			          GitScopeSelection.IsMomentary(selection.GitMode!.Value)
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

		var plan = new ProjectContextPlan(
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
			Fingerprint: BuildFingerprint(
				sourceRoot,
				effectiveSelection,
				includedFiles,
				includedFolders,
				cancellationToken),
			IncludedBytes: includedBytes,
			EffectiveFileSizes: effectiveFileSizes,
			SourceIdentity: sourceIdentity,
			HasIgnoreOptionCounts: loaded.HasIgnoreOptionCounts,
			IgnoreOptionCounts: loaded.IgnoreOptionCounts,
			IgnoreControllerImpactCounts: loaded.IgnoreControllerImpactCounts)
		{
			IncludesOutputMetrics = includeTreeOutputMetrics && includeContentOutputMetrics
		};
		if (loaded.TreeInventory is not null && loaded.EffectiveRules is not null)
		{
			_gitScopeProjectionContexts.Add(
				plan,
				new GitScopeProjectionContext(
					loaded.TreeInventory,
					loaded.EffectiveRules,
					loaded.RootSelectionIsExplicit,
					loaded.EffectiveExtensionPolicy));
		}
		return plan;
	}

	internal GitScopePresentationProjection BuildGitScopePresentation(
		ProjectContextPlan plan,
		GitScopePathResult scope,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(plan);
		ArgumentNullException.ThrowIfNull(scope);
		if (!_gitScopeProjectionContexts.TryGetValue(plan, out var context))
			return GitScopePresentationProjection.Empty;

		return GitScopePresentationProjector.Build(
			plan.SourceRoot,
			context.Inventory,
			scope,
			plan.SelectedRoots.ToHashSet(PathComparer.Default),
			plan.AvailableRoots.ToHashSet(PathComparer.Default),
			context.EffectiveExtensionPolicy,
			context.EffectiveRules,
			cancellationToken,
			rootSelectionIsExplicit: context.RootSelectionIsExplicit,
			includeIgnoreImpactCounts: plan.HasIgnoreOptionCounts);
	}

	internal IReadOnlyList<string> GetGitScopeRepositoryRoots(ProjectContextPlan plan)
	{
		ArgumentNullException.ThrowIfNull(plan);
		if (!_gitScopeProjectionContexts.TryGetValue(plan, out var context))
			return [];

		return GitScopeFilter.GetDiscoveredRepositoryRoots(
			context.Inventory,
			plan.SourceRoot,
			plan.SelectedRoots,
			context.RootSelectionIsExplicit,
			plan.Selection.SelectedPaths is { Count: > 0 }
				? plan.SelectedFullPaths
				: null);
	}

	private sealed record GitScopeProjectionContext(
		ProjectTreeInventorySnapshot Inventory,
		IgnoreRules EffectiveRules,
		bool RootSelectionIsExplicit,
		IExtensionInclusionPolicy? EffectiveExtensionPolicy);

	public Task<ProjectContextPlan> ReprojectSelectionAsync(
		ProjectContextPlan baseline,
		IReadOnlyCollection<string>? selectedPaths,
		CancellationToken cancellationToken = default) =>
		ReprojectSelectionCoreAsync(
			baseline,
			selectedPaths,
			forceEmptySelection: false,
			pathComparer: null,
			cancellationToken);

	public Task<ProjectContextPlan> ReprojectSelectionAsync(
		ProjectContextPlan baseline,
		IReadOnlyCollection<string>? selectedPaths,
		StringComparer pathComparer,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(pathComparer);
		return ReprojectSelectionCoreAsync(
			baseline,
			selectedPaths,
			forceEmptySelection: false,
			pathComparer,
			cancellationToken);
	}

	public Task<ProjectContextPlan> ReprojectEmptySelectionAsync(
		ProjectContextPlan baseline,
		CancellationToken cancellationToken = default) =>
		ReprojectSelectionCoreAsync(
			baseline,
			selectedPaths: [],
			forceEmptySelection: true,
			pathComparer: null,
			cancellationToken);

	private async Task<ProjectContextPlan> ReprojectSelectionCoreAsync(
		ProjectContextPlan baseline,
		IReadOnlyCollection<string>? selectedPaths,
		bool forceEmptySelection,
		StringComparer? pathComparer,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(baseline);
		var diagnostics = baseline.Diagnostics
			.Where(static diagnostic =>
				diagnostic.Code is not MissingSelectedPathCode and not InvalidSelectedPathCode)
			.ToList();
		IReadOnlySet<string> selectedFullPaths;
		bool explicitSelectionHadMatch;
		if (forceEmptySelection)
		{
			selectedFullPaths = new HashSet<string>(pathComparer ?? PathComparer.Default);
			explicitSelectionHadMatch = false;
		}
		else
		{
			selectedFullPaths = ResolveSelectedPaths(
				baseline.EffectiveTree,
				baseline.SourceRoot,
				selectedPaths,
				diagnostics,
				cancellationToken,
				out explicitSelectionHadMatch,
				pathComparer);
		}
		var selectsNoEffectivePaths =
			forceEmptySelection ||
			(selectedPaths is { Count: > 0 } && !explicitSelectionHadMatch);
		var projection = ResolveSelectionProjection(
			baseline.EffectiveTree,
			selectedFullPaths,
			selectsNoEffectivePaths,
			knownFullTreeFilePaths: null,
			cancellationToken,
			pathComparer);
		var projectedTree = projection.ProjectedTree;
		var includedFiles = projection.IncludedFiles;
		var includedFolders = projection.IncludedFolders;
		var includedBytes = CalculateIncludedBytes(
			includedFiles,
			baseline.EffectiveFileSizes,
			cancellationToken);
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
			.BuildReportFromTreeAsync(
				reportInput,
				baseline.IncludesOutputMetrics,
				cancellationToken)
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
				includedFiles,
				includedFolders,
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
		return baseline with
		{
			Selection = selection,
			Fingerprint = BuildFingerprint(
				baseline.SourceRoot,
				selection,
				baseline.IncludedFiles,
				baseline.IncludedFolders,
				cancellationToken),
			Redaction = null,
			Privacy = null
		};
	}

	internal static IReadOnlyDictionary<string, long> BuildEffectiveFileSizes(
		TreeNodeDescriptor root,
		IReadOnlyList<string>? orderedFilePaths,
		ProjectTreeInventorySnapshot? inventory,
		CancellationToken cancellationToken)
	{
		var sizes = new Dictionary<string, long>(orderedFilePaths?.Count ?? 0, StringComparer.Ordinal);
		if (orderedFilePaths is not null)
		{
			foreach (var path in orderedFilePaths)
			{
				cancellationToken.ThrowIfCancellationRequested();
				sizes.TryAdd(path, -1);
			}
		}
		else
		{
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
		}

		var unresolvedCount = sizes.Count;
		if (inventory is not null)
		{
			foreach (var entry in inventory.Entries)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (!entry.IsDirectory &&
				    sizes.TryGetValue(entry.FullPath, out var currentLength) &&
				    currentLength < 0)
				{
					sizes[entry.FullPath] = Math.Max(0, entry.Length);
					unresolvedCount--;
				}
			}
		}
		if (unresolvedCount == 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return sizes;
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
		if (selection.GitMode == GitFilteringMode.Diff &&
		    !GitScopeSelection.IsValidDiffRange(selection.GitDiffRange))
		{
			throw new ProjectContextValidationException(
				"DPX-GIT-STATE-UNAVAILABLE",
				"The Git diff range is invalid.");
		}

		var legacyHideSecrets = selection.Exclusions.Contains(ProjectExclusion.HideSecrets);
		return selection with
		{
			GitDiffRange = selection.GitMode == GitFilteringMode.Diff
				? selection.GitDiffRange
				: null,
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
		out bool explicitSelectionHadMatch,
		StringComparer? pathComparer = null)
	{
		explicitSelectionHadMatch = false;
		var effectivePathComparer = pathComparer ?? PathComparer.Default;
		if (selectedPaths is null || selectedPaths.Count == 0)
			return new HashSet<string>(effectivePathComparer);

		var effectivePathMap = BuildEffectivePathMap(
			root,
			sourceRoot,
			cancellationToken,
			effectivePathComparer);
		var resolved = new HashSet<string>(effectivePathComparer);
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

		return NormalizeSelectionFrontier(root, resolved, cancellationToken, effectivePathComparer);
	}

	private static IReadOnlySet<string> NormalizeSelectionFrontier(
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedPaths,
		CancellationToken cancellationToken,
		StringComparer? pathComparer = null)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var effectivePathComparer = pathComparer ?? PathComparer.Default;
		if (selectedPaths.Contains(root.FullPath))
			return new HashSet<string>(effectivePathComparer);

		if (selectedPaths.Count < 2)
			return selectedPaths;

		var frontier = new HashSet<string>(selectedPaths, effectivePathComparer);
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
		CancellationToken cancellationToken,
		StringComparer? pathComparer = null)
	{
		var paths = new Dictionary<string, string>(pathComparer ?? PathComparer.Default);
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

	internal static string BuildFingerprint(
		string sourceRoot,
		ProjectSelectionSpec selection,
		IReadOnlyList<string> orderedIncludedFiles,
		IReadOnlyList<string> orderedIncludedFolders,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		Append(selection.GitMode!.Value.ToString());
		if (selection.GitDiffRange is not null)
			Append("git-diff-range:" + selection.GitDiffRange);
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
		var fileIndex = 0;
		var folderIndex = 0;
		var pathComparer = PathComparer.Default;
		// The two canonical lists merge into the same global path order as the former node sort.
		while (fileIndex < orderedIncludedFiles.Count || folderIndex < orderedIncludedFolders.Count)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var useFolder = fileIndex >= orderedIncludedFiles.Count ||
			                folderIndex < orderedIncludedFolders.Count &&
			                pathComparer.Compare(
				                orderedIncludedFolders[folderIndex],
				                orderedIncludedFiles[fileIndex]) <= 0;
			var path = useFolder
				? orderedIncludedFolders[folderIndex++]
				: orderedIncludedFiles[fileIndex++];
			var relativePath = Path.GetRelativePath(sourceRoot, path);
			Append((useFolder ? "d:" : "f:") + NormalizePathSeparators(relativePath));
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

	internal static (
		TreeNodeDescriptor ProjectedTree,
		IReadOnlyList<string> IncludedFiles,
		IReadOnlyList<string> IncludedFolders) ResolveSelectionProjection(
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedFullPaths,
		bool selectsNoEffectivePaths,
		IReadOnlyList<string>? knownFullTreeFilePaths,
		CancellationToken cancellationToken,
		StringComparer? pathComparer = null)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var effectivePathComparer = pathComparer ?? PathComparer.Default;
		if (selectsNoEffectivePaths)
			return (root with { Children = [] }, Array.Empty<string>(), Array.Empty<string>());

		if (ProjectTreeSelectionProjection.CoversWholeTree(root, selectedFullPaths))
		{
			var (fullTreeFiles, fullTreeFolders) = BuildOrderedFullTreePaths(
				root,
				knownFullTreeFilePaths,
				cancellationToken,
				effectivePathComparer);
			return (root, fullTreeFiles, fullTreeFolders);
		}

		var includedNodes = ProjectTreeSelectionProjection.BuildIncludedNodesWithCancellation(
			root,
			selectedFullPaths,
			cancellationToken,
			effectivePathComparer);
		var projectedTree = ResolveProjectedTree(
			root,
			selectedFullPaths,
			includedNodes,
			selectsNoEffectivePaths: false,
			cancellationToken,
			effectivePathComparer);
		var includedFiles = ProjectTreeSelectionProjection.BuildOrderedSelectedFilePathsWithCancellation(
			root,
			selectedFullPaths,
			ensureExists: false,
			cancellationToken,
			effectivePathComparer);
		var includedFolders = BuildOrderedIncludedFolders(
			includedNodes,
			cancellationToken,
			effectivePathComparer);
		return (projectedTree, includedFiles, includedFolders);
	}

	private static (List<string> IncludedFiles, string[] IncludedFolders) BuildOrderedFullTreePaths(
		TreeNodeDescriptor root,
		IReadOnlyList<string>? knownFilePaths,
		CancellationToken cancellationToken,
		StringComparer? pathComparer = null)
	{
		var includedFiles = knownFilePaths is null
			? []
			: new List<string>(knownFilePaths.Count);
		if (knownFilePaths is not null)
		{
			foreach (var path in knownFilePaths)
			{
				cancellationToken.ThrowIfCancellationRequested();
				includedFiles.Add(path);
			}
		}

		var includedFolders = new List<string>();
		var stack = new Stack<TreeNodeDescriptor>();
		stack.Push(root);
		while (stack.Count > 0)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var node = stack.Pop();
			if (!node.IsDirectory)
			{
				if (knownFilePaths is null)
					includedFiles.Add(node.FullPath);
				continue;
			}

			includedFolders.Add(node.FullPath);
			for (var index = node.Children.Count - 1; index >= 0; index--)
			{
				var child = node.Children[index];
				if (knownFilePaths is null || child.IsDirectory)
					stack.Push(child);
			}
		}

		SortAndDeduplicatePaths(includedFiles, cancellationToken, pathComparer);
		SortAndDeduplicatePaths(includedFolders, cancellationToken, pathComparer);
		return (includedFiles, includedFolders.ToArray());
	}

	private static void SortAndDeduplicatePaths(
		List<string> paths,
		CancellationToken cancellationToken,
		StringComparer? comparer = null)
	{
		var pathComparer = comparer ?? PathComparer.Default;
		CancellationAwareSort.Sort(paths, pathComparer, cancellationToken);
		if (paths.Count < 2)
			return;

		var writeIndex = 1;
		for (var readIndex = 1; readIndex < paths.Count; readIndex++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (pathComparer.Equals(paths[writeIndex - 1], paths[readIndex]))
				continue;

			paths[writeIndex++] = paths[readIndex];
		}

		if (writeIndex < paths.Count)
			paths.RemoveRange(writeIndex, paths.Count - writeIndex);
	}

	private static HashSet<string> BuildIncludedPathSet(
		IReadOnlyList<TreeNodeDescriptor> includedNodes,
		CancellationToken cancellationToken,
		StringComparer? pathComparer = null)
	{
		var includedPaths = new HashSet<string>(pathComparer ?? PathComparer.Default);
		foreach (var node in includedNodes)
		{
			cancellationToken.ThrowIfCancellationRequested();
			includedPaths.Add(node.FullPath);
		}

		return includedPaths;
	}

	internal static TreeNodeDescriptor ResolveProjectedTree(
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedFullPaths,
		IReadOnlyList<TreeNodeDescriptor> includedNodes,
		bool selectsNoEffectivePaths,
		CancellationToken cancellationToken,
		StringComparer? pathComparer = null)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (selectsNoEffectivePaths)
			return root with { Children = [] };
		if (ProjectTreeSelectionProjection.CoversWholeTree(root, selectedFullPaths))
			return root;

		var includedPathSet = BuildIncludedPathSet(includedNodes, cancellationToken, pathComparer);
		return ProjectTreeSelectionProjection.BuildProjectedTreeWithCancellation(
			       root,
			       includedPathSet,
			       cancellationToken) ??
		       root with { Children = [] };
	}

	private static string[] BuildOrderedIncludedFolders(
		IReadOnlyList<TreeNodeDescriptor> includedNodes,
		CancellationToken cancellationToken,
		StringComparer? pathComparer = null)
	{
		var includedFolders = new List<string>();
		foreach (var node in includedNodes)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (node.IsDirectory)
				includedFolders.Add(node.FullPath);
		}

		CancellationAwareSort.Sort(includedFolders, pathComparer ?? PathComparer.Default, cancellationToken);
		return includedFolders.ToArray();
	}
}
