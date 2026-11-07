using DevProjex.Application.Diff;
using DevProjex.Terminal.Execution;
using DevProjex.Terminal.Rendering;

namespace DevProjex.Terminal.Tui;

internal sealed partial class TerminalWorkspaceSession
{
	internal TerminalWorkspaceCommandExecutionResult ExecuteDiffCommand(
		TerminalWorkspaceCommand command)
	{
		if (_state is null ||
			!GitScopeSelection.TryParse(command.Text, out var mode, out var diffRange) ||
			!GitScopeSelection.IsMomentary(mode))
		{
			return InvalidCommandExecution();
		}

		var state = _state;
		TrackActiveOperation(RunOperationAsync(
			L("Terminal.Tui.Command.Diff.Title"),
			async token =>
			{
				await AwaitLatestSettingsRefreshAsync(token).ConfigureAwait(false);
				// The workspace selection with the requested scope on top: checked items, file types,
				// filters and protection stay exactly as the workspace shows them.
				var current = await _controller.BuildCurrentPlanAsync(state, token).ConfigureAwait(false);
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
						.CompareAsync(_services, plan, DiffQueryRunner.DefaultContextLines, token, resolvedDiffRange)
						.ConfigureAwait(false);
				}
				catch (ProjectDiffException)
				{
					throw new TerminalWorkspaceOperationException(
						GitScopeFilter.UnavailableDiagnosticCode,
						L("Terminal.Diff.Unavailable"));
				}
				var output = DiffOutputRenderer.FormatText(diff, _services.Localization).TrimEnd('\n');
				return planDiagnostics.Length == 0
					? output
					: $"{planDiagnostics}\n\n{output}";
			},
			originatedFromCommandLine: true));
		return TerminalWorkspaceCommandExecutionResult.Deferred();
	}
}
