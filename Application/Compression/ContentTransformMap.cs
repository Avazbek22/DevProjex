namespace DevProjex.Application.Compression;

/// <summary>
/// Translates character offsets between the original file text and the text produced by applying
/// a <see cref="CodeCompressionPlan"/>, in both directions.
///
/// It exists because two features record positions in different coordinate spaces: session secret
/// marks are captured against the source, while detection and preview run on the transformed text.
/// Without a translation both silently point at the wrong characters once bodies are removed.
///
/// Offsets that fall strictly inside a removed region have no counterpart and are reported as
/// unmappable rather than clamped — a mark inside a body that no longer ships must disappear, not
/// drift onto neighbouring code. Region boundaries do map: the first character of a removed body
/// corresponds to the first character of its replacement.
///
/// Memory is proportional to the number of edits, never to file length.
/// </summary>
public sealed class ContentTransformMap
{
	private readonly int[] _sourceStarts;
	private readonly int[] _sourceLengths;
	private readonly int[] _transformedStarts;
	private readonly int[] _transformedLengths;

	private ContentTransformMap(
		int[] sourceStarts,
		int[] sourceLengths,
		int[] transformedStarts,
		int[] transformedLengths,
		int sourceLength,
		int transformedLength)
	{
		_sourceStarts = sourceStarts;
		_sourceLengths = sourceLengths;
		_transformedStarts = transformedStarts;
		_transformedLengths = transformedLengths;
		SourceLength = sourceLength;
		TransformedLength = transformedLength;
	}

	/// <summary>No transformation: every offset maps to itself. Used by the uncompressed paths.</summary>
	public static ContentTransformMap Identity { get; } = new([], [], [], [], -1, -1);

	public int SourceLength { get; }

	public int TransformedLength { get; }

	public bool IsIdentity => _sourceStarts.Length == 0;

	/// <summary>
	/// Builds a map from edits that are already sorted by start offset and non-overlapping —
	/// the shape <see cref="CodeCompressionPlan"/> guarantees.
	/// </summary>
	internal static ContentTransformMap Create(IReadOnlyList<CodeCompressionEdit> edits, int sourceLength)
		=> CreateFromEdits(edits, sourceLength, CancellationToken.None);

	internal static ContentTransformMap CreateForAnalysis(
		IReadOnlyList<CodeCompressionEdit> edits,
		int sourceLength,
		CancellationToken cancellationToken) =>
		CreateFromEdits(edits, sourceLength, cancellationToken);

	private static ContentTransformMap CreateFromEdits(
		IReadOnlyList<CodeCompressionEdit> edits,
		int sourceLength,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var ranges = new ContentTransformRange[edits.Count];
		for (var index = 0; index < edits.Count; index++)
		{
			ThrowIfCancellationRequestedPeriodically(cancellationToken, index);
			var edit = edits[index];
			ranges[index] = new ContentTransformRange(
				edit.SourceStart,
				edit.SourceLength,
				edit.Replacement.Length);
		}
		cancellationToken.ThrowIfCancellationRequested();

		return Create(ranges, sourceLength, cancellationToken);
	}

	/// <summary>
	/// Builds a map for any ordered set of text replacements. The ranges contain only lengths so
	/// callers do not need to retain either source text or replacement values.
	/// </summary>
	internal static ContentTransformMap Create(
		IReadOnlyList<ContentTransformRange> ranges,
		int sourceLength) =>
		Create(ranges, sourceLength, CancellationToken.None);

	private static ContentTransformMap Create(
		IReadOnlyList<ContentTransformRange> ranges,
		int sourceLength,
		CancellationToken cancellationToken)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(sourceLength);
		cancellationToken.ThrowIfCancellationRequested();
		if (ranges.Count == 0)
			return Identity;

		var sourceStarts = new int[ranges.Count];
		var sourceLengths = new int[ranges.Count];
		var transformedStarts = new int[ranges.Count];
		var transformedLengths = new int[ranges.Count];

		var delta = 0;
		var previousEnd = 0;
		for (var index = 0; index < ranges.Count; index++)
		{
			ThrowIfCancellationRequestedPeriodically(cancellationToken, index);
			var range = ranges[index];
			if (range.SourceStart < previousEnd ||
			    range.SourceLength < 0 ||
			    range.TransformedLength < 0 ||
			    range.SourceStart > sourceLength - range.SourceLength)
			{
				throw new ArgumentException(
					"Transform ranges must be ordered, non-overlapping, and inside the source text.",
					nameof(ranges));
			}

			sourceStarts[index] = range.SourceStart;
			sourceLengths[index] = range.SourceLength;
			transformedStarts[index] = checked(range.SourceStart + delta);
			transformedLengths[index] = range.TransformedLength;
			delta = checked(delta + range.TransformedLength - range.SourceLength);
			previousEnd = checked(range.SourceStart + range.SourceLength);
		}
		cancellationToken.ThrowIfCancellationRequested();

		return new ContentTransformMap(
			sourceStarts,
			sourceLengths,
			transformedStarts,
			transformedLengths,
			sourceLength,
			sourceLength + delta);
	}

	private static void ThrowIfCancellationRequestedPeriodically(
		CancellationToken cancellationToken,
		int iteration)
	{
		if (cancellationToken.CanBeCanceled && iteration != 0 && (iteration & 1023) == 0)
			cancellationToken.ThrowIfCancellationRequested();
	}

	/// <summary>
	/// Maps a source offset to its position in the transformed text. Returns false when the offset
	/// falls strictly inside a removed region.
	/// </summary>
	public bool TryToTransformed(int sourceOffset, out int transformedOffset) =>
		TryMap(sourceOffset, _sourceStarts, _sourceLengths, _transformedStarts, _transformedLengths, SourceLength, out transformedOffset);

	/// <summary>
	/// Maps an offset in the transformed text back to the source. Returns false when the offset
	/// falls strictly inside a replacement, which is text that has no original counterpart.
	/// </summary>
	public bool TryToSource(int transformedOffset, out int sourceOffset) =>
		TryMap(transformedOffset, _transformedStarts, _transformedLengths, _sourceStarts, _sourceLengths, TransformedLength, out sourceOffset);

	private static bool TryMap(
		int offset,
		int[] fromStarts,
		int[] fromLengths,
		int[] toStarts,
		int[] toLengths,
		int fromLength,
		out int mapped)
	{
		mapped = -1;
		if (offset < 0 || (fromLength >= 0 && offset > fromLength))
			return false;

		if (fromStarts.Length == 0)
		{
			mapped = offset;
			return true;
		}

		var index = FindLastStartingAtOrBefore(fromStarts, offset);
		if (index < 0)
		{
			// Before every edit, so nothing has shifted yet.
			mapped = offset;
			return true;
		}

		var regionStart = fromStarts[index];
		var regionEnd = regionStart + fromLengths[index];
		if (offset > regionStart && offset < regionEnd)
			return false;

		if (offset == regionStart)
		{
			// A deletion has no transformed character representing its source start. The character
			// now visible at this boundary originally followed the deleted range, so reverse mapping
			// must select the source end. Forward mapping of both source boundaries remains unchanged.
			mapped = fromLengths[index] == 0
				? toStarts[index] + toLengths[index]
				: toStarts[index];
			return true;
		}

		mapped = toStarts[index] + toLengths[index] + (offset - regionEnd);
		return true;
	}

	private static int FindLastStartingAtOrBefore(int[] starts, int offset)
	{
		var low = 0;
		var high = starts.Length - 1;
		var found = -1;
		while (low <= high)
		{
			var middle = low + ((high - low) >> 1);
			if (starts[middle] <= offset)
			{
				found = middle;
				low = middle + 1;
			}
			else
			{
				high = middle - 1;
			}
		}

		return found;
	}
}

internal readonly record struct ContentTransformRange(
	int SourceStart,
	int SourceLength,
	int TransformedLength);
