using System.Drawing;
using System.Text;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;

namespace DevProjex.Terminal.Tui;

internal sealed class TerminalVirtualizedPreviewView : View
{
	private IPreviewTextDocument? _document;
	private int _firstVisibleLine;
	private int _horizontalOffset;

	public TerminalVirtualizedPreviewView()
	{
		CanFocus = true;
		SchemeName = TerminalWorkspaceTheme.Base;
		ViewportChanged += (_, _) => RaiseVisibleRangeChanged();
	}

	public event EventHandler? VisibleRangeChanged;

	public int FirstVisibleLine => _firstVisibleLine;

	public int HorizontalOffset => _horizontalOffset;

	public int LineCount => _document?.LineCount ?? 1;

	public int PageSize => Math.Max(1, Viewport.Height);

	public int VisibleLastLine =>
		Math.Min(LineCount, _firstVisibleLine + PageSize);

	public int MaxLineLength => _document?.MaxLineLength ?? 0;

	public int VisibleTextWidth =>
		Math.Max(1, Viewport.Width - ResolveVerticalIndicatorWidth());

	public IReadOnlyList<PreviewDocumentSection> Sections =>
		_document?.Sections ?? [];

	public bool HasHorizontalOverflow =>
		MaxLineLength > VisibleTextWidth;

	public void SetDocument(IPreviewTextDocument document, bool preserveViewport)
	{
		ArgumentNullException.ThrowIfNull(document);
		_document = document;
		if (!preserveViewport)
		{
			_firstVisibleLine = 0;
			_horizontalOffset = 0;
		}

		ClampViewport();
		SetNeedsDraw();
		RaiseVisibleRangeChanged();
	}

	public void ScrollTo(int zeroBasedLine, int horizontalOffset)
	{
		var maximumFirstLine = Math.Max(0, LineCount - PageSize);
		_firstVisibleLine = Math.Clamp(zeroBasedLine, 0, maximumFirstLine);
		var textWidth = VisibleTextWidth;
		_horizontalOffset = Math.Clamp(
			horizontalOffset,
			0,
			Math.Max(0, MaxLineLength - textWidth));
		SetNeedsDraw();
		RaiseVisibleRangeChanged();
	}

	public int FindNext(string query, int startLine)
	{
		if (_document is null || string.IsNullOrWhiteSpace(query))
			return -1;

		var normalizedStart = Math.Clamp(startLine, -1, LineCount - 1);
		for (var offset = 1; offset <= LineCount; offset++)
		{
			var line = (normalizedStart + offset) % LineCount;
			if (_document.GetLineText(line + 1)
			    .Contains(query, StringComparison.OrdinalIgnoreCase))
			{
				return line;
			}
		}

		return -1;
	}

	protected override bool OnDrawingContent(DrawContext? context)
	{
		if (_document is null)
			return true;

		ClampViewport();
		var indicatorWidth = ResolveVerticalIndicatorWidth();
		var textWidth = VisibleTextWidth;
		SetAttributeForRole(VisualRole.ReadOnly);
		for (var row = 0; row < Viewport.Height; row++)
		{
			var lineIndex = _firstVisibleLine + row;
			if (lineIndex >= LineCount)
				break;

			var line = _document.GetLineText(lineIndex + 1);
			AddStr(0, row, SliceColumns(line, _horizontalOffset, textWidth));
		}

		if (indicatorWidth > 0)
			DrawVerticalPositionIndicator(textWidth);

		return true;
	}

	protected override bool OnMouseEvent(Mouse mouse)
	{
		if (mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed) ||
		    mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked))
		{
			SetFocus();
			return true;
		}
		if (mouse.Flags.HasFlag(MouseFlags.WheeledUp))
		{
			ScrollTo(_firstVisibleLine - 3, _horizontalOffset);
			return true;
		}
		if (mouse.Flags.HasFlag(MouseFlags.WheeledDown))
		{
			ScrollTo(_firstVisibleLine + 3, _horizontalOffset);
			return true;
		}
		if (mouse.Flags.HasFlag(MouseFlags.WheeledLeft))
		{
			ScrollTo(_firstVisibleLine, _horizontalOffset - 4);
			return true;
		}
		if (mouse.Flags.HasFlag(MouseFlags.WheeledRight))
		{
			ScrollTo(_firstVisibleLine, _horizontalOffset + 4);
			return true;
		}

		return base.OnMouseEvent(mouse);
	}

	private void DrawVerticalPositionIndicator(int column)
	{
		var height = Math.Max(1, Viewport.Height);
		var thumbHeight = Math.Max(1, (int)Math.Round(
			height * Math.Min(1d, height / (double)Math.Max(1, LineCount))));
		var maximumThumbTop = Math.Max(0, height - thumbHeight);
		var maximumFirstLine = Math.Max(1, LineCount - height);
		var thumbTop = Math.Clamp(
			(int)Math.Round(maximumThumbTop * (_firstVisibleLine / (double)maximumFirstLine)),
			0,
			maximumThumbTop);

		SetAttributeForRole(VisualRole.Disabled);
		for (var row = 0; row < height; row++)
			AddRune(
				column,
				row,
				new Rune(row >= thumbTop && row < thumbTop + thumbHeight ? '#' : '|'));
	}

	private int ResolveVerticalIndicatorWidth() =>
		LineCount > Math.Max(1, Viewport.Height) && Viewport.Width >= 4 ? 1 : 0;

	private void ClampViewport()
	{
		var maximumFirstLine = Math.Max(0, LineCount - PageSize);
		_firstVisibleLine = Math.Clamp(_firstVisibleLine, 0, maximumFirstLine);
		var textWidth = VisibleTextWidth;
		_horizontalOffset = Math.Clamp(
			_horizontalOffset,
			0,
			Math.Max(0, MaxLineLength - textWidth));
	}

	private void RaiseVisibleRangeChanged() =>
		VisibleRangeChanged?.Invoke(this, EventArgs.Empty);

	private static string SliceColumns(string value, int startColumn, int maximumColumns)
	{
		if (string.IsNullOrEmpty(value) || maximumColumns <= 0)
			return string.Empty;

		var output = new StringBuilder(Math.Min(value.Length, maximumColumns));
		var skippedColumns = 0;
		var writtenColumns = 0;
		foreach (var rune in value.EnumerateRunes())
		{
			var columns = Math.Max(0, rune.GetColumns());
			if (skippedColumns + columns <= startColumn)
			{
				skippedColumns += columns;
				continue;
			}
			if (writtenColumns + columns > maximumColumns)
				break;

			output.Append(rune);
			writtenColumns += columns;
		}

		return output.ToString();
	}
}
