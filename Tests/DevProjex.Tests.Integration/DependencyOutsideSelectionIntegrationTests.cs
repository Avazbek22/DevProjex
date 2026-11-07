using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class DependencyOutsideSelectionIntegrationTests
{
	[Fact]
	public async Task NarrowedSelection_ListsTheDependencyOutsideItAndKeepsTheOneInsideIt()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var model = fixture.CreateFile("Models/User.cs", "namespace App.Models; public sealed class User { }");
		var store = fixture.CreateFile("Services/Store.cs",
			"using App.Models; namespace App.Services; public sealed class Store { User Value; Clock Time; }");
		var clock = fixture.CreateFile("Services/Clock.cs", "namespace App.Services; public sealed class Clock { }");
		string[] projectManifest = [project, model, store, clock];
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
			projectManifest,
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
		Assert.DoesNotContain(seed.Dependencies, file => file.Path == "Services/Clock.cs");
		Assert.Equal(["Services/Clock.cs"], Paths(Assert.Single(outside.Selection.Seeds).Dependencies));
		Assert.Same(selection.Index, outside.Selection.Index);
		Assert.Equal(new DependencyResolutionCounts(1, 0, 0, 0, 1), outside.Resolution);
		await AssertSameAnswerAsUnnarrowedAsync(
			engine, fixture.Path, projectManifest, ["Services/Store.cs"], DependencyDirection.Dependencies, outside);
	}

	[Fact]
	public async Task NarrowedSelection_ListsDependentsOutsideIt()
	{
		using var fixture = new TemporaryDirectory();
		var core = fixture.CreateFile("src/demo/core.py", "class Command: pass");
		var package = fixture.CreateFile("src/demo/__init__.py", string.Empty);
		var test = fixture.CreateFile("tests/test_core.py", "from demo.core import Command");
		string[] projectManifest = [core, package, test];
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
			projectManifest,
			DependencyDirection.Dependents,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(DependencyFactsEngine.CanRelateOutsideSelection(selection));
		var seed = Assert.Single(outside.Seeds);
		var dependent = Assert.Single(seed.Dependents);
		Assert.Equal("tests/test_core.py", dependent.Path);
		Assert.Contains("import demo.core at line 1", dependent.Reasons);
		Assert.Empty(Assert.Single(outside.Selection.Seeds).Dependents);
		Assert.Equal(new DependencyResolutionCounts(0, 0, 0, 0, 1), outside.Resolution);
		await AssertSameAnswerAsUnnarrowedAsync(
			engine, fixture.Path, projectManifest, ["src/demo/core.py"], DependencyDirection.Dependents, outside);
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
		string[] projectManifest = [config, first, second, source];
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
			projectManifest,
			DependencyDirection.Dependencies,
			cancellationToken: TestContext.Current.CancellationToken);

		// The existing priority target stops the fallback, so the selection reports the import as
		// unresolved rather than settling on second/; the project names the file TypeScript uses.
		Assert.Contains(selection.Index.Edges, edge => edge.Source == "app/main.ts" &&
			edge.Reference == "@lib/util" && edge.Status == ResolutionStatus.Unresolved);
		var dependency = Assert.Single(Assert.Single(outside.Seeds).Dependencies);
		Assert.Equal("first/util.ts", dependency.Path);
		Assert.Equal(0, outside.Resolution.Unresolved);
		Assert.Equal(1, outside.Resolution.OutsideSelection);
		await AssertSameAnswerAsUnnarrowedAsync(
			engine, fixture.Path, projectManifest, ["app/main.ts"], DependencyDirection.Dependencies, outside);
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
		Assert.Equal(new DependencyResolutionCounts(0, 0, 0, 0, 25), outside.Resolution);
	}

	[Fact]
	public async Task WholeProjectSelection_KeepsTheSelectionAnswerAndHasNothingOutsideIt()
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
		Assert.Equal(
			DependencyFactsEngine.CountResolution(selection.Index, ["Services/Store.cs"], DependencyDirection.Both),
			outside.Resolution);
		Assert.Equal(0, outside.Resolution.OutsideSelection);
		var expected = Assert.Single(selection.Seeds);
		var actual = Assert.Single(outside.Selection.Seeds);
		Assert.Equal(Rows(expected.Dependencies), Rows(actual.Dependencies));
		Assert.Equal(Rows(expected.Dependents), Rows(actual.Dependents));
	}

	[Fact]
	public async Task CanRelateOutsideSelection_NeedsOnlyASeedWithFacts()
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

		// Even a seed whose references all resolved may have been answered wrongly: the selection
		// cannot see a second declaration, a global using, or a package file it left out.
		Assert.True(DependencyFactsEngine.CanRelateOutsideSelection(selection));
		Assert.False(DependencyFactsEngine.CanRelateOutsideSelection(unsupported));
	}

	[Fact]
	public async Task GlobalUsingOutsideTheSelection_ResolvesTheSeedsReferencesInsideIt()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("src/Lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var globalUsings = fixture.CreateFile("src/Lib/GlobalUsings.cs",
			"global using Lib.Core.Enrichers;\nglobal using Lib.Core.Pipeline;\n");
		var logger = fixture.CreateFile("src/Lib/Core/Logger.cs",
			"using Lib.Events;\nnamespace Lib.Core;\npublic sealed class Logger\n{\n" +
			"\tFixedEnricher First;\n\tSilentLogger Second;\n\tLevelSwitch Third;\n\tLogEvent Fourth;\n}\n");
		var enricher = fixture.CreateFile("src/Lib/Core/Enrichers/FixedEnricher.cs",
			"namespace Lib.Core.Enrichers;\npublic sealed class FixedEnricher { LevelSwitch Switch; }\n");
		var silent = fixture.CreateFile("src/Lib/Core/Pipeline/SilentLogger.cs",
			"namespace Lib.Core.Pipeline;\npublic sealed class SilentLogger { }\n");
		var levelSwitch = fixture.CreateFile("src/Lib/Core/LevelSwitch.cs",
			"namespace Lib.Core;\npublic sealed class LevelSwitch { }\n");
		var logEvent = fixture.CreateFile("src/Lib/Events/LogEvent.cs",
			"namespace Lib.Events;\npublic sealed class LogEvent { }\n");
		var configuration = fixture.CreateFile("src/Lib/Configuration.cs",
			"using Lib.Core;\nnamespace Lib;\npublic sealed class Configuration { Logger Created; }\n");
		string[] selectionManifest = [logger, enricher, silent, levelSwitch];
		string[] projectManifest = [project, globalUsings, logger, enricher, silent, levelSwitch, logEvent, configuration];
		string[] seeds = ["src/Lib/Core/Logger.cs"];
		using var engine = CreateEngine();

		var selection = await engine.FindRelatedAsync(
			fixture.Path, selectionManifest, seeds, DependencyDirection.Both,
			cancellationToken: TestContext.Current.CancellationToken);
		var narrowed = await engine.FindOutsideSelectionAsync(
			fixture.Path, selection, projectManifest, DependencyDirection.Both,
			cancellationToken: TestContext.Current.CancellationToken);

		// The selection alone cannot see the global using, so the enricher and the silent logger
		// look unresolved there, although both files are in the selection.
		Assert.DoesNotContain("src/Lib/Core/Enrichers/FixedEnricher.cs", Paths(Assert.Single(selection.Seeds).Dependencies));
		var inside = Assert.Single(narrowed.Selection.Seeds);
		Assert.Equal(
			[
				"src/Lib/Core/Enrichers/FixedEnricher.cs",
				"src/Lib/Core/LevelSwitch.cs",
				"src/Lib/Core/Pipeline/SilentLogger.cs"
			],
			Paths(inside.Dependencies));
		Assert.All(inside.Dependencies, static file => Assert.Equal(ResolutionStatus.Resolved, file.Status));
		Assert.Empty(inside.Dependents);
		var outside = Assert.Single(narrowed.Seeds);
		Assert.Equal(["src/Lib/Events/LogEvent.cs"], Paths(outside.Dependencies));
		Assert.Equal(["src/Lib/Configuration.cs"], Paths(outside.Dependents));
		Assert.Equal(new DependencyResolutionCounts(3, 0, 0, 0, 2), narrowed.Resolution);
		await AssertSameAnswerAsUnnarrowedAsync(
			engine, fixture.Path, projectManifest, seeds, DependencyDirection.Both, narrowed);
	}

	[Fact]
	public async Task PythonPackageOutsideTheSelection_IsItsRealInitFileNotANamespace()
	{
		using var fixture = new TemporaryDirectory();
		var pyproject = fixture.CreateFile("pyproject.toml", "[project]\nname = \"pkg\"\n");
		var package = fixture.CreateFile("pkg/__init__.py", "from ._client import Client\n");
		var client = fixture.CreateFile("pkg/_client.py",
			"from ._transports.default import Transport\nclass Client: pass\n");
		var config = fixture.CreateFile("pkg/_config.py", "class Limits: pass\n");
		var transports = fixture.CreateFile("pkg/_transports/__init__.py", "from .default import *\n");
		var transport = fixture.CreateFile("pkg/_transports/default.py",
			"import pkg\nfrom .._config import Limits\nfrom .base import Base\nclass Transport(Base): pass\n");
		var transportBase = fixture.CreateFile("pkg/_transports/base.py", "class Base: pass\n");
		string[] selectionManifest = [transports, transport, transportBase];
		string[] projectManifest = [pyproject, package, client, config, transports, transport, transportBase];
		string[] seeds = ["pkg/_transports/default.py"];
		using var engine = CreateEngine();

		var selection = await engine.FindRelatedAsync(
			fixture.Path, selectionManifest, seeds, DependencyDirection.Both,
			cancellationToken: TestContext.Current.CancellationToken);
		var narrowed = await engine.FindOutsideSelectionAsync(
			fixture.Path, selection, projectManifest, DependencyDirection.Both,
			cancellationToken: TestContext.Current.CancellationToken);

		// Without the package file the selection settles `import pkg` on a namespace package.
		Assert.Contains(Assert.Single(selection.Seeds).Dependencies, static file => file.Path == "namespace:pkg");
		var inside = Assert.Single(narrowed.Selection.Seeds);
		Assert.Equal(["pkg/_transports/base.py"], Paths(inside.Dependencies));
		Assert.Equal(["pkg/_transports/__init__.py"], Paths(inside.Dependents));
		var outside = Assert.Single(narrowed.Seeds);
		Assert.Equal(["pkg/__init__.py", "pkg/_config.py"], Paths(outside.Dependencies));
		Assert.Equal(["pkg/_client.py"], Paths(outside.Dependents));
		Assert.Equal(2, narrowed.Resolution.Resolved);
		Assert.Equal(3, narrowed.Resolution.OutsideSelection);
		await AssertSameAnswerAsUnnarrowedAsync(
			engine, fixture.Path, projectManifest, seeds, DependencyDirection.Both, narrowed);
	}

	[Fact]
	public async Task TypeScriptRelations_SplitBetweenTheSelectionAndTheFilesItLeftOut()
	{
		using var fixture = new TemporaryDirectory();
		var tsconfig = fixture.CreateFile("tsconfig.json", "{\"compilerOptions\":{\"module\":\"esnext\",\"moduleResolution\":\"bundler\"}}");
		var router = fixture.CreateFile("src/router/reg-exp/router.ts",
			"import { Node } from './node';\nimport { splitPath } from '../../utils/url';\nimport type { Router } from '../../router';\n" +
			"export class RegExpRouter { node = new Node(); }\n");
		var node = fixture.CreateFile("src/router/reg-exp/node.ts", "export class Node {}\n");
		var index = fixture.CreateFile("src/router/reg-exp/index.ts", "export { RegExpRouter } from './router';\n");
		var url = fixture.CreateFile("src/utils/url.ts", "export const splitPath = (path: string) => path.split('/');\n");
		var routerTypes = fixture.CreateFile("src/router.ts", "export interface Router {}\n");
		var preset = fixture.CreateFile("src/preset/quick.ts", "import { RegExpRouter } from '../router/reg-exp/router';\n");
		string[] selectionManifest = [router, node, index];
		string[] projectManifest = [tsconfig, router, node, index, url, routerTypes, preset];
		string[] seeds = ["src/router/reg-exp/router.ts"];
		using var engine = CreateEngine();

		var selection = await engine.FindRelatedAsync(
			fixture.Path, selectionManifest, seeds, DependencyDirection.Both,
			cancellationToken: TestContext.Current.CancellationToken);
		var narrowed = await engine.FindOutsideSelectionAsync(
			fixture.Path, selection, projectManifest, DependencyDirection.Both,
			cancellationToken: TestContext.Current.CancellationToken);

		var inside = Assert.Single(narrowed.Selection.Seeds);
		Assert.Equal(["src/router/reg-exp/node.ts"], Paths(inside.Dependencies));
		Assert.Equal(["src/router/reg-exp/index.ts"], Paths(inside.Dependents));
		var outside = Assert.Single(narrowed.Seeds);
		Assert.Equal(["src/router.ts", "src/utils/url.ts"], Paths(outside.Dependencies));
		Assert.Equal(["src/preset/quick.ts"], Paths(outside.Dependents));
		Assert.Equal(2, narrowed.Resolution.Resolved);
		Assert.Equal(3, narrowed.Resolution.OutsideSelection);
		await AssertSameAnswerAsUnnarrowedAsync(
			engine, fixture.Path, projectManifest, seeds, DependencyDirection.Both, narrowed);
	}

	[Fact]
	public async Task AmbiguousReference_IsListedOnlyWhereItsChosenCandidateIsShown()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var alpha = fixture.CreateFile("Models/Alpha/User.cs", "namespace App.Alpha; public sealed class User { }");
		var beta = fixture.CreateFile("Models/Beta/User.cs", "namespace App.Beta; public sealed class User { }");
		var seedPath = fixture.CreateFile("Use/Seed.cs",
			"using App.Alpha; using App.Beta; namespace App.Use; public sealed class Seed { User Value; }");
		string[] projectManifest = [project, alpha, beta, seedPath];
		string[] seeds = ["Use/Seed.cs"];
		using var engine = CreateEngine();

		var withChosen = await engine.FindRelatedAsync(
			fixture.Path, [alpha, seedPath], seeds, DependencyDirection.Dependencies,
			cancellationToken: TestContext.Current.CancellationToken);
		var narrowedWithChosen = await engine.FindOutsideSelectionAsync(
			fixture.Path, withChosen, projectManifest, DependencyDirection.Dependencies,
			cancellationToken: TestContext.Current.CancellationToken);
		var withOther = await engine.FindRelatedAsync(
			fixture.Path, [beta, seedPath], seeds, DependencyDirection.Dependencies,
			cancellationToken: TestContext.Current.CancellationToken);
		var narrowedWithOther = await engine.FindOutsideSelectionAsync(
			fixture.Path, withOther, projectManifest, DependencyDirection.Dependencies,
			cancellationToken: TestContext.Current.CancellationToken);

		// Each selection alone sees one declaration and calls the reference resolved.
		Assert.Equal(ResolutionStatus.Resolved, Assert.Single(Assert.Single(withChosen.Seeds).Dependencies).Status);
		Assert.Equal(ResolutionStatus.Resolved, Assert.Single(Assert.Single(withOther.Seeds).Dependencies).Status);
		var chosen = Assert.Single(Assert.Single(narrowedWithChosen.Selection.Seeds).Dependencies);
		Assert.Equal("Models/Alpha/User.cs", chosen.Path);
		Assert.Equal(ResolutionStatus.Ambiguous, chosen.Status);
		Assert.Equal(["Models/Alpha/User.cs", "Models/Beta/User.cs"], chosen.Candidates);
		Assert.Empty(Assert.Single(narrowedWithOther.Selection.Seeds).Dependencies);
		Assert.Empty(narrowedWithChosen.Seeds);
		Assert.Empty(narrowedWithOther.Seeds);
		Assert.Equal(new DependencyResolutionCounts(0, 1, 0, 0, 0), narrowedWithChosen.Resolution);
		Assert.Equal(new DependencyResolutionCounts(0, 1, 0, 0, 0), narrowedWithOther.Resolution);
		await AssertSameAnswerAsUnnarrowedAsync(
			engine, fixture.Path, projectManifest, seeds, DependencyDirection.Dependencies, narrowedWithChosen);
		await AssertSameAnswerAsUnnarrowedAsync(
			engine, fixture.Path, projectManifest, seeds, DependencyDirection.Dependencies, narrowedWithOther);
	}

	[Fact]
	public async Task DeeperTraversal_FollowsProjectRelationsOnlyIntoTheSelection()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("src/Lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var globalUsings = fixture.CreateFile("src/Lib/GlobalUsings.cs", "global using Lib.Core.Enrichers;\n");
		var logger = fixture.CreateFile("src/Lib/Core/Logger.cs",
			"using Lib.Events;\nnamespace Lib.Core;\npublic sealed class Logger { FixedEnricher First; LogEvent Second; }\n");
		var enricher = fixture.CreateFile("src/Lib/Core/Enrichers/FixedEnricher.cs",
			"using Lib.Core;\nnamespace Lib.Core.Enrichers;\npublic sealed class FixedEnricher { LevelSwitch Switch; }\n");
		var levelSwitch = fixture.CreateFile("src/Lib/Core/LevelSwitch.cs",
			"namespace Lib.Core;\npublic sealed class LevelSwitch { }\n");
		var logEvent = fixture.CreateFile("src/Lib/Events/LogEvent.cs",
			"namespace Lib.Events;\npublic sealed class LogEvent { Property Value; }\n");
		var property = fixture.CreateFile("src/Lib/Events/Property.cs",
			"namespace Lib.Events;\npublic sealed class Property { }\n");
		string[] selectionManifest = [logger, enricher, levelSwitch];
		string[] projectManifest = [project, globalUsings, logger, enricher, levelSwitch, logEvent, property];
		string[] seeds = ["src/Lib/Core/Logger.cs"];
		using var engine = CreateEngine();

		var selection = await engine.FindRelatedAsync(
			fixture.Path, selectionManifest, seeds, DependencyDirection.Dependencies, depth: 3,
			cancellationToken: TestContext.Current.CancellationToken);
		var narrowed = await engine.FindOutsideSelectionAsync(
			fixture.Path, selection, projectManifest, seeds, DependencyDirection.Dependencies, depth: 3,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(["src/Lib/Core/Logger.cs"], selection.Seeds.Select(static seed => seed.Seed));
		Assert.Equal(
			[
				"src/Lib/Core/Logger.cs",
				"src/Lib/Core/Enrichers/FixedEnricher.cs",
				"src/Lib/Core/LevelSwitch.cs"
			],
			narrowed.Selection.Seeds.Select(static seed => seed.Seed));
		var outside = Assert.Single(narrowed.Seeds);
		Assert.Equal("src/Lib/Core/Logger.cs", outside.Seed);
		Assert.Equal(["src/Lib/Events/LogEvent.cs"], Paths(outside.Dependencies));
		Assert.DoesNotContain(narrowed.Selection.Seeds, static seed => seed.Seed == "src/Lib/Events/Property.cs");
		Assert.Equal(new DependencyResolutionCounts(2, 0, 0, 0, 1), narrowed.Resolution);
	}

	[Fact]
	public async Task SeedTheProjectHasNoFactsFor_KeepsTheSelectionAnswer()
	{
		using var fixture = new TemporaryDirectory();
		const string seedSource = "namespace App.Zzzz; public sealed class Seed { Dep Value; }";
		var filler = fixture.CreateFile("A/Seed.cs", seedSource.Replace("Zzzz", "Aaaa", StringComparison.Ordinal));
		var dependency = fixture.CreateFile("Z/Dep.cs", "namespace App.Zzzz; public sealed class Dep { }");
		var seedPath = fixture.CreateFile("Z/Seed.cs", seedSource);
		fixture.CreateFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		string[] selectionManifest = [dependency, seedPath];
		string[] seeds = ["Z/Seed.cs"];
		long selectionFactBytes;
		using (var measuring = CreateEngine())
		{
			selectionFactBytes = (await measuring.IndexAsync(
				fixture.Path, selectionManifest, cancellationToken: TestContext.Current.CancellationToken))
				.Metrics.AccumulatedFactBytes;
		}
		// The selection fits the fact budget exactly; the project spends it on A/Seed.cs first.
		using var engine = new DependencyFactsEngine(
			new TreeSitterDependencyFactExtractor(),
			new FileDependencyConfigurationProvider(),
			new DependencyFactsLimits(MaximumAccumulatedFactBytes: selectionFactBytes));

		var selection = await engine.FindRelatedAsync(
			fixture.Path, selectionManifest, seeds, DependencyDirection.Dependencies,
			cancellationToken: TestContext.Current.CancellationToken);
		var narrowed = await engine.FindOutsideSelectionAsync(
			fixture.Path, selection, [filler, dependency, seedPath], DependencyDirection.Dependencies,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(narrowed.Index.FileByPath["Z/Seed.cs"].FactBudgetLimited);
		Assert.Null(Assert.Single(selection.Seeds).NoFactsReason);
		var inside = Assert.Single(narrowed.Selection.Seeds);
		Assert.Null(inside.NoFactsReason);
		Assert.Equal(Rows(Assert.Single(selection.Seeds).Dependencies), Rows(inside.Dependencies));
		Assert.Equal(["Z/Dep.cs"], Paths(inside.Dependencies));
		Assert.Empty(narrowed.Seeds);
		Assert.Equal(
			DependencyFactsEngine.CountResolution(selection.Index, seeds, DependencyDirection.Dependencies),
			narrowed.Resolution);
	}

	// Narrowing decides only where a related file is listed: together the selection's sections
	// and the outside sections hold what the unnarrowed call resolves, with the same counts.
	private static async Task AssertSameAnswerAsUnnarrowedAsync(
		DependencyFactsEngine engine,
		string root,
		IReadOnlyList<string> projectManifest,
		IReadOnlyList<string> seeds,
		DependencyDirection direction,
		DependencyOutsideSelectionResult narrowed)
	{
		var whole = await engine.FindRelatedAsync(
			root, projectManifest, seeds, direction, cancellationToken: TestContext.Current.CancellationToken);
		var expected = DependencyFactsEngine.CountResolution(whole.Index, seeds, direction);
		Assert.Equal(expected.Resolved, narrowed.Resolution.Resolved + narrowed.Resolution.OutsideSelection);
		Assert.Equal(expected.Ambiguous, narrowed.Resolution.Ambiguous);
		Assert.Equal(expected.Unresolved, narrowed.Resolution.Unresolved);
		Assert.Equal(expected.External, narrowed.Resolution.External);
		var visible = narrowed.Selection.Index.FileByPath;
		foreach (var wholeSeed in whole.Seeds)
		{
			var inside = Assert.Single(narrowed.Selection.Seeds, seed => seed.Seed == wholeSeed.Seed);
			var outside = narrowed.Seeds.SingleOrDefault(seed => seed.Seed == wholeSeed.Seed);
			Assert.Equal(
				ResolvedPaths(wholeSeed.Dependencies),
				ResolvedPaths(inside.Dependencies).Concat(Paths(outside?.Dependencies ?? [])).Order(StringComparer.Ordinal));
			Assert.Equal(
				ResolvedPaths(wholeSeed.Dependents),
				ResolvedPaths(inside.Dependents).Concat(Paths(outside?.Dependents ?? [])).Order(StringComparer.Ordinal));
			Assert.Equal(
				Rows(wholeSeed.Dependencies.Where(file => file.Status == ResolutionStatus.Ambiguous && visible.ContainsKey(file.Path))),
				Rows(inside.Dependencies.Where(static file => file.Status == ResolutionStatus.Ambiguous)));
		}
	}

	private static string[] Paths(IEnumerable<RelatedFile> files) =>
		files.Select(static file => file.Path).ToArray();

	private static IEnumerable<string> ResolvedPaths(IEnumerable<RelatedFile> files) =>
		files.Where(static file => file.Status == ResolutionStatus.Resolved)
			.Select(static file => file.Path)
			.Order(StringComparer.Ordinal);

	private static string[] Rows(IEnumerable<RelatedFile> files) =>
		files.Select(static file =>
				$"{file.Path}|{file.Status}|{string.Join(',', file.Reasons)}|{string.Join(',', file.Candidates)}|{file.CrossScope}|{file.EstimatedTokens}")
			.ToArray();

	private static DependencyFactsEngine CreateEngine() =>
		new(new TreeSitterDependencyFactExtractor(), new FileDependencyConfigurationProvider());
}
