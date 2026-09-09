using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class DependencyCSharpTypeScriptSemanticsIntegrationTests
{
	[Fact]
	public async Task New020_InactiveUnknownPackageConditionDoesNotBlockDefault()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var package = fixture.CreateFile(
			"package.json",
			"{\"imports\":{\"#value\":{\"browser\":null,\"default\":\"./right.ts\"}}}");
		var right = fixture.CreateFile("right.ts", "export default 2;");
		var source = fixture.CreateFile("main.ts", "import value from '#value';");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, package, right, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, candidate =>
			candidate.Source == "main.ts" && candidate.Reference == "#value");
		Assert.Equal(ResolutionStatus.Resolved, edge.Status);
		Assert.Equal("right.ts", edge.Target);
	}

	[Fact]
	public async Task New019_PackageWildcardPrefersLongestStaticPrefix()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var package = fixture.CreateFile(
			"package.json",
			"{\"imports\":{\"#a*-long-suffix\":\"./wrong.ts\",\"#ab*\":\"./right.ts\"}}");
		var wrong = fixture.CreateFile("wrong.ts", "export default 1;");
		var right = fixture.CreateFile("right.ts", "export default 2;");
		var source = fixture.CreateFile("main.ts", "import value from '#abc-long-suffix';");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, package, wrong, right, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, candidate =>
			candidate.Source == "main.ts" && candidate.Reference == "#abc-long-suffix");
		Assert.Equal(ResolutionStatus.Resolved, edge.Status);
		Assert.Equal("right.ts", edge.Target);
	}

	[Fact]
	public async Task New018_MultilineGenericReferenceUsesTokenCoordinates()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var declaration = fixture.CreateFile("Model.cs", "public sealed class Model { }");
		const string content = "public sealed class Consumer { Dictionary<\nstring,\nModel> Value; }";
		var source = fixture.CreateFile("Consumer.cs", content);
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[project, declaration, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var reference = Assert.Single(
			result.Files.Single(file => file.Path == "Consumer.cs").References,
			candidate => candidate.Name == "Model");
		Assert.Equal(3, reference.Site.Line);
		Assert.Equal(content.IndexOf("Model", StringComparison.Ordinal), reference.SourceStartIndex);
	}

	[Fact]
	public async Task New017_NonAsciiIdentifierStartIsExtractedAsTypeReference()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var declaration = fixture.CreateFile("Data.cs", "public sealed class Данные { }");
		var source = fixture.CreateFile("Consumer.cs", "public sealed class Consumer { Данные Value; }");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[project, declaration, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, candidate =>
			candidate.Source == "Consumer.cs" && candidate.Reference == "Данные");
		Assert.Equal(ResolutionStatus.Resolved, edge.Status);
		Assert.Equal("Data.cs", edge.Target);
	}

	[Fact]
	public async Task New016_GenericUsingAliasPreservesContainerAndArgumentReferences()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var container = fixture.CreateFile(
			"Container.cs",
			"namespace Company; public sealed class Container<T> { }");
		var model = fixture.CreateFile("Model.cs", "public sealed class Model { }");
		var source = fixture.CreateFile(
			"Consumer.cs",
			"using Items = Company.Container<Model>; public sealed class Consumer { Items Value; }");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[project, container, model, source],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Edges, edge =>
			edge.Source == "Consumer.cs" && edge.Target == "Container.cs");
		Assert.Contains(result.Edges, edge =>
			edge.Source == "Consumer.cs" && edge.Target == "Model.cs");
	}

	[Fact]
	public async Task New013_QualifiedNameUsesLexicalNamespaceBeforeGlobalNamespace()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var lexical = fixture.CreateFile(
			"AppItem.cs",
			"namespace App.Models; public sealed class Item { }");
		var global = fixture.CreateFile(
			"GlobalItem.cs",
			"namespace Models; public sealed class Item { }");
		var source = fixture.CreateFile(
			"Consumer.cs",
			"namespace App; public sealed class Consumer { Models.Item Value; }");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[project, lexical, global, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, candidate =>
			candidate.Source == "Consumer.cs" && candidate.Reference == "Models.Item");
		Assert.Equal(ResolutionStatus.Resolved, edge.Status);
		Assert.Equal("AppItem.cs", edge.Target);
	}

	[Fact]
	public async Task New012_NestedBlockNamespacesComposeTheirQualifiedName()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var nested = fixture.CreateFile(
			"Nested.cs",
			"namespace A { namespace B { public sealed class Item { } } }");
		var dotted = fixture.CreateFile(
			"Dotted.cs",
			"namespace A.B { public sealed class Other { } }");
		var source = fixture.CreateFile(
			"Consumer.cs",
			"public sealed class Consumer { A.B.Item First; A.B.Other Second; }");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[project, nested, dotted, source],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Declarations, declaration =>
			declaration.Identity.QualifiedName == "A.B.Item");
		Assert.Contains(result.Declarations, declaration =>
			declaration.Identity.QualifiedName == "A.B.Other");
		Assert.Contains(result.Edges, edge => edge.Source == "Consumer.cs" && edge.Target == "Nested.cs");
		Assert.Contains(result.Edges, edge => edge.Source == "Consumer.cs" && edge.Target == "Dotted.cs");
	}

	[Fact]
	public async Task New011_TupleCommasDoNotIncreaseConstructedGenericArity()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var unary = fixture.CreateFile("Unary.cs", "public sealed class Box<T> { }");
		var binary = fixture.CreateFile("Binary.cs", "public sealed class Box<T, U> { }");
		var source = fixture.CreateFile(
			"Consumer.cs",
			"public sealed class Consumer { Box<(int X, int Y)> Value { get; } }");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[project, unary, binary, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, candidate =>
			candidate.Source == "Consumer.cs" && candidate.Reference == "Box");
		Assert.Equal(ResolutionStatus.Resolved, edge.Status);
		Assert.Equal("Unary.cs", edge.Target);
	}

	[Fact]
	public async Task New010_DynamicImportAttributesPreserveEveryImportFact()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var data = fixture.CreateFile("data.ts", "export default 1;");
		var ordinary = fixture.CreateFile("ordinary.ts", "export default 2;");
		var source = fixture.CreateFile(
			"main.ts",
			"import('./data.js', { with: { type: 'json' } });\nimport './ordinary.js';");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, data, ordinary, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var facts = result.Files.Single(file => file.Path == "main.ts");
		Assert.Equal(DependencyFileStatus.Supported, facts.Status);
		Assert.Contains(facts.Imports, import =>
			import.Specifier == "./data.js" && import.ImportKind == ModuleImportKind.DynamicImport);
		Assert.Contains(facts.Imports, import => import.Specifier == "./ordinary.js");
		Assert.Contains(result.Edges, edge => edge.Source == "main.ts" && edge.Target == "data.ts");
		Assert.Contains(result.Edges, edge => edge.Source == "main.ts" && edge.Target == "ordinary.ts");
	}

	private static DependencyFactsEngine CreateEngine() => new(
		new TreeSitterDependencyFactExtractor(),
		new FileDependencyConfigurationProvider());
}
