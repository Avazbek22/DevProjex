using DevProjex.Application.Dependencies;
using DevProjex.Infrastructure.Dependencies;

namespace DevProjex.Tests.Integration;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DependencyFactsPerformanceCollection
{
	public const string Name = "Dependency facts performance";
}

[Collection(DependencyFactsPerformanceCollection.Name)]
public sealed class DependencyFactsPerformanceIntegrationTests(ITestOutputHelper output)
{
	[Fact]
	[Trait("Category", "Performance")]
	public async Task DevProjexIndex_StaysWithinColdAndWarmBudgets()
	{
		var root = FindRepositoryRoot();
		var files = EnumerateManifest(root).ToArray();
		using var engine = new DependencyFactsEngine(
			new TreeSitterDependencyFactExtractor(),
			new FileDependencyConfigurationProvider());

		var cold = await engine.IndexAsync(root, files,
			cancellationToken: TestContext.Current.CancellationToken);
		var warm = await engine.IndexAsync(root, files,
			cancellationToken: TestContext.Current.CancellationToken);
		output.WriteLine(
			$"dependency-index files={cold.Files.Count} cold={cold.Metrics.ElapsedMilliseconds}ms warm={warm.Metrics.ElapsedMilliseconds}ms " +
			$"parsed={cold.Metrics.ParsedFiles} csharp-error-files={cold.Files.Count(static file => file.LanguageId == LanguageId.CSharp && file.HasSyntaxErrors)}");
		output.WriteLine("csharp-error-node-kinds=" + JsonSerializer.Serialize(cold.Coverage.CSharpErrorNodeKinds));

		Assert.True(cold.Metrics.ElapsedMilliseconds <= 5_000,
			$"Cold dependency index took {cold.Metrics.ElapsedMilliseconds} ms for {cold.Files.Count} files.");
		Assert.True(warm.Metrics.ElapsedMilliseconds <= 1_000,
			$"Warm dependency index took {warm.Metrics.ElapsedMilliseconds} ms for {warm.Files.Count} files.");
		Assert.True(warm.Metrics.ResolutionCacheHit);
	}

	private static IEnumerable<string> EnumerateManifest(string root) =>
		Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
			.Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)
				.Any(segment => segment is ".git" or "bin" or "obj" or "publish" or "artifacts"))
			.Where(path => Path.GetExtension(path) is ".cs" or ".csproj" or ".ts" or ".tsx" or ".js" or ".py" ||
			               Path.GetFileName(path) is "tsconfig.json" or "jsconfig.json" or "package.json" or "pyproject.toml" or "setup.cfg");

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
}
