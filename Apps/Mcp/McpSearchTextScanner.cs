namespace DevProjex.Mcp;

internal readonly record struct McpTextLineRange(
	int LineNumber,
	int Offset,
	int Length);

internal sealed record McpSearchMatchContext(
	IReadOnlyList<int> MatchLineNumbers,
	IReadOnlyList<McpTextLineRange> Lines,
	bool StartsNewGroup,
	int SpanEndLineNumber = 0)
{
	public int MatchLineNumber => MatchLineNumbers[0];

	/// <summary>
	/// Whether one match runs from <see cref="MatchLineNumber"/> to <see cref="SpanEndLineNumber"/>.
	/// </summary>
	public bool SpansLines => SpanEndLineNumber > MatchLineNumber;
}

internal sealed record McpSearchTextScanResult(
	int TotalMatches,
	IReadOnlyList<McpSearchMatchContext> Matches,
	long ProtectedRangeComparisons);

internal readonly record struct McpSearchStreamResult(
	int TotalMatches,
	long ProtectedRangeComparisons);

internal readonly record struct McpSearchAppendResult(
	int WrittenMatches,
	bool Truncated);

internal static class McpSearchTextScanner
{
	public static McpSearchTextScanResult Scan(
		string content,
		McpSearchRegex regex,
		int contextLines,
		int maximumStoredMatches,
		CancellationToken cancellationToken) =>
		Scan(content, regex, contextLines, maximumStoredMatches, [], cancellationToken);

	public static McpSearchTextScanResult Scan(
		string content,
		McpSearchRegex regex,
		int contextLines,
		int maximumStoredMatches,
		IReadOnlyList<TransformedTextRange> protectedRanges,
		CancellationToken cancellationToken)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(maximumStoredMatches);
		var stored = new List<McpSearchMatchContext>(maximumStoredMatches);
		var result = ScanEach(
			content,
			regex,
			contextLines,
			protectedRanges,
			match =>
			{
				if (stored.Count < maximumStoredMatches)
					stored.Add(match);
			},
			cancellationToken);
		return new McpSearchTextScanResult(
			result.TotalMatches,
			MergeContexts(stored),
			result.ProtectedRangeComparisons);
	}

	public static McpSearchStreamResult ScanEach(
		string content,
		McpSearchRegex regex,
		int contextLines,
		IReadOnlyList<TransformedTextRange> protectedRanges,
		Action<McpSearchMatchContext> consume,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(content);
		ArgumentNullException.ThrowIfNull(regex);
		ArgumentNullException.ThrowIfNull(protectedRanges);
		ArgumentNullException.ThrowIfNull(consume);
		ArgumentOutOfRangeException.ThrowIfNegative(contextLines);
		cancellationToken.ThrowIfCancellationRequested();
		if (content.Length == 0)
			return new McpSearchStreamResult(0, 0);

		var previous = contextLines == 0
			? null
			: new Queue<McpTextLineRange>(contextLines);
		var active = new List<PendingMatch>(contextLines + 1);
		var totalMatches = 0;
		var protectedRangeIndex = 0;
		long protectedRangeComparisons = 0;

		void ProcessLine(McpTextLineRange line)
		{
			for (var index = active.Count - 1; index >= 0; index--)
			{
				var pending = active[index];
				pending.Lines.Add(line);
				pending.RemainingContextLines--;
				if (pending.RemainingContextLines == 0)
				{
					active.RemoveAt(index);
					consume(pending.ToContext());
				}
			}

			if (regex.IsMatch(
					content,
					line.Offset,
					line.Length,
					protectedRanges,
					ref protectedRangeIndex,
					ref protectedRangeComparisons,
					cancellationToken))
			{
				totalMatches++;
				var lines = previous is null
					? new List<McpTextLineRange>(1)
					: new List<McpTextLineRange>(previous);
				lines.Add(line);
				var pending = new PendingMatch(line.LineNumber, lines, contextLines);
				if (contextLines > 0)
					active.Add(pending);
				else
					consume(pending.ToContext());
			}

			if (previous is null)
				return;
			previous.Enqueue(line);
			if (previous.Count > contextLines)
				previous.Dequeue();
		}

		var lineStart = 0;
		var lineNumber = 1;
		for (var index = 0; index < content.Length; index++)
		{
			if ((index & 0xFFF) == 0)
				cancellationToken.ThrowIfCancellationRequested();
			if (content[index] is not ('\r' or '\n'))
				continue;

			ProcessLine(new McpTextLineRange(lineNumber++, lineStart, index - lineStart));
			if (content[index] == '\r' && index + 1 < content.Length && content[index + 1] == '\n')
				index++;
			lineStart = index + 1;
		}
		ProcessLine(new McpTextLineRange(lineNumber, lineStart, content.Length - lineStart));
		foreach (var pending in active)
			consume(pending.ToContext());

		return new McpSearchStreamResult(totalMatches, protectedRangeComparisons);
	}

	/// <summary>
	/// Matches the pattern against the whole text, so one match may cross line breaks. Each match
	/// is reported once, at its first line, with the lines it covers and the usual context around
	/// them. Matches that start on the same line are one match, as they would be per line.
	/// </summary>
	public static McpSearchStreamResult ScanEachMultiline(
		string content,
		McpSearchRegex regex,
		int contextLines,
		IReadOnlyList<TransformedTextRange> protectedRanges,
		Action<McpSearchMatchContext> consume,
		CancellationToken cancellationToken,
		bool preserveCarriageReturns = false)
	{
		ArgumentNullException.ThrowIfNull(content);
		ArgumentNullException.ThrowIfNull(regex);
		ArgumentNullException.ThrowIfNull(protectedRanges);
		ArgumentNullException.ThrowIfNull(consume);
		ArgumentOutOfRangeException.ThrowIfNegative(contextLines);
		cancellationToken.ThrowIfCancellationRequested();
		if (content.Length == 0)
			return new McpSearchStreamResult(0, 0);

		var lines = SplitLines(content, cancellationToken);
		var totalMatches = 0;
		var pendingStart = 0;
		var pendingEnd = 0;

		void Flush()
		{
			totalMatches++;
			var first = Math.Max(1, pendingStart - contextLines);
			var last = Math.Min(lines.Count, pendingEnd + contextLines);
			var covered = new McpTextLineRange[last - first + 1];
			for (var number = first; number <= last; number++)
				covered[number - first] = lines[number - 1];
			consume(new McpSearchMatchContext([pendingStart], covered, false, pendingEnd));
		}

		// CRLF is matched as "\n" so a pattern written with \n and $ works on either line ending;
		// a pattern that names \r itself sees the text as it is.
		var collapsedBreaks = preserveCarriageReturns ? null : CollapsedLineBreaks(content);
		var searchText = collapsedBreaks is { Count: > 0 }
			? content.Replace("\r\n", "\n", StringComparison.Ordinal)
			: content;
		Func<int, int>? toSourceOffset = collapsedBreaks is { Count: > 0 }
			? offset => ToSourceOffset(collapsedBreaks, offset)
			: null;
		var protectedRangeComparisons = regex.ForEachUnprotectedMatch(
			searchText,
			protectedRanges,
			(offset, length) =>
			{
				var startLine = LineNumberAt(lines, offset);
				var endLine = length == 0 ? startLine : LineNumberAt(lines, offset + length - 1);
				if (pendingStart == startLine)
				{
					pendingEnd = Math.Max(pendingEnd, endLine);
					return;
				}
				if (pendingStart > 0)
					Flush();
				pendingStart = startLine;
				pendingEnd = endLine;
			},
			cancellationToken,
			toSourceOffset);
		if (pendingStart > 0)
			Flush();

		return new McpSearchStreamResult(totalMatches, protectedRangeComparisons);
	}

	private static List<McpTextLineRange> SplitLines(string content, CancellationToken cancellationToken)
	{
		var lines = new List<McpTextLineRange>();
		var lineStart = 0;
		for (var index = 0; index < content.Length; index++)
		{
			if ((index & 0xFFF) == 0)
				cancellationToken.ThrowIfCancellationRequested();
			if (content[index] is not ('\r' or '\n'))
				continue;

			lines.Add(new McpTextLineRange(lines.Count + 1, lineStart, index - lineStart));
			if (content[index] == '\r' && index + 1 < content.Length && content[index + 1] == '\n')
				index++;
			lineStart = index + 1;
		}
		lines.Add(new McpTextLineRange(lines.Count + 1, lineStart, content.Length - lineStart));
		return lines;
	}

	// Positions, in the collapsed text, of each "\n" that stands for a source "\r\n".
	private static List<int> CollapsedLineBreaks(string content)
	{
		var positions = new List<int>();
		var index = content.IndexOf("\r\n", StringComparison.Ordinal);
		while (index >= 0)
		{
			positions.Add(index - positions.Count);
			index = content.IndexOf("\r\n", index + 2, StringComparison.Ordinal);
		}
		return positions;
	}

	// Every collapsed break before the offset gave up one source character.
	private static int ToSourceOffset(List<int> collapsedBreaks, int offset)
	{
		var found = collapsedBreaks.BinarySearch(offset);
		var before = found >= 0 ? found : ~found;
		return offset + before;
	}

	// A line owns its terminator, so a match that ends on a line break ends on that line.
	private static int LineNumberAt(List<McpTextLineRange> lines, int offset)
	{
		var low = 0;
		var high = lines.Count - 1;
		while (low < high)
		{
			var middle = (low + high + 1) / 2;
			if (lines[middle].Offset <= offset)
				low = middle;
			else
				high = middle - 1;
		}
		return lines[low].LineNumber;
	}

	private static IReadOnlyList<McpSearchMatchContext> MergeContexts(IReadOnlyList<McpSearchMatchContext> matches)
	{
		if (matches.Count == 0)
			return [];

		var merged = new List<McpSearchMatchContext>(matches.Count);
		var lineNumbers = new List<int> { matches[0].MatchLineNumber };
		var lines = new List<McpTextLineRange>(matches[0].Lines);
		for (var index = 1; index < matches.Count; index++)
		{
			var match = matches[index];
			var lastLine = lines[^1].LineNumber;
			if (match.Lines[0].LineNumber <= lastLine + 1)
			{
				lineNumbers.Add(match.MatchLineNumber);
				foreach (var line in match.Lines)
				{
					if (line.LineNumber > lastLine)
						lines.Add(line);
				}
				continue;
			}

			merged.Add(new McpSearchMatchContext(lineNumbers.ToArray(), lines.ToArray(), merged.Count > 0));
			lineNumbers = [match.MatchLineNumber];
			lines = new List<McpTextLineRange>(match.Lines);
		}
		merged.Add(new McpSearchMatchContext(lineNumbers.ToArray(), lines.ToArray(), merged.Count > 0));
		return merged;
	}

	private sealed class PendingMatch(
		int matchLineNumber,
		List<McpTextLineRange> lines,
		int remainingContextLines)
	{
		public int MatchLineNumber { get; } = matchLineNumber;
		public List<McpTextLineRange> Lines { get; } = lines;
		public int RemainingContextLines { get; set; } = remainingContextLines;

		public McpSearchMatchContext ToContext() => new([MatchLineNumber], Lines.ToArray(), false);
	}
}
