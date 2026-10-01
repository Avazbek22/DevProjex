using DevProjex.Application.Dependencies;
using DevProjex.Mcp;

namespace DevProjex.Tests.Unit;

public sealed class McpRelatedResolutionTests
{
	[Theory]
	[InlineData(DependencyDirection.Dependencies)]
	[InlineData(DependencyDirection.Dependents)]
	[InlineData(DependencyDirection.Both)]
	public void IndexedResolutionCountEqualsTheFullEdgeScan(DependencyDirection direction)
	{
		var edges = new[]
		{
			Edge("A.cs", "B.cs", ResolutionStatus.Resolved),
			Edge("A.cs", null, ResolutionStatus.Unresolved),
			Edge("B.cs", "A.cs", ResolutionStatus.Resolved),
			Edge("CandidateSource.cs", null, ResolutionStatus.Ambiguous, ["A.cs", "OtherCandidate.cs"]),
			Edge("Other.cs", "Else.cs", ResolutionStatus.External)
		};
		var snapshot = Snapshot(edges);
		var seeds = new[] { "A.cs" };

		var actual = DevProjexMcpTools.CountRelatedResolution(snapshot, seeds, direction);
		var expected = LegacyCount(edges, seeds, direction);

		Assert.Equal(expected, actual);
	}

	[Fact]
	public void IndexedResolutionVisitsOnlyEdgesAdjacentToSeeds()
	{
		var edges = Enumerable.Range(0, 10_000)
			.Select(index => Edge($"Source{index}.cs", $"Target{index}.cs", ResolutionStatus.Resolved))
			.Append(Edge("Seed.cs", "Dependency.cs", ResolutionStatus.Resolved))
			.ToArray();
		var snapshot = Snapshot(edges);

		var result = DevProjexMcpTools.CountRelatedResolution(
			snapshot,
			["Seed.cs"],
			DependencyDirection.Both);

		Assert.Equal(new DevProjexMcpTools.RelatedResolutionCounts(1, 0, 0, 0), result);
		Assert.Single(snapshot.EdgesBySource["Seed.cs"]);
		Assert.Empty(snapshot.EdgesByTarget.GetValueOrDefault("Seed.cs") ?? []);
	}

	private static DevProjexMcpTools.RelatedResolutionCounts LegacyCount(
		IReadOnlyList<DependencyEdge> edges,
		IReadOnlyList<string> seeds,
		DependencyDirection direction)
	{
		var seedSet = seeds.ToHashSet(StringComparer.Ordinal);
		var counts = new int[4];
		foreach (var edge in edges)
		{
			var dependency = (direction is DependencyDirection.Dependencies or DependencyDirection.Both) &&
			                 seedSet.Contains(edge.Source);
			var dependent = (direction is DependencyDirection.Dependents or DependencyDirection.Both) &&
			                edge.Target is not null && seedSet.Contains(edge.Target);
			if (dependency || dependent)
				counts[(int)edge.Status]++;
		}
		return new DevProjexMcpTools.RelatedResolutionCounts(counts[0], counts[1], counts[3], counts[2]);
	}

	private static DependencyIndexSnapshot Snapshot(IReadOnlyList<DependencyEdge> edges) =>
		new(
			"root",
			"manifest",
			"declarations",
			[],
			[],
			edges,
			edges.GroupBy(static edge => edge.Source, StringComparer.Ordinal)
				.ToDictionary(static group => group.Key, static group => (IReadOnlyList<DependencyEdge>)group.ToArray(),
					StringComparer.Ordinal),
			edges.SelectMany(static edge => (edge.Status == ResolutionStatus.Resolved
					? edge.DeclarationFiles.Count > 0 ? edge.DeclarationFiles : edge.Target is null ? [] : [edge.Target]
					: edge.Candidates)
				.Distinct(StringComparer.Ordinal)
				.Select(target => (Target: target, Edge: edge)))
				.GroupBy(static item => item.Target, StringComparer.Ordinal)
				.ToDictionary(static group => group.Key,
					static group => (IReadOnlyList<DependencyEdge>)group.Select(static item => item.Edge).Distinct().ToArray(),
					StringComparer.Ordinal),
			new DependencyFactsCoverage(0, 0, 0, 0,
				new Dictionary<string, int>(), new Dictionary<string, int>()),
			new DependencyIndexMetrics(0, 0, 0, 0, false));

	private static DependencyEdge Edge(
		string source,
		string? target,
		ResolutionStatus status,
		IReadOnlyList<string>? candidates = null) =>
		new(source, target, EvidenceLayer.ExplicitImport, status, "reference", candidates ?? [], [], [], false);
}
