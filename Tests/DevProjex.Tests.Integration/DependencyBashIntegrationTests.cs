using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class DependencyBashIntegrationTests
{
	[Fact]
	public async Task LiteralSourcesAndScriptCommandsExposeOnlyAmbiguousManifestCandidates()
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
			Assert.Equal(ResolutionStatus.Ambiguous, edge.Status);
			Assert.Null(edge.Target);
			Assert.Equal("the Bash execution working directory is unknown", Assert.Single(edge.Reasons));
		});
		Assert.Equal(
			new[] { "lib/common.sh" },
			Assert.Single(index.Edges, edge => edge.Reference == "../lib/common.sh").Candidates);
		Assert.Equal(
			new[] { "lib/other.bash" },
			Assert.Single(index.Edges, edge => edge.Reference == "../lib/other.bash").Candidates);
		var workerEdge = Assert.Single(index.Edges, edge => edge.Reference == "./worker.sh");
		Assert.Equal(new[] { "scripts/worker.sh" }, workerEdge.Candidates);
		Assert.Equal(2, workerEdge.Evidence.Count);
		Assert.DoesNotContain(index.Edges, edge => edge.Status == ResolutionStatus.Resolved);
	}

	[Fact]
	public async Task SlashlessSourceListsAllSuffixCandidatesAndNamesRuntimeUncertainty()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("scripts/main.sh", "source helper.sh\n");
		var first = fixture.CreateFile("one/helper.sh", ":\n");
		var second = fixture.CreateFile("two/helper.sh", ":\n");
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(
			fixture.Path,
			[source, first, second],
			cancellationToken: TestContext.Current.CancellationToken);
		var edge = Assert.Single(index.Edges);

		Assert.Equal(ResolutionStatus.Ambiguous, edge.Status);
		Assert.Null(edge.Target);
		Assert.Equal(new[] { "one/helper.sh", "two/helper.sh" }, edge.Candidates);
		Assert.Equal(
			"the Bash execution working directory is unknown; PATH and sourcepath settings are also unknown",
			Assert.Single(edge.Reasons));
	}

	[Fact]
	public async Task SuffixCandidateListIsBoundedAndReportsTheOmittedCount()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("main.sh", "source ./shared.sh\n");
		var manifest = new List<string> { source };
		for (var index = 0; index < 40; index++)
			manifest.Add(fixture.CreateFile($"roots/{index:D2}/shared.sh", ":\n"));
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			manifest,
			cancellationToken: TestContext.Current.CancellationToken);
		var edge = Assert.Single(result.Edges);

		Assert.Equal(ResolutionStatus.Ambiguous, edge.Status);
		Assert.Null(edge.Target);
		Assert.Equal(32, edge.Candidates.Count);
		Assert.Equal("roots/00/shared.sh", edge.Candidates[0]);
		Assert.Equal("roots/31/shared.sh", edge.Candidates[^1]);
		Assert.Equal(
			"the Bash execution working directory is unknown; showing 32 of 40 suffix-matching manifest candidates",
			Assert.Single(edge.Reasons));
	}

	[Fact]
	public async Task SuffixCandidateFanOutCountsTowardTheResolutionWorkLimit()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("main.sh", "source ./shared.sh\n");
		var first = fixture.CreateFile("one/shared.sh", ":\n");
		var second = fixture.CreateFile("two/shared.sh", ":\n");
		using var engine = new DependencyFactsEngine(
			new TreeSitterDependencyFactExtractor(),
			new FileDependencyConfigurationProvider(),
			new DependencyFactsLimits(MaximumWorkPerIndex: 2));

		var result = await engine.IndexAsync(
			fixture.Path,
			[source, first, second],
			cancellationToken: TestContext.Current.CancellationToken);
		var edge = Assert.Single(result.Edges, edge => edge.Source == "main.sh");

		Assert.Equal("<limit>", edge.Reference);
		Assert.Contains("index work limit exceeded", edge.Reasons);
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
