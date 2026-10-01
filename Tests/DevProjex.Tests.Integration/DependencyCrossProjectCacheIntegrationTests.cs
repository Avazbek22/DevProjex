using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class DependencyCrossProjectCacheIntegrationTests
{
	[Fact]
	public async Task EqualManifestsInDifferentRootsRetainTheirOwnPhysicalResolution()
	{
		using var shadowedProject = new TemporaryDirectory();
		using var unshadowedProject = new TemporaryDirectory();
		var shadowedManifest = CreateManifest(shadowedProject, createUnselectedShadow: true);
		var unshadowedManifest = CreateManifest(unshadowedProject, createUnselectedShadow: false);
		using var sharedEngine = CreateEngine();

		var shadowed = await sharedEngine.IndexAsync(
			shadowedProject.Path,
			shadowedManifest,
			cancellationToken: TestContext.Current.CancellationToken);
		var unshadowed = await sharedEngine.IndexAsync(
			unshadowedProject.Path,
			unshadowedManifest,
			cancellationToken: TestContext.Current.CancellationToken);
		using var isolatedEngine = CreateEngine();
		var isolated = await isolatedEngine.IndexAsync(
			unshadowedProject.Path,
			unshadowedManifest,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(shadowed.ManifestGeneration, unshadowed.ManifestGeneration);
		Assert.Equal(ResolutionStatus.Resolved, ImportEdge(isolated).Status);
		Assert.Equal(ResolutionStatus.Unresolved, ImportEdge(shadowed).Status);
		Assert.Equal(ResolutionStatus.Resolved, ImportEdge(unshadowed).Status);
		Assert.Equal("src/value.ts", ImportEdge(unshadowed).Target);
		Assert.False(unshadowed.Metrics.ResolutionCacheHit);
	}

	private static string[] CreateManifest(TemporaryDirectory project, bool createUnselectedShadow)
	{
		var config = project.CreateFile(
			"tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\",\"paths\":{\"alias\":[\"shadow.ts\",\"src/value.ts\"]}}}");
		var source = project.CreateFile("main.ts", "import value from 'alias';");
		var target = project.CreateFile("src/value.ts", "export default 1;");
		if (createUnselectedShadow)
			project.CreateFile("shadow.ts", "export default 2;");
		var manifest = new[] { config, source, target };
		var stableTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
		foreach (var path in manifest)
			File.SetLastWriteTimeUtc(path, stableTime);
		return manifest;
	}

	private static DependencyEdge ImportEdge(DependencyIndexSnapshot snapshot) =>
		Assert.Single(snapshot.Edges, edge => edge.Source == "main.ts" && edge.Reference == "alias");

	private static DependencyFactsEngine CreateEngine() => new(
		new TreeSitterDependencyFactExtractor(),
		new FileDependencyConfigurationProvider());
}
