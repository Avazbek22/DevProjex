using DevProjex.Terminal.Execution;
using DevProjex.Terminal.CommandLine;
using DevProjex.Application.Secrets;

namespace DevProjex.Terminal.Tui;

public sealed class TerminalWorkspaceController(
	TerminalServices services,
	ITerminalEnvironment environment)
{
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
		return new TerminalWorkspaceState(plan);
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

	public async Task RebuildRepositoryAsync(
		TerminalWorkspaceState state,
		ProjectSelectionSpec selection,
		CancellationToken cancellationToken)
	{
		// Pull and branch changes may add project markers, roots, extensions, or ignore
		// controls without changing the TUI selection itself. Revalidate the shared
		// Application cache before rebuilding so TUI, CLI, and Desktop see one topology.
		services.IgnoreRulesService.RevalidateCaches(state.Plan.SourceRoot, cancellationToken);
		var sourceIdentity = await services.SourceIdentityResolver
			.ResolveAsync(state.Plan.SourceRoot, cancellationToken: cancellationToken)
			.ConfigureAwait(false);
		var plan = await BuildPlanAsync(
				state.Plan.SourceRoot,
				selection,
				sourceIdentity,
				cancellationToken)
			.ConfigureAwait(false);
		ThrowIfTrackedModeIsUnavailable(plan);
		state.ReplacePlan(plan);
	}

	public async Task ReprojectSelectionAsync(
		TerminalWorkspaceState state,
		CancellationToken cancellationToken)
	{
		var plan = await BuildReprojectedPlanAsync(
				state.Plan,
				state.BuildSelectedRelativePaths(),
				cancellationToken)
			.ConfigureAwait(false);
		state.ReplacePlan(plan);
	}

	public Task<ProjectContextPlan> BuildReprojectedPlanAsync(
		ProjectContextPlan plan,
		IReadOnlyList<string> selectedPaths,
		CancellationToken cancellationToken) =>
		services.ContextPlanner.ReprojectSelectionAsync(
			plan,
			selectedPaths,
			cancellationToken);

	public async Task SetGitModeAsync(
		TerminalWorkspaceState state,
		GitFilteringMode mode,
		CancellationToken cancellationToken)
	{
		var plan = await BuildPlanAsync(
				state.Plan.SourceRoot,
				state.BuildSelection() with { GitMode = mode },
				state.Plan.SourceIdentity,
				cancellationToken)
			.ConfigureAwait(false);
		// Keep the last usable plan when an explicit tracked-mode request cannot be honored.
		ThrowIfTrackedModeIsUnavailable(plan);
		state.ReplacePlan(plan);
	}

	public async Task SetExclusionsAsync(
		TerminalWorkspaceState state,
		IReadOnlyCollection<ProjectExclusion> exclusions,
		CancellationToken cancellationToken)
	{
		await RebuildAsync(
				state,
				state.BuildSelection() with { Exclusions = exclusions },
				cancellationToken)
			.ConfigureAwait(false);
	}

	public void SetHideSecrets(
		TerminalWorkspaceState state,
		bool enabled,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var plan = services.ContextPlanner.ApplyContentTransformationSelection(
			state.Plan,
			enabled);
		state.ReplaceContentTransformationPlan(plan);
		if (!enabled)
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
		CancellationToken cancellationToken) =>
		RebuildAsync(
			state,
			state.BuildSelection() with { Extensions = extensions },
			cancellationToken);

	public async Task<ProjectContextPlan> BuildCurrentPlanAsync(
		TerminalWorkspaceState state,
		CancellationToken cancellationToken)
	{
		await ReprojectSelectionAsync(state, cancellationToken).ConfigureAwait(false);
		return state.Plan;
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
	{
		ValidateView(view);
		ValidateDocumentFormat(format);
		var plan = state.Plan;
		var tree = string.Empty;
		if (view is ProjectContextView.Tree or ProjectContextView.TreeContent)
		{
			tree = plain && format == ProjectContextDocumentFormat.Text
				? services.TreeExportService.BuildFullTreePlain(
					plan.SourceRoot,
					plan.ProjectedTree,
					GetDisplaySource(plan),
					GetDisplayName(plan),
					includeRootPath: false)
				: services.TreeExportService.BuildFullTree(
					plan.SourceRoot,
					plan.ProjectedTree,
					MapTreeFormat(format),
					GetDisplaySource(plan),
					GetDisplayName(plan),
					includeRootPath: false);
		}
		return await BuildInteractivePreviewAsync(
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
		CancellationToken cancellationToken)
	{
		ValidateView(view);
		ValidateDocumentFormat(format);
		return services.PreviewDocumentBuilder.CreateDocumentAsync(
			(stream, token) => services.ContextDocumentService.WriteCompleteAsync(
				state.Plan,
				view,
				format,
				stream,
				token),
			cancellationToken);
	}

	private async Task<IPreviewTextDocument> BuildInteractivePreviewAsync(
		ProjectContextPlan plan,
		string tree,
		ProjectContextView view,
		CancellationToken cancellationToken)
	{
		var files = view is ProjectContextView.Content or ProjectContextView.TreeContent
			? plan.IncludedFiles
			: [];
		var redactionContext = CreateRedactionContext(plan);
		if (view == ProjectContextView.Tree && redactionContext is not null)
		{
			await services.SecretRedactionOutputPreparer
				.AnalyzeAsync(redactionContext, plan.IncludedFiles, cancellationToken)
				.ConfigureAwait(false);
		}
		string MapDisplayPath(string path) =>
			Path.GetRelativePath(plan.SourceRoot, path).Replace('\\', '/');

		return view switch
		{
			ProjectContextView.Tree =>
				services.PreviewDocumentBuilder.CreateDocument(tree),
			ProjectContextView.Content =>
				await services.PreviewDocumentBuilder
					.BuildContentDocumentAsync(
						files,
						cancellationToken,
						MapDisplayPath,
						includeOmissionMarkers: true,
						transformationContext: redactionContext)
					.ConfigureAwait(false) ??
				services.PreviewDocumentBuilder.CreateInMemory(string.Empty),
			ProjectContextView.TreeContent => await services.PreviewDocumentBuilder
				.BuildTreeAndContentDocumentAsync(
					tree,
					files,
					cancellationToken,
					MapDisplayPath,
					includeOmissionMarkers: true,
					transformationContext: redactionContext)
				.ConfigureAwait(false),
			_ => throw new ArgumentOutOfRangeException(nameof(view), view, null)
		};
	}

	private SecretRedactionContext? CreateRedactionContext(ProjectContextPlan plan) =>
		plan.Selection.HideSecrets == true
			? new SecretRedactionContext(plan.SourceRoot, services.SecretRedactionSession)
			: null;

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
		CancellationToken cancellationToken)
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
		var (characters, tokens) = ResolveContextMetrics(plan, view);
		return CreateSummary(
			plan,
			TerminalExportKind.Context,
			view,
			format,
			exactDestination,
			destinationState,
			characters,
			tokens);
	}

	public async Task<string> ExportProjectAsync(
		TerminalWorkspaceState state,
		ProjectCopyExportFormat format,
		string destination,
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
			overwrite: false);
		var requestedDestination = Path.GetFullPath(destination);
		var result = await services.ProjectCopyExportService.ExportAsync(
				new ProjectCopyExportRequest(
					ProjectRootPath: plan.SourceRoot,
					ProjectName: plan.SourceIdentity?.DisplayName ??
					             Path.GetFileName(Path.TrimEndingDirectorySeparator(plan.SourceRoot)),
					TreeRoot: plan.ProjectedTree,
					SelectedPaths: new HashSet<string>(PathComparer.Default),
					DestinationPath: requestedDestination,
					Format: format,
					DestinationMode: ProjectCopyDestinationMode.Exact,
					ConflictPolicy: ProjectCopyConflictPolicy.Fail,
					RedactSecrets: plan.Selection.HideSecrets == true,
					CompressCode: plan.Selection.CompressCode == true,
					NoticeText: ProjectCopyExportService.BuildProjectCopyNoticeText(services.Localization)),
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
		CancellationToken cancellationToken) =>
		services.ContextFactory.BuildAsync(
			projectPath,
			selection,
			sourceIdentity,
			cancellationToken);

	private static void ThrowIfTrackedModeIsUnavailable(ProjectContextPlan plan)
	{
		var diagnostic = plan.Diagnostics.FirstOrDefault(static item =>
			item.Code == TrackedIndexUnavailableCode &&
			item.Severity == ContextDiagnosticSeverity.Error);
		if (diagnostic is not null)
		{
			throw new ProjectContextValidationException(
				diagnostic.Code,
				"Tracked Git files mode requires a readable repository index.");
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
			plan.Diagnostics.Count);

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

	private static (long Characters, long Tokens) ResolveContextMetrics(
		ProjectContextPlan plan,
		ProjectContextView view) =>
		view switch
		{
			ProjectContextView.Tree => (
				plan.Analysis.Metrics.Tree.Chars,
				plan.Analysis.Metrics.Tree.Tokens),
			ProjectContextView.Content => (
				plan.Analysis.Metrics.Content.Chars,
				plan.Analysis.Metrics.Content.Tokens),
			ProjectContextView.TreeContent => (
				SaturatingAdd(
					plan.Analysis.Metrics.Tree.Chars,
					plan.Analysis.Metrics.Content.Chars),
				SaturatingAdd(
					plan.Analysis.Metrics.Tree.Tokens,
					plan.Analysis.Metrics.Content.Tokens)),
			_ => throw new ArgumentOutOfRangeException(nameof(view), view, null)
		};

	private static long SaturatingAdd(long left, long right) =>
		left > long.MaxValue - right ? long.MaxValue : left + right;

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
		new DesktopCommandHandler(environment, writeOutput: false).OpenAsync(
			new DesktopOpenRequest(
				ProjectPath: state.Plan.SourceRoot,
				NewWindow: false,
				WaitForCompletion: false,
				OpenPreview: true,
				Selection: state.BuildSelection()),
			cancellationToken);

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
		foreach (var path in state.BuildSelectedRelativePaths())
		{
			arguments.Add("--select");
			arguments.Add(path);
		}
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
		foreach (var path in state.BuildSelectedRelativePaths())
		{
			arguments.Add("--select");
			arguments.Add(path);
		}
		if (dryRun)
			arguments.Add("--dry-run");
		return CliArgumentVectorFormatter.Format(arguments);
	}

	private static void AppendSelection(ICollection<string> arguments, ProjectContextPlan plan)
	{
		arguments.Add("--profile");
		arguments.Add("standard");
		arguments.Add("--git-mode");
		arguments.Add(ProjectSelectionTokens.ToToken(plan.GitReadiness.Mode));

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

		if (!SetEquals(plan.AvailableRoots, plan.SelectedRoots, PathComparer.Default))
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
