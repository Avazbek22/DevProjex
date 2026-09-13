using System.Text.Json.Serialization;
using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class DependencyFactsSpikeFixtureIntegrationTests
{
	[Theory]
	[InlineData("csharp")]
	[InlineData("csharp-ivt")]
	[InlineData("typescript")]
	[InlineData("typescript-legacy")]
	[InlineData("python")]
	public async Task SpikeFixture_ProducesItsExpectedStatuses(string fixtureName)
	{
		var root = Path.Combine(FindRepositoryRoot(), "Tests", "Fixtures", "DependencyFacts", fixtureName);
		var expectation = JsonSerializer.Deserialize<FixtureExpectation>(
			await File.ReadAllTextAsync(
				Path.Combine(root, "expected.json"),
				TestContext.Current.CancellationToken),
			new JsonSerializerOptions
			{
				PropertyNameCaseInsensitive = true,
				Converters = { new JsonStringEnumConverter() }
			}) ?? throw new InvalidOperationException($"Fixture '{fixtureName}' has no expectation.");
		var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
			.Where(static path => Path.GetFileName(path) != "expected.json")
			.ToArray();
		using var engine = new DependencyFactsEngine(
			new TreeSitterDependencyFactExtractor(),
			new FileDependencyConfigurationProvider());

		var result = await engine.IndexAsync(
			root,
			files,
			cancellationToken: TestContext.Current.CancellationToken);

		foreach (var expected in expectation.Edges)
		{
			var edge = Assert.Single(result.Edges, candidate =>
				candidate.Source == expected.Source &&
				candidate.Reference == expected.Reference &&
				candidate.Status == expected.Status);
			if (expected.Target is not null) Assert.Equal(expected.Target, edge.Target);
			if (expected.ReasonContains is not null)
				Assert.True(
					edge.Reasons.Any(reason => reason.Contains(expected.ReasonContains, StringComparison.Ordinal)),
					$"{fixtureName}/{expected.Reference}: expected reason containing '{expected.ReasonContains}', actual '{string.Join(" | ", edge.Reasons)}'.");
		}
		foreach (var absent in expectation.AbsentEdges ?? [])
			Assert.DoesNotContain(result.Edges, edge => edge.Source == absent.Source && edge.Reference == absent.Reference);
	}

	private static string FindRepositoryRoot()
	{
		var current = AppContext.BaseDirectory;
		while (current is not null)
		{
			if (File.Exists(Path.Combine(current, "DevProjex.sln"))) return current;
			current = Directory.GetParent(current)?.FullName;
		}
		throw new InvalidOperationException("Repository root was not found.");
	}

	private sealed record FixtureExpectation(
		string Name,
		IReadOnlyList<ExpectedEdge> Edges,
		IReadOnlyList<AbsentEdge>? AbsentEdges);

	private sealed record ExpectedEdge(
		string Source,
		string Reference,
		ResolutionStatus Status,
		string? Target,
		string? ReasonContains);

	private sealed record AbsentEdge(string Source, string Reference);
}
