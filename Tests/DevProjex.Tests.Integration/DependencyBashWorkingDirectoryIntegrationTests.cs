using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class DependencyBashWorkingDirectoryIntegrationTests
{
	private const string WorkingDirectoryReason = "the Bash execution working directory is unknown";
	private const string SourceSearchReason = "the Bash source search depends on unknown PATH and sourcepath settings";

	public static TheoryData<string, bool, bool> RelativePathCases
	{
		get
		{
			var cases = new TheoryData<string, bool, bool>();
			foreach (var command in new[] { "source './lib.sh'", ". ./lib.sh", "./lib.sh", "bash ./lib.sh", "sh ./lib.sh" })
				foreach (var inRoot in new[] { false, true })
					foreach (var besideScript in new[] { false, true })
						cases.Add(command, inRoot, besideScript);
			return cases;
		}
	}

	[Theory]
	[MemberData(nameof(RelativePathCases))]
	public async Task RelativePathsDoNotProveARuntimeTargetFromManifestLocations(string command, bool inRoot, bool besideScript)
	{
		using var fixture = new TemporaryDirectory();
		var files = new List<string> { fixture.CreateFile("scripts/main.sh", command + "\n") };
		if (inRoot) files.Add(fixture.CreateFile("lib.sh", "root_setup() { :; }\n"));
		if (besideScript) files.Add(fixture.CreateFile("scripts/lib.sh", "script_setup() { :; }\n"));
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(fixture.Path, files, cancellationToken: TestContext.Current.CancellationToken);

		AssertUnknownTarget(index, "./lib.sh", WorkingDirectoryReason);
	}

	[Theory]
	[InlineData("source ../lib.sh", "../lib.sh")]
	[InlineData(". ../lib.sh", "../lib.sh")]
	[InlineData("./other.sh", "./other.sh")]
	[InlineData("bash ./other.sh", "./other.sh")]
	[InlineData("bash -- ./other.sh", "./other.sh")]
	[InlineData("sh ./other.sh", "./other.sh")]
	[InlineData("bash other.sh", "other.sh")]
	public async Task ParentPathsAndScriptInvocationsKeepTheirLiteralEvidenceWithoutGuessingABase(string command, string specifier)
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("scripts/main.sh", command + "\n");
		var library = fixture.CreateFile("lib.sh", ":\n");
		var script = fixture.CreateFile("scripts/other.sh", ":\n");
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(fixture.Path, [source, library, script], cancellationToken: TestContext.Current.CancellationToken);

		AssertUnknownTarget(index, specifier, WorkingDirectoryReason);
	}

	[Theory]
	[InlineData("source lib.sh")]
	[InlineData(". lib.sh")]
	public async Task SourcesWithoutASlashDoNotSelectAManifestFileWhenShellSearchSettingsAreUnknown(string command)
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("scripts/main.sh", command + "\n");
		var rootLibrary = fixture.CreateFile("lib.sh", ":\n");
		var scriptLibrary = fixture.CreateFile("scripts/lib.sh", ":\n");
		var pathLibrary = fixture.CreateFile("path/lib.sh", ":\n");
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(fixture.Path, [source, rootLibrary, scriptLibrary, pathLibrary], cancellationToken: TestContext.Current.CancellationToken);

		AssertUnknownTarget(index, "lib.sh", SourceSearchReason);
	}

	[Fact]
	public async Task AWorkingDirectoryOutsideTheSuggestedBasesIsNotRuledOutByAUniqueNearbyFile()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("scripts/main.sh", "source ./lib.sh\n");
		var nearbyLibrary = fixture.CreateFile("scripts/lib.sh", ":\n");
		var runtimeLibrary = fixture.CreateFile("runtime/lib.sh", ":\n");
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(fixture.Path, [source, nearbyLibrary, runtimeLibrary], cancellationToken: TestContext.Current.CancellationToken);

		AssertUnknownTarget(index, "./lib.sh", WorkingDirectoryReason);
	}

	private static void AssertUnknownTarget(DependencyIndexSnapshot index, string specifier, string reason)
	{
		var source = Assert.Single(index.Files, file => file.Path == "scripts/main.sh");
		Assert.Equal(DependencyFileStatus.Supported, source.Status);
		var import = Assert.Single(source.Imports);
		Assert.Equal(specifier, import.Specifier);
		Assert.Equal(ResolutionStatus.Unresolved, import.Status);
		Assert.Equal(reason, import.Reason);
		Assert.Equal(1, import.Site.Line);
		Assert.Contains(specifier, import.Site.Evidence, StringComparison.Ordinal);
		var edge = Assert.Single(index.Edges);
		Assert.Equal(EvidenceLayer.ExplicitImport, edge.Layer);
		Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
		Assert.Null(edge.Target);
		Assert.Empty(edge.Candidates);
		Assert.Equal(reason, Assert.Single(edge.Reasons));
	}

	private static DependencyFactsEngine CreateEngine() => new(new TreeSitterDependencyFactExtractor(), new FileDependencyConfigurationProvider());
}
