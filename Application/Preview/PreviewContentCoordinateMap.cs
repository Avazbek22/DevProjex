namespace DevProjex.Application.Preview;

/// <summary>
/// Maps a position in one file's transformed preview back to its canonical source offset without
/// retaining either full text. The line index is local to the file content and zero-based.
/// </summary>
public sealed class PreviewContentCoordinateMap
{
	private readonly ContentTransformMap _sourceTransformMap;
	private readonly ContentTransformMap _redactionTransformMap;
	private readonly int[] _transformedLineStarts;
	private readonly int[] _transformedLineEnds;

	private PreviewContentCoordinateMap(
		ContentTransformMap sourceTransformMap,
		ContentTransformMap redactionTransformMap,
		int[] transformedLineStarts,
		int[] transformedLineEnds)
	{
		_sourceTransformMap = sourceTransformMap;
		_redactionTransformMap = redactionTransformMap;
		_transformedLineStarts = transformedLineStarts;
		_transformedLineEnds = transformedLineEnds;
	}

	public static PreviewContentCoordinateMap Create(
		ReadOnlySpan<char> transformedContent,
		ContentTransformMap sourceTransformMap,
		ContentTransformMap? redactionTransformMap = null)
	{
		ArgumentNullException.ThrowIfNull(sourceTransformMap);
		redactionTransformMap ??= ContentTransformMap.Identity;
		var lineCount = 1;
		for (var index = 0; index < transformedContent.Length;)
		{
			var lineBreakLength = GetLineBreakLength(transformedContent, index);
			if (lineBreakLength == 0)
			{
				index++;
				continue;
			}

			lineCount++;
			index += lineBreakLength;
		}

		var lineStarts = new int[lineCount];
		var lineEnds = new int[lineCount];
		var lineIndex = 0;
		var lineStart = 0;
		for (var index = 0; index < transformedContent.Length;)
		{
			var lineBreakLength = GetLineBreakLength(transformedContent, index);
			if (lineBreakLength == 0)
			{
				index++;
				continue;
			}

			lineStarts[lineIndex] = lineStart;
			lineEnds[lineIndex] = index;
			lineStart = index + lineBreakLength;
			lineIndex++;
			index = lineStart;
		}
		lineStarts[lineIndex] = lineStart;
		lineEnds[lineIndex] = transformedContent.Length;

		return new PreviewContentCoordinateMap(
			sourceTransformMap,
			redactionTransformMap,
			lineStarts,
			lineEnds);
	}

	private static int GetLineBreakLength(ReadOnlySpan<char> text, int index)
	{
		if (text[index] == '\n')
			return 1;
		if (text[index] != '\r')
			return 0;
		return index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1;
	}

	public bool TryToSourceOffset(int lineIndex, int column, out int sourceOffset)
	{
		sourceOffset = -1;
		if ((uint)lineIndex >= (uint)_transformedLineStarts.Length || column < 0)
			return false;

		var lineStart = _transformedLineStarts[lineIndex];
		var lineEnd = _transformedLineEnds[lineIndex];
		if (column > lineEnd - lineStart)
			return false;

		if (!_redactionTransformMap.TryToSource(
			    lineStart + column,
			    out var preRedactionOffset))
		{
			return false;
		}

		return _sourceTransformMap.TryToSource(preRedactionOffset, out sourceOffset);
	}
}
