namespace DevProjex.Mcp;

internal sealed class McpBatchTextLineView
{
	private readonly string _text;
	private readonly int _totalLines;
	private readonly Dictionary<int, int> _lineOffsets = [];

	public McpBatchTextLineView(
		string text,
		IEnumerable<int> requestedStartLines,
		CancellationToken cancellationToken = default)
	{
		_text = text ?? throw new ArgumentNullException(nameof(text));
		ArgumentNullException.ThrowIfNull(requestedStartLines);
		cancellationToken.ThrowIfCancellationRequested();
		if (text.Length == 0)
			return;

		var requested = requestedStartLines
			.Where(static line => line > 0)
			.Distinct()
			.Order()
			.ToArray();
		var requestedIndex = 0;
		var currentLine = 1;
		if (requestedIndex < requested.Length && requested[requestedIndex] == currentLine)
		{
			_lineOffsets.Add(currentLine, 0);
			requestedIndex++;
		}
		var nextCancellationOffset = 0;
		for (var index = 0; index < text.Length; index++)
		{
			if (index >= nextCancellationOffset)
			{
				cancellationToken.ThrowIfCancellationRequested();
				nextCancellationOffset = index + 4096;
			}
			if (text[index] == '\r')
			{
				if (index + 1 < text.Length && text[index + 1] == '\n')
					index++;
			}
			else if (text[index] != '\n')
			{
				continue;
			}

			currentLine++;
			while (requestedIndex < requested.Length && requested[requestedIndex] < currentLine)
				requestedIndex++;
			if (requestedIndex < requested.Length && requested[requestedIndex] == currentLine)
			{
				_lineOffsets.Add(currentLine, index + 1);
				requestedIndex++;
			}
		}
		_totalLines = currentLine;
		FullTextScans = 1;
	}

	public int FullTextScans { get; private set; }
	public int TotalLines => _totalLines;

	public string? DescribeInvalidRange(McpGetFileRange range)
	{
		if (range.IsWholeFile || _totalLines > 0 && range.StartLine >= 1 &&
			range.StartLine <= _totalLines && range.EndLine >= range.StartLine)
			return null;
		var boundary = _totalLines == 0 ? "file is empty" : $"valid lines 1-{_totalLines}";
		return $"requested lines {range.StartLine}-{range.EndLine}; {boundary}";
	}

	public McpTextPage Slice(
		int? startLine,
		int? endLine,
		int maximumLines,
		int maximumCharacters,
		CancellationToken cancellationToken,
		int? startColumn = null)
	{
		var start = startLine ?? 1;
		return McpTextRanges.SliceFromKnownLine(
			_text,
			_totalLines,
			start,
			_lineOffsets.GetValueOrDefault(start, -1),
			startLine,
			endLine,
			maximumLines,
			maximumCharacters,
			cancellationToken,
			startColumn);
	}
}
