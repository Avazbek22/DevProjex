using System.Security.Cryptography;
using DevProjex.Application.Selection;

namespace DevProjex.Application.Context;

public sealed class ProjectContextPlanner(ProjectAnalysisService analysisService)
{
	private const string InvalidSelectedPathCode = ProjectSelectionPath.InvalidPathCode;
	private const string MissingSelectedPathCode = "DPX-SELECTION-PATH-MISSING";

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
				LocalProfileState = selection.LocalProfileState
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
			out var explicitSelectionHadMatch);
		var selectsNoEffectivePaths =
			selection.SelectedPaths is { Count: > 0 } &&
			!explicitSelectionHadMatch;
		var includedNodes = selectsNoEffectivePaths
			? []
			: ProjectTreeSelectionProjection.BuildIncludedNodes(
				effectiveRoot,
				selectedFullPaths);
		var includedPathSet = includedNodes
			.Select(static node => node.FullPath)
			.ToHashSet(PathComparer.Default);
		var projectedTree = selectsNoEffectivePaths
			? loaded.Tree.Root with { Children = [] }
			: BuildProjectedTree(effectiveRoot, includedPathSet) ??
			  effectiveRoot with { Children = [] };
		var includedFiles = selectsNoEffectivePaths
			? []
			: ProjectTreeSelectionProjection.BuildOrderedSelectedFilePaths(
				effectiveRoot,
				selectedFullPaths,
				ensureExists: false);
		var effectiveFileSizes = BuildEffectiveFileSizes(
			effectiveRoot,
			loaded.TreeInventory,
			cancellationToken);
		var includedBytes = CalculateIncludedBytes(
			includedFiles,
			effectiveFileSizes,
			cancellationToken);
		var includedFolders = includedNodes
			.Where(static node => node.IsDirectory)
			.Select(static node => node.FullPath)
			.OrderBy(static path => path, PathComparer.Default)
			.ToArray();

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
		var effectiveSelection = selection with
		{
			// Selection is durable user intent; SelectedRoots/SelectedExtensions below are
			// the effective rows that exist now. Keeping them separate preserves stale-profile
			// diagnostics and hidden checkbox state without feeding phantom names to the tree.
			Roots = ResolveRootSelectionIntent(selection, refreshedLocalState, loaded),
			Extensions = ResolveExtensionSelectionIntent(selection, refreshedLocalState, loaded),
			GitMode = ResolveGitModeIntent(selection, refreshedLocalState, loaded),
			Exclusions = ResolveExclusionIntent(selection, refreshedLocalState, loaded),
			SelectedPaths = NormalizeRelativeSelectionForOutput(
				sourceRoot,
				selectedFullPaths),
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
			Fingerprint: BuildFingerprint(sourceRoot, effectiveSelection, includedNodes),
			IncludedBytes: includedBytes,
			EffectiveFileSizes: effectiveFileSizes,
			SourceIdentity: sourceIdentity);
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
			out var explicitSelectionHadMatch);
		var selectsNoEffectivePaths =
			selectedPaths is { Count: > 0 } &&
			!explicitSelectionHadMatch;
		var includedNodes = selectsNoEffectivePaths
			? []
			: ProjectTreeSelectionProjection.BuildIncludedNodes(
				baseline.EffectiveTree,
				selectedFullPaths);
		var includedPathSet = includedNodes
			.Select(static node => node.FullPath)
			.ToHashSet(PathComparer.Default);
		var projectedTree = selectsNoEffectivePaths
			? baseline.EffectiveTree with { Children = [] }
			: BuildProjectedTree(baseline.EffectiveTree, includedPathSet) ??
			  baseline.EffectiveTree with { Children = [] };
		var includedFiles = selectsNoEffectivePaths
			? []
			: ProjectTreeSelectionProjection.BuildOrderedSelectedFilePaths(
				baseline.EffectiveTree,
				selectedFullPaths,
				ensureExists: false);
		var includedBytes = CalculateIncludedBytes(
			includedFiles,
			baseline.EffectiveFileSizes,
			cancellationToken);
		var includedFolders = includedNodes
			.Where(static node => node.IsDirectory)
			.Select(static node => node.FullPath)
			.OrderBy(static path => path, PathComparer.Default)
			.ToArray();
		var selection = baseline.Selection with
		{
			SelectedPaths = NormalizeRelativeSelectionForOutput(
				baseline.SourceRoot,
				selectedFullPaths)
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
				includedNodes),
			IncludedBytes = includedBytes
		};
	}

	/// <summary>
	/// Applies content-only exclusions without touching discovery, ignore rules, or the tree.
	/// Hide Secrets is deliberately the only exclusion allowed through this path.
	/// </summary>
	public ProjectContextPlan ApplyContentTransformationSelection(
		ProjectContextPlan baseline,
		IReadOnlyCollection<ProjectExclusion> exclusions)
	{
		ArgumentNullException.ThrowIfNull(baseline);
		ArgumentNullException.ThrowIfNull(exclusions);
		var currentPathExclusions = (baseline.Selection.Exclusions ?? [])
			.Where(static exclusion => exclusion != ProjectExclusion.HideSecrets)
			.ToHashSet();
		var requestedPathExclusions = exclusions
			.Where(static exclusion => exclusion != ProjectExclusion.HideSecrets)
			.ToHashSet();
		if (!currentPathExclusions.SetEquals(requestedPathExclusions))
		{
			throw new ArgumentException(
				"Content-only selection updates cannot change path exclusions.",
				nameof(exclusions));
		}

		var selection = baseline.Selection with { Exclusions = exclusions.ToArray() };
		var includedNodes = ProjectTreeSelectionProjection.BuildIncludedNodes(
			baseline.EffectiveTree,
			baseline.SelectedFullPaths);
		return baseline with
		{
			Selection = selection,
			Fingerprint = BuildFingerprint(baseline.SourceRoot, selection, includedNodes),
			Redaction = null
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
		if (string.IsNullOrWhiteSpace(path))
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
		if (string.IsNullOrWhiteSpace(fallbackName))
			fallbackName = sourceRoot;

		if (sourceIdentity is null)
		{
			return new ProjectSourceIdentity(
				fallbackName,
				ProjectSourceType.LocalFolder,
				sourceRoot);
		}

		return sourceIdentity with
		{
			DisplayName = string.IsNullOrWhiteSpace(sourceIdentity.DisplayName)
				? fallbackName
				: sourceIdentity.DisplayName.Trim(),
			SourceReference = string.IsNullOrWhiteSpace(sourceIdentity.SourceReference)
				? sourceRoot
				: sourceIdentity.SourceReference
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

		return selection;
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
				                          EqualityComparer<ProjectExclusion>.Default)
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
		out bool explicitSelectionHadMatch)
	{
		explicitSelectionHadMatch = false;
		if (selectedPaths is null || selectedPaths.Count == 0)
			return new HashSet<string>(PathComparer.Default);

		var effectivePathMap = BuildEffectivePathMap(root, sourceRoot);
		var resolved = new HashSet<string>(PathComparer.Default);
		foreach (var input in selectedPaths)
		{
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

		return NormalizeSelectionFrontier(root, resolved);
	}

	private static IReadOnlySet<string> NormalizeSelectionFrontier(
		TreeNodeDescriptor root,
		IReadOnlySet<string> selectedPaths)
	{
		if (selectedPaths.Contains(root.FullPath))
			return new HashSet<string>(PathComparer.Default);

		if (selectedPaths.Count < 2)
			return selectedPaths;

		var frontier = new HashSet<string>(selectedPaths, PathComparer.Default);
		foreach (var selectedPath in selectedPaths)
		{
			var parent = Directory.GetParent(selectedPath);
			while (parent is not null)
			{
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
		string sourceRoot)
	{
		var paths = new Dictionary<string, string>(PathComparer.Default);
		var stack = new Stack<TreeNodeDescriptor>();
		stack.Push(root);
		while (stack.Count > 0)
		{
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
		path.Replace('\\', '/');

	private static TreeNodeDescriptor? BuildProjectedTree(
		TreeNodeDescriptor node,
		IReadOnlySet<string> includedPaths)
	{
		if (!includedPaths.Contains(node.FullPath))
			return null;

		if (!node.IsDirectory || node.Children.Count == 0)
			return node;

		var children = new List<TreeNodeDescriptor>();
		foreach (var child in node.Children)
		{
			var projected = BuildProjectedTree(child, includedPaths);
			if (projected is not null)
				children.Add(projected);
		}

		return node with { Children = children };
	}

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
				warning));
		}
	}

	private static IReadOnlyList<string> NormalizeRelativeSelectionForOutput(
		string sourceRoot,
		IReadOnlySet<string> selectedFullPaths)
	{
		if (selectedFullPaths.Count == 0)
			return [];

		return selectedFullPaths
			.Select(path => Path.GetRelativePath(sourceRoot, path))
			.Select(static path => path == "." ? "." : NormalizePathSeparators(path))
			.OrderBy(static path => path, StringComparer.Ordinal)
			.ToArray();
	}

	private static string BuildFingerprint(
		string sourceRoot,
		ProjectSelectionSpec selection,
		IReadOnlyList<TreeNodeDescriptor> includedNodes)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		Append(selection.GitMode!.Value.ToString());
		foreach (var exclusion in selection.Exclusions!.OrderBy(static value => value))
			Append(exclusion.ToString());
		foreach (var root in selection.Roots ?? [])
			Append("r:" + root);
		foreach (var extension in selection.Extensions ?? [])
			Append("e:" + extension);
		foreach (var node in includedNodes.OrderBy(static node => node.FullPath, PathComparer.Default))
		{
			var relativePath = Path.GetRelativePath(sourceRoot, node.FullPath);
			Append((node.IsDirectory ? "d:" : "f:") + NormalizePathSeparators(relativePath));
		}

		return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

		void Append(string value)
		{
			var bytes = Encoding.UTF8.GetBytes(value);
			hash.AppendData(bytes);
			hash.AppendData([0]);
		}
	}
}
