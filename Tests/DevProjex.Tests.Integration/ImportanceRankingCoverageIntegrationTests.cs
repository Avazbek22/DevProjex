using DevProjex.Application.Dependencies;
using DevProjex.Application.Ranking;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class ImportanceRankingCoverageIntegrationTests
{
	[Fact]
	public async Task ResolvedReferenceCoverageCountsLogicalGroupsSeparatelyFromFilePairs()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var types = fixture.CreateFile("Types.cs", "namespace Fixture; public sealed class Alpha { } public sealed class Beta { }");
		var consumer = fixture.CreateFile("Consumer.cs", "namespace Fixture; public sealed class Consumer { Alpha First; Beta Second; }");
		using var engine = CreateEngine();
		var ranking = new ImportanceRankingService(engine, new UnavailableHistoryReader());

		var report = await ranking.RankAsync(
			fixture.Path,
			[project, types, consumer],
			TestContext.Current.CancellationToken);

		Assert.Equal(2, report.ResolvedInternalReferences);
		Assert.Equal(2, report.InternalReferenceCandidates);
		Assert.Equal(1, report.ResolvedInternalReferenceCoverage, precision: 10);
		Assert.Equal(1, report.UniqueResolvedFilePairs);
		Assert.Equal(2, report.FilesWithResolvedEdges);
		Assert.Equal(["Types.cs", "Fixture.csproj", "Consumer.cs"],
			report.Entries.Select(static entry => entry.Path));
	}

	[Fact]
	public async Task PartialDeclarationFilesRemainOneResolvedLogicalReference()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var first = fixture.CreateFile("Partial.One.cs", "namespace Fixture; public partial class Shared { }");
		var second = fixture.CreateFile("Partial.Two.cs", "namespace Fixture; public partial class Shared { }");
		var consumer = fixture.CreateFile("Consumer.cs", "namespace Fixture; public sealed class Consumer { Shared Value; }");
		using var engine = CreateEngine();
		var ranking = new ImportanceRankingService(engine, new UnavailableHistoryReader());

		var report = await ranking.RankAsync(
			fixture.Path,
			[project, first, second, consumer],
			TestContext.Current.CancellationToken);

		Assert.Equal(1, report.ResolvedInternalReferences);
		Assert.Equal(1, report.InternalReferenceCandidates);
		Assert.Equal(1, report.ResolvedInternalReferenceCoverage, precision: 10);
		Assert.Equal(2, report.UniqueResolvedFilePairs);
	}

	[Fact]
	public async Task SelfReferenceCountsAsResolvedEvidenceButNotAsRankingGraphPair()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var source = fixture.CreateFile("Node.cs", "namespace Fixture; public sealed class Node { Node? Next; }");
		using var engine = CreateEngine();
		var ranking = new ImportanceRankingService(engine, new UnavailableHistoryReader());

		var report = await ranking.RankAsync(
			fixture.Path,
			[project, source],
			TestContext.Current.CancellationToken);

		Assert.Equal(1, report.ResolvedInternalReferences);
		Assert.Equal(1, report.InternalReferenceCandidates);
		Assert.Equal(1, report.ResolvedInternalReferenceCoverage, precision: 10);
		Assert.Equal(0, report.UniqueResolvedFilePairs);
		Assert.Equal(0, report.Entries.Single(entry => entry.Path == "Node.cs").Dependencies);
	}

	private static DependencyFactsEngine CreateEngine() => new(
		new TreeSitterDependencyFactExtractor(),
		new FileDependencyConfigurationProvider());

	private sealed class UnavailableHistoryReader : IProjectGitHistoryReader
	{
		public Task<ProjectGitHistorySnapshot> ReadAsync(
			string sourceRoot,
			IReadOnlyList<string> candidateFiles,
			CancellationToken cancellationToken = default) =>
			Task.FromResult(new ProjectGitHistorySnapshot(
				200,
				0,
				new Dictionary<string, ProjectGitFileActivity>(PathComparer.Default),
				candidateFiles.ToDictionary(
					Path.GetFullPath,
					static _ => ProjectGitHistoryUnavailableReason.NotRepository,
					PathComparer.Default),
				ProjectGitHistoryUnavailableReason.NotRepository));
	}
}
