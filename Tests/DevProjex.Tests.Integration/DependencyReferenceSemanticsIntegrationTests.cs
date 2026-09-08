using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class DependencyReferenceSemanticsIntegrationTests
{
	[Fact]
	public async Task CSharpReferences_OnOneLineRemainDistinctOccurrencesAcrossLexicalOwners()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var model = fixture.CreateFile("Models/User.cs", "namespace Models; public sealed class User { }");
		var source = fixture.CreateFile("Consumers.cs",
			"using Models; class Box<User> { User a; } class Consumer { User b; }");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[project, model, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var references = result.Files.Single(file => file.Path == "Consumers.cs").References
			.Where(reference => reference.Name == "User")
			.ToArray();
		Assert.Equal(2, references.Length);
		Assert.Contains(references, reference => reference.Status == ResolutionStatus.Unresolved);
		Assert.Contains(references, reference =>
			reference.Status == ResolutionStatus.Resolved && reference.Target == "Models/User.cs");
	}

	[Fact]
	public async Task CSharpDeclarationFiltering_UsesTheNameOccurrenceInsteadOfTheWholeLine()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var source = fixture.CreateFile("User.cs", "public sealed class User { public User Value { get; } }");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(fixture.Path, [project, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var facts = result.Files.Single(file => file.Path == "User.cs");
		Assert.Single(facts.Declarations, declaration => declaration.Identity.QualifiedName == "User");
		Assert.Single(facts.References, reference => reference.Name == "User");
		Assert.Contains(result.Edges, edge => edge.Source == "User.cs" && edge.Target == "User.cs" &&
			edge.Status == ResolutionStatus.Resolved);
	}

	[Fact]
	public async Task CSharpGlobalQualification_BypassesTypeParameterShadowingAndContextualFallback()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var global = fixture.CreateFile("User.cs", "public sealed class User { }");
		var model = fixture.CreateFile("Models/User.cs", "namespace Models; public sealed class User { }");
		var source = fixture.CreateFile("Consumers.cs", """
			namespace Consumers;
			class Box<User>
			{
				global::User AbsoluteGlobal;
				global::Models.User AbsoluteQualified;
				User Shadowed;
			}
			class Neighbor { global::User Absolute; User Ordinary; }
			""");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[project, global, model, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var facts = result.Files.Single(file => file.Path == "Consumers.cs");
		Assert.Contains(facts.References, reference => reference.Name == "User" &&
			reference.IsGlobalQualified && reference.Site.Line == 4 && reference.Target == "User.cs");
		Assert.Contains(facts.References, reference => reference.Name == "Models.User" &&
			reference.IsGlobalQualified && reference.Site.Line == 5 && reference.Target == "Models/User.cs");
		Assert.Contains(result.Edges, edge => edge.Source == "Consumers.cs" &&
			edge.Reference == "Models.User" && edge.Target == "Models/User.cs" &&
			edge.Status == ResolutionStatus.Resolved);
		Assert.Contains(result.Edges, edge => edge.Source == "Consumers.cs" &&
			edge.Reference == "User" && edge.Status == ResolutionStatus.Unresolved);
		Assert.Contains(result.Edges, edge => edge.Source == "Consumers.cs" &&
			edge.Reference == "User" && edge.Target == "User.cs" &&
			edge.Status == ResolutionStatus.Resolved);
	}

	[Fact]
	public async Task PythonFromImport_RejectsNestedDeclarationsAndMissingNamesButKeepsTopLevelBindings()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var model = fixture.CreateFile("model.py", "class Container:\n    def helper(self): pass\n\nclass Item: pass\ndef create(): pass");
		var nestedConsumer = fixture.CreateFile("nested_consumer.py", "from model import helper");
		var missingConsumer = fixture.CreateFile("missing_consumer.py", "from model import Missing");
		var consumer = fixture.CreateFile("consumer.py", "from model import Item\nfrom model import create");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(fixture.Path, [config, model, nestedConsumer, missingConsumer, consumer],
			cancellationToken: TestContext.Current.CancellationToken);

		var imports = result.Files.Single(file => file.Path == "consumer.py").Imports;
		Assert.Equal(2, imports.Count);
		var resolved = Assert.Single(result.Edges, edge => edge.Source == "consumer.py" &&
			edge.Status == ResolutionStatus.Resolved && edge.Target == "model.py");
		Assert.Equal(2, resolved.Evidence.Count);
		var unresolved = result.Edges.Where(edge => edge.Source is "nested_consumer.py" or "missing_consumer.py" &&
			edge.Status == ResolutionStatus.Unresolved).ToArray();
		Assert.Equal(2, unresolved.Length);
		Assert.All(unresolved, edge => Assert.Contains("name not found in module", edge.Reasons));
		var nested = result.Files.Single(file => file.Path == "model.py").Declarations
			.Single(declaration => declaration.Identity.QualifiedName.EndsWith(".helper", StringComparison.Ordinal));
		Assert.Equal("Container", nested.ContainingType);
	}

	[Fact]
	public async Task PythonImports_UseParsedNodesForMultilineAliasesRelativeAndWildcardForms()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var init = fixture.CreateFile("pkg/__init__.py", "from .model import Item");
		var model = fixture.CreateFile("pkg/model.py", "class Item: pass\nclass Other: pass");
		var consumer = fixture.CreateFile("pkg/consumer.py", """
			from .model import (
			    Item as Renamed,
			    # retained syntax comment
			    Other,
			)
			from .model import *
			from . import model
			import pkg.model as direct
			""");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(fixture.Path, [config, init, model, consumer],
			cancellationToken: TestContext.Current.CancellationToken);

		var facts = result.Files.Single(file => file.Path == "pkg/consumer.py");
		Assert.Contains(facts.Imports, fact => fact.Specifier == "model" && fact.ImportedName == "Item" && fact.Alias == "Renamed");
		Assert.Contains(facts.Imports, fact => fact.Specifier == "model" && fact.ImportedName == "Other");
		Assert.Contains(facts.Imports, fact => fact.Specifier == "model" && fact.IsWildcard);
		Assert.Contains(facts.Imports, fact => fact.Specifier.Length == 0 && fact.ImportedName == "model" && fact.RelativeLevel == 1);
		Assert.Contains(facts.Imports, fact => fact.Specifier == "pkg.model" && fact.Alias == "direct");
		Assert.All(result.Edges.Where(edge => edge.Source == "pkg/consumer.py"),
			edge => Assert.Equal(ResolutionStatus.Resolved, edge.Status));
	}

	[Fact]
	public async Task TypeScriptImports_UseSyntaxSourceAndRejectNonLiteralCalls()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var target = fixture.CreateFile("register.ts", "export const x = 1;");
		var source = fixture.CreateFile("main.ts", """
			import "./register.js";
			import value from "./register.js";
			import * as ns from "./register.js";
			import type { x } from "./register.js";
			export * from "./register.js";
			export { x } from "./register.js";
			require("./register.js");
			import("./register.js");
			const path = "./register.js";
			require(path);
			import(`./register.js`);
			""");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(fixture.Path, [config, target, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var imports = result.Files.Single(file => file.Path == "main.ts").Imports;
		Assert.Equal(8, imports.Count(import => import.Specifier == "./register.js"));
		Assert.Equal(2, imports.Count(import => import.Reason == "module specifier is not a string literal"));
		var edges = result.Edges.Where(edge => edge.Source == "main.ts").ToArray();
		var resolved = Assert.Single(edges, edge => edge.Reference == "./register.js" &&
			edge.Status == ResolutionStatus.Resolved && edge.Target == "register.ts");
		Assert.Equal(8, resolved.Evidence.Count);
		var unsupported = edges.Where(edge => edge.Status == ResolutionStatus.Unresolved &&
			edge.Reasons.Contains("module specifier is not a string literal")).ToArray();
		var unsupportedEdge = Assert.Single(unsupported);
		Assert.Equal(2, unsupportedEdge.Evidence.Count);
		Assert.DoesNotContain(unsupported, edge => edge.Target is not null);
	}

	[Fact]
	public async Task PythonNamespaceImport_ResolvesTheRequestedChildInsteadOfAnArbitraryPortionFile()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var unrelated = fixture.CreateFile("ns/aaa.py", "value = 1");
		var child = fixture.CreateFile("ns/child.py", "value = 2");
		var consumer = fixture.CreateFile("consumer.py", "from ns import child");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(fixture.Path, [config, unrelated, child, consumer],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, item => item.Source == "consumer.py");
		Assert.Equal(ResolutionStatus.Resolved, edge.Status);
		Assert.Equal("ns/child.py", edge.Target);
		Assert.DoesNotContain("ns/aaa.py", edge.Candidates);
	}

	[Fact]
	public async Task DependencyReasons_DoNotEchoProjectControlledNamesSpecifiersOrPaths()
	{
		const string sentinel = "PROJECT_SENTINEL_DO_NOT_ECHO";
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\",\"paths\":{\"" + sentinel + "\":42}}}");
		var source = fixture.CreateFile("main.ts", "import \"" + sentinel + "\";");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(fixture.Path, [config, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var reasons = result.Edges.SelectMany(static edge => edge.Reasons)
			.Concat(result.Files.SelectMany(static file => file.Imports.Select(import => import.Reason)))
			.Concat(result.Files.SelectMany(static file => file.References.Select(reference => reference.Reason)))
			.Concat(result.Files.Select(static file => file.StatusReason ?? string.Empty))
			.Concat(result.Coverage.ConfigurationDiagnostics.Select(static diagnostic => diagnostic.Reason));
		Assert.DoesNotContain(reasons, reason => reason.Contains(sentinel, StringComparison.Ordinal));
		Assert.Contains(result.Coverage.ConfigurationDiagnostics,
			diagnostic => diagnostic.Reason == "tsconfig path mapping must be an array of strings");
	}

	[Fact]
	public async Task MissingReferencedControlFile_IsCacheableUntilTheFileAppears()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("App/App.csproj", """
			<Project Sdk="Microsoft.NET.Sdk">
			  <ItemGroup><ProjectReference Include="../Missing/Missing.csproj" /></ItemGroup>
			</Project>
			""");
		var source = fixture.CreateFile("App/Consumer.cs", "public sealed class Consumer { }");
		var configuration = await new FileDependencyConfigurationProvider().ReadAsync(
			fixture.Path, [project, source], TestContext.Current.CancellationToken);
		Assert.Contains(Path.GetFullPath(Path.Combine(fixture.Path, "Missing/Missing.csproj")),
			configuration.AbsentControlFiles);
		using var engine = CreateEngine();

		var first = await engine.IndexAsync(fixture.Path, [project, source],
			cancellationToken: TestContext.Current.CancellationToken);
		var warm = await engine.IndexAsync(fixture.Path, [project, source],
			cancellationToken: TestContext.Current.CancellationToken);
		var appeared = fixture.CreateFile("Missing/Missing.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var invalidated = await engine.IndexAsync(fixture.Path, [project, source],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.False(first.Metrics.ResolutionCacheHit);
		Assert.True(warm.Metrics.ResolutionCacheHit);
		Assert.False(invalidated.Metrics.ResolutionCacheHit);
		Assert.True(File.Exists(appeared));
	}

	private static DependencyFactsEngine CreateEngine() => new(
		new TreeSitterDependencyFactExtractor(),
		new FileDependencyConfigurationProvider());
}
