using System.Drawing;
using DevProjex.Terminal.Rendering;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;

namespace DevProjex.Terminal.Tui;

internal sealed class TerminalTransparentTextEditor : View
{
	private string _value = string.Empty;
	private int _insertionPoint;
	private int _scrollOffset;
	private int _lastDrawnColumns;

	public TerminalTransparentTextEditor()
	{
		CanFocus = true;
		Height = 1;
		ViewportSettings |= ViewportSettingsFlags.Transparent;
		HasFocusChanged += (_, _) => UpdateCursor();
	}

	public event EventHandler? ValueChanged;
	public event Action<Key>? KeyPressed;

	public string Value
	{
		get => _value;
		set
		{
			value = TerminalCommandHistory.LimitLength(
				TerminalTextEscaping.EscapeSingleLine(value ?? string.Empty));
			if (string.Equals(_value, value, StringComparison.Ordinal))
				return;
			_value = value;
			_insertionPoint = Math.Min(_insertionPoint, RuneCount);
			EnsureCursorVisible();
			ValueChanged?.Invoke(this, EventArgs.Empty);
			InvalidateEditor();
		}
	}

	public int InsertionPoint
	{
		get => _insertionPoint;
		set
		{
			var next = Math.Clamp(value, 0, RuneCount);
			if (_insertionPoint == next)
				return;
			_insertionPoint = next;
			EnsureCursorVisible();
			InvalidateEditor();
		}
	}

	public int ScrollOffset => _scrollOffset;

	public void MoveEnd() => InsertionPoint = RuneCount;

	protected override bool OnKeyDown(Key key)
	{
		KeyPressed?.Invoke(key);
		if (key.Handled)
			return true;

		if (key == Key.CursorLeft)
		{
			InsertionPoint--;
			return true;
		}
		if (key == Key.CursorRight)
		{
			InsertionPoint++;
			return true;
		}
		if (key == Key.Home)
		{
			InsertionPoint = 0;
			return true;
		}
		if (key == Key.End)
		{
			MoveEnd();
			return true;
		}
		if (key == Key.Backspace)
		{
			if (_insertionPoint > 0)
				RemoveRune(_insertionPoint - 1);
			return true;
		}
		if (key == Key.Delete)
		{
			if (_insertionPoint < RuneCount)
				RemoveRune(_insertionPoint);
			return true;
		}

		var text = key.GetPrintableText();
		if (string.IsNullOrEmpty(text))
			return base.OnKeyDown(key);
		InsertText(text);
		return true;
	}

	protected override bool OnClearingViewport() => true;

	protected override bool OnDrawingContent(DrawContext? context)
	{
		EnsureCursorVisible();
		var visible = GetVisibleText();
		var visibleColumns = visible.GetColumns();
		var clearedColumns = Math.Min(
			Math.Max(_lastDrawnColumns, visibleColumns),
			Math.Max(0, Viewport.Width));
		SetAttributeForRole(VisualRole.ReadOnly);
		if (clearedColumns > 0)
			AddStr(0, 0, new string(' ', clearedColumns));
		if (visible.Length > 0)
			AddStr(0, 0, visible);
		_lastDrawnColumns = visibleColumns;
		if (clearedColumns > 0)
		{
			context?.AddDrawnRectangle(ViewportToScreen(
				new Rectangle(0, 0, clearedColumns, 1)));
		}
		UpdateCursor();
		return true;
	}

	private int RuneCount => _value.EnumerateRunes().Count();

	internal void InsertText(string text)
	{
		text = TerminalTextEscaping.EscapeSingleLine(text);
		var remaining = TerminalCommandHistory.MaximumCommandLength - _value.Length;
		text = TerminalCommandHistory.LimitLength(text, remaining);
		if (text.Length == 0)
			return;
		var utf16Index = TerminalTextPosition.RuneToUtf16Index(_value, _insertionPoint);
		_value = _value.Insert(utf16Index, text);
		_insertionPoint += text.EnumerateRunes().Count();
		EnsureCursorVisible();
		ValueChanged?.Invoke(this, EventArgs.Empty);
		InvalidateEditor();
	}

	private void RemoveRune(int runeIndex)
	{
		var start = TerminalTextPosition.RuneToUtf16Index(_value, runeIndex);
		var end = TerminalTextPosition.RuneToUtf16Index(_value, runeIndex + 1);
		_value = _value.Remove(start, end - start);
		if (runeIndex < _insertionPoint)
			_insertionPoint--;
		EnsureCursorVisible();
		ValueChanged?.Invoke(this, EventArgs.Empty);
		InvalidateEditor();
	}

	private void EnsureCursorVisible()
	{
		var width = Math.Max(1, Viewport.Width);
		var runes = _value.EnumerateRunes().ToArray();
		_scrollOffset = Math.Clamp(_scrollOffset, 0, runes.Length);
		if (_insertionPoint < _scrollOffset)
			_scrollOffset = _insertionPoint;
		while (_scrollOffset < _insertionPoint &&
		       Columns(runes, _scrollOffset, _insertionPoint) >= width)
		{
			_scrollOffset++;
		}
		while (_scrollOffset > 0 &&
		       Columns(runes, _scrollOffset - 1, _insertionPoint) < width)
		{
			_scrollOffset--;
		}
	}

	private string GetVisibleText()
	{
		var width = Math.Max(0, Viewport.Width);
		if (width == 0)
			return string.Empty;
		var builder = new StringBuilder();
		var columns = 0;
		foreach (var rune in _value.EnumerateRunes().Skip(_scrollOffset))
		{
			var runeColumns = rune.ToString().GetColumns();
			if (columns + runeColumns > width)
				break;
			builder.Append(rune);
			columns += runeColumns;
		}
		return builder.ToString();
	}

	private void UpdateCursor()
	{
		if (!HasFocus || Viewport.Width <= 0)
		{
			Cursor = new Cursor { Style = CursorStyle.Hidden };
			SetCursorNeedsUpdate();
			return;
		}

		var runes = _value.EnumerateRunes().ToArray();
		var column = Math.Clamp(
			Columns(runes, _scrollOffset, _insertionPoint),
			0,
			Math.Max(0, Viewport.Width - 1));
		Cursor = new Cursor
		{
			Position = ViewportToScreen(new Point(column, 0)),
			Style = CursorStyle.Default
		};
		SetCursorNeedsUpdate();
	}

	private static int Columns(IReadOnlyList<Rune> runes, int start, int end)
	{
		var columns = 0;
		for (var index = Math.Clamp(start, 0, runes.Count);
		     index < Math.Clamp(end, 0, runes.Count);
		     index++)
		{
			columns += runes[index].ToString().GetColumns();
		}
		return columns;
	}

	private void InvalidateEditor()
	{
		SetNeedsDraw();
		SetCursorNeedsUpdate();
	}
}
