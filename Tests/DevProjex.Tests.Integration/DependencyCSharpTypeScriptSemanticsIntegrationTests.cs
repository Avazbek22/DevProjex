using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class DependencyCSharpTypeScriptSemanticsIntegrationTests
{
	[Fact]
	public async Task New063_CustomConditionsFailClosedInsteadOfChoosingDefault()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\",\"customConditions\":[\"browser\"]}}");
		var package = fixture.CreateFile(
			"package.json",
			"{\"imports\":{\"#value\":{\"browser\":\"./browser.ts\",\"default\":\"./default.ts\"}}}");
		var browser = fixture.CreateFile("browser.ts", "export default 1;");
		var fallback = fixture.CreateFile("default.ts", "export default 2;");
		var source = fixture.CreateFile("main.ts", "import value from '#value';");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, package, browser, fallback, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, candidate =>
			candidate.Source == "main.ts" && candidate.Reference == "#value");
		Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
		Assert.Null(edge.Target);
		Assert.Equal("tsconfig customConditions are not supported", Assert.Single(edge.Reasons));
	}

	[Fact]
	public async Task New097_BarePackageImportTargetIsNotProbedAsLocalPath()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var package = fixture.CreateFile(
			"package.json",
			"{\"imports\":{\"#dep\":\"some-dependency\"},\"dependencies\":{\"some-dependency\":\"1.0.0\"}}");
		var decoy = fixture.CreateFile("some-dependency.ts", "export default 1;");
		var source = fixture.CreateFile("main.ts", "import value from '#dep';");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, package, decoy, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, candidate =>
			candidate.Source == "main.ts" && candidate.Reference == "#dep");
		Assert.Equal(ResolutionStatus.External, edge.Status);
		Assert.Null(edge.Target);
	}

	[Fact]
	public async Task New061_ModuleNodeNextInfersNodeNextResolution()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"tsconfig.json",
			"{\"compilerOptions\":{\"module\":\"NodeNext\"}}");
		var package = fixture.CreateFile("package.json", "{\"type\":\"module\"}");
		var target = fixture.CreateFile("target.ts", "export default 1;");
		var source = fixture.CreateFile("main.ts", "import value from './target';");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, package, target, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, candidate =>
			candidate.Source == "main.ts" && candidate.Reference == "./target");
		Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
		Assert.Contains("extension required", Assert.Single(edge.Reasons), StringComparison.Ordinal);
	}

	[Fact]
	public async Task New060_RelativeModuleUrlSuffixUsesPhysicalPath()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"nodenext\"}}");
		var package = fixture.CreateFile("package.json", "{\"type\":\"module\"}");
		var target = fixture.CreateFile("mod.ts", "export default 1;");
		var source = fixture.CreateFile(
			"main.ts",
			"import first from './mod.js?variant=1';\nimport second from './mod.js#named';");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, package, target, source],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal("mod.ts", Assert.Single(result.Edges, edge =>
			edge.Source == "main.ts" && edge.Reference == "./mod.js?variant=1").Target);
		Assert.Equal("mod.ts", Assert.Single(result.Edges, edge =>
			edge.Source == "main.ts" && edge.Reference == "./mod.js#named").Target);
	}

	[Fact]
	public async Task New052_OrdinaryCSharpTypePositionsProduceReferences()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var types = new[]
		{
			fixture.CreateFile("LocalItem.cs", "public sealed class LocalItem { }"),
			fixture.CreateFile("LoopItem.cs", "public sealed class LoopItem { }"),
			fixture.CreateFile("DomainError.cs", "public sealed class DomainError : System.Exception { }"),
			fixture.CreateFile("Signal.cs", "public delegate void Signal();"),
			fixture.CreateFile("IndexValue.cs", "public sealed class IndexValue { }"),
			fixture.CreateFile("LocalResult.cs", "public sealed class LocalResult { }"),
			fixture.CreateFile("DelegateResult.cs", "public sealed class DelegateResult { }"),
			fixture.CreateFile("Factory.cs", "public delegate DelegateResult Factory();")
		};
		var source = fixture.CreateFile(
			"Consumer.cs",
			"""
			public sealed class Consumer
			{
			    public event Signal Changed;
			    public event Signal Detailed { add { } remove { } }
			    public IndexValue this[int index] => null;
			    public void Run(System.Collections.IEnumerable values)
			    {
			        LocalItem local = null;
			        foreach (LoopItem value in values) { }
			        try { } catch (DomainError) { }
			        LocalResult Build() => null;
			    }
			}
			""");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[project, .. types, source],
			cancellationToken: TestContext.Current.CancellationToken);

		foreach (var target in new[]
		         {
			         "LocalItem.cs", "LoopItem.cs", "DomainError.cs", "Signal.cs", "IndexValue.cs", "LocalResult.cs"
		         })
			Assert.Contains(result.Edges, edge => edge.Source == "Consumer.cs" && edge.Target == target);
		Assert.Contains(result.Edges, edge => edge.Source == "Factory.cs" && edge.Target == "DelegateResult.cs");
		Assert.Equal(2, result.Files.Single(file => file.Path == "Consumer.cs").References.Count(reference =>
			reference.Name == "Signal"));
	}

	[Fact]
	public async Task New014_RelativeAliasTargetFallsBackToLexicalNamespace()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var model = fixture.CreateFile("Model.cs", "namespace App.Models; public sealed class Item { }");
		var source = fixture.CreateFile(
			"Consumer.cs",
			"namespace App { using M = Models.Item; public sealed class Consumer { M Value; } }");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[project, model, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, candidate =>
			candidate.Source == "Consumer.cs" && candidate.Reference == "M");
		Assert.Equal(ResolutionStatus.Resolved, edge.Status);
		Assert.Equal("Model.cs", edge.Target);
	}

	[Fact]
	public async Task New096_PackageExportsRejectTargetsOutsidePackage()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var package = fixture.CreateFile(
			"pkg/package.json",
			"{\"name\":\"pkg\",\"exports\":{\"./value\":\"../outside.js\"}}");
		var outside = fixture.CreateFile("outside.js", "export default 1;");
		var source = fixture.CreateFile("pkg/main.ts", "import value from 'pkg/value';");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, package, outside, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, candidate =>
			candidate.Source == "pkg/main.ts" && candidate.Reference == "pkg/value");
		Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
		Assert.Null(edge.Target);
		Assert.Equal("package exports target is invalid", Assert.Single(edge.Reasons));
	}

	[Fact]
	public async Task New095_RequireParameterDoesNotCreateModuleImport()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"jsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"node16\",\"allowJs\":true}}");
		var package = fixture.CreateFile("package.json", "{\"type\":\"commonjs\"}");
		var fake = fixture.CreateFile("fake.js", "module.exports = 1;");
		var real = fixture.CreateFile("real.js", "module.exports = 2;");
		var source = fixture.CreateFile(
			"main.js",
			"function test(require) { return require('./fake.js'); }\nrequire('./real.js');");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, package, fake, real, source],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.DoesNotContain(result.Edges, edge => edge.Source == "main.js" && edge.Target == "fake.js");
		Assert.Equal("real.js", Assert.Single(result.Edges, edge =>
			edge.Source == "main.js" && edge.Reference == "./real.js").Target);
	}

	[Fact]
	public async Task New091_TupleElementNamesAreNotTypeReferences()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var customer = fixture.CreateFile("Customer.cs", "public sealed class Customer { }");
		var count = fixture.CreateFile("Count.cs", "public sealed class Count { }");
		var source = fixture.CreateFile(
			"Consumer.cs",
			"public sealed class Consumer { (int Customer, int Count) Position; }");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[project, customer, count, source],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.DoesNotContain(result.Edges, edge =>
			edge.Source == "Consumer.cs" && edge.Target is "Customer.cs" or "Count.cs");
		var facts = result.Files.Single(file => file.Path == "Consumer.cs");
		Assert.DoesNotContain(facts.References, reference => reference.Name is "Customer" or "Count");
	}

	[Fact]
	public async Task New090_UnknownQualifiedTypeIsNotExternalByItsLastName()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var source = fixture.CreateFile(
			"Consumer.cs",
			"public sealed class Consumer { Acme.Task Value; System.Threading.Tasks.Task Known; };");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[project, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var unknown = Assert.Single(result.Edges, edge =>
			edge.Source == "Consumer.cs" && edge.Reference == "Acme.Task");
		Assert.Equal(ResolutionStatus.Unresolved, unknown.Status);
		var known = Assert.Single(result.Edges, edge =>
			edge.Source == "Consumer.cs" && edge.Reference == "System.Threading.Tasks.Task");
		Assert.Equal(ResolutionStatus.External, known.Status);
	}

	[Fact]
	public async Task New058_JsxSpecifierProbesTsxSource()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\",\"jsx\":\"react-jsx\"}}");
		var view = fixture.CreateFile("View.tsx", "export default function View() { return null; }");
		var source = fixture.CreateFile("main.ts", "import View from './View.jsx';");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, view, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, candidate =>
			candidate.Source == "main.ts" && candidate.Reference == "./View.jsx");
		Assert.Equal(ResolutionStatus.Resolved, edge.Status);
		Assert.Equal("View.tsx", edge.Target);
	}

	[Fact]
	public async Task New056_GlobalNamespaceTypePrecedesImportedTypeAtTopLevel()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var global = fixture.CreateFile("Global.cs", "public sealed class Widget { }");
		var imported = fixture.CreateFile("Imported.cs", "namespace Other; public sealed class Widget { }");
		var source = fixture.CreateFile(
			"Consumer.cs",
			"using Other; public sealed class Consumer { Widget Value; }");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[project, global, imported, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, candidate =>
			candidate.Source == "Consumer.cs" && candidate.Reference == "Widget");
		Assert.Equal(ResolutionStatus.Resolved, edge.Status);
		Assert.Equal("Global.cs", edge.Target);
	}

	[Fact]
	public async Task New055_AttributeShortNameFallbackRunsAfterVisibilityFiltering()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var hidden = fixture.CreateFile("Hidden.cs", "namespace Hidden; public sealed class Audit { }");
		var attribute = fixture.CreateFile("AuditAttribute.cs", "public sealed class AuditAttribute : System.Attribute { }");
		var source = fixture.CreateFile("Consumer.cs", "[Audit] public sealed class Consumer { }");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[project, hidden, attribute, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, candidate =>
			candidate.Source == "Consumer.cs" && candidate.Reference == "Audit");
		Assert.Equal(ResolutionStatus.Resolved, edge.Status);
		Assert.Equal("AuditAttribute.cs", edge.Target);
	}

	[Fact]
	public async Task New054_ExactPackageExportDoesNotProbeDirectoryIndex()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var package = fixture.CreateFile(
			"package.json",
			"{\"name\":\"fixture\",\"exports\":{\"./sub\":\"./dir\"}}");
		var index = fixture.CreateFile("dir/index.ts", "export default 1;");
		var source = fixture.CreateFile("main.ts", "import value from 'fixture/sub';");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, package, index, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, candidate =>
			candidate.Source == "main.ts" && candidate.Reference == "fixture/sub");
		Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
		Assert.Null(edge.Target);
	}

	[Fact]
	public async Task New053_DynamicImportInCommonJsRequiresRelativeExtension()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"nodenext\"}}");
		var target = fixture.CreateFile("target.ts", "export default 1;");
		var source = fixture.CreateFile(
			"main.cts",
			"import('./target');\nimport('./target.js');");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, target, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var extensionless = Assert.Single(result.Edges, edge =>
			edge.Source == "main.cts" && edge.Reference == "./target");
		Assert.Equal(ResolutionStatus.Unresolved, extensionless.Status);
		Assert.Contains("extension required", Assert.Single(extensionless.Reasons), StringComparison.Ordinal);
		Assert.Equal("target.ts", Assert.Single(result.Edges, edge =>
			edge.Source == "main.cts" && edge.Reference == "./target.js").Target);
	}

	[Fact]
	public async Task New021_NodeConditionIsActiveForNodeNextButNotBundler()
	{
		using var fixture = new TemporaryDirectory();
		var bundlerConfig = fixture.CreateFile(
			"bundler/tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var bundlerPackage = fixture.CreateFile(
			"bundler/package.json",
			"{\"imports\":{\"#value\":{\"node\":\"./node.ts\",\"default\":\"./default.ts\"}}}");
		var bundlerNode = fixture.CreateFile("bundler/node.ts", "export default 1;");
		var bundlerDefault = fixture.CreateFile("bundler/default.ts", "export default 2;");
		var bundlerSource = fixture.CreateFile("bundler/main.ts", "import value from '#value';");
		var nodeConfig = fixture.CreateFile(
			"node/tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"nodenext\"}}");
		var nodePackage = fixture.CreateFile(
			"node/package.json",
			"{\"type\":\"module\",\"imports\":{\"#value\":{\"node\":\"./node.ts\",\"default\":\"./default.ts\"}}}");
		var nodeTarget = fixture.CreateFile("node/node.ts", "export default 1;");
		var nodeDefault = fixture.CreateFile("node/default.ts", "export default 2;");
		var nodeSource = fixture.CreateFile("node/main.ts", "import value from '#value';");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[
				bundlerConfig, bundlerPackage, bundlerNode, bundlerDefault, bundlerSource,
				nodeConfig, nodePackage, nodeTarget, nodeDefault, nodeSource
			],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal("bundler/default.ts", Assert.Single(result.Edges, edge =>
			edge.Source == "bundler/main.ts" && edge.Reference == "#value").Target);
		Assert.Equal("node/node.ts", Assert.Single(result.Edges, edge =>
			edge.Source == "node/main.ts" && edge.Reference == "#value").Target);
	}

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
