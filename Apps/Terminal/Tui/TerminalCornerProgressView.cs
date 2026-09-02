using System.Diagnostics;
using Terminal.Gui.App;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace DevProjex.Terminal.Tui;

internal static class TerminalCornerProgressFormatter
{
	internal static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(200);

	internal static bool ShouldShow(bool active, TimeSpan elapsed, bool tooSmall) =>
		active && !tooSmall && elapsed >= ShowDelay;

	internal static string Format(
		string label,
		double? fraction,
		int spinnerFrame,
		int maximumColumns,
		bool plain,
		bool useUnicode)
	{
		if (maximumColumns <= 0)
			return string.Empty;

		var prefix = plain
			? string.Empty
			: $"{ResolveSpinner(spinnerFrame, useUnicode)} ";
		var suffix = fraction is { } value
			? $" {(int)Math.Round(
				Math.Clamp(value, 0, 1) * 100,
				MidpointRounding.AwayFromZero)}%"
			: string.Empty;
		var labelWidth = Math.Max(0, maximumColumns - prefix.GetColumns() - suffix.GetColumns());
		var fittedLabel = TerminalParameterRow.FitLabel(label, labelWidth, useUnicode && !plain);
		var result = prefix + fittedLabel + suffix;
		return result.GetColumns() <= maximumColumns
			? result
			: TerminalParameterRow.FitLabel(result, maximumColumns, useUnicode && !plain);
	}

	private static char ResolveSpinner(int frame, bool useUnicode)
	{
		const string unicodeFrames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";
		const string asciiFrames = "|/-\\";
		var frames = useUnicode ? unicodeFrames : asciiFrames;
		return frames[Math.Abs(frame % frames.Length)];
	}
}

internal sealed class TerminalCornerProgressView : IDisposable
{
	internal const int MaximumWidth = 24;
	private static readonly TimeSpan AnimationInterval = TimeSpan.FromMilliseconds(100);
	private readonly IApplication _application;
	private readonly Label _label;
	private readonly bool _plain;
	private readonly bool _useUnicode;
	private readonly Action _visibilityChanged;
	private readonly Dictionary<long, ProgressEntry> _operations = [];
	private readonly Stopwatch _activeStopwatch = new();
	private object? _delayToken;
	private object? _animationToken;
	private long _nextOperationId;
	private int _spinnerFrame;
	private bool _delayElapsed;
	private bool _tooSmall;
	private bool _disposed;

	public TerminalCornerProgressView(
		IApplication application,
		bool plain,
		bool useUnicode,
		Action visibilityChanged)
	{
		_application = application;
		_plain = plain;
		_useUnicode = useUnicode;
		_visibilityChanged = visibilityChanged;
		_label = new TerminalLiteralLabel
		{
			X = Pos.AnchorEnd(MaximumWidth + 1),
			Y = 0,
			Width = MaximumWidth,
			Height = 1,
			Visible = false,
			CanFocus = false,
			SchemeName = TerminalWorkspaceTheme.Accent
		};
	}

	public View View => _label;
	public bool IsVisible => _label.Visible;
	public int ReservedWidth => IsVisible ? MaximumWidth + 1 : 0;

	public long Begin(string label, double? fraction = null)
	{
		if (_disposed)
			return 0;

		var operationId = ++_nextOperationId;
		_operations[operationId] = new ProgressEntry(label, fraction);
		if (_operations.Count == 1)
		{
			_activeStopwatch.Restart();
			_delayElapsed = false;
			_delayToken = _application.AddTimeout(
				TerminalCornerProgressFormatter.ShowDelay,
				ShowAfterDelay);
		}
		else if (_delayElapsed)
		{
			Render();
		}
		return operationId;
	}

	public void Update(long operationId, string label, double? fraction = null)
	{
		if (_disposed || !_operations.ContainsKey(operationId))
			return;
		_operations[operationId] = new ProgressEntry(label, fraction);
		if (_delayElapsed)
			Render();
	}

	public void Complete(long operationId)
	{
		if (_disposed || operationId == 0 || !_operations.Remove(operationId))
			return;
		if (_operations.Count > 0)
		{
			if (_delayElapsed)
				Render();
			return;
		}

		_activeStopwatch.Reset();
		_delayElapsed = false;
		RemoveTimer(ref _delayToken);
		RemoveTimer(ref _animationToken);
		SetVisible(false);
	}

	public void Clear()
	{
		if (_disposed)
			return;
		_operations.Clear();
		_activeStopwatch.Reset();
		_delayElapsed = false;
		RemoveTimer(ref _delayToken);
		RemoveTimer(ref _animationToken);
		SetVisible(false);
	}

	public void ApplyLayout(bool tooSmall)
	{
		_tooSmall = tooSmall;
		_label.X = Pos.AnchorEnd(MaximumWidth + 1);
		_label.Width = MaximumWidth;
		if (tooSmall)
		{
			RemoveTimer(ref _animationToken);
		}
		else if (!_plain && _delayElapsed && _operations.Count > 0 && _animationToken is null)
		{
			_animationToken = _application.AddTimeout(AnimationInterval, Animate);
		}
		RefreshVisibility();
	}

	private bool ShowAfterDelay()
	{
		_delayToken = null;
		if (_disposed || _operations.Count == 0)
			return false;

		_delayElapsed = true;
		RefreshVisibility();
		if (!_plain && !_tooSmall && _animationToken is null)
			_animationToken = _application.AddTimeout(AnimationInterval, Animate);
		return false;
	}

	private bool Animate()
	{
		if (_disposed || _operations.Count == 0 || !_delayElapsed)
		{
			_animationToken = null;
			return false;
		}
		_spinnerFrame++;
		if (!_tooSmall)
			Render();
		return true;
	}

	private void RefreshVisibility()
	{
		var visible = TerminalCornerProgressFormatter.ShouldShow(
			_operations.Count > 0,
			_activeStopwatch.Elapsed,
			_tooSmall) && _delayElapsed;
		SetVisible(visible);
		if (visible)
			Render();
	}

	private void SetVisible(bool visible)
	{
		if (_label.Visible == visible)
			return;
		_label.Visible = visible;
		_label.Text = visible ? _label.Text : string.Empty;
		_label.SetNeedsDraw();
		_visibilityChanged();
	}

	private void Render()
	{
		if (!_label.Visible || _operations.Count == 0)
			return;
		var entry = _operations.MaxBy(static pair => pair.Key).Value;
		var text = TerminalCornerProgressFormatter.Format(
			entry.Label,
			entry.Fraction,
			_spinnerFrame,
			MaximumWidth,
			_plain,
			_useUnicode);
		var padding = Math.Max(0, MaximumWidth - text.GetColumns());
		_label.Text = new string(' ', padding) + text;
		_label.SetNeedsDraw();
	}

	private void RemoveTimer(ref object? token)
	{
		if (token is null)
			return;
		_application.RemoveTimeout(token);
		token = null;
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		Clear();
		_disposed = true;
	}

	private sealed record ProgressEntry(string Label, double? Fraction);
}
