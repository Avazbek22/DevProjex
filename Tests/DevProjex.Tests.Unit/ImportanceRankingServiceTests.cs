using DevProjex.Application.Dependencies;
using DevProjex.Application.Ranking;

namespace DevProjex.Tests.Unit;

public sealed class ImportanceRankingServiceTests
{
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
			});

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
			snapshot);

		Assert.True(ranks["b.cs"] > ranks["a.cs"]);
		Assert.Equal(ranks["a.cs"], ranks["c.cs"], precision: 10);
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
			[],
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
