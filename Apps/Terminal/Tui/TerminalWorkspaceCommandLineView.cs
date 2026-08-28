using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;

namespace DevProjex.Terminal.Tui;

internal sealed class TerminalWorkspaceCommandLineView : View
{
	private readonly Func<string, int, TerminalWorkspaceCommandCompletion> _complete;
	private readonly Func<string, int, TerminalWorkspaceCommandGhostCompletion> _completeGhost;
	private readonly Func<string, string> _localize;
	private readonly TerminalCommandHistory _history;
	private readonly bool _plain;
	private readonly bool _useUnicode;
	private readonly TerminalLiteralLabel _prompt;
	private readonly TerminalTransparentTextEditor _input;
	private readonly TerminalLiteralLabel _ghost;
	private readonly TerminalLiteralLabel _result;
	private IReadOnlyList<TerminalWorkspaceCommandCompletionCandidate> _cycleCandidates = [];
	private string? _cycleSeedText;
	private int _cycleSeedCursor;
	private int _cycleIndex = -1;
	private bool _applyingCompletion;
	private string _resultText = string.Empty;
	private bool _resultSuccess;

	public TerminalWorkspaceCommandLineView(
		IApplication application,
		Func<string, int, TerminalWorkspaceCommandCompletion> complete,
		Func<string, int, TerminalWorkspaceCommandGhostCompletion> completeGhost,
		Func<string, string> localize,
		TerminalCommandHistory history,
		bool plain,
		bool useUnicode)
	{
		_complete = complete;
		_completeGhost = completeGhost;
		_localize = localize;
		_history = history;
		_plain = plain;
		_useUnicode = useUnicode;
		CanFocus = true;
		Height = 1;
		Visible = false;
		ViewportSettings |= ViewportSettingsFlags.Transparent;
		_prompt = new TerminalLiteralLabel
		{
			X = 0,
			Y = 0,
			Width = 1,
			Height = 1,
			Text = ":",
			SchemeName = TerminalWorkspaceTheme.Accent
		};
		_input = new TerminalTransparentTextEditor
		{
			X = 1,
			Y = 0,
			Width = Dim.Fill(),
			Height = 1,
			SchemeName = TerminalWorkspaceTheme.Base
		};
		_ghost = new TerminalLiteralLabel
		{
			Y = 0,
			Height = 1,
			CanFocus = false,
			SchemeName = plain ? TerminalWorkspaceTheme.Base : TerminalWorkspaceTheme.Secondary
		};
		_result = new TerminalLiteralLabel
		{
			X = 0,
			Y = 0,
			Width = 1,
			Height = 1,
			CanFocus = false,
			Visible = false
		};
		_input.ValueChanged += (_, _) =>
		{
			if (_applyingCompletion)
				return;
			ResetCompletionCycle();
			UpdateGhost();
		};
		_input.InsertionPointChanged += (_, _) =>
		{
			if (_applyingCompletion)
				return;
			ResetCompletionCycle();
			UpdateGhost();
		};
		_input.KeyPressed += OnInputKeyDown;
		Add(_prompt, _input, _ghost, _result);
	}

	public event EventHandler<string>? Submitted;
	public event EventHandler? Canceled;

	public bool IsEditing { get; private set; }
	public bool IsShowingResult => Visible && _result.Visible;
	public string InputText => _input.Value;

	public void Open(string initialText = "")
	{
		IsEditing = true;
		Visible = true;
		_result.Visible = false;
		_prompt.Visible = true;
		_input.Visible = true;
		_ghost.Visible = true;
		_history.ResetNavigation();
		SetInputText(initialText);
		_input.SetFocus();
		SetNeedsDraw();
	}

	public void Close()
	{
		IsEditing = false;
		Visible = false;
		_result.Visible = false;
		ResetCompletionCycle();
		SetNeedsDraw();
	}

	public void ShowResult(string text, bool success)
	{
		IsEditing = false;
		Visible = true;
		_prompt.Visible = false;
		_input.Visible = false;
		_ghost.Visible = false;
		_result.Visible = true;
		_result.SchemeName = success
			? TerminalWorkspaceTheme.Success
			: TerminalWorkspaceTheme.Error;
		_resultText = text;
		_resultSuccess = success;
		RenderResult();
		SetNeedsDraw();
	}

	public void RefreshLayout()
	{
		if (_result.Visible)
			RenderResult();
		UpdateGhost();
	}

	private void RenderResult()
	{
		var marker = _useUnicode && !_plain
			? _resultSuccess ? "✓" : "✗"
			: _resultSuccess ? "+" : "x";
		_result.Text = TerminalParameterRow.FitLabel(
			$"{marker} {_resultText}",
			Math.Max(1, Viewport.Width),
			_useUnicode && !_plain);
		_result.Width = Math.Max(1, (_result.Text?.ToString() ?? string.Empty).GetColumns());
	}

	public void RestoreInputFocus()
	{
		if (IsEditing)
			_input.SetFocus();
	}

	private void OnInputKeyDown(Key key)
	{
		if (key == Key.Esc)
		{
			key.Handled = true;
			Canceled?.Invoke(this, EventArgs.Empty);
			return;
		}
		if (key == Key.Enter)
		{
			key.Handled = true;
			Submitted?.Invoke(this, InputText);
			return;
		}
		if (key == Key.CursorUp)
		{
			key.Handled = true;
			SetInputText(_history.Previous(InputText));
			return;
		}
		if (key == Key.CursorDown)
		{
			key.Handled = true;
			SetInputText(_history.Next());
			return;
		}
		if (key == Key.Tab)
		{
			key.Handled = true;
			CycleCompletion();
			return;
		}
	}

	private void CycleCompletion()
	{
		if (_cycleSeedText is null)
		{
			_cycleSeedText = InputText;
			_cycleSeedCursor = TerminalTextPosition.RuneToUtf16Index(
				_cycleSeedText,
				_input.InsertionPoint);
			_cycleCandidates = _complete(_cycleSeedText, _cycleSeedCursor).Candidates;
			_cycleIndex = -1;
		}
		if (_cycleCandidates.Count == 0)
			return;

		_cycleIndex = (_cycleIndex + 1) % _cycleCandidates.Count;
		var candidate = _cycleCandidates[_cycleIndex];
		_applyingCompletion = true;
		try
		{
			_input.Value = candidate.CompletedText;
			_input.InsertionPoint = TerminalTextPosition.Utf16ToRuneIndex(
				candidate.CompletedText,
				candidate.CursorPosition);
		}
		finally
		{
			_applyingCompletion = false;
		}
		UpdateGhost();
	}

	private void SetInputText(string text)
	{
		_applyingCompletion = true;
		try
		{
			_input.Value = text;
			_input.MoveEnd();
		}
		finally
		{
			_applyingCompletion = false;
		}
		ResetCompletionCycle();
		UpdateGhost();
	}

	private void UpdateGhost()
	{
		var text = InputText;
		if (!IsEditing || _input.InsertionPoint < text.EnumerateRunes().Count())
		{
			HideGhost();
			return;
		}

		var completion = _completeGhost(
			text,
			TerminalTextPosition.RuneToUtf16Index(text, _input.InsertionPoint));
		var ghost = completion.SchemaKey is { } schemaKey
			? " " + _localize(schemaKey)
			: completion.GhostSuffix;
		if (string.IsNullOrEmpty(ghost))
		{
			HideGhost();
			return;
		}

		var inputColumns = text.EnumerateRunes()
			.Skip(Math.Clamp(_input.ScrollOffset, 0, text.Length))
			.Sum(static rune => Math.Max(0, rune.GetColumns()));
		var x = 1 + inputColumns;
		var maximumColumns = Math.Max(0, Viewport.Width - x);
		if (maximumColumns == 0)
		{
			HideGhost();
			return;
		}

		var rendered = _plain ? $" [{ghost.Trim()}]" : ghost;
		_ghost.Visible = true;
		_ghost.X = x;
		_ghost.Text = TerminalParameterRow.FitLabel(
			rendered,
			maximumColumns,
			_useUnicode && !_plain);
		_ghost.Width = Math.Max(1, (_ghost.Text?.ToString() ?? string.Empty).GetColumns());
		_ghost.SetNeedsDraw();
	}

	private void HideGhost()
	{
		_ghost.Visible = false;
		_ghost.Text = string.Empty;
		_ghost.Width = 0;
		_ghost.SetNeedsDraw();
	}

	private void ResetCompletionCycle()
	{
		_cycleCandidates = [];
		_cycleSeedText = null;
		_cycleSeedCursor = 0;
		_cycleIndex = -1;
	}
}

internal static class TerminalTextPosition
{
	public static int RuneToUtf16Index(string text, int runeIndex)
	{
		ArgumentNullException.ThrowIfNull(text);
		if (runeIndex <= 0)
			return 0;

		var currentRune = 0;
		var utf16Index = 0;
		foreach (var rune in text.EnumerateRunes())
		{
			if (currentRune++ >= runeIndex)
				break;
			utf16Index += rune.Utf16SequenceLength;
		}
		return Math.Min(utf16Index, text.Length);
	}

	public static int Utf16ToRuneIndex(string text, int utf16Index)
	{
		ArgumentNullException.ThrowIfNull(text);
		utf16Index = Math.Clamp(utf16Index, 0, text.Length);
		var currentUtf16 = 0;
		var runeIndex = 0;
		foreach (var rune in text.EnumerateRunes())
		{
			if (currentUtf16 >= utf16Index)
				break;
			currentUtf16 += rune.Utf16SequenceLength;
			runeIndex++;
		}
		return runeIndex;
	}
}
