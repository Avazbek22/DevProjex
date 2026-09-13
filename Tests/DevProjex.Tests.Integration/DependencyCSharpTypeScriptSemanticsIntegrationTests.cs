using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class DependencyCSharpTypeScriptSemanticsIntegrationTests
{
	[Fact]
	public async Task UnknownCrossProjectAccessibilityRemainsAmbiguous()
	{
		using var fixture = new TemporaryDirectory();
		var projectA = fixture.CreateFile(
			"A/A.csproj",
			"<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../B/B.csproj\" /><ProjectReference Include=\"../C/C.csproj\" /></ItemGroup></Project>");
		var projectB = fixture.CreateFile("B/B.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var projectC = fixture.CreateFile("C/C.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var publicType = fixture.CreateFile("B/Widget.cs", "namespace Shared; public sealed class Widget { }");
		var internalType = fixture.CreateFile("C/Widget.cs", "namespace Shared; internal sealed class Widget { }");
		var source = fixture.CreateFile("A/Consumer.cs", "using Shared; public sealed class Consumer { Widget Value; }");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[projectA, projectB, projectC, publicType, internalType, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, candidate =>
			candidate.Source == "A/Consumer.cs" && candidate.Reference == "Widget");
		Assert.Equal(ResolutionStatus.Ambiguous, edge.Status);
		Assert.Null(edge.Target);
		Assert.Equal(["B/Widget.cs", "C/Widget.cs"], edge.Candidates);
	}

	[Fact]
	public async Task OnlyPreprocessorDependentReferencesAreHonestlyUnresolved()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var before = fixture.CreateFile("BeforeValue.cs", "public sealed class BeforeValue { }");
		var debug = fixture.CreateFile("DebugValue.cs", "public sealed class DebugValue { }");
		var staging = fixture.CreateFile("StagingValue.cs", "public sealed class StagingValue { }");
		var release = fixture.CreateFile("ReleaseValue.cs", "public sealed class ReleaseValue { }");
		var after = fixture.CreateFile("AfterValue.cs", "public sealed class AfterValue { }");
		var source = fixture.CreateFile(
			"Consumer.cs",
			"""
			public sealed class Consumer
			{
			    // Не-ASCII text keeps the byte-offset contract covered.
			    BeforeValue Before;
			#if DEBUG
			    DebugValue Value;
			#elif STAGING
			    StagingValue Value;
			#else
			    ReleaseValue Value;
			#endif
			    AfterValue After;
			}
			""");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[project, before, debug, staging, release, after, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var conditional = result.Edges.Where(edge =>
			edge.Source == "Consumer.cs" &&
			edge.Reference is "DebugValue" or "StagingValue" or "ReleaseValue").ToArray();
		Assert.Equal(3, conditional.Length);
		Assert.All(conditional, edge =>
		{
			Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
			Assert.Null(edge.Target);
			Assert.Equal("C# preprocessor configuration is not available", Assert.Single(edge.Reasons));
		});
		Assert.Contains(result.Edges, edge =>
			edge.Source == "Consumer.cs" && edge.Reference == "BeforeValue" &&
			edge.Status == ResolutionStatus.Resolved && edge.Target == "BeforeValue.cs");
		Assert.Contains(result.Edges, edge =>
			edge.Source == "Consumer.cs" && edge.Reference == "AfterValue" &&
			edge.Status == ResolutionStatus.Resolved && edge.Target == "AfterValue.cs");
	}

	[Fact]
	public async Task NestedConditionalRegionsRemainBoundedByTheOuterDirective()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var outer = fixture.CreateFile("OuterValue.cs", "public sealed class OuterValue { }");
		var inner = fixture.CreateFile("InnerValue.cs", "public sealed class InnerValue { }");
		var outside = fixture.CreateFile("OutsideValue.cs", "public sealed class OutsideValue { }");
		var source = fixture.CreateFile(
			"Consumer.cs",
			"""
			public sealed class Consumer
			{
			#if OUTER
			    OuterValue Outer;
			#if INNER
			    InnerValue Inner;
			#endif
			#endif
			    OutsideValue Outside;
			}
			""");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[project, outer, inner, outside, source],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.All(result.Edges.Where(edge =>
			edge.Source == "Consumer.cs" && edge.Reference is "OuterValue" or "InnerValue"), edge =>
		{
			Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
			Assert.Equal("C# preprocessor configuration is not available", Assert.Single(edge.Reasons));
		});
		Assert.Contains(result.Edges, edge =>
			edge.Source == "Consumer.cs" && edge.Reference == "OutsideValue" &&
			edge.Status == ResolutionStatus.Resolved && edge.Target == "OutsideValue.cs");
	}

	[Fact]
	public async Task UnterminatedConditionalRegionDropsFactsFromTheDamagedContainingType()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var before = fixture.CreateFile("BeforeValue.cs", "public sealed class BeforeValue { }");
		var tail = fixture.CreateFile("TailValue.cs", "public sealed class TailValue { }");
		var source = fixture.CreateFile(
			"Consumer.cs",
			"""
			public sealed class Consumer
			{
			    BeforeValue Before;
			#if DEBUG
			    TailValue Tail;
			}
			""");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[project, before, tail, source],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.DoesNotContain(result.Edges, edge => edge.Source == "Consumer.cs");
		Assert.Contains(result.Coverage.PartialParseDiagnostics,
			diagnostic => diagnostic.Path == "Consumer.cs" && diagnostic.DroppedConstructs > 0);
	}

	[Fact]
	public async Task RegionAndNullableDirectivesAreNotConditionalRegions()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var region = fixture.CreateFile("RegionValue.cs", "public sealed class RegionValue { }");
		var nullable = fixture.CreateFile("NullableValue.cs", "public sealed class NullableValue { }");
		var source = fixture.CreateFile(
			"Consumer.cs",
			"""
			#nullable enable
			public sealed class Consumer
			{
			#region Values
			    RegionValue Region;
			#endregion
			    NullableValue? Nullable;
			}
			#nullable restore
			""");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[project, region, nullable, source],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Edges, edge =>
			edge.Source == "Consumer.cs" && edge.Reference == "RegionValue" &&
			edge.Status == ResolutionStatus.Resolved && edge.Target == "RegionValue.cs");
		Assert.Contains(result.Edges, edge =>
			edge.Source == "Consumer.cs" && edge.Reference == "NullableValue" &&
			edge.Status == ResolutionStatus.Resolved && edge.Target == "NullableValue.cs");
	}

	[Fact]
	public async Task NestedGenericSegmentIsHonestlyUnresolved()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var plain = fixture.CreateFile("Plain.cs", "public class Outer { public class Inner { } }");
		var generic = fixture.CreateFile("Generic.cs", "public class Outer<T> { public class Inner { } }");
		var source = fixture.CreateFile("Consumer.cs", "public class Consumer { Outer<int>.Inner Value; }");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[project, plain, generic, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var nested = Assert.Single(result.Edges, edge =>
			edge.Source == "Consumer.cs" && edge.Reference == "Inner");
		Assert.Equal(ResolutionStatus.Unresolved, nested.Status);
		Assert.Empty(nested.Candidates);
		Assert.Equal("nested generic type resolution is not supported", Assert.Single(nested.Reasons));
		Assert.Contains(result.Edges, edge => edge.Source == "Consumer.cs" && edge.Target == "Generic.cs");
		Assert.DoesNotContain(result.Edges, edge => edge.Source == "Consumer.cs" && edge.Target == "Plain.cs");
	}

	[Fact]
	public async Task UsingStaticExposesNestedTypes()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var holder = fixture.CreateFile(
			"Holder.cs",
			"namespace Company; public static class Holder { public sealed class Nested { } }");
		var source = fixture.CreateFile(
			"Consumer.cs",
			"using static Company.Holder; public sealed class Consumer { Nested Value; }");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[project, holder, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, candidate =>
			candidate.Source == "Consumer.cs" && candidate.Reference == "Nested");
		Assert.Equal(ResolutionStatus.Resolved, edge.Status);
		Assert.Equal("Holder.cs", edge.Target);
	}

	[Fact]
	public async Task ExportsFallbackArrayIsHonestlyUnresolved()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var package = fixture.CreateFile(
			"package.json",
			"{\"name\":\"fixture\",\"exports\":{\".\":[\"./missing.js\",\"./value.js\"]}}");
		var target = fixture.CreateFile("value.ts", "export default 1;");
		var source = fixture.CreateFile("main.ts", "import value from 'fixture';");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, package, target, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, candidate =>
			candidate.Source == "main.ts" && candidate.Reference == "fixture");
		Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
		Assert.Null(edge.Target);
		Assert.Equal("package target kind is not supported", Assert.Single(edge.Reasons));
	}

	[Fact]
	public async Task DirectoryPackageMetadataDoesNotFallBackToIndex()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var package = fixture.CreateFile("feature/package.json", "{\"main\":\"./entry.js\"}");
		var entry = fixture.CreateFile("feature/entry.ts", "export default 1;");
		var decoy = fixture.CreateFile("feature/index.ts", "export default 2;");
		var source = fixture.CreateFile("main.ts", "import value from './feature';");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, package, entry, decoy, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, candidate =>
			candidate.Source == "main.ts" && candidate.Reference == "./feature");
		Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
		Assert.Null(edge.Target);
	}

	[Fact]
	public async Task ExcludedPrimaryPathMappingDoesNotSelectFallback()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\",\"paths\":{\"alias\":[\"primary/value\",\"fallback/value\"]}}}");
		_ = fixture.CreateFile("primary/value.ts", "export default 1;");
		var fallback = fixture.CreateFile("fallback/value.ts", "export default 2;");
		var source = fixture.CreateFile("main.ts", "import value from 'alias';");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, fallback, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, candidate =>
			candidate.Source == "main.ts" && candidate.Reference == "alias");
		Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
		Assert.Null(edge.Target);
		Assert.DoesNotContain("fallback/value.ts", edge.Candidates);
	}

	[Fact]
	public async Task CustomConditionsFailClosedOnlyForConditionalPackageMaps()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"tsconfig.json",
			"{\"extends\":\"./base.json\",\"compilerOptions\":{\"moduleResolution\":\"bundler\",\"paths\":{\"mapped\":[\"mapped/value\"]}}}");
		var baseConfig = fixture.CreateFile(
			"base.json",
			"{\"compilerOptions\":{\"customConditions\":[\"browser\"]}}");
		var package = fixture.CreateFile(
			"package.json",
			"{\"name\":\"fixture\",\"imports\":{\"#value\":{\"browser\":\"./browser.ts\",\"default\":\"./default.ts\"}},\"exports\":{\"./value\":{\"browser\":\"./browser.ts\",\"default\":\"./default.ts\"},\"./direct\":\"./direct.ts\"}}");
		var browser = fixture.CreateFile("browser.ts", "export default 1;");
		var fallback = fixture.CreateFile("default.ts", "export default 2;");
		var direct = fixture.CreateFile("direct.ts", "export default 5;");
		var relative = fixture.CreateFile("relative.ts", "export default 3;");
		var mapped = fixture.CreateFile("mapped/value.ts", "export default 4;");
		var source = fixture.CreateFile(
			"main.ts",
			"import internal from '#value';\nimport value from 'fixture/value';\nimport direct from 'fixture/direct';\nimport relative from './relative';\nimport mapped from 'mapped';");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, baseConfig, package, browser, fallback, direct, relative, mapped, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, candidate =>
			candidate.Source == "main.ts" && candidate.Reference == "fixture/value");
		Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
		Assert.Null(edge.Target);
		Assert.Equal("tsconfig customConditions are not supported", Assert.Single(edge.Reasons));
		var internalEdge = Assert.Single(result.Edges, candidate =>
			candidate.Source == "main.ts" && candidate.Reference == "#value");
		Assert.Equal(ResolutionStatus.Unresolved, internalEdge.Status);
		Assert.Equal("tsconfig customConditions are not supported", Assert.Single(internalEdge.Reasons));
		Assert.Empty(result.Coverage.ConfigurationDiagnostics);
		Assert.Contains(result.Edges, candidate =>
			candidate.Source == "main.ts" && candidate.Reference == "fixture/direct" &&
			candidate.Status == ResolutionStatus.Resolved && candidate.Target == "direct.ts");
		Assert.Contains(result.Edges, candidate =>
			candidate.Source == "main.ts" && candidate.Reference == "./relative" &&
			candidate.Status == ResolutionStatus.Resolved && candidate.Target == "relative.ts");
		Assert.Contains(result.Edges, candidate =>
			candidate.Source == "main.ts" && candidate.Reference == "mapped" &&
			candidate.Status == ResolutionStatus.Resolved && candidate.Target == "mapped/value.ts");
	}

	[Fact]
	public async Task BarePackageImportTargetIsNotProbedAsLocalPath()
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
	public async Task ModuleNodeNextInfersNodeNextResolution()
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
	public async Task RelativeModuleUrlSuffixUsesPhysicalPath()
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
	public async Task OrdinaryCSharpTypePositionsProduceReferences()
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
	public async Task RelativeAliasTargetFallsBackToLexicalNamespace()
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
	public async Task PackageExportsRejectTargetsOutsidePackage()
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
	public async Task RequireParameterDoesNotCreateModuleImport()
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
	public async Task TupleElementNamesAreNotTypeReferences()
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
	public async Task UnknownQualifiedTypeIsNotExternalByItsLastName()
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
	public async Task JsxSpecifierProbesTsxSource()
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
	public async Task TsxSourceUsesTheOwningTypeScriptConfigurationForRelativeImports()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"app/tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\",\"jsx\":\"react\"},\"include\":[\"./src\"]}");
		var widget = fixture.CreateFile("app/src/widget.tsx", "export default function Widget() { return <span />; }");
		var source = fixture.CreateFile("app/src/main.tsx", "import Widget from './widget';\nexport const app = <Widget />;");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, widget, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, candidate =>
			candidate.Source == "app/src/main.tsx" && candidate.Reference == "./widget");
		Assert.True(edge.Status == ResolutionStatus.Resolved, string.Join(" | ", edge.Reasons));
		Assert.Equal("app/src/widget.tsx", edge.Target);
	}

	[Fact]
	public async Task GlobalNamespaceTypePrecedesImportedTypeAtTopLevel()
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
	public async Task AttributeShortNameFallbackRunsAfterVisibilityFiltering()
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
	public async Task ExactPackageExportDoesNotProbeDirectoryIndex()
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
	public async Task DynamicImportInCommonJsRequiresRelativeExtension()
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
	public async Task NodeConditionIsActiveForNodeNextButNotBundler()
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
	public async Task InactiveUnknownPackageConditionDoesNotBlockDefault()
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
	public async Task PackageWildcardPrefersLongestStaticPrefix()
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
	public async Task MultilineGenericReferenceUsesTokenCoordinates()
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
	public async Task NonAsciiIdentifierStartIsExtractedAsTypeReference()
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
	public async Task GenericUsingAliasPreservesContainerAndArgumentReferences()
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
	public async Task QualifiedNameUsesLexicalNamespaceBeforeGlobalNamespace()
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
	public async Task NestedBlockNamespacesComposeTheirQualifiedName()
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
	public async Task TupleCommasDoNotIncreaseConstructedGenericArity()
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
	public async Task DynamicImportAttributesPreserveEveryImportFact()
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
