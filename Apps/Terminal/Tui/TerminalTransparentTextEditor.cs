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
	private int _runeCount;
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
	public event EventHandler? InsertionPointChanged;
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
			_runeCount = value.EnumerateRunes().Count();
			_insertionPoint = Math.Min(_insertionPoint, _runeCount);
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
			InsertionPointChanged?.Invoke(this, EventArgs.Empty);
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

	private int RuneCount => _runeCount;

	internal void InsertText(string text)
	{
		text = TerminalTextEscaping.EscapeSingleLine(text);
		var remaining = TerminalCommandHistory.MaximumCommandLength - _value.Length;
		text = TerminalCommandHistory.LimitLength(text, remaining);
		if (text.Length == 0)
			return;
		var utf16Index = TerminalTextPosition.RuneToUtf16Index(_value, _insertionPoint);
		_value = _value.Insert(utf16Index, text);
		var insertedRuneCount = text.EnumerateRunes().Count();
		_insertionPoint += insertedRuneCount;
		_runeCount += insertedRuneCount;
		EnsureCursorVisible();
		ValueChanged?.Invoke(this, EventArgs.Empty);
		InvalidateEditor();
	}

	private void RemoveRune(int runeIndex)
	{
		var start = TerminalTextPosition.RuneToUtf16Index(_value, runeIndex);
		var end = TerminalTextPosition.RuneToUtf16Index(_value, runeIndex + 1);
		_value = _value.Remove(start, end - start);
		_runeCount--;
		if (runeIndex < _insertionPoint)
			_insertionPoint--;
		EnsureCursorVisible();
		ValueChanged?.Invoke(this, EventArgs.Empty);
		InvalidateEditor();
	}

	private void EnsureCursorVisible()
	{
		var width = Math.Max(1, Viewport.Width);
		var insertionPoint = Math.Clamp(_insertionPoint, 0, _runeCount);
		var utf16Index = TerminalTextPosition.RuneToUtf16Index(_value, insertionPoint);
		var columns = 0;
		var offset = insertionPoint;
		while (offset > 0 && utf16Index > 0)
		{
			_ = Rune.DecodeLastFromUtf16(
				_value.AsSpan(0, utf16Index),
				out var rune,
				out var consumed);
			if (consumed == 0)
				break;
			var nextColumns = Math.Max(0, rune.GetColumns());
			if (columns + nextColumns >= width)
				break;
			columns += nextColumns;
			utf16Index -= consumed;
			offset--;
		}
		_scrollOffset = offset;
	}

	private string GetVisibleText()
	{
		var width = Math.Max(0, Viewport.Width);
		if (width == 0)
			return string.Empty;
		var builder = new StringBuilder();
		var columns = 0;
		var start = TerminalTextPosition.RuneToUtf16Index(_value, _scrollOffset);
		foreach (var rune in _value.AsSpan(start).EnumerateRunes())
		{
			var runeColumns = Math.Max(0, rune.GetColumns());
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

		var column = Math.Clamp(
			Columns(_value, _scrollOffset, _insertionPoint),
			0,
			Math.Max(0, Viewport.Width - 1));
		Cursor = new Cursor
		{
			Position = ViewportToScreen(new Point(column, 0)),
			Style = CursorStyle.Default
		};
		SetCursorNeedsUpdate();
	}

	private static int Columns(string value, int start, int end)
	{
		var columns = 0;
		var startIndex = TerminalTextPosition.RuneToUtf16Index(value, start);
		var endIndex = TerminalTextPosition.RuneToUtf16Index(value, end);
		foreach (var rune in value.AsSpan(startIndex, endIndex - startIndex).EnumerateRunes())
			columns += Math.Max(0, rune.GetColumns());
		return columns;
	}

	private void InvalidateEditor()
	{
		SetNeedsDraw();
		SetCursorNeedsUpdate();
	}
}
