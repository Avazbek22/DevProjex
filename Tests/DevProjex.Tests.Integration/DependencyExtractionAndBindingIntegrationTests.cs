using DevProjex.Application.Dependencies;
using DevProjex.Application.Ranking;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class DependencyExtractionAndBindingIntegrationTests(ITestOutputHelper output)
{
	[Fact]
	public async Task SyntaxDamageDropsOnlyFactsOwnedByTheDamagedConstruction()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var target = fixture.CreateFile("Target.cs", "public sealed class Target { }");
		var source = fixture.CreateFile("Consumers.cs", """
			public sealed class Before { Target value; }
			public sealed class AlsoBefore { Target value; }
			public static class Broken
			{
				public static void Run(
				{
					Missing value;
				}
			}
			""");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[project, target, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var facts = Assert.Single(result.Files, file => file.Path == "Consumers.cs");
		Assert.Equal(DependencyFileStatus.Supported, facts.Status);
		Assert.True(facts.HasSyntaxErrors);
		Assert.Contains(facts.Declarations, declaration => declaration.Identity.QualifiedName == "Before");
		Assert.Contains(facts.Declarations, declaration => declaration.Identity.QualifiedName == "AlsoBefore");
		Assert.DoesNotContain(facts.References, reference => reference.Name == "Missing");
		Assert.Equal(2, result.Edges.Single(edge => edge.Source == "Consumers.cs" && edge.Target == "Target.cs").Evidence.Count);
		var partial = Assert.IsType<DependencyPartialParseDiagnostic>(facts.PartialParse);
		Assert.Equal("Consumers.cs", partial.Path);
		Assert.Equal(1, partial.DroppedConstructs);
		Assert.Contains(partial.Ranges, range => range.StartLine <= 5 && range.EndLine >= 5);
		Assert.Contains(result.Coverage.PartialParseDiagnostics, diagnostic => diagnostic == partial);
	}

	[Fact]
	public async Task ValidSyntaxKeepsFactsAndReportsByteIdenticalResultsWithoutPartialDiagnostics()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var target = fixture.CreateFile("Target.cs", "public sealed class Target { }");
		var source = fixture.CreateFile("Consumer.cs", "public sealed class Consumer { Target value; }");
		using var engine = CreateEngine();

		var first = await engine.IndexAsync(fixture.Path, [project, target, source],
			cancellationToken: TestContext.Current.CancellationToken);
		var second = await engine.IndexAsync(fixture.Path, [project, target, source],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(first.Files, second.Files);
		Assert.Equal(first.Edges, second.Edges);
		Assert.All(first.Files, file => Assert.Null(file.PartialParse));
		Assert.Empty(first.Coverage.PartialParseDiagnostics);
	}

	[Fact]
	public async Task CSharpReferences_OnOneLineRemainDistinctOccurrences()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var model = fixture.CreateFile("Models/User.cs", "namespace Models; public sealed class User { }");
		var source = fixture.CreateFile("Consumers.cs", "using Models; class Box<User> { User a; } class Consumer { User b; }");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(fixture.Path, [project, model, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, item => item.Source == "Consumers.cs" && item.Target == "Models/User.cs");
		Assert.Single(edge.Evidence);
		Assert.Contains(result.Files.Single(file => file.Path == "Consumers.cs").References,
			reference => reference.Name == "User" && reference.ContainingType == "Consumer");
		Assert.Contains(result.Files.Single(file => file.Path == "Consumers.cs").References,
			reference => reference.Name == "User" && reference.ContainingType == "Box`1" && reference.Status == ResolutionStatus.Unresolved);
	}

	[Fact]
	public async Task TypeScriptImportFacts_DoNotDependOnOrdinaryCallOrder()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var target = fixture.CreateFile("dep.ts", "export const value = 1;");
		var calls = string.Join('\n', Enumerable.Repeat("noop();", 8));
		var before = fixture.CreateFile("before.ts", $"import \"./dep.js\";\n{calls}");
		var after = fixture.CreateFile("after.ts", $"{calls}\nimport \"./dep.js\";");
		using var engine = CreateEngine(new DependencyFactsLimits(MaximumFactsPerFile: 2));

		var result = await engine.IndexAsync(fixture.Path, [config, target, before, after],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.All(new[] { "before.ts", "after.ts" }, path =>
		{
			var edge = Assert.Single(result.Edges, item => item.Source == path);
			Assert.Equal(ResolutionStatus.Resolved, edge.Status);
			Assert.Equal("dep.ts", edge.Target);
		});
	}

	[Fact]
	public async Task TypeScriptFactLimits_AreExplicitAndCancellationIsObserved()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var first = fixture.CreateFile("first.ts", "export const first = 1;");
		var second = fixture.CreateFile("second.ts", "export const second = 2;");
		var third = fixture.CreateFile("third.ts", "export const third = 3;");
		var usefulOverflow = fixture.CreateFile("useful.ts", "import './first.js'; import './second.js'; import './third.js';");
		var rawOverflow = fixture.CreateFile("raw.ts", "noop(); noop(); noop(); noop(); import './first.js';");

		using (var usefulEngine = CreateEngine(new DependencyFactsLimits(MaximumFactsPerFile: 2)))
		{
			var index = await usefulEngine.IndexAsync(fixture.Path, [config, first, second, third, usefulOverflow],
				cancellationToken: TestContext.Current.CancellationToken);
			var file = Assert.Single(index.Files, item => item.Path == "useful.ts");
			Assert.Equal(DependencyFileStatus.ExtractionFailed, file.Status);
			Assert.Equal("fact limit exceeded", file.StatusReason);
			Assert.Equal(1, index.Coverage.ExtractionFailed);
			var related = await usefulEngine.FindRelatedAsync(fixture.Path, [config, first, second, third, usefulOverflow],
				["useful.ts"], DependencyDirection.Both, cancellationToken: TestContext.Current.CancellationToken);
			Assert.Equal("fact limit exceeded", Assert.Single(related.Seeds).NoFactsReason);
		}
		using (var focusEngine = CreateEngine(new DependencyFactsLimits(MaximumFactsPerFile: 2)))
		{
			var ranking = new ImportanceRankingService(focusEngine, new UnavailableHistoryReader());
			var report = await ranking.RankAsync(
				fixture.Path,
				[config, first, second, third, usefulOverflow],
				new FocusRankingRequest([new FocusRankingSeedRequest("useful.ts", usefulOverflow)]),
				cancellationToken: TestContext.Current.CancellationToken);
			var seed = Assert.Single(report.Focus!.Seeds);
			Assert.Equal(FocusSeedState.ExtractionFailed, seed.State);
			Assert.Equal("fact limit exceeded", seed.Reason);
		}

		using (var rawEngine = CreateEngine(new DependencyFactsLimits(MaximumRawCapturesPerFile: 4)))
		{
			var index = await rawEngine.IndexAsync(fixture.Path, [config, first, rawOverflow],
				cancellationToken: TestContext.Current.CancellationToken);
			var file = Assert.Single(index.Files, item => item.Path == "raw.ts");
			Assert.Equal(DependencyFileStatus.ExtractionFailed, file.Status);
			Assert.Equal("fact limit exceeded", file.StatusReason);
		}

		using var cancelledEngine = CreateEngine();
		using var cancellation = new CancellationTokenSource();
		await cancellation.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledEngine.IndexAsync(
			fixture.Path, [config, first], cancellationToken: cancellation.Token));
	}

	[Fact]
	public async Task TypeScriptNestedOrdinaryCalls_DoNotMaterializeNestedSourceText()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var target = fixture.CreateFile("dep.ts", "export const value = 1;");
		var nested = "value";
		for (var index = 0; index < 256; index++) nested = $"noop({nested})";
		var source = fixture.CreateFile("main.ts", nested + ";\nimport './dep.js';");
		using var extractor = new TreeSitterDependencyFactExtractor();
		using var engine = new DependencyFactsEngine(extractor, new FileDependencyConfigurationProvider());

		var result = await engine.IndexAsync(fixture.Path, [config, target, source],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Edges, edge => edge.Source == "main.ts" && edge.Target == "dep.ts");
		Assert.True(extractor.WorkState.RawCapturesVisited >= 257);
		Assert.True(extractor.WorkState.CreatedCaptures < 20);
		var previousMaterialization = Enumerable.Range(1, 256).Sum(depth => 5L + 6L * depth);
		output.WriteLine($"nested-call materialization: before={previousMaterialization}, " +
		                 $"after={extractor.WorkState.MaterializedCharacters}, " +
		                 $"raw-captures={extractor.WorkState.RawCapturesVisited}, " +
		                 $"created-captures={extractor.WorkState.CreatedCaptures}");
		Assert.True(previousMaterialization > extractor.WorkState.MaterializedCharacters * 100);
		Assert.True(extractor.WorkState.MaterializedCharacters < 2_000,
			$"Discarded calls materialized {extractor.WorkState.MaterializedCharacters} characters.");
		var work = extractor.WorkState;
		var warm = await engine.IndexAsync(fixture.Path, [config, target, source],
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.True(warm.Metrics.ResolutionCacheHit);
		Assert.Equal(work, extractor.WorkState);
	}

	[Fact]
	public async Task TypeScriptModuleSpecifiers_RequireLiteralSyntaxAndDecodeEscapes()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var target = fixture.CreateFile("target.ts", "export const value = 1;");
		var source = fixture.CreateFile("main.ts", """
			require("./" + "target.js");
			require("./\u0074arget.js");
			require(/* retained */ "./target.js");
			import "./target.js";
			import("./target.js");
			const variable = "./target.js";
			require(variable);
			import(`./${variable}`);
			require(`./target.js`);
			require("./target.js);
			""");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(fixture.Path, [config, target, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var facts = result.Files.Single(file => file.Path == "main.ts");
		Assert.Equal(4, facts.Imports.Count(import => import.Specifier == "./target.js"));
		Assert.Equal(4, facts.Imports.Count(import => import.Reason == "module specifier is not a string literal"));
		Assert.True(facts.HasSyntaxErrors);
		Assert.DoesNotContain(facts.Imports, import => import.Specifier.Contains(" + ", StringComparison.Ordinal));
		var resolved = Assert.Single(result.Edges, edge => edge.Source == "main.ts" && edge.Target == "target.ts");
		Assert.Equal(4, resolved.Evidence.Count);
	}

	[Fact]
	public async Task CSharpAliases_RespectAbsoluteNamesAndLexicalNamespaceScopes()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var real = fixture.CreateFile("Models/User.cs", "namespace Models; public sealed class User { }");
		var decoy = fixture.CreateFile("Models/Decoy.cs", "namespace Models; public sealed class Decoy { }");
		var one = fixture.CreateFile("Models/One.cs", "namespace Models; public sealed class One { }");
		var two = fixture.CreateFile("Models/Two.cs", "namespace Models; public sealed class Two { }");
		var source = fixture.CreateFile("Consumers.cs", """
			using User = Models.Decoy;
			class RootConsumer
			{
				Models.User Qualified;
				global::Models.User Absolute;
				User Aliased;
			}
			namespace Repeated
			{
				using X = Models.One;
				class FirstConsumer { X Value; }
			}
			namespace Repeated
			{
				using X = Models.Two;
				class SecondConsumer { X Value; }
			}
			""");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(fixture.Path, [project, real, decoy, one, two, source],
			cancellationToken: TestContext.Current.CancellationToken);
		var edges = result.Edges.Where(edge => edge.Source == "Consumers.cs").ToArray();

		var userEdge = Assert.Single(edges, edge => edge.Target == "Models/User.cs");
		Assert.Equal(2, userEdge.Evidence.Count);
		Assert.Single(edges, edge => edge.Target == "Models/Decoy.cs");
		Assert.Single(edges, edge => edge.Target == "Models/One.cs");
		Assert.Single(edges, edge => edge.Target == "Models/Two.cs");
	}

	[Fact]
	public async Task CSharpGlobalQualification_BypassesGenericShadowingAndAliases()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var global = fixture.CreateFile("User.cs", "public sealed class User { }");
		var model = fixture.CreateFile("Models/User.cs", "namespace Models; public sealed class User { }");
		var decoy = fixture.CreateFile("Models/Decoy.cs", "namespace Models; public sealed class Decoy { }");
		var source = fixture.CreateFile("Box.cs", """
			using User = Models.Decoy;
			class Box<User>
			{
				User Shadowed;
				global::User Absolute;
				global::Models.User QualifiedAbsolute;
			}
			""");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(fixture.Path, [project, global, model, decoy, source],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Edges, edge => edge.Source == "Box.cs" && edge.Target == "User.cs");
		Assert.Contains(result.Edges, edge => edge.Source == "Box.cs" && edge.Target == "Models/User.cs");
		Assert.DoesNotContain(result.Edges, edge => edge.Source == "Box.cs" && edge.Target == "Models/Decoy.cs");
		Assert.Contains(result.Files.Single(file => file.Path == "Box.cs").References,
			reference => reference.Name == "User" && !reference.IsGlobalQualified &&
			             reference.Status == ResolutionStatus.Unresolved);
	}

	[Fact]
	public async Task PythonLocalImports_CreateFileEdgesButNotModuleExports()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var implementation = fixture.CreateFile("impl.py", "class Item: pass");
		var model = fixture.CreateFile("model.py", """
			def loader():
			    from impl import Item as LocalItem
			class Container:
			    from impl import Item as ClassItem
			""");
		var relativeModel = fixture.CreateFile("pkg/model.py", """
			def loader():
			    from .impl import Item as RelativeItem
			""");
		var relativeImplementation = fixture.CreateFile("pkg/impl.py", "class Item: pass");
		var relativeConsumer = fixture.CreateFile("relative_consumer.py", "from pkg.model import RelativeItem");
		var consumer = fixture.CreateFile("consumer.py", """
			from model import LocalItem
			from model import ClassItem
			""");
		var initializer = fixture.CreateFile("pkg/__init__.py", "from impl import Item as PublicItem");
		var packageConsumer = fixture.CreateFile("package_consumer.py", "from pkg import PublicItem");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, implementation, model, consumer, initializer, packageConsumer,
				relativeModel, relativeImplementation, relativeConsumer],
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains(result.Edges, edge => edge.Source == "model.py" && edge.Target == "impl.py" &&
			edge.Status == ResolutionStatus.Resolved);
		var unresolved = Assert.Single(result.Edges, edge => edge.Source == "consumer.py" &&
			edge.Status == ResolutionStatus.Unresolved && edge.Reasons.Contains("name not found in module"));
		Assert.Equal(2, unresolved.Evidence.Count);
		Assert.Contains(result.Edges, edge => edge.Source == "package_consumer.py" &&
			edge.Target == "impl.py" && edge.Status == ResolutionStatus.Resolved);
		Assert.Contains(result.Edges, edge => edge.Source == "pkg/model.py" &&
			edge.Target == "pkg/impl.py" && edge.Status == ResolutionStatus.Resolved);
		Assert.Contains(result.Edges, edge => edge.Source == "relative_consumer.py" &&
			edge.Status == ResolutionStatus.Unresolved && edge.Reasons.Contains("name not found in module"));
	}

	[Fact]
	public async Task RelatedEvidence_IncludesTheProjectReferenceName()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var target = fixture.CreateFile("register.ts", "export const value = 1;");
		var source = fixture.CreateFile("main.ts", "import \"./register.js\";");
		using var engine = CreateEngine();
		var related = await engine.FindRelatedAsync(
			fixture.Path,
			[config, target, source],
			["main.ts"],
			DependencyDirection.Dependencies,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(Assert.Single(Assert.Single(related.Seeds).Dependencies).Reasons,
			reason => reason == "import ./register.js at line 1");
	}

	[Fact]
	public async Task MissingUnsupportedManifestFile_IsAPerFileTransientFailure()
	{
		using var fixture = new TemporaryDirectory();
		var readme = fixture.CreateFile("README.md", "# transient\n");
		var license = fixture.CreateFile("LICENSE", "fixture\n");
		var manifest = new[] { readme, license };
		File.Delete(readme);
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(fixture.Path, manifest,
			cancellationToken: TestContext.Current.CancellationToken);

		var file = Assert.Single(result.Files, item => item.Path == "README.md");
		Assert.Equal("README.md", file.Path);
		Assert.Equal(DependencyFileStatus.ExtractionFailed, file.Status);
		Assert.Equal("source file could not be read", file.StatusReason);
		Assert.False(file.CanCache);
		Assert.Equal(1, result.Coverage.ExtractionFailed);
		var existing = Assert.Single(result.Files, item => item.Path == "LICENSE");
		Assert.Equal(DependencyFileStatus.Unsupported, existing.Status);
		Assert.True(existing.CanCache);
	}

	[Fact]
	public async Task CSharpProjectReferences_AreTransitivelyVisibleBySdkDefault()
	{
		using var fixture = new TemporaryDirectory();
		var projectA = fixture.CreateFile("A/A.csproj", """
			<Project Sdk="Microsoft.NET.Sdk">
			  <ItemGroup><ProjectReference Include="../B/B.csproj" /></ItemGroup>
			</Project>
			""");
		var projectB = fixture.CreateFile("B/B.csproj", """
			<Project Sdk="Microsoft.NET.Sdk">
			  <ItemGroup><ProjectReference Include="../C/C.csproj" /></ItemGroup>
			</Project>
			""");
		var projectC = fixture.CreateFile("C/C.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var consumer = fixture.CreateFile("A/Consumer.cs", "using CScope; public sealed class Consumer { public Target Value { get; } }");
		var middle = fixture.CreateFile("B/Middle.cs", "namespace BScope; public sealed class Middle { }");
		var target = fixture.CreateFile("C/Target.cs", "namespace CScope; public sealed class Target { }");
		var unrelatedProject = fixture.CreateFile("D/D.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var unrelated = fixture.CreateFile("D/Other.cs", "namespace DScope; public sealed class Other { }");
		var unresolved = fixture.CreateFile("A/OtherConsumer.cs", "using DScope; public sealed class OtherConsumer { public Other Value { get; } }");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[projectA, projectB, projectC, consumer, middle, target, unrelatedProject, unrelated, unresolved],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, item => item.Source == "A/Consumer.cs" && item.Reference == "Target");
		Assert.Equal(ResolutionStatus.Resolved, edge.Status);
		Assert.Equal("C/Target.cs", edge.Target);
		Assert.True(edge.CrossScope);
		Assert.Contains(result.Edges, item => item.Source == "A/OtherConsumer.cs" && item.Reference == "Other" &&
			item.Status == ResolutionStatus.Unresolved);
	}

	[Fact]
	public async Task TypeScriptPackageConditions_DistinguishDynamicImportFromRequireInCommonJs()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"node16\"}}");
		var package = fixture.CreateFile("package.json", """
			{"imports":{"#dual":{"import":"./import.mts","require":"./require.cts"}}}
			""");
		var importTarget = fixture.CreateFile("import.mts", "export const value = 1;");
		var requireTarget = fixture.CreateFile("require.cts", "export const value = 2;");
		var source = fixture.CreateFile("main.cts", "import('#dual'); require('#dual');");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, package, importTarget, requireTarget, source],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Edges, edge => edge.Source == "main.cts" && edge.Target == "import.mts" &&
			edge.Reference == "#dual" && edge.Status == ResolutionStatus.Resolved);
		Assert.Contains(result.Edges, edge => edge.Source == "main.cts" && edge.Target == "require.cts" &&
			edge.Reference == "#dual" && edge.Status == ResolutionStatus.Resolved);
		var imports = result.Files.Single(file => file.Path == "main.cts").Imports;
		Assert.Contains(imports, import => import.ImportKind == ModuleImportKind.DynamicImport);
		Assert.Contains(imports, import => import.ImportKind == ModuleImportKind.Require);
	}

	private static DependencyFactsEngine CreateEngine(DependencyFactsLimits? limits = null) => new(
		new TreeSitterDependencyFactExtractor(),
		new FileDependencyConfigurationProvider(),
		limits);

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
