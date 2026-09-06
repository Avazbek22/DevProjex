using DevProjex.Terminal.Execution;
using DevProjex.Terminal.CommandLine;
using DevProjex.Terminal.DesktopControl;
using DevProjex.Application.Compression;
using DevProjex.Application.Secrets;
using DevProjex.Application.Selection;

namespace DevProjex.Terminal.Tui;

internal sealed record TerminalStructuralRefreshRequest(
	string SourceRoot,
	ProjectSelectionSpec Selection,
	IReadOnlySet<string> PreviousExtensions,
	IReadOnlySet<string> PreviousPaths,
	IReadOnlyCollection<string>? SelectedPathFrontier,
	IReadOnlyDictionary<string, bool> ExtensionOptionStates,
	IReadOnlyDictionary<string, bool> PathOptionStates,
	GitFilteringMode? FallbackGitMode);

internal sealed record TerminalStructuralRefreshResult(
	ProjectContextPlan Plan,
	IReadOnlyDictionary<string, bool> ExtensionOptionStates,
	IReadOnlyDictionary<string, bool> PathOptionStates,
	int PlanBuildCount);

public sealed class TerminalWorkspaceController(
	TerminalServices services,
	ITerminalEnvironment environment)
{
	internal const long MaximumClipboardPayloadBytes = 256L * 1024 * 1024;
	private const string TrackedIndexUnavailableCode = "DPX-GIT-TRACKED-INDEX-UNAVAILABLE";
	private static readonly ProjectContextDocumentLimits PreviewLimits = new();

	public async Task<TerminalWorkspaceState> OpenAsync(
		string projectPath,
		ProjectProfileReference profile,
		CancellationToken cancellationToken,
		ProjectSourceIdentity? sourceIdentity = null)
	{
		var selection = await services.SelectionResolver
			.ResolveAsync(projectPath, profile, new ProjectSelectionSpec(), cancellationToken)
			.ConfigureAwait(false);
		var plan = await BuildPlanAsync(
				projectPath,
				selection,
				sourceIdentity,
				cancellationToken)
			.ConfigureAwait(false);
		ThrowIfTrackedModeIsUnavailable(plan);
		return new TerminalWorkspaceState(
			plan,
			services.ContextPlanner.GetSelectedRelativePathFrontier(plan));
	}

	public async Task RebuildAsync(
		TerminalWorkspaceState state,
		ProjectSelectionSpec selection,
		CancellationToken cancellationToken)
	{
		var plan = await BuildPlanAsync(
				state.Plan.SourceRoot,
				selection,
				state.Plan.SourceIdentity,
				cancellationToken)
			.ConfigureAwait(false);
		state.ReplacePlan(plan);
	}

	public Task RebuildRepositoryAsync(
		TerminalWorkspaceState state,
		ProjectSelectionSpec selection,
		CancellationToken cancellationToken) =>
		ReconcileAndApplyProjectStructureAsync(state, selection, cancellationToken);

	public Task RefreshProjectAsync(
		TerminalWorkspaceState state,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(state);
		return ReconcileAndApplyProjectStructureAsync(
			state,
			state.BuildSelection(),
			cancellationToken);
	}

	private async Task ReconcileAndApplyProjectStructureAsync(
		TerminalWorkspaceState state,
		ProjectSelectionSpec selection,
		CancellationToken cancellationToken)
	{
		var request = CaptureStructuralRefresh(
			state,
			selection,
			ResolveDefaultFallbackGitMode(selection));
		var result = await BuildStructuralRefreshAsync(request, cancellationToken)
			.ConfigureAwait(false);
		ApplyStructuralRefresh(state, result);
	}

	internal TerminalStructuralRefreshRequest CaptureStructuralRefresh(
		TerminalWorkspaceState state,
		ProjectSelectionSpec selection,
		GitFilteringMode fallbackGitMode = GitFilteringMode.None)
	{
		ArgumentNullException.ThrowIfNull(state);
		ArgumentNullException.ThrowIfNull(selection);
		if (!GitScopeSelection.IsPersistent(fallbackGitMode))
			throw new ArgumentOutOfRangeException(nameof(fallbackGitMode), fallbackGitMode, null);
		var selectedItems = state.BuildSelectedItemRelativePaths();
		return new TerminalStructuralRefreshRequest(
			state.Plan.SourceRoot,
			selection,
			new HashSet<string>(
				selection.Extensions ?? state.Plan.SelectedExtensions,
				StringComparer.OrdinalIgnoreCase),
			selectedItems,
			state.BuildSelectedPathFrontier(),
			new Dictionary<string, bool>(
				state.ExtensionOptionStates,
				StringComparer.OrdinalIgnoreCase),
			ClonePathOptionStates(state.PathOptionStates),
			fallbackGitMode);
	}

	internal static Dictionary<string, bool> ClonePathOptionStates(
		IReadOnlyDictionary<string, bool> pathOptionStates)
	{
		ArgumentNullException.ThrowIfNull(pathOptionStates);
		return new Dictionary<string, bool>(
			pathOptionStates,
			ProjectTreePathIdentity.CanonicalComparer);
	}

	internal static SelectionEvolutionResult<string> ReconcilePathSelection(
		IEnumerable<string> availablePaths,
		IReadOnlySet<string> previousPaths,
		IReadOnlyDictionary<string, bool> pathOptionStates,
		IReadOnlyCollection<string>? selectedPathFrontier = null)
	{
		var normalizedFrontier = selectedPathFrontier?
			.Select(ProjectSelectionPath.NormalizeRelative)
			.ToArray();
		return SelectionEvolutionPolicy.Reconcile(
			availablePaths,
			previousPaths,
			pathOptionStates,
			path => IsInsideSelectedPathFrontier(path, normalizedFrontier),
			ProjectTreePathIdentity.CanonicalComparer);
	}

	private static bool IsInsideSelectedPathFrontier(
		string path,
		IReadOnlyCollection<string>? selectedPathFrontier)
	{
		if (selectedPathFrontier is null)
			return true;

		var normalizedPath = ProjectSelectionPath.NormalizeRelative(path);
		foreach (var selectedPath in selectedPathFrontier)
		{
			if (selectedPath.Length == 0 ||
			    string.Equals(normalizedPath, selectedPath, StringComparison.Ordinal) ||
			    normalizedPath.StartsWith(selectedPath + '/', StringComparison.Ordinal))
			{
				return true;
			}
		}

		return false;
	}

	internal async Task<TerminalStructuralRefreshResult> BuildStructuralRefreshAsync(
		TerminalStructuralRefreshRequest request,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		// Filesystem and repository changes can expose new roots, extensions, and paths.
		// Discover without the tree projection, then apply the shared evolution policy.
		services.IgnoreRulesService.RevalidateCaches(request.SourceRoot, cancellationToken);
		var sourceIdentity = await services.SourceIdentityResolver
			.ResolveAsync(request.SourceRoot, cancellationToken: cancellationToken)
			.ConfigureAwait(false);
		return await BuildReconciledStructuralPlanAsync(
				request,
				sourceIdentity,
				cancellationToken)
			.ConfigureAwait(false);
	}

	private async Task<TerminalStructuralRefreshResult> BuildReconciledStructuralPlanAsync(
		TerminalStructuralRefreshRequest request,
		ProjectSourceIdentity? sourceIdentity,
		CancellationToken cancellationToken)
	{
		var buildCount = 0;
		var repositoryScopeFullPaths = ResolveRepositoryScopeFullPaths(
			request.SourceRoot,
			request.SelectedPathFrontier);
		var discoverySelection = request.Selection with
		{
			Roots = ShouldPreserveRootsDuringDiscovery(request.Selection)
				? request.Selection.Roots
				: null,
			Extensions = null,
			SelectedPaths = request.SelectedPathFrontier is { Count: 0 }
				? []
				: null
		};
		var discovered = await BuildPlanAsync(
				request.SourceRoot,
				discoverySelection,
				sourceIdentity,
				cancellationToken,
				request.ExtensionOptionStates,
				repositoryScopeFullPaths)
			.ConfigureAwait(false);
		buildCount++;
		if (request.FallbackGitMode is { } requestedFallbackMode &&
		    GitScopeSelection.IsMomentary(discovered.Selection.GitMode!.Value) &&
		    !discovered.GitReadiness.HasRepositoryBoundary)
		{
			var fallbackMode = requestedFallbackMode == GitFilteringMode.TrackedFilesOnly
				? GitFilteringMode.None
				: requestedFallbackMode;
			discoverySelection = GitScopeSelection.WithMode(discoverySelection, fallbackMode);
			discovered = await BuildPlanAsync(
					request.SourceRoot,
					discoverySelection,
					sourceIdentity,
					cancellationToken,
					request.ExtensionOptionStates,
					repositoryScopeFullPaths)
				.ConfigureAwait(false);
			buildCount++;
		}
		ThrowIfTrackedModeIsUnavailable(discovered);

		var extensionEvolution = SelectionEvolutionPolicy.Reconcile(
			discovered.AvailableExtensions,
			request.PreviousExtensions,
			request.ExtensionOptionStates,
			static _ => true,
			StringComparer.OrdinalIgnoreCase);
		var selectedExtensions = extensionEvolution.SelectedItems
			.Order(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		var selection = discovered.Selection with
		{
			Extensions = selectedExtensions,
			SelectedPaths = discoverySelection.SelectedPaths
		};
		var availableExtensionSet = discovered.AvailableExtensions
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var selectedAvailableExtensions = discovered.SelectedExtensions
			.Where(availableExtensionSet.Contains)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		if (!selectedAvailableExtensions.SetEquals(extensionEvolution.SelectedItems))
		{
			discovered = await BuildPlanAsync(
					request.SourceRoot,
					selection,
					sourceIdentity,
					cancellationToken,
					extensionEvolution.KnownStates,
					repositoryScopeFullPaths)
				.ConfigureAwait(false);
			buildCount++;
			ThrowIfTrackedModeIsUnavailable(discovered);
		}

		var availablePaths = TerminalWorkspaceState.BuildSelectableRelativePaths(
			discovered.EffectiveTree,
			discovered.SourceRoot);
		var pathEvolution = ReconcilePathSelection(
			availablePaths,
			request.PreviousPaths,
			request.PathOptionStates,
			request.SelectedPathFrontier);
		var plan = discovered;
		if (pathEvolution.SelectedItems.Count == 0 &&
		    request.SelectedPathFrontier is not null)
		{
			plan = await services.ContextPlanner
				.ReprojectEmptySelectionAsync(discovered, cancellationToken)
				.ConfigureAwait(false);
		}
		else if (pathEvolution.SelectedItems.Count < availablePaths.Count)
		{
			plan = await services.ContextPlanner
				.ReprojectSelectionAsync(
					discovered,
					pathEvolution.SelectedItems
						.Order(StringComparer.Ordinal)
						.ToArray(),
					cancellationToken)
				.ConfigureAwait(false);
		}
		return new TerminalStructuralRefreshResult(
			plan,
			extensionEvolution.KnownStates,
			pathEvolution.KnownStates,
			buildCount);
	}

	private static bool ShouldPreserveRootsDuringDiscovery(ProjectSelectionSpec selection) =>
		selection.ProfileSource?.Kind == ProjectProfileSourceKind.Local ||
		selection.ApplicationIntent?.Roots == ProjectSelectionApplicationMode.ApplyResolvedValue;

	private static GitFilteringMode ResolveDefaultFallbackGitMode(ProjectSelectionSpec selection)
	{
		var mode = selection.GitMode ?? GitFilteringMode.None;
		if (GitScopeSelection.IsPersistent(mode))
			return mode;
		return GitScopeSelection.ToUnderlayMode(mode) == GitFilteringMode.RespectGitIgnore
			? GitFilteringMode.RespectGitIgnore
			: GitFilteringMode.None;
	}

	internal static void ApplyStructuralRefresh(
		TerminalWorkspaceState state,
		TerminalStructuralRefreshResult result)
	{
		ArgumentNullException.ThrowIfNull(state);
		ArgumentNullException.ThrowIfNull(result);
		state.ReplacePlan(result.Plan, result.ExtensionOptionStates, result.PathOptionStates);
	}

	public async Task ReprojectSelectionAsync(
		TerminalWorkspaceState state,
		CancellationToken cancellationToken)
	{
		var plan = await BuildReprojectedPlanAsync(
				state.Plan,
				state.BuildSelectedRelativePaths(),
				state.IsEffectiveRootUnchecked,
				cancellationToken)
			.ConfigureAwait(false);
		state.ReplacePlan(plan);
	}

	public Task<ProjectContextPlan> BuildReprojectedPlanAsync(
		ProjectContextPlan plan,
		IReadOnlyList<string> selectedPaths,
		bool forceEmptySelection,
		CancellationToken cancellationToken) =>
		forceEmptySelection
			? services.ContextPlanner.ReprojectEmptySelectionAsync(plan, cancellationToken)
			: services.ContextPlanner.ReprojectSelectionAsync(
				plan,
				selectedPaths,
				cancellationToken);

	public async Task SetGitModeAsync(
		TerminalWorkspaceState state,
		GitFilteringMode mode,
		CancellationToken cancellationToken)
		=> await SetPathFilteringAsync(
				state,
				mode,
				state.Plan.Selection.Exclusions ?? [],
				cancellationToken)
			.ConfigureAwait(false);

	public async Task SetPathFilteringAsync(
		TerminalWorkspaceState state,
		GitFilteringMode mode,
		IReadOnlyCollection<ProjectExclusion> exclusions,
		CancellationToken cancellationToken)
	{
		var selection = BuildPathFilteringSelection(
			state.BuildSelection(),
			mode,
			exclusions);
		var result = await BuildSettingsPlanAsync(
				state.Plan,
				selection,
				state.ExtensionOptionStates,
				state.BuildSelectedItemRelativePaths(),
				state.PathOptionStates,
				state.BuildSelectedPathFrontier(),
				ResolveDefaultFallbackGitMode(selection),
				cancellationToken)
			.ConfigureAwait(false);
		ApplySettingsPlan(state, result);
	}

	internal static ProjectSelectionSpec BuildPathFilteringSelection(
		ProjectSelectionSpec selection,
		GitFilteringMode mode,
		IReadOnlyCollection<ProjectExclusion> exclusions) =>
		GitScopeSelection.WithMode(selection, mode, selection.GitDiffRange) with
		{
			Exclusions = exclusions
		};

	public async Task SetExclusionsAsync(
		TerminalWorkspaceState state,
		IReadOnlyCollection<ProjectExclusion> exclusions,
		CancellationToken cancellationToken)
	{
		await SetPathFilteringAsync(
				state,
				state.Plan.GitReadiness.Mode,
				exclusions,
				cancellationToken)
			.ConfigureAwait(false);
	}

	public void SetHideSecrets(
		TerminalWorkspaceState state,
		bool enabled,
		CancellationToken cancellationToken)
		=> SetContentTransformation(
			state,
			IgnoreOptionId.HideSecrets,
			enabled,
			cancellationToken);

	public void SetContentTransformation(
		TerminalWorkspaceState state,
		IgnoreOptionId optionId,
		bool enabled,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var selection = state.Plan.Selection;
		var plan = services.ContextPlanner.ApplyContentTransformationSelectionWithCancellation(
			state.Plan,
			optionId == IgnoreOptionId.HideSecrets ? enabled : selection.HideSecrets == true,
			compressCode: optionId == IgnoreOptionId.CompressCode ? enabled : null,
			stripComments: optionId == IgnoreOptionId.StripComments ? enabled : null,
			stripBlankLines: optionId == IgnoreOptionId.StripBlankLines ? enabled : null,
			hidePrivateData: optionId == IgnoreOptionId.HidePrivateData ? enabled : null,
			cancellationToken: cancellationToken);
		state.ReplaceContentTransformationPlan(plan);
		if (plan.Selection.HideSecrets != true && plan.Selection.HidePrivateData != true)
			services.SecretRedactionSession.Disable();
	}

	public Task SetRootsAsync(
		TerminalWorkspaceState state,
		IReadOnlyCollection<string>? roots,
		CancellationToken cancellationToken) =>
		RebuildAsync(
			state,
			state.BuildSelection() with { Roots = roots },
			cancellationToken);

	public Task SetExtensionsAsync(
		TerminalWorkspaceState state,
		IReadOnlyCollection<string>? extensions,
		CancellationToken cancellationToken) => SetExtensionsCoreAsync(
		state,
		extensions ?? [],
		cancellationToken);

	private async Task SetExtensionsCoreAsync(
		TerminalWorkspaceState state,
		IReadOnlyCollection<string> extensions,
		CancellationToken cancellationToken)
	{
		var result = await BuildSettingsPlanAsync(
				state.Plan,
				state.BuildSelection() with { Extensions = extensions },
				state.BuildExtensionOptionStates(extensions),
				state.BuildSelectedItemRelativePaths(),
				state.PathOptionStates,
				state.BuildSelectedPathFrontier(),
				ResolveDefaultFallbackGitMode(state.Plan.Selection),
				cancellationToken)
			.ConfigureAwait(false);
		ApplySettingsPlan(state, result);
	}

	internal void ApplySettingsPlan(
		TerminalWorkspaceState state,
		TerminalSettingsPlanResult result)
	{
		ArgumentNullException.ThrowIfNull(state);
		ArgumentNullException.ThrowIfNull(result);
		var redactionWasEnabled = state.Plan.Selection.HideSecrets == true ||
		                          state.Plan.Selection.HidePrivateData == true;
		state.ReplacePlan(
			result.Plan,
			result.ExtensionOptionStates,
			result.PathOptionStates);
		if (redactionWasEnabled &&
		    result.Plan.Selection.HideSecrets != true &&
		    result.Plan.Selection.HidePrivateData != true)
		{
			services.SecretRedactionSession.Disable();
		}
	}

	internal async Task<TerminalSettingsPlanResult> BuildSettingsPlanAsync(
		ProjectContextPlan baseline,
		ProjectSelectionSpec selection,
		IReadOnlyDictionary<string, bool> extensionOptionStates,
		IReadOnlySet<string> previousPaths,
		IReadOnlyDictionary<string, bool> pathOptionStates,
		CancellationToken cancellationToken) =>
		await BuildSettingsPlanAsync(
			baseline,
			selection,
			extensionOptionStates,
			previousPaths,
			pathOptionStates,
			ResolveSelectedPathFrontier(baseline, selection),
			ResolveDefaultFallbackGitMode(selection),
			cancellationToken).ConfigureAwait(false);

	internal async Task<TerminalSettingsPlanResult> BuildSettingsPlanAsync(
		ProjectContextPlan baseline,
		ProjectSelectionSpec selection,
		IReadOnlyDictionary<string, bool> extensionOptionStates,
		IReadOnlySet<string> previousPaths,
		IReadOnlyDictionary<string, bool> pathOptionStates,
		GitFilteringMode fallbackGitMode,
		CancellationToken cancellationToken)
		=> await BuildSettingsPlanAsync(
			baseline,
			selection,
			extensionOptionStates,
			previousPaths,
			pathOptionStates,
			ResolveSelectedPathFrontier(baseline, selection),
			fallbackGitMode,
			cancellationToken).ConfigureAwait(false);

	internal async Task<TerminalSettingsPlanResult> BuildSettingsPlanAsync(
		ProjectContextPlan baseline,
		ProjectSelectionSpec selection,
		IReadOnlyDictionary<string, bool> extensionOptionStates,
		IReadOnlySet<string> previousPaths,
		IReadOnlyDictionary<string, bool> pathOptionStates,
		IReadOnlyCollection<string>? selectedPathFrontier,
		GitFilteringMode fallbackGitMode,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(baseline);
		ArgumentNullException.ThrowIfNull(selection);
		ArgumentNullException.ThrowIfNull(extensionOptionStates);
		ArgumentNullException.ThrowIfNull(previousPaths);
		ArgumentNullException.ThrowIfNull(pathOptionStates);
		if (!GitScopeSelection.IsPersistent(fallbackGitMode))
			throw new ArgumentOutOfRangeException(nameof(fallbackGitMode), fallbackGitMode, null);

		if (!RequiresStructuralRefresh(baseline, selection, extensionOptionStates))
		{
			var contentPlan = services.ContextPlanner.ApplyContentTransformationSelectionWithCancellation(
				baseline,
				selection.HideSecrets == true,
				selection.CompressCode,
				selection.StripComments,
				selection.StripBlankLines,
				selection.HidePrivateData,
				cancellationToken);
			return new TerminalSettingsPlanResult(
				contentPlan,
				new Dictionary<string, bool>(
					extensionOptionStates,
					StringComparer.OrdinalIgnoreCase),
				ClonePathOptionStates(pathOptionStates));
		}

		var request = new TerminalStructuralRefreshRequest(
			baseline.SourceRoot,
			selection,
			baseline.SelectedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase),
			previousPaths,
			selectedPathFrontier,
			extensionOptionStates,
			pathOptionStates,
			baseline.Selection.GitMode == selection.GitMode &&
			GitScopeSelection.IsMomentary(selection.GitMode ?? GitFilteringMode.None)
				? fallbackGitMode
				: null);
		var result = await BuildReconciledStructuralPlanAsync(
				request,
				baseline.SourceIdentity,
				cancellationToken)
			.ConfigureAwait(false);
		return new TerminalSettingsPlanResult(
			result.Plan,
			result.ExtensionOptionStates,
			result.PathOptionStates);
	}

	private IReadOnlyCollection<string>? ResolveSelectedPathFrontier(
		ProjectContextPlan baseline,
		ProjectSelectionSpec selection) =>
		selection.SelectedPaths switch
		{
			null => services.ContextPlanner.GetSelectedRelativePathFrontier(baseline),
			{ Count: 0 } => [],
			_ => selection.SelectedPaths.ToArray()
		};

	internal static bool RequiresStructuralRefresh(
		ProjectSelectionSpec baseline,
		ProjectSelectionSpec candidate) =>
		baseline.GitMode != candidate.GitMode ||
		!string.Equals(baseline.GitDiffRange, candidate.GitDiffRange, StringComparison.Ordinal) ||
		!SelectionSetEquals(baseline.Exclusions, candidate.Exclusions) ||
		!SelectionSetEquals(
			baseline.Extensions,
			candidate.Extensions,
			StringComparer.OrdinalIgnoreCase);

	internal static bool RequiresStructuralRefresh(
		ProjectContextPlan baseline,
		ProjectSelectionSpec candidate,
		IReadOnlyDictionary<string, bool> extensionOptionStates)
	{
		ArgumentNullException.ThrowIfNull(baseline);
		ArgumentNullException.ThrowIfNull(candidate);
		ArgumentNullException.ThrowIfNull(extensionOptionStates);
		if (RequiresStructuralRefresh(baseline.Selection, candidate))
			return true;

		var selected = baseline.SelectedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var extension in baseline.AvailableExtensions)
		{
			var currentlySelected = selected.Contains(extension);
			var requested = extensionOptionStates.TryGetValue(extension, out var isSelected)
				? isSelected
				: currentlySelected;
			if (requested != currentlySelected)
				return true;
		}

		return false;
	}

	private static bool SelectionSetEquals<T>(
		IReadOnlyCollection<T>? left,
		IReadOnlyCollection<T>? right,
		IEqualityComparer<T>? comparer = null) =>
		new HashSet<T>(left ?? [], comparer).SetEquals(right ?? []);

	public async Task<ProjectContextPlan> BuildCurrentPlanAsync(
		TerminalWorkspaceState state,
		CancellationToken cancellationToken)
	{
		var plan = await BuildReprojectedPlanAsync(
			state.Plan,
			state.BuildSelectedRelativePaths(),
			state.IsEffectiveRootUnchecked,
			cancellationToken).ConfigureAwait(false);
		state.ReplacePlan(plan);
		return plan;
	}

	public async Task RefreshPreviewAsync(
		TerminalWorkspaceState state,
		ProjectContextView view,
		ProjectContextDocumentFormat format,
		CancellationToken cancellationToken)
	{
		var preview = await services.ContextDocumentService
			.BuildAsync(
				state.Plan,
				view,
				format,
				PreviewLimits,
				cancellationToken)
			.ConfigureAwait(false);
		state.SetPreviewText(preview);
	}

	public async Task<IPreviewTextDocument> BuildPreviewDocumentAsync(
		TerminalWorkspaceState state,
		ProjectContextView view,
		ProjectContextDocumentFormat format,
		CancellationToken cancellationToken,
		bool plain = false)
		=> (await BuildPreviewDocumentWithMetricsAsync(
				state,
				view,
				format,
				cancellationToken,
				plain)
			.ConfigureAwait(false)).Document;

	public async Task<PreviewDocumentBuildResult> BuildPreviewDocumentWithMetricsAsync(
		TerminalWorkspaceState state,
		ProjectContextView view,
		ProjectContextDocumentFormat format,
		CancellationToken cancellationToken,
		bool plain = false)
	{
		ValidateView(view);
		ValidateDocumentFormat(format);
		var plan = state.Plan;
		var tree = string.Empty;
		if (view is ProjectContextView.Tree or ProjectContextView.TreeContent)
		{
			tree = plain && format == ProjectContextDocumentFormat.Text
				? services.TreeExportService.BuildFullTreePlainWithCancellation(
					plan.SourceRoot,
					plan.ProjectedTree,
					GetDisplaySource(plan),
					GetDisplayName(plan),
					includeRootPath: false,
					cancellationToken: cancellationToken)
				: services.TreeExportService.BuildFullTreeWithCancellation(
					plan.SourceRoot,
					plan.ProjectedTree,
					MapTreeFormat(format),
					GetDisplaySource(plan),
					GetDisplayName(plan),
					includeRootPath: false,
					cancellationToken: cancellationToken);
		}
		return await BuildInteractivePreviewWithMetricsAsync(
				plan,
				tree,
				view,
				cancellationToken)
			.ConfigureAwait(false);
	}

	public Task<IPreviewTextDocument> BuildExactExportDocumentAsync(
		TerminalWorkspaceState state,
		ProjectContextView view,
		ProjectContextDocumentFormat format,
		CancellationToken cancellationToken,
		bool plain = false)
		=> BuildExactExportDocumentAsync(state.Plan, view, format, cancellationToken, plain);

	private Task<IPreviewTextDocument> BuildExactExportDocumentAsync(
		ProjectContextPlan plan,
		ProjectContextView view,
		ProjectContextDocumentFormat format,
		CancellationToken cancellationToken,
		bool plain = false)
	{
		ValidateView(view);
		ValidateDocumentFormat(format);
		return services.PreviewDocumentBuilder.CreateDocumentAsync(
			(stream, token) => services.ContextDocumentService.WriteCompleteAsync(
				plan,
				view,
				format,
				stream,
				token,
				plain),
			cancellationToken);
	}

	public async Task<string?> BuildCopyPayloadAsync(
		TerminalWorkspaceState state,
		ProjectContextView view,
		ProjectContextDocumentFormat format,
		CancellationToken cancellationToken)
	{
		var plan = await BuildCurrentPlanAsync(state, cancellationToken).ConfigureAwait(false);
		using var document = await BuildExactExportDocumentAsync(
				plan,
				view,
				format,
				cancellationToken)
			.ConfigureAwait(false);
		return MaterializeCopyPayload(document);
	}

	internal static string? MaterializeCopyPayload(IPreviewTextDocument document)
	{
		ArgumentNullException.ThrowIfNull(document);
		return document.CharacterCount > MaximumClipboardPayloadBytes / sizeof(char)
			? null
			: PreviewClipboardPayloadBuilder.BuildFullDocumentPayload(document);
	}

	private async Task<PreviewDocumentBuildResult> BuildInteractivePreviewWithMetricsAsync(
		ProjectContextPlan plan,
		string tree,
		ProjectContextView view,
		CancellationToken cancellationToken)
	{
		var files = view is ProjectContextView.Content or ProjectContextView.TreeContent
			? plan.IncludedFiles
			: [];
		var transformationContext = CreateTransformationContext(plan);
		if (view == ProjectContextView.Tree && transformationContext?.Redaction is not null)
		{
			// The tree view ships no file content; this scan only measures the selection so the
			// redaction rows can show counts. Discovery keeps one unreadable file from failing
			// the whole view - the label reads the snapshot and reports coverage honestly.
			await services.SecretRedactionOutputPreparer
				.DiscoverAsync(transformationContext, plan.IncludedFiles, cancellationToken)
				.ConfigureAwait(false);
		}
		string MapDisplayPath(string path) =>
			PathUtility.GetPortableRelativePath(plan.SourceRoot, path);

		return view switch
		{
			ProjectContextView.Tree =>
				services.PreviewDocumentBuilder.CreateDocumentWithMetrics(tree),
			ProjectContextView.Content =>
				await services.PreviewDocumentBuilder
					.BuildContentDocumentWithMetricsAsync(
						files,
						cancellationToken,
						MapDisplayPath,
						includeOmissionMarkers: true,
						transformationContext: transformationContext,
						displayRootPath: GetContentDisplaySource(plan),
						projectRoot: plan.SourceRoot)
					.ConfigureAwait(false) ??
				services.PreviewDocumentBuilder.CreateDocumentWithMetrics(string.Empty),
			ProjectContextView.TreeContent => await services.PreviewDocumentBuilder
				.BuildTreeAndContentDocumentWithMetricsAsync(
					tree,
					files,
					cancellationToken,
					MapDisplayPath,
					includeOmissionMarkers: true,
					transformationContext: transformationContext,
					projectRoot: plan.SourceRoot)
				.ConfigureAwait(false),
			_ => throw new ArgumentOutOfRangeException(nameof(view), view, null)
		};
	}

	private ContentTransformationContext? CreateTransformationContext(ProjectContextPlan plan)
	{
		var kinds = CodeTransformIdentity.Resolve(
			plan.Selection.CompressCode == true,
			plan.Selection.StripComments == true,
			plan.Selection.StripBlankLines == true);
		var features = SecretRedactionFeatureSelection.Resolve(
			plan.Selection.HideSecrets == true,
			plan.Selection.HidePrivateData == true);
		return ContentTransformationContext.For(
			kinds == CodeTransformKinds.None
				? null
				: new CodeCompressionContext(plan.SourceRoot, services.CodeCompressionSession, kinds),
			features == SecretRedactionFeatures.None
				? null
				: new SecretRedactionContext(
					plan.SourceRoot,
					services.SecretRedactionSession,
					features));
	}

	private static TreeTextFormat MapTreeFormat(ProjectContextDocumentFormat format) =>
		format switch
		{
			ProjectContextDocumentFormat.Text => TreeTextFormat.Ascii,
			ProjectContextDocumentFormat.Json => TreeTextFormat.Json,
			ProjectContextDocumentFormat.Xml => TreeTextFormat.Xml,
			ProjectContextDocumentFormat.Markdown => TreeTextFormat.Markdown,
			_ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
		};

	private static string GetDisplayName(ProjectContextPlan plan) =>
		plan.SourceIdentity?.DisplayName is { Length: > 0 } name
			? name
			: Path.GetFileName(Path.TrimEndingDirectorySeparator(plan.SourceRoot));

	private static string GetDisplaySource(ProjectContextPlan plan) =>
		plan.SourceIdentity is
		{
			SourceType: ProjectSourceType.GitClone,
			SourceReference.Length: > 0
		} identity
			? identity.SourceReference
			: plan.SourceRoot;

	private static string GetContentDisplaySource(ProjectContextPlan plan)
	{
		if (plan.SourceIdentity is not
		    {
			    SourceType: ProjectSourceType.GitClone,
			    SourceReference.Length: > 0
		    } identity)
		{
			return plan.SourceRoot;
		}

		var displayRootPath = RepositoryWebPathPresentationService.NormalizeForDisplay(identity.SourceReference);
		return displayRootPath.Length > 0 ? displayRootPath : identity.SourceReference;
	}

	public async Task<string> ExportContextAsync(
		TerminalWorkspaceState state,
		ProjectContextView view,
		ProjectContextDocumentFormat format,
		string destination,
		bool overwrite,
		CancellationToken cancellationToken,
		bool plain = false)
	{
		ValidateView(view);
		ValidateDocumentFormat(format);
		var plan = await BuildCurrentPlanAsync(state, cancellationToken).ConfigureAwait(false);
		EnsureExportable(plan);
		var exactDestination = ExactOutputDestinationValidator.ValidateContext(
			plan.SourceRoot,
			destination,
			overwrite);
		var requestedDestination = Path.GetFullPath(destination);
		return await AtomicOutputWriter
			.WriteAsync(
				requestedDestination,
				overwrite,
				(stream, token) => services.ContextDocumentService.WriteCompleteAsync(
					plan,
					view,
					format,
					stream,
					token,
					plain),
				cancellationToken,
				path => ExactOutputDestinationValidator.ValidateContext(
					plan.SourceRoot,
					path,
					overwrite))
			.ConfigureAwait(false);
	}

	public async Task<TerminalExportSummary> PrepareContextExportAsync(
		TerminalWorkspaceState state,
		ProjectContextView view,
		ProjectContextDocumentFormat format,
		string destination,
		bool overwrite,
		CancellationToken cancellationToken,
		bool plain = false)
	{
		ValidateView(view);
		ValidateDocumentFormat(format);
		var plan = await BuildCurrentPlanAsync(state, cancellationToken).ConfigureAwait(false);
		EnsureExportable(plan);
		var (exactDestination, destinationState) = ResolveDestination(
			destination,
			path => ExactOutputDestinationValidator.ValidateContext(
				plan.SourceRoot,
				path,
				overwrite));
		var outputMetrics = await ExportOutputMetricsCalculator
			.FromUtf8WriterAsync(
				(stream, token) => services.ContextDocumentService.WriteCompleteAsync(
					plan,
					view,
					format,
					stream,
					token,
					plain),
				cancellationToken)
			.ConfigureAwait(false);
		return CreateSummary(
			plan,
			TerminalExportKind.Context,
			view,
			format,
			exactDestination,
			destinationState,
			outputMetrics.Chars,
			outputMetrics.Tokens);
	}

	public async Task<string> ExportProjectAsync(
		TerminalWorkspaceState state,
		ProjectCopyExportFormat format,
		string destination,
		CancellationToken cancellationToken,
		IProgress<ProjectCopyExportProgress>? progress = null) =>
		await ExportProjectAsync(
			state,
			format,
			destination,
			overwrite: false,
			cancellationToken,
			progress).ConfigureAwait(false);

	public async Task<string> ExportProjectAsync(
		TerminalWorkspaceState state,
		ProjectCopyExportFormat format,
		string destination,
		bool overwrite,
		CancellationToken cancellationToken,
		IProgress<ProjectCopyExportProgress>? progress = null)
	{
		ValidateProjectDestinationExtension(format, destination);

		var plan = await BuildCurrentPlanAsync(state, cancellationToken).ConfigureAwait(false);
		EnsureExportable(plan);
		_ = ExactOutputDestinationValidator.ValidateProject(
			plan.SourceRoot,
			destination,
			format,
			overwrite);
		var requestedDestination = Path.GetFullPath(destination);
		var result = await services.ProjectCopyExportService.ExportAsync(
				new ProjectCopyExportRequest(
					ProjectRootPath: plan.SourceRoot,
					ProjectName: plan.SourceIdentity?.DisplayName ??
								 Path.GetFileName(Path.TrimEndingDirectorySeparator(plan.SourceRoot)),
					TreeRoot: plan.ProjectedTree,
					SelectedPaths: new HashSet<string>(ProjectTreePathIdentity.CanonicalComparer),
					DestinationPath: requestedDestination,
					Format: format,
					DestinationMode: ProjectCopyDestinationMode.Exact,
					ConflictPolicy: overwrite
						? ProjectCopyConflictPolicy.ReplaceAtomically
						: ProjectCopyConflictPolicy.Fail,
					RedactSecrets: plan.Selection.HideSecrets == true,
					CompressCode: plan.Selection.CompressCode == true,
					StripComments: plan.Selection.StripComments == true,
					StripBlankLines: plan.Selection.StripBlankLines == true,
					NoticeText: ProjectCopyExportService.BuildProjectCopyNoticeText(services.Localization),
					RedactPrivateData: plan.Selection.HidePrivateData == true),
				progress,
				cancellationToken: cancellationToken)
			.ConfigureAwait(false);
		return result.DestinationPath;
	}

	public async Task<TerminalExportSummary> PrepareProjectExportAsync(
		TerminalWorkspaceState state,
		ProjectCopyExportFormat format,
		string destination,
		CancellationToken cancellationToken)
	{
		ValidateProjectDestinationExtension(format, destination);
		var plan = await BuildCurrentPlanAsync(state, cancellationToken).ConfigureAwait(false);
		EnsureExportable(plan);
		var (exactDestination, destinationState) = ResolveDestination(
			destination,
			path => ExactOutputDestinationValidator.ValidateProject(
				plan.SourceRoot,
				path,
				format,
				overwrite: false));
		return CreateSummary(
			plan,
			MapExportKind(format),
			view: null,
			documentFormat: null,
			exactDestination,
			destinationState,
			plan.Analysis.Metrics.Content.Chars,
			plan.Analysis.Metrics.Content.Tokens);
	}

	private static void EnsureExportable(ProjectContextPlan plan)
	{
		var error = plan.Diagnostics.FirstOrDefault(static diagnostic =>
			diagnostic.Severity == ContextDiagnosticSeverity.Error);
		if (error is not null)
		{
			throw new ProjectContextValidationException(
				error.Code,
				"The current project context contains a blocking diagnostic.");
		}
	}

	private Task<ProjectContextPlan> BuildPlanAsync(
		string projectPath,
		ProjectSelectionSpec selection,
		ProjectSourceIdentity? sourceIdentity,
		CancellationToken cancellationToken,
		IReadOnlyDictionary<string, bool>? knownExtensionStates = null,
		IReadOnlyCollection<string>? repositoryScopeFullPaths = null) =>
		services.ContextFactory.BuildAsync(
			projectPath,
			selection,
			sourceIdentity,
			cancellationToken,
			captureIgnoreImpactCounts: true,
			knownExtensionStates,
			repositoryScopeFullPaths);

	private static IReadOnlyList<string>? ResolveRepositoryScopeFullPaths(
		string sourceRoot,
		IReadOnlyCollection<string>? selectedRelativePaths)
	{
		if (selectedRelativePaths is null)
			return null;
		if (selectedRelativePaths.Count == 0)
			return [];

		var paths = new List<string>(selectedRelativePaths.Count);
		foreach (var path in selectedRelativePaths)
		{
			var relativePath = ProjectSelectionPath.NormalizeRelative(path);
			var fullPath = PathUtility.Normalize(Path.Combine(sourceRoot, relativePath));
			if (PathUtility.IsPathInside(fullPath, sourceRoot))
				paths.Add(fullPath);
		}
		return paths;
	}

	private static void ThrowIfTrackedModeIsUnavailable(ProjectContextPlan plan)
	{
		var diagnostic = plan.Diagnostics.FirstOrDefault(static item =>
			item.Code is TrackedIndexUnavailableCode or
				GitScopeFilter.UnavailableDiagnosticCode or
				GitScopeFilter.UnsafeFilterDiagnosticCode &&
			item.Severity == ContextDiagnosticSeverity.Error);
		if (diagnostic is not null)
		{
			throw new ProjectContextValidationException(
				diagnostic.Code,
				"The requested Git filtering mode is unavailable.");
		}
	}

	private static TerminalExportSummary CreateSummary(
		ProjectContextPlan plan,
		TerminalExportKind kind,
		ProjectContextView? view,
		ProjectContextDocumentFormat? documentFormat,
		string destination,
		TerminalExportDestinationState destinationState,
		long characters,
		long tokens) =>
		new(
			kind,
			view,
			documentFormat,
			destination,
			destinationState,
			plan.IncludedFiles.Count,
			plan.IncludedFolders.Count,
			plan.IncludedBytes,
			characters,
			tokens,
			plan.GitReadiness.Mode,
			(plan.Selection.Exclusions ?? []).OrderBy(static value => value).ToArray(),
			plan.Diagnostics.Count,
			plan.Selection.HideSecrets == true,
			plan.Selection.HidePrivateData == true,
			plan.Selection.GitDiffRange);

	private static (string Destination, TerminalExportDestinationState State) ResolveDestination(
		string destination,
		Func<string, string> validate)
	{
		var exactDestination = Path.GetFullPath(destination);
		try
		{
			_ = validate(exactDestination);
			return (exactDestination, TerminalExportDestinationState.Ready);
		}
		catch (OutputDestinationConflictException exception)
		{
			return (exception.Path, TerminalExportDestinationState.Conflict);
		}
	}

	private static void ValidateProjectDestinationExtension(
		ProjectCopyExportFormat format,
		string destination)
	{
		switch (format)
		{
			case ProjectCopyExportFormat.Folder:
				return;
			case ProjectCopyExportFormat.Zip
				when !destination.EndsWith(".zip", StringComparison.OrdinalIgnoreCase):
				throw new ProjectContextValidationException(
					"DPX-CLI-ZIP-EXTENSION-REQUIRED",
					"ZIP output must use the .zip extension.");
			case ProjectCopyExportFormat.Zip:
				return;
			default:
				throw new ArgumentOutOfRangeException(nameof(format), format, null);
		}
	}

	public async Task<string> SavePortableProfileAsync(
		TerminalWorkspaceState state,
		string destination,
		bool overwrite,
		CancellationToken cancellationToken)
	{
		var plan = await BuildCurrentPlanAsync(state, cancellationToken).ConfigureAwait(false);
		return await services.PortableProfileService
			.SaveAsync(
				plan.SourceRoot,
				destination,
				plan.Selection,
				overwrite,
				cancellationToken)
			.ConfigureAwait(false);
	}

	public Task<int> OpenDesktopAsync(
		TerminalWorkspaceState state,
		CancellationToken cancellationToken) =>
		new DesktopCommandHandler(
			environment,
			launcher: new DesktopProcessLauncher(services.HostCapabilities),
			writeOutput: false).OpenAsync(
			new DesktopOpenRequest(
				ProjectPath: state.Plan.SourceRoot,
				NewWindow: false,
				WaitForCompletion: false,
				OpenPreview: true,
				Selection: BuildDesktopSelection(state)),
			cancellationToken);

	internal static ProjectSelectionSpec BuildDesktopSelection(TerminalWorkspaceState state)
	{
		ArgumentNullException.ThrowIfNull(state);
		return state.BuildSelection() with
		{
			SelectedPaths = state.BuildPersistedSelectedRelativePaths()
		};
	}

	public static string BuildEquivalentContextCommand(
		TerminalWorkspaceState state,
		ProjectContextView view,
		ProjectContextDocumentFormat format,
		string destination,
		bool dryRun = false)
	{
		var arguments = new List<string>
		{
			"devprojex",
			"export",
			"context",
			state.Plan.SourceRoot,
			"--view",
			ToToken(view),
			"--format",
			ToToken(format),
			"-o",
			destination
		};
		AppendSelectedPaths(arguments, state);
		AppendSelection(arguments, state.Plan);
		if (dryRun)
			arguments.Add("--dry-run");
		return CliArgumentVectorFormatter.Format(arguments);
	}

	public static string BuildEquivalentProjectCommand(
		TerminalWorkspaceState state,
		ProjectCopyExportFormat format,
		string destination,
		bool dryRun = false)
	{
		var arguments = new List<string>
		{
			"devprojex",
			"export",
			"project",
			state.Plan.SourceRoot,
			"--as",
			ToToken(format),
			"-o",
			destination
		};
		AppendSelection(arguments, state.Plan);
		AppendSelectedPaths(arguments, state);
		if (dryRun)
			arguments.Add("--dry-run");
		return CliArgumentVectorFormatter.Format(arguments);
	}

	private static void AppendSelectedPaths(
		ICollection<string> arguments,
		TerminalWorkspaceState state)
	{
		var selectedPaths = state.BuildPersistedSelectedRelativePaths();
		if (selectedPaths.Count == 1 && selectedPaths[0] == ".")
			return;
		if (selectedPaths.Count == 0)
		{
			arguments.Add("--select-from");
			arguments.Add(OperatingSystem.IsWindows() ? "NUL" : "/dev/null");
			return;
		}

		foreach (var path in selectedPaths)
		{
			arguments.Add("--select");
			arguments.Add(path);
		}
	}

	private static void AppendSelection(ICollection<string> arguments, ProjectContextPlan plan)
	{
		arguments.Add("--profile");
		arguments.Add("standard");
		arguments.Add("--git-mode");
		arguments.Add(ProjectSelectionTokens.ToToken(plan.Selection));

		var exclusions = plan.Selection.Exclusions ?? [];
		if (exclusions.Count == 0)
		{
			arguments.Add("--exclude");
			arguments.Add("none");
		}
		else
		{
			foreach (var exclusion in exclusions.OrderBy(static value => value))
			{
				arguments.Add("--exclude");
				arguments.Add(ProjectSelectionTokens.ToToken(exclusion));
			}
		}
		if (plan.Selection.HideSecrets == true)
			arguments.Add("--hide-secrets");
		if (plan.Selection.HidePrivateData == true)
			arguments.Add("--hide-private-data");
		if (plan.Selection.CompressCode == true)
			arguments.Add("--compress-code");
		if (plan.Selection.StripComments == true)
			arguments.Add("--strip-comments");
		if (plan.Selection.StripBlankLines == true)
			arguments.Add("--strip-blank-lines");

		if (!SetEquals(
				plan.AvailableRoots,
				plan.SelectedRoots,
				ProjectTreePathIdentity.CanonicalComparer))
		{
			foreach (var root in plan.SelectedRoots)
			{
				arguments.Add("--root");
				arguments.Add(root);
			}
		}

		if (!SetEquals(
				plan.AvailableExtensions,
				plan.SelectedExtensions,
				StringComparer.OrdinalIgnoreCase))
		{
			foreach (var extension in plan.SelectedExtensions)
			{
				arguments.Add("--extension");
				arguments.Add(extension);
			}
		}
	}

	private static bool SetEquals(
		IReadOnlyList<string> left,
		IReadOnlyList<string> right,
		StringComparer comparer) =>
		left.Count == right.Count &&
		left.ToHashSet(comparer).SetEquals(right);

	private static string ToToken(ProjectContextView view) =>
		ProjectPresentationCatalog.Get(view).Token;

	private static string ToToken(ProjectContextDocumentFormat format) =>
		ProjectPresentationCatalog.Get(format).Token;

	private static string ToToken(ProjectCopyExportFormat format) =>
		format switch
		{
			ProjectCopyExportFormat.Folder => "folder",
			ProjectCopyExportFormat.Zip => "zip",
			_ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
		};

	private static TerminalExportKind MapExportKind(ProjectCopyExportFormat format) =>
		format switch
		{
			ProjectCopyExportFormat.Folder => TerminalExportKind.Folder,
			ProjectCopyExportFormat.Zip => TerminalExportKind.Zip,
			_ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
		};

	private static void ValidateView(ProjectContextView view) =>
		_ = ProjectPresentationCatalog.Get(view);

	private static void ValidateDocumentFormat(ProjectContextDocumentFormat format) =>
		_ = ProjectPresentationCatalog.Get(format);

}

internal sealed record TerminalSettingsPlanResult(
	ProjectContextPlan Plan,
	IReadOnlyDictionary<string, bool> ExtensionOptionStates,
	IReadOnlyDictionary<string, bool> PathOptionStates);
