using System.Security.Cryptography;
using System.Text;
using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class CSharpInvocationTypeArgumentIntegrationTests
{
	[Fact]
	public async Task GenericRegistrationArgumentsBecomeResolvedSyntacticReferences()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var contract = fixture.CreateFile("IUserRepository.cs", "namespace Contracts; public interface IUserRepository { }");
		var implementation = fixture.CreateFile("SqlUserRepository.cs", "namespace Storage; public sealed class SqlUserRepository { }");
		var composition = fixture.CreateFile("Composition.cs", """
			using Contracts;
			using Storage;
			public sealed class Services { public void AddScoped<TService, TImplementation>() { } }
			public sealed class Composition
			{
			    public void Configure(Services services) => services.AddScoped<IUserRepository, SqlUserRepository>();
			}
			""");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[project, contract, implementation, composition],
			cancellationToken: TestContext.Current.CancellationToken);

		var references = result.Files.Single(file => file.Path == "Composition.cs").References;
		Assert.Contains(references, reference => reference.Name == "IUserRepository" && reference.Status == ResolutionStatus.Resolved);
		Assert.Contains(references, reference => reference.Name == "SqlUserRepository" && reference.Status == ResolutionStatus.Resolved);
		Assert.DoesNotContain(references, reference => reference.Name == "AddScoped");
		var related = await engine.FindRelatedAsync(
			fixture.Path,
			[project, contract, implementation, composition],
			["IUserRepository.cs"],
			DependencyDirection.Dependents,
			cancellationToken: TestContext.Current.CancellationToken);
		var dependent = Assert.Single(Assert.Single(related.Seeds).Dependents);
		Assert.Equal("Composition.cs", dependent.Path);
		Assert.Contains("type argument of a call", dependent.Reasons);
	}

	[Fact]
	public async Task InvocationTypeArgumentsSupportNestedQualifiedGlobalAndAliasFormsWithoutDuplicatingObjectCreation()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var types = fixture.CreateFile("Types.cs", """
			namespace Models;
			public sealed class Item { }
			public sealed class Envelope<T> { }
			public sealed class Qualified { }
			""");
		var source = fixture.CreateFile("Consumer.cs", """
			using Models;
			using Alias = Models.Item;
			public sealed class Consumer
			{
			    public void Register<T>() { }
			    public void Run()
			    {
			        Register<Item>();
			        Register<Envelope<Item>>();
			        Register<Models.Qualified>();
			        Register<global::Models.Qualified>();
			        Register<Alias>();
			        _ = new Envelope<Item>();
			    }
			}
			""");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[project, types, source],
			cancellationToken: TestContext.Current.CancellationToken);

		var references = result.Files.Single(file => file.Path == "Consumer.cs").References;
		Assert.Equal(3, references.Count(reference => reference.Name == "Item"));
		Assert.Contains(references, reference => reference.Name == "Envelope" && reference.GenericArity == 1);
		Assert.Equal(2, references.Count(reference => reference.Name == "Models.Qualified"));
		Assert.Contains(references, reference => reference.Name == "Alias" && reference.Status == ResolutionStatus.Resolved);
		Assert.DoesNotContain(references, reference => reference.Name == "Register");
	}

	[Fact]
	public async Task InvocationTypeArgumentsDoNotWidenTheManifestOrGuessSameNamedDeclarations()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var local = fixture.CreateFile("A/Service.cs", "namespace A; public sealed class Service { }");
		var neighbor = fixture.CreateFile("B/Service.cs", "namespace B; public sealed class Service { }");
		_ = fixture.CreateFile("Outside.cs", "public sealed class HiddenType { }");
		var source = fixture.CreateFile("A/Composition.cs", """
			namespace A;
			public sealed class Composition
			{
			    public void Register<T>() { }
			    public void Run() { Register<Service>(); Register<HiddenType>(); }
			}
			""");
		using var engine = CreateEngine();

		var result = await engine.IndexAsync(
			fixture.Path,
			[project, local, neighbor, source],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(result.Edges, edge => edge.Source == "A/Composition.cs" && edge.Reference == "Service" &&
			edge.Target == "A/Service.cs" && edge.Status == ResolutionStatus.Resolved);
		Assert.DoesNotContain(result.Edges, edge => edge.Source == "A/Composition.cs" && edge.Target == "B/Service.cs");
		var hidden = Assert.Single(result.Edges, edge => edge.Source == "A/Composition.cs" && edge.Reference == "HiddenType");
		Assert.Equal(ResolutionStatus.Unresolved, hidden.Status);
		Assert.Empty(hidden.Candidates);
		Assert.DoesNotContain(result.Edges, edge => edge.Reference == "Register");
	}

	[Fact]
	public async Task NonCSharpFactProjectionsRemainPinned()
	{
		using var fixture = new TemporaryDirectory();
		var tsConfig = fixture.CreateFile("tsconfig.json", "{\"compilerOptions\":{\"moduleResolution\":\"bundler\"}}");
		var tsTarget = fixture.CreateFile("target.ts", "export class Target {}");
		var tsSource = fixture.CreateFile("source.ts", "import { Target } from './target.js'; const value: Target = new Target();");
		var pyConfig = fixture.CreateFile("pyproject.toml", "[project]\nname = \"fixture\"");
		var pyTarget = fixture.CreateFile("model.py", "class Model: pass");
		var pySource = fixture.CreateFile("consumer.py", "from model import Model\nvalue = Model()");
		using var engine = CreateEngine();

		var typescript = await engine.IndexAsync(
			fixture.Path,
			[tsConfig, tsTarget, tsSource],
			cancellationToken: TestContext.Current.CancellationToken);
		var python = await engine.IndexAsync(
			fixture.Path,
			[pyConfig, pyTarget, pySource],
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(
			"c9ca0cc3f9168eefb472a7157d5803924a18ff249f7bdfdba462e9dd924211c5",
			ProjectionHash(typescript));
		Assert.Equal(
			"4b2b829a31926f53c38f8d57c1d364355efd823b5c7a3f265fa2d50ed7a13b43",
			ProjectionHash(python));
	}

	private static string ProjectionHash(DependencyIndexSnapshot snapshot)
	{
		var projection = string.Join('\n', snapshot.Files.OrderBy(static file => file.Path, StringComparer.Ordinal)
			.Select(file => $"F|{file.Path}|{file.LanguageId}|{file.Status}|" +
				$"{string.Join(',', file.Declarations.Select(static fact => fact.Identity.QualifiedName).Order(StringComparer.Ordinal))}|" +
				$"{string.Join(',', file.Imports.Select(static fact => fact.Specifier).Order(StringComparer.Ordinal))}|" +
				$"{string.Join(',', file.References.Select(static fact => fact.Name).Order(StringComparer.Ordinal))}")) + "\n" +
			string.Join('\n', snapshot.Edges.OrderBy(static edge => edge.Source, StringComparer.Ordinal)
				.ThenBy(static edge => edge.Reference, StringComparer.Ordinal)
				.Select(edge => $"E|{edge.Source}|{edge.Target}|{edge.Reference}|{edge.Status}"));
		return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(projection)));
	}

	private static DependencyFactsEngine CreateEngine() => new(
		new TreeSitterDependencyFactExtractor(),
		new FileDependencyConfigurationProvider());
}
