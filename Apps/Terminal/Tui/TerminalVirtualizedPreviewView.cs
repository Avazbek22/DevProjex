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
	private IPreviewTextDocument? _document;
	private string _searchQuery = string.Empty;
	private int _currentSearchMatchIndex = -1;
	private Dictionary<int, PreviewRedactionSpan[]> _redactionsByLine = [];
	private List<PreviewRedactionSpan> _redactionOccurrences = [];
	private string? _activeRedactionOccurrenceId;
	private int _maximumDisplayColumns;
	private int _cachedCharacterCount;
	private long _viewportChangeRevision;

	public TerminalVirtualizedPreviewView(
		bool useUnicode = true,
		bool showScrollBars = true)
	{
		CanFocus = true;
		SchemeName = TerminalWorkspaceTheme.Base;
		if (!showScrollBars)
			return;

		TerminalScrollBarStyle.Apply(this, useUnicode, vertical: true, horizontal: true);
		ViewportChanged += (_, _) =>
		{
			_viewportChangeRevision++;
			RaiseVisibleRangeChanged();
		};
	}

	public event EventHandler? VisibleRangeChanged;
	public event EventHandler<TerminalPreviewRedactionToggleRequestedEventArgs>? RedactionToggleRequested;
	public event EventHandler? CommandLineRequested;

	protected override bool OnKeyDown(Key key)
	{
		if (!TerminalWorkspaceCommandKey.IsActivation(key))
			return base.OnKeyDown(key);
		CommandLineRequested?.Invoke(this, EventArgs.Empty);
		return true;
	}

	public int FirstVisibleLine => Viewport.Y;

	public int HorizontalOffset => Viewport.X;

	public int LineCount => _document?.LineCount ?? 1;

	public int PageSize => Math.Max(1, Viewport.Height);

	public int VisibleLastLine =>
		Math.Min(LineCount, FirstVisibleLine + PageSize);

	public int MaxLineLength => _maximumDisplayColumns;

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
		ClearLineCache();
		_maximumDisplayColumns = document.MaxLineLength;
		RebuildRedactionIndex(document.Redactions);
		if (_activeRedactionOccurrenceId is not null &&
			!_redactionOccurrences.Any(span => span.OccurrenceId == _activeRedactionOccurrenceId))
		{
			_activeRedactionOccurrenceId = null;
		}
		SetContentSize(new Size(
			Math.Max(1, _maximumDisplayColumns),
			Math.Max(1, document.LineCount)));
		_searchMatches.Clear();
		_currentSearchMatchIndex = -1;
		ScrollTo(
			preserveViewport ? previousLocation.Y : 0,
			preserveViewport ? previousLocation.X : 0);
		return true;
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
		var maximumFirstLine = Math.Max(0, LineCount - PageSize);
		var firstLine = Math.Clamp(zeroBasedLine, 0, maximumFirstLine);
		var maximumColumn = Math.Max(0, MaxLineLength - VisibleTextWidth);
		var firstColumn = Math.Clamp(horizontalOffset, 0, maximumColumn);
		var viewportChangeRevision = _viewportChangeRevision;
		Viewport = new Rectangle(
			firstColumn,
			firstLine,
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

		PrimeVisibleLineCache();
		var maximumVisibleWidth = _maximumDisplayColumns;
		for (var row = 0; row < Viewport.Height; row++)
		{
			var lineIndex = FirstVisibleLine + row;
			if (lineIndex >= LineCount)
				break;

			var line = GetDisplayLine(lineIndex + 1);
			maximumVisibleWidth = Math.Max(maximumVisibleWidth, GetColumns(line.AsSpan()));
			DrawPreviewLine(line, lineIndex + 1, row);
		}
		if (maximumVisibleWidth > _maximumDisplayColumns)
			SetContentWidth(maximumVisibleWidth);

		return true;
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

	private void DrawPreviewLine(string line, int lineNumber, int row)
	{
		SetAttributeForRole(VisualRole.ReadOnly);
		AddStr(0, row, SliceColumns(line, HorizontalOffset, VisibleTextWidth));
		if (!_redactionsByLine.TryGetValue(lineNumber, out var redactions))
			return;

		foreach (var span in redactions)
		{
			var startIndex = Math.Clamp(span.StartColumn, 0, line.Length);
			var length = Math.Clamp(span.Length, 0, line.Length - startIndex);
			if (length == 0)
				continue;

			var startColumn = GetColumns(line.AsSpan(0, startIndex));
			var endColumn = startColumn + GetColumns(line.AsSpan(startIndex, length));
			var visibleStart = Math.Max(startColumn, HorizontalOffset);
			var visibleEnd = Math.Min(endColumn, HorizontalOffset + VisibleTextWidth);
			if (visibleStart >= visibleEnd)
				continue;

			var skipColumns = visibleStart - startColumn;
			var text = SliceColumns(
				line.Substring(startIndex, length),
				skipColumns,
				visibleEnd - visibleStart);
			SetAttribute(GetAttributeForRole(
				span.OccurrenceId == _activeRedactionOccurrenceId
					? VisualRole.HotFocus
					: VisualRole.HotNormal));
			AddStr(visibleStart - HorizontalOffset, row, text);
		}
	}

	private string? FindOccurrenceAt(int viewportColumn, int viewportRow)
	{
		var lineNumber = FirstVisibleLine + viewportRow + 1;
		if (_document is null || !_redactionsByLine.TryGetValue(lineNumber, out var redactions))
			return null;

		var line = GetDisplayLine(lineNumber);
		var documentColumn = HorizontalOffset + viewportColumn;
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
			SetContentWidth(displayColumns);
	}

	private void SetContentWidth(int displayColumns)
	{
		_maximumDisplayColumns = displayColumns;
		SetContentSize(new Size(
			Math.Max(1, _maximumDisplayColumns),
			Math.Max(1, LineCount)));
	}
}

internal sealed class TerminalPreviewRedactionToggleRequestedEventArgs(string occurrenceId) : EventArgs
{
	public string OccurrenceId { get; } = occurrenceId;
}
