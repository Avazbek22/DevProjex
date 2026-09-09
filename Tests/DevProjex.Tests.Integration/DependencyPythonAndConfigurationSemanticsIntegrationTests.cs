using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Compression;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class DependencyPythonAndConfigurationSemanticsIntegrationTests
{
	[Fact]
	public async Task DottedImportsBindTheTopLevelNameUnlessAliased()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var package = fixture.CreateFile("pkg/__init__.py", string.Empty);
		var target = fixture.CreateFile("pkg/sub.py", "VALUE = 1");
		var facade = fixture.CreateFile("facade.py", "import pkg.sub\nimport pkg.sub as alias");
		var consumer = fixture.CreateFile("consumer.py", "from facade import pkg\nfrom facade import alias");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, package, target, facade, consumer],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Edges, edge => edge.Source == "consumer.py" && edge.Reference == "facade" &&
			edge.Target == "pkg/sub.py" && edge.Status == ResolutionStatus.Resolved && edge.Evidence.Count == 2);
	}

	[Fact]
	public async Task ReExportDoesNotTreatAnOrdinaryModuleAsAPackage()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var ordinaryModule = fixture.CreateFile("module.py", "VALUE = 1");
		var falseChild = fixture.CreateFile("module/Name.py", "class Name: pass");
		var facade = fixture.CreateFile("facade.py", "from module import Name");
		var consumer = fixture.CreateFile("consumer.py", "from facade import Name");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, ordinaryModule, falseChild, facade, consumer],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, item =>
			item.Source == "consumer.py" && item.Reference == "facade");
		Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
		Assert.Null(edge.Target);
		Assert.DoesNotContain("module/Name.py", edge.Candidates);
	}

	[Fact]
	public async Task PyProjectDependenciesUseTomlArrayBoundaries()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"pyproject.toml",
			"""
			[project]
			name = "fixture"
			dependencies = [
			  "requests[socks]",
			  "quoted-bracket; python_version == ']'",
			  "pydantic",
			]
			""");
		var source = fixture.CreateFile("consumer.py", "import pydantic");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, item => item.Source == "consumer.py");
		Assert.Equal(ResolutionStatus.External, edge.Status);
		Assert.Equal("declared Python package outside the manifest", Assert.Single(edge.Reasons));
	}

	[Theory]
	[InlineData("[project]\nname = \"unterminated")]
	[InlineData("[project]\ndependencies = [\n  \"requests\",")]
	public async Task InvalidPyProjectTomlIsCorrupt(string content)
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", content);

		var result = await new FileDependencyConfigurationProvider().ReadAsync(
			fixture.Path,
			[config],
			TestContext.Current.CancellationToken);

		var diagnostic = Assert.Single(result.ConfigurationDiagnostics);
		Assert.Equal(DependencyConfigurationState.Corrupt, diagnostic.State);
		Assert.Equal("invalid pyproject TOML", diagnostic.Reason);
		var scope = Assert.Single(result.Scopes, item => item.HasConfiguration);
		Assert.Equal(DependencyConfigurationState.Corrupt, scope.ConfigurationState);
		Assert.Empty(scope.PythonExternalPackages);
		Assert.Null(scope.PythonVersion);
	}

	[Fact]
	public async Task NamedDirectUrlDependencyRetainsItsDistributionName()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"pyproject.toml",
			"[project]\nname = \"fixture\"\ndependencies = [\"company-sdk @ https://packages.invalid/sdk.whl\"]");
		var source = fixture.CreateFile("consumer.py", "import company_sdk");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, item => item.Source == "consumer.py");
		Assert.Equal(ResolutionStatus.External, edge.Status);
		Assert.Equal("declared Python package outside the manifest", Assert.Single(edge.Reasons));
	}

	[Fact]
	public async Task BareDirectUrlDoesNotInventAPackageName()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile(
			"pyproject.toml",
			"[project]\nname = \"fixture\"\ndependencies = [\"https://packages.invalid/sdk.whl\"]");

		var result = await new FileDependencyConfigurationProvider().ReadAsync(
			fixture.Path,
			[config],
			TestContext.Current.CancellationToken);

		Assert.Empty(Assert.Single(result.Scopes, item => item.HasConfiguration).PythonExternalPackages);
	}

	[Fact]
	public async Task NestedPyProjectDoesNotChangeUnownedRootFiles()
	{
		using var fixture = new TemporaryDirectory();
		var module = fixture.CreateFile("module.py", "VALUE = 1");
		var source = fixture.CreateFile("consumer.py", "import module");
		var nestedConfig = fixture.CreateFile("tools/pyproject.toml", "[project]\nname = \"tools\"");

		using var beforeEngine = CreateEngine();
		var before = await beforeEngine.IndexAsync(
			fixture.Path,
			[module, source],
			cancellationToken: TestContext.Current.CancellationToken);
		using var afterEngine = CreateEngine();
		var after = await afterEngine.IndexAsync(
			fixture.Path,
			[module, source, nestedConfig],
			cancellationToken: TestContext.Current.CancellationToken);

		var beforeEdge = Assert.Single(before.Edges, item => item.Source == "consumer.py");
		var afterEdge = Assert.Single(after.Edges, item => item.Source == "consumer.py");
		Assert.Equal(beforeEdge.Status, afterEdge.Status);
		Assert.Equal(beforeEdge.Target, afterEdge.Target);
		Assert.Equal(beforeEdge.Reasons, afterEdge.Reasons);
	}

	[Fact]
	public async Task ModuleFileBlocksDottedChildResolution()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var parentModule = fixture.CreateFile("pkg.py", "VALUE = 1");
		var falseChild = fixture.CreateFile("pkg/sub/child.py", "VALUE = 2");
		var source = fixture.CreateFile("consumer.py", "import pkg.sub");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, parentModule, falseChild, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, item => item.Source == "consumer.py");
		Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
		Assert.Null(edge.Target);
	}

	[Fact]
	public async Task RegularPackageBlocksChildrenFromAnotherRoot()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var package = fixture.CreateFile("pkg/__init__.py", string.Empty);
		var falseChild = fixture.CreateFile("src/pkg/sub.py", "VALUE = 1");
		var source = fixture.CreateFile("consumer.py", "import pkg.sub");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, package, falseChild, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, item => item.Source == "consumer.py");
		Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
		Assert.Null(edge.Target);
	}

	[Fact]
	public async Task LastUnconditionalReExportBindingWins()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var old = fixture.CreateFile("old.py", "class Model: pass");
		var current = fixture.CreateFile("current.py", "class Model: pass");
		var facade = fixture.CreateFile("facade.py", "from old import Model\nfrom current import Model");
		var consumer = fixture.CreateFile("consumer.py", "from facade import Model");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, old, current, facade, consumer],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, item =>
			item.Source == "consumer.py" && item.Reference == "facade");
		Assert.Equal(ResolutionStatus.Resolved, edge.Status);
		Assert.Equal("current.py", edge.Target);
	}

	[Fact]
	public async Task ConditionalReExportDoesNotClaimOneTarget()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var old = fixture.CreateFile("old.py", "class Model: pass");
		var current = fixture.CreateFile("current.py", "class Model: pass");
		var facade = fixture.CreateFile(
			"facade.py",
			"from old import Model\nif enabled:\n    from current import Model");
		var consumer = fixture.CreateFile("consumer.py", "from facade import Model");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, old, current, facade, consumer],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, item =>
			item.Source == "consumer.py" && item.Reference == "facade");
		Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
		Assert.Null(edge.Target);
		Assert.Equal("conditional Python re-export bindings are not indexed", Assert.Single(edge.Reasons));
	}

	[Fact]
	public async Task DocstringAndLocalAllAssignmentsDoNotSetModulePolicy()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var module = fixture.CreateFile(
			"module.py",
			"""
			'''Example:
			__all__ = build_names()
			'''
			def configure():
			    __all__ = build_names()
			class Item: pass
			""");
		var source = fixture.CreateFile("consumer.py", "from module import *");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, module, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, item => item.Source == "consumer.py");
		Assert.Equal(ResolutionStatus.Resolved, edge.Status);
		Assert.Equal("module.py", edge.Target);
	}

	[Fact]
	public async Task ModuleLevelDynamicAllRemainsExplicitlyUnresolved()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var module = fixture.CreateFile("module.py", "__all__ = build_names()\nclass Item: pass");
		var source = fixture.CreateFile("consumer.py", "from module import *");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, module, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var moduleFacts = Assert.Single(result.Files, item => item.Path == "module.py");
		Assert.True(moduleFacts.Aliases.ContainsKey("$dynamic-all"),
			string.Join(", ", moduleFacts.Aliases.Keys));
		var edge = Assert.Single(result.Edges, item => item.Source == "consumer.py");
		Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
		Assert.Equal("dynamic __all__ is an unsupported mechanism", Assert.Single(edge.Reasons));
	}

	[Fact]
	public async Task ModuleAssignmentsHaveAnExplicitResolutionLimitation()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var settings = fixture.CreateFile("settings.py", "API_VERSION = \"1\"\nDEFAULTS = dict()");
		var source = fixture.CreateFile("consumer.py", "from settings import API_VERSION\nfrom settings import DEFAULTS");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, settings, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, item => item.Source == "consumer.py");
		Assert.Equal(2, edge.Evidence.Count);
		Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
		Assert.Equal("Python module assignment bindings are not indexed", Assert.Single(edge.Reasons));
	}

	[Fact]
	public async Task WildcardReExportHasAnExplicitResolutionLimitation()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var model = fixture.CreateFile("model.py", "__all__ = [\"Item\"]\nclass Item: pass");
		var facade = fixture.CreateFile("facade.py", "from model import *");
		var source = fixture.CreateFile("consumer.py", "from facade import Item");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, model, facade, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, item =>
			item.Source == "consumer.py" && item.Reference == "facade");
		Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
		Assert.Equal("Python wildcard re-export bindings are not indexed", Assert.Single(edge.Reasons));
	}

	[Fact]
	public async Task EqualRootConfigurationsFailClosedAsAmbiguousOwnership()
	{
		using var fixture = new TemporaryDirectory();
		var pyproject = fixture.CreateFile(
			"pyproject.toml",
			"[project]\nname = \"fixture\"\ndependencies = [\"alpha\"]");
		var setup = fixture.CreateFile("setup.cfg", "[options]\ninstall_requires =\n  beta");
		var source = fixture.CreateFile("consumer.py", "import alpha");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[pyproject, setup, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, item => item.Source == "consumer.py");
		Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
		Assert.Equal("multiple owning dependency configurations", Assert.Single(edge.Reasons));
		Assert.Equal(2, result.Coverage.ConfigurationDiagnostics.Count(item =>
			item.Reason == "multiple owning dependency configurations"));
	}

	[Fact]
	public async Task StaticCompileItemsFailClosedWithoutEvaluatingMsBuild()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile(
			"Fixture.csproj",
			"<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Remove=\"Removed.cs\" /></ItemGroup></Project>");
		var kept = fixture.CreateFile("Kept.cs", "namespace Models; public sealed class Item { }");
		var removed = fixture.CreateFile("Removed.cs", "namespace Models; public sealed class Item { }");
		var consumer = fixture.CreateFile("Consumer.cs", "using Models; public sealed class Consumer { Item Value; }");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[project, kept, removed, consumer],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, item =>
			item.Source == "Consumer.cs" && item.Reference == "Item");
		Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
		Assert.Equal("C# Compile item membership is not supported", Assert.Single(edge.Reasons));
	}

	[Fact]
	public async Task FilteredOwningConfigurationHasAnExplicitMissingReason()
	{
		using var fixture = new TemporaryDirectory();
		fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var target = fixture.CreateFile("Target.cs", "namespace Models; public sealed class Item { }");
		var source = fixture.CreateFile("Consumer.cs", "using Models; public sealed class Consumer { Item Value; }");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[target, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, item => item.Source == "Consumer.cs");
		Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
		Assert.Equal("no owning .csproj in the manifest", Assert.Single(edge.Reasons));
	}

	[Fact]
	public async Task DisableTransitiveProjectReferencesLimitsVisibilityToDirectProjects()
	{
		using var fixture = new TemporaryDirectory();
		var projectA = fixture.CreateFile(
			"A/A.csproj",
			"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><DisableTransitiveProjectReferences>true</DisableTransitiveProjectReferences></PropertyGroup><ItemGroup><ProjectReference Include=\"../B/B.csproj\" /></ItemGroup></Project>");
		var projectB = fixture.CreateFile(
			"B/B.csproj",
			"<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../C/C.csproj\" /></ItemGroup></Project>");
		var projectC = fixture.CreateFile("C/C.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var middle = fixture.CreateFile("B/Middle.cs", "namespace BScope; public sealed class Middle { }");
		var target = fixture.CreateFile("C/Target.cs", "namespace CScope; public sealed class Target { }");
		var source = fixture.CreateFile(
			"A/Consumer.cs",
			"using BScope; using CScope; public sealed class Consumer { Middle Direct; Target Transitive; }");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[projectA, projectB, projectC, middle, target, source],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Edges, edge => edge.Source == "A/Consumer.cs" && edge.Reference == "Middle" &&
			edge.Status == ResolutionStatus.Resolved && edge.Target == "B/Middle.cs");
		Assert.Contains(result.Edges, edge => edge.Source == "A/Consumer.cs" && edge.Reference == "Target" &&
			edge.Status == ResolutionStatus.Unresolved && edge.Target is null);
	}

	[Theory]
	[InlineData("false", true, 0)]
	[InlineData("conditional", true, 1)]
	public async Task DisableTransitiveProjectReferencesLiteralPolicyIsDeterministic(
		string value,
		bool targetIsVisible,
		int expectedDiagnostics)
	{
		using var fixture = new TemporaryDirectory();
		var projectA = fixture.CreateFile(
			"A/A.csproj",
			$"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><DisableTransitiveProjectReferences>{value}</DisableTransitiveProjectReferences></PropertyGroup><ItemGroup><ProjectReference Include=\"../B/B.csproj\" /></ItemGroup></Project>");
		var projectB = fixture.CreateFile(
			"B/B.csproj",
			"<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../C/C.csproj\" /></ItemGroup></Project>");
		var projectC = fixture.CreateFile("C/C.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var target = fixture.CreateFile("C/Target.cs", "namespace CScope; public sealed class Target { }");
		var source = fixture.CreateFile("A/Consumer.cs", "using CScope; public sealed class Consumer { Target Value; }");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[projectA, projectB, projectC, target, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, item =>
			item.Source == "A/Consumer.cs" && item.Reference == "Target");
		Assert.Equal(targetIsVisible ? ResolutionStatus.Resolved : ResolutionStatus.Unresolved, edge.Status);
		Assert.Equal(expectedDiagnostics, result.Coverage.ConfigurationDiagnostics.Count(item =>
			item.Reason == "DisableTransitiveProjectReferences could not be evaluated safely"));
	}

	[Fact]
	public async Task TransientGrammarResolutionFailureIsRetried()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var source = fixture.CreateFile("module.py", "class Item: pass");
		using var locator = new FailOnceGrammarLibraryLocator(CodeCompressionFactory.CreateLocator());
		using var engine = new DependencyFactsEngine(
			new TreeSitterDependencyFactExtractor(locator),
			new FileDependencyConfigurationProvider());

		var first = await engine.IndexAsync(
			fixture.Path,
			[config, source],
			cancellationToken: TestContext.Current.CancellationToken);
		fixture.CreateFile("module.py", "class Item: pass\nclass Other: pass");
		var second = await engine.IndexAsync(
			fixture.Path,
			[config, source],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(DependencyFileStatus.ExtractionFailed,
			Assert.Single(first.Files, item => item.Path == "module.py").Status);
		Assert.Equal(DependencyFileStatus.Supported,
			Assert.Single(second.Files, item => item.Path == "module.py").Status);
		Assert.Equal(2, locator.ResolveCalls);
	}

	private static DependencyFactsEngine CreateEngine() => new(
		new TreeSitterDependencyFactExtractor(),
		new FileDependencyConfigurationProvider());

	private sealed class FailOnceGrammarLibraryLocator(IGrammarLibraryLocator inner)
		: IGrammarLibraryLocator, IDisposable
	{
		private int _resolveCalls;

		public int ResolveCalls => Volatile.Read(ref _resolveCalls);
		public string StrategyName => inner.StrategyName;
		public IReadOnlyList<string> EnumerateLibraries() => inner.EnumerateLibraries();

		public string Resolve(string libraryBaseName)
		{
			if (Interlocked.Increment(ref _resolveCalls) == 1)
				throw new IOException("transient grammar lookup failure");
			return inner.Resolve(libraryBaseName);
		}

		public void Dispose()
		{
			if (inner is IDisposable disposable)
				disposable.Dispose();
		}
	}
}
