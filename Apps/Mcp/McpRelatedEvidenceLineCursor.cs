namespace DevProjex.Mcp;

internal sealed class McpRelatedEvidenceLineCursor
{
	private readonly string _content;
	private readonly Dictionary<int, (int Start, int End)> _ranges = [];

	public McpRelatedEvidenceLineCursor(string content, IEnumerable<int> requestedLines)
	{
		_content = content ?? throw new ArgumentNullException(nameof(content));
		ArgumentNullException.ThrowIfNull(requestedLines);
		var lineNumber = 1;
		var start = 0;
		foreach (var requestedLine in requestedLines
			.Where(static line => line > 0)
			.Distinct()
			.Order())
		{
			while (lineNumber < requestedLine)
			{
				var lineFeed = FindLineFeed(start);
				if (lineFeed < 0)
					return;
				start = lineFeed + 1;
				lineNumber++;
			}

			var end = FindLineFeed(start);
			var lineEnd = end < 0 ? content.Length : end;
			if (lineEnd > start && content[lineEnd - 1] == '\r')
				lineEnd--;
			_ranges.Add(requestedLine, (start, lineEnd));
			if (end < 0)
				return;
			start = end + 1;
			lineNumber++;
		}
	}

	public long LineBoundarySearches { get; private set; }

	public bool TryReadLine(
		int lineNumber,
		out string line,
		out int lineStart,
		out int lineEnd)
	{
		line = string.Empty;
		lineStart = 0;
		lineEnd = 0;
		if (!_ranges.TryGetValue(lineNumber, out var range))
			return false;
		line = _content[range.Start..range.End];
		lineStart = range.Start;
		lineEnd = range.End;
		return true;
	}

	private int FindLineFeed(int start)
	{
		LineBoundarySearches++;
		return _content.IndexOf('\n', start);
	}
}
