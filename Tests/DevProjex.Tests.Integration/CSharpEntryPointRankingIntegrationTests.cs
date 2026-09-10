using DevProjex.Application.Dependencies;
using DevProjex.Application.Ranking;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class CSharpEntryPointRankingIntegrationTests
{
	[Fact]
	public async Task StaticMainMethodProvidesEntryPointRoleWithoutBecomingADeclaration()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var program = fixture.CreateFile("Program.cs", "public sealed class Program { public static void Main(string[] args) { } }");

		var (snapshot, entry) = await RankFileAsync(fixture.Path, [project, program], "Program.cs");

		Assert.Equal(ImportanceFileRole.EntryPoint, entry.Role);
		Assert.DoesNotContain(snapshot.Declarations, declaration =>
			declaration.Identity.SymbolKind == SymbolKind.Function &&
			declaration.Identity.QualifiedName.EndsWith("Main", StringComparison.Ordinal));
		Assert.DoesNotContain(snapshot.Edges, edge => edge.Reference == "Main");
	}

	[Fact]
	public async Task TopLevelStatementProvidesEntryPointRole()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var program = fixture.CreateFile("Startup.cs", "System.Console.WriteLine(\"ready\");");

		var (_, entry) = await RankFileAsync(fixture.Path, [project, program], "Startup.cs");

		Assert.Equal(ImportanceFileRole.EntryPoint, entry.Role);
	}

	[Theory]
	[InlineData("public sealed class Program { public void Main() { } }")]
	[InlineData("public sealed class Main { public void Run() { } }")]
	public async Task NamesWithoutStaticMainDoNotProvideEntryPointRole(string source)
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var candidate = fixture.CreateFile("Candidate.cs", source);

		var (_, entry) = await RankFileAsync(fixture.Path, [project, candidate], "Candidate.cs");

		Assert.Equal(ImportanceFileRole.Source, entry.Role);
	}

	[Fact]
	public async Task MultipleEntryPointSyntaxFormsProduceOneFileRole()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var program = fixture.CreateFile("Program.cs", "System.Console.WriteLine(\"ready\"); class Program { public static void Main() { } }");

		var (snapshot, entry) = await RankFileAsync(fixture.Path, [project, program], "Program.cs");

		Assert.Equal(ImportanceFileRole.EntryPoint, entry.Role);
		Assert.True(snapshot.Files.Single(file => file.Path == "Program.cs").HasEntryPointEvidence);
	}

	[Fact]
	public async Task PythonMainDeclarationKeepsItsEntryPointRole()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var program = fixture.CreateFile("program.py", "def __main__():\n    return 0\n");

		var (_, entry) = await RankFileAsync(fixture.Path, [config, program], "program.py");

		Assert.Equal(ImportanceFileRole.EntryPoint, entry.Role);
	}

	[Fact]
	public async Task ExistingManifestTestAndSourceRolesRemainDistinct()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var source = fixture.CreateFile("Service.cs", "public sealed class Service { }");
		var test = fixture.CreateFile("ServiceTests.cs", "using Xunit; public sealed class ServiceTests { }");
		using var engine = CreateEngine();
		var ranking = new ImportanceRankingService(engine, new UnavailableHistoryReader());

		var report = await ranking.RankAsync(
			fixture.Path,
			[project, source, test],
			TestContext.Current.CancellationToken);

		Assert.Equal(ImportanceFileRole.Manifest, report.Entries.Single(entry => entry.Path == "Fixture.csproj").Role);
		Assert.Equal(ImportanceFileRole.Source, report.Entries.Single(entry => entry.Path == "Service.cs").Role);
		Assert.Equal(ImportanceFileRole.TestSource, report.Entries.Single(entry => entry.Path == "ServiceTests.cs").Role);
	}

	private static async Task<(DependencyIndexSnapshot Snapshot, ImportanceRankingEntry Entry)> RankFileAsync(
		string root,
		IReadOnlyList<string> manifest,
		string relativePath)
	{
		using var engine = CreateEngine();
		var snapshot = await engine.IndexAsync(
			root,
			manifest,
			cancellationToken: TestContext.Current.CancellationToken);
		var ranking = new ImportanceRankingService(engine, new UnavailableHistoryReader());
		var report = await ranking.RankAsync(root, manifest, TestContext.Current.CancellationToken);
		return (snapshot, report.Entries.Single(entry => entry.Path == relativePath));
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
