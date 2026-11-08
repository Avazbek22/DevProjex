using DevProjex.Application.Diff;
using DevProjex.Terminal.Execution;
using DevProjex.Terminal.Rendering;

namespace DevProjex.Terminal.Tui;

internal sealed partial class TerminalWorkspaceSession
{
	// Keeps a huge diff from stalling the panel renderer; the direct CLI command has no such
	// limit, so a reader who needs the rest is pointed at it instead of a second TUI command.
	private const int MaximumDiffPanelCharacters = 50_000;

	internal TerminalWorkspaceCommandExecutionResult ExecuteDiffCommand(
		TerminalWorkspaceCommand command)
	{
		if (_state is null ||
			!GitScopeSelection.TryParse(command.Text, out var mode, out var diffRange) ||
			!GitScopeSelection.IsMomentary(mode))
		{
			return InvalidCommandExecution();
		}

		var contextLines = command.ContextLines ?? DiffQueryRunner.DefaultContextLines;
		var state = _state;
		var persistentGitMode = _preferredGitMode;
		// Taken on the UI thread, as for :related, so the affected files are related against the
		// filters in force when the command was entered.
		var relationSnapshot = TerminalWorkspaceController.CaptureRelationSnapshot(state);
		TrackActiveOperation(RunOperationAsync(
			L("Terminal.Tui.Command.Diff.Title"),
			async token =>
			{
				await AwaitLatestSettingsRefreshAsync(token).ConfigureAwait(false);
				// The requested scope replaces any momentary scope already active rather than
				// intersecting with it: a momentary baseline is first lifted back to the persistent
				// Git mode, exactly as a related-files lookup lifts it, while checked items, file
				// types, path filters and protection carry over unchanged.
				var baseline = state.Plan;
				if (GitScopeSelection.IsMomentary(baseline.Selection.GitMode ?? GitFilteringMode.None))
				{
					var lifted = await _controller.BuildSettingsPlanAsync(
							baseline,
							state.BuildSelection() with
							{
								GitMode = persistentGitMode,
								GitDiffRange = null
							},
							state.ExtensionOptionStates,
							state.BuildSelectedItemRelativePaths(),
							state.PathOptionStates,
							state.BuildSelectedPathFrontier(),
							persistentGitMode,
							token)
						.ConfigureAwait(false);
					baseline = lifted.Plan;
				}
				var current = state.IsEffectiveRootUnchecked
					? await _services.ContextPlanner
						.ReprojectEmptySelectionWithOutputMetricsAsync(baseline, token)
						.ConfigureAwait(false)
					: await _services.ContextPlanner
						.ReprojectSelectionWithOutputMetricsAsync(baseline, state.BuildSelectedRelativePaths(), token)
						.ConfigureAwait(false);
				var plan = await DiffQueryRunner
					.ApplyScopeAsync(_services.ContextPlanner, current, mode, diffRange, token)
					.ConfigureAwait(false);
				var planDiagnostics = FormatContextDiagnostics(plan.Diagnostics);
				if (plan.HasErrors)
				{
					throw new TerminalWorkspaceOperationException(
						"DPX-TUI-DIFF-SELECTION-FAILED",
						planDiagnostics);
				}
				// A momentary diff scope is applied after the plan is built, so, unlike the CLI
				// command, resolution against the remote (for a Git URL source) only happens here.
				var resolvedDiffRange = mode == GitFilteringMode.Diff && diffRange is { } requestedDiffRange
					? await _services.ContextFactory.ResolveDiffRangeAsync(plan, requestedDiffRange, token).ConfigureAwait(false)
					: null;
				ProjectDiffResult diff;
				try
				{
					diff = await DiffQueryRunner
						.CompareAsync(_services, plan, contextLines, token, resolvedDiffRange)
						.ConfigureAwait(false);
				}
				catch (ProjectDiffException)
				{
					throw new TerminalWorkspaceOperationException(
						GitScopeFilter.UnavailableDiagnosticCode,
						L("Terminal.Diff.Unavailable"));
				}
				var affected = await FindDiffImpactAsync(relationSnapshot, plan, diff, persistentGitMode, token)
					.ConfigureAwait(false);
				var output = LimitDiffPanelOutput(
					DiffOutputRenderer.FormatText(diff, _services.Localization, affected).TrimEnd('\n'));
				return planDiagnostics.Length == 0
					? output
					: $"{planDiagnostics}\n\n{output}";
			},
			originatedFromCommandLine: true));
		return TerminalWorkspaceCommandExecutionResult.Deferred();
	}

	// The affected files add to the diff; a project that cannot be related still shows its diff.
	private async Task<DependencyChangeImpact?> FindDiffImpactAsync(
		TerminalRelationStateSnapshot snapshot,
		ProjectContextPlan plan,
		ProjectDiffResult diff,
		GitFilteringMode persistentGitMode,
		CancellationToken cancellationToken)
	{
		if (!diff.HasChanges)
			return null;
		try
		{
			return await _controller
				.FindDiffImpactAsync(snapshot, plan, diff, persistentGitMode, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			return null;
		}
	}

	/// <summary>
	/// Cuts at the last line break at or before the limit, so no line is cut in half, and appends
	/// a trusted summary of how many characters were left out.
	/// </summary>
	private string LimitDiffPanelOutput(string text)
	{
		if (text.Length <= MaximumDiffPanelCharacters)
			return text;
		var cut = text.LastIndexOf('\n', MaximumDiffPanelCharacters - 1);
		var shown = cut < 0 ? MaximumDiffPanelCharacters : cut;
		var omitted = text.Length - shown;
		return text[..shown] + "\n" + NormalizeLocalizedText(
			_services.Localization.Format("Terminal.Tui.Diff.Truncated", omitted),
			_options.Plain,
			_environment.SupportsUnicode);
	}
}
