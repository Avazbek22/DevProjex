using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class DependencyBashIntegrationTests
{
	[Fact]
	public async Task LiteralSourcesAndScriptCommandsRemainUnresolvedWithoutRuntimeDirectories()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("scripts/main.sh", "source '../lib/common.sh'\n. \"../lib/other.bash\"\n./worker.sh\nbash ./worker.sh\ngrep value input\n");
		var common = fixture.CreateFile("lib/common.sh", "function setup { :; }\n");
		var other = fixture.CreateFile("lib/other.bash", "cleanup() { :; }\n");
		var worker = fixture.CreateFile("scripts/worker.sh", ":\n");
		using var engine = CreateEngine();
		var index = await engine.IndexAsync(fixture.Path, [source, common, other, worker], cancellationToken: TestContext.Current.CancellationToken);
		Assert.All(index.Files, file => Assert.Equal(DependencyFileStatus.Supported, file.Status));
		Assert.Equal(3, index.Edges.Count);
		Assert.Equal(4, index.Files.Single(file => file.Path == "scripts/main.sh").Imports.Count);
		Assert.All(index.Edges, edge =>
		{
			Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
			Assert.Null(edge.Target);
			Assert.Empty(edge.Candidates);
			Assert.Equal("the Bash execution working directory is unknown", Assert.Single(edge.Reasons));
		});
		Assert.Contains(index.Edges, edge => edge.Reference == "../lib/common.sh");
		Assert.Contains(index.Edges, edge => edge.Reference == "../lib/other.bash");
		Assert.Equal(2, Assert.Single(index.Edges, edge => edge.Reference == "./worker.sh").Evidence.Count);
	}

	[Theory]
	[InlineData("source \"$SCRIPT_DIR/lib.sh\"")]
	[InlineData("source \"${0%/*}/lib.sh\"")]
	[InlineData(". $(dirname \"$0\")/lib.sh")]
	[InlineData("source ~/lib.sh")]
	[InlineData("bash \"$SCRIPT_DIR/lib.sh\"")]
	[InlineData("source ./lib*.sh")]
	[InlineData("source ./lib\\ file.sh")]
	public async Task ExpandedPathsRemainExplicitlyUnresolved(string command)
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("main.sh", command + "\n");
		var target = fixture.CreateFile("lib.sh", ":\n");
		using var engine = CreateEngine();
		var index = await engine.IndexAsync(fixture.Path, [source, target], cancellationToken: TestContext.Current.CancellationToken);
		var edge = Assert.Single(index.Edges);
		Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
		Assert.Null(edge.Target);
		Assert.Equal("Bash path expansion is not supported", Assert.Single(edge.Reasons));
		Assert.Empty(edge.Candidates);
	}

	[Fact]
	public async Task FunctionNavigationNamesRoundTripThroughTheNavigationExtractor()
	{
		using var fixture = new TemporaryDirectory();
		const string text = "prepare() { :; }\nfunction finish { :; }\nfunction reset() { :; }\n";
		var source = fixture.CreateFile("main.sh", text);
		using var engine = CreateEngine();
		var index = await engine.IndexAsync(fixture.Path, [source], cancellationToken: TestContext.Current.CancellationToken);
		var facts = Assert.Single(index.Files);
		Assert.Equal(new[] { "prepare", "finish", "reset" }, facts.NavigationDeclarations.Select(item => item.Name));
		Assert.Equal(3, facts.Declarations.Count);
		using var extractor = new TreeSitterDependencyFactExtractor();
		var navigation = extractor.ExtractNavigation("main.sh", text, facts.ContentFingerprint, TestContext.Current.CancellationToken);
		Assert.Equal(facts.NavigationDeclarations, navigation);
		Assert.All(navigation, item => Assert.Contains(item.Name, text[item.StartIndex..item.EndIndex], StringComparison.Ordinal));
	}

	[Fact]
	public async Task MissingAndExcludedSourcesNeverExpandTheManifest()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("main.sh", "source ./hidden.sh\n. ./missing.sh\n");
		fixture.CreateFile("hidden.sh", ":\n");
		using var engine = CreateEngine();
		var index = await engine.IndexAsync(fixture.Path, [source], cancellationToken: TestContext.Current.CancellationToken);
		Assert.Equal(2, index.Edges.Count);
		Assert.All(index.Edges, edge => { Assert.Equal(ResolutionStatus.Unresolved, edge.Status); Assert.Empty(edge.Candidates); });
	}

	private static DependencyFactsEngine CreateEngine() => new(new TreeSitterDependencyFactExtractor(), new FileDependencyConfigurationProvider());

	[Fact]
	public async Task AbsoluteAndOutsidePathsRemainUnresolvedAndInterpreterFlagsAreNotCommands()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("main.sh", "source /etc/profile\n. ../outside.sh\nbash -c './worker.sh'\nsh -x ./worker.sh\n");
		using var engine = CreateEngine();
		var index = await engine.IndexAsync(fixture.Path, [source], cancellationToken: TestContext.Current.CancellationToken);
		Assert.Equal(2, index.Edges.Count);
		Assert.All(index.Edges, edge => { Assert.Equal(ResolutionStatus.Unresolved, edge.Status); Assert.Null(edge.Target); Assert.Empty(edge.Candidates); });
	}
}
