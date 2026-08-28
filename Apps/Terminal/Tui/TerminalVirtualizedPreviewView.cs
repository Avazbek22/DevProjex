using System.Drawing;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;

namespace DevProjex.Terminal.Tui;

internal sealed class TerminalVirtualizedPreviewView : View
{
	private const int MaximumCachedLineCount = 256;
	private const int MaximumCachedCharacterCount = 1_000_000;
	private readonly List<PreviewTextSearchMatch> _searchMatches = [];
	private readonly Dictionary<int, string> _cachedLines = [];
	private readonly Queue<int> _cachedLineOrder = new(MaximumCachedLineCount);
	private Dictionary<int, PreviewTextSearchMatch[]> _searchMatchesByLine = [];
	private IPreviewTextDocument? _document;
	private string _searchQuery = string.Empty;
	private int _currentSearchMatchIndex = -1;
	private Dictionary<int, PreviewRedactionSpan[]> _redactionsByLine = [];
	private List<PreviewRedactionSpan> _redactionOccurrences = [];
	private string? _activeRedactionOccurrenceId;
	private int _maximumDisplayColumns;
	private int _cachedCharacterCount;
	private int[] _wrappedLineStarts = [0, 1];
	private int[][] _wrappedSegmentColumns = [[0]];
	private int _wrappedWidth;
	private bool _updatingContentGeometry;
	private long _viewportChangeRevision;
	private bool _wordWrap;

	public TerminalVirtualizedPreviewView(
		bool useUnicode = true,
		bool showScrollBars = true)
	{
		CanFocus = true;
		SchemeName = TerminalWorkspaceTheme.Base;
		ViewportChanged += (_, _) => HandleViewportChanged();
		if (!showScrollBars)
			return;

		TerminalScrollBarStyle.Apply(this, useUnicode, vertical: true, horizontal: true);
	}

	public event EventHandler? VisibleRangeChanged;
	public event EventHandler<TerminalPreviewRedactionToggleRequestedEventArgs>? RedactionToggleRequested;
	public event EventHandler? CommandLineRequested;

	protected override bool OnKeyDown(Key key)
	{
		return TerminalInteractiveView.TryActivateCommandLine(
			key,
			() => CommandLineRequested?.Invoke(this, EventArgs.Empty)) || base.OnKeyDown(key);
	}

	public int FirstVisibleLine => ResolveDocumentPosition(Viewport.Y).Line;

	public int HorizontalOffset => _wordWrap
		? ResolveDocumentPosition(Viewport.Y).DisplayColumn
		: Viewport.X;

	public int LineCount => _document?.LineCount ?? 1;

	public int PageSize => Math.Max(1, Viewport.Height);

	public int FirstVisibleContentRow => Viewport.Y;

	public int ContentRowCount => _wordWrap ? WrappedRowCount : LineCount;

	public int VisibleLastLine
	{
		get
		{
			if (!_wordWrap)
				return Math.Min(LineCount, FirstVisibleLine + PageSize);
			var lastRow = Math.Min(ContentRowCount - 1, Viewport.Y + PageSize - 1);
			return Math.Min(LineCount, ResolveDocumentPosition(lastRow).Line + 1);
		}
	}

	public int MaxLineLength => _maximumDisplayColumns;

	public int VisibleTextWidth => Math.Max(1, Viewport.Width);

	public IReadOnlyList<PreviewDocumentSection> Sections =>
		_document?.Sections ?? [];

	public bool HasHorizontalOverflow => !_wordWrap && MaxLineLength > VisibleTextWidth;
	public bool WordWrap => _wordWrap;

	public bool HasVerticalOverflow => ContentRowCount > PageSize;

	public string SearchQuery => _searchQuery;

	public int SearchMatchCount => _searchMatches.Count;
	public bool IsSearchCapped { get; private set; }

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

		var previousLine = FirstVisibleLine;
		var previousColumn = HorizontalOffset;
		_document = document;
		ClearLineCache();
		_maximumDisplayColumns = document.MaxLineLength;
		InvalidateWrappedLayout();
		RebuildRedactionIndex(document.Redactions);
		if (_activeRedactionOccurrenceId is not null &&
			!_redactionOccurrences.Any(span => span.OccurrenceId == _activeRedactionOccurrenceId))
		{
			_activeRedactionOccurrenceId = null;
		}
		ApplyContentGeometry();
		ReplaceSearchMatches([]);
		_currentSearchMatchIndex = -1;
		ScrollTo(
			preserveViewport ? previousLine : 0,
			preserveViewport ? previousColumn : 0);
		return true;
	}

	public bool ToggleWordWrap()
	{
		var anchor = ResolveDocumentPosition(Viewport.Y);
		_wordWrap = !_wordWrap;
		InvalidateWrappedLayout();
		ApplyContentGeometry();
		ScrollTo(anchor.Line, anchor.DisplayColumn);
		SetNeedsDraw();
		return _wordWrap;
	}

	public bool MoveActiveRedaction(bool reverse)
	{
		var occurrences = _redactionOccurrences;
		if (occurrences.Count == 0)
			return false;

		var currentIndex = _activeRedactionOccurrenceId is null
			? -1
			: occurrences.FindIndex(span => span.OccurrenceId == _activeRedactionOccurrenceId);
		var nextIndex = reverse
			? currentIndex <= 0 ? occurrences.Count - 1 : currentIndex - 1
			: currentIndex < 0 || currentIndex == occurrences.Count - 1 ? 0 : currentIndex + 1;
		var next = occurrences[nextIndex];
		_activeRedactionOccurrenceId = next.OccurrenceId;
		var displayColumn = GetDisplayColumn(next.LineNumber - 1, next.StartColumn);
		ScrollTo(Math.Max(0, next.LineNumber - 1), Math.Max(0, displayColumn - 2));
		SetNeedsDraw();
		return true;
	}

	public int GetDisplayColumn(int zeroBasedLine, int utf16Column)
	{
		if (_document is null || zeroBasedLine < 0 || zeroBasedLine >= _document.LineCount)
			return Math.Max(0, utf16Column);

		var line = GetDisplayLine(zeroBasedLine + 1);
		EnsureContentWidth(line);
		var characterIndex = Math.Clamp(utf16Column, 0, line.Length);
		return GetColumns(line.AsSpan(0, characterIndex));
	}

	public bool TryToggleActiveRedaction()
	{
		var occurrenceId = ResolveActiveOrFirstVisibleOccurrenceId();
		if (occurrenceId is null)
			return false;

		_activeRedactionOccurrenceId = occurrenceId;
		RedactionToggleRequested?.Invoke(
			this,
			new TerminalPreviewRedactionToggleRequestedEventArgs(occurrenceId));
		return true;
	}

	public void ScrollTo(int zeroBasedLine, int horizontalOffset)
	{
		var normalizedLine = Math.Clamp(zeroBasedLine, 0, Math.Max(0, LineCount - 1));
		var contentRow = _wordWrap
			? GetWrappedRow(normalizedLine, horizontalOffset)
			: normalizedLine;
		ScrollToContentRow(contentRow, horizontalOffset);
	}

	public void ScrollToContentRow(int contentRow, int horizontalOffset)
	{
		var maximumFirstRow = Math.Max(0, ContentRowCount - PageSize);
		var firstRow = Math.Clamp(contentRow, 0, maximumFirstRow);
		var maximumColumn = Math.Max(0, MaxLineLength - VisibleTextWidth);
		var firstColumn = _wordWrap
			? 0
			: Math.Clamp(horizontalOffset, 0, maximumColumn);
		var viewportChangeRevision = _viewportChangeRevision;
		Viewport = new Rectangle(
			firstColumn,
			firstRow,
			Viewport.Width,
			Viewport.Height);
		SetNeedsDraw();
		if (_viewportChangeRevision == viewportChangeRevision)
			RaiseVisibleRangeChanged();
	}

	public PreviewTextSearchMatch? SetSearchQuery(
		string? query,
		int startLine,
		int startColumn = -1)
	{
		_searchQuery = query?.Trim() ?? string.Empty;
		var result = _document is null
			? new PreviewTextSearchResult([], IsCapped: false)
			: PreviewTextDocumentSearch.Find(_document, _searchQuery);
		ReplaceSearchMatches(result.Matches, result.IsCapped);
		return FindNextSearchMatch(startLine, startColumn, reverse: false);
	}

	public void BeginSearch(string query)
	{
		_searchQuery = query.Trim();
		ReplaceSearchMatches([]);
		_currentSearchMatchIndex = -1;
		SetNeedsDraw();
		RaiseVisibleRangeChanged();
	}

	public PreviewTextSearchMatch? ApplySearchResults(
		string query,
		PreviewTextSearchResult result,
		int startLine,
		int startColumn = -1)
	{
		if (!string.Equals(_searchQuery, query.Trim(), StringComparison.Ordinal))
			return null;

		ReplaceSearchMatches(result.Matches, result.IsCapped);
		_currentSearchMatchIndex = -1;
		return FindNextSearchMatch(startLine, startColumn, reverse: false);
	}

	public int FindNext(string query, int startLine) =>
		SetSearchQuery(query, startLine)?.Line ?? -1;

	public void ClearSearch()
	{
		_searchQuery = string.Empty;
		ReplaceSearchMatches([]);
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
		SetNeedsDraw();
		return _searchMatches[index];
	}

	protected override bool OnDrawingContent(DrawContext? context)
	{
		if (_document is null)
			return true;
		ApplyContentGeometry();
		if (_wordWrap)
		{
			DrawWrappedContent();
			return true;
		}

		PrimeVisibleLineCache();
		var maximumVisibleWidth = _maximumDisplayColumns;
		for (var row = 0; row < Viewport.Height; row++)
		{
			var lineIndex = FirstVisibleLine + row;
			if (lineIndex >= LineCount)
				break;

			var line = GetDisplayLine(lineIndex + 1);
			maximumVisibleWidth = Math.Max(maximumVisibleWidth, GetColumns(line.AsSpan()));
			DrawPreviewLine(line, lineIndex + 1, row, HorizontalOffset);
		}
		if (maximumVisibleWidth > _maximumDisplayColumns)
			SetMaximumContentWidth(maximumVisibleWidth);

		return true;
	}

	private void DrawWrappedContent()
	{
		PrimeVisibleLineCache();
		for (var row = 0; row < Viewport.Height; row++)
		{
			var contentRow = Viewport.Y + row;
			if (contentRow >= ContentRowCount)
				break;
			var position = ResolveDocumentPosition(contentRow);
			var line = GetDisplayLine(position.Line + 1);
			DrawPreviewLine(line, position.Line + 1, row, position.DisplayColumn);
		}
	}

	protected override bool OnMouseEvent(Mouse mouse)
	{
		if (mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked))
		{
			SetFocus();
			var occurrenceId = mouse.Position is { } position
				? FindOccurrenceAt(position.X, position.Y)
				: null;
			if (occurrenceId is not null)
			{
				_activeRedactionOccurrenceId = occurrenceId;
				RedactionToggleRequested?.Invoke(
					this,
					new TerminalPreviewRedactionToggleRequestedEventArgs(occurrenceId));
			}
			return true;
		}
		if (mouse.Flags.HasFlag(MouseFlags.LeftButtonPressed))
		{
			SetFocus();
			return true;
		}
		if (mouse.Flags.HasFlag(MouseFlags.WheeledUp))
		{
			ScrollToContentRow(FirstVisibleContentRow - 3, HorizontalOffset);
			return true;
		}
		if (mouse.Flags.HasFlag(MouseFlags.WheeledDown))
		{
			ScrollToContentRow(FirstVisibleContentRow + 3, HorizontalOffset);
			return true;
		}
		if (mouse.Flags.HasFlag(MouseFlags.WheeledLeft))
		{
			if (!_wordWrap)
				ScrollTo(FirstVisibleLine, HorizontalOffset - 4);
			return true;
		}
		if (mouse.Flags.HasFlag(MouseFlags.WheeledRight))
		{
			if (!_wordWrap)
				ScrollTo(FirstVisibleLine, HorizontalOffset + 4);
			return true;
		}

		return base.OnMouseEvent(mouse);
	}

	private void DrawPreviewLine(
		string line,
		int lineNumber,
		int row,
		int displayOffset)
	{
		SetAttributeForRole(VisualRole.ReadOnly);
		AddStr(0, row, SliceColumns(line, displayOffset, VisibleTextWidth));
		if (_searchMatchesByLine.TryGetValue(lineNumber - 1, out var matches))
		{
			foreach (var match in matches)
			{
				DrawHighlight(
					line,
					match.Column,
					_searchQuery.Length,
					displayOffset,
					row,
					match == CurrentSearchMatch
						? VisualRole.HotFocus
						: VisualRole.HotNormal);
			}
		}
		if (!_redactionsByLine.TryGetValue(lineNumber, out var redactions))
			return;

		foreach (var span in redactions)
		{
			DrawHighlight(
				line,
				span.StartColumn,
				span.Length,
				displayOffset,
				row,
				span.OccurrenceId == _activeRedactionOccurrenceId
					? VisualRole.HotFocus
					: VisualRole.HotNormal);
		}
	}

	private void DrawHighlight(
		string line,
		int startIndex,
		int requestedLength,
		int displayOffset,
		int row,
		VisualRole role)
	{
		startIndex = Math.Clamp(startIndex, 0, line.Length);
		var length = Math.Clamp(requestedLength, 0, line.Length - startIndex);
		if (length == 0)
			return;

		var startColumn = GetColumns(line.AsSpan(0, startIndex));
		var endColumn = startColumn + GetColumns(line.AsSpan(startIndex, length));
		var visibleStart = Math.Max(startColumn, displayOffset);
		var visibleEnd = Math.Min(endColumn, displayOffset + VisibleTextWidth);
		if (visibleStart >= visibleEnd)
			return;

		var text = SliceColumns(
			line.Substring(startIndex, length),
			visibleStart - startColumn,
			visibleEnd - visibleStart);
		SetAttribute(GetAttributeForRole(role));
		AddStr(visibleStart - displayOffset, row, text);
	}

	private string? FindOccurrenceAt(int viewportColumn, int viewportRow)
	{
		var contentRow = Viewport.Y + viewportRow;
		if (contentRow < 0 || contentRow >= ContentRowCount)
			return null;
		var position = ResolveDocumentPosition(contentRow);
		var lineNumber = position.Line + 1;
		if (_document is null || !_redactionsByLine.TryGetValue(lineNumber, out var redactions))
			return null;

		var line = GetDisplayLine(lineNumber);
		var documentColumn = position.DisplayColumn + viewportColumn;
		foreach (var span in redactions)
		{
			var startIndex = Math.Clamp(span.StartColumn, 0, line.Length);
			var length = Math.Clamp(span.Length, 0, line.Length - startIndex);
			var startColumn = GetColumns(line.AsSpan(0, startIndex));
			var endColumn = startColumn + GetColumns(line.AsSpan(startIndex, length));
			if (documentColumn >= startColumn && documentColumn < endColumn)
				return span.OccurrenceId;
		}

		return null;
	}

	private string? ResolveActiveOrFirstVisibleOccurrenceId()
	{
		if (_redactionOccurrences.Count == 0)
			return null;
		if (_activeRedactionOccurrenceId is not null &&
			_redactionOccurrences.Any(span => span.OccurrenceId == _activeRedactionOccurrenceId))
		{
			return _activeRedactionOccurrenceId;
		}

		var firstVisibleLine = FirstVisibleLine + 1;
		var lastVisibleLine = VisibleLastLine;
		return _redactionOccurrences.FirstOrDefault(span =>
			span.LineNumber >= firstVisibleLine && span.LineNumber <= lastVisibleLine)?.OccurrenceId;
	}

	private void RebuildRedactionIndex(IReadOnlyList<PreviewRedactionSpan> redactions)
	{
		_redactionsByLine = redactions
			.GroupBy(static span => span.LineNumber)
			.ToDictionary(
				static group => group.Key,
				static group => group.OrderBy(static span => span.StartColumn).ToArray());
		_redactionOccurrences = redactions
			.GroupBy(static span => span.OccurrenceId, StringComparer.Ordinal)
			.Select(static group => group.OrderBy(static span => span.LineNumber)
				.ThenBy(static span => span.StartColumn)
				.First())
			.OrderBy(static span => span.LineNumber)
			.ThenBy(static span => span.StartColumn)
			.ToList();
	}

	private void ReplaceSearchMatches(
		IReadOnlyList<PreviewTextSearchMatch> matches,
		bool isCapped = false)
	{
		_searchMatches.Clear();
		_searchMatches.AddRange(matches);
		_searchMatchesByLine = matches
			.GroupBy(static match => match.Line)
			.ToDictionary(
				static group => group.Key,
				static group => group.OrderBy(static match => match.Column).ToArray());
		IsSearchCapped = isCapped;
	}

	private void HandleViewportChanged()
	{
		if (_updatingContentGeometry)
			return;

		if (_wordWrap && _document is not null && _wrappedWidth != VisibleTextWidth)
		{
			var anchor = ResolveCachedWrappedPosition(Viewport.Y);
			InvalidateWrappedLayout();
			ApplyContentGeometry();
			var contentRow = GetWrappedRow(anchor.Line, anchor.DisplayColumn);
			var maximumFirstRow = Math.Max(0, ContentRowCount - PageSize);
			_updatingContentGeometry = true;
			try
			{
				Viewport = new Rectangle(
					0,
					Math.Clamp(contentRow, 0, maximumFirstRow),
					Viewport.Width,
					Viewport.Height);
			}
			finally
			{
				_updatingContentGeometry = false;
			}
		}

		_viewportChangeRevision++;
		RaiseVisibleRangeChanged();
	}

	private (int Line, int DisplayColumn) ResolveCachedWrappedPosition(int contentRow)
	{
		if (_document is null || _wrappedWidth <= 0 ||
		    _wrappedLineStarts.Length != _document.LineCount + 1)
		{
			return (Math.Clamp(contentRow, 0, Math.Max(0, LineCount - 1)), 0);
		}

		var normalizedRow = Math.Clamp(contentRow, 0, Math.Max(0, _wrappedLineStarts[^1] - 1));
		var line = Array.BinarySearch(_wrappedLineStarts, normalizedRow);
		if (line < 0)
			line = ~line - 1;
		else if (line == LineCount)
			line--;
		line = Math.Clamp(line, 0, Math.Max(0, LineCount - 1));
		var segment = normalizedRow - _wrappedLineStarts[line];
		return (line, _wrappedSegmentColumns[line][segment]);
	}

	private int WrappedRowCount
	{
		get
		{
			EnsureWrappedLayout();
			return _wrappedLineStarts[^1];
		}
	}

	internal (int Line, int DisplayColumn) ResolveDocumentPosition(int contentRow)
	{
		if (!_wordWrap || _document is null)
		{
			return (
				Math.Clamp(contentRow, 0, Math.Max(0, LineCount - 1)),
				Math.Max(0, Viewport.X));
		}

		EnsureWrappedLayout();
		var normalizedRow = Math.Clamp(contentRow, 0, Math.Max(0, WrappedRowCount - 1));
		var line = Array.BinarySearch(_wrappedLineStarts, normalizedRow);
		if (line < 0)
			line = ~line - 1;
		else if (line == LineCount)
			line--;
		line = Math.Clamp(line, 0, Math.Max(0, LineCount - 1));
		var segment = normalizedRow - _wrappedLineStarts[line];
		return (line, _wrappedSegmentColumns[line][segment]);
	}

	private int GetWrappedRow(int zeroBasedLine, int displayColumn)
	{
		EnsureWrappedLayout();
		var line = Math.Clamp(zeroBasedLine, 0, Math.Max(0, LineCount - 1));
		var segmentColumns = _wrappedSegmentColumns[line];
		var segment = Array.BinarySearch(segmentColumns, Math.Max(0, displayColumn));
		if (segment < 0)
			segment = ~segment - 1;
		segment = Math.Clamp(segment, 0, segmentColumns.Length - 1);
		return _wrappedLineStarts[line] + segment;
	}

	private void EnsureWrappedLayout()
	{
		if (_document is null)
		{
			_wrappedLineStarts = [0, 1];
			_wrappedSegmentColumns = [[0]];
			_wrappedWidth = Math.Max(1, VisibleTextWidth);
			return;
		}

		var width = Math.Max(1, VisibleTextWidth);
		if (_wrappedWidth == width && _wrappedLineStarts.Length == _document.LineCount + 1)
			return;

		var starts = new int[_document.LineCount + 1];
		var segmentColumns = new int[_document.LineCount][];
		var row = 0;
		for (var line = 0; line < _document.LineCount; line++)
		{
			starts[line] = row;
			segmentColumns[line] = BuildWrappedSegmentColumns(
				_document.GetLineText(line + 1),
				width);
			row = checked(row + segmentColumns[line].Length);
		}
		starts[^1] = Math.Max(1, row);
		_wrappedLineStarts = starts;
		_wrappedSegmentColumns = segmentColumns;
		_wrappedWidth = width;
	}

	internal static int[] BuildWrappedSegmentColumns(string value, int width)
	{
		var effectiveWidth = Math.Max(1, width);
		List<int>? starts = null;
		var displayColumn = 0;
		var segmentWidth = 0;
		foreach (var rune in value.EnumerateRunes())
		{
			var runeWidth = Math.Max(0, rune.GetColumns());
			if (runeWidth > 0 && segmentWidth > 0 && segmentWidth + runeWidth > effectiveWidth)
			{
				starts ??= [0];
				starts.Add(displayColumn);
				segmentWidth = 0;
			}
			displayColumn += runeWidth;
			segmentWidth += runeWidth;
		}
		return starts?.ToArray() ?? [0];
	}

	private void InvalidateWrappedLayout()
	{
		_wrappedWidth = 0;
		_wrappedLineStarts = [0, 1];
		_wrappedSegmentColumns = [[0]];
	}

	private void ApplyContentGeometry()
	{
		if (_document is null || _updatingContentGeometry)
			return;

		_updatingContentGeometry = true;
		try
		{
			SetContentSize(new Size(
				_wordWrap ? Math.Max(1, VisibleTextWidth) : Math.Max(1, _maximumDisplayColumns),
				Math.Max(1, ContentRowCount)));
		}
		finally
		{
			_updatingContentGeometry = false;
		}
	}

	private void RaiseVisibleRangeChanged() =>
		VisibleRangeChanged?.Invoke(this, EventArgs.Empty);

	internal string GetDisplayLine(int lineNumber)
	{
		if (_document is null)
			return string.Empty;
		if (_cachedLines.TryGetValue(lineNumber, out var cached))
			return cached;

		var line = _document.GetLineText(lineNumber);
		CacheLine(lineNumber, line);
		return line;
	}

	internal void PrimeVisibleLineCache()
	{
		var document = _document;
		if (document is null || Viewport.Height <= 0)
			return;

		var firstLine = FirstVisibleLine + 1;
		var lastLine = Math.Min(
			document.LineCount,
			firstLine + Math.Min(Viewport.Height, MaximumCachedLineCount) - 1);
		var hasMissingLine = false;
		for (var lineNumber = firstLine; lineNumber <= lastLine; lineNumber++)
		{
			if (!_cachedLines.ContainsKey(lineNumber))
			{
				hasMissingLine = true;
				break;
			}
		}
		if (!hasMissingLine)
			return;

		document.VisitLines(
			firstLine,
			lastLine,
			(lineNumber, line) =>
			{
				if (!_cachedLines.ContainsKey(lineNumber))
					CacheLine(lineNumber, line.ToString());
				return true;
			});
	}

	private void CacheLine(int lineNumber, string line)
	{
		if (line.Length > MaximumCachedCharacterCount)
			return;

		while (_cachedLineOrder.Count > 0 &&
		       (_cachedLines.Count >= MaximumCachedLineCount ||
		        _cachedCharacterCount + line.Length > MaximumCachedCharacterCount))
		{
			var expiredLineNumber = _cachedLineOrder.Dequeue();
			if (_cachedLines.Remove(expiredLineNumber, out var expiredLine))
				_cachedCharacterCount -= expiredLine.Length;
		}

		_cachedLines[lineNumber] = line;
		_cachedLineOrder.Enqueue(lineNumber);
		_cachedCharacterCount += line.Length;
	}

	private void ClearLineCache()
	{
		_cachedLines.Clear();
		_cachedLineOrder.Clear();
		_cachedCharacterCount = 0;
	}

	internal static string SliceColumns(string value, int startColumn, int maximumColumns)
	{
		if (string.IsNullOrEmpty(value) || maximumColumns <= 0)
			return string.Empty;

		var output = new StringBuilder(Math.Min(value.Length, maximumColumns));
		var sourceColumn = 0;
		var writtenColumns = 0;
		foreach (var rune in value.EnumerateRunes())
		{
			var columns = Math.Max(0, rune.GetColumns());
			var runeStart = sourceColumn;
			var runeEnd = runeStart + columns;
			sourceColumn = runeEnd;
			if (columns == 0)
			{
				if (runeStart > startColumn)
					output.Append(rune);
				continue;
			}
			if (runeEnd <= startColumn)
				continue;
			if (runeStart < startColumn)
			{
				var padding = Math.Min(
					runeEnd - startColumn,
					maximumColumns - writtenColumns);
				output.Append(' ', padding);
				writtenColumns += padding;
				if (writtenColumns == maximumColumns)
					break;
				continue;
			}
			if (writtenColumns + columns > maximumColumns)
				break;

			output.Append(rune);
			writtenColumns += columns;
		}

		return output.ToString();
	}

	private static int GetColumns(ReadOnlySpan<char> value)
	{
		var columns = 0;
		foreach (var rune in value.EnumerateRunes())
			columns += Math.Max(0, rune.GetColumns());
		return columns;
	}

	private void EnsureContentWidth(string line)
	{
		var displayColumns = GetColumns(line.AsSpan());
		if (displayColumns > _maximumDisplayColumns)
			SetMaximumContentWidth(displayColumns);
	}

	private void SetMaximumContentWidth(int displayColumns)
	{
		_maximumDisplayColumns = displayColumns;
		if (!_wordWrap)
			ApplyContentGeometry();
	}
}

internal sealed class TerminalPreviewRedactionToggleRequestedEventArgs(string occurrenceId) : EventArgs
{
	public string OccurrenceId { get; } = occurrenceId;
}
