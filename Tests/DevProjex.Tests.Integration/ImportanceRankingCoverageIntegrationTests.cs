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
	public async Task AStaticMainMakesItsFileTheEntryPoint()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var program = fixture.CreateFile("Program.cs", """
			namespace Fixture;
			public static class Program
			{
				public static void Main(string[] args) { }
			}
			""");
		var ordinary = fixture.CreateFile(
			"Service.cs",
			"namespace Fixture; public sealed class Service { }");
		using var engine = CreateEngine();
		var ranking = new ImportanceRankingService(engine, new UnavailableHistoryReader());

		var report = await ranking.RankAsync(
			fixture.Path,
			[project, program, ordinary],
			TestContext.Current.CancellationToken);

		Assert.Equal(
			ImportanceFileRole.EntryPoint,
			report.Entries.Single(entry => entry.Path == "Program.cs").Role);
		Assert.Equal(
			ImportanceFileRole.Source,
			report.Entries.Single(entry => entry.Path == "Service.cs").Role);
	}

	[Fact]
	public async Task TopLevelStatementsMakeTheirCompilationUnitTheEntryPoint()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var program = fixture.CreateFile("Program.cs", """
			System.Console.WriteLine("started");
			System.Console.WriteLine("finished");
			""");
		using var engine = CreateEngine();
		var ranking = new ImportanceRankingService(engine, new UnavailableHistoryReader());

		var report = await ranking.RankAsync(
			fixture.Path,
			[project, program],
			TestContext.Current.CancellationToken);

		Assert.Equal(
			ImportanceFileRole.EntryPoint,
			report.Entries.Single(entry => entry.Path == "Program.cs").Role);
	}

	[Fact]
	public async Task AClassNamedMainAndAnInstanceMainAreOrdinarySource()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var named = fixture.CreateFile(
			"Main.cs",
			"namespace Fixture; public sealed class Main { }");
		var instance = fixture.CreateFile("Runner.cs", """
			namespace Fixture;
			public sealed class Runner
			{
				public void Main() { }
			}
			""");
		using var engine = CreateEngine();
		var ranking = new ImportanceRankingService(engine, new UnavailableHistoryReader());

		var report = await ranking.RankAsync(
			fixture.Path,
			[project, named, instance],
			TestContext.Current.CancellationToken);

		// A type named Main is not an entry point, and an instance method named Main is not one
		// either, because the runtime only starts a static method.
		Assert.Equal(
			ImportanceFileRole.Source,
			report.Entries.Single(entry => entry.Path == "Main.cs").Role);
		Assert.Equal(
			ImportanceFileRole.Source,
			report.Entries.Single(entry => entry.Path == "Runner.cs").Role);
	}

	[Fact]
	public async Task ATestFileKeepsItsTestRoleEvenWithAStaticMain()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var test = fixture.CreateFile("tests/HarnessTests.cs", """
			using Xunit;
			namespace Fixture;
			public static class HarnessTests
			{
				public static void Main() { }
			}
			""");
		using var engine = CreateEngine();
		var ranking = new ImportanceRankingService(engine, new UnavailableHistoryReader());

		var report = await ranking.RankAsync(
			fixture.Path,
			[project, test],
			TestContext.Current.CancellationToken);

		// Test evidence is checked before entry-point evidence and keeps precedence.
		Assert.Equal(
			ImportanceFileRole.TestSource,
			report.Entries.Single(entry => entry.Path == "tests/HarnessTests.cs").Role);
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
