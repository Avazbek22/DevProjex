using DevProjex.Application.Compression;

namespace DevProjex.Application.Preview;

/// <summary>
/// Maps a position in one file's transformed preview back to its canonical source offset without
/// retaining either full text. The line index is local to the file content and zero-based.
/// </summary>
public sealed class PreviewContentCoordinateMap
{
	private readonly ContentTransformMap _transformMap;
	private readonly int[] _transformedLineStarts;
	private readonly int[] _transformedLineEnds;

	private PreviewContentCoordinateMap(
		ContentTransformMap transformMap,
		int[] transformedLineStarts,
		int[] transformedLineEnds)
	{
		_transformMap = transformMap;
		_transformedLineStarts = transformedLineStarts;
		_transformedLineEnds = transformedLineEnds;
	}

	public static PreviewContentCoordinateMap Create(
		ReadOnlySpan<char> transformedContent,
		ContentTransformMap transformMap)
	{
		ArgumentNullException.ThrowIfNull(transformMap);
		var lineCount = 1;
		foreach (var character in transformedContent)
		{
			if (character == '\n')
				lineCount++;
		}

		var lineStarts = new int[lineCount];
		var lineEnds = new int[lineCount];
		var lineIndex = 0;
		var lineStart = 0;
		for (var index = 0; index < transformedContent.Length; index++)
		{
			if (transformedContent[index] != '\n')
				continue;

			lineStarts[lineIndex] = lineStart;
			lineEnds[lineIndex] = index > lineStart && transformedContent[index - 1] == '\r'
				? index - 1
				: index;
			lineStart = index + 1;
			lineIndex++;
		}
		lineStarts[lineIndex] = lineStart;
		lineEnds[lineIndex] = transformedContent.Length > lineStart && transformedContent[^1] == '\r'
			? transformedContent.Length - 1
			: transformedContent.Length;

		return new PreviewContentCoordinateMap(
			transformMap,
			lineStarts,
			lineEnds);
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

		return _transformMap.TryToSource(lineStart + column, out sourceOffset);
	}
}
