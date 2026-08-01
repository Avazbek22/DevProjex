using System.Drawing;
using System.Text;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;

namespace DevProjex.Terminal.Tui;

internal sealed class TerminalVirtualizedPreviewView : View
{
	private readonly List<PreviewTextSearchMatch> _searchMatches = [];
	private IPreviewTextDocument? _document;
	private string _searchQuery = string.Empty;
	private int _currentSearchMatchIndex = -1;
	private readonly Rune _horizontalScrollThumb;
	private readonly Rune _verticalScrollThumb;
	private readonly Rune _scrollTrack;

	public TerminalVirtualizedPreviewView(
		bool useUnicode = true,
		bool showScrollBars = true)
	{
		_horizontalScrollThumb = new Rune(useUnicode ? '━' : '-');
		_verticalScrollThumb = new Rune(useUnicode ? '┃' : '|');
		_scrollTrack = new Rune(useUnicode ? '·' : '.');
		CanFocus = true;
		SchemeName = TerminalWorkspaceTheme.Base;
		if (!showScrollBars)
			return;

		ViewportSettings |= ViewportSettingsFlags.HasScrollBars;
		VerticalScrollBar.SchemeName = TerminalWorkspaceTheme.Secondary;
		HorizontalScrollBar.SchemeName = TerminalWorkspaceTheme.Secondary;
		VerticalScrollBar.DrawingContent += (_, _) =>
			DrawScrollTrack(VerticalScrollBar, _scrollTrack, vertical: true);
		HorizontalScrollBar.DrawingContent += (_, _) =>
			DrawScrollTrack(HorizontalScrollBar, _scrollTrack, vertical: false);
		VerticalScrollBar.Slider.DrawingContent += (_, _) =>
			DrawScrollThumb(VerticalScrollBar.Slider, _verticalScrollThumb);
		HorizontalScrollBar.Slider.DrawingContent += (_, _) =>
			DrawScrollThumb(HorizontalScrollBar.Slider, _horizontalScrollThumb);
		ViewportChanged += (_, _) => RaiseVisibleRangeChanged();
	}

	public event EventHandler? VisibleRangeChanged;

	public int FirstVisibleLine => Viewport.Y;

	public int HorizontalOffset => Viewport.X;

	public int LineCount => _document?.LineCount ?? 1;

	public int PageSize => Math.Max(1, Viewport.Height);

	public int VisibleLastLine =>
		Math.Min(LineCount, FirstVisibleLine + PageSize);

	public int MaxLineLength => _document?.MaxLineLength ?? 0;

	public int VisibleTextWidth => Math.Max(1, Viewport.Width);

	public IReadOnlyList<PreviewDocumentSection> Sections =>
		_document?.Sections ?? [];

	public bool HasHorizontalOverflow => MaxLineLength > VisibleTextWidth;

	public bool HasVerticalOverflow => LineCount > PageSize;

	public string SearchQuery => _searchQuery;

	public int SearchMatchCount => _searchMatches.Count;

	public int CurrentSearchMatchOrdinal =>
		_currentSearchMatchIndex >= 0 ? _currentSearchMatchIndex + 1 : 0;

	public PreviewTextSearchMatch? CurrentSearchMatch =>
		_currentSearchMatchIndex >= 0
			? _searchMatches[_currentSearchMatchIndex]
			: null;

	public bool SetDocument(IPreviewTextDocument document, bool preserveViewport)
	{
		ArgumentNullException.ThrowIfNull(document);
		if (ReferenceEquals(_document, document))
			return false;

		var previousLocation = Viewport.Location;
		_document = document;
		SetContentSize(new Size(
			Math.Max(1, document.MaxLineLength),
			Math.Max(1, document.LineCount)));
		_searchMatches.Clear();
		_currentSearchMatchIndex = -1;
		ScrollTo(
			preserveViewport ? previousLocation.Y : 0,
			preserveViewport ? previousLocation.X : 0);
		return true;
	}

	public void ScrollTo(int zeroBasedLine, int horizontalOffset)
	{
		var maximumFirstLine = Math.Max(0, LineCount - PageSize);
		var firstLine = Math.Clamp(zeroBasedLine, 0, maximumFirstLine);
		var maximumColumn = Math.Max(0, MaxLineLength - VisibleTextWidth);
		var firstColumn = Math.Clamp(horizontalOffset, 0, maximumColumn);
		Viewport = new Rectangle(
			firstColumn,
			firstLine,
			Viewport.Width,
			Viewport.Height);
		SetNeedsDraw();
		RaiseVisibleRangeChanged();
	}

	public PreviewTextSearchMatch? SetSearchQuery(
		string? query,
		int startLine,
		int startColumn = -1)
	{
		_searchQuery = query?.Trim() ?? string.Empty;
		_searchMatches.Clear();
		_searchMatches.AddRange(
			_document is null
				? []
				: PreviewTextDocumentSearch.FindAll(_document, _searchQuery));
		return FindNextSearchMatch(startLine, startColumn, reverse: false);
	}

	public void BeginSearch(string query)
	{
		_searchQuery = query.Trim();
		_searchMatches.Clear();
		_currentSearchMatchIndex = -1;
		SetNeedsDraw();
		RaiseVisibleRangeChanged();
	}

	public PreviewTextSearchMatch? ApplySearchResults(
		string query,
		IReadOnlyList<PreviewTextSearchMatch> matches,
		int startLine,
		int startColumn = -1)
	{
		if (!string.Equals(_searchQuery, query.Trim(), StringComparison.Ordinal))
			return null;

		_searchMatches.Clear();
		_searchMatches.AddRange(matches);
		_currentSearchMatchIndex = -1;
		return FindNextSearchMatch(startLine, startColumn, reverse: false);
	}

	public int FindNext(string query, int startLine) =>
		SetSearchQuery(query, startLine)?.Line ?? -1;

	public void ClearSearch()
	{
		_searchQuery = string.Empty;
		_searchMatches.Clear();
		_currentSearchMatchIndex = -1;
		SetNeedsDraw();
		RaiseVisibleRangeChanged();
	}

	public PreviewTextSearchMatch? FindNextSearchMatch(
		int startLine,
		int startColumn,
		bool reverse)
	{
		if (_searchMatches.Count == 0)
			return null;

		var index = reverse
			? _searchMatches.FindLastIndex(match =>
				match.Line < startLine ||
				match.Line == startLine && match.Column < startColumn)
			: _searchMatches.FindIndex(match =>
				match.Line > startLine ||
				match.Line == startLine && match.Column > startColumn);
		if (index < 0)
			index = reverse ? _searchMatches.Count - 1 : 0;

		_currentSearchMatchIndex = index;
		return _searchMatches[index];
	}

	protected override bool OnDrawingContent(DrawContext? context)
	{
		if (_document is null)
			return true;

		SetAttributeForRole(VisualRole.ReadOnly);
		for (var row = 0; row < Viewport.Height; row++)
		{
			var lineIndex = FirstVisibleLine + row;
			if (lineIndex >= LineCount)
				break;

			var line = _document.GetLineText(lineIndex + 1);
			AddStr(0, row, SliceColumns(line, HorizontalOffset, VisibleTextWidth));
		}

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
			ScrollTo(FirstVisibleLine - 3, HorizontalOffset);
			return true;
		}
		if (mouse.Flags.HasFlag(MouseFlags.WheeledDown))
		{
			ScrollTo(FirstVisibleLine + 3, HorizontalOffset);
			return true;
		}
		if (mouse.Flags.HasFlag(MouseFlags.WheeledLeft))
		{
			ScrollTo(FirstVisibleLine, HorizontalOffset - 4);
			return true;
		}
		if (mouse.Flags.HasFlag(MouseFlags.WheeledRight))
		{
			ScrollTo(FirstVisibleLine, HorizontalOffset + 4);
			return true;
		}

		return base.OnMouseEvent(mouse);
	}

	private void RaiseVisibleRangeChanged() =>
		VisibleRangeChanged?.Invoke(this, EventArgs.Empty);

	private static void DrawScrollThumb(View slider, Rune glyph)
	{
		slider.SetAttributeForRole(VisualRole.ReadOnly);
		slider.FillRect(
			new Rectangle(Point.Empty, slider.Viewport.Size),
			glyph);
	}

	private static void DrawScrollTrack(View scrollBar, Rune glyph, bool vertical)
	{
		scrollBar.SetAttributeForRole(VisualRole.ReadOnly);
		var track = vertical
			? new Rectangle(0, 1, scrollBar.Viewport.Width, Math.Max(0, scrollBar.Viewport.Height - 2))
			: new Rectangle(1, 0, Math.Max(0, scrollBar.Viewport.Width - 2), scrollBar.Viewport.Height);
		if (track.Width > 0 && track.Height > 0)
			scrollBar.FillRect(track, glyph);
	}

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
