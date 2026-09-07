using DevProjex.Application.Dependencies;
using DevProjex.Application.Ranking;

namespace DevProjex.Tests.Unit;

public sealed class ImportanceRankingServiceTests
{
	[Theory]
	[InlineData(100, 60, 20, 20, 0.60)]
	[InlineData(10, 5, 0, 5, 0.50)]
	public void ExtractedFactsCoverage_DoesNotSubtractMutuallyExclusiveFailures(
		int candidates,
		int supported,
		int unsupported,
		int extractionFailed,
		double expected)
	{
		var coverage = new DependencyFactsCoverage(
			candidates,
			supported,
			unsupported,
			extractionFailed,
			new Dictionary<string, int>(),
			new Dictionary<string, int>());

		var actual = ImportanceRankingService.CalculateExtractedFactsCoverage(coverage, candidates);

		Assert.Equal(expected, actual, precision: 10);
		Assert.Equal(candidates, coverage.Supported + coverage.Unsupported + coverage.ExtractionFailed);
	}

	[Fact]
	public void RankNormalize_UsesRanksAndPreservesMissingSignals()
	{
		var normalized = ImportanceRankingService.RankNormalize(
			new Dictionary<string, double?>
			{
				["small.cs"] = 1,
				["peer.cs"] = 1,
				["ordinary.cs"] = 10,
				["giant.cs"] = 10_000,
				["missing.cs"] = null
			},
			TestContext.Current.CancellationToken);

		Assert.Equal(0, normalized["small.cs"]);
		Assert.Equal(0, normalized["peer.cs"]);
		Assert.Equal(0.5, normalized["ordinary.cs"]);
		Assert.Equal(1, normalized["giant.cs"]);
		Assert.Null(normalized["missing.cs"]);
	}

	[Fact]
	public void CalculateScore_ScalesGraphWeightByCoverageAndRedistributesMissingSignals()
	{
		var fullCoverage = ImportanceRankingService.CalculateScore(graph: 1, git: 0, role: 0, graphCoverage: 1);
		var halfCoverage = ImportanceRankingService.CalculateScore(graph: 1, git: 0, role: 0, graphCoverage: 0.5);
		var noGraph = ImportanceRankingService.CalculateScore(graph: null, git: 1, role: 0, graphCoverage: 1);

		Assert.Equal(ImportanceRankingService.GraphWeight, fullCoverage, precision: 10);
		Assert.True(halfCoverage < fullCoverage);
		Assert.Equal(
			ImportanceRankingService.GitWeight /
			(ImportanceRankingService.GitWeight + ImportanceRankingService.RoleWeight),
			noGraph,
			precision: 10);
	}

	[Theory]
	[InlineData("tests/ServiceTests.cs", "xunit", ImportanceFileRole.TestSource)]
	[InlineData("src/service.spec.ts", "@vitest/expect", ImportanceFileRole.TestSource)]
	[InlineData("tests/test_service.py", "pytest", ImportanceFileRole.TestSource)]
	[InlineData("src/service.cs", "NUnit.Framework", ImportanceFileRole.TestSource)]
	[InlineData("src/App.csproj", null, ImportanceFileRole.Manifest)]
	[InlineData("package.json", null, ImportanceFileRole.Manifest)]
	[InlineData("src/main.cs", null, ImportanceFileRole.EntryPoint)]
	[InlineData("src/model.cs", null, ImportanceFileRole.Source)]
	public void ClassifyRole_UsesFactsBeforePathConventions(
		string path,
		string? importedFramework,
		ImportanceFileRole expected)
	{
		var facts = CreateFacts(path, importedFramework);
		var role = ImportanceRankingService.ClassifyRole(
			path,
			facts,
			dependents: expected == ImportanceFileRole.EntryPoint ? 0 : 1,
			dependencies: expected == ImportanceFileRole.EntryPoint ? 3 : 0);

		Assert.Equal(expected, role);
	}

	[Fact]
	public void CalculatePageRank_UsesUniqueResolvedNonSelfEdges()
	{
		var edges = new[]
		{
			Edge("a.cs", "b.cs"),
			Edge("a.cs", "b.cs"),
			Edge("b.cs", "b.cs"),
			Edge("c.cs", "b.cs"),
			Edge("c.cs", null, ResolutionStatus.Unresolved)
		};
		var snapshot = Snapshot(edges);

		var ranks = ImportanceRankingService.CalculatePageRank(
			new[]
			{
				(FullPath: "a.cs", RelativePath: "a.cs"),
				(FullPath: "b.cs", RelativePath: "b.cs"),
				(FullPath: "c.cs", RelativePath: "c.cs")
			},
			snapshot,
			TestContext.Current.CancellationToken);

		Assert.True(ranks["b.cs"] > ranks["a.cs"]);
		Assert.Equal(ranks["a.cs"], ranks["c.cs"], precision: 10);
	}

	[Fact]
	public void CalculatePageRank_IsDeterministicAcrossInputOrderAndOmitsUnsupportedFiles()
	{
		var edges = new[] { Edge("a.cs", "b.cs"), Edge("c.cs", "b.cs"), Edge("b.cs", "a.cs") };
		var firstSnapshot = Snapshot(edges) with
		{
			Files =
			[
				CreateFacts("a.cs", null),
				CreateFacts("b.cs", null),
				CreateFacts("c.cs", null),
				CreateFacts("notes.md", null) with { Status = DependencyFileStatus.Unsupported }
			]
		};
		var secondSnapshot = firstSnapshot with
		{
			Files = firstSnapshot.Files.Reverse().ToArray(),
			Edges = firstSnapshot.Edges.Reverse().ToArray()
		};
		var candidates = new[]
		{
			(FullPath: "notes.md", RelativePath: "notes.md"),
			(FullPath: "c.cs", RelativePath: "c.cs"),
			(FullPath: "b.cs", RelativePath: "b.cs"),
			(FullPath: "a.cs", RelativePath: "a.cs")
		};

		var first = ImportanceRankingService.CalculatePageRank(
			candidates,
			firstSnapshot,
			TestContext.Current.CancellationToken);
		var second = ImportanceRankingService.CalculatePageRank(
			candidates.Reverse().ToArray(),
			secondSnapshot,
			TestContext.Current.CancellationToken);

		Assert.DoesNotContain("notes.md", first.Keys);
		Assert.Equal(first.OrderBy(static pair => pair.Key), second.OrderBy(static pair => pair.Key));
	}

	[Fact]
	public void CalculatePageRank_ObservesCancellationInsideIterations()
	{
		const int count = 2_000;
		var candidates = Enumerable.Range(0, count)
			.Select(index => (
				FullPath: $"{index:D4}.cs",
				RelativePath: $"{index:D4}.cs"))
			.ToArray();
		var edges = Enumerable.Range(0, count)
			.Select(index => Edge($"{index:D4}.cs", $"{(index + 1) % count:D4}.cs"))
			.ToArray();
		var snapshot = Snapshot(edges) with
		{
			Files = candidates.Select(candidate => CreateFacts(candidate.RelativePath, null)).ToArray()
		};
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.Current.CancellationToken);

		Assert.Throws<OperationCanceledException>(() =>
			ImportanceRankingService.CalculatePageRank(
				candidates,
				snapshot,
				cancellation.Token,
				iteration =>
				{
					if (iteration == 1)
						cancellation.Cancel();
				}));
	}

	private static FileFacts CreateFacts(string path, string? importedFramework)
	{
		var imports = importedFramework is null
			? Array.Empty<ImportFact>()
			: new[]
			{
				new ImportFact(
					importedFramework,
					null,
					null,
					false,
					0,
					new SourceSite(path, 1, "test framework"))
			};
		return new FileFacts(
			path,
			"scope",
			LanguageId.CSharp,
			"fingerprint",
			0,
			DependencyFileStatus.Supported,
			null,
			false,
			new Dictionary<string, int>(),
			[],
			imports,
			[],
			[],
			new Dictionary<string, string>(),
			[],
			new Dictionary<string, string>(),
			[]);
	}

	private static DependencyEdge Edge(
		string source,
		string? target,
		ResolutionStatus status = ResolutionStatus.Resolved) =>
		new(
			source,
			target,
			EvidenceLayer.ExplicitImport,
			status,
			"reference",
			[],
			[],
			[],
			false);

	private static DependencyIndexSnapshot Snapshot(IReadOnlyList<DependencyEdge> edges) =>
		new(
			"root",
			"manifest",
			"declarations",
			[CreateFacts("a.cs", null), CreateFacts("b.cs", null), CreateFacts("c.cs", null)],
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
			new DependencyFactsCoverage(0, 0, 0, 0, new Dictionary<string, int>(), new Dictionary<string, int>()),
			new DependencyIndexMetrics(0, 0, 0, 0, false));
}
