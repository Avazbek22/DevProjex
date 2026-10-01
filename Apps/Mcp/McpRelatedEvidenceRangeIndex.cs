using DevProjex.Application.Secrets;

namespace DevProjex.Mcp;

internal sealed class McpRelatedEvidenceRangeIndex(IReadOnlyList<TransformedTextRange> ranges)
{
	public long IndexedRangeReads { get; private set; }

	public bool Intersects(int start, int end)
	{
		// Transformed ranges retain the sorted, disjoint redaction-segment order.
		var lower = 0;
		var upper = ranges.Count;
		while (lower < upper)
		{
			var middle = lower + (upper - lower) / 2;
			IndexedRangeReads++;
			if (ranges[middle].End <= start)
				lower = middle + 1;
			else
				upper = middle;
		}

		if (lower == ranges.Count)
			return false;
		IndexedRangeReads++;
		var range = ranges[lower];
		return range.Start < end && range.End > start;
	}
}
