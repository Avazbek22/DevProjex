using DevProjex.Terminal.Execution;
using DevProjex.Terminal.CommandLine;

namespace DevProjex.Terminal.Tui;

public sealed class TerminalWorkspaceController(
	TerminalServices services,
	ITerminalEnvironment environment)
{
	private static readonly ProjectContextDocumentLimits PreviewLimits = new();

	public async Task<TerminalWorkspaceState> OpenAsync(
		string projectPath,
		ProjectProfileReference profile,
		CancellationToken cancellationToken)
	{
		var selection = await services.SelectionResolver
			.ResolveAsync(projectPath, profile, new ProjectSelectionSpec(), cancellationToken)
			.ConfigureAwait(false);
		var plan = await services.ContextPlanner
			.BuildAsync(new ProjectContextRequest(projectPath, selection), cancellationToken)
			.ConfigureAwait(false);
		return new TerminalWorkspaceState(plan);
	}

	public async Task RebuildAsync(
		TerminalWorkspaceState state,
		ProjectSelectionSpec selection,
		CancellationToken cancellationToken)
	{
		var plan = await services.ContextPlanner
			.BuildAsync(
				new ProjectContextRequest(state.Plan.SourceRoot, selection),
				cancellationToken)
			.ConfigureAwait(false);
		state.ReplacePlan(plan);
	}

	public async Task ReprojectSelectionAsync(
		TerminalWorkspaceState state,
		CancellationToken cancellationToken)
	{
		var plan = await services.ContextPlanner
			.ReprojectSelectionAsync(
				state.Plan,
				state.BuildSelectedRelativePaths(),
				cancellationToken)
			.ConfigureAwait(false);
		state.ReplacePlan(plan);
	}

	public Task SetGitModeAsync(
		TerminalWorkspaceState state,
		GitFilteringMode mode,
		CancellationToken cancellationToken) =>
		RebuildAsync(
			state,
			state.BuildSelection() with { GitMode = mode },
			cancellationToken);

	public Task SetExclusionsAsync(
		TerminalWorkspaceState state,
		IReadOnlyCollection<ProjectExclusion> exclusions,
		CancellationToken cancellationToken) =>
		RebuildAsync(
			state,
			state.BuildSelection() with { Exclusions = exclusions },
			cancellationToken);

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
				cancellationToken,
				PreviewLimits)
			.ConfigureAwait(false);
		state.SetPreviewText(preview);
	}

	public async Task<string> ExportContextAsync(
		TerminalWorkspaceState state,
		ProjectContextView view,
		ProjectContextDocumentFormat format,
		string destination,
		bool overwrite,
		CancellationToken cancellationToken)
	{
		var plan = await BuildCurrentPlanAsync(state, cancellationToken).ConfigureAwait(false);
		EnsureExportable(plan);
		var exactDestination = ExactOutputDestinationValidator.ValidateContext(
			plan.SourceRoot,
			destination,
			overwrite);
		var payload = await services.ContextDocumentService
			.BuildAsync(plan, view, format, cancellationToken)
			.ConfigureAwait(false);
		return await AtomicOutputWriter
			.WriteTextAsync(
				exactDestination,
				payload,
				overwrite,
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
		var exactDestination = ExactOutputDestinationValidator.ValidateProject(
			plan.SourceRoot,
			destination,
			format,
			overwrite: false);
		var result = await services.ProjectCopyExportService.ExportAsync(
				new ProjectCopyExportRequest(
					ProjectRootPath: plan.SourceRoot,
					ProjectName: Path.GetFileName(Path.TrimEndingDirectorySeparator(plan.SourceRoot)),
					TreeRoot: plan.ProjectedTree,
					SelectedPaths: new HashSet<string>(PathComparer.Default),
					DestinationPath: exactDestination,
					Format: format,
					DestinationMode: ProjectCopyDestinationMode.Exact,
					ConflictPolicy: ProjectCopyConflictPolicy.Fail),
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
			format == ProjectCopyExportFormat.Zip
				? TerminalExportKind.Zip
				: TerminalExportKind.Folder,
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
			return (validate(exactDestination), TerminalExportDestinationState.Ready);
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
			_ => (
				SaturatingAdd(
					plan.Analysis.Metrics.Tree.Chars,
					plan.Analysis.Metrics.Content.Chars),
				SaturatingAdd(
					plan.Analysis.Metrics.Tree.Tokens,
					plan.Analysis.Metrics.Content.Tokens))
		};

	private static long SaturatingAdd(long left, long right) =>
		left > long.MaxValue - right ? long.MaxValue : left + right;

	private static void ValidateProjectDestinationExtension(
		ProjectCopyExportFormat format,
		string destination)
	{
		if (format == ProjectCopyExportFormat.Zip &&
		    !destination.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
		{
			throw new ProjectContextValidationException(
				"DPX-CLI-ZIP-EXTENSION-REQUIRED",
				"ZIP output must use the .zip extension.");
		}
	}

	public async Task<string> SavePortableProfileAsync(
		TerminalWorkspaceState state,
		string destination,
		bool overwrite,
		CancellationToken cancellationToken)
	{
		var plan = await BuildCurrentPlanAsync(state, cancellationToken).ConfigureAwait(false);
		await services.PortableProfileService
			.SaveAsync(destination, plan.Selection, overwrite, cancellationToken)
			.ConfigureAwait(false);
		return Path.GetFullPath(destination);
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
		ProjectContextDocumentFormat format)
	{
		var command = new StringBuilder("devprojex export context ");
		AppendQuoted(command, state.Plan.SourceRoot);
		command.Append(" --view ").Append(ToToken(view));
		command.Append(" --format ").Append(ToToken(format));
		foreach (var path in state.BuildSelectedRelativePaths())
		{
			command.Append(" --select ");
			AppendQuoted(command, path);
		}
		AppendSelection(command, state.Plan);
		return command.ToString();
	}

	public static string BuildEquivalentProjectCommand(
		TerminalWorkspaceState state,
		ProjectCopyExportFormat format,
		string destination)
	{
		var command = new StringBuilder("devprojex export project ");
		AppendQuoted(command, state.Plan.SourceRoot);
		command.Append(" --as ")
			.Append(format == ProjectCopyExportFormat.Zip ? "zip" : "folder")
			.Append(" -o ");
		AppendQuoted(command, destination);
		AppendSelection(command, state.Plan);
		foreach (var path in state.BuildSelectedRelativePaths())
		{
			command.Append(" --select ");
			AppendQuoted(command, path);
		}
		return command.ToString();
	}

	private static void AppendSelection(StringBuilder command, ProjectContextPlan plan)
	{
		command.Append(" --profile standard --git-mode ")
			.Append(ProjectSelectionTokens.ToToken(plan.GitReadiness.Mode));

		var exclusions = plan.Selection.Exclusions ?? [];
		if (exclusions.Count == 0)
		{
			command.Append(" --exclude none");
		}
		else
		{
			foreach (var exclusion in exclusions.OrderBy(static value => value))
			{
				command.Append(" --exclude ")
					.Append(ProjectSelectionTokens.ToToken(exclusion));
			}
		}

		if (!SetEquals(plan.AvailableRoots, plan.SelectedRoots, PathComparer.Default))
		{
			foreach (var root in plan.SelectedRoots)
			{
				command.Append(" --root ");
				AppendQuoted(command, root);
			}
		}

		if (!SetEquals(
			    plan.AvailableExtensions,
			    plan.SelectedExtensions,
			    StringComparer.OrdinalIgnoreCase))
		{
			foreach (var extension in plan.SelectedExtensions)
			{
				command.Append(" --extension ");
				AppendQuoted(command, extension);
			}
		}
	}

	private static bool SetEquals(
		IReadOnlyList<string> left,
		IReadOnlyList<string> right,
		StringComparer comparer) =>
		left.Count == right.Count &&
		left.ToHashSet(comparer).SetEquals(right);

	private static string ToToken(ProjectContextView view) => view switch
	{
		ProjectContextView.Tree => "tree",
		ProjectContextView.Content => "content",
		_ => "tree-content"
	};

	private static string ToToken(ProjectContextDocumentFormat format) => format switch
	{
		ProjectContextDocumentFormat.Text => "text",
		ProjectContextDocumentFormat.Json => "json",
		ProjectContextDocumentFormat.Xml => "xml",
		_ => "markdown"
	};

	private static void AppendQuoted(StringBuilder command, string value)
	{
		if (!value.Any(char.IsWhiteSpace) && !value.Contains('"'))
		{
			command.Append(value);
			return;
		}

		command.Append('"').Append(value.Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
	}
}
