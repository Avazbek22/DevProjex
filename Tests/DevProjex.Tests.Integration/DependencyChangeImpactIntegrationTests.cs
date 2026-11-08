using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class DependencyChangeImpactIntegrationTests
{
	[Fact]
	public async Task PackageReExportIsNotADirectImportOfThePrivateModule()
	{
		using var fixture = new TemporaryDirectory();
		var (index, _) = await IndexPackageAsync(fixture);

		Assert.True(Assert.Single(index.Edges, edge => edge.Source == "tests/test_api.py" &&
			edge.Target == "pkg/_config.py").ThroughReExport);
		Assert.False(Assert.Single(index.Edges, edge => edge.Source == "tests/test_direct.py").ThroughReExport);
		Assert.False(Assert.Single(index.Edges, edge => edge.Source == "pkg/_api.py").ThroughReExport);
		Assert.False(Assert.Single(index.Edges, edge => edge.Source == "pkg/__init__.py" &&
			edge.Target == "pkg/_config.py").ThroughReExport);
		Assert.False(Assert.Single(index.Edges, edge => edge.Source == "tests/test_module.py").ThroughReExport);
	}

	[Fact]
	public async Task ImpactListsOnlyDirectImportersAndTestsNamedAfterTheChange()
	{
		using var fixture = new TemporaryDirectory();
		var (index, candidates) = await IndexPackageAsync(fixture);

		var impact = DependencyChangeImpact.Compute(index, ["pkg/_config.py"], candidates);

		Assert.Equal(["pkg/_api.py"], impact.Dependents);
		Assert.Equal(1, impact.DependentCount);
		Assert.Equal(["tests/test_config.py", "tests/test_direct.py", "tests/test_module.py"], impact.Tests);
		Assert.Equal(3, impact.TestCount);
	}

	[Fact]
	public async Task ImpactCapsEachListAtFiveAfterCountingNamedTestsFirst()
	{
		using var fixture = new TemporaryDirectory();
		var manifest = new List<string>
		{
			fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\""),
			fixture.CreateFile("pkg/__init__.py", string.Empty),
			fixture.CreateFile("pkg/_config.py", "class Timeout: pass"),
			fixture.CreateFile("tests/test_config.py", "def test_nothing(): pass")
		};
		for (var number = 0; number < 7; number++)
		{
			manifest.Add(fixture.CreateFile($"pkg/user{number}.py", "from ._config import Timeout"));
			manifest.Add(fixture.CreateFile($"tests/test_a{number}.py", "from pkg._config import Timeout"));
		}
		using var engine = CreateEngine();
		var index = await engine.IndexAsync(fixture.Path, manifest,
			cancellationToken: TestContext.Current.CancellationToken);

		var impact = DependencyChangeImpact.Compute(index, ["pkg/_config.py"], Relative(fixture, manifest));

		Assert.Equal(["pkg/user0.py", "pkg/user1.py", "pkg/user2.py", "pkg/user3.py", "pkg/user4.py"], impact.Dependents);
		Assert.Equal(7, impact.DependentCount);
		Assert.Equal(
			["tests/test_config.py", "tests/test_a0.py", "tests/test_a1.py", "tests/test_a2.py", "tests/test_a3.py"],
			impact.Tests);
		Assert.Equal(8, impact.TestCount);
	}

	[Fact]
	public async Task BarrelIndexThatOnlyReExportsIsNotADependent()
	{
		using var fixture = new TemporaryDirectory();
		var manifest = new[]
		{
			fixture.CreateFile("tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}"),
			fixture.CreateFile("src/lib/a.ts", "export const a = 1;"),
			fixture.CreateFile("src/lib/index.ts", "export * from './a';"),
			fixture.CreateFile("src/b.ts", "import { a } from './lib/a'; export const b = a;")
		};
		using var engine = CreateEngine();
		var index = await engine.IndexAsync(fixture.Path, manifest,
			cancellationToken: TestContext.Current.CancellationToken);

		var impact = DependencyChangeImpact.Compute(index, ["src/lib/a.ts"], Relative(fixture, manifest));

		Assert.Equal(["src/b.ts"], impact.Dependents);
		Assert.Equal(1, impact.DependentCount);
	}

	private static async Task<(DependencyIndexSnapshot Index, IReadOnlySet<string> Candidates)> IndexPackageAsync(
		TemporaryDirectory fixture)
	{
		var manifest = new[]
		{
			fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\""),
			fixture.CreateFile("pkg/__init__.py", "from ._config import Timeout\nfrom ._api import get"),
			fixture.CreateFile("pkg/_config.py", "class Timeout: pass"),
			fixture.CreateFile("pkg/_api.py", "from ._config import Timeout\ndef get(): return Timeout()"),
			fixture.CreateFile("tests/test_config.py", "def test_timeout(): pass"),
			fixture.CreateFile("tests/test_api.py", "from pkg import Timeout\ndef test_api(): pass"),
			fixture.CreateFile("tests/test_direct.py", "from pkg._config import Timeout\ndef test_direct(): pass"),
			fixture.CreateFile("tests/test_module.py", "from pkg import _config\ndef test_module(): pass")
		};
		using var engine = CreateEngine();
		var index = await engine.IndexAsync(fixture.Path, manifest,
			cancellationToken: TestContext.Current.CancellationToken);
		return (index, Relative(fixture, manifest));
	}

	private static IReadOnlySet<string> Relative(TemporaryDirectory fixture, IEnumerable<string> files) =>
		files.Select(path => Path.GetRelativePath(fixture.Path, path).Replace('\\', '/'))
			.ToHashSet(StringComparer.Ordinal);

	private static DependencyFactsEngine CreateEngine() => new(
		new TreeSitterDependencyFactExtractor(),
		new FileDependencyConfigurationProvider());
}
