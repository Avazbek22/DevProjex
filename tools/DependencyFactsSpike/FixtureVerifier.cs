using System.Text.Json;

namespace DependencyFactsSpike;

internal static class FixtureVerifier
{
	public static IReadOnlyList<string> Verify(string fixtureRoot, string grammarCache)
	{
		var failures = new List<string>();
		foreach (var expectedPath in Directory.EnumerateFiles(fixtureRoot, "expected.json", SearchOption.AllDirectories)
			.OrderBy(static path => path, StringComparer.Ordinal))
		{
			var directory = Path.GetDirectoryName(expectedPath)!;
			var expectation = JsonSerializer.Deserialize(File.ReadAllBytes(expectedPath), SpikeJsonContext.Default.FixtureExpectation)
			                  ?? throw new InvalidOperationException($"Empty fixture expectation '{expectedPath}'.");
			var output = Path.Combine(Path.GetTempPath(), "DevProjex.DependencyFactsSpike", "fixture-results", expectation.Name + ".json");
			var result = new DependencyFactsEngine(grammarCache).Index(new IndexOptions(directory, output, "fixture", false, grammarCache));
			foreach (var edge in expectation.Edges)
			{
				var actual = result.Edges.FirstOrDefault(candidate =>
					candidate.Source == edge.Source && candidate.Reference == edge.Reference && candidate.Status == edge.Status);
				if (actual is null)
				{
					failures.Add($"{expectation.Name}: missing {edge.Status} edge {edge.Source} -> {edge.Reference}");
					continue;
				}
				if (edge.Target is not null && actual.Target != edge.Target)
					failures.Add($"{expectation.Name}: {edge.Reference} target '{actual.Target}', expected '{edge.Target}'");
				if (edge.ReasonContains is not null && !actual.Reason.Contains(edge.ReasonContains, StringComparison.OrdinalIgnoreCase))
					failures.Add($"{expectation.Name}: {edge.Reference} reason '{actual.Reason}' lacks '{edge.ReasonContains}'");
			}
			foreach (var absent in expectation.AbsentEdges ?? [])
			{
				if (result.Edges.Any(edge => edge.Source == absent.Source && edge.Reference == absent.Reference))
					failures.Add($"{expectation.Name}: forbidden edge {absent.Source} -> {absent.Reference} was extracted");
			}
		}
		return failures;
	}
}
