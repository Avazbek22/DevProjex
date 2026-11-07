using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class DependencyOutsideSelectionIntegrationTests
{
	[Fact]
	public async Task NarrowedSelection_ListsTheDependencyOutsideItAndExplainsItsUnresolvedReference()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var model = fixture.CreateFile("Models/User.cs", "namespace App.Models; public sealed class User { }");
		var store = fixture.CreateFile("Services/Store.cs",
			"using App.Models; namespace App.Services; public sealed class Store { User Value; Clock Time; }");
		var clock = fixture.CreateFile("Services/Clock.cs", "namespace App.Services; public sealed class Clock { }");
		using var engine = CreateEngine();

		var selection = await engine.FindRelatedAsync(
			fixture.Path,
			[store, clock],
			["Services/Store.cs"],
			DependencyDirection.Dependencies,
			cancellationToken: TestContext.Current.CancellationToken);
		var outside = await engine.FindOutsideSelectionAsync(
			fixture.Path,
			selection,
			[project, model, store, clock],
			DependencyDirection.Dependencies,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(selection.Index.Edges, edge => edge.Source == "Services/Store.cs" &&
			edge.Reference == "User" && edge.Status == ResolutionStatus.Unresolved);
		var seed = Assert.Single(outside.Seeds);
		Assert.Equal("Services/Store.cs", seed.Seed);
		var dependency = Assert.Single(seed.Dependencies);
		Assert.Equal("Models/User.cs", dependency.Path);
		Assert.Equal(ResolutionStatus.Resolved, dependency.Status);
		Assert.Contains("type reference User at line 1", dependency.Reasons);
		Assert.Equal(1, seed.DependencyCount);
		Assert.Empty(seed.Dependents);
		Assert.Equal(1, outside.Edges);
		Assert.Equal(1, outside.ExplainedUnresolvedEdges);
		// The selection resolved Clock itself, so it is not repeated outside.
		Assert.DoesNotContain(seed.Dependencies, file => file.Path == "Services/Clock.cs");
	}

	[Fact]
	public async Task NarrowedSelection_ListsDependentsOutsideIt()
	{
		using var fixture = new TemporaryDirectory();
		var core = fixture.CreateFile("src/demo/core.py", "class Command: pass");
		var package = fixture.CreateFile("src/demo/__init__.py", string.Empty);
		var test = fixture.CreateFile("tests/test_core.py", "from demo.core import Command");
		using var engine = CreateEngine();

		var selection = await engine.FindRelatedAsync(
			fixture.Path,
			[core, package],
			["src/demo/core.py"],
			DependencyDirection.Dependents,
			cancellationToken: TestContext.Current.CancellationToken);
		var outside = await engine.FindOutsideSelectionAsync(
			fixture.Path,
			selection,
			[core, package, test],
			DependencyDirection.Dependents,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(DependencyFactsEngine.CanRelateOutsideSelection(selection, DependencyDirection.Dependents));
		var seed = Assert.Single(outside.Seeds);
		var dependent = Assert.Single(seed.Dependents);
		Assert.Equal("tests/test_core.py", dependent.Path);
		Assert.Contains("import demo.core at line 1", dependent.Reasons);
		Assert.Equal(0, outside.ExplainedUnresolvedEdges);
		Assert.Equal(1, outside.Edges);
	}

	[Fact]
	public async Task PriorityPathsTargetOutsideTheSelection_IsListedInsteadOfAFallback()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json",
			"{\"compilerOptions\":{\"paths\":{\"@lib/*\":[\"./first/*\",\"./second/*\"]}}}");
		var first = fixture.CreateFile("first/util.ts", "export const util = 1;");
		var second = fixture.CreateFile("second/util.ts", "export const util = 2;");
		var source = fixture.CreateFile("app/main.ts", "import { util } from '@lib/util';");
		using var engine = CreateEngine();

		var selection = await engine.FindRelatedAsync(
			fixture.Path,
			[second, source],
			["app/main.ts"],
			DependencyDirection.Dependencies,
			cancellationToken: TestContext.Current.CancellationToken);
		var outside = await engine.FindOutsideSelectionAsync(
			fixture.Path,
			selection,
			[config, first, second, source],
			DependencyDirection.Dependencies,
			cancellationToken: TestContext.Current.CancellationToken);

		// The existing priority target stops the fallback, so the selection reports the import as
		// unresolved rather than settling on second/; the project names the file TypeScript uses.
		Assert.Contains(selection.Index.Edges, edge => edge.Source == "app/main.ts" &&
			edge.Reference == "@lib/util" && edge.Status == ResolutionStatus.Unresolved);
		var dependency = Assert.Single(Assert.Single(outside.Seeds).Dependencies);
		Assert.Equal("first/util.ts", dependency.Path);
		Assert.Equal(1, outside.ExplainedUnresolvedEdges);
	}

	[Fact]
	public async Task OutsideLists_AreCappedInPathOrderAndKeepTheirTotals()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var manifest = new List<string> { project };
		var fields = new List<string>();
		for (var index = 0; index < 25; index++)
		{
			var name = $"Type{index:00}";
			manifest.Add(fixture.CreateFile($"Models/{name}.cs", $"namespace App; public sealed class {name} {{ }}"));
			fields.Add($"{name} Field{index};");
		}
		var seedPath = fixture.CreateFile("Use/Seed.cs",
			"namespace App; public sealed class Seed { " + string.Join(' ', fields) + " }");
		manifest.Add(seedPath);
		using var engine = CreateEngine();

		var selection = await engine.FindRelatedAsync(
			fixture.Path,
			[seedPath],
			["Use/Seed.cs"],
			DependencyDirection.Dependencies,
			cancellationToken: TestContext.Current.CancellationToken);
		var outside = await engine.FindOutsideSelectionAsync(
			fixture.Path,
			selection,
			manifest,
			DependencyDirection.Dependencies,
			cancellationToken: TestContext.Current.CancellationToken);

		var seed = Assert.Single(outside.Seeds);
		Assert.Equal(25, seed.DependencyCount);
		Assert.Equal(DependencyOutsideSelectionResult.MaximumFilesPerSection, seed.Dependencies.Count);
		Assert.Equal(
			Enumerable.Range(0, 20).Select(static index => $"Models/Type{index:00}.cs"),
			seed.Dependencies.Select(static file => file.Path));
		Assert.Equal(25, outside.Edges);
		Assert.Equal(25, outside.ExplainedUnresolvedEdges);
	}

	[Fact]
	public async Task WholeProjectSelection_HasNothingOutsideIt()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var model = fixture.CreateFile("Models/User.cs", "namespace App.Models; public sealed class User { }");
		var store = fixture.CreateFile("Services/Store.cs",
			"using App.Models; namespace App.Services; public sealed class Store { User Value; Missing Other; }");
		var manifest = new[] { project, model, store };
		using var engine = CreateEngine();

		var selection = await engine.FindRelatedAsync(
			fixture.Path,
			manifest,
			["Services/Store.cs"],
			DependencyDirection.Both,
			cancellationToken: TestContext.Current.CancellationToken);
		var outside = await engine.FindOutsideSelectionAsync(
			fixture.Path,
			selection,
			manifest,
			DependencyDirection.Both,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Empty(outside.Seeds);
		Assert.Equal(0, outside.Edges);
		Assert.Equal(0, outside.ExplainedUnresolvedEdges);
	}

	[Fact]
	public async Task ProjectIndexIsSkipped_WhenNoDependentIsRequestedAndEverythingResolved()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var model = fixture.CreateFile("Models/User.cs", "namespace App.Models; public sealed class User { }");
		var store = fixture.CreateFile("Services/Store.cs",
			"using App.Models; namespace App.Services; public sealed class Store { User Value; }");
		using var engine = CreateEngine();

		var selection = await engine.FindRelatedAsync(
			fixture.Path,
			[project, model, store],
			["Services/Store.cs"],
			DependencyDirection.Dependencies,
			cancellationToken: TestContext.Current.CancellationToken);
		var unsupported = await engine.FindRelatedAsync(
			fixture.Path,
			[fixture.CreateFile("notes.txt", "plain text")],
			["notes.txt"],
			DependencyDirection.Both,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.False(DependencyFactsEngine.CanRelateOutsideSelection(selection, DependencyDirection.Dependencies));
		Assert.True(DependencyFactsEngine.CanRelateOutsideSelection(selection, DependencyDirection.Both));
		Assert.False(DependencyFactsEngine.CanRelateOutsideSelection(unsupported, DependencyDirection.Both));
	}

	[Fact]
	public async Task NarrowedSelection_ListsAPythonModuleConstantSourceOutsideIt()
	{
		using var fixture = new TemporaryDirectory();
		var package = fixture.CreateFile("pkg/__init__.py", string.Empty);
		var config = fixture.CreateFile("pkg/_config.py", "DEFAULT_TIMEOUT_CONFIG = 5.0");
		var api = fixture.CreateFile("pkg/_api.py", "from ._config import DEFAULT_TIMEOUT_CONFIG");
		using var engine = CreateEngine();

		var selection = await engine.FindRelatedAsync(
			fixture.Path,
			[package, api],
			["pkg/_api.py"],
			DependencyDirection.Dependencies,
			cancellationToken: TestContext.Current.CancellationToken);
		var outside = await engine.FindOutsideSelectionAsync(
			fixture.Path,
			selection,
			[package, config, api],
			DependencyDirection.Dependencies,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(DependencyFactsEngine.CanRelateOutsideSelection(selection, DependencyDirection.Dependencies));
		var dependency = Assert.Single(Assert.Single(outside.Seeds).Dependencies);
		Assert.Equal("pkg/_config.py", dependency.Path);
		Assert.Contains("imported name not indexed", dependency.Reasons);
		Assert.Equal(1, outside.Edges);
		Assert.Equal(1, outside.ExplainedUnresolvedEdges);
	}

	private static DependencyFactsEngine CreateEngine() =>
		new(new TreeSitterDependencyFactExtractor(), new FileDependencyConfigurationProvider());
}
