using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class DependencyScalaIntegrationTests
{
	[Theory]
	[InlineData("build.sbt", "=>")]
	[InlineData("build.sc", "=>")]
	[InlineData("build.sbt", "as")]
	public async Task PackageImportsSelectorsAliasesAndTypeReferencesResolveInsideTheOwningProject(string configurationName, string renameSyntax)
	{
		using var fixture = new TemporaryDirectory();
		var configuration = fixture.CreateFile(configurationName, "// static ownership only\n");
		var models = fixture.CreateFile("src/main/scala/models/Remote.scala", "package models\nclass Remote\ntrait Contract\nobject Tools { def run(): Unit = () }\n");
		var source = fixture.CreateFile("src/main/scala/app/Main.scala", "package app\nimport models.{Remote " + renameSyntax + " Renamed, Contract}\nimport models.Tools\nclass Main(value: Renamed) extends Contract\n");
		using var engine = CreateEngine();
		var index = await engine.IndexAsync(fixture.Path, [configuration, models, source], cancellationToken: TestContext.Current.CancellationToken);
		var facts = index.Files.Single(file => file.Path.EndsWith("Main.scala", StringComparison.Ordinal));
		Assert.Equal(DependencyFileStatus.Supported, facts.Status);
		Assert.Equal(3, facts.Imports.Count);
		Assert.All(facts.Imports, import => Assert.Equal(ResolutionStatus.Resolved, import.Status));
		Assert.Contains(facts.References, reference => reference.Name == "Renamed" && reference.Status == ResolutionStatus.Resolved);
		Assert.Contains(facts.References, reference => reference.Name == "Contract" && reference.Status == ResolutionStatus.Resolved);
		Assert.All(index.Edges.Where(edge => edge.Source.EndsWith("Main.scala", StringComparison.Ordinal)), edge => Assert.Equal("src/main/scala/models/Remote.scala", edge.Target));
	}

	[Theory]
	[InlineData("object First { def run(): String = \"first-marker\" }; object Second { def run(): String = \"second-marker\" }")]
	[InlineData("object First:\n  def run(): String =\n    \"first-marker\"\nobject Second:\n  def run(): String =\n    \"second-marker\"\n")]
	public async Task NavigationPreservesOwnersForBracedAndIndentedBodies(string declarations)
	{
		using var fixture = new TemporaryDirectory();
		var sourceText = "package sample\n" + declarations + "\ncase class Model(value: String)\ntrait Contract { def execute(): Int }\nval outside: Int = 1\n";
		var source = fixture.CreateFile("Members.scala", sourceText);
		using var engine = CreateEngine();
		var index = await engine.IndexAsync(fixture.Path, [source], cancellationToken: TestContext.Current.CancellationToken);
		var facts = Assert.Single(index.Files);
		var names = facts.NavigationDeclarations.Select(item => item.Name).ToArray();
		Assert.Contains("sample.First.run", names);
		Assert.Contains("sample.Second.run", names);
		Assert.Contains("sample.Model", names);
		Assert.Contains("sample.Contract", names);
		Assert.Contains("sample.Contract.execute", names);
		Assert.Contains("sample.outside", names);
		using var extractor = new TreeSitterDependencyFactExtractor();
		Assert.Equal(facts.NavigationDeclarations, extractor.ExtractNavigation("Members.scala", sourceText, facts.ContentFingerprint, TestContext.Current.CancellationToken));
		var first = Assert.Single(facts.NavigationDeclarations, item => item.Name == "sample.First.run");
		Assert.Contains("first-marker", sourceText[first.StartIndex..first.EndIndex], StringComparison.Ordinal);
		Assert.DoesNotContain("second-marker", sourceText[first.StartIndex..first.EndIndex], StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("_")]
	[InlineData("*")]
	public async Task WildcardsAndExcludedTargetsStayUnresolvedWithoutInventedEdges(string wildcard)
	{
		using var fixture = new TemporaryDirectory();
		var configuration = fixture.CreateFile("build.sbt", "// ownership\n");
		var source = fixture.CreateFile("src/main/scala/app/Main.scala", "package app\nimport models." + wildcard + "\nimport hidden.Remote\nclass Main(value: Unknown)\n");
		var target = fixture.CreateFile("src/main/scala/models/Model.scala", "package models\nclass Unknown\n");
		fixture.CreateFile("src/main/scala/hidden/Remote.scala", "package hidden\nclass Remote\n");
		using var engine = CreateEngine();
		var index = await engine.IndexAsync(fixture.Path, [configuration, source, target], cancellationToken: TestContext.Current.CancellationToken);
		Assert.Equal(3, index.Edges.Count);
		Assert.All(index.Edges, edge => { Assert.Equal(ResolutionStatus.Unresolved, edge.Status); Assert.Null(edge.Target); Assert.Empty(edge.Candidates); });
	}

	[Fact]
	public async Task MissingConfigurationDoesNotPermitCrossFileResolution()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("Main.scala", "import models.Remote\nclass Main(value: models.Remote)\n");
		var target = fixture.CreateFile("Remote.scala", "package models\nclass Remote\n");
		using var engine = CreateEngine();
		var index = await engine.IndexAsync(fixture.Path, [source, target], cancellationToken: TestContext.Current.CancellationToken);
		Assert.NotEmpty(index.Edges);
		Assert.All(index.Edges, edge => Assert.Equal(ResolutionStatus.Unresolved, edge.Status));
	}

	private static DependencyFactsEngine CreateEngine() => new(new TreeSitterDependencyFactExtractor(), new FileDependencyConfigurationProvider());

	[Fact]
	public async Task TopLevelTrailingCommentsAreOutsideIndentedDeclarationNavigation()
	{
		const string text = "object Owner:\n  def run(): String =\n    \"body-marker\"\n// outside-marker\n";
		using var extractor = new TreeSitterDependencyFactExtractor();
		var navigation = extractor.ExtractNavigation("Members.scala", text, "fixture", TestContext.Current.CancellationToken);
		Assert.Equal(2, navigation.Count);
		Assert.All(navigation, item => Assert.DoesNotContain("outside-marker", text[item.StartIndex..item.EndIndex], StringComparison.Ordinal));
	}

	[Fact]
	public void IndentedBodyCommentsStayInsideTheirDeclaration()
	{
		const string text = "object Owner:\n  def run(): String =\n    \"body-marker\"\n    // inside-marker\n// outside-marker\n";
		using var extractor = new TreeSitterDependencyFactExtractor();
		var method = Assert.Single(extractor.ExtractNavigation("Members.scala", text, "fixture", TestContext.Current.CancellationToken), item => item.Name == "Owner.run");
		Assert.Contains("inside-marker", text[method.StartIndex..method.EndIndex], StringComparison.Ordinal);
		Assert.DoesNotContain("outside-marker", text[method.StartIndex..method.EndIndex], StringComparison.Ordinal);
	}

	[Fact]
	public async Task ConfigurationChangesReresolveWithoutReparsingSources()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("Main.scala", "import models.Remote\nclass Main(value: Remote)\n");
		var target = fixture.CreateFile("Remote.scala", "package models\nclass Remote\n");
		using var engine = CreateEngine();
		var before = await engine.IndexAsync(fixture.Path, [source, target], cancellationToken: TestContext.Current.CancellationToken);
		var parses = engine.ParseCount;
		var configuration = fixture.CreateFile("build.sbt", "// ownership\n");
		var after = await engine.IndexAsync(fixture.Path, [configuration, source, target], cancellationToken: TestContext.Current.CancellationToken);
		Assert.All(before.Edges, edge => Assert.Equal(ResolutionStatus.Unresolved, edge.Status));
		Assert.All(after.Edges, edge => Assert.Equal(ResolutionStatus.Resolved, edge.Status));
		Assert.Equal(parses, engine.ParseCount);
	}

	[Fact]
	public async Task DuplicateDeclarationsAreAmbiguousAndMainSourcesCannotSeeTestTypes()
	{
		using var fixture = new TemporaryDirectory();
		var configuration = fixture.CreateFile("build.sbt", "// ownership\n");
		var source = fixture.CreateFile("src/main/scala/Main.scala", "import models.Remote\nimport tests.OnlyTest\nclass Main(value: Remote)\n");
		var first = fixture.CreateFile("src/main/scala/one/Remote.scala", "package models\nclass Remote\n");
		var second = fixture.CreateFile("src/main/scala/two/Remote.scala", "package models\nclass Remote\n");
		var test = fixture.CreateFile("src/test/scala/OnlyTest.scala", "package tests\nclass OnlyTest\n");
		using var engine = CreateEngine();
		var index = await engine.IndexAsync(fixture.Path, [configuration, source, first, second, test], cancellationToken: TestContext.Current.CancellationToken);
		var facts = index.Files.Single(file => file.Path == "src/main/scala/Main.scala");
		var import = Assert.Single(facts.Imports, item => item.Specifier == "models.Remote");
		Assert.Equal(ResolutionStatus.Ambiguous, import.Status);
		Assert.Equal(new[] { "src/main/scala/one/Remote.scala", "src/main/scala/two/Remote.scala" }, import.Candidates);
		Assert.Equal(ResolutionStatus.Unresolved, Assert.Single(facts.Imports, item => item.Specifier == "tests.OnlyTest").Status);
	}

	[Fact]
	public async Task NestedPackageNamesAndOverloadedNavigationRetainTheirLexicalOwners()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("Members.scala", "package outer\npackage inner { object Owner { def run(): Int = 1; def run(value: Int): Int = value } }\nobject Other\n");
		using var engine = CreateEngine();
		var index = await engine.IndexAsync(fixture.Path, [source], cancellationToken: TestContext.Current.CancellationToken);
		var names = Assert.Single(index.Files).NavigationDeclarations.Select(item => item.Name).ToArray();
		Assert.Contains("outer.inner.Owner.run", names);
		Assert.Contains("outer.inner.Owner.run#2", names);
		Assert.Contains("outer.Other", names);
		Assert.DoesNotContain("outer.inner.Other", names);
	}

	[Fact]
	public async Task ScopedImportsAndTypeParametersNeverLeakIntoNeighboringDeclarations()
	{
		using var fixture = new TemporaryDirectory();
		var configuration = fixture.CreateFile("build.sbt", "// ownership\n");
		var target = fixture.CreateFile("Remote.scala", "package models\nclass Remote\n");
		var source = fixture.CreateFile("Main.scala", "object First { import models.Remote; def run(value: Remote): Remote = value }\nobject Second { def run(value: Remote): Remote = value }\nclass Generic[Remote](value: Remote)\n");
		using var engine = CreateEngine();
		var index = await engine.IndexAsync(fixture.Path, [configuration, source, target], cancellationToken: TestContext.Current.CancellationToken);
		var references = index.Files.Single(file => file.Path == "Main.scala").References;
		Assert.Equal(2, references.Count(item => item.ContainingType == "First.run"));
		Assert.Equal(2, references.Count(item => item.ContainingType == "Second.run"));
		Assert.Single(references, item => item.ContainingType == "Generic");
		Assert.All(references.Where(item => item.ContainingType == "First.run"), item => Assert.Equal(ResolutionStatus.Resolved, item.Status));
		Assert.All(references.Where(item => item.ContainingType == "Second.run"), item => Assert.Equal(ResolutionStatus.Unresolved, item.Status));
		Assert.All(references.Where(item => item.ContainingType == "Generic"), item => Assert.Equal(ResolutionStatus.Unresolved, item.Status));
	}

	[Theory]
	[InlineData("object Main { val models = ???; import models.Remote; def run(value: Remote): Remote = value }")]
	[InlineData("import models.Remote\nobject Main { type Remote = String; def run(value: Remote): Remote = value }")]
	[InlineData("import models.Remote\nobject Main { def run(): Unit = { class Remote; val value: Remote = ??? } }")]
	public async Task UnprovenLocalBindingsNeverCreateAnImportedTypeEdge(string text)
	{
		using var fixture = new TemporaryDirectory();
		var configuration = fixture.CreateFile("build.sbt", "// ownership\n");
		var target = fixture.CreateFile("Remote.scala", "package models\nclass Remote\n");
		var source = fixture.CreateFile("Main.scala", text + "\n");
		using var engine = CreateEngine();
		var index = await engine.IndexAsync(fixture.Path, [configuration, source, target], cancellationToken: TestContext.Current.CancellationToken);
		var references = index.Files.Single(file => file.Path == "Main.scala").References.Where(item => item.Name == "Remote").ToArray();
		Assert.NotEmpty(references);
		Assert.All(references, item => Assert.Equal(ResolutionStatus.Unresolved, item.Status));
	}

	[Theory]
	[InlineData("models.Remote", "app/Remote.scala")]
	[InlineData("_root_.models.Remote", "Remote.scala")]
	public async Task ImportQualificationDistinguishesLexicalAndRootPackages(string specifier, string expected)
	{
		using var fixture = new TemporaryDirectory();
		var configuration = fixture.CreateFile("build.sbt", "// ownership\n");
		var root = fixture.CreateFile("Remote.scala", "package models\nclass Remote\n");
		var local = fixture.CreateFile("app/Remote.scala", "package app.models\nclass Remote\n");
		var source = fixture.CreateFile("app/Main.scala", "package app\nimport " + specifier + "\nclass Main(value: Remote)\n");
		using var engine = CreateEngine();
		var index = await engine.IndexAsync(fixture.Path, [configuration, source, root, local], cancellationToken: TestContext.Current.CancellationToken);
		var facts = index.Files.Single(file => file.Path == "app/Main.scala");
		Assert.Equal(expected, Assert.Single(facts.Imports).Target);
		Assert.Equal(expected, Assert.Single(facts.References).Target);
	}
}
