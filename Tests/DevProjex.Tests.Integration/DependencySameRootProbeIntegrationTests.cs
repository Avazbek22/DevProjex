using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

public sealed class DependencySameRootProbeIntegrationTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task UnselectedPathTargetChangeInvalidatesWarmResolution(bool initiallyShadowed)
	{
		using var project = new TemporaryDirectory();
		var config = project.CreateFile(
			"tsconfig.json",
			"{\"compilerOptions\":{\"moduleResolution\":\"bundler\",\"paths\":{\"alias\":[\"shadow.ts\",\"src/value.ts\"]}}}");
		var source = project.CreateFile("main.ts", "import value from 'alias';");
		var target = project.CreateFile("src/value.ts", "export default 1;");
		var shadow = Path.Combine(project.Path, "shadow.ts");
		if (initiallyShadowed)
			project.CreateFile("shadow.ts", "export default 2;");
		var manifest = new[] { config, source, target };
		using var engine = CreateEngine();
		var initial = await engine.IndexAsync(
			project.Path,
			manifest,
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.Equal(initiallyShadowed ? ResolutionStatus.Unresolved : ResolutionStatus.Resolved,
			ImportEdge(initial).Status);
		var unchanged = await engine.IndexAsync(
			project.Path,
			manifest,
			cancellationToken: TestContext.Current.CancellationToken);
		Assert.True(unchanged.Metrics.ResolutionCacheHit);
		Assert.Equal(ImportEdge(initial).Status, ImportEdge(unchanged).Status);

		if (initiallyShadowed)
			File.Delete(shadow);
		else
			project.CreateFile("shadow.ts", "export default 2;");

		using var isolatedEngine = CreateEngine();
		var isolated = await isolatedEngine.IndexAsync(
			project.Path,
			manifest,
			cancellationToken: TestContext.Current.CancellationToken);
		var updated = await engine.IndexAsync(
			project.Path,
			manifest,
			cancellationToken: TestContext.Current.CancellationToken);

		Assert.Equal(initiallyShadowed ? ResolutionStatus.Resolved : ResolutionStatus.Unresolved,
			ImportEdge(isolated).Status);
		Assert.Equal(ImportEdge(isolated).Status, ImportEdge(updated).Status);
		Assert.False(updated.Metrics.ResolutionCacheHit);
		Assert.Equal(0, updated.Metrics.ParsedFiles);
	}

	private static DependencyEdge ImportEdge(DependencyIndexSnapshot snapshot) =>
		Assert.Single(snapshot.Edges, edge => edge.Source == "main.ts" && edge.Reference == "alias");

	private static DependencyFactsEngine CreateEngine() => new(
		new TreeSitterDependencyFactExtractor(),
		new FileDependencyConfigurationProvider());
}
