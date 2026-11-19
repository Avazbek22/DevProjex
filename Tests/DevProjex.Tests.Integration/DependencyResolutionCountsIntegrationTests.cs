using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

/// <summary>
/// The resolution counts describe the same edges the related rows are projected from, so a count
/// never promises a row that is not there and a listed row is never left uncounted.
/// </summary>
public sealed class DependencyResolutionCountsIntegrationTests
{
	[Fact]
	public async Task ReferenceToTheSeedsOwnFile_IsNeitherListedNorCounted()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var node = fixture.CreateFile("Node.cs", "namespace App; public sealed class Node { Node Next; }");
		using var engine = CreateEngine();

		var related = await engine.FindRelatedAsync(
			fixture.Path,
			[project, node],
			["Node.cs"],
			DependencyDirection.Both,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Contains(related.Index.Edges, static edge =>
			edge.Source == "Node.cs" && edge.Target == "Node.cs" && edge.Status == ResolutionStatus.Resolved);
		var seed = Assert.Single(related.Seeds);
		Assert.Empty(seed.Dependencies);
		Assert.Empty(seed.Dependents);
		Assert.Equal(
			new DependencyResolutionCounts(0, 0, 0, 0, 0),
			DependencyFactsEngine.CountResolution(related.Index, ["Node.cs"], DependencyDirection.Both));
	}

	[Fact]
	public async Task SelfReference_DoesNotHideAnUnresolvedReference()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var node = fixture.CreateFile("Node.cs", "namespace App; public sealed class Node { Node Next; Missing Gone; }");
		using var engine = CreateEngine();

		var related = await engine.FindRelatedAsync(
			fixture.Path,
			[project, node],
			["Node.cs"],
			DependencyDirection.Dependencies,
			cancellationToken: TestContext.Current.CancellationToken);

		var counts = DependencyFactsEngine.CountResolution(related.Index, ["Node.cs"], DependencyDirection.Dependencies);
		Assert.Equal(0, counts.Resolved);
		Assert.Equal(
			related.Index.EdgesBySource["Node.cs"].Count(static edge => edge.Status == ResolutionStatus.Unresolved) +
			related.Index.EdgesBySource["Node.cs"].Count(static edge => edge.Status == ResolutionStatus.External),
			counts.Unresolved + counts.External);
		Assert.True(counts.Unresolved + counts.External > 0);
	}

	[Fact]
	public async Task DependentOfAPartialDeclaration_IsCountedForEveryPartItIsListedFor()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var first = fixture.CreateFile("First.cs", "namespace Alpha; public partial class User { }");
		var second = fixture.CreateFile("Second.cs", "namespace Alpha; public partial class User { public int Id; }");
		var consumer = fixture.CreateFile("Consumer.cs", "using Alpha; namespace Use; public class Consumer { User Value; }");
		string[] manifest = [project, first, second, consumer];
		using var engine = CreateEngine();

		foreach (var part in new[] { "First.cs", "Second.cs" })
		{
			var related = await engine.FindRelatedAsync(
				fixture.Path,
				manifest,
				[part],
				DependencyDirection.Dependents,
				cancellationToken: TestContext.Current.CancellationToken);

			var dependent = Assert.Single(Assert.Single(related.Seeds).Dependents);
			Assert.Equal("Consumer.cs", dependent.Path);
			Assert.Equal(ResolutionStatus.Resolved, dependent.Status);
			Assert.Equal(
				new DependencyResolutionCounts(1, 0, 0, 0, 0),
				DependencyFactsEngine.CountResolution(related.Index, [part], DependencyDirection.Dependents));
		}
	}

	[Fact]
	public async Task AmbiguousDependent_IsCountedForEveryCandidateItIsListedFor()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var alpha = fixture.CreateFile("Models/Alpha/User.cs", "namespace App.Alpha; public sealed class User { }");
		var beta = fixture.CreateFile("Models/Beta/User.cs", "namespace App.Beta; public sealed class User { }");
		var seedFile = fixture.CreateFile("Use/Seed.cs",
			"using App.Alpha; using App.Beta; namespace App.Use; public sealed class Seed { User Value; }");
		string[] manifest = [project, alpha, beta, seedFile];
		using var engine = CreateEngine();

		foreach (var candidate in new[] { "Models/Alpha/User.cs", "Models/Beta/User.cs" })
		{
			var related = await engine.FindRelatedAsync(
				fixture.Path,
				manifest,
				[candidate],
				DependencyDirection.Dependents,
				cancellationToken: TestContext.Current.CancellationToken);

			var dependent = Assert.Single(Assert.Single(related.Seeds).Dependents);
			Assert.Equal("Use/Seed.cs", dependent.Path);
			Assert.Equal(ResolutionStatus.Ambiguous, dependent.Status);
			Assert.Equal(
				new DependencyResolutionCounts(0, 1, 0, 0, 0),
				DependencyFactsEngine.CountResolution(related.Index, [candidate], DependencyDirection.Dependents));
		}
	}

	[Fact]
	public async Task EdgeBetweenTwoSeeds_CountsOnce()
	{
		using var fixture = new TemporaryDirectory();
		var project = fixture.CreateFile("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		var model = fixture.CreateFile("Models/User.cs", "namespace App.Models; public sealed class User { }");
		var store = fixture.CreateFile("Services/Store.cs",
			"using App.Models; namespace App.Services; public sealed class Store { User Value; }");
		string[] seeds = ["Models/User.cs", "Services/Store.cs"];
		using var engine = CreateEngine();

		var related = await engine.FindRelatedAsync(
			fixture.Path,
			[project, model, store],
			seeds,
			DependencyDirection.Both,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(
			new DependencyResolutionCounts(1, 0, 0, 0, 0),
			DependencyFactsEngine.CountResolution(related.Index, seeds, DependencyDirection.Both));
	}

	private static DependencyFactsEngine CreateEngine() =>
		new(new TreeSitterDependencyFactExtractor(), new FileDependencyConfigurationProvider());
}
