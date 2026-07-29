using Terminal.Gui.Drawing;
using Terminal.Gui.Views;

namespace DevProjex.Terminal.Tui;

#pragma warning disable CS0618

internal sealed partial class TerminalWorkspaceSession
{
	private string _clonePhaseKey = "Terminal.Tui.Clone.Validating";
	private int? _clonePercent;

	private void ShowCloneProgress(string repositoryName, string safeRepositoryUrl)
	{
		CancelWorkspaceRefreshes();
		ClearRoot();
		_screen = TerminalWorkspaceScreen.Loading;
		_layoutMode = ResolveLayout();
		_clonePhaseKey = "Terminal.Tui.Clone.Validating";
		_clonePercent = null;
		var heading = new TerminalLiteralLabel
		{
			X = 2,
			Y = 1,
			Text = "DevProjex Terminal",
			SchemeName = TerminalWorkspaceTheme.Accent
		};
		_operationProgress = new TerminalOperationProgressView(
			_application,
			$"{L("Terminal.Tui.CloningRepository")} · {repositoryName}",
			L(_clonePhaseKey),
			L("Terminal.Tui.Progress.CancelHint"),
			FormatElapsed,
			safeRepositoryUrl,
			UseTextProgress);
		_operationProgress.SetIndeterminate(
			L(_clonePhaseKey),
			repositoryName,
			L("Terminal.Tui.Clone.ValidatingUrl"));
		_operationProgress.ApplyLayout(_terminalWidth, _terminalHeight);
		_tooSmall = CreateTooSmallLabel();
		_root.Add(heading, _operationProgress.View, _tooSmall);
		ApplyLoadingLayout();
		CompleteRootTransition();
	}

	private void UpdateCloneProgressSafe(string status)
	{
		if (_stopping)
			return;
		_application.Invoke(() =>
		{
			if (_screen != TerminalWorkspaceScreen.Loading || _operationProgress is null)
				return;

			var parsed = TerminalGitProgressParser.Parse(
				status,
				_clonePhaseKey,
				_clonePercent);
			_clonePhaseKey = parsed.PhaseKey;
			_clonePercent = parsed.Percent;
			var phase = L(parsed.PhaseKey);
			var metrics = parsed.Percent is { } percent
				? $"{percent}%"
				: L("Terminal.Tui.Clone.InProgress");
			if (parsed.Percent is { } measured)
			{
				_operationProgress.SetMeasured(
					phase,
					measured / 100d,
					metrics,
					parsed.Detail);
			}
			else
			{
				_operationProgress.SetIndeterminate(
					phase,
					metrics,
					parsed.Detail);
			}
		});
	}

	private void UpdateClonePhaseSafe(string phaseKey, string detail)
	{
		if (_stopping)
			return;
		_application.Invoke(() =>
		{
			if (_screen != TerminalWorkspaceScreen.Loading || _operationProgress is null)
				return;
			_clonePhaseKey = phaseKey;
			_clonePercent = null;
			_operationProgress.SetIndeterminate(
				L(phaseKey),
				L("Terminal.Tui.Clone.InProgress"),
				detail);
		});
	}
}

#pragma warning restore CS0618
