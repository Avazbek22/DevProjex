using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class DependencyPythonModuleOnlyImportIntegrationTests
{
	private const string ModuleOnlyReason = "imported name not indexed";
	private const string ModuleTargetReason = "one module target";

	[Fact]
	public async Task ModuleConstantImportRelatesBothFilesWithoutClaimingADeclaration()
	{
		using var fixture = new TemporaryDirectory();
		var manifest = new[]
		{
			fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\""),
			fixture.CreateFile("pkg/__init__.py", string.Empty),
			fixture.CreateFile("pkg/_config.py", "class Timeout: pass\nDEFAULT_TIMEOUT_CONFIG = Timeout()"),
			fixture.CreateFile("pkg/_api.py", "from ._config import DEFAULT_TIMEOUT_CONFIG")
		};
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(
			fixture.Path,
			manifest,
			cancellationToken: TestContext.Current.CancellationToken);
		var dependencies = await engine.FindRelatedAsync(
			fixture.Path,
			manifest,
			["pkg/_api.py"],
			DependencyDirection.Dependencies,
			cancellationToken: TestContext.Current.CancellationToken);
		var dependents = await engine.FindRelatedAsync(
			fixture.Path,
			manifest,
			["pkg/_config.py"],
			DependencyDirection.Dependents,
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(index.Edges, item => item.Source == "pkg/_api.py");
		Assert.Equal(ResolutionStatus.Resolved, edge.Status);
		Assert.Equal("pkg/_config.py", edge.Target);
		Assert.Equal("._config", edge.Reference);
		Assert.True(edge.ModuleOnly);
		Assert.Empty(edge.DeclarationFiles);
		Assert.Equal([ModuleTargetReason, ModuleOnlyReason], edge.Reasons);
		var dependency = Assert.Single(Assert.Single(dependencies.Seeds).Dependencies);
		Assert.Equal("pkg/_config.py", dependency.Path);
		Assert.Equal(ResolutionStatus.Resolved, dependency.Status);
		Assert.Contains(ModuleOnlyReason, dependency.Reasons);
		Assert.Contains("import ._config at line 1", dependency.Reasons);
		Assert.Equal("pkg/_api.py", Assert.Single(Assert.Single(dependents.Seeds).Dependents).Path);
	}

	[Fact]
	public async Task DeclarationImportKeepsItsReasonAndStaysApartFromTheModuleOnlyGroup()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var package = fixture.CreateFile("pkg/__init__.py", string.Empty);
		var module = fixture.CreateFile("pkg/_config.py", "class Timeout: pass\ndef build(): pass\nDEFAULT = Timeout()");
		var declarations = fixture.CreateFile("pkg/declarations.py", "from ._config import Timeout\nfrom ._config import build");
		var mixed = fixture.CreateFile("pkg/mixed.py", "from ._config import Timeout, DEFAULT");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, package, module, declarations, mixed],
			cancellationToken: TestContext.Current.CancellationToken);

		var declarationEdge = Assert.Single(result.Edges, item => item.Source == "pkg/declarations.py");
		Assert.Equal(ResolutionStatus.Resolved, declarationEdge.Status);
		Assert.Equal("pkg/_config.py", declarationEdge.Target);
		Assert.False(declarationEdge.ModuleOnly);
		Assert.Equal(ModuleTargetReason, Assert.Single(declarationEdge.Reasons));
		var mixedEdges = result.Edges.Where(item => item.Source == "pkg/mixed.py").ToArray();
		Assert.Equal(2, mixedEdges.Length);
		Assert.All(mixedEdges, item =>
		{
			Assert.Equal(ResolutionStatus.Resolved, item.Status);
			Assert.Equal("pkg/_config.py", item.Target);
		});
		Assert.Equal(ModuleTargetReason, Assert.Single(Assert.Single(mixedEdges, item => !item.ModuleOnly).Reasons));
		Assert.Contains(ModuleOnlyReason, Assert.Single(mixedEdges, item => item.ModuleOnly).Reasons);
	}

	[Fact]
	public async Task SeveralUnindexedNamesFromOneStatementFormOneEdge()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var package = fixture.CreateFile("pkg/__init__.py", string.Empty);
		var module = fixture.CreateFile("pkg/constants.py", "FIRST = 1\nSECOND = 2\nTHIRD = 3");
		var inline = fixture.CreateFile("pkg/inline.py", "from .constants import FIRST, SECOND");
		var parenthesized = fixture.CreateFile("pkg/parenthesized.py", """
			from .constants import (
			    FIRST,
			    SECOND,
			    THIRD,
			)
			""");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, package, module, inline, parenthesized],
			cancellationToken: TestContext.Current.CancellationToken);

		foreach (var source in new[] { "pkg/inline.py", "pkg/parenthesized.py" })
		{
			var edge = Assert.Single(result.Edges, item => item.Source == source);
			Assert.Equal(ResolutionStatus.Resolved, edge.Status);
			Assert.Equal("pkg/constants.py", edge.Target);
			Assert.True(edge.ModuleOnly);
			Assert.Single(edge.Evidence);
		}
		Assert.Equal(3, result.Files.Single(file => file.Path == "pkg/parenthesized.py").Imports.Count);
	}

	[Fact]
	public async Task ParentRelativeAbsoluteAndPackageInitializerModulesAllRelateToTheirFile()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var initializer = fixture.CreateFile("pkg/__init__.py", "VERSION = \"1\"");
		var settings = fixture.CreateFile("pkg/settings.py", "LIMIT = 10");
		var subpackage = fixture.CreateFile("pkg/sub/__init__.py", string.Empty);
		var parent = fixture.CreateFile("pkg/sub/parent.py", "from ..settings import LIMIT");
		var absolute = fixture.CreateFile("absolute.py", "from pkg.settings import LIMIT");
		var package = fixture.CreateFile("package.py", "from pkg import VERSION");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, initializer, settings, subpackage, parent, absolute, package],
			cancellationToken: TestContext.Current.CancellationToken);

		AssertModuleOnly(result, "pkg/sub/parent.py", "..settings", "pkg/settings.py");
		AssertModuleOnly(result, "absolute.py", "pkg.settings", "pkg/settings.py");
		AssertModuleOnly(result, "package.py", "pkg", "pkg/__init__.py");
	}

	[Fact]
	public async Task AnnotatedModuleBindingRelatesToItsModuleButABareAnnotationDoesNot()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var module = fixture.CreateFile("globals.py", """
			current_app: AppProxy = LocalProxy(  # type: ignore[assignment]
			    _cv_app, "app"
			)
			pending: int
			""");
		var bound = fixture.CreateFile("bound.py", "from globals import current_app");
		var declaredOnly = fixture.CreateFile("declared_only.py", "from globals import pending");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, module, bound, declaredOnly],
			cancellationToken: TestContext.Current.CancellationToken);

		AssertModuleOnly(result, "bound.py", "globals", "globals.py");
		var unbound = Assert.Single(result.Edges, item => item.Source == "declared_only.py");
		Assert.Equal(ResolutionStatus.Unresolved, unbound.Status);
		Assert.False(unbound.ModuleOnly);
		Assert.Equal("name not found in module", Assert.Single(unbound.Reasons));
	}

	[Fact]
	public async Task SubmoduleWildcardAndStandardLibraryImportsKeepTheirResolution()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var package = fixture.CreateFile("pkg/__init__.py", string.Empty);
		var submodule = fixture.CreateFile("pkg/sub.py", "VALUE = 1");
		var submoduleConsumer = fixture.CreateFile("submodule_consumer.py", "from pkg import sub");
		var wildcardConsumer = fixture.CreateFile("wildcard_consumer.py", "from pkg.sub import *");
		var standardLibrary = fixture.CreateFile("standard_library.py", "from os import path");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, package, submodule, submoduleConsumer, wildcardConsumer, standardLibrary],
			cancellationToken: TestContext.Current.CancellationToken);

		var submoduleEdge = Assert.Single(result.Edges, item => item.Source == "submodule_consumer.py");
		Assert.Equal(ResolutionStatus.Resolved, submoduleEdge.Status);
		Assert.Equal("pkg/sub.py", submoduleEdge.Target);
		Assert.False(submoduleEdge.ModuleOnly);
		Assert.Equal(ModuleTargetReason, Assert.Single(submoduleEdge.Reasons));
		var wildcardEdge = Assert.Single(result.Edges, item => item.Source == "wildcard_consumer.py");
		Assert.Equal(ResolutionStatus.Resolved, wildcardEdge.Status);
		Assert.Equal("pkg/sub.py", wildcardEdge.Target);
		Assert.False(wildcardEdge.ModuleOnly);
		Assert.Equal(ModuleTargetReason, Assert.Single(wildcardEdge.Reasons));
		var externalEdge = Assert.Single(result.Edges, item => item.Source == "standard_library.py");
		Assert.Equal(ResolutionStatus.External, externalEdge.Status);
		Assert.Null(externalEdge.Target);
		Assert.False(externalEdge.ModuleOnly);
		Assert.Equal("known Python standard-library module", Assert.Single(externalEdge.Reasons));
	}

	[Fact]
	public async Task ModulesOutsideTheManifestReExportsAndBareRelativeImportsStayUnresolved()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var initializer = fixture.CreateFile("pkg/__init__.py", "VERSION = \"1\"");
		fixture.CreateFile("pkg/_config.py", "DEFAULT = 1");
		var outside = fixture.CreateFile("pkg/outside.py", "from ._config import DEFAULT");
		var bareRelative = fixture.CreateFile("pkg/bare.py", "from . import VERSION");
		var settings = fixture.CreateFile("settings.py", "API_VERSION = \"1\"");
		var facade = fixture.CreateFile("facade.py", "from settings import API_VERSION");
		var reExport = fixture.CreateFile("re_export.py", "from facade import API_VERSION");
		var model = fixture.CreateFile("model.py", "class Item: pass");
		var missing = fixture.CreateFile("missing.py", "from model import Missing");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, initializer, outside, bareRelative, settings, facade, reExport, model, missing],
			cancellationToken: TestContext.Current.CancellationToken);

		AssertUnresolved(result, "pkg/outside.py", "no target in the manifest for this module");
		AssertUnresolved(result, "pkg/bare.py", "Python module assignment bindings are not indexed");
		AssertUnresolved(result, "re_export.py", "name not found in module");
		AssertUnresolved(result, "missing.py", "name not found in module");
		AssertModuleOnly(result, "facade.py", "settings", "settings.py");
	}

	[Fact]
	public async Task AnnotatedAllKeepsWildcardImportsResolved()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var module = fixture.CreateFile("module.py", "__all__: list[str] = build_names()\nclass Item: pass");
		var consumer = fixture.CreateFile("consumer.py", "from module import *");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, module, consumer],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.False(Assert.Single(result.Files, item => item.Path == "module.py").Aliases.ContainsKey("$dynamic-all"));
		var edge = Assert.Single(result.Edges, item => item.Source == "consumer.py");
		Assert.Equal(ResolutionStatus.Resolved, edge.Status);
		Assert.Equal("module.py", edge.Target);
		Assert.False(edge.ModuleOnly);
	}

	private static void AssertModuleOnly(DependencyIndexSnapshot result, string source, string reference, string target)
	{
		var edge = Assert.Single(result.Edges, item => item.Source == source);
		Assert.Equal(reference, edge.Reference);
		Assert.Equal(ResolutionStatus.Resolved, edge.Status);
		Assert.Equal(target, edge.Target);
		Assert.True(edge.ModuleOnly);
		Assert.Equal([ModuleTargetReason, ModuleOnlyReason], edge.Reasons);
	}

	private static void AssertUnresolved(DependencyIndexSnapshot result, string source, string reason)
	{
		var edge = Assert.Single(result.Edges, item => item.Source == source);
		Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
		Assert.Null(edge.Target);
		Assert.False(edge.ModuleOnly);
		Assert.Equal(reason, Assert.Single(edge.Reasons));
	}

	private static DependencyFactsEngine CreateEngine() => new(
		new TreeSitterDependencyFactExtractor(),
		new FileDependencyConfigurationProvider());
}
