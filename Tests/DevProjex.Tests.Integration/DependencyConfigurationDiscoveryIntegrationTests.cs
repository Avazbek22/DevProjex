using System.Collections.Concurrent;
using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class DependencyConfigurationDiscoveryIntegrationTests
{
	[Fact]
	public async Task NarrowedPythonSelection_ResolvesEveryEdgeTheFullProjectResolvesInsideIt()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", """
			[project]
			name = "demo"
			dependencies = ["colorama"]
			""");
		var package = fixture.CreateFile("src/demo/__init__.py", "from .core import Command");
		var core = fixture.CreateFile("src/demo/core.py", "class Command: pass\nclass Group: pass");
		var decorators = fixture.CreateFile("src/demo/decorators.py", """
			import os
			import colorama
			from .core import Command
			from demo.core import Group
			from . import core
			""");
		var test = fixture.CreateFile("tests/test_core.py", "from demo.core import Command");
		using var engine = CreateEngine();

		var full = await engine.IndexAsync(
			fixture.Path,
			[config, package, core, decorators, test],
			cancellationToken: TestContext.Current.CancellationToken);
		var narrowed = await engine.IndexAsync(
			fixture.Path,
			[package, core, decorators],
			cancellationToken: TestContext.Current.CancellationToken);

		AssertNarrowingKeepsEdges(full, narrowed);
		Assert.Contains(narrowed.Edges, edge => edge.Source == "src/demo/decorators.py" &&
			edge.Reference == "colorama" && edge.Status == ResolutionStatus.External &&
			edge.Reasons.Contains("declared Python package outside the manifest"));
		Assert.Contains(narrowed.Edges, edge => edge.Source == "src/demo/decorators.py" &&
			edge.Reference == "os" && edge.Status == ResolutionStatus.External);
		Assert.Equal(3, narrowed.Edges.Count(edge => edge.Source == "src/demo/decorators.py" &&
			edge.Status == ResolutionStatus.Resolved));
		Assert.DoesNotContain(narrowed.Files, file => file.Path == "pyproject.toml");
	}

	[Fact]
	public async Task ExtensionFilteredView_ReadsTypeScriptPathsWithoutDeliveringTheConfiguration()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json",
			"{\"compilerOptions\":{\"paths\":{\"@lib/*\":[\"./lib/*\"]}}}");
		var package = fixture.CreateFile("package.json", "{\"name\":\"demo\",\"type\":\"module\"}");
		var target = fixture.CreateFile("lib/util.ts", "export const util = 1;");
		var source = fixture.CreateFile("app/main.ts", "import { util } from '@lib/util';");
		using var engine = CreateEngine();

		var full = await engine.IndexAsync(
			fixture.Path,
			[config, package, target, source],
			cancellationToken: TestContext.Current.CancellationToken);
		var filtered = await engine.IndexAsync(
			fixture.Path,
			[target, source],
			cancellationToken: TestContext.Current.CancellationToken);

		AssertNarrowingKeepsEdges(full, filtered);
		Assert.Contains(filtered.Edges, edge => edge.Source == "app/main.ts" &&
			edge.Reference == "@lib/util" && edge.Status == ResolutionStatus.Resolved &&
			edge.Target == "lib/util.ts");
		Assert.Equal(["app/main.ts", "lib/util.ts"], filtered.Files.Select(static file => file.Path));
	}

	[Fact]
	public async Task CSharpProjectOutsideTheSelection_StillOwnsItsSourceFiles()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var target = fixture.CreateFile("App/Models/User.cs", "namespace App.Models; public sealed class User { }");
		var source = fixture.CreateFile("App/Services/Store.cs",
			"using App.Models; namespace App.Services; public sealed class Store { User Value; }");
		using var engine = CreateEngine();

		var full = await engine.IndexAsync(
			fixture.Path,
			[project, target, source],
			cancellationToken: TestContext.Current.CancellationToken);
		var narrowed = await engine.IndexAsync(
			fixture.Path,
			[target, source],
			cancellationToken: TestContext.Current.CancellationToken);

		AssertNarrowingKeepsEdges(full, narrowed);
		Assert.Contains(narrowed.Edges, edge => edge.Source == "App/Services/Store.cs" &&
			edge.Reference == "User" && edge.Status == ResolutionStatus.Resolved &&
			edge.Target == "App/Models/User.cs");
	}

	[Fact]
	public async Task Discovery_ReadsOnlyAncestorConfigurationInsideTheRootForThePresentLanguages()
	{
		using var fixture = new TemporaryDirectory();
		fixture.CreateFile("pyproject.toml", "[project]\nname = \"outside\"");
		fixture.CreateFile("repo/package.json", "{\"name\":\"web\"}");
		fixture.CreateFile("repo/other/pyproject.toml", "[project]\nname = \"other\"");
		fixture.CreateFile("repo/pkg/pyproject.toml", "[project]\nname = \"pkg\"");
		var source = fixture.CreateFile("repo/pkg/mod/a.py", "VALUE = 1");
		var root = Path.Combine(fixture.Path, "repo");
		var reader = new CountingControlFileReader();
		var provider = new FileDependencyConfigurationProvider(reader);

		var configuration = await provider.ReadAsync(root, [source], TestContext.Current.CancellationToken);

		Assert.Contains(configuration.Scopes, scope => scope.ScopeId == "python:pkg/pyproject.toml" &&
			scope.HasConfiguration && scope.ConfigurationState == DependencyConfigurationState.Valid);
		Assert.Equal([Path.Combine(root, "pkg", "pyproject.toml")], reader.Paths);
		Assert.Empty(configuration.PackageMaps);
		Assert.True(configuration.CanCache);
		Assert.Contains(configuration.ObservedControlPaths, stamp => !stamp.IsDirectory &&
			PathComparer.Default.Equals(stamp.Path, Path.Combine(root, "pkg", "pyproject.toml")));
		Assert.Equal(
			new[] { root, Path.Combine(root, "pkg"), Path.Combine(root, "pkg", "mod") }.Order(StringComparer.Ordinal),
			configuration.ObservedControlPaths.Where(static stamp => stamp.IsDirectory)
				.Select(static stamp => stamp.Path));
	}

	[Fact]
	public async Task RepeatedNarrowedIndex_ReusesTheSnapshotUntilDiscoveredConfigurationChanges()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{\"compilerOptions\":{\"paths\":{\"alias\":[\"./first.ts\"]}}}");
		var source = fixture.CreateFile("main.ts", "import { value } from 'alias';");
		var firstTarget = fixture.CreateFile("first.ts", "export const value = 1;");
		var secondTarget = fixture.CreateFile("second.ts", "export const value = 2;");
		var manifest = new[] { source, firstTarget, secondTarget };
		var reader = new CountingControlFileReader();
		using var engine = CreateEngine(reader);

		var first = await engine.IndexAsync(fixture.Path, manifest,
			cancellationToken: TestContext.Current.CancellationToken);
		var repeated = await engine.IndexAsync(fixture.Path, manifest,
			cancellationToken: TestContext.Current.CancellationToken);
		File.WriteAllText(config, "{\"compilerOptions\":{\"paths\":{\"alias\":[\"./second.ts\"]}}}");
		File.SetLastWriteTimeUtc(config, DateTime.UtcNow.AddSeconds(2));
		var edited = await engine.IndexAsync(fixture.Path, manifest,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(first.Edges, edge => edge.Source == "main.ts" && edge.Target == "first.ts");
		Assert.True(repeated.Metrics.ResolutionCacheHit);
		Assert.Equal(2, reader.CountFor(config));
		Assert.Contains(edited.Edges, edge => edge.Source == "main.ts" && edge.Target == "second.ts");
		Assert.DoesNotContain(edited.Edges, edge => edge.Source == "main.ts" && edge.Target == "first.ts");
	}

	[Fact]
	public async Task ConfigurationCreatedOutsideTheSelection_InvalidatesTheSnapshot()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("lib/pkg/mod.py", "import colorama");
		var reader = new CountingControlFileReader();
		using var engine = CreateEngine(reader);

		var before = await engine.IndexAsync(fixture.Path, [source],
			cancellationToken: TestContext.Current.CancellationToken);
		var config = fixture.CreateFile("lib/pyproject.toml", "[project]\nname = \"lib\"\ndependencies = [\"colorama\"]");
		var after = await engine.IndexAsync(fixture.Path, [source],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(before.Edges, edge => edge.Reference == "colorama" &&
			edge.Status == ResolutionStatus.Unresolved);
		Assert.Contains(after.Edges, edge => edge.Reference == "colorama" &&
			edge.Status == ResolutionStatus.External);
		Assert.Equal(1, reader.CountFor(config));
	}

	[Fact]
	public async Task TypeScriptBaseOutsideTheSelection_KeepsTheConfigurationCacheable()
	{
		using var fixture = new TemporaryDirectory();
		var baseConfig = fixture.CreateFile("tsconfig.base.json", "{\"compilerOptions\":{\"paths\":{\"alias\":[\"./first.ts\"]}}}");
		var config = fixture.CreateFile("tsconfig.json", "{\"extends\":\"./tsconfig.base.json\"}");
		var source = fixture.CreateFile("main.ts", "import { value } from 'alias';");
		var firstTarget = fixture.CreateFile("first.ts", "export const value = 1;");
		var secondTarget = fixture.CreateFile("second.ts", "export const value = 2;");
		var manifest = new[] { source, firstTarget, secondTarget };
		var reader = new CountingControlFileReader();
		using var engine = CreateEngine(reader);

		await engine.IndexAsync(fixture.Path, manifest, cancellationToken: TestContext.Current.CancellationToken);
		var repeated = await engine.IndexAsync(fixture.Path, manifest,
			cancellationToken: TestContext.Current.CancellationToken);
		File.WriteAllText(baseConfig, "{\"compilerOptions\":{\"paths\":{\"alias\":[\"./second.ts\"]}}}");
		File.SetLastWriteTimeUtc(baseConfig, DateTime.UtcNow.AddSeconds(2));
		var edited = await engine.IndexAsync(fixture.Path, manifest,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(repeated.Metrics.ResolutionCacheHit);
		Assert.Equal(2, reader.CountFor(config));
		Assert.Contains(edited.Edges, edge => edge.Source == "main.ts" && edge.Target == "second.ts");
	}

	// Every edge the full project resolves between two files of the narrowed view resolves to the
	// same target there: narrowing changes what is shown, not what is understood.
	private static void AssertNarrowingKeepsEdges(DependencyIndexSnapshot full, DependencyIndexSnapshot narrowed)
	{
		var visible = narrowed.Files.Select(static file => file.Path).ToHashSet(StringComparer.Ordinal);
		var expected = full.Edges
			.Where(edge => edge.Status == ResolutionStatus.Resolved && visible.Contains(edge.Source) &&
				edge.Target is not null && visible.Contains(edge.Target))
			.ToArray();
		Assert.NotEmpty(expected);
		Assert.All(expected, edge => Assert.Contains(narrowed.Edges, candidate =>
			candidate.Source == edge.Source && candidate.Reference == edge.Reference &&
			candidate.Status == ResolutionStatus.Resolved && candidate.Target == edge.Target));
	}

	private static DependencyFactsEngine CreateEngine(IDependencyControlFileReader? reader = null) =>
		new(new TreeSitterDependencyFactExtractor(), reader is null
			? new FileDependencyConfigurationProvider()
			: new FileDependencyConfigurationProvider(reader));

	private sealed class CountingControlFileReader : IDependencyControlFileReader
	{
		private readonly BoundedDependencyControlFileReader _inner = new();
		private readonly ConcurrentDictionary<string, int> _counts = new(PathComparer.Default);
		private readonly ConcurrentQueue<string> _paths = new();

		public IReadOnlyList<string> Paths => _paths.ToArray();

		public int CountFor(string path) => _counts.GetValueOrDefault(Path.GetFullPath(path));

		public ValueTask<DependencyControlFileSnapshot> ReadAsync(
			string path,
			int maximumBytes,
			CancellationToken cancellationToken)
		{
			_paths.Enqueue(path);
			_counts.AddOrUpdate(Path.GetFullPath(path), 1, static (_, count) => count + 1);
			return _inner.ReadAsync(path, maximumBytes, cancellationToken);
		}
	}
}
