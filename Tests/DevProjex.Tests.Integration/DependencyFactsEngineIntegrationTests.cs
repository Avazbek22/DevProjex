using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Compression;
using DevProjex.Infrastructure.Dependencies;
using DevProjex.Kernel.Abstractions;

namespace DevProjex.Tests.Integration;

public sealed class DependencyFactsEngineIntegrationTests
{
	[Fact]
	public async Task CppFactsResolveHeadersInheritanceAndQualifiedTypes()
	{
		using var fixture = new TemporaryDirectory();
		var header = fixture.CreateFile("include/model.hpp", "namespace Models { class Base {}; class Model : public Base {}; }\n");
		var source = fixture.CreateFile("src/app.cpp", "#include \"../include/model.hpp\"\nModels::Model make(Models::Model value) { return value; }\n");
		using var engine = CreateEngine();
		var index = await engine.IndexAsync(fixture.Path, [header, source], cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains(index.Edges, static edge => edge.Source == "src/app.cpp" && edge.Target == "include/model.hpp");
		Assert.Contains(index.Declarations, static declaration => declaration.Identity.QualifiedName == "Models::Model");
		Assert.Contains(index.Files.Single(static file => file.Path == "src/app.cpp").References,
			static reference => reference.Name.Contains("Model", StringComparison.Ordinal));
	}

	[Fact]
	public async Task CppNavigationDistinguishesOwnersAndOverloads()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("members.cpp", "namespace Sample { class A { int run(int value) { return value; } int run() { return 0; } }; class B { int run() { return 1; } }; }");
		using var engine = CreateEngine();
		var index = await engine.IndexAsync(fixture.Path, [source], cancellationToken: TestContext.Current.CancellationToken);
		var names = Assert.Single(index.Files).NavigationDeclarations.Where(static item => item.Kind == NavigationSymbolKind.Method)
			.Select(static item => item.Name).ToArray();
		Assert.Contains("Sample::A::run", names);
		Assert.Contains("Sample::A::run#2", names);
		Assert.Contains("Sample::B::run", names);
	}

	[Fact]
	public async Task CppHeaderDetectionUsesCppGrammarAndFailsClosedOnErrors()
	{
		using var fixture = new TemporaryDirectory();
		var valid = fixture.CreateFile("valid.h", "namespace Sample { class Model { public: int value; }; }\n");
		var broken = fixture.CreateFile("broken.hpp", "namespace Sample { class Broken {");
		using var engine = CreateEngine();
		var index = await engine.IndexAsync(fixture.Path, [valid, broken], cancellationToken: TestContext.Current.CancellationToken);
		Assert.Equal(LanguageId.Cpp, index.Files.Single(static file => file.Path == "valid.h").LanguageId);
		Assert.Contains(index.Declarations, static declaration => declaration.Identity.QualifiedName == "Sample::Model");
		Assert.Equal(DependencyFileStatus.ExtractionFailed, index.Files.Single(static file => file.Path == "broken.hpp").Status);
	}

	[Fact]
	public async Task CFactsResolveRepositoryHeadersAndNamedTypes()
	{
		using var fixture = new TemporaryDirectory();
		var header = fixture.CreateFile("include/model.h", "typedef struct Model { int value; } Model;\n");
		var source = fixture.CreateFile("src/app.c", "#include \"../include/model.h\"\nstatic Model make(Model value) { return value; }\n");
		using var engine = CreateEngine();
		var index = await engine.IndexAsync(fixture.Path, [header, source],
			cancellationToken: TestContext.Current.CancellationToken);
		var facts = index.Files.Single(static file => file.Path == "src/app.c");

		Assert.Equal(DependencyFileStatus.Supported, facts.Status);
		Assert.Contains(index.Edges, static edge => edge.Source == "src/app.c" &&
			edge.Target == "include/model.h" && edge.Status == ResolutionStatus.Resolved);
		Assert.Contains(index.Declarations, static declaration =>
			declaration.Identity.QualifiedName == "src/app.c#make" && declaration.Identity.FileScope == "src/app.c");
		Assert.Contains(facts.References, static reference => reference.Name == "Model");
		Assert.DoesNotContain(index.Declarations, static declaration =>
			declaration.Identity.QualifiedName.EndsWith("#value", StringComparison.Ordinal));
	}

	[Fact]
	public async Task CNavigationDistinguishesEqualFieldsAcrossOwners()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("members.c", "struct A { int value; }; struct B { int value; }; int run(void) { return 1; }");
		using var engine = CreateEngine();
		var index = await engine.IndexAsync(fixture.Path, [source], cancellationToken: TestContext.Current.CancellationToken);
		var names = Assert.Single(index.Files).NavigationDeclarations.Select(static item => item.Name).ToArray();
		Assert.Contains("members.c#A#value", names);
		Assert.Contains("members.c#B#value", names);
		Assert.Contains("members.c#run", names);
	}

	[Fact]
	public async Task CCMakeIncludeDirectoriesResolveOnlyManifestHeaders()
	{
		using var fixture = new TemporaryDirectory();
		var configuration = fixture.CreateFile("CMakeLists.txt", "add_executable(app src/app.c)\ntarget_include_directories(app PRIVATE libs/include)\n");
		var header = fixture.CreateFile("libs/include/model.h", "typedef struct Model { int value; } Model;\n");
		var source = fixture.CreateFile("src/app.c", "#include <model.h>\nModel read_model(void);\n");
		using var engine = CreateEngine();
		var index = await engine.IndexAsync(fixture.Path, [configuration, header, source],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(index.Edges, static edge => edge.Source == "src/app.c" &&
			edge.Target == "libs/include/model.h" && edge.Status == ResolutionStatus.Resolved);
	}

	[Fact]
	public async Task CSyntaxErrorsFailClosedWithoutPublishingRecoveredFacts()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("broken.c", "struct Broken { int value;");
		using var engine = CreateEngine();
		var index = await engine.IndexAsync(fixture.Path, [source], cancellationToken: TestContext.Current.CancellationToken);
		var facts = Assert.Single(index.Files);
		Assert.Equal(DependencyFileStatus.ExtractionFailed, facts.Status);
		Assert.Empty(index.Declarations);
		Assert.Empty(index.Edges);
	}

	[Fact]
	public async Task PhpFactsResolveUsesInheritanceAndNamespacedTypes()
	{
		using var fixture = new TemporaryDirectory();
		var model = fixture.CreateFile("src/Models/User.php", "<?php namespace Models; class Base {} class User extends Base {}\n");
		var service = fixture.CreateFile("src/App/Service.php", """
			<?php
			namespace App;
			use Models\User as Person;
			class Service {
			    private Person $value;
			    public function read(): Person { return $this->value; }
			}
			""");
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(fixture.Path, [model, service],
			cancellationToken: TestContext.Current.CancellationToken);
		var facts = index.Files.Single(static file => file.Path == "src/App/Service.php");

		Assert.Equal(DependencyFileStatus.Supported, facts.Status);
		Assert.Contains(index.Declarations, static declaration => declaration.Identity.QualifiedName == "Models\\User");
		Assert.Contains(index.Edges, static edge => edge.Source == "src/App/Service.php" &&
			edge.Target == "src/Models/User.php" && edge.Status == ResolutionStatus.Resolved);
		Assert.Contains(facts.NavigationDeclarations, static declaration =>
			declaration.Name == "App.Service.read" && declaration.Kind == NavigationSymbolKind.Method);
		Assert.DoesNotContain(index.Declarations, static declaration =>
			declaration.Identity.QualifiedName.EndsWith("read", StringComparison.Ordinal));
	}

	[Fact]
	public async Task PhpNavigationDistinguishesEqualMembersAcrossOwners()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("Members.php", "<?php namespace Sample; class A { function run() { return 1; } } class B { function run() { return 2; } }");
		using var engine = CreateEngine();
		var index = await engine.IndexAsync(fixture.Path, [source], cancellationToken: TestContext.Current.CancellationToken);
		var names = Assert.Single(index.Files).NavigationDeclarations.Where(static item => item.Kind == NavigationSymbolKind.Method)
			.Select(static item => item.Name).ToArray();
		Assert.Equal(["Sample.A.run", "Sample.B.run"], names);
	}

	[Fact]
	public async Task PhpComposerDependenciesExposeDeclaredRepositoryScopes()
	{
		using var fixture = new TemporaryDirectory();
		var libraryConfig = fixture.CreateFile("library/composer.json", "{\"name\":\"sample/library\",\"autoload\":{\"psr-4\":{\"Library\\\\\":\"src/\"}}}");
		var library = fixture.CreateFile("library/src/Remote.php", "<?php namespace Library; class Remote {}");
		var appConfig = fixture.CreateFile("app/composer.json", "{\"name\":\"sample/app\",\"require\":{\"sample/library\":\"*\"}}");
		var app = fixture.CreateFile("app/src/App.php", "<?php namespace App; use Library\\Remote; class App { private Remote $value; }");
		using var engine = CreateEngine();
		var index = await engine.IndexAsync(fixture.Path, [libraryConfig, library, appConfig, app],
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Contains(index.Edges, static edge => edge.Source == "app/src/App.php" &&
			edge.Target == "library/src/Remote.php" && edge.CrossScope);
	}

	[Fact]
	public async Task PhpSyntaxErrorsFailClosedWithoutPublishingRecoveredFacts()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("Broken.php", "<?php class Broken { function run(");
		using var engine = CreateEngine();
		var index = await engine.IndexAsync(fixture.Path, [source], cancellationToken: TestContext.Current.CancellationToken);
		var facts = Assert.Single(index.Files);
		Assert.Equal(DependencyFileStatus.ExtractionFailed, facts.Status);
		Assert.Empty(index.Declarations);
		Assert.Empty(index.Edges);
	}

	[Fact]
	public async Task RubyFactsResolveRelativeRequiresConstantsAndNestedOwners()
	{
		using var fixture = new TemporaryDirectory();
		var model = fixture.CreateFile("lib/model.rb", "module Models\n class Base\n end\n class User\n end\nend\n");
		var service = fixture.CreateFile("lib/service.rb", """
			require_relative "model"
			module App
			  class Service < Models::Base
			    def read
			      Models::User.new
			    end
			    def self.build
			      new
			    end
			  end
			end
			""");
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(
			fixture.Path,
			[model, service],
			cancellationToken: TestContext.Current.CancellationToken);
		var facts = index.Files.Single(static file => file.Path == "lib/service.rb");

		Assert.Equal(DependencyFileStatus.Supported, facts.Status);
		Assert.Contains(index.Declarations, static declaration => declaration.Identity.QualifiedName == "Models::User");
		Assert.Contains(index.Edges, static edge => edge.Source == "lib/service.rb" &&
			edge.Target == "lib/model.rb" && edge.Status == ResolutionStatus.Resolved);
		Assert.Contains(facts.NavigationDeclarations, static declaration =>
			declaration.Name == "App::Service#read" && declaration.Kind == NavigationSymbolKind.Method);
		Assert.Contains(facts.NavigationDeclarations, static declaration =>
			declaration.Name == "App::Service.build" && declaration.Kind == NavigationSymbolKind.Method);
		Assert.DoesNotContain(index.Declarations, static declaration =>
			declaration.Identity.QualifiedName.Contains("read", StringComparison.Ordinal));
	}

	[Fact]
	public async Task RubyReopenedContainersDoNotCreateEdgesToEveryDeclarationFile()
	{
		using var fixture = new TemporaryDirectory();
		var indifferentHash = fixture.CreateFile("lib/sinatra/indifferent_hash.rb", "module Sinatra\n  class IndifferentHash\n  end\nend\n");
		var baseType = fixture.CreateFile("lib/sinatra/base.rb", "module Sinatra\n  class Base\n  end\nend\n");
		var consumer = fixture.CreateFile("test/consumer.rb", "class Consumer\n  include Sinatra\n  VALUE = Sinatra::Base\nend\n");
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(
			fixture.Path,
			[indifferentHash, baseType, consumer],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.DoesNotContain(index.Edges, static edge => edge.Source == "test/consumer.rb" &&
			edge.Status == ResolutionStatus.Resolved && edge.Target == "lib/sinatra/indifferent_hash.rb");
		Assert.DoesNotContain(index.Edges, static edge => edge.Source == "test/consumer.rb" &&
			edge.Reference == "Sinatra" && edge.Candidates.Count > 0);
		Assert.Contains(index.Edges, static edge => edge.Source == "test/consumer.rb" &&
			edge.Reference == "Sinatra::Base" && edge.Status == ResolutionStatus.Resolved &&
			edge.Target == "lib/sinatra/base.rb");
	}

	[Fact]
	public async Task RubyNavigationDistinguishesOrdinarySingletonAndNestedMethods()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("members.rb", """
			module First
			  def run
			    @value = 1
			  end
			  def self.run
			    2
			  end
			end
			module Second
			  def run
			    3
			  end
			end
			""");
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(
			fixture.Path,
			[source],
			cancellationToken: TestContext.Current.CancellationToken);
		var declarations = Assert.Single(index.Files).NavigationDeclarations;

		Assert.Contains(declarations, static item => item.Name == "First#run");
		Assert.Contains(declarations, static item => item.Name == "First.run");
		Assert.Contains(declarations, static item => item.Name == "Second#run");
		Assert.Contains(declarations, static item => item.Name == "First::run::@value" &&
			item.Kind == NavigationSymbolKind.Field);
		Assert.Equal(declarations.Count, declarations.Select(static item => item.Name).Distinct(StringComparer.Ordinal).Count());
	}

	[Fact]
	public async Task RubyGemfilePathDependenciesExposeDeclaredRepositoryScopes()
	{
		using var fixture = new TemporaryDirectory();
		var gemspec = fixture.CreateFile("shared/shared.gemspec", "Gem::Specification.new { |spec| spec.name = 'shared' }");
		var shared = fixture.CreateFile("shared/lib/shared.rb", "module Shared\n class Item\n end\nend\n");
		var gemfile = fixture.CreateFile("app/Gemfile", "source 'https://example.invalid'\ngem 'shared', path: '../shared'\n");
		var app = fixture.CreateFile("app/lib/app.rb", "require 'shared'\nclass App\n VALUE = Shared::Item\nend\n");
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(
			fixture.Path,
			[gemspec, shared, gemfile, app],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(index.Edges, static edge => edge.Source == "app/lib/app.rb" &&
			edge.Target == "shared/lib/shared.rb" && edge.Status == ResolutionStatus.Resolved && edge.CrossScope);
	}

	[Fact]
	public async Task RubySyntaxErrorsFailClosedWithoutPublishingRecoveredFacts()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("broken.rb", "class Broken\n  def run(\nend\n");
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(
			fixture.Path,
			[source],
			cancellationToken: TestContext.Current.CancellationToken);

		var facts = Assert.Single(index.Files);
		Assert.Equal(DependencyFileStatus.ExtractionFailed, facts.Status);
		Assert.Empty(index.Declarations);
		Assert.Empty(index.Edges);
	}

	[Fact]
	public async Task KotlinFactsResolveImportsAliasesAndRepositoryTypes()
	{
		using var fixture = new TemporaryDirectory();
		var model = fixture.CreateFile("models/User.kt", "package models\nopen class User");
		var consumer = fixture.CreateFile("app/Consumer.kt", """
			package app
			import models.User as Person
			class Consumer(val value: Person) : Person() {
			    fun String.render(): Person = value
			}
			""");
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(
			fixture.Path,
			[model, consumer],
			cancellationToken: TestContext.Current.CancellationToken);
		var facts = index.Files.Single(static file => file.Path == "app/Consumer.kt");

		Assert.True(facts.Status == DependencyFileStatus.Supported,
			$"{facts.StatusReason}; {string.Join(", ", facts.ErrorNodeKinds.Keys)}");
		Assert.Contains(index.Declarations, static declaration => declaration.Identity.QualifiedName == "app.Consumer");
		Assert.Contains(index.Edges, static edge => edge.Source == "app/Consumer.kt" &&
			edge.Target == "models/User.kt" && edge.Status == ResolutionStatus.Resolved);
		Assert.Contains(facts.NavigationDeclarations, static declaration =>
			declaration.Name == "app.Consumer.render[String]" && declaration.Kind == NavigationSymbolKind.Method);
		Assert.Contains(facts.NavigationDeclarations, static declaration =>
			declaration.Name == "app.Consumer.value" && declaration.Kind == NavigationSymbolKind.Property);
		Assert.Contains(facts.NavigationDeclarations, static declaration =>
			declaration.Name == "app.Consumer.constructor" && declaration.Kind == NavigationSymbolKind.Method);
		Assert.DoesNotContain(index.Declarations, static declaration =>
			declaration.Identity.QualifiedName.Contains("render", StringComparison.Ordinal));
	}

	[Fact]
	public async Task KotlinNavigationDistinguishesOwnersAndRepeatedMembers()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("Members.kt", """
			package sample
			class First {
			    fun run() = 1
			    fun run(value: Int) = value
			}
			class Second {
			    fun run() = 2
			}
			""");
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(
			fixture.Path,
			[source],
			cancellationToken: TestContext.Current.CancellationToken);
		var names = Assert.Single(index.Files).NavigationDeclarations
			.Where(static declaration => declaration.Kind == NavigationSymbolKind.Method)
			.Select(static declaration => declaration.Name)
			.ToArray();

		Assert.Equal(["sample.First.run", "sample.First.run#2", "sample.Second.run"], names);
		Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
	}

	[Fact]
	public async Task KotlinMavenAndGradleProjectReferencesBoundCrossScopeImports()
	{
		using var fixture = new TemporaryDirectory();
		var mavenLibrary = fixture.CreateFile("maven-lib/pom.xml", """
			<project><modelVersion>4.0.0</modelVersion><groupId>sample</groupId><artifactId>library</artifactId></project>
			""");
		var mavenLibrarySource = fixture.CreateFile(
			"maven-lib/src/main/kotlin/library/Remote.kt", "package library\nclass Remote");
		var mavenApp = fixture.CreateFile("maven-app/pom.xml", """
			<project><modelVersion>4.0.0</modelVersion><groupId>sample</groupId><artifactId>app</artifactId>
			<dependencies><dependency><groupId>sample</groupId><artifactId>library</artifactId></dependency></dependencies></project>
			""");
		var mavenAppSource = fixture.CreateFile(
			"maven-app/src/main/kotlin/app/App.kt", "package app\nimport library.Remote\nclass App(val value: Remote)");
		var gradleLibrary = fixture.CreateFile("gradle/lib/build.gradle.kts", "plugins { kotlin(\"jvm\") }");
		var gradleLibrarySource = fixture.CreateFile(
			"gradle/lib/src/main/kotlin/shared/Service.kt", "package shared\nclass Service");
		var gradleApp = fixture.CreateFile(
			"gradle/app/build.gradle.kts", "dependencies { implementation(project(\":gradle:lib\")) }");
		var gradleAppSource = fixture.CreateFile(
			"gradle/app/src/main/kotlin/client/Client.kt", "package client\nimport shared.Service\nclass Client(val value: Service)");
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(
			fixture.Path,
			[mavenLibrary, mavenLibrarySource, mavenApp, mavenAppSource,
				gradleLibrary, gradleLibrarySource, gradleApp, gradleAppSource],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(index.Edges, static edge => edge.Source.EndsWith("maven-app/src/main/kotlin/app/App.kt", StringComparison.Ordinal) &&
			edge.Target == "maven-lib/src/main/kotlin/library/Remote.kt" && edge.CrossScope);
		Assert.Contains(index.Edges, static edge => edge.Source.EndsWith("gradle/app/src/main/kotlin/client/Client.kt", StringComparison.Ordinal) &&
			edge.Target == "gradle/lib/src/main/kotlin/shared/Service.kt" && edge.CrossScope);
	}

	[Fact]
	public async Task KotlinSyntaxErrorsFailClosedWithoutPublishingRecoveredFacts()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("Broken.kt", "package sample\nclass Broken(val value: Missing");
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(
			fixture.Path,
			[source],
			cancellationToken: TestContext.Current.CancellationToken);

		var facts = Assert.Single(index.Files);
		Assert.Equal(DependencyFileStatus.ExtractionFailed, facts.Status);
		Assert.Equal("syntax tree contains errors", facts.StatusReason);
		Assert.Empty(index.Declarations);
		Assert.Empty(index.Edges);
	}

	[Fact]
	public async Task RustFactsResolveModulesUsesAndTypesFromTheManifest()
	{
		using var fixture = new TemporaryDirectory();
		var root = fixture.CreateFile("src/lib.rs", "mod models; mod service;");
		var model = fixture.CreateFile("src/models.rs", "pub struct User { pub id: u64 }");
		var service = fixture.CreateFile("src/service.rs", """
			use crate::models::User;
			pub struct Service { value: User }
			impl Service { pub fn read(&self) -> &User { &self.value } }
			""");
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(
			fixture.Path,
			[root, model, service],
			cancellationToken: TestContext.Current.CancellationToken);
		var facts = index.Files.Single(static file => file.Path == "src/service.rs");

		Assert.Equal(DependencyFileStatus.Supported, facts.Status);
		Assert.Contains(index.Declarations, static declaration => declaration.Identity.QualifiedName == "models::User");
		Assert.Contains(index.Edges, static edge => edge.Source == "src/service.rs" &&
			edge.Target == "src/models.rs" && edge.Reference == "models::User");
		Assert.Contains(index.Edges, static edge => edge.Source == "src/lib.rs" && edge.Target == "src/models.rs");
		Assert.Contains(facts.NavigationDeclarations, static declaration =>
			declaration.Name == "service::impl<Service>::read" && declaration.Kind == NavigationSymbolKind.Function);
		Assert.DoesNotContain(index.Declarations, static declaration =>
			declaration.Identity.QualifiedName.Contains("::read", StringComparison.Ordinal));
	}

	[Fact]
	public async Task RustCargoPathDependenciesExposeOnlyDeclaredRepositoryScopes()
	{
		using var fixture = new TemporaryDirectory();
		var libraryManifest = fixture.CreateFile("library/Cargo.toml", "[package]\nname = \"shared-lib\"\nversion = \"1.0.0\"\n");
		var librarySource = fixture.CreateFile("library/src/lib.rs", "pub struct Remote;");
		var appManifest = fixture.CreateFile("app/Cargo.toml", """
			[package]
			name = "app"
			version = "1.0.0"
			[dependencies]
			shared-lib = { path = "../library" }
			""");
		var appSource = fixture.CreateFile("app/src/lib.rs", "use shared_lib::Remote; pub struct App(Remote);");
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(
			fixture.Path,
			[libraryManifest, librarySource, appManifest, appSource],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(index.Edges, static edge => edge.Source == "app/src/lib.rs" &&
			edge.Target == "library/src/lib.rs" && edge.Status == ResolutionStatus.Resolved && edge.CrossScope);
	}

	[Fact]
	public async Task RustSyntaxErrorsFailClosedWithoutRecoveredEdges()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("src/lib.rs", "mod missing; struct Broken {");
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(
			fixture.Path,
			[source],
			cancellationToken: TestContext.Current.CancellationToken);

		var facts = Assert.Single(index.Files);
		Assert.Equal(DependencyFileStatus.ExtractionFailed, facts.Status);
		Assert.Empty(index.Declarations);
		Assert.Empty(index.Edges);
	}

	[Fact]
	public async Task JavaFactsResolveManifestTypesAndKeepMembersInNavigationOnly()
	{
		using var fixture = new TemporaryDirectory();
		var dependency = fixture.CreateFile("src/sample/Dependency.java", "package sample; public class Dependency { }");
		var remote = fixture.CreateFile("src/library/Remote.java", "package library; public interface Remote { }");
		var consumer = fixture.CreateFile("src/sample/Consumer.java", """
			package sample;
			import library.Remote;
			public class Consumer extends Dependency implements Remote {
			    private Dependency value;
			    public Dependency read() { return value; }
			}
			""");
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(
			fixture.Path,
			[dependency, remote, consumer],
			cancellationToken: TestContext.Current.CancellationToken);
		var facts = index.Files.Single(static file => file.Path == "src/sample/Consumer.java");

		Assert.Equal(DependencyFileStatus.Supported, facts.Status);
		Assert.Contains(index.Declarations, static declaration =>
			declaration.Identity.QualifiedName == "sample.Consumer");
		Assert.Contains(index.Edges, static edge => edge.Source == "src/sample/Consumer.java" &&
			edge.Target == "src/sample/Dependency.java" && edge.Reference == "Dependency");
		Assert.Contains(index.Edges, static edge => edge.Source == "src/sample/Consumer.java" &&
			edge.Target == "src/library/Remote.java" && edge.Layer == EvidenceLayer.ExplicitImport);
		Assert.Contains(facts.NavigationDeclarations, static declaration =>
			declaration.Name == "sample.Consumer.read" && declaration.Kind == NavigationSymbolKind.Method);
		Assert.DoesNotContain(index.Declarations, static declaration =>
			declaration.Identity.QualifiedName.EndsWith(".read", StringComparison.Ordinal));
	}

	[Fact]
	public async Task JavaNavigationDistinguishesNestedOwnersAndOverloads()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("Members.java", """
			package sample;
			class First { void run() { } void run(int value) { } }
			class Second { void run() { } }
			""");
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(
			fixture.Path,
			[source],
			cancellationToken: TestContext.Current.CancellationToken);
		var names = Assert.Single(index.Files).NavigationDeclarations
			.Where(static declaration => declaration.Kind == NavigationSymbolKind.Method)
			.Select(static declaration => declaration.Name)
			.ToArray();

		Assert.Equal(["sample.First.run", "sample.First.run#2", "sample.Second.run"], names);
		Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
	}

	[Fact]
	public async Task JavaSyntaxErrorsFailClosedWithoutPublishingRecoveredFacts()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("Broken.java", "package sample; class Broken { Missing value");
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(
			fixture.Path,
			[source],
			cancellationToken: TestContext.Current.CancellationToken);

		var facts = Assert.Single(index.Files);
		Assert.Equal(DependencyFileStatus.ExtractionFailed, facts.Status);
		Assert.Equal("syntax tree contains errors", facts.StatusReason);
		Assert.Empty(index.Declarations);
		Assert.Empty(index.Edges);
	}

	[Fact]
	public async Task JavaMavenAndGradleProjectReferencesBoundCrossScopeImports()
	{
		using var fixture = new TemporaryDirectory();
		var mavenLibrary = fixture.CreateFile("maven-lib/pom.xml", """
			<project><modelVersion>4.0.0</modelVersion><groupId>sample</groupId><artifactId>library</artifactId></project>
			""");
		var mavenLibrarySource = fixture.CreateFile(
			"maven-lib/src/main/java/library/Remote.java",
			"package library; public class Remote { }");
		var mavenApp = fixture.CreateFile("maven-app/pom.xml", """
			<project><modelVersion>4.0.0</modelVersion><groupId>sample</groupId><artifactId>app</artifactId>
			<dependencies><dependency><groupId>sample</groupId><artifactId>library</artifactId></dependency></dependencies></project>
			""");
		var mavenAppSource = fixture.CreateFile(
			"maven-app/src/main/java/app/App.java",
			"package app; import library.Remote; public class App { Remote value; }");
		var gradleLibrary = fixture.CreateFile("gradle/lib/build.gradle", "plugins { id 'java' }");
		var gradleLibrarySource = fixture.CreateFile(
			"gradle/lib/src/main/java/shared/Service.java",
			"package shared; public class Service { }");
		var gradleApp = fixture.CreateFile(
			"gradle/app/build.gradle",
			"dependencies { implementation(project(\":gradle:lib\")) }");
		var gradleAppSource = fixture.CreateFile(
			"gradle/app/src/main/java/client/Client.java",
			"package client; import shared.Service; public class Client { Service value; }");
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(
			fixture.Path,
			[mavenLibrary, mavenLibrarySource, mavenApp, mavenAppSource,
				gradleLibrary, gradleLibrarySource, gradleApp, gradleAppSource],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(index.Edges, static edge => edge.Source.EndsWith("maven-app/src/main/java/app/App.java", StringComparison.Ordinal) &&
			edge.Target == "maven-lib/src/main/java/library/Remote.java" && edge.CrossScope);
		Assert.Contains(index.Edges, static edge => edge.Source.EndsWith("gradle/app/src/main/java/client/Client.java", StringComparison.Ordinal) &&
			edge.Target == "gradle/lib/src/main/java/shared/Service.java" && edge.CrossScope);
	}

	[Fact]
	public async Task NavigationMembersRemainSeparateFromResolutionDeclarations()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var source = fixture.CreateFile("Members.cs", """
			namespace Sample;
			sealed class Holder
			{
				private string field = "field";
				public string Property => field;
				public string Method() => Property;
			}
			""");
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(
			fixture.Path,
			[project, source],
			cancellationToken: TestContext.Current.CancellationToken);
		var facts = index.Files.Single(static file => file.Path == "Members.cs");

		Assert.Single(facts.Declarations, static declaration =>
			declaration.Identity.QualifiedName == "Sample.Holder");
		Assert.Contains(facts.NavigationDeclarations, static declaration =>
			declaration.Name == "Sample.Holder.field" && declaration.Kind == NavigationSymbolKind.Field);
		Assert.Contains(facts.NavigationDeclarations, static declaration =>
			declaration.Name == "Sample.Holder.Property" && declaration.Kind == NavigationSymbolKind.Property);
		Assert.Contains(facts.NavigationDeclarations, static declaration =>
			declaration.Name == "Sample.Holder.Method" && declaration.Kind == NavigationSymbolKind.Method);
		Assert.DoesNotContain(index.Declarations, static declaration =>
			declaration.Identity.QualifiedName.EndsWith(".Method", StringComparison.Ordinal));
	}

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
		Assert.Equal(["First.cs", "Second.cs"], userEdge.DeclarationFiles);
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

		var dependencies = await engine.FindRelatedAsync(
			fixture.Path,
			[project, first, second, ambiguous, global, marker, consumer],
			["Consumer.cs"],
			DependencyDirection.Dependencies,
			cancellationToken: TestContext.Current.CancellationToken);
		var partialDependencies = Assert.Single(dependencies.Seeds).Dependencies
			.Where(item => item.Path is "First.cs" or "Second.cs")
			.ToArray();
		Assert.Equal(["First.cs", "Second.cs"], partialDependencies.Select(static item => item.Path));
		Assert.All(partialDependencies, item =>
		{
			Assert.Equal(ResolutionStatus.Resolved, item.Status);
			Assert.Contains(item.Reasons, reason => reason.Contains("one resolved symbol with 2 files", StringComparison.Ordinal));
		});

		var dependents = await engine.FindRelatedAsync(
			fixture.Path,
			[project, first, second, ambiguous, global, marker, consumer],
			["Second.cs"],
			DependencyDirection.Dependents,
			cancellationToken: TestContext.Current.CancellationToken);
		var caller = Assert.Single(Assert.Single(dependents.Seeds).Dependents, item => item.Path == "Consumer.cs");
		Assert.Contains(caller.Reasons, reason => reason.Contains("one resolved symbol with 2 files", StringComparison.Ordinal));
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
	public async Task CSharpTypeParameters_ShadowOnlyInsideTheirLexicalOwner()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var model = fixture.CreateFile("Models/User.cs", "namespace Models; public class User { }");
		var source = fixture.CreateFile("Consumers.cs", """
			using Models;
			public class Box<User>
			{
				public User GenericValue { get; }
				public Models.User QualifiedValue { get; }
			}
			public class Consumer
			{
				public User NeighborValue { get; }
				public void Map<User>(User value) { }
				public User OutsideMethod { get; }
				public User LocalFunctionOwner()
				{
					User Local<User>(User value) => value;
					return new User();
				}
			}
			""");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[project, model, source],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Edges, edge => edge.Source == "Consumers.cs" && edge.Reference == "User" &&
			edge.Status == ResolutionStatus.Unresolved && edge.Evidence.Any(site => site.Line == 4));
		Assert.Contains(result.Edges, edge => edge.Source == "Consumers.cs" && edge.Reference == "Models.User" &&
			edge.Status == ResolutionStatus.Resolved && edge.Target == "Models/User.cs");
		Assert.Contains(result.Edges, edge => edge.Source == "Consumers.cs" && edge.Reference == "User" &&
			edge.Status == ResolutionStatus.Resolved && edge.Target == "Models/User.cs" &&
			edge.Evidence.Any(site => site.Line == 9));
		Assert.Contains(result.Edges, edge => edge.Source == "Consumers.cs" && edge.Reference == "User" &&
			edge.Status == ResolutionStatus.Unresolved && edge.Evidence.Any(site => site.Line == 10));
		Assert.Contains(result.Edges, edge => edge.Source == "Consumers.cs" && edge.Reference == "User" &&
			edge.Status == ResolutionStatus.Resolved && edge.Target == "Models/User.cs" &&
			edge.Evidence.Any(site => site.Line == 11));
		Assert.Contains(result.Edges, edge => edge.Source == "Consumers.cs" && edge.Reference == "User" &&
			edge.Status == ResolutionStatus.Unresolved && edge.Evidence.Any(site => site.Line == 14));
		Assert.Contains(result.Edges, edge => edge.Source == "Consumers.cs" && edge.Reference == "User" &&
			edge.Status == ResolutionStatus.Resolved && edge.Target == "Models/User.cs" &&
			edge.Evidence.Any(site => site.Line == 15));
	}

	[Fact]
	public async Task CSharpProjectReference_NormalizesBothMsBuildSeparatorsAndKeepsCrossScopeResolution()
	{
		using var fixture = new TemporaryDirectory();
		var producerProject = fixture.CreateFile("Lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var producer = fixture.CreateFile("Lib/User.cs", "namespace Models; public class User { }");
		var consumerProject = fixture.CreateFile("App/App.csproj", """
			<Project Sdk="Microsoft.NET.Sdk">
			  <ItemGroup>
			    <ProjectReference Include="../Lib/Lib.csproj" />
			    <ProjectReference Include="..\Lib\Lib.csproj" />
			  </ItemGroup>
			</Project>
			""");
		var consumer = fixture.CreateFile("App/Consumer.cs", "using Models; public class Consumer { User Value; }");
		var manifest = new[] { producerProject, producer, consumerProject, consumer };
		var provider = new FileDependencyConfigurationProvider();

		var configuration = await provider.ReadAsync(
			fixture.Path,
			manifest,
			TestContext.Current.CancellationToken);
		var appScope = Assert.Single(configuration.Scopes, scope => scope.ScopeId.EndsWith("App/App.csproj", StringComparison.Ordinal));
		Assert.Single(appScope.ProjectReferences);

		using var engine = new DependencyFactsEngine(new TreeSitterDependencyFactExtractor(), provider);
		var result = await engine.IndexAsync(
			fixture.Path,
			manifest,
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, item => item.Source == "App/Consumer.cs" && item.Reference == "User");
		Assert.Equal(ResolutionStatus.Resolved, edge.Status);
		Assert.Equal("Lib/User.cs", edge.Target);
		Assert.True(edge.CrossScope);
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
	public async Task CSharpFacts_CaptureOnlyExplicitTypePositions()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var source = fixture.CreateFile("Types.cs", """
			public class Target { }
			public sealed class TargetAttribute : System.Attribute { }
			[Target]
			public class Consumer<T> : Target where T : Target
			{
				private Target field;
				public Target Property { get; }
				public Target Method(Target parameter)
				{
					var created = new Target();
					_ = typeof(Target);
					_ = sizeof(Target);
					_ = default(Target);
					object value = parameter;
					_ = (Target)value;
					_ = value as Target;
					if (value is Target named) { }
					return created;
				}
			}
			""");
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(fixture.Path, [project, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var syntaxKinds = index.Files.Single(file => file.Path == "Types.cs").References
			.Where(reference => reference.Name == "Target")
			.Select(static reference => reference.SyntaxKind)
			.ToHashSet(StringComparer.Ordinal);
		Assert.All(
			new[] { "variable_type", "property_type", "parameter_type", "return_type", "base", "constraint",
				"attribute", "object_creation", "typeof", "sizeof", "default", "cast", "as", "pattern" },
			syntaxKind => Assert.Contains(syntaxKind, syntaxKinds));
	}

	[Fact]
	public async Task TypeScriptFacts_ApplyOrderedJsSubstitutionPathsAndBundlerIndexFallback()
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
		Assert.Contains(result.Edges, edge => edge.Source == "src/main.ts" && edge.Reference == "./dir" && edge.Target == "src/dir/index.ts");
	}

	[Fact]
	public async Task TypeScriptRelativeResolution_UsesTheFirstExistingProbeAndPreservesJavaScriptFallback()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var source = fixture.CreateFile("main.ts", "import one from './worker.js'; import two from './plain.js';");
		var workerTypeScript = fixture.CreateFile("worker.ts", "export default 1;");
		var workerDeclaration = fixture.CreateFile("worker.d.ts", "declare const value: number; export default value;");
		var plainJavaScript = fixture.CreateFile("plain.js", "export default 2;");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, source, workerTypeScript, workerDeclaration, plainJavaScript],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Edges, edge => edge.Reference == "./worker.js" &&
			edge.Status == ResolutionStatus.Resolved && edge.Target == "worker.ts");
		Assert.Contains(result.Edges, edge => edge.Reference == "./plain.js" &&
			edge.Status == ResolutionStatus.Resolved && edge.Target == "plain.js");
		Assert.DoesNotContain(result.Edges, edge => edge.Reference == "./worker.js" &&
			edge.Status == ResolutionStatus.Ambiguous);
	}

	[Fact]
	public async Task TypeScriptExtensionlessResolution_ProbesJavaScriptWithoutAllowJs()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var source = fixture.CreateFile("main.ts", "import value from './dep';");
		var target = fixture.CreateFile("dep.js", "export default 1;");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, source, target],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Edges, edge => edge.Source == "main.ts" &&
			edge.Reference == "./dep" && edge.Status == ResolutionStatus.Resolved && edge.Target == "dep.js");
	}

	[Fact]
	public async Task TypeScriptNode16_DefaultsOrdinaryTypeScriptFilesToCommonJsWithoutPackageType()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"node16\"}}");
		var source = fixture.CreateFile("main.ts", "import value from './dep';");
		var target = fixture.CreateFile("dep.ts", "export default 1;");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, source, target],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Edges, edge => edge.Source == "main.ts" &&
			edge.Reference == "./dep" && edge.Status == ResolutionStatus.Resolved && edge.Target == "dep.ts");
	}

	[Fact]
	public async Task TypeScriptDirectoryResolution_DistinguishesBundlerFromNodeEsm()
	{
		using var fixture = new TemporaryDirectory();
		var bundlerConfig = fixture.CreateFile("bundler/tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var bundlerSource = fixture.CreateFile("bundler/main.ts", "import value from './dir';");
		var bundlerIndex = fixture.CreateFile("bundler/dir/index.ts", "export default 1;");
		var nodeConfig = fixture.CreateFile("node/tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"node16\"}}");
		var nodePackage = fixture.CreateFile("node/package.json", "{\"type\":\"module\"}");
		var nodeSource = fixture.CreateFile("node/main.mts", "import value from './dir';");
		var nodeIndex = fixture.CreateFile("node/dir/index.ts", "export default 1;");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[bundlerConfig, bundlerSource, bundlerIndex, nodeConfig, nodePackage, nodeSource, nodeIndex],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Edges, edge => edge.Source == "bundler/main.ts" &&
			edge.Reference == "./dir" && edge.Target == "bundler/dir/index.ts");
		Assert.Contains(result.Edges, edge => edge.Source == "node/main.mts" &&
			edge.Reference == "./dir" && edge.Status == ResolutionStatus.Unresolved &&
			edge.Reasons.Contains("extension required for a relative ESM import under node16/nodenext"));
	}

	[Fact]
	public async Task TypeScriptPaths_UsesTheFirstFallbackTargetThatExists()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", """
			{"compilerOptions":{"moduleResolution":"bundler","paths":{"alias":["missing.ts","src/value.ts"]}}}
			""");
		var source = fixture.CreateFile("main.ts", "import value from 'alias';");
		var target = fixture.CreateFile("src/value.ts", "export default 1;");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, source, target],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Edges, edge => edge.Reference == "alias" &&
			edge.Status == ResolutionStatus.Resolved && edge.Target == "src/value.ts");
	}

	[Fact]
	public async Task TypeScriptPaths_SelectsTheLongestPrefixBeforeTheWildcard()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", """
			{"compilerOptions":{"moduleResolution":"bundler","paths":{
				"foo/*":["correct/*"],
				"f*tail":["wrong/*"]
			}}}
			""");
		var source = fixture.CreateFile("main.ts", "import value from 'foo/xtail';");
		var correct = fixture.CreateFile("correct/xtail.ts", "export default 1;");
		var wrong = fixture.CreateFile("wrong/oo/x.ts", "export default 2;");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, source, correct, wrong],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Edges, edge => edge.Reference == "foo/xtail" &&
			edge.Status == ResolutionStatus.Resolved && edge.Target == "correct/xtail.ts");
		Assert.DoesNotContain(result.Edges, edge => edge.Reference == "foo/xtail" && edge.Target == "wrong/oo/x.ts");
	}

	[Fact]
	public async Task TypeScriptPaths_DoesNotFallBackToALessSpecificPattern()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", """
			{"compilerOptions":{"moduleResolution":"bundler","paths":{
				"foo/*":["missing/*"],
				"*":["fallback/*"]
			}}}
			""");
		var source = fixture.CreateFile("main.ts", "import value from 'foo/item';");
		var fallback = fixture.CreateFile("fallback/foo/item.ts", "export default 1;");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, source, fallback],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, item => item.Reference == "foo/item");
		Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
		Assert.Null(edge.Target);
	}

	[Fact]
	public async Task TypeScriptPaths_RejectsOverlappingPrefixAndSuffixWithoutThrowing()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", """
			{"compilerOptions":{"moduleResolution":"bundler","paths":{"ab*bc":["target/*"]}}}
			""");
		var source = fixture.CreateFile("main.ts", "import missing from 'abc'; import value from 'abXbc';");
		var target = fixture.CreateFile("target/X.ts", "export default 1;");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, source, target],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Edges, edge => edge.Reference == "abc" && edge.Status == ResolutionStatus.Unresolved);
		Assert.Contains(result.Edges, edge => edge.Reference == "abXbc" && edge.Target == "target/X.ts");
	}

	[Fact]
	public async Task TypeScriptPackageMap_RejectsOverlappingPrefixAndSuffixWithoutThrowing()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var package = fixture.CreateFile("package.json", "{\"imports\":{\"#ab*bc\":\"./target/*.ts\"}}");
		var source = fixture.CreateFile("main.ts", "import missing from '#abc'; import value from '#abXbc';");
		var target = fixture.CreateFile("target/X.ts", "export default 1;");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, package, source, target],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Edges, edge => edge.Reference == "#abc" && edge.Status == ResolutionStatus.Unresolved);
		Assert.Contains(result.Edges, edge => edge.Reference == "#abXbc" && edge.Target == "target/X.ts");
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
	public async Task TypeScriptConditionalExports_SelectRequireForCommonJsSourceInObjectOrder()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"node16\"}}");
		var package = fixture.CreateFile("package.json", "{\"name\":\"self\",\"exports\":{\"import\":\"./import.mjs\",\"require\":\"./require.cjs\"}}");
		var source = fixture.CreateFile("main.cts", "import value from 'self';");
		var importTarget = fixture.CreateFile("import.mjs", "export default 1;");
		var requireTarget = fixture.CreateFile("require.cjs", "module.exports = 1;");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(fixture.Path, [config, package, source, importTarget, requireTarget],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, item => item.Reference == "self");
		Assert.Equal("require.cjs", edge.Target);
		Assert.Equal(ResolutionStatus.Resolved, edge.Status);
	}

	[Fact]
	public async Task TypeScriptConditionalExports_NullBlocksOnlyTheApplicableCondition()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"node16\"}}");
		var package = fixture.CreateFile("package.json", "{\"name\":\"self\",\"exports\":{\"import\":null,\"require\":\"./require.cjs\"}}");
		var source = fixture.CreateFile("main.cts", "import value from 'self';");
		var requireTarget = fixture.CreateFile("require.cjs", "module.exports = 1;");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(fixture.Path, [config, package, source, requireTarget],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal("require.cjs", Assert.Single(result.Edges, item => item.Reference == "self").Target);
	}

	[Fact]
	public async Task TypeScriptConditionalExports_DefaultBeforeImportWinsByDeclarationOrder()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"node16\"}}");
		var package = fixture.CreateFile("package.json", "{\"name\":\"self\",\"type\":\"module\",\"exports\":{\"default\":\"./default.js\",\"import\":\"./import.mjs\"}}");
		var source = fixture.CreateFile("main.mts", "import value from 'self';");
		var defaultTarget = fixture.CreateFile("default.js", "export default 1;");
		var importTarget = fixture.CreateFile("import.mjs", "export default 2;");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(fixture.Path, [config, package, source, defaultTarget, importTarget],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal("default.js", Assert.Single(result.Edges, item => item.Reference == "self").Target);
	}

	[Fact]
	public async Task TypeScriptConditionalExports_ResolveNestedEsmConditionsAndSkipInactiveUnknownConditions()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"node16\"}}");
		var validPackage = fixture.CreateFile("valid/package.json", "{\"name\":\"valid\",\"type\":\"module\",\"exports\":{\"node\":{\"import\":\"./entry.mjs\",\"require\":\"./entry.cjs\"}}}");
		var validSource = fixture.CreateFile("valid/main.mts", "import value from 'valid';");
		var esmTarget = fixture.CreateFile("valid/entry.mjs", "export default 1;");
		var cjsTarget = fixture.CreateFile("valid/entry.cjs", "module.exports = 1;");
		var unknownPackage = fixture.CreateFile("unknown/package.json", "{\"name\":\"unknown\",\"type\":\"module\",\"exports\":{\"browser\":\"./browser.js\",\"default\":\"./default.js\"}}");
		var unknownSource = fixture.CreateFile("unknown/main.mts", "import value from 'unknown';");
		var browserTarget = fixture.CreateFile("unknown/browser.js", "export default 1;");
		var fallbackTarget = fixture.CreateFile("unknown/default.js", "export default 2;");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, validPackage, validSource, esmTarget, cjsTarget, unknownPackage, unknownSource, browserTarget, fallbackTarget],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal("valid/entry.mjs", Assert.Single(result.Edges, item => item.Reference == "valid").Target);
		var inactiveUnknown = Assert.Single(result.Edges, item => item.Reference == "unknown");
		Assert.Equal(ResolutionStatus.Resolved, inactiveUnknown.Status);
		Assert.Equal("unknown/default.js", inactiveUnknown.Target);
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

	[Theory]
	[InlineData("{\"compilerOptions\":null}", DependencyConfigurationState.Corrupt, "compilerOptions must be an object")]
	[InlineData("{\"compilerOptions\":{", DependencyConfigurationState.Corrupt, "JSON")]
	[InlineData("[]", DependencyConfigurationState.UnsupportedSemantics, "root must be an object")]
	public async Task TypeScriptConfigurationFailures_AreDiagnosedAndLeaveReferencesUnresolved(
		string configurationContent,
		DependencyConfigurationState expectedState,
		string expectedReason)
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", configurationContent);
		var source = fixture.CreateFile("main.ts", "import value from './target.js';");
		var target = fixture.CreateFile("target.ts", "export default 1;");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(fixture.Path, [config, source, target],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, item => item.Source == "main.ts");
		Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
		Assert.Contains(expectedReason, Assert.Single(edge.Reasons), StringComparison.OrdinalIgnoreCase);
		var diagnostic = Assert.Single(result.Coverage.ConfigurationDiagnostics);
		Assert.Equal("tsconfig.json", diagnostic.Path);
		Assert.Equal(expectedState, diagnostic.State);
	}

	[Fact]
	public async Task DependencyConfigurationRead_IsBoundedAndMissingConfigurationRemainsExplicit()
	{
		using var fixture = new TemporaryDirectory();
		var oversized = fixture.CreateFile(
			"tsconfig.json",
			"{\"padding\":\"" + new string('x', FileDependencyConfigurationProvider.MaximumConfigurationBytes) + "\"}");
		var source = fixture.CreateFile("main.ts", "import value from './target.js';");
		var target = fixture.CreateFile("target.ts", "export default 1;");
		var provider = new FileDependencyConfigurationProvider();

		var oversizedConfiguration = await provider.ReadAsync(
			fixture.Path,
			[oversized, source, target],
			TestContext.Current.CancellationToken);
		var oversizedScope = Assert.Single(oversizedConfiguration.Scopes,
			scope => scope.LanguageId == LanguageId.TypeScript && scope.HasConfiguration);
		Assert.Equal(DependencyConfigurationState.UnsupportedSemantics, oversizedScope.ConfigurationState);
		Assert.Contains("byte limit", oversizedScope.ConfigurationDiagnostic, StringComparison.Ordinal);

		var missingConfiguration = await provider.ReadAsync(
			fixture.Path,
			[source, target],
			TestContext.Current.CancellationToken);
		var fallback = Assert.Single(missingConfiguration.Scopes, scope => scope.LanguageId == LanguageId.TypeScript);
		Assert.False(fallback.HasConfiguration);
		Assert.Equal(DependencyConfigurationState.Missing, fallback.ConfigurationState);
		Assert.Empty(missingConfiguration.ConfigurationDiagnostics);
	}

	[Fact]
	public async Task ConfigurationProvider_ReadsEachControlFileOncePerOperation()
	{
		using var fixture = new TemporaryDirectory();
		var package = fixture.CreateFile("package.json", "{\"name\":\"fixture\",\"type\":\"module\"}");
		var rootConfig = fixture.CreateFile("tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var nestedConfig = fixture.CreateFile("nested/tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var reader = new CountingControlFileReader();
		var provider = new FileDependencyConfigurationProvider(reader);

		_ = await provider.ReadAsync(
			fixture.Path,
			[package, rootConfig, nestedConfig],
			TestContext.Current.CancellationToken);

		Assert.Equal(1, reader.CountFor(package));
		Assert.Equal(1, reader.CountFor(rootConfig));
		Assert.Equal(1, reader.CountFor(nestedConfig));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Utf8Bom_PreservesTypeScriptAndPackageConfigurationSemantics(bool includeBom)
	{
		using var fixture = new TemporaryDirectory();
		var package = WriteUtf8ControlFile(
			fixture,
			"package.json",
			"{\"name\":\"fixture\",\"exports\":{\".\":\"./entry.ts\"}}",
			includeBom);
		var rootConfig = WriteUtf8ControlFile(
			fixture,
			"tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}",
			includeBom);
		var nestedConfig = WriteUtf8ControlFile(
			fixture,
			"nested/tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\",\"paths\":{\"alias\":[\"../entry.ts\"]}}}",
			includeBom);
		var rootSource = fixture.CreateFile("main.ts", "import value from 'fixture';");
		var nestedSource = fixture.CreateFile("nested/main.ts", "import value from 'alias';");
		var entry = fixture.CreateFile("entry.ts", "export default 1;");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[package, rootConfig, nestedConfig, rootSource, nestedSource, entry],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Empty(result.Coverage.ConfigurationDiagnostics);
		Assert.Contains(result.Edges, edge => edge.Source == "main.ts" && edge.Target == "entry.ts");
		Assert.Contains(result.Edges, edge => edge.Source == "nested/main.ts" && edge.Target == "entry.ts");
	}

	[Fact]
	public async Task TransientControlFileRead_IsRetriedBeforePublishingAManifestSnapshot()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var source = fixture.CreateFile("main.ts", "import value from './target.js';");
		var target = fixture.CreateFile("target.ts", "export default 1;");
		var reader = new FailOnceControlFileReader(config);
		using var engine = new DependencyFactsEngine(
			new TreeSitterDependencyFactExtractor(),
			new FileDependencyConfigurationProvider(reader));

		var first = await engine.IndexAsync(
			fixture.Path,
			[config, source, target],
			cancellationToken: TestContext.Current.CancellationToken);
		var second = await engine.IndexAsync(
			fixture.Path,
			[config, source, target],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(first.Edges, edge => edge.Source == "main.ts" && edge.Status == ResolutionStatus.Unresolved);
		Assert.Contains(second.Edges, edge => edge.Source == "main.ts" && edge.Target == "target.ts");
		Assert.Equal(2, reader.CountFor(config));
		Assert.False(second.Metrics.ResolutionCacheHit);
	}

	[Fact]
	public async Task StableConfigurationSyntaxFailure_RemainsCacheable()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{\"compilerOptions\":{");
		var source = fixture.CreateFile("main.ts", "import value from './target.js';");
		var target = fixture.CreateFile("target.ts", "export default 1;");
		var reader = new CountingControlFileReader();
		using var engine = new DependencyFactsEngine(
			new TreeSitterDependencyFactExtractor(),
			new FileDependencyConfigurationProvider(reader));

		var first = await engine.IndexAsync(
			fixture.Path,
			[config, source, target],
			cancellationToken: TestContext.Current.CancellationToken);
		var second = await engine.IndexAsync(
			fixture.Path,
			[config, source, target],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(first.Coverage.ConfigurationDiagnostics, item => item.Path == "tsconfig.json");
		Assert.Equal(1, reader.CountFor(config));
		Assert.True(second.Metrics.ResolutionCacheHit);
	}

	[Fact]
	public async Task WindowsSharingViolationOnControlFile_IsRetriedAfterTheFileIsReleased()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Skip("An exclusive Windows sharing lock is required for this scenario.");
			return;
		}
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var source = fixture.CreateFile("main.ts", "import value from './target.js';");
		var target = fixture.CreateFile("target.ts", "export default 1;");
		using var engine = CreateEngine();
		DependencyIndexSnapshot first;
		await using (var locked = new FileStream(config, FileMode.Open, FileAccess.Read, FileShare.None))
		{
			first = await engine.IndexAsync(
				fixture.Path,
				[config, source, target],
				cancellationToken: TestContext.Current.CancellationToken);
		}

		var second = await engine.IndexAsync(
			fixture.Path,
			[config, source, target],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(first.Coverage.ConfigurationDiagnostics, item => item.Path == "tsconfig.json");
		Assert.All(first.Coverage.ConfigurationDiagnostics, item =>
			Assert.Equal("configuration file could not be read", item.Reason));
		Assert.Contains(second.Edges, edge => edge.Source == "main.ts" && edge.Target == "target.ts");
		Assert.Empty(second.Coverage.ConfigurationDiagnostics);
		Assert.False(second.Metrics.ResolutionCacheHit);
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
	public async Task WithoutCompilationConfiguration_ALiteralRelativeSpecifierNamingAManifestFileResolves()
	{
		using var fixture = new TemporaryDirectory();
		var helper = fixture.CreateFile("lib/helper.mjs", "export const helper = 1;");
		var typed = fixture.CreateFile("lib/typed.ts", "export const typed = 1;");
		var plain = fixture.CreateFile("plain.js", "export const plain = 1;");
		var reexport = fixture.CreateFile("lib/reexport.js", "export { typed } from './typed.ts';");
		var entry = fixture.CreateFile("app/entry.js", """
			import { helper } from '../lib/helper.mjs';
			import { typed } from '../lib/typed.ts';
			import { plain } from '../plain.js';
			const lazy = await import('../plain.js');
			""");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[helper, typed, plain, reexport, entry],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.All(
			new[] { "../lib/helper.mjs", "../lib/typed.ts", "../plain.js" },
			reference => Assert.Contains(
				result.Edges, edge => edge.Source == "app/entry.js" &&
					edge.Reference == reference &&
					edge.Status == ResolutionStatus.Resolved &&
					edge.Reasons.Contains("relative specifier names a file in the manifest")));
		// Re-export and dynamic import reach the same rule as a static import.
		Assert.Contains(result.Edges, edge => edge.Source == "lib/reexport.js" &&
			edge.Reference == "./typed.ts" && edge.Status == ResolutionStatus.Resolved &&
			edge.Target == "lib/typed.ts");
		Assert.Contains(result.Edges, edge => edge.Source == "app/entry.js" &&
			edge.Reference == "../plain.js" && edge.Target == "plain.js");
	}

	[Fact]
	public async Task WithoutCompilationConfiguration_ADirectoryResolvesOnlyWhenOneIndexFileIsUncontested()
	{
		using var fixture = new TemporaryDirectory();
		var single = fixture.CreateFile("single/index.ts", "export const single = 1;");
		var declaration = fixture.CreateFile("typings/index.d.ts", "export declare const typed: number;");
		var firstOfTwo = fixture.CreateFile("both/index.ts", "export const both = 1;");
		var secondOfTwo = fixture.CreateFile("both/index.js", "export const both = 1;");
		var siblingFile = fixture.CreateFile("util.js", "export const util = 1;");
		var siblingIndex = fixture.CreateFile("util/index.js", "export const util = 2;");
		var packaged = fixture.CreateFile("pkg/package.json", "{ \"main\": \"./dist/entry.js\" }");
		var packagedIndex = fixture.CreateFile("pkg/index.js", "export const pkg = 1;");
		var entry = fixture.CreateFile("entry.js", """
			import { single } from './single';
			import { typed } from './typings';
			import { both } from './both';
			import { util } from './util';
			import { pkg } from './pkg';
			""");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[single, declaration, firstOfTwo, secondOfTwo, siblingFile, siblingIndex, packaged, packagedIndex, entry],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.All(
			new[]
			{
				("./single", "single/index.ts"),
				("./typings", "typings/index.d.ts")
			},
			expected => Assert.Contains(
				result.Edges, edge => edge.Source == "entry.js" &&
					edge.Reference == expected.Item1 &&
					edge.Status == ResolutionStatus.Resolved &&
					edge.Target == expected.Item2 &&
					edge.Reasons.Contains("relative specifier names a directory with one index file")));
		// Two index files, a sibling module of the same stem, and a directory that owns
		// package.json are all choices only the configuration could make.
		Assert.All(
			new[] { "./both", "./util", "./pkg" },
			reference => Assert.Contains(
				result.Edges, edge => edge.Source == "entry.js" &&
					edge.Reference == reference &&
					edge.Status == ResolutionStatus.Unresolved &&
					edge.Target is null &&
					edge.Reasons.Contains("no owning tsconfig.json or jsconfig.json in the manifest")));
	}

	[Fact]
	public async Task WithoutCompilationConfiguration_NothingBeyondTheLiteralNameIsGuessed()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("src/value.ts", "export const value = 1;");
		var built = fixture.CreateFile("dist/value.js", "export const value = 1;");
		var dotted = fixture.CreateFile(".config/app.js", "export const app = 1;");
		var entry = fixture.CreateFile("src/entry.ts", """
			import { value } from './value';
			import { built } from '../dist/value.mjs';
			import { app } from '.config/app.js';
			import { lodash } from 'lodash';
			""");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[source, built, dotted, entry],
			cancellationToken: TestContext.Current.CancellationToken);

		// No extension substitution, no counterpart file, no bare specifier that merely starts
		// with a dot, and no package name.
		Assert.All(
			new[] { "./value", "../dist/value.mjs", ".config/app.js", "lodash" },
			reference => Assert.Contains(
				result.Edges, edge => edge.Source == "src/entry.ts" &&
					edge.Reference == reference &&
					edge.Status == ResolutionStatus.Unresolved &&
					edge.Target is null &&
					edge.Reasons.Contains("no owning tsconfig.json or jsconfig.json in the manifest")));
	}

	[Fact]
	public async Task WithoutCompilationConfiguration_RequireKeepsItsCommonJsContextRule()
	{
		using var fixture = new TemporaryDirectory();
		var package = fixture.CreateFile("package.json", "{ \"type\": \"module\" }");
		var target = fixture.CreateFile("value.js", "module.exports = 1;");
		var moduleSource = fixture.CreateFile("main.mjs", "const value = require('./value.js');");
		var commonJsSource = fixture.CreateFile("worker.cjs", "const value = require('./value.js');");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[package, target, moduleSource, commonJsSource],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Edges, edge => edge.Source == "worker.cjs" &&
			edge.Reference == "./value.js" && edge.Status == ResolutionStatus.Resolved &&
			edge.Target == "value.js");
		Assert.Contains(result.Edges, edge => edge.Source == "main.mjs" &&
			edge.Reference == "./value.js" && edge.Status == ResolutionStatus.Unresolved &&
			edge.Target is null &&
			edge.Reasons.Contains("no owning tsconfig.json or jsconfig.json in the manifest"));
	}

	[Fact]
	public async Task WithoutOwningConfiguration_AFileOutsideEveryConfigResolvesAcrossScopes()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("packages/web/tsconfig.json", "{ }");
		var owned = fixture.CreateFile("packages/web/src/app.ts", "export const app = 1;");
		var unowned = fixture.CreateFile(
			"tools/build.js",
			"import { app } from '../packages/web/src/app.ts';");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, owned, unowned],
			cancellationToken: TestContext.Current.CancellationToken);

		// The tsconfig owns packages/web only, so tools/build.js has no owning configuration
		// and the edge it produces crosses into the configured scope.
		Assert.Contains(result.Edges, edge => edge.Source == "tools/build.js" &&
			edge.Reference == "../packages/web/src/app.ts" &&
			edge.Status == ResolutionStatus.Resolved &&
			edge.Target == "packages/web/src/app.ts" && edge.CrossScope);
	}

	[Fact]
	public async Task WithCompilationConfiguration_TheCounterpartProbeStillOutranksTheLiteralFile()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("tsconfig.json", "{ }");
		var typed = fixture.CreateFile("value.ts", "export const value = 1;");
		var literal = fixture.CreateFile("value.js", "export const value = 2;");
		var entry = fixture.CreateFile("entry.ts", "import { value } from './value.js';");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, typed, literal, entry],
			cancellationToken: TestContext.Current.CancellationToken);

		// With configuration the .ts counterpart wins over the literally named .js file;
		// the no-configuration rule would have taken value.js instead.
		Assert.Contains(result.Edges, edge => edge.Source == "entry.ts" &&
			edge.Reference == "./value.js" && edge.Status == ResolutionStatus.Resolved &&
			edge.Target == "value.ts" &&
			edge.Reasons.Contains("one module target under configured module resolution"));
	}

	[Fact]
	public async Task GoFacts_ResolveNamesDeclaredInTheSamePackageDirectory()
	{
		using var fixture = new TemporaryDirectory();
		var model = fixture.CreateFile("store/model.go", """
			package store

			type Record struct {
				Name string
			}
			""");
		var service = fixture.CreateFile("store/service.go", """
			package store

			func Load() Record {
				return Record{}
			}
			""");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[model, service],
			cancellationToken: TestContext.Current.CancellationToken);

		// A Go package is a directory, so a sibling file needs no import to use the name.
		Assert.Contains(result.Edges, edge => edge.Source == "store/service.go" &&
			edge.Reference == "Record" && edge.Status == ResolutionStatus.Resolved &&
			edge.Target == "store/model.go");
		Assert.All(
			result.Files,
			file => Assert.Equal(DependencyFileStatus.Supported, file.Status));
	}

	[Fact]
	public async Task GoFacts_DoNotReachAcrossPackagesOrResolveImportPaths()
	{
		using var fixture = new TemporaryDirectory();
		var first = fixture.CreateFile("alpha/kind.go", """
			package alpha

			type Shared struct {
			}
			""");
		var second = fixture.CreateFile("beta/kind.go", """
			package beta

			type Shared struct {
			}
			""");
		var consumer = fixture.CreateFile("beta/use.go", """
			package beta

			import "example.com/module/alpha"

			func Use() Shared {
				return Shared{}
			}
			""");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[first, second, consumer],
			cancellationToken: TestContext.Current.CancellationToken);

		// The same name exists in two packages; only the one in this directory is a candidate.
		Assert.Contains(result.Edges, edge => edge.Source == "beta/use.go" &&
			edge.Reference == "Shared" && edge.Status == ResolutionStatus.Resolved &&
			edge.Target == "beta/kind.go");
		Assert.DoesNotContain(result.Edges, edge => edge.Source == "beta/use.go" &&
			edge.Target == "alpha/kind.go");
		// Import paths are outside this capability and produce no edge at all.
		Assert.DoesNotContain(result.Edges, edge => edge.Source == "beta/use.go" &&
			edge.Reference.Contains("example.com", StringComparison.Ordinal));
	}
	[Fact]
	public async Task GoFacts_IgnorePredeclaredTypesDeclarationNamesAndOtherPackages()
	{
		using var fixture = new TemporaryDirectory();
		var alpha = fixture.CreateFile("alpha/kind.go", """
			package alpha

			type Shared struct {
			}
			""");
		var beta = fixture.CreateFile("beta/kind.go", """
			package beta

			type Shared struct {
				Name string
				Count int
			}
			""");
		var consumer = fixture.CreateFile("beta/use.go", """
			package beta

			import "example.com/module/alpha"

			func Borrow() alpha.Shared {
				return alpha.Shared{}
			}
			""");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[alpha, beta, consumer],
			cancellationToken: TestContext.Current.CancellationToken);

		// A declaration's own name is not a reference to itself.
		Assert.DoesNotContain(result.Edges, edge => edge.Source == "beta/kind.go" &&
			edge.Target == "beta/kind.go");
		// Predeclared types name no file and produce no edge.
		Assert.DoesNotContain(result.Edges, edge =>
			edge.Reference == "string" || edge.Reference == "int");
		// A qualified reference names another package, which is outside this capability, so it is
		// not matched against the same-named type in this directory.
		Assert.DoesNotContain(result.Edges, edge => edge.Source == "beta/use.go" &&
			edge.Reference == "Shared");
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
	public async Task PythonImport_PrefersARegularPackageOverTheSameNamedModule()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var module = fixture.CreateFile("pkg.py", "value = 'module'");
		var initializer = fixture.CreateFile("pkg/__init__.py", "value = 'package'");
		var consumer = fixture.CreateFile("consumer.py", "import pkg");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(fixture.Path, [config, module, initializer, consumer],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, item => item.Source == "consumer.py" && item.Reference == "pkg");
		Assert.Equal("pkg/__init__.py", edge.Target);
		Assert.DoesNotContain("pkg.py", edge.Candidates);
	}

	[Fact]
	public async Task PythonFromImport_PrefersAStaticPackageBindingOverAChildModule()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var initializer = fixture.CreateFile("pkg/__init__.py", "from .impl import Service");
		var implementation = fixture.CreateFile("pkg/impl.py", "class Service: pass");
		var child = fixture.CreateFile("pkg/Service.py", "class Wrong: pass");
		var consumer = fixture.CreateFile("consumer.py", "from pkg import Service");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, initializer, implementation, child, consumer],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, item => item.Source == "consumer.py" && item.Reference == "pkg");
		Assert.Equal("pkg/impl.py", edge.Target);
		Assert.DoesNotContain("pkg/Service.py", edge.Candidates);
	}

	[Fact]
	public async Task PythonFromImport_ResolvesNamesProvidedByAnOrdinaryModule()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var initializer = fixture.CreateFile("pkg/__init__.py", string.Empty);
		var model = fixture.CreateFile("pkg/model.py", "class Item: pass\ndef create(): pass");
		var consumer = fixture.CreateFile("pkg/consumer.py", "from .model import Item as ModelItem\nfrom .model import create\nimport pkg.model");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, initializer, model, consumer],
			cancellationToken: TestContext.Current.CancellationToken);

		var moduleImport = Assert.Single(result.Edges, item => item.Source == "pkg/consumer.py" && item.Reference == "model");
		Assert.Equal(ResolutionStatus.Resolved, moduleImport.Status);
		Assert.Equal("pkg/model.py", moduleImport.Target);
		Assert.Equal(2, moduleImport.Evidence.Count);
		var directImport = Assert.Single(result.Edges, item => item.Source == "pkg/consumer.py" && item.Reference == "pkg.model");
		Assert.Equal("pkg/model.py", directImport.Target);
	}

	[Fact]
	public async Task PythonFromImport_ResolvesANameProvidedByAStubModule()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var model = fixture.CreateFile("model.pyi", "class Item: ...");
		var consumer = fixture.CreateFile("consumer.py", "from model import Item");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, model, consumer],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, item => item.Source == "consumer.py");
		Assert.Equal(ResolutionStatus.Resolved, edge.Status);
		Assert.Equal("model.pyi", edge.Target);
	}

	[Fact]
	public async Task PythonFromImport_DoesNotProbeAChildOfAnOrdinaryModuleForAMissingName()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var model = fixture.CreateFile("model.py", "class Present: pass");
		var falseChild = fixture.CreateFile("model/Missing.py", "class Wrong: pass");
		var consumer = fixture.CreateFile("consumer.py", "from model import Missing");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[config, model, falseChild, consumer],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, item => item.Source == "consumer.py");
		Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
		Assert.Null(edge.Target);
		Assert.DoesNotContain("model/Missing.py", edge.Candidates);
	}

	[Fact]
	public async Task PythonRelativeImport_RejectsTraversalBeyondTheTopLevelPackage()
	{
		using var fixture = new TemporaryDirectory();
		var config = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var initializer = fixture.CreateFile("pkg/__init__.py", string.Empty);
		var consumer = fixture.CreateFile("pkg/consumer.py", "from .. import target");
		var target = fixture.CreateFile("target.py", "value = 1");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(fixture.Path, [config, initializer, consumer, target],
			cancellationToken: TestContext.Current.CancellationToken);

		var edge = Assert.Single(result.Edges, item => item.Source == "pkg/consumer.py");
		Assert.Equal(ResolutionStatus.Unresolved, edge.Status);
		Assert.Null(edge.Target);
		Assert.Contains("beyond the top-level package", Assert.Single(edge.Reasons), StringComparison.Ordinal);
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
	public async Task ManifestSnapshotBuiltWithoutIdentitiesIsNotReusedByIdentityAwareRequest()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var source = fixture.CreateFile("Source.cs", "public class Source { }");
		var provider = new CountingConfigurationProvider();
		using var engine = new DependencyFactsEngine(new TreeSitterDependencyFactExtractor(), provider);
		var manifest = new[] { project, source };

		_ = await engine.IndexAsync(
			fixture.Path,
			manifest,
			cancellationToken: TestContext.Current.CancellationToken);
		var identityAware = await engine.IndexAsync(
			fixture.Path,
			manifest,
			cancellationToken: TestContext.Current.CancellationToken,
			contentIdentities: Identities((project, "project-v1"), (source, "source-v1")));

		Assert.Equal(2, provider.ReadCount);
		Assert.True(identityAware.Metrics.ResolutionCacheHit);
	}

	[Fact]
	public async Task ManifestSnapshotIdentityIsOptionalForLaterMetadataOnlyRequest()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var source = fixture.CreateFile("Source.cs", "public class Source { }");
		var provider = new CountingConfigurationProvider();
		using var engine = new DependencyFactsEngine(new TreeSitterDependencyFactExtractor(), provider);
		var manifest = new[] { project, source };

		_ = await engine.IndexAsync(
			fixture.Path,
			manifest,
			cancellationToken: TestContext.Current.CancellationToken,
			contentIdentities: Identities((project, "project-v1"), (source, "source-v1")));
		var metadataOnly = await engine.IndexAsync(
			fixture.Path,
			manifest,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(1, provider.ReadCount);
		Assert.True(metadataOnly.Metrics.ResolutionCacheHit);
	}

	[Fact]
	public async Task ManifestSnapshotIdentityAwareHotPathReusesUnchangedResolution()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var source = fixture.CreateFile("Source.cs", "public class Source { }");
		var provider = new CountingConfigurationProvider();
		using var engine = new DependencyFactsEngine(new TreeSitterDependencyFactExtractor(), provider);
		var manifest = new[] { project, source };
		var identities = Identities((project, "project-v1"), (source, "source-v1"));

		_ = await engine.IndexAsync(
			fixture.Path,
			manifest,
			cancellationToken: TestContext.Current.CancellationToken,
			contentIdentities: identities);
		var warm = await engine.IndexAsync(
			fixture.Path,
			manifest,
			cancellationToken: TestContext.Current.CancellationToken,
			contentIdentities: identities);

		Assert.Equal(1, provider.ReadCount);
		Assert.True(warm.Metrics.ResolutionCacheHit);
	}

	[Fact]
	public async Task CSharpResolution_UsesLanguageVisibilityWithoutProjectWideNameFallback()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var hiddenTask = fixture.CreateFile("CompanyTask.cs", "namespace Company.Internal; public sealed class Task { }\n");
		var nestedWidget = fixture.CreateFile("NestedWidget.cs", "namespace Parent.Child; public sealed class Widget { }\n");
		var enclosing = fixture.CreateFile("Envelope.cs", "namespace Parent; public sealed class Envelope { }\n");
		var consumer = fixture.CreateFile("Consumer.cs", """
			using System.Threading.Tasks;
			using Parent;
			namespace App;
			public sealed class Consumer
			{
				public Task ExternalTask { get; }
				public Widget HiddenChild { get; }
				public Company.Internal.Task Qualified { get; }
			}
			""");
		var childConsumer = fixture.CreateFile(
			"ChildConsumer.cs",
			"namespace Parent.Child; public sealed class Consumer { public Envelope Value { get; } }\n");
		var nestedConsumer = fixture.CreateFile(
			"NestedConsumer.cs",
			"""
			namespace Nest;
			public class Outer
			{
				public class Inner { }
				public class Consumer
				{
					public Inner Value { get; }
				}
			}
			""");
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(
			fixture.Path,
			[project, hiddenTask, nestedWidget, enclosing, consumer, childConsumer, nestedConsumer],
			cancellationToken: TestContext.Current.CancellationToken);

		var externalTask = Assert.Single(index.Edges, edge => edge.Source == "Consumer.cs" && edge.Reference == "Task");
		Assert.Equal(ResolutionStatus.External, externalTask.Status);
		Assert.Null(externalTask.Target);
		Assert.Empty(externalTask.Candidates);
		var hiddenChild = Assert.Single(index.Edges, edge => edge.Source == "Consumer.cs" && edge.Reference == "Widget");
		Assert.Equal(ResolutionStatus.Unresolved, hiddenChild.Status);
		Assert.Null(hiddenChild.Target);
		Assert.Contains(index.Edges, edge => edge.Source == "Consumer.cs" &&
			edge.Reference == "Company.Internal.Task" && edge.Target == "CompanyTask.cs");
		Assert.Contains(index.Edges, edge => edge.Source == "ChildConsumer.cs" &&
			edge.Reference == "Envelope" && edge.Target == "Envelope.cs");
		Assert.Contains(index.Edges, edge => edge.Source == "NestedConsumer.cs" &&
			edge.Reference == "Inner" && edge.Target == "NestedConsumer.cs");
	}

	[Fact]
	public async Task CSharpResolution_KeepsNestedTypesOutOfNamespaceLookupAndUsesTheNearestContainingType()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var holder = fixture.CreateFile("Holder.cs", """
			namespace App;
			public partial class Holder { public class Task { } }
			""");
		var holderConsumer = fixture.CreateFile("HolderConsumer.cs", """
			namespace App;
			public partial class Holder { public Task Inside { get; } }
			""");
		var consumer = fixture.CreateFile("Consumer.cs", """
			using System.Threading.Tasks;
			namespace App;
			public sealed class Consumer
			{
				public Task External { get; }
				public Holder.Task Qualified { get; }
			}
			""");
		var globalHolder = fixture.CreateFile(
			"GlobalHolder.cs",
			"public sealed class GlobalHolder { public class Task { } }\n");
		var globalConsumer = fixture.CreateFile(
			"GlobalConsumer.cs",
			"using System.Threading.Tasks; public sealed class GlobalConsumer { public Task Value { get; } }\n");
		var outerTask = fixture.CreateFile("OuterTask.cs", """
			namespace App;
			public partial class Outer { public class Task { } }
			""");
		var innerTask = fixture.CreateFile("InnerTask.cs", """
			namespace App;
			public partial class Outer { public partial class Inner { public class Task { } } }
			""");
		var outerConsumer = fixture.CreateFile("OuterConsumer.cs", """
			namespace App;
			public partial class Outer { public Task Value { get; } }
			""");
		var innerConsumer = fixture.CreateFile("InnerConsumer.cs", """
			namespace App;
			public partial class Outer { public partial class Inner { public Task Value { get; } } }
			""");
		using var engine = CreateEngine();

		var index = await engine.IndexAsync(
			fixture.Path,
			[project, holder, holderConsumer, consumer, globalHolder, globalConsumer,
				outerTask, innerTask, outerConsumer, innerConsumer],
			cancellationToken: TestContext.Current.CancellationToken);

		var appExternal = Assert.Single(index.Edges, edge =>
			edge.Source == "Consumer.cs" && edge.Reference == "Task");
		Assert.Equal(ResolutionStatus.External, appExternal.Status);
		Assert.DoesNotContain("Holder.cs", appExternal.Candidates);
		Assert.Contains(index.Edges, edge => edge.Source == "Consumer.cs" &&
			edge.Reference == "Holder.Task" && edge.Target == "Holder.cs");
		Assert.Contains(index.Edges, edge => edge.Source == "HolderConsumer.cs" &&
			edge.Reference == "Task" && edge.Target == "Holder.cs");

		var globalExternal = Assert.Single(index.Edges, edge =>
			edge.Source == "GlobalConsumer.cs" && edge.Reference == "Task");
		Assert.Equal(ResolutionStatus.External, globalExternal.Status);
		Assert.DoesNotContain("GlobalHolder.cs", globalExternal.Candidates);
		Assert.Contains(index.Edges, edge => edge.Source == "OuterConsumer.cs" &&
			edge.Reference == "Task" && edge.Target == "OuterTask.cs");
		Assert.Contains(index.Edges, edge => edge.Source == "InnerConsumer.cs" &&
			edge.Reference == "Task" && edge.Target == "InnerTask.cs");
	}

	[Fact]
	public async Task TransientExtractionFailure_IsRetriedAtThePreparedAndManifestCacheLayers()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("Source.cs", "public sealed class Source { }\n");
		var extractor = new FailOnceDependencyFactExtractor();
		using var engine = new DependencyFactsEngine(extractor, new EmptyDependencyConfigurationProvider());

		var failed = await engine.IndexAsync(
			fixture.Path,
			[source],
			cancellationToken: TestContext.Current.CancellationToken);
		var recovered = await engine.IndexAsync(
			fixture.Path,
			[source],
			cancellationToken: TestContext.Current.CancellationToken);
		var warm = await engine.IndexAsync(
			fixture.Path,
			[source],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(DependencyFileStatus.ExtractionFailed, Assert.Single(failed.Files).Status);
		Assert.Equal(DependencyFileStatus.Supported, Assert.Single(recovered.Files).Status);
		Assert.False(recovered.Metrics.ResolutionCacheHit);
		Assert.True(warm.Metrics.ResolutionCacheHit);
		Assert.Equal(2, extractor.PrepareCount);
	}

	[Fact]
	public async Task WindowsExclusiveSourceLock_IsRetriedAfterTheLockIsReleased()
	{
		if (!OperatingSystem.IsWindows())
			Assert.Skip("Exclusive source locking has the required access-denied behavior only on Windows.");
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var source = fixture.CreateFile("Source.cs", "public sealed class Source { }\n");
		using var engine = CreateEngine();
		DependencyIndexSnapshot failed;
		using (File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
		{
			failed = await engine.IndexAsync(
				fixture.Path,
				[project, source],
				cancellationToken: TestContext.Current.CancellationToken);
		}

		var recovered = await engine.IndexAsync(
			fixture.Path,
			[project, source],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(DependencyFileStatus.ExtractionFailed,
			Assert.Single(failed.Files, file => file.Path == "Source.cs").Status);
		Assert.Equal(DependencyFileStatus.Supported,
			Assert.Single(recovered.Files, file => file.Path == "Source.cs").Status);
		Assert.False(recovered.Metrics.ResolutionCacheHit);
	}

	[Fact]
	public async Task PreparedSourceCacheIdentityMismatchReadsSameStampReplacement()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		const string original = "public class Use { Alpha Value; }";
		const string replacement = "public class Use { Bravo Value; }";
		var source = fixture.CreateFile("Use.cs", original);
		var provider = new FileDependencyConfigurationProvider();
		var configuration = await provider.ReadAsync(
			fixture.Path,
			[project, source],
			TestContext.Current.CancellationToken);
		using var extractor = new TreeSitterDependencyFactExtractor();

		var first = await extractor.PrepareAsync(
			fixture.Path,
			source,
			configuration,
			new DependencyFactsLimits(),
			TestContext.Current.CancellationToken,
			"source-v1");
		var warm = await extractor.PrepareAsync(
			fixture.Path,
			source,
			configuration,
			new DependencyFactsLimits(),
			TestContext.Current.CancellationToken,
			"source-v1");
		ReplaceWithSameFileStampOrSkip(source, replacement);
		var changed = await extractor.PrepareAsync(
			fixture.Path,
			source,
			configuration,
			new DependencyFactsLimits(),
			TestContext.Current.CancellationToken,
			"source-v2");

		Assert.Equal(original, first.Source);
		Assert.Same(first.Source, warm.Source);
		Assert.Equal(replacement, changed.Source);
		Assert.NotEqual(first.ContentFingerprint, changed.ContentFingerprint);
	}

	[Fact]
	public async Task PreparedSourceCacheWithoutIdentityKeepsMetadataOnlyBehavior()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		const string original = "public class Use { Alpha Value; }";
		const string replacement = "public class Use { Bravo Value; }";
		var source = fixture.CreateFile("Use.cs", original);
		var provider = new FileDependencyConfigurationProvider();
		var configuration = await provider.ReadAsync(
			fixture.Path,
			[project, source],
			TestContext.Current.CancellationToken);
		using var extractor = new TreeSitterDependencyFactExtractor();

		var first = await extractor.PrepareAsync(
			fixture.Path,
			source,
			configuration,
			new DependencyFactsLimits(),
			TestContext.Current.CancellationToken);
		ReplaceWithSameFileStampOrSkip(source, replacement);
		var metadataOnly = await extractor.PrepareAsync(
			fixture.Path,
			source,
			configuration,
			new DependencyFactsLimits(),
			TestContext.Current.CancellationToken);

		Assert.Equal(original, first.Source);
		Assert.Same(first.Source, metadataOnly.Source);
	}

	[Fact]
	public async Task PreparedSourceCache_TransientFailuresDoNotAccumulateEvictionEntries()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("Source.cs", "public sealed class Source { }\n");
		var analyzer = new TransientPreparedSourceAnalyzer();
		using var extractor = new TreeSitterDependencyFactExtractor(new MissingGrammarLocator(), analyzer);
		var configuration = EmptyConfiguration();

		for (var attempt = 0; attempt < 10_000; attempt++)
		{
			var prepared = await extractor.PrepareAsync(
				fixture.Path,
				source,
				configuration,
				new DependencyFactsLimits(),
				TestContext.Current.CancellationToken,
				$"attempt-{attempt}");
			Assert.False(prepared.CanCache);
		}

		Assert.Equal(10_000, analyzer.OpenCount);
		Assert.Equal(new TreeSitterDependencyFactExtractor.PreparedSourceCacheState(0, 0, 0), extractor.CacheState);
	}

	[Fact]
	public async Task PreparedSourceCache_StaleCompletionCannotRemoveReplacementWeight()
	{
		using var fixture = new TemporaryDirectory();
		var source = fixture.CreateFile("Source.cs", "public sealed class Source { }\n");
		var analyzer = new CoordinatedPreparedSourceAnalyzer();
		using var extractor = new TreeSitterDependencyFactExtractor(new MissingGrammarLocator(), analyzer);
		var configuration = EmptyConfiguration();

		var staleTask = extractor.PrepareAsync(
			fixture.Path,
			source,
			configuration,
			new DependencyFactsLimits(),
			TestContext.Current.CancellationToken,
			"identity-v1").AsTask();
		await analyzer.FirstReadStarted.Task.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.Current.CancellationToken);

		var replacement = await extractor.PrepareAsync(
			fixture.Path,
			source,
			configuration,
			new DependencyFactsLimits(),
			TestContext.Current.CancellationToken,
			"identity-v2");
		var replacementState = extractor.CacheState;
		analyzer.ReleaseFirstRead();
		var stale = await staleTask;
		var afterStaleCompletion = extractor.CacheState;
		var warm = await extractor.PrepareAsync(
			fixture.Path,
			source,
			configuration,
			new DependencyFactsLimits(),
			TestContext.Current.CancellationToken,
			"identity-v2");

		Assert.Equal("new source", replacement.Source);
		Assert.Equal("old source", stale.Source);
		Assert.Same(replacement.Source, warm.Source);
		Assert.Equal(2, analyzer.OpenCount);
		Assert.Equal(1, replacementState.Entries);
		Assert.Equal(1, replacementState.EvictionEntries);
		Assert.True(replacementState.RetainedBytes > 0);
		Assert.Equal(replacementState, afterStaleCompletion);
		Assert.Equal(replacementState, extractor.CacheState);
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
	public async Task ManifestSnapshot_LengthPrefixesPathsAndVerifiesTheCanonicalManifest()
	{
		if (OperatingSystem.IsWindows())
			Assert.Skip("Windows does not permit line-feed characters in file names.");
		using var fixture = new TemporaryDirectory();
		var backing = fixture.CreateFile("backing", "public sealed class Shared { }\n");
		var a = Path.Combine(fixture.Path, "A.cs");
		var bc = Path.Combine(fixture.Path, "B.cs\nC.cs");
		var ab = Path.Combine(fixture.Path, "A.cs\nB.cs");
		var c = Path.Combine(fixture.Path, "C.cs");
		foreach (var link in new[] { a, bc, ab, c })
			CreateHardLinkOrSkip(link, backing);
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var seed = fixture.CreateFile("Seed.cs", "public sealed class Seed { }\n");
		using var engine = CreateEngine();

		_ = await engine.FindRelatedAsync(
			fixture.Path,
			[a, bc, project, seed],
			["Seed.cs"],
			cancellationToken: TestContext.Current.CancellationToken);
		var second = await engine.FindRelatedAsync(
			fixture.Path,
			[ab, c, project, seed],
			["Seed.cs"],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.DoesNotContain(second.Index.Files, file => file.Path is "A.cs" or "B.cs\nC.cs");
		Assert.Contains(second.Index.Files, file => file.Path == "A.cs\nB.cs");
		Assert.Contains(second.Index.Files, file => file.Path == "C.cs");
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
		Assert.Equal(0, extractor.ParseCount);
	}

	[Theory]
	[InlineData("utf8")]
	[InlineData("utf16-le")]
	[InlineData("utf16-be")]
	public async Task BoundedDependencySourceRead_StopsAfterTheDecodedCharacterLimit(string encodingName)
	{
		const int maximumCharacters = 100_000;
		using var fixture = new TemporaryDirectory();
		Encoding encoding = encodingName switch
		{
			"utf8" => new UTF8Encoding(true, true),
			"utf16-le" => new UnicodeEncoding(false, true, true),
			"utf16-be" => new UnicodeEncoding(true, true, true),
			_ => throw new ArgumentOutOfRangeException(nameof(encodingName))
		};
		var bytes = encoding.GetPreamble()
			.Concat(encoding.GetBytes(new string('x', maximumCharacters * 3)))
			.ToArray();
		var path = fixture.CreateFile("Large.cs", string.Empty);
		File.WriteAllBytes(path, bytes);
		var reader = new TreeSitterDependencyFactExtractor.BoundedDependencySourceReader();

		var result = await reader.ReadAsync(path, maximumCharacters, TestContext.Current.CancellationToken);

		Assert.Equal(DependencyFileStatus.ExtractionFailed, result.Status);
		Assert.Contains("character parse limit", result.StatusReason, StringComparison.Ordinal);
		Assert.InRange(reader.LastBytesRead, 1, bytes.Length - 1L);
	}

	[Fact]
	public async Task BoundedDependencySourceRead_PreservesBomTextAndRejectsAnIncompleteSequence()
	{
		using var fixture = new TemporaryDirectory();
		var unicode = new UnicodeEncoding(false, true, true);
		var validBytes = unicode.GetPreamble().Concat(unicode.GetBytes("class Valid { }")).ToArray();
		var valid = fixture.CreateFile("Valid.cs", string.Empty);
		File.WriteAllBytes(valid, validBytes);
		var incomplete = fixture.CreateFile("Incomplete.cs", string.Empty);
		File.WriteAllBytes(incomplete, [0x63, 0x6c, 0x61, 0x73, 0x73, 0x20, 0xE2, 0x82]);
		var reader = new TreeSitterDependencyFactExtractor.BoundedDependencySourceReader();

		var validResult = await reader.ReadAsync(valid, 100, TestContext.Current.CancellationToken);
		var incompleteResult = await reader.ReadAsync(incomplete, 100, TestContext.Current.CancellationToken);

		Assert.Equal(DependencyFileStatus.Supported, validResult.Status);
		Assert.Equal("class Valid { }", validResult.Source);
		Assert.Equal(DependencyFileStatus.ExtractionFailed, incompleteResult.Status);
		Assert.Contains("unsupported encoding", incompleteResult.StatusReason, StringComparison.Ordinal);
	}

	[Fact]
	public async Task BoundedDependencySourceRead_SizesPooledBuffersWithoutRetainingTheFileLimit()
	{
		const int maximumCharacters = 2 * 1024 * 1024;
		using var fixture = new TemporaryDirectory();
		var small = fixture.CreateFile("Small.cs", "public class Small { }");
		var large = fixture.CreateFile("Large.cs", new string('x', 1024 * 1024));
		var reader = new TreeSitterDependencyFactExtractor.BoundedDependencySourceReader();

		var smallAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
		var smallStarted = Stopwatch.StartNew();
		for (var index = 0; index < 500; index++)
		{
			var smallResult = await reader.ReadAsync(small, maximumCharacters, TestContext.Current.CancellationToken);
			Assert.Equal(DependencyFileStatus.Supported, smallResult.Status);
		}
		smallStarted.Stop();
		var smallAllocated = GC.GetTotalAllocatedBytes(precise: true) - smallAllocatedBefore;
		var smallCharacterCapacity = reader.LastCharacterBufferCapacity;
		var smallByteCapacity = reader.LastByteBufferCapacity;

		var workingSetBefore = Process.GetCurrentProcess().WorkingSet64;
		var largeAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
		var largeStarted = Stopwatch.StartNew();
		var largeResult = await reader.ReadAsync(large, maximumCharacters, TestContext.Current.CancellationToken);
		largeStarted.Stop();
		var largeAllocated = GC.GetTotalAllocatedBytes(precise: true) - largeAllocatedBefore;
		var workingSetAfter = Process.GetCurrentProcess().WorkingSet64;
		var largeCharacterCapacity = reader.LastCharacterBufferCapacity;

		var afterLarge = await reader.ReadAsync(small, maximumCharacters, TestContext.Current.CancellationToken);

		Assert.Equal(DependencyFileStatus.Supported, largeResult.Status);
		Assert.Equal(DependencyFileStatus.Supported, afterLarge.Status);
		Assert.InRange(smallCharacterCapacity, 1, 256);
		Assert.InRange(smallByteCapacity, 1, 256);
		Assert.InRange(largeCharacterCapacity, 1, 64 * 1024);
		Assert.InRange(reader.LastCharacterBufferCapacity, 1, 256);
		TestContext.Current.TestOutputHelper?.WriteLine(
			$"Bounded reader: small-500={smallStarted.ElapsedMilliseconds}ms/{smallAllocated}B, " +
			$"large-1MiChars={largeStarted.ElapsedMilliseconds}ms/{largeAllocated}B, " +
			$"char-buffer={largeCharacterCapacity} chars, working-set-delta={workingSetAfter - workingSetBefore}B.");
	}

	[Fact]
	public async Task WarmRelatedQuery_ReusesTheCanonicalFileLookupFromTheResolvedSnapshot()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var target = fixture.CreateFile("Target.cs", "public class Target { }");
		var source = fixture.CreateFile("Source.cs", "public class Source { Target Value; }");
		using var engine = CreateEngine();
		var manifest = new[] { project, source, target };
		var indexed = await engine.IndexAsync(
			fixture.Path,
			manifest,
			cancellationToken: TestContext.Current.CancellationToken);

		var related = await engine.FindRelatedAsync(
			fixture.Path,
			manifest,
			["Source.cs"],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Same(indexed.FileByPath, related.Index.FileByPath);
		Assert.Same(indexed.FileByPath["Source.cs"], related.Index.FileByPath["Source.cs"]);
	}

	[Fact]
	public async Task RelatedFiles_KeepResolvedAndDistinctAmbiguousReferencesInSeparateRows()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var a = fixture.CreateFile("A.cs", """
			namespace Targets { public sealed class ResolvedType { } }
			namespace One { public sealed class SharedAB { } public sealed class SharedAC { } }
			""");
		var b = fixture.CreateFile("B.cs", "namespace Two; public sealed class SharedAB { }\n");
		var c = fixture.CreateFile("C.cs", "namespace Three; public sealed class SharedAC { }\n");
		var seed = fixture.CreateFile("Seed.cs", """
			using Targets;
			using One;
			using Two;
			using Three;
			public sealed class Seed
			{
				public ResolvedType Resolved { get; }
				public SharedAB FirstAmbiguous { get; }
				public SharedAC SecondAmbiguous { get; }
			}
			""");
		using var engine = CreateEngine();

		var fromSeed = await engine.FindRelatedAsync(
			fixture.Path,
			[project, a, b, c, seed],
			["Seed.cs"],
			DependencyDirection.Dependencies,
			cancellationToken: TestContext.Current.CancellationToken);
		var toA = await engine.FindRelatedAsync(
			fixture.Path,
			[project, a, b, c, seed],
			["A.cs"],
			DependencyDirection.Dependents,
			cancellationToken: TestContext.Current.CancellationToken);

		AssertRelatedOverlap(Assert.Single(fromSeed.Seeds).Dependencies, "A.cs", fromSeed.Index.Edges);
		AssertRelatedOverlap(Assert.Single(toA.Seeds).Dependents, "Seed.cs", toA.Index.Edges);
	}

	private static void AssertRelatedOverlap(
		IReadOnlyList<RelatedFile> related,
		string expectedPath,
		IReadOnlyList<DependencyEdge> edges)
	{
		Assert.True(related.Count == 3, JsonSerializer.Serialize(edges));
		Assert.All(related, item => Assert.Equal(expectedPath, item.Path));
		Assert.Single(related, static item => item.Status == ResolutionStatus.Resolved);
		var ambiguous = related.Where(static item => item.Status == ResolutionStatus.Ambiguous).ToArray();
		Assert.Equal(2, ambiguous.Length);
		Assert.Contains(ambiguous, item => item.Candidates.SequenceEqual(["A.cs", "B.cs"]));
		Assert.Contains(ambiguous, item => item.Candidates.SequenceEqual(["A.cs", "C.cs"]));
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
		// Exception messages and resource names stay out of trusted diagnostics.
		Assert.Equal("dependency grammar could not be loaded", failed.StatusReason);
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
	public async Task WorkLimit_AdmitsALaterSmallFileAfterRejectingAnOversizedCandidate()
	{
		using var fixture = new TemporaryDirectory();
		var first = fixture.CreateFile("A.cs", "first");
		var rejected = fixture.CreateFile("B.cs", "rejected");
		var later = fixture.CreateFile("C.cs", "later");
		using var engine = new DependencyFactsEngine(
			new CostedFactExtractor(new Dictionary<string, int>(StringComparer.Ordinal)
			{
				["A.cs"] = 8,
				["B.cs"] = 4,
				["C.cs"] = 1
			}),
			new EmptyDependencyConfigurationProvider(),
			new DependencyFactsLimits(MaximumWorkPerIndex: 10));

		var result = await engine.IndexAsync(
			fixture.Path,
			[first, rejected, later],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(8, result.Edges.Count(edge => edge.Source == "A.cs" && edge.Reference != "<limit>"));
		var limited = Assert.Single(result.Edges, edge => edge.Source == "B.cs");
		Assert.Equal("<limit>", limited.Reference);
		Assert.Contains("index work limit exceeded", limited.Reasons);
		Assert.Single(result.Edges, edge => edge.Source == "C.cs" && edge.Reference != "<limit>");
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

	private static string WriteUtf8ControlFile(
		TemporaryDirectory fixture,
		string relativePath,
		string content,
		bool includeBom)
	{
		var path = fixture.CreateFile(relativePath, string.Empty);
		var encoding = new UTF8Encoding(includeBom, true);
		File.WriteAllBytes(path, encoding.GetPreamble().Concat(encoding.GetBytes(content)).ToArray());
		return path;
	}

	private static DependencyResolverConfiguration EmptyConfiguration() => new(
		"fixture",
		[],
		new Dictionary<string, PackageMapDescriptor>(StringComparer.Ordinal),
		new HashSet<string>(StringComparer.Ordinal),
		new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal),
		new HashSet<string>(StringComparer.Ordinal));

	private static DependencyManifestContentIdentities Identities(
		params (string Path, string Identity)[] values) =>
		new(values.ToDictionary(
			static value => value.Path,
			static value => value.Identity,
			PathComparer.Default));

	private static void ReplaceWithSameFileStampOrSkip(string path, string replacement)
	{
		var before = new FileInfo(path);
		var length = before.Length;
		var lastWrite = before.LastWriteTimeUtc;
		var creation = before.CreationTimeUtc;
		try
		{
			File.WriteAllText(path, replacement, new UTF8Encoding(false));
			File.SetLastWriteTimeUtc(path, lastWrite);
			File.SetCreationTimeUtc(path, creation);
		}
		catch (Exception exception) when (
			exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
		{
			Assert.Skip($"The file system cannot restore creation and write timestamps: {exception.Message}");
			return;
		}

		var after = new FileInfo(path);
		if (after.Length != length || after.LastWriteTimeUtc != lastWrite || after.CreationTimeUtc != creation)
		{
			Assert.Skip(
				$"The file system did not preserve the complete file stamp: " +
				$"length {length}/{after.Length}, mtime {lastWrite:o}/{after.LastWriteTimeUtc:o}, " +
				$"creation {creation:o}/{after.CreationTimeUtc:o}.");
		}
	}

	private static void CreateHardLinkOrSkip(string linkPath, string targetPath)
	{
		var startInfo = new ProcessStartInfo("ln")
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		startInfo.ArgumentList.Add(targetPath);
		startInfo.ArgumentList.Add(linkPath);
		try
		{
			using var process = Process.Start(startInfo);
			if (process is null || !process.WaitForExit(TimeSpan.FromSeconds(10)) || process.ExitCode != 0)
				Assert.Skip("Hard links are unavailable in this test environment.");
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
		{
			Assert.Skip($"Hard links are unavailable: {exception.GetType().Name}.");
		}
	}

	private sealed class CountingConfigurationProvider : IDependencyConfigurationProvider
	{
		private readonly FileDependencyConfigurationProvider _inner = new();

		public int ReadCount { get; private set; }

		public Task<DependencyResolverConfiguration> ReadAsync(
			string sourceRoot,
			IReadOnlyList<string> manifestFiles,
			CancellationToken cancellationToken)
		{
			ReadCount++;
			return _inner.ReadAsync(sourceRoot, manifestFiles, cancellationToken);
		}
	}

	private sealed class CountingControlFileReader : IDependencyControlFileReader
	{
		private readonly BoundedDependencyControlFileReader _inner = new();
		private readonly ConcurrentDictionary<string, int> _counts = new(PathComparer.Default);

		public int CountFor(string path) => _counts.GetValueOrDefault(Path.GetFullPath(path));

		public ValueTask<DependencyControlFileSnapshot> ReadAsync(
			string path,
			int maximumBytes,
			CancellationToken cancellationToken)
		{
			_counts.AddOrUpdate(Path.GetFullPath(path), 1, static (_, count) => count + 1);
			return _inner.ReadAsync(path, maximumBytes, cancellationToken);
		}
	}

	private sealed class FailOnceControlFileReader(string failingPath) : IDependencyControlFileReader
	{
		private readonly BoundedDependencyControlFileReader _inner = new();
		private readonly ConcurrentDictionary<string, int> _counts = new(PathComparer.Default);

		public int CountFor(string path) => _counts.GetValueOrDefault(Path.GetFullPath(path));

		public ValueTask<DependencyControlFileSnapshot> ReadAsync(
			string path,
			int maximumBytes,
			CancellationToken cancellationToken)
		{
			var count = _counts.AddOrUpdate(Path.GetFullPath(path), 1, static (_, value) => value + 1);
			return PathComparer.Default.Equals(path, failingPath) && count == 1
				? ValueTask.FromResult(new DependencyControlFileSnapshot(
					DependencyConfigurationState.Corrupt,
					string.Empty,
					"configuration access failed",
					"transient",
					CanCache: false))
				: _inner.ReadAsync(path, maximumBytes, cancellationToken);
		}
	}

	private sealed class MissingGrammarLocator : IGrammarLibraryLocator
	{
		public string StrategyName => "missing-test-grammar";
		public IReadOnlyList<string> EnumerateLibraries() => [];
		public string Resolve(string libraryBaseName) =>
			throw new FileNotFoundException($"Grammar '{libraryBaseName}' is unavailable.", libraryBaseName);
	}

	private sealed class TransientPreparedSourceAnalyzer : IFileContentAnalyzer
	{
		private int _openCount;

		public int OpenCount => Volatile.Read(ref _openCount);

		public ValueTask<IFileContentSnapshot> OpenCompleteSnapshotAsync(
			string path,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Interlocked.Increment(ref _openCount);
			return ValueTask.FromResult<IFileContentSnapshot>(
				new ClassifiedSnapshot(FileContentClassification.Missing));
		}

		public ValueTask<bool> IsTextFileAsync(string path, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();
		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(string path, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();
		public ValueTask<TextFileContent?> TryReadAsTextAsync(string path, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();
		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class CoordinatedPreparedSourceAnalyzer : IFileContentAnalyzer
	{
		private readonly TaskCompletionSource _firstReadStarted =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _releaseFirstRead =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		private int _openCount;

		public TaskCompletionSource FirstReadStarted => _firstReadStarted;
		public int OpenCount => Volatile.Read(ref _openCount);

		public ValueTask<IFileContentSnapshot> OpenCompleteSnapshotAsync(
			string path,
			CancellationToken cancellationToken = default)
		{
			var invocation = Interlocked.Increment(ref _openCount);
			return invocation == 1
				? AwaitFirstReadAsync(cancellationToken)
				: ValueTask.FromResult<IFileContentSnapshot>(new TextSnapshot("new source"));
		}

		public void ReleaseFirstRead() => _releaseFirstRead.TrySetResult();

		private async ValueTask<IFileContentSnapshot> AwaitFirstReadAsync(CancellationToken cancellationToken)
		{
			_firstReadStarted.TrySetResult();
			await _releaseFirstRead.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
			return new TextSnapshot("old source");
		}

		public ValueTask<bool> IsTextFileAsync(string path, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();
		public ValueTask<TextFileMetrics?> GetTextFileMetricsAsync(string path, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();
		public ValueTask<TextFileContent?> TryReadAsTextAsync(string path, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();
		public ValueTask<TextFileContent?> TryReadAsTextAsync(
			string path,
			long maxSizeForFullRead,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class ClassifiedSnapshot(FileContentClassification classification) : IFileContentSnapshot
	{
		public FileContentMetricsResult Result { get; } = new(classification);

		public ValueTask CopyTextToAsync(
			int maximumCharacters,
			Func<ReadOnlyMemory<char>, CancellationToken, ValueTask> writeChunk,
			CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private sealed class TextSnapshot(string content) : IFileContentSnapshot
	{
		public FileContentMetricsResult Result { get; } = new(
			FileContentClassification.Text,
			new TextFileMetrics(
				Encoding.UTF8.GetByteCount(content),
				1,
				content.Length,
				content.Length == 0,
				string.IsNullOrWhiteSpace(content)));

		public async ValueTask CopyTextToAsync(
			int maximumCharacters,
			Func<ReadOnlyMemory<char>, CancellationToken, ValueTask> writeChunk,
			CancellationToken cancellationToken = default)
		{
			await writeChunk(
				content.AsMemory(0, Math.Min(content.Length, maximumCharacters)),
				cancellationToken).ConfigureAwait(false);
		}

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private sealed class CostedFactExtractor(IReadOnlyDictionary<string, int> costs) : IDependencyFactExtractor
	{
		public int ParseCount { get; private set; }
		public int CompiledQuerySetCount => 0;

		public ValueTask<PreparedDependencySource> PrepareAsync(
			string sourceRoot,
			string fullPath,
			DependencyResolverConfiguration configuration,
			DependencyFactsLimits limits,
			CancellationToken cancellationToken,
			string? contentIdentity = null)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var relativePath = PathUtility.GetPortableRelativePath(sourceRoot, fullPath);
			return ValueTask.FromResult(new PreparedDependencySource(
				fullPath,
				relativePath,
				"fixture",
				LanguageId.CSharp,
				relativePath,
				"costed-fixture",
				string.Empty));
		}

		public FileFacts Extract(PreparedDependencySource source, DependencyFactsLimits limits)
		{
			ParseCount++;
			var references = Enumerable.Range(0, costs[source.RelativePath])
				.Select(index => new ReferenceFact(
					EvidenceLayer.TypeReference,
					$"Missing{index}",
					0,
					"type",
					new SourceSite(source.RelativePath, index + 1, $"Missing{index}")))
				.ToArray();
			return new FileFacts(
				source.RelativePath,
				source.ScopeId,
				source.LanguageId,
				source.ContentFingerprint,
				0,
				DependencyFileStatus.Supported,
				null,
				HasSyntaxErrors: false,
				new Dictionary<string, int>(StringComparer.Ordinal),
				[],
				[],
				references,
				[],
				new Dictionary<string, string>(StringComparer.Ordinal),
				[],
				new Dictionary<string, string>(StringComparer.Ordinal),
				[]);
		}

		public void Dispose()
		{
		}
	}

	private sealed class FailOnceDependencyFactExtractor : IDependencyFactExtractor
	{
		private int _prepareCount;

		public int PrepareCount => Volatile.Read(ref _prepareCount);
		public int ParseCount => 0;
		public int CompiledQuerySetCount => 0;

		public ValueTask<PreparedDependencySource> PrepareAsync(
			string sourceRoot,
			string fullPath,
			DependencyResolverConfiguration configuration,
			DependencyFactsLimits limits,
			CancellationToken cancellationToken,
			string? contentIdentity = null)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var relativePath = PathUtility.GetPortableRelativePath(sourceRoot, fullPath);
			var failed = Interlocked.Increment(ref _prepareCount) == 1;
			return ValueTask.FromResult(new PreparedDependencySource(
				fullPath,
				relativePath,
				"fixture",
				LanguageId.CSharp,
				failed ? "transient" : "recovered",
				"fail-once-fixture",
				string.Empty,
				failed ? DependencyFileStatus.ExtractionFailed : DependencyFileStatus.Supported,
				failed ? "IOException: sharing violation" : null,
				CanCache: !failed));
		}

		public FileFacts Extract(PreparedDependencySource source, DependencyFactsLimits limits) => new(
			source.RelativePath,
			source.ScopeId,
			source.LanguageId,
			source.ContentFingerprint,
			0,
			source.PreparedStatus,
			source.PreparedStatusReason,
			HasSyntaxErrors: false,
			new Dictionary<string, int>(StringComparer.Ordinal),
			[], [], [], [],
			new Dictionary<string, string>(StringComparer.Ordinal),
			[],
			new Dictionary<string, string>(StringComparer.Ordinal),
			[])
		{
			CanCache = source.CanCache
		};

		public void Dispose()
		{
		}
	}

	private sealed class EmptyDependencyConfigurationProvider : IDependencyConfigurationProvider
	{
		public Task<DependencyResolverConfiguration> ReadAsync(
			string sourceRoot,
			IReadOnlyList<string> manifestFiles,
			CancellationToken cancellationToken) =>
			Task.FromResult(new DependencyResolverConfiguration(
				"fixture",
				[],
				new Dictionary<string, PackageMapDescriptor>(StringComparer.Ordinal),
				new HashSet<string>(StringComparer.Ordinal),
				new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal),
				new HashSet<string>(StringComparer.Ordinal)));
	}
}
