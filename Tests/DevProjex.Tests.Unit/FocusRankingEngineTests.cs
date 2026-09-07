using DevProjex.Application.Dependencies;
using DevProjex.Application.Ranking;

namespace DevProjex.Tests.Unit;

public sealed class FocusRankingEngineTests
{
	[Fact]
	public void MultiSourceBfs_IsUndirectedAndKeepsUnreachableComponentsLast()
	{
		var fixture = CreateFixture(
			["a.cs", "b.cs", "c.cs", "d.cs", "other.cs"],
			[Edge("b.cs", "a.cs"), Edge("b.cs", "c.cs"), Edge("other.cs", "d.cs")]);

		var result = Apply(fixture, [Seed(fixture, "a.cs"), Seed(fixture, "c.cs")]);

		Assert.Equal(["a.cs", "c.cs", "b.cs", "d.cs", "other.cs"], result.Entries.Select(static entry => entry.Path));
		Assert.Equal([0, 0, 1, null, null], result.Entries.Select(static entry => entry.Hop));
		Assert.Equal(FocusRankingRelation.DependentOf, result.Entries[2].Via?.Relation);
		Assert.Equal(2, result.Focus!.Unreachable);
	}

	[Fact]
	public void Bfs_DoesNotTraverseAHiddenManifestNode()
	{
		var fixture = CreateFixture(
			["a.cs", "b.cs"],
			[Edge("a.cs", "hidden.cs"), Edge("hidden.cs", "b.cs")],
			extraFacts: [Facts("hidden.cs")]);

		var result = Apply(fixture, [Seed(fixture, "a.cs")]);

		Assert.Equal(0, result.Entries.Single(entry => entry.Path == "a.cs").Hop);
		Assert.Null(result.Entries.Single(entry => entry.Path == "b.cs").Hop);
		Assert.Equal(1, result.Focus!.Unreachable);
	}

	[Fact]
	public void Parent_IsChosenAfterDistancesByCanonicalPathAndNotBySeedOrder()
	{
		var fixture = CreateFixture(
			["a.cs", "b.cs", "leaf.cs", "z.cs"],
			[
				Edge("a.cs", "leaf.cs"),
				Edge("z.cs", "leaf.cs"),
				Edge("a.cs", "b.cs"),
				Edge("z.cs", "b.cs"),
				Edge("b.cs", "leaf.cs")
			]);

		var first = Apply(fixture, [Seed(fixture, "z.cs"), Seed(fixture, "a.cs")]);
		var second = Apply(fixture, [Seed(fixture, "a.cs"), Seed(fixture, "z.cs")]);

		Assert.Equal(["z.cs", "a.cs"], first.Entries.Take(2).Select(static entry => entry.Path));
		Assert.Equal(["a.cs", "z.cs"], second.Entries.Take(2).Select(static entry => entry.Path));
		Assert.Equal("a.cs", first.Entries.Single(entry => entry.Path == "leaf.cs").Via?.Path);
		Assert.Equal("a.cs", second.Entries.Single(entry => entry.Path == "leaf.cs").Via?.Path);
		Assert.Equal(
			first.Entries.Where(static entry => !entry.IsFocusSeed).Select(static entry => (entry.Path, entry.Hop, entry.Via)),
			second.Entries.Where(static entry => !entry.IsFocusSeed).Select(static entry => (entry.Path, entry.Hop, entry.Via)));
	}

	[Fact]
	public void Apply_DeduplicatesSeedsPreservingFirstRequestAndReportsFourStates()
	{
		var files = new[]
		{
			Facts("resolved.cs"),
			Facts("neighbor.cs"),
			Facts("lonely.cs"),
			Facts("failed.cs", DependencyFileStatus.ExtractionFailed, "read failed"),
			Facts("notes.md", DependencyFileStatus.Unsupported, "unsupported language")
		};
		var fixture = CreateFixture(
			files.Select(static file => file.Path).ToArray(),
			[Edge("resolved.cs", "neighbor.cs")],
			files: files);
		var duplicate = new FocusRankingSeedRequest("second spelling", Full(fixture, "resolved.cs"));

		var result = Apply(
			fixture,
			[
				new FocusRankingSeedRequest("first spelling", Full(fixture, "resolved.cs")),
				duplicate,
				Seed(fixture, "lonely.cs"),
				Seed(fixture, "failed.cs"),
				Seed(fixture, "notes.md")
			]);

		Assert.Equal(4, result.Focus!.Seeds.Count);
		Assert.Equal("first spelling", result.Focus.Seeds[0].Requested);
		Assert.Equal(
			[
				FocusSeedState.Resolved,
				FocusSeedState.NoResolvedNeighbors,
				FocusSeedState.ExtractionFailed,
				FocusSeedState.Unsupported
			],
			result.Focus.Seeds.Select(static seed => seed.State));
		Assert.Equal([0, 0, 0, 0], result.Entries.Take(4).Select(static entry => entry.Hop));
	}

	[Fact]
	public void Apply_RenumbersPriorityPreservesImportanceScoreVersionsAndCapsHistogram()
	{
		var paths = Enumerable.Range(0, 12).Select(index => $"{index:D2}.cs").Append("isolated.cs").ToArray();
		var edges = Enumerable.Range(0, 11).Select(index => Edge($"{index:D2}.cs", $"{index + 1:D2}.cs")).ToArray();
		var fixture = CreateFixture(paths.Reverse().ToArray(), edges);

		var result = Apply(fixture, [Seed(fixture, "00.cs")]);

		Assert.Equal(Enumerable.Range(1, paths.Length), result.Entries.Select(static entry => entry.Priority));
		Assert.Equal(13, result.Entries.Single(entry => entry.Path == "00.cs").BaseImportancePriority);
		Assert.Equal(13d, result.Entries.Single(entry => entry.Path == "00.cs").Score);
		Assert.Equal(Enumerable.Range(0, 8), result.Focus!.Hops.Keys);
		Assert.Equal(4, result.Focus.HopsBeyond);
		Assert.Equal(11, result.Focus.MaxHop);
		Assert.Equal(1, result.Focus.Unreachable);
		Assert.Same(fixture.Report.SourceVersions, result.SourceVersions);
	}

	[Fact]
	public void EmptyGraph_KeepsValidUnsupportedSeedAtHopZero()
	{
		var files = new[]
		{
			Facts("seed.txt", DependencyFileStatus.Unsupported, "unsupported language"),
			Facts("other.txt", DependencyFileStatus.Unsupported, "unsupported language")
		};
		var fixture = CreateFixture(files.Select(static file => file.Path).ToArray(), [], files: files);

		var result = Apply(fixture, [Seed(fixture, "seed.txt")]);

		Assert.Equal("seed.txt", result.Entries[0].Path);
		Assert.Equal(0, result.Entries[0].Hop);
		Assert.Null(result.Entries[1].Hop);
		Assert.Equal(FocusSeedState.Unsupported, result.Focus!.Seeds.Single().State);
	}

	[Fact]
	public void FocusOrder_IsDeterministicWhenNodesAndEdgesArePermuted()
	{
		var paths = new[] { "a.cs", "b.cs", "c.cs", "d.cs" };
		var edges = new[] { Edge("a.cs", "b.cs"), Edge("c.cs", "b.cs"), Edge("c.cs", "d.cs") };
		var first = CreateFixture(paths, edges);
		var second = CreateFixture(paths.Reverse().ToArray(), edges.Reverse().ToArray());

		var left = Apply(first, [Seed(first, "a.cs")]);
		var right = Apply(second, [Seed(second, "a.cs")]);

		Assert.Equal(
			left.Entries.Select(static entry => (entry.Path, entry.Hop, entry.Via, entry.Priority)),
			right.Entries.Select(static entry => (entry.Path, entry.Hop, entry.Via, entry.Priority)));
	}

	private static ImportanceRankingReport Apply(
		Fixture fixture,
		IReadOnlyList<FocusRankingSeedRequest> seeds) =>
		FocusRankingEngine.Apply(
			fixture.Report,
			new FocusRankingRequest(seeds),
			fixture.Graph,
			fixture.Snapshot,
			TestContext.Current.CancellationToken);

	private static FocusRankingSeedRequest Seed(Fixture fixture, string path) => new(path, Full(fixture, path));

	private static string Full(Fixture fixture, string path) => Path.Combine(fixture.Root, path);

	private static Fixture CreateFixture(
		IReadOnlyList<string> importanceOrder,
		IReadOnlyList<DependencyEdge> edges,
		IReadOnlyList<FileFacts>? extraFacts = null,
		IReadOnlyList<FileFacts>? files = null)
	{
		var root = Path.GetFullPath(Path.Combine("focus-fixture", Guid.NewGuid().ToString("N")));
		var facts = files ?? importanceOrder.Select(static path => Facts(path)).Concat(extraFacts ?? []).ToArray();
		var snapshot = new DependencyIndexSnapshot(
			root,
			"manifest",
			"declarations",
			facts,
			[],
			edges,
			edges.GroupBy(static edge => edge.Source).ToDictionary(
				static group => group.Key,
				static group => (IReadOnlyList<DependencyEdge>)group.ToArray(),
				StringComparer.Ordinal),
			edges.Where(static edge => edge.Target is not null).GroupBy(static edge => edge.Target!).ToDictionary(
				static group => group.Key,
				static group => (IReadOnlyList<DependencyEdge>)group.ToArray(),
				StringComparer.Ordinal),
			new DependencyFactsCoverage(facts.Count, facts.Count(file => file.Status == DependencyFileStatus.Supported), facts.Count(file => file.Status == DependencyFileStatus.Unsupported), facts.Count(file => file.Status == DependencyFileStatus.ExtractionFailed), new Dictionary<string, int>(), new Dictionary<string, int>()),
			new DependencyIndexMetrics(0, 0, 0, 0, false));
		var entries = importanceOrder.Select((path, index) => new ImportanceRankingEntry(
			Path.Combine(root, path),
			path,
			index + 1,
			index + 1,
			0,
			0,
			1,
			1,
			ImportanceFileRole.Source,
			true,
			true)).ToArray();
		var versions = new Dictionary<string, RankingSourceVersion>(PathComparer.Default)
		{
			[entries[0].FullPath] = default
		};
		var report = new ImportanceRankingReport(
			ImportanceRankingService.AlgorithmId,
			entries,
			entries.Take(10).ToArray(),
			entries.Length,
			facts.Count(file => file.Status == DependencyFileStatus.Supported),
			facts.Count(file => file.Status == DependencyFileStatus.ExtractionFailed),
			1,
			200,
			1,
			ProjectGitHistoryUnavailableReason.None,
			false,
			ImportanceRankingService.GraphVariant)
		{
			SourceVersions = versions
		};
		var candidates = importanceOrder.Select(path =>
			new ImportanceRankingService.Candidate(Path.Combine(root, path), path)).ToArray();
		return new Fixture(root, report, snapshot, ImportanceRankingService.BuildGraph(candidates, snapshot, CancellationToken.None));
	}

	private static FileFacts Facts(
		string path,
		DependencyFileStatus status = DependencyFileStatus.Supported,
		string? reason = null) =>
		new(
			path,
			"scope",
			LanguageId.CSharp,
			"fingerprint",
			0,
			status,
			reason,
			false,
			new Dictionary<string, int>(),
			[],
			[],
			[],
			[],
			new Dictionary<string, string>(),
			[],
			new Dictionary<string, string>(),
			[]);

	private static DependencyEdge Edge(string source, string target) =>
		new(
			source,
			target,
			EvidenceLayer.ExplicitImport,
			ResolutionStatus.Resolved,
			"reference",
			[],
			[],
			[],
			false);

	private sealed record Fixture(
		string Root,
		ImportanceRankingReport Report,
		DependencyIndexSnapshot Snapshot,
		RankingGraph Graph);
}
