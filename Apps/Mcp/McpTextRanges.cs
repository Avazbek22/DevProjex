namespace DevProjex.Mcp;

internal sealed record McpTextPage(
	string Text,
	int StartLine,
	int EndLine,
	int TotalLines,
	bool IsTruncated,
	bool CharacterLimitReached);

internal static class McpTextRanges
{
	public static async Task<McpTextPage> ReadPageAsync(
		Stream stream,
		int? startLine,
		int? endLine,
		int maximumLines,
		int maximumCharacters,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(stream);
		var start = startLine ?? 1;
		var requestedEnd = endLine ?? int.MaxValue;
		if (start < 1 || requestedEnd < start)
			throw InvalidRange(start, endLine, 0);

		using var reader = new StreamReader(
			stream,
			new UTF8Encoding(false, true),
			detectEncodingFromByteOrderMarks: false,
			bufferSize: 16 * 1024,
			leaveOpen: true);
		var builder = new StringBuilder();
		var total = 0;
		var actualEnd = start - 1;
		var characterLimit = false;
		while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
		{
			total++;
			if (total < start || total > requestedEnd || total >= start + maximumLines)
				continue;
			if (characterLimit)
				continue;
			var required = line.Length + (builder.Length == 0 ? 0 : 1);
			if (builder.Length + required > maximumCharacters)
			{
				characterLimit = true;
				if (builder.Length == 0)
				{
					builder.Append(line.AsSpan(0, Math.Min(line.Length, maximumCharacters)));
					actualEnd = total;
				}
				continue;
			}
			if (builder.Length > 0)
				builder.Append('\n');
			builder.Append(line);
			actualEnd = total;
		}

		if (total == 0)
		{
			if (startLine is > 1 || endLine is > 0)
				throw InvalidRange(start, endLine, total);
			return new McpTextPage(string.Empty, 0, 0, 0, false, false);
		}
		if (start > total || endLine > total)
			throw InvalidRange(start, endLine, total);
		var effectiveEnd = endLine ?? total;
		return new McpTextPage(
			builder.ToString(),
			start,
			actualEnd,
			total,
			actualEnd < effectiveEnd,
			characterLimit);
	}

	public static McpTextPage Slice(
		IReadOnlyList<string> lines,
		int? startLine,
		int? endLine,
		int maximumLines,
		int maximumCharacters)
	{
		ArgumentNullException.ThrowIfNull(lines);
		var total = lines.Count;
		if (total == 0)
		{
			if (startLine is > 1 || endLine is > 0)
				throw InvalidRange(startLine ?? 1, endLine, total);
			return new McpTextPage(string.Empty, 0, 0, 0, false, false);
		}

		var start = startLine ?? 1;
		var requestedEnd = endLine ?? total;
		if (start < 1 || start > total || requestedEnd < start || requestedEnd > total)
			throw InvalidRange(start, endLine, total);

		var upper = Math.Min(requestedEnd, checked(start + maximumLines - 1));
		var builder = new StringBuilder();
		var actualEnd = start - 1;
		var characterLimit = false;
		for (var lineNumber = start; lineNumber <= upper; lineNumber++)
		{
			var line = lines[lineNumber - 1];
			var required = line.Length + (builder.Length == 0 ? 0 : 1);
			if (builder.Length + required > maximumCharacters)
			{
				characterLimit = true;
				if (builder.Length == 0)
				{
					builder.Append(line.AsSpan(0, Math.Min(line.Length, maximumCharacters)));
					actualEnd = lineNumber;
				}
				break;
			}
			if (builder.Length > 0)
				builder.Append('\n');
			builder.Append(line);
			actualEnd = lineNumber;
		}

		var truncated = actualEnd < requestedEnd;
		return new McpTextPage(
			builder.ToString(),
			start,
			actualEnd,
			total,
			truncated,
			characterLimit);
	}

	public static IReadOnlyList<string> SplitLines(string text)
	{
		if (text.Length == 0)
			return [];
		var lines = new List<string>();
		var start = 0;
		for (var index = 0; index < text.Length; index++)
		{
			if (text[index] is not ('\r' or '\n'))
				continue;
			lines.Add(text[start..index]);
			if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
				index++;
			start = index + 1;
		}
		if (start < text.Length)
			lines.Add(text[start..]);
		else if (text.Length > 0 && text[^1] is '\r' or '\n')
			lines.Add(string.Empty);
		return lines;
	}

	private static McpToolException InvalidRange(int start, int? end, int total) =>
		new(
			McpErrorCodes.InvalidRange,
			$"{McpErrorCodes.InvalidRange}: requested line range {start}-{end?.ToString() ?? "end"} is invalid. " +
			$"Valid lines are 1-{total} and start_line must not exceed end_line.");
}
