using System.Buffers;

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
		var lineBuilder = new StringBuilder(Math.Min(maximumCharacters, 16 * 1024));
		var readBuffer = ArrayPool<char>.Shared.Rent(16 * 1024);
		var total = 0;
		var actualEnd = start - 1;
		var characterLimit = false;
		var hasAppendedLine = false;
		var currentLineHasContent = false;
		var currentLineOverflowed = false;
		var previousWasCarriageReturn = false;
		var bufferedLineLimit = maximumCharacters == int.MaxValue
			? int.MaxValue
			: maximumCharacters + 1;

		bool ShouldCaptureLine(int lineNumber) =>
			lineNumber >= start &&
			lineNumber <= requestedEnd &&
			lineNumber - start < maximumLines;

		void CompleteLine()
		{
			var lineNumber = ++total;
			if (ShouldCaptureLine(lineNumber) && !characterLimit)
			{
				var separatorLength = hasAppendedLine ? 1 : 0;
				var exceedsLimit = currentLineOverflowed ||
				                   (long)builder.Length + separatorLength + lineBuilder.Length > maximumCharacters;
				if (exceedsLimit)
				{
					characterLimit = true;
					if (!hasAppendedLine)
					{
						AppendBoundedPrefix(builder, lineBuilder.ToString(), maximumCharacters);
						actualEnd = lineNumber;
						hasAppendedLine = true;
					}
				}
				else
				{
					if (hasAppendedLine)
						builder.Append('\n');
					builder.Append(lineBuilder.ToString());
					actualEnd = lineNumber;
					hasAppendedLine = true;
				}
			}

			lineBuilder.Clear();
			currentLineHasContent = false;
			currentLineOverflowed = false;
		}

		try
		{
			while (true)
			{
				var read = await reader
					.ReadAsync(readBuffer.AsMemory(), cancellationToken)
					.ConfigureAwait(false);
				if (read == 0)
					break;

				foreach (var character in readBuffer.AsSpan(0, read))
				{
					if (character == '\n')
					{
						if (previousWasCarriageReturn)
						{
							previousWasCarriageReturn = false;
							continue;
						}
						CompleteLine();
						continue;
					}

					if (character == '\r')
					{
						previousWasCarriageReturn = true;
						CompleteLine();
						continue;
					}

					previousWasCarriageReturn = false;
					currentLineHasContent = true;
					if (characterLimit || !ShouldCaptureLine(total + 1))
						continue;
					if (lineBuilder.Length < bufferedLineLimit)
						lineBuilder.Append(character);
					else
						currentLineOverflowed = true;
				}
			}

			if (currentLineHasContent)
				CompleteLine();
		}
		finally
		{
			ArrayPool<char>.Shared.Return(readBuffer);
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
		var hasAppendedLine = false;
		for (var lineNumber = start; lineNumber <= upper; lineNumber++)
		{
			var line = lines[lineNumber - 1];
			var required = line.Length + (hasAppendedLine ? 1 : 0);
			if (builder.Length + required > maximumCharacters)
			{
				characterLimit = true;
				if (!hasAppendedLine)
				{
					AppendBoundedPrefix(builder, line, maximumCharacters);
					actualEnd = lineNumber;
					hasAppendedLine = true;
				}
				break;
			}
			if (hasAppendedLine)
				builder.Append('\n');
			builder.Append(line);
			actualEnd = lineNumber;
			hasAppendedLine = true;
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

	private static void AppendBoundedPrefix(StringBuilder builder, string line, int maximumCharacters)
	{
		var length = Math.Min(line.Length, maximumCharacters);
		if (length > 0 &&
		    length < line.Length &&
		    char.IsHighSurrogate(line[length - 1]) &&
		    char.IsLowSurrogate(line[length]))
		{
			length--;
		}
		builder.Append(line.AsSpan(0, length));
	}

	private static McpToolException InvalidRange(int start, int? end, int total) =>
		new(
			McpErrorCodes.InvalidRange,
			$"{McpErrorCodes.InvalidRange}: requested line range {start}-{end?.ToString() ?? "end"} is invalid. " +
			$"Valid lines are 1-{total} and start_line must not exceed end_line.");
}
