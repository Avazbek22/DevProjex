using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Compression;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class DependencyFactsEngineIntegrationTests
{
	[Fact]
	public async Task CSharpFacts_MergePartialsHonorUsingAndKeepFileLocalTypesScoped()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var first = fixture.CreateFile("First.cs", """
			namespace Alpha;
			public partial class User { }
			file class Helper { }
			public class LocalConsumer { Helper Value; }
			""");
		var second = fixture.CreateFile("Second.cs", """
			namespace Alpha;
			public partial class User { }
			public partial class User { }
			public class OtherConsumer { Helper Value; }
			""");
		var ambiguous = fixture.CreateFile("Ambiguous.cs", "namespace Beta; public class User { }");
		var global = fixture.CreateFile("Global.cs", "global using Alpha;");
		var marker = fixture.CreateFile("MarkerAttribute.cs", "public sealed class MarkerAttribute : System.Attribute { }");
		var consumer = fixture.CreateFile("Consumer.cs", "[Marker] public class Consumer { User Value; }");
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(fixture.Path, [project, first, second, ambiguous, global, marker, consumer],
			cancellationToken: TestContext.Current.CancellationToken);

		var user = Assert.Single(index.Declarations, declaration =>
			declaration.Identity.QualifiedName == "Alpha.User");
		Assert.Equal(3, user.DeclarationSites.Count);
		var userEdge = Assert.Single(index.Edges, edge =>
			edge.Source == "Consumer.cs" && edge.Reference == "User");
		Assert.Equal(ResolutionStatus.Resolved, userEdge.Status);
		Assert.Contains("First.cs", userEdge.Candidates);
		var resolvedReference = Assert.Single(index.Files.Single(file => file.Path == "Consumer.cs").References,
			reference => reference.Name == "User");
		Assert.Equal(ResolutionStatus.Resolved, resolvedReference.Status);
		Assert.Equal("First.cs", resolvedReference.Target);
		Assert.Contains(index.Edges, edge => edge.Source == "Consumer.cs" &&
			edge.Reference == "Marker" && edge.Target == "MarkerAttribute.cs");
		var localEdge = Assert.Single(index.Edges, edge =>
			edge.Source == "Second.cs" && edge.Reference == "Helper");
		Assert.Equal(ResolutionStatus.Unresolved, localEdge.Status);
	}

	[Fact]
	public async Task CSharpTypeParameter_ShadowsADeclarationAndProjectReferenceControlsCrossScope()
	{
		using var fixture = new TemporaryDirectory();
		var producerProject = fixture.CreateFile("Producer/Producer.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var producer = fixture.CreateFile("Producer/User.cs", "namespace Models; public class User { }");
		var consumerProject = fixture.CreateFile("Consumer/Consumer.csproj", """
			<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../Producer/Producer.csproj" /></ItemGroup></Project>
			""");
		var consumer = fixture.CreateFile("Consumer/Box.cs", "using Models; public class Box<User> { User Value; }");
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(fixture.Path, [producerProject, producer, consumerProject, consumer],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(index.Edges, candidate => candidate.Source == "Consumer/Box.cs" && candidate.Reference == "User");
		Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
		Assert.Contains("shadows", Assert.Single(edge.Reasons), StringComparison.Ordinal);
	}

	[Fact]
	public async Task CSharpIdentity_PreservesContainingGenericArityAndFileScope()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var source = fixture.CreateFile("Nested.cs", "namespace Models; public class Outer<T> { public class Inner<U,V> { } } file class Helper { }");
		var consumer = fixture.CreateFile("Consumer.cs", "using ModelAlias = Models; public class Consumer { ModelAlias.Outer<string> Value; }");
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(fixture.Path, [project, source, consumer],
			cancellationToken: TestContext.Current.CancellationToken);

		var inner = Assert.Single(index.Declarations, declaration => declaration.Identity.QualifiedName.EndsWith("Inner`2", StringComparison.Ordinal));
		Assert.Equal("Models.Outer`1.Inner`2", inner.Identity.QualifiedName);
		Assert.Equal(2, inner.Identity.GenericArity);
		var helper = Assert.Single(index.Declarations, declaration => declaration.Identity.QualifiedName == "Models.Helper");
		Assert.Equal("Nested.cs", helper.Identity.FileScope);
		var genericReference = Assert.Single(index.Edges, edge =>
			edge.Source == "Consumer.cs" && edge.Reference == "ModelAlias.Outer");
		Assert.Equal(ResolutionStatus.Resolved, genericReference.Status);
		Assert.Equal("Nested.cs", genericReference.Target);
	}

	[Fact]
	public async Task TypeScriptFacts_ApplyJsSubstitutionPathsAndNoBundlerIndexFallback()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", """
			{"compilerOptions":{"moduleResolution":"bundler","paths":{"exact":["src/exact.ts"],"lib/*":["src/lib/*"]}}}
			""");
		var main = fixture.CreateFile("src/main.ts", """
			import { x } from "./x.js";
			import { exact } from "exact";
			import { item } from "lib/item";
			import { hidden } from "./dir";
			""");
		var x = fixture.CreateFile("src/x.ts", "export const x = 1;");
		var exact = fixture.CreateFile("src/exact.ts", "export const exact = 1;");
		var item = fixture.CreateFile("src/lib/item.ts", "export const item = 1;");
		var index = fixture.CreateFile("src/dir/index.ts", "export const hidden = 1;");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(fixture.Path, [config, main, x, exact, item, index],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Edges, edge => edge.Source == "src/main.ts" && edge.Target == "src/x.ts");
		var resolvedImport = Assert.Single(result.Files.Single(file => file.Path == "src/main.ts").Imports,
			import => import.Specifier == "./x.js");
		Assert.Equal(ResolutionStatus.Resolved, resolvedImport.Status);
		Assert.Equal("src/x.ts", resolvedImport.Target);
		Assert.Contains(result.Edges, edge => edge.Source == "src/main.ts" && edge.Target == "src/exact.ts");
		Assert.Contains(result.Edges, edge => edge.Source == "src/main.ts" && edge.Target == "src/lib/item.ts");
		Assert.Contains(result.Edges, edge => edge.Source == "src/main.ts" && edge.Reference == "./dir" && edge.Status == ResolutionStatus.Unresolved);
	}

	[Fact]
	public async Task TypeScriptPackageExports_NullTargetIsUnresolvedAndLegacyConfigIsExplicit()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{" + "\"compilerOptions\":{\"moduleResolution\":\"node10\",\"baseUrl\":\".\"}}" );
		var package = fixture.CreateFile("package.json", "{" + "\"name\":\"self\",\"exports\":{\"./blocked\":null}}" );
		var main = fixture.CreateFile("main.ts", "import value from \"self/blocked\";");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(fixture.Path, [config, package, main],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, edge => edge.Source == "main.ts" && edge.Layer == EvidenceLayer.ExplicitImport);
		Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
		Assert.Contains("legacy", Assert.Single(edge.Reasons), StringComparison.Ordinal);
	}

	[Fact]
	public async Task TypeScriptPackageSelfReference_UsesConditionalExportsAndHonorsNullBlocking()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var package = fixture.CreateFile("package.json", "{\"name\":\"self\",\"exports\":{\"import\":\"./entry.ts\",\"default\":null},\"imports\":{\"#blocked\":null}}");
		var source = fixture.CreateFile("main.ts", "import value from 'self'; import blocked from '#blocked';");
		var entry = fixture.CreateFile("entry.ts", "export default 1;");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(fixture.Path, [config, package, source, entry],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Edges, edge => edge.Reference == "self" && edge.Target == "entry.ts");
		Assert.Contains(result.Edges, edge => edge.Reference == "#blocked" &&
			edge.Status == ResolutionStatus.Unresolved && edge.Reasons.Contains("package imports target is null-blocked"));
	}

	[Fact]
	public async Task TypeScriptBareImports_RequireDeclaredExternalEvidence()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var package = fixture.CreateFile("package.json", "{\"dependencies\":{\"react\":\"19.0.0\"}}");
		var source = fixture.CreateFile("main.ts", "import React from 'react'; import value from 'not-declared';");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(fixture.Path, [config, package, source],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Edges, edge => edge.Reference == "react" && edge.Status == ResolutionStatus.External);
		Assert.Contains(result.Edges, edge => edge.Reference == "not-declared" && edge.Status == ResolutionStatus.Unresolved);
	}

	[Fact]
	public async Task TypeScriptExternalPackageEvidence_DoesNotLeakAcrossPackageScopes()
	{
		using var fixture = new TemporaryDirectory();
		var firstConfig = fixture.CreateFile("first/tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var firstPackage = fixture.CreateFile("first/package.json", "{\"dependencies\":{\"react\":\"19.0.0\"}}");
		var firstSource = fixture.CreateFile("first/main.ts", "import React from 'react';");
		var secondConfig = fixture.CreateFile("second/tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var secondPackage = fixture.CreateFile("second/package.json", "{\"name\":\"second\"}");
		var secondSource = fixture.CreateFile("second/main.ts", "import React from 'react';");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[firstConfig, firstPackage, firstSource, secondConfig, secondPackage, secondSource],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Edges, edge => edge.Source == "first/main.ts" &&
			edge.Reference == "react" && edge.Status == ResolutionStatus.External);
		Assert.Contains(result.Edges, edge => edge.Source == "second/main.ts" &&
			edge.Reference == "react" && edge.Status == ResolutionStatus.Unresolved);
	}

	[Fact]
	public async Task TypeScriptRequire_OnlyResolvesInSupportedCommonJsContext()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"node16\"}}");
		var package = fixture.CreateFile("package.json", "{\"type\":\"module\"}");
		var esm = fixture.CreateFile("main.js", "const value = require('./value.js');");
		var commonJs = fixture.CreateFile("worker.cjs", "const value = require('./value.js');");
		var target = fixture.CreateFile("value.ts", "export const value = 1;");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(fixture.Path, [config, package, esm, commonJs, target],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Edges, edge => edge.Source == "main.js" &&
			edge.Reference == "./value.js" && edge.Status == ResolutionStatus.Unresolved &&
			edge.Reasons.Contains("require call is outside a supported CommonJS context"));
		Assert.Contains(result.Edges, edge => edge.Source == "worker.cjs" &&
			edge.Reference == "./value.js" && edge.Target == "value.ts");
	}

	[Fact]
	public async Task MissingCompilationConfiguration_RemainsUnresolvedInsteadOfGuessingATarget()
	{
		using var fixture = new TemporaryDirectory();
		var csharpTarget = fixture.CreateFile("Target.cs", "public class Target { }");
		var csharpSource = fixture.CreateFile("Source.cs", "public class Source { Target Value; }");
		var typeScriptTarget = fixture.CreateFile("value.ts", "export const value = 1;");
		var typeScriptSource = fixture.CreateFile("main.ts", "import { value } from './value.js';");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[csharpTarget, csharpSource, typeScriptTarget, typeScriptSource],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Edges, edge => edge.Source == "Source.cs" &&
			edge.Reference == "Target" && edge.Status == ResolutionStatus.Unresolved &&
			edge.Reasons.Contains("no owning .csproj in the manifest"));
		Assert.Contains(result.Edges, edge => edge.Source == "main.ts" &&
			edge.Reference == "./value.js" && edge.Status == ResolutionStatus.Unresolved &&
			edge.Reasons.Contains("no owning tsconfig.json or jsconfig.json in the manifest"));
	}

	[Fact]
	public async Task PythonFacts_ResolveRelativeImportsAndClassifyKnownStdlibOnly()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\ndependencies = [\"flask>=3\"]");
		var init = fixture.CreateFile("src/pkg/__init__.py", string.Empty);
		var sibling = fixture.CreateFile("src/pkg/sibling.py", "class Value: pass");
		var consumer = fixture.CreateFile("src/pkg/consumer.py", "from . import sibling\nimport pathlib\nimport unknown_package");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(fixture.Path, [config, init, sibling, consumer],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Edges, edge => edge.Source == "src/pkg/consumer.py" && edge.Target == "src/pkg/sibling.py");
		Assert.Contains(result.Edges, edge => edge.Source == "src/pkg/consumer.py" && edge.Reference == "pathlib" && edge.Status == ResolutionStatus.External);
		Assert.Contains(result.Edges, edge => edge.Source == "src/pkg/consumer.py" && edge.Reference == "unknown_package" && edge.Status == ResolutionStatus.Unresolved);
		var configuration = await new FileDependencyConfigurationProvider().ReadAsync(
			fixture.Path,
			[config],
			TestContext.Current.CancellationToken);
		var pythonScope = Assert.Single(configuration.Scopes, scope => scope.LanguageId == LanguageId.Python && scope.HasConfiguration);
		Assert.Contains("flask", pythonScope.PythonExternalPackages);
		Assert.DoesNotContain("project", pythonScope.PythonExternalPackages);
	}

	[Fact]
	public async Task PythonPlatformCatalog_UsesDeclaredTargetVersionAndAConservativeUnknownVersion()
	{
		using var fixture = new TemporaryDirectory();
		var python312 = fixture.CreateFile("v312/pyproject.toml", "[project]\nrequires-python = \">=3.12,<3.13\"");
		var source312 = fixture.CreateFile("v312/main.py", "import aifc\nimport pathlib");
		var python313 = fixture.CreateFile("v313/pyproject.toml", "[project]\nrequires-python = \">=3.13\"");
		var source313 = fixture.CreateFile("v313/main.py", "import aifc\nimport pathlib");
		var unknown = fixture.CreateFile("unknown/pyproject.toml", "[project]\nname = \"fixture\"");
		var unknownSource = fixture.CreateFile("unknown/main.py", "import aifc\nimport pathlib");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(fixture.Path, [python312, source312, python313, source313, unknown, unknownSource],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Edges, edge => edge.Source == "v312/main.py" && edge.Reference == "aifc" &&
			edge.Status == ResolutionStatus.External);
		Assert.Contains(result.Edges, edge => edge.Source == "v313/main.py" && edge.Reference == "aifc" &&
			edge.Status == ResolutionStatus.Unresolved);
		Assert.Contains(result.Edges, edge => edge.Source == "unknown/main.py" && edge.Reference == "aifc" &&
			edge.Status == ResolutionStatus.Unresolved);
		Assert.All(result.Edges.Where(edge => edge.Reference == "pathlib"),
			edge => Assert.Equal(ResolutionStatus.External, edge.Status));
	}

	[Fact]
	public async Task Cache_ReparsesOnlyChangedSourceAndReresolvesConfigurationWithoutParsing()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{" + "\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}" );
		var main = fixture.CreateFile("main.ts", "import { value } from \"alias\";");
		var value = fixture.CreateFile("value.ts", "export const value = 1;");
		using var engine = CreateEngine();
		var manifest = new[] { config, main, value };
		_ = await engine.IndexAsync(fixture.Path, manifest,
			cancellationToken: TestContext.Current.CancellationToken);

		File.WriteAllText(config, "{" + "\"compilerOptions\":{\"moduleResolution\":\"bundler\",\"paths\":{\"alias\":[\"value.ts\"]}}}" );
		var configured = await engine.IndexAsync(fixture.Path, manifest,
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Equal(0, configured.Metrics.ParsedFiles);
		Assert.Contains(configured.Edges, edge => edge.Source == "main.ts" && edge.Target == "value.ts");

		File.WriteAllText(value, "export const value = 2;");
		var changed = await engine.IndexAsync(fixture.Path, manifest,
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Equal(1, changed.Metrics.ParsedFiles);
	}

	[Fact]
	public async Task Cache_RebindsFactsWhenConfigurationChangesFileOwnershipWithoutParsingSource()
	{
		using var fixture = new TemporaryDirectory();
		var rootProject = fixture.CreateFile("Root.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var source = fixture.CreateFile("Sub/Source.cs", "namespace Fixture; public class Source { }");
		using var engine = CreateEngine();
		var first = await engine.IndexAsync(fixture.Path, [rootProject, source],
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Equal("csharp:Root.csproj", Assert.Single(first.Files, file => file.Path == "Sub/Source.cs").ScopeId);

		var nestedProject = fixture.CreateFile("Sub/Sub.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var second = await engine.IndexAsync(fixture.Path, [rootProject, nestedProject, source],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(0, second.Metrics.ParsedFiles);
		Assert.Equal("csharp:Sub/Sub.csproj", Assert.Single(second.Files, file => file.Path == "Sub/Source.cs").ScopeId);
		Assert.Equal("csharp:Sub/Sub.csproj", Assert.Single(second.Declarations).Identity.ScopeId);
	}

	[Fact]
	public async Task Cache_DoesNotReuseRootRelativeFactsAcrossDifferentSourceRoots()
	{
		using var fixture = new TemporaryDirectory();
		var projectRoot = fixture.CreateDirectory("Project");
		var project = fixture.CreateFile("Project/Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var target = fixture.CreateFile("Project/Target.cs", "public class Target { }");
		var source = fixture.CreateFile("Project/Source.cs", "public class Source { Target Value; }");
		using var engine = CreateEngine();
		_ = await engine.IndexAsync(fixture.Path, [project, target, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var nested = await engine.IndexAsync(projectRoot, [project, target, source],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.All(nested.Files, file => Assert.False(file.Path.StartsWith("Project/", StringComparison.Ordinal), file.Path));
		Assert.All(nested.Edges, edge => Assert.False(edge.Source.StartsWith("Project/", StringComparison.Ordinal), edge.Source));
		Assert.Equal(2, nested.Metrics.ParsedFiles);
	}

	[Fact]
	public async Task Cache_MemoryBudgetsEvictCompletedFactsAndResolvedIndexes()
	{
		using var fixture = new TemporaryDirectory();
		var target = fixture.CreateFile("Target.cs", "public class Target { }");
		var source = fixture.CreateFile("Source.cs", "public class Source { Target Value; }");
		using var engine = new DependencyFactsEngine(
			new TreeSitterDependencyFactExtractor(),
			new FileDependencyConfigurationProvider(),
			new DependencyFactsLimits(MaximumFileCacheBytes: 1, MaximumIndexCacheBytes: 1));

		_ = await engine.IndexAsync(fixture.Path, [target, source],
			cancellationToken: TestContext.Current.CancellationToken);
		var repeated = await engine.IndexAsync(fixture.Path, [source, target],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.True(repeated.Metrics.ParsedFiles > 0);
		Assert.False(repeated.Metrics.ResolutionCacheHit);
	}

	[Fact]
	public async Task ManifestGate_DropsCachedTargetsAndOrderingIsDeterministic()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var target = fixture.CreateFile("Target.cs", "public class Target { }");
		var source = fixture.CreateFile("Source.cs", "public class Source { Target Value; }");
		using var engine = CreateEngine();
		var first = await engine.IndexAsync(fixture.Path, [source, target, project],
			cancellationToken: TestContext.Current.CancellationToken);
		var second = await engine.IndexAsync(fixture.Path, [project, target, source],
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Equal(JsonSerializer.Serialize(first.Edges), JsonSerializer.Serialize(second.Edges));

		var narrowed = await engine.IndexAsync(fixture.Path, [project, source],
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.DoesNotContain(narrowed.Edges, edge => edge.Target == "Target.cs" || edge.Candidates.Contains("Target.cs"));
		Assert.DoesNotContain(narrowed.Files.SelectMany(static file => file.References), reference =>
			reference.Target == "Target.cs" || reference.Candidates?.Contains("Target.cs") == true);

		var concurrent = await Task.WhenAll(Enumerable.Range(0, 6).Select(index => engine.IndexAsync(
			fixture.Path,
			index % 2 == 0 ? [source, target, project] : [project, target, source],
			cancellationToken: TestContext.Current.CancellationToken)));
		Assert.Single(concurrent.Select(static value => JsonSerializer.Serialize(value.Edges)).Distinct(StringComparer.Ordinal));
	}

	[Fact]
	public async Task ConcurrentColdRequests_DeduplicateFileParsingAndResolution()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var target = fixture.CreateFile("Target.cs", "public class Target { }");
		var source = fixture.CreateFile("Source.cs", "public class Source { Target Value; }");
		using var engine = CreateEngine();

		var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => engine.IndexAsync(
			fixture.Path,
			[project, target, source],
			cancellationToken: TestContext.Current.CancellationToken)));

		Assert.Equal(2, engine.ParseCount);
		Assert.Single(results.Select(static result => JsonSerializer.Serialize(result.Edges)).Distinct(StringComparer.Ordinal));
		Assert.Single(results, static result => !result.Metrics.ResolutionCacheHit);
	}

	[Fact]
	public async Task LimitsAndUnsupportedLanguages_AreReportedInsteadOfEmptySuccess()
	{
		using var fixture = new TemporaryDirectory();
		var large = fixture.CreateFile("Large.cs", "public class Large { " + new string(' ', 100) + " }");
		var markdown = fixture.CreateFile("README.md", "# fixture");
		using var extractor = new TreeSitterDependencyFactExtractor();
		using var engine = new DependencyFactsEngine(
			extractor,
			new FileDependencyConfigurationProvider(),
			new DependencyFactsLimits(MaximumCharactersPerFile: 32));

		var result = await engine.IndexAsync(fixture.Path, [large, markdown],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(1, result.Coverage.Unsupported);
		Assert.Equal(1, result.Coverage.ExtractionFailed);
		Assert.Contains(result.Files, file => file.Path == "Large.cs" && file.StatusReason!.Contains("parse limit", StringComparison.Ordinal));
	}

	[Fact]
	public async Task MissingGrammar_IsAnExtractionFailureWithAReason()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var source = fixture.CreateFile("Source.cs", "public class Source { }");
		using var engine = new DependencyFactsEngine(
			new TreeSitterDependencyFactExtractor(new MissingGrammarLocator()),
			new FileDependencyConfigurationProvider());

		var result = await engine.IndexAsync(fixture.Path, [project, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var failed = Assert.Single(result.Files, file => file.Path == "Source.cs");
		Assert.Equal(DependencyFileStatus.ExtractionFailed, failed.Status);
		Assert.Contains(nameof(FileNotFoundException), failed.StatusReason, StringComparison.Ordinal);
		Assert.Equal(1, result.Coverage.ExtractionFailed);
	}

	[Fact]
	public async Task FactEdgeAndWorkLimits_ProduceExplicitUnresolvedReasons()
	{
		using var fixture = new TemporaryDirectory();
		var declarations = fixture.CreateFile("Types.cs", "public class One {} public class Two {} public class Three {}");
		var source = fixture.CreateFile("Source.cs", "public class Source { One A; Two B; Three C; }");

		using (var factLimited = new DependencyFactsEngine(
			new TreeSitterDependencyFactExtractor(),
			new FileDependencyConfigurationProvider(),
			new DependencyFactsLimits(MaximumFactsPerFile: 1)))
		{
			var result = await factLimited.IndexAsync(fixture.Path, [declarations, source],
				cancellationToken: TestContext.Current.CancellationToken);
			Assert.Contains(result.Files, file => file.StatusReason == "fact limit exceeded");
		}

		using (var edgeLimited = new DependencyFactsEngine(
			new TreeSitterDependencyFactExtractor(),
			new FileDependencyConfigurationProvider(),
			new DependencyFactsLimits(MaximumEdgesPerFile: 1)))
		{
			var result = await edgeLimited.IndexAsync(fixture.Path, [declarations, source],
				cancellationToken: TestContext.Current.CancellationToken);
			Assert.Contains(result.Edges, edge => edge.Reference == "<limit>" && edge.Reasons.Contains("edge limit exceeded"));
		}

		using var workLimited = new DependencyFactsEngine(
			new TreeSitterDependencyFactExtractor(),
			new FileDependencyConfigurationProvider(),
			new DependencyFactsLimits(MaximumWorkPerIndex: 1));
		var work = await workLimited.IndexAsync(fixture.Path, [declarations, source],
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains(work.Edges, edge => edge.Reference == "<limit>" && edge.Reasons.Contains("index work limit exceeded"));
	}

	[Fact]
	public async Task VersionedPlatformCatalogsClassifyOnlyRecordedSymbols()
	{
		using var fixture = new TemporaryDirectory();
		var package = fixture.CreateFile("package.json", "{\"optionalDependencies\":{\"known-package\":\"1.0.0\"}}");
		var provider = new FileDependencyConfigurationProvider();
		var configuration = await provider.ReadAsync(
			fixture.Path,
			[package],
			TestContext.Current.CancellationToken);

		Assert.Contains("System.String", configuration.DotNetExternalSymbols);
		Assert.True(configuration.DotNetExternalSymbols.Count > 1_000);
		Assert.Equal(["3.12", "3.13"], configuration.PythonStandardLibraryModules.Keys.Order(StringComparer.Ordinal));
		Assert.All(configuration.PythonStandardLibraryModules.Values, catalog => Assert.Contains("asyncio", catalog));
		Assert.True(configuration.PythonStandardLibraryModules["3.12"].Count >= 300);
		Assert.True(configuration.PythonStandardLibraryModules["3.13"].Count >= 290);
		Assert.Contains("fs", configuration.NodeBuiltInModules);
		Assert.Contains("known-package", Assert.Single(configuration.PackageMaps).Value.ExternalPackages);
		Assert.False(DependencyPlatformCatalog.IsNodeExternal(configuration, "not-a-recorded-module"));
	}

	[Fact]
	public void CompressionWithoutDependencyRequest_DoesNotCompileDependencyQueriesOrParseFiles()
	{
		using var extractor = new TreeSitterDependencyFactExtractor();
		using var engine = new DependencyFactsEngine(extractor, new FileDependencyConfigurationProvider());
		using var compression = CodeCompressionFactory.CreateSession();

		Assert.Equal(0, engine.CompiledQuerySetCount);
		Assert.Equal(0, engine.ParseCount);
	}

	private static DependencyFactsEngine CreateEngine() => new(
		new TreeSitterDependencyFactExtractor(),
		new FileDependencyConfigurationProvider());

	private sealed class MissingGrammarLocator : IGrammarLibraryLocator
	{
		public string StrategyName => "missing-test-grammar";
		public IReadOnlyList<string> EnumerateLibraries() => [];
		public string Resolve(string libraryBaseName) =>
			throw new FileNotFoundException($"Grammar '{libraryBaseName}' is unavailable.", libraryBaseName);
	}
}
