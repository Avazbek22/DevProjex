using System.Diagnostics;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DevProjex.Terminal.Tui;

#pragma warning disable CS0618 // Terminal.Gui v2 TextView remains the only built-in wrapped, read-only text surface.

internal sealed class TerminalOperationProgressView : IDisposable
{
	private readonly IApplication _application;
	private readonly Func<TimeSpan, string> _elapsedFormatter;
	private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
	private readonly FrameView _frame;
	private readonly Label _phase;
	private readonly Label _source;
	private readonly string _sourceText;
	private readonly SpinnerView _spinner;
	private readonly ProgressBar _progressBar;
	private readonly Label _metrics;
	private readonly Label _detail;
	private readonly Label _elapsed;
	private readonly Label _cancelHint;
	private readonly object? _timerToken;
	private bool _disposed;

	public TerminalOperationProgressView(
		IApplication application,
		string operationName,
		string phase,
		string cancelHint,
		Func<TimeSpan, string> elapsedFormatter,
		string? source = null)
	{
		_application = application;
		_elapsedFormatter = elapsedFormatter;
		_sourceText = source ?? string.Empty;
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
		_source = new Label
		{
			X = 2,
			Y = 2,
			Width = Dim.Fill(2),
			Height = 1,
			Text = _sourceText,
			CanFocus = false,
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		_spinner = new SpinnerView
		{
			X = 2,
			Y = 5,
			AutoSpin = true,
			CanFocus = false,
			SchemeName = TerminalWorkspaceTheme.Accent
		};
		_progressBar = new ProgressBar
		{
			X = 2,
			Y = 5,
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
			Y = 7,
			Width = Dim.Fill(2),
			SchemeName = TerminalWorkspaceTheme.Base
		};
		_detail = new Label
		{
			X = 2,
			Y = 8,
			Width = Dim.Fill(2),
			SchemeName = TerminalWorkspaceTheme.Secondary
		};
		_elapsed = new Label
		{
			X = 2,
			Y = 9,
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
			_source,
			_spinner,
			_progressBar,
			_metrics,
			_detail,
			_elapsed,
			_cancelHint);
		_timerToken = application.AddTimeout(TimeSpan.FromMilliseconds(250), UpdateElapsed);
	}

	public View View => _frame;
	public TimeSpan Elapsed => _stopwatch.Elapsed;

	public void ApplyLayout(int terminalWidth, int terminalHeight)
	{
		var availableWidth = Math.Max(20, terminalWidth - 4);
		var frameWidth = Math.Min(90, availableWidth);
		_frame.Width = frameWidth;
		_frame.Height = Math.Min(15, Math.Max(13, terminalHeight - 4));
		_frame.X = Pos.Center();
		_frame.Y = Pos.Center();
		_source.Text = FitSourceToWidth(_sourceText, Math.Max(8, frameWidth - 6));
	}

	public void SetIndeterminate(
		string phase,
		string? metrics = null,
		string? detail = null)
	{
		_phase.Text = phase;
		_spinner.Visible = true;
		_progressBar.Visible = false;
		_metrics.Text = metrics ?? string.Empty;
		_detail.Text = detail ?? string.Empty;
		_frame.SetNeedsDraw();
	}

	public void SetMeasured(
		string phase,
		double fraction,
		string metrics,
		string? detail = null)
	{
		_phase.Text = phase;
		_spinner.Visible = false;
		_progressBar.Visible = true;
		_progressBar.Fraction = Math.Clamp((float)fraction, 0, 1);
		_metrics.Text = metrics;
		_detail.Text = detail ?? string.Empty;
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

	private static string FitSourceToWidth(string value, int width)
	{
		if (string.IsNullOrEmpty(value) || value.GetColumns() <= width)
			return value;
		if (width <= 3)
			return new string('.', Math.Max(0, width));

		var stablePrefix = GetStableSourcePrefix(value, width);
		var remaining = width - stablePrefix.GetColumns() - 3;
		if (remaining <= 0)
			return FitEndToWidth(value, width);
		var runes = value.EnumerateRunes().ToArray();
		var start = runes.Length;
		while (start > 0)
		{
			var columns = runes[start - 1].GetColumns();
			if (columns > remaining)
				break;
			remaining -= columns;
			start--;
		}
		return stablePrefix + "..." + string.Concat(runes.AsSpan(start).ToArray());
	}

	private static string GetStableSourcePrefix(string value, int width)
	{
		if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
			return string.Empty;
		if (uri.IsFile)
			return "file:///";

		var authority = $"{uri.Scheme}://{uri.Host}/";
		return authority.GetColumns() + 4 < width
			? authority
			: string.Empty;
	}

	private static string FitEndToWidth(string value, int width)
	{
		var remaining = width - 3;
		var builder = new StringBuilder();
		foreach (var rune in value.EnumerateRunes())
		{
			var columns = rune.GetColumns();
			if (columns > remaining)
				break;
			builder.Append(rune);
			remaining -= columns;
		}
		return builder.Append("...").ToString();
	}
}

#pragma warning restore CS0618
