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
		CancellationToken cancellationToken,
		int? knownTotalLines = null,
		int firstStreamLineNumber = 1)
	{
		ArgumentNullException.ThrowIfNull(stream);
		if (knownTotalLines is < 0)
			throw new ArgumentOutOfRangeException(nameof(knownTotalLines));
		var start = startLine ?? 1;
		if (firstStreamLineNumber < 1 || firstStreamLineNumber > start)
			throw new ArgumentOutOfRangeException(nameof(firstStreamLineNumber));
		var requestedEnd = endLine ?? int.MaxValue;
		if (start < 1 || requestedEnd < start)
			throw InvalidRange(start, endLine, knownTotalLines ?? 0);
		if (knownTotalLines is 0 && (startLine is > 1 || endLine is > 0))
			throw InvalidRange(start, endLine, 0);
		if (knownTotalLines is { } knownTotal && knownTotal > 0 &&
		    (start > knownTotal || endLine > knownTotal))
		{
			throw InvalidRange(start, endLine, knownTotal);
		}
		var maximumRequestedLine = (int)Math.Min(
			int.MaxValue,
			(long)start + maximumLines - 1);
		var lastLineToRead = knownTotalLines is { } totalLines
			? Math.Min(totalLines, Math.Min(requestedEnd, maximumRequestedLine))
			: int.MaxValue;

		using var reader = new StreamReader(
			stream,
			new UTF8Encoding(false, true),
			detectEncodingFromByteOrderMarks: false,
			bufferSize: 16 * 1024,
			leaveOpen: true);
		var builder = new StringBuilder();
		var lineBuilder = new StringBuilder(Math.Min(maximumCharacters, 16 * 1024));
		var readBuffer = ArrayPool<char>.Shared.Rent(16 * 1024);
		var total = firstStreamLineNumber - 1;
		var actualEnd = start - 1;
		var characterLimit = false;
		var hasAppendedLine = false;
		var currentLineHasContent = false;
		var currentLineOverflowed = false;
		var previousWasCarriageReturn = false;
		var endedWithLineBreak = false;
		var requestedLinesRead = knownTotalLines is 0;
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
						AppendBoundedPrefix(builder, lineBuilder, maximumCharacters);
						actualEnd = lineNumber;
						hasAppendedLine = true;
					}
				}
				else
				{
					if (hasAppendedLine)
						builder.Append('\n');
					builder.Append(lineBuilder);
					actualEnd = lineNumber;
					hasAppendedLine = true;
				}
			}

			lineBuilder.Clear();
			currentLineHasContent = false;
			currentLineOverflowed = false;
			requestedLinesRead = knownTotalLines.HasValue && lineNumber >= lastLineToRead;
		}

		try
		{
			while (true)
			{
				if (requestedLinesRead)
					break;
				var read = await reader
					.ReadAsync(readBuffer.AsMemory(), cancellationToken)
					.ConfigureAwait(false);
				if (read == 0)
					break;

				foreach (var character in readBuffer.AsSpan(0, read))
				{
					if (character == '\n')
					{
						endedWithLineBreak = true;
						if (previousWasCarriageReturn)
						{
							previousWasCarriageReturn = false;
							continue;
						}
						CompleteLine();
						if (requestedLinesRead)
							break;
						continue;
					}

					if (character == '\r')
					{
						endedWithLineBreak = true;
						previousWasCarriageReturn = true;
						CompleteLine();
						if (requestedLinesRead)
							break;
						continue;
					}

					previousWasCarriageReturn = false;
					endedWithLineBreak = false;
					currentLineHasContent = true;
					if (characterLimit || !ShouldCaptureLine(total + 1))
						continue;
					if (lineBuilder.Length < bufferedLineLimit)
						lineBuilder.Append(character);
					else
						currentLineOverflowed = true;
				}
			}

			if (!requestedLinesRead && (currentLineHasContent || endedWithLineBreak))
				CompleteLine();
		}
		finally
		{
			ArrayPool<char>.Shared.Return(readBuffer, clearArray: true);
		}

		var reportedTotal = knownTotalLines ?? total;
		if (reportedTotal == 0)
		{
			if (startLine is > 1 || endLine is > 0)
				throw InvalidRange(start, endLine, reportedTotal);
			return new McpTextPage(string.Empty, 0, 0, 0, false, false);
		}
		if (start > reportedTotal || endLine > reportedTotal)
			throw InvalidRange(start, endLine, reportedTotal);
		var effectiveEnd = endLine ?? reportedTotal;
		return new McpTextPage(
			builder.ToString(),
			start,
			actualEnd,
			reportedTotal,
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

	public static McpTextPage Slice(
		string text,
		int? startLine,
		int? endLine,
		int maximumLines,
		int maximumCharacters,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(text);
		cancellationToken.ThrowIfCancellationRequested();
		var total = CountLines(text, cancellationToken);
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

		bool CaptureLine(int offset, int length, int lineNumber)
		{
			if (lineNumber < start)
				return true;
			if (lineNumber > upper)
				return false;

			var line = text.AsSpan(offset, length);
			var required = line.Length + (hasAppendedLine ? 1 : 0);
			if ((long)builder.Length + required > maximumCharacters)
			{
				characterLimit = true;
				if (!hasAppendedLine)
				{
					AppendBoundedPrefix(builder, line, maximumCharacters);
					actualEnd = lineNumber;
					hasAppendedLine = true;
				}
				return false;
			}

			if (hasAppendedLine)
				builder.Append('\n');
			builder.Append(line);
			actualEnd = lineNumber;
			hasAppendedLine = true;
			return lineNumber < upper;
		}

		var lineStart = 0;
		var lineNumber = 1;
		var continueScanning = true;
		for (var index = 0; index < text.Length && continueScanning; index++)
		{
			if ((index & 0xFFF) == 0)
				cancellationToken.ThrowIfCancellationRequested();
			if (text[index] is not ('\r' or '\n'))
				continue;

			continueScanning = CaptureLine(lineStart, index - lineStart, lineNumber++);
			if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
				index++;
			lineStart = index + 1;
		}
		if (continueScanning && lineNumber <= upper)
			CaptureLine(lineStart, text.Length - lineStart, lineNumber);

		return new McpTextPage(
			builder.ToString(),
			start,
			actualEnd,
			total,
			actualEnd < requestedEnd,
			characterLimit);
	}

	private static void AppendBoundedPrefix(StringBuilder builder, string line, int maximumCharacters)
		=> AppendBoundedPrefix(builder, line.AsSpan(), maximumCharacters);

	private static void AppendBoundedPrefix(
		StringBuilder builder,
		StringBuilder line,
		int maximumCharacters)
	{
		var length = Math.Min(line.Length, maximumCharacters);
		if (length > 0 &&
		    length < line.Length &&
		    char.IsHighSurrogate(line[length - 1]) &&
		    char.IsLowSurrogate(line[length]))
		{
			length--;
		}
		builder.Append(line, 0, length);
	}

	private static void AppendBoundedPrefix(
		StringBuilder builder,
		ReadOnlySpan<char> line,
		int maximumCharacters)
	{
		var length = Math.Min(line.Length, maximumCharacters);
		if (length > 0 &&
		    length < line.Length &&
		    char.IsHighSurrogate(line[length - 1]) &&
		    char.IsLowSurrogate(line[length]))
		{
			length--;
		}
		builder.Append(line[..length]);
	}

	private static int CountLines(string text, CancellationToken cancellationToken)
	{
		if (text.Length == 0)
			return 0;

		var total = 1;
		for (var index = 0; index < text.Length; index++)
		{
			if ((index & 0xFFF) == 0)
				cancellationToken.ThrowIfCancellationRequested();
			if (text[index] == '\r')
			{
				total++;
				if (index + 1 < text.Length && text[index + 1] == '\n')
					index++;
			}
			else if (text[index] == '\n')
			{
				total++;
			}
		}
		return total;
	}

	private static McpToolException InvalidRange(int start, int? end, int total) =>
		new(
			McpErrorCodes.InvalidRange,
			$"{McpErrorCodes.InvalidRange}: requested line range {start}-{end?.ToString() ?? "end"} is invalid. " +
			$"Valid lines are 1-{total} and start_line must not exceed end_line.");
}
