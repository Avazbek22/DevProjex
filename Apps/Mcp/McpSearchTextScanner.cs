namespace DevProjex.Mcp;

internal readonly record struct McpTextLineRange(
	int LineNumber,
	int Offset,
	int Length);

internal sealed record McpSearchMatchContext(
	int MatchLineNumber,
	IReadOnlyList<McpTextLineRange> Lines);

internal sealed record McpSearchTextScanResult(
	int TotalMatches,
	IReadOnlyList<McpSearchMatchContext> Matches);

internal static class McpSearchTextScanner
{
	public static McpSearchTextScanResult Scan(
		string content,
		McpSearchRegex regex,
		int contextLines,
		int maximumStoredMatches,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(content);
		ArgumentNullException.ThrowIfNull(regex);
		ArgumentOutOfRangeException.ThrowIfNegative(contextLines);
		ArgumentOutOfRangeException.ThrowIfNegative(maximumStoredMatches);
		cancellationToken.ThrowIfCancellationRequested();
		if (content.Length == 0)
			return new McpSearchTextScanResult(0, []);

		var previous = contextLines == 0
			? null
			: new Queue<McpTextLineRange>(contextLines);
		var active = new List<PendingMatch>(Math.Min(contextLines + 1, maximumStoredMatches));
		var stored = new List<PendingMatch>(maximumStoredMatches);
		var totalMatches = 0;

		void ProcessLine(McpTextLineRange line)
		{
			for (var index = active.Count - 1; index >= 0; index--)
			{
				var pending = active[index];
				pending.Lines.Add(line);
				pending.RemainingContextLines--;
				if (pending.RemainingContextLines == 0)
					active.RemoveAt(index);
			}

			if (regex.IsMatch(content, line.Offset, line.Length))
			{
				totalMatches++;
				if (stored.Count < maximumStoredMatches)
				{
					var lines = previous is null
						? new List<McpTextLineRange>(1)
						: new List<McpTextLineRange>(previous);
					lines.Add(line);
					var pending = new PendingMatch(line.LineNumber, lines, contextLines);
					stored.Add(pending);
					if (contextLines > 0)
						active.Add(pending);
				}
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

		return new McpSearchTextScanResult(
			totalMatches,
			stored
				.Select(static match => new McpSearchMatchContext(
					match.MatchLineNumber,
					match.Lines.ToArray()))
				.ToArray());
	}

	private sealed class PendingMatch(
		int matchLineNumber,
		List<McpTextLineRange> lines,
		int remainingContextLines)
	{
		public int MatchLineNumber { get; } = matchLineNumber;
		public List<McpTextLineRange> Lines { get; } = lines;
		public int RemainingContextLines { get; set; } = remainingContextLines;
	}
}
