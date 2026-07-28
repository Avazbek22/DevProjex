using System.Diagnostics;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DevProjex.Terminal.Tui;

internal sealed class TerminalOperationProgressView : IDisposable
{
	private readonly IApplication _application;
	private readonly Func<TimeSpan, string> _elapsedFormatter;
	private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
	private readonly FrameView _frame;
	private readonly Label _phase;
	private readonly SpinnerView _spinner;
	private readonly ProgressBar _progressBar;
	private readonly Label _metrics;
	private readonly Label _elapsed;
	private readonly Label _cancelHint;
	private readonly object? _timerToken;
	private bool _disposed;

	public TerminalOperationProgressView(
		IApplication application,
		string operationName,
		string phase,
		string cancelHint,
		Func<TimeSpan, string> elapsedFormatter)
	{
		_application = application;
		_elapsedFormatter = elapsedFormatter;
		_frame = new FrameView
		{
			Title = operationName,
			BorderStyle = LineStyle.Single,
			SchemeName = TerminalWorkspaceTheme.Dialog,
			CanFocus = false
		};
		_phase = new Label
		{
			X = 2,
			Y = 1,
			Width = Dim.Fill(2),
			Text = phase,
			SchemeName = TerminalWorkspaceTheme.Accent
		};
		_spinner = new SpinnerView
		{
			X = 2,
			Y = 3,
			AutoSpin = true,
			CanFocus = false,
			SchemeName = TerminalWorkspaceTheme.Accent
		};
		_progressBar = new ProgressBar
		{
			X = 2,
			Y = 3,
			Width = Dim.Fill(2),
			Height = 1,
			Visible = false,
			CanFocus = false,
			ProgressBarStyle = ProgressBarStyle.Continuous,
			ProgressBarFormat = ProgressBarFormat.SimplePlusPercentage,
			SchemeName = TerminalWorkspaceTheme.Accent
		};
		_metrics = new Label
		{
			X = 2,
			Y = 5,
			Width = Dim.Fill(2),
			SchemeName = TerminalWorkspaceTheme.Base
		};
		_elapsed = new Label
		{
			X = 2,
			Y = 6,
			Width = Dim.Fill(2),
			Text = elapsedFormatter(TimeSpan.Zero),
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		_cancelHint = new Label
		{
			X = 2,
			Y = Pos.AnchorEnd(1),
			Width = Dim.Fill(2),
			Text = cancelHint,
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		_frame.Add(
			_phase,
			_spinner,
			_progressBar,
			_metrics,
			_elapsed,
			_cancelHint);
		_timerToken = application.AddTimeout(TimeSpan.FromMilliseconds(250), UpdateElapsed);
	}

	public View View => _frame;
	public TimeSpan Elapsed => _stopwatch.Elapsed;

	public void ApplyLayout(int terminalWidth, int terminalHeight)
	{
		var availableWidth = Math.Max(20, terminalWidth - 4);
		_frame.Width = Math.Min(90, availableWidth);
		_frame.Height = Math.Min(11, Math.Max(9, terminalHeight - 4));
		_frame.X = Pos.Center();
		_frame.Y = Pos.Center();
	}

	public void SetIndeterminate(string phase, string? metrics = null)
	{
		_phase.Text = phase;
		_spinner.Visible = true;
		_progressBar.Visible = false;
		_metrics.Text = metrics ?? string.Empty;
		_frame.SetNeedsDraw();
	}

	public void SetMeasured(
		string phase,
		double fraction,
		string metrics)
	{
		_phase.Text = phase;
		_spinner.Visible = false;
		_progressBar.Visible = true;
		_progressBar.Fraction = Math.Clamp((float)fraction, 0, 1);
		_metrics.Text = metrics;
		_frame.SetNeedsDraw();
	}

	private bool UpdateElapsed()
	{
		if (_disposed)
			return false;
		_elapsed.Text = _elapsedFormatter(_stopwatch.Elapsed);
		_elapsed.SetNeedsDraw();
		return true;
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_stopwatch.Stop();
		if (_timerToken is not null)
			_application.RemoveTimeout(_timerToken);
		_frame.Dispose();
	}
}
