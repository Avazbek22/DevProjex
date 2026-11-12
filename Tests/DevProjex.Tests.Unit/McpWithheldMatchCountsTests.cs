using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpWithheldMatchCountsTests
{
	[Fact]
	public void BoundedLeadersProduceTheSameTopFilesAndTotalsAsExhaustiveCounting()
	{
		var random = new Random(76231);
		for (var sample = 0; sample < 50; sample++)
		{
			var files = Enumerable.Range(0, 1_000).Select(index =>
			{
				var file = new McpSearchObservedFile($"folder/file{index:D4}.txt");
				file.Complete(random.Next(1, 18));
				return file;
			}).ToArray();
			var counts = new McpWithheldMatchCounts(200, 20);
			foreach (var file in files.OrderBy(_ => random.Next())) counts.Observe(file);
			var written = new List<McpSearchHit>();
			var retained = new List<McpSearchCandidate>();
			var expected = files.ToDictionary(static file => file.Path, static file => file.Count, StringComparer.Ordinal);
			foreach (var file in files.OrderBy(_ => random.Next()).Take(100))
			{
				var delivered = Math.Min(file.Count, Math.Min(200 - written.Count, random.Next(1, 5)));
				if (delivered == 0) break;
				written.AddRange(Enumerable.Range(1, delivered).Select(line => new McpSearchHit(file.Path, file.Path, line)));
				retained.Add(new(new(file.Path, file.Path, [1], []), 1, 0, "", 0, ObservedFile: file));
				expected[file.Path] -= delivered;
			}
			var bounded = counts.Snapshot(retained, written, files.Length, files.Sum(static file => file.Count));
			var missing = expected.Where(static pair => pair.Value > 0).ToArray();
			Assert.Equal(missing.Length, bounded.Files);
			Assert.Equal(missing.Sum(static pair => pair.Value), bounded.Matches);
			Assert.True(bounded.Counts.Count <= 220 + retained.Count);
			var top = missing.OrderByDescending(static pair => pair.Value).ThenBy(static pair => pair.Key, StringComparer.Ordinal).Take(20).ToArray();
			Assert.Equal(top, bounded.Counts.OrderByDescending(static pair => pair.Value).ThenBy(static pair => pair.Key, StringComparer.Ordinal).Take(20));
			Assert.Equal(DevProjexMcpTools.FormatWithheldDistribution(expected.Where(static pair => pair.Value > 0)
				.ToDictionary(static pair => pair.Key, static pair => pair.Value)),
				DevProjexMcpTools.FormatWithheldDistribution(bounded.Counts, bounded.Files, bounded.Matches));
		}
	}
}
