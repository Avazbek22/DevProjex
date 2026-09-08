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

	private static DependencyFactsEngine CreateEngine() => new(
		new TreeSitterDependencyFactExtractor(),
		new FileDependencyConfigurationProvider());
}
