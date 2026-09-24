using DevProjex.Application.Secrets;
using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpRelatedEvidenceRangeIndexTests
{
	[Fact(Timeout = 15_000)]
	public void DenseEvidenceAndRangesUseBoundedIndexedReads()
	{
		const int rangeCount = 2_048;
		var ranges = Enumerable.Range(0, rangeCount)
			.Select(static index => new TransformedTextRange(index * 3, 1))
			.ToArray();
		var index = new McpRelatedEvidenceRangeIndex(ranges);
		for (var line = 0; line < rangeCount; line++)
			Assert.False(index.Intersects(line * 3 + 1, line * 3 + 2));

		Assert.InRange(index.IndexedRangeReads, 0, rangeCount * 16L);
	}

	[Fact]
	public void SortedDisjointRangesMatchStrictLinearOverlapAtBoundaries()
	{
		TransformedTextRange[] ranges =
		[
			new(2, 2),
			new(4, 0),
			new(4, 3),
			new(10, 0),
			new(12, 2)
		];
		var index = new McpRelatedEvidenceRangeIndex(ranges);
		for (var start = 0; start <= 16; start++)
		{
			for (var end = start; end <= 16; end++)
			{
				var expected = ranges.Any(range => range.Start < end && range.End > start);
				Assert.Equal(expected, index.Intersects(start, end));
			}
		}
	}
}
